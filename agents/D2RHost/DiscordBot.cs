using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AgentCommon;
using Discord;
using Discord.WebSocket;

namespace D2RHost;

public sealed class DiscordBot
{
    // 150s left ~0s margin over the agent's own internal retry budget on slower VM
    // hardware (Battle.net launch + splash-skip retries can legitimately take several
    // minutes), so the command was timing out moments before D2R would have reached
    // the character screen on its own. 420s gives real headroom above that budget.
    private static readonly TimeSpan ReadyCommandTimeout = TimeSpan.FromSeconds(420);
    private static readonly TimeSpan JoinPrepareCommandTimeout = TimeSpan.FromSeconds(35);
    private const int JoinAutoDefaultIdleMinutes = 60;

    private readonly HostConfig _config;
    private readonly HostRuntimeOptions _runtime;
    private readonly AgentRegistry _registry;
    private readonly HyperVOperations _hyperV;
    private readonly AppDb _db;
    private readonly ILogger<DiscordBot> _logger;
    private readonly DiscordSocketClient _client;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private IUserMessage? _activeSessionMessage;
    private string? _activeSessionGameName;
    private DateTimeOffset? _activeSessionStartedUtc;
    private int _activeSessionExpected;
    private int _activeSessionJoined;
    private string? _activeSessionRepresentativeAgentId;
    private bool _commandsRegistered;
    private GameNameTemplate? _gameTemplate;
    private readonly SemaphoreSlim _joinAutoLock = new(1, 1);
    private CancellationTokenSource? _joinAutoCts;
    private string? _joinAutoStopReason;
    private IUserMessage? _joinAutoMonitorMessage;
    private string? _joinAutoMonitorGameName;
    private int _joinAutoMonitorJoined;
    private int _joinAutoMonitorTotal;
    private int _joinAutoCyclesCompleted;
    private DateTimeOffset? _joinAutoStartedUtc;
    private const int FollowAutoDefaultIdleMinutes = 60;
    private readonly SemaphoreSlim _followAutoLock = new(1, 1);
    private CancellationTokenSource? _followAutoCts;
    private string? _followAutoStopReason;
    private string? _followBoundAccountKey;

    // join-all's "join the last known game if it's recent" (issue #20, item 5) needs to mean
    // a game create-game-all/join-all actually acted on, not just whatever /game set last had -
    // a 3-day-old manual /game set shouldn't be silently (re)joined.
    private static readonly TimeSpan ActiveGameFreshness = TimeSpan.FromHours(1);

