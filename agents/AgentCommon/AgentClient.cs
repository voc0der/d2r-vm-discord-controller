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

    private readonly TConfig _config;
    private readonly string _agentKind;
    private readonly Action<string> _log;

    public AgentClient(TConfig config, string agentKind, Action<string>? log = null)
    {
        _config = config;
        _agentKind = agentKind;
        _log = log ?? Console.WriteLine;
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
                var exitRequested = await RunOnceAsync(statusFactory, commandHandler, cancellationToken);
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

        await socket.ConnectAsync(new Uri(_config.ControllerUrl), timeoutCts.Token);
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
        await socket.ConnectAsync(new Uri(_config.ControllerUrl), cancellationToken);
        await SendHelloAsync(socket, sendLock, probeOnly: false, cancellationToken);

        _log("Connected to controller.");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = SendHeartbeatAsync(socket, sendLock, statusFactory, linkedCts.Token);

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var raw = await ReceiveStringAsync(socket, cancellationToken);
                if (raw is null)
                {
                    break;
                }

                var exitRequested = await HandleControllerMessageAsync(socket, sendLock, raw, commandHandler, cancellationToken);
                if (exitRequested)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            linkedCts.Cancel();
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

    private async Task SendHeartbeatAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendLock,
        Func<CancellationToken, Task<object>> statusFactory,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(_config.HeartbeatSeconds, 5));

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var status = await statusFactory(cancellationToken);
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

    private async Task<bool> HandleControllerMessageAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendLock,
        string raw,
        Func<CommandRequest, CancellationToken, Task<CommandResult>> commandHandler,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(raw);
        var type = document.RootElement.GetProperty("type").GetString();
        if (!string.Equals(type, "command", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var envelope = JsonSerializer.Deserialize<ControllerCommandEnvelope>(raw, JsonOptions)
            ?? throw new InvalidOperationException("Controller command payload was invalid.");

        CommandResult result;
        try
        {
            var request = new CommandRequest(envelope.CommandId, envelope.Command, envelope.Args);
            result = await commandHandler(request, cancellationToken);
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
            cancellationToken);

        return result.ExitAfterResult;
    }

    private static string GetCurrentVersionText()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AgentClient<TConfig>).Assembly;
        return assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "0.0.0";
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
