using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using AgentCommon;

namespace D2RAgent;

public sealed class VmOperations
{
    private const string DefaultBattleNetPath = @"C:\Program Files (x86)\Battle.net\Battle.net.exe";
    private const string DefaultBattleNetD2RArgs = "--exec=\"launch OSI\"";
    private const string DefaultD2RInstallDirectory = @"C:\Program Files (x86)\Diablo II Resurrected";
    private const string BattleNetFolderDialogTitle = "Choose a Folder";
    private const int MaxD2RStartTimeoutSeconds = 40;
    private const int MaxReadyStartupSkipSeconds = 45;
    private const int MaxCharacterScreenReconnectSeconds = 45;
    // Killing/relaunching D2R over a broken Battle.net session already costs ~45s of reconnect
    // attempts plus launch overhead, so this cooldown is mostly a backstop against a pathological
    // case (a session that keeps coming back broken) turning into a tight restart loop.
    private const int BrokenSessionRestartCooldownSeconds = 60;
    // A repeat broken-session recovery inside this window - with no healthy session observed in
    // between - means the D2R restart changed nothing, which points past the game at the
    // launcher (see RecoverFromBrokenBattleNetSessionAsync). Generous on purpose: while the
    // session stays broken, consecutive recovery attempts arrive only minutes apart (cooldown
    // skips plus a follow-auto cycle plus the ~45s reconnect timeout), and a healthy cycle
    // resets the streak, so the window only has to separate "same incident, restart didn't
    // take" from "new incident on a VM whose last recovery was long ago".
    private const int BrokenSessionEscalationWindowMinutes = 30;
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
    private const int GraphicsDeviceFailureProbeIntervalMs = 1000;
    private const int GraphicsDeviceFailureRestartDelaySeconds = 3;
    // Dismiss-and-relaunch attempts allowed inside one incident before this agent stops trying
    // and asks (through status) for a VM power cycle instead.
    internal const int GraphicsDeviceFailureRelaunchLimit = 5;
    private const int GraphicsDeviceFailureIncidentWindowMinutes = 20;
    private const int GraphicsDeviceFailureGiveUpLogMinutes = 5;
    private const int GraphicsDeviceFailureProbeBoundMs = 2000;
    private const int ReadyStartupSampleGrid = 5;
    private const int MenuSampleGrid = 9;
    // The "Game is full" discriminator reads thin single-line dialog text; the default 9-grid
    // can land entirely between glyphs (measured center std drops from 45.7 to 23.0), so its
    // narrow text bands sample denser. Mirrored by ReferenceCaptureClassifier's tests.
    internal const int GameFullTextBandSampleGrid = 17;
    private const int FastMenuDelayMs = 150;
    // Each round sends one stateful Escape and one click. Multiple delivery routes in one round
    // toggle D2R's pause menu open then closed; retries provide the reliability fallback without
    // undoing a successful Escape.
    private const int SaveExitMaxAttempts = 3;
    // A confirmed save-exit only proves the HUD is gone, but follow-auto deliberately schedules
    // its next join check two seconds later and D2R normally returns to the lobby. Preserve that
    // narrow expectation long enough to bypass the expensive general-purpose lobby classifiers
    // once. Only a follow-auto-tagged leave sets it, and the run id plus D2R process generation
    // must still match; manual/unknown/recovery entry points retain the full classifiers.
    internal static readonly TimeSpan ExpectedLobbyAfterSaveExitWindow = TimeSpan.FromSeconds(30);
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
    // WaitForPostSaveExitMenuAsync's HUD sample needs more headroom than the 1s general bound:
    // during the save-exit transition the D2R window is redrawing and a HUD-region BitBlt can take
    // 1-3s, so a 1s bound returned "couldn't sample" (null) on every iteration and the counter never
    // advanced (watch-follow-auto-20260715-145732.log). But 4s made each iteration so slow the loop
    // could not accumulate two gone reads before the deadline (watch-follow-auto-20260715-151530.log),
    // so 2.5s is the compromise: enough for a transition read to complete, tight enough to keep the
    // loop iterating fast (the loop polls only the HUD now, so nothing else slows an iteration).
    private const int PostSaveExitHudSampleBoundMs = 2500;
    // These sibling entry-loop checks all use the same pixel-sampling path, so bound them at
    // their definitions instead of trying to guard every current and future call site.
    private const int EntryLoopCheckBoundMs = 1500;
    private const double FollowFingerprintMaxAverageDifference = 18.0;
    private const double FollowFingerprintMaxSignalAverageDifference = 90.0;
    private const double FollowFingerprintMinSignalSeparation = 12.0;
    private const int FollowFingerprintMinSignalPixels = 3;
    private const int FollowFingerprintMaxVisibleFriendRows = 8;
    private const int FollowFingerprintMinAutoClickGridColumns = 16;
    private const int FollowFingerprintMinAutoClickGridRows = 4;
    // Each slot is one small region capture - cheap relative to the in-game HUD checks above,
    // but there are up to 8 of them per tick, so still bound each individually rather than
    // relying on the tick interval alone to cap worst-case cost.
    private const int PartyFrameSampleBoundMs = 800;
    // Five small surround-region captures; runs at most once per idle-monitor tick and only
    // while the screen has already been unrecognizable past the stuck threshold. The fallback
    // is false ("not confirmed"), so a timeout can only delay the quit, never cause one.
    private const int StuckLoadScreenSampleBoundMs = 2500;
    // Seven small captures, and the fallback is false, so a timeout reads as "not the gamma
    // screen" - the repair path stays unarmed rather than firing off a bad read.
    private const int GammaCalibrationSampleBoundMs = 1500;
    // Consecutive gamma-calibration sightings required before this agent reports that it needs a
    // donor settings file. The screen is static and the detector has enormous margins, but the
    // repair quits a live client and overwrites its settings, so it waits for a second look.
    private const int GammaCalibrationConfirmSightings = 2;
    // Unconfirmed sightings belong to one short observation incident. Status and menu detection
    // take an immediate independent second probe; the window prevents isolated positives from
    // unrelated observations minutes apart from authorizing a settings-file replacement.
    internal static readonly TimeSpan GammaCalibrationIncidentWindow = TimeSpan.FromSeconds(30);

    internal readonly record struct D2RProcessGeneration(int ProcessId, DateTimeOffset StartedUtc);

    private readonly record struct SettingsRepairProcessStopResult(bool Ok, string Message);

    private readonly VmAgentConfig _config;
    private readonly MachineTelemetrySampler _telemetry = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private readonly object _activityLock = new();
    private readonly string[] _restartArgs;
    private D2RActivityState _activityState = D2RActivityState.Unknown;
    private DateTimeOffset? _characterScreenIdleSinceUtc;
    private DateTimeOffset? _lastLobbyOrGameInteractionUtc;
    private ExpectedLobbyAfterSaveExit? _expectedLobbyAfterSaveExit;
    private DateTimeOffset? _lastObservedD2RStartUtc;
    private DateTimeOffset? _lastBrokenSessionRestartUtc;
    // Consecutive broken-session recoveries with no confirmed-healthy session in between;
    // guarded by _activityLock, reset by MarkBattleNetSessionHealthy.
    private int _brokenSessionRecoveryStreak;
    private string? _lastActivityReason;
    private LastInputActionSnapshot? _lastInputAction;
    private string? _lastObservedFrame;
    private DateTimeOffset? _lastObservedFrameUtc;
    // Start of the current run of consecutive Unknown frame observations, fed by
    // RecordObservedFrame (any recognized frame clears it). Diagnostics only (surfaced as
    // unknownFrameSinceUtc in status) - deliberately NOT what the stuck-load-screen watchdog
    // keys on, because a wedged load screen can classify as a recognized state (the brighter
    // doorway frames cross IsDiabloSplashScreen's thresholds) and reset this indefinitely.
    private DateTimeOffset? _unknownFrameSinceUtc;
    // Start of the current run of consecutive stuck-load-screen watchdog ticks whose
    // black-surround confirmation passed. Only the watchdog reads/writes this; a tick that
    // fails the confirmation (any real screen content, degraded sampling, or D2R not
    // running) resets it.
    private DateTimeOffset? _stuckSurroundSinceUtc;
    private string? _lastClassifierBreakdown;
    private DateTimeOffset? _lastClassifierBreakdownUtc;
    private string? _lastHudEvidence;
    private DateTimeOffset? _lastHudEvidenceUtc;
    private int? _lastPartyMemberCount;
    private DateTimeOffset? _lastPartyMemberCountUtc;
    private string? _lastCommandCheckpoint;
    private DateTimeOffset? _lastCommandCheckpointUtc;
    // Consecutive graphics-initialization failures inside one incident, guarded by
    // _activityLock. A dismissal + relaunch that lands back on the same dialog is not progress:
    // the guest's display driver is what is broken, and only a VM power cycle clears it. The
    // streak is what tells the host when to stop asking this agent to try again.
    private int _graphicsDeviceFailureStreak;
    private DateTimeOffset? _lastGraphicsDeviceFailureUtc;
    private string? _lastGraphicsDeviceFailureDetail;
    private DateTimeOffset? _lastGraphicsDeviceFailureGiveUpLogUtc;
    private DateTimeOffset? _detailedStatusBackoffUntilUtc;
    private DateTimeOffset _nextInGameHudSampleAt = DateTimeOffset.MinValue;
    private bool _lastInGameHudResult;
    private long _followAutoStoppedThroughRunId;
    // Consecutive first-run gamma-calibration sightings (D2R reset its own Settings.json), guarded
    // by _activityLock. An unconfirmed incident is cleared by any failed/non-gamma observation or
    // process restart. Once confirmed it stays latched while the broken client is stopped, and is
    // cleared only by a successful settings replacement or a healthy rendered frame.
    private int _gammaCalibrationSightings;
    private DateTimeOffset? _firstGammaCalibrationUtc;
    private DateTimeOffset? _lastGammaCalibrationUtc;
    private DateTimeOffset? _gammaCalibrationProcessStartUtc;
    private int _settingsRepairsApplied;
    private DateTimeOffset? _lastSettingsRepairUtc;
    private string? _lastSettingsRepairMessage;
    // PREPARED is flushed before the client is quit and before a destructive replacement is
    // attempted. READY means the replacement completed and only a healthy rendered frame may
    // close the incident. The phase and attempt budget live beside Settings.json so process/VM
    // restarts cannot accidentally authorize an uncounted duplicate replacement.
    private D2RSettingsRepairPhase? _settingsRepairPhase;
    private int _settingsRepairIncidentAttempts;
    private DateTimeOffset? _settingsRepairIncidentFirstAttemptUtc;
    private DateTimeOffset? _settingsRepairIncidentLastAttemptUtc;
    private bool _settingsRepairBudgetJournalRewritePending;
    private string? _settingsRepairJournalError;
    private string? _settingsRepairJournalWarning;
    private bool _settingsRepairTransactionInProgress;

