using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AgentCommon;

public sealed class AgentClient<TConfig> where TConfig : AgentConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    private readonly TConfig _config;
    private readonly string _agentKind;
    private readonly Action<string> _log;
    private readonly Action<AgentConnectionState>? _connectionStateChanged;
    private AgentConnectionState? _lastConnectionState;

    public AgentClient(
        TConfig config,
        string agentKind,
        Action<string>? log = null,
        Action<AgentConnectionState>? connectionStateChanged = null)
    {
        _config = config;
        _agentKind = agentKind;
        _log = log ?? Console.WriteLine;
        _connectionStateChanged = connectionStateChanged;
    }

    public async Task RunForeverAsync(
        Func<CancellationToken, Task<object>> statusFactory,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> commandHandler,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(2);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                NotifyConnectionState(AgentConnectionState.Connecting);
                var exitRequested = await RunOnceAsync(statusFactory, commandHandler, cancellationToken);
                NotifyConnectionState(AgentConnectionState.Disconnected);
                if (exitRequested)
                {
                    break;
                }

                delay = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                NotifyConnectionState(AgentConnectionState.Disconnected);
                _log($"Controller connection failed: {ex.Message}");
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    public async Task ProbeConnectionAsync(
        Func<CancellationToken, Task<object>> statusFactory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var socket = new ClientWebSocket();
        using var sendLock = new SemaphoreSlim(1, 1);

        await ConnectWithTimeoutAsync(socket, new Uri(_config.ControllerUrl), timeout, cancellationToken);
        await SendHelloAsync(socket, sendLock, probeOnly: true, timeoutCts.Token);

        var raw = await ReceiveStringAsync(socket, timeoutCts.Token)
            ?? throw new InvalidOperationException("Host closed the connection before acknowledging the agent.");

        using var document = JsonDocument.Parse(raw);
        var type = document.RootElement.GetProperty("type").GetString();
        if (!string.Equals(type, "hello_ack", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Host returned an unexpected handshake message: {type ?? "(none)"}.");
        }

        if (document.RootElement.TryGetProperty("ok", out var okProperty) && !okProperty.GetBoolean())
        {
            var message = document.RootElement.TryGetProperty("message", out var messageProperty)
                ? messageProperty.GetString()
                : "Handshake was rejected.";
            throw new InvalidOperationException(message);
        }

        _ = await statusFactory(timeoutCts.Token);
    }

    private async Task<bool> RunOnceAsync(
        Func<CancellationToken, Task<object>> statusFactory,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> commandHandler,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        using var sendLock = new SemaphoreSlim(1, 1);

        _log($"Connecting to {_config.ControllerUrl} as {_config.AgentId}...");
        await ConnectWithTimeoutAsync(socket, new Uri(_config.ControllerUrl), ConnectTimeout, cancellationToken);
        await SendHelloAsync(socket, sendLock, probeOnly: false, cancellationToken);

        _log("Connected to controller.");
        NotifyConnectionState(AgentConnectionState.Connected);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exitRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runningCommands = new ConcurrentDictionary<string, Task>();
        var heartbeatTask = SendHeartbeatAsync(socket, sendLock, statusFactory, linkedCts.Token);

        try
        {
            while (socket.State == WebSocketState.Open && !linkedCts.Token.IsCancellationRequested)
            {
                var raw = await ReceiveStringAsync(socket, linkedCts.Token);
                if (raw is null)
                {
                    break;
                }

                // Keep the receive loop moving while commands run. Heartbeats already
                // send on a separate task; if the receive loop awaits a long menu
                // command, later live status/screenshot commands sit unread and the
                // host reports timeouts despite fresh heartbeat cache.
                HandleControllerMessage(
                    socket,
                    sendLock,
                    raw,
                    commandHandler,
                    linkedCts,
                    exitRequested,
                    runningCommands);
            }

            return exitRequested.Task.IsCompletedSuccessfully;
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested
                                                && !cancellationToken.IsCancellationRequested)
        {
            return exitRequested.Task.IsCompletedSuccessfully;
        }
        finally
        {
            linkedCts.Cancel();
            await WaitForRunningCommandsToSettleAsync(runningCommands.Values);
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }
    }

    private Task SendHelloAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        bool probeOnly,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            socket,
            sendLock,
            new
            {
                type = "hello",
                agentId = _config.AgentId,
                agentKind = _agentKind,
                sharedSecret = _config.SharedSecret,
                version = GetCurrentVersionText(),
                hostName = Environment.MachineName,
                probeOnly
            },
            cancellationToken);
    }

    private static readonly TimeSpan HeartbeatStatusTimeout = TimeSpan.FromSeconds(15);

    private async Task SendHeartbeatAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendLock,
        Func<CancellationToken, Task<object>> statusFactory,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(_config.HeartbeatSeconds, 5));

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var status = await CollectHeartbeatStatusAsync(statusFactory, HeartbeatStatusTimeout, cancellationToken);
            await SendAsync(
                socket,
                sendLock,
                new
                {
                    type = "status",
                    agentId = _config.AgentId,
                    status
                },
                cancellationToken);

            await Task.Delay(interval, cancellationToken);
        }
    }

    // statusFactory's underlying Win32 calls (screen sampling, window enumeration) don't
    // observe CancellationToken once inside a blocking call, so a single wedged detection
    // call could previously hang this await forever - the heartbeat would silently stop,
    // "seen" would freeze, and the host would keep reporting the agent as healthy on a
    // stale timestamp with no signal that detection itself had stalled. Race it against a
    // timeout instead of trusting it to return, and abandon the slow task rather than
    // waiting on it again next cycle.
    private static async Task<object> CollectHeartbeatStatusAsync(
        Func<CancellationToken, Task<object>> statusFactory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var statusTask = statusFactory(cancellationToken);
        var completed = await Task.WhenAny(statusTask, Task.Delay(timeout, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (completed == statusTask)
        {
            return await statusTask;
        }

        return new { error = $"Status collection did not return within {timeout.TotalSeconds:N0}s; a detection call is likely stuck." };
    }

    private void HandleControllerMessage(
        ClientWebSocket socket,
        SemaphoreSlim sendLock,
        string raw,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> commandHandler,
        CancellationTokenSource connectionCts,
        TaskCompletionSource exitRequested,
        ConcurrentDictionary<string, Task> runningCommands)
    {
        using var document = JsonDocument.Parse(raw);
        var type = document.RootElement.GetProperty("type").GetString();
        if (!string.Equals(type, "command", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var envelope = JsonSerializer.Deserialize<ControllerCommandEnvelope>(raw, JsonOptions)
            ?? throw new InvalidOperationException("Controller command payload was invalid.");

        var task = Task.Run(
            () => ExecuteControllerCommandAsync(
                socket,
                sendLock,
                envelope,
                commandHandler,
                connectionCts,
                exitRequested),
            CancellationToken.None);
        runningCommands[envelope.CommandId] = task;
        _ = task.ContinueWith(
            completed =>
            {
                runningCommands.TryRemove(envelope.CommandId, out _);
                if (completed.Exception is not null
                    && completed.Exception.GetBaseException() is not OperationCanceledException)
                {
                    _log($"Controller command \"{envelope.Command}\" failed before a result could be sent: {completed.Exception.GetBaseException().Message}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ExecuteControllerCommandAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendLock,
        ControllerCommandEnvelope envelope,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> commandHandler,
        CancellationTokenSource connectionCts,
        TaskCompletionSource exitRequested)
    {
        var commandTimeout = envelope.TimeoutMs is > 0
            ? TimeSpan.FromMilliseconds(envelope.TimeoutMs.Value)
            : (TimeSpan?)null;
        using var commandTimeoutCts = commandTimeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(connectionCts.Token)
            : null;
        if (commandTimeout.HasValue)
        {
            commandTimeoutCts!.CancelAfter(commandTimeout.Value);
        }

        var commandToken = commandTimeoutCts?.Token ?? connectionCts.Token;
        CommandResult result;
        try
        {
            var request = new CommandRequest(envelope.CommandId, envelope.Command, envelope.Args);
            result = await commandHandler(request, commandToken);
        }
        catch (OperationCanceledException) when (commandTimeoutCts?.IsCancellationRequested == true
                                                && !connectionCts.IsCancellationRequested)
        {
            result = CommandResult.Failure(
                $"Command \"{envelope.Command}\" exceeded agent-side timeout of {commandTimeout!.Value.TotalSeconds:N0}s.");
        }
        catch (Exception ex)
        {
            result = CommandResult.Failure(ex.Message);
        }

        await SendAsync(
            socket,
            sendLock,
            new
            {
                type = "command_result",
                agentId = _config.AgentId,
                commandId = envelope.CommandId,
                ok = result.Ok,
                message = result.Message,
                data = result.Data
            },
            connectionCts.Token);

        if (result.ExitAfterResult)
        {
            exitRequested.TrySetResult();
            connectionCts.Cancel();
        }
    }

    private static async Task WaitForRunningCommandsToSettleAsync(IEnumerable<Task> commands)
    {
        var tasks = commands.ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        var allCommands = Task.WhenAll(tasks);
        var settled = await Task.WhenAny(allCommands, Task.Delay(TimeSpan.FromSeconds(5)));
        if (settled == allCommands)
        {
            try
            {
                await allCommands;
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }
    }

    private static string GetCurrentVersionText()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AgentClient<TConfig>).Assembly;
        return assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "0.0.0";
    }

    private void NotifyConnectionState(AgentConnectionState state)
    {
        if (_lastConnectionState == state)
        {
            return;
        }

        _lastConnectionState = state;
        _connectionStateChanged?.Invoke(state);
    }

    private static async Task ConnectWithTimeoutAsync(
        ClientWebSocket socket,
        Uri uri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var connectTask = socket.ConnectAsync(uri, timeoutCts.Token);
        var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
        var completed = await Task.WhenAny(connectTask, timeoutTask);
        if (completed == connectTask)
        {
            await connectTask;
            return;
        }

        _ = connectTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException($"Connecting to {uri} timed out after {timeout.TotalSeconds:N0}s.");
    }

    private static async Task SendAsync(
        WebSocket socket,
        SemaphoreSlim sendLock,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task<string?> ReceiveStringAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

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
}
