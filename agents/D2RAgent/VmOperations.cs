using System.Diagnostics;
using System.Text;
using AgentCommon;

namespace D2RAgent;

public sealed class VmOperations
{
    private const string DefaultBattleNetPath = @"C:\Program Files (x86)\Battle.net\Battle.net.exe";
    private const string DefaultBattleNetD2RArgs = "--exec=\"launch OSI\"";

    private readonly VmAgentConfig _config;
    private readonly object _activityLock = new();
    private readonly string[] _restartArgs;
    private D2RActivityState _activityState = D2RActivityState.Unknown;
    private DateTimeOffset? _characterScreenIdleSinceUtc;
    private DateTimeOffset? _lastLobbyOrGameInteractionUtc;
    private string? _lastActivityReason;

    public VmOperations(VmAgentConfig config, string[]? restartArgs = null)
    {
        _config = config;
        _restartArgs = restartArgs ?? [];
    }

    public Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var battleNetRunning = IsBattleNetRunning();
        var d2rRunning = IsD2RRunning();
        if (!d2rRunning)
        {
            ClearD2RActivity();
        }

        var activity = GetActivitySnapshot();

        return Task.FromResult<object>(new
        {
            hostName = Environment.MachineName,
            userName = Environment.UserName,
            battleNetRunning,
            d2rRunning,
            d2rActivityState = activity.State.ToString(),
            characterScreenIdleSinceUtc = activity.CharacterScreenIdleSinceUtc,
            lastLobbyOrGameInteractionUtc = activity.LastLobbyOrGameInteractionUtc,
            lastActivityReason = activity.Reason,
            idleQuitEnabled = _config.IdleQuitEnabled,
            idleQuitMinutes = _config.IdleQuitMinutes,
            timeUtc = DateTimeOffset.UtcNow
        });
    }

    public async Task<CommandResult> HandleCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return request.Command switch
        {
            "status" => CommandResult.Success("Status collected.", await GetStatusAsync(cancellationToken)),
            "launch_battlenet" => LaunchBattleNet(),
            "launch_d2r" => await LaunchD2RAsync(cancellationToken),
            "kill_d2r" => KillD2R(),
            "quit_d2r" => await QuitD2RAsync(cancellationToken),
            "restart_d2r" => await RestartD2RAsync(cancellationToken),
            "screenshot" => await TakeScreenshotAsync(cancellationToken),
            "menu_ready" => await ReadyClientAsync(cancellationToken),
            "menu_lobby" => await GoLobbyAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_play" => await PlayCharacterAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_join_game" => await JoinGameAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_create_game" => await CreateGameAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_join_friend" => await JoinFriendAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_save_exit" => await SaveAndExitAsync(cancellationToken),
            "self_update" => await SelfUpdateAsync(cancellationToken),
            _ => CommandResult.Failure($"Unsupported VM command: {request.Command}")
        };
    }

    private async Task<CommandResult> SelfUpdateAsync(CancellationToken cancellationToken)
    {
        var result = await SelfUpdater.CheckAndStartUpdateAsync(
            SelfUpdateOptions.D2RAgent(_restartArgs),
            requirePrompt: false,
            cancellationToken);
        var data = new
        {
            result.CheckedLatest,
            result.UpdateAvailable,
            result.UpdateStarted,
            result.CurrentVersion,
            result.LatestVersion,
            result.LogPath
        };

        if (!result.Ok)
        {
            return CommandResult.Failure(result.Message, data);
        }

        return CommandResult.Success(
            result.Message,
            data,
            exitAfterResult: result.UpdateStarted);
    }

    private async Task<CommandResult> LaunchD2RAsync(CancellationToken cancellationToken)
    {
        if (IsD2RRunning())
        {
            return CommandResult.Success("D2R is already running.", await GetStatusAsync(cancellationToken));
        }

        var battleNetWasRunning = IsBattleNetRunning();
        await PrepareDesktopForD2RLaunchAsync(battleNetWasRunning, cancellationToken);

        var usedBattleNetExec = false;
        var launchAttempts = 1;

        if (!_config.PreferBattleNetExecLaunch && !string.IsNullOrWhiteSpace(_config.D2RPath))
        {
            var launch = LaunchProcess(_config.D2RPath, _config.D2RArgs);
            if (!launch.Ok)
            {
                return launch;
            }
        }
        else
        {
            usedBattleNetExec = true;
            var launch = LaunchBattleNetD2R();
            if (!launch.Ok)
            {
                return launch;
            }
        }

        if (usedBattleNetExec && !battleNetWasRunning)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(_config.BattleNetExecRetryDelaySeconds, 1)), cancellationToken);
            if (!IsD2RRunning())
            {
                var retry = LaunchBattleNetD2R();
                if (!retry.Ok)
                {
                    return retry;
                }

                launchAttempts++;
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(Math.Max(_config.LaunchGraceSeconds, 1)), cancellationToken);
        var status = await GetStatusAsync(cancellationToken);
        var message = launchAttempts > 1
            ? "Battle.net cold-started; D2R launch command sent twice. Check status for final client state."
            : "Launch command sent. Check status for final client state.";
        return CommandResult.Success(message, status);
    }

    private async Task PrepareDesktopForD2RLaunchAsync(bool battleNetWasRunning, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var input = new WindowsInput();
        input.ShowDesktop();
        await DelayStepAsync(cancellationToken);

        if (!battleNetWasRunning)
        {
            return;
        }

        try
        {
            input.FocusProcess(GetBattleNetProcessNames());
            await DelayStepAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Battle.net may be between windows during startup; launching can still proceed.
        }
    }

    private async Task<CommandResult> RestartD2RAsync(CancellationToken cancellationToken)
    {
        KillD2R();
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        return await LaunchD2RAsync(cancellationToken);
    }

    public async Task RunIdleMonitorAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(_config.IdleQuitCheckSeconds, 10));
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval, cancellationToken);
            try
            {
                await QuitIfCharacterScreenIdleAsync(log ?? (_ => { }), cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                log?.Invoke($"Idle monitor failed: {ex.Message}");
            }
        }
    }

    private async Task QuitIfCharacterScreenIdleAsync(Action<string> log, CancellationToken cancellationToken)
    {
        if (!_config.IdleQuitEnabled || !OperatingSystem.IsWindows())
        {
            return;
        }

        if (!IsD2RRunning())
        {
            ClearD2RActivity();
            return;
        }

        var activity = GetActivitySnapshot();
        if (activity.State != D2RActivityState.CharacterScreenIdle
            || activity.CharacterScreenIdleSinceUtc is not { } idleSince)
        {
            return;
        }

        var timeout = TimeSpan.FromMinutes(Math.Max(_config.IdleQuitMinutes, 1));
        var idleFor = DateTimeOffset.UtcNow - idleSince;
        if (idleFor < timeout)
        {
            return;
        }

        log($"D2R has been idle at the character screen for {idleFor.TotalMinutes:N0} minute(s); sending Alt+F4.");
        var result = await QuitD2RAsync(cancellationToken);
        if (!result.Ok)
        {
            log($"Idle quit failed: {result.Message}");
        }
    }

    private async Task<CommandResult> QuitD2RAsync(CancellationToken cancellationToken)
    {
        if (!IsD2RRunning())
        {
            ClearD2RActivity();
            return CommandResult.Success("D2R is not running.", await GetStatusAsync(cancellationToken));
        }

        var input = new WindowsInput(GetD2RProcessNames());
        if (!input.TryFocusProcess(GetD2RProcessNames()))
        {
            return CommandResult.Failure("D2R is running, but no focusable window was found.");
        }

        await DelayStepAsync(cancellationToken);
        input.PressAltF4();
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        ClearD2RActivity();
        return CommandResult.Success("Alt+F4 sent to D2R.", await GetStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> ReadyClientAsync(CancellationToken cancellationToken)
    {
        var launch = await LaunchD2RAsync(cancellationToken);
        if (!launch.Ok)
        {
            return launch;
        }

        var input = new WindowsInput(GetD2RProcessNames());
        await DelayLongAsync(cancellationToken);

        var d2rStarted = await WaitForD2RProcessStartedAsync(input, cancellationToken);
        if (!d2rStarted)
        {
            return CommandResult.Failure(
                $"D2R was not detected within {Math.Max(_config.D2RStartTimeoutSeconds, 1)}s after repeated launch/Play attempts.",
                await GetStatusAsync(cancellationToken));
        }

        var ready = await PumpStartupSkipInputsUntilCharacterScreenAsync(input, cancellationToken);
        if (!ready.Ready)
        {
            return CommandResult.Failure(
                FormatCharacterScreenReadyFailure(ready),
                await GetStatusAsync(cancellationToken));
        }

        await DelayCharacterScreenSettleAsync(cancellationToken);
        MarkCharacterScreenIdle("Ready flow completed.");
        return CommandResult.Success("D2R ready flow completed.", await GetStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> GoLobbyAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        var input = FocusD2R();
        if (IsAnyLobbyTabReady(input))
        {
            MarkLobbyOrGameInteraction("Lobby was already open.");
            return CommandResult.Success("Lobby was already open.", await GetStatusAsync(cancellationToken));
        }

        await SelectCharacterAsync(input, args.CharacterSlot, cancellationToken);
        var lobbyReady = await ClickLobbyUntilReadyAsync(input, cancellationToken);
        if (!lobbyReady)
        {
            return CommandResult.Failure(
                $"Clicked Lobby, but the lobby tabs were not detected within {Math.Max(_config.Ui.LobbyReadyTimeoutSeconds, 1)}s.",
                await GetStatusAsync(cancellationToken));
        }

        MarkLobbyOrGameInteraction("Opened Lobby.");
        return CommandResult.Success("Lobby command completed.", await GetStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> PlayCharacterAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        var input = FocusD2R();
        await SelectCharacterAsync(input, args.CharacterSlot, cancellationToken);
        input.LeftClick(_config.Ui.CharacterPlayButton);
        await WaitForGameEntryAsync(input, cancellationToken);
        MarkLobbyOrGameInteraction("Clicked Play.");
        return CommandResult.Success("Play character command completed.", await GetStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> JoinGameAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.GameName))
        {
            return CommandResult.Failure("gameName is required for menu_join_game.");
        }

        var lobby = await GoLobbyAsync(args, cancellationToken);
        if (!lobby.Ok)
        {
            return lobby;
        }

        var input = FocusD2R();
        var joinTabReady = await ClickLobbyTabUntilReadyAsync(input, _config.Ui.JoinGameTab, cancellationToken);
        if (!joinTabReady)
        {
            return CommandResult.Failure(
                $"Clicked Join Game tab, but it was not detected within {Math.Max(_config.Ui.LobbyReadyTimeoutSeconds, 1)}s.",
                await GetStatusAsync(cancellationToken));
        }

        await SelectJoinDifficultyAsync(input, args.Difficulty, cancellationToken);
        await FillTextFieldAsync(input, _config.Ui.JoinGameNameField, args.GameName, cancellationToken);
        await FillTextFieldAsync(input, _config.Ui.JoinPasswordField, args.Password ?? "", cancellationToken);
        var joinEntry = await ClickMenuEntryButtonUntilEnteredGameAsync(
            input,
            _config.Ui.JoinGameButton,
            _config.Ui.JoinGameTab,
            () => RestoreJoinGameFormAsync(input, args, cancellationToken),
            cancellationToken);
        if (!joinEntry.Entered)
        {
            return CommandResult.Failure(
                $"Clicked Join Game, but the client did not enter the game within {Math.Max(_config.Ui.GameEntryStartTimeoutSeconds, 1)}s. {joinEntry.Message}",
                await GetStatusAsync(cancellationToken));
        }

        MarkLobbyOrGameInteraction($"Joined game {args.GameName}.");
        var retrySuffix = FormatEntryRecoverySuffix(joinEntry);
        return CommandResult.Success($"Join game flow completed for {args.GameName}.{retrySuffix}", await GetStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> CreateGameAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.GameName))
        {
            return CommandResult.Failure("gameName is required for menu_create_game.");
        }

        var lobby = await GoLobbyAsync(args, cancellationToken);
        if (!lobby.Ok)
        {
            return lobby;
        }

        var input = FocusD2R();
        var createTabReady = await ClickLobbyTabUntilReadyAsync(input, _config.Ui.CreateGameTab, cancellationToken);
        if (!createTabReady)
        {
            return CommandResult.Failure(
                $"Clicked Create Game tab, but it was not detected within {Math.Max(_config.Ui.LobbyReadyTimeoutSeconds, 1)}s.",
                await GetStatusAsync(cancellationToken));
        }

        await FillTextFieldAsync(input, _config.Ui.CreateGameNameField, args.GameName, cancellationToken);
        await FillTextFieldAsync(input, _config.Ui.CreatePasswordField, args.Password ?? "", cancellationToken);
        input.LeftClick(GetCreateDifficultyPoint(args.Difficulty));
        await DelayStepAsync(cancellationToken);
        var createEntry = await ClickMenuEntryButtonUntilEnteredGameAsync(
            input,
            _config.Ui.CreateGameButton,
            _config.Ui.CreateGameTab,
            () => RestoreCreateGameFormAsync(input, args, cancellationToken),
            cancellationToken);
        if (!createEntry.Entered)
        {
            return CommandResult.Failure(
                $"Clicked Create Game, but the client did not enter the game within {Math.Max(_config.Ui.GameEntryStartTimeoutSeconds, 1)}s. {createEntry.Message}",
                await GetStatusAsync(cancellationToken));
        }

        MarkLobbyOrGameInteraction($"Created game {args.GameName}.");
        var retrySuffix = FormatEntryRecoverySuffix(createEntry);
        return CommandResult.Success($"Create game flow completed for {args.GameName}.{retrySuffix}", await GetStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> JoinFriendAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        var lobby = await GoLobbyAsync(args, cancellationToken);
        if (!lobby.Ok)
        {
            return lobby;
        }

        var input = FocusD2R();
        input.LeftClick(_config.Ui.LobbyPartyIcon);
        await DelayLongAsync(cancellationToken);
        input.RightClick(GetFriendRowPoint(args.FriendRow));
        await DelayStepAsync(cancellationToken);
        input.LeftClick(_config.Ui.FriendContextJoinGame);
        await WaitForGameEntryAsync(input, cancellationToken);
        MarkLobbyOrGameInteraction("Joined friend game.");
        return CommandResult.Success("Join friend/follow flow completed.", await GetStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> SaveAndExitAsync(CancellationToken cancellationToken)
    {
        var input = FocusD2R();
        input.PressEscape();
        await DelayStepAsync(cancellationToken);
        input.LeftClick(_config.Ui.SaveAndExitButton);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(_config.Ui.LobbyLoadSeconds, 1)), cancellationToken);
        MarkCharacterScreenIdle("Save and Exit completed.");
        return CommandResult.Success("Save and Exit flow completed.", await GetStatusAsync(cancellationToken));
    }

    private CommandResult KillD2R()
    {
        var result = KillProcesses(GetD2RProcessNames());
        ClearD2RActivity();
        return result;
    }

    private void MarkCharacterScreenIdle(string reason)
    {
        lock (_activityLock)
        {
            _activityState = D2RActivityState.CharacterScreenIdle;
            _characterScreenIdleSinceUtc = DateTimeOffset.UtcNow;
            _lastActivityReason = reason;
        }
    }

    private void MarkLobbyOrGameInteraction(string reason)
    {
        lock (_activityLock)
        {
            _activityState = D2RActivityState.LobbyOrGame;
            _characterScreenIdleSinceUtc = null;
            _lastLobbyOrGameInteractionUtc = DateTimeOffset.UtcNow;
            _lastActivityReason = reason;
        }
    }

    private void ClearD2RActivity()
    {
        lock (_activityLock)
        {
            _activityState = D2RActivityState.Unknown;
            _characterScreenIdleSinceUtc = null;
            _lastActivityReason = null;
        }
    }

    private ActivitySnapshot GetActivitySnapshot()
    {
        lock (_activityLock)
        {
            return new ActivitySnapshot(
                _activityState,
                _characterScreenIdleSinceUtc,
                _lastLobbyOrGameInteractionUtc,
                _lastActivityReason);
        }
    }

    private WindowsInput FocusD2R()
    {
        var processNames = GetD2RProcessNames();
        if (!IsD2RRunning())
        {
            throw new InvalidOperationException($"Process is not running: {FormatProcessNames(processNames)}");
        }

        var input = new WindowsInput(processNames);
        _ = TryPrepareD2RForInput(input);

        return input;
    }

    private bool TryPrepareD2RForInput(WindowsInput input)
    {
        try
        {
            var processNames = GetD2RProcessNames();
            return input.TryFocusProcess(processNames)
                || input.TryClickProcessWindowCenter(processNames);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task DelayCharacterScreenSettleAsync(CancellationToken cancellationToken)
    {
        var settleSeconds = Math.Max(_config.Ui.CharacterScreenSettleSeconds, 0);
        if (settleSeconds == 0)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(settleSeconds), cancellationToken);
    }

    private async Task<ReadyWaitResult> PumpStartupSkipInputsUntilCharacterScreenAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        var skipSeconds = Math.Max(
            _config.Ui.ReadyStartupSkipSeconds,
            Math.Max(_config.Ui.CharacterScreenReadyTimeoutSeconds, 1));
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(skipSeconds);
        var intervalMs = Math.Max(_config.Ui.ReadyStartupSkipIntervalMs, 100);
        var nudges = 0;
        var lastState = ReadyScreenState.Unknown;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = DetectReadyScreenState(input);
            lastState = state;
            if (state == ReadyScreenState.CharacterScreen)
            {
                return new ReadyWaitResult(true, nudges, lastState);
            }

            SendReadySkipKey(input);
            nudges++;
            var remainingMs = Math.Max((deadline - DateTimeOffset.UtcNow).TotalMilliseconds, 0);
            if (remainingMs == 0)
            {
                break;
            }

            await Task.Delay((int)Math.Min(intervalMs, remainingMs), cancellationToken);
        }

        return new ReadyWaitResult(false, nudges, lastState);
    }

    private void SendReadySkipKey(WindowsInput input)
    {
        TryPrimeD2RInput(input);
        input.PressReadySkipKey();
        _ = input.SendWindowReadySkipKey(GetD2RProcessNames());
    }

    private void TryPrimeD2RInput(WindowsInput input)
    {
        try
        {
            _ = TryPrepareD2RForInput(input);
        }
        catch (InvalidOperationException)
        {
            // The blind ready loop should keep nudging even if Windows cannot prove focus yet.
        }
    }

    private async Task<bool> ClickLobbyUntilReadyAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(_config.Ui.LobbyReadyTimeoutSeconds, 1));
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = TryPrepareD2RForInput(input);

            if (IsAnyLobbyTabReady(input))
            {
                return true;
            }

            input.LeftClick(_config.Ui.CharacterLobbyButton);

            await DelayStepAsync(cancellationToken);
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return IsAnyLobbyTabReady(input);
            }
        }
    }

    private async Task<bool> ClickLobbyTabUntilReadyAsync(
        WindowsInput input,
        AgentCommon.UiPoint tab,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(_config.Ui.LobbyReadyTimeoutSeconds, 1));
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = TryPrepareD2RForInput(input);

            if (IsLobbyTabReady(input, tab))
            {
                return true;
            }

            input.LeftClick(tab);
            await DelayStepAsync(cancellationToken);
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return IsLobbyTabReady(input, tab);
            }
        }
    }

    private async Task<GameEntryAttemptResult> ClickMenuEntryButtonUntilEnteredGameAsync(
        WindowsInput input,
        AgentCommon.UiPoint button,
        AgentCommon.UiPoint activeTab,
        Func<Task<bool>> restoreFormAsync,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(_config.Ui.GameEntryStartTimeoutSeconds, 1));
        var deadline = DateTimeOffset.UtcNow + timeout;
        var dialogRetries = 0;
        var connectionRetries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = TryPrepareD2RForInput(input);

            if (IsGameEntryErrorDialogOpen(input))
            {
                dialogRetries++;
                if (!await DismissGameEntryErrorDialogAsync(input, cancellationToken)
                    || !await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "A game-entry error dialog appeared, but the menu form could not be restored.");
                }
            }

            if (IsConnectionInterruptedScreen(input))
            {
                connectionRetries++;
                if (!await WaitForMenuAfterConnectionInterruptedAsync(input, activeTab, cancellationToken)
                    || !await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "Connection was interrupted, but the menu form could not be restored.");
                }
            }

            if (!IsLobbyTabReady(input, activeTab))
            {
                var waitResult = await WaitForGameEntryAsync(input, activeTab, cancellationToken);
                if (waitResult == GameEntryWaitResult.EnteredGame)
                {
                    return new GameEntryAttemptResult(true, dialogRetries, connectionRetries, "Entered game.");
                }

                connectionRetries++;
                if (waitResult == GameEntryWaitResult.ConnectionInterrupted
                    && !await WaitForMenuAfterConnectionInterruptedAsync(input, activeTab, cancellationToken))
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "Connection was interrupted, but the menu tab did not return.");
                }

                if (!await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned from game entry, but the menu form could not be restored.");
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, FormatEntryTimeoutMessage(dialogRetries, connectionRetries));
            }

            input.LeftClick(button);
            await Task.Delay(
                TimeSpan.FromSeconds(Math.Clamp(_config.Ui.GameLoadSeconds, 2, 5)),
                cancellationToken);

            if (IsGameEntryErrorDialogOpen(input))
            {
                dialogRetries++;
                if (!await DismissGameEntryErrorDialogAsync(input, cancellationToken)
                    || !await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "A game-entry error dialog appeared, but the menu form could not be restored.");
                }
            }

            if (IsConnectionInterruptedScreen(input))
            {
                connectionRetries++;
                if (!await WaitForMenuAfterConnectionInterruptedAsync(input, activeTab, cancellationToken)
                    || !await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "Connection was interrupted, but the menu form could not be restored.");
                }
            }

            if (!IsLobbyTabReady(input, activeTab))
            {
                var waitResult = await WaitForGameEntryAsync(input, activeTab, cancellationToken);
                if (waitResult == GameEntryWaitResult.EnteredGame)
                {
                    return new GameEntryAttemptResult(true, dialogRetries, connectionRetries, "Entered game.");
                }

                connectionRetries++;
                if (waitResult == GameEntryWaitResult.ConnectionInterrupted
                    && !await WaitForMenuAfterConnectionInterruptedAsync(input, activeTab, cancellationToken))
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "Connection was interrupted, but the menu tab did not return.");
                }

                if (!await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned from game entry, but the menu form could not be restored.");
                }
            }
        }
    }

    private async Task<bool> DismissGameEntryErrorDialogAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            _ = TryPrepareD2RForInput(input);
            input.LeftClick(_config.Ui.GameEntryErrorDialogOkButton);
            await DelayLongAsync(cancellationToken);

            if (!IsGameEntryErrorDialogOpen(input))
            {
                return true;
            }
        }

        return !IsGameEntryErrorDialogOpen(input);
    }

    private async Task<bool> WaitForMenuAfterConnectionInterruptedAsync(
        WindowsInput input,
        AgentCommon.UiPoint activeTab,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(_config.Ui.GameEntryStartTimeoutSeconds, 1));
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = TryPrepareD2RForInput(input);
            if (!IsConnectionInterruptedScreen(input) && IsLobbyTabReady(input, activeTab))
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await DelayLongAsync(cancellationToken);
        }
    }

    private async Task<bool> RestoreJoinGameFormAsync(
        WindowsInput input,
        MenuCommandArgs args,
        CancellationToken cancellationToken)
    {
        var joinTabReady = await ClickLobbyTabUntilReadyAsync(input, _config.Ui.JoinGameTab, cancellationToken);
        if (!joinTabReady)
        {
            return false;
        }

        await SelectJoinDifficultyAsync(input, args.Difficulty, cancellationToken);
        await FillTextFieldAsync(input, _config.Ui.JoinGameNameField, args.GameName ?? "", cancellationToken);
        await FillTextFieldAsync(input, _config.Ui.JoinPasswordField, args.Password ?? "", cancellationToken);
        return true;
    }

    private async Task<bool> RestoreCreateGameFormAsync(
        WindowsInput input,
        MenuCommandArgs args,
        CancellationToken cancellationToken)
    {
        var createTabReady = await ClickLobbyTabUntilReadyAsync(input, _config.Ui.CreateGameTab, cancellationToken);
        if (!createTabReady)
        {
            return false;
        }

        await FillTextFieldAsync(input, _config.Ui.CreateGameNameField, args.GameName ?? "", cancellationToken);
        await FillTextFieldAsync(input, _config.Ui.CreatePasswordField, args.Password ?? "", cancellationToken);
        input.LeftClick(GetCreateDifficultyPoint(args.Difficulty));
        await DelayStepAsync(cancellationToken);
        return true;
    }

    private static string FormatEntryRecoverySuffix(GameEntryAttemptResult result)
    {
        var parts = new List<string>();
        if (result.DialogRetries > 0)
        {
            parts.Add($"{result.DialogRetries} error dialog(s)");
        }

        if (result.ConnectionRetries > 0)
        {
            parts.Add($"{result.ConnectionRetries} connection interruption(s)");
        }

        return parts.Count == 0
            ? ""
            : $" Recovered from {string.Join(" and ", parts)}.";
    }

    private static string FormatEntryTimeoutMessage(int dialogRetries, int connectionRetries)
    {
        if (dialogRetries == 0 && connectionRetries == 0)
        {
            return "No game-entry error state was detected.";
        }

        var parts = new List<string>();
        if (dialogRetries > 0)
        {
            parts.Add($"{dialogRetries} error dialog(s)");
        }

        if (connectionRetries > 0)
        {
            parts.Add($"{connectionRetries} connection interruption(s)");
        }

        return $"Recovered from {string.Join(" and ", parts)}, but the menu tab stayed visible.";
    }

    private bool IsCharacterScreenReady(WindowsInput input)
    {
        return DetectReadyScreenState(input) == ReadyScreenState.CharacterScreen;
    }

    private ReadyScreenState DetectReadyScreenState(WindowsInput input)
    {
        if (IsDiabloSplashScreen(input))
        {
            return ReadyScreenState.DiabloSplash;
        }

        var play = input.SampleRegion(_config.Ui.CharacterPlayButton, widthRatio: 0.13, heightRatio: 0.055);
        var lobby = input.SampleRegion(_config.Ui.CharacterLobbyButton, widthRatio: 0.13, heightRatio: 0.055);
        return IsCharacterButtonRegion(play) && IsCharacterButtonRegion(lobby)
            ? ReadyScreenState.CharacterScreen
            : ReadyScreenState.Unknown;
    }

    private string FormatCharacterScreenReadyFailure(ReadyWaitResult result)
    {
        return $"D2R is running, but the character screen Play/Lobby buttons were not detected within {Math.Max(_config.Ui.CharacterScreenReadyTimeoutSeconds, 1)}s. Last detected ready state: {result.LastState}; ready input bursts sent: {result.Nudges}.";
    }

    private bool IsDiabloSplashScreen(WindowsInput input)
    {
        var logo = input.SampleRegion(new AgentCommon.UiPoint(0.500, 0.290), widthRatio: 0.45, heightRatio: 0.22);
        var prompt = input.SampleRegion(new AgentCommon.UiPoint(0.500, 0.600), widthRatio: 0.32, heightRatio: 0.055);
        return logo.OrangeRatio > 0.05
            && prompt.OrangeRatio > 0.04
            && logo.DarkRatio > 0.45
            && prompt.DarkRatio > 0.45;
    }

    private bool IsGameEntryErrorDialogOpen(WindowsInput input)
    {
        var okButton = input.SampleRegion(_config.Ui.GameEntryErrorDialogOkButton, widthRatio: 0.14, heightRatio: 0.050);
        var topBorder = input.SampleRegion(new AgentCommon.UiPoint(0.500, 0.381), widthRatio: 0.32, heightRatio: 0.025);
        var body = input.SampleRegion(new AgentCommon.UiPoint(0.500, 0.465), widthRatio: 0.32, heightRatio: 0.20);
        return okButton.AverageLuminance > 45
            && okButton.LuminanceStdDev > 25
            && okButton.GreyRatio > 0.35
            && okButton.DarkRatio < 0.60
            && topBorder.AverageLuminance > 28
            && topBorder.GreyRatio > 0.25
            && topBorder.DarkRatio < 0.75
            && body.AverageLuminance < 40
            && body.DarkRatio > 0.70;
    }

    private bool IsConnectionInterruptedScreen(WindowsInput input)
    {
        var screen = input.SampleRegion(new AgentCommon.UiPoint(0.500, 0.500), widthRatio: 0.80, heightRatio: 0.60);
        var text = input.SampleRegion(new AgentCommon.UiPoint(0.500, 0.502), widthRatio: 0.55, heightRatio: 0.08);
        return screen.AverageLuminance < 5
            && screen.DarkRatio > 0.97
            && text.AverageLuminance > 3
            && text.LuminanceStdDev > 15
            && text.GreyRatio > 0.02
            && text.DarkRatio > 0.85;
    }

    private bool IsAnyLobbyTabReady(WindowsInput input)
    {
        return IsLobbyTabReady(input, _config.Ui.CreateGameTab)
            || IsLobbyTabReady(input, _config.Ui.JoinGameTab);
    }

    private bool IsLobbyTabReady(WindowsInput input, AgentCommon.UiPoint tab)
    {
        var stats = input.SampleRegion(tab, widthRatio: 0.10, heightRatio: 0.045);
        return stats.AverageLuminance > 28
            && stats.AverageLuminance < 90
            && stats.GreyRatio > 0.25
            && stats.DarkRatio > 0.20
            && stats.DarkRatio < 0.80
            && IsLobbyEntryButtonReady(input);
    }

    private bool IsLobbyEntryButtonReady(WindowsInput input)
    {
        var stats = input.SampleRegion(_config.Ui.CreateGameButton, widthRatio: 0.16, heightRatio: 0.055);
        return stats.AverageLuminance > 30
            && stats.AverageLuminance < 90
            && stats.GreyRatio > 0.30
            && stats.DarkRatio > 0.25
            && stats.DarkRatio < 0.70;
    }

    private static bool IsCharacterButtonRegion(ScreenRegionStats stats)
    {
        return stats.AverageLuminance > 45
            && stats.GreyRatio > 0.35
            && stats.DarkRatio < 0.55;
    }

    private async Task SelectCharacterAsync(
        WindowsInput input,
        int? characterSlot,
        CancellationToken cancellationToken)
    {
        input.LeftClick(GetCharacterSlotPoint(characterSlot));
        await DelayStepAsync(cancellationToken);
    }

    private async Task FillTextFieldAsync(
        WindowsInput input,
        AgentCommon.UiPoint point,
        string value,
        CancellationToken cancellationToken)
    {
        input.LeftClick(point);
        await DelayStepAsync(cancellationToken);
        input.LeftClick(point);
        await DelayStepAsync(cancellationToken);
        input.SelectAll();
        await DelayStepAsync(cancellationToken);
        input.TypeText(value);
        await DelayStepAsync(cancellationToken);
    }

    private async Task SelectJoinDifficultyAsync(
        WindowsInput input,
        string? difficulty,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(difficulty))
        {
            return;
        }

        input.LeftClick(_config.Ui.JoinDifficultyDropdown);
        await DelayStepAsync(cancellationToken);
        input.LeftClick(GetJoinDifficultyPoint(difficulty));
        await DelayStepAsync(cancellationToken);
    }

    private async Task WaitForGameEntryAsync(WindowsInput input, CancellationToken cancellationToken)
    {
        _ = await WaitForGameEntryAsync(input, returnTab: null, cancellationToken);
    }

    private async Task<GameEntryWaitResult> WaitForGameEntryAsync(
        WindowsInput input,
        AgentCommon.UiPoint? returnTab,
        CancellationToken cancellationToken)
    {
        var delaySeconds = _config.Ui.ToggleLegacyGraphicsAfterEnteringGame
            ? Math.Max(_config.Ui.LegacyGraphicsToggleDelaySeconds, 1)
            : Math.Max(_config.Ui.GameLoadSeconds, 1);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(delaySeconds);
        var sawConnectionInterrupted = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = TryPrepareD2RForInput(input);

            if (IsConnectionInterruptedScreen(input))
            {
                sawConnectionInterrupted = true;
            }
            else if (returnTab is not null && IsLobbyTabReady(input, returnTab))
            {
                return sawConnectionInterrupted
                    ? GameEntryWaitResult.ConnectionInterrupted
                    : GameEntryWaitResult.ReturnedToMenu;
            }

            var remainingMs = Math.Max((deadline - DateTimeOffset.UtcNow).TotalMilliseconds, 0);
            if (remainingMs == 0)
            {
                break;
            }

            await Task.Delay((int)Math.Min(1000, remainingMs), cancellationToken);
        }

        if (sawConnectionInterrupted)
        {
            return GameEntryWaitResult.ConnectionInterrupted;
        }

        if (!_config.Ui.ToggleLegacyGraphicsAfterEnteringGame)
        {
            return GameEntryWaitResult.EnteredGame;
        }

        _ = TryPrepareD2RForInput(input);
        await DelayStepAsync(cancellationToken);
        input.PressLegacyGraphicsToggle();
        await DelayStepAsync(cancellationToken);
        return GameEntryWaitResult.EnteredGame;
    }

    private AgentCommon.UiPoint GetCharacterSlotPoint(int? characterSlot)
    {
        var slot = characterSlot ?? _config.Ui.DefaultCharacterSlot;
        if (slot < 1 || slot > _config.Ui.CharacterSlots.Length)
        {
            throw new InvalidOperationException($"Character slot must be between 1 and {_config.Ui.CharacterSlots.Length}.");
        }

        return _config.Ui.CharacterSlots[slot - 1];
    }

    private AgentCommon.UiPoint GetFriendRowPoint(int? friendRow)
    {
        var row = friendRow ?? _config.Ui.DefaultFriendRow;
        if (row < 1)
        {
            throw new InvalidOperationException("friendRow must be 1 or greater.");
        }

        return new AgentCommon.UiPoint(
            _config.Ui.FriendRowStart.X,
            _config.Ui.FriendRowStart.Y + ((row - 1) * _config.Ui.FriendRowHeight));
    }

    private AgentCommon.UiPoint GetCreateDifficultyPoint(string? difficulty)
    {
        return NormalizeDifficulty(difficulty) switch
        {
            "nightmare" => _config.Ui.CreateNightmareButton,
            "hell" => _config.Ui.CreateHellButton,
            _ => _config.Ui.CreateNormalButton
        };
    }

    private AgentCommon.UiPoint GetJoinDifficultyPoint(string? difficulty)
    {
        return NormalizeDifficulty(difficulty) switch
        {
            "nightmare" => _config.Ui.JoinDifficultyNightmareOption,
            "hell" => _config.Ui.JoinDifficultyHellOption,
            _ => _config.Ui.JoinDifficultyNormalOption
        };
    }

    private static string NormalizeDifficulty(string? difficulty)
    {
        return string.IsNullOrWhiteSpace(difficulty)
            ? "normal"
            : difficulty.Trim().ToLowerInvariant();
    }

    private Task DelayStepAsync(CancellationToken cancellationToken)
    {
        return Task.Delay(Math.Max(_config.Ui.StepDelayMs, 50), cancellationToken);
    }

    private Task DelayLongAsync(CancellationToken cancellationToken)
    {
        return Task.Delay(Math.Max(_config.Ui.LongDelayMs, 100), cancellationToken);
    }

    private Task DelayReadyNudgeAsync(CancellationToken cancellationToken)
    {
        var minDelayMs = Math.Max(_config.Ui.ReadyNudgeMinDelayMs, 250);
        var maxDelayMs = Math.Max(_config.Ui.ReadyNudgeMaxDelayMs, minDelayMs);
        var exclusiveMax = maxDelayMs == int.MaxValue ? int.MaxValue : maxDelayMs + 1;
        var delayMs = minDelayMs == maxDelayMs
            ? minDelayMs
            : Random.Shared.Next(minDelayMs, exclusiveMax);
        return Task.Delay(delayMs, cancellationToken);
    }

    private async Task<bool> TryFocusProcessUntilAsync(
        WindowsInput input,
        string processName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (input.TryFocusProcess(processName))
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // The process may appear before its main window is ready.
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await DelayStepAsync(cancellationToken);
        }
    }

    private async Task<bool> WaitForD2RProcessStartedAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(_config.D2RStartTimeoutSeconds, 1));
        var deadline = DateTimeOffset.UtcNow + timeout;
        var launchRetryDelay = TimeSpan.FromSeconds(Math.Max(_config.BattleNetExecRetryDelaySeconds, 1));
        var nextLaunchRetryAt = DateTimeOffset.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsD2RRunning())
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            if (DateTimeOffset.UtcNow >= nextLaunchRetryAt)
            {
                _ = TrySendD2RLaunchCommand();
                nextLaunchRetryAt = DateTimeOffset.UtcNow + launchRetryDelay;
            }

            _ = TryClickBattleNetPlay(input);
            await DelayReadyNudgeAsync(cancellationToken);
        }
    }

    private CommandResult TrySendD2RLaunchCommand()
    {
        if (!_config.PreferBattleNetExecLaunch && !string.IsNullOrWhiteSpace(_config.D2RPath))
        {
            return LaunchProcess(_config.D2RPath, _config.D2RArgs);
        }

        return LaunchBattleNetD2R();
    }

    private bool TryClickBattleNetPlay(WindowsInput input)
    {
        if (!_config.Ui.ClickBattleNetPlayWhenNeeded
            || !IsBattleNetRunning())
        {
            return false;
        }

        try
        {
            if (!input.TryFocusProcess(GetBattleNetProcessNames()))
            {
                return false;
            }

            if (TryDismissBattleNetWhatsNewPopup(input))
            {
                return false;
            }

            if (!IsBattleNetPlayButtonReady(input))
            {
                return false;
            }

            input.LeftClick(_config.Ui.BattleNetPlayButton, GetBattleNetProcessNames());
            return true;
        }
        catch (InvalidOperationException)
        {
            // Battle.net may be running before its main window can receive input.
            return false;
        }
    }

    private bool TryDismissBattleNetWhatsNewPopup(WindowsInput input)
    {
        if (!_config.Ui.DismissBattleNetWhatsNewWhenNeeded)
        {
            return false;
        }

        if (!IsBattleNetWhatsNewPopupOpen(input))
        {
            return false;
        }

        input.LeftClick(_config.Ui.BattleNetWhatsNewCloseButton, GetBattleNetProcessNames());
        return true;
    }

    private bool IsBattleNetWhatsNewPopupOpen(WindowsInput input)
    {
        var title = input.SampleRegion(
            _config.Ui.BattleNetWhatsNewTitle,
            widthRatio: 0.16,
            heightRatio: 0.06,
            coordinateProcessNames: GetBattleNetProcessNames());

        return title.AverageLuminance > 30
            && title.LuminanceStdDev > 40
            && title.BrightRatio > 0.04
            && title.DarkRatio > 0.75;
    }

    private bool IsBattleNetPlayButtonReady(WindowsInput input)
    {
        var stats = input.SampleRegion(
            _config.Ui.BattleNetPlayButton,
            widthRatio: 0.16,
            heightRatio: 0.06,
            coordinateProcessNames: GetBattleNetProcessNames());
        return stats.BlueRatio > 0.20
            && stats.AverageLuminance > 40
            && stats.DarkRatio < 0.70;
    }

    private CommandResult LaunchBattleNet()
    {
        if (IsBattleNetRunning())
        {
            return CommandResult.Success("Battle.net is already running.");
        }

        if (string.IsNullOrWhiteSpace(_config.BattleNetPath))
        {
            return CommandResult.Failure("battleNetPath is not configured.");
        }

        return LaunchProcess(_config.BattleNetPath, _config.BattleNetArgs);
    }

    private CommandResult LaunchBattleNetD2R()
    {
        var path = ResolveBattleNetExecutablePath();
        var args = string.IsNullOrWhiteSpace(_config.BattleNetArgs)
            ? DefaultBattleNetD2RArgs
            : _config.BattleNetArgs;

        return LaunchProcess(path, args);
    }

    private string ResolveBattleNetExecutablePath()
    {
        if (string.IsNullOrWhiteSpace(_config.BattleNetPath))
        {
            return DefaultBattleNetPath;
        }

        var fileName = Path.GetFileName(_config.BattleNetPath);
        if (fileName.Equals("Battle.net Launcher.exe", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(_config.BattleNetPath) is { } directory)
        {
            var battleNetExe = Path.Combine(directory, "Battle.net.exe");
            if (File.Exists(battleNetExe))
            {
                return battleNetExe;
            }
        }

        return _config.BattleNetPath;
    }

    private CommandResult LaunchProcess(string path, string? args)
    {
        var isProtocolLaunch = Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile;
        if (!isProtocolLaunch && !File.Exists(path))
        {
            return CommandResult.Failure($"Launch target was not found: {path}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            Arguments = args ?? "",
            WorkingDirectory = !isProtocolLaunch && !string.IsNullOrWhiteSpace(_config.WorkingDirectory)
                ? _config.WorkingDirectory
                : !isProtocolLaunch
                    ? Path.GetDirectoryName(path) ?? Environment.CurrentDirectory
                    : Environment.CurrentDirectory,
            UseShellExecute = true
        };

        try
        {
            Process.Start(startInfo);
            return CommandResult.Success($"Started {(isProtocolLaunch ? path : Path.GetFileName(path))}.");
        }
        catch (Exception ex)
        {
            return CommandResult.Failure($"Failed to start {path}: {ex.Message}");
        }
    }

    private bool IsBattleNetRunning()
    {
        return IsAnyProcessRunning(GetBattleNetProcessNames());
    }

    private bool IsD2RRunning()
    {
        return IsAnyProcessRunning(GetD2RProcessNames());
    }

    private string[] GetBattleNetProcessNames()
    {
        return GetConfiguredProcessNames(_config.BattleNetProcessName, _config.BattleNetProcessNames);
    }

    private string[] GetD2RProcessNames()
    {
        return GetConfiguredProcessNames(_config.D2RProcessName, _config.D2RProcessNames);
    }

    private static string[] GetConfiguredProcessNames(string primaryProcessName, IEnumerable<string>? additionalProcessNames)
    {
        return new[] { primaryProcessName }
            .Concat(additionalProcessNames ?? [])
            .Where(processName => !string.IsNullOrWhiteSpace(processName))
            .Select(processName => Path.GetFileNameWithoutExtension(processName) ?? "")
            .Where(processName => !string.IsNullOrWhiteSpace(processName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static CommandResult KillProcesses(IEnumerable<string> processNames)
    {
        var names = GetConfiguredProcessNames("", processNames);
        var processes = names
            .SelectMany(Process.GetProcessesByName)
            .GroupBy(process => process.Id)
            .Select(group => group.First())
            .ToArray();
        if (processes.Length == 0)
        {
            return CommandResult.Success($"{FormatProcessNames(names)} was not running.");
        }

        foreach (var process in processes)
        {
            using (process)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }

        return CommandResult.Success($"Killed {processes.Length} {FormatProcessNames(names)} process(es).");
    }

    private static bool IsAnyProcessRunning(IEnumerable<string> processNames)
    {
        return GetConfiguredProcessNames("", processNames)
            .Any(processName => Process.GetProcessesByName(processName).Length > 0);
    }

    private static string FormatProcessNames(IEnumerable<string> processNames)
    {
        var names = GetConfiguredProcessNames("", processNames);
        return names.Length == 0 ? "(none)" : string.Join("/", names);
    }

    private async Task<CommandResult> TakeScreenshotAsync(CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            Add-Type -AssemblyName System.Windows.Forms
            Add-Type -AssemblyName System.Drawing
            $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
            $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
            $stream = New-Object System.IO.MemoryStream
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            [Convert]::ToBase64String($stream.ToArray())
            $graphics.Dispose()
            $bitmap.Dispose()
            $stream.Dispose()
            """;

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _config.PowerShellPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(script);

        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                output.AppendLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                error.AppendLine(eventArgs.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(_config.ScreenshotTimeoutSeconds, 5)));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return CommandResult.Failure($"Screenshot timed out after {_config.ScreenshotTimeoutSeconds}s.");
        }

        if (process.ExitCode != 0)
        {
            var stderr = error.ToString().Trim();
            return CommandResult.Failure(string.IsNullOrWhiteSpace(stderr) ? "Screenshot failed." : stderr);
        }

        var base64 = output.ToString().Trim();
        if (string.IsNullOrWhiteSpace(base64))
        {
            return CommandResult.Failure("Screenshot command did not return image data.");
        }

        return CommandResult.Success(
            "Screenshot captured.",
            new
            {
                mimeType = "image/png",
                base64
            });
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup after timeout.
        }
    }

    private enum D2RActivityState
    {
        Unknown,
        CharacterScreenIdle,
        LobbyOrGame
    }

    private enum ReadyScreenState
    {
        Unknown,
        DiabloSplash,
        CharacterScreen
    }

    private enum GameEntryWaitResult
    {
        EnteredGame,
        ConnectionInterrupted,
        ReturnedToMenu
    }

    private sealed record GameEntryAttemptResult(
        bool Entered,
        int DialogRetries,
        int ConnectionRetries,
        string Message);

    private sealed record ReadyWaitResult(
        bool Ready,
        int Nudges,
        ReadyScreenState LastState);

    private sealed record ActivitySnapshot(
        D2RActivityState State,
        DateTimeOffset? CharacterScreenIdleSinceUtc,
        DateTimeOffset? LastLobbyOrGameInteractionUtc,
        string? Reason);
}
