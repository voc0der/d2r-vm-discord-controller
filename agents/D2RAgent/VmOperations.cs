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
    private const int ReadyStartupDetectionIntervalMs = 250;
    private const int ReadyStartupWindowRelativeDetectionIntervalMs = 1000;
    private const int ReadyStartupProcessCheckIntervalMs = 1000;
    private const int ReadyStartupSampleGrid = 5;
    private const int MenuSampleGrid = 9;
    private const int FastMenuDelayMs = 150;
    private const int EntryPollIntervalMs = 200;
    private const int LobbyPollIntervalMs = 250;
    // ComputeVisibleStateClassifierBreakdown/ComputeReadyScreenClassifierBreakdown are ~25-35
    // unbounded GDI region samples, purely for diagnostic display - bounding them can only
    // shorten or blank a diagnostic string, never change a pass/fail decision.
    private const int ClassifierBreakdownBoundMs = 2000;
    private const int StatusCollectionTimeoutSeconds = 4;
    // watch-xogij6-20260625-164231.log showed entry confirmation stuck in one HUD sample
    // for 36-48s during D2R's load spike. Bound each expensive HUD probe so the entry loop
    // keeps polling; throttle fresh probes so the 200ms poll loop does not pile up bounded
    // Task.Run work while Windows/GDI is still unwinding the previous sample.
    private const int InGameHudSampleBoundMs = 1000;
    private const int InGameHudSampleThrottleMs = 1000;
    // These sibling entry-loop checks all use the same pixel-sampling path, so bound them at
    // their definitions instead of trying to guard every current and future call site.
    private const int EntryLoopCheckBoundMs = 1500;
    private const double FollowFingerprintMaxAverageDifference = 18.0;
    private const double FollowFingerprintMaxSignalAverageDifference = 42.0;
    private const double FollowFingerprintMinSignalSeparation = 6.0;
    private const int FollowFingerprintMinSignalPixels = 3;
    // Each slot is one small region capture - cheap relative to the in-game HUD checks above,
    // but there are up to 8 of them per tick, so still bound each individually rather than
    // relying on the tick interval alone to cap worst-case cost.
    private const int PartyFrameSampleBoundMs = 800;

    private readonly VmAgentConfig _config;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private readonly object _activityLock = new();
    private readonly string[] _restartArgs;
    private D2RActivityState _activityState = D2RActivityState.Unknown;
    private DateTimeOffset? _characterScreenIdleSinceUtc;
    private DateTimeOffset? _lastLobbyOrGameInteractionUtc;
    private DateTimeOffset? _lastObservedD2RStartUtc;
    private string? _lastActivityReason;
    private LastInputActionSnapshot? _lastInputAction;
    private string? _lastObservedFrame;
    private DateTimeOffset? _lastObservedFrameUtc;
    private string? _lastClassifierBreakdown;
    private DateTimeOffset? _lastClassifierBreakdownUtc;
    private string? _lastHudEvidence;
    private DateTimeOffset? _lastHudEvidenceUtc;
    private int? _lastPartyMemberCount;
    private DateTimeOffset? _lastPartyMemberCountUtc;
    private string? _lastCommandCheckpoint;
    private DateTimeOffset? _lastCommandCheckpointUtc;
    private DateTimeOffset? _detailedStatusBackoffUntilUtc;
    private DateTimeOffset _nextInGameHudSampleAt = DateTimeOffset.MinValue;
    private bool _lastInGameHudResult;

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
        return CollectStatusAsync(cancellationToken);
    }

    private async Task<object> CollectStatusAsync(CancellationToken cancellationToken)
    {
        if (_commandGate.CurrentCount == 0)
        {
            return CollectProcessOnlyStatus(
                "UI command is active; using process-only status so diagnostics cannot starve menu input.",
                cancellationToken);
        }

        if (_detailedStatusBackoffUntilUtc is { } backoffUntil
            && DateTimeOffset.UtcNow < backoffUntil)
        {
            return CollectProcessOnlyStatus(
                "Detailed status collection recently exceeded its budget; using process-only fallback.",
                cancellationToken);
        }

        if (!_statusGate.Wait(0))
        {
            return CollectProcessOnlyStatus(
                "Detailed status collection is still running; using process-only fallback.",
                cancellationToken);
        }

        var statusTask = Task.Run(() => CollectDetailedStatus(cancellationToken), cancellationToken);
        var completed = await Task.WhenAny(
            statusTask,
            Task.Delay(TimeSpan.FromSeconds(StatusCollectionTimeoutSeconds), cancellationToken));
        if (completed == statusTask)
        {
            try
            {
                return await statusTask;
            }
            finally
            {
                _detailedStatusBackoffUntilUtc = null;
                _statusGate.Release();
            }
        }

        _detailedStatusBackoffUntilUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        _ = statusTask.ContinueWith(
            task =>
            {
                _ = task.Exception;
                _statusGate.Release();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return CollectProcessOnlyStatus(
            $"Detailed status collection did not return within {StatusCollectionTimeoutSeconds}s; using process-only fallback.",
            cancellationToken);
    }

    private object CollectDetailedStatus(CancellationToken cancellationToken)
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

        return new
        {
            hostName = Environment.MachineName,
            userName = Environment.UserName,
            statusMode = "detailed",
            statusDegraded = false,
            statusError = (string?)null,
            battleNetRunning,
            d2rRunning,
            d2rVisibleState = visibleState.ToString(),
            d2rProcessDiscovery = OperatingSystem.IsWindows() ? WindowsProcessFinder.Discover(GetD2RProcessNames(), windowScanCache) : null,
            // Gating this on d2rRunning blacked out the one field (foregroundProcessName) that
            // would show what's actually focused/visible when process-name matching itself is
            // what's failing - exactly the case where this is most needed.
            d2rInput = OperatingSystem.IsWindows() ? TryGetD2RInputDiagnostics(windowScanCache) : null,
            lastInputAction = _lastInputAction,
            lastObservedFrame = _lastObservedFrame,
            lastObservedFrameUtc = _lastObservedFrameUtc,
            lastClassifierBreakdown = _lastClassifierBreakdown,
            lastClassifierBreakdownUtc = _lastClassifierBreakdownUtc,
            lastHudEvidence = _lastHudEvidence,
            lastHudEvidenceUtc = _lastHudEvidenceUtc,
            lastPartyMemberCount = _lastPartyMemberCount,
            lastPartyMemberCountUtc = _lastPartyMemberCountUtc,
            threadPoolThreads = System.Threading.ThreadPool.ThreadCount,
            threadPoolPending = System.Threading.ThreadPool.PendingWorkItemCount,
            lastCommandCheckpoint = _lastCommandCheckpoint,
            lastCommandCheckpointUtc = _lastCommandCheckpointUtc,
            d2rActivityState = activity.State.ToString(),
            characterScreenIdleSinceUtc = activity.CharacterScreenIdleSinceUtc,
            lastLobbyOrGameInteractionUtc = activity.LastLobbyOrGameInteractionUtc,
            lastActivityReason = activity.Reason,
            idleQuitEnabled = _config.IdleQuitEnabled,
            idleQuitMinutes = _config.IdleQuitMinutes,
            timeUtc = DateTimeOffset.UtcNow
        };
    }

    private object CollectProcessOnlyStatus(string reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var battleNetRunning = OperatingSystem.IsWindows()
            && WindowsProcessFinder.IsAnyNamedProcessRunning(GetBattleNetProcessNames());
        var d2rRunning = OperatingSystem.IsWindows()
            && WindowsProcessFinder.IsAnyNamedProcessRunning(GetD2RProcessNames());
        RefreshD2RProcessActivity(d2rRunning);
        var visibleState = GetBestProcessOnlyVisibleState(d2rRunning);
        var activity = DetectVisibleActivitySnapshot(d2rRunning, visibleState);

        return new
        {
            hostName = Environment.MachineName,
            userName = Environment.UserName,
            statusMode = "processOnly",
            statusDegraded = true,
            statusError = reason,
            battleNetRunning,
            d2rRunning,
            d2rVisibleState = visibleState.ToString(),
            d2rProcessDiscovery = new ProcessDiscoverySnapshot(GetD2RProcessNames(), [], []),
            d2rInput = (InputDiagnostics?)null,
            lastInputAction = _lastInputAction,
            lastObservedFrame = _lastObservedFrame,
            lastObservedFrameUtc = _lastObservedFrameUtc,
            lastClassifierBreakdown = _lastClassifierBreakdown,
            lastClassifierBreakdownUtc = _lastClassifierBreakdownUtc,
            lastHudEvidence = _lastHudEvidence,
            lastHudEvidenceUtc = _lastHudEvidenceUtc,
            lastPartyMemberCount = _lastPartyMemberCount,
            lastPartyMemberCountUtc = _lastPartyMemberCountUtc,
            threadPoolThreads = System.Threading.ThreadPool.ThreadCount,
            threadPoolPending = System.Threading.ThreadPool.PendingWorkItemCount,
            lastCommandCheckpoint = _lastCommandCheckpoint,
            lastCommandCheckpointUtc = _lastCommandCheckpointUtc,
            d2rActivityState = activity.State.ToString(),
            characterScreenIdleSinceUtc = activity.CharacterScreenIdleSinceUtc,
            lastLobbyOrGameInteractionUtc = activity.LastLobbyOrGameInteractionUtc,
            lastActivityReason = activity.Reason,
            idleQuitEnabled = _config.IdleQuitEnabled,
            idleQuitMinutes = _config.IdleQuitMinutes,
            timeUtc = DateTimeOffset.UtcNow
        };
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

        // status only reads process/window state - it never sends input - so it must not
        // wait on _commandGate either, for the exact same reason GetStatusAsync (the
        // heartbeat path) already bypasses it: a long-running menu_ready/menu_create_game
        // command can hold the gate for minutes, and a live on-demand status check queued
        // behind it would just time out instead of reporting what's actually happening.
        if (string.Equals(request.Command, "status", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Success("Status collected.", await CollectStatusAsync(cancellationToken));
        }

        // follow_set_template/follow_clear_template are pure local file writes, no D2R
        // interaction - same reasoning as screenshot/status above, so binding/unbinding from the
        // Host doesn't queue behind whatever long-running menu command this agent is mid-way
        // through.
        if (string.Equals(request.Command, "follow_set_template", StringComparison.OrdinalIgnoreCase))
        {
            return FollowSetTemplate(MenuCommandArgs.From(request.Args));
        }

        if (string.Equals(request.Command, "follow_clear_template", StringComparison.OrdinalIgnoreCase))
        {
            return FollowClearTemplate();
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
            "menu_follow_bind" => await FollowBindCaptureAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_follow_auto_check" => await FollowAutoCheckAsync(MenuCommandArgs.From(request.Args), cancellationToken),
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

    private async Task<CommandResult> LaunchD2RAsync(CancellationToken cancellationToken, bool quickForReady = false)
    {
        if (IsD2RNamedProcessRunning())
        {
            RefreshD2RProcessActivity(d2rRunning: true);
            if (quickForReady)
            {
                return CommandResult.Success("D2R is already running.");
            }

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

        if (quickForReady)
        {
            return CommandResult.Success(
                usedBattleNetExec
                    ? "Initial Battle.net D2R launch command sent; ready loop will keep nudging launch/Play while skipping startup screens."
                    : "Initial D2R launch command sent; ready loop will start skipping startup screens immediately.");
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

        // The cached state above only changes when an automated command explicitly transitions
        // it (MarkLobbyOrGameInteraction etc.) - a join-all attempt that fails its own entry
        // check and returns without marking, then the client recovers or gets joined some other
        // way, leaves this stuck on CharacterScreenIdle with the original timestamp forever, even
        // though the client is actually in a game. /d2r status already re-derives a live snapshot
        // (DetectVisibleActivitySnapshot) rather than trusting this cache for display; the actual
        // quit decision needs that same live look before doing something irreversible (issue #20,
        // item 1).
        var visibleState = DetectVisibleD2RState(d2rRunning: true);
        var liveActivity = DetectVisibleActivitySnapshot(d2rRunning: true, visibleState);
        if (liveActivity.State != D2RActivityState.CharacterScreenIdle)
        {
            ReconcileActivityFromLiveSnapshot(liveActivity);
            log($"Skipped idle quit: cached state said character-screen-idle for {idleFor.TotalMinutes:N0}m, but a live check now shows {liveActivity.State}.");
            return;
        }

        log($"D2R has been idle at the character screen for {idleFor.TotalMinutes:N0} minute(s); sending Alt+F4.");
        var result = await QuitD2RAsync(cancellationToken);
        if (!result.Ok)
        {
            log($"Idle quit failed: {result.Message}");
        }
    }

    internal void ReconcileActivityFromLiveSnapshot(ActivitySnapshot liveActivity)
    {
        lock (_activityLock)
        {
            _activityState = liveActivity.State;
            _characterScreenIdleSinceUtc = liveActivity.CharacterScreenIdleSinceUtc;
            _lastLobbyOrGameInteractionUtc = liveActivity.LastLobbyOrGameInteractionUtc;
            _lastActivityReason = liveActivity.Reason;
        }
    }

    // issue #20, item 6. Runs alongside RunIdleMonitorAsync on its own configurable interval
    // (default 30s, distinct from the 60s idle-quit check and the agent-to-host HeartbeatSeconds)
    // rather than piggybacking on either - this is sampling the game screen for a feature
    // (join-auto's "did someone leave" signal), not a liveness/safety check, so it should stay
    // independently tunable and disable-able without touching either of those.
    public async Task RunPartyMemberMonitorAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(_config.PartyMemberCountIntervalSeconds, 5));
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval, cancellationToken);
            try
            {
                await _commandGate.WaitAsync(cancellationToken);
                try
                {
                    SamplePartyMemberCount();
                }
                finally
                {
                    _commandGate.Release();
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                log?.Invoke($"Party member monitor failed: {ex.Message}");
            }
        }
    }

    private void SamplePartyMemberCount()
    {
        if (!_config.PartyMemberCountEnabled || !OperatingSystem.IsWindows() || !IsD2RRunning())
        {
            return;
        }

        // The party portrait row is only meaningful in an actual game - sampling it from the
        // lobby/join-create form would just read whatever happens to be in that screen corner.
        var input = new WindowsInput();
        if (DetectVisibleD2RState(input) != VisibleD2RState.InGame)
        {
            return;
        }

        _lastPartyMemberCount = CountOtherPartyMembers(input);
        _lastPartyMemberCountUtc = DateTimeOffset.UtcNow;
    }

    // Scans slots in order and stops at the first miss rather than checking all 8 unconditionally
    // - D2R fills slots left-to-right with no gaps (PartyMemberSlots), so the common case (a
    // handful of accounts, not a full 8-player lobby) samples only as many regions as there are
    // actual members instead of always paying for 8.
    private int CountOtherPartyMembers(WindowsInput input)
    {
        for (var slot = 1; slot <= PartyMemberSlots.MaxSlots; slot++)
        {
            var ratio = TryRunBounded(
                () => input.SamplePartyFrameRatio(
                    PartyMemberSlots.GetSlotTopEdgeCenter(slot),
                    PartyMemberSlots.EdgeWidthRatio,
                    PartyMemberSlots.EdgeHeightRatio),
                PartyFrameSampleBoundMs,
                0.0);
            if (ratio < PartyMemberSlots.FrameRatioThreshold)
            {
                return slot - 1;
            }
        }

        return PartyMemberSlots.MaxSlots;
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
        var launch = BeginD2RReadyLaunch();
        if (!launch.Ok)
        {
            return launch;
        }

        var input = new WindowsInput();
        var ready = await RunStartupReadyInputPlanUntilCharacterScreenAsync(input, cancellationToken, keepLaunchAlive: true);
        if (!ready.Ready)
        {
            var detectorReady = await PumpStartupSkipInputsUntilCharacterScreenAsync(
                input,
                cancellationToken,
                Math.Max(GetReadyLoopTimeoutSeconds(), MenuReadyFallbackTimeoutSeconds),
                keepLaunchAlive: true);
            ready = detectorReady with
            {
                Nudges = ready.Nudges + detectorReady.Nudges,
                TimeoutSeconds = ready.TimeoutSeconds + detectorReady.TimeoutSeconds,
                LaunchAttempts = ready.LaunchAttempts + detectorReady.LaunchAttempts,
                PlayClicks = ready.PlayClicks + detectorReady.PlayClicks,
                LastLaunchMessage = detectorReady.LastLaunchMessage == "(none)"
                    ? ready.LastLaunchMessage
                    : detectorReady.LastLaunchMessage
            };
        }

        if (!ready.Ready)
        {
            return CommandResult.Failure(
                $"{FormatCharacterScreenReadyFailure(ready, input)} Initial launch result: {launch.Message}. Ready loop sent {ready.LaunchAttempts} retry launch command(s) and {ready.PlayClicks} Battle.net Play click(s). Last launch result: {ready.LastLaunchMessage}.{FormatD2RProcessDiscoverySuffix()}",
                await CollectStatusAsync(cancellationToken));
        }

        var online = await EnsureReadyCharacterScreenOnlineAsync(input, ready, cancellationToken);
        if (online is not null)
        {
            return online;
        }

        MarkCharacterScreenIdle("Ready flow completed.");
        return CommandResult.Success("D2R ready flow completed.", await CollectStatusAsync(cancellationToken));
    }

    private CommandResult BeginD2RReadyLaunch()
    {
        if (IsD2RNamedProcessRunning())
        {
            RefreshD2RProcessActivity(d2rRunning: true);
            return CommandResult.Success("D2R is already running.");
        }

        ClearD2RActivity();
        var launch = TrySendD2RLaunchCommand();
        if (!launch.Ok)
        {
            return launch;
        }

        return CommandResult.Success(
            _config.PreferBattleNetExecLaunch || string.IsNullOrWhiteSpace(_config.D2RPath)
                ? "Initial Battle.net D2R launch command sent; ready loop is already sending startup skip input."
                : "Initial D2R launch command sent; ready loop is already sending startup skip input.");
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
        ClickD2R(input, GetUiPoint(D2RUiCoordinateTarget.CharacterPlayButton));
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

        MarkCommandCheckpoint("JoinGameAsync: ClickMenuEntryButtonUntilEnteredGameAsync(JoinGameButton)");
        var joinEntry = await ClickMenuEntryButtonUntilEnteredGameAsync(
            input,
            GetUiPoint(D2RUiCoordinateTarget.JoinGameButton),
            GetUiPoint(D2RUiCoordinateTarget.JoinGameTab),
            () => RestoreJoinGameFormAsync(input, args, cancellationToken, guardAgainstInGame: true),
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
        MarkCommandCheckpoint("SubmitPreparedJoinGameAsync: using prepared Join Game form");
        MarkCommandCheckpoint("SubmitPreparedJoinGameAsync: ClickMenuEntryButtonUntilEnteredGameAsync(JoinGameButton)");
        var joinEntry = await ClickMenuEntryButtonUntilEnteredGameAsync(
            input,
            GetUiPoint(D2RUiCoordinateTarget.JoinGameButton),
            GetUiPoint(D2RUiCoordinateTarget.JoinGameTab),
            () => RestoreJoinGameFormAsync(input, args, cancellationToken, guardAgainstInGame: true),
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
        MarkCommandCheckpoint("CreateGameAsync: EnsureLobbyOpenedAsync");
        var lobby = await EnsureLobbyOpenedAsync(input, args, cancellationToken);
        if (lobby is not null)
        {
            return lobby;
        }

        MarkCommandCheckpoint("CreateGameAsync: ClickLobbyTabDirectAsync(CreateGameTab)");
        await ClickLobbyTabDirectAsync(input, GetUiPoint(D2RUiCoordinateTarget.CreateGameTab), cancellationToken);

        MarkCommandCheckpoint("CreateGameAsync: filling game name/password fields");
        await FillTextFieldAsync(input, GetUiPoint(D2RUiCoordinateTarget.CreateGameNameField), args.GameName, cancellationToken);
        await FillTextFieldAsync(input, GetUiPoint(D2RUiCoordinateTarget.CreatePasswordField), args.Password ?? "", cancellationToken);
        await SelectCreateDifficultyAsync(input, args.Difficulty, cancellationToken);
        MarkCommandCheckpoint("CreateGameAsync: ClickMenuEntryButtonUntilEnteredGameAsync(CreateGameButton)");
        var createEntry = await ClickMenuEntryButtonUntilEnteredGameAsync(
            input,
            GetUiPoint(D2RUiCoordinateTarget.CreateGameButton),
            GetUiPoint(D2RUiCoordinateTarget.CreateGameTab),
            () => RestoreCreateGameFormAsync(input, args, cancellationToken, guardAgainstInGame: true),
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
                $"Join Game form preparation timed out after {timeoutSeconds}s while activity state was {GetActivitySnapshot().State}.{FormatCommandCheckpointSuffix()}{FormatInputDiagnosticsSuffix()}",
                await CollectStatusAsync(cancellationToken));
        }
    }

    private async Task<CommandResult?> PrepareJoinGameFormAsync(
        WindowsInput input,
        MenuCommandArgs args,
        CancellationToken cancellationToken)
    {
        MarkCommandCheckpoint("PrepareJoinGameFormAsync: start");
        if (GetActivitySnapshot().State == D2RActivityState.LobbyOrGame)
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

        // EnsureLobbyOpenedAsync's LobbyOrGame branch trusts the cached activity state and
        // returns without clicking or checking anything - fine for create-game/join-game, whose
        // very next action is a tab click that's harmless even if we're not quite where expected.
        // It is not fine here: the very next action is a precise click on the party icon, and if
        // that cache is stale (eg. a prior command actually left the client in-game), the click
        // lands on whatever's really on screen instead of opening the friends drawer, and every
        // click after it free-wheels with nothing real to land on - "seemed confused" with 3 VMs
        // stuck at the lobby with the drawer never open (issue #20, item 8). Confirm live before
        // spending the click, and try the same direct navigation EnsureLobbyOpenedAsync's other
        // branches already use if it's not where the cache claimed.
        if (!IsAnyLobbyEntryMenuVisible(input))
        {
            MarkCommandCheckpoint("JoinFriendAsync: lobby not visually confirmed - navigating directly");
            await SelectCharacterAsync(input, args.CharacterSlot, cancellationToken);
            await ClickLobbyDirectAsync(input, cancellationToken, guardAgainstInGame: true);
            await DelayStepAsync(cancellationToken);

            if (!IsAnyLobbyEntryMenuVisible(input))
            {
                return CommandResult.Failure(
                    "Could not visually confirm the Lobby before attempting to follow a friend.",
                    await CollectStatusAsync(cancellationToken));
            }

            MarkLobbyOrGameInteraction("Confirmed Lobby for follow after the cached state did not match what was actually on screen.");
        }

        var friends = await EnsureFriendsListVisibleAsync(input, "follow", cancellationToken);
        if (friends is not null)
        {
            return friends;
        }

        var entry = await ClickFriendJoinOptionUntilEnteredGameAsync(input, args.FriendRow ?? 1, "follow", cancellationToken);
        if (!entry.Entered)
        {
            return CommandResult.Failure(
                $"Clicked friend Join Game, but the client did not enter the game within {Math.Max(_config.Ui.GameEntryStartTimeoutSeconds, 1)}s. {entry.Message}",
                await CollectStatusAsync(cancellationToken));
        }

        MarkLobbyOrGameInteraction("Joined friend game.");
        return CommandResult.Success("Join friend/follow flow completed.", await CollectStatusAsync(cancellationToken));
    }

    // Issue #25: capture a small grid-sample "fingerprint" of whoever is sitting in friend row 1
    // right now, so the Host can distribute it to every agent and follow-auto can later recognize
    // that same name wherever it appears, instead of every agent needing a manually-supplied
    // friendRow that breaks the moment the friends list re-sorts. The operator is responsible for
    // making sure the intended friend is actually at the top of their own list before binding -
    // this command has no way to know who it's capturing, only where to look.
    private async Task<CommandResult> FollowBindCaptureAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        var input = FocusD2R();
        var lobby = await EnsureLobbyOpenedAsync(input, args, cancellationToken);
        if (lobby is not null)
        {
            return lobby;
        }

        if (!IsAnyLobbyEntryMenuVisible(input))
        {
            MarkCommandCheckpoint("FollowBindCaptureAsync: lobby not visually confirmed - navigating directly");
            await SelectCharacterAsync(input, args.CharacterSlot, cancellationToken);
            await ClickLobbyDirectAsync(input, cancellationToken, guardAgainstInGame: true);
            await DelayStepAsync(cancellationToken);

            if (!IsAnyLobbyEntryMenuVisible(input))
            {
                return CommandResult.Failure(
                    "Could not visually confirm the Lobby before capturing a follow-bind fingerprint.",
                    await CollectStatusAsync(cancellationToken));
            }

            MarkLobbyOrGameInteraction("Confirmed Lobby for follow-bind after the cached state did not match what was actually on screen.");
        }

        var friends = await EnsureFriendsListVisibleAsync(input, "follow-bind", cancellationToken);
        if (friends is not null)
        {
            return friends;
        }

        var region = D2RUiCoordinateCatalog.GetFriendRowFingerprintRegion(_config.Ui, row: 1);
        var samples = input.CaptureFingerprintGrid(region.Center, region.WidthRatio, region.HeightRatio, region.GridColumns, region.GridRows);
        var fingerprint = new FriendFingerprint(region.GridColumns, region.GridRows, samples);

        return CommandResult.Success(
            "Captured a follow-bind fingerprint from the top friend row.",
            new { fingerprint = fingerprint.ToBase64() });
    }

    // Pure local file I/O, no D2R interaction - bypasses _commandGate the same way screenshot and
    // status do (see HandleCommandAsync), so binding/unbinding on the Host isn't stuck waiting
    // behind whatever long-running menu command an agent happens to be mid-way through.
    private const string FollowTemplateFileName = "follow-template.txt";

    private static string FollowTemplatePath => Path.Combine(AppContext.BaseDirectory, FollowTemplateFileName);

    private sealed record FollowTemplateLoadResult(
        FriendFingerprint? Template,
        bool Exists,
        string Path,
        int ContentLength,
        string? Error);

    private static FollowTemplateLoadResult LoadFollowTemplate()
    {
        var path = FollowTemplatePath;
        try
        {
            if (!File.Exists(path))
            {
                return new FollowTemplateLoadResult(null, Exists: false, path, ContentLength: 0, Error: null);
            }

            var content = File.ReadAllText(path).Trim();
            var template = FriendFingerprint.FromBase64(content);
            return template is not null
                ? new FollowTemplateLoadResult(template, Exists: true, path, content.Length, Error: null)
                : new FollowTemplateLoadResult(null, Exists: true, path, content.Length, Error: "file does not contain a valid follow-bind fingerprint");
        }
        catch (Exception ex)
        {
            return new FollowTemplateLoadResult(null, File.Exists(path), path, ContentLength: 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static CommandResult FollowSetTemplate(MenuCommandArgs args)
    {
        var fingerprint = args.Fingerprint?.Trim();
        if (string.IsNullOrWhiteSpace(fingerprint) || FriendFingerprint.FromBase64(fingerprint) is null)
        {
            return CommandResult.Failure("follow_set_template requires a valid fingerprint.");
        }

        File.WriteAllText(FollowTemplatePath, fingerprint);
        return CommandResult.Success(
            "Follow-bind fingerprint saved.",
            new { templatePath = FollowTemplatePath, templateLength = fingerprint.Length });
    }

    private static CommandResult FollowClearTemplate()
    {
        if (File.Exists(FollowTemplatePath))
        {
            File.Delete(FollowTemplatePath);
        }

        return CommandResult.Success("Follow-bind fingerprint cleared.");
    }

    // One cycle of follow-auto: if nobody's bound, say so (not a failure - this is the normal
    // state for any account that isn't part of a follow-auto run). If bound, scan every visible
    // friend row for a fingerprint match - not just row 1 - since other tracked friends coming
    // online can outrank the bound friend in Battle.net's own online-sort at any point.
    private async Task<CommandResult> FollowAutoCheckAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        var templateLoad = LoadFollowTemplate();
        if (templateLoad.Template is null)
        {
            var message = templateLoad.Exists
                ? $"No valid follow-bind fingerprint is set at {templateLoad.Path}: {templateLoad.Error ?? "unknown template parse error"}."
                : $"No follow-bind fingerprint is set at {templateLoad.Path}.";
            return CommandResult.Success(message, new
            {
                bound = false,
                joined = false,
                templatePath = templateLoad.Path,
                templateExists = templateLoad.Exists,
                templateLength = templateLoad.ContentLength,
                templateError = templateLoad.Error
            });
        }

        var template = templateLoad.Template;
        WindowsInput input;
        try
        {
            input = FocusD2R();
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Process is not running:", StringComparison.Ordinal))
        {
            return CommandResult.Success(
                $"Follow-bind fingerprint is set, but D2R is not running: {FormatProcessNames(GetD2RProcessNames())}.",
                new
                {
                    bound = true,
                    joined = false,
                    d2rReady = false,
                    d2rRunning = false,
                    templatePath = templateLoad.Path,
                    templateExists = templateLoad.Exists,
                    templateLength = templateLoad.ContentLength
                });
        }

        var lobby = await EnsureLobbyOpenedAsync(input, args, cancellationToken);
        if (lobby is not null)
        {
            return lobby;
        }

        if (!IsAnyLobbyEntryMenuVisible(input))
        {
            MarkCommandCheckpoint("FollowAutoCheckAsync: lobby not visually confirmed - navigating directly");
            await SelectCharacterAsync(input, args.CharacterSlot, cancellationToken);
            await ClickLobbyDirectAsync(input, cancellationToken, guardAgainstInGame: true);
            await DelayStepAsync(cancellationToken);

            if (!IsAnyLobbyEntryMenuVisible(input))
            {
                return CommandResult.Failure(
                    "Could not visually confirm the Lobby during a follow-auto check.",
                    await CollectStatusAsync(cancellationToken));
            }

            MarkLobbyOrGameInteraction("Confirmed Lobby for follow-auto after the cached state did not match what was actually on screen.");
        }

        var friends = await EnsureFriendsListVisibleAsync(input, "follow-auto", cancellationToken);
        if (friends is not null)
        {
            return friends;
        }

        var maxRows = _config.Ui.FriendRowFingerprintMaxScanRows > 0 ? _config.Ui.FriendRowFingerprintMaxScanRows : 10;
        var rowMatches = new List<FriendRowFingerprintMatch>();
        for (var row = 1; row <= maxRows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkCommandCheckpoint($"FollowAutoCheckAsync: sampling friend row {row}");
            var region = D2RUiCoordinateCatalog.GetFriendRowFingerprintRegion(_config.Ui, row);
            var samples = TryCaptureFriendFingerprintSamples(
                () => input.CaptureFingerprintGrid(region.Center, region.WidthRatio, region.HeightRatio, region.GridColumns, region.GridRows),
                EntryLoopCheckBoundMs);
            var comparison = samples is null
                ? FriendFingerprintComparison.NotComparable
                : FriendFingerprint.Compare(template, new FriendFingerprint(region.GridColumns, region.GridRows, samples));
            rowMatches.Add(new FriendRowFingerprintMatch(row, comparison));
        }

        var rankedMatches = rowMatches
            .Where(match => match.Comparison.Comparable)
            .OrderBy(match => match.Comparison.SignalAverageDifference)
            .ThenBy(match => match.Comparison.AverageDifference)
            .ToList();
        var bestMatch = rankedMatches.FirstOrDefault();
        if (bestMatch is null || !IsUsableFollowFingerprintMatch(bestMatch.Comparison))
        {
            return CommandResult.Success(
                $"Bound friend not confidently found in the visible friends list this cycle. {FormatFollowFingerprintScores(rowMatches)}",
                new { bound = true, joined = false, fingerprintScores = FormatFollowFingerprintScores(rowMatches) });
        }

        var secondMatch = rankedMatches.Skip(1).FirstOrDefault();
        if (secondMatch is not null
            && secondMatch.Comparison.SignalAverageDifference <= bestMatch.Comparison.SignalAverageDifference + FollowFingerprintMinSignalSeparation)
        {
            return CommandResult.Success(
                $"Bound friend fingerprint was ambiguous; not clicking a friend row this cycle. {FormatFollowFingerprintScores(rowMatches)}",
                new { bound = true, joined = false, fingerprintScores = FormatFollowFingerprintScores(rowMatches) });
        }

        var matchedRow = bestMatch.Row;
        MarkCommandCheckpoint($"FollowAutoCheckAsync: matched bound friend at row {matchedRow}");
        var entry = await ClickFriendJoinOptionUntilEnteredGameAsync(input, matchedRow, "follow-auto", cancellationToken);
        if (!entry.Entered)
        {
            return CommandResult.Failure(
                $"Found the bound friend at row {matchedRow} and clicked Join Game, but the client did not enter the game within {Math.Max(_config.Ui.GameEntryStartTimeoutSeconds, 1)}s. {entry.Message}",
                await CollectStatusAsync(cancellationToken));
        }

        if (!await WaitForStrictFollowAutoEntryAsync(input, cancellationToken))
        {
            return CommandResult.Failure(
                $"Found the bound friend at row {matchedRow} and clicked Join Game, but strict in-game HUD confirmation did not appear after the join flow reported success.",
                await CollectStatusAsync(cancellationToken));
        }

        MarkLobbyOrGameInteraction("Joined bound friend's game via follow-auto.");
        return CommandResult.Success("Joined the bound friend's game.", new { bound = true, joined = true });
    }

    private static bool IsUsableFollowFingerprintMatch(FriendFingerprintComparison comparison)
    {
        return comparison.Comparable
            && comparison.SignalPixels >= FollowFingerprintMinSignalPixels
            && comparison.AverageDifference <= FollowFingerprintMaxAverageDifference
            && comparison.SignalAverageDifference <= FollowFingerprintMaxSignalAverageDifference;
    }

    internal static byte[]? TryCaptureFriendFingerprintSamples(Func<byte[]> capture, int timeoutMs)
    {
        return TryRunBounded<byte[]?>(capture, timeoutMs, fallback: null);
    }

    private static string FormatFollowFingerprintScores(IEnumerable<FriendRowFingerprintMatch> matches)
    {
        var parts = matches.Select(match => match.Comparison.Comparable
            ? $"r{match.Row}=avg{match.Comparison.AverageDifference:0.0}/sig{match.Comparison.SignalAverageDifference:0.0}/px{match.Comparison.SignalPixels}"
            : $"r{match.Row}=n/a");
        return $"scores: {string.Join(", ", parts)}.";
    }

    private async Task<bool> WaitForStrictFollowAutoEntryAsync(WindowsInput input, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Clamp(_config.Ui.GameLoadSeconds, 3, 8));
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkCommandCheckpoint("FollowAutoCheckAsync: verifying strict in-game HUD");
            if (TryRunBounded(() => IsInGameReadyStrict(input), InGameHudSampleBoundMs, fallback: false))
            {
                RecordObservedFrame(VisibleD2RState.InGame.ToString());
                MarkLobbyOrGameInteraction("Strictly confirmed in-game HUD for follow-auto.");
                return true;
            }

            await Task.Delay(EntryPollIntervalMs, cancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return false;
    }

    private async Task<GameEntryAttemptResult> ClickFriendJoinOptionUntilEnteredGameAsync(
        WindowsInput input,
        int friendRow,
        string context,
        CancellationToken cancellationToken)
    {
        var joinGameTab = GetUiPoint(D2RUiCoordinateTarget.JoinGameTab);

        async Task<bool> SelectFriendGameAsync()
        {
            var friends = await EnsureFriendsListVisibleAsync(input, $"{context}-entry-retry", cancellationToken);
            if (friends is not null)
            {
                return false;
            }

            ClickD2R(input, GetFriendRowPoint(friendRow), MouseButton.Right);
            await DelayStepAsync(cancellationToken);
            ClickD2R(input, GetFriendContextJoinGamePoint(friendRow));
            await DelayFastMenuAsync(cancellationToken);
            return !IsAnyLobbyEntryMenuVisible(input) || IsLobbyTabReady(input, joinGameTab);
        }

        if (!await SelectFriendGameAsync())
        {
            return new GameEntryAttemptResult(false, DialogRetries: 0, ConnectionRetries: 0, "The friend Join Game option could not be selected.");
        }

        if (!IsAnyLobbyEntryMenuVisible(input))
        {
            var entry = await WaitForGameEntryAsync(input, cancellationToken);
            return entry == GameEntryWaitResult.EnteredGame
                ? new GameEntryAttemptResult(true, DialogRetries: 0, ConnectionRetries: 0, "Entered game.")
                : new GameEntryAttemptResult(false, DialogRetries: 0, ConnectionRetries: 0, FormatGameEntryWaitFailure(entry));
        }

        MarkCommandCheckpoint($"ClickFriendJoinOptionUntilEnteredGameAsync({context}): submitting Join Game tab");
        return await ClickMenuEntryButtonUntilEnteredGameAsync(
            input,
            GetUiPoint(D2RUiCoordinateTarget.JoinGameButton),
            joinGameTab,
            SelectFriendGameAsync,
            cancellationToken);
    }

    private async Task<CommandResult?> EnsureFriendsListVisibleAsync(
        WindowsInput input,
        string context,
        CancellationToken cancellationToken)
    {
        var openedDrawer = false;
        if (!IsFriendsDrawerOpen(input))
        {
            MarkCommandCheckpoint($"EnsureFriendsListVisibleAsync({context}): opening friends drawer");
            ClickD2RStatefulToggle(input, GetUiPoint(D2RUiCoordinateTarget.LobbyPartyIcon));
            openedDrawer = true;
            await DelayLongAsync(cancellationToken);
        }
        else
        {
            MarkCommandCheckpoint($"EnsureFriendsListVisibleAsync({context}): friends drawer already open");
        }

        if (!IsFriendsDrawerOpen(input))
        {
            return CommandResult.Failure(
                $"Could not open the friends drawer before {context}.",
                await CollectStatusAsync(cancellationToken));
        }

        var expanded = openedDrawer
            ? (IsExpanded: false, Summary: "fresh drawer open; skipped pre-expand row evidence")
            : GetFriendsListExpandedEvidence(input);
        var accordionAction = ChooseFriendsAccordionAction(openedDrawer, expanded.IsExpanded);
        if (accordionAction == FriendsAccordionAction.ExpandAfterOpeningDrawer)
        {
            MarkCommandCheckpoint($"EnsureFriendsListVisibleAsync({context}): expanding Friends accordion after opening drawer");
            ClickD2RStatefulToggle(input, GetUiPoint(D2RUiCoordinateTarget.FriendsAccordionHeader));
            await DelayLongAsync(cancellationToken);
        }
        else if (accordionAction == FriendsAccordionAction.ExpandCollapsed)
        {
            MarkCommandCheckpoint($"EnsureFriendsListVisibleAsync({context}): expanding Friends accordion");
            ClickD2RStatefulToggle(input, GetUiPoint(D2RUiCoordinateTarget.FriendsAccordionHeader));
            await DelayLongAsync(cancellationToken);
        }
        else
        {
            MarkCommandCheckpoint($"EnsureFriendsListVisibleAsync({context}): Friends accordion already expanded");
        }

        if (!ShouldVerifyFriendsExpansionAfterAction(accordionAction))
        {
            MarkCommandCheckpoint($"EnsureFriendsListVisibleAsync({context}): Friends accordion clicked; proceeding to row scan");
            return null;
        }

        MarkCommandCheckpoint($"EnsureFriendsListVisibleAsync({context}): verifying already-expanded Friends rows");
        expanded = GetFriendsListExpandedEvidence(input);
        if (!expanded.IsExpanded)
        {
            return CommandResult.Failure(
                $"Could not expand the Friends list before {context}. Friends list evidence: {expanded.Summary}.",
                await CollectStatusAsync(cancellationToken));
        }

        return null;
    }

    internal static FriendsAccordionAction ChooseFriendsAccordionAction(bool openedDrawer, bool expandedEvidence)
    {
        if (openedDrawer)
        {
            return FriendsAccordionAction.ExpandAfterOpeningDrawer;
        }

        return expandedEvidence
            ? FriendsAccordionAction.SkipExpanded
            : FriendsAccordionAction.ExpandCollapsed;
    }

    internal static bool ShouldVerifyFriendsExpansionAfterAction(FriendsAccordionAction action)
    {
        return action == FriendsAccordionAction.SkipExpanded;
    }

    private bool IsFriendsDrawerOpen(WindowsInput input)
    {
        return TryRunBounded(() =>
        {
            var stats = input.SampleRegion(
                GetUiPoint(D2RUiCoordinateTarget.FriendsAccordionHeader),
                widthRatio: 0.200,
                heightRatio: 0.022,
                sampleGrid: MenuSampleGrid);
            return D2RScreenClassifier.IsFriendsDrawerHeaderVisible(stats);
        }, EntryLoopCheckBoundMs);
    }

    private (bool IsExpanded, string Summary) GetFriendsListExpandedEvidence(WindowsInput input)
    {
        return TryRunBounded(() =>
        {
            var expanded = false;
            var summary = new StringBuilder();

            for (var row = 1; row <= 3; row++)
            {
                var nameRegion = D2RUiCoordinateCatalog.GetFriendRowFingerprintRegion(_config.Ui, row);
                var nameStats = input.SampleRegion(
                    nameRegion.Center,
                    nameRegion.WidthRatio,
                    nameRegion.HeightRatio,
                    sampleGrid: MenuSampleGrid);
                var nameVisible = D2RScreenClassifier.IsFriendRowNameVisible(nameStats)
                    || (row > 1 && D2RScreenClassifier.IsLowGreyFriendRowNameVisible(nameStats));

                var markerPoint = GetFriendRowMarkerPoint(row);
                var markerStats = input.SampleRegion(
                    markerPoint,
                    widthRatio: 0.035,
                    heightRatio: 0.032,
                    sampleGrid: MenuSampleGrid);
                var markerVisible = D2RScreenClassifier.IsFriendRowMarkerVisible(markerStats);

                expanded |= nameVisible && markerVisible;

                if (summary.Length > 0)
                {
                    summary.Append(' ');
                }

                summary
                    .Append(FormatCheck($"r{row}txt", nameVisible, nameStats))
                    .Append('/')
                    .Append(FormatCheck($"r{row}mark", markerVisible, markerStats));
            }

            return (expanded, summary.ToString());
        }, EntryLoopCheckBoundMs, (false, "timeout"));
    }

    private AgentCommon.UiPoint GetFriendRowMarkerPoint(int row)
    {
        var rowPoint = D2RUiCoordinateCatalog.GetFriendRowPoint(_config.Ui, row);
        return new AgentCommon.UiPoint(Math.Clamp(rowPoint.X - 0.090, 0, 1), rowPoint.Y);
    }

    private async Task<CommandResult> SaveAndExitAsync(CancellationToken cancellationToken)
    {
        var input = FocusD2R();
        input.PressEscape();
        _ = input.SendWindowEscapeKey(GetD2RProcessNames());
        await DelayStepAsync(cancellationToken);
        ClickD2R(input, GetUiPoint(D2RUiCoordinateTarget.SaveAndExitButton));
        var postExitState = await WaitForPostSaveExitMenuAsync(input, cancellationToken);
        return CommandResult.Success($"Save and Exit flow completed; {postExitState}.", await CollectStatusAsync(cancellationToken));
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

        return await EnsureReadyCharacterScreenOnlineAsync(input, ready, cancellationToken);
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

    private void MarkD2RActivityUnknown(string reason)
    {
        lock (_activityLock)
        {
            _activityState = D2RActivityState.Unknown;
            _characterScreenIdleSinceUtc = null;
            _lastLobbyOrGameInteractionUtc = null;
            _lastActivityReason = reason;
        }
    }

    private void MarkCommandCheckpoint(string checkpoint)
    {
        // Every other surfaced field (lastObservedFrame, lastInputAction, lastActivityReason)
        // only updates after a step finishes - successfully or not. None of them can show
        // "this is what the command is doing right now," which is exactly the visibility
        // missing when a command goes silent for minutes with no further click/key logged:
        // there was no way to tell whether it was stuck before, during, or after any specific
        // step. This is set at the start of each meaningful step so a stalled run's last
        // checkpoint points at the actual stuck call instead of leaving that to guesswork.
        _lastCommandCheckpoint = checkpoint;
        _lastCommandCheckpointUtc = DateTimeOffset.UtcNow;
    }

    private string FormatCommandCheckpointSuffix()
    {
        var checkpoint = _lastCommandCheckpoint;
        if (string.IsNullOrWhiteSpace(checkpoint))
        {
            return "";
        }

        var age = _lastCommandCheckpointUtc is { } reachedAt
            ? $" ({FormatCompactAge(DateTimeOffset.UtcNow - reachedAt)} ago)"
            : "";
        return $" Last command checkpoint: {checkpoint}{age}.";
    }

    private static string FormatCompactAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60)
        {
            return $"{Math.Max(0, age.TotalSeconds):N0}s";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{age.TotalMinutes:N0}m";
        }

        return $"{age.TotalHours:N1}h";
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

    internal ActivitySnapshot GetActivitySnapshot()
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

    private VisibleD2RState GetBestProcessOnlyVisibleState(bool d2rRunning)
    {
        if (!d2rRunning)
        {
            return VisibleD2RState.NotRunning;
        }

        if (_lastObservedFrameUtc is null
            || DateTimeOffset.UtcNow - _lastObservedFrameUtc.Value > TimeSpan.FromSeconds(15)
            || string.IsNullOrWhiteSpace(_lastObservedFrame))
        {
            return VisibleD2RState.Unknown;
        }

        return _lastObservedFrame switch
        {
            nameof(ReadyScreenState.DiabloSplash) => VisibleD2RState.DiabloSplash,
            nameof(ReadyScreenState.CharacterMenu) => VisibleD2RState.CharacterScreen,
            nameof(ReadyScreenState.CharacterScreen) => VisibleD2RState.CharacterScreen,
            nameof(ReadyScreenState.OfflineCharacterScreen) => VisibleD2RState.OfflineCharacterScreen,
            nameof(VisibleD2RState.NotRunning) => VisibleD2RState.NotRunning,
            nameof(VisibleD2RState.LobbyOrGame) => VisibleD2RState.LobbyOrGame,
            nameof(VisibleD2RState.InGame) => VisibleD2RState.InGame,
            _ => VisibleD2RState.Unknown
        };
    }

    private VisibleD2RState DetectVisibleD2RState(bool d2rRunning)
    {
        if (!OperatingSystem.IsWindows())
        {
            var unsupported = d2rRunning ? VisibleD2RState.Unknown : VisibleD2RState.NotRunning;
            RecordObservedFrame(unsupported.ToString());
            return unsupported;
        }

        try
        {
            var visibleState = DetectVisibleD2RState(new WindowsInput());
            if (visibleState != VisibleD2RState.Unknown)
            {
                RecordObservedFrame(visibleState.ToString());
                return visibleState;
            }
        }
        catch (Exception)
        {
        }

        var fallback = d2rRunning ? VisibleD2RState.Unknown : VisibleD2RState.NotRunning;
        RecordObservedFrame(fallback.ToString());
        return fallback;
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

        // sitting_in_town.png (a real in-game town capture) proved the lobby-tab/entry-button
        // thresholds below can coincidentally match ordinary outdoor scenery: createTab read
        // lum=38.7/grey=0.85/dark=0.15 (passes) and createButton read lum=32.7/grey=0.32/
        // dark=0.68 (passes) purely by chance, at a real reference capture's exact coordinates.
        // Checking strict in-game evidence (modern/legacy HUD globes, IsInGameReadyStrict) first
        // means a real in-game scene with visible globes - the common case - never reaches the
        // lobby check at all. The broader Frame-kind fallback stays AFTER the lobby check,
        // unchanged from v0.2.64, which added that ordering because a filled join/create form
        // could satisfy IsInGameHudFrame's looser thresholds - reordering this block back
        // wholesale would have resurrected that exact bug.
        if (IsInGameReadyStrict(input))
        {
            return VisibleD2RState.InGame;
        }

        if (IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap(input))
        {
            return VisibleD2RState.LobbyOrGame;
        }

        if (IsInGameReady(input))
        {
            return VisibleD2RState.InGame;
        }

        RecordClassifierBreakdown(TryRunBounded(() => ComputeVisibleStateClassifierBreakdown(input, MenuSampleGrid), ClassifierBreakdownBoundMs, ""));
        return VisibleD2RState.Unknown;
    }

    // Every top-level state check (DiabloSplash, offline, character screen, in-game, lobby)
    // is itself a handful of named pixel-region sub-checks (IsLobbyTabReady,
    // IsLobbyEntryButtonReady, IsCharacterButtonPairReady, ...) that previously only showed
    // up in the raw ScreenRegionStats dumped on a menu_ready timeout - never live, and never
    // for the lobby/in-game checks at all. Recording a compact pass/fail per sub-check
    // whenever the result is Unknown means a live `watch` can show *why* nothing matched
    // instead of just "Unknown", without needing a screenshot first.
    private string ComputeVisibleStateClassifierBreakdown(WindowsInput input, int sampleGrid)
    {
        try
        {
            var logo = SampleD2RRegion(input, new AgentCommon.UiPoint(0.500, 0.290), widthRatio: 0.45, heightRatio: 0.22, windowRelative: false, sampleGrid: sampleGrid);
            var prompt = SampleD2RRegion(input, new AgentCommon.UiPoint(0.500, 0.600), widthRatio: 0.32, heightRatio: 0.055, windowRelative: false, sampleGrid: sampleGrid);
            var splash = IsDiabloSplashScreen(input, sampleGrid);
            var connecting = splash && IsConnectingToBattleNetDialog(input, sampleGrid);
            var emptyPanel = SampleD2RRegion(input, new AgentCommon.UiPoint(0.895, 0.455), widthRatio: 0.17, heightRatio: 0.66, windowRelative: false, sampleGrid: sampleGrid);
            var offline = IsCharacterScreenOffline(input, sampleGrid: sampleGrid);
            var play = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.CharacterPlayButton), widthRatio: 0.13, heightRatio: 0.055, windowRelative: false, sampleGrid: sampleGrid);
            var charMenuLogo = SampleD2RRegion(input, new AgentCommon.UiPoint(0.105, 0.170), widthRatio: 0.13, heightRatio: 0.16, windowRelative: false, sampleGrid: sampleGrid);
            var charButtons = IsCharacterButtonPairReady(input, windowRelative: false, sampleGrid);
            var charMenu = IsCharacterMenuReady(input, windowRelative: false, sampleGrid);
            var inGameScreenEvidence = SampleInGameHudEvidence(input, windowRelative: false);
            var inGameWindowEvidence = SampleInGameHudEvidence(input, windowRelative: true);
            var inGameScreen = IsInGameHudEvidenceReady(inGameScreenEvidence);
            var inGameWindow = IsInGameHudEvidenceReady(inGameWindowEvidence);
            var joinTab = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.JoinGameTab), widthRatio: 0.10, heightRatio: 0.045, windowRelative: false, sampleGrid: sampleGrid);
            var tabReady = IsLobbyTabReady(input, GetUiPoint(D2RUiCoordinateTarget.CreateGameTab))
                || IsLobbyTabReady(input, GetUiPoint(D2RUiCoordinateTarget.JoinGameTab));
            var entry = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.CreateGameButton), widthRatio: 0.16, heightRatio: 0.055, windowRelative: false, sampleGrid: sampleGrid);
            var entryReady = IsLobbyEntryButtonReady(input);
            var form = SampleD2RRegion(input, new AgentCommon.UiPoint(0.765, 0.365), widthRatio: 0.30, heightRatio: 0.42, windowRelative: false, sampleGrid: sampleGrid);
            var formReady = IsLobbyFormPanelReady(input, windowRelative: false)
                || IsLobbyFormPanelReady(input, windowRelative: true);

            return $"{FormatCheck("splash", splash, logo)} {FormatCheck("connecting", connecting, prompt)} {FormatCheck("offline", offline, emptyPanel)} "
                + $"char(btn={FormatCheck("", charButtons, play)},menu={FormatCheck("", charMenu, charMenuLogo)}) "
                + $"inGame={FormatInGameEvidence(inGameScreen, inGameWindow, inGameScreenEvidence, inGameWindowEvidence)} "
                + $"lobby(tab={FormatCheck("", tabReady, joinTab)},entry={FormatCheck("", entryReady, entry)},form={FormatCheck("", formReady, form)})";
        }
        catch (Exception)
        {
            return "";
        }
    }

    // Narrower than ComputeVisibleStateClassifierBreakdown - DetectReadyScreenState never
    // evaluates in-game/lobby, so including them here would misleadingly imply they were
    // checked as part of this decision.
    private string ComputeReadyScreenClassifierBreakdown(WindowsInput input, int sampleGrid)
    {
        try
        {
            var logo = SampleD2RRegion(input, new AgentCommon.UiPoint(0.500, 0.290), widthRatio: 0.45, heightRatio: 0.22, windowRelative: false, sampleGrid: sampleGrid);
            var prompt = SampleD2RRegion(input, new AgentCommon.UiPoint(0.500, 0.600), widthRatio: 0.32, heightRatio: 0.055, windowRelative: false, sampleGrid: sampleGrid);
            var splash = IsDiabloSplashScreen(input, sampleGrid);
            var connecting = splash && IsConnectingToBattleNetDialog(input, sampleGrid);
            var emptyPanel = SampleD2RRegion(input, new AgentCommon.UiPoint(0.895, 0.455), widthRatio: 0.17, heightRatio: 0.66, windowRelative: false, sampleGrid: sampleGrid);
            var offline = IsCharacterScreenOffline(input, sampleGrid: sampleGrid);
            var play = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.CharacterPlayButton), widthRatio: 0.13, heightRatio: 0.055, windowRelative: false, sampleGrid: sampleGrid);
            var charMenuLogo = SampleD2RRegion(input, new AgentCommon.UiPoint(0.105, 0.170), widthRatio: 0.13, heightRatio: 0.16, windowRelative: false, sampleGrid: sampleGrid);
            var charButtons = IsCharacterButtonPairReady(input, windowRelative: false, sampleGrid);
            var charMenu = IsCharacterMenuReady(input, windowRelative: false, sampleGrid);

            return $"{FormatCheck("splash", splash, logo)} {FormatCheck("connecting", connecting, prompt)} {FormatCheck("offline", offline, emptyPanel)} "
                + $"char(btn={FormatCheck("", charButtons, play)},menu={FormatCheck("", charMenu, charMenuLogo)})";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string FormatInGameEvidence(
        bool screenReady,
        bool windowReady,
        InGameHudEvidence screenEvidence,
        InGameHudEvidence windowEvidence)
    {
        if (screenReady || windowReady)
        {
            return "T";
        }

        return $"F(scr:{FormatHudEvidence(screenEvidence)},win:{FormatHudEvidence(windowEvidence)})";
    }

    private static string FormatHudEvidence(InGameHudEvidence evidence)
    {
        return $"hpR={evidence.ModernHealth.RedRatio:F2},mpB={evidence.ModernMana.BlueRatio:F2},"
            + $"lhpR={evidence.LegacyHealth.RedRatio:F2},lmpB={evidence.LegacyMana.BlueRatio:F2},"
            + $"bar(l={evidence.ActionHud.AverageLuminance:F0},sd={evidence.ActionHud.LuminanceStdDev:F0},d={evidence.ActionHud.DarkRatio:F2},br={evidence.ActionHud.BrightRatio:F2},g={evidence.ActionHud.GreyRatio:F2}),"
            + $"bot(sd={evidence.BottomHud.LuminanceStdDev:F0},d={evidence.BottomHud.DarkRatio:F2}),"
            + $"ctr(sd={evidence.CenterHud.LuminanceStdDev:F0},d={evidence.CenterHud.DarkRatio:F2},br={evidence.CenterHud.BrightRatio:F2},g={evidence.CenterHud.GreyRatio:F2})";
    }

    // Passing checks stay a bare "name=T" - the value only earns its space in the line when
    // it's the reason something didn't match. Reuses the same lum/grey/dark/orange summary
    // FormatCharacterScreenClassifierDiagnostics already prints in the menu_ready timeout
    // message, so the two are directly comparable.
    private static string FormatCheck(string name, bool passed, ScreenRegionStats stats)
    {
        var prefix = string.IsNullOrEmpty(name) ? "" : $"{name}=";
        return passed
            ? $"{prefix}T"
            : $"{prefix}F(lum={stats.AverageLuminance:F0},grey={stats.GreyRatio:F2},dark={stats.DarkRatio:F2},orange={stats.OrangeRatio:F2})";
    }

    private WindowsInput FocusD2R()
    {
        var processNames = GetD2RProcessNames();
        if (!IsD2RRunning())
        {
            throw new InvalidOperationException($"Process is not running: {FormatProcessNames(processNames)}");
        }

        var input = new WindowsInput();
        // Do not block commands on foreground negotiation here. Live VM runs showed
        // SetForegroundWindow/AttachThreadInput can stall for tens of seconds while D2R is
        // responsive on screen. Menu clicks now go full-screen first, which focuses the visible
        // game as a side effect, plus HWND-direct fallback for cases where focus is unreliable.
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

    // SetForegroundWindow/AttachThreadInput can stall for tens of seconds on a live VM while
    // D2R is otherwise responsive on screen - that's why startup bursts stopped calling
    // TryPrepareD2RForInput outright. But with no focus-steal attempt at all, SendInput-based
    // clicks land wherever OS focus already is (commonly Battle.net, still foreground) and the
    // HWND-targeted PostMessage fallback alone often isn't enough to get a fullscreen D2R window
    // to react before it has ever held real focus. Bound the attempt to one detection cycle so
    // it can still win the common case without blocking the rest of the burst when it doesn't.
    private bool TryPrepareD2RForInputBounded(WindowsInput input)
    {
        return TryRunBounded(() => TryPrepareD2RForInput(input), ReadyStartupDetectionIntervalMs);
    }

    // watch-kfwuq5-20260625-191907.log proved this, not theorized it: ThreadPool.ThreadCount on
    // hc1 was flat at 6-7 for the run's first 78s, then climbed monotonically the instant
    // ClickMenuEntryButtonUntilEnteredGameAsync's loop started - 9, 13, 15, 30, 60, 98 threads,
    // never leveling off. TryRunBounded's Task.Run only stops *waiting* on timeout; it never
    // kills the underlying thread. That was fine when the guarded GDI call was merely slow (it
    // would eventually finish and the thread would return to the pool) - but if it now hangs
    // forever instead, every bounded call permanently abandons one more thread, and since the
    // abandoned thread never reaches its own SampleD2RRegion's `finally { ReleaseDC(...) }`
    // either, it leaks a GDI device-context handle on top of the thread - a resource with a
    // hard per-process ceiling on Windows, which would make GDI calls likelier to hang as the
    // leak grows, accelerating itself over time. This caps the blast radius without needing to
    // know why the underlying call hangs: once MaxConcurrentBoundedCalls slots are held by calls
    // that haven't returned yet, further calls fail fast with the fallback instead of spawning
    // another thread that will never come back either.
    private const int MaxConcurrentBoundedCalls = 32;
    private static readonly SemaphoreSlim BoundedCallSlots = new(MaxConcurrentBoundedCalls, MaxConcurrentBoundedCalls);

    // Pulled out of TryPrepareD2RForInputBounded so the bounding behavior itself - not the
    // Win32 focus call - can be regression-tested without a Windows host. The bug this guards
    // against: an action that hangs (or throws) must not make the caller wait past timeoutMs.
    internal static bool TryRunBounded(Func<bool> action, int timeoutMs)
    {
        if (!BoundedCallSlots.Wait(0))
        {
            return false;
        }

        try
        {
            var task = Task.Run(() =>
            {
                try
                {
                    return action();
                }
                finally
                {
                    BoundedCallSlots.Release();
                }
            });
            return task.Wait(timeoutMs) && task.Result;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Same bounding shape as TryRunBounded above, generalized to a fallback value instead of a
    // bool. watch-xy4wiew2-20260625-132336.log: ComputeVisibleStateClassifierBreakdown (~25-35
    // unbounded GDI region samples) froze a deadline-boundary checkpoint for 1m19s when called
    // from TryConfirmAtElapsedDeadlineAsync on every failed HUD confirmation, not just on
    // Unknown like its other call sites - this is purely diagnostic output (RecordClassifierBreakdown
    // doesn't feed any pass/fail decision), so bounding it carries none of the staleness risk
    // that caching IsInGameReady's result did in v0.2.71/72.
    internal static T TryRunBounded<T>(Func<T> action, int timeoutMs, T fallback)
    {
        if (!BoundedCallSlots.Wait(0))
        {
            return fallback;
        }

        try
        {
            var task = Task.Run(() =>
            {
                try
                {
                    return action();
                }
                finally
                {
                    BoundedCallSlots.Release();
                }
            });
            return task.Wait(timeoutMs) ? task.Result : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    // watch-xitui34-20260625-144937.log: WaitForGameEntryAsync's IsGameEntryMenuStillVisible
    // false-positived "the create/join form is still on screen" while the user was actually
    // already in-game (confirmed by direct observation, watching the whole time). The recovery
    // path that follows blindly clicks fixed lobby-UI coordinates (tab, form fields, entry
    // button) to restore the form and retry - in D2R, a click anywhere that isn't UI is a
    // click-to-move command, so those "safe" recovery clicks became movement clicks in a live
    // game, which can permanently kill a hardcore character. This gate goes in front of every
    // blind recovery/entry click in this loop: if there's any reasonably-cheap evidence we might
    // already be in-game, skip the click entirely and let the loop's own entry-confirmation
    // check (already run at the top of every iteration) catch up safely instead.
    //
    // The fallback on timeout is deliberately the opposite of every other bounded check in this
    // file: everywhere else, "couldn't confirm in time" defaults to false/not-yet because the
    // cost of a wrong "not ready" is just one more retry. Here, the cost of a wrong "safe to
    // click" is a movement click into a live game, so an inconclusive check must default to
    // "might be in-game, don't click" - true, not false.
    //
    // The bound itself has to be generous, not tight: this wraps the same IsInGameReady sample
    // that's been measured taking 12-56s under D2R's load spike - which is exactly the window
    // this gate exists to protect. A short bound would time out (and correctly default to skip)
    // during nearly every ordinary slow moment too, not just real danger, stalling the whole
    // recovery flow far more often than necessary. 2.5s gives the real check a fair chance to
    // resolve before falling back to the safe default.
    private const int InGameSafetyCheckBoundMs = 2500;

    private bool MightAlreadyBeInGame(WindowsInput input)
    {
        return TryRunBounded(() => IsInGameReady(input), InGameSafetyCheckBoundMs, fallback: true);
    }

    internal static bool ShouldSkipMenuClickForInGameSafety(bool guardAgainstInGame, Func<bool> mightAlreadyBeInGame)
    {
        return guardAgainstInGame && mightAlreadyBeInGame();
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

    private async Task<CommandResult?> EnsureReadyCharacterScreenOnlineAsync(
        WindowsInput input,
        ReadyWaitResult ready,
        CancellationToken cancellationToken)
    {
        MarkCommandCheckpoint($"EnsureReadyCharacterScreenOnlineAsync: ready loop detected {ready.LastState}");
        await DelayCharacterScreenSettleAsync(cancellationToken);

        if (ready.LastState != ReadyScreenState.OfflineCharacterScreen)
        {
            MarkCommandCheckpoint("EnsureReadyCharacterScreenOnlineAsync: online character screen accepted");
            return null;
        }

        MarkCommandCheckpoint("EnsureReadyCharacterScreenOnlineAsync: offline character screen detected, clicking Online");
        if (!await EnsureOnlineCharacterScreenAsync(input, cancellationToken))
        {
            return CommandResult.Failure(
                $"D2R reached the offline character screen, but clicking Online did not reveal the online character list within {GetCharacterScreenReconnectSeconds()}s.{FormatInputDiagnosticsSuffix()}",
                await CollectStatusAsync(cancellationToken));
        }

        MarkCommandCheckpoint("EnsureReadyCharacterScreenOnlineAsync: online character screen restored");
        return null;
    }

    private ReadyLaunchNudgeState CreateReadyLaunchNudgeState()
    {
        return new ReadyLaunchNudgeState
        {
            NextLaunchRetryAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(GetBattleNetExecRetryDelaySeconds()),
            NextPlayClickAt = DateTimeOffset.UtcNow
        };
    }

    private void NudgeD2RLaunchDuringReady(WindowsInput input, ReadyLaunchNudgeState state)
    {
        if (IsD2RNamedProcessRunning())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now >= state.NextPlayClickAt)
        {
            if (TryClickBattleNetPlay(input, requireButtonReady: true))
            {
                state.PlayClicks++;
            }

            state.NextPlayClickAt = now + TimeSpan.FromSeconds(1.5);
        }

        if (now < state.NextLaunchRetryAt)
        {
            return;
        }

        var launch = TrySendD2RLaunchCommand();
        state.LaunchAttempts++;
        state.LastLaunchMessage = launch.Message;
        state.NextLaunchRetryAt = now + TimeSpan.FromSeconds(GetBattleNetExecRetryDelaySeconds());
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

            if (IsCharacterScreenReady(input))
            {
                return true;
            }

            if (IsCharacterScreenOffline(input))
            {
                ClickD2R(input, GetUiPoint(D2RUiCoordinateTarget.CharacterOnlineTab));
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
        int? timeoutSeconds = null,
        bool keepLaunchAlive = false)
    {
        var skipSeconds = timeoutSeconds ?? GetReadyLoopTimeoutSeconds();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(skipSeconds);
        var intervalMs = Math.Clamp(_config.Ui.ReadyStartupSkipIntervalMs, 50, 250);
        var nudges = 0;
        var lastState = ReadyScreenState.Unknown;
        var nextDetectionAt = DateTimeOffset.UtcNow;
        var nextWindowRelativeDetectionAt = DateTimeOffset.UtcNow;
        var nextProcessCheckAt = DateTimeOffset.UtcNow;
        var sawD2RProcessRunning = false;
        var launchNudges = CreateReadyLaunchNudgeState();

        ReadyWaitResult Result(bool ready, int nudges, ReadyScreenState state, int timeout, bool processExitedDuringWait = false)
        {
            return new ReadyWaitResult(
                ready,
                nudges,
                state,
                timeout,
                processExitedDuringWait,
                launchNudges.LaunchAttempts,
                launchNudges.PlayClicks,
                launchNudges.LastLaunchMessage);
        }

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;

            // Detect before choosing/sending this iteration's burst, not after. Detecting
            // after meant the burst just sent could already be one detection cycle stale -
            // including the case where the screen reached character select between the last
            // detection and now, but lastState still said Unknown, so the generic burst (which
            // includes Escape, then Space/Enter moments later in the same burst) fired anyway.
            // Escape at character select can open the exit-confirmation dialog, and the
            // following Enter can confirm it, silently exiting back to the title screen.
            if (now >= nextDetectionAt)
            {
                var includeWindowRelativeDetection = now >= nextWindowRelativeDetectionAt;
                var state = DetectReadyScreenStateFast(
                    input,
                    includeWindowRelativeDetection,
                    out var detectedViaWindowRelative);
                lastState = state;
                if (includeWindowRelativeDetection)
                {
                    nextWindowRelativeDetectionAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(ReadyStartupWindowRelativeDetectionIntervalMs);
                }

                if (IsReadyScreenState(state))
                {
                    if (detectedViaWindowRelative)
                    {
                        MarkCommandCheckpoint($"ready loop detected {state} via bounded window-relative probe");
                    }

                    return Result(true, nudges, lastState, skipSeconds);
                }

                nextDetectionAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(ReadyStartupDetectionIntervalMs);
            }

            if (lastState == ReadyScreenState.ConnectingToBattleNet)
            {
                // Do not press Escape here - that can cancel a real login handshake - but keep
                // sending the same click/G/Space/Enter burst used for the post-intro splash.
                // The classifier can temporarily mistake the plain splash for this modal, and
                // going silent in that case strands the VM exactly where a keypress is needed.
                SendReadySplashContinueBurst(input);
                nudges++;
            }
            else if (lastState is ReadyScreenState.DiabloSplash or ReadyScreenState.CharacterMenu)
            {
                SendReadySplashContinueBurst(input);
                nudges++;
            }
            else
            {
                SendReadySkipBurst(input);
                nudges++;
            }

            if (keepLaunchAlive)
            {
                NudgeD2RLaunchDuringReady(input, launchNudges);
            }

            if (now >= nextProcessCheckAt)
            {
                var d2rNamedProcessRunning = IsD2RNamedProcessRunning();
                if (d2rNamedProcessRunning)
                {
                    sawD2RProcessRunning = true;
                }
                else if (sawD2RProcessRunning)
                {
                    // Do not fail the ready loop on a transient exact-name miss. Live VM runs
                    // can still be visibly sitting on the D2R splash while this cheap process
                    // probe briefly says no, especially around Battle.net handoff/startup.
                    // Keep pumping input and let the launch nudge retry if D2R really exited.
                    sawD2RProcessRunning = false;
                }

                nextProcessCheckAt = now + TimeSpan.FromMilliseconds(ReadyStartupProcessCheckIntervalMs);
            }

            var remainingMs = Math.Max((deadline - DateTimeOffset.UtcNow).TotalMilliseconds, 0);
            if (remainingMs == 0)
            {
                break;
            }

            await Task.Delay((int)Math.Min(intervalMs, remainingMs), cancellationToken);
        }

        return Result(false, nudges, lastState, skipSeconds);
    }

    private async Task<ReadyWaitResult> RunStartupReadyInputPlanUntilCharacterScreenAsync(
        WindowsInput input,
        CancellationToken cancellationToken,
        bool keepLaunchAlive = false)
    {
        var plan = StartupReadyInputPlan.FromConfig(_config.Ui);
        var timeoutSeconds = GetReadyStartupSkipSeconds();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        var lastState = ReadyScreenState.Unknown;
        var nudges = 0;
        var nextDetectionAt = DateTimeOffset.UtcNow;
        var nextWindowRelativeDetectionAt = DateTimeOffset.UtcNow;
        var nextProcessCheckAt = DateTimeOffset.UtcNow;
        var sawD2RProcessRunning = false;
        var launchNudges = CreateReadyLaunchNudgeState();

        ReadyWaitResult Result(bool ready, int nudges, ReadyScreenState state, int timeout, bool processExitedDuringWait = false)
        {
            return new ReadyWaitResult(
                ready,
                nudges,
                state,
                timeout,
                processExitedDuringWait,
                launchNudges.LaunchAttempts,
                launchNudges.PlayClicks,
                launchNudges.LastLaunchMessage);
        }

        for (var i = 0; i < plan.IntroClickCount && DateTimeOffset.UtcNow < deadline; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;

            // Detect before choosing/sending this iteration's burst - see the comment in
            // PumpStartupSkipInputsUntilCharacterScreenAsync for why this order matters
            // (a stale lastState can send Escape+Enter at an already-reached character
            // screen and accidentally confirm an exit dialog).
            if (now >= nextDetectionAt)
            {
                var includeWindowRelativeDetection = now >= nextWindowRelativeDetectionAt;
                lastState = DetectReadyScreenStateFast(
                    input,
                    includeWindowRelativeDetection,
                    out var detectedViaWindowRelative);
                if (includeWindowRelativeDetection)
                {
                    nextWindowRelativeDetectionAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(ReadyStartupWindowRelativeDetectionIntervalMs);
                }

                if (IsReadyScreenState(lastState))
                {
                    if (detectedViaWindowRelative)
                    {
                        MarkCommandCheckpoint($"startup plan detected {lastState} via bounded window-relative probe");
                    }

                    return Result(true, nudges, lastState, timeoutSeconds);
                }

                nextDetectionAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(ReadyStartupDetectionIntervalMs);
            }

            if (lastState == ReadyScreenState.ConnectingToBattleNet)
            {
                // No Escape, but do keep nudging. A false positive here otherwise freezes the
                // post-intro splash until the ready command times out.
                SendReadySplashContinueBurst(input);
                nudges++;
            }
            else if (lastState is ReadyScreenState.DiabloSplash or ReadyScreenState.CharacterMenu)
            {
                SendReadySplashContinueBurst(input);
                nudges++;
            }
            else
            {
                SendReadyIntroClick(input);
                nudges++;
            }

            if (keepLaunchAlive)
            {
                NudgeD2RLaunchDuringReady(input, launchNudges);
            }

            if (now >= nextProcessCheckAt)
            {
                var d2rNamedProcessRunning = IsD2RNamedProcessRunning();
                if (d2rNamedProcessRunning)
                {
                    sawD2RProcessRunning = true;
                }
                else if (sawD2RProcessRunning)
                {
                    sawD2RProcessRunning = false;
                }

                nextProcessCheckAt = now + TimeSpan.FromMilliseconds(ReadyStartupProcessCheckIntervalMs);
            }

            await Task.Delay(plan.IntroClickDelayMs, cancellationToken);
        }

        for (var i = 0; i < plan.TitleScreenKeyPressCount && DateTimeOffset.UtcNow < deadline; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;

            // Detect before choosing/sending this iteration's burst - see the comment in
            // PumpStartupSkipInputsUntilCharacterScreenAsync for why this order matters
            // (a stale lastState can send Escape+Enter at an already-reached character
            // screen and accidentally confirm an exit dialog).
            if (now >= nextDetectionAt)
            {
                var includeWindowRelativeDetection = now >= nextWindowRelativeDetectionAt;
                lastState = DetectReadyScreenStateFast(
                    input,
                    includeWindowRelativeDetection,
                    out var detectedViaWindowRelative);
                if (includeWindowRelativeDetection)
                {
                    nextWindowRelativeDetectionAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(ReadyStartupWindowRelativeDetectionIntervalMs);
                }

                if (IsReadyScreenState(lastState))
                {
                    if (detectedViaWindowRelative)
                    {
                        MarkCommandCheckpoint($"startup plan detected {lastState} via bounded window-relative probe");
                    }

                    return Result(true, nudges, lastState, timeoutSeconds);
                }

                nextDetectionAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(ReadyStartupDetectionIntervalMs);
            }

            if (lastState == ReadyScreenState.ConnectingToBattleNet)
            {
                // Still avoid Escape, but keep sending G/Space/Enter/click in case this is the
                // plain post-intro splash being misread as the login modal.
                SendReadySplashContinueBurst(input);
                nudges++;
            }
            else if (lastState is ReadyScreenState.DiabloSplash or ReadyScreenState.CharacterMenu)
            {
                SendReadySplashContinueBurst(input);
                nudges++;
            }
            else
            {
                SendReadyTitleSkipBurst(input);
                nudges++;
            }

            if (keepLaunchAlive)
            {
                NudgeD2RLaunchDuringReady(input, launchNudges);
            }

            if (now >= nextProcessCheckAt)
            {
                var d2rNamedProcessRunning = IsD2RNamedProcessRunning();
                if (d2rNamedProcessRunning)
                {
                    sawD2RProcessRunning = true;
                }
                else if (sawD2RProcessRunning)
                {
                    sawD2RProcessRunning = false;
                }

                nextProcessCheckAt = now + TimeSpan.FromMilliseconds(ReadyStartupProcessCheckIntervalMs);
            }

            await Task.Delay(plan.TitleScreenKeyPressDelayMs, cancellationToken);
        }

        lastState = DetectReadyScreenStateFast(
            input,
            includeWindowRelativeDetection: true,
            out var finalDetectedViaWindowRelative);
        if (IsReadyScreenState(lastState) && finalDetectedViaWindowRelative)
        {
            MarkCommandCheckpoint($"startup plan final check detected {lastState} via bounded window-relative probe");
        }

        return Result(
            IsReadyScreenState(lastState),
            nudges,
            lastState,
            timeoutSeconds);
    }

    private void SendReadyIntroClick(WindowsInput input)
    {
        var introPoint = GetUiPoint(D2RUiCoordinateTarget.IntroSkipPoint);
        var target = input.ResolveScreenPoint(introPoint);
        var beforeCursor = input.GetCursorPosition();
        foreach (var action in StartupReadyInputPlan.IntroActions)
        {
            switch (action)
            {
                case StartupReadyInputAction.FocusD2R:
                    TryReadyInputAction(() => _ = TryPrepareD2RForInputBounded(input));
                    break;
                case StartupReadyInputAction.ClickWindowCenter:
                    TryReadyInputAction(() => _ = TryClickD2RWindowCenter(input));
                    break;
                case StartupReadyInputAction.PressEscapeKey:
                    TryReadyInputAction(input.PressEscape);
                    break;
                case StartupReadyInputAction.SendWindowEscapeKey:
                    TryD2RWindowReadyInputAction(() => _ = input.SendWindowEscapeKey(GetD2RProcessNames()));
                    break;
                case StartupReadyInputAction.ClickIntroPoint:
                    TryReadyInputAction(() => ClickD2RDesktopOnly(input, introPoint));
                    break;
                case StartupReadyInputAction.SendWindowClickIntroPoint:
                    TryD2RWindowReadyInputAction(() => _ = input.SendWindowClick(introPoint, GetD2RProcessNames(), MouseButton.Left));
                    break;
                case StartupReadyInputAction.SendWindowReadyBurst:
                    TryD2RWindowReadyInputAction(() => _ = input.SendWindowReadyBurst(GetD2RProcessNames(), introPoint, includeEscape: true));
                    break;
                case StartupReadyInputAction.PressStartupSkipKey:
                    TryReadyInputAction(input.PressStartupSkipKey);
                    break;
                case StartupReadyInputAction.PressStartKey:
                    TryReadyInputAction(input.PressStartKey);
                    break;
                case StartupReadyInputAction.SendWindowStartupSkipKey:
                    TryD2RWindowReadyInputAction(() => _ = input.SendWindowReadySkipKey(GetD2RProcessNames()));
                    break;
            }
        }

        var afterCursor = input.GetCursorPosition();
        RecordD2RInputAction(
            kind: "key",
            button: "G", // IntroActions only ever sends G now - see StartupReadyInputPlan.cs
            point: introPoint,
            target,
            beforeCursor,
            afterCursor,
            beforeDiagnostics: null,
            afterDiagnostics: null);
    }

    private bool IsD2RForeground()
    {
        return TryGetD2RInputDiagnostics()?.IsForeground == true;
    }

    private void SendReadyTitleSkipBurst(WindowsInput input)
    {
        var introPoint = GetUiPoint(D2RUiCoordinateTarget.IntroSkipPoint);
        var target = input.ResolveScreenPoint(introPoint);
        var beforeCursor = input.GetCursorPosition();
        foreach (var action in StartupReadyInputPlan.TitleActions)
        {
            switch (action)
            {
                case StartupReadyInputAction.FocusD2R:
                    TryReadyInputAction(() => _ = TryPrepareD2RForInputBounded(input));
                    break;
                case StartupReadyInputAction.ClickWindowCenter:
                    TryReadyInputAction(() => _ = TryClickD2RWindowCenter(input));
                    break;
                case StartupReadyInputAction.PressStartupSkipKey:
                    TryReadyInputAction(input.PressStartupSkipKey);
                    break;
                case StartupReadyInputAction.PressStartKey:
                    TryReadyInputAction(input.PressStartKey);
                    break;
                case StartupReadyInputAction.SendWindowStartupSkipKey:
                    TryD2RWindowReadyInputAction(() => _ = input.SendWindowReadySkipKey(GetD2RProcessNames()));
                    break;
                case StartupReadyInputAction.SendWindowReadyBurst:
                    // Escape is deliberately NOT sent here. The dedicated intro burst
                    // (SendReadyIntroClick) already gets a generous, time-bounded chance to
                    // Escape-skip the cinematic; by the time the title-skip burst is running,
                    // that budget is spent. Live runs showed the classifier can stay stuck on
                    // "Unknown" at an already-reached character screen for a minute or more
                    // (detection blind spot, not a real cinematic), and Escape there can open
                    // D2R's exit-confirmation dialog - with the Space/Enter sent moments later
                    // in this same burst risking confirming it and exiting back to title.
                    TryD2RWindowReadyInputAction(() => _ = input.SendWindowReadyBurst(GetD2RProcessNames(), introPoint, includeEscape: false));
                    break;
            }
        }

        var afterCursor = input.GetCursorPosition();
        RecordD2RInputAction(
            kind: "key",
            button: "G", // TitleActions only ever sends G now - see StartupReadyInputPlan.cs
            point: introPoint,
            target,
            beforeCursor,
            afterCursor,
            beforeDiagnostics: null,
            afterDiagnostics: null);
    }

    private void SendReadySplashContinueBurst(WindowsInput input)
    {
        var introPoint = GetUiPoint(D2RUiCoordinateTarget.IntroSkipPoint);
        var target = input.ResolveScreenPoint(introPoint);
        var beforeCursor = input.GetCursorPosition();
        foreach (var action in StartupReadyInputPlan.SplashActions)
        {
            switch (action)
            {
                case StartupReadyInputAction.FocusD2R:
                    TryReadyInputAction(() => _ = TryPrepareD2RForInputBounded(input));
                    break;
                case StartupReadyInputAction.ClickWindowCenter:
                    TryReadyInputAction(() => _ = TryClickD2RWindowCenter(input));
                    break;
                case StartupReadyInputAction.ClickIntroPoint:
                    TryReadyInputAction(() => ClickD2RDesktopOnly(input, introPoint));
                    break;
                case StartupReadyInputAction.SendWindowClickIntroPoint:
                    TryD2RWindowReadyInputAction(() => _ = input.SendWindowClick(introPoint, GetD2RProcessNames(), MouseButton.Left));
                    break;
                case StartupReadyInputAction.PressStartupSkipKey:
                    TryReadyInputAction(input.PressStartupSkipKey);
                    break;
                case StartupReadyInputAction.PressStartKey:
                    TryReadyInputAction(input.PressStartKey);
                    break;
                case StartupReadyInputAction.SendWindowStartupSkipKey:
                    TryD2RWindowReadyInputAction(() => _ = input.SendWindowReadySkipKey(GetD2RProcessNames()));
                    break;
                case StartupReadyInputAction.SendWindowReadyBurst:
                    TryD2RWindowReadyInputAction(() => _ = input.SendWindowReadyBurst(GetD2RProcessNames(), introPoint, includeEscape: false));
                    break;
            }
        }

        var afterCursor = input.GetCursorPosition();
        RecordD2RInputAction(
            kind: "key",
            button: "SplashContinue/G", // SplashActions only ever clicks + sends G now
            point: introPoint,
            target,
            beforeCursor,
            afterCursor,
            beforeDiagnostics: null,
            afterDiagnostics: null);
    }

    private void SendReadySkipBurst(WindowsInput input)
    {
        var introPoint = GetUiPoint(D2RUiCoordinateTarget.IntroSkipPoint);
        var target = input.ResolveScreenPoint(introPoint);
        var beforeCursor = input.GetCursorPosition();
        foreach (var action in StartupReadyInputPlan.BurstActions)
        {
            switch (action)
            {
                case StartupReadyInputAction.FocusD2R:
                    TryReadyInputAction(() => _ = TryPrepareD2RForInputBounded(input));
                    break;
                case StartupReadyInputAction.ClickWindowCenter:
                    TryReadyInputAction(() => _ = TryClickD2RWindowCenter(input));
                    break;
                case StartupReadyInputAction.ClickIntroPoint:
                    TryReadyInputAction(() => ClickD2RDesktopOnly(input, introPoint));
                    break;
                case StartupReadyInputAction.SendWindowClickIntroPoint:
                    TryD2RWindowReadyInputAction(() => _ = input.SendWindowClick(introPoint, GetD2RProcessNames(), MouseButton.Left));
                    break;
                case StartupReadyInputAction.PressStartupSkipKey:
                    TryReadyInputAction(input.PressStartupSkipKey);
                    break;
                case StartupReadyInputAction.PressStartKey:
                    TryReadyInputAction(input.PressStartKey);
                    break;
                case StartupReadyInputAction.SendWindowStartupSkipKey:
                    TryD2RWindowReadyInputAction(() => _ = input.SendWindowReadySkipKey(GetD2RProcessNames()));
                    break;
                case StartupReadyInputAction.SendWindowReadyBurst:
                    // Unreachable - BurstActions no longer includes this. Kept so the switch
                    // still compiles exhaustively if a future plan ever re-adds it; see
                    // StartupReadyInputPlan.cs for why Escape/Space/Enter were removed.
                    TryD2RWindowReadyInputAction(() => _ = input.SendWindowReadyBurst(GetD2RProcessNames(), introPoint, includeEscape: false));
                    break;
            }
        }

        var afterCursor = input.GetCursorPosition();
        RecordD2RInputAction(
            kind: "key",
            button: "G", // BurstActions only ever clicks + sends G now - see StartupReadyInputPlan.cs
            point: introPoint,
            target,
            beforeCursor,
            afterCursor,
            beforeDiagnostics: null,
            afterDiagnostics: null);
    }

    private static void TryReadyInputAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Startup screens are exactly where process windows appear, disappear, and
            // briefly reject HWND input. One failing route must not suppress the rest
            // of the burst, especially the global keypresses that skip cinematics.
        }
    }

    private void TryD2RWindowReadyInputAction(Action action)
    {
        if (!IsD2RNamedProcessRunning())
        {
            return;
        }

        TryReadyInputAction(action);
    }

    private static void ClickD2RDesktopOnly(WindowsInput input, AgentCommon.UiPoint point)
    {
        input.LeftClick(point);
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
        // Fire the visible desktop click against the full VM screen first. The supported VM
        // layout is 1366x768 with D2R full-screen, and this path avoids blocking on slow
        // process/window lookup before the click. The HWND-targeted fallback below still uses
        // D2R's client rect when a window handle is available.
        var processNames = GetD2RProcessNames();
        var target = input.ResolveScreenPoint(point);
        var beforeCursor = input.GetCursorPosition();
        if (button == MouseButton.Left)
        {
            input.LeftClick(point);
        }
        else
        {
            input.RightClick(point);
        }

        // SendInput above only lands if D2R is genuinely the foreground/topmost window at these
        // screen coordinates - the same focus dependency that "fg lost" in the watch log already
        // caught happening for real. The intro/title skip bursts never rely on SendInput alone;
        // they always also post the click straight to D2R's HWND via SendWindowClick, which works
        // regardless of focus or z-order. Every other click in the lobby/create-game flow only
        // ever went through SendInput, so once focus is wrong post-character-screen, every one of
        // those clicks silently lands nowhere. Back it up the same way the intro path does.
        _ = input.SendWindowClick(point, processNames, button);

        var afterCursor = input.GetCursorPosition();
        RecordD2RInputAction(
            kind: "click",
            button: button.ToString(),
            point,
            target,
            beforeCursor,
            afterCursor,
            beforeDiagnostics: null,
            afterDiagnostics: null);
    }

    private void ClickD2RStatefulToggle(
        WindowsInput input,
        AgentCommon.UiPoint point,
        MouseButton button = MouseButton.Left)
    {
        // The redundant ClickD2R layers are useful for ordinary buttons, but a second delivered
        // click on a toggle (friends drawer, accordion header) immediately reverses the first.
        var target = input.ResolveScreenPoint(point);
        var beforeCursor = input.GetCursorPosition();
        input.VisibleClickOnce(point, button);
        var afterCursor = input.GetCursorPosition();
        RecordD2RInputAction(
            kind: "stateful-toggle-click",
            button: button.ToString(),
            point,
            target,
            beforeCursor,
            afterCursor,
            beforeDiagnostics: null,
            afterDiagnostics: null);
    }

    // Both the status path (DetectVisibleD2RState) and the ready loop (DetectReadyScreenStateStable)
    // run their own classifier passes on independent timers, and CollectProcessOnlyStatus's fast
    // fallback never recomputes a screen state at all - it just hardcodes Unknown. Stamping every
    // classifier result here, regardless of which caller produced it, gives /d2r status a live
    // "what did we last actually see" answer even while detailed status collection is stuck.
    private void RecordObservedFrame(string frame)
    {
        _lastObservedFrame = frame;
        _lastObservedFrameUtc = DateTimeOffset.UtcNow;
    }

    private void RecordClassifierBreakdown(string breakdown)
    {
        if (string.IsNullOrWhiteSpace(breakdown))
        {
            return;
        }

        _lastClassifierBreakdown = breakdown;
        _lastClassifierBreakdownUtc = DateTimeOffset.UtcNow;
    }

    private void RecordHudEvidence(string evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return;
        }

        _lastHudEvidence = evidence;
        _lastHudEvidenceUtc = DateTimeOffset.UtcNow;
    }

    // The watch ticker could only ever show *where* a stuck command was (checkpoints), never
    // *what it actually saw* - "expected {x,y,z}, lastgrab {a,b,c}" was the explicit ask after
    // checkpoints alone proved unconvincing. Called from inside the same throttle window
    // IsInGameReady already samples on, so this records real ground truth at roughly the same
    // cadence as the real decision, not a separate/unrelated sampling pass.
    private string RecordLiveHudEvidence(WindowsInput input)
    {
        var screenEvidence = TryRunBounded(() => SampleInGameHudEvidence(input, windowRelative: false), InGameHudSampleBoundMs, EmptyInGameHudEvidence());
        var windowEvidence = TryRunBounded(() => SampleInGameHudEvidence(input, windowRelative: true), InGameHudSampleBoundMs, EmptyInGameHudEvidence());
        var screenReady = IsInGameHudEvidenceReady(screenEvidence);
        var windowReady = IsInGameHudEvidenceReady(windowEvidence);
        var summary = FormatInGameEvidence(screenReady, windowReady, screenEvidence, windowEvidence);
        RecordHudEvidence(summary);
        return summary;
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
        MarkCommandCheckpoint("FormatInputDiagnosticsSuffix: collecting input diagnostics");
        var diagnostics = TryGetD2RInputDiagnostics();
        MarkCommandCheckpoint("FormatInputDiagnosticsSuffix: input diagnostics collected");
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

    private async Task<CommandResult?> EnsureLobbyOpenedAsync(
        WindowsInput input,
        MenuCommandArgs args,
        CancellationToken cancellationToken)
    {
        MarkCommandCheckpoint("EnsureLobbyOpenedAsync: start");
        var activity = GetActivitySnapshot();
        if (activity.State == D2RActivityState.LobbyOrGame)
        {
            MarkLobbyOrGameInteraction("Using remembered lobby/game state for menu automation.");
            return null;
        }

        if (activity.State == D2RActivityState.CharacterScreenIdle)
        {
            MarkCommandCheckpoint("EnsureLobbyOpenedAsync: remembered-character-screen click");
            await ClickLobbyFromRememberedCharacterScreenAsync(input, cancellationToken);
            MarkLobbyOrGameInteraction("Clicked Lobby from remembered character screen.");
            return null;
        }

        MarkCommandCheckpoint("EnsureLobbyOpenedAsync: direct character slot/lobby click");
        await SelectCharacterAsync(input, args.CharacterSlot, cancellationToken);
        await ClickLobbyDirectAsync(input, cancellationToken);
        MarkLobbyOrGameInteraction("Clicked Lobby without visual verification.");
        return null;
    }

    private async Task<string> WaitForPostSaveExitMenuAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Clamp(
            Math.Max(Math.Max(_config.Ui.GameLoadSeconds, _config.Ui.LobbyLoadSeconds), 3),
            3,
            12);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsAnyLobbyEntryMenuVisible(input))
            {
                MarkLobbyOrGameInteraction("Save and Exit returned to the lobby.");
                return "returned to the lobby";
            }

            if (IsCharacterScreenReady(input) || IsCharacterScreenOffline(input))
            {
                MarkCharacterScreenIdle("Save and Exit returned to the character screen.");
                return "returned to the character screen";
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                MarkD2RActivityUnknown("Save and Exit completed, but the post-exit menu state was not detected.");
                return "post-exit menu state was not visually confirmed";
            }

            await Task.Delay(EntryPollIntervalMs, cancellationToken);
        }
    }

    private async Task ClickLobbyFromRememberedCharacterScreenAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClickD2R(input, GetUiPoint(D2RUiCoordinateTarget.CharacterLobbyButton));
        await DelayLongAsync(cancellationToken);
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
        CancellationToken cancellationToken,
        bool guardAgainstInGame = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ShouldSkipMenuClickForInGameSafety(guardAgainstInGame, () => MightAlreadyBeInGame(input)))
        {
            MarkCommandCheckpoint("ClickLobbyDirectAsync: skipped click, might already be in-game");
            return true;
        }

        ClickD2R(input, GetUiPoint(D2RUiCoordinateTarget.CharacterLobbyButton));
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_config.Ui.LobbyLoadSeconds, 1, 4)), cancellationToken);

        return true;
    }

    private async Task ClickLobbyTabDirectAsync(
        WindowsInput input,
        AgentCommon.UiPoint tab,
        CancellationToken cancellationToken,
        bool guardAgainstInGame = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ShouldSkipMenuClickForInGameSafety(guardAgainstInGame, () => MightAlreadyBeInGame(input)))
        {
            MarkCommandCheckpoint("ClickLobbyTabDirectAsync: skipped click, might already be in-game");
            return;
        }

        MarkCommandCheckpoint("ClickLobbyTabDirectAsync: first ClickD2R");
        ClickD2R(input, tab);
        await DelayStepAsync(cancellationToken);
        MarkCommandCheckpoint("ClickLobbyTabDirectAsync: second ClickD2R");
        ClickD2R(input, tab);
        await DelayStepAsync(cancellationToken);
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
        var iteration = 0;
        var legacyToggle = new LegacyGraphicsToggleState();
        DateTimeOffset GetBroadHudFrameAcceptAt() =>
            DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Clamp(_config.Ui.GameLoadSeconds, 3, 8));

        MarkCommandCheckpoint("ClickMenuEntryButtonUntilEnteredGameAsync: initial click");
        await ClickMenuEntryButtonAsync(input, button, cancellationToken);
        var broadHudFrameAcceptAt = GetBroadHudFrameAcceptAt();
        var entryReclickAt = broadHudFrameAcceptAt;
        var blindEntryReclicks = 0;

        async Task<GameEntryAttemptResult?> TryConfirmAtElapsedDeadlineAsync(string checkpointContext)
        {
            if (DateTimeOffset.UtcNow < deadline)
            {
                return null;
            }

            if (!await TryConfirmEnteredGameAsync(
                    input,
                    cancellationToken,
                    legacyToggle,
                    checkpointContext,
                    broadHudFrameAcceptAt,
                    forceFreshSample: true))
            {
                RecordClassifierBreakdown(TryRunBounded(() => ComputeVisibleStateClassifierBreakdown(input, MenuSampleGrid), ClassifierBreakdownBoundMs, ""));
                return null;
            }

            MarkCommandCheckpoint($"{checkpointContext}: entered game confirmed");
            return new GameEntryAttemptResult(true, dialogRetries, connectionRetries, "Entered game at timeout boundary.");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iteration++;
            // Live runs (watch-xyz1-20260624-195749.log, watch-nq1-20260624-203207.log) showed
            // the checkpoint frozen at "initial click" for 46s-2m+ with the next checkpoint
            // (WaitForGameEntryAsync) never appearing even once. Every call in between is either
            // PostMessage/SendMessageTimeout-bounded or a pure GDI pixel read - nothing here
            // should structurally block that long. This checkpoint exists to find out which:
            // if iteration stays 1 here for the whole stuck window, the GDI pixel sampling
            // itself (SampleD2RRegion/GetPixel, unbounded, unlike the focus-steal path) is the
            // actual blocking call - plausibly because D2R is hammering the VM's GPU/CPU loading
            // the just-created/joined level, the same RAM/VRAM load lag already confirmed after
            // intro-skip, just recurring here. If iteration climbs instead, the loop is cycling
            // fine and WaitForGameEntryAsync's own internal poll is where the time really goes.
            MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: loop iteration {iteration}, checking entry");

            if (DateTimeOffset.UtcNow < entryReclickAt)
            {
                MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: loop iteration {iteration}, waiting entry grace before HUD check");
                var graceRemainingMs = Math.Max((entryReclickAt - DateTimeOffset.UtcNow).TotalMilliseconds, 0);
                await Task.Delay((int)Math.Min(EntryPollIntervalMs, graceRemainingMs), cancellationToken);
                continue;
            }

            if (blindEntryReclicks == 0)
            {
                // Avoid visible-menu pixel probes here; live runs showed GDI sampling can stall
                // during D2R's entry-load spike before it can tell us whether the menu remains.
                blindEntryReclicks++;
                MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: entry grace elapsed (iteration {iteration}), blind re-clicking entry button");
                await ClickMenuEntryButtonAsync(input, button, cancellationToken, guardAgainstInGame: true);
                broadHudFrameAcceptAt = GetBroadHudFrameAcceptAt();
                entryReclickAt = broadHudFrameAcceptAt;
                continue;
            }

            if (await TryConfirmEnteredGameAsync(
                    input,
                    cancellationToken,
                    legacyToggle,
                    $"ClickMenuEntryButtonUntilEnteredGameAsync: loop iteration {iteration}",
                    broadHudFrameAcceptAt))
            {
                MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: entered game confirmed (iteration {iteration})");
                return new GameEntryAttemptResult(true, dialogRetries, connectionRetries, "Entered game.");
            }

            MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: loop iteration {iteration}, checking game-entry error dialog");
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

            MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: loop iteration {iteration}, checking connection interruption");
            if (IsConnectionInterruptedScreen(input))
            {
                connectionRetries++;
                MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: connection interrupted (retry {connectionRetries}), waiting for bounce-back menu");
                if (!await WaitForMenuAfterConnectionInterruptedAsync(input, activeTab, cancellationToken)
                    || !await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "Connection was interrupted, but the menu form could not be restored.");
                }

                deadline = DateTimeOffset.UtcNow + timeout;
                MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: re-clicking entry button after interruption (retry {connectionRetries})");
                await ClickMenuEntryButtonAsync(input, button, cancellationToken, guardAgainstInGame: true);
                broadHudFrameAcceptAt = GetBroadHudFrameAcceptAt();
                entryReclickAt = broadHudFrameAcceptAt;
                blindEntryReclicks = 0;
                continue;
            }

            var elapsedDeadlineResult = await TryConfirmAtElapsedDeadlineAsync(
                $"ClickMenuEntryButtonUntilEnteredGameAsync: timeout boundary after connection check (iteration {iteration})");
            if (elapsedDeadlineResult is not null)
            {
                return elapsedDeadlineResult;
            }

            MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: loop iteration {iteration}, checking offline character screen");
            var returnedToOfflineCharacterScreen = IsCharacterScreenOffline(input);
            if (returnedToOfflineCharacterScreen)
            {
                MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: returned to offline character screen (iteration {iteration}), recovering");
                if (!await EnsureOnlineCharacterScreenAsync(input, cancellationToken)
                    || !await ClickLobbyDirectAsync(input, cancellationToken, guardAgainstInGame: true)
                    || !await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned to the offline character screen, and the menu form could not be restored after clicking Online.");
                }
            }

            elapsedDeadlineResult = await TryConfirmAtElapsedDeadlineAsync(
                $"ClickMenuEntryButtonUntilEnteredGameAsync: timeout boundary after offline-screen check (iteration {iteration})");
            if (elapsedDeadlineResult is not null)
            {
                return elapsedDeadlineResult;
            }

            if (!returnedToOfflineCharacterScreen)
            {
                MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: loop iteration {iteration}, checking character select");
                if (IsCharacterScreenReady(input))
                {
                    MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: returned to character select (iteration {iteration}), recovering");
                    if (!await ClickLobbyDirectAsync(input, cancellationToken, guardAgainstInGame: true)
                        || !await restoreFormAsync())
                    {
                        return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned to character select, but the menu form could not be restored.");
                    }
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                if (await TryConfirmEnteredGameAsync(
                        input,
                        cancellationToken,
                        legacyToggle,
                        $"ClickMenuEntryButtonUntilEnteredGameAsync: timeout boundary (iteration {iteration})",
                        broadHudFrameAcceptAt,
                        forceFreshSample: true))
                {
                    MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: entered game confirmed at timeout boundary (iteration {iteration})");
                    return new GameEntryAttemptResult(true, dialogRetries, connectionRetries, "Entered game at timeout boundary.");
                }

                return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, FormatEntryTimeoutMessage(input, activeTab, dialogRetries, connectionRetries));
            }

            MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: WaitForGameEntryAsync (iteration {iteration})");
            var waitResult = await WaitForGameEntryAsync(input, activeTab, cancellationToken, legacyToggle, broadHudFrameAcceptAt);
            if (waitResult == GameEntryWaitResult.EnteredGame)
            {
                return new GameEntryAttemptResult(true, dialogRetries, connectionRetries, "Entered game.");
            }

            if (await TryConfirmEnteredGameAsync(
                    input,
                    cancellationToken,
                    legacyToggle,
                    $"ClickMenuEntryButtonUntilEnteredGameAsync: after wait result (iteration {iteration})",
                    broadHudFrameAcceptAt))
            {
                MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: entered game confirmed after wait result (iteration {iteration})");
                return new GameEntryAttemptResult(true, dialogRetries, connectionRetries, "Entered game after wait result.");
            }

            if (waitResult == GameEntryWaitResult.ConnectionInterrupted)
            {
                connectionRetries++;
                MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: connection interrupted (retry {connectionRetries}), waiting for bounce-back menu");
                if (!await WaitForMenuAfterConnectionInterruptedAsync(input, activeTab, cancellationToken)
                    || !await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "Connection was interrupted, but the menu form could not be restored.");
                }

                deadline = DateTimeOffset.UtcNow + timeout;
                MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: re-clicking entry button after interruption (retry {connectionRetries})");
                await ClickMenuEntryButtonAsync(input, button, cancellationToken, guardAgainstInGame: true);
                broadHudFrameAcceptAt = GetBroadHudFrameAcceptAt();
                entryReclickAt = broadHudFrameAcceptAt;
                blindEntryReclicks = 0;
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
                    || !await ClickLobbyDirectAsync(input, cancellationToken, guardAgainstInGame: true)
                    || !await restoreFormAsync())
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned to the offline character screen, and the menu form could not be restored after clicking Online.");
                }
            }
            else if (waitResult == GameEntryWaitResult.ReturnedToCharacterScreen)
            {
                if (!await ClickLobbyDirectAsync(input, cancellationToken, guardAgainstInGame: true)
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
                await ClickMenuEntryButtonAsync(input, button, cancellationToken, guardAgainstInGame: true);
                broadHudFrameAcceptAt = GetBroadHudFrameAcceptAt();
                entryReclickAt = broadHudFrameAcceptAt;
                blindEntryReclicks = 0;
                continue;
            }

            if (DateTimeOffset.UtcNow < deadline)
            {
                await ClickMenuEntryButtonAsync(input, button, cancellationToken, guardAgainstInGame: true);
                broadHudFrameAcceptAt = GetBroadHudFrameAcceptAt();
                entryReclickAt = broadHudFrameAcceptAt;
                blindEntryReclicks = 0;
            }
        }
    }

    private async Task ClickMenuEntryButtonAsync(
        WindowsInput input,
        AgentCommon.UiPoint button,
        CancellationToken cancellationToken,
        bool guardAgainstInGame = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ShouldSkipMenuClickForInGameSafety(guardAgainstInGame, () => MightAlreadyBeInGame(input)))
        {
            MarkCommandCheckpoint("ClickMenuEntryButtonAsync: skipped click, might already be in-game");
            return;
        }

        ClickD2R(input, button);
        await DelayFastMenuAsync(cancellationToken);
    }

    private async Task<bool> DismissGameEntryErrorDialogAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ClickD2R(input, GetUiPoint(D2RUiCoordinateTarget.GameEntryErrorDialogOkButton));
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
        CancellationToken cancellationToken,
        bool guardAgainstInGame = false)
    {
        MarkCommandCheckpoint("RestoreJoinGameFormAsync: click Join Game tab");
        await ClickLobbyTabDirectAsync(input, GetUiPoint(D2RUiCoordinateTarget.JoinGameTab), cancellationToken, guardAgainstInGame);

        MarkCommandCheckpoint("RestoreJoinGameFormAsync: select difficulty");
        await SelectJoinDifficultyAsync(input, args.Difficulty, cancellationToken, guardAgainstInGame);
        MarkCommandCheckpoint("RestoreJoinGameFormAsync: fill game name");
        await FillTextFieldAsync(input, GetUiPoint(D2RUiCoordinateTarget.JoinGameNameField), args.GameName ?? "", cancellationToken, guardAgainstInGame);
        MarkCommandCheckpoint("RestoreJoinGameFormAsync: fill password");
        await FillTextFieldAsync(input, GetUiPoint(D2RUiCoordinateTarget.JoinPasswordField), args.Password ?? "", cancellationToken, guardAgainstInGame);
        return true;
    }

    private async Task<bool> RestoreCreateGameFormAsync(
        WindowsInput input,
        MenuCommandArgs args,
        CancellationToken cancellationToken,
        bool guardAgainstInGame = false)
    {
        MarkCommandCheckpoint("RestoreCreateGameFormAsync: click Create Game tab");
        await ClickLobbyTabDirectAsync(input, GetUiPoint(D2RUiCoordinateTarget.CreateGameTab), cancellationToken, guardAgainstInGame);

        MarkCommandCheckpoint("RestoreCreateGameFormAsync: fill game name");
        await FillTextFieldAsync(input, GetUiPoint(D2RUiCoordinateTarget.CreateGameNameField), args.GameName ?? "", cancellationToken, guardAgainstInGame);
        MarkCommandCheckpoint("RestoreCreateGameFormAsync: fill password");
        await FillTextFieldAsync(input, GetUiPoint(D2RUiCoordinateTarget.CreatePasswordField), args.Password ?? "", cancellationToken, guardAgainstInGame);
        MarkCommandCheckpoint("RestoreCreateGameFormAsync: select difficulty");
        await SelectCreateDifficultyAsync(input, args.Difficulty, cancellationToken, guardAgainstInGame);
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
        // watch-xkewfuj5-20260625-180228.log: stuck 5+ minutes at the "HUD not ready" checkpoint
        // immediately before this call, on v0.2.87 which already bounds every IsLobby* call this
        // function (via FormatGameEntryMenuDiagnostics) reaches - so by the numbers that path
        // can't take more than ~9s. These checkpoints exist to find out which specific step is
        // actually stuck on the next failure instead of inferring it again.
        MarkCommandCheckpoint("FormatEntryTimeoutMessage: formatting menu diagnostics");
        var menuDiagnostics = FormatGameEntryMenuDiagnostics(input, activeTab);
        MarkCommandCheckpoint("FormatEntryTimeoutMessage: formatting input diagnostics");
        var diagnostics = $"{menuDiagnostics}{FormatInputDiagnosticsSuffix()}";
        MarkCommandCheckpoint("FormatEntryTimeoutMessage: diagnostics formatted");
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

    private string FormatGameEntryMenuDiagnostics(WindowsInput input, AgentCommon.UiPoint activeTab)
    {
        MarkCommandCheckpoint("FormatGameEntryMenuDiagnostics: checking lobby tab");
        var tab = IsLobbyTabReady(input, activeTab);
        MarkCommandCheckpoint("FormatGameEntryMenuDiagnostics: checking entry button");
        var entry = IsLobbyEntryButtonReady(input);
        MarkCommandCheckpoint("FormatGameEntryMenuDiagnostics: checking form panel (screen-relative)");
        var formScreen = IsLobbyFormPanelReady(input, windowRelative: false);
        MarkCommandCheckpoint("FormatGameEntryMenuDiagnostics: checking form panel (window-relative)");
        var formWindow = IsLobbyFormPanelReady(input, windowRelative: true);
        MarkCommandCheckpoint("FormatGameEntryMenuDiagnostics: samples collected");
        var visible = D2RScreenClassifier.IsGameEntryMenuVisible(tab, entry, formScreen || formWindow);

        // The checkpoints around this function only prove the command didn't hang - they say
        // nothing about *why* HUD detection failed, which was the actual open question
        // ("debug what it sees vs what it expects to see," not just where it is). Every other
        // diagnosis this session that actually went somewhere used real numbers (sitting_in_town's
        // red=0.54/blue=0.63 etc., measured from a screenshot via a throwaway test) - this puts
        // those same numbers directly into the live failure message instead of requiring a
        // separate screenshot and a manual test run after the fact.
        MarkCommandCheckpoint("FormatGameEntryMenuDiagnostics: sampling HUD evidence");
        var hudEvidenceSummary = RecordLiveHudEvidence(input);

        return $"Menu samples: visible={visible}, tab={tab}, entry={entry}, formScreen={formScreen}, formWindow={formWindow}. "
            + $"HUD pixels: ready={hudEvidenceSummary} "
            + "(hpR/mpB=modern health-red/mana-blue ratio, lhpR/lmpB=legacy equivalents, "
            + "thresholds health>0.20 mana>0.18; bar/bot/ctr=action-bar/bottom/center HUD luminance stats).";
    }

    private static InGameHudEvidence EmptyInGameHudEvidence()
    {
        var empty = new ScreenRegionStats(0, 0, 0, 0, 0, 0, 0, 0, 0);
        return new InGameHudEvidence(empty, empty, empty, empty, empty, empty, empty);
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
        return TryRunBounded(() => DetectReadyScreenState(input) == ReadyScreenState.CharacterScreen, EntryLoopCheckBoundMs);
    }

    private bool IsCharacterScreenOffline(WindowsInput input)
    {
        return TryRunBounded(
            () => IsCharacterScreenOffline(input, windowRelative: false) || IsCharacterScreenOffline(input, windowRelative: true),
            EntryLoopCheckBoundMs);
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

        if (IsCharacterButtonPairReady(input, windowRelative: false, sampleGrid)
            || IsCharacterButtonPairReady(input, windowRelative: true, sampleGrid))
        {
            return ReadyScreenState.CharacterScreen;
        }

        return IsCharacterMenuReady(input, windowRelative: false, sampleGrid)
            || IsCharacterMenuReady(input, windowRelative: true, sampleGrid)
            ? ReadyScreenState.CharacterMenu
            : ReadyScreenState.Unknown;
    }

    private ReadyScreenState DetectReadyScreenStateStable(WindowsInput input)
    {
        var state = DetectReadyScreenState(input);
        var sampleGrid = MenuSampleGrid;
        if (state == ReadyScreenState.Unknown)
        {
            sampleGrid = ReadyStartupSampleGrid;
            state = DetectReadyScreenState(input, sampleGrid);
        }

        if (state == ReadyScreenState.Unknown)
        {
            RecordClassifierBreakdown(TryRunBounded(() => ComputeReadyScreenClassifierBreakdown(input, sampleGrid), ClassifierBreakdownBoundMs, ""));
        }

        RecordObservedFrame(state.ToString());
        return state;
    }

    private ReadyScreenState DetectReadyScreenStateFast(
        WindowsInput input,
        bool includeWindowRelativeDetection,
        out bool detectedViaWindowRelative)
    {
        detectedViaWindowRelative = false;
        var state = DetectReadyScreenStateScreenOnly(input, ReadyStartupSampleGrid);
        if (state == ReadyScreenState.Unknown && includeWindowRelativeDetection)
        {
            state = DetectReadyScreenStateWindowOnlyBounded(input, ReadyStartupSampleGrid);
            detectedViaWindowRelative = IsReadyScreenState(state);
        }

        if (state == ReadyScreenState.Unknown)
        {
            RecordClassifierBreakdown(TryRunBounded(() => ComputeReadyScreenClassifierBreakdown(input, ReadyStartupSampleGrid), ClassifierBreakdownBoundMs, ""));
        }

        RecordObservedFrame(state.ToString());
        return state;
    }

    private ReadyScreenState DetectReadyScreenStateWindowOnlyBounded(WindowsInput input, int sampleGrid)
    {
        var state = ReadyScreenState.Unknown;
        var detected = TryRunBounded(
            () =>
            {
                state = DetectReadyScreenStateWindowOnly(input, sampleGrid);
                return IsReadyScreenState(state);
            },
            ReadyStartupDetectionIntervalMs);

        return detected ? state : ReadyScreenState.Unknown;
    }

    private ReadyScreenState DetectReadyScreenStateScreenOnly(WindowsInput input, int sampleGrid)
    {
        if (IsDiabloSplashScreen(input, sampleGrid))
        {
            return IsConnectingToBattleNetDialog(input, sampleGrid)
                ? ReadyScreenState.ConnectingToBattleNet
                : ReadyScreenState.DiabloSplash;
        }

        if (IsCharacterScreenOffline(input, windowRelative: false, sampleGrid))
        {
            return ReadyScreenState.OfflineCharacterScreen;
        }

        if (IsCharacterButtonPairReady(input, windowRelative: false, sampleGrid))
        {
            return ReadyScreenState.CharacterScreen;
        }

        return IsCharacterMenuReady(input, windowRelative: false, sampleGrid)
            ? ReadyScreenState.CharacterMenu
            : ReadyScreenState.Unknown;
    }

    private ReadyScreenState DetectReadyScreenStateWindowOnly(WindowsInput input, int sampleGrid)
    {
        if (IsCharacterScreenOffline(input, windowRelative: true, sampleGrid))
        {
            return ReadyScreenState.OfflineCharacterScreen;
        }

        if (IsCharacterButtonPairReady(input, windowRelative: true, sampleGrid))
        {
            return ReadyScreenState.CharacterScreen;
        }

        return IsCharacterMenuReady(input, windowRelative: true, sampleGrid)
            ? ReadyScreenState.CharacterMenu
            : ReadyScreenState.Unknown;
    }

    private static bool IsReadyScreenState(ReadyScreenState state)
    {
        return state is ReadyScreenState.CharacterScreen
            or ReadyScreenState.CharacterMenu
            or ReadyScreenState.OfflineCharacterScreen;
    }

    private bool IsCharacterButtonPairReady(WindowsInput input, bool windowRelative, int sampleGrid = MenuSampleGrid)
    {
        var play = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.CharacterPlayButton), widthRatio: 0.13, heightRatio: 0.055, windowRelative: windowRelative, sampleGrid: sampleGrid);
        var lobby = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.CharacterLobbyButton), widthRatio: 0.13, heightRatio: 0.055, windowRelative: windowRelative, sampleGrid: sampleGrid);
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
            var play = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.CharacterPlayButton), widthRatio: 0.13, heightRatio: 0.055, windowRelative: false, sampleGrid: MenuSampleGrid);
            var lobby = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.CharacterLobbyButton), widthRatio: 0.13, heightRatio: 0.055, windowRelative: false, sampleGrid: MenuSampleGrid);
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
        return D2RScreenClassifier.IsDiabloSplashScreen(logo, prompt);
    }

    private bool IsGameEntryErrorDialogOpen(WindowsInput input)
    {
        return TryRunBounded(() =>
        {
            var okButton = input.SampleRegion(GetUiPoint(D2RUiCoordinateTarget.GameEntryErrorDialogOkButton), widthRatio: 0.14, heightRatio: 0.050);
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
        }, EntryLoopCheckBoundMs);
    }

    private bool IsConnectionInterruptedScreen(WindowsInput input)
    {
        return TryRunBounded(() =>
        {
            var screen = input.SampleRegion(new AgentCommon.UiPoint(0.500, 0.500), widthRatio: 0.80, heightRatio: 0.60);
            var text = input.SampleRegion(new AgentCommon.UiPoint(0.500, 0.502), widthRatio: 0.55, heightRatio: 0.08);
            return screen.AverageLuminance < 5
                && screen.DarkRatio > 0.97
                && text.AverageLuminance > 3
                && text.LuminanceStdDev > 15
                && text.GreyRatio > 0.02
                && text.DarkRatio > 0.85;
        }, EntryLoopCheckBoundMs);
    }

    private bool IsLobbyTabReady(WindowsInput input, AgentCommon.UiPoint tab)
    {
        // watch-xigue5-20260625-174035.log: ClickMenuEntryButtonUntilEnteredGameAsync froze
        // forever at its final "timeout boundary: HUD not ready" checkpoint with the command
        // gate still held - the checkpoint right after that point builds the failure message
        // via FormatGameEntryMenuDiagnostics, which called this (and IsLobbyEntryButtonReady/
        // IsLobbyFormPanelReady) raw. Every sibling entry-loop check got bounded in v0.2.83/84
        // for the exact same "GDI sampling can stall under D2R's load spike" reason - this trio
        // was the one left unwrapped, and unlike the others, its only callers ran right when a
        // command was already in trouble (the failure-message formatter and the main lobby
        // detector), the worst possible time for an unbounded GDI call to hang.
        return TryRunBounded(() => IsLobbyTabReady(input, tab, windowRelative: false), EntryLoopCheckBoundMs)
            || TryRunBounded(() => IsLobbyTabReady(input, tab, windowRelative: true), EntryLoopCheckBoundMs);
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
        return TryRunBounded(() => IsLobbyEntryButtonReady(input, windowRelative: false), EntryLoopCheckBoundMs)
            || TryRunBounded(() => IsLobbyEntryButtonReady(input, windowRelative: true), EntryLoopCheckBoundMs);
    }

    private bool IsLobbyEntryButtonReady(WindowsInput input, bool windowRelative)
    {
        var stats = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.CreateGameButton), widthRatio: 0.16, heightRatio: 0.055, windowRelative: windowRelative);
        return D2RScreenClassifier.IsLobbyEntryButtonReady(stats);
    }

    private bool IsLobbyFormPanelReady(WindowsInput input, bool windowRelative)
    {
        return TryRunBounded(() =>
        {
            var stats = SampleD2RRegion(input, new AgentCommon.UiPoint(0.765, 0.365), widthRatio: 0.30, heightRatio: 0.42, windowRelative: windowRelative);
            return stats.AverageLuminance < 30
                && stats.GreyRatio < 0.25
                && stats.DarkRatio > 0.80;
        }, EntryLoopCheckBoundMs);
    }

    private bool IsFriendJoinGameOptionReady(WindowsInput input)
    {
        return IsFriendJoinGameOptionReady(input, windowRelative: false)
            || IsFriendJoinGameOptionReady(input, windowRelative: true);
    }

    private bool IsFriendJoinGameOptionReady(WindowsInput input, bool windowRelative)
    {
        var stats = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.FriendContextJoinGame), widthRatio: 0.12, heightRatio: 0.040, windowRelative: windowRelative);
        return stats.AverageLuminance > 36
            && stats.GreyRatio > 0.56
            && stats.DarkRatio < 0.44;
    }

    private bool IsInGameReady(WindowsInput input)
    {
        return IsInGameReady(input, windowRelative: false)
            || IsInGameReady(input, windowRelative: true);
    }

    // Only the modern/legacy HUD globe profiles - never the broader Frame-kind fallback. The
    // globes are a far more distinctive signal than the generic lobby-tab/entry-button
    // luminance thresholds, which sitting_in_town.png proved can coincidentally match ordinary
    // outdoor scenery. Used to check strict in-game evidence before the lobby check in
    // DetectVisibleD2RState, without touching the deliberately-after-lobby Frame fallback.
    private bool IsInGameReadyStrict(WindowsInput input)
    {
        return DetectInGameHudMatch(input, windowRelative: false) is InGameHudMatchKind.ModernProfile or InGameHudMatchKind.LegacyProfile
            || DetectInGameHudMatch(input, windowRelative: true) is InGameHudMatchKind.ModernProfile or InGameHudMatchKind.LegacyProfile;
    }

    private bool IsInGameReady(WindowsInput input, string checkpointContext, DateTimeOffset? broadHudFrameAcceptAt = null, bool forceFreshSample = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!forceFreshSample && now < _nextInGameHudSampleAt)
        {
            return _lastInGameHudResult;
        }

        _nextInGameHudSampleAt = now + TimeSpan.FromMilliseconds(InGameHudSampleThrottleMs);
        RecordLiveHudEvidence(input);

        MarkCommandCheckpoint($"{checkpointContext}: sampling process-relative HUD");
        var windowMatch = TryRunBounded(() => DetectInGameHudMatch(input, windowRelative: true), InGameHudSampleBoundMs, InGameHudMatchKind.None);
        if (IsAcceptedInGameHudMatch(windowMatch, checkpointContext, broadHudFrameAcceptAt))
        {
            _lastInGameHudResult = true;
            return true;
        }

        MarkCommandCheckpoint($"{checkpointContext}: sampling screen-relative HUD");
        var screenMatch = TryRunBounded(() => DetectInGameHudMatch(input, windowRelative: false), InGameHudSampleBoundMs, InGameHudMatchKind.None);
        _lastInGameHudResult = IsAcceptedInGameHudMatch(screenMatch, checkpointContext, broadHudFrameAcceptAt);
        return _lastInGameHudResult;
    }

    private bool IsInGameReady(WindowsInput input, bool windowRelative)
    {
        return DetectInGameHudMatch(input, windowRelative) != InGameHudMatchKind.None;
    }

    private InGameHudMatchKind DetectInGameHudMatch(WindowsInput input, bool windowRelative)
    {
        var actionHud = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.InGameHudBar), widthRatio: 0.42, heightRatio: 0.08, windowRelative: windowRelative);
        var modernHealth = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.ModernHealthGlobe), widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
        var modernMana = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.ModernManaGlobe), widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
        if (D2RScreenClassifier.IsInGameHudProfile(modernHealth, modernMana, actionHud, healthRedThreshold: 0.20, manaBlueThreshold: 0.18))
        {
            return InGameHudMatchKind.ModernProfile;
        }

        var legacyHealth = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.LegacyHealthGlobe), widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
        var legacyMana = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.LegacyManaGlobe), widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
        if (D2RScreenClassifier.IsInGameHudProfile(legacyHealth, legacyMana, actionHud, healthRedThreshold: 0.20, manaBlueThreshold: 0.18))
        {
            return InGameHudMatchKind.LegacyProfile;
        }

        var bottomHud = SampleD2RRegion(input, new AgentCommon.UiPoint(0.500, 0.940), widthRatio: 0.70, heightRatio: 0.13, windowRelative: windowRelative);
        var centerHud = SampleD2RRegion(input, new AgentCommon.UiPoint(0.500, 0.940), widthRatio: 0.22, heightRatio: 0.08, windowRelative: windowRelative);
        return D2RScreenClassifier.IsInGameHudFrame(actionHud, bottomHud, centerHud)
            ? InGameHudMatchKind.Frame
            : InGameHudMatchKind.None;
    }

    private bool IsAcceptedInGameHudMatch(
        InGameHudMatchKind match,
        string checkpointContext,
        DateTimeOffset? broadHudFrameAcceptAt)
    {
        if (match == InGameHudMatchKind.None)
        {
            return false;
        }

        if (match != InGameHudMatchKind.Frame || broadHudFrameAcceptAt is null)
        {
            return true;
        }

        if (DateTimeOffset.UtcNow < broadHudFrameAcceptAt.Value)
        {
            MarkCommandCheckpoint($"{checkpointContext}: broad HUD frame matched during entry grace window");
            return false;
        }

        return true;
    }

    private InGameHudEvidence SampleInGameHudEvidence(WindowsInput input, bool windowRelative)
    {
        var actionHud = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.InGameHudBar), widthRatio: 0.42, heightRatio: 0.08, windowRelative: windowRelative);
        var modernHealth = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.ModernHealthGlobe), widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
        var modernMana = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.ModernManaGlobe), widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
        var legacyHealth = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.LegacyHealthGlobe), widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
        var legacyMana = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.LegacyManaGlobe), widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
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
        CancellationToken cancellationToken,
        bool guardAgainstInGame = false)
    {
        // watch-xy4wiew2-20260625-132336.log: hc2/hc3 froze at the caller's "fill password"
        // checkpoint for 2m40s+ with no further progress. Every call in this function looks
        // bounded on paper (ClickD2R is PostMessage-based, SetClipboardText caps its
        // OpenClipboard retry at 10 x 50ms) - same paradox as the HUD-confirmation freeze, which
        // only got root-caused once iteration-level checkpoints existed to disprove the
        // "it's just slow" theories. These checkpoints exist to find out which specific step
        // this is actually stuck in on the next run, instead of guessing again.
        if (ShouldSkipMenuClickForInGameSafety(guardAgainstInGame, () => MightAlreadyBeInGame(input)))
        {
            MarkCommandCheckpoint("FillTextFieldAsync: skipped, might already be in-game");
            return;
        }

        MarkCommandCheckpoint("FillTextFieldAsync: first click");
        ClickD2R(input, point);
        await DelayFastMenuAsync(cancellationToken);
        MarkCommandCheckpoint("FillTextFieldAsync: second click");
        ClickD2R(input, point);
        await DelayFastMenuAsync(cancellationToken);
        MarkCommandCheckpoint("FillTextFieldAsync: select all");
        input.SelectAll();
        await DelayFastMenuAsync(cancellationToken);
        // TypeText no-ops for an empty value, so without this, clearing a field to "no password"
        // left the prior value selected but never actually deleted (issue #24).
        MarkCommandCheckpoint("FillTextFieldAsync: clear selection");
        input.DeleteSelection();
        await DelayFastMenuAsync(cancellationToken);
        MarkCommandCheckpoint("FillTextFieldAsync: type text");
        input.TypeText(value);
        await DelayFastMenuAsync(cancellationToken);
    }

    private async Task SelectJoinDifficultyAsync(
        WindowsInput input,
        string? difficulty,
        CancellationToken cancellationToken,
        bool guardAgainstInGame = false)
    {
        if (string.IsNullOrWhiteSpace(difficulty))
        {
            return;
        }

        if (ShouldSkipMenuClickForInGameSafety(guardAgainstInGame, () => MightAlreadyBeInGame(input)))
        {
            MarkCommandCheckpoint("SelectJoinDifficultyAsync: skipped, might already be in-game");
            return;
        }

        ClickD2R(input, GetUiPoint(D2RUiCoordinateTarget.JoinDifficultyDropdown));
        await DelayFastMenuAsync(cancellationToken);
        ClickD2R(input, GetJoinDifficultyPoint(difficulty));
        await DelayFastMenuAsync(cancellationToken);
    }

    private async Task SelectCreateDifficultyAsync(
        WindowsInput input,
        string? difficulty,
        CancellationToken cancellationToken,
        bool guardAgainstInGame = false)
    {
        if (ShouldSkipMenuClickForInGameSafety(guardAgainstInGame, () => MightAlreadyBeInGame(input)))
        {
            MarkCommandCheckpoint("SelectCreateDifficultyAsync: skipped, might already be in-game");
            return;
        }

        ClickD2R(input, GetCreateDifficultyPoint(difficulty));
        await DelayStepAsync(cancellationToken);
    }

    private async Task<GameEntryWaitResult> WaitForGameEntryAsync(WindowsInput input, CancellationToken cancellationToken)
    {
        return await WaitForGameEntryAsync(input, returnTab: null, cancellationToken, new LegacyGraphicsToggleState());
    }

    private async Task<GameEntryWaitResult> WaitForGameEntryAsync(
        WindowsInput input,
        AgentCommon.UiPoint? returnTab,
        CancellationToken cancellationToken,
        LegacyGraphicsToggleState legacyToggle,
        DateTimeOffset? broadHudFrameAcceptAt = null)
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
        var sawConnectionInterrupted = false;
        var pollIteration = 0;
        MarkCommandCheckpoint("WaitForGameEntryAsync: polling for HUD/menu/connection state");

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pollIteration++;
            var canDetectReturn = returnTab is not null && DateTimeOffset.UtcNow >= returnDetectionAt;

            if (await TryConfirmEnteredGameAsync(
                    input,
                    cancellationToken,
                    legacyToggle,
                    $"WaitForGameEntryAsync: poll iteration {pollIteration}",
                    broadHudFrameAcceptAt))
            {
                MarkCommandCheckpoint("WaitForGameEntryAsync: confirmed in-game HUD");
                return GameEntryWaitResult.EnteredGame;
            }

            MarkCommandCheckpoint($"WaitForGameEntryAsync: poll iteration {pollIteration}, checking connection interruption");
            if (IsConnectionInterruptedScreen(input))
            {
                sawConnectionInterrupted = true;
                MarkCommandCheckpoint("WaitForGameEntryAsync: connection interrupted visible");
            }
            else
            {
                MarkCommandCheckpoint($"WaitForGameEntryAsync: poll iteration {pollIteration}, checking game-entry error dialog");
                if (IsGameEntryErrorDialogOpen(input))
                {
                    MarkCommandCheckpoint("WaitForGameEntryAsync: game-entry error dialog visible");
                    return GameEntryWaitResult.ErrorDialog;
                }

                MarkCommandCheckpoint($"WaitForGameEntryAsync: poll iteration {pollIteration}, checking offline character screen");
                if (IsCharacterScreenOffline(input))
                {
                    MarkCommandCheckpoint("WaitForGameEntryAsync: offline character screen visible");
                    return GameEntryWaitResult.OfflineCharacterScreen;
                }

                if (canDetectReturn)
                {
                    MarkCommandCheckpoint($"WaitForGameEntryAsync: poll iteration {pollIteration}, checking character screen return");
                    if (IsCharacterScreenReady(input))
                    {
                        MarkCommandCheckpoint("WaitForGameEntryAsync: returned to character screen");
                        return GameEntryWaitResult.ReturnedToCharacterScreen;
                    }

                    MarkCommandCheckpoint($"WaitForGameEntryAsync: poll iteration {pollIteration}, checking lobby menu return");
                    if (IsGameEntryMenuStillVisible(input, returnTab!))
                    {
                        MarkCommandCheckpoint("WaitForGameEntryAsync: lobby menu visible again");
                        return sawConnectionInterrupted
                            ? GameEntryWaitResult.ConnectionInterrupted
                            : GameEntryWaitResult.ReturnedToMenu;
                    }

                    MarkCommandCheckpoint($"WaitForGameEntryAsync: poll iteration {pollIteration}, lobby menu absent; waiting for HUD confirmation");
                }
            }

            var remainingMs = Math.Max((deadline - DateTimeOffset.UtcNow).TotalMilliseconds, 0);
            if (remainingMs == 0)
            {
                break;
            }

            await Task.Delay((int)Math.Min(EntryPollIntervalMs, remainingMs), cancellationToken);
        }

        if (await TryConfirmEnteredGameAsync(
                input,
                cancellationToken,
                legacyToggle,
                "WaitForGameEntryAsync: deadline",
                broadHudFrameAcceptAt,
                forceFreshSample: true))
        {
            MarkCommandCheckpoint("WaitForGameEntryAsync: confirmed in-game at deadline");
            return GameEntryWaitResult.EnteredGame;
        }

        if (sawConnectionInterrupted)
        {
            return GameEntryWaitResult.ConnectionInterrupted;
        }

        MarkCommandCheckpoint("WaitForGameEntryAsync: deadline, checking game-entry error dialog");
        if (IsGameEntryErrorDialogOpen(input))
        {
            return GameEntryWaitResult.ErrorDialog;
        }

        MarkCommandCheckpoint("WaitForGameEntryAsync: deadline, checking lobby menu return");
        if (returnTab is not null && IsGameEntryMenuStillVisible(input, returnTab))
        {
            return GameEntryWaitResult.ReturnedToMenu;
        }

        MarkCommandCheckpoint("WaitForGameEntryAsync: deadline, checking offline character screen");
        if (IsCharacterScreenOffline(input))
        {
            return GameEntryWaitResult.OfflineCharacterScreen;
        }

        MarkCommandCheckpoint("WaitForGameEntryAsync: deadline, checking character screen return");
        if (returnTab is not null && IsCharacterScreenReady(input))
        {
            return GameEntryWaitResult.ReturnedToCharacterScreen;
        }

        return GameEntryWaitResult.TimedOut;
    }

    private bool IsGameEntryMenuStillVisible(WindowsInput input, AgentCommon.UiPoint returnTab)
    {
        // watch-xiy6-20260625-165553.log: this was the one entry-loop check left unbounded after
        // v0.2.83 - froze WaitForGameEntryAsync's "checking lobby menu return" checkpoint for
        // 130s+ (65 consecutive watch-ticks) under D2R's load spike, same vulnerability as the
        // four sibling checks bounded there. Also the exact function implicated in the v0.2.79
        // safety incident (false-positived "menu still visible" while already in-game) - bounding
        // it here is safe the same way: MightAlreadyBeInGame already gates the click that would
        // follow a true result, so a bounded false on timeout just means one more poll iteration,
        // not a missed safety check.
        return TryRunBounded(() =>
        {
            var tab = IsLobbyTabReady(input, returnTab);
            var entry = IsLobbyEntryButtonReady(input);
            var formScreen = IsLobbyFormPanelReady(input, windowRelative: false);
            var formWindow = IsLobbyFormPanelReady(input, windowRelative: true);
            return D2RScreenClassifier.IsGameEntryMenuVisible(tab, entry, formScreen || formWindow);
        }, EntryLoopCheckBoundMs);
    }

    private bool IsAnyLobbyEntryMenuVisible(WindowsInput input)
    {
        if (IsInGameReady(input) || IsCharacterScreenReady(input) || IsCharacterScreenOffline(input))
        {
            return false;
        }

        return IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap(input);
    }

    private bool IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap(WindowsInput input)
    {
        if (IsCharacterScreenReady(input) || IsCharacterScreenOffline(input))
        {
            return false;
        }

        var createTab = IsLobbyTabReady(input, GetUiPoint(D2RUiCoordinateTarget.CreateGameTab));
        var joinTab = IsLobbyTabReady(input, GetUiPoint(D2RUiCoordinateTarget.JoinGameTab));
        var entry = IsLobbyEntryButtonReady(input);
        var formScreen = IsLobbyFormPanelReady(input, windowRelative: false);
        var formWindow = IsLobbyFormPanelReady(input, windowRelative: true);
        return D2RScreenClassifier.IsGameEntryMenuVisible(createTab || joinTab, entry, formScreen || formWindow);
    }

    private async Task<bool> TryConfirmEnteredGameAsync(
        WindowsInput input,
        CancellationToken cancellationToken,
        LegacyGraphicsToggleState legacyToggle,
        string checkpointContext = "TryConfirmEnteredGameAsync",
        DateTimeOffset? broadHudFrameAcceptAt = null,
        bool forceFreshSample = false)
    {
        if (!IsInGameReady(input, checkpointContext, broadHudFrameAcceptAt, forceFreshSample))
        {
            MarkCommandCheckpoint($"{checkpointContext}: HUD not ready");
            return false;
        }

        RecordObservedFrame(VisibleD2RState.InGame.ToString());
        MarkLobbyOrGameInteraction("Detected in-game HUD.");
        MarkCommandCheckpoint($"{checkpointContext}: confirmed in-game HUD");
        await ToggleLegacyGraphicsAfterEntryAsync(input, cancellationToken, legacyToggle);
        return true;
    }

    private async Task ToggleLegacyGraphicsAfterEntryAsync(
        WindowsInput input,
        CancellationToken cancellationToken,
        LegacyGraphicsToggleState legacyToggle)
    {
        if (!_config.Ui.ToggleLegacyGraphicsAfterEnteringGame)
        {
            return;
        }

        if (legacyToggle.Toggled)
        {
            return;
        }

        legacyToggle.Toggled = true;
        await DelayFastMenuAsync(cancellationToken);
        input.PressLegacyGraphicsToggle();
        _ = input.SendWindowLegacyGraphicsToggle(GetD2RProcessNames());
        await DelayFastMenuAsync(cancellationToken);
    }

    private AgentCommon.UiPoint GetUiPoint(D2RUiCoordinateTarget target)
    {
        return D2RUiCoordinateCatalog.GetPoint(_config.Ui, target);
    }

    private AgentCommon.UiPoint GetCharacterSlotPoint(int? characterSlot)
    {
        return D2RUiCoordinateCatalog.GetCharacterSlotPoint(_config.Ui, characterSlot);
    }

    private AgentCommon.UiPoint GetFriendRowPoint(int? friendRow)
    {
        return D2RUiCoordinateCatalog.GetFriendRowPoint(_config.Ui, friendRow);
    }

    private AgentCommon.UiPoint GetFriendContextJoinGamePoint(int? friendRow)
    {
        return D2RUiCoordinateCatalog.GetFriendContextJoinGamePoint(_config.Ui, friendRow);
    }

    private AgentCommon.UiPoint GetCreateDifficultyPoint(string? difficulty)
    {
        return D2RUiCoordinateCatalog.GetCreateDifficultyPoint(_config.Ui, difficulty);
    }

    private AgentCommon.UiPoint GetJoinDifficultyPoint(string? difficulty)
    {
        return D2RUiCoordinateCatalog.GetJoinDifficultyPoint(_config.Ui, difficulty);
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

        try
        {
            if (requireButtonReady && !IsBattleNetPlayButtonReady(input))
            {
                return false;
            }

            var battleNetNames = GetBattleNetProcessNames();
            var focused = input.TryFocusProcess(battleNetNames);
            _ = TryDismissBattleNetWhatsNewPopup(input);

            if (focused)
            {
                input.LeftClick(GetUiPoint(D2RUiCoordinateTarget.BattleNetPlayButton), battleNetNames);
            }

            _ = input.SendWindowClick(GetUiPoint(D2RUiCoordinateTarget.BattleNetPlayButton), battleNetNames, MouseButton.Left);
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
        input.LeftClick(GetUiPoint(D2RUiCoordinateTarget.BattleNetWhatsNewCloseButton), battleNetNames);
        _ = input.SendWindowClick(GetUiPoint(D2RUiCoordinateTarget.BattleNetWhatsNewCloseButton), battleNetNames, MouseButton.Left);
        return true;
    }

    private bool IsBattleNetWhatsNewPopupOpen(WindowsInput input)
    {
        var title = input.SampleRegion(
            GetUiPoint(D2RUiCoordinateTarget.BattleNetWhatsNewTitle),
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
            GetUiPoint(D2RUiCoordinateTarget.BattleNetPlayButton),
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

    // internal so tests can pin ReconcileActivityFromLiveSnapshot/GetActivitySnapshot's contract
    // (issue #20, item 1) without needing the real Win32 screen classifier behind it.
    internal enum D2RActivityState
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
        CharacterMenu,
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

    private enum InGameHudMatchKind
    {
        None,
        ModernProfile,
        LegacyProfile,
        Frame
    }

    internal enum FriendsAccordionAction
    {
        ExpandAfterOpeningDrawer,
        ExpandCollapsed,
        SkipExpanded
    }

    private sealed record FriendRowFingerprintMatch(
        int Row,
        FriendFingerprintComparison Comparison);

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
        bool ProcessExitedDuringWait = false,
        int LaunchAttempts = 0,
        int PlayClicks = 0,
        string LastLaunchMessage = "(none)");

    private sealed class ReadyLaunchNudgeState
    {
        public DateTimeOffset NextLaunchRetryAt { get; set; }
        public DateTimeOffset NextPlayClickAt { get; set; }
        public int LaunchAttempts { get; set; }
        public int PlayClicks { get; set; }
        public string LastLaunchMessage { get; set; } = "(none)";
    }

    private sealed class LegacyGraphicsToggleState
    {
        public bool Toggled { get; set; }
    }

    internal sealed record ActivitySnapshot(
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