    public DiscordBot(
        HostConfig config,
        HostRuntimeOptions runtime,
        AgentRegistry registry,
        HyperVOperations hyperV,
        AppDb db,
        ILogger<DiscordBot> logger)
    {
        _config = config;
        _runtime = runtime;
        _registry = registry;
        _hyperV = hyperV;
        _db = db;
        _logger = logger;
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds
        });
        _client.Log += OnDiscordLogAsync;
        _client.Ready += OnReadyAsync;
        _client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    public async Task StartAsync()
    {
        if (_config.DisableDiscord)
        {
            _logger.LogWarning("Discord is disabled.");
            return;
        }

        await _client.LoginAsync(TokenType.Bot, _config.DiscordToken);
        await _client.StartAsync();
    }

    public async Task StopAsync()
    {
        if (_config.DisableDiscord)
        {
            return;
        }

        await _client.StopAsync();
        await _client.LogoutAsync();
        await _client.DisposeAsync();
    }

    private async Task OnReadyAsync()
    {
        _logger.LogInformation("Discord bot logged in as {User}", _client.CurrentUser);
        if (_commandsRegistered)
        {
            return;
        }

        var commands = DiscordSlashCommands.Build();
        if (_config.DiscordGuildId is { } guildId)
        {
            var guild = _client.GetGuild(guildId)
                ?? throw new InvalidOperationException($"Discord guild {guildId} is not visible to the bot.");
            await guild.BulkOverwriteApplicationCommandAsync(commands);
            _logger.LogInformation("Registered {Count} guild slash commands in {GuildId}.", commands.Length, guildId);
        }
        else
        {
            await ((IDiscordClient)_client).BulkOverwriteGlobalApplicationCommand(commands);
            _logger.LogInformation("Registered {Count} global slash commands.", commands.Length);
        }

        _commandsRegistered = true;
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (!IsAllowed(command.User.Id))
        {
            await command.RespondAsync("Not authorized for this controller.", ephemeral: true);
            return;
        }

        try
        {
            var context = SlashContext.From(command);
            switch (command.CommandName)
            {
                case "d2r":
                    await HandleD2RAsync(context);
                    break;
                case "vm":
                    await HandleVmAsync(context);
                    break;
                case "game":
                    await HandleGameAsync(context);
                    break;
                case "config":
                    await HandleConfigAsync(context);
                    break;
                case "restart":
                    await HandleRestartAsync(context);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discord command failed.");
            var content = $"Command failed: {ex.Message}";
            if (command.HasResponded)
            {
                await command.ModifyOriginalResponseAsync(properties => properties.Content = content);
            }
            else
            {
                await command.RespondAsync(content, ephemeral: true);
            }
        }
    }

    private async Task HandleD2RAsync(SlashContext context)
    {
        var subcommand = context.SubcommandName;

        if (subcommand == "health")
        {
            await context.Command.RespondAsync(FormatHealth(), ephemeral: true);
            return;
        }

        if (subcommand == "status")
        {
            await context.Command.DeferAsync(ephemeral: true);
            var accountKey = context.GetString("account");
            var content = accountKey is null
                ? await FormatAllAccountStatusesLiveAsync(CancellationToken.None)
                : await FormatAccountStatusLiveAsync(accountKey, CancellationToken.None);
            await context.Command.ModifyOriginalResponseAsync(properties => properties.Content = content);
            return;
        }

        if (subcommand == "start-all")
        {
            await QueueAllCommandsAsync(
                context,
                "menu_ready",
                (accountKey, account) => BuildAccountArgs(accountKey, account),
                ReadyCommandTimeout,
                displayName: "ready");
            return;
        }

        if (subcommand == "join-all")
        {
            var game = ResolveJoinAllInput(context);
            if (game is null)
            {
                await context.Command.RespondAsync(
                    "Nothing to join: no recent game and no template set. Pass name, or set one with /game set or /d2r template.",
                    ephemeral: true);
                return;
            }

            await QueueJoinAllAsync(context, game, watch: context.GetBool("watch") ?? false);
            return;
        }

        if (subcommand == "create-game-all")
        {
            await QueueCreateGameAllAsync(context, ResolveCreateGameAllInput(context), watch: context.GetBool("watch") ?? false);
            return;
        }

        if (subcommand == "template")
        {
            _gameTemplate = new GameNameTemplate(
                context.GetRequiredString("name"),
                BlankToNull(context.GetString("password")));
            var passwordSuffix = _gameTemplate.Password is null ? string.Empty : $"/{_gameTemplate.Password}";
            await context.Command.RespondAsync(
                $"Template set: {_gameTemplate.Name}1{passwordSuffix} is next. "
                    + "create-game-all/join-all with no name will use this until /d2r template is set again or the host restarts.",
                ephemeral: true);
            return;
        }

        if (subcommand == "join-auto")
        {
            if (context.GetBool("stop") == true)
            {
                await StopJoinAutoAsync(context);
                return;
            }

            await StartJoinAutoAsync(
                context,
                Math.Max(context.GetInt("delay") ?? 0, 0),
                context.GetBool("watch") == true,
                TimeSpan.FromMinutes(Math.Max(context.GetInt("idle-minutes") ?? JoinAutoDefaultIdleMinutes, 1)));
            return;
        }

        if (subcommand == "follow-all")
        {
            await QueueAllCommandsAsync(
                context,
                "menu_join_friend",
                (accountKey, account) => BuildMenuArgs(accountKey, account, null, context),
                TimeSpan.FromSeconds(210),
                readyFirstIfNotMenuReady: true);
            return;
        }

        if (subcommand == "save-exit-all")
        {
            // menu_save_exit's own automation (Escape, click, wait up to ~12s) is fast - the
            // budget here almost entirely covers time spent waiting for the agent's command
            // gate, which a preceding create-game-all/join-all can still be holding for as long
            // as those commands' own 210s timeout allows. A shorter budget here doesn't make
            // save-exit faster; it just means the gate frees up, save-exit actually runs and
            // succeeds, and the result arrives after the host already gave up and discarded the
            // pending request - reported as a failure even though it worked.
            await QueueAllCommandsAsync(
                context,
                "menu_save_exit",
                (accountKey, account) => BuildAccountArgs(accountKey, account),
                TimeSpan.FromSeconds(210));
            return;
        }

        if (subcommand == "quit-all")
        {
            // Same gate-wait headroom reasoning as save-exit-all above, not yet applied here
            // until issue #24: quit_d2r's own work (focus, Alt+F4, 2s settle) is fast, but it
            // shares _commandGate with whatever a join-auto retry loop (or any other 210s-class
            // command) is mid-attempt on. A real run showed quit_d2r failing for 2/3 accounts
            // with "exceeded agent-side timeout of 25s" while join-auto was actively retrying a
            // join in the background - the gate was always going to free up, just not within 30s.
            await CancelJoinAutoIfRunningAsync("quit-all was called");
            await CancelFollowAutoIfRunningAsync("quit-all was called");
            await QueueAllCommandsAsync(
                context,
                "quit_d2r",
                (accountKey, account) => BuildAccountArgs(accountKey, account),
                TimeSpan.FromSeconds(210));
            return;
        }

        if (subcommand == "follow" && (context.GetBool("bind") is not null || context.GetBool("auto") is not null))
        {
            if (context.GetBool("auto") is { } autoFlag)
            {
                if (!autoFlag)
                {
                    await StopFollowAutoAsync(context);
                    return;
                }

                await StartFollowAutoAsync(
                    context,
                    Math.Max(context.GetInt("delay") ?? 0, 0),
                    TimeSpan.FromMinutes(Math.Max(context.GetInt("idle-minutes") ?? FollowAutoDefaultIdleMinutes, 1)));
                return;
            }

            await HandleFollowBindAsync(context, context.GetBool("bind")!.Value);
            return;
        }

        var (singleAccountKey, singleAccount) = RequireAccount(context.GetRequiredString("account"));

        switch (subcommand)
        {
            case "start":
                // Only "status"/"screenshot" bypass the agent's _commandGate (VmOperations.cs) -
                // every other command, including this one, queues behind whatever's already
                // running. Same gate-wait headroom reasoning as quit/quit-all below.
                await RunVmCommandAsync(context, singleAccount, "launch_d2r", BuildAccountArgs(singleAccountKey, singleAccount), TimeSpan.FromSeconds(210));
                return;
            case "stop":
                await RunVmCommandAsync(context, singleAccount, "kill_d2r", BuildAccountArgs(singleAccountKey, singleAccount), TimeSpan.FromSeconds(210));
                return;
            case "quit":
                // See the quit-all gate-wait comment above - same risk for a single account.
                await CancelJoinAutoIfRunningAsync($"quit was called for {singleAccountKey}");
                await CancelFollowAutoIfRunningAsync($"quit was called for {singleAccountKey}");
                await RunVmCommandAsync(context, singleAccount, "quit_d2r", BuildAccountArgs(singleAccountKey, singleAccount), TimeSpan.FromSeconds(210));
                return;
            case "restart-client":
                await RunVmCommandAsync(context, singleAccount, "restart_d2r", BuildAccountArgs(singleAccountKey, singleAccount), TimeSpan.FromSeconds(210));
                return;
            case "ready":
                await RunVmCommandAsync(context, singleAccount, "menu_ready", BuildAccountArgs(singleAccountKey, singleAccount), ReadyCommandTimeout);
                return;
            case "lobby":
                await RunVmCommandAsync(context, singleAccount, "menu_lobby", BuildMenuArgs(singleAccountKey, singleAccount, null, context), TimeSpan.FromSeconds(150), readyFirstIfNotMenuReady: true);
                return;
            case "play":
                await RunVmCommandAsync(context, singleAccount, "menu_play", BuildMenuArgs(singleAccountKey, singleAccount, null, context), TimeSpan.FromSeconds(300), readyFirstIfNotMenuReady: true);
                return;
            case "join-game":
                await RunVmCommandAsync(context, singleAccount, "menu_join_game", BuildMenuArgs(singleAccountKey, singleAccount, ResolveGameInput(context), context), TimeSpan.FromSeconds(210), readyFirstIfNotMenuReady: true);
                return;
            case "create-game":
                await RunVmCommandAsync(context, singleAccount, "menu_create_game", BuildMenuArgs(singleAccountKey, singleAccount, ResolveGameInput(context), context), TimeSpan.FromSeconds(210), readyFirstIfNotMenuReady: true);
                return;
            case "follow":
                await RunVmCommandAsync(context, singleAccount, "menu_join_friend", BuildMenuArgs(singleAccountKey, singleAccount, null, context), TimeSpan.FromSeconds(210), readyFirstIfNotMenuReady: true);
                return;
            case "save-exit":
                // See the save-exit-all timeout comment above: this is gate-wait headroom, not
                // expected automation time.
                await RunVmCommandAsync(context, singleAccount, "menu_save_exit", BuildAccountArgs(singleAccountKey, singleAccount), TimeSpan.FromSeconds(210));
                return;
            case "remote":
                var remoteUrl = _config.Agents[singleAccount.AgentId].RemoteUrl;
                await context.Command.RespondAsync(
                    string.IsNullOrWhiteSpace(remoteUrl)
                        ? $"No remoteUrl is configured for {singleAccountKey} ({singleAccount.AgentId})."
                        : $"{singleAccountKey} remote link: {remoteUrl}",
                    ephemeral: true);
                return;
            case "screenshot":
                await RunScreenshotAsync(context, singleAccount, BuildAccountArgs(singleAccountKey, singleAccount));
                return;
        }
    }

    private async Task HandleVmAsync(SlashContext context)
    {
        var (accountKey, account) = RequireAccount(context.GetRequiredString("account"));
        var vmName = account.VmName ?? account.AgentId;
        var commandName = context.SubcommandName switch
        {
            "status" => "vm_status",
            "start" => "vm_start",
            "stop" => "vm_stop",
            "reboot" => "vm_reboot",
            "snapshot" => "vm_snapshot",
            _ => throw new InvalidOperationException($"Unsupported VM subcommand: {context.SubcommandName}")
        };

        await context.Command.DeferAsync(ephemeral: true);
        var args = JsonSerializer.SerializeToElement(new
        {
            accountKey,
            vmName,
            snapshotName = context.GetString("name")
        });
        QueueDiscordWork(context, $"vm {commandName}", async () =>
        {
            var result = await _hyperV.HandleCommandAsync(
                new CommandRequest(Guid.NewGuid().ToString("N"), commandName, args),
                CancellationToken.None);
            await context.Command.ModifyOriginalResponseAsync(properties => properties.Content = FormatCommandResult(result.Ok, result.Message));
        });
    }

    private async Task HandleGameAsync(SlashContext context)
    {
        switch (context.SubcommandName)
        {
            case "set":
                var game = _db.SetActiveGame(
                    context.GetRequiredString("name"),
                    BlankToNull(context.GetString("password")),
                    context.GetString("difficulty"),
                    BlankToNull(context.GetString("notes")),
                    context.Command.User.Id.ToString());
                await context.Command.RespondAsync($"Stored current game:\n{FormatActiveGame(game)}", ephemeral: true);
                return;
            case "show":
                var stored = _db.GetActiveGame();
                await context.Command.RespondAsync(stored is null ? "No current game is stored." : FormatActiveGame(stored), ephemeral: true);
                return;
            case "clear":
                var cleared = _db.ClearActiveGame();
                await context.Command.RespondAsync(cleared ? "Cleared the stored game." : "No current game was stored.", ephemeral: true);
                return;
        }
    }

    private async Task HandleConfigAsync(SlashContext context)
    {
        switch (context.SubcommandName)
        {
            case "show":
                await context.Command.RespondAsync(FormatRuntimeConfig(), ephemeral: true);
                return;
            case "stagger":
                var seconds = context.GetRequiredInt("seconds");
                _config.StartAllDelaySeconds = seconds;
                _config.ClientStaggerSeconds = seconds;
                await SaveConfigAndRespawnAsync(
                    context,
                    $"Set all-client stagger to {seconds}s.");
                return;
            case "notifications":
                var enabled = context.GetRequiredBool("enabled");
                var channelText = BlankToNull(context.GetString("channel-id"));
                if (channelText is not null)
                {
                    _config.GuildChannel = ParseChannelId(channelText);
                }

                if (enabled && _config.GuildChannel is null)
                {
                    await context.Command.RespondAsync("channel-id is required when enabling notifications.", ephemeral: true);
                    return;
                }

                _config.GameSessionNotificationsEnabled = enabled;
                await SaveConfigAndRespawnAsync(
                    context,
                    enabled
                        ? $"Enabled game session notifications in channel {_config.GuildChannel}."
                        : "Disabled game session notifications.");
                return;
            default:
                throw new InvalidOperationException($"Unsupported config subcommand: {context.SubcommandName}");
        }
    }

    private async Task HandleRestartAsync(SlashContext context)
    {
        await context.Command.RespondAsync(
            "Respawning D2RHost. Startup self-update will run before Discord reconnects.",
            ephemeral: true);
        QueueHostRespawn();
    }

    private string FormatRuntimeConfig()
    {
        var stagger = _config.ClientStaggerSeconds ?? _config.StartAllDelaySeconds;
        var notifications = _config.GameSessionNotificationsEnabled
            ? $"enabled in {_config.GuildChannel?.ToString() ?? "(no channel)"}"
            : "disabled";
        return string.Join("\n", new[]
        {
            $"Config path: {_runtime.ConfigPath}",
            $"All-client stagger: {stagger}s",
            $"Session notifications: {notifications}"
        });
    }

    private async Task SaveConfigAndRespawnAsync(SlashContext context, string message)
    {
        HostConfigLoader.Save(_runtime.ConfigPath, _config);
        await context.Command.RespondAsync(
            $"{message}\nSaved `{_runtime.ConfigPath}`. Respawning host.",
            ephemeral: true);
        QueueHostRespawn();
    }

    private void QueueHostRespawn()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            {
                _logger.LogWarning("Config was saved, but the host cannot respawn because Environment.ProcessPath is unavailable.");
                return;
            }

            try
            {
                var scriptPath = WriteRespawnScript(processPath, _runtime.RestartArgs);
                var startInfo = new ProcessStartInfo
                {
                    FileName = _config.PowerShellPath,
                    UseShellExecute = false
                };
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-ExecutionPolicy");
                startInfo.ArgumentList.Add("Bypass");
                startInfo.ArgumentList.Add("-File");
                startInfo.ArgumentList.Add(scriptPath);
                Process.Start(startInfo);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Config was saved, but the host respawn failed.");
            }
        });
    }

    private static string WriteRespawnScript(string processPath, IEnumerable<string> restartArgs)
    {
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"d2rops-host-respawn-{Guid.NewGuid():N}.ps1");
        var workingDirectory = Path.GetDirectoryName(processPath) ?? Environment.CurrentDirectory;
        var restartArgumentLine = string.Join(" ", restartArgs.Select(WindowsArgumentQuote));
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Wait-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 750
            if ({{PsQuote(restartArgumentLine)}}.Length -gt 0) {
                Start-Process -FilePath {{PsQuote(processPath)}} -ArgumentList {{PsQuote(restartArgumentLine)}} -WorkingDirectory {{PsQuote(workingDirectory)}}
            } else {
                Start-Process -FilePath {{PsQuote(processPath)}} -WorkingDirectory {{PsQuote(workingDirectory)}}
            }
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            """;
        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }

    private async Task RunVmCommandAsync(
        SlashContext context,
        AccountConfig account,
        string commandName,
        object args,
        TimeSpan? timeout = null,
        bool readyFirstIfNotMenuReady = false)
    {
        await context.Command.DeferAsync(ephemeral: true);
        QueueDiscordWork(context, commandName, () => RunVmCommandDeferredAsync(
            context,
            account,
            commandName,
            args,
            timeout,
            readyFirstIfNotMenuReady));
    }

    private async Task RunVmCommandDeferredAsync(
        SlashContext context,
        AccountConfig account,
        string commandName,
        object args,
        TimeSpan? timeout,
        bool readyFirstIfNotMenuReady)
    {
        CommandResultInfo? readyResult = null;
        if (readyFirstIfNotMenuReady)
        {
            try
            {
                readyResult = await SendReadyIfNotMenuReadyAsync(account, args);
            }
            catch (Exception ex)
            {
                await context.Command.ModifyOriginalResponseAsync(
                    properties => properties.Content = "This client needed `/d2r ready` before menu automation, but ready did not return: "
                        + FormatExceptionWithAccountStatus(ex, account));
                return;
            }
        }

        if (readyResult?.Ok == false)
        {
            await context.Command.ModifyOriginalResponseAsync(
                properties => properties.Content = "This client needed `/d2r ready` before menu automation, but ready failed: "
                    + readyResult.Message);
            return;
        }

        CommandResultInfo result;
        try
        {
            result = await _registry.SendCommandAsync(account.AgentId, commandName, args, timeout ?? TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            await context.Command.ModifyOriginalResponseAsync(
                properties => properties.Content = $"Command `{commandName}` did not return: {FormatExceptionWithAccountStatus(ex, account)}");
            return;
        }

        var prefix = readyResult is null
            ? ""
            : $"Ran `/d2r ready` before menu automation: {FormatCommandResult(readyResult.Ok, readyResult.Message)}\n";
        await context.Command.ModifyOriginalResponseAsync(properties => properties.Content = prefix + FormatCommandResult(result.Ok, result.Message));
    }

    private async Task RunScreenshotAsync(SlashContext context, AccountConfig account, object args)
    {
        await context.Command.DeferAsync(ephemeral: true);
        QueueDiscordWork(context, "screenshot", () => RunScreenshotDeferredAsync(context, account, args));
    }

    private async Task RunScreenshotDeferredAsync(SlashContext context, AccountConfig account, object args)
    {
        var result = await _registry.SendCommandAsync(account.AgentId, "screenshot", args, TimeSpan.FromSeconds(60));
        if (!result.Ok || result.Data is not { } data)
        {
            await context.Command.ModifyOriginalResponseAsync(properties => properties.Content = FormatCommandResult(result.Ok, result.Message));
            return;
        }

        if (!TryReadScreenshot(data, out var bytes, out var extension))
        {
            await context.Command.ModifyOriginalResponseAsync(properties => properties.Content = FormatCommandResult(false, "Screenshot result did not contain image data."));
            return;
        }

        await using var stream = new MemoryStream(bytes);
        await context.Command.FollowupWithFileAsync(stream, $"{account.AgentId}-screenshot.{extension}", result.Message, ephemeral: true);
        await context.Command.ModifyOriginalResponseAsync(properties => properties.Content = "Screenshot attached.");
    }

    private void QueueDiscordWork(SlashContext context, string operationName, Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discord command background work failed for {OperationName}.", operationName);
                await SetCommandResponseSafeAsync(context.Command, $"Command failed: {ex.Message}");
            }
        });
    }

    private async Task SetCommandResponseSafeAsync(SocketSlashCommand command, string content)
    {
        try
        {
            if (command.HasResponded)
            {
                await command.ModifyOriginalResponseAsync(properties => properties.Content = content);
            }
            else
            {
                await command.RespondAsync(content, ephemeral: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not update Discord command response after background failure.");
        }
    }

    private async Task QueueCreateGameAllAsync(SlashContext context, GameInput game, bool watch)
    {
        var (entries, offlineEntries) = GetAccountEntriesByConnectivity();
        if (entries.Length == 0)
        {
            await context.Command.RespondAsync(
                "No online accounts are available for create-game-all." + FormatOfflineSkipSuffix(offlineEntries),
                ephemeral: true);
            return;
        }

        var creator = entries[0];
        var joiners = entries.Skip(1).ToArray();
        var staggerSeconds = _config.ClientStaggerSeconds ?? _config.StartAllDelaySeconds;
        var readyFirstCount = entries.Count(entry => ShouldRunReadyFirst(entry.Value));

        await context.Command.RespondAsync(
            $"Queued create-game-all for {entries.Length} online account(s). {creator.Key} will create {game.GameName}; "
                + $"{entries.Length} account(s) will warm up with {staggerSeconds}s stagger; "
                + $"{joiners.Length} joiner(s) will prepare Join Game while {creator.Key} creates."
                + FormatReadyFirstSuffix(readyFirstCount)
                + FormatOfflineSkipSuffix(offlineEntries),
            ephemeral: true);

        await StartGameSessionAsync(
            game.GameName,
            entries.Length,
            $"Queued create-game-all. {creator.Key} will create; {joiners.Length} account(s) will join.",
            creator.Value.AgentId);

        var watchCts = new CancellationTokenSource();
        if (watch)
        {
            _ = RunGameAllWatchTickerAsync(context, "create-game-all", game.GameName, entries, watchCts.Token);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var argsByAccount = entries.ToDictionary(
                    entry => entry.Key,
                    entry => BuildMenuArgs(entry.Key, entry.Value, game, context),
                    StringComparer.OrdinalIgnoreCase);
                var readyTasks = entries
                    .Select((entry, index) => new
                    {
                        entry.Key,
                        Task = RunCreateGameAllReadyAsync(entry, index, staggerSeconds, argsByAccount[entry.Key])
                    })
                    .ToDictionary(item => item.Key, item => item.Task, StringComparer.OrdinalIgnoreCase);
                var prepareJoinerTasks = joiners.ToDictionary(
                    entry => entry.Key,
                    entry => RunCreateGameAllPrepareJoinerAsync(entry, readyTasks[entry.Key], argsByAccount[entry.Key]),
                    StringComparer.OrdinalIgnoreCase);

                var creatorReadyResult = await readyTasks[creator.Key];
                if (!creatorReadyResult.Ok)
                {
                    await CompleteGameSessionAsync(
                        ok: false,
                        joined: 0,
                        status: $"Stopped before create: {creator.Key} failed ready.",
                        detail: creatorReadyResult.Message);
                    await SendFollowupSafeAsync(
                        context,
                        $"create-game-all stopped before create: {creator.Key} failed ready: {creatorReadyResult.Message}");
                    return;
                }

                var warmupMessage = FormatCreateGameAllCreatorReadyResult(creatorReadyResult, creator.Key, game.GameName, joiners.Length);
                await UpdateGameSessionAsync("Creator ready; creating game while joiners prepare.", joined: 0, detail: warmupMessage);
                await SendFollowupSafeAsync(context, warmupMessage);

                var creatorArgs = argsByAccount[creator.Key];
                CommandResultInfo createResult;
                try
                {
                    createResult = await _registry.SendCommandAsync(
                        creator.Value.AgentId,
                        "menu_create_game",
                        creatorArgs,
                        TimeSpan.FromSeconds(210));
                }
                catch (Exception ex)
                {
                    var message = FormatExceptionWithAccountStatus(ex, creator.Key, creator.Value);
                    _logger.LogError(ex, "create-game-all creator command timed out or failed for {AccountKey}.", creator.Key);
                    await CompleteGameSessionAsync(
                        ok: false,
                        joined: 0,
                        status: $"{creator.Key} failed to create {game.GameName}.",
                        detail: message);
                    await SendFollowupSafeAsync(
                        context,
                        $"create-game-all failed while creating {game.GameName}: {message}");
                    return;
                }

                if (!createResult.Ok)
                {
                    _logger.LogWarning(
                        "create-game-all stopped because creator {AccountKey} failed: {Message}",
                        creator.Key,
                        createResult.Message);
                    await CompleteGameSessionAsync(
                        ok: false,
                        joined: 0,
                        status: $"{creator.Key} failed to create {game.GameName}.",
                        detail: createResult.Message);
                    await SendFollowupSafeAsync(
                        context,
                        $"create-game-all stopped: {creator.Key} failed to create {game.GameName}: {createResult.Message}");
                    return;
                }

                // So a later plain join-all/create-game-all (no flags) sees what actually just
                // got created, the same way a manual /game set would - not just whatever was
                // true before this run started.
                _db.SetActiveGame(game.GameName, game.Password, game.Difficulty, notes: "create-game-all", context.Command.User.Id.ToString());

                await UpdateGameSessionAsync(
                    $"Game created by {creator.Key}; joiners entering as they finish preparing.",
                    joined: 1,
                    detail: createResult.Message);

                var allJoinResults = await Task.WhenAll(joiners.Select(entry =>
                    RunCreateGameAllJoinerAfterCreateAsync(
                        entry,
                        prepareJoinerTasks[entry.Key],
                        argsByAccount[entry.Key],
                        game.GameName)));
                var joinedCount = 1 + allJoinResults.Count(result => result.Ok);
                var allOk = allJoinResults.All(result => result.Ok);
                await CompleteGameSessionAsync(
                    ok: allOk,
                    joined: joinedCount,
                    status: allOk
                        ? $"Create/join flow completed for {game.GameName}."
                        : $"Create/join flow completed with failures for {game.GameName}.",
                    detail: joiners.Length == 0
                        ? "No other accounts were configured to join."
                        : FormatCreateGameAllResult(creator.Key, game.GameName, allJoinResults));

                await SendFollowupSafeAsync(
                    context,
                    joiners.Length == 0
                        ? $"Create flow completed on {creator.Key} for {game.GameName}. No other accounts were configured to join."
                        : FormatCreateGameAllResult(creator.Key, game.GameName, allJoinResults));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "create-game-all orchestration failed for {GameName}.", game.GameName);
                await CompleteGameSessionAsync(
                    ok: false,
                    joined: _activeSessionJoined,
                    status: $"create-game-all failed for {game.GameName}.",
                    detail: ex.Message);
                await SendFollowupSafeAsync(context, $"create-game-all failed while creating {game.GameName}: {ex.Message}");
            }
            finally
            {
                watchCts.Cancel();
            }
        });
    }

    // The regular game-session message only updates at orchestration milestones (creator ready,
    // game created, joiners done) - it can't show what's happening *between* those, which is
    // exactly the gap the operator is trying to see when a run looks stuck. This posts a second,
    // separate message and re-polls live status for every account on a short interval so it shows
    // per-account click attempts and detected screen state as they happen, not just at milestones.
    private async Task RunGameAllWatchTickerAsync(
        SlashContext context,
        string label,
        string gameName,
        KeyValuePair<string, AccountConfig>[] entries,
        CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        IUserMessage message;
        try
        {
            message = await context.Command.Channel.SendMessageAsync(
                FormatWatchHeader(label, gameName, startedUtc) + "\nStarting...");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start {Label}-watch message.", label);
            return;
        }

        var logPath = GetWatchLogPath(gameName, startedUtc);

        while (true)
        {
            var lines = await Task.WhenAll(
                entries.Select(entry => FormatAccountWatchLineAsync(entry.Key, entry.Value, CancellationToken.None)));
            var content = string.Join("\n", new[] { FormatWatchHeader(label, gameName, startedUtc) }.Concat(lines));

            try
            {
                await message.ModifyAsync(properties => properties.Content = content);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not update {Label}-watch message.", label);
            }

            AppendWatchLogTick(logPath, lines);

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await SendWatchLogAttachmentAsync(context, gameName, logPath);
    }

    // The live message only ever shows the latest tick - once a run is done, the operator can't
    // scroll back to see what the frame/click history looked like a minute ago. Every tick is
    // also appended to a plain-text log file on the host and the full file is attached to the
    // channel when the run ends, so the entire history can be pulled up (or handed to someone
    // else for review) after the fact, not just whatever was on screen at the moment of a
    // screenshot.
    private string GetWatchLogPath(string gameName, DateTimeOffset startedUtc)
    {
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(_runtime.ConfigPath)) ?? ".";
        var logsDirectory = Path.Combine(configDirectory, "logs");
        Directory.CreateDirectory(logsDirectory);

        var safeName = new string(gameName.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        return Path.Combine(logsDirectory, $"watch-{safeName}-{startedUtc:yyyyMMdd-HHmmss}.log");
    }

    private void AppendWatchLogTick(string logPath, IEnumerable<string> lines)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("u");
        try
        {
            File.AppendAllLines(logPath, lines.Select(line => $"{timestamp} {line}"));
        }
        catch (Exception ex)
        {
            // Was LogDebug - silent by default, so a dropped tick (eg. a transient lock right
            // after the log directory is created) left the file missing lines the live message
            // still showed, with nothing in the logs to explain the gap. Warn so a dropped tick
            // is at least visible instead of indistinguishable from "nothing happened."
            _logger.LogWarning(ex, "Could not append to watch log {LogPath}.", logPath);
        }
    }

    private async Task SendWatchLogAttachmentAsync(SlashContext context, string gameName, string logPath)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        try
        {
            await context.Command.Channel.SendFileAsync(logPath, $"Full watch log for {gameName}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not upload watch log {LogPath}.", logPath);
        }
    }

    private static string FormatWatchHeader(string label, string gameName, DateTimeOffset startedUtc)
    {
        return $"Watching {label}: {gameName} (elapsed {FormatElapsed(DateTimeOffset.UtcNow - startedUtc)})";
    }

    // Condensed for screenshots: frame + the single most recent input attempt, not the full
    // verbose /d2r status line (Battle.net/D2R running flags, process discovery, etc).
    private async Task<string> FormatAccountWatchLineAsync(
        string accountKey, AccountConfig account, CancellationToken cancellationToken)
    {
        var name = FormatAccountDisplayName(accountKey, account);
        var agent = _registry.GetAgent(account.AgentId);
        if (agent?.Connected != true)
        {
            return $"{name}: offline";
        }

        CommandResultInfo result;
        try
        {
            result = await _registry.SendCommandAsync(
                account.AgentId, "status", args: null, TimeSpan.FromSeconds(6), cancellationToken);
        }
        catch (Exception ex)
        {
            return $"{name}: watch check failed ({ex.Message})";
        }

        if (!result.Ok || result.Data is not { } data)
        {
            return $"{name}: status unavailable ({result.Message})";
        }

        return FormatWatchLine(name, data.GetRawText());
    }

    private static string FormatWatchLine(string name, string? statusJson)
    {
        // [degraded] alone doesn't say why - whether it's gate contention from an active
        // automation command (expected, transient) or CollectDetailedStatus itself missing its
        // StatusCollectionTimeoutSeconds budget every single poll (a standing condition that
        // makes "frame" never reflect live ground truth at all). statusError carries that reason
        // already; it just wasn't surfaced here.
        var degraded = TryReadStatusBool(statusJson, "statusDegraded", out var isDegraded) && isDegraded
            ? $" [degraded{FormatDegradedReason(statusJson)}]"
            : "";
        var frame = TryReadFrameSummary(statusJson, out var frameSummary) ? frameSummary : "frame unknown";
        var lastInput = TryReadLastInputActionWatchSummary(statusJson, out var inputSummary) ? inputSummary : "no input yet";
        // lastObservedFrame/lastInputAction only update once a step finishes, so a command stuck
        // mid-step (the exact failure mode under investigation: one click lands, then nothing for
        // minutes) shows stale values for both with no hint of what it's actually doing right now.
        // lastCommandCheckpoint is set at the start of each step, so it still moves even while
        // everything else looks frozen, and pinpoints which call the run is stuck in.
        var checkpoint = TryReadCheckpointSummary(statusJson, out var checkpointSummary)
            ? $" | at {checkpointSummary}"
            : "";
        // The per-sub-check breakdown is usually only worth the line length when nothing else
        // resolved. During game-entry waits, though, a recognized LobbyOrGame frame can be the
        // symptom we need to debug, so show a recent checkpoint-triggered breakdown there too.
        var checks = ShouldShowClassifierBreakdown(statusJson) && TryReadClassifierBreakdownSummary(statusJson, out var checksSummary)
            ? $" | checks {checksSummary}"
            : "";
        // The actual pixel ratios IsInGameReady just measured, against the same thresholds it
        // decides with - "expected vs lastgrab," not just where the command is stuck.
        var hud = ShouldShowHudEvidence(statusJson) && TryReadHudEvidenceSummary(statusJson, out var hudSummary)
            ? $" | hud {hudSummary}"
            : "";
        // Free to read, no sampling cost - shown unconditionally so a thread leak from
        // TryRunBounded's Task.Run-and-abandon-on-timeout pattern (every bounded call spawns a
        // background thread that's never actually killed if the underlying GDI call hangs
        // forever instead of just being slow) climbs visibly in real time instead of only being
        // inferred after the fact from anomalous timing.
        var threadPool = TryReadThreadPoolSummary(statusJson, out var threadPoolSummary)
            ? $" | pool {threadPoolSummary}"
            : "";
        // Gated on recency like hud above: a count from minutes ago (the client left the game,
        // or the monitor's disabled) would read as a live number otherwise.
        var party = ShouldShowPartyMemberCount(statusJson) && TryReadPartyMemberCountSummary(statusJson, out var partySummary)
            ? $" | party {partySummary}"
            : "";
        return $"{name}: {frame}{degraded} | last {lastInput}{checkpoint}{checks}{hud}{threadPool}{party}";
    }

    private static bool ShouldShowClassifierBreakdown(string? json)
    {
        if (IsObservedFrameUnknown(json))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetString(document.RootElement, "lastCommandCheckpoint", out var checkpoint)
                || (!checkpoint.Contains("ClickMenuEntryButtonUntilEnteredGameAsync", StringComparison.Ordinal)
                    && !checkpoint.Contains("WaitForGameEntryAsync", StringComparison.Ordinal)))
            {
                return false;
            }

            return TryReadDateTimeOffset(document.RootElement, "lastClassifierBreakdownUtc", out var recordedAt)
                && DateTimeOffset.UtcNow - recordedAt <= TimeSpan.FromSeconds(30);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsObservedFrameUnknown(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryGetString(document.RootElement, "lastObservedFrame", out var frame)
                && string.Equals(frame, "Unknown", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadClassifierBreakdownSummary(string? json, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetString(document.RootElement, "lastClassifierBreakdown", out var breakdown)
                || string.IsNullOrWhiteSpace(breakdown))
            {
                return false;
            }

            var age = TryReadDateTimeOffset(document.RootElement, "lastClassifierBreakdownUtc", out var recordedAt)
                ? FormatAge(DateTimeOffset.UtcNow - recordedAt)
                : "?";
            value = $"{breakdown} ({age} ago)";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Checkpoints only ever proved *where* a stuck command was, never *what it actually saw* -
    // every real root-cause this session came from comparing actual pixel ratios against their
    // thresholds (sitting_in_town's red=0.54 vs the 0.20 cutoff, etc.), and that comparison only
    // existed in throwaway tests run against a screenshot after the fact. This surfaces the same
    // numbers live, gated on recency rather than on checkpoint text, so it shows up exactly when
    // IsInGameReady is actively sampling and goes quiet again once it isn't.
    private static bool ShouldShowHudEvidence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryReadDateTimeOffset(document.RootElement, "lastHudEvidenceUtc", out var recordedAt)
                && DateTimeOffset.UtcNow - recordedAt <= TimeSpan.FromSeconds(15);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadHudEvidenceSummary(string? json, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetString(document.RootElement, "lastHudEvidence", out var evidence)
                || string.IsNullOrWhiteSpace(evidence))
            {
                return false;
            }

            var age = TryReadDateTimeOffset(document.RootElement, "lastHudEvidenceUtc", out var recordedAt)
                ? FormatAge(DateTimeOffset.UtcNow - recordedAt)
                : "?";
            value = $"{evidence} ({age} ago)";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // issue #20, item 6. Gated on recency rather than just "is the field present" for the same
    // reason as ShouldShowHudEvidence: the agent only samples this while actually in a game, on
    // its own PartyMemberCountIntervalSeconds tick (default 30s), so a present-but-old value means
    // the client left the game (or the monitor is disabled) since the last sample, not that the
    // count shown is still true right now.
    private static bool ShouldShowPartyMemberCount(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryReadDateTimeOffset(document.RootElement, "lastPartyMemberCountUtc", out var recordedAt)
                && DateTimeOffset.UtcNow - recordedAt <= TimeSpan.FromSeconds(75);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadPartyMemberCountSummary(string? json, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetInt(document.RootElement, "lastPartyMemberCount", out var otherMembers))
            {
                return false;
            }

            var age = TryReadDateTimeOffset(document.RootElement, "lastPartyMemberCountUtc", out var recordedAt)
                ? FormatAge(DateTimeOffset.UtcNow - recordedAt)
                : "?";
            value = $"{otherMembers + 1} player(s) ({age} ago)";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadThreadPoolSummary(string? json, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetInt(document.RootElement, "threadPoolThreads", out var threads))
            {
                return false;
            }

            var pending = TryGetInt(document.RootElement, "threadPoolPending", out var pendingValue) ? pendingValue : 0;
            value = $"threads={threads},pending={pending}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadCheckpointSummary(string? json, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetString(document.RootElement, "lastCommandCheckpoint", out var checkpoint)
                || string.IsNullOrWhiteSpace(checkpoint))
            {
                return false;
            }

            var age = TryReadDateTimeOffset(document.RootElement, "lastCommandCheckpointUtc", out var reachedAt)
                ? FormatAge(DateTimeOffset.UtcNow - reachedAt)
                : "?";
            value = $"{checkpoint} ({age} ago)";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string FormatDegradedReason(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryGetString(document.RootElement, "statusError", out var reason)
                ? $": {reason}"
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static bool TryReadStatusBool(string? json, string propertyName, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryGetBoolean(document.RootElement, propertyName, out value);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadFrameSummary(string? json, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetString(document.RootElement, "lastObservedFrame", out var frame)
                || string.IsNullOrWhiteSpace(frame))
            {
                return false;
            }

            var age = TryReadDateTimeOffset(document.RootElement, "lastObservedFrameUtc", out var observedAt)
                ? FormatAge(DateTimeOffset.UtcNow - observedAt)
                : "?";
            value = $"frame {frame} ({age} ago)";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadLastInputActionWatchSummary(string? json, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("lastInputAction", out var action)
                || action.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return false;
            }

            var kind = TryGetString(action, "kind", out var kindValue) ? kindValue : "?";
            var button = TryGetString(action, "button", out var buttonValue) ? buttonValue : "?";
            var screen = TryGetInt(action, "screenX", out var x) && TryGetInt(action, "screenY", out var y)
                ? $"{x},{y}"
                : "?,?";
            var age = TryReadDateTimeOffset(action, "timeUtc", out var actedAt)
                ? FormatAge(DateTimeOffset.UtcNow - actedAt)
                : "?";
            var fgAfter = TryGetBoolean(action, "d2rForegroundAfter", out var fgAfterValue)
                ? (fgAfterValue ? "fg ok" : "fg lost")
                : "fg ?";

            value = $"{kind}/{button}@{screen} ({age} ago), {fgAfter}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadDateTimeOffset(JsonElement root, string propertyName, out DateTimeOffset value)
    {
        value = default;
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && property.TryGetDateTimeOffset(out value);
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age.TotalMinutes >= 1
            ? $"{(int)age.TotalMinutes}m{age.Seconds}s"
            : $"{(int)age.TotalSeconds}s";
    }

    private async Task<ReadyResult> RunCreateGameAllReadyAsync(
        KeyValuePair<string, AccountConfig> entry,
        int index,
        int staggerSeconds,
        object args)
    {
        if (!ShouldRunReadyFirst(entry.Value))
        {
            return new ReadyResult(entry.Key, true, "Already menu-ready.", RanReady: false);
        }

        await Task.Delay(TimeSpan.FromSeconds(index * staggerSeconds));
        try
        {
            var readyResult = await SendReadyIfNotMenuReadyAsync(entry.Value, args);
            return readyResult is null
                ? new ReadyResult(entry.Key, true, "Already menu-ready.", RanReady: false)
                : new ReadyResult(entry.Key, readyResult.Ok, readyResult.Message, RanReady: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Queued ready before create-game-all failed for {AccountKey}.", entry.Key);
            return new ReadyResult(
                entry.Key,
                false,
                FormatExceptionWithAccountStatus(ex, entry.Key, entry.Value),
                RanReady: true);
        }
    }

    private async Task<JoinResult> RunCreateGameAllPrepareJoinerAsync(
        KeyValuePair<string, AccountConfig> entry,
        Task<ReadyResult> readyTask,
        object joinArgs)
    {
        var readyResult = await readyTask;
        if (!readyResult.Ok)
        {
            return new JoinResult(entry.Key, false, $"ready failed before join prepare: {readyResult.Message}");
        }

        try
        {
            var prepareResult = await _registry.SendCommandAsync(
                entry.Value.AgentId,
                "menu_prepare_join_game",
                joinArgs,
                JoinPrepareCommandTimeout);
            return new JoinResult(entry.Key, prepareResult.Ok, prepareResult.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Queued join prepare during create-game-all failed for {AccountKey}.", entry.Key);
            return new JoinResult(entry.Key, false, FormatExceptionWithAccountStatus(ex, entry.Key, entry.Value));
        }
    }

    private async Task<JoinResult> RunCreateGameAllPreparedJoinerAsync(
        KeyValuePair<string, AccountConfig> entry,
        object joinArgs,
        string gameName)
    {
        try
        {
            var joinResult = await _registry.SendCommandAsync(
                entry.Value.AgentId,
                "menu_submit_join_game",
                joinArgs,
                TimeSpan.FromSeconds(210));
            if (joinResult.Ok)
            {
                await IncrementGameSessionJoinedAsync($"{entry.Key} joined {gameName}.");
            }

            return new JoinResult(entry.Key, joinResult.Ok, joinResult.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Queued prepared join after create-game-all failed for {AccountKey}.", entry.Key);
            return new JoinResult(entry.Key, false, FormatExceptionWithAccountStatus(ex, entry.Key, entry.Value));
        }
    }

    private async Task<JoinResult> RunCreateGameAllJoinerAfterCreateAsync(
        KeyValuePair<string, AccountConfig> entry,
        Task<JoinResult> prepareTask,
        object joinArgs,
        string gameName)
    {
        var prepareResult = await prepareTask;
        if (!prepareResult.Ok)
        {
            return prepareResult;
        }

        return await RunCreateGameAllPreparedJoinerAsync(entry, joinArgs, gameName);
    }

    private async Task QueueJoinAllAsync(SlashContext context, GameInput game, bool watch)
    {
        var (entries, offlineEntries) = GetAccountEntriesByConnectivity();
        if (entries.Length == 0)
        {
            await context.Command.RespondAsync(
                "No online accounts are available for join-all." + FormatOfflineSkipSuffix(offlineEntries),
                ephemeral: true);
            return;
        }

        // So a later plain join-all/create-game-all (no flags) sees what this run actually
        // joined - the game already exists by definition, so this is true regardless of whether
        // every account's join below succeeds.
        _db.SetActiveGame(game.GameName, game.Password, game.Difficulty, notes: "join-all", context.Command.User.Id.ToString());

        var staggerSeconds = _config.ClientStaggerSeconds ?? _config.StartAllDelaySeconds;
        var readyFirstCount = entries.Count(entry => ShouldRunReadyFirst(entry.Value));
        await context.Command.RespondAsync(
            $"Queued join-all for {entries.Length} online account(s) into {game.GameName} with {staggerSeconds}s stagger."
                + " Accounts will prepare Join Game first, then submit."
                + FormatReadyFirstSuffix(readyFirstCount)
                + FormatOfflineSkipSuffix(offlineEntries),
            ephemeral: true);

        await StartGameSessionAsync(
            game.GameName,
            entries.Length,
            $"Queued join-all for {entries.Length} account(s).",
            entries[0].Value.AgentId);

        var watchCts = new CancellationTokenSource();
        if (watch)
        {
            _ = RunGameAllWatchTickerAsync(context, "join-all", game.GameName, entries, watchCts.Token);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var argsByAccount = entries.ToDictionary(
                    entry => entry.Key,
                    entry => BuildMenuArgs(entry.Key, entry.Value, game, context),
                    StringComparer.OrdinalIgnoreCase);
                var prepareTasks = entries
                    .Select((entry, index) => new
                    {
                        entry.Key,
                        Task = RunJoinAllPrepareEntryAsync(entry, index, staggerSeconds, argsByAccount[entry.Key])
                    })
                    .ToDictionary(item => item.Key, item => item.Task, StringComparer.OrdinalIgnoreCase);
                var joinResults = await Task.WhenAll(entries.Select(entry =>
                    RunJoinAllPreparedEntryAsync(
                        entry,
                        prepareTasks[entry.Key],
                        argsByAccount[entry.Key],
                        game.GameName)));
                var joinedCount = joinResults.Count(result => result.Ok);
                var allOk = joinResults.All(result => result.Ok);
                var summary = FormatJoinAllResult(game.GameName, joinResults);
                await CompleteGameSessionAsync(
                    ok: allOk,
                    joined: joinedCount,
                    status: allOk
                        ? $"join-all completed for {game.GameName}."
                        : $"join-all completed with failures for {game.GameName}.",
                    detail: summary);
                await SendFollowupSafeAsync(context, summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "join-all orchestration failed for {GameName}.", game.GameName);
                await CompleteGameSessionAsync(
                    ok: false,
                    joined: _activeSessionJoined,
                    status: $"join-all failed for {game.GameName}.",
                    detail: ex.Message);
                await SendFollowupSafeAsync(context, $"join-all failed for {game.GameName}: {ex.Message}");
            }
            finally
            {
                watchCts.Cancel();
            }
        });
    }

    private async Task<JoinResult> RunJoinAllPrepareEntryAsync(
        KeyValuePair<string, AccountConfig> entry,
        int index,
        int staggerSeconds,
        object args)
    {
        await Task.Delay(TimeSpan.FromSeconds(index * staggerSeconds));
        try
        {
            var readyResult = await SendReadyIfNotMenuReadyAsync(entry.Value, args);
            if (readyResult?.Ok == false)
            {
                return new JoinResult(entry.Key, false, $"ready failed before join prepare: {readyResult.Message}");
            }

            var prepareResult = await _registry.SendCommandAsync(
                entry.Value.AgentId,
                "menu_prepare_join_game",
                args,
                JoinPrepareCommandTimeout);

            return new JoinResult(entry.Key, prepareResult.Ok, prepareResult.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Queued join-all prepare failed for {AccountKey}.", entry.Key);
            return new JoinResult(entry.Key, false, FormatExceptionWithAccountStatus(ex, entry.Key, entry.Value));
        }
    }

    private async Task<JoinResult> RunJoinAllPreparedEntryAsync(
        KeyValuePair<string, AccountConfig> entry,
        Task<JoinResult> prepareTask,
        object args,
        string gameName)
    {
        var prepareResult = await prepareTask;
        if (!prepareResult.Ok)
        {
            return prepareResult;
        }

        try
        {
            var joinResult = await _registry.SendCommandAsync(
                entry.Value.AgentId,
                "menu_submit_join_game",
                args,
                TimeSpan.FromSeconds(210));
            if (joinResult.Ok)
            {
                await IncrementGameSessionJoinedAsync($"{entry.Key} joined {gameName}.");
            }

            return new JoinResult(entry.Key, joinResult.Ok, joinResult.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Queued prepared join-all submit failed for {AccountKey}.", entry.Key);
            return new JoinResult(entry.Key, false, FormatExceptionWithAccountStatus(ex, entry.Key, entry.Value));
        }
    }

    private static string FormatCreateGameAllCreatorReadyResult(
        ReadyResult creatorReadyResult,
        string creatorKey,
        string gameName,
        int joinerCount)
    {
        var creatorState = creatorReadyResult.RanReady ? "warmed" : "already ready";
        return $"Creator {creatorKey} is {creatorState}; creating {gameName}. "
            + $"{joinerCount} joiner(s) are warming and preparing Join Game in parallel.";
    }

    private static string FormatCreateGameAllResult(string creatorKey, string gameName, IReadOnlyCollection<JoinResult> joinResults)
    {
        var joined = joinResults.Where(result => result.Ok).Select(result => result.AccountKey).ToArray();
        var failed = joinResults.Where(result => !result.Ok).ToArray();
        var lines = new List<string>
        {
            $"Create flow completed on {creatorKey} for {gameName}.",
            joined.Length == 0
                ? "No join flows completed successfully."
                : $"Join flows completed: {string.Join(", ", joined)}."
        };

        if (failed.Length > 0)
        {
            lines.Add("Failed: " + string.Join("; ", failed.Select(result => $"{result.AccountKey}: {result.Message}")));
        }

        return string.Join("\n", lines);
    }

    private static string FormatJoinAllResult(string gameName, IReadOnlyCollection<JoinResult> joinResults)
    {
        var joined = joinResults.Where(result => result.Ok).Select(result => result.AccountKey).ToArray();
        var failed = joinResults.Where(result => !result.Ok).ToArray();
        var lines = new List<string>
        {
            $"Join-all completed for {gameName}.",
            joined.Length == 0
                ? "No join flows completed successfully."
                : $"Join flows completed: {string.Join(", ", joined)}."
        };

        if (failed.Length > 0)
        {
            lines.Add("Failed: " + string.Join("; ", failed.Select(result => $"{result.AccountKey}: {result.Message}")));
        }

        return string.Join("\n", lines);
    }

    private async Task StartGameSessionAsync(string gameName, int expected, string status, string representativeAgentId)
    {
        var channel = GetGameSessionChannel();
        if (channel is null)
        {
            return;
        }

        await _sessionLock.WaitAsync();
        try
        {
            _activeSessionGameName = gameName;
            _activeSessionStartedUtc = DateTimeOffset.UtcNow;
            _activeSessionExpected = expected;
            _activeSessionJoined = 0;
            _activeSessionRepresentativeAgentId = representativeAgentId;
            _activeSessionMessage = await channel.SendMessageAsync(await FormatGameSessionMessageAsync(status, detail: null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start Discord game session notification.");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task UpdateGameSessionAsync(string status, int? joined = null, string? detail = null)
    {
        await _sessionLock.WaitAsync();
        try
        {
            if (_activeSessionMessage is null)
            {
                return;
            }

            if (joined is not null)
            {
                _activeSessionJoined = joined.Value;
            }

            var content = await FormatGameSessionMessageAsync(status, detail);
            await _activeSessionMessage.ModifyAsync(properties => properties.Content = content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update Discord game session notification.");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task IncrementGameSessionJoinedAsync(string status)
    {
        await _sessionLock.WaitAsync();
        try
        {
            if (_activeSessionMessage is null)
            {
                return;
            }

            _activeSessionJoined++;
            var content = await FormatGameSessionMessageAsync(status, detail: null);
            await _activeSessionMessage.ModifyAsync(properties => properties.Content = content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update Discord game session joined count.");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task CompleteGameSessionAsync(bool ok, int joined, string status, string? detail = null)
    {
        await _sessionLock.WaitAsync();
        try
        {
            if (_activeSessionMessage is null)
            {
                return;
            }

            _activeSessionJoined = joined;
            var content = await FormatGameSessionMessageAsync(status, detail);
            await _activeSessionMessage.ModifyAsync(properties => properties.Content = content);
            await _activeSessionMessage.AddReactionAsync(new Emoji(ok ? "✅" : "⛔"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not complete Discord game session notification.");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private IMessageChannel? GetGameSessionChannel()
    {
        if (!_config.GameSessionNotificationsEnabled || _config.GuildChannel is not { } channelId)
        {
            return null;
        }

        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel is null)
        {
            _logger.LogWarning("Game session notifications are enabled, but channel {ChannelId} is not visible.", channelId);
        }

        return channel;
    }

    private async Task<string> FormatGameSessionMessageAsync(string status, string? detail)
    {
        var elapsed = _activeSessionStartedUtc is { } started
            ? DateTimeOffset.UtcNow - started
            : TimeSpan.Zero;
        var lines = new List<string>
        {
            $"Game session: {_activeSessionGameName ?? "(unknown)"}",
            $"Status: {status}",
            $"Bots in game: {_activeSessionJoined}/{_activeSessionExpected}",
            $"Elapsed: {FormatElapsed(elapsed)}"
        };

        var playerCount = await TryFetchPlayerCountLineAsync();
        if (playerCount is not null)
        {
            lines.Add(playerCount);
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            lines.Add(detail);
        }

        return string.Join("\n", lines);
    }

    // issue #20, item 6 (consumer). The representative account's own RunPartyMemberMonitorAsync
    // ticks on its own 30s interval independent of this message's update schedule, so this reads
    // whatever it last sampled rather than forcing a fresh capture here - keeps this on the same
    // "efficient, not on the hot path of every Discord interaction" footing as that monitor.
    // That means a session message can render before the first in-game tick (no line at all) on
    // a very fast join, and catches up on whichever later update happens to land after that tick.
    private async Task<string?> TryFetchPlayerCountLineAsync()
    {
        if (_activeSessionRepresentativeAgentId is not { } agentId)
        {
            return null;
        }

        try
        {
            var result = await _registry.SendCommandAsync(agentId, "status", args: null, TimeSpan.FromSeconds(6));
            if (!result.Ok || result.Data is not { } data)
            {
                return null;
            }

            var json = data.GetRawText();
            return ShouldShowPartyMemberCount(json) && TryReadPartyMemberCountSummary(json, out var summary)
                ? $"Players in game: {summary}"
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch live player count for game session message.");
            return null;
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
            : $"{elapsed.Minutes}m {elapsed.Seconds}s";
    }

    private async Task SendFollowupSafeAsync(SlashContext context, string message)
    {
        try
        {
            await context.Command.FollowupAsync(message, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not send Discord follow-up for background command.");
        }
    }

    private async Task QueueAllCommandsAsync(
        SlashContext context,
        string commandName,
        Func<string, AccountConfig, object> argsFactory,
        TimeSpan? timeout = null,
        bool readyFirstIfNotMenuReady = false,
        string? displayName = null)
    {
        var label = displayName ?? commandName;
        var (entries, offlineEntries) = GetAccountEntriesByConnectivity();
        if (entries.Length == 0)
        {
            await context.Command.RespondAsync(
                $"No online accounts are available for {label}." + FormatOfflineSkipSuffix(offlineEntries),
                ephemeral: true);
            return;
        }

        var staggerSeconds = _config.ClientStaggerSeconds ?? _config.StartAllDelaySeconds;
        var readyFirstCount = readyFirstIfNotMenuReady
            ? entries.Count(entry => ShouldRunReadyFirst(entry.Value))
            : 0;
        await context.Command.RespondAsync(
            $"Queued {entries.Length} online {label} command(s) with {staggerSeconds}s stagger."
                + FormatReadyFirstSuffix(readyFirstCount)
                + FormatOfflineSkipSuffix(offlineEntries),
            ephemeral: true);

        var tracker = new FanInCompletionTracker(entries.Length);

        foreach (var (entry, index) in entries.Select((entry, index) => (entry, index)))
        {
            _ = Task.Run(async () =>
            {
                var ok = true;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(index * staggerSeconds));
                    var args = argsFactory(entry.Key, entry.Value);
                    if (readyFirstIfNotMenuReady)
                    {
                        var readyResult = await SendReadyIfNotMenuReadyAsync(entry.Value, args);
                        if (readyResult?.Ok == false)
                        {
                            ok = false;
                            _logger.LogWarning(
                                "Queued command {Command} skipped for {AccountKey} because ready failed: {Message}",
                                commandName,
                                entry.Key,
                                readyResult.Message);
                            await SendFollowupSafeAsync(
                                context,
                                $"{label} skipped for {entry.Key}: ready failed: {readyResult.Message}");
                            return;
                        }
                    }

                    var result = await _registry.SendCommandAsync(
                        entry.Value.AgentId,
                        commandName,
                        args,
                        timeout ?? TimeSpan.FromSeconds(60));
                    if (!result.Ok)
                    {
                        ok = false;
                        await SendFollowupSafeAsync(
                            context,
                            $"{label} failed for {entry.Key}: {result.Message}");
                    }
                }
                catch (Exception ex)
                {
                    ok = false;
                    _logger.LogError(ex, "Queued command {Command} failed for {AccountKey}.", commandName, entry.Key);
                    await SendFollowupSafeAsync(
                        context,
                        $"{label} failed for {entry.Key}: {FormatExceptionWithAccountStatus(ex, entry.Key, entry.Value)}");
                }
                finally
                {
                    if (tracker.Complete(ok))
                    {
                        await SendQueueCompletionFollowupAsync(context, label, entries.Length, tracker.FailedCount);
                    }
                }
            });
        }
    }

    // join-all/create-game-all already signal completion with a checkmark/X reaction on their
    // public session message (see CompleteGameSessionAsync) - save-exit-all/start-all/quit-all
    // (all routed through QueueAllCommandsAsync) had no equivalent "everyone's
    // done" signal, only ad-hoc per-VM failure follow-ups, so a fully successful run looked
    // identical to one nobody had checked on yet. The reaction has to land on a fresh
    // non-ephemeral follow-up rather than the initial ack: that ack is sent ephemeral (visible
    // only to the invoker), and Discord does not support reacting to ephemeral messages.
    private async Task SendQueueCompletionFollowupAsync(SlashContext context, string label, int total, int failed)
    {
        try
        {
            var message = failed == 0
                ? $"{label} complete: {total}/{total} succeeded."
                : $"{label} complete: {total - failed}/{total} succeeded, {failed} failed (see above).";
            var sent = await context.Command.FollowupAsync(message, ephemeral: false);
            await sent.AddReactionAsync(new Emoji(failed == 0 ? "✅" : "⛔"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not send queue-completion follow-up for {Label}.", label);
        }
    }

    private (KeyValuePair<string, AccountConfig>[] Online, KeyValuePair<string, AccountConfig>[] Offline) GetAccountEntriesByConnectivity()
    {
        var online = new List<KeyValuePair<string, AccountConfig>>();
        var offline = new List<KeyValuePair<string, AccountConfig>>();

        foreach (var entry in _config.Accounts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (_registry.GetAgent(entry.Value.AgentId)?.Connected == true)
            {
                online.Add(entry);
            }
            else
            {
                offline.Add(entry);
            }
        }

        return (online.ToArray(), offline.ToArray());
    }

    private async Task<CommandResultInfo?> SendReadyIfNotMenuReadyAsync(AccountConfig account, object args)
    {
        if (!ShouldRunReadyFirst(account))
        {
            return null;
        }

        try
        {
            var result = await _registry.SendCommandAsync(
                account.AgentId,
                "menu_ready",
                args,
                ReadyCommandTimeout);
            return result.Ok || ShouldRunReadyFirst(account)
                ? result
                : ReadySucceededFromCurrentStatus(account, result.Message);
        }
        catch (Exception ex) when (!ShouldRunReadyFirst(account))
        {
            return ReadySucceededFromCurrentStatus(
                account,
                FormatExceptionWithAccountStatus(ex, account));
        }
    }

    private static CommandResultInfo ReadySucceededFromCurrentStatus(AccountConfig account, string failureMessage)
    {
        return new CommandResultInfo(
            account.AgentId,
            "menu-ready-status-fallback",
            Ok: true,
            $"Ready command did not complete cleanly, but current status is already menu-ready. Previous ready result: {failureMessage}",
            Data: null);
    }

    private bool ShouldRunReadyFirst(AccountConfig account)
    {
        var agent = _registry.GetAgent(account.AgentId);
        if (agent?.Connected != true)
        {
            return false;
        }

        if (agent.LastSeenAt is null
            || DateTimeOffset.UtcNow - agent.LastSeenAt.Value > TimeSpan.FromSeconds(45))
        {
            return true;
        }

        return MenuReadyPolicy.ShouldRunReadyFirstFromStatusJson(agent.Connected, agent.LastStatusJson);
    }

    private static bool TryGetBoolean(JsonElement root, string propertyName, out bool value)
    {
        value = false;
        if (root.TryGetProperty(propertyName, out var property)
            && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False))
        {
            value = property.GetBoolean();
            return true;
        }

        return false;
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = "";
        if (root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    private static bool TryGetInt(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static string FormatReadyFirstSuffix(int readyFirstCount)
    {
        if (readyFirstCount == 0)
        {
            return "";
        }

        return $" {readyFirstCount} account(s) need `/d2r ready` before menu automation.";
    }

    private static string FormatOfflineSkipSuffix(IReadOnlyCollection<KeyValuePair<string, AccountConfig>> offlineEntries)
    {
        if (offlineEntries.Count == 0)
        {
            return "";
        }

        return $" Skipped {offlineEntries.Count} offline account(s): {string.Join(", ", offlineEntries.Select(entry => entry.Key))}.";
    }

    private object BuildMenuArgs(
        string accountKey,
        AccountConfig account,
        GameInput? game,
        SlashContext context)
    {
        return new
        {
            accountKey,
            displayName = account.DisplayName ?? accountKey,
            vmName = account.VmName ?? account.AgentId,
            gameName = game?.GameName,
            password = game?.Password,
            difficulty = game?.Difficulty,
            characterSlot = context.GetInt("character-slot") ?? account.CharacterSlot,
            friendRow = context.GetInt("friend-row")
        };
    }

    private static object BuildAccountArgs(string accountKey, AccountConfig account)
    {
        return new
        {
            accountKey,
            displayName = account.DisplayName ?? accountKey,
            vmName = account.VmName ?? account.AgentId
        };
    }

    private GameInput ResolveGameInput(SlashContext context)
    {
        var stored = _db.GetActiveGame();
        var gameName = BlankToNull(context.GetString("name")) ?? stored?.Name;
        if (string.IsNullOrWhiteSpace(gameName))
        {
            throw new InvalidOperationException("Game name is required. Pass name or set it first with /game set.");
        }

        return new GameInput(
            gameName,
            BlankToNull(context.GetString("password")) ?? stored?.Password,
            context.GetString("difficulty") ?? stored?.Difficulty);
    }

    // create-game-all with no name (issue #20, items 3 and 5), in priority order: explicit flags
    // always win; otherwise a template mints a fresh numbered name every call (it never reuses
    // /game show's stored value - that's what would stop netrunner1 -> netrunner2 from advancing);
    // otherwise fall back to today's stored-active-game behavior; otherwise random credentials so
    // the command always works rather than erroring.
    private GameInput ResolveCreateGameAllInput(SlashContext context)
    {
        var explicitName = BlankToNull(context.GetString("name"));
        var difficulty = context.GetString("difficulty");
        var stored = _db.GetActiveGame();
        if (explicitName is not null)
        {
            return new GameInput(explicitName, BlankToNull(context.GetString("password")), difficulty ?? stored?.Difficulty);
        }

        if (_gameTemplate is { } template)
        {
            var (name, password) = template.MintNext();
            return new GameInput(name, password, difficulty);
        }

        if (!string.IsNullOrWhiteSpace(stored?.Name))
        {
            return new GameInput(stored.Name, stored.Password, difficulty ?? stored.Difficulty);
        }

        return new GameInput(RandomGameCredentials.NewGameName(), RandomGameCredentials.NewPassword(), difficulty);
    }

    // join-all with no name (issue #20, items 4 and 5), in priority order: explicit flags always
    // win; otherwise the active game if it's recent enough to plausibly still be running;
    // otherwise the template's current game (so join-all can find what the last create-game-all
    // minted even after the active-game freshness window lapses); otherwise null, meaning do
    // nothing rather than guess.
    private GameInput? ResolveJoinAllInput(SlashContext context)
    {
        var explicitName = BlankToNull(context.GetString("name"));
        var difficulty = context.GetString("difficulty");
        var stored = _db.GetActiveGame();
        if (explicitName is not null)
        {
            return new GameInput(explicitName, BlankToNull(context.GetString("password")), difficulty ?? stored?.Difficulty);
        }

        if (!string.IsNullOrWhiteSpace(stored?.Name) && DateTimeOffset.UtcNow - stored.UpdatedUtc <= ActiveGameFreshness)
        {
            return new GameInput(stored.Name, stored.Password, difficulty ?? stored.Difficulty);
        }

        if (_gameTemplate is { } template)
        {
            var (name, password) = template.Current();
            return new GameInput(name, password, difficulty);
        }

        return null;
    }

    // issue #20, item 7. Assumes a human (not one of this bot's own VMs) creates each numbered
    // game externally using the same template naming, so this only ever joins/waits/leaves/
    // advances - it never calls create-game-all itself. Runs until /d2r join-auto stop:true or
    // an idle timeout (see TryJoinAutoCycleAsync). The interaction's own follow-up token expires
    // long before a multi-cycle farming run would finish, so every message after the initial ack
    // goes straight to the invoking channel instead.
    private async Task StartJoinAutoAsync(SlashContext context, int delaySeconds, bool watch, TimeSpan idleTimeout)
    {
        if (_gameTemplate is null)
        {
            await context.Command.RespondAsync(
                "join-auto needs a template first - set one with /d2r template name:<x> password:<y>.",
                ephemeral: true);
            return;
        }

        await _joinAutoLock.WaitAsync();
        CancellationTokenSource cts;
        try
        {
            if (_joinAutoCts is not null)
            {
                await context.Command.RespondAsync(
                    "join-auto is already running. Use /d2r join-auto stop:true first.",
                    ephemeral: true);
                return;
            }

            cts = new CancellationTokenSource();
            _joinAutoCts = cts;
        }
        finally
        {
            _joinAutoLock.Release();
        }

        await context.Command.RespondAsync(
            $"join-auto started{(delaySeconds > 0 ? $" with a {delaySeconds}s delay before each join attempt" : "")}. Updates will post in this channel until it's stopped.",
            ephemeral: true);

        await StartJoinAutoMonitorAsync(context.Command.Channel);

        _ = Task.Run(() => RunJoinAutoLoopAsync(context, delaySeconds, watch, idleTimeout, cts.Token));
    }

    private async Task StopJoinAutoAsync(SlashContext context)
    {
        var wasRunning = await CancelJoinAutoIfRunningAsync(reason: null);
        await context.Command.RespondAsync(
            wasRunning ? "join-auto is stopping." : "join-auto is not running.",
            ephemeral: true);
    }

    // issue #24: a manual quit on an account join-auto is managing used to just be retried past
    // on the next attempt as though nothing had happened - the user's own words, "if you quit, it
    // should stop auto if its running." Cancelling here also means join-auto stops trying to
    // re-acquire the gate for a new attempt, which is most of what was making the quit itself
    // slow/unreliable in the first place (see the quit/quit-all timeout comments above).
    private async Task<bool> CancelJoinAutoIfRunningAsync(string? reason)
    {
        await _joinAutoLock.WaitAsync();
        try
        {
            if (_joinAutoCts is null)
            {
                return false;
            }

            _joinAutoStopReason = reason;
            _joinAutoCts.Cancel();
            _joinAutoCts = null;
            return true;
        }
        finally
        {
            _joinAutoLock.Release();
        }
    }

    private async Task RunJoinAutoLoopAsync(SlashContext context, int delaySeconds, bool watch, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        var channel = context.Command.Channel;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_gameTemplate is not { } template)
                {
                    await SendJoinAutoMessageAsync(channel, "join-auto stopped: the template was cleared.");
                    await CompleteJoinAutoMonitorAsync(ok: true, "Stopped: the template was cleared.");
                    break;
                }

                var (gameName, password) = template.MintNext();
                var game = new GameInput(gameName, password, Difficulty: null);

                var outcome = await TryJoinAutoCycleAsync(channel, context, game, delaySeconds, watch, idleTimeout, cancellationToken);
                if (outcome == JoinAutoCycleOutcome.IdleTimedOut)
                {
                    await SendJoinAutoMessageAsync(channel, "join-auto: idle timeout detected, disabled.");
                    await CompleteJoinAutoMonitorAsync(ok: false, $"Idle timeout - gave up joining {gameName} and disabled.");
                    break;
                }

                await SendJoinAutoMessageAsync(channel, $"join-auto: everyone is in {gameName}. Watching for someone to leave...");
                await UpdateJoinAutoMonitorAsync("Everyone is in - watching for someone to leave.");

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                var baseline = await TryFetchFirstOnlineAccountPlayerCountAsync();
                await WaitForPlayerCountDropAsync(baseline, cancellationToken);

                await SendJoinAutoMessageAsync(channel, $"join-auto: player count dropped - leaving {gameName}.");
                await UpdateJoinAutoMonitorAsync($"Player count dropped - leaving {gameName}...");
                await LeaveAllJoinAutoAsync(channel);
                _joinAutoCyclesCompleted++;
                await UpdateJoinAutoMonitorAsync($"Left {gameName}. Advancing to the next game...", joined: 0);
            }
        }
        catch (OperationCanceledException)
        {
            var reasonText = _joinAutoStopReason is { } reason ? $"join-auto stopped: {reason}." : "join-auto stopped.";
            await SendJoinAutoMessageAsync(channel, reasonText);
            await CompleteJoinAutoMonitorAsync(ok: true, reasonText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "join-auto loop failed.");
            await SendJoinAutoMessageAsync(channel, $"join-auto stopped unexpectedly: {ex.Message}");
            await CompleteJoinAutoMonitorAsync(ok: false, $"Stopped unexpectedly: {ex.Message}");
        }
        finally
        {
            await _joinAutoLock.WaitAsync();
            try
            {
                if (_joinAutoCts?.Token == cancellationToken)
                {
                    _joinAutoCts = null;
                }

                _joinAutoStopReason = null;
            }
            finally
            {
                _joinAutoLock.Release();
            }
        }
    }

    private enum JoinAutoCycleOutcome
    {
        Joined,
        IdleTimedOut,
    }

    // Failing to join the next numbered game on the first few attempts is the normal, expected
    // shape of this flow, not a problem - the human running the farming session has to notice the
    // previous game ended and set the next one up, which takes a real amount of wall-clock time.
    // So this retries patiently rather than giving up after a small fixed attempt count (the
    // user's own words: "that failure isn't the end of the world, it just should be part of the
    // flow"). idleTimeout is the actual safety net: if it's genuinely stuck (nobody ever sets up
    // the next game, or something is actually broken), give up after idleTimeout of unbroken
    // failure instead of retrying forever and silently never telling anyone. Per-attempt failure
    // detail is watch-gated - it's only useful for debugging a real problem, not for the routine
    // wait, and the user explicitly only wants to see it when intentionally watching for that.
    private async Task<JoinAutoCycleOutcome> TryJoinAutoCycleAsync(
        IMessageChannel channel, SlashContext context, GameInput game, int delaySeconds, bool watch, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        var deadlineUtc = DateTime.UtcNow + idleTimeout;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }

            var (entries, offlineEntries) = GetAccountEntriesByConnectivity();
            if (entries.Length == 0)
            {
                await UpdateJoinAutoMonitorAsync($"No online accounts available (attempt {attempt}).", gameName: game.GameName, joined: 0, total: 0);
                if (watch)
                {
                    await SendJoinAutoMessageAsync(channel, $"join-auto: no online accounts available (attempt {attempt})." + FormatOfflineSkipSuffix(offlineEntries));
                }
            }
            else
            {
                await UpdateJoinAutoMonitorAsync(
                    attempt == 1 ? $"Joining {game.GameName}..." : $"Joining {game.GameName} (attempt {attempt})...",
                    gameName: game.GameName,
                    joined: 0,
                    total: entries.Length);

                var staggerSeconds = _config.ClientStaggerSeconds ?? _config.StartAllDelaySeconds;
                var argsByAccount = entries.ToDictionary(
                    entry => entry.Key,
                    entry => BuildMenuArgs(entry.Key, entry.Value, game, context),
                    StringComparer.OrdinalIgnoreCase);
                var prepareTasks = entries
                    .Select((entry, index) => new
                    {
                        entry.Key,
                        Task = RunJoinAllPrepareEntryAsync(entry, index, staggerSeconds, argsByAccount[entry.Key])
                    })
                    .ToDictionary(item => item.Key, item => item.Task, StringComparer.OrdinalIgnoreCase);
                var joinResults = await Task.WhenAll(entries.Select(entry =>
                    SubmitJoinAutoEntryAsync(entry, prepareTasks[entry.Key], argsByAccount[entry.Key])));

                if (joinResults.All(result => result.Ok))
                {
                    await UpdateJoinAutoMonitorAsync($"Everyone joined {game.GameName}.", joined: entries.Length, total: entries.Length);
                    return JoinAutoCycleOutcome.Joined;
                }

                if (watch)
                {
                    var failed = joinResults.Where(result => !result.Ok);
                    await SendJoinAutoMessageAsync(
                        channel,
                        $"join-auto: attempt {attempt} to join {game.GameName} failed - "
                            + string.Join("; ", failed.Select(result => $"{result.AccountKey}: {result.Message}")));
                }
            }

            if (DateTime.UtcNow >= deadlineUtc)
            {
                return JoinAutoCycleOutcome.IdleTimedOut;
            }
        }
    }

    private static Task SendJoinAutoMessageAsync(IMessageChannel channel, string content)
    {
        return channel.SendMessageAsync(DiscordMessageTruncator.Truncate(content));
    }

    // Issue #25: capture a follow-bind fingerprint from one account's friend row 1, then push it
    // (or clear it) to every online account so follow-auto can recognize the same name anywhere.
    // Deliberately simpler messaging than join-auto's persistent monitor message - plain
    // progress/outcome posts only, not a live-edited status message. Worth revisiting if this
    // sees as much use as join-auto did.
    private async Task HandleFollowBindAsync(SlashContext context, bool bindFlag)
    {
        var (online, _) = GetAccountEntriesByConnectivity();

        if (!bindFlag)
        {
            await CancelFollowAutoIfRunningAsync("the follow-bind target was cleared");
            await context.Command.DeferAsync(ephemeral: true);
            var cleared = 0;
            foreach (var (accountKey, account) in online)
            {
                try
                {
                    await _registry.SendCommandAsync(account.AgentId, "follow_clear_template", new { }, TimeSpan.FromSeconds(15));
                    cleared++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "follow_clear_template failed for {AccountKey}.", accountKey);
                }
            }

            _followBoundAccountKey = null;
            await context.Command.ModifyOriginalResponseAsync(
                properties => properties.Content = $"Follow-bind cleared on {cleared}/{online.Length} online accounts.");
            return;
        }

        var bindAccountKey = context.GetString("account");
        if (bindAccountKey is null)
        {
            await context.Command.RespondAsync(
                "follow bind:true requires account (whose friend row 1 to capture from).",
                ephemeral: true);
            return;
        }

        var (resolvedAccountKey, bindAccount) = RequireAccount(bindAccountKey);
        await context.Command.DeferAsync(ephemeral: true);

        CommandResultInfo captureResult;
        try
        {
            captureResult = await _registry.SendCommandAsync(
                bindAccount.AgentId, "menu_follow_bind", BuildAccountArgs(resolvedAccountKey, bindAccount), TimeSpan.FromSeconds(210));
        }
        catch (Exception ex)
        {
            await context.Command.ModifyOriginalResponseAsync(
                properties => properties.Content = $"follow bind:true failed to capture from {resolvedAccountKey}: {ex.Message}");
            return;
        }

        if (!captureResult.Ok
            || captureResult.Data is not { } data
            || !data.TryGetProperty("fingerprint", out var fingerprintProperty)
            || fingerprintProperty.GetString() is not { } fingerprint)
        {
            await context.Command.ModifyOriginalResponseAsync(
                properties => properties.Content = $"follow bind:true failed to capture from {resolvedAccountKey}: {captureResult.Message}");
            return;
        }

        var distributed = 0;
        var distributionFailures = new List<string>();
        foreach (var (accountKey, account) in online)
        {
            try
            {
                var result = await _registry.SendCommandAsync(account.AgentId, "follow_set_template", new { fingerprint }, TimeSpan.FromSeconds(15));
                if (result.Ok)
                {
                    distributed++;
                }
                else
                {
                    distributionFailures.Add($"{accountKey}: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "follow_set_template failed for {AccountKey}.", accountKey);
                distributionFailures.Add($"{accountKey}: {ex.Message}");
            }
        }

        _followBoundAccountKey = resolvedAccountKey;
        var followBindMessage =
            $"Captured the friend at {resolvedAccountKey}'s friend row 1 and distributed it to {distributed}/{online.Length} online accounts. "
            + "Use /d2r follow auto:true to start following.";
        if (distributionFailures.Count > 0)
        {
            followBindMessage += $" Template save failures: {string.Join("; ", distributionFailures)}";
        }

        await context.Command.ModifyOriginalResponseAsync(
            properties => properties.Content = DiscordMessageTruncator.Truncate(followBindMessage));
    }

    private async Task StartFollowAutoAsync(SlashContext context, int delaySeconds, TimeSpan idleTimeout)
    {
        await _followAutoLock.WaitAsync();
        CancellationTokenSource cts;
        try
        {
            if (_followAutoCts is not null)
            {
                await context.Command.RespondAsync(
                    "follow auto:true is already running. Use /d2r follow auto:false first.",
                    ephemeral: true);
                return;
            }

            cts = new CancellationTokenSource();
            _followAutoCts = cts;
        }
        finally
        {
            _followAutoLock.Release();
        }

        await context.Command.RespondAsync(
            $"follow-auto started{(delaySeconds > 0 ? $" with a {delaySeconds}s delay between checks" : "")}. Updates will post in this channel until it's stopped.",
            ephemeral: true);

        _ = Task.Run(() => RunFollowAutoLoopAsync(context, delaySeconds, idleTimeout, cts.Token));
    }

    private async Task StopFollowAutoAsync(SlashContext context)
    {
        var wasRunning = await CancelFollowAutoIfRunningAsync(reason: null);
        await context.Command.RespondAsync(
            wasRunning ? "follow-auto is stopping." : "follow-auto is not running.",
            ephemeral: true);
    }

    // Same "if you quit, it should stop auto if its running" precedent as join-auto (issue #24) -
    // wired into the same quit/quit-all call sites as CancelJoinAutoIfRunningAsync.
    private async Task<bool> CancelFollowAutoIfRunningAsync(string? reason)
    {
        await _followAutoLock.WaitAsync();
        try
        {
            if (_followAutoCts is null)
            {
                return false;
            }

            _followAutoStopReason = reason;
            _followAutoCts.Cancel();
            _followAutoCts = null;
            return true;
        }
        finally
        {
            _followAutoLock.Release();
        }
    }

    private async Task RunFollowAutoLoopAsync(SlashContext context, int delaySeconds, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        var channel = context.Command.Channel;
        var joined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var idleDeadlineUtc = DateTimeOffset.UtcNow + idleTimeout;
        string? lastWaitingReport = null;
        var lastWaitingReportUtc = DateTimeOffset.MinValue;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (online, _) = GetAccountEntriesByConnectivity();
                var pending = online.Where(entry => !joined.Contains(entry.Key)).ToArray();
                if (pending.Length == 0 && online.Length > 0)
                {
                    await SendJoinAutoMessageAsync(channel, "follow-auto: all online accounts have joined the bound friend's game.");
                    break;
                }

                var anyBound = false;
                var unboundReports = new List<string>();
                var checkFailures = new List<string>();
                var waitingReports = new List<string>();
                foreach (var (accountKey, account) in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CommandResultInfo result;
                    try
                    {
                        result = await _registry.SendCommandAsync(
                            account.AgentId, "menu_follow_auto_check", BuildAccountArgs(accountKey, account), TimeSpan.FromSeconds(210), cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "menu_follow_auto_check failed for {AccountKey}.", accountKey);
                        checkFailures.Add($"{accountKey}: {ex.Message}");
                        continue;
                    }

                    if (!result.Ok)
                    {
                        checkFailures.Add($"{accountKey}: {result.Message}");
                        continue;
                    }

                    if (result.Data is not { } data || !TryGetBoolean(data, "bound", out var bound))
                    {
                        checkFailures.Add($"{accountKey}: follow check returned no bound flag ({result.Message})");
                        continue;
                    }

                    if (!bound)
                    {
                        unboundReports.Add($"{accountKey}: {result.Message}");
                        continue;
                    }

                    anyBound = true;
                    if (TryGetBoolean(data, "d2rReady", out var d2rReady) && !d2rReady)
                    {
                        waitingReports.Add($"{accountKey}: {result.Message}");
                        continue;
                    }

                    if (TryGetBoolean(data, "joined", out var didJoin) && didJoin)
                    {
                        joined.Add(accountKey);
                        await SendJoinAutoMessageAsync(channel, $"follow-auto: {accountKey} joined the bound friend's game.");
                        idleDeadlineUtc = DateTimeOffset.UtcNow + idleTimeout;
                    }
                }

                if (!anyBound)
                {
                    var details = string.Join("; ", checkFailures.Concat(unboundReports));
                    var reason = checkFailures.Count > 0
                        ? "follow-auto stopped: no VM reported a usable follow-bind fingerprint. "
                        : "follow-auto stopped: no follow-bind fingerprint is set. ";
                    await SendJoinAutoMessageAsync(channel, reason + details);
                    break;
                }

                if (waitingReports.Count > 0)
                {
                    var waitingReport = string.Join("; ", waitingReports);
                    if (!string.Equals(waitingReport, lastWaitingReport, StringComparison.Ordinal)
                        || DateTimeOffset.UtcNow - lastWaitingReportUtc >= TimeSpan.FromMinutes(5))
                    {
                        await SendJoinAutoMessageAsync(channel, $"follow-auto waiting: {waitingReport}");
                        lastWaitingReport = waitingReport;
                        lastWaitingReportUtc = DateTimeOffset.UtcNow;
                    }
                }

                if (DateTimeOffset.UtcNow >= idleDeadlineUtc)
                {
                    await SendJoinAutoMessageAsync(channel, "follow-auto: idle timeout detected, disabled.");
                    break;
                }

                if (delaySeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            var reasonText = _followAutoStopReason is { } reason ? $"follow-auto stopped: {reason}." : "follow-auto stopped.";
            await SendJoinAutoMessageAsync(channel, reasonText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "follow-auto loop crashed.");
            await SendJoinAutoMessageAsync(channel, $"follow-auto stopped on an unexpected error: {ex.Message}");
        }
        finally
        {
            await _followAutoLock.WaitAsync();
            try
            {
                if (_followAutoCts?.Token == cancellationToken)
                {
                    _followAutoCts = null;
                }

                _followAutoStopReason = null;
            }
            finally
            {
                _followAutoLock.Release();
            }
        }
    }

    // issue #24: "the join-auto feature should still make a game monitor like the other one
    // with the bot / player count / game name etc." Deliberately a separate message/state from
    // _activeSessionMessage (create-game-all/join-all's own monitor) rather than sharing it - one
    // persistent message edited for the entire join-auto run (confirmed with the user), not a
    // fresh one per game the way the other one is per-invocation, since a farming session can
    // advance through many numbered games over hours.
    private async Task StartJoinAutoMonitorAsync(IMessageChannel channel)
    {
        _joinAutoMonitorGameName = null;
        _joinAutoMonitorJoined = 0;
        _joinAutoMonitorTotal = 0;
        _joinAutoCyclesCompleted = 0;
        _joinAutoStartedUtc = DateTimeOffset.UtcNow;
        try
        {
            _joinAutoMonitorMessage = await channel.SendMessageAsync(await FormatJoinAutoMonitorMessageAsync("Starting..."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start join-auto monitor message.");
            _joinAutoMonitorMessage = null;
        }
    }

    private async Task UpdateJoinAutoMonitorAsync(string status, string? gameName = null, int? joined = null, int? total = null)
    {
        if (_joinAutoMonitorMessage is null)
        {
            return;
        }

        if (gameName is not null)
        {
            _joinAutoMonitorGameName = gameName;
        }

        if (joined is not null)
        {
            _joinAutoMonitorJoined = joined.Value;
        }

        if (total is not null)
        {
            _joinAutoMonitorTotal = total.Value;
        }

        try
        {
            var content = await FormatJoinAutoMonitorMessageAsync(status);
            await _joinAutoMonitorMessage.ModifyAsync(properties => properties.Content = content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update join-auto monitor message.");
        }
    }

    private async Task CompleteJoinAutoMonitorAsync(bool ok, string status)
    {
        if (_joinAutoMonitorMessage is null)
        {
            return;
        }

        try
        {
            var content = await FormatJoinAutoMonitorMessageAsync(status);
            await _joinAutoMonitorMessage.ModifyAsync(properties => properties.Content = content);
            await _joinAutoMonitorMessage.AddReactionAsync(new Emoji(ok ? "✅" : "⛔"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not complete join-auto monitor message.");
        }
        finally
        {
            _joinAutoMonitorMessage = null;
        }
    }

    private async Task<string> FormatJoinAutoMonitorMessageAsync(string status)
    {
        var elapsed = _joinAutoStartedUtc is { } started ? DateTimeOffset.UtcNow - started : TimeSpan.Zero;
        var lines = new List<string>
        {
            "join-auto monitor",
            $"Game: {_joinAutoMonitorGameName ?? "(none yet)"}",
            $"Status: {status}",
            $"Bots in game: {_joinAutoMonitorJoined}/{_joinAutoMonitorTotal}"
        };

        var playerCount = await TryFetchJoinAutoPlayerCountLineAsync();
        if (playerCount is not null)
        {
            lines.Add(playerCount);
        }

        lines.Add($"Cycles completed: {_joinAutoCyclesCompleted}");
        lines.Add($"Session elapsed: {FormatElapsed(elapsed)}");

        return string.Join("\n", lines);
    }

    // Mirrors TryFetchPlayerCountLineAsync, but reads from join-auto's own account selection
    // instead of _activeSessionRepresentativeAgentId - a different mechanism's state that this
    // must not touch (see the comment on SubmitJoinAutoEntryAsync).
    private async Task<string?> TryFetchJoinAutoPlayerCountLineAsync()
    {
        var (entries, _) = GetAccountEntriesByConnectivity();
        if (entries.Length == 0)
        {
            return null;
        }

        try
        {
            var result = await _registry.SendCommandAsync(entries[0].Value.AgentId, "status", args: null, TimeSpan.FromSeconds(6));
            if (!result.Ok || result.Data is not { } data)
            {
                return null;
            }

            var json = data.GetRawText();
            return ShouldShowPartyMemberCount(json) && TryReadPartyMemberCountSummary(json, out var summary)
                ? $"Players in game: {summary}"
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch live player count for join-auto monitor.");
            return null;
        }
    }

    // Mirrors RunJoinAllPreparedEntryAsync minus the IncrementGameSessionJoinedAsync call -
    // join-auto reports its own progress via channel messages per attempt instead of a single
    // live-edited session message, and must not touch _activeSessionMessage/_sessionLock state
    // that an unrelated, concurrently-running manual create-game-all/join-all might own.
    private async Task<JoinResult> SubmitJoinAutoEntryAsync(
        KeyValuePair<string, AccountConfig> entry, Task<JoinResult> prepareTask, object args)
    {
        var prepareResult = await prepareTask;
        if (!prepareResult.Ok)
        {
            return prepareResult;
        }

        try
        {
            var joinResult = await _registry.SendCommandAsync(entry.Value.AgentId, "menu_submit_join_game", args, TimeSpan.FromSeconds(210));
            return new JoinResult(entry.Key, joinResult.Ok, joinResult.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "join-auto submit failed for {AccountKey}.", entry.Key);
            return new JoinResult(entry.Key, false, FormatExceptionWithAccountStatus(ex, entry.Key, entry.Value));
        }
    }

    private async Task LeaveAllJoinAutoAsync(IMessageChannel channel)
    {
        var (entries, _) = GetAccountEntriesByConnectivity();
        if (entries.Length == 0)
        {
            await SendJoinAutoMessageAsync(channel, "join-auto: no online accounts to leave with.");
            return;
        }

        var leaveResults = await Task.WhenAll(entries.Select(async entry =>
        {
            try
            {
                var result = await _registry.SendCommandAsync(entry.Value.AgentId, "menu_save_exit", BuildAccountArgs(entry.Key, entry.Value), TimeSpan.FromSeconds(210));
                return new JoinResult(entry.Key, result.Ok, result.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "join-auto leave failed for {AccountKey}.", entry.Key);
                return new JoinResult(entry.Key, false, FormatExceptionWithAccountStatus(ex, entry.Key, entry.Value));
            }
        }));

        var failed = leaveResults.Where(result => !result.Ok).ToArray();
        await SendJoinAutoMessageAsync(channel, failed.Length == 0
            ? "join-auto: all accounts left."
            : "join-auto: leave failed for " + string.Join("; ", failed.Select(result => $"{result.AccountKey}: {result.Message}")));
    }

    // Polls independently of RunPartyMemberMonitorAsync's own 30s tick - this needs to notice a
    // drop promptly while actively farming, not just whenever the next heartbeat happens to land.
    // Only returns once a drop is actually detected; the only other way out is cancellation,
    // which throws OperationCanceledException through Task.Delay and is handled by the caller.
    private async Task WaitForPlayerCountDropAsync(int? baseline, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            var current = await TryFetchFirstOnlineAccountPlayerCountAsync();
            if (current is { } count && baseline is { } known && count < known)
            {
                return;
            }

            if (baseline is null)
            {
                baseline = current;
            }
        }
    }

    private async Task<int?> TryFetchFirstOnlineAccountPlayerCountAsync()
    {
        var (entries, _) = GetAccountEntriesByConnectivity();
        if (entries.Length == 0)
        {
            return null;
        }

        try
        {
            var result = await _registry.SendCommandAsync(entries[0].Value.AgentId, "status", args: null, TimeSpan.FromSeconds(6));
            if (!result.Ok || result.Data is not { } data)
            {
                return null;
            }

            using var document = JsonDocument.Parse(data.GetRawText());
            return TryGetInt(document.RootElement, "lastPartyMemberCount", out var otherMembers) ? otherMembers + 1 : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private (string AccountKey, AccountConfig Account) RequireAccount(string accountKey)
    {
        if (!_config.Accounts.TryGetValue(accountKey, out var account))
        {
            throw new InvalidOperationException($"Unknown account \"{accountKey}\".");
        }

        return (accountKey, account);
    }

    private bool IsAllowed(ulong userId)
    {
        return _config.AllowedDiscordUserIds.Length == 0
            || _config.AllowedDiscordUserIds.Contains(userId.ToString(), StringComparer.Ordinal);
    }

    private string FormatHealth()
    {
        var agents = _registry.Snapshot();
        var connected = agents.Count(agent => agent.Connected);
        var lines = agents.Select(agent =>
        {
            var label = string.IsNullOrWhiteSpace(agent.DisplayName)
                ? agent.Id
                : $"{agent.Id} ({agent.DisplayName})";
            return $"{(agent.Connected ? "online " : "offline")} {label}";
        });

        return string.Join("\n", new[] { $"Agents: {connected}/{agents.Count} connected" }.Concat(lines));
    }

    private string FormatAllAccountStatuses()
    {
        var lines = _config.Accounts.Select(pair => FormatAccountStatusLine(pair.Key, pair.Value));
        return string.Join("\n", new[] { $"D2RHost version: {GetHostVersionText()}" }.Concat(lines));
    }

    private async Task<string> FormatAllAccountStatusesLiveAsync(CancellationToken cancellationToken)
    {
        var lines = await Task.WhenAll(_config.Accounts.Select(
            pair => FormatAccountStatusLineLiveAsync(pair.Key, pair.Value, cancellationToken)));
        return string.Join("\n", new[] { $"D2RHost version: {GetHostVersionText()}" }.Concat(lines));
    }

    private static string GetHostVersionText()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(DiscordBot).Assembly;
        return assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "0.0.0";
    }

    private string FormatAccountStatus(string accountKey)
    {
        var (_, account) = RequireAccount(accountKey);
        return FormatAccountStatusLine(accountKey, account);
    }

    private async Task<string> FormatAccountStatusLiveAsync(string accountKey, CancellationToken cancellationToken)
    {
        var (_, account) = RequireAccount(accountKey);
        return await FormatAccountStatusLineLiveAsync(accountKey, account, cancellationToken);
    }

    private string FormatExceptionWithAccountStatus(Exception ex, AccountConfig account)
    {
        var entry = _config.Accounts.FirstOrDefault(
            pair => string.Equals(pair.Value.AgentId, account.AgentId, StringComparison.OrdinalIgnoreCase));
        var accountKey = string.IsNullOrWhiteSpace(entry.Key) ? account.AgentId : entry.Key;
        return FormatExceptionWithAccountStatus(ex, accountKey, account);
    }

    private string FormatExceptionWithAccountStatus(Exception ex, string accountKey, AccountConfig account)
    {
        return $"{ex.Message}. Current status: {FormatAccountStatusLine(accountKey, account)}";
    }

    private string FormatAccountStatusLine(string accountKey, AccountConfig account)
    {
        var name = FormatAccountDisplayName(accountKey, account);
        var agent = _registry.GetAgent(account.AgentId);
        if (agent?.Connected != true)
        {
            return $"{name}: offline";
        }

        return FormatStatusLine(name, agent, agent.LastStatusJson);
    }

    // /d2r status used to read whatever the last heartbeat happened to cache, which could be
    // tens of seconds stale and - while status collection was timing out - could be a frozen
    // "unknown" snapshot from before the operator even asked. Sending a live "status" command
    // gets a real-time read every time the user actually asks.
    private async Task<string> FormatAccountStatusLineLiveAsync(
        string accountKey, AccountConfig account, CancellationToken cancellationToken)
    {
        var name = FormatAccountDisplayName(accountKey, account);
        var agent = _registry.GetAgent(account.AgentId);
        if (agent?.Connected != true)
        {
            return $"{name}: offline";
        }

        CommandResultInfo result;
        try
        {
            result = await _registry.SendCommandAsync(
                account.AgentId, "status", args: null, TimeSpan.FromSeconds(20), cancellationToken);
        }
        catch (Exception ex)
        {
            return $"{name}: live status check failed ({ex.Message}); last cached: {FormatStatusLine(name, agent, agent.LastStatusJson)}";
        }

        if (!result.Ok || result.Data is not { } data)
        {
            return $"{name}: live status check failed: {result.Message}; last cached: {FormatStatusLine(name, agent, agent.LastStatusJson)}";
        }

        return FormatStatusLine(name, agent, data.GetRawText());
    }

    private static string FormatAccountDisplayName(string accountKey, AccountConfig account)
    {
        return string.IsNullOrWhiteSpace(account.DisplayName)
            ? accountKey
            : $"{accountKey} ({account.DisplayName})";
    }

    private static string FormatStatusLine(string name, AgentSnapshot agent, string? statusJson)
    {
        var status = ParseStatus(statusJson);
        var battleNet = FormatRunning(status.TryGetValue("battleNetRunning", out var battleNetRunning) ? battleNetRunning : null);
        var d2r = FormatRunning(status.TryGetValue("d2rRunning", out var d2rRunning) ? d2rRunning : null);
        var visible = TryReadStatusString(statusJson, "d2rVisibleState", out var visibleState)
            ? $", visible {visibleState}"
            : "";
        var activity = TryReadStatusString(statusJson, "d2rActivityState", out var activityState)
            ? $", state {activityState}"
            : "";
        var statusMode = TryReadStatusString(statusJson, "statusMode", out var mode)
            ? $", statusMode {mode}"
            : "";
        var statusError = TryReadStatusString(statusJson, "statusError", out var error)
            && !string.IsNullOrWhiteSpace(error)
                ? $", statusError {error}"
                : "";
        var processDiscovery = d2rRunning != true
            && TryReadD2RProcessDiscoverySummary(statusJson, out var processDiscoverySummary)
            ? $", process {processDiscoverySummary}"
            : "";
        var input = TryReadD2RInputSummary(statusJson, out var inputSummary)
            ? $", input {inputSummary}"
            : "";
        var lastInput = TryReadLastInputActionSummary(statusJson, out var lastInputSummary)
            ? $", lastInput {lastInputSummary}"
            : "";
        // The watch ticker line already gets this (added when a stuck create-game-all run showed
        // one click landing and then total silence with no way to tell what it was doing). This
        // is the other place a stuck command's status gets surfaced - a timed-out menu_* command's
        // failure message - and it had the exact same blind spot until now.
        var checkpoint = TryReadCheckpointSummary(statusJson, out var checkpointSummary)
            ? $", at {checkpointSummary}"
            : "";
        var version = string.IsNullOrWhiteSpace(agent.Version)
            ? ""
            : $", version {agent.Version}";
        var lastSeen = agent.LastSeenAt?.ToLocalTime().ToString("G") ?? "unknown";
        return $"{name}: online{version}, Battle.net {battleNet}, D2R {d2r}{visible}{activity}{statusMode}{statusError}{processDiscovery}{input}{lastInput}{checkpoint}, seen {lastSeen}";
    }

    private static Dictionary<string, bool?> ParseStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateObject()
                .Where(property => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                .ToDictionary(property => property.Name, property => (bool?)property.Value.GetBoolean(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool TryReadStatusString(string? json, string propertyName, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryGetString(document.RootElement, propertyName, out value);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadD2RInputSummary(string? json, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("d2rInput", out var input)
                || input.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return false;
            }

            var interactive = TryGetBoolean(input, "userInteractive", out var isInteractive)
                ? isInteractive.ToString().ToLowerInvariant()
                : "?";
            var window = TryGetBoolean(input, "hasMainWindow", out var hasWindow)
                ? hasWindow.ToString().ToLowerInvariant()
                : "?";
            var foreground = TryGetBoolean(input, "isForeground", out var isForeground)
                ? isForeground.ToString().ToLowerInvariant()
                : "?";
            var foregroundProcess = TryGetString(input, "foregroundProcessName", out var foregroundName)
                ? foregroundName
                : "?";
            var targetProcess = TryGetString(input, "processName", out var processName)
                ? processName
                : "?";
            var targetTitle = TryGetString(input, "mainWindowTitle", out var mainWindowTitle)
                ? mainWindowTitle
                : "?";
            var session = TryGetInt(input, "sessionId", out var sessionId)
                ? sessionId.ToString()
                : "?";
            var screen = TryGetInt(input, "screenWidth", out var screenWidth)
                && TryGetInt(input, "screenHeight", out var screenHeight)
                ? $"{screenWidth}x{screenHeight}"
                : "?";
            var windowRect = TryReadInputRect(input, "windowRect");
            var clientRect = TryReadInputRect(input, "clientRect");
            var agentElevated = TryGetBoolean(input, "agentElevated", out var isAgentElevated)
                ? isAgentElevated.ToString().ToLowerInvariant()
                : "?";
            var targetElevated = TryGetBoolean(input, "targetElevated", out var isTargetElevated)
                ? isTargetElevated.ToString().ToLowerInvariant()
                : "?";
            var sessionActive = TryGetBoolean(input, "targetSessionActive", out var isSessionActive)
                ? isSessionActive.ToString().ToLowerInvariant()
                : "?";

            value = $"interactive={interactive}, session={session}, sessionActive={sessionActive}, target={targetProcess}, title={targetTitle}, window={window}, foreground={foreground}, fg={foregroundProcess}, agentElevated={agentElevated}, targetElevated={targetElevated}, screen={screen}, windowRect={windowRect}, client={clientRect}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadD2RProcessDiscoverySummary(string? json, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("d2rProcessDiscovery", out var discovery)
                || discovery.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return false;
            }

            var searchNames = discovery.TryGetProperty("searchNames", out var searchNamesElement)
                && searchNamesElement.ValueKind == JsonValueKind.Array
                    ? string.Join("/", searchNamesElement.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString())
                        .Where(item => !string.IsNullOrWhiteSpace(item)))
                    : "?";

            if (!discovery.TryGetProperty("matches", out var matches)
                || matches.ValueKind != JsonValueKind.Array)
            {
                value = $"search={searchNames}, matches=?";
                return true;
            }

            var matchSummaries = FormatProcessMatchSummaries(matches);
            value = matchSummaries.Length == 0
                ? $"search={searchNames}, matches=0"
                : $"search={searchNames}, matches={string.Join("|", matchSummaries)}";

            if (matchSummaries.Length == 0
                && discovery.TryGetProperty("fallbackMatches", out var fallbackMatches)
                && fallbackMatches.ValueKind == JsonValueKind.Array)
            {
                var fallbackSummaries = FormatProcessMatchSummaries(fallbackMatches);
                if (fallbackSummaries.Length > 0)
                {
                    value += $", unmatchedD2rLike={string.Join("|", fallbackSummaries)}";
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string[] FormatProcessMatchSummaries(JsonElement matches)
    {
        return matches.EnumerateArray()
            .Take(3)
            .Select(match =>
            {
                var name = TryGetString(match, "processName", out var processName) ? processName : "?";
                var id = match.TryGetProperty("processId", out var processId)
                    && processId.TryGetInt32(out var pid)
                        ? pid.ToString()
                        : "?";
                var window = TryGetBoolean(match, "hasMainWindow", out var hasMainWindow)
                    ? (hasMainWindow ? "window" : "noWindow")
                    : "window?";
                return $"{name}#{id}:{window}";
            })
            .ToArray();
    }

    private static string TryReadInputRect(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var rect)
            || rect.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || !TryGetInt(rect, "left", out var left)
            || !TryGetInt(rect, "top", out var top)
            || !TryGetInt(rect, "right", out var right)
            || !TryGetInt(rect, "bottom", out var bottom))
        {
            return "?";
        }

        return $"{left},{top},{Math.Max(right - left, 0)}x{Math.Max(bottom - top, 0)}";
    }

    private static bool TryReadLastInputActionSummary(string? json, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("lastInputAction", out var action)
                || action.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return false;
            }

            var kind = TryGetString(action, "kind", out var kindValue) ? kindValue : "?";
            var button = TryGetString(action, "button", out var buttonValue) ? buttonValue : "?";
            var screen = TryGetInt(action, "screenX", out var x)
                && TryGetInt(action, "screenY", out var y)
                ? $"{x},{y}"
                : "?,?";
            var cursorBefore = TryReadCursor(action, "cursorBefore");
            var cursorAfter = TryReadCursor(action, "cursorAfter");
            var foregroundBefore = TryGetBoolean(action, "d2rForegroundBefore", out var fgBefore)
                ? fgBefore.ToString().ToLowerInvariant()
                : "?";
            var foregroundAfter = TryGetBoolean(action, "d2rForegroundAfter", out var fgAfter)
                ? fgAfter.ToString().ToLowerInvariant()
                : "?";
            var processBefore = TryGetString(action, "foregroundProcessBefore", out var beforeProcess)
                ? beforeProcess
                : "?";
            var processAfter = TryGetString(action, "foregroundProcessAfter", out var afterProcess)
                ? afterProcess
                : "?";

            value = $"{kind}/{button}@{screen}, cursor={cursorBefore}->{cursorAfter}, d2rFg={foregroundBefore}->{foregroundAfter}, fg={processBefore}->{processAfter}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string TryReadCursor(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var cursor)
            || cursor.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || !TryGetInt(cursor, "x", out var x)
            || !TryGetInt(cursor, "y", out var y))
        {
            return "?,?";
        }

        return $"{x},{y}";
    }

    private static string FormatCommandResult(bool ok, string message)
    {
        return $"{(ok ? "OK" : "Failed")}: {message}";
    }

    private static string FormatActiveGame(ActiveGame game)
    {
        return string.Join("\n", new[]
        {
            $"Game: {game.Name}",
            $"Password: {game.Password ?? "(none)"}",
            $"Difficulty: {game.Difficulty ?? "(not set)"}",
            string.IsNullOrWhiteSpace(game.Notes) ? null : $"Notes: {game.Notes}",
            $"Updated: {game.UpdatedUtc.ToLocalTime():G}"
        }.Where(line => line is not null));
    }

    private static string FormatRunning(bool? value)
    {
        return value switch
        {
            true => "running",
            false => "stopped",
            _ => "unknown"
        };
    }

    private static bool TryReadScreenshot(JsonElement data, out byte[] bytes, out string extension)
    {
        bytes = [];
        extension = "png";
        if (!data.TryGetProperty("base64", out var base64Property)
            || base64Property.GetString() is not { } base64
            || string.IsNullOrWhiteSpace(base64))
        {
            return false;
        }

        if (data.TryGetProperty("mimeType", out var mimeTypeProperty)
            && string.Equals(mimeTypeProperty.GetString(), "image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            extension = "jpg";
        }

        bytes = Convert.FromBase64String(base64);
        return true;
    }

    private static string? BlankToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static ulong ParseChannelId(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("<#") && trimmed.EndsWith('>'))
        {
            trimmed = trimmed[2..^1];
        }

        return ulong.TryParse(trimmed, out var channelId)
            ? channelId
            : throw new InvalidOperationException($"Invalid Discord channel ID: {value}");
    }

    private static string PsQuote(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static string WindowsArgumentQuote(string value)
    {
        if (value.Length > 0 && !value.Any(static c => char.IsWhiteSpace(c) || c == '"'))
        {
            return value;
        }

        var builder = new System.Text.StringBuilder();
        builder.Append('"');
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashes);
                builder.Append(c);
            }

            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private Task OnDiscordLogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Debug,
            _ => LogLevel.Information
        };
        _logger.Log(level, message.Exception, "{Source}: {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }

    private sealed record GameInput(string GameName, string? Password, string? Difficulty);

    private sealed record ReadyResult(string AccountKey, bool Ok, string Message, bool RanReady);

    private sealed record JoinResult(string AccountKey, bool Ok, string Message);

    private sealed class SlashContext
    {
        private SlashContext(
            SocketSlashCommand command,
            string subcommandName,
            IReadOnlyDictionary<string, SocketSlashCommandDataOption> options)
        {
            Command = command;
            SubcommandName = subcommandName;
            _options = options;
        }

        private readonly IReadOnlyDictionary<string, SocketSlashCommandDataOption> _options;

        public SocketSlashCommand Command { get; }
        public string SubcommandName { get; }

        public static SlashContext From(SocketSlashCommand command)
        {
            var subcommand = command.Data.Options.FirstOrDefault();
            if (subcommand is null)
            {
                return new SlashContext(
                    command,
                    "",
                    new Dictionary<string, SocketSlashCommandDataOption>(StringComparer.OrdinalIgnoreCase));
            }

            return new SlashContext(
                command,
                subcommand.Name,
                subcommand.Options.ToDictionary(option => option.Name, StringComparer.OrdinalIgnoreCase));
        }

        public string? GetString(string name)
        {
            return _options.TryGetValue(name, out var option)
                ? option.Value?.ToString()
                : null;
        }

        public string GetRequiredString(string name)
        {
            return GetString(name)
                ?? throw new InvalidOperationException($"{name} is required.");
        }

        public int? GetInt(string name)
        {
            if (!_options.TryGetValue(name, out var option) || option.Value is null)
            {
                return null;
            }

            return Convert.ToInt32(option.Value);
        }

        public int GetRequiredInt(string name)
        {
            return GetInt(name)
                ?? throw new InvalidOperationException($"{name} is required.");
        }

        public bool? GetBool(string name)
        {
            if (!_options.TryGetValue(name, out var option) || option.Value is null)
            {
                return null;
            }

            return Convert.ToBoolean(option.Value);
        }

        public bool GetRequiredBool(string name)
        {
            return GetBool(name)
                ?? throw new InvalidOperationException($"{name} is required.");
        }
    }
}
