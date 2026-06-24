using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace D2RHost;

public sealed class AgentRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HostConfig _config;
    private readonly AgentAutoUpdateState _autoUpdate;
    private readonly AppDb _db;
    private readonly ILogger<AgentRegistry> _logger;
    private readonly ConcurrentDictionary<string, ConnectedAgent> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingCommand> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _autoUpdateAttempts = new(StringComparer.OrdinalIgnoreCase);

    public AgentRegistry(
        HostConfig config,
        AgentAutoUpdateState autoUpdate,
        AppDb db,
        ILogger<AgentRegistry> logger)
    {
        _config = config;
        _autoUpdate = autoUpdate;
        _db = db;
        _logger = logger;
    }

    public async Task HandleWebSocketAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        string? agentId = null;

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var raw = await ReceiveStringAsync(socket, cancellationToken);
                if (raw is null)
                {
                    break;
                }

                using var document = JsonDocument.Parse(raw);
                var type = document.RootElement.GetProperty("type").GetString();

                if (agentId is null)
                {
                    if (!string.Equals(type, "hello", StringComparison.OrdinalIgnoreCase))
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "hello required", cancellationToken);
                        return;
                    }

                    var hello = JsonSerializer.Deserialize<HelloMessage>(raw, JsonOptions)
                        ?? throw new InvalidOperationException("Invalid hello payload.");

                    if (!Authenticate(hello))
                    {
                        _logger.LogWarning("Agent authentication failed for {AgentId}", hello.AgentId);
                        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "authentication failed", cancellationToken);
                        return;
                    }

                    if (hello.ProbeOnly)
                    {
                        await SendJsonAsync(
                            socket,
                            new
                            {
                                type = "hello_ack",
                                agentId = hello.AgentId,
                                ok = true
                            },
                            cancellationToken);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe ok", cancellationToken);
                        return;
                    }

                    agentId = hello.AgentId;
                    RegisterAgent(socket, hello);
                    await SendJsonAsync(
                        socket,
                        new
                        {
                            type = "hello_ack",
                            agentId = hello.AgentId,
                            ok = true
                        },
                        cancellationToken);
                    QueueSelfUpdateAfterAuthentication(hello.AgentId, hello.Version);
                    continue;
                }

                if (string.Equals(type, "status", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateStatus(agentId, document.RootElement);
                    continue;
                }

                if (string.Equals(type, "command_result", StringComparison.OrdinalIgnoreCase))
                {
                    CompleteCommand(document.RootElement);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent websocket failed for {AgentId}", agentId ?? "(unauthenticated)");
        }
        finally
        {
            if (agentId is not null
                && _agents.TryGetValue(agentId, out var current)
                && ReferenceEquals(current.Socket, socket)
                && _agents.TryRemove(agentId, out var removed))
            {
                _db.UpsertAgentStatus(removed.Id, removed.Kind, connected: false, removed.LastStatusJson ?? "{}");
                _logger.LogWarning("Agent disconnected: {AgentId}", agentId);
            }
        }
    }

    public IReadOnlyList<AgentSnapshot> Snapshot()
    {
        return _config.Agents
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                if (_agents.TryGetValue(pair.Key, out var connected))
                {
                    return connected.ToSnapshot(connected: true);
                }

                return new AgentSnapshot(
                    pair.Key,
                    pair.Value.Kind,
                    pair.Value.DisplayName,
                    null,
                    null,
                    false,
                    null,
                    null,
                    null);
            })
            .ToArray();
    }

    public AgentSnapshot? GetAgent(string agentId)
    {
        if (_agents.TryGetValue(agentId, out var connected))
        {
            return connected.ToSnapshot(connected: true);
        }

        if (!_config.Agents.TryGetValue(agentId, out var configured))
        {
            return null;
        }

        return new AgentSnapshot(
            agentId,
            configured.Kind,
            configured.DisplayName,
            null,
            null,
            false,
            null,
            null,
            null);
    }

    public async Task<CommandResultInfo> SendCommandAsync(
        string agentId,
        string command,
        object? args = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!_agents.TryGetValue(agentId, out var agent)
            || agent.Socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException($"Agent \"{agentId}\" is not connected.");
        }

        var commandId = Guid.NewGuid().ToString("N");
        var pending = new PendingCommand(agentId, command);
        if (!_pending.TryAdd(commandId, pending))
        {
            throw new InvalidOperationException("Could not register pending command.");
        }

        var commandTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(commandTimeout);
        await using var registration = timeoutCts.Token.Register(() =>
        {
            if (_pending.TryRemove(commandId, out var removed))
            {
                removed.TrySetException(new TimeoutException($"Command \"{command}\" timed out for agent \"{agentId}\"."));
            }
        });

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                type = "command",
                commandId,
                command,
                args = args ?? new { },
                timeoutMs = GetAgentCommandTimeoutMs(commandTimeout)
            },
            JsonOptions);

        await agent.SendLock.WaitAsync(cancellationToken);
        try
        {
            await agent.Socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            agent.SendLock.Release();
        }

        return await pending.Task;
    }

    private static int GetAgentCommandTimeoutMs(TimeSpan hostTimeout)
    {
        var timeout = hostTimeout - TimeSpan.FromSeconds(5);
        if (timeout < TimeSpan.FromSeconds(5))
        {
            timeout = hostTimeout;
        }

        return (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
    }

    private bool Authenticate(HelloMessage hello)
    {
        return _config.Agents.TryGetValue(hello.AgentId, out var configured)
            && string.Equals(configured.Kind, hello.AgentKind, StringComparison.OrdinalIgnoreCase)
            && configured.SharedSecret == hello.SharedSecret;
    }

    private void RegisterAgent(WebSocket socket, HelloMessage hello)
    {
        if (_agents.TryRemove(hello.AgentId, out var existing))
        {
            _ = existing.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "new connection established", CancellationToken.None);
        }

        var configured = _config.Agents[hello.AgentId];
        var connected = new ConnectedAgent(
            hello.AgentId,
            hello.AgentKind,
            configured.DisplayName,
            hello.HostName,
            hello.Version,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            socket);

        _agents[hello.AgentId] = connected;
        _db.UpsertAgentStatus(connected.Id, connected.Kind, connected: true, "{}");
        _logger.LogInformation("Agent authenticated: {AgentId}", hello.AgentId);
    }

    private void QueueSelfUpdateAfterAuthentication(string agentId, string? version)
    {
        if (!_autoUpdate.Enabled)
        {
            _logger.LogDebug(
                "Skipping satellite auto-update for {AgentId}: {Reason}",
                agentId,
                _autoUpdate.Reason);
            return;
        }

        var key = $"{agentId}|{version ?? "(unknown)"}";
        if (!_autoUpdateAttempts.TryAdd(key, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                var result = await SendCommandAsync(
                    agentId,
                    "self_update",
                    new
                    {
                        initiatedBy = "host",
                        hostVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                    },
                    TimeSpan.FromSeconds(45));

                if (result.Ok)
                {
                    _logger.LogInformation(
                        "Satellite auto-update command completed for {AgentId}: {Message}",
                        agentId,
                        result.Message);
                }
                else
                {
                    _logger.LogWarning(
                        "Satellite auto-update command failed for {AgentId}: {Message}",
                        agentId,
                        result.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Satellite auto-update command failed for {AgentId}.", agentId);
            }
        });
    }

    private void UpdateStatus(string agentId, JsonElement root)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
        {
            return;
        }

        var statusJson = root.GetProperty("status").GetRawText();
        agent.LastSeenAt = DateTimeOffset.UtcNow;
        agent.LastStatusJson = statusJson;
        _db.UpsertAgentStatus(agent.Id, agent.Kind, connected: true, statusJson);
    }

    private void CompleteCommand(JsonElement root)
    {
        var commandId = root.GetProperty("commandId").GetString() ?? "";
        if (!_pending.TryRemove(commandId, out var pending))
        {
            // Most commonly this is a late result: the agent's command actually ran to
            // completion (often successfully) after the host already gave up waiting and
            // reported a timeout to Discord. Logging ok/message here is the only trace that
            // the operator-visible failure was wrong - without it, a late success and a late
            // failure are indistinguishable from "Received unknown command result".
            var lateAgentId = root.TryGetProperty("agentId", out var agentIdProp) ? agentIdProp.GetString() : null;
            var lateOk = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            var lateMessage = root.TryGetProperty("message", out var messageProp) ? messageProp.GetString() : null;
            _logger.LogWarning(
                "Received command result for {CommandId} (agent {AgentId}) after the host already stopped waiting on it - ok={Ok}, message={Message}",
                commandId,
                lateAgentId,
                lateOk,
                lateMessage);
            return;
        }

        var result = new CommandResultInfo(
            root.GetProperty("agentId").GetString() ?? pending.AgentId,
            commandId,
            root.GetProperty("ok").GetBoolean(),
            root.GetProperty("message").GetString() ?? "",
            root.TryGetProperty("data", out var data) ? data.Clone() : null);

        _db.InsertCommandHistory(commandId, pending.AgentId, pending.Command, result.Ok, result.Message);
        pending.TrySetResult(result);
    }

    private static async Task<string?> ReceiveStringAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        await using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", cancellationToken);
                }

                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static async Task SendJsonAsync(
        WebSocket socket,
        object payload,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private sealed record HelloMessage(
        string Type,
        string AgentId,
        string AgentKind,
        string SharedSecret,
        string? Version,
        string? HostName,
        bool ProbeOnly = false);

    private sealed class ConnectedAgent
    {
        public ConnectedAgent(
            string id,
            string kind,
            string? displayName,
            string? hostName,
            string? version,
            DateTimeOffset connectedAt,
            DateTimeOffset lastSeenAt,
            WebSocket socket)
        {
            Id = id;
            Kind = kind;
            DisplayName = displayName;
            HostName = hostName;
            Version = version;
            ConnectedAt = connectedAt;
            LastSeenAt = lastSeenAt;
            Socket = socket;
        }

        public string Id { get; }
        public string Kind { get; }
        public string? DisplayName { get; }
        public string? HostName { get; }
        public string? Version { get; }
        public DateTimeOffset ConnectedAt { get; }
        public DateTimeOffset LastSeenAt { get; set; }
        public string? LastStatusJson { get; set; }
        public WebSocket Socket { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public AgentSnapshot ToSnapshot(bool connected)
        {
            return new AgentSnapshot(
                Id,
                Kind,
                DisplayName,
                HostName,
                Version,
                connected,
                ConnectedAt,
                LastSeenAt,
                LastStatusJson);
        }
    }

    private sealed class PendingCommand
    {
        private readonly TaskCompletionSource<CommandResultInfo> _source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingCommand(string agentId, string command)
        {
            AgentId = agentId;
            Command = command;
        }

        public string AgentId { get; }
        public string Command { get; }
        public Task<CommandResultInfo> Task => _source.Task;

        public void TrySetResult(CommandResultInfo result) => _source.TrySetResult(result);
        public void TrySetException(Exception exception) => _source.TrySetException(exception);
    }
}

public sealed record AgentSnapshot(
    string Id,
    string Kind,
    string? DisplayName,
    string? HostName,
    string? Version,
    bool Connected,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? LastSeenAt,
    string? LastStatusJson);

public sealed record CommandResultInfo(
    string AgentId,
    string CommandId,
    bool Ok,
    string Message,
    JsonElement? Data);
