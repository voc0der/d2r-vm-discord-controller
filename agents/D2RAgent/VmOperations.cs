using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using AgentCommon;

namespace D2RAgent;

public sealed class VmOperations
{
    private const string DefaultBattleNetPath = @"C:\Program Files (x86)\Battle.net\Battle.net.exe";
    private const string DefaultBattleNetD2RArgs = "--exec=\"launch OSI\"";
    private const int MaxD2RStartTimeoutSeconds = 40;
    private const int MaxReadyStartupSkipSeconds = 45;
    private const int MaxCharacterScreenReconnectSeconds = 45;
    // Per-VM config can be stale on already-provisioned satellites (it predates this
    // floor and won't pick up a new default just because the code changed). These
    // are hard floors applied on top of the configured/clamped values rather than
    // replacements, so a misconfigured (too-low) per-VM value still gets a sane
    // minimum. They must stay below the Max*Seconds clamps above or they become the
    // effective ceiling for every run, including ones where detection is simply
    // wrong rather than slow.
    private const int D2RProcessStartFallbackTimeoutSeconds = 20;
    private const int MenuReadyFallbackTimeoutSeconds = 30;
    private const int MaxJoinPrepareSeconds = 25;
    private const int ReadyStartupDetectionIntervalMs = 1000;
    private const int ReadyStartupSampleGrid = 5;
    private const int MenuSampleGrid = 9;
    private const int FastMenuDelayMs = 150;
    private const int EntryPollIntervalMs = 200;
    private const int LobbyPollIntervalMs = 250;

    private readonly VmAgentConfig _config;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _activityLock = new();
    private readonly string[] _restartArgs;
    private D2RActivityState _activityState = D2RActivityState.Unknown;
    private DateTimeOffset? _characterScreenIdleSinceUtc;
    private DateTimeOffset? _lastLobbyOrGameInteractionUtc;
    private DateTimeOffset? _lastObservedD2RStartUtc;
    private string? _lastActivityReason;
    private LastInputActionSnapshot? _lastInputAction;

    public VmOperations(VmAgentConfig config, string[]? restartArgs = null)
    {
        _config = config;
        _restartArgs = restartArgs ?? [];
    }

    public Task<object> GetStatusAsync(CancellationToken cancellationToken)
    {
        // CollectStatusAsync only reads process/window state - it never sends input -
        // so it must not wait on _commandGate. A long-running menu_ready/menu_create_game
        // command can legitimately hold that gate for minutes; gating status on it meant
        // every status check during that window served a snapshot frozen from before the
        // command started, making live detection look broken for as long as the command
        // ran (sometimes ~10 minutes) even though the agent was tracking reality fine the
        // moment the gate freed up. Read live, every time.
        //
        // CollectStatusAsync itself is synchronous - it has no await in it, it just returns
        // Task.FromResult at the end. Calling it directly therefore blocks the calling
        // thread for the full duration of its Win32 detection calls before any Task even
        // exists to race against a timeout, which made the heartbeat's bounded-timeout
        // race (CollectHeartbeatStatusAsync) a no-op for exactly the case it was meant to
        // catch: a wedged Win32 call. Run it on the thread pool so callers actually get a
        // pending Task back immediately and can bound/abandon it.
        return Task.Run(() => CollectStatusAsync(cancellationToken), cancellationToken);
    }

    private Task<object> CollectStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Battle.net check, D2R check, process discovery, and input diagnostics each used to
        // run their own independent EnumWindows + per-window GetWindowTitle pass - up to 7 full
        // desktop scans for one status collection whenever exact-name matching failed, which is
        // exactly the case under investigation. Sharing one cache across all of them means the
        // window enumeration and any title lookups happen at most once per status call.
        var windowScanCache = OperatingSystem.IsWindows() ? new DesktopWindowScanCache() : null;
        var battleNetRunning = IsBattleNetRunning(windowScanCache);
        var d2rRunning = IsD2RRunning(windowScanCache);
        RefreshD2RProcessActivity(d2rRunning);

        var visibleState = DetectVisibleD2RState(d2rRunning);
        var activity = DetectVisibleActivitySnapshot(d2rRunning, visibleState);

