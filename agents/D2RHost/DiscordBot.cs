using System.Text.Json;
using AgentCommon;
using Discord;
using Discord.WebSocket;

namespace D2RHost;

public sealed class DiscordBot
{
    private readonly HostConfig _config;
    private readonly AgentRegistry _registry;
    private readonly HyperVOperations _hyperV;
    private readonly AppDb _db;
    private readonly ILogger<DiscordBot> _logger;
    private readonly DiscordSocketClient _client;
    private bool _commandsRegistered;

    public DiscordBot(
        HostConfig config,
        AgentRegistry registry,
        HyperVOperations hyperV,
        AppDb db,
        ILogger<DiscordBot> logger)
    {
        _config = config;
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
                TimeSpan.FromSeconds(240),
                displayName: "ready");
            return;
        }

        if (subcommand == "join-all")
        {
            var game = ResolveGameInput(context);
            await QueueAllCommandsAsync(
                context,
                "menu_join_game",
                (accountKey, account) => BuildMenuArgs(accountKey, account, game, context),
                TimeSpan.FromSeconds(210),
                readyFirstIfNotMenuReady: true);
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
                await RunVmCommandAsync(context, singleAccount, "menu_ready", BuildAccountArgs(singleAccountKey, singleAccount), TimeSpan.FromSeconds(240));
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
        var readyResult = readyFirstIfNotMenuReady
            ? await SendReadyIfNotMenuReadyAsync(account, args)
            : null;

        if (readyResult?.Ok == false)
        {
            await context.Command.ModifyOriginalResponseAsync(
                properties => properties.Content = "This client needed `/d2r ready` before menu automation, but ready failed: "
                    + readyResult.Message);
            return;
        }

        var result = await _registry.SendCommandAsync(account.AgentId, commandName, args, timeout ?? TimeSpan.FromSeconds(60));
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
                + $"{joiners.Length} account(s) will join after creation with {staggerSeconds}s stagger."
                + FormatReadyFirstSuffix(readyFirstCount)
                + FormatOfflineSkipSuffix(offlineEntries),
            ephemeral: true);

        _ = Task.Run(async () =>
        {
            try
            {
                var creatorArgs = BuildMenuArgs(creator.Key, creator.Value, game, context);
                var creatorReadyResult = await SendReadyIfNotMenuReadyAsync(creator.Value, creatorArgs);
                if (creatorReadyResult?.Ok == false)
                {
                    await SendFollowupSafeAsync(
                        context,
                        $"create-game-all stopped: {creator.Key} needed `/d2r ready` first, but ready failed: {creatorReadyResult.Message}");
                    return;
                }

                var createResult = await _registry.SendCommandAsync(
                    creator.Value.AgentId,
                    "menu_create_game",
                    creatorArgs,
                    TimeSpan.FromSeconds(210));

                if (!createResult.Ok)
                {
                    _logger.LogWarning(
                        "create-game-all stopped because creator {AccountKey} failed: {Message}",
                        creator.Key,
                        createResult.Message);
                    await SendFollowupSafeAsync(
                        context,
                        $"create-game-all stopped: {creator.Key} failed to create {game.GameName}: {createResult.Message}");
                    return;
                }

                var joinResults = await Task.WhenAll(joiners.Select((entry, index) =>
                    RunCreateGameAllJoinerAsync(entry, index, staggerSeconds, game, context)));

                await SendFollowupSafeAsync(
                    context,
                    joiners.Length == 0
                        ? $"Create flow completed on {creator.Key} for {game.GameName}. No other accounts were configured to join."
                        : FormatCreateGameAllResult(creator.Key, game.GameName, joinResults));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "create-game-all orchestration failed for {GameName}.", game.GameName);
                await SendFollowupSafeAsync(context, $"create-game-all failed while creating {game.GameName}: {ex.Message}");
            }
        });
    }

    private async Task<JoinResult> RunCreateGameAllJoinerAsync(
        KeyValuePair<string, AccountConfig> entry,
        int index,
        int staggerSeconds,
        GameInput game,
        SlashContext context)
    {
        await Task.Delay(TimeSpan.FromSeconds(index * staggerSeconds));
        try
        {
            var joinArgs = BuildMenuArgs(entry.Key, entry.Value, game, context);
            var readyResult = await SendReadyIfNotMenuReadyAsync(entry.Value, joinArgs);
            if (readyResult?.Ok == false)
            {
                _logger.LogWarning(
                    "Queued join after create-game-all skipped for {AccountKey} because ready failed: {Message}",
                    entry.Key,
                    readyResult.Message);
                return new JoinResult(entry.Key, false, $"ready failed: {readyResult.Message}");
            }

            var joinResult = await _registry.SendCommandAsync(
                entry.Value.AgentId,
                "menu_join_game",
                joinArgs,
                TimeSpan.FromSeconds(210));
            return new JoinResult(entry.Key, joinResult.Ok, joinResult.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Queued join after create-game-all failed for {AccountKey}.", entry.Key);
            return new JoinResult(entry.Key, false, ex.Message);
        }
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
                            return;
                        }
                    }

                    await _registry.SendCommandAsync(
                        entry.Value.AgentId,
                        commandName,
                        args,
                        timeout ?? TimeSpan.FromSeconds(60));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Queued command {Command} failed for {AccountKey}.", commandName, entry.Key);
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
            TimeSpan.FromSeconds(240));
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

        if (!TryGetString(root, "d2rActivityState", out var activityState))
        {
            return true;
        }

        return !string.Equals(activityState, "CharacterScreenIdle", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(activityState, "LobbyOrGame", StringComparison.OrdinalIgnoreCase);
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
        var lastSeen = agent.LastSeenAt?.ToLocalTime().ToString("G") ?? "unknown";
        return $"{name}: online, Battle.net {battleNet}, D2R {d2r}{activity}, seen {lastSeen}";
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
            var subcommand = command.Data.Options.FirstOrDefault()
                ?? throw new InvalidOperationException("Slash command did not include a subcommand.");
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
    }
}
