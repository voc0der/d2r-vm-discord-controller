using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgentCommon;

namespace D2RHost;

public sealed class AgentRegistry
{
    private const int MinimumAdvertisedHeartbeatSeconds = 5;
    private const int MaximumAdvertisedHeartbeatSeconds = 300;
    private const int HeartbeatCollectionAndJitterGraceSeconds = 20;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HostConfig _config;
    private readonly AgentAutoUpdateState _autoUpdate;
    private readonly SatelliteUpdateGate _updateGate;
    private readonly DiscordNotificationQueue _notifications;
    private readonly AppDb _db;
    private readonly ILogger<AgentRegistry> _logger;
    private readonly ConcurrentDictionary<string, ConnectedAgent> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingCommand> _pending = new();
    private readonly object _registrationLock = new();

    // Raised on the websocket receive loop after an agent authenticates or drops; handlers
    // must hand off to a background task rather than block the agent's socket.
    public event Action? ConnectivityChanged;

    public AgentRegistry(
        HostConfig config,
        AgentAutoUpdateState autoUpdate,
        DiscordNotificationQueue notifications,
        SatelliteUpdateGate updateGate,
        AppDb db,
        ILogger<AgentRegistry> logger)
    {
        _config = config;
        _autoUpdate = autoUpdate;
        _notifications = notifications;
        _updateGate = updateGate;
        _db = db;
        _logger = logger;

        LoadPersistedAgentStatuses();
    }

    public async Task HandleWebSocketAsync(
        WebSocket socket,
        CancellationToken cancellationToken,
        string? expectedAgentKind = null)
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