    public VmOperations(VmAgentConfig config, string[]? restartArgs = null)
    {
        _config = config;
        _restartArgs = restartArgs ?? [];
        LoadPersistedSettingsRepairState();
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

        // The host asks for live status immediately before every follow-auto check. After this
        // same run just confirmed Save and Exit, doing the full detailed screen classification
        // here would spend the exact GDI-heavy delay the follow fast path is meant to avoid and
        // could then trigger a redundant menu_ready. Report the process-bound expectation through
        // the cheap status path without consuming it; the follow command remains the one-shot
        // consumer and performs the narrow Friends verification next.
        if (HasExpectedLobbyAfterSaveExitCandidate(DateTimeOffset.UtcNow))
        {
            return CollectProcessOnlyStatus(
                "Recent follow-auto Save and Exit expects the lobby; skipped the detailed status classifier.",
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
            machineTelemetry = _telemetry.Sample(),
            statusMode = "detailed",
            statusDegraded = false,
            statusError = (string?)null,
            battleNetRunning,
            d2rRunning,
            d2rVisibleState = visibleState.ToString(),
            d2rGraphicsDeviceFailure = DescribeGraphicsDeviceFailure(visibleState),
            d2rSettingsRepair = DescribeSettingsRepair(visibleState),
            d2rProcessDiscovery = OperatingSystem.IsWindows() ? WindowsProcessFinder.Discover(GetD2RProcessNames(), windowScanCache) : null,
            // Gating this on d2rRunning blacked out the one field (foregroundProcessName) that
            // would show what's actually focused/visible when process-name matching itself is
            // what's failing - exactly the case where this is most needed.
            d2rInput = OperatingSystem.IsWindows() ? TryGetD2RInputDiagnostics(windowScanCache) : null,
            lastInputAction = _lastInputAction,
            lastObservedFrame = _lastObservedFrame,
            lastObservedFrameUtc = _lastObservedFrameUtc,
            unknownFrameSinceUtc = _unknownFrameSinceUtc,
            stuckSurroundSinceUtc = _stuckSurroundSinceUtc,
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
            followTemplates = CollectFollowTemplateDigests(),
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
        var visibleState = d2rRunning && IsExpectedLobbyAfterSaveExitForCurrentProcess()
            ? VisibleD2RState.LobbyOrGame
            : GetBestProcessOnlyVisibleState(d2rRunning);
        var activity = DetectVisibleActivitySnapshot(d2rRunning, visibleState);

        return new
        {
            hostName = Environment.MachineName,
            userName = Environment.UserName,
            machineTelemetry = _telemetry.Sample(),
            statusMode = "processOnly",
            statusDegraded = true,
            statusError = reason,
            battleNetRunning,
            d2rRunning,
            d2rVisibleState = visibleState.ToString(),
            d2rGraphicsDeviceFailure = DescribeGraphicsDeviceFailure(visibleState),
            d2rSettingsRepair = DescribeSettingsRepair(visibleState),
            d2rProcessDiscovery = new ProcessDiscoverySnapshot(GetD2RProcessNames(), [], []),
            d2rInput = (InputDiagnostics?)null,
            lastInputAction = _lastInputAction,
            lastObservedFrame = _lastObservedFrame,
            lastObservedFrameUtc = _lastObservedFrameUtc,
            unknownFrameSinceUtc = _unknownFrameSinceUtc,
            stuckSurroundSinceUtc = _stuckSurroundSinceUtc,
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
            followTemplates = CollectFollowTemplateDigests(),
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

        // follow_set_template/follow_clear_template/follow_stop_auto are pure local state
        // changes, no D2R interaction - same reasoning as screenshot/status above, so
        // binding/unbinding/stopping from the Host doesn't queue behind whatever long-running
        // menu command this agent is mid-way through.
        if (string.Equals(request.Command, "follow_set_template", StringComparison.OrdinalIgnoreCase))
        {
            return FollowSetTemplate(MenuCommandArgs.From(request.Args));
        }

        if (string.Equals(request.Command, "follow_clear_template", StringComparison.OrdinalIgnoreCase))
        {
            return FollowClearTemplate();
        }

        if (string.Equals(request.Command, "follow_set_leader_template", StringComparison.OrdinalIgnoreCase))
        {
            return FollowSetLeaderTemplate(MenuCommandArgs.From(request.Args));
        }

        if (string.Equals(request.Command, "follow_remove_leader_template", StringComparison.OrdinalIgnoreCase))
        {
            return FollowRemoveLeaderTemplate(MenuCommandArgs.From(request.Args));
        }

        if (string.Equals(request.Command, "follow_clear_leader_template", StringComparison.OrdinalIgnoreCase))
        {
            return FollowClearLeaderTemplate();
        }

        if (string.Equals(request.Command, "follow_stop_auto", StringComparison.OrdinalIgnoreCase))
        {
            return FollowStopAuto(MenuCommandArgs.From(request.Args));
        }

        // settings_export only reads a file - no D2R interaction at all - and it is issued to a
        // HEALTHY donor VM while some other VM is broken. Queueing it behind that donor's own
        // long-running menu command would make one wedged client's repair wait on an unrelated
        // client's automation.
        if (string.Equals(request.Command, "settings_export", StringComparison.OrdinalIgnoreCase))
        {
            return ExportSettings();
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
        // A single gamma sighting is enough to suppress input even though two are required before
        // authorizing settings replacement. Ready/menu automation sends startup keys and targeted
        // clicks; a click that lands on Continue can accept the reset defaults before the
        // independent confirmation probe runs. Once the detector has seen this screen, only read-only,
        // lifecycle, and settings-repair commands may proceed until the observation is disproved
        // or the incident is repaired.
        if (request.Command.StartsWith("menu_", StringComparison.Ordinal)
            && ShouldSuppressMenuInputForSettingsReset())
        {
            ConfirmGammaCalibrationWithImmediateSecondProbe();
            return CommandResult.Failure(
                "D2R menu input is suppressed because the first-run gamma calibration screen was detected; Settings.json must be confirmed and repaired before automation continues.",
                await CollectStatusAsync(cancellationToken));
        }

        return request.Command switch
        {
            "launch_battlenet" => LaunchBattleNet(),
            "launch_d2r" => await LaunchD2RAsync(cancellationToken),
            "kill_d2r" => KillD2R(),
            "quit_d2r" => await QuitD2RAsync(cancellationToken),
            "restart_d2r" => await RestartD2RAsync(cancellationToken),
            "screenshot" => await TakeScreenshotAsync(cancellationToken),
            "sample_player_count" => SamplePlayerCount(MenuCommandArgs.From(request.Args)),
            "menu_ready" => await ReadyClientAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_lobby" => await GoLobbyAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_play" => await PlayCharacterAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_prepare_join_game" => await PrepareJoinGameAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_submit_join_game" => await SubmitPreparedJoinGameAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_join_game" => await JoinGameAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_create_game" => await CreateGameAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_join_friend" => await JoinFriendAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_follow_bind" => await FollowBindCaptureAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_follow_bind_game" => FollowBindInGameCapture(MenuCommandArgs.From(request.Args)),
            "menu_follow_auto_check" => await FollowAutoCheckAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            "menu_save_exit" => await SaveAndExitAsync(MenuCommandArgs.From(request.Args).FollowAutoRunId, cancellationToken),
            "settings_repair" => await RepairSettingsAsync(MenuCommandArgs.From(request.Args), cancellationToken),
            _ => CommandResult.Failure($"Unsupported VM command: {request.Command}")
        };
    }

    /// <summary>
    /// Hands this VM's Settings.json to the host so it can be copied onto a fleet member whose own
    /// copy D2R has reset. Refuses if this client is itself on the gamma screen: a donor has to be
    /// a client whose settings demonstrably still work.
    /// </summary>
    private CommandResult ExportSettings()
    {
        if (GetGammaCalibrationSnapshot().Sightings > 0)
        {
            return CommandResult.Failure(
                "This client is on D2R's first-run gamma calibration screen, so its own settings file is the broken one; it cannot be a donor.");
        }

        var path = D2RSettingsFile.ResolveSettingsPath(_config.D2RSettingsPath);
        if (!D2RSettingsFile.TryRead(path, out var snapshot, out var error))
        {
            return CommandResult.Failure(error);
        }

        return CommandResult.Success(
            $"Exported {snapshot.Length} characters from {snapshot.Path}.",
            new
            {
                path = snapshot.Path,
                content = snapshot.Content,
                sha256 = snapshot.Sha256,
                length = snapshot.Length,
                lastWriteUtc = snapshot.LastWriteUtc
            });
    }

    /// <summary>
    /// Replaces this VM's Settings.json with a healthy fleet member's copy. The PREPARED journal
    /// is flushed before D2R is quit, so a process or VM crash cannot replay an uncounted
    /// destructive attempt. D2R owns the file while it runs and rewrites it on exit, so the write
    /// then waits <see cref="VmAgentConfig.SettingsRepairSettleSeconds"/> for that final write to
    /// land. Relaunching is left to the host, which owns retry and escalation.
    /// </summary>
    private async Task<CommandResult> RepairSettingsAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        if (!_config.SettingsRepairEnabled)
        {
            return CommandResult.Failure("settingsRepairEnabled is false in this agent's config; refusing to replace Settings.json.");
        }

        var content = args.SettingsContent;
        if (!D2RSettingsFile.IsPlausibleSettingsJson(content, out var reason))
        {
            return CommandResult.Failure($"Refusing the donor settings payload: {reason}.");
        }

        var path = D2RSettingsFile.ResolveSettingsPath(_config.D2RSettingsPath);
        if (path is null)
        {
            return CommandResult.Failure(
                $"Could not resolve the Saved Games folder; set d2rSettingsPath to the full path of {D2RSettingsFile.SettingsFileName}.");
        }

        lock (_activityLock)
        {
            RefreshPersistedSettingsRepairStateLocked(path);
            NormalizePersistedSettingsRepairBudgetLocked(DateTimeOffset.UtcNow);
            if (_settingsRepairJournalError is not null)
            {
                return CommandResult.Failure(
                    $"The settings-repair journal is invalid or unreadable; refusing to replace Settings.json until an operator removes or fixes it: {_settingsRepairJournalError}");
            }

            if (_settingsRepairPhase == D2RSettingsRepairPhase.Ready)
            {
                return CommandResult.Failure(
                    "A donor settings replacement is already in Ready phase; refusing a duplicate copy until the client renders a healthy frame.");
            }
        }

        if (!TryCaptureD2RProcessGenerations(out var authorizedProcesses, out var processIdentityError))
        {
            return CommandResult.Failure(
                $"Could not establish the exact D2R process generation before settings repair; no attempt was charged and nothing was changed: {processIdentityError}");
        }

        if (authorizedProcesses.Length > 1)
        {
            return CommandResult.Failure(
                $"Found {authorizedProcesses.Length} D2R process generations; the Gamma screen cannot be bound unambiguously to one client, so no attempt was charged and nothing was changed.");
        }

        var d2rRunning = authorizedProcesses.Length == 1;
        var freshGammaDetected = !d2rRunning;
        if (d2rRunning)
        {
            freshGammaDetected = DetectFreshGammaCalibrationForSettingsRepair();
            RecordObservedFrame(freshGammaDetected
                ? nameof(VisibleD2RState.GammaCalibration)
                : nameof(VisibleD2RState.Unknown));
        }

        if (!TryCaptureD2RProcessGenerations(out var processesAfterGammaProbe, out processIdentityError))
        {
            return CommandResult.Failure(
                $"Could not revalidate the exact D2R process generation after the Gamma probe; no attempt was charged and nothing was changed: {processIdentityError}");
        }

        if (!ContainsOnlyAuthorizedSettingsRepairProcesses(authorizedProcesses, processesAfterGammaProbe))
        {
            return CommandResult.Failure(
                "A new D2R process generation appeared during the Gamma authorization probe; no attempt was charged and the new client was left untouched.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var chargedAttemptCount = 0;
        lock (_activityLock)
        {
            RefreshPersistedSettingsRepairStateLocked(path);
            var preparedUtc = DateTimeOffset.UtcNow;
            NormalizePersistedSettingsRepairBudgetLocked(preparedUtc);
            if (!CanApplySettingsRepair(
                    d2rRunning,
                    freshGammaDetected,
                    IsGammaCalibrationConfirmedLocked(),
                    _settingsRepairPhase,
                    _settingsRepairJournalError is not null,
                    out var refusalReason))
            {
                return CommandResult.Failure(refusalReason);
            }

            if (_settingsRepairIncidentAttempts >= D2RSettingsRepairState.MaxAttemptsPerIncident)
            {
                return CommandResult.Failure(
                    $"The settings-repair incident already spent its {D2RSettingsRepairState.MaxAttemptsPerIncident} replacement attempts; refusing another copy until the incident window expires or a healthy frame closes it.");
            }

            var nextAttemptCount = _settingsRepairIncidentAttempts + 1;
            var firstAttemptUtc = _settingsRepairIncidentFirstAttemptUtc ?? preparedUtc;
            var preparedState = new D2RSettingsRepairState(
                D2RSettingsRepairState.CurrentSchemaVersion,
                D2RSettingsRepairPhase.Prepared,
                nextAttemptCount,
                firstAttemptUtc,
                preparedUtc);
            if (!D2RSettingsFile.TryWriteRepairState(path, preparedState, out var stateError))
            {
                _settingsRepairJournalWarning =
                    $"Could not persist PREPARED before replacement; no client/file changes were made: {stateError}";
                _lastSettingsRepairMessage = _settingsRepairJournalWarning;
                return CommandResult.Failure(_settingsRepairJournalWarning);
            }

            _settingsRepairPhase = D2RSettingsRepairPhase.Prepared;
            _settingsRepairIncidentAttempts = nextAttemptCount;
            _settingsRepairIncidentFirstAttemptUtc = firstAttemptUtc;
            _settingsRepairIncidentLastAttemptUtc = preparedUtc;
            _settingsRepairBudgetJournalRewritePending = false;
            _settingsRepairJournalWarning = null;
            _settingsRepairTransactionInProgress = true;
            chargedAttemptCount = nextAttemptCount;
        }

        CommandResult ChargedFailure(string failure)
        {
            lock (_activityLock)
            {
                _lastSettingsRepairMessage = failure;
            }

            return CommandResult.Failure(
                failure,
                new
                {
                    settingsRepairAttemptCharged = true,
                    incidentRepairAttempts = chargedAttemptCount
                });
        }

        try
        {
            var quit = await StopAuthorizedD2RForSettingsRepairAsync(
                authorizedProcesses,
                cancellationToken);
            if (!quit.Ok)
            {
                return ChargedFailure(
                    $"{quit.Message} PREPARED remains retryable, and Settings.json was not replaced.");
            }

            var settle = TimeSpan.FromSeconds(Math.Clamp(_config.SettingsRepairSettleSeconds, 0, 30));
            if (settle > TimeSpan.Zero)
            {
                await Task.Delay(settle, cancellationToken);
            }

            if (!TryCaptureD2RProcessGenerations(out var processesBeforeCopy, out processIdentityError))
            {
                return ChargedFailure(
                    $"Could not prove D2R remained stopped before replacing Settings.json: {processIdentityError} PREPARED remains retryable, and Settings.json was not replaced.");
            }

            if (processesBeforeCopy.Length > 0)
            {
                var processDescription = ContainsOnlyAuthorizedSettingsRepairProcesses(
                    authorizedProcesses,
                    processesBeforeCopy)
                    ? "the authorized D2R process is still running"
                    : "a new D2R process generation appeared";
                return ChargedFailure(
                    $"Refusing to replace Settings.json because {processDescription} after the quit/settle step. PREPARED remains retryable, and every running client was left untouched.");
            }

            (bool Allowed, string Error) ValidateCommitBoundary()
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return (
                        false,
                        "Settings repair was cancelled before the atomic settings-file commit; the destination was left unchanged.");
                }

                if (!TryCaptureD2RProcessGenerations(
                        out var processesAtCommit,
                        out var commitIdentityError))
                {
                    return (
                        false,
                        $"Could not prove D2R was stopped at the atomic settings-file commit: {commitIdentityError} The destination was left unchanged.");
                }

                if (processesAtCommit.Length > 0)
                {
                    var processDescription = ContainsOnlyAuthorizedSettingsRepairProcesses(
                        authorizedProcesses,
                        processesAtCommit)
                        ? "the authorized D2R process was still running"
                        : "a new D2R process generation had appeared";
                    return (
                        false,
                        $"Refused the atomic settings-file commit because {processDescription}; every running client and the destination file were left untouched.");
                }

                return (true, "");
            }

            if (!D2RSettingsFile.TryReplace(
                    path,
                    content!,
                    out var backupPath,
                    out var writeError,
                    ValidateCommitBoundary))
            {
                return ChargedFailure(
                    $"PREPARED replacement attempt failed; the donor may be retried within budget: {writeError}");
            }

            // The corrupted state is cleared as far as this agent knows, but the proof is the
            // client reaching character select on the next launch - so the sighting count resets
            // and the detector gets to make that call again from scratch.
            ClearGammaCalibrationSightings();
            var message = $"Replaced {path} with {content!.Length} characters"
                + (string.IsNullOrWhiteSpace(args.SettingsSourceAgentId) ? "" : $" from {args.SettingsSourceAgentId}")
                + (string.IsNullOrWhiteSpace(backupPath) ? "" : $"; previous file kept at {backupPath}")
                + $". Client was closed first: {quit.Message}";
            lock (_activityLock)
            {
                var appliedUtc = DateTimeOffset.UtcNow;
                _settingsRepairPhase = D2RSettingsRepairPhase.Ready;
                _settingsRepairsApplied++;
                _lastSettingsRepairUtc = appliedUtc;

                var readyState = new D2RSettingsRepairState(
                    D2RSettingsRepairState.CurrentSchemaVersion,
                    D2RSettingsRepairPhase.Ready,
                    _settingsRepairIncidentAttempts,
                    _settingsRepairIncidentFirstAttemptUtc,
                    _settingsRepairIncidentLastAttemptUtc);
                if (!D2RSettingsFile.TryWriteRepairState(path, readyState, out var stateError))
                {
                    // The donor is already installed. Memory stays READY so this process refuses
                    // duplicates; the durable PREPARED state intentionally stays retryable if the
                    // VM restarts before a later status pass can finish this rewrite.
                    _settingsRepairBudgetJournalRewritePending = true;
                    _settingsRepairJournalWarning =
                        $"Could not persist READY after the donor copy; disk remains safely PREPARED: {stateError}";
                    message += $" WARNING: {_settingsRepairJournalWarning}";
                }
                else
                {
                    _settingsRepairBudgetJournalRewritePending = false;
                    _settingsRepairJournalWarning = null;
                }

                _lastSettingsRepairMessage = message;
            }

            return CommandResult.Success(
                message,
                new
                {
                    path,
                    backupPath = string.IsNullOrWhiteSpace(backupPath) ? null : backupPath,
                    sha256 = D2RSettingsFile.Sha256(content),
                    length = content.Length,
                    sourceAgentId = args.SettingsSourceAgentId,
                    settleSeconds = settle.TotalSeconds,
                    settingsRepairAttemptCharged = true,
                    incidentRepairAttempts = chargedAttemptCount
                });
        }
        catch (OperationCanceledException)
        {
            return ChargedFailure(
                "Settings repair was cancelled after PREPARED was persisted; the charged attempt remains retryable, and Settings.json was not replaced.");
        }
        catch (Exception ex)
        {
            return ChargedFailure(
                $"Settings repair failed after PREPARED was persisted: {ex.Message} The charged attempt remains retryable.");
        }
        finally
        {
            lock (_activityLock)
            {
                _settingsRepairTransactionInProgress = false;
            }
        }
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
        var graphicsDeviceFailure = default(D2RGraphicsDeviceFailureDismissalResult);
        if (OperatingSystem.IsWindows())
        {
            graphicsDeviceFailure = await TryDismissGraphicsDeviceFailureAndWaitAsync(
                new WindowsInput(),
                cancellationToken);
        }

        if (graphicsDeviceFailure.Detected && !graphicsDeviceFailure.DismissalSent)
        {
            return CommandResult.Failure(
                $"Detected D2R's failed-to-initialize-graphics dialog ({graphicsDeviceFailure.Describe()}), but it survived OK, WM_COMMAND, Enter and Close. The failed process was left untouched; retry the launch command.");
        }

        var recoveredGraphicsDeviceFailure = graphicsDeviceFailure.DismissalSent;

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
            var recoveryPrefix = recoveredGraphicsDeviceFailure
                ? "Dismissed D2R's failed-to-initialize-graphics dialog and restarted the client. "
                : "";
            return CommandResult.Success(
                recoveryPrefix + (usedBattleNetExec
                    ? "Initial Battle.net D2R launch command sent; ready loop will keep nudging launch/Play while skipping startup screens."
                    : "Initial D2R launch command sent; ready loop will start skipping startup screens immediately."));
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
        if (recoveredGraphicsDeviceFailure)
        {
            message = "Dismissed D2R's failed-to-initialize-graphics dialog and restarted the client. " + message;
        }

        return CommandResult.Success(message, status);
    }

    private async Task<D2RGraphicsDeviceFailureDismissalResult> TryDismissGraphicsDeviceFailureAndWaitAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        var dismissal = input.TryDismissD2RGraphicsDeviceFailureDialog(GetD2RProcessNames());
        if (!dismissal.Detected)
        {
            return dismissal;
        }

        // Stamp the frame, not just the streak. Every status collected while a UI command is
        // active is the process-only one, and that path never runs DetectVisibleD2RState - it
        // reports _lastObservedFrame instead. A graphics-device failure is detected *from inside*
        // a command (launch, ready loop, idle probe all reach here), so without this the frame
        // keeps whatever the client was doing before it died and status contradicts itself:
        // "visible InGame" alongside "Detected D2R's failed-to-initialize-graphics dialog".
        //
        // That is load-bearing, in the same way the missing gamma mapping was. MenuReadyPolicy
        // reads this state: "GraphicsDeviceFailure" needs a ready pass - the comment there says a
        // ready pass "is exactly what clears it" - while a stale "InGame" answers NeedsReady with
        // false. So the wrong frame does not merely misreport a wedged client, it suppresses the
        // one pass that would have recovered it, and the client sits on its error dialog until
        // something coarser (a warmup-failure power cycle) notices.
        RecordObservedFrame(VisibleD2RState.GraphicsDeviceFailure.ToString());
        RecordGraphicsDeviceFailureSighting(dismissal);
        if (!dismissal.DismissalSent)
        {
            MarkCommandCheckpoint(
                $"Detected D2R's failed-to-initialize-graphics dialog ({dismissal.Describe()}), but OK, WM_COMMAND, Enter and Close all left it on screen.");
            return dismissal;
        }

        MarkCommandCheckpoint(
            $"Detected D2R's failed-to-initialize-graphics dialog ({dismissal.Describe()}); dismissed it and waiting before relaunch.");
        await Task.Delay(TimeSpan.FromSeconds(GraphicsDeviceFailureRestartDelaySeconds), cancellationToken);

        // Clicking OK normally terminates the failed D2R process. If Intel's fragile graphics
        // initialization leaves that process behind, a Battle.net launch command can be ignored
        // as an attempted second instance. Kill only that lingering D2R process before retrying;
        // Battle.net and every other VM process remain untouched.
        if (IsD2RNamedProcessRunning())
        {
            _ = KillD2R();
            MarkCommandCheckpoint(
                "D2R still existed after its graphics-device error was dismissed; killed the lingering process before relaunch.");
        }

        return dismissal;
    }

    /// <summary>
    /// Records one sighting of the graphics-initialization dialog. Sightings inside
    /// <see cref="GraphicsDeviceFailureIncidentWindowMinutes"/> of each other belong to the same
    /// incident and accumulate; a longer quiet gap (or any confirmed-healthy client) starts over.
    /// </summary>
    private int RecordGraphicsDeviceFailureSighting(D2RGraphicsDeviceFailureDismissalResult dismissal)
    {
        lock (_activityLock)
        {
            var now = DateTimeOffset.UtcNow;
            var withinIncident = _lastGraphicsDeviceFailureUtc is { } last
                && now - last < TimeSpan.FromMinutes(GraphicsDeviceFailureIncidentWindowMinutes);
            _graphicsDeviceFailureStreak = withinIncident ? _graphicsDeviceFailureStreak + 1 : 1;
            _lastGraphicsDeviceFailureUtc = now;
            _lastGraphicsDeviceFailureDetail = dismissal.Describe();
            return _graphicsDeviceFailureStreak;
        }
    }

    private void ClearGraphicsDeviceFailureStreak()
    {
        lock (_activityLock)
        {
            _graphicsDeviceFailureStreak = 0;
            _lastGraphicsDeviceFailureGiveUpLogUtc = null;
        }
    }

    private bool ShouldLogGraphicsDeviceFailureGiveUp()
    {
        lock (_activityLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastGraphicsDeviceFailureGiveUpLogUtc is { } last
                && now - last < TimeSpan.FromMinutes(GraphicsDeviceFailureGiveUpLogMinutes))
            {
                return false;
            }

            _lastGraphicsDeviceFailureGiveUpLogUtc = now;
            return true;
        }
    }

    private (int Streak, DateTimeOffset? LastUtc, string? Detail) GetGraphicsDeviceFailureSnapshot()
    {
        lock (_activityLock)
        {
            return (_graphicsDeviceFailureStreak, _lastGraphicsDeviceFailureUtc, _lastGraphicsDeviceFailureDetail);
        }
    }

    /// <summary>
    /// True once this VM has failed graphics initialization enough times in one incident that
    /// relaunching the client again is pointless. The host escalates to a VM power cycle.
    /// </summary>
    private bool IsGraphicsDeviceFailureExhausted()
    {
        lock (_activityLock)
        {
            return _graphicsDeviceFailureStreak >= GraphicsDeviceFailureRelaunchLimit
                && _lastGraphicsDeviceFailureUtc is { } last
                && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(GraphicsDeviceFailureIncidentWindowMinutes);
        }
    }

    /// <summary>
    /// Status view of a settings-file failure: whether this client is sitting on D2R's first-run
    /// gamma calibration screen (which means it reset its own Settings.json), and what repair this
    /// agent has already accepted. Null when there is nothing to say, so agents and hosts that
    /// predate this field read alike.
    /// </summary>
    private object? DescribeSettingsRepair(VisibleD2RState visibleState)
    {
        // Heartbeats may legally be five minutes apart, while unconfirmed sightings intentionally
        // expire after 30 seconds. Confirm immediately from an independent capture so autonomous
        // status collection can arm repair without relying on a later menu command or heartbeat.
        if (visibleState == VisibleD2RState.GammaCalibration
            && !IsGammaCalibrationIncidentConfirmed())
        {
            ConfirmGammaCalibrationWithImmediateSecondProbe();
        }

        var path = D2RSettingsFile.ResolveSettingsPath(_config.D2RSettingsPath);
        int sightings;
        DateTimeOffset? firstUtc;
        DateTimeOffset? lastUtc;
        bool gammaIncidentConfirmed;
        int repairsApplied;
        DateTimeOffset? lastRepairUtc;
        string? lastRepairMessage;
        D2RSettingsRepairPhase? journalPhase;
        int incidentRepairAttempts;
        DateTimeOffset? incidentFirstRepairUtc;
        DateTimeOffset? incidentLastRepairUtc;
        string? repairJournalError;
        string? repairJournalWarning;
        lock (_activityLock)
        {
            RefreshPersistedSettingsRepairStateLocked(path);
            NormalizePersistedSettingsRepairBudgetLocked(DateTimeOffset.UtcNow);
            if (ShouldTransitionReadyRepairToPrepared(
                    visibleState == VisibleD2RState.GammaCalibration,
                    IsGammaCalibrationConfirmedLocked(),
                    _settingsRepairPhase,
                    _settingsRepairJournalError is not null))
            {
                TransitionReadyRepairToPreparedLocked(path);
            }

            sightings = _gammaCalibrationSightings;
            firstUtc = _firstGammaCalibrationUtc;
            lastUtc = _lastGammaCalibrationUtc;
            gammaIncidentConfirmed = IsGammaCalibrationConfirmedLocked();
            repairsApplied = _settingsRepairsApplied;
            lastRepairUtc = _lastSettingsRepairUtc;
            lastRepairMessage = _lastSettingsRepairMessage;
            journalPhase = _settingsRepairPhase;
            incidentRepairAttempts = _settingsRepairIncidentAttempts;
            incidentFirstRepairUtc = _settingsRepairIncidentFirstAttemptUtc;
            incidentLastRepairUtc = _settingsRepairIncidentLastAttemptUtc;
            repairJournalError = _settingsRepairJournalError;
            repairJournalWarning = _settingsRepairJournalWarning;
        }

        var detected = visibleState == VisibleD2RState.GammaCalibration;
        var needsReadyAfterSettingsRepair = journalPhase == D2RSettingsRepairPhase.Ready;
        var needsDonorSettings = _config.SettingsRepairEnabled
            && repairJournalError is null
            && journalPhase != D2RSettingsRepairPhase.Ready
            && (gammaIncidentConfirmed || journalPhase == D2RSettingsRepairPhase.Prepared);
        if (!detected
            && sightings == 0
            && repairsApplied == 0
            && journalPhase is null
            && incidentRepairAttempts == 0
            && repairJournalError is null
            && repairJournalWarning is null)
        {
            return null;
        }

        // Only read the file while something is actually wrong. Keying this on repairsApplied too
        // meant every status collection for the rest of the process re-read and hashed a 4 KB file
        // to report a repair that had already succeeded.
        D2RSettingsSnapshot? settingsFile = null;
        string? settingsError = null;
        if (detected || sightings > 0)
        {
            if (D2RSettingsFile.TryRead(path, out var snapshot, out var readError))
            {
                settingsFile = snapshot;
            }
            else
            {
                settingsError = readError;
            }
        }

        return new
        {
            detected,
            state = detected ? nameof(VisibleD2RState.GammaCalibration) : "None",
            sightings,
            confirmSightings = GammaCalibrationConfirmSightings,
            // The flag the host acts on. Repairs are opt-out per VM, and one sighting is not
            // enough. Once two looks confirm the incident, keep advertising it even if D2R has
            // stopped: settings_repair intentionally quits the client before writing, and a
            // cancellation or failed write must remain retryable on the next host sweep.
            needsDonorSettings,
            repairEnabled = _config.SettingsRepairEnabled,
            firstSeenUtc = firstUtc,
            lastSeenUtc = lastUtc,
            settingsPath = path,
            settingsReadable = settingsFile is not null,
            settingsSha256 = settingsFile?.Sha256,
            settingsLength = settingsFile?.Length,
            settingsLastWriteUtc = settingsFile?.LastWriteUtc,
            settingsError,
            repairsApplied,
            lastRepairUtc,
            lastRepairMessage,
            needsReadyAfterSettingsRepair,
            journalPhase = journalPhase?.ToString(),
            repairBlocked = repairJournalError is not null,
            repairJournalError,
            repairJournalWarning,
            // settings_repair replacement attempts durably dispatched in this still-unresolved
            // incident. PREPARED charges the attempt before quit/copy, so the host can rehydrate
            // its destructive-copy budget without a crash creating a free replay.
            incidentRepairAttempts,
            incidentFirstRepairUtc,
            incidentLastRepairUtc
        };
    }

    private void LoadPersistedSettingsRepairState()
    {
        var path = D2RSettingsFile.ResolveSettingsPath(_config.D2RSettingsPath);
        if (path is null)
        {
            if (!string.IsNullOrWhiteSpace(_config.D2RSettingsPath))
            {
                _settingsRepairJournalError =
                    $"Configured d2rSettingsPath \"{_config.D2RSettingsPath}\" is invalid or cannot be resolved.";
                _lastSettingsRepairMessage =
                    $"Settings repair and menu input are blocked: {_settingsRepairJournalError}";
            }

            return;
        }

        var readResult = D2RSettingsFile.ReadRepairState(path, out var state, out var readError);
        if (readResult == D2RSettingsRepairStateReadResult.Invalid)
        {
            _settingsRepairJournalError = readError;
            _lastSettingsRepairMessage =
                $"The settings-repair journal is invalid or unreadable; repair and menu input are blocked: {readError}";
            return;
        }

        if (readResult == D2RSettingsRepairStateReadResult.Missing)
        {
            return;
        }

        ApplyPersistedSettingsRepairStateLocked(state);
        NormalizePersistedSettingsRepairBudgetLocked(DateTimeOffset.UtcNow);
    }

    private void NormalizePersistedSettingsRepairBudgetLocked(DateTimeOffset nowUtc)
    {
        var expiredNow = _settingsRepairIncidentAttempts > 0
            && _settingsRepairIncidentLastAttemptUtc is { } lastAttemptUtc
            && nowUtc - lastAttemptUtc >= D2RSettingsRepairState.IncidentWindow;
        if ((!expiredNow && !_settingsRepairBudgetJournalRewritePending)
            || _settingsRepairPhase is null
            || _settingsRepairJournalError is not null
            || _settingsRepairTransactionInProgress)
        {
            return;
        }

        if (expiredNow)
        {
            _settingsRepairIncidentAttempts = 0;
            _settingsRepairIncidentFirstAttemptUtc = null;
            _settingsRepairIncidentLastAttemptUtc = null;
        }

        var path = D2RSettingsFile.ResolveSettingsPath(_config.D2RSettingsPath);
        var currentState = new D2RSettingsRepairState(
            D2RSettingsRepairState.CurrentSchemaVersion,
            _settingsRepairPhase.Value,
            _settingsRepairIncidentAttempts,
            _settingsRepairIncidentFirstAttemptUtc,
            _settingsRepairIncidentLastAttemptUtc);
        if (!D2RSettingsFile.TryWriteRepairState(path, currentState, out var stateError))
        {
            // An expired in-memory count still stays expired so status cannot block forever. A
            // later load normalizes by the persisted timestamp, and every status pass retries the
            // write whether this is expiry cleanup or recovery from an earlier journal failure.
            _settingsRepairBudgetJournalRewritePending = true;
            _settingsRepairJournalWarning = expiredNow
                ? $"The settings-repair attempt budget expired in memory, but its journal could not be updated: {stateError}"
                : $"The {_settingsRepairPhase} settings-repair journal still could not be persisted: {stateError}";
            _lastSettingsRepairMessage = _settingsRepairJournalWarning;
        }
        else
        {
            _settingsRepairBudgetJournalRewritePending = false;
            _settingsRepairJournalWarning = null;
        }
    }

    private void RefreshPersistedSettingsRepairStateLocked(string? path)
    {
        if (path is null || _settingsRepairTransactionInProgress)
        {
            return;
        }

        var readResult = D2RSettingsFile.ReadRepairState(path, out var state, out var readError);
        if (readResult == D2RSettingsRepairStateReadResult.Invalid)
        {
            _settingsRepairJournalError = readError;
            _lastSettingsRepairMessage =
                $"The settings-repair journal is invalid or unreadable; repair and menu input are blocked: {readError}";
            return;
        }

        if (readResult == D2RSettingsRepairStateReadResult.Missing)
        {
            // Missing is normal. If an operator removed an invalid journal, that is the explicit
            // recovery action which clears fail-closed mode. Do not erase an in-memory valid phase
            // merely because its durable rewrite disappeared during this process.
            if (_settingsRepairJournalError is not null)
            {
                if (_settingsRepairPhase is null)
                {
                    ClearPersistedSettingsRepairStateLocked();
                }
                else
                {
                    _settingsRepairJournalError = null;
                    _settingsRepairBudgetJournalRewritePending = true;
                    _settingsRepairJournalWarning =
                        $"The invalid journal was removed; restoring the known in-memory {_settingsRepairPhase} phase to disk.";
                }
            }

            return;
        }

        var wasBlocked = _settingsRepairJournalError is not null;
        _settingsRepairJournalError = null;
        if (ShouldAdoptPersistedSettingsRepairState(
                _settingsRepairPhase,
                _settingsRepairBudgetJournalRewritePending,
                wasBlocked,
                state.Phase))
        {
            ApplyPersistedSettingsRepairStateLocked(state);
            return;
        }

        if (_settingsRepairPhase == D2RSettingsRepairPhase.Ready
            && state.Phase == D2RSettingsRepairPhase.Prepared)
        {
            // READY is proof that this process already completed the copy. A stale PREPARED file
            // may survive a failed READY write or a transient unreadable-journal interval, but it
            // must never roll the live process back into donor-ready state. Rewrite our known
            // READY state on the Normalize pass immediately following this refresh.
            _settingsRepairBudgetJournalRewritePending = true;
            _settingsRepairJournalWarning =
                "Ignored a stale Prepared repair journal because this process already reached Ready; restoring Ready to disk.";
            _lastSettingsRepairMessage = _settingsRepairJournalWarning;
        }
    }

    internal static bool ShouldAdoptPersistedSettingsRepairState(
        D2RSettingsRepairPhase? inMemoryPhase,
        bool journalRewritePending,
        bool wasBlocked,
        D2RSettingsRepairPhase persistedPhase)
    {
        if (inMemoryPhase is null)
        {
            return true;
        }

        // A durable READY is always the safer direction: it prevents replaying a copy which may
        // already have completed, even if an older in-memory budget rewrite was pending.
        if (inMemoryPhase == D2RSettingsRepairPhase.Prepared
            && persistedPhase == D2RSettingsRepairPhase.Ready)
        {
            return true;
        }

        // Never downgrade proof of a completed copy from READY to PREPARED.
        if (inMemoryPhase == D2RSettingsRepairPhase.Ready
            && persistedPhase == D2RSettingsRepairPhase.Prepared)
        {
            return false;
        }

        return wasBlocked
            && !journalRewritePending
            && inMemoryPhase == persistedPhase;
    }

    internal static bool ShouldTransitionReadyRepairToPrepared(
        bool gammaDetected,
        bool gammaIncidentConfirmed,
        D2RSettingsRepairPhase? phase,
        bool journalBlocked)
    {
        return gammaDetected
            && gammaIncidentConfirmed
            && phase == D2RSettingsRepairPhase.Ready
            && !journalBlocked;
    }

    private void TransitionReadyRepairToPreparedLocked(string? path)
    {
        if (_settingsRepairTransactionInProgress
            || _settingsRepairJournalError is not null
            || _settingsRepairPhase != D2RSettingsRepairPhase.Ready)
        {
            return;
        }

        var retryState = new D2RSettingsRepairState(
            D2RSettingsRepairState.CurrentSchemaVersion,
            D2RSettingsRepairPhase.Prepared,
            _settingsRepairIncidentAttempts,
            _settingsRepairIncidentFirstAttemptUtc,
            _settingsRepairIncidentLastAttemptUtc);
        if (!D2RSettingsFile.TryWriteRepairState(path, retryState, out var stateError))
        {
            // Keep Ready in memory until Prepared is durable. Advertising donor work before that
            // point would let a duplicate command race a process/VM crash back to Ready.
            _settingsRepairJournalWarning =
                $"Post-copy Gamma was confirmed, but the retry phase could not be persisted: {stateError}";
            _lastSettingsRepairMessage = _settingsRepairJournalWarning;
            return;
        }

        _settingsRepairPhase = D2RSettingsRepairPhase.Prepared;
        _settingsRepairBudgetJournalRewritePending = false;
        _settingsRepairJournalWarning = null;
        _lastSettingsRepairMessage =
            "The copied settings still reached Gamma Calibration; durable Prepared authorizes the next bounded donor attempt.";
    }

    private void ApplyPersistedSettingsRepairStateLocked(D2RSettingsRepairState state)
    {
        _settingsRepairPhase = state.Phase;
        _settingsRepairIncidentAttempts = state.AttemptCount;
        _settingsRepairIncidentFirstAttemptUtc = state.FirstAttemptUtc;
        _settingsRepairIncidentLastAttemptUtc = state.LastAttemptUtc;
        _settingsRepairBudgetJournalRewritePending = false;
        _settingsRepairJournalError = null;
        _settingsRepairJournalWarning = null;
        if (state.Phase == D2RSettingsRepairPhase.Ready)
        {
            _settingsRepairsApplied = Math.Max(_settingsRepairsApplied, 1);
            _lastSettingsRepairUtc ??= state.LastAttemptUtc;
        }

        _lastSettingsRepairMessage =
            $"Reloaded an unresolved {state.Phase} settings-repair incident from disk.";
    }

    private void ClearPersistedSettingsRepairStateLocked()
    {
        _settingsRepairPhase = null;
        _settingsRepairIncidentAttempts = 0;
        _settingsRepairIncidentFirstAttemptUtc = null;
        _settingsRepairIncidentLastAttemptUtc = null;
        _settingsRepairBudgetJournalRewritePending = false;
        _settingsRepairJournalError = null;
        _settingsRepairJournalWarning = null;
        _lastSettingsRepairMessage = null;
    }

    /// <summary>
    /// Status view of the graphics-initialization incident: whether the dialog is on screen right
    /// now, how many dismiss-and-relaunch attempts this agent has already spent on it, and whether
    /// it has given up and needs the host to power-cycle the VM. Null when there is nothing to say.
    /// </summary>
    private object? DescribeGraphicsDeviceFailure(VisibleD2RState visibleState)
    {
        var (streak, lastUtc, detail) = GetGraphicsDeviceFailureSnapshot();
        var detected = visibleState == VisibleD2RState.GraphicsDeviceFailure;
        if (!detected && streak == 0)
        {
            return null;
        }

        return new
        {
            detected,
            streak,
            relaunchLimit = GraphicsDeviceFailureRelaunchLimit,
            needsVmPowerCycle = IsGraphicsDeviceFailureExhausted(),
            lastSeenUtc = lastUtc,
            detail
        };
    }

    private string FormatGraphicsDeviceFailureSuffix()
    {
        var (streak, _, detail) = GetGraphicsDeviceFailureSnapshot();
        if (streak == 0)
        {
            return "";
        }

        // "Recovery attempts", not "failures": an undismissable dialog is one failure that this
        // agent tried to clear N times, and both roads end at the same VM power cycle.
        return IsGraphicsDeviceFailureExhausted()
            ? $" This incident has spent all {streak} graphics-device recovery attempts ({detail}); the guest's display driver is not recovering and this VM needs a power cycle."
            : $" Graphics-device recovery attempts in this incident: {streak} ({detail}).";
    }

    // Bounded because this now runs on the status path. The scan's own cross-process text reads
    // are individually capped (SendMessageTimeout + SMTO_ABORTIFHUNG), but status collection is
    // exactly where one slow Win32 call has wedged this agent before - see
    // detection-and-status-troubleshooting.md #4 - so the whole probe gets a ceiling too, and a
    // timeout reads as "no dialog" rather than delaying the status reply.
    private D2RGraphicsDeviceFailureDismissalResult DetectGraphicsDeviceFailureDialog()
    {
        if (!OperatingSystem.IsWindows())
        {
            return default;
        }

        try
        {
            return TryRunBounded(
                () => new WindowsInput().DetectD2RGraphicsDeviceFailureDialog(GetD2RProcessNames()),
                GraphicsDeviceFailureProbeBoundMs,
                fallback: default);
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary>
    /// Clears and relaunches from the graphics-initialization dialog outside any command. The
    /// launch and ready paths only probe while they are running, so a client that fails
    /// initialization while nothing is driving it (a crash mid-session, follow-auto stopped,
    /// a manual launch left alone) used to sit on this modal indefinitely with no detection,
    /// no report, and no recovery. The idle monitor already runs unconditionally; this makes it
    /// the one place that always notices.
    /// </summary>
    private async Task RecoverGraphicsDeviceFailureIfPresentAsync(
        Action<string> log,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var input = new WindowsInput();
        var detected = input.DetectD2RGraphicsDeviceFailureDialog(GetD2RProcessNames());
        if (!detected.Detected)
        {
            return;
        }

        if (IsGraphicsDeviceFailureExhausted())
        {
            // The dialog stays on screen until the host power-cycles the VM, and this monitor
            // ticks every few seconds - say it once every few minutes instead of filling the log
            // with the same line. (After the incident window lapses with no new sighting, the
            // budget resets and the agent tries again on its own.)
            if (ShouldLogGraphicsDeviceFailureGiveUp())
            {
                log($"Graphics-device failure watchdog: all {GetGraphicsDeviceFailureSnapshot().Streak} recovery "
                    + $"attempts spent ({detected.Describe()}); not relaunching again - this guest needs a VM power cycle.");
            }

            return;
        }

        var dismissal = await TryDismissGraphicsDeviceFailureAndWaitAsync(input, cancellationToken);
        if (!dismissal.DismissalSent)
        {
            log($"Graphics-device failure watchdog: could not dismiss the dialog ({dismissal.Describe()}).");
            return;
        }

        var launch = TrySendD2RLaunchCommand();
        var streak = GetGraphicsDeviceFailureSnapshot().Streak;
        log($"Graphics-device failure watchdog: dismissed the dialog (failure {streak} of "
            + $"{GraphicsDeviceFailureRelaunchLimit} this incident) and relaunched D2R: {launch.Message}");
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

    internal enum BrokenSessionRecoveryAction
    {
        // The last recovery was too recent; nothing was restarted this cycle.
        SkipRecentRestart,
        RestartD2R,
        RestartBattleNetAndD2R
    }

    internal enum GraphicsDeviceFailureReadyAction
    {
        None,
        SuppressInput,
        Relaunch
    }

    internal readonly record struct BrokenSessionRecoveryDecision(BrokenSessionRecoveryAction Action, int RecoveryStreak);

    // Pure decision half of RecoverFromBrokenBattleNetSessionAsync: given the last recovery
    // time and the running streak of recoveries that never produced a confirmed-healthy
    // session, choose what this recovery should restart. The first recovery of an incident
    // restarts D2R alone; a repeat inside the escalation window means that restart changed
    // nothing, so the launcher itself is wedged and gets cold-restarted too.
    internal static BrokenSessionRecoveryDecision ClassifyBrokenSessionRecovery(
        DateTimeOffset? lastRecoveryUtc,
        int recoveryStreak,
        DateTimeOffset nowUtc)
    {
        if (lastRecoveryUtc is { } last
            && nowUtc - last < TimeSpan.FromSeconds(BrokenSessionRestartCooldownSeconds))
        {
            return new(BrokenSessionRecoveryAction.SkipRecentRestart, recoveryStreak);
        }

        var streak = lastRecoveryUtc is { } previous
            && nowUtc - previous < TimeSpan.FromMinutes(BrokenSessionEscalationWindowMinutes)
            ? recoveryStreak + 1
            : 1;
        return new(
            streak >= 2 ? BrokenSessionRecoveryAction.RestartBattleNetAndD2R : BrokenSessionRecoveryAction.RestartD2R,
            streak);
    }

    // A stuck-offline character screen that won't reconnect via the Online tab means the
    // client's Battle.net session itself is wedged - no client-side click fixes that, only a
    // full close + relaunch does, so Battle.net can hand it a fresh session on the next login.
    // Skips (nothing restarted) if the last recovery was too recent, so a session that keeps
    // coming back broken doesn't turn into a tight kill/relaunch loop.
    //
    // One D2R restart is usually enough - but when the launcher ITSELF is wedged (Battle.net
    // stuck at "Connecting..." with the account never signing in), every relaunched D2R just
    // lands back on "Cannot Connect to Server", forever. Only killing Battle.net.exe clears
    // that state, so a repeat recovery with no healthy session observed since the last one
    // escalates to a cold restart of the launcher as well (ClassifyBrokenSessionRecovery);
    // MarkBattleNetSessionHealthy resets the streak once a live session is confirmed.
    private async Task<BrokenSessionRecoveryAction> RecoverFromBrokenBattleNetSessionAsync(CancellationToken cancellationToken)
    {
        BrokenSessionRecoveryDecision decision;
        lock (_activityLock)
        {
            decision = ClassifyBrokenSessionRecovery(
                _lastBrokenSessionRestartUtc,
                _brokenSessionRecoveryStreak,
                DateTimeOffset.UtcNow);
            if (decision.Action != BrokenSessionRecoveryAction.SkipRecentRestart)
            {
                _brokenSessionRecoveryStreak = decision.RecoveryStreak;
                _lastBrokenSessionRestartUtc = DateTimeOffset.UtcNow;
            }
        }

        switch (decision.Action)
        {
            case BrokenSessionRecoveryAction.RestartD2R:
                await RestartD2RAsync(cancellationToken);
                break;
            case BrokenSessionRecoveryAction.RestartBattleNetAndD2R:
                MarkCommandCheckpoint("RecoverFromBrokenBattleNetSession: D2R restart did not produce a working session; cold-restarting Battle.net too");
                KillD2R();
                _ = KillProcesses(GetBattleNetProcessNames());
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                if (!_config.PreferBattleNetExecLaunch && !string.IsNullOrWhiteSpace(_config.D2RPath))
                {
                    // The direct-launch path never touches the launcher on its own; exec
                    // launches cold-start Battle.net inside LaunchD2RAsync (with a retry).
                    _ = LaunchBattleNet();
                }

                await LaunchD2RAsync(cancellationToken);
                break;
        }

        return decision.Action;
    }

    private void MarkBattleNetSessionHealthy()
    {
        lock (_activityLock)
        {
            _brokenSessionRecoveryStreak = 0;
        }
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
                    await QuitIfStuckLoadScreenAsync(log ?? (_ => { }), cancellationToken);
                    await RecoverGraphicsDeviceFailureIfPresentAsync(log ?? (_ => { }), cancellationToken);
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

    // A client that crashes or freezes on the game-load screen never resolves on its own,
    // so every follow-auto cycle and menu command just keeps failing (or waiting) forever
    // while the fleet stalls. Observed live twice: a VM wedged at load_screen_phase_2 for
    // 5+ minutes (v0.2.193's trigger) and again for 45+ minutes on v0.2.193 itself. Once the
    // client is quit, the existing machinery recovers on its own - the host's next
    // follow-auto cycle runs menu_ready, which relaunches and re-readies the client.
    //
    // The quit decision is deliberately keyed on the black-surround pixel signature persisting
    // across monitor ticks, with one explicit exclusion for the first-run Gamma Calibration
    // screen. That screen shares the signature but needs a settings-file repair, not a relaunch.
    // Apart from that exclusion, the watchdog never keys on what the screen CLASSIFIES as. v0.2.193
    // required a continuous streak of Unknown classifications first, and that's exactly why
    // it never fired on the second live wedge: the load screen's doorway artwork brightens
    // as loading progresses, and a brighter frame crosses IsDiabloSplashScreen's thresholds
    // (the reference capture's prompt region already reads orange 0.062 vs the 0.04 floor;
    // brightening the panel 1.3x flips the whole check true - measured, not theorized). The
    // frozen frame classified DiabloSplash, a recognized state, so the Unknown streak reset
    // on every observation and the watchdog sat disarmed while follow-auto "waited for
    // ready" indefinitely. The surround regions all sit outside the artwork panel, so no
    // amount of panel brightness can touch them.
    //
    // Safety (Hardcore makes a wrong quit expensive): every LoadScreenSurroundRegion must
    // read near-black with real pixel data on ticks spanning StuckLoadScreenQuitMinutes -
    // the bottom-center region overlaps the always-bright in-game HUD bar, so no live game
    // can confirm (pinned by StuckLoadScreenSurroundTests), and degraded sampling (the
    // v0.2.93 DWM stall class) returns bounded fallbacks, which reset the streak instead of
    // extending it.
    private async Task QuitIfStuckLoadScreenAsync(Action<string> log, CancellationToken cancellationToken)
    {
        if (!_config.StuckLoadScreenQuitEnabled || !OperatingSystem.IsWindows())
        {
            return;
        }

        if (!IsD2RRunning())
        {
            _stuckSurroundSinceUtc = null;
            return;
        }

        var input = new WindowsInput();
        if (!IsStuckLoadScreenConfirmed(input))
        {
            _stuckSurroundSinceUtc = null;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_stuckSurroundSinceUtc is not { } surroundSince)
        {
            _stuckSurroundSinceUtc = now;
            return;
        }

        var timeout = TimeSpan.FromMinutes(Math.Max(_config.StuckLoadScreenQuitMinutes, 1));
        var stuckFor = now - surroundSince;
        if (stuckFor < timeout)
        {
            return;
        }

        MarkCommandCheckpoint($"stuck-load-screen watchdog: black surround held for {stuckFor.TotalMinutes:N0}m; quitting D2R");
        log($"D2R appears stuck on the game-load screen (black surround held for {stuckFor.TotalMinutes:N0} minute(s)); quitting so the next ready cycle can relaunch it.");
        var quit = await QuitD2RAsync(cancellationToken);
        if (!quit.Ok)
        {
            log($"Stuck-load-screen quit: {quit.Message}");
        }

        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        if (IsD2RRunning())
        {
            // A frozen client ignores Alt+F4 - that's the expected case here, not a surprise.
            log("D2R is still running after Alt+F4; killing the process.");
            KillD2R();
        }

        _stuckSurroundSinceUtc = null;
    }

    private bool IsStuckLoadScreenConfirmed(WindowsInput input)
    {
        var surroundConfirmed = TryRunBounded(
            () =>
            {
                foreach (var region in D2RScreenClassifier.LoadScreenSurroundRegions)
                {
                    var stats = input.SampleRegion(
                        new AgentCommon.UiPoint(region.CenterX, region.CenterY),
                        region.WidthRatio,
                        region.HeightRatio,
                        MenuSampleGrid);
                    if (!D2RScreenClassifier.IsLoadScreenSurroundRegion(stats))
                    {
                        return false;
                    }
                }

                return true;
            },
            StuckLoadScreenSampleBoundMs,
            fallback: false);

        if (!surroundConfirmed)
        {
            return false;
        }

        // gamma_calibration_settings_reset.png satisfies every black-surround region. Only pay
        // for its seven-region detector after that cheap gate passes, then check both coordinate
        // paths so the watchdog cannot quit the client before the settings-repair sweep sees it.
        // Recording the observation also feeds the two-look incident tracker.
        var gammaCalibrationDetected = IsGammaCalibrationScreen(input, windowRelative: false)
            || IsGammaCalibrationScreen(input, windowRelative: true);
        if (gammaCalibrationDetected)
        {
            RecordObservedFrame(nameof(VisibleD2RState.GammaCalibration));
        }

        // Retain the veto if this particular GDI read times out after an earlier detector already
        // identified Gamma. An unconfirmed stale sighting is cleared by the next non-gamma frame
        // or process-instance change; a confirmed one intentionally lasts until repair/health.
        var gammaCalibrationIncident = gammaCalibrationDetected
            || ShouldSuppressMenuInputForSettingsReset();

        return IsStuckLoadScreenWatchdogCandidate(surroundConfirmed, gammaCalibrationIncident);
    }

    internal static bool IsStuckLoadScreenWatchdogCandidate(
        bool surroundConfirmed,
        bool gammaCalibrationIncident)
    {
        return surroundConfirmed && !gammaCalibrationIncident;
    }

    internal void ReconcileActivityFromLiveSnapshot(ActivitySnapshot liveActivity)
    {
        lock (_activityLock)
        {
            _activityState = liveActivity.State;
            _characterScreenIdleSinceUtc = liveActivity.CharacterScreenIdleSinceUtc;
            _lastLobbyOrGameInteractionUtc = liveActivity.LastLobbyOrGameInteractionUtc;
            _expectedLobbyAfterSaveExit = null;
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
                    _ = SamplePartyMembers();
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

    private CommandResult SamplePlayerCount(MenuCommandArgs args)
    {
        var templates = LoadLeaderTemplates();
        var (otherMembers, visibleState) = SamplePartyMembers();
        if (otherMembers is null)
        {
            return CommandResult.Success(
                $"Player count is not available from the current screen ({visibleState}).",
                new
                {
                    playerCount = (int?)null,
                    lastPartyMemberCount = _lastPartyMemberCount,
                    lastPartyMemberCountUtc = _lastPartyMemberCountUtc,
                    visibleState = visibleState.ToString(),
                    inGame = ClassifyPulseInGame(visibleState),
                    leaderBound = templates.Count > 0,
                    leaderPresent = (bool?)null,
                    leaderMatches = templates
                        .Select(entry => new { fingerprint = entry.Serialized, present = (bool?)null, slot = (int?)null, score = 0.0 })
                        .ToArray()
                });
        }

        var matches = SampleLeaderMatches(templates, otherMembers.Value, args.Fingerprint?.Trim());

        // Aggregate for display and older hosts: any nametag verifiably present wins; "all
        // sampled and all absent" is a definite false; anything else (some or all unverifiable)
        // stays null so it can never read as a leave trigger.
        var aggregatePresent = matches.Any(match => match.Present == true)
            ? true
            : matches.Count > 0 && matches.All(match => match.Present == false)
                ? false
                : (bool?)null;
        var bestMatch = matches
            .Where(match => match.Present == true)
            .OrderByDescending(match => match.BestScore)
            .FirstOrDefault();

        return CommandResult.Success(
            $"Player count sampled: {otherMembers.Value + 1}.",
            new
            {
                playerCount = otherMembers.Value + 1,
                lastPartyMemberCount = otherMembers.Value,
                lastPartyMemberCountUtc = _lastPartyMemberCountUtc,
                visibleState = visibleState.ToString(),
                inGame = ClassifyPulseInGame(visibleState),
                leaderBound = templates.Count > 0,
                leaderPresent = aggregatePresent,
                leaderSlot = bestMatch?.Slot,
                leaderScore = Math.Round(bestMatch?.BestScore ?? matches.Select(match => match.BestScore).DefaultIfEmpty(0.0).Max(), 3),
                leaderMatches = matches
                    .Select(match => new
                    {
                        fingerprint = match.Serialized,
                        present = match.Present,
                        slot = match.Slot,
                        score = Math.Round(match.BestScore, 3)
                    })
                    .ToArray()
            });
    }

    private sealed record LeaderMatchSample(string Serialized, bool? Present, int? Slot, double BestScore);

    // Issue #25 follow-up (bind-in-game), multi-alt version: scans the visible party-bar name
    // bands once and scores EVERY bound nametag against each captured band - the BitBlt per
    // slot is the expensive part and is shared, while each extra template costs only a CPU
    // Dice slide (~100k ops), so binding many alts adds no screen time. Per-template rules
    // mirror the old single-template scan: Present is null (not false) when that template
    // never got a comparable band (capture timeout, or a template bound at a different game
    // resolution than this vantage runs - a config mismatch must not read as "leader gone" and
    // mass-leave every game), and zero visible portraits while in-game is a definite absence
    // for every template. activeFingerprint is the host's session-locked nametag: once locked,
    // only that entry drives any decision, so the scan stops as soon as it is found.
    private IReadOnlyList<LeaderMatchSample> SampleLeaderMatches(
        IReadOnlyList<(string Serialized, PartyNameFingerprint Template)> templates,
        int visibleMembers,
        string? activeFingerprint)
    {
        if (templates.Count == 0 || !OperatingSystem.IsWindows())
        {
            return templates
                .Select(entry => new LeaderMatchSample(entry.Serialized, null, null, 0.0))
                .ToArray();
        }

        if (visibleMembers <= 0)
        {
            return templates
                .Select(entry => new LeaderMatchSample(entry.Serialized, false, null, 0.0))
                .ToArray();
        }

        var activeIndex = -1;
        for (var i = 0; i < templates.Count; i++)
        {
            if (string.Equals(templates[i].Serialized, activeFingerprint, StringComparison.Ordinal))
            {
                activeIndex = i;
                break;
            }
        }

        var input = new WindowsInput();
        var best = new double[templates.Count];
        var bestSlot = new int?[templates.Count];
        var comparable = new int[templates.Count];
        for (var slot = 1; slot <= Math.Min(visibleMembers, PartyMemberSlots.MaxSlots); slot++)
        {
            var slotToSample = slot;
            var mask = TryRunBounded<PartyNameFingerprint?>(
                () =>
                {
                    var band = input.CapturePixelRegion(
                        PartyMemberSlots.GetSlotNameBandCenter(slotToSample),
                        PartyMemberSlots.NameBandWidthRatio,
                        PartyMemberSlots.NameBandHeightRatio);
                    return PartyNameFingerprint.FromPixels(band.Rgb, band.Width, band.Height);
                },
                PartyFrameSampleBoundMs,
                fallback: null);
            if (mask is null)
            {
                continue;
            }

            var allMatched = true;
            for (var i = 0; i < templates.Count; i++)
            {
                var template = templates[i].Template;
                if (template.Width > mask.Width || template.Height > mask.Height)
                {
                    allMatched = false;
                    continue;
                }

                comparable[i]++;
                var score = template.BestScoreIn(mask);
                if (score > best[i])
                {
                    best[i] = score;
                    bestSlot[i] = slot;
                }

                allMatched &= best[i] >= PartyNameFingerprint.MatchThreshold;
            }

            if (allMatched || (activeIndex >= 0 && best[activeIndex] >= PartyNameFingerprint.MatchThreshold))
            {
                break;
            }
        }

        var results = new LeaderMatchSample[templates.Count];
        for (var i = 0; i < templates.Count; i++)
        {
            if (comparable[i] == 0)
            {
                results[i] = new LeaderMatchSample(templates[i].Serialized, null, null, 0.0);
                continue;
            }

            var present = best[i] >= PartyNameFingerprint.MatchThreshold;
            results[i] = new LeaderMatchSample(templates[i].Serialized, present, present ? bestSlot[i] : null, best[i]);
        }

        return results;
    }

    // A null count means "no party row to read", which used to be the whole answer - and that
    // conflated two very different situations for follow-auto's watch. A client sitting at the
    // lobby has verifiably fallen OUT of the game the host still counts it in (the observed
    // case: a post-join "Connection Interrupted" drops one bot back to the lobby, the join flow's
    // own retry never sees it because entry was already confirmed, and every later pulse from
    // that vantage reads as an inconclusive null - so it sat out the rest of the game while the
    // monitor still said 7/7 in game). A load screen or a failed capture, by contrast, is simply
    // unknown and must never be acted on. Return the state alongside the count so the host can
    // tell those apart.
    private (int? Count, VisibleD2RState State) SamplePartyMembers()
    {
        if (!_config.PartyMemberCountEnabled || !OperatingSystem.IsWindows())
        {
            return (null, VisibleD2RState.Unknown);
        }

        if (!IsD2RRunning())
        {
            return (null, VisibleD2RState.NotRunning);
        }

        // The party portrait row is only meaningful in an actual game - sampling it from the
        // lobby/join-create form would just read whatever happens to be in that screen corner.
        var input = new WindowsInput();
        var visibleState = DetectVisibleD2RState(input);
        if (visibleState != VisibleD2RState.InGame)
        {
            return (null, visibleState);
        }

        _lastPartyMemberCount = CountOtherPartyMembers(input);
        _lastPartyMemberCountUtc = DateTimeOffset.UtcNow;
        return (_lastPartyMemberCount, visibleState);
    }

    // Only screens that D2R cannot possibly show from inside a game count as a definite "out of
    // game". DiabloSplash and Unknown are the load-screen/degraded-capture states, so they stay
    // null: the host treats null as "no evidence" and never resyncs a bot on their word.
    internal static bool? ClassifyPulseInGame(VisibleD2RState state) => state switch
    {
        VisibleD2RState.InGame => true,
        VisibleD2RState.LobbyOrGame
            or VisibleD2RState.CharacterScreen
            or VisibleD2RState.OfflineCharacterScreen
            or VisibleD2RState.NotRunning
            // A client sitting on the graphics-initialization dialog has no rendered game at
            // all - that is evidence, not a degraded capture. The first-run gamma screen is the
            // same kind of evidence: the client is at a startup prompt, definitively not in a game.
            or VisibleD2RState.GraphicsDeviceFailure
            or VisibleD2RState.GammaCalibration => false,
        _ => null
    };

    // Scans slots in order and stops at the first miss rather than checking all 7 unconditionally
    // - D2R fills slots top-to-bottom with no gaps (PartyMemberSlots), so the common case (a
    // handful of accounts, not a full 8-player lobby) samples only as many regions as there are
    // actual members instead of always paying for 7.
    private int CountOtherPartyMembers(WindowsInput input)
    {
        for (var slot = 1; slot <= PartyMemberSlots.MaxSlots; slot++)
        {
            var ratio = TryRunBounded(
                () => input.SamplePartyFrameRatio(
                    PartyMemberSlots.GetSlotTopEdgeCenter(slot),
                    PartyMemberSlots.EdgeWidthRatio,
                    PartyMemberSlots.EdgeHeightRatio,
                    PartyMemberSlots.FrameSampleGrid),
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

        // A frozen client (the stuck-load-screen wedge) can fail the focus attempt or ignore
        // Alt+F4 entirely - the pre-hardening version reported "Alt+F4 sent" as success either
        // way and left the wedged process running, so a quit-all could read 4/4 succeeded
        // while a client was still stuck on the load screen. Verify the process actually
        // exited and escalate to a hard kill if it didn't; every caller of quit has already
        // decided the client should be gone, and killing is no more destructive to a live
        // character than the Alt+F4 it asked for.
        var input = new WindowsInput();
        if (input.TryFocusProcess(GetD2RProcessNames()))
        {
            await DelayStepAsync(cancellationToken);
            input.PressAltF4();
            for (var waited = 0; waited < 8 && IsD2RRunning(); waited += 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            if (!IsD2RRunning())
            {
                ClearD2RActivity();
                return CommandResult.Success("Alt+F4 sent to D2R.", await CollectStatusAsync(cancellationToken));
            }
        }

        KillD2R();
        return CommandResult.Success(
            "D2R did not close from Alt+F4 (frozen or unfocusable window); killed the process.",
            await CollectStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> ReadyClientAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        var followAutoRunId = args.FollowAutoRunId;
        var launch = BeginD2RReadyLaunch();
        if (!launch.Ok)
        {
            return launch;
        }

        var input = new WindowsInput();

        // A timed-out/cancelled follow attempt can leave this modal covering the lobby. It
        // makes every ordinary ready-state detector read Unknown and absorbs the startup
        // plan's clicks, so clear the dedicated two-button dialog before sending any generic
        // startup input. A confirmed dismissal is enough to report ready: this modal only
        // exists over the online lobby, and the next menu command performs its own checks.
        var leftoverJoinDialog = await DismissCannotJoinDialogDuringReadyAsync(input, cancellationToken);
        if (leftoverJoinDialog is not null)
        {
            return leftoverJoinDialog;
        }

        // menu_ready is frequently run defensively right before another menu command - notably
        // the follow-auto rejoin, which fires it immediately after the client left its last
        // game. When the client is already back at the lobby (confirmed operationally within
        // 1-2s of Save and Exit) or still in a game, there is nothing to launch or skip. Running
        // the startup plan below anyway is pure dead time: it pumps toward the CHARACTER screen
        // and only recognizes "already at the lobby/in game" AFTER the whole plan finishes (see
        // the ready.LastState check further down). Detect those states up front and return
        // immediately so a rejoin can start right away instead of waiting the plan out.
        if (TryRunBounded(() => IsInGameReady(input), InGameHudSampleBoundMs, fallback: false)
            || IsAnyLobbyEntryMenuVisible(input))
        {
            MarkLobbyOrGameInteraction("Ready flow short-circuited: client already at the lobby or in a game.");
            return CommandResult.Success(
                "D2R is already at the lobby or in a game; ready flow skipped.",
                await CollectStatusAsync(cancellationToken));
        }

        var battleNetRepair = new BattleNetInstallRepairState();
        var ready = await RunStartupReadyInputPlanUntilCharacterScreenAsync(
            input,
            cancellationToken,
            keepLaunchAlive: true,
            followAutoRunId: followAutoRunId,
            battleNetRepair: battleNetRepair);
        if (!ready.Ready)
        {
            var detectorReady = await PumpStartupSkipInputsUntilCharacterScreenAsync(
                input,
                cancellationToken,
                Math.Max(GetReadyLoopTimeoutSeconds(), MenuReadyFallbackTimeoutSeconds),
                keepLaunchAlive: true,
                followAutoRunId: followAutoRunId,
                battleNetRepair: battleNetRepair);
            ready = detectorReady with
            {
                Nudges = ready.Nudges + detectorReady.Nudges,
                TimeoutSeconds = ready.TimeoutSeconds + detectorReady.TimeoutSeconds,
                LaunchAttempts = ready.LaunchAttempts + detectorReady.LaunchAttempts,
                PlayClicks = ready.PlayClicks + detectorReady.PlayClicks,
                GraphicsDeviceFailureDismissals = ready.GraphicsDeviceFailureDismissals
                    + detectorReady.GraphicsDeviceFailureDismissals,
                LastLaunchMessage = detectorReady.LastLaunchMessage == "(none)"
                    ? ready.LastLaunchMessage
                    : detectorReady.LastLaunchMessage
            };
        }

        if (!ready.Ready && ready.LastState == ReadyScreenState.GammaCalibration)
        {
            // Named separately from the generic ready timeout because the operator response is
            // completely different: nothing about this client's launch is wrong, and relaunching
            // or power-cycling the VM will not change it. Its Settings.json needs replacing, and
            // the status payload carries everything the host needs to do that (d2rSettingsRepair).
            return CommandResult.Failure(
                "D2R is stopped on its first-run gamma calibration screen, which means it reset its own Settings.json. "
                    + "Relaunching cannot clear this; the file has to be replaced from a healthy fleet member "
                    + $"(see d2rSettingsRepair in this status). Initial launch result: {launch.Message}.",
                await CollectStatusAsync(cancellationToken));
        }

        if (!ready.Ready)
        {
            return CommandResult.Failure(
                $"{FormatCharacterScreenReadyFailure(ready, input)} Initial launch result: {launch.Message}. Ready loop sent {ready.LaunchAttempts} retry launch command(s), clicked Battle.net Play {ready.PlayClicks} time(s), and dismissed {ready.GraphicsDeviceFailureDismissals} failed-to-initialize-graphics dialog(s).{FormatGraphicsDeviceFailureSuffix()} Last launch result: {ready.LastLaunchMessage}.{FormatD2RProcessDiscoverySuffix()}",
                await CollectStatusAsync(cancellationToken));
        }

        if (ready.LastState == ReadyScreenState.CannotJoinCurrentCharacterDialog)
        {
            var cleanup = await DismissCannotJoinDialogDuringReadyAsync(input, cancellationToken);
            if (cleanup is not null)
            {
                return cleanup;
            }

            MarkLobbyOrGameInteraction(
                "Ready flow saw a leftover current-character join restriction that disappeared before cleanup.");
            return CommandResult.Success(
                "The leftover current-character join restriction is no longer visible; D2R is ready at the lobby.",
                await CollectStatusAsync(cancellationToken));
        }

        if (ready.LastState is ReadyScreenState.LobbyOrGame or ReadyScreenState.InGame)
        {
            MarkLobbyOrGameInteraction($"Ready flow detected {ready.LastState} already in progress.");
            return CommandResult.Success("D2R ready flow completed; already at the lobby or in a game.", await CollectStatusAsync(cancellationToken));
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
            if (!await ClickLobbyDirectAsync(input, cancellationToken, guardAgainstInGame: true))
            {
                return CommandResult.Failure(
                    "Could not safely click Lobby before attempting to follow a friend because the client might already be in-game.",
                    await CollectStatusAsync(cancellationToken));
            }

            await DelayStepAsync(cancellationToken);

            if (!IsAnyLobbyEntryMenuVisible(input))
            {
                return CommandResult.Failure(
                    $"Could not visually confirm the Lobby before attempting to follow a friend.{FormatLobbyConfirmationDiagnostics(input)}",
                    await CollectStatusAsync(cancellationToken));
            }

            MarkLobbyOrGameInteraction("Confirmed Lobby for follow after the cached state did not match what was actually on screen.");
        }

        var friends = await EnsureFriendsListVisibleAsync(input, "follow", cancellationToken);
        if (friends is not null)
        {
            return friends;
        }

        var friendRow = ResolveFriendRow(args.FriendRow);
        var entry = await ClickFriendJoinOptionUntilEnteredGameAsync(input, friendRow, "follow", cancellationToken);
        if (!entry.Entered)
        {
            return CommandResult.Failure(
                $"Clicked friend row {friendRow} Join Game, but the client did not enter the game within {Math.Max(_config.Ui.GameEntryStartTimeoutSeconds, 1)}s. {entry.Message}",
                await CollectStatusAsync(cancellationToken));
        }

        MarkLobbyOrGameInteraction("Joined friend game.");
        return CommandResult.Success("Join friend/follow flow completed.", await CollectStatusAsync(cancellationToken));
    }

    // Issue #25: capture a small grid-sample "fingerprint" of whoever is sitting in the selected friend row
    // right now, so the Host can distribute it to every agent and follow-auto can later recognize
    // that same name wherever it appears, instead of every agent needing a manually-supplied
    // friendRow that breaks the moment the friends list re-sorts. The operator is responsible for
    // making sure the intended friend is actually in the selected row before binding - this command
    // has no way to know who it's capturing, only where to look.
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
            if (!await ClickLobbyDirectAsync(input, cancellationToken, guardAgainstInGame: true))
            {
                return CommandResult.Failure(
                    "Could not safely click Lobby before capturing a follow-bind fingerprint because the client might already be in-game.",
                    await CollectStatusAsync(cancellationToken));
            }

            await DelayStepAsync(cancellationToken);

            if (!IsAnyLobbyEntryMenuVisible(input))
            {
                return CommandResult.Failure(
                    $"Could not visually confirm the Lobby before capturing a follow-bind fingerprint.{FormatLobbyConfirmationDiagnostics(input)}",
                    await CollectStatusAsync(cancellationToken));
            }

            MarkLobbyOrGameInteraction("Confirmed Lobby for follow-bind after the cached state did not match what was actually on screen.");
        }

        var friends = await EnsureFriendsListVisibleAsync(input, "follow-bind", cancellationToken);
        if (friends is not null)
        {
            return friends;
        }

        var friendRow = ResolveFriendRow(args.FriendRow);
        var region = D2RUiCoordinateCatalog.GetFriendRowFingerprintRegion(_config.Ui, row: friendRow);
        var samples = input.CaptureFingerprintGrid(region.Center, region.WidthRatio, region.HeightRatio, region.GridColumns, region.GridRows);
        var fingerprint = new FriendFingerprint(region.GridColumns, region.GridRows, samples);
        var collidingRows = FindFollowBindCollisions(input, fingerprint, friendRow);

        return CommandResult.Success(
            $"Captured a follow-bind fingerprint from friend row {friendRow}.",
            new { fingerprint = fingerprint.ToBase64(), friendRow, collidingRows });
    }

    // Bind-in-game cross-checks a fresh nametag capture against the other visible names; the
    // friend-row bind had no equivalent, so a name that another row can also satisfy was only ever
    // discovered at follow time as "ambiguous; not clicking a friend row this cycle" - after the
    // fleet was already trying to follow.
    //
    // The runtime separation rule is deliberately NOT reused here. Compared against the row it was
    // just taken from, a fresh capture scores near zero, so any rival looks far away by that rule
    // and nothing would ever be flagged. What actually matters is whether a rival clears the same
    // usability gate at all: at follow time the bound row's own score degrades to the same order as
    // its rivals' (list re-sorts, a friend coming online re-renders their name from dim to bright),
    // and any row already inside the gate can then tie or overtake it.
    private int[] FindFollowBindCollisions(WindowsInput input, FriendFingerprint fingerprint, int boundRow)
    {
        var maxRows = GetFollowFingerprintMaxScanRows(_config.Ui);
        // One bounded call for every row, matching the scan path: a per-row bounded capture can
        // exhaust the shared bounded-call slots when individual GDI reads go slow.
        var rowSamples = TryRunBounded<List<(int Row, byte[]? Samples)>?>(
            () =>
            {
                var results = new List<(int Row, byte[]? Samples)>();
                for (var row = 1; row <= maxRows; row++)
                {
                    if (row == boundRow)
                    {
                        continue;
                    }

                    var rowRegion = D2RUiCoordinateCatalog.GetFriendRowFingerprintRegion(_config.Ui, row);
                    var (searchHeight, searchRows) = GetFollowFingerprintSearchBand(rowRegion);
                    byte[]? captured;
                    try
                    {
                        captured = input.CaptureFingerprintGrid(
                            rowRegion.Center, rowRegion.WidthRatio, searchHeight, rowRegion.GridColumns, searchRows);
                    }
                    catch
                    {
                        captured = null;
                    }

                    results.Add((row, captured));
                }

                return results;
            },
            EntryLoopCheckBoundMs * maxRows,
            fallback: null);

        if (rowSamples is null)
        {
            // Inconclusive, not clean: report nothing rather than implying the capture was checked.
            return [];
        }

        var colliding = new List<int>();
        foreach (var (row, captured) in rowSamples)
        {
            if (captured is null)
            {
                continue;
            }

            var rowRegion = D2RUiCoordinateCatalog.GetFriendRowFingerprintRegion(_config.Ui, row);
            // Matched across alignments exactly like the follow-time scan: a row that only
            // collides once it shifts is still a row that will collide at follow time.
            var comparison = CompareFollowFingerprintAcrossAlignments(
                fingerprint, captured, rowRegion.GridColumns, rowRegion.GridRows);
            if (IsUsableFollowFingerprintMatch(comparison))
            {
                colliding.Add(row);
            }
        }

        return colliding.ToArray();
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

    // Advertised on every status frame so the host can spot a replica that diverged from its
    // authoritative copy - a VM that was offline during a bind, one rebuilt from a clean image,
    // or one added to the fleet afterwards - and repair it without the operator having to notice
    // and re-run the bind. Deliberately reported in the process-only status too: a VM in degraded
    // status collection is exactly the kind that has been out of touch and needs reconciling.
    // Any read failure reports as a missing template rather than throwing, so a corrupt file is
    // simply overwritten by the next push instead of silently disabling this VM's follow-auto.
    private static object CollectFollowTemplateDigests()
    {
        var errors = new List<string>();
        var friendDigest = FollowTemplateDigest.None;
        try
        {
            friendDigest = FollowTemplateDigest.OfFriendTemplate(
                File.Exists(FollowTemplatePath) ? File.ReadAllText(FollowTemplatePath) : null);
        }
        catch (Exception ex)
        {
            errors.Add($"{FollowTemplateFileName}: {ex.GetType().Name}: {ex.Message}");
        }

        var leaderDigest = FollowTemplateDigest.None;
        var leaderCount = 0;
        try
        {
            var leaderList = PartyNameFingerprintList.Normalize(
                File.Exists(LeaderTemplatePath) ? File.ReadAllText(LeaderTemplatePath) : null);
            leaderDigest = FollowTemplateDigest.OfLeaderList(leaderList);
            leaderCount = leaderList.Count;
        }
        catch (Exception ex)
        {
            errors.Add($"{LeaderTemplateFileName}: {ex.GetType().Name}: {ex.Message}");
        }

        return new
        {
            friendDigest,
            leaderDigest,
            leaderCount,
            error = errors.Count > 0 ? string.Join("; ", errors) : null
        };
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

        // A leader fingerprint only ever augments a follow-bind (it is follow-auto's in-game
        // "is the bound player still here" signal), so a full unbind clears it too rather than
        // leaving a stale leader mask to silently apply to the next, different bind.
        if (File.Exists(LeaderTemplatePath))
        {
            File.Delete(LeaderTemplatePath);
        }

        return CommandResult.Success("Follow-bind fingerprint cleared.");
    }

    // Issue #25 follow-up ("bind-in-game"): the party-bar name mask of the operator's own
    // character, captured via menu_follow_bind_game from one VM's vantage and distributed
    // host-side to every agent the same way follow-template.txt is. Same bypass-the-gate file
    // I/O reasoning as FollowSetTemplate above.
    private const string LeaderTemplateFileName = "leader-template.txt";

    private static string LeaderTemplatePath => Path.Combine(AppContext.BaseDirectory, LeaderTemplateFileName);

    // Multi-alt bind-in-game: the file holds one serialized nametag per line, in bind order
    // (that order is the rolodex order the host locks by). A pre-multi single-line file is a
    // one-entry list. Each entry is returned alongside its serialized form so callers can key
    // results by content - the host locks onto a nametag by its serialized string, never by a
    // list index, so agents whose lists diverged (offline during a bind) stay unambiguous.
    private static IReadOnlyList<(string Serialized, PartyNameFingerprint Template)> LoadLeaderTemplates()
    {
        try
        {
            if (!File.Exists(LeaderTemplatePath))
            {
                return Array.Empty<(string, PartyNameFingerprint)>();
            }

            return PartyNameFingerprintList.Normalize(File.ReadAllText(LeaderTemplatePath))
                .Select(serialized => (serialized, PartyNameFingerprint.FromBase64(serialized)!))
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<(string, PartyNameFingerprint)>();
        }
    }

    private static CommandResult FollowSetLeaderTemplate(MenuCommandArgs args)
    {
        var incoming = PartyNameFingerprintList.Normalize(args.Fingerprint);
        if (incoming.Count == 0)
        {
            return CommandResult.Failure("follow_set_leader_template requires at least one valid party-name fingerprint.");
        }

        IReadOnlyList<string> list = incoming;
        if (args.Append == true)
        {
            list = PartyNameFingerprintList.Normalize(
                File.Exists(LeaderTemplatePath) ? File.ReadAllText(LeaderTemplatePath) : null);
            foreach (var fingerprint in incoming)
            {
                list = PartyNameFingerprintList.Append(list, fingerprint);
            }
        }

        File.WriteAllText(LeaderTemplatePath, PartyNameFingerprintList.Serialize(list));
        return CommandResult.Success(
            args.Append == true ? "Leader nametag appended." : "Leader nametag list saved.",
            new { templatePath = LeaderTemplatePath, templateCount = list.Count });
    }

    // Bind rollback path: removes exactly one nametag (by serialized content) and leaves every
    // other bound alt untouched - a bad capture must not nuke the operator's whole rolodex.
    private static CommandResult FollowRemoveLeaderTemplate(MenuCommandArgs args)
    {
        var fingerprint = args.Fingerprint?.Trim();
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return CommandResult.Failure("follow_remove_leader_template requires the fingerprint to remove.");
        }

        var list = PartyNameFingerprintList.Normalize(
            File.Exists(LeaderTemplatePath) ? File.ReadAllText(LeaderTemplatePath) : null);
        var remaining = PartyNameFingerprintList.Remove(list, fingerprint);
        if (remaining.Count == 0)
        {
            if (File.Exists(LeaderTemplatePath))
            {
                File.Delete(LeaderTemplatePath);
            }
        }
        else
        {
            File.WriteAllText(LeaderTemplatePath, PartyNameFingerprintList.Serialize(remaining));
        }

        return CommandResult.Success(
            "Leader nametag removed.",
            new { templateCount = remaining.Count });
    }

    private static CommandResult FollowClearLeaderTemplate()
    {
        if (File.Exists(LeaderTemplatePath))
        {
            File.Delete(LeaderTemplatePath);
        }

        return CommandResult.Success("Leader nametags cleared.");
    }

    // Issue #25 follow-up: capture the party-bar name mask at the requested visible position
    // (1-7, counted left to right; the vantage character itself never appears in its own party
    // bar). This must run from inside an actual game - unlike FollowBindCaptureAsync there is no
    // navigation to do, because the party bar only exists in-game; the operator lines the game
    // up first (typically their own bot game where the member layout is known) and tells us
    // which position is theirs. Like the friend-row bind, this has no way to know WHO it is
    // capturing, only where to look, so the result echoes enough diagnostics (visible member
    // count, glyph box size) for the operator to sanity-check the capture.
    private CommandResult FollowBindInGameCapture(MenuCommandArgs args)
    {
        if (args.PartyPosition is not { } position || position < 1 || position > PartyMemberSlots.MaxSlots)
        {
            return CommandResult.Failure($"menu_follow_bind_game requires partyPosition between 1 and {PartyMemberSlots.MaxSlots}.");
        }

        if (!IsD2RRunning())
        {
            return CommandResult.Failure("D2R is not running, so there is no party bar to capture from.");
        }

        var input = FocusD2R();
        if (DetectVisibleD2RState(input) != VisibleD2RState.InGame)
        {
            return CommandResult.Failure("The vantage account is not visibly in a game; bind-in-game reads the in-game party bar.");
        }

        var visibleMembers = CountOtherPartyMembers(input);
        if (position > visibleMembers)
        {
            return CommandResult.Failure(
                $"Only {visibleMembers} party member portrait(s) are visible; position {position} has nobody to capture.");
        }

        var band = input.CapturePixelRegion(
            PartyMemberSlots.GetSlotNameBandCenter(position),
            PartyMemberSlots.NameBandWidthRatio,
            PartyMemberSlots.NameBandHeightRatio);
        var template = PartyNameFingerprint.FromPixels(band.Rgb, band.Width, band.Height)?.CropToGlyphBox();
        if (template is null)
        {
            return CommandResult.Failure(
                $"No name text was found under party position {position}. The name band may be obscured; try again with the party bar unobstructed.");
        }

        // The captured template must be unique among the names it will later be scanned
        // against: if it also clears the match threshold on ANOTHER visible member's band
        // right now (a short name contained in a longer one, or two visually similar names),
        // runtime pulses can keep reading the leader "present" off that other player after
        // the real leader leaves - follow-auto then never leaves. Bind time is the only
        // moment both names are known side by side, so measure it here and let the host warn.
        var ambiguousSlots = new List<int>();
        for (var slot = 1; slot <= Math.Min(visibleMembers, PartyMemberSlots.MaxSlots); slot++)
        {
            if (slot == position)
            {
                continue;
            }

            var otherBand = input.CapturePixelRegion(
                PartyMemberSlots.GetSlotNameBandCenter(slot),
                PartyMemberSlots.NameBandWidthRatio,
                PartyMemberSlots.NameBandHeightRatio);
            var otherMask = PartyNameFingerprint.FromPixels(otherBand.Rgb, otherBand.Width, otherBand.Height);
            if (otherMask is null
                || template.Width > otherMask.Width
                || template.Height > otherMask.Height)
            {
                continue;
            }

            if (template.BestScoreIn(otherMask) >= PartyNameFingerprint.MatchThreshold)
            {
                ambiguousSlots.Add(slot);
            }
        }

        MarkCommandCheckpoint(
            $"FollowBindInGameCapture: captured position {position}/{visibleMembers}, glyph box {template.Width}x{template.Height}, {template.BitCount} text pixels");
        return CommandResult.Success(
            $"Captured the party-bar name at position {position} of {visibleMembers} visible member(s).",
            new
            {
                fingerprint = template.ToBase64(),
                partyPosition = position,
                visibleMembers,
                glyphWidth = template.Width,
                glyphHeight = template.Height,
                glyphBits = template.BitCount,
                ambiguousSlots = ambiguousSlots.ToArray()
            });
    }

    private CommandResult FollowStopAuto(MenuCommandArgs args)
    {
        var stoppedThroughRunId = RecordFollowAutoStoppedThrough(args.FollowAutoRunId);
        MarkCommandCheckpoint(args.FollowAutoRunId is > 0
            ? $"follow-auto stop requested through run {args.FollowAutoRunId.Value}"
            : "follow-auto stop requested without a run id");
        return CommandResult.Success(
            "Follow-auto stop signal recorded.",
            new
            {
                followAutoRunId = args.FollowAutoRunId,
                stoppedThroughRunId
            });
    }

    private long RecordFollowAutoStoppedThrough(long? followAutoRunId)
    {
        if (followAutoRunId is not > 0)
        {
            return Volatile.Read(ref _followAutoStoppedThroughRunId);
        }

        var runId = followAutoRunId.Value;
        while (true)
        {
            var current = Volatile.Read(ref _followAutoStoppedThroughRunId);
            if (current >= runId)
            {
                return current;
            }

            var observed = Interlocked.CompareExchange(ref _followAutoStoppedThroughRunId, runId, current);
            if (observed == current)
            {
                return runId;
            }
        }
    }

    private void ThrowIfFollowAutoStopped(long? followAutoRunId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stoppedThroughRunId = Volatile.Read(ref _followAutoStoppedThroughRunId);
        if (!IsFollowAutoRunStopped(followAutoRunId, stoppedThroughRunId))
        {
            return;
        }

        MarkCommandCheckpoint($"follow-auto run {followAutoRunId!.Value} stopped before next click");
        throw new OperationCanceledException("follow-auto was stopped.", cancellationToken);
    }

    internal static bool IsFollowAutoRunStopped(long? followAutoRunId, long stoppedThroughRunId)
    {
        return followAutoRunId is > 0
            && stoppedThroughRunId >= followAutoRunId.Value;
    }

    // One cycle of follow-auto: if nobody's bound, say so (not a failure - this is the normal
    // state for any account that isn't part of a follow-auto run). If bound, scan every visible
    // friend row for a fingerprint match - not just row 1 - since other tracked friends coming
    // online can outrank the bound friend in Battle.net's own online-sort at any point.
    private async Task<CommandResult> FollowAutoCheckAsync(MenuCommandArgs args, CancellationToken cancellationToken)
    {
        var followAutoRunId = args.FollowAutoRunId;
        ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);

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
        if (!CanAutoClickFollowFingerprint(template))
        {
            return CommandResult.Success(
                "Follow-bind fingerprint was captured with an older, low-detail grid. Re-run `/d2r follow bind:true` before follow-auto can safely click friend rows.",
                new
                {
                    bound = true,
                    joined = false,
                    templatePath = templateLoad.Path,
                    templateExists = templateLoad.Exists,
                    templateLength = templateLoad.ContentLength,
                    templateGridColumns = template.GridColumns,
                    templateGridRows = template.GridRows
                });
        }

        ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);
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

        ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);
        var usedExpectedPostSaveExitLobby = ConsumeExpectedLobbyAfterSaveExit(followAutoRunId);
        if (usedExpectedPostSaveExitLobby)
        {
            MarkCommandCheckpoint(
                "FollowAutoCheckAsync: expected post-save-exit lobby; skipping initial and lobby classifiers");
        }

        CommandResult? unexpectedGameLeave = null;
        var recoveredOpenModernPauseMenu = false;
        var unexpectedGameRecovery = ShouldRunFollowAutoScreenClassifiers(usedExpectedPostSaveExitLobby)
            ? await RunFollowAutoInGameRecoveryAsync(
            detectInGameMatch: () => TryRunBounded<InGameHudMatchKind?>(
                () => DetectBestInGameHudMatch(input),
                InGameSafetyCheckBoundMs,
                fallback: null),
            leaveGame: async token =>
            {
                MarkCommandCheckpoint("FollowAutoCheckAsync: confirmed unexpected game by strict HUD globes; using Save and Exit");
                unexpectedGameLeave = await SaveAndExitAsync(followAutoRunId, token);
                return unexpectedGameLeave.Ok;
            },
            leaveOpenPauseMenu: async token =>
            {
                recoveredOpenModernPauseMenu = true;
                MarkCommandCheckpoint("FollowAutoCheckAsync: confirmed an open Save and Exit menu; clicking its Save and Exit button directly");
                unexpectedGameLeave = await SaveAndExitFromOpenModernPauseMenuAsync(
                    input,
                    followAutoRunId,
                    token);
                return unexpectedGameLeave.Ok;
            },
            cancellationToken)
            : FollowAutoInGameRecoveryOutcome.NotInGame;

        if (unexpectedGameRecovery == FollowAutoInGameRecoveryOutcome.LeftGame)
        {
            return CommandResult.Success(
                recoveredOpenModernPauseMenu
                    ? "This pending client was already in a game with the Save and Exit menu open. Clicked Save and Exit directly; waiting for the next follow-auto cycle to join the bound friend."
                    : "This pending client was already in a game. Confirmed it by the in-game HUD globes and used Save and Exit; waiting for the next follow-auto cycle to join the bound friend.",
                new
                {
                    bound = true,
                    joined = false,
                    d2rReady = false,
                    recoveredUnexpectedGame = true,
                    recoveredOpenModernPauseMenu
                });
        }

        if (unexpectedGameRecovery == FollowAutoInGameRecoveryOutcome.LeaveFailed)
        {
            return CommandResult.Failure(
                $"Follow-auto confirmed this pending client was already in a game, but Save and Exit failed: {unexpectedGameLeave?.Message ?? "unknown failure"}",
                unexpectedGameLeave?.Data);
        }

        if (unexpectedGameRecovery == FollowAutoInGameRecoveryOutcome.DetectionInconclusive)
        {
            return CommandResult.Success(
                "Follow-auto suspected this pending client was already in a game, but a strict in-game HUD profile could not be confirmed; waiting for the next follow-auto cycle without clicking.",
                new
                {
                    bound = true,
                    joined = false,
                    d2rReady = false
                });
        }

        ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);
        // A previous follow attempt may have been cancelled or timed out after D2R rendered
        // this modal but before it could dismiss it. Clear it before trying to navigate the
        // lobby; otherwise the overlay absorbs every friends-list click and looks like a
        // generic "could not confirm lobby" failure.
        if (ShouldRunFollowAutoScreenClassifiers(usedExpectedPostSaveExitLobby)
            && IsCannotJoinCurrentCharacterDialogOpen(input))
        {
            MarkCommandCheckpoint("FollowAutoCheckAsync: current-character join restriction still visible; dismissing");
            var dismissed = await DismissCannotJoinCurrentCharacterDialogAsync(input, cancellationToken);
            return CurrentCharacterCannotJoinFollowResult(dismissed);
        }

        // Same leftover-modal hazard for "Game is full": a cancelled/timed-out attempt can leave
        // the OK dialog up, absorbing every lobby click. Report it as a full game (not a generic
        // navigation failure) so the host's park counter sees this attempt too.
        if (ShouldRunFollowAutoScreenClassifiers(usedExpectedPostSaveExitLobby)
            && IsGameFullDialogOpen(input))
        {
            MarkCommandCheckpoint("FollowAutoCheckAsync: game-is-full dialog still visible; dismissing");
            var dismissed = await DismissGameEntryErrorDialogAsync(input, cancellationToken);
            return GameFullFollowResult(dismissed);
        }

        ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);
        // An offline character screen that won't reconnect via the Online tab needs more than
        // another click - the client's Battle.net session is wedged, and only a close + relaunch
        // rehooks a fresh one. Handled here rather than inside EnsureLobbyOpenedAsync because a
        // follow-auto session is unattended and long-running (tolerates a "Waiting" cycle while
        // D2R comes back up); other menu commands surface the same offline condition as a
        // failure instead so an interactive caller isn't surprised by their game being restarted.
        if (ShouldRunFollowAutoScreenClassifiers(usedExpectedPostSaveExitLobby)
            && IsCharacterScreenOffline(input)
            && !await EnsureOnlineCharacterScreenAsync(input, cancellationToken))
        {
            MarkCommandCheckpoint("FollowAutoCheckAsync: offline character screen did not reconnect; recovering with a restart");
            var recovery = await RecoverFromBrokenBattleNetSessionAsync(cancellationToken);
            return CommandResult.Success(
                recovery switch
                {
                    BrokenSessionRecoveryAction.RestartD2R => "D2R's Battle.net session was stuck offline and the Online tab did not reconnect; restarted D2R to force a fresh session. Waiting for the next follow-auto cycle.",
                    BrokenSessionRecoveryAction.RestartBattleNetAndD2R => "D2R came back from a restart still stuck offline, so the Battle.net launcher itself is wedged (stuck Connecting); killed Battle.net.exe and cold-started the launcher and D2R. Waiting for the next follow-auto cycle.",
                    _ => "D2R's Battle.net session is still stuck offline after a recent restart; waiting before trying again."
                },
                new
                {
                    bound = true,
                    joined = false,
                    d2rReady = false,
                    templatePath = templateLoad.Path,
                    templateExists = templateLoad.Exists,
                    templateLength = templateLoad.ContentLength
                });
        }

        ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);
        if (!ShouldRunFollowAutoScreenClassifiers(usedExpectedPostSaveExitLobby))
        {
            MarkCommandCheckpoint(
                "FollowAutoCheckAsync: expected post-save-exit lobby; proceeding to Friends verification");
        }
        else
        {
            var lobby = await EnsureLobbyOpenedAsync(input, args, cancellationToken);
            ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);
            if (lobby is not null)
            {
                return lobby;
            }
        }

        if (ShouldRunFollowAutoScreenClassifiers(usedExpectedPostSaveExitLobby)
            && !IsAnyLobbyEntryMenuVisible(input))
        {
            var readyState = DetectReadyScreenStateStable(input);
            if (readyState == ReadyScreenState.GammaCalibration)
            {
                return await RefuseMenuInputForGammaCalibrationAsync(
                    "Follow-auto stopped before lobby navigation",
                    cancellationToken);
            }

            if (readyState is ReadyScreenState.DiabloSplash or ReadyScreenState.ConnectingToBattleNet)
            {
                MarkCommandCheckpoint($"FollowAutoCheckAsync: D2R is {readyState}; waiting for ready");
                return CommandResult.Success(
                    $"D2R is still at {readyState}; waiting for the next follow-auto cycle.",
                    new
                    {
                        bound = true,
                        joined = false,
                        d2rReady = false,
                        visibleState = readyState.ToString(),
                        templatePath = templateLoad.Path,
                        templateExists = templateLoad.Exists,
                        templateLength = templateLoad.ContentLength
                    });
            }

            MarkCommandCheckpoint("FollowAutoCheckAsync: lobby not visually confirmed - navigating directly");
            await SelectCharacterAsync(input, args.CharacterSlot, cancellationToken);
            ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);
            if (!await ClickLobbyDirectAsync(input, cancellationToken, guardAgainstInGame: true))
            {
                return CommandResult.Success(
                    "Lobby navigation was skipped because the in-game safety check was inconclusive; waiting for the next follow-auto cycle.",
                    new
                    {
                        bound = true,
                        joined = false,
                        d2rReady = false,
                        templatePath = templateLoad.Path,
                        templateExists = templateLoad.Exists,
                        templateLength = templateLoad.ContentLength
                    });
            }

            await DelayStepAsync(cancellationToken);
            ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);

            if (!IsAnyLobbyEntryMenuVisible(input))
            {
                return CommandResult.Failure(
                    $"Could not visually confirm the Lobby during a follow-auto check.{FormatLobbyConfirmationDiagnostics(input)}",
                    await CollectStatusAsync(cancellationToken));
            }

            MarkLobbyOrGameInteraction("Confirmed Lobby for follow-auto after the cached state did not match what was actually on screen.");
        }

        var friends = await EnsureFriendsListVisibleAsync(input, "follow-auto", cancellationToken);
        ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);
        if (friends is not null)
        {
            return friends;
        }

