using AgentCommon;
using D2RHost;

var configPath = args.FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("CONFIG_PATH")
    ?? @"C:\D2ROps\d2r-host.config.json";

var logsDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "logs");
var logFilePath = LogFileRotator.RotateAndPrepare(logsDir);

void LogStartup(string message)
{
    Console.WriteLine(message);
    File.AppendAllText(logFilePath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [Startup] {message}{Environment.NewLine}");
}

try
{
    var hostUpdate = await SelfUpdater.CheckAndStartUpdateAsync(
        SelfUpdateOptions.D2RHost(args),
        requirePrompt: false);
    if (!hostUpdate.Ok)
    {
        LogStartup($"Host update check failed; satellite auto-update disabled: {hostUpdate.Message}");
    }
    else if (!hostUpdate.CheckedLatest)
    {
        LogStartup($"Host update check skipped; satellite auto-update disabled: {hostUpdate.Message}");
    }

    if (hostUpdate.UpdateStarted)
    {
        return 0;
    }

    var agentAutoUpdate = new AgentAutoUpdateState(
        Enabled: hostUpdate.Ok && hostUpdate.CheckedLatest,
        Reason: hostUpdate.Message);

    var config = HostConfigLoader.LoadOrCreate(configPath);
    _ = WindowsFirewallSelfHeal.EnsureHostInboundTcp(config.HttpPort, LogStartup);

    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls($"http://0.0.0.0:{config.HttpPort}");
    builder.Logging.AddProvider(new FileLoggerProvider(logFilePath));

    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton(new HostRuntimeOptions(configPath, args));
    builder.Services.AddSingleton(agentAutoUpdate);
    builder.Services.AddSingleton<AppDb>();
    builder.Services.AddSingleton<AgentRegistry>();
    builder.Services.AddSingleton<HyperVOperations>();
    builder.Services.AddSingleton<DiscordBot>();

    var app = builder.Build();

    app.UseWebSockets();

    app.MapGet("/healthz", (AgentRegistry registry) =>
    {
        return Results.Json(new
        {
            ok = true,
            agents = registry.Snapshot().Count
        });
    });

    app.MapGet("/agents", (AgentRegistry registry) =>
    {
        return Results.Json(registry.Snapshot());
    });

    app.MapGet("/config/accounts", () =>
    {
        return Results.Json(config.Accounts.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray());
    });

    app.Map("/agent", async context =>
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket required.");
            return;
        }

        var registry = context.RequestServices.GetRequiredService<AgentRegistry>();
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await registry.HandleWebSocketAsync(socket, context.RequestAborted);
    });

    var bot = app.Services.GetRequiredService<DiscordBot>();
    await bot.StartAsync();
    app.Lifetime.ApplicationStopping.Register(() =>
    {
        try
        {
            bot.StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Shutdown best effort.
        }
    });

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    // This process is respawned unattended (e.g. via the /restart Discord command),
    // so blocking on Console.ReadLine() here would hang forever with nobody at the
    // console to answer it.
    Console.Error.WriteLine(ex);
    LogStartup($"Fatal startup exception: {ex}");
    return 1;
}
