using System.Diagnostics;
using System.Text.Json;
using AgentCommon;
using Discord;
using Discord.WebSocket;

namespace D2RHost;

public sealed class DiscordBot
{
    private static readonly TimeSpan ReadyCommandTimeout = TimeSpan.FromSeconds(150);

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
    private bool _commandsRegistered;

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
            var accountKey = context.GetString("account");
            await context.Command.RespondAsync(
                accountKey is null ? FormatAllAccountStatuses() : FormatAccountStatus(accountKey),
                ephemeral: true);
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
            var game = ResolveGameInput(context);
            await QueueJoinAllAsync(context, game);
            return;
        }

        if (subcommand == "create-game-all")
        {
            await QueueCreateGameAllAsync(context, ResolveGameInput(context));
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

        if (subcommand is "save-exit-all" or "leave-all")
        {
            await QueueAllCommandsAsync(
                context,
                "menu_save_exit",
                (accountKey, account) => BuildAccountArgs(accountKey, account),
                TimeSpan.FromSeconds(90));
            return;
        }

        if (subcommand is "quit-all" or "close-all")
        {
            await QueueAllCommandsAsync(
                context,
                "quit_d2r",
                (accountKey, account) => BuildAccountArgs(accountKey, account),
                TimeSpan.FromSeconds(30));
            return;
        }

        var (singleAccountKey, singleAccount) = RequireAccount(context.GetRequiredString("account"));

        switch (subcommand)
        {
            case "start":
                await RunVmCommandAsync(context, singleAccount, "launch_d2r", BuildAccountArgs(singleAccountKey, singleAccount));
                return;
            case "stop":
                await RunVmCommandAsync(context, singleAccount, "kill_d2r", BuildAccountArgs(singleAccountKey, singleAccount));
                return;
            case "quit":
            case "close":
                await RunVmCommandAsync(context, singleAccount, "quit_d2r", BuildAccountArgs(singleAccountKey, singleAccount), TimeSpan.FromSeconds(30));
                return;
            case "restart-client":
                await RunVmCommandAsync(context, singleAccount, "restart_d2r", BuildAccountArgs(singleAccountKey, singleAccount));
                return;
            case "ready":
                await RunVmCommandAsync(context, singleAccount, "menu_ready", BuildAccountArgs(singleAccountKey, singleAccount), ReadyCommandTimeout);
                return;
            case "lobby":
                await RunVmCommandAsync(context, singleAccount, "menu_lobby", BuildMenuArgs(singleAccountKey, singleAccount, null, context), TimeSpan.FromSeconds(150), readyFirstIfNotMenuReady: true);
                return;
            case "play":
                await RunVmCommandAsync(context, singleAccount, "menu_play", BuildMenuArgs(singleAccountKey, singleAccount, null, context), TimeSpan.FromSeconds(150), readyFirstIfNotMenuReady: true);
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
            case "leave":
                await RunVmCommandAsync(context, singleAccount, "menu_save_exit", BuildAccountArgs(singleAccountKey, singleAccount), TimeSpan.FromSeconds(90));
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

    private async Task QueueCreateGameAllAsync(SlashContext context, GameInput game)
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
            $"Queued create-game-all. {creator.Key} will create; {joiners.Length} account(s) will join.");

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

                var prepareJoinerTasks = joiners.ToDictionary(
                    entry => entry.Key,
                    entry => RunCreateGameAllPrepareJoinerAsync(entry, readyTasks[entry.Key], argsByAccount[entry.Key]),
                    StringComparer.OrdinalIgnoreCase);
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
        });
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
            var readyResult = await _registry.SendCommandAsync(
                entry.Value.AgentId,
                "menu_ready",
                args,
                ReadyCommandTimeout);
            return new ReadyResult(entry.Key, readyResult.Ok, readyResult.Message, RanReady: true);
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
                TimeSpan.FromSeconds(210));
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

    private async Task QueueJoinAllAsync(SlashContext context, GameInput game)
    {
        var (entries, offlineEntries) = GetAccountEntriesByConnectivity();
        if (entries.Length == 0)
        {
            await context.Command.RespondAsync(
                "No online accounts are available for join-all." + FormatOfflineSkipSuffix(offlineEntries),
                ephemeral: true);
            return;
        }

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
            $"Queued join-all for {entries.Length} account(s).");

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
                TimeSpan.FromSeconds(210));

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

    private async Task StartGameSessionAsync(string gameName, int expected, string status)
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
            _activeSessionMessage = await channel.SendMessageAsync(FormatGameSessionMessage(status, detail: null));
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

            await _activeSessionMessage.ModifyAsync(properties =>
                properties.Content = FormatGameSessionMessage(status, detail));
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
            await _activeSessionMessage.ModifyAsync(properties =>
                properties.Content = FormatGameSessionMessage(status, detail: null));
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
            await _activeSessionMessage.ModifyAsync(properties =>
                properties.Content = FormatGameSessionMessage(status, detail));
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

    private string FormatGameSessionMessage(string status, string? detail)
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

        if (!string.IsNullOrWhiteSpace(detail))
        {
            lines.Add(detail);
        }

        return string.Join("\n", lines);
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

        foreach (var (entry, index) in entries.Select((entry, index) => (entry, index)))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(index * staggerSeconds));
                try
                {
                    var args = argsFactory(entry.Key, entry.Value);
                    if (readyFirstIfNotMenuReady)
                    {
                        var readyResult = await SendReadyIfNotMenuReadyAsync(entry.Value, args);
                        if (readyResult?.Ok == false)
                        {
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
                        await SendFollowupSafeAsync(
                            context,
                            $"{label} failed for {entry.Key}: {result.Message}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Queued command {Command} failed for {AccountKey}.", commandName, entry.Key);
                    await SendFollowupSafeAsync(
                        context,
                        $"{label} failed for {entry.Key}: {FormatExceptionWithAccountStatus(ex, entry.Key, entry.Value)}");
                }
            });
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

        return await _registry.SendCommandAsync(
            account.AgentId,
            "menu_ready",
            args,
            ReadyCommandTimeout);
    }

    private bool ShouldRunReadyFirst(AccountConfig account)
    {
        var agent = _registry.GetAgent(account.AgentId);
        if (agent?.Connected != true)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(agent.LastStatusJson))
        {
            return true;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(agent.LastStatusJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Could not parse last status JSON for {AgentId}.", account.AgentId);
            return true;
        }

        if (!TryGetBoolean(root, "d2rRunning", out var d2rRunning))
        {
            return true;
        }

        if (!d2rRunning)
        {
            return true;
        }

        return !d2rRunning;
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
        return string.Join("\n", _config.Accounts.Select(pair => FormatAccountStatusLine(pair.Key, pair.Value)));
    }

    private string FormatAccountStatus(string accountKey)
    {
        var (_, account) = RequireAccount(accountKey);
        return FormatAccountStatusLine(accountKey, account);
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
        var name = string.IsNullOrWhiteSpace(account.DisplayName)
            ? accountKey
            : $"{accountKey} ({account.DisplayName})";
        var agent = _registry.GetAgent(account.AgentId);
        if (agent?.Connected != true)
        {
            return $"{name}: offline";
        }

        var status = ParseStatus(agent.LastStatusJson);
        var battleNet = FormatRunning(status.TryGetValue("battleNetRunning", out var battleNetRunning) ? battleNetRunning : null);
        var d2r = FormatRunning(status.TryGetValue("d2rRunning", out var d2rRunning) ? d2rRunning : null);
        var activity = TryReadStatusString(agent.LastStatusJson, "d2rActivityState", out var activityState)
            ? $", state {activityState}"
            : "";
        var input = TryReadD2RInputSummary(agent.LastStatusJson, out var inputSummary)
            ? $", input {inputSummary}"
            : "";
        var lastInput = TryReadLastInputActionSummary(agent.LastStatusJson, out var lastInputSummary)
            ? $", lastInput {lastInputSummary}"
            : "";
        var version = string.IsNullOrWhiteSpace(agent.Version)
            ? ""
            : $", version {agent.Version}";
        var lastSeen = agent.LastSeenAt?.ToLocalTime().ToString("G") ?? "unknown";
        return $"{name}: online{version}, Battle.net {battleNet}, D2R {d2r}{activity}{input}{lastInput}, seen {lastSeen}";
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

            value = $"interactive={interactive}, session={session}, target={targetProcess}, title={targetTitle}, window={window}, foreground={foreground}, fg={foregroundProcess}, screen={screen}, windowRect={windowRect}, client={clientRect}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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