        return Task.FromResult<object>(new
        {
            hostName = Environment.MachineName,
            userName = Environment.UserName,
            battleNetRunning,
            d2rRunning,
            d2rVisibleState = visibleState.ToString(),
            d2rProcessDiscovery = OperatingSystem.IsWindows() ? WindowsProcessFinder.Discover(GetD2RProcessNames(), windowScanCache) : null,
            // Gating this on d2rRunning blacked out the one field (foregroundProcessName) that
            // would show what's actually focused/visible when process-name matching itself is
            // what's failing - exactly the case where this is most needed.
            d2rInput = OperatingSystem.IsWindows() ? TryGetD2RInputDiagnostics(windowScanCache) : null,
            lastInputAction = _lastInputAction,
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
        // self_update doesn't touch D2R/Battle.net window state, so it never needs
        // to wait behind (or block) the gate that serializes UI automation. Routing
        // it through the gate meant a host-triggered update check - which fires
        // automatically on every agent reconnect, e.g. after a D2RHost restart -
        // could sit ahead of a real menu_ready/launch_d2r command and starve it for
        // the command's entire timeout before any launch was ever attempted.
        if (string.Equals(request.Command, "self_update", StringComparison.OrdinalIgnoreCase))
        {
            return await SelfUpdateAsync(cancellationToken);
        }

        // screenshot only captures the screen via a separate process - it never sends
        // input - so it doesn't need the gate either. It's also the main tool for
        // diagnosing a stuck automation command from the outside, which is exactly when
        // it's most needed and was previously most blocked (queued for the full length
        // of whatever menu_ready/menu_create_game was already running).
        if (string.Equals(request.Command, "screenshot", StringComparison.OrdinalIgnoreCase))
        {
            return await TakeScreenshotAsync(cancellationToken);
        }

        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteCommandAsync(request, cancellationToken);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task<CommandResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return request.Command switch
        {
            "status" => CommandResult.Success("Status collected.", await CollectStatusAsync(cancellationToken)),
            "launch_battlenet" => LaunchBattleNet(),
            "launch_d2r" => await LaunchD2RAsync(cancellationToken),
            "kill_d2r" => KillD2R(),
            "quit_d2r" => await QuitD2RAsync(cancellationToken),
            "restart_d2r" => await RestartD2RAsync(cancellationToken),
            "screenshot" => await TakeScreenshotAsync(cancellationToken),
            "menu_ready" => await ReadyClientAsync(cancellationToken),
            "menu_lobby" => await GoLobbyAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_play" => await PlayCharacterAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_prepare_join_game" => await PrepareJoinGameAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_submit_join_game" => await SubmitPreparedJoinGameAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_join_game" => await JoinGameAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_create_game" => await CreateGameAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_join_friend" => await JoinFriendAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_save_exit" => await SaveAndExitAsync(cancellationToken),
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
        if (IsD2RNamedProcessRunning())
        {
            RefreshD2RProcessActivity(d2rRunning: true);
            return CommandResult.Success("D2R is already running.", await CollectStatusAsync(cancellationToken));
        }

        ClearD2RActivity();
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
            await Task.Delay(TimeSpan.FromSeconds(GetBattleNetExecRetryDelaySeconds()), cancellationToken);
            if (!IsD2RNamedProcessRunning())
            {
                var retry = LaunchBattleNetD2R();
                if (!retry.Ok)
                {
                    return retry;
                }

                launchAttempts++;
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(GetLaunchGraceSeconds()), cancellationToken);
        var status = await CollectStatusAsync(cancellationToken);
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
                await _commandGate.WaitAsync(cancellationToken);
                try
                {
                    await QuitIfCharacterScreenIdleAsync(log ?? (_ => { }), cancellationToken);
                }
                finally
                {
                    _commandGate.Release();
                }
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
            return CommandResult.Success("D2R is not running.", await CollectStatusAsync(cancellationToken));
        }

        var input = new WindowsInput();
        if (!input.TryFocusProcess(GetD2RProcessNames()))
        {
            return CommandResult.Failure("D2R is running, but no focusable window was found.");
        }

        await DelayStepAsync(cancellationToken);
        input.PressAltF4();
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        ClearD2RActivity();
        return CommandResult.Success("Alt+F4 sent to D2R.", await CollectStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> ReadyClientAsync(CancellationToken cancellationToken)
    {
        var launch = await LaunchD2RAsync(cancellationToken);
        if (!launch.Ok)
        {
            return launch;
        }

        var input = new WindowsInput();
        await DelayLongAsync(cancellationToken);

        var d2rStarted = await WaitForD2RProcessStartedAsync(
            input,
            cancellationToken,
            Math.Max(GetD2RStartTimeoutSeconds(), D2RProcessStartFallbackTimeoutSeconds));

        var ready = await RunStartupReadyInputPlanUntilCharacterScreenAsync(input, cancellationToken);
        if (!ready.Ready)
        {
            var detectorReady = await PumpStartupSkipInputsUntilCharacterScreenAsync(
                input,
                cancellationToken,
                Math.Max(GetReadyLoopTimeoutSeconds(), MenuReadyFallbackTimeoutSeconds));
            ready = detectorReady with
            {
                Nudges = ready.Nudges + detectorReady.Nudges,
                TimeoutSeconds = ready.TimeoutSeconds + detectorReady.TimeoutSeconds
            };
        }

        if (!ready.Ready)
        {
            return CommandResult.Failure(
                $"{FormatCharacterScreenReadyFailure(ready, input)} D2R was not detected after {d2rStarted.LaunchAttempts} launch command(s) and {d2rStarted.PlayClicks} Battle.net Play click(s). Last launch result: {d2rStarted.LastLaunchMessage}.{FormatD2RProcessDiscoverySuffix()}",
                await CollectStatusAsync(cancellationToken));
        }

        await DelayCharacterScreenSettleAsync(cancellationToken);
        if (!await EnsureOnlineCharacterScreenAsync(input, cancellationToken))
        {
            return CommandResult.Failure(
                $"D2R reached the offline character screen, but clicking Online did not reveal the online character list within {GetCharacterScreenReconnectSeconds()}s.{FormatInputDiagnosticsSuffix()}",
                await CollectStatusAsync(cancellationToken));
        }

        MarkCharacterScreenIdle("Ready flow completed.");
        return CommandResult.Success("D2R ready flow completed.", await CollectStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> GoLobbyAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        var input = FocusD2R();
        var lobbyReady = await EnsureLobbyOpenedAsync(input, args, cancellationToken);
        if (lobbyReady is not null)
        {
            return lobbyReady;
        }

        return CommandResult.Success("Lobby command completed.", await CollectStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> PlayCharacterAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        var input = FocusD2R();
        var menuReady = await EnsureCharacterScreenReadyForMenuAsync(input, cancellationToken);
        if (menuReady is not null)
        {
            return menuReady;
        }

        await SelectCharacterAsync(input, args.CharacterSlot, cancellationToken);
        ClickD2R(input, _config.Ui.CharacterPlayButton);
        var entry = await WaitForGameEntryAsync(input, cancellationToken);
        if (entry != GameEntryWaitResult.EnteredGame)
        {
            return CommandResult.Failure(
                $"Clicked Play, but the client did not enter the game within {Math.Max(_config.Ui.GameEntryStartTimeoutSeconds, 1)}s. {FormatGameEntryWaitFailure(entry)}",
                await CollectStatusAsync(cancellationToken));
        }

        MarkLobbyOrGameInteraction("Clicked Play.");
        return CommandResult.Success("Play character command completed.", await CollectStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> JoinGameAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.GameName))
        {
            return CommandResult.Failure("gameName is required for menu_join_game.");
        }

        var input = FocusD2R();
        var prepared = await PrepareJoinGameFormWithTimeoutAsync(input, args, cancellationToken);
        if (prepared is not null)
        {
            return prepared;
        }

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
                await CollectStatusAsync(cancellationToken));
        }

        MarkLobbyOrGameInteraction($"Joined game {args.GameName}.");
        var retrySuffix = FormatEntryRecoverySuffix(joinEntry);
        return CommandResult.Success($"Join game flow completed for {args.GameName}.{retrySuffix}", await CollectStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> PrepareJoinGameAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.GameName))
        {
            return CommandResult.Failure("gameName is required for menu_prepare_join_game.");
        }

        var input = FocusD2R();
        var prepared = await PrepareJoinGameFormWithTimeoutAsync(input, args, cancellationToken);
        if (prepared is not null)
        {
            return prepared;
        }

        return CommandResult.Success($"Join game form prepared for {args.GameName}.", await CollectStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> SubmitPreparedJoinGameAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.GameName))
        {
            return CommandResult.Failure("gameName is required for menu_submit_join_game.");
        }

        var input = FocusD2R();
        var prepared = await PrepareJoinGameFormWithTimeoutAsync(input, args, cancellationToken);
        if (prepared is not null)
        {
            return prepared;
        }

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
                await CollectStatusAsync(cancellationToken));
        }

        MarkLobbyOrGameInteraction($"Joined game {args.GameName}.");
        var retrySuffix = FormatEntryRecoverySuffix(joinEntry);
        return CommandResult.Success($"Join game flow completed for {args.GameName}.{retrySuffix}", await CollectStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> CreateGameAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.GameName))
        {
            return CommandResult.Failure("gameName is required for menu_create_game.");
        }

        var input = FocusD2R();
        var lobby = await EnsureLobbyOpenedAsync(input, args, cancellationToken);
        if (lobby is not null)
        {
            return lobby;
        }

        await ClickLobbyTabDirectAsync(input, _config.Ui.CreateGameTab, cancellationToken);

        await FillTextFieldAsync(input, _config.Ui.CreateGameNameField, args.GameName, cancellationToken);
        await FillTextFieldAsync(input, _config.Ui.CreatePasswordField, args.Password ?? "", cancellationToken);
        ClickD2R(input, GetCreateDifficultyPoint(args.Difficulty));
        await DelayStepAsync(cancellationToken);
        var createEntry = await ClickMenuEntryButtonUntilEnteredGameAsync(
            input,
            _config.Ui.CreateGameButton,
            _config.Ui.CreateGameTab,
            () => RestoreCreateGameFormAsync(input, args, cancellationToken),
            cancellationToken,
            "A game-entry error dialog appeared after clicking Create Game. The game name may already exist.");
        if (!createEntry.Entered)
        {
            return CommandResult.Failure(
                $"Clicked Create Game, but the client did not enter the game within {Math.Max(_config.Ui.GameEntryStartTimeoutSeconds, 1)}s. {createEntry.Message}",
                await CollectStatusAsync(cancellationToken));
        }

        MarkLobbyOrGameInteraction($"Created game {args.GameName}.");
        var retrySuffix = FormatEntryRecoverySuffix(createEntry);
        return CommandResult.Success($"Create game flow completed for {args.GameName}.{retrySuffix}", await CollectStatusAsync(cancellationToken));
    }

    private async Task<CommandResult?> PrepareJoinGameFormWithTimeoutAsync(
        WindowsInput input,
        MenuCommandArgs args,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = GetJoinPrepareTimeoutSeconds();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            return await PrepareJoinGameFormAsync(input, args, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Failure(
                $"Join Game form preparation timed out after {timeoutSeconds}s while activity state was {GetActivitySnapshot().State}.{FormatInputDiagnosticsSuffix()}",
                await CollectStatusAsync(cancellationToken));
        }
    }

    private async Task<CommandResult?> PrepareJoinGameFormAsync(
        WindowsInput input,
        MenuCommandArgs args,
        CancellationToken cancellationToken)
    {
        if (IsInGameReady(input))
        {
            return CommandResult.Failure(
                "D2R is already in a game; use /d2r save-exit before character-screen menu automation.",
                await CollectStatusAsync(cancellationToken));
        }

        if (CanUseRememberedLobbyOrGameState(input))
        {
            MarkLobbyOrGameInteraction("Preparing Join Game from existing lobby state.");
            await RestoreJoinGameFormAsync(input, args, cancellationToken);
            return null;
        }

        var lobby = await EnsureLobbyOpenedAsync(input, args, cancellationToken);
        if (lobby is not null)
        {
            return lobby;
        }

        await RestoreJoinGameFormAsync(input, args, cancellationToken);
        return null;
    }

    private async Task<CommandResult> JoinFriendAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        var input = FocusD2R();
        var lobby = await EnsureLobbyOpenedAsync(input, args, cancellationToken);
        if (lobby is not null)
        {
            return lobby;
        }

        ClickD2R(input, _config.Ui.LobbyPartyIcon);
        await DelayLongAsync(cancellationToken);
        ClickD2R(input, GetFriendRowPoint(args.FriendRow), MouseButton.Right);
        await DelayStepAsync(cancellationToken);
        ClickD2R(input, _config.Ui.FriendContextJoinGame);
        var entry = await WaitForGameEntryAsync(input, cancellationToken);
        if (entry != GameEntryWaitResult.EnteredGame)
        {
            return CommandResult.Failure(
                $"Clicked friend Join Game, but the client did not enter the game within {Math.Max(_config.Ui.GameEntryStartTimeoutSeconds, 1)}s. {FormatGameEntryWaitFailure(entry)}",
                await CollectStatusAsync(cancellationToken));
        }

        MarkLobbyOrGameInteraction("Joined friend game.");
        return CommandResult.Success("Join friend/follow flow completed.", await CollectStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> SaveAndExitAsync(CancellationToken cancellationToken)
    {
        var input = FocusD2R();
        input.PressEscape();
        await DelayStepAsync(cancellationToken);
        ClickD2R(input, _config.Ui.SaveAndExitButton);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(_config.Ui.LobbyLoadSeconds, 1)), cancellationToken);
        MarkCharacterScreenIdle("Save and Exit completed.");
        return CommandResult.Success("Save and Exit flow completed.", await CollectStatusAsync(cancellationToken));
    }

    private async Task<CommandResult?> EnsureCharacterScreenReadyForMenuAsync(
        WindowsInput input,
        CancellationToken cancellationToken,
        int? readyTimeoutSeconds = null)
    {
        if (IsInGameReady(input))
        {
            return CommandResult.Failure(
                "D2R is already in a game; use /d2r save-exit before character-screen menu automation.",
                await CollectStatusAsync(cancellationToken));
        }

        if (IsCharacterScreenOffline(input))
        {
            var online = await EnsureOnlineCharacterScreenAsync(input, cancellationToken);
            if (!online)
            {
                return CommandResult.Failure(
                    $"D2R is at the offline character screen, and the Online tab did not reconnect within {GetCharacterScreenReconnectSeconds()}s.{FormatInputDiagnosticsSuffix()}",
                    await CollectStatusAsync(cancellationToken));
            }
        }

        if (IsCharacterScreenReady(input))
        {
            return null;
        }

        if (GetActivitySnapshot().State == D2RActivityState.CharacterScreenIdle)
        {
            return null;
        }

        var ready = await RunStartupReadyInputPlanUntilCharacterScreenAsync(input, cancellationToken);
        if (!ready.Ready)
        {
            var detectorReady = await PumpStartupSkipInputsUntilCharacterScreenAsync(
                input,
                cancellationToken,
                readyTimeoutSeconds ?? Math.Max(GetReadyLoopTimeoutSeconds(), MenuReadyFallbackTimeoutSeconds));
            ready = detectorReady with
            {
                Nudges = ready.Nudges + detectorReady.Nudges,
                TimeoutSeconds = ready.TimeoutSeconds + detectorReady.TimeoutSeconds
            };
        }

        if (!ready.Ready)
        {
            return CommandResult.Failure(
                FormatCharacterScreenReadyFailure(ready, input),
                await CollectStatusAsync(cancellationToken));
        }

        await DelayCharacterScreenSettleAsync(cancellationToken);
        return null;
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
            _lastLobbyOrGameInteractionUtc = null;
            _lastObservedD2RStartUtc = null;
            _lastActivityReason = null;
        }
    }

    private void RefreshD2RProcessActivity(bool d2rRunning)
    {
        if (!d2rRunning)
        {
            ClearD2RActivity();
            return;
        }

        var processStartedUtc = TryGetD2RProcessStartUtc();
        if (processStartedUtc is null)
        {
            return;
        }

        lock (_activityLock)
        {
            if (_lastObservedD2RStartUtc is not null
                && Math.Abs((processStartedUtc.Value - _lastObservedD2RStartUtc.Value).TotalSeconds) > 1)
            {
                _activityState = D2RActivityState.Unknown;
                _characterScreenIdleSinceUtc = null;
                _lastLobbyOrGameInteractionUtc = null;
                _lastActivityReason = "D2R process restarted.";
            }

            _lastObservedD2RStartUtc = processStartedUtc;
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

    private ActivitySnapshot DetectVisibleActivitySnapshot(bool d2rRunning, VisibleD2RState visibleState)
    {
        var activity = GetActivitySnapshot();
        if (!d2rRunning || !OperatingSystem.IsWindows())
        {
            return activity;
        }

        return visibleState switch
        {
            VisibleD2RState.CharacterScreen or VisibleD2RState.OfflineCharacterScreen => new ActivitySnapshot(
                D2RActivityState.CharacterScreenIdle,
                activity.CharacterScreenIdleSinceUtc ?? DateTimeOffset.UtcNow,
                activity.LastLobbyOrGameInteractionUtc,
                activity.Reason ?? "Detected character screen."),
            VisibleD2RState.LobbyOrGame or VisibleD2RState.InGame => new ActivitySnapshot(
                D2RActivityState.LobbyOrGame,
                null,
                activity.LastLobbyOrGameInteractionUtc ?? DateTimeOffset.UtcNow,
                activity.Reason ?? "Detected lobby or in-game UI."),
            VisibleD2RState.DiabloSplash => new ActivitySnapshot(
                D2RActivityState.Unknown,
                null,
                activity.LastLobbyOrGameInteractionUtc,
                "Detected Diablo splash screen."),
            VisibleD2RState.Unknown => activity.State == D2RActivityState.Unknown
                ? activity
                : new ActivitySnapshot(
                    D2RActivityState.Unknown,
                    null,
                    activity.LastLobbyOrGameInteractionUtc,
                    "Visible D2R screen is not a known menu/game state."),
            _ => activity
        };
    }

    private VisibleD2RState DetectVisibleD2RState(bool d2rRunning)
    {
        if (!OperatingSystem.IsWindows())
        {
            return d2rRunning ? VisibleD2RState.Unknown : VisibleD2RState.NotRunning;
        }

        try
        {
            var visibleState = DetectVisibleD2RState(new WindowsInput());
            if (visibleState != VisibleD2RState.Unknown)
            {
                return visibleState;
            }
        }
        catch (Exception)
        {
        }

        return d2rRunning ? VisibleD2RState.Unknown : VisibleD2RState.NotRunning;
    }

    private VisibleD2RState DetectVisibleD2RState(WindowsInput input)
    {
        if (IsDiabloSplashScreen(input))
        {
            return VisibleD2RState.DiabloSplash;
        }

        if (IsCharacterScreenOffline(input))
        {
            return VisibleD2RState.OfflineCharacterScreen;
        }

        if (IsCharacterScreenReady(input))
        {
            return VisibleD2RState.CharacterScreen;
        }

        if (IsInGameReady(input))
        {
            return VisibleD2RState.InGame;
        }

        return IsAnyLobbyEntryMenuVisible(input)
            ? VisibleD2RState.LobbyOrGame
            : VisibleD2RState.Unknown;
    }

    private WindowsInput FocusD2R()
    {
        var processNames = GetD2RProcessNames();
        if (!IsD2RRunning())
        {
            throw new InvalidOperationException($"Process is not running: {FormatProcessNames(processNames)}");
        }

        var input = new WindowsInput();
        if (!TryPrepareD2RForInput(input))
        {
            // ClickD2R/PressKey route through SendInput, which delivers to whatever
            // window currently has focus - not to a specific HWND. If D2R never
            // became the foreground window, every click and keypress for the rest
            // of the command lands on whatever does have focus instead (commonly an
            // operator's own Task Manager/RDP window), and the command silently
            // grinds for its full timeout instead of failing in milliseconds. Fail
            // fast and name the window that actually has focus.
            var foregroundProcessName = input.GetInputDiagnostics(processNames).ForegroundProcessName;
            throw new InvalidOperationException(
                $"Could not bring D2R to the foreground; focus is on {foregroundProcessName ?? "an unknown window"} instead. Close or minimize whatever currently has focus on the VM and retry.");
        }

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

    private async Task<bool> EnsureOnlineCharacterScreenAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        if (!IsCharacterScreenOffline(input))
        {
            return true;
        }

        var timeout = TimeSpan.FromSeconds(GetCharacterScreenReconnectSeconds());
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = TryPrepareD2RForInput(input);

            if (IsCharacterScreenReady(input))
            {
                return true;
            }

            if (IsCharacterScreenOffline(input))
            {
                ClickD2R(input, _config.Ui.CharacterOnlineTab);
            }

            var remainingMs = Math.Max((deadline - DateTimeOffset.UtcNow).TotalMilliseconds, 0);
            if (remainingMs == 0)
            {
                break;
            }

            await Task.Delay((int)Math.Min(Math.Max(_config.Ui.LobbyLoadSeconds, 1) * 1000, remainingMs), cancellationToken);
        }

        return IsCharacterScreenReady(input);
    }

    private async Task<ReadyWaitResult> PumpStartupSkipInputsUntilCharacterScreenAsync(
        WindowsInput input,
        CancellationToken cancellationToken,
        int? timeoutSeconds = null)
    {
        var skipSeconds = timeoutSeconds ?? GetReadyLoopTimeoutSeconds();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(skipSeconds);
        var intervalMs = Math.Clamp(_config.Ui.ReadyStartupSkipIntervalMs, 50, 250);
        var nudges = 0;
        var lastState = ReadyScreenState.Unknown;
        var nextDetectionAt = DateTimeOffset.UtcNow;
        var sawD2RProcessRunning = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow >= nextDetectionAt)
            {
                // The screen can keep showing D2R's last rendered frame for a while after the
                // process itself dies (stale framebuffer on the VM's virtual display), so pixel
                // classification alone can't tell "still loading" apart from "already crashed."
                // Catching the live-to-gone transition here turns a silent multi-minute stall
                // into an immediate, accurate failure instead of nudging a dead window.
                var d2rNamedProcessRunning = IsD2RNamedProcessRunning();
                if (d2rNamedProcessRunning)
                {
                    sawD2RProcessRunning = true;
                }
                else if (sawD2RProcessRunning)
                {
                    return new ReadyWaitResult(false, nudges, lastState, skipSeconds, ProcessExitedDuringWait: true);
                }

                var state = DetectReadyScreenStateStable(input);
                lastState = state;
                if (IsReadyScreenState(state))
                {
                    return new ReadyWaitResult(true, nudges, lastState, skipSeconds);
                }

                nextDetectionAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(ReadyStartupDetectionIntervalMs);
            }

            if (lastState != ReadyScreenState.ConnectingToBattleNet)
            {
                SendReadySkipBurst(input);
                nudges++;
            }

            var remainingMs = Math.Max((deadline - DateTimeOffset.UtcNow).TotalMilliseconds, 0);
            if (remainingMs == 0)
            {
                break;
            }

            await Task.Delay((int)Math.Min(intervalMs, remainingMs), cancellationToken);
        }

        return new ReadyWaitResult(false, nudges, lastState, skipSeconds);
    }

    private async Task<ReadyWaitResult> RunStartupReadyInputPlanUntilCharacterScreenAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        var plan = StartupReadyInputPlan.FromConfig(_config.Ui);
        var timeoutSeconds = GetReadyStartupSkipSeconds();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        var lastState = ReadyScreenState.Unknown;
        var nudges = 0;
        var nextDetectionAt = DateTimeOffset.UtcNow;
        var sawD2RProcessRunning = false;

        for (var i = 0; i < plan.IntroClickCount && DateTimeOffset.UtcNow < deadline; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= nextDetectionAt)
            {
                // See PumpStartupSkipInputsUntilCharacterScreenAsync: the splash frame can
                // outlive the process that drew it, so a process-gone transition after we've
                // already seen it alive is treated as a crash, not "still loading."
                var d2rNamedProcessRunning = IsD2RNamedProcessRunning();
                if (d2rNamedProcessRunning)
                {
                    sawD2RProcessRunning = true;
                }
                else if (sawD2RProcessRunning)
                {
                    return new ReadyWaitResult(false, nudges, lastState, timeoutSeconds, ProcessExitedDuringWait: true);
                }

                lastState = DetectReadyScreenStateStable(input);
                if (IsReadyScreenState(lastState))
                {
                    return new ReadyWaitResult(true, nudges, lastState, timeoutSeconds);
                }

                nextDetectionAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(ReadyStartupDetectionIntervalMs);
            }

            if (lastState == ReadyScreenState.ConnectingToBattleNet)
            {
                i--;
                await Task.Delay(ReadyStartupDetectionIntervalMs, cancellationToken);
                continue;
            }

            SendReadyIntroClick(input);
            nudges++;
            await Task.Delay(plan.IntroClickDelayMs, cancellationToken);
        }

        for (var i = 0; i < plan.TitleScreenKeyPressCount && DateTimeOffset.UtcNow < deadline; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= nextDetectionAt)
            {
                var d2rNamedProcessRunning = IsD2RNamedProcessRunning();
                if (d2rNamedProcessRunning)
                {
                    sawD2RProcessRunning = true;
                }
                else if (sawD2RProcessRunning)
                {
                    return new ReadyWaitResult(false, nudges, lastState, timeoutSeconds, ProcessExitedDuringWait: true);
                }

                lastState = DetectReadyScreenStateStable(input);
                if (IsReadyScreenState(lastState))
                {
                    return new ReadyWaitResult(true, nudges, lastState, timeoutSeconds);
                }

                nextDetectionAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(ReadyStartupDetectionIntervalMs);
            }

            if (lastState == ReadyScreenState.ConnectingToBattleNet)
            {
                i--;
                await Task.Delay(ReadyStartupDetectionIntervalMs, cancellationToken);
                continue;
            }

            SendReadyTitleSkipBurst(input);
            nudges++;
            await Task.Delay(plan.TitleScreenKeyPressDelayMs, cancellationToken);
        }

        lastState = DetectReadyScreenStateStable(input);
        return new ReadyWaitResult(
            IsReadyScreenState(lastState),
            nudges,
            lastState,
            timeoutSeconds);
    }

    private void SendReadyIntroClick(WindowsInput input)
    {
        var target = ResolveD2RScreenPoint(_config.Ui.IntroSkipPoint);
        var beforeCursor = input.GetCursorPosition();
        var beforeDiagnostics = TryGetD2RInputDiagnostics();
        foreach (var action in StartupReadyInputPlan.IntroActions)
        {
            switch (action)
            {
                case StartupReadyInputAction.FocusD2R:
                    _ = TryPrepareD2RForInput(input);
                    break;
                case StartupReadyInputAction.ClickWindowCenter:
                    _ = TryClickD2RWindowCenter(input);
                    break;
                case StartupReadyInputAction.PressEscapeKey:
                    input.PressEscape();
                    break;
                case StartupReadyInputAction.SendWindowEscapeKey:
                    _ = input.SendWindowEscapeKey(GetD2RProcessNames());
                    break;
                case StartupReadyInputAction.ClickIntroPoint:
                    ClickD2R(input, _config.Ui.IntroSkipPoint);
                    break;
                case StartupReadyInputAction.SendWindowClickIntroPoint:
                    _ = input.SendWindowClick(_config.Ui.IntroSkipPoint, GetD2RProcessNames(), MouseButton.Left);
                    break;
                case StartupReadyInputAction.PressStartupSkipKey:
                    input.PressStartupSkipKey();
                    break;
                case StartupReadyInputAction.PressStartKey:
                    input.PressStartKey();
                    break;
                case StartupReadyInputAction.SendWindowStartupSkipKey:
                    _ = input.SendWindowReadySkipKey(GetD2RProcessNames());
                    break;
            }
        }

        var afterCursor = input.GetCursorPosition();
        var afterDiagnostics = TryGetD2RInputDiagnostics();
        RecordD2RInputAction(
            kind: "key",
            button: "Escape/G/Space/Enter",
            point: _config.Ui.IntroSkipPoint,
            target,
            beforeCursor,
            afterCursor,
            beforeDiagnostics,
            afterDiagnostics);
    }

    private bool IsD2RForeground()
    {
        return TryGetD2RInputDiagnostics()?.IsForeground == true;
    }

    private void SendReadyTitleSkipBurst(WindowsInput input)
    {
        var target = ResolveD2RScreenPoint(_config.Ui.IntroSkipPoint);
        var beforeCursor = input.GetCursorPosition();
        var beforeDiagnostics = TryGetD2RInputDiagnostics();
        foreach (var action in StartupReadyInputPlan.TitleActions)
        {
            switch (action)
            {
                case StartupReadyInputAction.FocusD2R:
                    _ = TryPrepareD2RForInput(input);
                    break;
                case StartupReadyInputAction.ClickWindowCenter:
                    _ = TryClickD2RWindowCenter(input);
                    break;
                case StartupReadyInputAction.PressStartupSkipKey:
                    input.PressStartupSkipKey();
                    break;
                case StartupReadyInputAction.PressStartKey:
                    input.PressStartKey();
                    break;
                case StartupReadyInputAction.SendWindowStartupSkipKey:
                    _ = input.SendWindowReadySkipKey(GetD2RProcessNames());
                    break;
                case StartupReadyInputAction.SendWindowReadyBurst:
                    _ = input.SendWindowReadyBurst(GetD2RProcessNames(), _config.Ui.IntroSkipPoint, includeEscape: true);
                    break;
            }
        }

        var afterCursor = input.GetCursorPosition();
        var afterDiagnostics = TryGetD2RInputDiagnostics();
        RecordD2RInputAction(
            kind: "key",
            button: "G/Space/Enter",
            point: _config.Ui.IntroSkipPoint,
            target,
            beforeCursor,
            afterCursor,
            beforeDiagnostics,
            afterDiagnostics);
    }

    private void SendReadySkipBurst(WindowsInput input)
    {
        var target = ResolveD2RScreenPoint(_config.Ui.IntroSkipPoint);
        var beforeCursor = input.GetCursorPosition();
        var beforeDiagnostics = TryGetD2RInputDiagnostics();
        foreach (var action in StartupReadyInputPlan.BurstActions)
        {
            switch (action)
            {
                case StartupReadyInputAction.FocusD2R:
                    _ = TryPrepareD2RForInput(input);
                    break;
                case StartupReadyInputAction.ClickWindowCenter:
                    _ = TryClickD2RWindowCenter(input);
                    break;
                case StartupReadyInputAction.ClickIntroPoint:
                    ClickD2R(input, _config.Ui.IntroSkipPoint);
                    break;
                case StartupReadyInputAction.SendWindowClickIntroPoint:
                    _ = input.SendWindowClick(_config.Ui.IntroSkipPoint, GetD2RProcessNames(), MouseButton.Left);
                    break;
                case StartupReadyInputAction.PressStartupSkipKey:
                    input.PressStartupSkipKey();
                    break;
                case StartupReadyInputAction.PressStartKey:
                    input.PressStartKey();
                    break;
                case StartupReadyInputAction.SendWindowStartupSkipKey:
                    _ = input.SendWindowReadySkipKey(GetD2RProcessNames());
                    break;
                case StartupReadyInputAction.SendWindowReadyBurst:
                    _ = input.SendWindowReadyBurst(GetD2RProcessNames(), _config.Ui.IntroSkipPoint, includeEscape: true);
                    break;
            }
        }

        var afterCursor = input.GetCursorPosition();
        var afterDiagnostics = TryGetD2RInputDiagnostics();
        RecordD2RInputAction(
            kind: "key",
            button: "G/Space/Enter",
            point: _config.Ui.IntroSkipPoint,
            target,
            beforeCursor,
            afterCursor,
            beforeDiagnostics,
            afterDiagnostics);
    }

    private bool TryClickD2RWindowCenter(WindowsInput input)
    {
        try
        {
            return input.TryClickProcessWindowCenter(GetD2RProcessNames());
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void ClickD2R(
        WindowsInput input,
        AgentCommon.UiPoint point,
        MouseButton button = MouseButton.Left)
    {
        var processNames = GetD2RProcessNames();
        var target = input.ResolveScreenPoint(point, processNames);
        var beforeCursor = input.GetCursorPosition();
        InputDiagnostics? beforeDiagnostics = null;
        InputDiagnostics? afterDiagnostics = null;
        try
        {
            beforeDiagnostics = input.GetInputDiagnostics(processNames);
        }
        catch (Exception)
        {
            beforeDiagnostics = null;
        }

        if (button == MouseButton.Left)
        {
            input.LeftClick(point, processNames);
        }
        else
        {
            input.RightClick(point, processNames);
        }

        var afterCursor = input.GetCursorPosition();
        try
        {
            afterDiagnostics = input.GetInputDiagnostics(processNames);
        }
        catch (Exception)
        {
            afterDiagnostics = null;
        }

        RecordD2RInputAction(
            kind: "click",
            button: button.ToString(),
            point,
            target,
            beforeCursor,
            afterCursor,
            beforeDiagnostics,
            afterDiagnostics);
    }

    private void RecordD2RInputAction(
        string kind,
        string button,
        AgentCommon.UiPoint point,
        (int X, int Y) target,
        CursorPosition? beforeCursor,
        CursorPosition? afterCursor,
        InputDiagnostics? beforeDiagnostics,
        InputDiagnostics? afterDiagnostics)
    {
        _lastInputAction = new LastInputActionSnapshot(
            DateTimeOffset.UtcNow,
            kind,
            button,
            point.X,
            point.Y,
            target.X,
            target.Y,
            beforeCursor,
            afterCursor,
            beforeDiagnostics?.IsForeground,
            afterDiagnostics?.IsForeground,
            beforeDiagnostics?.ForegroundProcessName,
            afterDiagnostics?.ForegroundProcessName);
    }

    private (int X, int Y) ResolveD2RScreenPoint(AgentCommon.UiPoint point)
    {
        try
        {
            return new WindowsInput().ResolveScreenPoint(point, GetD2RProcessNames());
        }
        catch (Exception)
        {
            return new WindowsInput().ResolveScreenPoint(point);
        }
    }

    private InputDiagnostics? TryGetD2RInputDiagnostics(DesktopWindowScanCache? cache = null)
    {
        try
        {
            return new WindowsInput().GetInputDiagnostics(GetD2RProcessNames(), cache);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string FormatInputDiagnosticsSuffix()
    {
        var diagnostics = TryGetD2RInputDiagnostics();
        if (diagnostics is null)
        {
            return "";
        }

        return $" Input diagnostics: userInteractive={diagnostics.UserInteractive}, d2rSession={diagnostics.SessionId?.ToString() ?? "?"}, sessionActive={diagnostics.TargetSessionActive?.ToString() ?? "?"}, hasWindow={diagnostics.HasMainWindow}, foreground={diagnostics.IsForeground}, foregroundProcess={diagnostics.ForegroundProcessName ?? "?"}, agentElevated={diagnostics.AgentElevated?.ToString() ?? "?"}, targetElevated={diagnostics.TargetElevated?.ToString() ?? "?"}, screen={diagnostics.ScreenWidth}x{diagnostics.ScreenHeight}, window={FormatInputRect(diagnostics.WindowRect)}, client={FormatInputRect(diagnostics.ClientRect)}.";
    }

    private string FormatD2RProcessDiscoverySuffix()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "";
        }

        var discovery = WindowsProcessFinder.Discover(GetD2RProcessNames());
        var search = discovery.SearchNames.Length == 0
            ? "?"
            : string.Join("/", discovery.SearchNames);
        var matches = discovery.Matches.Length == 0
            ? "0"
            : string.Join("|", discovery.Matches.Take(3).Select(match =>
                $"{match.ProcessName}#{match.ProcessId}:{(match.HasMainWindow ? "window" : "noWindow")}"));
        var fallback = discovery.FallbackMatches.Length == 0
            ? ""
            : $" Unmatched processes with a d2r/diablo-like name: {string.Join("|", discovery.FallbackMatches.Take(5).Select(match => $"{match.ProcessName}#{match.ProcessId}:{(match.HasMainWindow ? "window" : "noWindow")}"))}.";
        return $" Process discovery: search={search}, matches={matches}.{fallback}";
    }

    private static string FormatInputRect(InputRect? rect)
    {
        return rect is null
            ? "?"
            : $"{rect.Left},{rect.Top},{rect.Width}x{rect.Height}";
    }

    private bool CanUseRememberedLobbyOrGameState(WindowsInput input)
    {
        if (IsCharacterScreenReady(input)
            || IsCharacterScreenOffline(input)
            || IsConnectionInterruptedScreen(input))
        {
            return false;
        }

        return IsAnyLobbyEntryMenuVisible(input)
            || GetActivitySnapshot().State == D2RActivityState.LobbyOrGame;
    }

    private async Task<CommandResult?> EnsureLobbyOpenedAsync(
        WindowsInput input,
        MenuCommandArgs args,
        CancellationToken cancellationToken)
    {
        _ = TryPrepareD2RForInput(input);
        if (IsAnyLobbyEntryMenuVisible(input))
        {
            MarkLobbyOrGameInteraction("Lobby already visible.");
            return null;
        }

        if (IsInGameReady(input))
        {
            return CommandResult.Failure(
                "D2R is already in a game; use /d2r save-exit before character-screen menu automation.",
                await CollectStatusAsync(cancellationToken));
        }

        if (CanUseRememberedLobbyOrGameState(input))
        {
            MarkLobbyOrGameInteraction("Using existing lobby state for menu automation.");
            return null;
        }

        if (await TryOpenLobbyFromCurrentScreenAsync(input, args, cancellationToken))
        {
            MarkLobbyOrGameInteraction("Opened Lobby from current screen.");
            return null;
        }

        var activity = GetActivitySnapshot();
        if (activity.State != D2RActivityState.CharacterScreenIdle)
        {
            var menuReady = await EnsureCharacterScreenReadyForMenuAsync(
                input,
                cancellationToken,
                readyTimeoutSeconds: Math.Min(GetReadyLoopTimeoutSeconds(), 8));
            if (menuReady is not null)
            {
                return menuReady;
            }
        }

        if (await OpenLobbyFromCharacterScreenAsync(input, args, cancellationToken))
        {
            MarkLobbyOrGameInteraction("Clicked Lobby from character screen.");
            return null;
        }

        return CommandResult.Failure(
            $"D2R is at the character screen, but clicking Lobby did not reveal the lobby menu within {Math.Clamp(_config.Ui.LobbyLoadSeconds, 1, 4)}s.{FormatInputDiagnosticsSuffix()}",
            await CollectStatusAsync(cancellationToken));
    }

    private async Task<bool> TryOpenLobbyFromCurrentScreenAsync(
        WindowsInput input,
        MenuCommandArgs args,
        CancellationToken cancellationToken)
    {
        if (IsAnyLobbyEntryMenuVisible(input))
        {
            return true;
        }

        if (IsCharacterScreenOffline(input)
            && !await EnsureOnlineCharacterScreenAsync(input, cancellationToken))
        {
            return false;
        }

        if (IsInGameReady(input))
        {
            return false;
        }

        if (IsCharacterScreenReady(input))
        {
            return await OpenLobbyFromCharacterScreenAsync(input, args, cancellationToken);
        }

        if (DetectReadyScreenStateStable(input) == ReadyScreenState.CharacterScreen)
        {
            return await OpenLobbyFromCharacterScreenAsync(input, args, cancellationToken);
        }

        await SelectCharacterAsync(input, args.CharacterSlot, cancellationToken);
        return await ClickLobbyDirectAsync(input, cancellationToken);
    }

    private async Task<bool> OpenLobbyFromCharacterScreenAsync(
        WindowsInput input,
        MenuCommandArgs args,
        CancellationToken cancellationToken)
    {
        if (IsCharacterScreenOffline(input)
            && !await EnsureOnlineCharacterScreenAsync(input, cancellationToken))
        {
            return false;
        }

        if (await ClickLobbyDirectAsync(input, cancellationToken))
        {
            return true;
        }

        await SelectCharacterAsync(input, args.CharacterSlot, cancellationToken);
        return await ClickLobbyDirectAsync(input, cancellationToken);
    }

    private async Task<bool> ClickLobbyDirectAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_config.Ui.LobbyLoadSeconds, 2, 4));
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = TryPrepareD2RForInput(input);
            if (IsAnyLobbyEntryMenuVisible(input))
            {
                return true;
            }

            ClickD2R(input, _config.Ui.CharacterLobbyButton);
            var remainingMs = Math.Max((deadline - DateTimeOffset.UtcNow).TotalMilliseconds, 0);
            if (remainingMs == 0)
            {
                break;
            }

            await Task.Delay((int)Math.Min(LobbyPollIntervalMs, remainingMs), cancellationToken);
        }

        return IsAnyLobbyEntryMenuVisible(input);
    }

    private async Task ClickLobbyTabDirectAsync(
        WindowsInput input,
        AgentCommon.UiPoint tab,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_config.Ui.LobbyLoadSeconds, 2, 4));
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = TryPrepareD2RForInput(input);
            if (IsLobbyTabReady(input, tab))
            {
                break;
            }

            ClickD2R(input, tab);
            var remainingMs = Math.Max((deadline - DateTimeOffset.UtcNow).TotalMilliseconds, 0);
            if (remainingMs == 0)
            {
                break;
            }

            await Task.Delay((int)Math.Min(LobbyPollIntervalMs, remainingMs), cancellationToken);
        }

        MarkLobbyOrGameInteraction("Clicked lobby tab.");
    }

    private async Task<GameEntryAttemptResult> ClickMenuEntryButtonUntilEnteredGameAsync(
        WindowsInput input,
        AgentCommon.UiPoint button,
        AgentCommon.UiPoint activeTab,
        Func<Task<bool>> restoreFormAsync,
        CancellationToken cancellationToken,
        string? errorDialogFailureMessage = null)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(_config.Ui.GameEntryStartTimeoutSeconds, 1));
        var deadline = DateTimeOffset.UtcNow + timeout;
        var dialogRetries = 0;
        var connectionRetries = 0;
        await ClickMenuEntryButtonAsync(input, button, cancellationToken);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = TryPrepareD2RForInput(input);

            if (await TryConfirmEnteredGameAsync(input, cancellationToken))
            {
                return new GameEntryAttemptResult(true, dialogRetries, connectionRetries, "Entered game.");
            }

            if (IsGameEntryErrorDialogOpen(input))
            {
                dialogRetries++;
                if (errorDialogFailureMessage is not null)
                {
                    _ = await DismissGameEntryErrorDialogAsync(input, cancellationToken);
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, errorDialogFailureMessage);
                }

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

                deadline = DateTimeOffset.UtcNow + timeout;
                await ClickMenuEntryButtonAsync(input, button, cancellationToken);
                continue;
            }

            if (IsCharacterScreenOffline(input))
            {
                if (!await EnsureOnlineCharacterScreenAsync(input, cancellationToken)
                    || !await ClickLobbyDirectAsync(input, cancellationToken)
                    || !await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned to the offline character screen, and the menu form could not be restored after clicking Online.");
                }
            }
            else if (IsCharacterScreenReady(input))
            {
                if (!await ClickLobbyDirectAsync(input, cancellationToken)
                    || !await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned to character select, but the menu form could not be restored.");
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                if (await TryConfirmEnteredGameAsync(input, cancellationToken))
                {
                    return new GameEntryAttemptResult(true, dialogRetries, connectionRetries, "Entered game at timeout boundary.");
                }

                return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, FormatEntryTimeoutMessage(input, activeTab, dialogRetries, connectionRetries));
            }

            var waitResult = await WaitForGameEntryAsync(input, activeTab, cancellationToken);
            if (waitResult == GameEntryWaitResult.EnteredGame)
            {
                return new GameEntryAttemptResult(true, dialogRetries, connectionRetries, "Entered game.");
            }

            if (await TryConfirmEnteredGameAsync(input, cancellationToken))
            {
                return new GameEntryAttemptResult(true, dialogRetries, connectionRetries, "Entered game after wait result.");
            }

            if (waitResult == GameEntryWaitResult.ConnectionInterrupted)
            {
                connectionRetries++;
                if (!await WaitForMenuAfterConnectionInterruptedAsync(input, activeTab, cancellationToken)
                    || !await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "Connection was interrupted, but the menu form could not be restored.");
                }

                deadline = DateTimeOffset.UtcNow + timeout;
                await ClickMenuEntryButtonAsync(input, button, cancellationToken);
                continue;
            }
            else if (waitResult == GameEntryWaitResult.ErrorDialog)
            {
                dialogRetries++;
                if (errorDialogFailureMessage is not null)
                {
                    _ = await DismissGameEntryErrorDialogAsync(input, cancellationToken);
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, errorDialogFailureMessage);
                }

                if (!await DismissGameEntryErrorDialogAsync(input, cancellationToken)
                    || !await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "A game-entry error dialog appeared, but the menu form could not be restored.");
                }
            }
            else if (waitResult == GameEntryWaitResult.OfflineCharacterScreen)
            {
                if (!await EnsureOnlineCharacterScreenAsync(input, cancellationToken)
                    || !await ClickLobbyDirectAsync(input, cancellationToken)
                    || !await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned to the offline character screen, and the menu form could not be restored after clicking Online.");
                }
            }
            else if (waitResult == GameEntryWaitResult.ReturnedToCharacterScreen)
            {
                if (!await ClickLobbyDirectAsync(input, cancellationToken)
                    || !await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned to character select, but the menu form could not be restored.");
                }
            }
            else if (waitResult == GameEntryWaitResult.ReturnedToMenu
                     && !await restoreFormAsync())
            {
                return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned from game entry, but the menu form could not be restored.");
            }
            else if (waitResult == GameEntryWaitResult.ReturnedToMenu)
            {
                deadline = DateTimeOffset.UtcNow + timeout;
                await ClickMenuEntryButtonAsync(input, button, cancellationToken);
                continue;
            }

            if (DateTimeOffset.UtcNow < deadline)
            {
                await ClickMenuEntryButtonAsync(input, button, cancellationToken);
            }
        }
    }

    private async Task ClickMenuEntryButtonAsync(
        WindowsInput input,
        AgentCommon.UiPoint button,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = TryPrepareD2RForInput(input);
        ClickD2R(input, button);
        await DelayFastMenuAsync(cancellationToken);
    }

    private async Task<bool> DismissGameEntryErrorDialogAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            _ = TryPrepareD2RForInput(input);
            ClickD2R(input, _config.Ui.GameEntryErrorDialogOkButton);
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
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_config.Ui.GameEntryStartTimeoutSeconds, 3, 8));
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = TryPrepareD2RForInput(input);
            if (!IsConnectionInterruptedScreen(input))
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
        await ClickLobbyTabDirectAsync(input, _config.Ui.JoinGameTab, cancellationToken);

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
        await ClickLobbyTabDirectAsync(input, _config.Ui.CreateGameTab, cancellationToken);

        await FillTextFieldAsync(input, _config.Ui.CreateGameNameField, args.GameName ?? "", cancellationToken);
        await FillTextFieldAsync(input, _config.Ui.CreatePasswordField, args.Password ?? "", cancellationToken);
        ClickD2R(input, GetCreateDifficultyPoint(args.Difficulty));
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

    private string FormatEntryTimeoutMessage(
        WindowsInput input,
        AgentCommon.UiPoint activeTab,
        int dialogRetries,
        int connectionRetries)
    {
        var diagnostics = $"{FormatInGameHudDiagnostics(input)} {FormatGameEntryMenuDiagnostics(input, activeTab)}";
        if (dialogRetries == 0 && connectionRetries == 0)
        {
            return $"No game-entry error state was detected. {diagnostics}";
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

        return $"Recovered from {string.Join(" and ", parts)}, but the menu tab stayed visible. {diagnostics}";
    }

    private string FormatInGameHudDiagnostics(WindowsInput input)
    {
        var evidence = SampleInGameHudEvidence(input, windowRelative: true);
        return "HUD samples: "
            + $"ready={IsInGameHudEvidenceReady(evidence)}, "
            + $"health(r={evidence.ModernHealth.RedRatio:N3},b={evidence.ModernHealth.BlueRatio:N3}), "
            + $"mana(r={evidence.ModernMana.RedRatio:N3},b={evidence.ModernMana.BlueRatio:N3}), "
            + $"action(avg={evidence.ActionHud.AverageLuminance:N1},std={evidence.ActionHud.LuminanceStdDev:N1},dark={evidence.ActionHud.DarkRatio:N3}), "
            + $"bottom(std={evidence.BottomHud.LuminanceStdDev:N1},dark={evidence.BottomHud.DarkRatio:N3}), "
            + $"center(std={evidence.CenterHud.LuminanceStdDev:N1},bright={evidence.CenterHud.BrightRatio:N3},grey={evidence.CenterHud.GreyRatio:N3},dark={evidence.CenterHud.DarkRatio:N3}).";
    }

    private string FormatGameEntryMenuDiagnostics(WindowsInput input, AgentCommon.UiPoint activeTab)
    {
        var tab = IsLobbyTabReady(input, activeTab);
        var entry = IsLobbyEntryButtonReady(input);
        var formScreen = IsLobbyFormPanelReady(input, windowRelative: false);
        var formWindow = IsLobbyFormPanelReady(input, windowRelative: true);
        var hudReady = IsInGameReady(input);
        var visible = !hudReady && D2RScreenClassifier.IsGameEntryMenuVisible(tab, entry, formScreen || formWindow);
        return $"Menu samples: visible={visible}, hudReady={hudReady}, tab={tab}, entry={entry}, formScreen={formScreen}, formWindow={formWindow}.";
    }

    private static string FormatGameEntryWaitFailure(GameEntryWaitResult result)
    {
        return result switch
        {
            GameEntryWaitResult.ConnectionInterrupted => "Connection interrupted was detected.",
            GameEntryWaitResult.ErrorDialog => "A game-entry error dialog was detected.",
            GameEntryWaitResult.ReturnedToMenu => "The client returned to the menu instead of entering the game.",
            GameEntryWaitResult.ReturnedToCharacterScreen => "The client returned to character select instead of entering the game.",
            GameEntryWaitResult.OfflineCharacterScreen => "The client returned to the offline character screen instead of entering the game.",
            GameEntryWaitResult.TimedOut => "No in-game HUD/globe state, lobby return, or connection-interrupted state was detected.",
            _ => "The game-entry result was inconclusive."
        };
    }

    private bool IsCharacterScreenReady(WindowsInput input)
    {
        return DetectReadyScreenState(input) == ReadyScreenState.CharacterScreen;
    }

    private bool IsCharacterScreenOffline(WindowsInput input)
    {
        return IsCharacterScreenOffline(input, windowRelative: false)
            || IsCharacterScreenOffline(input, windowRelative: true);
    }

    private bool IsCharacterScreenOffline(WindowsInput input, int sampleGrid)
    {
        return IsCharacterScreenOffline(input, windowRelative: false, sampleGrid)
            || IsCharacterScreenOffline(input, windowRelative: true, sampleGrid);
    }

    private ReadyScreenState DetectReadyScreenState(WindowsInput input, int sampleGrid = MenuSampleGrid)
    {
        // The "Connecting to Battle.net" dialog renders as a modal box layered on top of the
        // same splash background, so the splash logo/prompt regions still pass their own
        // thresholds while this dialog is up - checked only once we already know we're on
        // the splash screen family, so a transient dark/low-contrast cinematic frame elsewhere
        // in the ready window can't be mistaken for it. Treating the dialog as plain
        // DiabloSplash made the ready loop keep firing Escape/Enter/Space at a screen that's
        // actually waiting on a live login handshake - input here doesn't speed anything up
        // and Escape in particular can cancel the connection attempt and bounce back to title,
        // manufacturing a retry loop that looks like a multi-minute hang.
        if (IsDiabloSplashScreen(input, sampleGrid))
        {
            return IsConnectingToBattleNetDialog(input, sampleGrid)
                ? ReadyScreenState.ConnectingToBattleNet
                : ReadyScreenState.DiabloSplash;
        }

        if (IsCharacterScreenOffline(input, sampleGrid: sampleGrid))
        {
            return ReadyScreenState.OfflineCharacterScreen;
        }

        return IsCharacterButtonPairReady(input, windowRelative: false, sampleGrid)
            || IsCharacterButtonPairReady(input, windowRelative: true, sampleGrid)
            || IsCharacterMenuReady(input, windowRelative: false, sampleGrid)
            || IsCharacterMenuReady(input, windowRelative: true, sampleGrid)
            ? ReadyScreenState.CharacterScreen
            : ReadyScreenState.Unknown;
    }

    private ReadyScreenState DetectReadyScreenStateStable(WindowsInput input)
    {
        var state = DetectReadyScreenState(input);
        return state == ReadyScreenState.Unknown
            ? DetectReadyScreenState(input, ReadyStartupSampleGrid)
            : state;
    }

    private static bool IsReadyScreenState(ReadyScreenState state)
    {
        return state is ReadyScreenState.CharacterScreen or ReadyScreenState.OfflineCharacterScreen;
    }

    private bool IsCharacterButtonPairReady(WindowsInput input, bool windowRelative, int sampleGrid = MenuSampleGrid)
    {
        var play = SampleD2RRegion(input, _config.Ui.CharacterPlayButton, widthRatio: 0.13, heightRatio: 0.055, windowRelative: windowRelative, sampleGrid: sampleGrid);
        var lobby = SampleD2RRegion(input, _config.Ui.CharacterLobbyButton, widthRatio: 0.13, heightRatio: 0.055, windowRelative: windowRelative, sampleGrid: sampleGrid);
        return D2RScreenClassifier.IsCharacterButtonRegion(play)
            && D2RScreenClassifier.IsCharacterButtonRegion(lobby)
            && IsOnlineCharacterListReady(input, windowRelative, sampleGrid);
    }

    private bool IsCharacterMenuReady(WindowsInput input, bool windowRelative, int sampleGrid = MenuSampleGrid)
    {
        var logo = SampleD2RRegion(input, new AgentCommon.UiPoint(0.105, 0.170), widthRatio: 0.13, heightRatio: 0.16, windowRelative: windowRelative, sampleGrid: sampleGrid);
        var options = SampleD2RRegion(input, new AgentCommon.UiPoint(0.105, 0.405), widthRatio: 0.13, heightRatio: 0.05, windowRelative: windowRelative, sampleGrid: sampleGrid);
        var cinematics = SampleD2RRegion(input, new AgentCommon.UiPoint(0.105, 0.460), widthRatio: 0.13, heightRatio: 0.05, windowRelative: windowRelative, sampleGrid: sampleGrid);
        return D2RScreenClassifier.IsCharacterMenuReady(logo, options, cinematics);
    }

    private bool IsCharacterScreenOffline(WindowsInput input, bool windowRelative)
    {
        return IsCharacterScreenOffline(input, windowRelative, sampleGrid: MenuSampleGrid);
    }

    private bool IsCharacterScreenOffline(WindowsInput input, bool windowRelative, int sampleGrid)
    {
        if (!IsCharacterMenuReady(input, windowRelative, sampleGrid))
        {
            return false;
        }

        var emptyCharacterPanel = SampleD2RRegion(input, new AgentCommon.UiPoint(0.895, 0.455), widthRatio: 0.17, heightRatio: 0.66, windowRelative: windowRelative, sampleGrid: sampleGrid);
        return D2RScreenClassifier.IsOfflineCharacterPanelRegion(emptyCharacterPanel);
    }

    private bool IsOnlineCharacterListReady(WindowsInput input, bool windowRelative, int sampleGrid = MenuSampleGrid)
    {
        var characterList = SampleD2RRegion(input, new AgentCommon.UiPoint(0.890, 0.455), widthRatio: 0.17, heightRatio: 0.66, windowRelative: windowRelative, sampleGrid: sampleGrid);
        return D2RScreenClassifier.IsOnlineCharacterListRegion(characterList);
    }

    private string FormatCharacterScreenReadyFailure(ReadyWaitResult result, WindowsInput input)
    {
        if (result.ProcessExitedDuringWait)
        {
            return $"D2R process was running, then exited before reaching the character screen (crash or forced close), not stuck input delivery. Last detected ready state: {result.LastState}; ready input bursts sent: {result.Nudges}.{FormatD2RProcessDiscoverySuffix()}";
        }

        return IsD2RRunning()
            ? $"D2R is running, but the character screen was not reached within {result.TimeoutSeconds}s. Last detected ready state: {result.LastState}; ready input bursts sent: {result.Nudges}.{FormatInputDiagnosticsSuffix()}{FormatCharacterScreenClassifierDiagnostics(input)}"
            : $"D2R stopped before the ready loop finished. Last detected ready state: {result.LastState}; ready input bursts sent: {result.Nudges}.";
    }

    // The ready loop's "is this the character screen" check is a handful of pixel-region
    // color-ratio heuristics (D2RScreenClassifier), tuned against specific UI coordinates.
    // When the loop times out, "Unknown" alone doesn't say whether the screen genuinely
    // isn't the character screen or whether one sample region's thresholds just don't match
    // the current UI rendering/resolution. Report the actual computed stats for every region
    // the classifier checks so a failure is diagnosable from the log instead of guessed at.
    private string FormatCharacterScreenClassifierDiagnostics(WindowsInput input)
    {
        try
        {
            var logo = SampleD2RRegion(input, new AgentCommon.UiPoint(0.105, 0.170), widthRatio: 0.13, heightRatio: 0.16, windowRelative: false, sampleGrid: MenuSampleGrid);
            var options = SampleD2RRegion(input, new AgentCommon.UiPoint(0.105, 0.405), widthRatio: 0.13, heightRatio: 0.05, windowRelative: false, sampleGrid: MenuSampleGrid);
            var cinematics = SampleD2RRegion(input, new AgentCommon.UiPoint(0.105, 0.460), widthRatio: 0.13, heightRatio: 0.05, windowRelative: false, sampleGrid: MenuSampleGrid);
            var play = SampleD2RRegion(input, _config.Ui.CharacterPlayButton, widthRatio: 0.13, heightRatio: 0.055, windowRelative: false, sampleGrid: MenuSampleGrid);
            var lobby = SampleD2RRegion(input, _config.Ui.CharacterLobbyButton, widthRatio: 0.13, heightRatio: 0.055, windowRelative: false, sampleGrid: MenuSampleGrid);
            var characterList = SampleD2RRegion(input, new AgentCommon.UiPoint(0.890, 0.455), widthRatio: 0.17, heightRatio: 0.66, windowRelative: false, sampleGrid: MenuSampleGrid);

            static string Fmt(string name, ScreenRegionStats stats) =>
                $"{name}(lum={stats.AverageLuminance:F0},grey={stats.GreyRatio:F2},dark={stats.DarkRatio:F2},orange={stats.OrangeRatio:F2})";

            return " Classifier samples: "
                + string.Join(", ",
                    Fmt("logo", logo),
                    Fmt("options", options),
                    Fmt("cinematics", cinematics),
                    Fmt("play", play),
                    Fmt("lobby", lobby),
                    Fmt("charList", characterList))
                + ".";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private bool IsConnectingToBattleNetDialog(WindowsInput input, int sampleGrid = MenuSampleGrid)
    {
        var dialog = input.SampleRegion(new AgentCommon.UiPoint(0.500, 0.490), widthRatio: 0.30, heightRatio: 0.12, sampleGrid: sampleGrid);
        return D2RScreenClassifier.IsConnectingToBattleNetDialogRegion(dialog);
    }

    private bool IsDiabloSplashScreen(WindowsInput input, int sampleGrid = MenuSampleGrid)
    {
        var logo = input.SampleRegion(new AgentCommon.UiPoint(0.500, 0.290), widthRatio: 0.45, heightRatio: 0.22, sampleGrid: sampleGrid);
        var prompt = input.SampleRegion(new AgentCommon.UiPoint(0.500, 0.600), widthRatio: 0.32, heightRatio: 0.055, sampleGrid: sampleGrid);
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

    private bool IsLobbyTabReady(WindowsInput input, AgentCommon.UiPoint tab)
    {
        return IsLobbyTabReady(input, tab, windowRelative: false)
            || IsLobbyTabReady(input, tab, windowRelative: true);
    }

    private bool IsLobbyTabReady(WindowsInput input, AgentCommon.UiPoint tab, bool windowRelative)
    {
        var stats = SampleD2RRegion(input, tab, widthRatio: 0.10, heightRatio: 0.045, windowRelative: windowRelative);
        return D2RScreenClassifier.IsLobbyTabReady(
            stats,
            IsCharacterButtonPairReady(input, windowRelative),
            IsCharacterMenuReady(input, windowRelative));
    }

    private bool IsLobbyEntryButtonReady(WindowsInput input)
    {
        return IsLobbyEntryButtonReady(input, windowRelative: false)
            || IsLobbyEntryButtonReady(input, windowRelative: true);
    }

    private bool IsLobbyEntryButtonReady(WindowsInput input, bool windowRelative)
    {
        var stats = SampleD2RRegion(input, _config.Ui.CreateGameButton, widthRatio: 0.16, heightRatio: 0.055, windowRelative: windowRelative);
        return stats.AverageLuminance > 30
            && stats.AverageLuminance < 90
            && stats.GreyRatio > 0.30
            && stats.DarkRatio > 0.25
            && stats.DarkRatio < 0.70;
    }

    private bool IsLobbyFormPanelReady(WindowsInput input, bool windowRelative)
    {
        var stats = SampleD2RRegion(input, new AgentCommon.UiPoint(0.765, 0.365), widthRatio: 0.30, heightRatio: 0.42, windowRelative: windowRelative);
        return stats.AverageLuminance < 30
            && stats.GreyRatio < 0.25
            && stats.DarkRatio > 0.80;
    }

    private bool IsFriendJoinGameOptionReady(WindowsInput input)
    {
        return IsFriendJoinGameOptionReady(input, windowRelative: false)
            || IsFriendJoinGameOptionReady(input, windowRelative: true);
    }

    private bool IsFriendJoinGameOptionReady(WindowsInput input, bool windowRelative)
    {
        var stats = SampleD2RRegion(input, _config.Ui.FriendContextJoinGame, widthRatio: 0.12, heightRatio: 0.040, windowRelative: windowRelative);
        return stats.AverageLuminance > 36
            && stats.GreyRatio > 0.56
            && stats.DarkRatio < 0.44;
    }

    private bool IsInGameReady(WindowsInput input)
    {
        return IsInGameReady(input, windowRelative: false)
            || IsInGameReady(input, windowRelative: true);
    }

    private bool IsInGameReady(WindowsInput input, bool windowRelative)
    {
        return IsInGameHudEvidenceReady(SampleInGameHudEvidence(input, windowRelative));
    }

    private InGameHudEvidence SampleInGameHudEvidence(WindowsInput input, bool windowRelative)
    {
        var actionHud = SampleD2RRegion(input, _config.Ui.InGameHudBar, widthRatio: 0.42, heightRatio: 0.08, windowRelative: windowRelative);
        var modernHealth = SampleD2RRegion(input, _config.Ui.ModernHealthGlobe, widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
        var modernMana = SampleD2RRegion(input, _config.Ui.ModernManaGlobe, widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
        var legacyHealth = SampleD2RRegion(input, _config.Ui.LegacyHealthGlobe, widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
        var legacyMana = SampleD2RRegion(input, _config.Ui.LegacyManaGlobe, widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
        var bottomHud = SampleD2RRegion(input, new AgentCommon.UiPoint(0.500, 0.940), widthRatio: 0.70, heightRatio: 0.13, windowRelative: windowRelative);
        var centerHud = SampleD2RRegion(input, new AgentCommon.UiPoint(0.500, 0.940), widthRatio: 0.22, heightRatio: 0.08, windowRelative: windowRelative);
        return new InGameHudEvidence(modernHealth, modernMana, legacyHealth, legacyMana, actionHud, bottomHud, centerHud);
    }

    private static bool IsInGameHudEvidenceReady(InGameHudEvidence evidence)
    {
        if (D2RScreenClassifier.IsInGameHudProfile(evidence.ModernHealth, evidence.ModernMana, evidence.ActionHud, healthRedThreshold: 0.20, manaBlueThreshold: 0.18))
        {
            return true;
        }

        if (D2RScreenClassifier.IsInGameHudProfile(evidence.LegacyHealth, evidence.LegacyMana, evidence.ActionHud, healthRedThreshold: 0.20, manaBlueThreshold: 0.18))
        {
            return true;
        }

        return D2RScreenClassifier.IsInGameHudFrame(evidence.ActionHud, evidence.BottomHud, evidence.CenterHud);
    }

    private ScreenRegionStats SampleD2RRegion(
        WindowsInput input,
        AgentCommon.UiPoint center,
        double widthRatio,
        double heightRatio,
        bool windowRelative,
        int sampleGrid = MenuSampleGrid)
    {
        return windowRelative
            ? input.SampleRegion(center, widthRatio, heightRatio, coordinateProcessNames: GetD2RProcessNames(), sampleGrid: sampleGrid)
            : input.SampleRegion(center, widthRatio, heightRatio, sampleGrid: sampleGrid);
    }

    private async Task SelectCharacterAsync(
        WindowsInput input,
        int? characterSlot,
        CancellationToken cancellationToken)
    {
        ClickD2R(input, GetCharacterSlotPoint(characterSlot));
        await DelayFastMenuAsync(cancellationToken);
    }

    private async Task FillTextFieldAsync(
        WindowsInput input,
        AgentCommon.UiPoint point,
        string value,
        CancellationToken cancellationToken)
    {
        ClickD2R(input, point);
        await DelayFastMenuAsync(cancellationToken);
        ClickD2R(input, point);
        await DelayFastMenuAsync(cancellationToken);
        input.SelectAll();
        await DelayFastMenuAsync(cancellationToken);
        input.TypeText(value);
        await DelayFastMenuAsync(cancellationToken);
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

        ClickD2R(input, _config.Ui.JoinDifficultyDropdown);
        await DelayFastMenuAsync(cancellationToken);
        ClickD2R(input, GetJoinDifficultyPoint(difficulty));
        await DelayFastMenuAsync(cancellationToken);
    }

    private async Task<GameEntryWaitResult> WaitForGameEntryAsync(WindowsInput input, CancellationToken cancellationToken)
    {
        return await WaitForGameEntryAsync(input, returnTab: null, cancellationToken);
    }

    private async Task<GameEntryWaitResult> WaitForGameEntryAsync(
        WindowsInput input,
        AgentCommon.UiPoint? returnTab,
        CancellationToken cancellationToken)
    {
        var delaySeconds = Math.Max(
            Math.Max(_config.Ui.GameEntryStartTimeoutSeconds, 1),
            Math.Max(_config.Ui.GameLoadSeconds, 1));
        if (_config.Ui.ToggleLegacyGraphicsAfterEnteringGame)
        {
            delaySeconds = Math.Max(delaySeconds, Math.Max(_config.Ui.LegacyGraphicsToggleDelaySeconds, 1));
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(delaySeconds);
        var returnDetectionAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Clamp(_config.Ui.GameLoadSeconds, 2, 5));
        var presumedEntryAfter = TimeSpan.FromSeconds(Math.Clamp(_config.Ui.GameLoadSeconds, 4, 10));
        DateTimeOffset? menuAbsentSince = null;
        var sawConnectionInterrupted = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = TryPrepareD2RForInput(input);
            var canDetectReturn = returnTab is not null && DateTimeOffset.UtcNow >= returnDetectionAt;

            if (await TryConfirmEnteredGameAsync(input, cancellationToken))
            {
                return GameEntryWaitResult.EnteredGame;
            }

            if (IsConnectionInterruptedScreen(input))
            {
                sawConnectionInterrupted = true;
            }
            else if (IsGameEntryErrorDialogOpen(input))
            {
                return GameEntryWaitResult.ErrorDialog;
            }
            else if (IsCharacterScreenOffline(input))
            {
                return GameEntryWaitResult.OfflineCharacterScreen;
            }
            else if (canDetectReturn)
            {
                if (IsCharacterScreenReady(input))
                {
                    return GameEntryWaitResult.ReturnedToCharacterScreen;
                }

                if (IsGameEntryMenuStillVisible(input, returnTab!))
                {
                    return sawConnectionInterrupted
                        ? GameEntryWaitResult.ConnectionInterrupted
                        : GameEntryWaitResult.ReturnedToMenu;
                }

                menuAbsentSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - menuAbsentSince >= presumedEntryAfter)
                {
                    await ToggleLegacyGraphicsAfterEntryAsync(input, cancellationToken);
                    return GameEntryWaitResult.EnteredGame;
                }
            }

            var remainingMs = Math.Max((deadline - DateTimeOffset.UtcNow).TotalMilliseconds, 0);
            if (remainingMs == 0)
            {
                break;
            }

            await Task.Delay((int)Math.Min(EntryPollIntervalMs, remainingMs), cancellationToken);
        }

        if (await TryConfirmEnteredGameAsync(input, cancellationToken))
        {
            return GameEntryWaitResult.EnteredGame;
        }

        if (sawConnectionInterrupted)
        {
            return GameEntryWaitResult.ConnectionInterrupted;
        }

        if (IsGameEntryErrorDialogOpen(input))
        {
            return GameEntryWaitResult.ErrorDialog;
        }

        if (returnTab is not null && IsGameEntryMenuStillVisible(input, returnTab))
        {
            return GameEntryWaitResult.ReturnedToMenu;
        }

        if (IsCharacterScreenOffline(input))
        {
            return GameEntryWaitResult.OfflineCharacterScreen;
        }

        if (returnTab is not null && IsCharacterScreenReady(input))
        {
            return GameEntryWaitResult.ReturnedToCharacterScreen;
        }

        return GameEntryWaitResult.TimedOut;
    }

    private bool IsGameEntryMenuStillVisible(WindowsInput input, AgentCommon.UiPoint returnTab)
    {
        if (IsInGameReady(input))
        {
            return false;
        }

        var tab = IsLobbyTabReady(input, returnTab);
        var entry = IsLobbyEntryButtonReady(input);
        var formScreen = IsLobbyFormPanelReady(input, windowRelative: false);
        var formWindow = IsLobbyFormPanelReady(input, windowRelative: true);
        return D2RScreenClassifier.IsGameEntryMenuVisible(tab, entry, formScreen || formWindow);
    }

    private bool IsAnyLobbyEntryMenuVisible(WindowsInput input)
    {
        if (IsInGameReady(input) || IsCharacterScreenReady(input) || IsCharacterScreenOffline(input))
        {
            return false;
        }

        var createTab = IsLobbyTabReady(input, _config.Ui.CreateGameTab);
        var joinTab = IsLobbyTabReady(input, _config.Ui.JoinGameTab);
        var entry = IsLobbyEntryButtonReady(input);
        var formScreen = IsLobbyFormPanelReady(input, windowRelative: false);
        var formWindow = IsLobbyFormPanelReady(input, windowRelative: true);
        return D2RScreenClassifier.IsGameEntryMenuVisible(createTab || joinTab, entry, formScreen || formWindow);
    }

    private async Task<bool> TryConfirmEnteredGameAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        if (!IsInGameReady(input))
        {
            return false;
        }

        await ToggleLegacyGraphicsAfterEntryAsync(input, cancellationToken);
        return true;
    }

    private async Task ToggleLegacyGraphicsAfterEntryAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        if (!_config.Ui.ToggleLegacyGraphicsAfterEnteringGame)
        {
            return;
        }

        _ = TryPrepareD2RForInput(input);
        await DelayFastMenuAsync(cancellationToken);
        input.PressStartupSkipKey();
        _ = input.SendWindowLegacyGraphicsToggle(GetD2RProcessNames());
        await DelayFastMenuAsync(cancellationToken);
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

    private Task DelayFastMenuAsync(CancellationToken cancellationToken)
    {
        return Task.Delay(Math.Clamp(_config.Ui.StepDelayMs, 50, FastMenuDelayMs), cancellationToken);
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

    private int GetD2RStartTimeoutSeconds()
    {
        return Math.Clamp(_config.D2RStartTimeoutSeconds, 1, MaxD2RStartTimeoutSeconds);
    }

    private int GetBattleNetExecRetryDelaySeconds()
    {
        return Math.Clamp(_config.BattleNetExecRetryDelaySeconds, 1, 8);
    }

    private int GetLaunchGraceSeconds()
    {
        return Math.Clamp(_config.LaunchGraceSeconds, 1, 5);
    }

    private int GetReadyStartupSkipSeconds()
    {
        return Math.Clamp(_config.Ui.ReadyStartupSkipSeconds, 1, MaxReadyStartupSkipSeconds);
    }

    private int GetReadyLoopTimeoutSeconds()
    {
        return Math.Max(GetReadyStartupSkipSeconds(), GetCharacterScreenReconnectSeconds());
    }

    private int GetCharacterScreenReconnectSeconds()
    {
        return Math.Clamp(_config.Ui.CharacterScreenReadyTimeoutSeconds, 1, MaxCharacterScreenReconnectSeconds);
    }

    private int GetJoinPrepareTimeoutSeconds()
    {
        return Math.Clamp(_config.Ui.LobbyReadyTimeoutSeconds, 8, MaxJoinPrepareSeconds);
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

    private async Task<D2RStartWaitResult> WaitForD2RProcessStartedAsync(
        WindowsInput input,
        CancellationToken cancellationToken,
        int? timeoutSeconds = null)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds ?? GetD2RStartTimeoutSeconds());
        var deadline = DateTimeOffset.UtcNow + timeout;
        var launchRetryDelay = TimeSpan.FromSeconds(GetBattleNetExecRetryDelaySeconds());
        var playClickRetryDelay = TimeSpan.FromSeconds(1.5);
        var nextLaunchRetryAt = DateTimeOffset.UtcNow;
        var nextPlayClickAt = DateTimeOffset.UtcNow;
        var launchAttempts = 0;
        var playClicks = 0;
        var lastLaunchMessage = "(none)";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsD2RRunning())
            {
                return new D2RStartWaitResult(true, launchAttempts, playClicks, lastLaunchMessage);
            }

            var visibleState = DetectVisibleD2RState(input);
            if (visibleState is not (VisibleD2RState.NotRunning or VisibleD2RState.Unknown))
            {
                return new D2RStartWaitResult(true, launchAttempts, playClicks, lastLaunchMessage);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return new D2RStartWaitResult(false, launchAttempts, playClicks, lastLaunchMessage);
            }

            if (DateTimeOffset.UtcNow >= nextLaunchRetryAt)
            {
                var launch = TrySendD2RLaunchCommand();
                launchAttempts++;
                lastLaunchMessage = launch.Message;
                nextLaunchRetryAt = DateTimeOffset.UtcNow + launchRetryDelay;
            }

            if (DateTimeOffset.UtcNow >= nextPlayClickAt)
            {
                if (TryClickBattleNetPlay(input))
                {
                    playClicks++;
                }

                nextPlayClickAt = DateTimeOffset.UtcNow + playClickRetryDelay;
            }

            await Task.Delay(250, cancellationToken);
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

    private bool TryClickBattleNetPlay(WindowsInput input, bool requireButtonReady = false)
    {
        if (!_config.Ui.ClickBattleNetPlayWhenNeeded
            || !IsBattleNetRunning())
        {
            return false;
        }

        var battleNetNames = GetBattleNetProcessNames();
        try
        {
            var focused = input.TryFocusProcess(battleNetNames);
            _ = TryDismissBattleNetWhatsNewPopup(input);

            if (requireButtonReady && focused && !IsBattleNetPlayButtonReady(input))
            {
                return false;
            }

            if (focused)
            {
                input.LeftClick(_config.Ui.BattleNetPlayButton, battleNetNames);
            }

            _ = input.SendWindowClick(_config.Ui.BattleNetPlayButton, battleNetNames, MouseButton.Left);
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

        var battleNetNames = GetBattleNetProcessNames();
        input.LeftClick(_config.Ui.BattleNetWhatsNewCloseButton, battleNetNames);
        _ = input.SendWindowClick(_config.Ui.BattleNetWhatsNewCloseButton, battleNetNames, MouseButton.Left);
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

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return CommandResult.Failure($"Failed to start {path}: {ex.Message}");
        }

        // UseShellExecute can hand off to the shell without ever throwing - e.g. it
        // silently shows an error dialog, or routes to an existing single-instance
        // window - so a clean return here does not prove anything actually launched.
        // Confirm a live PID directly instead of trusting the absence of an exception.
        var target = isProtocolLaunch ? path : Path.GetFileName(path);
        if (process is null)
        {
            return CommandResult.Success($"Started {target} (no process handle was returned by the shell).");
        }

        Thread.Sleep(500);
        if (process.HasExited)
        {
            return CommandResult.Success(
                $"Started {target}, but pid {process.Id} exited within 500ms (exit code {process.ExitCode}).");
        }

        return CommandResult.Success($"Started {target} (pid {process.Id} confirmed running).");
    }

    private bool IsBattleNetRunning(DesktopWindowScanCache? cache = null)
    {
        return IsAnyProcessRunning(GetBattleNetProcessNames(), cache);
    }

    private bool IsD2RRunning(DesktopWindowScanCache? cache = null)
    {
        return IsAnyProcessRunning(GetD2RProcessNames(), cache);
    }

    private bool IsD2RNamedProcessRunning()
    {
        return WindowsProcessFinder.IsAnyNamedProcessRunning(GetD2RProcessNames());
    }

    private DateTimeOffset? TryGetD2RProcessStartUtc()
    {
        return FindProcessesByNameOrWindowTitle(GetD2RProcessNames())
            .Select(TryGetProcessStartUtc)
            .Where(started => started.HasValue)
            .OrderBy(started => started!.Value)
            .FirstOrDefault();
    }

    private string[] GetBattleNetProcessNames()
    {
        return WindowsProcessIdentity.GetConfiguredProcessNames(_config.BattleNetProcessName, _config.BattleNetProcessNames);
    }

    private string[] GetD2RProcessNames()
    {
        return WindowsProcessIdentity.GetD2RProcessNames(_config.D2RProcessName, _config.D2RProcessNames);
    }

    private static CommandResult KillProcesses(IEnumerable<string> processNames)
    {
        var names = WindowsProcessIdentity.NormalizeProcessNames(processNames);
        var processes = FindProcessesByNameOrWindowTitle(names)
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

    private static bool IsAnyProcessRunning(IEnumerable<string> processNames, DesktopWindowScanCache? cache = null)
    {
        return WindowsProcessFinder.IsAnyProcessRunning(processNames, cache);
    }

    private static IEnumerable<Process> FindProcessesByNameOrWindowTitle(IEnumerable<string> processNames)
    {
        return WindowsProcessFinder.FindProcessesByNameOrWindowTitle(processNames);
    }

    private static string SafeGetMainWindowTitle(Process process)
    {
        return WindowsProcessFinder.SafeGetMainWindowTitle(process);
    }

    private static DateTimeOffset? TryGetProcessStartUtc(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime).ToUniversalTime();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string FormatProcessNames(IEnumerable<string> processNames)
    {
        var names = WindowsProcessIdentity.NormalizeProcessNames(processNames);
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

    private enum VisibleD2RState
    {
        NotRunning,
        Unknown,
        DiabloSplash,
        CharacterScreen,
        OfflineCharacterScreen,
        LobbyOrGame,
        InGame
    }

    private enum ReadyScreenState
    {
        Unknown,
        DiabloSplash,
        ConnectingToBattleNet,
        OfflineCharacterScreen,
        CharacterScreen
    }

    private enum GameEntryWaitResult
    {
        EnteredGame,
        ConnectionInterrupted,
        ErrorDialog,
        ReturnedToMenu,
        ReturnedToCharacterScreen,
        OfflineCharacterScreen,
        TimedOut
    }

    private sealed record GameEntryAttemptResult(
        bool Entered,
        int DialogRetries,
        int ConnectionRetries,
        string Message);

    private sealed record ReadyWaitResult(
        bool Ready,
        int Nudges,
        ReadyScreenState LastState,
        int TimeoutSeconds,
        bool ProcessExitedDuringWait = false);

    private sealed record D2RStartWaitResult(
        bool Started,
        int LaunchAttempts,
        int PlayClicks,
        string LastLaunchMessage);

    private sealed record ActivitySnapshot(
        D2RActivityState State,
        DateTimeOffset? CharacterScreenIdleSinceUtc,
        DateTimeOffset? LastLobbyOrGameInteractionUtc,
        string? Reason);

    private sealed record InGameHudEvidence(
        ScreenRegionStats ModernHealth,
        ScreenRegionStats ModernMana,
        ScreenRegionStats LegacyHealth,
        ScreenRegionStats LegacyMana,
        ScreenRegionStats ActionHud,
        ScreenRegionStats BottomHud,
        ScreenRegionStats CenterHud);

    private sealed record LastInputActionSnapshot(
        DateTimeOffset TimeUtc,
        string Kind,
        string Button,
        double UiX,
        double UiY,
        int ScreenX,
        int ScreenY,
        CursorPosition? CursorBefore,
        CursorPosition? CursorAfter,
        [property: JsonPropertyName("d2rForegroundBefore")] bool? D2RForegroundBefore,
        [property: JsonPropertyName("d2rForegroundAfter")] bool? D2RForegroundAfter,
        string? ForegroundProcessBefore,
        string? ForegroundProcessAfter);
}