        // A live friends list proves this client's Battle.net session works again, so the
        // next stuck-offline detection is a fresh incident - not evidence that the last
        // restart failed (which is what escalates recovery to a Battle.net cold start).
        MarkBattleNetSessionHealthy();

        var maxRows = GetFollowFingerprintMaxScanRows(_config.Ui);
        var rowMatches = new List<FriendRowFingerprintMatch>();
        MarkCommandCheckpoint($"FollowAutoCheckAsync: sampling friend rows 1-{maxRows}");
        ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);
        // All rows in one bounded call - each row previously required its own GetDC(NULL)
        // round-trip; with up to 8 rows per cycle and threads=32 cap, a few cycles of
        // hung captures exhausted the slot budget. One call acquires one desktop DC and
        // captures all rows before releasing it.
        var allRowSamples = TryRunBounded(() =>
        {
            var results = new List<(int Row, byte[]? Samples)>();
            for (var row = 1; row <= maxRows; row++)
            {
                var region = D2RUiCoordinateCatalog.GetFriendRowFingerprintRegion(_config.Ui, row);
                var (searchHeight, searchRows) = GetFollowFingerprintSearchBand(region);
                byte[]? rowSamples;
                try
                {
                    rowSamples = input.CaptureFingerprintGrid(region.Center, region.WidthRatio, searchHeight, region.GridColumns, searchRows);
                }
                catch
                {
                    rowSamples = null;
                }
                results.Add((row, rowSamples));
            }
            return results;
        }, EntryLoopCheckBoundMs * maxRows, fallback: null);
        for (var row = 1; row <= maxRows; row++)
        {
            var region = D2RUiCoordinateCatalog.GetFriendRowFingerprintRegion(_config.Ui, row);
            var samples = allRowSamples?.FirstOrDefault(r => r.Row == row).Samples;
            var comparison = samples is null
                ? FriendFingerprintComparison.NotComparable
                : CompareFollowFingerprintAcrossAlignments(
                    template, samples, region.GridColumns, region.GridRows);
            rowMatches.Add(new FriendRowFingerprintMatch(row, comparison));
        }

        ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);
        var selection = SelectFollowFingerprintMatch(rowMatches);
        var scoreSummary = FormatFollowFingerprintScores(rowMatches);

        // A template captured on a different sampling grid can never compare against anything this
        // agent captures now (FriendFingerprint.Compare rejects a dimension mismatch outright), so
        // every row would report NotComparable and the generic "not confidently found" message
        // would send the operator hunting a detection bug that is really a stale bind. Name it.
        var currentRegion = D2RUiCoordinateCatalog.GetFriendRowFingerprintRegion(_config.Ui, row: 1);
        if (template.GridColumns != currentRegion.GridColumns || template.GridRows != currentRegion.GridRows)
        {
            MarkCommandCheckpoint(
                $"FollowAutoCheckAsync: bound fingerprint grid {template.GridColumns}x{template.GridRows} does not match this agent's {currentRegion.GridColumns}x{currentRegion.GridRows}");
            return CommandResult.Success(
                $"The bound friend fingerprint was captured on a {template.GridColumns}x{template.GridRows} sampling grid but this agent now samples {currentRegion.GridColumns}x{currentRegion.GridRows}, "
                    + "so it can never match. Re-run /d2r follow bind:true once to recapture it.",
                new { bound = false, joined = false, fingerprintGridStale = true });
        }

        if (selection.Status == FollowFingerprintSelectionStatus.NoUsableMatch || selection.Match is null)
        {
            MarkCommandCheckpoint($"FollowAutoCheckAsync: no confident bound friend match; {scoreSummary}");
            return CommandResult.Success(
                $"Bound friend not confidently found in the visible friends list this cycle. {scoreSummary}",
                new { bound = true, joined = false, fingerprintScores = scoreSummary });
        }

        if (selection.Status == FollowFingerprintSelectionStatus.Ambiguous)
        {
            MarkCommandCheckpoint($"FollowAutoCheckAsync: ambiguous bound friend match; {scoreSummary}");
            return CommandResult.Success(
                $"Bound friend fingerprint was ambiguous; not clicking a friend row this cycle. {scoreSummary}",
                new { bound = true, joined = false, fingerprintScores = scoreSummary });
        }

        var matchedRow = selection.Match.Row;
        MarkCommandCheckpoint($"FollowAutoCheckAsync: matched bound friend at row {matchedRow}; {scoreSummary}");
        ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);
        var entry = await ClickFriendJoinOptionUntilEnteredGameAsync(
            input,
            matchedRow,
            "follow-auto",
            cancellationToken,
            () => ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken));
        if (!entry.Entered)
        {
            if (entry.FailureResult == GameEntryWaitResult.CurrentCharacterCannotJoin)
            {
                return CurrentCharacterCannotJoinFollowResult(entry.DialogDismissed);
            }

            if (entry.FailureResult == GameEntryWaitResult.GameIsFull)
            {
                return GameFullFollowResult(entry.DialogDismissed);
            }

            return CommandResult.Failure(
                $"Found the bound friend at row {matchedRow} and clicked Join Game, but the client did not enter the game within {Math.Max(_config.Ui.GameEntryStartTimeoutSeconds, 1)}s. {entry.Message} {scoreSummary}",
                await CollectStatusAsync(cancellationToken));
        }

        ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);

        // ClickFriendJoinOptionUntilEnteredGameAsync already confirmed entry through
        // WaitForGameEntryAsync, which samples the strict HUD globes FIRST and only accepts a
        // broad in-game frame after a grace period. Re-running a second, globes-only strict gate
        // here just re-litigates that same decision with no fallback: when entry was legitimately
        // confirmed via the broad frame (a dark load area, or the HUD
        // difference), the gate fails even though the client is in and playing - and the old
        // failure return then called CollectStatusAsync, which stalled for the entire game
        // (2m35s captured in watch-follow-auto-20260714-182232.log, the bot in-game the whole
        // time, only unblocked when the leader left). Trust the entry the join flow already
        // confirmed; keep the strict check only as a brief best-effort signal for the log, never
        // as a gate and never on the heavy CollectStatusAsync path.
        var strictlyConfirmed = await WaitForStrictFollowAutoEntryAsync(input, cancellationToken);
        MarkLobbyOrGameInteraction("Joined bound friend's game via follow-auto.");
        return CommandResult.Success(
            strictlyConfirmed
                ? "Joined the bound friend's game."
                : "Joined the bound friend's game (entry confirmed by the join flow; strict HUD globes were not separately confirmed).",
            new { bound = true, joined = true, strictlyConfirmed, fingerprintScores = scoreSummary });
    }

    internal static CommandResult CurrentCharacterCannotJoinFollowResult(bool dismissed)
    {
        var action = dismissed
            ? "Clicked Cancel; follow-auto will keep retrying the bound friend's game."
            : "Cancel could not be visually confirmed; follow-auto will try to dismiss the dialog and join again next cycle.";
        return CommandResult.Success(
            $"D2R reports: \"You cannot join the game with your current character.\" This can indicate a difficulty, level, capacity, or other game restriction. {action}",
            new
            {
                bound = true,
                joined = false,
                joinBlocked = true,
                joinBlockReason = "currentCharacterCannotJoin",
                dialogDismissed = dismissed
            });
    }

    internal static CommandResult GameFullFollowResult(bool dismissed)
    {
        var action = dismissed
            ? "Clicked OK and stayed warm at the lobby; the host decides whether to retry or park this client until the fleet's next game."
            : "OK was clicked but dismissal could not be visually confirmed; the leftover-modal cleanup on the next cycle will clear it.";
        return CommandResult.Success(
            $"D2R reports: \"Game is full.\" {action}",
            new
            {
                bound = true,
                joined = false,
                joinBlocked = true,
                joinBlockReason = "gameIsFull",
                dialogDismissed = dismissed
            });
    }

    internal static bool CanAutoClickFollowFingerprint(FriendFingerprint template)
    {
        return template.GridColumns >= FollowFingerprintMinAutoClickGridColumns
            && template.GridRows >= FollowFingerprintMinAutoClickGridRows;
    }

    internal static bool IsUsableFollowFingerprintMatchForTests(FriendFingerprintComparison comparison) =>
        IsUsableFollowFingerprintMatch(comparison);

    // A bound friend does not stay on the row it was captured from - the list re-sorts whenever
    // anyone goes online or offline, which is the entire reason this is a fingerprint rather than
    // a stored row number. But D2R's real row pitch is not exactly the configured ratio, so the
    // band lands on a name 1-3px differently depending on which row that name currently occupies.
    // With the band sitting tightly on the name text, one pixel of that is enough to turn a
    // perfect match into a miss: measured against two real captures, the bound friend scored 95.4
    // where it sat (over the 90 gate, reported as "not confidently found") and 0.0 one pixel up.
    //
    // So each row is captured with extra sample rows above and below at the SAME vertical pitch,
    // and the template is slid through them to find its best alignment. The search is deliberately
    // narrow: it exists to absorb per-row banding error, not to hunt for the name anywhere on
    // screen, and a wider window starts letting unrelated rows drift into a false match.
    internal const int FollowFingerprintVerticalSlackRows = 3;

    internal static FriendFingerprintComparison CompareFollowFingerprintAcrossAlignments(
        FriendFingerprint template,
        byte[] extendedSamples,
        int columns,
        int templateRows)
    {
        var stride = columns * 3;
        var capturedRows = extendedSamples.Length / stride;
        var best = FriendFingerprintComparison.NotComparable;
        for (var offset = 0; offset + templateRows <= capturedRows; offset++)
        {
            var window = new byte[templateRows * stride];
            Array.Copy(extendedSamples, offset * stride, window, 0, window.Length);
            var comparison = FriendFingerprint.Compare(
                template, new FriendFingerprint(columns, templateRows, window));
            if (comparison.Comparable
                && (!best.Comparable
                    || comparison.SignalAverageDifference < best.SignalAverageDifference))
            {
                best = comparison;
            }
        }

        return best;
    }

    // Same band, widened vertically by the slack on both sides so the pitch is preserved: the
    // template's own sample rows still land exactly one captured row apart.
    internal static (double HeightRatio, int GridRows) GetFollowFingerprintSearchBand(
        FriendRowFingerprintRegion region)
    {
        var searchRows = region.GridRows + (2 * FollowFingerprintVerticalSlackRows);
        return (region.HeightRatio * searchRows / region.GridRows, searchRows);
    }

    private static bool IsUsableFollowFingerprintMatch(FriendFingerprintComparison comparison)
    {
        return comparison.Comparable
            && comparison.SignalPixels >= FollowFingerprintMinSignalPixels
            && comparison.AverageDifference <= FollowFingerprintMaxAverageDifference
            && comparison.SignalAverageDifference <= FollowFingerprintMaxSignalAverageDifference;
    }

    internal static FollowFingerprintSelection SelectFollowFingerprintMatch(IEnumerable<FriendRowFingerprintMatch> matches)
    {
        var usableMatches = matches
            .Where(match => IsUsableFollowFingerprintMatch(match.Comparison))
            .OrderBy(match => match.Comparison.SignalAverageDifference)
            .ThenBy(match => match.Comparison.AverageDifference)
            .ToList();

        var bestMatch = usableMatches.FirstOrDefault();
        if (bestMatch is null)
        {
            return new FollowFingerprintSelection(FollowFingerprintSelectionStatus.NoUsableMatch, Match: null);
        }

        var secondMatch = usableMatches.Skip(1).FirstOrDefault();
        if (secondMatch is not null
            && secondMatch.Comparison.SignalAverageDifference <= bestMatch.Comparison.SignalAverageDifference + FollowFingerprintMinSignalSeparation)
        {
            return new FollowFingerprintSelection(FollowFingerprintSelectionStatus.Ambiguous, bestMatch);
        }

        return new FollowFingerprintSelection(FollowFingerprintSelectionStatus.Selected, bestMatch);
    }

    internal static int GetFollowFingerprintMaxScanRows(D2RUiAutomationConfig ui)
    {
        var configuredMaxRows = ui.FriendRowFingerprintMaxScanRows > 0
            ? ui.FriendRowFingerprintMaxScanRows
            : new D2RUiAutomationConfig().FriendRowFingerprintMaxScanRows;
        return Math.Clamp(configuredMaxRows, 1, FollowFingerprintMaxVisibleFriendRows);
    }

    internal static byte[]? TryCaptureFriendFingerprintSamples(Func<byte[]> capture, int timeoutMs)
    {
        return TryRunBounded<byte[]?>(capture, timeoutMs, fallback: null);
    }

    internal static bool ShouldReselectFriendGameBeforeRetry(GameEntryWaitResult waitResult)
    {
        return waitResult is GameEntryWaitResult.ConnectionInterrupted
            or GameEntryWaitResult.ErrorDialog
            or GameEntryWaitResult.ReturnedToMenu
            or GameEntryWaitResult.ReturnedToCharacterScreen
            or GameEntryWaitResult.OfflineCharacterScreen;
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
        CancellationToken cancellationToken,
        Action? stopCheck = null)
    {
        var joinGameTab = GetUiPoint(D2RUiCoordinateTarget.JoinGameTab);

        async Task<bool> SelectFriendGameAsync()
        {
            stopCheck?.Invoke();
            var friends = await EnsureFriendsListVisibleAsync(input, $"{context}-entry-retry", cancellationToken);
            stopCheck?.Invoke();
            if (friends is not null)
            {
                return false;
            }

            var friendJoinPoint = GetFriendContextJoinGamePoint(friendRow);
            if (!IsFriendContextJoinPointInLeftPane(friendJoinPoint))
            {
                MarkCommandCheckpoint($"ClickFriendJoinOptionUntilEnteredGameAsync({context}): friend context Join Game point was outside the Friends pane at row {friendRow}");
                return false;
            }

            // Sample the target BEFORE the right-click so the click below can be proven to land on
            // a context menu rather than on whatever the Friends pane happens to draw there.
            var beforeRightClick = TryRunBounded<ScreenRegionStats?>(
                () => SampleFriendContextMenuRegion(input, friendJoinPoint),
                InGameHudSampleBoundMs,
                fallback: null);

            ClickD2R(input, GetFriendRowPoint(friendRow), MouseButton.Right);
            await DelayStepAsync(cancellationToken);
            stopCheck?.Invoke();

            var afterRightClick = TryRunBounded<ScreenRegionStats?>(
                () => SampleFriendContextMenuRegion(input, friendJoinPoint),
                InGameHudSampleBoundMs,
                fallback: null);

            // The context menu is an overlay: if it opened, the pixels under the Join Game point
            // changed. If they did not, the right-click did not produce a menu - the friend row
            // was empty, the click missed, or the menu opened somewhere this offset does not
            // predict - and clicking anyway is a blind click into the Friends pane. At the default
            // geometry, row 7's Join Game point is (0.278, 0.638), which lands inside the Add
            // Friend button; that is where the stray "Enter e-mail address or BattleTag" modal
            // came from. A modal like that blocks every later click, so the cost of guessing wrong
            // here is a client that is stuck until someone dismisses it by hand.
            if (!FriendContextMenuProbe.MenuAppeared(beforeRightClick, afterRightClick))
            {
                MarkCommandCheckpoint(
                    $"ClickFriendJoinOptionUntilEnteredGameAsync({context}): no context menu appeared under the "
                        + $"Join Game point after right-clicking friend row {friendRow}; skipping the click rather "
                        + "than clicking blind into the Friends pane");
                return false;
            }

            ClickD2R(input, friendJoinPoint);
            await DelayFastMenuAsync(cancellationToken);
            stopCheck?.Invoke();
            return true;
        }

        if (!await SelectFriendGameAsync())
        {
            return new GameEntryAttemptResult(false, DialogRetries: 0, ConnectionRetries: 0, "The friend Join Game option could not be selected.");
        }

        if (!IsAnyLobbyEntryMenuVisible(input))
        {
            var entry = await WaitForGameEntryAsync(input, cancellationToken);
            if (entry == GameEntryWaitResult.CurrentCharacterCannotJoin)
            {
                return await BuildCurrentCharacterCannotJoinAttemptResultAsync(
                    input,
                    dialogRetries: 0,
                    connectionRetries: 0,
                    cancellationToken);
            }

            if (entry == GameEntryWaitResult.GameIsFull)
            {
                return await BuildGameFullAttemptResultAsync(
                    input,
                    dialogRetries: 0,
                    connectionRetries: 0,
                    cancellationToken);
            }

            return entry == GameEntryWaitResult.EnteredGame
                ? new GameEntryAttemptResult(true, DialogRetries: 0, ConnectionRetries: 0, "Entered game.")
                : new GameEntryAttemptResult(
                    false,
                    DialogRetries: 0,
                    ConnectionRetries: 0,
                    FormatGameEntryWaitFailure(entry),
                    FailureResult: entry);
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(_config.Ui.GameEntryStartTimeoutSeconds, 1));
        // Every retry below renews `deadline`. The budget is what stops those renewals from
        // running past the agent-side command timeout, which would replace this method's
        // diagnosis with a bare "exceeded agent-side timeout" the host cannot act on.
        var budget = new FriendJoinRetryBudget(DateTimeOffset.UtcNow);
        var deadline = budget.ClampDeadline(DateTimeOffset.UtcNow + timeout);
        var dialogRetries = 0;
        var connectionRetries = 0;

        GameEntryAttemptResult BudgetExhausted(string exhaustedReason)
        {
            MarkCommandCheckpoint(
                $"ClickFriendJoinOptionUntilEnteredGameAsync({context}): retry budget exhausted - {exhaustedReason}");
            return new GameEntryAttemptResult(
                false,
                dialogRetries,
                connectionRetries,
                $"Gave up on this follow cycle: {exhaustedReason}. The client stays at the lobby and the next "
                    + "cycle retries from a clean start.");
        }

        async Task<bool> SelectAndSubmitFriendGameAsync(string reason, bool resetDeadline = false)
        {
            stopCheck?.Invoke();
            MarkCommandCheckpoint($"ClickFriendJoinOptionUntilEnteredGameAsync({context}): {reason}: selecting friend context Join Game");
            if (!await SelectFriendGameAsync())
            {
                return false;
            }

            if (resetDeadline)
            {
                deadline = budget.ClampDeadline(DateTimeOffset.UtcNow + timeout);
            }

            return true;
        }

        if (!await SelectAndSubmitFriendGameAsync("initial"))
        {
            return new GameEntryAttemptResult(false, DialogRetries: 0, ConnectionRetries: 0, "The selected friend game could not be submitted.");
        }

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stopCheck?.Invoke();
            var broadHudFrameAcceptAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Clamp(_config.Ui.GameLoadSeconds, 3, 8));
            var waitResult = await WaitForGameEntryAsync(input, joinGameTab, cancellationToken, broadHudFrameAcceptAt);
            stopCheck?.Invoke();
            if (waitResult == GameEntryWaitResult.EnteredGame)
            {
                return new GameEntryAttemptResult(true, dialogRetries, connectionRetries, "Entered game.");
            }

            if (waitResult == GameEntryWaitResult.CurrentCharacterCannotJoin
                || IsCannotJoinCurrentCharacterDialogOpen(input))
            {
                MarkCommandCheckpoint($"ClickFriendJoinOptionUntilEnteredGameAsync({context}): current character cannot join; dismissing for next follow cycle");
                stopCheck?.Invoke();
                return await BuildCurrentCharacterCannotJoinAttemptResultAsync(
                    input,
                    dialogRetries,
                    connectionRetries,
                    cancellationToken);
            }

            // Never reselect-and-retry a full game inside this command the way stale error
            // dialogs are retried: the host decides whether another attempt is allowed. An
            // in-command retry loop would grab a freed slot (a dropped human's) instantly.
            if (waitResult == GameEntryWaitResult.GameIsFull)
            {
                MarkCommandCheckpoint($"ClickFriendJoinOptionUntilEnteredGameAsync({context}): game is full; dismissing and reporting to the host");
                stopCheck?.Invoke();
                return await BuildGameFullAttemptResultAsync(
                    input,
                    dialogRetries,
                    connectionRetries,
                    cancellationToken);
            }

            if (!ShouldReselectFriendGameBeforeRetry(waitResult))
            {
                break;
            }

            if (waitResult == GameEntryWaitResult.ErrorDialog || IsGameEntryErrorDialogOpen(input))
            {
                // A full-game modal can pop between the wait result and this live re-probe
                // (waitResult reads ReturnedToMenu/TimedOut). Disambiguate before the generic
                // dismiss-and-reselect retry, which must never run against a full game.
                if (IsGameFullDialogOpen(input))
                {
                    MarkCommandCheckpoint($"ClickFriendJoinOptionUntilEnteredGameAsync({context}): late game-is-full dialog; dismissing and reporting to the host");
                    stopCheck?.Invoke();
                    return await BuildGameFullAttemptResultAsync(
                        input,
                        dialogRetries,
                        connectionRetries,
                        cancellationToken);
                }

                dialogRetries++;
                if (!budget.TryConsume(FriendJoinRetryReason.ErrorDialog, DateTimeOffset.UtcNow, out var dialogExhausted))
                {
                    // Dismiss before returning: leaving the modal up would block the next cycle's
                    // menu_ready and turn one exhausted budget into a permanently stuck client.
                    stopCheck?.Invoke();
                    await DismissGameEntryErrorDialogAsync(input, cancellationToken);
                    return BudgetExhausted(dialogExhausted);
                }

                MarkCommandCheckpoint($"ClickFriendJoinOptionUntilEnteredGameAsync({context}): stale/error dialog, reselecting friend game");
                stopCheck?.Invoke();
                if (!await DismissGameEntryErrorDialogAsync(input, cancellationToken)
                    || !await SelectAndSubmitFriendGameAsync("retry after game-entry dialog", resetDeadline: true))
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "A game-entry error dialog appeared, but the friend game could not be reselected.");
                }

                continue;
            }

            if (waitResult == GameEntryWaitResult.ConnectionInterrupted)
            {
                connectionRetries++;
                if (!budget.TryConsume(FriendJoinRetryReason.ConnectionInterrupted, DateTimeOffset.UtcNow, out var connectionExhausted))
                {
                    return BudgetExhausted(connectionExhausted);
                }

                MarkCommandCheckpoint($"ClickFriendJoinOptionUntilEnteredGameAsync({context}): connection interrupted, reselecting friend game");
                stopCheck?.Invoke();
                if (!await WaitForMenuAfterConnectionInterruptedAsync(input, joinGameTab, cancellationToken)
                    || !await SelectAndSubmitFriendGameAsync("retry after connection interruption", resetDeadline: true))
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "Connection was interrupted, but the friend game could not be reselected.");
                }

                continue;
            }

            if (waitResult == GameEntryWaitResult.OfflineCharacterScreen)
            {
                if (!budget.TryConsume(FriendJoinRetryReason.OfflineCharacterScreen, DateTimeOffset.UtcNow, out var offlineExhausted))
                {
                    return BudgetExhausted(offlineExhausted);
                }

                stopCheck?.Invoke();
                if (!await EnsureOnlineCharacterScreenAsync(input, cancellationToken))
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned to the offline character screen, and the friend game could not be reselected after clicking Online.");
                }

                stopCheck?.Invoke();
                if (!await ClickLobbyDirectAsync(input, cancellationToken, guardAgainstInGame: true))
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned to the offline character screen, and the friend game could not be reselected after clicking Online.");
                }

                if (!await SelectAndSubmitFriendGameAsync("retry after offline character screen", resetDeadline: true))
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned to the offline character screen, and the friend game could not be reselected after clicking Online.");
                }

                continue;
            }

            if (waitResult == GameEntryWaitResult.ReturnedToCharacterScreen)
            {
                if (!budget.TryConsume(FriendJoinRetryReason.ReturnedToCharacterScreen, DateTimeOffset.UtcNow, out var characterExhausted))
                {
                    return BudgetExhausted(characterExhausted);
                }

                stopCheck?.Invoke();
                if (!await ClickLobbyDirectAsync(input, cancellationToken, guardAgainstInGame: true))
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned to character select, but the friend game could not be reselected.");
                }

                if (!await SelectAndSubmitFriendGameAsync("retry after character screen return", resetDeadline: true))
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned to character select, but the friend game could not be reselected.");
                }

                continue;
            }

            if (waitResult == GameEntryWaitResult.ReturnedToMenu)
            {
                if (!budget.TryConsume(FriendJoinRetryReason.ReturnedToMenu, DateTimeOffset.UtcNow, out var menuExhausted))
                {
                    return BudgetExhausted(menuExhausted);
                }

                stopCheck?.Invoke();
                if (!await SelectAndSubmitFriendGameAsync("retry after menu return", resetDeadline: true))
                {
                    return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, "The client returned from game entry, but the friend game could not be reselected.");
                }
            }
        }

        return new GameEntryAttemptResult(false, dialogRetries, connectionRetries, FormatEntryTimeoutMessage(input, joinGameTab, dialogRetries, connectionRetries));
    }

    private async Task<GameEntryAttemptResult> BuildGameFullAttemptResultAsync(
        WindowsInput input,
        int dialogRetries,
        int connectionRetries,
        CancellationToken cancellationToken)
    {
        var dismissed = await DismissGameEntryErrorDialogAsync(input, cancellationToken);
        return new GameEntryAttemptResult(
            Entered: false,
            dialogRetries,
            connectionRetries,
            dismissed
                ? "D2R reported that the game is full; clicked OK and stayed at the lobby."
                : "D2R reported that the game is full; OK was clicked but dismissal could not be visually confirmed.",
            FailureResult: GameEntryWaitResult.GameIsFull,
            DialogDismissed: dismissed);
    }

    private async Task<GameEntryAttemptResult> BuildCurrentCharacterCannotJoinAttemptResultAsync(
        WindowsInput input,
        int dialogRetries,
        int connectionRetries,
        CancellationToken cancellationToken)
    {
        var dismissed = await DismissCannotJoinCurrentCharacterDialogAsync(input, cancellationToken);
        return new GameEntryAttemptResult(
            Entered: false,
            dialogRetries,
            connectionRetries,
            dismissed
                ? "D2R reported that the game cannot be joined with the current character; clicked Cancel so a later follow cycle can retry."
                : "D2R reported that the game cannot be joined with the current character; Cancel was clicked but dismissal could not be visually confirmed.",
            FailureResult: GameEntryWaitResult.CurrentCharacterCannotJoin,
            DialogDismissed: dismissed);
    }

    private async Task<CommandResult?> EnsureFriendsListVisibleAsync(
        WindowsInput input,
        string context,
        CancellationToken cancellationToken)
    {
        var openedDrawer = false;
        var drawerOpen = TryDetectFriendsDrawerOpen(input);
        if (drawerOpen == false)
        {
            MarkCommandCheckpoint($"EnsureFriendsListVisibleAsync({context}): opening friends drawer");
            ClickD2RStatefulToggle(input, GetUiPoint(D2RUiCoordinateTarget.LobbyPartyIcon));
            openedDrawer = true;
            await PollForConditionAsync(() => TryDetectFriendsDrawerOpen(input) == true, cancellationToken);
        }
        else if (drawerOpen == true)
        {
            MarkCommandCheckpoint($"EnsureFriendsListVisibleAsync({context}): friends drawer already open");
        }
        else
        {
            MarkCommandCheckpoint($"EnsureFriendsListVisibleAsync({context}): friends drawer state unknown, skipping toggle");
        }

        if (TryDetectFriendsDrawerOpen(input) != true)
        {
            return CommandResult.Failure(
                $"Could not open the friends drawer before {context}.",
                await CollectStatusAsync(cancellationToken));
        }

        var expanded = GetFriendsListExpandedEvidence(input);
        var accordionAction = ChooseFriendsAccordionAction(
            openedDrawer,
            expanded.IsExpanded,
            ShouldAvoidFriendsAccordionToggle(expanded.IsExpanded, expanded.IsReliable));
        if (accordionAction == FriendsAccordionAction.ExpandAfterOpeningDrawer)
        {
            MarkCommandCheckpoint($"EnsureFriendsListVisibleAsync({context}): expanding Friends accordion after opening drawer");
            ClickD2RStatefulToggle(input, GetUiPoint(D2RUiCoordinateTarget.FriendsAccordionHeader));
            await PollForConditionAsync(() => GetFriendsListExpandedEvidence(input).IsExpanded, cancellationToken);
        }
        else if (accordionAction == FriendsAccordionAction.ExpandCollapsed)
        {
            MarkCommandCheckpoint($"EnsureFriendsListVisibleAsync({context}): expanding Friends accordion");
            ClickD2RStatefulToggle(input, GetUiPoint(D2RUiCoordinateTarget.FriendsAccordionHeader));
            await PollForConditionAsync(() => GetFriendsListExpandedEvidence(input).IsExpanded, cancellationToken);
        }
        else
        {
            MarkCommandCheckpoint($"EnsureFriendsListVisibleAsync({context}): Friends accordion already expanded");
        }

        MarkCommandCheckpoint(FormatFriendsExpansionVerificationCheckpoint(context, accordionAction));
        expanded = GetFriendsListExpandedEvidence(input);
        if (ShouldRecoverFriendsAccordionAfterVerification(
                accordionAction,
                expanded.IsExpanded,
                expanded.HasRowEvidence,
                expanded.IsReliable))
        {
            MarkCommandCheckpoint($"EnsureFriendsListVisibleAsync({context}): expanding Friends accordion after verification found it collapsed");
            ClickD2RStatefulToggle(input, GetUiPoint(D2RUiCoordinateTarget.FriendsAccordionHeader));
            await DelayLongAsync(cancellationToken);
            expanded = GetFriendsListExpandedEvidence(input);
        }

        if (!expanded.IsExpanded)
        {
            return CommandResult.Failure(
                $"Could not expand the Friends list before {context}. Friends list evidence: {expanded.Summary}.",
                await CollectStatusAsync(cancellationToken));
        }

        return null;
    }

    internal static FriendsAccordionAction ChooseFriendsAccordionAction(
        bool openedDrawer,
        bool expandedEvidence,
        bool avoidToggleEvidence)
    {
        if (expandedEvidence)
        {
            return FriendsAccordionAction.SkipExpanded;
        }

        if (avoidToggleEvidence)
        {
            return FriendsAccordionAction.VerifyAfterOpeningDrawer;
        }

        if (openedDrawer)
        {
            return FriendsAccordionAction.ExpandAfterOpeningDrawer;
        }

        return FriendsAccordionAction.ExpandCollapsed;
    }

    internal static bool ShouldVerifyFriendsExpansionAfterAction(FriendsAccordionAction action)
    {
        return true;
    }

    internal static bool ShouldAvoidFriendsAccordionToggle(bool expandedEvidence, bool reliableEvidence)
    {
        return !expandedEvidence && !reliableEvidence;
    }

    internal static bool ShouldRecoverFriendsAccordionAfterVerification(
        FriendsAccordionAction action,
        bool expandedEvidence,
        bool rowEvidence,
        bool reliableEvidence)
    {
        return action == FriendsAccordionAction.SkipExpanded
            && !expandedEvidence
            && !rowEvidence
            && reliableEvidence;
    }

    internal static string FormatFriendsExpansionVerificationCheckpoint(string context, FriendsAccordionAction action)
    {
        return action switch
        {
            FriendsAccordionAction.SkipExpanded => $"EnsureFriendsListVisibleAsync({context}): verifying already-expanded Friends rows",
            FriendsAccordionAction.VerifyAfterOpeningDrawer => $"EnsureFriendsListVisibleAsync({context}): verifying Friends rows after opening drawer",
            FriendsAccordionAction.ExpandAfterOpeningDrawer => $"EnsureFriendsListVisibleAsync({context}): verifying Friends rows after accordion click",
            _ => $"EnsureFriendsListVisibleAsync({context}): verifying Friends rows after accordion click"
        };
    }

    private bool? TryDetectFriendsDrawerOpen(WindowsInput input)
    {
        // null = couldn't sample (timeout or slot exhaustion) — callers must not toggle on null
        return TryRunBounded<bool?>(() =>
        {
            var stats = input.SampleRegion(
                GetUiPoint(D2RUiCoordinateTarget.FriendsAccordionHeader),
                widthRatio: 0.200,
                heightRatio: 0.022,
                sampleGrid: MenuSampleGrid);
            return D2RScreenClassifier.IsFriendsDrawerHeaderVisible(stats);
        }, EntryLoopCheckBoundMs, fallback: null);
    }

    private (bool IsExpanded, bool HasRowEvidence, bool IsReliable, string Summary) GetFriendsListExpandedEvidence(WindowsInput input)
    {
        return TryRunBounded(() =>
        {
            var visibleRows = 0;
            var markerRows = 0;
            var summary = new StringBuilder();
            // Expansion detection only needs a few rows to confirm the accordion is open -
            // GetFollowFingerprintMaxScanRows (up to 8) is for fingerprint matching, and
            // scanning 8 rows × 2 SampleRegion calls each = 16 GDI calls inside one 1500ms
            // bounded call. Under DWM load even modest per-call delays push that past the
            // timeout, abandoning the thread and consuming a BoundedCallSlots slot every time.
            // Three rows (6 GDI calls, the original pre-fingerprint-expansion limit) is
            // sufficient to hit visibleRows>=2 or markerRows>=3 on an expanded accordion.
            var maxRows = Math.Min(GetFollowFingerprintMaxScanRows(_config.Ui), 3);

            for (var row = 1; row <= maxRows; row++)
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
                if (markerVisible)
                {
                    markerRows++;
                }

                if (nameVisible && markerVisible)
                {
                    visibleRows++;
                }

                if (summary.Length > 0)
                {
                    summary.Append(' ');
                }

                summary
                    .Append(FormatCheck($"r{row}txt", nameVisible, nameStats))
                    .Append('/')
                    .Append(FormatCheck($"r{row}mark", markerVisible, markerStats));
            }

            if (summary.Length > 0)
            {
                summary.Append(' ');
            }

            summary
                .Append("visibleRows=").Append(visibleRows)
                .Append(",markerRows=").Append(markerRows);
            return (
                IsFriendsListExpandedByEvidence(visibleRows, markerRows),
                HasFriendsListRowEvidence(visibleRows, markerRows),
                true,
                summary.ToString());
        }, EntryLoopCheckBoundMs, (false, false, false, "timeout"));
    }

    internal static bool IsFriendsListExpandedByEvidence(int visibleRows, int markerRows)
    {
        return visibleRows >= 2
            || (visibleRows >= 1 && markerRows >= 2)
            || markerRows >= 3;
    }

    internal static bool HasFriendsListRowEvidence(int visibleRows, int markerRows)
    {
        return visibleRows >= 1 || markerRows >= 2;
    }

    private AgentCommon.UiPoint GetFriendRowMarkerPoint(int row)
    {
        var rowPoint = D2RUiCoordinateCatalog.GetFriendRowPoint(_config.Ui, row);
        return new AgentCommon.UiPoint(Math.Clamp(rowPoint.X - 0.090, 0, 1), rowPoint.Y);
    }

    private async Task<CommandResult> SaveAndExitAsync(
        long? followAutoRunId,
        CancellationToken cancellationToken)
    {
        var input = FocusD2R();
        var lastPostExitState = "post-exit menu state was not visually confirmed";
        for (var attempt = 1; attempt <= SaveExitMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkCommandCheckpoint($"SaveAndExitAsync: attempt {attempt}/{SaveExitMaxAttempts}");
            SendOneEscape(input);
            await DelayStepAsync(cancellationToken);
            ClickD2R(input, GetUiPoint(D2RUiCoordinateTarget.SaveAndExitButton));
            var (confirmed, postExitState) = await WaitForPostSaveExitMenuAsync(
                input,
                followAutoRunId,
                cancellationToken);
            lastPostExitState = postExitState;
            if (confirmed)
            {
                var attemptNote = attempt > 1 ? $" on attempt {attempt}" : "";
                return CommandResult.Success(
                    $"Save and Exit flow completed{attemptNote}; {postExitState}.",
                    await CollectStatusAsync(cancellationToken));
            }

            // Only a visible in-game HUD justifies another Escape+click round. Anything else
            // (loading screen after a slow exit, disconnect prompt) gains nothing from more
            // Escape presses, so keep the old unconfirmed-success behavior for those.
            if (!IsInGameReady(input))
            {
                MarkD2RActivityUnknown("Save and Exit completed, but the post-exit menu state was not detected.");
                return CommandResult.Success(
                    $"Save and Exit flow completed; {postExitState}.",
                    await CollectStatusAsync(cancellationToken));
            }

            // Retrying blind is safe even when the previous Escape actually opened the menu
            // and only the click missed: the next Escape closes it, that click lands in the
            // world, and the attempt after that opens and clicks cleanly - it converges
            // within the attempt budget either way.
            MarkLobbyOrGameInteraction($"Save and Exit attempt {attempt} left D2R still in a game; retrying.");
        }

        MarkLobbyOrGameInteraction("Save and Exit failed; the in-game HUD is still visible.");
        return CommandResult.Failure(
            $"Save and Exit did not leave the game after {SaveExitMaxAttempts} attempts; the in-game HUD is still visible ({lastPostExitState}).{FormatInputDiagnosticsSuffix()}",
            await CollectStatusAsync(cancellationToken));
    }

    private async Task<CommandResult> SaveAndExitFromOpenModernPauseMenuAsync(
        WindowsInput input,
        long? followAutoRunId,
        CancellationToken cancellationToken)
    {
        var lastPostExitState = "post-exit menu state was not visually confirmed";
        for (var attempt = 1; attempt <= SaveExitMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Never retry this fixed-coordinate click merely because a generic HUD sample
            // still says "in game." Once the pause overlay is gone, the same point is the live
            // world and could move a Hardcore character. Every attempt must freshly re-prove
            // the distinctive three-button overlay first.
            var pauseMenuMatch = TryRunBounded<InGameHudMatchKind?>(
                () => DetectBestInGameHudMatch(input),
                InGameSafetyCheckBoundMs,
                fallback: null);
            if (pauseMenuMatch != InGameHudMatchKind.SaveAndExitMenu)
            {
                MarkLobbyOrGameInteraction(
                    "Direct Save and Exit retry was suppressed because the pause menu was no longer confirmed.");
                return CommandResult.Failure(
                    $"The Save and Exit menu was not freshly confirmed before attempt {attempt}; no fixed-coordinate click was sent.{FormatInputDiagnosticsSuffix()}",
                    await CollectStatusAsync(cancellationToken));
            }

            MarkCommandCheckpoint(
                $"SaveAndExitFromOpenModernPauseMenuAsync: direct attempt {attempt}/{SaveExitMaxAttempts}");
            var saveAndExitPoint = GetUiPoint(D2RUiCoordinateTarget.SaveAndExitButton);
            if (!input.SendWindowClick(saveAndExitPoint, GetD2RProcessNames(), MouseButton.Left))
            {
                // SendWindowClick returning false means it found no usable D2R HWND, so no
                // window-targeted click was delivered. Use one visible click as the fallback;
                // never fire both successful routes at this state-changing button.
                input.VisibleClickOnce(saveAndExitPoint, MouseButton.Left);
            }

            var (confirmed, postExitState) = await WaitForPostSaveExitMenuAsync(
                input,
                followAutoRunId,
                cancellationToken);
            lastPostExitState = postExitState;
            if (confirmed)
            {
                var attemptNote = attempt > 1 ? $" on attempt {attempt}" : "";
                return CommandResult.Success(
                    $"Open-menu Save and Exit flow completed{attemptNote}; {postExitState}.",
                    await CollectStatusAsync(cancellationToken));
            }

            if (!IsInGameReady(input))
            {
                MarkD2RActivityUnknown(
                    "Open-menu Save and Exit completed, but the post-exit menu state was not detected.");
                return CommandResult.Success(
                    $"Open-menu Save and Exit flow completed; {postExitState}.",
                    await CollectStatusAsync(cancellationToken));
            }

            MarkLobbyOrGameInteraction(
                $"Direct Save and Exit attempt {attempt} left the confirmed modern pause menu visible; retrying.");
        }

        MarkLobbyOrGameInteraction("Direct Save and Exit failed; the modern pause menu is still visible.");
        return CommandResult.Failure(
            $"Direct Save and Exit did not leave the game after {SaveExitMaxAttempts} attempts ({lastPostExitState}).{FormatInputDiagnosticsSuffix()}",
            await CollectStatusAsync(cancellationToken));
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

        if (ready.LastState is ReadyScreenState.LobbyOrGame
            or ReadyScreenState.InGame
            or ReadyScreenState.CannotJoinCurrentCharacterDialog)
        {
            MarkLobbyOrGameInteraction($"Ready loop detected {ready.LastState} instead of the character screen.");
            return CommandResult.Failure(
                $"D2R reached the {ready.LastState} state instead of the character screen; this command needs the character screen specifically.",
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
            _expectedLobbyAfterSaveExit = null;
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
            _expectedLobbyAfterSaveExit = null;
            _lastActivityReason = reason;
        }
    }

    private void MarkExpectedLobbyAfterSaveExit(long? followAutoRunId, string reason)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var processStartedUtc = followAutoRunId is > 0
            ? TryGetD2RProcessStartUtc()
            : null;
        ExpectedLobbyAfterSaveExit? expectation = followAutoRunId is > 0 && processStartedUtc is { } startedUtc
            ? new ExpectedLobbyAfterSaveExit(
                followAutoRunId.Value,
                startedUtc,
                nowUtc + ExpectedLobbyAfterSaveExitWindow)
            : null;

        lock (_activityLock)
        {
            _activityState = D2RActivityState.Unknown;
            _characterScreenIdleSinceUtc = null;
            _lastLobbyOrGameInteractionUtc = null;
            _expectedLobbyAfterSaveExit = expectation;
            _lastActivityReason = expectation is not null
                ? reason
                : "Save and Exit left the game; the specific post-exit menu was not identified.";
        }
    }

    private bool ConsumeExpectedLobbyAfterSaveExit(long? followAutoRunId)
    {
        var processStartedUtc = followAutoRunId is > 0
            ? TryGetD2RProcessStartUtc()
            : null;
        lock (_activityLock)
        {
            var expected = ShouldTrustExpectedLobbyAfterSaveExit(
                _expectedLobbyAfterSaveExit,
                followAutoRunId,
                processStartedUtc,
                _activityState,
                DateTimeOffset.UtcNow);
            _expectedLobbyAfterSaveExit = null;
            return expected;
        }
    }

    private bool HasExpectedLobbyAfterSaveExitCandidate(DateTimeOffset nowUtc)
    {
        lock (_activityLock)
        {
            return _expectedLobbyAfterSaveExit is { } expected
                && expected.ExpiresUtc >= nowUtc
                && _activityState != D2RActivityState.CharacterScreenIdle;
        }
    }

    private bool IsExpectedLobbyAfterSaveExitForCurrentProcess()
    {
        var processStartedUtc = TryGetD2RProcessStartUtc();
        lock (_activityLock)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var expected = ShouldReportExpectedLobbyAfterSaveExit(
                _expectedLobbyAfterSaveExit,
                processStartedUtc,
                _activityState,
                nowUtc);

            // Keep a valid expectation for the follow command to consume. Contradictory process
            // or activity evidence and expiry are terminal; a transient inability to read the
            // process start time is not, so it may try again on the actual follow command.
            if (!expected
                && _expectedLobbyAfterSaveExit is { } stale
                && (stale.ExpiresUtc < nowUtc
                    || _activityState == D2RActivityState.CharacterScreenIdle
                    || (processStartedUtc is { } currentProcessStartedUtc
                        && Math.Abs((stale.ProcessStartedUtc - currentProcessStartedUtc).TotalSeconds) > 1)))
            {
                _expectedLobbyAfterSaveExit = null;
            }

            return expected;
        }
    }

    internal static bool ShouldTrustExpectedLobbyAfterSaveExit(
        ExpectedLobbyAfterSaveExit? expectation,
        long? followAutoRunId,
        DateTimeOffset? processStartedUtc,
        D2RActivityState activityState,
        DateTimeOffset nowUtc)
    {
        return expectation is { } expected
            && followAutoRunId is > 0
            && expected.FollowAutoRunId == followAutoRunId.Value
            && ShouldReportExpectedLobbyAfterSaveExit(
                expectation,
                processStartedUtc,
                activityState,
                nowUtc);
    }

    internal static bool ShouldReportExpectedLobbyAfterSaveExit(
        ExpectedLobbyAfterSaveExit? expectation,
        DateTimeOffset? processStartedUtc,
        D2RActivityState activityState,
        DateTimeOffset nowUtc)
    {
        return expectation is { } expected
            && processStartedUtc is { } currentProcessStartedUtc
            && Math.Abs((expected.ProcessStartedUtc - currentProcessStartedUtc).TotalSeconds) <= 1
            && expected.ExpiresUtc >= nowUtc
            && activityState != D2RActivityState.CharacterScreenIdle;
    }

    internal static bool ShouldRunFollowAutoScreenClassifiers(bool usedExpectedPostSaveExitLobby)
    {
        return !usedExpectedPostSaveExitLobby;
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
            _expectedLobbyAfterSaveExit = null;
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
            _expectedLobbyAfterSaveExit = null;
            _lastObservedD2RStartUtc = null;
            _lastActivityReason = null;
        }

        // Every path through here means the process is gone or was just killed/relaunched -
        // a fresh client always gets a fresh stuck-load-screen grace period.
        _unknownFrameSinceUtc = null;
        _stuckSurroundSinceUtc = null;
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

        ReconcileGammaCalibrationProcessInstance(processStartedUtc.Value);
        lock (_activityLock)
        {
            if (_lastObservedD2RStartUtc is not null
                && Math.Abs((processStartedUtc.Value - _lastObservedD2RStartUtc.Value).TotalSeconds) > 1)
            {
                _activityState = D2RActivityState.Unknown;
                _characterScreenIdleSinceUtc = null;
                _lastLobbyOrGameInteractionUtc = null;
                _expectedLobbyAfterSaveExit = null;
                _lastActivityReason = "D2R process restarted.";
            }

            _lastObservedD2RStartUtc = processStartedUtc;
        }
    }

    /// <summary>
    /// Keeps an unconfirmed gamma sighting from one D2R process from combining with a sighting
    /// after a relaunch. A confirmed settings-reset incident remains latched across a stop/restart
    /// because relaunching cannot repair the file; only replacement or a healthy rendered frame
    /// proves that incident is over.
    /// </summary>
    internal void ReconcileGammaCalibrationProcessInstance(DateTimeOffset processStartedUtc)
    {
        lock (_activityLock)
        {
            var processChanged = _gammaCalibrationProcessStartUtc is { } previousStart
                && Math.Abs((processStartedUtc - previousStart).TotalSeconds) > 1;
            if (processChanged && !IsGammaCalibrationConfirmedLocked())
            {
                ClearGammaCalibrationSightingsLocked();
            }

            _gammaCalibrationProcessStartUtc = processStartedUtc;
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
            // Explicit rather than falling through to "keep the previous activity": a client that
            // crashed out of a game onto this dialog would otherwise keep reporting LobbyOrGame.
            VisibleD2RState.GraphicsDeviceFailure => new ActivitySnapshot(
                D2RActivityState.Unknown,
                null,
                activity.LastLobbyOrGameInteractionUtc,
                "Detected D2R's failed-to-initialize-graphics-device dialog."),
            // Same reasoning as the dialog above: a client that restarted into the first-run gamma
            // screen must not keep reporting the LobbyOrGame it was in before, which would make
            // both the idle watchdog and MenuReadyPolicy treat it as a healthy client.
            VisibleD2RState.GammaCalibration => new ActivitySnapshot(
                D2RActivityState.Unknown,
                null,
                activity.LastLobbyOrGameInteractionUtc,
                "Detected D2R's first-run gamma calibration screen; this client reset its own settings."),
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
            return FallbackProcessOnlyVisibleState();
        }

        return MapObservedFrameToVisibleState(_lastObservedFrame) ?? FallbackProcessOnlyVisibleState();
    }

    /// <summary>
    /// Translates a recorded frame name into the visible state a process-only status should report.
    /// Null means there is no mapping and the caller should fall back to activity-based guessing.
    /// </summary>
    /// <remarks>
    /// Load-bearing, not cosmetic. Every menu command runs under the command gate, so the status a
    /// command attaches to its own result is always the process-only one - this table is what
    /// decides whether a state the detector genuinely saw survives into that payload. The gamma
    /// screen was missing here at first, which silently made the host's automatic settings repair
    /// (whose only trigger is exactly that payload) impossible to fire.
    /// </remarks>
    internal static VisibleD2RState? MapObservedFrameToVisibleState(string? frame) => frame switch
    {
        nameof(ReadyScreenState.DiabloSplash) => VisibleD2RState.DiabloSplash,
        nameof(ReadyScreenState.CharacterMenu) => VisibleD2RState.CharacterScreen,
        nameof(ReadyScreenState.CharacterScreen) => VisibleD2RState.CharacterScreen,
        nameof(ReadyScreenState.OfflineCharacterScreen) => VisibleD2RState.OfflineCharacterScreen,
        nameof(VisibleD2RState.NotRunning) => VisibleD2RState.NotRunning,
        nameof(VisibleD2RState.LobbyOrGame) => VisibleD2RState.LobbyOrGame,
        nameof(VisibleD2RState.InGame) => VisibleD2RState.InGame,
        nameof(VisibleD2RState.GraphicsDeviceFailure) => VisibleD2RState.GraphicsDeviceFailure,
        nameof(VisibleD2RState.GammaCalibration) => VisibleD2RState.GammaCalibration,
        _ => null
    };

    private VisibleD2RState FallbackProcessOnlyVisibleState()
    {
        var interaction = _lastLobbyOrGameInteractionUtc;
        if (interaction is not null && DateTimeOffset.UtcNow - interaction.Value < TimeSpan.FromSeconds(120))
        {
            return VisibleD2RState.LobbyOrGame;
        }

        return VisibleD2RState.Unknown;
    }

    private VisibleD2RState DetectVisibleD2RState(bool d2rRunning)
    {
        if (!OperatingSystem.IsWindows())
        {
            var unsupported = d2rRunning ? VisibleD2RState.Unknown : VisibleD2RState.NotRunning;
            RecordObservedFrame(unsupported.ToString());
            return unsupported;
        }

        // Checked before any pixel sampling: this modal renders over a black screen that every
        // pixel classifier reads as Unknown, which is indistinguishable from a load screen or a
        // degraded capture. Naming it here is what puts it in /d2r status (visible
        // GraphicsDeviceFailure) and what makes MenuReadyPolicy run a ready pass instead of
        // assuming a client that is never coming up on its own.
        if (DetectGraphicsDeviceFailureDialog().Detected)
        {
            RecordObservedFrame(VisibleD2RState.GraphicsDeviceFailure.ToString());
            return VisibleD2RState.GraphicsDeviceFailure;
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
        // Checking strict in-game evidence (HUD globes, IsInGameReadyStrict) first
        // means a real in-game scene with visible globes - the common case - never reaches the
        // lobby check at all. The broader Frame-kind fallback stays AFTER the lobby check,
        // unchanged from v0.2.64, which added that ordering because a filled join/create form
        // could satisfy IsInGameHudFrame's looser thresholds - reordering this block back
        // wholesale would have resurrected that exact bug.
        if (IsInGameReadyStrictBounded(input, fallbackOnTimeout: false))
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

        // Last, because it is the only state here that means "this client is not coming back
        // without a file being replaced" - everything above is a screen the client can leave on
        // its own, so none of them should ever have to wait behind this check.
        if (IsGammaCalibrationScreen(input, windowRelative: false)
            || IsGammaCalibrationScreen(input, windowRelative: true))
        {
            return VisibleD2RState.GammaCalibration;
        }

        RecordClassifierBreakdown(TryRunBounded(() => ComputeVisibleStateClassifierBreakdown(input, MenuSampleGrid, abandonWhenCommandActive: true), ClassifierBreakdownBoundMs, ""));
        return VisibleD2RState.Unknown;
    }

    // Every top-level state check (DiabloSplash, offline, character screen, in-game, lobby)
    // is itself a handful of named pixel-region sub-checks (IsLobbyTabReady,
    // IsLobbyEntryButtonReady, IsCharacterButtonPairReady, ...) that previously only showed
    // up in the raw ScreenRegionStats dumped on a menu_ready timeout - never live, and never
    // for the lobby/in-game checks at all. Recording a compact pass/fail per sub-check
    // whenever the result is Unknown means a live `watch` can show *why* nothing matched
    // instead of just "Unknown", without needing a screenshot first.
    // The classifier breakdown is ~20 sequential GDI region samples feeding only the status line's
    // diagnostic hud F(...) text - it drives no decision (DetectVisibleD2RState returns Unknown
    // whether or not it runs). watch-follow-auto-20260715-133238.log: an already-running detailed
    // status collection that reached this breakdown during a save-exit held GDI for ~15s, and every
    // bounded IsInGameReady sample in WaitForPostSaveExitMenuAsync timed out against it, so the leave
    // sat at its full deadline even though the client had returned to the lobby in ~2s. When the
    // caller opts in (only the live-status path does; the menu_ready diagnostic paths own the command
    // gate themselves and must not bail), abandon the breakdown the instant a UI command takes the
    // gate - the partial diagnostic is worthless next to unblocking that command's own screen reads.
    private void AbandonBreakdownIfCommandActive(bool enabled)
    {
        if (enabled && _commandGate.CurrentCount == 0)
        {
            throw new OperationCanceledException(
                "A UI command needs the display; abandoning the diagnostic classifier breakdown.");
        }
    }

    private string ComputeVisibleStateClassifierBreakdown(WindowsInput input, int sampleGrid, bool abandonWhenCommandActive = false)
    {
        try
        {
            AbandonBreakdownIfCommandActive(abandonWhenCommandActive);
            var logo = SampleD2RRegion(input, new AgentCommon.UiPoint(0.500, 0.290), widthRatio: 0.45, heightRatio: 0.22, windowRelative: false, sampleGrid: sampleGrid);
            var prompt = SampleD2RRegion(input, new AgentCommon.UiPoint(0.500, 0.600), widthRatio: 0.32, heightRatio: 0.055, windowRelative: false, sampleGrid: sampleGrid);
            var splash = IsDiabloSplashScreen(input, sampleGrid);
            var connecting = splash && IsConnectingToBattleNetDialog(input, sampleGrid);
            AbandonBreakdownIfCommandActive(abandonWhenCommandActive);
            var emptyPanel = SampleD2RRegion(input, new AgentCommon.UiPoint(0.895, 0.455), widthRatio: 0.17, heightRatio: 0.66, windowRelative: false, sampleGrid: sampleGrid);
            var offline = IsCharacterScreenOffline(input, sampleGrid: sampleGrid);
            var play = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.CharacterPlayButton), widthRatio: 0.13, heightRatio: 0.055, windowRelative: false, sampleGrid: sampleGrid);
            var charMenuLogo = SampleD2RRegion(input, new AgentCommon.UiPoint(0.105, 0.170), widthRatio: 0.13, heightRatio: 0.16, windowRelative: false, sampleGrid: sampleGrid);
            AbandonBreakdownIfCommandActive(abandonWhenCommandActive);
            var charButtons = IsCharacterButtonPairReady(input, windowRelative: false, sampleGrid);
            var charMenu = IsCharacterMenuReady(input, windowRelative: false, sampleGrid);
            var inGameScreenEvidence = SampleInGameHudEvidence(input, windowRelative: false);
            var inGameWindowEvidence = SampleInGameHudEvidence(input, windowRelative: true);
            var inGameScreen = IsInGameHudEvidenceReady(inGameScreenEvidence);
            var inGameWindow = IsInGameHudEvidenceReady(inGameWindowEvidence);
            AbandonBreakdownIfCommandActive(abandonWhenCommandActive);
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

    // Now matches ComputeVisibleStateClassifierBreakdown's lobby/in-game evidence too -
    // DetectReadyScreenState evaluates both (stops the ready loop's input bursts once the
    // client has reached the lobby or a live game), so omitting them here would hide exactly
    // the evidence a diagnosis needs when the loop unexpectedly keeps running.
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
            var inGameStrict = IsInGameReadyStrictBounded(input, fallbackOnTimeout: false);
            var lobby = IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap(input);
            var createTab = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.CreateGameTab), widthRatio: 0.12, heightRatio: 0.040, windowRelative: false, sampleGrid: sampleGrid);
            var joinTab = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.JoinGameTab), widthRatio: 0.12, heightRatio: 0.040, windowRelative: false, sampleGrid: sampleGrid);

            return $"{FormatCheck("splash", splash, logo)} {FormatCheck("connecting", connecting, prompt)} {FormatCheck("offline", offline, emptyPanel)} "
                + $"char(btn={FormatCheck("", charButtons, play)},menu={FormatCheck("", charMenu, charMenuLogo)}) "
                + $"inGameStrict={(inGameStrict ? "T" : "F")} "
                + $"lobby(any={(lobby ? "T" : "F")},create={FormatCheck("", D2RScreenClassifier.IsLobbyCreateTabActive(createTab), createTab)},join={FormatCheck("", D2RScreenClassifier.IsLobbyJoinTabActive(joinTab), joinTab)})";
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
        return $"hpR={evidence.Health.RedRatio:F2},mpB={evidence.Mana.BlueRatio:F2},"
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
    internal const int MaxConcurrentBoundedCalls = 32;
    private static readonly SemaphoreSlim BoundedCallSlots = new(MaxConcurrentBoundedCalls, MaxConcurrentBoundedCalls);
    internal static int AvailableBoundedCallSlots => BoundedCallSlots.CurrentCount;

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
    // from TryConfirmAtElapsedDeadline on every failed HUD confirmation, not just on
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

    // A pending follow-auto account can be left in the previous game after an agent/host
    // restart or a partial-join bookkeeping loss. The ordinary menu-click safety gate correctly
    // refuses to click Lobby in that state, but merely turning that refusal into "wait one
    // cycle" strands the account forever: every cycle reaches the same gate again.
    //
    // Until Reign of the Warlock this method had a normalization step in front of the decision:
    // any non-legacy in-game match got exactly one G press to force the client into legacy
    // graphics, because legacy gave the detector stable globe/action-bar anchors, and only a
    // fresh LegacyProfile match afterwards authorized the exit. RoTW removed legacy graphics,
    // so G normalizes nothing and the step is gone. What survives unchanged is the property
    // that step existed to guarantee: the ordinary Escape+Save-and-Exit flow is authorized only
    // by a STRICT globe match (HudProfile), never by the broad Frame fallback. Frame matches on
    // ordinary outdoor scenery (see IsInGameReadyStrict), and acting on one would mean sending
    // Escape and a fixed-coordinate click into the live world.
    //
    // A positively identified Save-and-Exit menu is handled separately because
    // SaveAndExitAsync's leading Escape would close the menu that is already open. Its dedicated
    // three-button-plus-globes classifier authorizes only the already-visible Save and Exit
    // button, never an in-world or lobby click.
    internal static async Task<FollowAutoInGameRecoveryOutcome> RunFollowAutoInGameRecoveryAsync(
        Func<InGameHudMatchKind?> detectInGameMatch,
        Func<CancellationToken, Task<bool>> leaveGame,
        Func<CancellationToken, Task<bool>> leaveOpenPauseMenu,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return detectInGameMatch() switch
        {
            InGameHudMatchKind.None => FollowAutoInGameRecoveryOutcome.NotInGame,
            null => FollowAutoInGameRecoveryOutcome.DetectionInconclusive,
            InGameHudMatchKind.SaveAndExitMenu => await leaveOpenPauseMenu(cancellationToken)
                ? FollowAutoInGameRecoveryOutcome.LeftGame
                : FollowAutoInGameRecoveryOutcome.LeaveFailed,
            InGameHudMatchKind.HudProfile => await leaveGame(cancellationToken)
                ? FollowAutoInGameRecoveryOutcome.LeftGame
                : FollowAutoInGameRecoveryOutcome.LeaveFailed,
            _ => FollowAutoInGameRecoveryOutcome.DetectionInconclusive
        };
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

    private ReadyLaunchNudgeState CreateReadyLaunchNudgeState(
        BattleNetInstallRepairState? battleNetRepair = null)
    {
        return new ReadyLaunchNudgeState
        {
            NextLaunchRetryAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(GetBattleNetExecRetryDelaySeconds()),
            NextPlayClickAt = DateTimeOffset.UtcNow,
            NextGraphicsDeviceFailureProbeAt = DateTimeOffset.UtcNow,
            BattleNetRepair = battleNetRepair ?? new BattleNetInstallRepairState()
        };
    }

    private async Task<bool> NudgeD2RLaunchDuringReadyAsync(
        WindowsInput input,
        ReadyLaunchNudgeState state,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now >= state.NextGraphicsDeviceFailureProbeAt)
        {
            state.NextGraphicsDeviceFailureProbeAt = now
                + TimeSpan.FromMilliseconds(GraphicsDeviceFailureProbeIntervalMs);

            // Once this incident has burned its relaunch budget, another dismiss-and-relaunch is
            // just a faster way to reach the same dialog. Stop driving the client (but keep
            // suppressing the generic bursts, which cannot help either) and let ready fail with
            // the streak in its message, so the host escalates to a VM power cycle.
            if (IsGraphicsDeviceFailureExhausted()
                && input.DetectD2RGraphicsDeviceFailureDialog(GetD2RProcessNames()).Detected)
            {
                // Fail the whole ready command now instead of idling out its multi-minute
                // timeout. The host escalates on consecutive ready failures, and waiting out
                // five full timeouts before power-cycling a VM that is provably not coming back
                // would leave the account down for half an hour.
                state.GraphicsDeviceFailureExhausted = true;
                MarkCommandCheckpoint(
                    $"Graphics-device failure has repeated {GetGraphicsDeviceFailureSnapshot().Streak} times in this incident; "
                        + "no further relaunch attempts - this guest needs a VM power cycle.");
                return true;
            }

            var graphicsDeviceFailure = await TryDismissGraphicsDeviceFailureAndWaitAsync(input, cancellationToken);
            var graphicsDeviceFailureAction = ClassifyGraphicsDeviceFailureReadyAction(graphicsDeviceFailure);
            if (graphicsDeviceFailureAction != GraphicsDeviceFailureReadyAction.None)
            {
                // A detected-but-undismissed modal must also suppress the ready loop's generic
                // clicks and G presses. They cannot repair this native dialog, and letting them
                // fall through would make an explicitly recognized failure look like ordinary
                // startup input was still making progress.
                if (graphicsDeviceFailureAction == GraphicsDeviceFailureReadyAction.SuppressInput)
                {
                    return true;
                }

                state.GraphicsDeviceFailureDismissals++;
                var recoveryLaunch = TrySendD2RLaunchCommand();
                state.LaunchAttempts++;
                state.LastLaunchMessage =
                    $"Dismissed a failed-to-initialize-graphics dialog; {recoveryLaunch.Message}";

                var relaunchedAt = DateTimeOffset.UtcNow;
                state.NextLaunchRetryAt = relaunchedAt
                    + TimeSpan.FromSeconds(GetBattleNetExecRetryDelaySeconds());
                state.NextPlayClickAt = relaunchedAt + TimeSpan.FromSeconds(1.5);
                state.NextGraphicsDeviceFailureProbeAt = relaunchedAt
                    + TimeSpan.FromMilliseconds(GraphicsDeviceFailureProbeIntervalMs);
                MarkCommandCheckpoint(
                    $"Graphics-device recovery resent the normal D2R launch command ({recoveryLaunch.Message}).");
                return true;
            }
        }

        if (IsD2RNamedProcessRunning())
        {
            state.BattleNetRepair.Completed = true;
            return false;
        }

        if (await TryRepairBattleNetInstallLocationAsync(input, state, cancellationToken))
        {
            return true;
        }

        now = DateTimeOffset.UtcNow;
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
            return false;
        }

        var launch = TrySendD2RLaunchCommand();
        state.LaunchAttempts++;
        state.LastLaunchMessage = launch.Message;
        state.NextLaunchRetryAt = now + TimeSpan.FromSeconds(GetBattleNetExecRetryDelaySeconds());
        return false;
    }

    internal static GraphicsDeviceFailureReadyAction ClassifyGraphicsDeviceFailureReadyAction(
        D2RGraphicsDeviceFailureDismissalResult dismissal)
    {
        if (!dismissal.Detected)
        {
            return GraphicsDeviceFailureReadyAction.None;
        }

        return dismissal.DismissalSent
            ? GraphicsDeviceFailureReadyAction.Relaunch
            : GraphicsDeviceFailureReadyAction.SuppressInput;
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

            // A broken Battle.net session surfaces here as "Failed to authenticate" or "Cannot
            // Connect to Server" over the Online tab click - the same generic OK-dialog widget
            // as the join/create game error dialogs (identical box position and OK button,
            // confirmed against reference captures). Left undismissed it blocks every further
            // Online tab click for the rest of this loop, so the retry below never actually
            // retries - it just spins against a dialog nobody is closing.
            if (IsGameEntryErrorDialogOpen(input))
            {
                await DismissGameEntryErrorDialogAsync(input, cancellationToken);
            }
            else if (IsCharacterScreenOffline(input))
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
        bool keepLaunchAlive = false,
        long? followAutoRunId = null,
        BattleNetInstallRepairState? battleNetRepair = null)
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
        var launchNudges = CreateReadyLaunchNudgeState(battleNetRepair);

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
                launchNudges.GraphicsDeviceFailureDismissals,
                launchNudges.LastLaunchMessage);
        }

        while (DateTimeOffset.UtcNow < deadline)
        {
            ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);

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

                if (state == ReadyScreenState.GammaCalibration)
                {
                    return AbandonReadyLoopForGammaCalibration(
                        () => Result(false, nudges, lastState, skipSeconds));
                }

                nextDetectionAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(ReadyStartupDetectionIntervalMs);
            }

            var repairUiActive = keepLaunchAlive
                && await NudgeD2RLaunchDuringReadyAsync(input, launchNudges, cancellationToken);
            if (launchNudges.GraphicsDeviceFailureExhausted)
            {
                return Result(false, nudges, lastState, skipSeconds);
            }

            if (repairUiActive)
            {
                // The generic center-click/G startup bursts are safe inside D2R, but not over
                // Battle.net's Install/Continue UI. The dedicated repair state machine owns all
                // input until the validated existing install has been located.
            }
            else if (lastState == ReadyScreenState.ConnectingToBattleNet)
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
        bool keepLaunchAlive = false,
        long? followAutoRunId = null,
        BattleNetInstallRepairState? battleNetRepair = null)
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
        var launchNudges = CreateReadyLaunchNudgeState(battleNetRepair);

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
                launchNudges.GraphicsDeviceFailureDismissals,
                launchNudges.LastLaunchMessage);
        }

        for (var i = 0; i < plan.IntroClickCount && DateTimeOffset.UtcNow < deadline; i++)
        {
            ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);

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

                if (lastState == ReadyScreenState.GammaCalibration)
                {
                    return AbandonReadyLoopForGammaCalibration(
                        () => Result(false, nudges, lastState, timeoutSeconds));
                }

                nextDetectionAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(ReadyStartupDetectionIntervalMs);
            }

            var repairUiActive = keepLaunchAlive
                && await NudgeD2RLaunchDuringReadyAsync(input, launchNudges, cancellationToken);
            if (launchNudges.GraphicsDeviceFailureExhausted)
            {
                return Result(false, nudges, lastState, timeoutSeconds);
            }

            if (repairUiActive)
            {
                // Dedicated Battle.net repair input ran (or is waiting on its next safe step).
            }
            else if (lastState == ReadyScreenState.ConnectingToBattleNet)
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

        // The title-key phase shares nextDetectionAt with the intro phase above, so without this
        // its first iteration can send a burst on a detection up to ReadyStartupDetectionIntervalMs
        // old. That burst contains Space and Enter, which on the gamma calibration screen is the
        // Continue button - one press accepts the defaults D2R invented when it reset the settings
        // file, including a resolution every pixel classifier is calibrated against. A screen that
        // appeared inside that window has to be seen before anything is sent at it.
        nextDetectionAt = DateTimeOffset.UtcNow;

        for (var i = 0; i < plan.TitleScreenKeyPressCount && DateTimeOffset.UtcNow < deadline; i++)
        {
            ThrowIfFollowAutoStopped(followAutoRunId, cancellationToken);

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

                if (lastState == ReadyScreenState.GammaCalibration)
                {
                    return AbandonReadyLoopForGammaCalibration(
                        () => Result(false, nudges, lastState, timeoutSeconds));
                }

                nextDetectionAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(ReadyStartupDetectionIntervalMs);
            }

            var repairUiActive = keepLaunchAlive
                && await NudgeD2RLaunchDuringReadyAsync(input, launchNudges, cancellationToken);
            if (launchNudges.GraphicsDeviceFailureExhausted)
            {
                return Result(false, nudges, lastState, timeoutSeconds);
            }

            if (repairUiActive)
            {
                // Dedicated Battle.net repair input ran (or is waiting on its next safe step).
            }
            else if (lastState == ReadyScreenState.ConnectingToBattleNet)
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
    internal void RecordObservedFrame(string frame, DateTimeOffset? observedUtc = null)
    {
        var observedAt = observedUtc ?? DateTimeOffset.UtcNow;
        _lastObservedFrame = frame;
        _lastObservedFrameUtc = observedAt;

        // "Unknown" is the same literal for both VisibleD2RState and ReadyScreenState, so every
        // live classification that resolves nothing extends the unknown streak, and any
        // recognizable frame (including NotRunning) clears it. Diagnostics only - see the
        // field comment for why the stuck-load-screen watchdog must not key on this.
        if (frame == nameof(VisibleD2RState.Unknown))
        {
            _unknownFrameSinceUtc ??= _lastObservedFrameUtc;
        }
        else
        {
            _unknownFrameSinceUtc = null;
        }

        // Any frame that proves the client rendered something real closes the graphics-device
        // incident: the driver initialized, so the next failure (if there ever is one) is a new
        // incident with its own relaunch budget rather than an inherited exhausted one.
        if (IsHealthyRenderedFrame(frame))
        {
            ClearGraphicsDeviceFailureStreak();
            lock (_activityLock)
            {
                if (!_settingsRepairTransactionInProgress)
                {
                    var path = D2RSettingsFile.ResolveSettingsPath(_config.D2RSettingsPath);
                    var journalReadResult = D2RSettingsRepairStateReadResult.Missing;
                    string? journalReadError = null;
                    if (path is not null)
                    {
                        journalReadResult = D2RSettingsFile.ReadRepairState(
                            path,
                            out _,
                            out journalReadError);
                    }

                    if (_settingsRepairPhase is not null
                        || _settingsRepairIncidentAttempts > 0
                        || _settingsRepairJournalError is not null
                        || _settingsRepairJournalWarning is not null
                        || journalReadResult != D2RSettingsRepairStateReadResult.Missing)
                    {
                        if (D2RSettingsFile.TryClearRepairState(path, out var clearError))
                        {
                            _settingsRepairPhase = null;
                            _settingsRepairIncidentAttempts = 0;
                            _settingsRepairIncidentFirstAttemptUtc = null;
                            _settingsRepairIncidentLastAttemptUtc = null;
                            _settingsRepairBudgetJournalRewritePending = false;
                            _settingsRepairJournalError = null;
                            _settingsRepairJournalWarning = null;
                        }
                        else
                        {
                            // Keep the in-memory latch aligned with the sidecar so a later healthy
                            // observation retries deletion instead of resurrecting stale work on restart.
                            if (journalReadResult == D2RSettingsRepairStateReadResult.Invalid)
                            {
                                _settingsRepairJournalError = journalReadError ?? clearError;
                            }

                            _settingsRepairJournalWarning =
                                $"Healthy frame rendered, but the repair journal could not be cleared: {clearError}";
                            _lastSettingsRepairMessage = _settingsRepairJournalWarning;
                        }
                    }
                }
            }
        }

        if (frame == nameof(VisibleD2RState.GammaCalibration))
        {
            if (OperatingSystem.IsWindows() && TryGetD2RProcessStartUtc() is { } processStartedUtc)
            {
                ReconcileGammaCalibrationProcessInstance(processStartedUtc);
            }

            RecordGammaCalibrationSighting(observedAt);
            return;
        }

        lock (_activityLock)
        {
            // Before confirmation, *every* non-gamma observation is a failed consecutive look.
            // In particular, the ready loop's immediate second probe records Unknown on a miss;
            // retaining the first sighting there let two isolated positives authorize a write.
            // After confirmation the incident is deliberately latched through Unknown, splash,
            // NotRunning, and process-only fallbacks so a failed/cancelled repair stays retryable.
            if (!IsGammaCalibrationConfirmedLocked() || IsHealthyRenderedFrame(frame))
            {
                ClearGammaCalibrationSightingsLocked();
            }
        }
    }

    /// <summary>
    /// Counts consecutive gamma-calibration sightings. The repair this arms quits a live client
    /// and overwrites its settings file, so it takes <see cref="GammaCalibrationConfirmSightings"/>
    /// separate looks at the screen, not one.
    /// </summary>
    private void RecordGammaCalibrationSighting(DateTimeOffset observedUtc)
    {
        lock (_activityLock)
        {
            if (!IsGammaCalibrationConfirmedLocked()
                && _lastGammaCalibrationUtc is { } lastObservation
                && observedUtc - lastObservation > GammaCalibrationIncidentWindow)
            {
                ClearGammaCalibrationSightingsLocked();
            }

            _gammaCalibrationSightings = Math.Min(
                _gammaCalibrationSightings + 1,
                GammaCalibrationConfirmSightings);
            _lastGammaCalibrationUtc = observedUtc;
            _firstGammaCalibrationUtc ??= observedUtc;

            if (ShouldTransitionReadyRepairToPrepared(
                    gammaDetected: true,
                    IsGammaCalibrationConfirmedLocked(),
                    _settingsRepairPhase,
                    _settingsRepairJournalError is not null))
            {
                TransitionReadyRepairToPreparedLocked(
                    D2RSettingsFile.ResolveSettingsPath(_config.D2RSettingsPath));
            }
        }
    }

    private void ClearGammaCalibrationSightings()
    {
        lock (_activityLock)
        {
            ClearGammaCalibrationSightingsLocked();
        }
    }

    private void ClearGammaCalibrationSightingsLocked()
    {
        _gammaCalibrationSightings = 0;
        _firstGammaCalibrationUtc = null;
        _lastGammaCalibrationUtc = null;
    }

    private bool IsGammaCalibrationConfirmedLocked()
    {
        return _gammaCalibrationSightings >= GammaCalibrationConfirmSightings;
    }

    private (int Sightings, DateTimeOffset? FirstUtc, DateTimeOffset? LastUtc) GetGammaCalibrationSnapshot()
    {
        lock (_activityLock)
        {
            return (_gammaCalibrationSightings, _firstGammaCalibrationUtc, _lastGammaCalibrationUtc);
        }
    }

    internal bool IsSettingsRepairConfirmed()
    {
        return _config.SettingsRepairEnabled && IsGammaCalibrationIncidentConfirmed();
    }

    internal bool NeedsDonorSettings()
    {
        if (!_config.SettingsRepairEnabled)
        {
            return false;
        }

        lock (_activityLock)
        {
            var path = D2RSettingsFile.ResolveSettingsPath(_config.D2RSettingsPath);
            RefreshPersistedSettingsRepairStateLocked(path);
            NormalizePersistedSettingsRepairBudgetLocked(DateTimeOffset.UtcNow);
            return _settingsRepairJournalError is null
                && _settingsRepairPhase != D2RSettingsRepairPhase.Ready
                && (IsGammaCalibrationConfirmedLocked()
                    || _settingsRepairPhase == D2RSettingsRepairPhase.Prepared);
        }
    }

    internal bool IsGammaCalibrationIncidentConfirmed()
    {
        lock (_activityLock)
        {
            return IsGammaCalibrationConfirmedLocked();
        }
    }

    internal bool ShouldSuppressMenuInputForSettingsReset()
    {
        lock (_activityLock)
        {
            var path = D2RSettingsFile.ResolveSettingsPath(_config.D2RSettingsPath);
            RefreshPersistedSettingsRepairStateLocked(path);
            NormalizePersistedSettingsRepairBudgetLocked(DateTimeOffset.UtcNow);
            return _gammaCalibrationSightings > 0
                || _settingsRepairPhase == D2RSettingsRepairPhase.Prepared
                || _settingsRepairJournalError is not null;
        }
    }

    /// <summary>
    /// Pure authorization gate for a destructive donor copy. A live client must still be on the
    /// Gamma screen in a fresh screen/window probe; a stopped client is eligible only from a
    /// confirmed Gamma incident or a durable PREPARED retry latch.
    /// </summary>
    internal static bool CanApplySettingsRepair(
        bool d2rRunning,
        bool freshGammaDetected,
        bool gammaIncidentConfirmed,
        D2RSettingsRepairPhase? journalPhase,
        bool journalBlocked,
        out string refusalReason)
    {
        if (journalBlocked)
        {
            refusalReason =
                "The settings-repair journal is invalid or unreadable; refusing replacement until an operator removes or fixes it.";
            return false;
        }

        if (journalPhase == D2RSettingsRepairPhase.Ready)
        {
            refusalReason =
                "A donor settings replacement is already in Ready phase; refusing a duplicate copy until the client renders a healthy frame.";
            return false;
        }

        if (d2rRunning && !freshGammaDetected)
        {
            refusalReason =
                "D2R is running, but a fresh screen-and-window probe no longer detects Gamma Calibration; refusing to quit or replace its settings.";
            return false;
        }

        if (!gammaIncidentConfirmed && journalPhase != D2RSettingsRepairPhase.Prepared)
        {
            refusalReason =
                "No confirmed Gamma Calibration incident or durable Prepared retry exists; refusing to replace Settings.json.";
            return false;
        }

        refusalReason = "";
        return true;
    }

    private T AbandonReadyLoopForGammaCalibration<T>(Func<T> result)
    {
        // Every remaining nudge in the plan would land on this screen. A startup click that lands
        // on Continue accepts the defaults D2R invented for the settings file it just reset,
        // which is how a bad resolution gets baked in and takes every pixel classifier on this VM
        // with it.
        MarkCommandCheckpoint("ready loop stopped: D2R is on the first-run gamma calibration screen (settings reset)");
        ConfirmGammaCalibrationWithImmediateSecondProbe();
        return result();
    }

    /// <summary>
    /// Takes the second look the repair needs, right here, instead of leaving it to a later pass.
    /// </summary>
    /// <remarks>
    /// The host only acts on <c>needsDonorSettings</c>, which requires
    /// <see cref="GammaCalibrationConfirmSightings"/> sightings. The ready loop aborts on the
    /// first one, so a client whose gamma screen appeared during this pass would report just one
    /// sighting and wait a whole follow-auto cycle for its second - a client that is going nowhere
    /// on its own, delayed by the very check meant to protect it. Re-sampling costs seven small
    /// regions and keeps the two-look rule intact: this is a genuinely independent read, and a
    /// screen that has changed underneath simply fails it and clears the streak.
    /// </remarks>
    private void ConfirmGammaCalibrationWithImmediateSecondProbe()
    {
        if (!OperatingSystem.IsWindows() || IsGammaCalibrationIncidentConfirmed())
        {
            return;
        }

        var input = new WindowsInput();
        var confirmed = IsGammaCalibrationScreen(input, windowRelative: false)
            || IsGammaCalibrationScreen(input, windowRelative: true);
        RecordObservedFrame(confirmed
            ? nameof(VisibleD2RState.GammaCalibration)
            : nameof(VisibleD2RState.Unknown));
    }

    private bool DetectFreshGammaCalibrationForSettingsRepair()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var input = new WindowsInput();
            var screenDetected = IsGammaCalibrationScreen(input, windowRelative: false);
            var windowDetected = IsGammaCalibrationScreen(input, windowRelative: true);
            return screenDetected || windowDetected;
        }
        catch (Exception)
        {
            // A capture failure is not evidence authorizing a quit and file replacement.
            return false;
        }
    }

    // Deliberately excludes DiabloSplash: a client that renders its splash and then fails device
    // initialization would reset the incident on every relaunch, so the streak could never reach
    // the limit and the VM would never be power-cycled. Only a screen the client cannot reach
    // without a working renderer counts.
    private static bool IsHealthyRenderedFrame(string frame)
    {
        return frame == nameof(ReadyScreenState.CharacterMenu)
            || frame == nameof(VisibleD2RState.CharacterScreen)
            || frame == nameof(VisibleD2RState.OfflineCharacterScreen)
            || frame == nameof(VisibleD2RState.LobbyOrGame)
            || frame == nameof(VisibleD2RState.InGame)
            || frame == nameof(ReadyScreenState.CannotJoinCurrentCharacterDialog);
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

    // "Could not visually confirm the Lobby" carried no evidence of *why* - the live
    // IsAnyLobbyEntryMenuVisible check that produced it samples the tab/entry/form regions
    // and folds them into one bool, so a failure here was previously indistinguishable
    // between "genuinely not at the lobby," "a bounded sample call timed out under VM load
    // and defaulted to false" (TryRunBounded's documented bias - see the GDI/dwm.exe history
    // in pixel-classifier-catalog.md), and "IsInGameReady excluded it." Reuses the same
    // ComputeVisibleStateClassifierBreakdown the live status detector already builds on
    // Unknown, so a failure here shows the actual measured tab/entry/form/inGame values
    // instead of a bare true/false.
    private string FormatLobbyConfirmationDiagnostics(WindowsInput input)
    {
        var breakdown = TryRunBounded(() => ComputeVisibleStateClassifierBreakdown(input, MenuSampleGrid), ClassifierBreakdownBoundMs, "");
        return string.IsNullOrEmpty(breakdown) ? "" : $" Classifier breakdown: {breakdown}";
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

        // Check the live screen before trusting cached activity. Besides self-healing any stale
        // cache, this prevents a remembered LobbyOrGame state from bypassing the Gamma safety
        // guard after D2R has restarted onto the settings-reset screen.
        if (IsAnyLobbyEntryMenuVisible(input))
        {
            MarkLobbyOrGameInteraction("Confirmed Lobby visually before menu automation.");
            return null;
        }

        // This is the last common point before lobby-oriented commands begin blind character-slot
        // and Lobby clicks. Detect the terminal settings-reset screen here as well as in
        // menu_ready: a caller may issue a direct menu command after an agent/client restart, and
        // a window-relative Gamma result must stop that command rather than fall through to input.
        if (DetectReadyScreenStateStable(input) == ReadyScreenState.GammaCalibration)
        {
            return await RefuseMenuInputForGammaCalibrationAsync(
                "Lobby automation stopped before sending input",
                cancellationToken);
        }

        if (activity.State == D2RActivityState.LobbyOrGame)
        {
            MarkLobbyOrGameInteraction("Using remembered lobby/game state for menu automation.");
            return null;
        }

        // Previously unchecked here: a cold/stale cache landing on the offline character screen
        // fell straight through to the blind character-slot-then-Lobby click below, which clicks
        // real UI elements on a screen that isn't actually ready yet and surfaces as a confusing
        // "lobby not visible" failure with no mention of Battle.net being offline.
        if (IsCharacterScreenOffline(input)
            && !await EnsureOnlineCharacterScreenAsync(input, cancellationToken))
        {
            return CommandResult.Failure(
                $"D2R is at the offline character screen, and the Online tab did not reconnect within {GetCharacterScreenReconnectSeconds()}s.{FormatInputDiagnosticsSuffix()}",
                await CollectStatusAsync(cancellationToken));
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

    private async Task<CommandResult> RefuseMenuInputForGammaCalibrationAsync(
        string context,
        CancellationToken cancellationToken)
    {
        MarkCommandCheckpoint($"{context}: first-run gamma calibration screen detected");
        ConfirmGammaCalibrationWithImmediateSecondProbe();
        return CommandResult.Failure(
            $"{context}: D2R reset Settings.json and is stopped on the first-run gamma calibration screen; menu input is suppressed until settings repair succeeds.",
            await CollectStatusAsync(cancellationToken));
    }

    private async Task<(bool Confirmed, string Description)> WaitForPostSaveExitMenuAsync(
        WindowsInput input,
        long? followAutoRunId,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Clamp(
            Math.Max(Math.Max(_config.Ui.GameLoadSeconds, _config.Ui.LobbyLoadSeconds), 3),
            3,
            12);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);

        // The leave is done the moment the in-game HUD is gone. Positively re-identifying the
        // exact post-exit menu can lag (the client returns to the lobby with the friends drawer
        // still covering the Join/Create tabs, or sits a beat on a black exit transition), and
        // waiting the full timeout for that match is what left a client that was ready to rejoin
        // in ~1-2s idling for the whole window. So also treat "no longer in a game" as done.
        // Require two consecutive HUD-gone reads so a single frame of exit-animation flicker
        // can't fire it early; a genuinely missed Save and Exit click keeps the HUD up, so
        // IsInGameReady stays true, this never trips, and the caller's retry path is preserved.
        // Poll ONLY the in-game HUD here. watch-follow-auto-20260715-161810.log's elapsed stamps
        // proved the leave is done ~1s after the click (present @0.9s -> gone x1 @1.0s, never present
        // again), but the two positive re-identification checks (lobby / character screen) that used
        // to run first never fired during the transition and, even bounded, burned ~2.4s per
        // iteration timing out - exactly in the gone x1 -> gone x2 window - so confirmation slipped to
        // @4-8s. They are also redundant: a stably-gone HUD already covers "at the lobby / character
        // screen", and IsInGameReady reads gone (not a false present) once out of the game, so the
        // 2-consecutive-gone rule plus the rejoin path's own IsInGameReady guard keep this safe
        // without them. Treat a failed sample as no information (leave the counter alone); the
        // elapsed-stamped checkpoint keeps the present->gone timeline visible in a live `watch`.
        var startedUtc = DateTimeOffset.UtcNow;
        var consecutiveHudGone = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hudPresent = TryRunBounded<bool?>(() => IsInGameReady(input), PostSaveExitHudSampleBoundMs, fallback: null);
            if (hudPresent == true)
            {
                consecutiveHudGone = 0;
            }
            else if (hudPresent == false)
            {
                consecutiveHudGone++;
            }

            var elapsed = (DateTimeOffset.UtcNow - startedUtc).TotalSeconds;
            MarkCommandCheckpoint(
                $"WaitForPostSaveExitMenuAsync: post-exit HUD {(hudPresent == true ? "present" : hudPresent == false ? $"gone x{consecutiveHudGone}" : "unsampled")} @{elapsed:0.0}s");

            if (hudPresent == false && consecutiveHudGone >= 2)
            {
                MarkExpectedLobbyAfterSaveExit(
                    followAutoRunId,
                    "Save and Exit left the game; expecting the lobby for the next follow-auto check.");
                return (true, "left the game");
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                // Activity marking is left to the caller: only it knows whether the HUD is
                // still visible (retry) or the screen is genuinely unrecognized (unknown).
                return (false, "post-exit menu state was not visually confirmed");
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

        var readyState = DetectReadyScreenStateStable(input);
        if (readyState == ReadyScreenState.GammaCalibration)
        {
            ConfirmGammaCalibrationWithImmediateSecondProbe();
            return false;
        }

        if (readyState == ReadyScreenState.CharacterScreen)
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
            return false;
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
        DateTimeOffset GetBroadHudFrameAcceptAt() =>
            DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Clamp(_config.Ui.GameLoadSeconds, 3, 8));

        MarkCommandCheckpoint("ClickMenuEntryButtonUntilEnteredGameAsync: initial click");
        await ClickMenuEntryButtonAsync(input, button, cancellationToken);
        var broadHudFrameAcceptAt = GetBroadHudFrameAcceptAt();
        var entryReclickAt = broadHudFrameAcceptAt;
        var blindEntryReclicks = 0;

        GameEntryAttemptResult? TryConfirmAtElapsedDeadline(string checkpointContext)
        {
            if (DateTimeOffset.UtcNow < deadline)
            {
                return null;
            }

            if (!TryConfirmEnteredGame(
                    input,
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

            if (TryConfirmEnteredGame(
                    input,
                    $"ClickMenuEntryButtonUntilEnteredGameAsync: loop iteration {iteration}",
                    broadHudFrameAcceptAt))
            {
                MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: entered game confirmed (iteration {iteration})");
                return new GameEntryAttemptResult(true, dialogRetries, connectionRetries, "Entered game.");
            }

            MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: loop iteration {iteration}, checking current-character join restriction");
            if (IsCannotJoinCurrentCharacterDialogOpen(input))
            {
                return await BuildCurrentCharacterCannotJoinAttemptResultAsync(
                    input,
                    dialogRetries,
                    connectionRetries,
                    cancellationToken);
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

            var elapsedDeadlineResult = TryConfirmAtElapsedDeadline(
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

            elapsedDeadlineResult = TryConfirmAtElapsedDeadline(
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
                if (TryConfirmEnteredGame(
                        input,
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
            var waitResult = await WaitForGameEntryAsync(input, activeTab, cancellationToken, broadHudFrameAcceptAt);
            if (waitResult == GameEntryWaitResult.EnteredGame)
            {
                return new GameEntryAttemptResult(true, dialogRetries, connectionRetries, "Entered game.");
            }

            if (TryConfirmEnteredGame(
                    input,
                    $"ClickMenuEntryButtonUntilEnteredGameAsync: after wait result (iteration {iteration})",
                    broadHudFrameAcceptAt))
            {
                MarkCommandCheckpoint($"ClickMenuEntryButtonUntilEnteredGameAsync: entered game confirmed after wait result (iteration {iteration})");
                return new GameEntryAttemptResult(true, dialogRetries, connectionRetries, "Entered game after wait result.");
            }

            if (waitResult == GameEntryWaitResult.CurrentCharacterCannotJoin)
            {
                return await BuildCurrentCharacterCannotJoinAttemptResultAsync(
                    input,
                    dialogRetries,
                    connectionRetries,
                    cancellationToken);
            }
            else if (waitResult == GameEntryWaitResult.ConnectionInterrupted)
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
            // GameIsFull deliberately rides the ErrorDialog path here: only follow-auto parks on
            // a full game. The named join/create flows keep their existing dismiss-and-retry
            // behavior so a transient full read never changes how join-all races into a game.
            else if (waitResult is GameEntryWaitResult.ErrorDialog or GameEntryWaitResult.GameIsFull)
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

    private async Task<bool> DismissCannotJoinCurrentCharacterDialogAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ClickD2R(input, GetUiPoint(D2RUiCoordinateTarget.CannotJoinCurrentCharacterCancelButton));
            await DelayLongAsync(cancellationToken);

            if (!IsCannotJoinCurrentCharacterDialogOpen(input))
            {
                return true;
            }
        }

        return !IsCannotJoinCurrentCharacterDialogOpen(input);
    }

    private async Task<CommandResult?> DismissCannotJoinDialogDuringReadyAsync(
        WindowsInput input,
        CancellationToken cancellationToken)
    {
        if (!IsCannotJoinCurrentCharacterDialogOpen(input))
        {
            return null;
        }

        MarkCommandCheckpoint(
            "ReadyClientAsync: leftover current-character join restriction visible; dismissing before startup input");
        var dismissed = await DismissCannotJoinCurrentCharacterDialogAsync(input, cancellationToken);
        if (!dismissed)
        {
            return CommandResult.Failure(
                $"D2R's leftover current-character join restriction could not be dismissed.{FormatInputDiagnosticsSuffix()}",
                await CollectStatusAsync(cancellationToken));
        }

        MarkLobbyOrGameInteraction(
            "Ready flow dismissed a leftover current-character join restriction at the lobby.");
        return CommandResult.Success(
            "Dismissed a leftover current-character join restriction; D2R is ready at the lobby.",
            await CollectStatusAsync(cancellationToken));
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
            + "(hpR/mpB=health-red/mana-blue globe ratio, "
            + "thresholds health>0.20 mana>0.18; bar/bot/ctr=action-bar/bottom/center HUD luminance stats).";
    }

    private static InGameHudEvidence EmptyInGameHudEvidence()
    {
        var empty = new ScreenRegionStats(0, 0, 0, 0, 0, 0, 0, 0, 0);
        return new InGameHudEvidence(empty, empty, empty, empty, empty);
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
            GameEntryWaitResult.CurrentCharacterCannotJoin => "D2R reported that the game cannot be joined with the current character.",
            GameEntryWaitResult.GameIsFull => "D2R reported that the game is full.",
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

        if (IsCharacterMenuReady(input, windowRelative: false, sampleGrid)
            || IsCharacterMenuReady(input, windowRelative: true, sampleGrid))
        {
            return ReadyScreenState.CharacterMenu;
        }

        // Mirrors DetectVisibleD2RState's ordering: strict in-game HUD evidence (the health and
        // mana globes) is checked before the lobby check, because sitting_in_town*.png proved the
        // lobby-tab/entry-button thresholds can coincidentally match ordinary outdoor scenery.
        // Recognizing both here - not just at the top-level status detector - means the ready
        // loop itself stops sending click/key input the moment the client has already reached
        // the lobby or a live game, instead of treating "lobby" or "in-game" as Unknown and
        // continuing to blast input. A misdirected click in a live game is a movement click,
        // which can kill a Hardcore character (the v0.2.79 incident this guards against).
        if (IsInGameReadyStrictBounded(input, fallbackOnTimeout: true))
        {
            return ReadyScreenState.InGame;
        }

        if (IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap(input))
        {
            return ReadyScreenState.LobbyOrGame;
        }

        if (IsCannotJoinCurrentCharacterDialogOpen(input))
        {
            return ReadyScreenState.CannotJoinCurrentCharacterDialog;
        }

        // Checked last (see IsGammaCalibrationScreen). Recognizing it here is what stops the ready
        // loop pumping intro-skip clicks and title keys at a screen whose Continue button would
        // just commit the defaults D2R invented.
        if (IsGammaCalibrationScreen(input, windowRelative: false, sampleGrid)
            || IsGammaCalibrationScreen(input, windowRelative: true, sampleGrid))
        {
            return ReadyScreenState.GammaCalibration;
        }

        return ReadyScreenState.Unknown;
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
            detectedViaWindowRelative = IsActionableReadyScreenDetection(state);
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
                return IsActionableReadyScreenDetection(state);
            },
            ReadyStartupDetectionIntervalMs);

        return ResolveBoundedWindowReadyDetection(state, detected);
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

        if (IsCharacterMenuReady(input, windowRelative: false, sampleGrid))
        {
            return ReadyScreenState.CharacterMenu;
        }

        // See DetectReadyScreenState for why strict in-game evidence is checked before the
        // lobby check, and why both matter here: stopping the ready loop's input bursts the
        // moment the client has reached the lobby or a live game, not just reporting Unknown.
        if (IsInGameReadyStrictBounded(input, fallbackOnTimeout: true))
        {
            return ReadyScreenState.InGame;
        }

        if (IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap(input))
        {
            return ReadyScreenState.LobbyOrGame;
        }

        if (IsCannotJoinCurrentCharacterDialogOpen(input))
        {
            return ReadyScreenState.CannotJoinCurrentCharacterDialog;
        }

        if (IsGammaCalibrationScreen(input, windowRelative: false, sampleGrid))
        {
            return ReadyScreenState.GammaCalibration;
        }

        return ReadyScreenState.Unknown;
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

        if (IsCharacterMenuReady(input, windowRelative: true, sampleGrid))
        {
            return ReadyScreenState.CharacterMenu;
        }

        // See DetectReadyScreenState for why strict in-game evidence is checked before the
        // lobby check, and why both matter here: stopping the ready loop's input bursts the
        // moment the client has reached the lobby or a live game, not just reporting Unknown.
        if (IsInGameReadyStrictBounded(input, fallbackOnTimeout: true))
        {
            return ReadyScreenState.InGame;
        }

        if (IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap(input))
        {
            return ReadyScreenState.LobbyOrGame;
        }

        if (IsCannotJoinCurrentCharacterDialogOpen(input))
        {
            return ReadyScreenState.CannotJoinCurrentCharacterDialog;
        }

        if (IsGammaCalibrationScreen(input, windowRelative: true, sampleGrid))
        {
            return ReadyScreenState.GammaCalibration;
        }

        return ReadyScreenState.Unknown;
    }

    private static bool IsReadyScreenState(ReadyScreenState state)
    {
        return state is ReadyScreenState.CharacterScreen
            or ReadyScreenState.CharacterMenu
            or ReadyScreenState.OfflineCharacterScreen
            or ReadyScreenState.LobbyOrGame
            or ReadyScreenState.InGame
            or ReadyScreenState.CannotJoinCurrentCharacterDialog;
    }

    // GammaCalibration is not a successful "ready" state, but it is an actionable terminal
    // detection: every ready/menu input burst must stop there. Keeping this separate from
    // IsReadyScreenState prevents callers from reporting success while ensuring the bounded
    // window-relative wrapper does not collapse GammaCalibration back to Unknown.
    internal static bool IsActionableReadyScreenDetection(ReadyScreenState state)
    {
        return IsReadyScreenState(state) || state == ReadyScreenState.GammaCalibration;
    }

    internal static ReadyScreenState ResolveBoundedWindowReadyDetection(
        ReadyScreenState state,
        bool completedWithinBound)
    {
        return completedWithinBound && IsActionableReadyScreenDetection(state)
            ? state
            : ReadyScreenState.Unknown;
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
        return TryRunBounded(() => IsGameEntryErrorDialogVisible(input), EntryLoopCheckBoundMs);
    }

    private bool IsGameEntryErrorDialogVisible(WindowsInput input)
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
    }

    // "Game is full" renders in the exact generic OK-dialog geometry IsGameEntryErrorDialogVisible
    // matches, so this must be checked FIRST wherever a generic dialog would otherwise absorb it -
    // a full game must not be treated as a retryable stale-dialog error, or a follow-auto client
    // will hammer Join and grab the first freed slot (typically a human's) the moment one opens.
    // Text-band regions/grid mirror ReferenceCaptureClassifier.IsGameFullDialogOpen exactly.
    private bool IsGameFullDialogOpen(WindowsInput input)
    {
        return TryRunBounded(() =>
        {
            if (!IsGameEntryErrorDialogVisible(input))
            {
                return false;
            }

            var textCenter = input.SampleRegion(new AgentCommon.UiPoint(0.500, 0.457), widthRatio: 0.055, heightRatio: 0.034, sampleGrid: GameFullTextBandSampleGrid);
            var textLeftFlank = input.SampleRegion(new AgentCommon.UiPoint(0.435, 0.457), widthRatio: 0.055, heightRatio: 0.034, sampleGrid: GameFullTextBandSampleGrid);
            var textRightFlank = input.SampleRegion(new AgentCommon.UiPoint(0.565, 0.457), widthRatio: 0.055, heightRatio: 0.034, sampleGrid: GameFullTextBandSampleGrid);
            var secondLine = input.SampleRegion(new AgentCommon.UiPoint(0.500, 0.478), widthRatio: 0.20, heightRatio: 0.026, sampleGrid: GameFullTextBandSampleGrid);
            return D2RScreenClassifier.IsGameFullDialogTextBand(textCenter, textLeftFlank, textRightFlank, secondLine);
        }, EntryLoopCheckBoundMs);
    }

    private bool IsCannotJoinCurrentCharacterDialogOpen(WindowsInput input)
    {
        return TryRunBounded(() =>
        {
            var cancelButton = input.SampleRegion(
                GetUiPoint(D2RUiCoordinateTarget.CannotJoinCurrentCharacterCancelButton),
                widthRatio: 0.13,
                heightRatio: 0.050);
            var switchCharactersButton = input.SampleRegion(
                new AgentCommon.UiPoint(0.570, 0.539),
                widthRatio: 0.15,
                heightRatio: 0.050);
            var topBorder = input.SampleRegion(
                new AgentCommon.UiPoint(0.500, 0.381),
                widthRatio: 0.32,
                heightRatio: 0.025);
            var body = input.SampleRegion(
                new AgentCommon.UiPoint(0.500, 0.455),
                widthRatio: 0.32,
                heightRatio: 0.15);
            return D2RScreenClassifier.IsCannotJoinCurrentCharacterDialog(
                cancelButton,
                switchCharactersButton,
                topBorder,
                body);
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

    internal static bool IsFriendContextJoinPointInLeftPane(AgentCommon.UiPoint point)
    {
        return point.X < 0.45;
    }

    // Sampled twice per join attempt - once before the right-click and once after - so
    // FriendContextMenuProbe can tell an open context menu from the Friends pane art the
    // predicted Join Game point would otherwise be clicked against.
    private static ScreenRegionStats SampleFriendContextMenuRegion(
        WindowsInput input,
        AgentCommon.UiPoint friendJoinPoint)
    {
        return input.SampleRegion(
            friendJoinPoint,
            FriendContextMenuProbe.RegionWidthRatio,
            FriendContextMenuProbe.RegionHeightRatio,
            sampleGrid: FriendContextMenuProbe.RegionSampleGrid);
    }

    private bool IsInGameReady(WindowsInput input)
    {
        return IsInGameReady(input, windowRelative: false)
            || IsInGameReady(input, windowRelative: true);
    }

    // Only the HUD globe profiles - never the broader Frame-kind fallback. The
    // globes are a far more distinctive signal than the generic lobby-tab/entry-button
    // luminance thresholds, which sitting_in_town.png proved can coincidentally match ordinary
    // outdoor scenery. Used to check strict in-game evidence before the lobby check in
    // DetectVisibleD2RState, without touching the deliberately-after-lobby Frame fallback.
    private bool IsInGameReadyStrict(WindowsInput input)
    {
        return DetectInGameHudMatch(input, windowRelative: false) is InGameHudMatchKind.HudProfile
                or InGameHudMatchKind.SaveAndExitMenu
            || DetectInGameHudMatch(input, windowRelative: true) is InGameHudMatchKind.HudProfile
                or InGameHudMatchKind.SaveAndExitMenu;
    }

    private bool IsInGameReadyStrictBounded(WindowsInput input, bool fallbackOnTimeout)
    {
        return TryRunBounded(() => IsInGameReadyStrict(input), InGameHudSampleBoundMs, fallbackOnTimeout);
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
        var health = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.HealthGlobe), widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
        var mana = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.ManaGlobe), widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
        if (D2RScreenClassifier.IsInGameHudProfile(health, mana, actionHud, healthRedThreshold: 0.20, manaBlueThreshold: 0.18))
        {
            return InGameHudMatchKind.HudProfile;
        }

        // The Escape menu dims the HUD enough to fail the ordinary profile. Only pay for its
        // three extra samples when both dimmed globe colors are still present.
        if (health.RedRatio > 0.12 && mana.BlueRatio > 0.20)
        {
            var optionsButton = SampleD2RRegion(
                input,
                GetUiPoint(D2RUiCoordinateTarget.OptionsButton),
                widthRatio: 0.16,
                heightRatio: 0.045,
                windowRelative: windowRelative);
            var saveAndExitButton = SampleD2RRegion(
                input,
                GetUiPoint(D2RUiCoordinateTarget.SaveAndExitButton),
                widthRatio: 0.16,
                heightRatio: 0.045,
                windowRelative: windowRelative);
            var returnToGameButton = SampleD2RRegion(
                input,
                GetUiPoint(D2RUiCoordinateTarget.ReturnToGameButton),
                widthRatio: 0.16,
                heightRatio: 0.045,
                windowRelative: windowRelative);
            if (D2RScreenClassifier.IsSaveAndExitMenu(
                    health,
                    mana,
                    optionsButton,
                    saveAndExitButton,
                    returnToGameButton))
            {
                return InGameHudMatchKind.SaveAndExitMenu;
            }
        }

        var bottomHud = SampleD2RRegion(input, new AgentCommon.UiPoint(0.500, 0.940), widthRatio: 0.70, heightRatio: 0.13, windowRelative: windowRelative);
        var centerHud = SampleD2RRegion(input, new AgentCommon.UiPoint(0.500, 0.940), widthRatio: 0.22, heightRatio: 0.08, windowRelative: windowRelative);
        return D2RScreenClassifier.IsInGameHudFrame(actionHud, bottomHud, centerHud)
            ? InGameHudMatchKind.Frame
            : InGameHudMatchKind.None;
    }

    private InGameHudMatchKind DetectBestInGameHudMatch(WindowsInput input)
    {
        var screenMatch = DetectInGameHudMatch(input, windowRelative: false);
        if (screenMatch is InGameHudMatchKind.HudProfile
            or InGameHudMatchKind.SaveAndExitMenu)
        {
            return screenMatch;
        }

        var windowMatch = DetectInGameHudMatch(input, windowRelative: true);
        if (windowMatch is InGameHudMatchKind.HudProfile
            or InGameHudMatchKind.SaveAndExitMenu)
        {
            return windowMatch;
        }

        return screenMatch != InGameHudMatchKind.None ? screenMatch : windowMatch;
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
        var health = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.HealthGlobe), widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
        var mana = SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.ManaGlobe), widthRatio: 0.055, heightRatio: 0.080, windowRelative: windowRelative);
        var bottomHud = SampleD2RRegion(input, new AgentCommon.UiPoint(0.500, 0.940), widthRatio: 0.70, heightRatio: 0.13, windowRelative: windowRelative);
        var centerHud = SampleD2RRegion(input, new AgentCommon.UiPoint(0.500, 0.940), widthRatio: 0.22, heightRatio: 0.08, windowRelative: windowRelative);
        return new InGameHudEvidence(health, mana, actionHud, bottomHud, centerHud);
    }

    private static bool IsInGameHudEvidenceReady(InGameHudEvidence evidence)
    {
        if (D2RScreenClassifier.IsInGameHudProfile(evidence.Health, evidence.Mana, evidence.ActionHud, healthRedThreshold: 0.20, manaBlueThreshold: 0.18))
        {
            return true;
        }

        return D2RScreenClassifier.IsInGameHudFrame(evidence.ActionHud, evidence.BottomHud, evidence.CenterHud);
    }

    // D2R's first-run Gamma Calibration screen: the client decided Settings.json was unusable,
    // rewrote it from defaults, and now stops here on the way from the intro videos to character
    // select. It never advances on its own, so every menu_ready times out, follow-auto burns its
    // whole escalation ladder (VM power cycle, then a node restart) on a client whose problem is
    // a file, and the fleet stalls on one VM. See docs/runbooks/ui-state-catalog.md.
    //
    // Seven small samples, so it is checked last in every detection chain - after the states that
    // matter on the hot path have all had their turn - rather than adding cost to the common case.
    private bool IsGammaCalibrationScreen(WindowsInput input, bool windowRelative, int sampleGrid = MenuSampleGrid)
    {
        return TryRunBounded(
            () =>
            {
                var ramp = D2RScreenClassifier.GammaCalibrationRampPatches
                    .Select(patch => SampleD2RRegion(
                        input,
                        new AgentCommon.UiPoint(patch.CenterX, patch.CenterY),
                        patch.WidthRatio,
                        patch.HeightRatio,
                        windowRelative,
                        sampleGrid))
                    .ToArray();
                var leftFlank = SampleGammaCalibrationFlank(
                    input, D2RScreenClassifier.GammaCalibrationLeftFlank, windowRelative, sampleGrid);
                var rightFlank = SampleGammaCalibrationFlank(
                    input, D2RScreenClassifier.GammaCalibrationRightFlank, windowRelative, sampleGrid);
                return D2RScreenClassifier.IsGammaCalibrationScreen(ramp, leftFlank, rightFlank);
            },
            GammaCalibrationSampleBoundMs,
            fallback: false);
    }

    private ScreenRegionStats SampleGammaCalibrationFlank(
        WindowsInput input,
        ScreenSampleRegion region,
        bool windowRelative,
        int sampleGrid)
    {
        return SampleD2RRegion(
            input,
            new AgentCommon.UiPoint(region.CenterX, region.CenterY),
            region.WidthRatio,
            region.HeightRatio,
            windowRelative,
            sampleGrid);
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
        return await WaitForGameEntryAsync(input, returnTab: null, cancellationToken);
    }

    private async Task<GameEntryWaitResult> WaitForGameEntryAsync(
        WindowsInput input,
        AgentCommon.UiPoint? returnTab,
        CancellationToken cancellationToken,
        DateTimeOffset? broadHudFrameAcceptAt = null)
    {
        // Before Reign of the Warlock this deadline was also stretched to cover the post-entry
        // legacy-graphics toggle and its settle time. RoTW removed legacy graphics, so nothing
        // is done to the client after entry is confirmed and the entry/load timeouts are the
        // only thing left to wait on.
        var delaySeconds = Math.Max(
            Math.Max(_config.Ui.GameEntryStartTimeoutSeconds, 1),
            Math.Max(_config.Ui.GameLoadSeconds, 1));

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

            if (TryConfirmEnteredGame(
                    input,
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
                MarkCommandCheckpoint($"WaitForGameEntryAsync: poll iteration {pollIteration}, checking current-character join restriction");
                if (IsCannotJoinCurrentCharacterDialogOpen(input))
                {
                    MarkCommandCheckpoint("WaitForGameEntryAsync: current character cannot join dialog visible");
                    return GameEntryWaitResult.CurrentCharacterCannotJoin;
                }

                MarkCommandCheckpoint($"WaitForGameEntryAsync: poll iteration {pollIteration}, checking game-entry error dialog");
                if (IsGameEntryErrorDialogOpen(input))
                {
                    if (IsGameFullDialogOpen(input))
                    {
                        MarkCommandCheckpoint("WaitForGameEntryAsync: game-is-full dialog visible");
                        return GameEntryWaitResult.GameIsFull;
                    }

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

        if (TryConfirmEnteredGame(
                input,
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

        MarkCommandCheckpoint("WaitForGameEntryAsync: deadline, checking current-character join restriction");
        if (IsCannotJoinCurrentCharacterDialogOpen(input))
        {
            return GameEntryWaitResult.CurrentCharacterCannotJoin;
        }

        MarkCommandCheckpoint("WaitForGameEntryAsync: deadline, checking game-entry error dialog");
        if (IsGameEntryErrorDialogOpen(input))
        {
            return IsGameFullDialogOpen(input)
                ? GameEntryWaitResult.GameIsFull
                : GameEntryWaitResult.ErrorDialog;
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

        // IsLobbyTabReady internally re-runs IsCharacterMenuReady to exclude character-screen
        // Act backgrounds. When the friends-list drawer is open and expanded, the friend rows'
        // grey text/icons at the left-side y=0.405/0.460 sample points can trigger a false
        // positive on IsCharacterMenuReady, which then makes !characterMenuReady=false and
        // blocks the tab check - even though the outer IsCharacterScreenReady guard (which
        // requires the full character-select button pair) already confirmed we're not at a
        // character screen. Sample the tab stats directly and call the classifier with the
        // character-screen parameters forced to false, trusting the outer guard.
        var createTabStats = TryRunBounded<ScreenRegionStats?>(
            () => SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.CreateGameTab), widthRatio: 0.10, heightRatio: 0.045, windowRelative: false),
            EntryLoopCheckBoundMs, null);
        var joinTabStats = TryRunBounded<ScreenRegionStats?>(
            () => SampleD2RRegion(input, GetUiPoint(D2RUiCoordinateTarget.JoinGameTab), widthRatio: 0.10, heightRatio: 0.045, windowRelative: false),
            EntryLoopCheckBoundMs, null);
        var createTab = createTabStats is { } ct && D2RScreenClassifier.IsLobbyTabReady(ct, characterButtonPairReady: false, characterMenuReady: false);
        var joinTab = joinTabStats is { } jt && D2RScreenClassifier.IsLobbyTabReady(jt, characterButtonPairReady: false, characterMenuReady: false);
        var entry = IsLobbyEntryButtonReady(input);
        var formScreen = IsLobbyFormPanelReady(input, windowRelative: false);
        var formWindow = IsLobbyFormPanelReady(input, windowRelative: true);
        return D2RScreenClassifier.IsGameEntryMenuVisible(createTab || joinTab, entry, formScreen || formWindow);
    }

    private bool TryConfirmEnteredGame(
        WindowsInput input,
        string checkpointContext = "TryConfirmEnteredGame",
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
        return true;
    }

    private void SendOneEscape(WindowsInput input)
    {
        // Prefer the D2R HWND because FocusD2R deliberately does not block on foreground
        // negotiation. A false return means no usable target was found and therefore no
        // window key was sent, so the visible scan-code press is a true fallback rather than
        // a second state-changing delivery.
        SendSingleStatefulKey(
            () => input.SendWindowEscapeKey(GetD2RProcessNames()),
            input.PressEscape);
    }

    internal static void SendSingleStatefulKey(
        Func<bool> trySendWindowKey,
        Action sendVisibleKey)
    {
        if (!trySendWindowKey())
        {
            sendVisibleKey();
        }
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

    private int ResolveFriendRow(int? friendRow)
    {
        var row = friendRow ?? _config.Ui.DefaultFriendRow;
        return row > 0 ? row : new D2RUiAutomationConfig().DefaultFriendRow;
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

    // After a menu toggle with an observable result (the friends drawer opening, the accordion
    // expanding), wait for that result to actually appear instead of sleeping a flat
    // DelayLongAsync regardless of how fast the UI responded. Capped at the same LongDelayMs
    // budget the fixed sleep used, so a slow VM still gets the full time - this can only ever
    // finish earlier, never later. Same "poll for evidence, capped" shape as
    // WaitForPostSaveExitMenuAsync; the condition is bounded so a hung GDI sample can't stall it.
    private async Task PollForConditionAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(Math.Max(_config.Ui.LongDelayMs, 100));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryRunBounded(condition, EntryLoopCheckBoundMs, fallback: false))
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return;
            }

            await Task.Delay(EntryPollIntervalMs, cancellationToken);
        }
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

    private async Task<bool> TryRepairBattleNetInstallLocationAsync(
        WindowsInput input,
        ReadyLaunchNudgeState launchState,
        CancellationToken cancellationToken)
    {
        var repair = launchState.BattleNetRepair;
        if (!_config.RepairBattleNetInstallLocationWhenNeeded
            || repair.Completed
            || !IsBattleNetRunning())
        {
            return false;
        }

        var battleNetNames = GetBattleNetProcessNames();
        bool installationRequired;
        try
        {
            var continueButton = input.SampleRegion(
                GetUiPoint(D2RUiCoordinateTarget.BattleNetInstallRequiredContinueButton),
                widthRatio: 0.105,
                heightRatio: 0.050,
                coordinateProcessNames: battleNetNames);
            var cancelButton = input.SampleRegion(
                GetUiPoint(D2RUiCoordinateTarget.BattleNetInstallRequiredCancelButton),
                widthRatio: 0.090,
                heightRatio: 0.050,
                coordinateProcessNames: battleNetNames);
            installationRequired = BattleNetScreenClassifier.IsInstallationRequiredModal(
                continueButton,
                cancelButton);
        }
        catch (InvalidOperationException)
        {
            installationRequired = false;
        }

        if (installationRequired)
        {
            repair.Authorized = true;
            if (!repair.ModalCancelled)
            {
                // Continue enters the 30GB installation path. Cancel is the only safe exit before
                // selecting the already-existing D2R directory.
                if (!TryClickBattleNetPoint(
                        input,
                        D2RUiCoordinateTarget.BattleNetInstallRequiredCancelButton,
                        battleNetNames))
                {
                    repair.NextActionAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                    launchState.LastLaunchMessage =
                        "Detected Battle.net's Installation Required prompt, but its Cancel button could not receive input; retrying safely.";
                    return true;
                }

                repair.ModalCancelled = true;
                repair.NextActionAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                launchState.LastLaunchMessage =
                    "Detected Battle.net's Installation Required prompt and cancelled the new-install path; locating the existing game.";
            }

            return true;
        }

        if (!repair.Authorized)
        {
            return false;
        }

        var installDirectory = ResolveD2RInstallDirectory();
        var d2rExecutable = Path.Combine(installDirectory, "D2R.exe");
        if (!File.Exists(d2rExecutable))
        {
            launchState.LastLaunchMessage =
                $"Battle.net install-location repair was detected, but the validated game executable is missing: {d2rExecutable}";
            return true;
        }

        if (input.IsWindowWithExactTitleOpen(BattleNetFolderDialogTitle))
        {
            if (DateTimeOffset.UtcNow < repair.NextActionAt)
            {
                return true;
            }

            if (!input.TryFocusWindowWithExactTitle(BattleNetFolderDialogTitle)
                || !input.LeftClickWindowWithExactTitle(
                    BattleNetFolderDialogTitle,
                    GetUiPoint(D2RUiCoordinateTarget.BattleNetFolderPathField)))
            {
                launchState.LastLaunchMessage =
                    "Battle.net opened Choose a Folder, but the VM agent could not focus its path field.";
                repair.NextActionAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            input.SelectAll();
            input.DeleteSelection();
            input.TypeText(installDirectory);
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            if (!input.LeftClickWindowWithExactTitle(
                    BattleNetFolderDialogTitle,
                    GetUiPoint(D2RUiCoordinateTarget.BattleNetFolderSelectButton)))
            {
                launchState.LastLaunchMessage =
                    "Battle.net's existing-game directory was typed, but Select Folder could not be clicked.";
                repair.NextActionAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                return true;
            }

            repair.FolderSubmitted = true;
            repair.NextActionAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            launchState.LastLaunchMessage =
                $"Submitted the validated existing D2R directory to Battle.net: {installDirectory}";
            return true;
        }

        if (DateTimeOffset.UtcNow < repair.NextActionAt)
        {
            return true;
        }

        var confirmationVisible = IsBattleNetInstallLocationConfirmationVisible(input, battleNetNames);
        if (confirmationVisible)
        {
            if (!repair.FolderSubmitted)
            {
                // A stale confirmation panel may already be open from a previous manual attempt.
                // Never accept its path: force the validated directory through the chooser first.
                if (!TryClickBattleNetPoint(
                        input,
                        D2RUiCoordinateTarget.BattleNetChangeInstallFolder,
                        battleNetNames))
                {
                    repair.NextActionAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                    launchState.LastLaunchMessage =
                        "Battle.net showed a stale install-location confirmation, but Change Folder could not receive input; retrying safely.";
                    return true;
                }

                repair.NextActionAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                launchState.LastLaunchMessage =
                    "Battle.net already showed an install-location confirmation; reopening Change Folder to validate D2R.exe first.";
                return true;
            }

            if (!repair.StartInstallClicked)
            {
                // With a directory whose D2R.exe was verified above, this button makes Battle.net
                // scan/register the existing files; it must never be clicked for an unvalidated
                // or default-only path.
                if (!TryClickBattleNetPoint(
                        input,
                        D2RUiCoordinateTarget.BattleNetStartInstallButton,
                        battleNetNames))
                {
                    repair.NextActionAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                    launchState.LastLaunchMessage =
                        "Battle.net's validated Start Install scan button could not receive input; retrying safely.";
                    return true;
                }

                repair.StartInstallClicked = true;
                repair.NextActionAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
                launchState.LastLaunchMessage =
                    "Battle.net is scanning the validated existing D2R install; waiting to relaunch.";
                return true;
            }
        }

        if (!repair.FolderSubmitted)
        {
            // --exec may have left Battle.net on Shop. Reissue it before each bounded Locate
            // attempt; clicking the same relative point on Shop is inert, and the next attempt
            // runs after the product command has brought the D2R card back.
            var relaunch = TrySendD2RLaunchCommand();
            launchState.LaunchAttempts++;
            launchState.LastLaunchMessage = relaunch.Message;
            _ = TryClickBattleNetPoint(
                input,
                D2RUiCoordinateTarget.BattleNetLocateGameLink,
                battleNetNames);
            repair.LocateAttempts++;
            repair.NextActionAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
            return true;
        }

        var launch = TrySendD2RLaunchCommand();
        launchState.LaunchAttempts++;
        launchState.LastLaunchMessage = launch.Ok
            ? "Battle.net install-location repair completed; D2R launch command resent."
            : launch.Message;
        repair.NextActionAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        return true;
    }

    private bool IsBattleNetInstallLocationConfirmationVisible(
        WindowsInput input,
        string[] battleNetNames)
    {
        try
        {
            var startInstall = input.SampleRegion(
                GetUiPoint(D2RUiCoordinateTarget.BattleNetStartInstallButton),
                widthRatio: 0.135,
                heightRatio: 0.050,
                coordinateProcessNames: battleNetNames);
            var title = input.SampleRegion(
                GetUiPoint(D2RUiCoordinateTarget.BattleNetInstallConfirmationTitle),
                widthRatio: 0.360,
                heightRatio: 0.060,
                coordinateProcessNames: battleNetNames);
            return BattleNetScreenClassifier.IsInstallLocationConfirmation(startInstall, title);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryClickBattleNetPoint(
        WindowsInput input,
        D2RUiCoordinateTarget target,
        string[] battleNetNames)
    {
        var point = GetUiPoint(target);
        try
        {
            var delivered = false;
            if (input.TryFocusProcess(battleNetNames))
            {
                input.LeftClick(point, battleNetNames);
                delivered = true;
            }

            return input.SendWindowClick(point, battleNetNames, MouseButton.Left) || delivered;
        }
        catch (InvalidOperationException)
        {
            // Battle.net can replace its main window while switching cards/dialogs. The next
            // ready-loop pass re-resolves the window and safely retries the same state-machine step.
            return false;
        }
    }

    private string ResolveD2RInstallDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_config.D2RPath)
            && Path.GetDirectoryName(_config.D2RPath) is { Length: > 0 } directDirectory)
        {
            return Path.GetFullPath(directDirectory);
        }

        return Path.GetFullPath(
            string.IsNullOrWhiteSpace(_config.D2RInstallDirectory)
                ? DefaultD2RInstallDirectory
                : _config.D2RInstallDirectory);
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
        return BattleNetScreenClassifier.IsPrimaryActionReady(stats);
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

    internal static bool ContainsOnlyAuthorizedSettingsRepairProcesses(
        IReadOnlyCollection<D2RProcessGeneration> authorizedProcesses,
        IReadOnlyCollection<D2RProcessGeneration> currentProcesses)
    {
        var authorized = authorizedProcesses.ToHashSet();
        return currentProcesses.All(authorized.Contains);
    }

    private bool TryCaptureD2RProcessGenerations(
        out D2RProcessGeneration[] generations,
        out string error)
    {
        generations = [];
        error = "";
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var byProcessId = new Dictionary<int, D2RProcessGeneration>();
        try
        {
            foreach (var process in FindProcessesByNameOrWindowTitle(GetD2RProcessNames()))
            {
                using (process)
                {
                    var processId = process.Id;
                    if (byProcessId.ContainsKey(processId))
                    {
                        continue;
                    }

                    if (process.HasExited)
                    {
                        continue;
                    }

                    var startedUtc = TryGetProcessStartUtc(process);
                    if (startedUtc is null)
                    {
                        error =
                            $"Could not read the start time for D2R pid {processId}; refusing to guess which process generation owns the screen.";
                        return false;
                    }

                    byProcessId.Add(
                        processId,
                        new D2RProcessGeneration(processId, startedUtc.Value));
                }
            }
        }
        catch (Exception ex)
        {
            error = $"Could not enumerate D2R process generations: {ex.Message}";
            return false;
        }

        generations = byProcessId.Values
            .OrderBy(generation => generation.ProcessId)
            .ThenBy(generation => generation.StartedUtc)
            .ToArray();
        return true;
    }

    private async Task<SettingsRepairProcessStopResult> StopAuthorizedD2RForSettingsRepairAsync(
        IReadOnlyCollection<D2RProcessGeneration> authorizedProcesses,
        CancellationToken cancellationToken)
    {
        if (authorizedProcesses.Count > 1)
        {
            return new SettingsRepairProcessStopResult(
                false,
                "The settings repair was authorized against more than one D2R process generation; every client was left untouched.");
        }

        bool TryCaptureOnlyAuthorized(
            out D2RProcessGeneration[] currentProcesses,
            out string failure)
        {
            if (!TryCaptureD2RProcessGenerations(out currentProcesses, out var captureError))
            {
                failure =
                    $"Could not revalidate D2R process identity: {captureError} Every client was left untouched.";
                return false;
            }

            if (!ContainsOnlyAuthorizedSettingsRepairProcesses(authorizedProcesses, currentProcesses))
            {
                failure =
                    "A new D2R process generation appeared after settings repair was authorized; the new client was left untouched.";
                return false;
            }

            failure = "";
            return true;
        }

        if (!TryCaptureOnlyAuthorized(out var current, out var failure))
        {
            return new SettingsRepairProcessStopResult(false, failure);
        }

        if (current.Length == 0)
        {
            ClearD2RActivity();
            return new SettingsRepairProcessStopResult(
                true,
                "The authorized D2R process was already stopped.");
        }

        var expected = current[0];
        if (!TryOpenExactD2RProcess(expected, out var process, out var openError))
        {
            return new SettingsRepairProcessStopResult(false, openError);
        }

        var gracefulCloseSent = false;
        if (process is not null)
        {
            using (process)
            {
                try
                {
                    gracefulCloseSent = process.CloseMainWindow();
                }
                catch (InvalidOperationException)
                {
                    // The authorized generation exited between validation and WM_CLOSE. The
                    // generation scan below decides whether that is a clean stop or a restart.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // An unresponsive or windowless authorized process still gets the exact-PID
                    // hard-kill path below, after another generation check.
                }
            }
        }

        if (gracefulCloseSent)
        {
            for (var waitedSeconds = 0; waitedSeconds < 8; waitedSeconds += 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                if (!TryCaptureOnlyAuthorized(out current, out failure))
                {
                    return new SettingsRepairProcessStopResult(false, failure);
                }

                if (current.Length == 0)
                {
                    ClearD2RActivity();
                    return new SettingsRepairProcessStopResult(
                        true,
                        $"Closed authorized D2R pid {expected.ProcessId} gracefully.");
                }
            }
        }

        if (!TryCaptureOnlyAuthorized(out current, out failure))
        {
            return new SettingsRepairProcessStopResult(false, failure);
        }

        if (current.Length == 0)
        {
            ClearD2RActivity();
            return new SettingsRepairProcessStopResult(
                true,
                $"Authorized D2R pid {expected.ProcessId} exited before a hard kill was needed.");
        }

        expected = current[0];
        if (!TryOpenExactD2RProcess(expected, out process, out openError))
        {
            return new SettingsRepairProcessStopResult(false, openError);
        }

        if (process is not null)
        {
            using (process)
            {
                try
                {
                    process.Kill(entireProcessTree: false);
                    using var exitWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    exitWait.CancelAfter(TimeSpan.FromSeconds(10));
                    try
                    {
                        await process.WaitForExitAsync(exitWait.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // The final generation scan below reports a still-live exact process.
                    }
                }
                catch (InvalidOperationException)
                {
                    // It exited after the exact-generation handle was opened. Verify below.
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    return new SettingsRepairProcessStopResult(
                        false,
                        $"Could not kill authorized D2R pid {expected.ProcessId}: {ex.Message}");
                }
            }
        }

        if (!TryCaptureOnlyAuthorized(out current, out failure))
        {
            return new SettingsRepairProcessStopResult(false, failure);
        }

        if (current.Length > 0)
        {
            return new SettingsRepairProcessStopResult(
                false,
                $"Authorized D2R pid {expected.ProcessId} is still running after the exact-generation kill attempt.");
        }

        ClearD2RActivity();
        return new SettingsRepairProcessStopResult(
            true,
            $"Killed only the authorized D2R generation (pid {expected.ProcessId}, started {expected.StartedUtc:O}).");
    }

    private static bool TryOpenExactD2RProcess(
        D2RProcessGeneration expected,
        out Process? process,
        out string error)
    {
        process = null;
        error = "";
        try
        {
            process = Process.GetProcessById(expected.ProcessId);
            // Acquire and retain the process handle before checking StartTime. Keeping that handle
            // open prevents PID reuse between generation validation and CloseMainWindow/Kill.
            _ = process.Handle;
            if (process.HasExited)
            {
                process.Dispose();
                process = null;
                return true;
            }

            var actualStartUtc = TryGetProcessStartUtc(process);
            if (actualStartUtc is null)
            {
                error =
                    $"Could not re-read the start time for authorized D2R pid {expected.ProcessId}; the process was left untouched.";
                process.Dispose();
                process = null;
                return false;
            }

            if (actualStartUtc.Value != expected.StartedUtc)
            {
                error =
                    $"D2R pid {expected.ProcessId} now belongs to a different process generation; the new client was left untouched.";
                process.Dispose();
                process = null;
                return false;
            }

            return true;
        }
        catch (ArgumentException)
        {
            process?.Dispose();
            process = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or System.ComponentModel.Win32Exception
                                   or NotSupportedException)
        {
            process?.Dispose();
            process = null;
            error =
                $"Could not bind authorized D2R pid {expected.ProcessId} to its exact process generation: {ex.Message}";
            return false;
        }
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

    internal enum VisibleD2RState
    {
        NotRunning,
        Unknown,
        DiabloSplash,
        CharacterScreen,
        OfflineCharacterScreen,
        LobbyOrGame,
        InGame,
        GraphicsDeviceFailure,
        GammaCalibration
    }

    internal enum ReadyScreenState
    {
        Unknown,
        DiabloSplash,
        ConnectingToBattleNet,
        CharacterMenu,
        OfflineCharacterScreen,
        CharacterScreen,
        LobbyOrGame,
        InGame,
        CannotJoinCurrentCharacterDialog,
        GammaCalibration
    }

    internal enum GameEntryWaitResult
    {
        EnteredGame,
        ConnectionInterrupted,
        ErrorDialog,
        ReturnedToMenu,
        ReturnedToCharacterScreen,
        OfflineCharacterScreen,
        TimedOut,
        CurrentCharacterCannotJoin,
        GameIsFull
    }

    internal enum FollowAutoInGameRecoveryOutcome
    {
        NotInGame,
        LeftGame,
        DetectionInconclusive,
        LeaveFailed
    }

    internal enum InGameHudMatchKind
    {
        None,
        HudProfile,
        SaveAndExitMenu,
        Frame
    }

    internal enum FriendsAccordionAction
    {
        VerifyAfterOpeningDrawer,
        ExpandAfterOpeningDrawer,
        ExpandCollapsed,
        SkipExpanded
    }

    internal enum FollowFingerprintSelectionStatus
    {
        Selected,
        NoUsableMatch,
        Ambiguous
    }

    internal sealed record FriendRowFingerprintMatch(
        int Row,
        FriendFingerprintComparison Comparison);

    internal sealed record FollowFingerprintSelection(
        FollowFingerprintSelectionStatus Status,
        FriendRowFingerprintMatch? Match);

    private sealed record GameEntryAttemptResult(
        bool Entered,
        int DialogRetries,
        int ConnectionRetries,
        string Message,
        GameEntryWaitResult? FailureResult = null,
        bool DialogDismissed = false);

    private sealed record ReadyWaitResult(
        bool Ready,
        int Nudges,
        ReadyScreenState LastState,
        int TimeoutSeconds,
        bool ProcessExitedDuringWait = false,
        int LaunchAttempts = 0,
        int PlayClicks = 0,
        int GraphicsDeviceFailureDismissals = 0,
        string LastLaunchMessage = "(none)");

    private sealed class ReadyLaunchNudgeState
    {
        public DateTimeOffset NextLaunchRetryAt { get; set; }
        public DateTimeOffset NextPlayClickAt { get; set; }
        public DateTimeOffset NextGraphicsDeviceFailureProbeAt { get; set; }
        public int LaunchAttempts { get; set; }
        public int PlayClicks { get; set; }
        public int GraphicsDeviceFailureDismissals { get; set; }
        public bool GraphicsDeviceFailureExhausted { get; set; }
        public string LastLaunchMessage { get; set; } = "(none)";
        public required BattleNetInstallRepairState BattleNetRepair { get; init; }
    }

    private sealed class BattleNetInstallRepairState
    {
        public bool Authorized { get; set; }
        public bool ModalCancelled { get; set; }
        public bool FolderSubmitted { get; set; }
        public bool StartInstallClicked { get; set; }
        public bool Completed { get; set; }
        public int LocateAttempts { get; set; }
        public DateTimeOffset NextActionAt { get; set; }
    }

    internal readonly record struct ExpectedLobbyAfterSaveExit(
        long FollowAutoRunId,
        DateTimeOffset ProcessStartedUtc,
        DateTimeOffset ExpiresUtc);

    internal sealed record ActivitySnapshot(
        D2RActivityState State,
        DateTimeOffset? CharacterScreenIdleSinceUtc,
        DateTimeOffset? LastLobbyOrGameInteractionUtc,
        string? Reason);

    private sealed record InGameHudEvidence(
        ScreenRegionStats Health,
        ScreenRegionStats Mana,
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