                    if (!Authenticate(hello, expectedAgentKind))
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
                    await SendJsonAsync(
                        socket,
                        new
                        {
                            type = "hello_ack",
                            agentId = hello.AgentId,
                            ok = true
                        },
                        cancellationToken);
                    // Do not expose the connection to command senders until hello_ack is
                    // on the wire; otherwise a command can race that un-serialized send.
                    RegisterAgent(socket, hello);
                    QueueSelfUpdateAfterAuthentication(hello.AgentId, hello.AgentKind, hello.Version);
                    continue;
                }

                if (string.Equals(type, "status", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateStatus(agentId, socket, document.RootElement);
                    continue;
                }

                if (string.Equals(type, "command_result", StringComparison.OrdinalIgnoreCase))
                {
                    CompleteCommand(agentId, socket, document.RootElement);
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
                && current.TryDetachSocket(socket))
            {
                FailPendingCommands(current.Id, socket);
                _db.MarkAgentDisconnected(current.Id);
                _logger.LogWarning("Agent disconnected: {AgentId}", agentId);
                ConnectivityChanged?.Invoke();
            }
        }
    }

    private void LoadPersistedAgentStatuses()
    {
        var persistedStatuses = _db.GetAgentStatuses();
        _db.MarkAllAgentsDisconnected();

        foreach (var persisted in persistedStatuses)
        {
            if (!_config.Agents.TryGetValue(persisted.AgentId, out var configured))
            {
                continue;
            }

            _agents[persisted.AgentId] = new ConnectedAgent(
                persisted.AgentId,
                configured.Kind,
                configured.DisplayName,
                hostName: null,
                version: null,
                connectedAt: null,
                persisted.LastSeenAt,
                persisted.PayloadJson,
                statusReceivedAt: persisted.LastSeenAt,
                heartbeatSeconds: null,
                socket: null);
        }
    }

    public IReadOnlyList<AgentSnapshot> Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return _config.Agents
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                if (_agents.TryGetValue(pair.Key, out var agent))
                {
                    return agent.ToSnapshot(IsConnected(agent, now));
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
        if (_agents.TryGetValue(agentId, out var agent))
        {
            return agent.ToSnapshot(IsConnected(agent, DateTimeOffset.UtcNow));
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
            || !TryGetConnectedSocket(agent, DateTimeOffset.UtcNow, out var socket))
        {
            throw new InvalidOperationException($"Agent \"{agentId}\" is not connected or its heartbeat is stale.");
        }

        var commandId = Guid.NewGuid().ToString("N");
        var pending = new PendingCommand(agentId, command, socket);
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
                if (cancellationToken.IsCancellationRequested)
                {
                    removed.TrySetCanceled(cancellationToken);
                }
                else
                {
                    removed.TrySetException(new TimeoutException($"Command \"{command}\" timed out for agent \"{agentId}\"."));
                }
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

        var sendLockHeld = false;
        try
        {
            await agent.SendLock.WaitAsync(timeoutCts.Token);
            sendLockHeld = true;

            if (!_pending.TryGetValue(commandId, out var activePending)
                || !ReferenceEquals(activePending, pending))
            {
                return await pending.Task;
            }

            if (!_agents.TryGetValue(agentId, out var current)
                || !ReferenceEquals(current, agent)
                || !agent.HasSocket(socket)
                || !IsConnected(agent, DateTimeOffset.UtcNow))
            {
                throw new InvalidOperationException($"Agent \"{agentId}\" disconnected or its heartbeat became stale before the command was sent.");
            }

            await socket.SendAsync(payload, WebSocketMessageType.Text, true, timeoutCts.Token);
        }
        catch
        {
            if (_pending.TryRemove(commandId, out _))
            {
                throw;
            }

            return await pending.Task;
        }
        finally
        {
            if (sendLockHeld)
            {
                agent.SendLock.Release();
            }
        }

        return await pending.Task;
    }

    private bool IsConnected(ConnectedAgent agent, DateTimeOffset now)
    {
        return TryGetConnectedSocket(agent, now, out _);
    }

    private bool TryGetConnectedSocket(
        ConnectedAgent agent,
        DateTimeOffset now,
        out WebSocket socket)
    {
        var currentSocket = agent.Socket;
        var lastSeenAt = agent.LastSeenAt;
        if (currentSocket is not null
            && currentSocket.State == WebSocketState.Open
            && lastSeenAt is not null
            && now - lastSeenAt.Value <= GetOfflineThreshold(agent)
            && agent.HasSocket(currentSocket))
        {
            socket = currentSocket;
            return true;
        }

        socket = null!;
        return false;
    }

    private TimeSpan GetOfflineThreshold(ConnectedAgent agent)
    {
        var configured = TimeSpan.FromSeconds(_config.AgentOfflineAfterSeconds);
        if (agent.HeartbeatSeconds is not { } heartbeatSeconds)
        {
            return configured;
        }

        var advertised = Math.Clamp(
            heartbeatSeconds,
            MinimumAdvertisedHeartbeatSeconds,
            MaximumAdvertisedHeartbeatSeconds);
        var negotiated = TimeSpan.FromSeconds(
            advertised + HeartbeatCollectionAndJitterGraceSeconds);
        return negotiated > configured ? negotiated : configured;
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

    private bool Authenticate(HelloMessage hello, string? expectedAgentKind)
    {
        return _config.Agents.TryGetValue(hello.AgentId, out var configured)
            && (string.IsNullOrWhiteSpace(expectedAgentKind)
                || string.Equals(expectedAgentKind, hello.AgentKind, StringComparison.OrdinalIgnoreCase))
            && string.Equals(configured.Kind, hello.AgentKind, StringComparison.OrdinalIgnoreCase)
            && configured.SharedSecret == hello.SharedSecret;
    }

    private void RegisterAgent(WebSocket socket, HelloMessage hello)
    {
        var configured = _config.Agents[hello.AgentId];
        var now = DateTimeOffset.UtcNow;
        ConnectedAgent? existing;
        ConnectedAgent connected;
        WebSocket? existingSocket;
        lock (_registrationLock)
        {
            _agents.TryGetValue(hello.AgentId, out existing);
            connected = new ConnectedAgent(
                hello.AgentId,
                hello.AgentKind,
                configured.DisplayName,
                hello.HostName,
                hello.Version,
                now,
                now,
                existing?.LastStatusJson,
                statusReceivedAt: null,
                heartbeatSeconds: hello.HeartbeatSeconds,
                socket: socket);

            _agents[hello.AgentId] = connected;
            existingSocket = existing?.Socket;
            if (existingSocket is not null && !ReferenceEquals(existingSocket, socket))
            {
                existing!.TryDetachSocket(existingSocket);
            }
        }

        _db.UpsertAgentStatus(connected.Id, connected.Kind, connected: true, connected.LastStatusJson ?? "{}");

        if (existingSocket is not null && !ReferenceEquals(existingSocket, socket))
        {
            FailPendingCommands(hello.AgentId, existingSocket);
            _ = existingSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "new connection established",
                CancellationToken.None);
        }

        _logger.LogInformation("Agent authenticated: {AgentId}", hello.AgentId);
        ConnectivityChanged?.Invoke();
    }

    private void QueueSelfUpdateAfterAuthentication(string agentId, string agentKind, string? version)
    {
        // Worker nodes used to be excluded here, so a VM agent got two chances to update (this,
        // plus the fleet sweep) while a worker got only the sweep - which lands 30s after the
        // master starts rather than the moment the node reconnects. That is exactly why a
        // /d2r restart visibly updated every VM and appeared to do nothing to the node.
        var isNode = string.Equals(agentKind, "host", StringComparison.OrdinalIgnoreCase);
        if (!isNode && !string.Equals(agentKind, "vm", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_autoUpdate.Enabled)
        {
            _logger.LogDebug(
                "Skipping auto-update for {AgentId}: {Reason}",
                agentId,
                _autoUpdate.Reason);
            return;
        }

        // Shared with the fleet sweep: both paths offer updates, and after a master restart they
        // line up on the same satellite at the same version within seconds of each other.
        if (!_updateGate.TryBeginOffer(agentId, version))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var retryable = false;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                var result = await SendCommandAsync(
                    agentId,
                    "self_update",
                    new
                    {
                        initiatedBy = "host",
                        hostVersion = AgentVersion.Current()
                    },
                    TimeSpan.FromSeconds(45));

                if (result.Ok)
                {
                    if (TryReadSelfUpdateStarted(result.Data, out var currentVersion, out var latestVersion, out var logPath))
                    {
                        _notifications.Enqueue(
                            SatelliteUpdateNotifications.FormatStarted(
                                agentId,
                                isNode,
                                version,
                                currentVersion,
                                latestVersion,
                                logPath));
                    }

                    _logger.LogInformation(
                        "Auto-update command completed for {AgentId}: {Message}",
                        agentId,
                        result.Message);
                }
                else
                {
                    // A worker old enough to predate the self_update command answers "unsupported
                    // worker command" and can never update itself out of that state. That used to
                    // be a log line nobody reads, which is how a node sits behind indefinitely, so
                    // say it in Discord. The per-version attempt key keeps it to once per build.
                    if (isNode)
                    {
                        _notifications.Enqueue(
                            $"D2RHost worker node `{agentId}` did not accept an update: {result.Message}"
                                + "\nA node this far behind needs one manual update before it can self-update again.");
                    }

                    _logger.LogWarning(
                        "Auto-update command failed for {AgentId}: {Message}",
                        agentId,
                        result.Message);
                }
            }
            catch (Exception ex)
            {
                // Only a transport failure is worth retrying. A satellite that answered and
                // declined has given a real answer, and asking again every reconnect would
                // repost the same Discord warning forever.
                retryable = true;
                _logger.LogWarning(ex, "Auto-update command failed for {AgentId}.", agentId);
            }
            finally
            {
                _updateGate.CompleteOffer(agentId, version, retryable);
            }
        });
    }

    private static bool TryReadSelfUpdateStarted(
        JsonElement? data,
        out string? currentVersion,
        out string? latestVersion,
        out string? logPath)
    {
        currentVersion = null;
        latestVersion = null;
        logPath = null;
        if (data is not { } root
            || !root.TryGetProperty("updateStarted", out var started)
            || started.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !started.GetBoolean())
        {
            return false;
        }

        currentVersion = TryGetString(root, "currentVersion");
        latestVersion = TryGetString(root, "latestVersion");
        logPath = TryGetString(root, "logPath");
        return true;
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private void UpdateStatus(string agentId, WebSocket socket, JsonElement root)
    {
        if (!_agents.TryGetValue(agentId, out var agent) || !agent.HasSocket(socket))
        {
            return;
        }

        var statusJson = root.GetProperty("status").GetRawText();
        var previousConnectivity = string.Equals(agent.Kind, "host", StringComparison.OrdinalIgnoreCase)
            ? GetAdvertisedAgentConnectivity(agent.LastStatusJson)
            : null;
        agent.UpdateStatus(DateTimeOffset.UtcNow, statusJson);
        if (_agents.TryGetValue(agentId, out var current)
            && ReferenceEquals(current, agent)
            && current.HasSocket(socket))
        {
            _db.UpsertAgentStatus(agent.Id, agent.Kind, connected: true, statusJson);
        }

        if (previousConnectivity is not null)
        {
            // A worker cannot reach Discord itself, so anything it needs an operator to see rides
            // its heartbeat instead. It clears its mailbox as it sends, so each alert arrives once.
            foreach (var alert in ReadWorkerAlerts(statusJson))
            {
                _notifications.Enqueue(alert);
                _logger.LogWarning("Worker node {AgentId} reported: {Alert}", agentId, alert);
            }
        }

        if (previousConnectivity is not null
            && !string.Equals(
                previousConnectivity,
                GetAdvertisedAgentConnectivity(statusJson),
                StringComparison.Ordinal))
        {
            ConnectivityChanged?.Invoke();
        }
    }

    private static IReadOnlyList<string> ReadWorkerAlerts(string? statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(statusJson);
            if (!document.RootElement.TryGetProperty("alerts", out var alerts)
                || alerts.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return alerts.EnumerateArray()
                .Where(alert => alert.ValueKind == JsonValueKind.String)
                .Select(alert => alert.GetString())
                .Where(alert => !string.IsNullOrWhiteSpace(alert))
                .Select(alert => alert!)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? GetAdvertisedAgentConnectivity(string? statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(statusJson);
            if (!document.RootElement.TryGetProperty("agents", out var agents)
                || agents.ValueKind != JsonValueKind.Array)
            {
                return "";
            }

            return string.Join(
                "|",
                agents.EnumerateArray()
                    .Select(agent =>
                    {
                        if (agent.ValueKind != JsonValueKind.Object)
                        {
                            return "";
                        }

                        var id = agent.TryGetProperty("id", out var idProperty)
                            && idProperty.ValueKind == JsonValueKind.String
                            ? idProperty.GetString() ?? ""
                            : "";
                        var connected = agent.TryGetProperty("snapshot", out var snapshot)
                            && snapshot.ValueKind == JsonValueKind.Object
                            && snapshot.TryGetProperty("connected", out var connectedProperty)
                            && connectedProperty.ValueKind == JsonValueKind.True;
                        return $"{id.ToUpperInvariant()}:{connected}";
                    })
                    .OrderBy(value => value, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return "";
        }
    }

    private void CompleteCommand(string sourceAgentId, WebSocket socket, JsonElement root)
    {
        var commandId = root.GetProperty("commandId").GetString() ?? "";
        if (!_pending.TryGetValue(commandId, out var pending)
            || !string.Equals(pending.AgentId, sourceAgentId, StringComparison.OrdinalIgnoreCase)
            || !ReferenceEquals(pending.Socket, socket)
            || !_pending.TryRemove(commandId, out pending))
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

        if (string.Equals(pending.Command, "status", StringComparison.OrdinalIgnoreCase)
            && result.Data is { ValueKind: JsonValueKind.Object } resultData)
        {
            UpdateStatusJson(pending.AgentId, socket, resultData.GetRawText());
        }

        _db.InsertCommandHistory(commandId, pending.AgentId, pending.Command, result.Ok, result.Message);
        pending.TrySetResult(result);
    }

    private void UpdateStatusJson(string agentId, WebSocket socket, string statusJson)
    {
        if (!_agents.TryGetValue(agentId, out var agent) || !agent.HasSocket(socket))
        {
            return;
        }

        agent.UpdateStatus(DateTimeOffset.UtcNow, statusJson);
        if (_agents.TryGetValue(agentId, out var current)
            && ReferenceEquals(current, agent)
            && current.HasSocket(socket))
        {
            _db.UpsertAgentStatus(agent.Id, agent.Kind, connected: true, statusJson);
        }
    }

    private void FailPendingCommands(string agentId, WebSocket socket)
    {
        var failedCount = 0;
        foreach (var pair in _pending)
        {
            if (!string.Equals(pair.Value.AgentId, agentId, StringComparison.OrdinalIgnoreCase)
                || !ReferenceEquals(pair.Value.Socket, socket)
                || !_pending.TryRemove(pair.Key, out var pending))
            {
                continue;
            }

            failedCount++;
            pending.TrySetException(new InvalidOperationException(
                $"Agent \"{agentId}\" disconnected before command \"{pending.Command}\" completed."));
        }

        if (failedCount > 0)
        {
            _logger.LogWarning(
                "Failed {PendingCommandCount} pending command(s) after agent {AgentId} disconnected.",
                failedCount,
                agentId);
        }
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
        int? HeartbeatSeconds = null,
        bool ProbeOnly = false);

    private sealed class ConnectedAgent
    {
        private readonly object _statusLock = new();
        private WebSocket? _socket;
        private DateTimeOffset? _lastSeenAt;
        private string? _lastStatusJson;
        private DateTimeOffset? _statusReceivedAt;

        public ConnectedAgent(
            string id,
            string kind,
            string? displayName,
            string? hostName,
            string? version,
            DateTimeOffset? connectedAt,
            DateTimeOffset? lastSeenAt,
            string? lastStatusJson,
            DateTimeOffset? statusReceivedAt,
            int? heartbeatSeconds,
            WebSocket? socket)
        {
            Id = id;
            Kind = kind;
            DisplayName = displayName;
            HostName = hostName;
            Version = version;
            ConnectedAt = connectedAt;
            HeartbeatSeconds = heartbeatSeconds;
            _lastSeenAt = lastSeenAt;
            _lastStatusJson = lastStatusJson;
            _statusReceivedAt = statusReceivedAt;
            _socket = socket;
        }

        public string Id { get; }
        public string Kind { get; }
        public string? DisplayName { get; }
        public string? HostName { get; }
        public string? Version { get; }
        public DateTimeOffset? ConnectedAt { get; }
        public int? HeartbeatSeconds { get; }
        public DateTimeOffset? LastSeenAt
        {
            get
            {
                lock (_statusLock)
                {
                    return _lastSeenAt;
                }
            }
        }

        public string? LastStatusJson
        {
            get
            {
                lock (_statusLock)
                {
                    return _lastStatusJson;
                }
            }
        }

        public DateTimeOffset? StatusReceivedAt
        {
            get
            {
                lock (_statusLock)
                {
                    return _statusReceivedAt;
                }
            }
        }

        public WebSocket? Socket => Volatile.Read(ref _socket);
        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public bool HasSocket(WebSocket socket) => ReferenceEquals(Socket, socket);

        public bool TryDetachSocket(WebSocket socket)
        {
            return ReferenceEquals(
                Interlocked.CompareExchange(ref _socket, null, socket),
                socket);
        }

        public void UpdateStatus(DateTimeOffset lastSeenAt, string statusJson)
        {
            lock (_statusLock)
            {
                _lastSeenAt = lastSeenAt;
                _lastStatusJson = statusJson;
                _statusReceivedAt = lastSeenAt;
            }
        }

        public AgentSnapshot ToSnapshot(bool connected)
        {
            lock (_statusLock)
            {
                return new AgentSnapshot(
                    Id,
                    Kind,
                    DisplayName,
                    HostName,
                    Version,
                    connected,
                    ConnectedAt,
                    _lastSeenAt,
                    _lastStatusJson,
                    _statusReceivedAt);
            }
        }
    }

    private sealed class PendingCommand
    {
        private readonly TaskCompletionSource<CommandResultInfo> _source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingCommand(string agentId, string command, WebSocket socket)
        {
            AgentId = agentId;
            Command = command;
            Socket = socket;
        }

        public string AgentId { get; }
        public string Command { get; }
        public WebSocket Socket { get; }
        public Task<CommandResultInfo> Task => _source.Task;

        public void TrySetResult(CommandResultInfo result) => _source.TrySetResult(result);
        public void TrySetException(Exception exception) => _source.TrySetException(exception);
        public void TrySetCanceled(CancellationToken cancellationToken) =>
            _source.TrySetCanceled(cancellationToken);
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
    string? LastStatusJson,
    DateTimeOffset? StatusReceivedAt = null);

public sealed record CommandResultInfo(
    string AgentId,
    string CommandId,
    bool Ok,
    string Message,
    JsonElement? Data);
