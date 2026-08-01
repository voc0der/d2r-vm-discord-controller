using AgentCommon;
using D2RHost;

var configPath = args.FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("CONFIG_PATH")
    ?? @"C:\D2ROps\d2r-host.config.json";

var logsDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", "logs");
var logFilePath = LogFileRotator.RotateAndPrepare(logsDir);
var updateNotificationPath = HostUpdateNotificationStore.GetPath(configPath);

void LogStartup(string message)
{
    Console.WriteLine(message);
    File.AppendAllText(logFilePath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [Startup] {message}{Environment.NewLine}");
}

try
{
    var hostUpdate = await SelfUpdater.CheckAndStartUpdateAsync(
        SelfUpdateOptions.D2RHost(args) with { CompletionMarkerPath = updateNotificationPath },
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
    foreach (var warning in HostConfigLoader.GetWarnings(config))
    {
        LogStartup($"Configuration warning: {warning}");
    }

    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls($"http://0.0.0.0:{config.HttpPort}");
    builder.Logging.AddProvider(new FileLoggerProvider(logFilePath));

    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton(new HostRuntimeOptions(configPath, args));
    builder.Services.AddSingleton(agentAutoUpdate);
    builder.Services.AddSingleton<DiscordNotificationQueue>();
    builder.Services.AddSingleton<SatelliteUpdateGate>();
    builder.Services.AddSingleton<HostUpdateNotificationStore>();
    builder.Services.AddSingleton<AppDb>();
    builder.Services.AddSingleton<AgentRegistry>();
    builder.Services.AddSingleton<FleetRegistry>();
    builder.Services.AddSingleton<HyperVOperations>();
    builder.Services.AddSingleton<ILocalVmPowerOperations>(provider =>
        provider.GetRequiredService<HyperVOperations>());
    builder.Services.AddSingleton<VmPowerLifecycleCoordinator>();
    builder.Services.AddSingleton<FleetHostOperations>();
    builder.Services.AddSingleton<HostSystemOperations>();
    builder.Services.AddSingleton<WorkerNodeOperations>();
    builder.Services.AddSingleton<WorkerNodeLink>();
    builder.Services.AddSingleton<FollowTemplateStore>();
    builder.Services.AddSingleton<DiscordBot>();
    builder.Services.AddSingleton<IHostFirewallBackend, WindowsComHostFirewallBackend>();
    builder.Services.AddSingleton<IHostNetworkAddressProvider, SystemHostNetworkAddressProvider>();
    builder.Services.AddSingleton<HostFirewallManager>();
    builder.Services.AddSingleton<VmPowerRestoreService>();
    builder.Services.AddSingleton<IHostedService>(provider =>
        provider.GetRequiredService<VmPowerRestoreService>());
    builder.Services.AddSingleton<IHostedService>(provider =>
        provider.GetRequiredService<HostFirewallManager>());
    builder.Services.AddSingleton<FleetAgentUpdater>();
    if (config.IsMaster)
    {
        // Master-only: a worker relays follow-template commands for its VMs but never owns the
        // fleet's bind, so a sweep there would have nothing authoritative to sweep towards.
        builder.Services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<FollowTemplateStore>());
        // Also master-only: the master is the only process with a view of the whole fleet,
        // including VM agents whose connections belong to a worker.
        builder.Services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<FleetAgentUpdater>());
    }

    var app = builder.Build();
    var db = app.Services.GetRequiredService<AppDb>();
    if (db.JournalMode != "wal")
    {
        // Not fatal - the host runs fine on the rollback journal, just with an fsync on every
        // write, which is what puts a Discord command handler at risk of missing its
        // three-second acknowledgement deadline. The usual cause is another process still
        // holding the database file.
        LogStartup(
            $"The host database is in journal mode '{db.JournalMode}', not WAL. Writes will fsync "
                + "per commit and may stall Discord commands; check whether another host process "
                + "still has the database open.");
    }

    var vmLifecycle = app.Services.GetRequiredService<VmPowerLifecycleCoordinator>();
    var initialVmRestore = await vmLifecycle.RestorePendingAsync();
    if (!initialVmRestore.Complete)
    {
        LogStartup(
            "VM restore remains pending and will retry in the background: "
                + string.Join(", ", initialVmRestore.PendingVmNames)
                + (initialVmRestore.Failures.Count == 0
                    ? ""
                    : ". " + string.Join("; ", initialVmRestore.Failures)));
    }

    var firewallManager = app.Services.GetRequiredService<HostFirewallManager>();
    _ = firewallManager.ReconcileNow();

    app.UseWebSockets();

    app.MapGet("/healthz", (HostConfig hostConfig, AgentRegistry localRegistry, FleetRegistry fleetRegistry, HostFirewallManager firewall) =>
    {
        var agents = hostConfig.IsMaster
            ? fleetRegistry.Snapshot()
            : localRegistry.Snapshot();
        var connected = agents.Count(agent => agent.Connected);
        var firewallStatus = firewall.Status;
        return Results.Json(new
        {
            ok = firewallStatus.Ok,
            mode = hostConfig.Mode,
            nodeId = hostConfig.NodeId,
            agents = agents.Count,
            agentsConfigured = agents.Count,
            agentsConnected = connected,
            nodes = hostConfig.IsMaster ? fleetRegistry.NodeSnapshot() : null,
            firewall = firewallStatus
        });
    });

    app.MapGet("/agents", (HostConfig hostConfig, AgentRegistry localRegistry, FleetRegistry fleetRegistry) =>
    {
        return Results.Json(hostConfig.IsMaster ? fleetRegistry.Snapshot() : localRegistry.Snapshot());
    });

    app.MapGet("/nodes", (HostConfig hostConfig, FleetRegistry fleetRegistry) =>
    {
        return hostConfig.IsMaster
            ? Results.Json(fleetRegistry.NodeSnapshot())
            : Results.Json(new
            {
                nodeId = hostConfig.NodeId,
                mode = hostConfig.Mode,
                masterUrl = hostConfig.MasterUrl
            });
    });

    app.MapGet("/config/accounts", (HostConfig hostConfig, FleetRegistry fleetRegistry) =>
    {
        var accounts = hostConfig.IsMaster ? fleetRegistry.Accounts : hostConfig.Accounts;
        return Results.Json(accounts.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray());
    });

    RequestDelegate AgentEndpoint(string expectedAgentKind) => async context =>
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket required.");
            return;
        }

        var registry = context.RequestServices.GetRequiredService<AgentRegistry>();
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await registry.HandleWebSocketAsync(socket, context.RequestAborted, expectedAgentKind);
    };
    app.Map("/agent", AgentEndpoint("vm"));
    app.Map("/node", AgentEndpoint("host"));

    DiscordBot? bot = null;
    Task? workerLinkTask = null;
    if (config.IsMaster)
    {
        // A sleep that fails after the command has already answered would otherwise only reach
        // the log file, which is unreadable precisely when it matters: the machine is headless
        // and a failed sleep looks exactly like a successful one.
        var localSystem = app.Services.GetRequiredService<HostSystemOperations>();
        var notifications = app.Services.GetRequiredService<DiscordNotificationQueue>();
        localSystem.SleepFailed += message => notifications.Enqueue(message);

        bot = app.Services.GetRequiredService<DiscordBot>();
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
    }
    else
    {
        var workerLink = app.Services.GetRequiredService<WorkerNodeLink>();
        workerLinkTask = Task.Run(
            async () =>
            {
                await workerLink.RunForeverAsync(app.Lifetime.ApplicationStopping);

                // The link only returns on its own when a command asked this process to exit,
                // which today means self_update. Unlike the VM agent - where the link *is* the
                // process - a worker is a web host, so the loop ending leaves it running and the
                // updater sits on Wait-Process until someone restarts the machine by hand.
                if (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
                {
                    LogStartup("Worker link requested process exit (self-update); stopping host.");
                    app.Lifetime.StopApplication();
                }
            },
            CancellationToken.None);
    }

    await app.RunAsync();
    if (workerLinkTask is not null)
    {
        try
        {
            await workerLinkTask;
        }
        catch (OperationCanceledException)
        {
            // Normal worker shutdown.
        }
    }

    return 0;
}
catch (Exception ex)
{
    // This process is respawned unattended (e.g. via the /d2r restart Discord command),
    // so blocking on Console.ReadLine() here would hang forever with nobody at the
    // console to answer it.
    Console.Error.WriteLine(ex);
    LogStartup($"Fatal startup exception: {ex}");
    return 1;
}
