using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AgentCommon;
using Discord;
using Discord.WebSocket;

namespace D2RHost;

public sealed class DiscordBot
{
    private const string TemplateCreateButtonId = "d2r:template:create-game";
    private const string TemplateJoinButtonId = "d2r:template:join";
    private const string FollowAutoStartButtonId = "d2r:follow:auto-start";
    private const string FollowAutoStopButtonId = "d2r:follow:auto-stop";
    // The wire ID keeps its historical "save-exit" spelling so the button still routes on
    // messages posted by older host processes; the label users see is "Leave" (the standard
    // label for the save-exit-all action everywhere, matching GameSessionLeaveButtonId).
    private const string FollowAutoStopLeaveButtonId = "d2r:follow:auto-stop:save-exit";
    private const string FollowAutoStopFollowButtonId = "d2r:follow:auto-stop:follow";
    private const string FollowAutoStopQuitButtonId = "d2r:follow:auto-stop:quit";
    private const string FollowAutoStopSleepButtonId = "d2r:follow:auto-stop:sleep";
    private const string FollowAutoRemoveBotButtonId = "d2r:follow:bots:remove";
    private const string FollowAutoAddBotButtonId = "d2r:follow:bots:add";
    private const string FollowAutoPartyModeButtonId = "d2r:follow:party-mode";
    private const string FollowAutoParkButtonId = "d2r:follow:park";
    private const string FollowAutoJoinDelayButtonId = "d2r:follow:join-delay";
    private const string DcloneStopButtonId = "d2r:dclone:stop";
    private const string DclonePrivateButtonId = "d2r:dclone:private";
    private const string DclonePublicButtonId = "d2r:dclone:public";
    // What an HTTP caller is recorded as in the database's updated-by columns, where a Discord
    // user id would otherwise go. Deliberately not a number, so it can never be mistaken for one.
    private const string ApiActorId = "api";
    // How long an HTTP caller waits before it is told to poll instead. Sized above the slowest
    // single command (menu_ready's 420s budget) so an ordinary slow warmup still answers inline,
    // and the timeout only ever fires on something genuinely stuck.
    private static readonly TimeSpan ApiCommandTimeout = TimeSpan.FromSeconds(600);
    private const string GameSessionLeaveButtonId = "d2r:session:leave";
    private const string GameSessionQuitButtonId = "d2r:session:quit";
    private const string StartupFollowButtonId = "d2r:startup:follow";
    private const string StartupReadyButtonId = "d2r:startup:ready";
    private const string StartupQuitButtonId = "d2r:startup:quit";
    private const string StartupSleepButtonId = "d2r:startup:sleep";

    [Flags]
    private enum QuickActions
    {
        None = 0,
        Follow = 1,
        Ready = 2,
        Quit = 4,
        Sleep = 8
    }
    // 150s left ~0s margin over the agent's own internal retry budget on slower VM
    // hardware (Battle.net launch + splash-skip retries can legitimately take several
    // minutes), so the command was timing out moments before D2R would have reached
    // the character screen on its own. 420s gives real headroom above that budget.
    private static readonly TimeSpan ReadyCommandTimeout = TimeSpan.FromSeconds(420);
    private static readonly TimeSpan JoinPrepareCommandTimeout = TimeSpan.FromSeconds(35);
    // Both settings commands are file work, not UI automation: reading a few KB, or closing the
    // client and writing a few KB. The repair budget covers the agent's own quit-and-settle wait.
    private static readonly TimeSpan SettingsExportCommandTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettingsRepairCommandTimeout = TimeSpan.FromSeconds(90);
    private const int MaxSettingsDonorAttempts = 3;
    // Slow on purpose. A reset settings file is rare, agents report it on every heartbeat, and the
    // per-account rate limit means a client that keeps coming back broken is not retried anyway -
    // so there is nothing to gain from sweeping often, and a repair closes a live client.
    private static readonly TimeSpan SettingsRepairSweepInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SettingsRepairFirstSweepDelay = TimeSpan.FromMinutes(2);
    // Nothing is power-cycled on the strength of one sweep - the policy's own grace windows decide
    // that - so the first sweep only has to start the offline clocks. Delaying it past a fresh host
    // start keeps those clocks from beginning while the fleet's VMs are still booting.
    private static readonly TimeSpan StuckVmFirstSweepDelay = TimeSpan.FromMinutes(2);
    // Shorter than the full recovery's budget on purpose. This rung is only tried on a guest whose
    // integration services are answering, so if a restart is going to work at all it works on the
    // same timescale as an ordinary reboot - and every minute spent waiting past that is a minute
    // stolen from the power cycle that actually will.
    private static readonly TimeSpan StuckVmRestartReconnectTimeout = TimeSpan.FromMinutes(6);

    /// <summary>
    /// How long to wait for a <c>Restart-VM</c> to visibly take effect before calling it a no-op.
    /// Measured against the guest's uptime, which the hypervisor reports whether or not anything
    /// inside the guest is working, so a frozen guest cannot fake it.
    /// </summary>
    private static readonly TimeSpan StuckVmRestartTransitionTimeout = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan StuckVmRestartTransitionPollInterval = TimeSpan.FromSeconds(5);
    private const int JoinAutoDefaultIdleMinutes = 60;

    private readonly HostConfig _config;
    private readonly HostRuntimeOptions _runtime;
    private readonly FleetRegistry _registry;
    private readonly FleetHostOperations _hyperV;
    private readonly DiscordNotificationQueue _notifications;
    private readonly HostUpdateNotificationStore _hostUpdateNotifications;
    private readonly AppDb _db;
    private readonly FollowTemplateStore _followTemplates;
    private readonly ILogger<DiscordBot> _logger;
    private readonly MachineTelemetrySampler _hostTelemetry = new();
    // Per-account rate limit for replacing a VM's Settings.json from a fleet donor. Lives on the
    // bot, not on a follow-auto run, because the corruption survives runs - a fresh run must not
    // hand a client that already came back broken another two attempts.
    private readonly SettingsRepairTracker _settingsRepairs = new();
    private CancellationTokenSource? _settingsRepairSweepCts;
    private Task? _settingsRepairSweepTask;
    // Offline streaks and recovery budgets for the stuck-VM sweep. Also on the bot rather than on a
    // run, for the same reason as the repair tracker above: a wedged guest survives follow-auto
    // starting and stopping, and the sweep has to work when no run exists at all.
    private readonly StuckVmWatchdogTracker _stuckVms = new();
    private CancellationTokenSource? _stuckVmSweepCts;
    private Task? _stuckVmSweepTask;
    // Accounts whose guest is being power-cycled right now, by either the watchdog or follow-auto.
    // Both paths call the same recovery, so without this a sweep could start a second stop/start on
    // a VM follow-auto already had halfway through one.
    private readonly HashSet<string> _vmRecoveriesInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly DiscordSocketClient _client;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly SemaphoreSlim _notificationLock = new(1, 1);
    private IUserMessage? _activeSessionMessage;
    private string? _activeSessionGameName;
    private DateTimeOffset? _activeSessionStartedUtc;
    private int _activeSessionExpected;
    private int _activeSessionJoined;
    private string? _activeSessionRepresentativeAgentId;
    private bool _activeSessionMetricsEnabled;
    private bool _commandsRegistered;
    private bool _discordReady;
    private bool _startupMessageTaskStarted;
    private readonly SemaphoreSlim _startupMessageLock = new(1, 1);
    private IUserMessage? _startupMessage;
    private const QuickActions BootQuickActions = QuickActions.Follow | QuickActions.Ready | QuickActions.Sleep;

    // Quick-start buttons live on exactly one message at a time (the startup message on
    // boot/wake, or the newest ready/leave/quit completion follow-up). Both fields are
    // guarded by _startupMessageLock.
    private IUserMessage? _quickButtonsMessage;
    private QuickActions _quickActionsOffered = BootQuickActions;
    private string _startupIntro = DefaultStartupIntro;
    private int _startupPostInProgress;
    private const string DefaultStartupIntro = "D2RHost is alive and available.";
    private static readonly TimeSpan HostSleepPulseInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HostSleepGapThreshold = TimeSpan.FromMinutes(3);
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
    private bool _joinAutoMetricsEnabled;
    // /d2r dclone: every rostered bot opens its OWN game and sits in it, so that whichever game
    // Diablo Clone eventually walks into is one the operator can hand a name and password out
    // for. Modelled on join-auto's lifecycle (one CTS under a lock) rather than follow-auto's
    // lease machinery - there is no resume intent or local-restart journal to serialise against
    // here, because a park is rebuilt from scratch on the next start rather than resumed.
    private const string DcloneDefaultDifficulty = "hell";
    private static readonly TimeSpan DcloneCreateGameTimeout = TimeSpan.FromSeconds(210);
    private static readonly TimeSpan DcloneStatusTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan DcloneSaveExitTimeout = TimeSpan.FromSeconds(210);
    // How long a mode switch waits for the outgoing mode to finish unwinding before it gives up
    // on starting the new one. Both loops observe cancellation at their next await, so this only
    // ever runs out on a wedged agent command.
    private static readonly TimeSpan FleetModeSwitchUnwindTimeout = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _dcloneLock = new(1, 1);
    private CancellationTokenSource? _dcloneCts;
    private DcloneParkRun? _dcloneRun;
    // Completes once the current park's loop - and every create it started - has fully returned.
    private Task? _dcloneUnwound;
    private bool _dcloneMetricsEnabled;
    private IUserMessage? _dcloneMonitorMessage;
    private const int FollowAutoDefaultIdleMinutes = 60;
    private const int FollowAutoDefaultCheckSeconds = 5;
    private const int FollowAutoPostLeaveCheckSeconds = 2;
    private static readonly TimeSpan FollowAutoNodeRecoveryTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan FollowAutoNodeRecoveryPollInterval = TimeSpan.FromSeconds(2);
    // Hyper-V transitions are fast; this only has to cover a guest that ignores the shutdown
    // request long enough for Stop-VM -Force to turn it off the hard way.
    private static readonly TimeSpan FollowAutoVmPowerStateTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan FollowAutoVmPowerStatePollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FollowAutoLocalRestartFallbackDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FollowAutoStopActionWindow = TimeSpan.FromMinutes(2);
    // How many bots the active run should put in the leader's game. This also provides the
    // restart-journal barrier: once local recovery is armed a button change cannot land after the
    // persisted snapshot and disappear when the host comes back.
    private readonly FollowAutoTargetControl _followAutoTarget = new();
    private FollowAutoRosterAvailability _followAutoRosterAvailability = new(
        FollowAutoRosterPolicy.DefaultBotCount,
        OnlineAccountCount: 0,
        ConnectedBenchedCount: 0);
    // Per-game high-water rather than the latest count: a low/degraded later vantage must not
    // reopen +1 after this game was observed full. Confirmed advancement resets it.
    private readonly FollowAutoPlayerCountHighWater _followAutoLivePlayers = new();
    // Public mode's controller. It wants the opposite of the high-water above - the CURRENT party
    // size, because bots have to come back as humans leave - so it keeps its own reading and
    // guards against a bad sample with an agreement streak instead.
    private readonly FollowAutoPublicModeTracker _followAutoPublicMode = new();

    private readonly FollowAutoRosterAdjustmentGate _followAutoRosterGate = new();
    private readonly FollowAutoLifecycle _followAutoLifecycle = new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private readonly object _followAutoStopActionSync = new();
    private bool _followAutoStopActionsRequested;
    private CancellationTokenSource? _followAutoStopActionCts;
    private long _followAutoStopActionPromptId;
    private IUserMessage? _followAutoMonitorMessage;
    private DateTimeOffset? _followAutoStartedUtc;
    private int _followAutoGameNumber;
    private int _followAutoGamesCompleted;
    private int _followAutoJoined;
    private int _followAutoTotal;
    private bool _followAutoMetricsEnabled;
    // Redundant generic Ready probes are coalesced, while exact fallbacks wait and are never
    // dropped. A startup attempt can spend two minutes resolving a channel while an exact local-
    // restart fallback becomes due; the latter must run afterwards against the fresh journal.
    private readonly SemaphoreSlim _followAutoResumeGate = new(1, 1);

    // Multi-alt bind-in-game: the serialized nametag fingerprint this follow-auto run locked
    // onto, decided by the first pulse of the run that verifiably sees one of the bound
    // nametags (rolodex order breaks score ties). Content-keyed, never index-keyed - see
    // FollowLeaderMatch. Ordinal is display-only. Reset when a run starts and when the bind
    // list changes, so the next run re-resolves against whatever alt the operator is on.
    private string? _followAutoLockedNametag;
    private int? _followAutoLockedNametagOrdinal;

    // join-all's "join the last known game if it's recent" (issue #20, item 5) needs to mean
    // a game create-game-all/join-all actually acted on, not just whatever /d2r game set last had -
    // a 3-day-old manual /d2r game set shouldn't be silently (re)joined.
    private static readonly TimeSpan ActiveGameFreshness = TimeSpan.FromHours(1);

    public DiscordBot(
        HostConfig config,
        HostRuntimeOptions runtime,
        FleetRegistry registry,
        FleetHostOperations hyperV,
        DiscordNotificationQueue notifications,
        HostUpdateNotificationStore hostUpdateNotifications,
        AppDb db,
        FollowTemplateStore followTemplates,
        ILogger<DiscordBot> logger)
    {
        _config = config;
        _runtime = runtime;
        _registry = registry;
        _hyperV = hyperV;
        _notifications = notifications;
        _hostUpdateNotifications = hostUpdateNotifications;
        _db = db;
        _followTemplates = followTemplates;
        _logger = logger;
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds
        });
        _client.Log += OnDiscordLogAsync;
        _client.Ready += OnReadyAsync;
        _client.SlashCommandExecuted += OnSlashCommandAsync;
        _client.ButtonExecuted += OnButtonExecutedAsync;
        _notifications.MessageQueued += OnDiscordNotificationQueued;
        _registry.ConnectivityChanged += OnAgentConnectivityChanged;
    }

    public async Task StartAsync()
    {
        // Settings repair is a fleet/master responsibility, not a Discord feature. Start it before
        // the headless-mode return so masters with disableDiscord=true still recover their VMs.
        _settingsRepairSweepCts ??= new CancellationTokenSource();
        _settingsRepairSweepTask ??= Task.Run(
            () => RunSettingsRepairSweepAsync(_settingsRepairSweepCts.Token),
            CancellationToken.None);

        // Same reasoning as the repair sweep: a guest that stops running its agent is a fleet
        // problem, not a Discord feature, so it must still be recovered on a headless master.
        _stuckVmSweepCts ??= new CancellationTokenSource();
        _stuckVmSweepTask ??= Task.Run(
            () => RunStuckVmSweepAsync(_stuckVmSweepCts.Token),
            CancellationToken.None);

        if (_config.DisableDiscord)
        {
            _logger.LogWarning("Discord is disabled.");
            return;
        }

        await _client.LoginAsync(TokenType.Bot, _config.DiscordToken);
        await _client.StartAsync();
        _ = Task.Run(RunHostWakeMonitorAsync);
    }

    /// <summary>
    /// Repairs any VM that reports a reset Settings.json, whether or not follow-auto is running.
    /// </summary>
    /// <remarks>
    /// The repair used to hang off follow-auto's warmup-failure path alone, so a client that reset
    /// its settings outside a run sat on the gamma calibration screen indefinitely - visible in
    /// /d2r status, and fixed by nothing. Agents report the state on every heartbeat, so a sweep
    /// over the fleet's last status is all it takes to make the recovery autonomous. The per-account
    /// rate limit is shared with the follow-auto path, so the two cannot double up on one client.
    /// </remarks>
    private async Task RunSettingsRepairSweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SettingsRepairFirstSweepDelay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SweepSettingsRepairsAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The D2R settings repair sweep failed.");
            }

            try
            {
                await Task.Delay(SettingsRepairSweepInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Power-cycles any VM that has stopped running its agent while Hyper-V still reports it
    /// powered on, whether or not follow-auto is running.
    /// </summary>
    /// <remarks>
    /// The gap this closes: nothing was watching for a guest that goes quiet. Follow-auto's ladders
    /// only count failures it can observe, and an account whose agent never reconnects is filtered
    /// out as offline before those ladders ever see it - so the two most common shapes of this,
    /// a guest wedged partway through boot and a guest that came back from a host resume without
    /// its agent, could sit untouched for a whole session. When follow-auto did eventually notice,
    /// it took nine failed checks to reach the same power cycle this reaches directly.
    /// </remarks>
    private async Task RunStuckVmSweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(StuckVmFirstSweepDelay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var lastTick = DateTimeOffset.UtcNow;
        var lastSweepDuration = TimeSpan.Zero;
        while (!cancellationToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(_config.StuckVmWatchdog.SweepIntervalSeconds);

            // This sweep keeps its own resume detector rather than relying on the Discord wake
            // monitor's, because that one does not run on a headless master - and a missed reset
            // here is the worst bug this feature can have: every offline clock would carry the
            // whole suspended duration and the first sweep back would cycle the entire fleet at
            // once. A tick that arrives far later than its own interval is the same evidence the
            // wake monitor uses, and needs nothing from Discord.
            //
            // The window measured is the WHOLE previous iteration, sweep included. Measuring only
            // the delay left a hole: a machine that suspends while a sweep is running resumes with
            // the gap already absorbed, and on a headless master nothing else would have caught it.
            // The allowance therefore carries the previous sweep's own duration, timed with a
            // monotonic clock so a suspension inside it cannot be mistaken for work.
            //
            // The asymmetry is deliberate. A false positive - a tick delayed because a recovery on
            // one guest ran long - only makes the watchdog more patient. A false negative cycles
            // healthy VMs. So this errs towards resetting.
            var now = DateTimeOffset.UtcNow;
            var gap = now - lastTick;
            if (gap > interval + lastSweepDuration + HostSleepGapThreshold)
            {
                _logger.LogInformation(
                    "Stuck-VM sweep saw a {Gap} gap between ticks; clearing every offline clock before sweeping.",
                    gap);
                _stuckVms.Reset();
            }

            lastTick = now;
            var sweepClock = Stopwatch.StartNew();
            try
            {
                await SweepStuckVmsAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The stuck-VM sweep failed.");
            }

            lastSweepDuration = sweepClock.Elapsed;
            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    internal async Task<int> SweepStuckVmsAsync(CancellationToken cancellationToken)
    {
        var options = BuildStuckVmWatchdogOptions();
        if (!options.Enabled)
        {
            // The policy would refuse every guest anyway, but only after this sweep had already
            // spent a Hyper-V round trip per silent VM asking about it. Disabled has to mean the
            // pre-existing behavior including its cost, not the same cost for a fixed answer.
            return 0;
        }

        var policy = new StuckVmWatchdogPolicy(options);
        var recovered = 0;

        // One connectivity snapshot for the whole sweep. FleetRegistry rebuilds the entire fleet -
        // every worker's inventory JSON included - on each Accounts/GetAgent call, so asking it
        // per account cost a full rebuild per VM per tick to answer one bit.
        var (onlineAccounts, offlineAccounts) = GetAccountEntriesByConnectivity();
        var sweptAt = DateTimeOffset.UtcNow;
        foreach (var entry in onlineAccounts)
        {
            // Staying connected for as long as the silence that would have condemned it is the bar
            // for refunding a guest's recovery budget. Anything cheaper - refunding on the first
            // sighting - lets a guest whose agent connects and dies again reclaim its whole
            // allowance every few minutes, so maxRecoveriesPerVm would bound nothing.
            _stuckVms.RecordOnline(entry.Key, sweptAt, options.AgentOfflineGrace);
        }

        foreach (var (accountKey, account) in offlineAccounts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Silence the host could not observe is not evidence about the guest. Every VM agent on
            // a worker node reaches the master through that worker's own process, so a worker that
            // restarts - a self-update is enough - takes all of its agents offline with it. Counting
            // that would power-cycle a whole node's healthy guests the moment the worker came back,
            // before its agents had finished re-registering.
            if (!_hyperV.IsNodeOnline(_hyperV.ResolveNodeId(account)))
            {
                _stuckVms.RecordUnobserved(accountKey);
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var offlineFor = _stuckVms.RecordOffline(accountKey, now);

            // Everything below costs a PowerShell process on the owning node, so the cheapest
            // precondition is checked first. A VM whose agent only just went quiet cannot be
            // actionable yet no matter what Hyper-V would have said about it.
            if (offlineFor < policy.ProbeAfter)
            {
                continue;
            }

            // Follow-auto may already be cycling this exact guest. Yield rather than queue behind
            // it: its recovery ends with the same wait for the same agent, so if it works there is
            // nothing left for the watchdog to do, and if it does not the next sweep still gets a
            // turn.
            if (IsVmRecoveryInFlight(accountKey))
            {
                continue;
            }

            var vmName = ResolveVmName(account);
            if (string.IsNullOrWhiteSpace(vmName))
            {
                continue;
            }

            var args = JsonSerializer.SerializeToElement(new { accountKey, vmName });
            var status = await SendVmPowerCommandAsync(account, "vm_status", args, cancellationToken);
            if (!status.Ok)
            {
                // The owning node went unreachable between the check above and here, or Hyper-V
                // would not answer. Either way the guest was not observed, so the streak restarts
                // for the same reason it does for an offline node - a node-level outage is a
                // separate problem with its own reporting.
                _stuckVms.RecordUnobserved(accountKey);

                // But say so out loud. The node passed the online check moments ago, so this is not
                // a node outage: the name the fleet is asking about does not resolve on the host
                // that is supposed to own it - a renamed guest, a VM moved to another host, a wrong
                // vmName in config. Restarting the clock is correct and also means this VM can never
                // accumulate a streak, so it can never be recovered and no ladder will ever mention
                // it. Reported once per incident because the sweep runs every minute, and no amount
                // of waiting is going to fix it.
                if (_stuckVms.TryClaimUnreadableNotice(accountKey))
                {
                    _notifications.Enqueue(
                        $"{FormatAccountDisplayName(accountKey, account)}: {_hyperV.ResolveNodeId(account)} is online but "
                            + $"cannot read VM \"{vmName}\" ({status.Message}). Nothing can recover this guest until that "
                            + "name resolves on that node - check whether the VM was renamed or moved, or fix vmName in "
                            + "host config.");
                }

                _logger.LogWarning(
                    "Stuck-VM sweep could not read {VmName} for {AccountKey} on {NodeId}: {Message}",
                    vmName,
                    accountKey,
                    _hyperV.ResolveNodeId(account),
                    status.Message);
                continue;
            }

            _stuckVms.ClearUnreadableNotice(accountKey);

            var powerState = TryReadVmPowerState(status, out var observed) ? observed : null;
            // Held rather than passed inline: this is also the baseline the in-place restart
            // proves itself against, since a guest that actually rebooted comes back with a
            // smaller uptime than the one measured here.
            var uptimeBeforeRecovery = TryReadVmUptime(status);

            // Ask the screen before asking the health signals, because on this failure every health
            // signal agrees - and they are all wrong. See VmBootLogoPolicy: a guest frozen on the
            // boot logo for an hour reported Heartbeat OK, KvpOSName "Windows 10 Pro", Status
            // "Operating normally" and 21% CPU, identical field for field to a sibling that was
            // healthy and in-game. Left to the ladder below, that guest is read as heartbeat-OK and
            // handed an in-place restart forever, because nothing it can be asked knows it is stuck.
            if (await TryRecoverBootLoggedVmAsync(accountKey, account, vmName, args, powerState, cancellationToken))
            {
                continue;
            }

            var assessment = policy.Assess(
                agentOnline: false,
                powerState,
                TryReadVmHeartbeat(status),
                uptimeBeforeRecovery,
                offlineFor,
                _stuckVms.RecoveriesUsed(accountKey));

            if (assessment.Verdict == VmWatchdogVerdict.GiveUp)
            {
                if (_stuckVms.TryClaimGiveUpNotice(accountKey))
                {
                    _notifications.Enqueue(
                        $"{FormatAccountDisplayName(accountKey, account)}: still offline after "
                            + $"{_config.StuckVmWatchdog.MaxRecoveriesPerVm} watchdog power cycle(s) of {vmName}. "
                            + "Not cycling it again - this needs a look.");
                    _logger.LogWarning(
                        "Stuck-VM watchdog gave up on {AccountKey} ({VmName}): {Reason}",
                        accountKey,
                        vmName,
                        assessment.Reason);
                }

                continue;
            }

            if (assessment.Verdict is not (VmWatchdogVerdict.Restart or VmWatchdogVerdict.PowerCycle))
            {
                _logger.LogDebug(
                    "Stuck-VM sweep is holding off on {AccountKey} ({VmName}): {Reason}",
                    accountKey,
                    vmName,
                    assessment.Reason);
                continue;
            }

            if (!TryBeginVmRecovery(accountKey))
            {
                continue;
            }

            _logger.LogWarning(
                "Stuck-VM watchdog is {Action} {VmName} for {AccountKey}: {Reason}",
                assessment.Verdict == VmWatchdogVerdict.Restart ? "restarting" : "power-cycling",
                vmName,
                accountKey,
                assessment.Reason);

            // Charged before the attempt, not after. A recovery that throws or is cancelled partway
            // still consumed a power cycle on that guest, and a budget that only counted clean
            // completions would let a reliably-crashing recovery loop forever.
            _stuckVms.RecordRecoveryAttempt(accountKey, DateTimeOffset.UtcNow);
            CommandResult recovery;
            var restartNote = "";
            try
            {
                // A guest whose integration services still answer gets the cheap rung first: one
                // Restart-VM, no Off state to get stranded in. Only its failure buys the full
                // stop/start, and that escalation is charged to the same attempt - the two rungs
                // are one recovery of one guest, not two.
                if (assessment.Verdict == VmWatchdogVerdict.Restart)
                {
                    var restart = await RestartStuckVmAsync(
                        accountKey,
                        account,
                        vmName,
                        args,
                        uptimeBeforeRecovery,
                        cancellationToken);
                    if (restart.Ok)
                    {
                        recovered++;
                        _notifications.Enqueue(
                            $"{FormatAccountDisplayName(accountKey, account)}: {restart.Message}");
                        continue;
                    }

                    restartNote = $"An in-place restart was tried first and did not bring it back: {restart.Message} ";
                }

                recovery = await RecoverVmAsync(accountKey, account, cancellationToken);
            }
            finally
            {
                // The clock is restarted here as well as when the attempt was charged. A recovery
                // runs for as long as half an hour - an in-place restart plus a full stop/start,
                // each waiting out its own reconnect budget - so by the time it returns the streak
                // charged at the start already reads far past the grace window, and the next sweep
                // a minute later would spend the rest of the budget on a guest that has been
                // booting for sixty seconds.
                _stuckVms.RestartClock(accountKey, DateTimeOffset.UtcNow);
                EndVmRecovery(accountKey);
            }

            var displayName = FormatAccountDisplayName(accountKey, account);
            if (recovery.Ok)
            {
                recovered++;
                _notifications.Enqueue(
                    $"{displayName}: {vmName} was powered on but its agent had stopped answering "
                        + $"({assessment.Reason}). {restartNote}{recovery.Message}");
            }
            else
            {
                _notifications.Enqueue(
                    $"{displayName}: tried to recover {vmName} because {assessment.Reason}, and it did not "
                        + $"come back. {restartNote}{recovery.Message}");
            }
        }

        return recovered;
    }

    /// <summary>
    /// The cheap rung of the watchdog's ladder: one <c>Restart-VM</c> on a guest that is still
    /// answering the hypervisor, then a wait for its agent.
    /// </summary>
    /// <remarks>
    /// This is deliberately not a stop followed by a start. The guest keeps its virtual power the
    /// whole way through, so there is no Off state in which a failed Start-VM could strand the VM -
    /// which is the failure mode the full recovery has to guard against with confirm-Off polling
    /// and a hard-cut escalation. It is also the only rung that can work at all without turning a
    /// machine off, so it is worth trying before one that does.
    ///
    /// It can still fail, and silently: Restart-VM is routed through the same guest integration
    /// services as a graceful stop, so a guest that is answering heartbeats but wedged above the
    /// kernel may take the request and never act on it. That is why success is judged by the agent
    /// coming back rather than by the cmdlet returning, and why the caller escalates on failure.
    /// </remarks>
    private async Task<CommandResult> RestartStuckVmAsync(
        string accountKey,
        AccountConfig account,
        string vmName,
        JsonElement args,
        TimeSpan? uptimeBeforeRestart,
        CancellationToken cancellationToken)
    {
        var restart = await SendVmPowerCommandAsync(account, "vm_reboot", args, cancellationToken);
        if (!restart.Ok)
        {
            return CommandResult.Failure($"Restart-VM for {vmName} failed: {restart.Message}");
        }

        // A cmdlet that did not error is not a guest that restarted. Restart-VM goes through the
        // same integration services that a wedged guest has already stopped answering, so it can
        // return cleanly having done nothing at all - and this rung is chosen precisely when the
        // guest looks answerable, which is exactly when that mistake is easiest to make. Without
        // this check the only evidence either way was the agent reconnecting, so a restart that
        // never happened was indistinguishable from one that happened and did not help, and both
        // spent the full reconnect timeout before anything escalated.
        var cycled = await WaitForVmRestartAsync(
            account,
            vmName,
            args,
            uptimeBeforeRestart,
            cancellationToken);
        if (cycled == VmRestartObservation.DidNotRestart)
        {
            return CommandResult.Failure(
                $"Restart-VM reported success but {vmName}'s uptime never reset within "
                    + $"{StuckVmRestartTransitionTimeout.TotalSeconds:N0}s, so the guest did not actually restart. "
                    + "Escalating rather than waiting out the agent reconnect on a guest that never rebooted");
        }

        // Supervised, not slept. This used to be a flat six-minute wait for the agent, which spent
        // the whole budget the same way whether the guest was mid-boot or frozen on the Windows
        // logo - and a guest that is never coming back on its own looks identical to a slow one
        // right up until the timeout expires. Ask the hypervisor what is actually happening on
        // every poll instead, and stop the moment the answer is "the boot is wedged", so the
        // remaining budget is spent on the power cycle that can fix it rather than on waiting.
        //
        // The verdict comes from VmHangRecoveryPolicy - the same policy the full recovery uses for
        // the same question - so a guest whose heartbeat still answers is never condemned here.
        // The escalation is handed to the caller rather than cut from here: RecoverVmAsync is what
        // owns confirm-Off polling and the hard cut, and this rung deliberately never turns a
        // machine off.
        var restartedAt = DateTimeOffset.UtcNow;
        var deadline = restartedAt + StuckVmRestartReconnectTimeout;
        var hangPolicy = new VmHangRecoveryPolicy(BuildVmHangRecoveryOptions());
        var lastAssessment = "the agent simply had not reconnected yet";
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsAccountOnline(accountKey))
            {
                return CommandResult.Success(
                    $"{vmName} was powered on but its agent had stopped answering. Restarting the guest in place "
                        + "brought it back; no power cycle was needed.");
            }

            var probe = await SendVmPowerCommandAsync(account, "vm_status", args, cancellationToken);
            var powerState = probe.Ok && TryReadVmPowerState(probe, out var observed) ? observed : null;
            var heartbeat = probe.Ok ? TryReadVmHeartbeat(probe) : VmHeartbeatStatus.Unknown;
            var bootAssessment = hangPolicy.AssessBootHang(
                powerState,
                heartbeat,
                DateTimeOffset.UtcNow - restartedAt,
                hardPowerCutsUsed: 0);
            lastAssessment = bootAssessment.Reason;
            if (bootAssessment.Verdict == VmHangVerdict.HardPowerCut)
            {
                return CommandResult.Failure(
                    $"{vmName} restarted, but its boot is wedged ({bootAssessment.Reason}). Escalating now instead of "
                        + $"waiting out the remaining {(deadline - DateTimeOffset.UtcNow).TotalMinutes:N0} minutes of "
                        + "reconnect budget");
            }

            await Task.Delay(FollowAutoNodeRecoveryPollInterval, cancellationToken);
        }

        return CommandResult.Failure(
            $"its agent did not reconnect within {StuckVmRestartReconnectTimeout.TotalMinutes:N0} minutes "
                + $"of the restart ({lastAssessment})");
    }

    internal enum VmRestartObservation
    {
        /// <summary>The guest's uptime reset, so it genuinely went down and came back.</summary>
        Restarted,

        /// <summary>Uptime never reset: the cmdlet returned but the guest kept running.</summary>
        DidNotRestart,

        /// <summary>No usable uptime to compare, so the restart can be neither proven nor denied.</summary>
        Unverifiable
    }

    /// <summary>
    /// Watches a guest's uptime to decide whether a <c>Restart-VM</c> actually restarted it.
    /// </summary>
    /// <remarks>
    /// Uptime is the right signal because the hypervisor measures it from outside: a guest frozen
    /// on the Windows boot logo reports it just as accurately as a healthy one, and it is the only
    /// reading that distinguishes "restarted" from "still the same boot". Power state cannot do
    /// this job - an in-place restart is not required to pass through any state this poll would be
    /// fast enough to catch, so a guest that never moved and a guest that restarted between two
    /// polls both read Running.
    ///
    /// A missing baseline is reported as Unverifiable rather than as a failure. Not being able to
    /// prove a restart happened is not evidence that it did not, and escalating to a power cut on
    /// an absent reading would turn a diagnostic gap into an outage.
    /// </remarks>
    private async Task<VmRestartObservation> WaitForVmRestartAsync(
        AccountConfig account,
        string vmName,
        JsonElement args,
        TimeSpan? uptimeBeforeRestart,
        CancellationToken cancellationToken)
    {
        if (uptimeBeforeRestart is not { } before)
        {
            return VmRestartObservation.Unverifiable;
        }

        var deadline = DateTimeOffset.UtcNow + StuckVmRestartTransitionTimeout;
        var sawAnyUptime = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(StuckVmRestartTransitionPollInterval, cancellationToken);

            var status = await SendVmPowerCommandAsync(account, "vm_status", args, cancellationToken);
            if (!status.Ok)
            {
                continue;
            }

            if (TryReadVmUptime(status) is not { } now)
            {
                continue;
            }

            sawAnyUptime = true;
            if (now < before)
            {
                _logger.LogInformation(
                    "{VmName} restarted in place: uptime fell from {Before} to {After}.",
                    vmName,
                    before,
                    now);
                return ClassifyRestartOutcome(sawAnyUptime: true, uptimeReset: true);
            }
        }

        return ClassifyRestartOutcome(sawAnyUptime, uptimeReset: false);
    }

    /// <summary>
    /// The terminal verdict once the transition window has closed without an uptime reset.
    /// </summary>
    /// <remarks>
    /// Split out because the distinction it draws is the whole safety property. Never getting a
    /// reading at all is a reachability problem - the owning node stopped answering, Hyper-V would
    /// not report - and that is not evidence the guest ignored the restart. Reporting it as
    /// DidNotRestart would escalate a guest to a power cycle on the strength of the host's own
    /// blindness, so silence and a contradicted restart are deliberately different answers.
    /// </remarks>
    internal static VmRestartObservation ClassifyRestartOutcome(bool sawAnyUptime, bool uptimeReset)
    {
        if (uptimeReset)
        {
            return VmRestartObservation.Restarted;
        }

        return sawAnyUptime ? VmRestartObservation.DidNotRestart : VmRestartObservation.Unverifiable;
    }

    /// <summary>
    /// Reads the guest's console and, if it has been sitting on the Windows boot logo longer than a
    /// real boot takes, cuts its power and starts it again. Returns whether it took the guest.
    /// </summary>
    /// <remarks>
    /// Deliberately a hard cut rather than a stop or a restart. A guest wedged this way answers its
    /// integration services - that is exactly why no other signal catches it - so a graceful stop
    /// may well be accepted and change nothing, and a restart hands it back to the same boot that
    /// already hung. Only removing power resolves it.
    ///
    /// Nothing here acts on a single frame. The tracker clears the clock on any frame that is not
    /// the logo, and on any frame that could not be captured at all, so a guest booting normally
    /// cannot accumulate a streak however often it is sampled mid-boot.
    /// </remarks>
    private async Task<bool> TryRecoverBootLoggedVmAsync(
        string accountKey,
        AccountConfig account,
        string vmName,
        JsonElement args,
        string? powerState,
        CancellationToken cancellationToken)
    {
        var options = BuildBootLogoWatchdogOptions();
        if (!options.Enabled)
        {
            return false;
        }

        // Only a powered-on guest has a console worth reading, and anything else is a transition
        // somebody already started.
        if (!string.Equals(powerState?.Trim(), "Running", StringComparison.OrdinalIgnoreCase))
        {
            _stuckVms.ClearBootLogo(accountKey);
            return false;
        }

        var console = await SendVmPowerCommandAsync(account, "vm_console", args, cancellationToken);
        var frame = TryReadVmConsoleFrame(console);
        if (frame is null || !VmBootLogoPolicy.IsWindowsBootLogo(frame))
        {
            _stuckVms.ClearBootLogo(accountKey);
            return false;
        }

        var heldFor = _stuckVms.RecordBootLogo(accountKey, DateTimeOffset.UtcNow);
        var assessment = new VmBootLogoPolicy(options).Assess(
            frame,
            heldFor,
            _stuckVms.BootLogoCutsUsed(accountKey));

        if (assessment.Verdict == VmBootLogoVerdict.GiveUp)
        {
            if (_stuckVms.TryClaimGiveUpNotice(accountKey))
            {
                _notifications.Enqueue(
                    $"{FormatAccountDisplayName(accountKey, account)}: {vmName} keeps coming back to the Windows boot "
                        + $"logo after {options.MaxHardPowerCuts} power cut(s). Not cutting it again - this needs a look.");
            }

            return true;
        }

        if (assessment.Verdict != VmBootLogoVerdict.HardPowerCut)
        {
            // KeepWatching still counts as taking the guest: it is demonstrably mid-boot, and the
            // ladder below would otherwise spend a recovery attempt on a machine that is simply not
            // finished starting.
            return assessment.Verdict == VmBootLogoVerdict.KeepWatching;
        }

        if (!TryBeginVmRecovery(accountKey))
        {
            return true;
        }

        try
        {
            _logger.LogWarning(
                "Boot-logo watchdog is cutting power to {VmName} for {AccountKey}: {Reason}",
                vmName,
                accountKey,
                assessment.Reason);
            _stuckVms.RecordBootLogoCut(accountKey, DateTimeOffset.UtcNow);

            var cut = await HardPowerCutAsync(account, args, vmName, assessment.Reason, cancellationToken);
            if (!cut.Ok)
            {
                _notifications.Enqueue(
                    $"{FormatAccountDisplayName(accountKey, account)}: {vmName} has been frozen on the Windows boot "
                        + $"logo, and cutting its power failed: {cut.Message}");
                return true;
            }

            var start = await SendVmPowerCommandAsync(account, "vm_start", args, cancellationToken);
            if (!start.Ok)
            {
                _notifications.Enqueue(
                    $"{FormatAccountDisplayName(accountKey, account)}: {vmName} was powered off after freezing on the "
                        + $"Windows boot logo, but starting it again failed: {start.Message}");
                return true;
            }

            _notifications.Enqueue(
                $"{FormatAccountDisplayName(accountKey, account)}: {vmName} was frozen on the Windows boot logo "
                    + "(which reports as a perfectly healthy guest to every other signal), so its power was cut and "
                    + "it was started again.");
            return true;
        }
        finally
        {
            _stuckVms.RestartClock(accountKey, DateTimeOffset.UtcNow);
            EndVmRecovery(accountKey);
        }
    }

    private VmBootLogoOptions BuildBootLogoWatchdogOptions()
    {
        var configured = _config.BootLogoWatchdog;
        return new VmBootLogoOptions(
            configured.Enabled,
            TimeSpan.FromSeconds(Math.Max(configured.StuckAfterSeconds, 120)),
            Math.Clamp(configured.MaxHardPowerCuts, 1, 5));
    }

    /// <summary>
    /// Decodes a vm_console reply into frame statistics, or null when no frame came back.
    /// </summary>
    /// <remarks>
    /// Every failure here is null rather than an exception, and null means "learned nothing" rather
    /// than "the guest is fine" - the caller clears the logo clock on it, so an unreadable console
    /// can only ever delay a cut, never cause one. A worker too old to know vm_console lands here
    /// too, and degrades to the previous behaviour instead of breaking the sweep.
    /// </remarks>
    internal static VmConsoleFrameStats? TryReadVmConsoleFrame(CommandResult console)
    {
        if (!console.Ok || string.IsNullOrWhiteSpace(console.Message))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(console.Message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Base64", out var base64)
                || base64.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("Width", out var width)
                || !root.TryGetProperty("Height", out var height))
            {
                return null;
            }

            var pixels = Convert.FromBase64String(base64.GetString() ?? "");
            return VmConsoleFrame.FromRgb565(pixels, width.GetInt32(), height.GetInt32());
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The Hyper-V VM behind an account, falling back to its agent id for the fleets that named the
    /// two the same. Empty means the account has no guest anything here can power-cycle.
    /// </summary>
    private static string ResolveVmName(AccountConfig account)
    {
        return string.IsNullOrWhiteSpace(account.VmName) ? account.AgentId : account.VmName!;
    }

    private bool IsAccountOnline(string accountKey)
    {
        return GetAccountEntriesByConnectivity().Online
            .Any(entry => string.Equals(entry.Key, accountKey, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> WaitForAgentReconnectAsync(
        string accountKey,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsAccountOnline(accountKey))
            {
                return true;
            }

            await Task.Delay(FollowAutoNodeRecoveryPollInterval, cancellationToken);
        }

        return false;
    }

    private StuckVmWatchdogOptions BuildStuckVmWatchdogOptions()
    {
        var configured = _config.StuckVmWatchdog;
        return new StuckVmWatchdogOptions(
            configured.Enabled,
            TimeSpan.FromSeconds(configured.AgentOfflineGraceSeconds),
            TimeSpan.FromSeconds(configured.NoHeartbeatEvidenceGraceSeconds),
            TimeSpan.FromSeconds(configured.MinimumVmUptimeSeconds),
            configured.MaxRecoveriesPerVm);
    }

    /// <summary>
    /// Claims the exclusive right to power-cycle one account's guest. Both the watchdog sweep and
    /// follow-auto's failure ladders end at the same stop/start, and two of those interleaved on
    /// one VM would have the second one's Start-VM land on a guest the first had just turned off.
    /// </summary>
    private bool TryBeginVmRecovery(string accountKey)
    {
        lock (_vmRecoveriesInFlight)
        {
            return _vmRecoveriesInFlight.Add(accountKey);
        }
    }

    private void EndVmRecovery(string accountKey)
    {
        lock (_vmRecoveriesInFlight)
        {
            _vmRecoveriesInFlight.Remove(accountKey);
        }
    }

    private bool IsVmRecoveryInFlight(string accountKey)
    {
        lock (_vmRecoveriesInFlight)
        {
            return _vmRecoveriesInFlight.Contains(accountKey);
        }
    }

    /// <summary>
    /// Takes the recovery latch for a caller that must not give up if the watchdog happens to hold
    /// it, waiting out the sweep's cycle rather than skipping its own escalation.
    /// </summary>
    private async Task<bool> WaitToBeginVmRecoveryAsync(
        string accountKey,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + FollowAutoNodeRecoveryTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (TryBeginVmRecovery(accountKey))
            {
                return true;
            }

            await Task.Delay(FollowAutoVmPowerStatePollInterval, cancellationToken);
        }

        return false;
    }

    internal async Task<int> SweepSettingsRepairsAsync(CancellationToken cancellationToken)
    {
        var repaired = 0;
        foreach (var (accountKey, account) in _registry.Accounts
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var agent = _registry.GetAgent(account.AgentId);
            if (!HasStatusFromCurrentAgentConnection(agent))
            {
                continue;
            }

            _settingsRepairs.ObserveCurrentStatus(
                accountKey,
                agent!.LastStatusJson,
                DateTimeOffset.UtcNow);
            var recovery = _settingsRepairs.RecoveryFor(accountKey);
            if (recovery.Stage == SettingsRecoveryStage.None)
            {
                continue;
            }

            if (recovery.Stage == SettingsRecoveryStage.ReadyRequired)
            {
                var readyRetry = await SendReadyAfterSettingsRepairAsync(accountKey, account, cancellationToken);
                if (readyRetry.Ok)
                {
                    var donorDescription = string.IsNullOrWhiteSpace(recovery.DonorAccountKey)
                        ? "a fleet donor"
                        : recovery.DonorAccountKey;
                    _notifications.Enqueue(
                        $"{FormatAccountDisplayName(accountKey, account)}: restarted successfully after its Settings.json "
                            + $"was replaced from {donorDescription}.");
                }
                else
                {
                    _logger.LogWarning(
                        "Sweep could not restart {AccountKey} after settings repair: {Message}",
                        accountKey,
                        readyRetry.Message);
                }

                continue;
            }

            var repair = await TryRepairSettingsFromFleetAsync(
                accountKey,
                account,
                agent.LastStatusJson,
                DateTimeOffset.UtcNow,
                agent.ConnectedAt,
                agent.StatusReceivedAt,
                SettingsRepairPolicy.RepairEvidenceToken(agent.LastStatusJson),
                cancellationToken);
            if (repair is null)
            {
                continue;
            }

            if (!repair.Ok)
            {
                _logger.LogWarning(
                    "Sweep could not repair {AccountKey}'s D2R settings: {Message}", accountKey, repair.Message);
                continue;
            }

            if (!repair.CopyApplied)
            {
                if (_settingsRepairs.RecoveryFor(accountKey).Stage == SettingsRecoveryStage.ReadyRequired)
                {
                    var resumedReady = await SendReadyAfterSettingsRepairAsync(accountKey, account, cancellationToken);
                    _notifications.Enqueue(
                        $"{FormatAccountDisplayName(accountKey, account)}: resumed a settings repair that had already "
                            + $"copied successfully: {resumedReady.Message}");
                }

                continue;
            }

            repaired++;
            // A failed ready leaves the tracker in ReadyRequired. The next sweep retries only this
            // phase, without copying the file again or consuming another replacement attempt.
            var readied = await SendReadyAfterSettingsRepairAsync(accountKey, account, cancellationToken);
            _notifications.Enqueue(
                $"{FormatAccountDisplayName(accountKey, account)}: D2R had reset its own Settings.json (first-run gamma "
                    + $"screen). Replaced it from {repair.DonorAccountKey} and restarted the client: {readied.Message}");
        }

        return repaired;
    }

    private async Task<SettingsReadyAttempt> SendReadyAfterSettingsRepairAsync(
        string accountKey,
        AccountConfig account,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _registry.SendCommandAsync(
                account.AgentId,
                "menu_ready",
                new { },
                ReadyCommandTimeout,
                cancellationToken);
            if (!result.Ok)
            {
                var failedStatusJson = result.Data?.GetRawText();
                if (SettingsRepairPolicy.NeedsDonorSettings(failedStatusJson))
                {
                    // A genuinely new post-copy gamma frame means the donor copy did not recover
                    // this client. ObserveCurrentStatus also restores the agent's durable copy
                    // budget when this is the first status seen after a master restart.
                    _settingsRepairs.ObserveCurrentStatus(
                        accountKey,
                        failedStatusJson,
                        DateTimeOffset.UtcNow);
                }

                return new SettingsReadyAttempt(false, $"the follow-up ready failed ({result.Message}).");
            }

            // menu_ready success is positive proof that the copied file works. It also completes
            // standalone sweep recovery; follow-auto has an additional proof point later, but
            // should not be required to reset this incident's budget.
            _settingsRepairs.RecordRecovered(accountKey);
            return new SettingsReadyAttempt(true, "client is ready again.");
        }
        catch (Exception ex)
        {
            return new SettingsReadyAttempt(false, $"the follow-up ready failed ({ex.Message}).");
        }
    }

    internal static bool HasStatusFromCurrentAgentConnection(AgentSnapshot? agent)
    {
        // AgentRegistry retains LastStatusJson for diagnostics across reconnects, but resets
        // StatusReceivedAt until the new authenticated connection supplies its own status frame.
        return agent is { Connected: true, ConnectedAt: not null, StatusReceivedAt: not null };
    }

    internal static bool TryGetAgentChargedSettingsRepairAttempt(
        CommandResultInfo result,
        out int? durableAttemptCount)
    {
        durableAttemptCount = null;
        if (result.Data is { ValueKind: JsonValueKind.Object } data)
        {
            if (TryGetInt(data, "incidentRepairAttempts", out var attempts) && attempts > 0)
            {
                durableAttemptCount = attempts;
            }

            if (TryGetBoolean(data, "settingsRepairAttemptCharged", out var charged))
            {
                return charged;
            }
        }

        // Compatibility with agents released before the explicit marker: a successful repair
        // necessarily got past Prepared and completed the copy. Failures without the marker are
        // deliberately free because they can be generation/precondition rejections.
        return result.Ok;
    }

    // A pulse that takes minutes longer than requested means the process was suspended - the
    // host machine slept (e.g. the sleep button after quit-all) and woke back up. Discord.NET
    // reconnects the gateway on its own, but the channel deserves a fresh intro: the pre-sleep
    // startup message describes a world that no longer exists.
    private async Task RunHostWakeMonitorAsync()
    {
        var lastPulse = DateTimeOffset.UtcNow;
        while (true)
        {
            await Task.Delay(HostSleepPulseInterval);
            var now = DateTimeOffset.UtcNow;
            var gap = now - lastPulse;
            lastPulse = now;
            if (gap - HostSleepPulseInterval < HostSleepGapThreshold)
            {
                await RefreshStartupHealthIfChangedAsync();
                continue;
            }

            _logger.LogInformation("Host wake detected: pulse gap of {Gap}.", gap);

            // Every offline clock the stuck-VM sweep is keeping just jumped by however long this
            // machine was suspended, and none of that elapsed time is evidence about guests that
            // were suspended along with it. Without this the first sweep after a resume would read
            // the whole fleet as hours-stuck and power-cycle all of it at once.
            _stuckVms.Reset();

            try
            {
                await HandleHostWakeAsync(gap);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not post the wake-up startup message.");
            }
        }
    }

    private async Task RefreshStartupHealthIfChangedAsync()
    {
        await _startupMessageLock.WaitAsync();
        try
        {
            if (_startupMessage is not { } message)
            {
                return;
            }

            var content = AppendMetrics(metricsEnabled: false, FormatStartupMessageContent());
            if (!string.Equals(message.Content, content, StringComparison.Ordinal))
            {
                await message.ModifyAsync(properties => properties.Content = content);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not refresh periodic startup health.");
        }
        finally
        {
            _startupMessageLock.Release();
        }
    }

    private async Task HandleHostWakeAsync(TimeSpan gap)
    {
        var minutes = Math.Max(1, (int)Math.Round(gap.TotalMinutes));
        IUserMessage? previousStartup;
        IUserMessage? previousHolder;
        await _startupMessageLock.WaitAsync();
        try
        {
            previousStartup = _startupMessage;
            previousHolder = _quickButtonsMessage;
            _startupMessage = null;
            _quickButtonsMessage = null;
            _quickActionsOffered = BootQuickActions;
            _startupIntro = $"D2RHost woke up from sleep (~{minutes}m).";
        }
        finally
        {
            _startupMessageLock.Release();
        }

        await StripComponentsSafeAsync(previousStartup, "pre-sleep startup message");
        if (previousHolder is not null && previousHolder.Id != previousStartup?.Id)
        {
            await StripComponentsSafeAsync(previousHolder, "pre-sleep quick-start message");
        }

        await PostStartupMessageAsync();
    }

    private async Task StripComponentsSafeAsync(IUserMessage? message, string label)
    {
        if (message is null)
        {
            return;
        }

        try
        {
            await message.ModifyAsync(properties => properties.Components = new ComponentBuilder().Build());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not clear buttons on the {Label}.", label);
        }
    }

    public async Task StopAsync()
    {
        if (_settingsRepairSweepCts is { } sweepCts)
        {
            sweepCts.Cancel();
            if (_settingsRepairSweepTask is { } sweepTask)
            {
                try
                {
                    await sweepTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            sweepCts.Dispose();
            _settingsRepairSweepCts = null;
            _settingsRepairSweepTask = null;
        }

        if (_stuckVmSweepCts is { } stuckVmCts)
        {
            stuckVmCts.Cancel();
            if (_stuckVmSweepTask is { } stuckVmTask)
            {
                try
                {
                    await stuckVmTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            stuckVmCts.Dispose();
            _stuckVmSweepCts = null;
            _stuckVmSweepTask = null;
        }

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
        _discordReady = true;
        if (!_commandsRegistered)
        {
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

        PostStartupMessageOnce();
        await FlushUpdateNotificationsAsync();
        _ = Task.Run(() => TryResumeFollowAutoAsync());
    }

    private async Task TryResumeFollowAutoAsync(long? expectedRecoveryGeneration = null)
    {
        if (expectedRecoveryGeneration is null)
        {
            // Ready can fire repeatedly while Discord reconnects. Those generic startup probes are
            // interchangeable, so coalesce them instead of queueing N identical 120-second channel
            // lookups ahead of a recovery fallback. An exact fallback below is never dropped.
            if (!await _followAutoResumeGate.WaitAsync(0))
            {
                return;
            }
        }
        else
        {
            await _followAutoResumeGate.WaitAsync();
        }

        try
        {
            var intent = _db.GetFollowAutoResumeIntent();
            if (intent is null)
            {
                return;
            }

            if (expectedRecoveryGeneration is { } expectedGeneration
                && !IsExpectedFollowAutoResumeIntent(expectedGeneration, intent))
            {
                // A delayed fallback belongs to one exact local-restart transaction. A newer row
                // is somebody else's recovery and must be left untouched.
                return;
            }

            IMessageChannel? channel = null;
            for (var attempt = 1; attempt <= 24 && channel is null; attempt++)
            {
                channel = _client.GetChannel(intent.ChannelId) as IMessageChannel;
                if (channel is null
                    && _config.GuildChannel is { } fallbackChannelId
                    && fallbackChannelId != intent.ChannelId)
                {
                    channel = _client.GetChannel(fallbackChannelId) as IMessageChannel;
                }

                if (channel is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }

            if (channel is null)
            {
                _logger.LogWarning(
                    "Could not resume follow-auto after host recovery because Discord channel {ChannelId} is not visible. The resume intent remains recorded for the next start.",
                    intent.ChannelId);
                return;
            }

            var start = await TryBeginFollowAutoRunAsync(
                intent.RecoveryGeneration,
                waitForPredecessorRunId: expectedRecoveryGeneration);
            if (start is null)
            {
                var currentIntent = _db.GetFollowAutoResumeIntent();
                if (currentIntent is null)
                {
                    _logger.LogInformation(
                        "The recorded follow-auto resume was cleared before it started; automatic resume was skipped.");
                    return;
                }


                if (!IsExpectedFollowAutoResumeIntent(intent.RecoveryGeneration, currentIntent))
                {
                    _logger.LogInformation(
                        "The recorded follow-auto resume was replaced by a newer recovery while the old channel was resolving; leaving the newer intent intact.");
                    return;
                }

                // An operator-started run wins over a stale reboot intent; otherwise the next
                // process restart would unexpectedly launch a second run.
                _logger.LogWarning(
                    "Discarding the recorded follow-auto resume intent because a follow-auto run is already active.");
                _db.ClearFollowAutoResumeIntent(intent.RecoveryGeneration);
                return;
            }

            var queued = false;
            try
            {
                var options = new FollowAutoRunOptions(
                    channel,
                    Math.Max(intent.DelaySeconds, 0),
                    intent.Watch,
                    TimeSpan.FromMinutes(Math.Max(intent.IdleMinutes, 1)),
                    intent.MetricsEnabled,
                    intent.CharacterSlot,
                    intent.FriendRow,
                    intent.RecoveryAccountKeys,
                    intent.Reason,
                    FollowAutoTargetControl.ClampTargetForMode(
                        intent.TargetBotCount,
                        intent.PublicMode ? FollowAutoPartyMode.Public : FollowAutoPartyMode.Private),
                    intent.PublicMode ? FollowAutoPartyMode.Public : FollowAutoPartyMode.Private);

                // The one-shot is consumed only after the in-memory run has been installed. A
                // crash before this point leaves the intent available to the next process start.
                // Conditional deletion closes the delayed-fallback race: a replacement row that
                // appears during channel resolution belongs to a later run and is never consumed.
                if (!_db.ClearFollowAutoResumeIntent(intent.RecoveryGeneration))
                {
                    _logger.LogInformation(
                        "The recorded follow-auto resume changed before it could be consumed; automatic resume was skipped.");
                    return;
                }

                start.Token.ThrowIfCancellationRequested();
                QueueFollowAutoRun(options, start);
                queued = true;
            }
            finally
            {
                if (!queued)
                {
                    // An installed lease with no loop can never unwind itself.
                    await _followAutoLifecycle.CompleteUnwindAsync(start);
                }
            }

            _logger.LogInformation(
                "Resumed follow-auto after host recovery in Discord channel {ChannelId}; recovery accounts: {Accounts}.",
                channel.Id,
                string.Join(", ", intent.RecoveryAccountKeys));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not resume the recorded follow-auto run after host recovery.");
        }
        finally
        {
            _followAutoResumeGate.Release();
        }
    }

    private void PostStartupMessageOnce()
    {
        if (_startupMessageTaskStarted)
        {
            return;
        }

        _startupMessageTaskStarted = true;
        _ = Task.Run(PostStartupMessageAsync);
    }

    // The startup announcement used to be a one-shot string pushed through the notification
    // queue. That had two failure modes: the agent count was frozen at whatever happened to be
    // connected the instant Discord came up (agents are still restarting after an update, so
    // "2/4" never became "4/4"), and if the channel wasn't visible yet on the first flush the
    // message sat in the queue until some unrelated notification triggered another flush -
    // which never happens on a no-update start, so nothing was posted at all. Post it as a
    // tracked message instead: retry until the channel resolves, then keep the content live
    // from registry connectivity events.
    private async Task PostStartupMessageAsync()
    {
        if (!_config.UpdateNotificationsEnabled || _config.GuildChannel is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _startupPostInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            for (var attempt = 1; attempt <= 24; attempt++)
            {
                try
                {
                    if (GetUpdateNotificationChannel() is { } channel)
                    {
                        var message = await channel.SendMessageAsync(
                            AppendMetrics(metricsEnabled: false, FormatStartupMessageContent()),
                            components: BuildQuickStartComponents(_quickActionsOffered));
                        await _startupMessageLock.WaitAsync();
                        _startupMessage = message;
                        _quickButtonsMessage = _quickActionsOffered != QuickActions.None ? message : null;
                        _startupMessageLock.Release();

                        // Agents that connected while the send was in flight raised their events
                        // before _startupMessage existed; reconcile once now.
                        await RefreshStartupMessageAsync();

                        // The channel is definitely visible now, so re-drain anything (host
                        // update-complete marker, early agent notifications) a failed earlier
                        // flush left behind.
                        await FlushUpdateNotificationsAsync();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not send the startup availability message (attempt {Attempt}).", attempt);
                }

                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            _logger.LogWarning("Gave up posting the startup availability message; the channel never became available.");
        }
        finally
        {
            Interlocked.Exchange(ref _startupPostInProgress, 0);
        }
    }

    private void OnAgentConnectivityChanged()
    {
        _ = Task.Run(RefreshStartupMessageAsync);
    }

    private async Task RefreshStartupMessageAsync()
    {
        await _startupMessageLock.WaitAsync();
        try
        {
            if (_startupMessage is not { } message)
            {
                return;
            }

            var content = AppendMetrics(metricsEnabled: false, FormatStartupMessageContent());
            var components = _quickButtonsMessage is { } holder && holder.Id == message.Id
                ? BuildQuickStartComponents(_quickActionsOffered)
                : new ComponentBuilder().Build();
            await message.ModifyAsync(properties =>
            {
                properties.Content = content;
                properties.Components = components;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh the startup availability message.");
        }
        finally
        {
            _startupMessageLock.Release();
        }
    }

    private void OnDiscordNotificationQueued()
    {
        if (!_discordReady)
        {
            return;
        }

        _ = Task.Run(FlushUpdateNotificationsAsync);
    }

    private async Task FlushUpdateNotificationsAsync()
    {
        if (!_discordReady || !_config.UpdateNotificationsEnabled)
        {
            return;
        }

        var channel = GetUpdateNotificationChannel();
        if (channel is null)
        {
            return;
        }

        await _notificationLock.WaitAsync();
        var runtimeMessages = Array.Empty<string>();
        var runtimeIndex = 0;
        try
        {
            foreach (var message in _hostUpdateNotifications.ReadPendingMessages())
            {
                await channel.SendMessageAsync(AppendMetrics(metricsEnabled: false, message));
            }

            _hostUpdateNotifications.Clear();

            runtimeMessages = _notifications.Drain();
            for (; runtimeIndex < runtimeMessages.Length; runtimeIndex++)
            {
                await channel.SendMessageAsync(AppendMetrics(metricsEnabled: false, runtimeMessages[runtimeIndex]));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send Discord update notification.");
            _notifications.Requeue(runtimeMessages.Skip(runtimeIndex));
        }
        finally
        {
            _notificationLock.Release();
        }
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (!IsAllowed(command.User.Id))
        {
            await command.RespondAsync("Not authorized for this controller.", ephemeral: true);
            return;
        }

        RetireQuickButtons();

        SlashContext? context = null;
        try
        {
            context = SlashContext.From(command);
            await DispatchCommandAsync(command.CommandName, context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discord command failed.");
            var content = context is null
                ? AppendMetrics(metricsEnabled: false, $"Command failed: {ex.Message}")
                : AppendMetrics(context, $"Command failed: {ex.Message}");
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

    /// <summary>
    /// The live dclone park, as data rather than as a Discord message.
    /// </summary>
    /// <remarks>
    /// This is what makes a monitor-less park usable: on a host with no Discord channel the minted
    /// game names and passwords have nowhere to be written down, so the API has to be able to read
    /// them back. It is also simply a better way for a caller to poll a park than scraping text.
    /// </remarks>
    public DcloneParkStatus GetDcloneParkStatus()
    {
        var run = Volatile.Read(ref _dcloneRun);
        if (run is null)
        {
            return new DcloneParkStatus(false, null, null, 0, 0, null, Array.Empty<DcloneParkGame>());
        }

        var slots = run.Snapshot();
        return new DcloneParkStatus(
            Running: IsDcloneParkRunning(),
            StartedUtc: run.StartedUtc,
            Difficulty: run.Difficulty,
            Parked: slots.Count(slot => slot.State == DcloneSlotState.Parked),
            Total: slots.Count,
            MaxBots: run.MaxBots,
            Games: slots.Select(slot => new DcloneParkGame(
                slot.AccountKey,
                slot.GameName,
                slot.Password,
                slot.State.ToString(),
                slot.Detail,
                slot.Reparks)).ToArray());
    }

    /// <summary>
    /// Runs one <c>/d2r</c> command on behalf of an authenticated HTTP caller and returns what
    /// Discord would have been told.
    /// </summary>
    /// <remarks>
    /// This is the entire API implementation: it builds a context whose replies go to a sink
    /// instead of an interaction, and hands it to the same dispatcher the gateway uses. Commands
    /// are never listed or special-cased here, so the HTTP surface is the Discord surface by
    /// construction.
    /// </remarks>
    public async Task<ApiCommandResult> ExecuteApiCommandAsync(
        string? group,
        string command,
        IReadOnlyDictionary<string, object?> options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var descriptor = DiscordSlashCommandCatalog.Find(group, command);
        if (descriptor is null)
        {
            var path = string.IsNullOrWhiteSpace(group) ? command : $"{group} {command}";
            return new ApiCommandResult(
                Ok: false,
                $"Unknown command `{path}`. GET /api/commands lists what this host accepts.",
                Array.Empty<string>(),
                File: null,
                Error: ApiCommandRejection.UnknownCommand.ToString());
        }

        if (RejectUnknownOptions(descriptor, options) is { } unknownOptionError)
        {
            return new ApiCommandResult(
                Ok: false,
                unknownOptionError,
                Array.Empty<string>(),
                File: null,
                Error: ApiCommandRejection.UnknownCommand.ToString());
        }

        var sink = new ApiCommandSink();
        var context = SlashContext.FromApi(
            descriptor.Group,
            descriptor.Command,
            options,
            sink,
            ResolveNotificationChannel());

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(ApiCommandTimeout);
            var work = DispatchCommandAsync("d2r", context);
            var finished = await Task.WhenAny(work, Task.Delay(Timeout.Infinite, linked.Token));
            if (!ReferenceEquals(finished, work))
            {
                // The command keeps running - a ready pass or a create is not something to abandon
                // halfway - the caller just stops waiting for it.
                _logger.LogWarning(
                    "API command {Path} exceeded {Timeout} and the response was returned without it.",
                    descriptor.Path,
                    ApiCommandTimeout);
                sink.AddDetail(
                    $"Still running after {(int)ApiCommandTimeout.TotalSeconds}s; it was not cancelled. "
                        + "Poll /api/status or the relevant mode endpoint for the outcome.");
                return sink.ToResult(ok: false, error: "Timeout");
            }

            await work;
            return sink.ToResult(ok: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API command {Path} failed.", descriptor.Path);
            sink.AddDetail(ex.Message);
            return sink.ToResult(ok: false, error: ex.GetType().Name);
        }
    }

    /// <summary>
    /// A typo in an option name would otherwise be silently ignored and the command would run with
    /// a default the caller did not intend - the kind of failure an automated caller cannot see.
    /// </summary>
    private static string? RejectUnknownOptions(
        CommandDescriptor descriptor,
        IReadOnlyDictionary<string, object?> options)
    {
        var unknown = options.Keys
            .Where(name => !descriptor.Options.Any(option =>
                string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknown.Length == 0)
        {
            return null;
        }

        var accepted = descriptor.Options.Count == 0
            ? "it takes no options"
            : "it accepts: " + string.Join(", ", descriptor.Options.Select(option => option.Name));
        return $"`{descriptor.Path}` does not have option(s) {string.Join(", ", unknown)}; {accepted}.";
    }

    /// <summary>
    /// The channel a mode's live monitor goes to when the command did not arrive from a Discord
    /// channel of its own. Null when Discord is disabled, no channel is configured, or the
    /// configured one is not visible to the bot.
    /// </summary>
    private IMessageChannel? ResolveNotificationChannel()
    {
        if (_config.DisableDiscord || _config.GuildChannel is not { } channelId)
        {
            return null;
        }

        try
        {
            return _client.GetChannel(channelId) as IMessageChannel;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve the configured notification channel for an API command.");
            return null;
        }
    }

    /// <summary>
    /// Routes one command to its handler. Shared by the Discord gateway and the HTTP API so both
    /// doors reach the same code - the API is not a second implementation of the command surface.
    /// </summary>
    private async Task DispatchCommandAsync(string commandName, SlashContext context)
    {
        switch (commandName)
        {
            case "d2r":
                if (context.GroupName == "config")
                {
                    await HandleConfigAsync(context);
                }
                else if (context.GroupName == "vm")
                {
                    await HandleVmAsync(context);
                }
                else if (context.GroupName == "game")
                {
                    await HandleGameAsync(context);
                }
                else if (context.GroupName == "system")
                {
                    await HandleSystemAsync(context);
                }
                else if (context.SubcommandName == "restart")
                {
                    await HandleRestartAsync(context);
                }
                else
                {
                    await HandleD2RAsync(context);
                }

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

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (!IsAllowed(component.User.Id))
        {
            await component.RespondAsync("Not authorized for this controller.", ephemeral: true);
            return;
        }

        if (component.Data.CustomId is not (StartupFollowButtonId or StartupReadyButtonId
            or StartupQuitButtonId or StartupSleepButtonId))
        {
            RetireQuickButtons();
        }

        try
        {
            switch (component.Data.CustomId)
            {
                case TemplateCreateButtonId:
                    await HandleTemplateCreateButtonAsync(component);
                    return;
                case TemplateJoinButtonId:
                    await HandleTemplateJoinButtonAsync(component);
                    return;
                case FollowAutoStartButtonId:
                    await StartFollowAutoAsync(
                        SlashContext.FromComponent(component, "follow"),
                        delaySeconds: 0,
                        watch: false,
                        TimeSpan.FromMinutes(FollowAutoDefaultIdleMinutes));
                    return;
                case FollowAutoStopButtonId:
                    await StopFollowAutoAsync(SlashContext.FromComponent(component, "follow"));
                    return;
                case FollowAutoStopLeaveButtonId:
                    await HandleFollowAutoStopLeaveButtonAsync(component);
                    return;
                case FollowAutoStopFollowButtonId:
                    await HandleFollowAutoStopFollowButtonAsync(component);
                    return;
                case FollowAutoStopQuitButtonId:
                    await HandleFollowAutoStopQuitButtonAsync(component);
                    return;
                case FollowAutoStopSleepButtonId:
                    await HandleFollowAutoStopSleepButtonAsync(component);
                    return;
                case FollowAutoRemoveBotButtonId:
                    await HandleFollowAutoBotCountButtonAsync(component, delta: -1);
                    return;
                case FollowAutoAddBotButtonId:
                    await HandleFollowAutoBotCountButtonAsync(component, delta: +1);
                    return;
                case FollowAutoPartyModeButtonId:
                    await HandleFollowAutoPartyModeButtonAsync(component);
                    return;
                case FollowAutoParkButtonId:
                    await HandleFollowAutoParkButtonAsync(component);
                    return;
                case FollowAutoJoinDelayButtonId:
                    await HandleFollowAutoJoinDelayButtonAsync(component);
                    return;
                case DcloneStopButtonId:
                    await StopDcloneParkAsync(SlashContext.FromComponent(component, "dclone"));
                    return;
                case DclonePrivateButtonId:
                    await HandleDcloneFollowButtonAsync(component, FollowAutoPartyMode.Private);
                    return;
                case DclonePublicButtonId:
                    await HandleDcloneFollowButtonAsync(component, FollowAutoPartyMode.Public);
                    return;
                case GameSessionLeaveButtonId:
                    await QueueSaveExitAllAsync(SlashContext.FromComponent(component, "save-exit"));
                    return;
                case GameSessionQuitButtonId:
                    await QueueQuitAllAsync(SlashContext.FromComponent(component, "quit"), "game-session quit button was pressed");
                    return;
                case StartupFollowButtonId:
                    await HandleStartupFollowButtonAsync(component);
                    return;
                case StartupReadyButtonId:
                    await HandleStartupReadyButtonAsync(component);
                    return;
                case StartupQuitButtonId:
                    await HandleStartupQuitButtonAsync(component);
                    return;
                case StartupSleepButtonId:
                    await HandleStartupSleepButtonAsync(component);
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discord button failed.");
            var content = AppendMetrics(metricsEnabled: false, $"Button action failed: {ex.Message}");
            if (component.HasResponded)
            {
                await component.ModifyOriginalResponseAsync(properties => properties.Content = content);
            }
            else
            {
                await component.RespondAsync(content, ephemeral: true);
            }
        }
    }

    // Both acknowledge before resolving: Resolve*Input reads the stored game from the database,
    // and a click gets the same three seconds a slash command does.
    private async Task HandleTemplateCreateButtonAsync(SocketMessageComponent component)
    {
        await EnsureAcknowledgedAsync(component);
        var context = SlashContext.FromComponent(component, "create-game");
        await QueueCreateGameAllAsync(context, ResolveCreateGameAllInput(context), watch: false);
    }

    private async Task HandleTemplateJoinButtonAsync(SocketMessageComponent component)
    {
        await EnsureAcknowledgedAsync(component);
        var context = SlashContext.FromComponent(component, "join");
        var game = ResolveJoinAllInput(context);
        if (game is null)
        {
            await SetInitialCommandResponseAsync(
                context,
                "Nothing to join: no recent game and no template set.",
                ephemeral: true);
            return;
        }

        await QueueJoinAllAsync(context, game, watch: false);
    }

    private static MessageComponent BuildTemplateActionComponents()
    {
        return new ComponentBuilder()
            .WithButton("Create Game", TemplateCreateButtonId, ButtonStyle.Primary)
            .WithButton("Join Game", TemplateJoinButtonId, ButtonStyle.Secondary)
            .Build();
    }

    private static MessageComponent BuildQuickStartComponents(QuickActions actions)
    {
        var builder = new ComponentBuilder();
        if (actions.HasFlag(QuickActions.Follow))
        {
            builder.WithButton("Follow", StartupFollowButtonId, ButtonStyle.Primary);
        }

        if (actions.HasFlag(QuickActions.Ready))
        {
            builder.WithButton("Ready", StartupReadyButtonId, ButtonStyle.Secondary);
        }

        if (actions.HasFlag(QuickActions.Quit))
        {
            builder.WithButton("Quit", StartupQuitButtonId, ButtonStyle.Danger);
        }

        if (actions.HasFlag(QuickActions.Sleep))
        {
            builder.WithButton("Sleep", StartupSleepButtonId, ButtonStyle.Danger);
        }

        return builder.Build();
    }

    // These four acknowledge before stripping the buttons: ClearQuickStartButtonsAsync edits a
    // message over REST, which is a network round-trip Discord's three-second budget for the
    // click should not be paying for.
    private async Task HandleStartupFollowButtonAsync(SocketMessageComponent component)
    {
        await EnsureAcknowledgedAsync(component);
        await ClearQuickStartButtonsAsync(component);
        await StartFollowAutoAsync(
            SlashContext.FromComponent(component, "follow"),
            delaySeconds: 0,
            watch: false,
            TimeSpan.FromMinutes(FollowAutoDefaultIdleMinutes));
    }

    private async Task HandleStartupReadyButtonAsync(SocketMessageComponent component)
    {
        await EnsureAcknowledgedAsync(component);
        await ClearQuickStartButtonsAsync(component);
        await QueueReadyAllAsync(SlashContext.FromComponent(component, "ready"));
    }

    private async Task HandleStartupQuitButtonAsync(SocketMessageComponent component)
    {
        await EnsureAcknowledgedAsync(component);
        await ClearQuickStartButtonsAsync(component);
        await QueueQuitAllAsync(SlashContext.FromComponent(component, "quit"), "quick-start quit button was pressed");
    }

    private async Task HandleStartupSleepButtonAsync(SocketMessageComponent component)
    {
        await EnsureAcknowledgedAsync(component);
        await ClearQuickStartButtonsAsync(component);
        await RunQuitAllThenSleepAsync(SlashContext.FromComponent(component, "system"));
    }

    // Clicking either quick-start button retires both: they describe an "idle, nothing touched
    // yet" state, and either action ends that state.
    private async Task ClearQuickStartButtonsAsync(SocketMessageComponent component)
    {
        var retiredMessageId = await RetireQuickButtonsAsync();
        if (retiredMessageId == component.Message.Id)
        {
            return;
        }

        // The click landed on a message this process no longer tracks (a previous host
        // process, pre-sleep, or an already-superseded holder - its custom IDs still route
        // here); strip that message's buttons in place.
        try
        {
            await component.Message.ModifyAsync(properties => properties.Components = new ComponentBuilder().Build());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove the quick-start buttons from the clicked message.");
        }
    }

    // Any other instruction to the bot makes the quick-start buttons stale too - retire them
    // without blocking the command that triggered it.
    private void RetireQuickButtons()
    {
        if (_quickButtonsMessage is null && _quickActionsOffered == QuickActions.None)
        {
            return;
        }

        _ = Task.Run(RetireQuickButtonsAsync);
    }

    private async Task<ulong?> RetireQuickButtonsAsync()
    {
        IUserMessage? holder;
        bool holderIsStartupMessage;
        await _startupMessageLock.WaitAsync();
        try
        {
            _quickActionsOffered = QuickActions.None;
            holder = _quickButtonsMessage;
            _quickButtonsMessage = null;
            holderIsStartupMessage = holder is not null && _startupMessage is { } startup && holder.Id == startup.Id;
        }
        finally
        {
            _startupMessageLock.Release();
        }

        if (holder is null)
        {
            return null;
        }

        if (holderIsStartupMessage)
        {
            // Re-render rather than blank the components so the health block stays current.
            await RefreshStartupMessageAsync();
        }
        else
        {
            await StripComponentsSafeAsync(holder, "quick-start follow-up message");
        }

        return holder.Id;
    }

    private static MessageComponent BuildFollowBindActionComponents()
    {
        return new ComponentBuilder()
            .WithButton("Follow", FollowAutoStartButtonId, ButtonStyle.Primary)
            .Build();
    }

    private MessageComponent BuildFollowAutoMonitorComponents(bool running)
    {
        var builder = new ComponentBuilder();
        if (!running)
        {
            return builder.Build();
        }

        builder.WithButton("Stop", FollowAutoStopButtonId, ButtonStyle.Danger);

        var (target, mode) = _followAutoTarget.Snapshot;
        var availability = Volatile.Read(ref _followAutoRosterAvailability);
        // The manual count buttons only exist in private mode. In public mode the target is an
        // output of the live player count, so a -1 the next pulse silently undoes would be a
        // control that lies about what it does.
        if (mode == FollowAutoPartyMode.Private)
        {
            // -1 is offered whenever there is a bot to give up. +1 only when there is a benched VM
            // to promote AND the live game has a free slot - offering a button that cannot work is
            // worse than not offering it, since the operator cannot tell a rejected press from a
            // slow one.
            builder.WithButton(
                "-1",
                FollowAutoRemoveBotButtonId,
                ButtonStyle.Secondary,
                disabled: _followAutoTarget.LocalRestartArmed || !FollowAutoRosterPolicy.CanRemoveBot(target));
            if (!_followAutoTarget.LocalRestartArmed
                && availability.CanAddBot(target, _followAutoLivePlayers.Value))
            {
                builder.WithButton("+1 VM", FollowAutoAddBotButtonId, ButtonStyle.Success);
            }

            // Labelled with what the press does, like the mode toggle above it: the hold is either
            // being added or taken away. Private only - public mode derives its count from a client
            // that has to be inside the game to read it, and a deliberate half-minute with nobody
            // in there yet is a half-minute of no samples at all.
            var joinDelayArmed = _followAutoTarget.JoinDelayArmed;
            builder.WithButton(
                joinDelayArmed
                    ? $"-{FollowAutoJoinDelayPolicy.DelaySeconds}s Delay"
                    : $"+{FollowAutoJoinDelayPolicy.DelaySeconds}s Delay",
                FollowAutoJoinDelayButtonId,
                joinDelayArmed ? ButtonStyle.Secondary : ButtonStyle.Success,
                disabled: _followAutoTarget.LocalRestartArmed);
        }

        // Labelled with the mode it switches TO, so the button reads as the action it performs.
        builder.WithButton(
            mode == FollowAutoPartyMode.Private ? "Public" : "Private",
            FollowAutoPartyModeButtonId,
            mode == FollowAutoPartyMode.Private ? ButtonStyle.Success : ButtonStyle.Secondary,
            disabled: _followAutoTarget.LocalRestartArmed);

        // Park is the fleet's third mode beside Private and Public, so it sits with the toggle.
        // Locked with it too: armed local recovery is about to respawn the host and resume this
        // run from its journal, which a park started now would simply be torn down by.
        builder.WithButton(
            "Park",
            FollowAutoParkButtonId,
            ButtonStyle.Primary,
            disabled: _followAutoTarget.LocalRestartArmed);

        return builder.Build();
    }

    // Leave only makes sense while the loop believes bots are still in a game; once it
    // already confirmed the leave (Bots in game: 0), the useful next move is Follow.
    private static MessageComponent BuildFollowAutoStopActionComponents(bool botsInGame)
    {
        var builder = new ComponentBuilder();
        if (botsInGame)
        {
            builder.WithButton("Leave", FollowAutoStopLeaveButtonId, ButtonStyle.Secondary);
        }
        else
        {
            builder.WithButton("Follow", FollowAutoStopFollowButtonId, ButtonStyle.Primary);
        }

        return builder
            .WithButton("Quit", FollowAutoStopQuitButtonId, ButtonStyle.Danger)
            .WithButton("Sleep", FollowAutoStopSleepButtonId, ButtonStyle.Danger)
            .Build();
    }

    private static MessageComponent BuildGameSessionActionComponents()
    {
        return new ComponentBuilder()
            .WithButton("Leave", GameSessionLeaveButtonId, ButtonStyle.Secondary)
            .WithButton("Quit", GameSessionQuitButtonId, ButtonStyle.Danger)
            .Build();
    }

    // The three response helpers are the seam the HTTP API rides in on: every command handler
    // answers through one of them, so routing an API context's replies into its sink here is all
    // it takes for the whole /d2r surface to work over HTTP. Buttons and metrics are Discord-only
    // decoration and are simply dropped for an API caller.
    private Task RespondWithMetricsAsync(
        SlashContext context,
        string content,
        bool ephemeral = true,
        MessageComponent? components = null)
    {
        if (context.Api is { } sink)
        {
            sink.SetMessage(content);
            return Task.CompletedTask;
        }

        return context.Interaction!.RespondAsync(
            AppendMetrics(context, content),
            ephemeral: ephemeral,
            components: components);
    }

    private Task ModifyOriginalResponseWithMetricsAsync(
        SlashContext context,
        string content,
        Action<MessageProperties>? configure = null)
    {
        if (context.Api is { } sink)
        {
            sink.SetMessage(content);
            return Task.CompletedTask;
        }

        return context.Interaction!.ModifyOriginalResponseAsync(properties =>
        {
            properties.Content = AppendMetrics(context, content);
            configure?.Invoke(properties);
        });
    }

    private async Task<IUserMessage?> FollowupWithMetricsAsync(
        SlashContext context,
        string content,
        bool ephemeral = true,
        MessageComponent? components = null)
    {
        if (context.Api is { } sink)
        {
            sink.AddDetail(content);
            return null;
        }

        return await context.Interaction!.FollowupAsync(
            AppendMetrics(context, content),
            ephemeral: ephemeral,
            components: components);
    }

    private string AppendMetrics(SlashContext context, string content)
    {
        return AppendMetrics(context.MetricsEnabled, content);
    }

    private string AppendMetrics(bool metricsEnabled, string content)
    {
        if (!metricsEnabled)
        {
            return DiscordMessageTruncator.Truncate(content);
        }

        var metrics = FormatMetricsBlock();
        if (string.IsNullOrWhiteSpace(metrics))
        {
            return DiscordMessageTruncator.Truncate(content);
        }

        var suffix = "\n\n" + metrics;
        if (suffix.Length >= DiscordMessageTruncator.DiscordContentLimit)
        {
            return DiscordMessageTruncator.Truncate(metrics);
        }

        var body = DiscordMessageTruncator.Truncate(
            content,
            DiscordMessageTruncator.DiscordContentLimit - suffix.Length);
        return body + suffix;
    }

    private string FormatMetricsBlock()
    {
        var hostTelemetry = _hostTelemetry.Sample();
        var lines = new List<string>
        {
            "Metrics:",
            $"host: RAM {FormatMemoryUsage(hostTelemetry)}, CPU {FormatCpuPercent(hostTelemetry.CpuPercent)}"
        };

        foreach (var (accountKey, account) in _registry.Accounts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var name = FormatAccountDisplayName(accountKey, account);
            var agent = _registry.GetAgent(account.AgentId);
            if (agent?.Connected != true)
            {
                lines.Add($"{name}: offline");
                continue;
            }

            lines.Add(TryReadMachineTelemetry(agent.LastStatusJson, out var telemetry)
                ? $"{name}: RAM {FormatMemoryUsage(telemetry)}, CPU {FormatCpuPercent(telemetry.CpuPercent)}"
                : $"{name}: telemetry unavailable");
        }

        return string.Join("\n", lines);
    }

    private static string FormatMemoryUsage(MachineTelemetrySnapshot telemetry)
    {
        if (telemetry.MemoryUsedBytes is { } used && telemetry.MemoryTotalBytes is { } total && total > 0)
        {
            var percent = Math.Clamp(used * 100.0 / total, 0, 100);
            return $"{FormatBytes(used)}/{FormatBytes(total)} ({percent:0}%)";
        }

        if (telemetry.MemoryUsedBytes is { } usedOnly)
        {
            return FormatBytes(usedOnly);
        }

        return "?";
    }

    private static string FormatCpuPercent(double? value)
    {
        return value is { } percent
            ? $"{Math.Clamp(percent, 0, 100):0}%"
            : "?";
    }

    private static string FormatBytes(long bytes)
    {
        const double kib = 1024;
        const double mib = kib * 1024;
        const double gib = mib * 1024;

        if (bytes >= 10 * gib)
        {
            return $"{bytes / gib:0.#} GB";
        }

        if (bytes >= gib)
        {
            return $"{bytes / gib:0.##} GB";
        }

        if (bytes >= mib)
        {
            return $"{bytes / mib:0.#} MB";
        }

        return $"{bytes / kib:0.#} KB";
    }

    private static bool TryReadMachineTelemetry(string? json, out MachineTelemetrySnapshot telemetry)
    {
        telemetry = new MachineTelemetrySnapshot(null, null, null, null);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("machineTelemetry", out var root)
                || root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return false;
            }

            telemetry = new MachineTelemetrySnapshot(
                MemoryTotalBytes: TryGetNullableInt64(root, "memoryTotalBytes"),
                MemoryAvailableBytes: TryGetNullableInt64(root, "memoryAvailableBytes"),
                MemoryUsedBytes: TryGetNullableInt64(root, "memoryUsedBytes"),
                CpuPercent: TryGetNullableDouble(root, "cpuPercent"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long? TryGetNullableInt64(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out var value)
                ? value
                : null;
    }

    private static double? TryGetNullableDouble(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out var value)
                ? value
                : null;
    }

    private async Task HandleD2RAsync(SlashContext context)
    {
        var subcommand = context.SubcommandName;

        // No "health" branch: 87e3780 folded that subcommand into status, which renders the same
        // health block above the account list. The unreachable handler it left behind outlived the
        // command by long enough to send people chasing a command Discord never offers.
        if (subcommand == "status")
        {
            await DeferIfInteractiveAsync(context);
            var accountKey = context.GetString("account");
            var content = accountKey is null
                ? FormatHealth() + "\n\n" + await FormatAllAccountStatusesLiveAsync(CancellationToken.None)
                : await FormatAccountStatusLiveAsync(accountKey, CancellationToken.None);
            await ModifyOriginalResponseWithMetricsAsync(context, content);
            return;
        }

        if (subcommand == "start-all" || (subcommand == "start" && ShouldRunAll(context)))
        {
            await QueueReadyAllAsync(context);
            return;
        }

        if (subcommand == "start" && !ShouldRunAll(context))
        {
            var (accountKey, account) = RequireAccount(context.GetRequiredString("account"));
            await RunVmCommandAsync(context, account, "launch_d2r", BuildAccountArgs(accountKey, account), TimeSpan.FromSeconds(210));
            return;
        }

        if (subcommand == "ready" && context.GetString("account") is null)
        {
            await QueueReadyAllAsync(context);
            return;
        }

        if (subcommand == "dclone")
        {
            if (context.GetBool("stop") == true)
            {
                await StopDcloneParkAsync(context);
                return;
            }

            await StartDcloneParkAsync(context, context.GetBool("watch") == true);
            return;
        }

        if (subcommand == "join-auto"
            || (subcommand == "join" && (context.OptionCount == 0 || context.GetBool("auto") is not null)))
        {
            if (context.GetBool("stop") == true || context.GetBool("auto") == false)
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

        // The Resolve*Input helpers all read the stored game out of the database, and an argument
        // expression is evaluated before the call it belongs to - so passing one straight into
        // RunVmCommandAsync would do that read before the deferral inside it. Acknowledge first
        // and resolve into a local instead.
        if (subcommand == "join-all" || (subcommand == "join" && ShouldRunAll(context)))
        {
            await EnsureAcknowledgedAsync(context);
            var game = ResolveJoinAllInput(context);
            if (game is null)
            {
                await SetInitialCommandResponseAsync(
                    context,
                    "Nothing to join: no recent game and no template set. Pass name, or set one with /d2r game set or /d2r template.",
                    ephemeral: true);
                return;
            }

            await QueueJoinAllAsync(context, game, watch: context.GetBool("watch") ?? false);
            return;
        }

        if (subcommand == "join" && !ShouldRunAll(context))
        {
            var (accountKey, account) = RequireAccount(context.GetRequiredString("account"));
            await EnsureAcknowledgedAsync(context);
            var joinGame = ResolveGameInput(context);
            await RunVmCommandAsync(context, account, "menu_join_game", BuildMenuArgs(accountKey, account, joinGame, context), TimeSpan.FromSeconds(210), readyFirstIfNotMenuReady: true);
            return;
        }

        if (subcommand == "create-game-all" || (subcommand == "create-game" && ShouldRunAll(context)))
        {
            await EnsureAcknowledgedAsync(context);
            await QueueCreateGameAllAsync(context, ResolveCreateGameAllInput(context), watch: context.GetBool("watch") ?? false);
            return;
        }

        if (subcommand == "create-game" && !ShouldRunAll(context))
        {
            var (accountKey, account) = RequireAccount(context.GetRequiredString("account"));
            await EnsureAcknowledgedAsync(context);
            var createGame = ResolveGameInput(context);
            await RunVmCommandAsync(context, account, "menu_create_game", BuildMenuArgs(accountKey, account, createGame, context), TimeSpan.FromSeconds(210), readyFirstIfNotMenuReady: true);
            return;
        }

        if (subcommand == "template")
        {
            _gameTemplate = new GameNameTemplate(
                context.GetRequiredString("name"),
                BlankToNull(context.GetString("password")));
            var passwordSuffix = _gameTemplate.Password is null ? string.Empty : $"/{_gameTemplate.Password}";
            await RespondWithMetricsAsync(
                context,
                $"Template set: {_gameTemplate.Name}1{passwordSuffix} is next. "
                    + "/d2r create-game and /d2r join with no name will use it until /d2r template is set again or the host restarts.",
                components: BuildTemplateActionComponents());
            return;
        }

        if (subcommand == "follow-all"
            || (subcommand == "follow"
                && context.GetBool("bind") is null
                && context.GetBool("auto") is null
                && IsManualFollowRequest(context)
                && ShouldRunAll(context)))
        {
            await QueueAllCommandsAsync(
                context,
                "menu_join_friend",
                (accountKey, account) => BuildMenuArgs(accountKey, account, null, context),
                TimeSpan.FromSeconds(210),
                readyFirstIfNotMenuReady: true,
                displayName: "follow",
                watch: context.GetBool("watch") == true,
                watchLabel: "follow",
                watchName: "follow");
            return;
        }

        if (subcommand == "save-exit-all" || (subcommand == "save-exit" && ShouldRunAll(context)))
        {
            await QueueSaveExitAllAsync(context);
            return;
        }

        if (subcommand == "save-exit" && !ShouldRunAll(context))
        {
            var (accountKey, account) = RequireAccount(context.GetRequiredString("account"));
            await RunVmCommandAsync(context, account, "menu_save_exit", BuildAccountArgs(accountKey, account), TimeSpan.FromSeconds(210));
            return;
        }

        if (subcommand == "quit-all" || (subcommand == "quit" && ShouldRunAll(context)))
        {
            await QueueQuitAllAsync(context, "quit-all was called");
            return;
        }

        if (subcommand == "quit" && !ShouldRunAll(context))
        {
            var (accountKey, account) = RequireAccount(context.GetRequiredString("account"));
            // Cancelling follow-auto writes to the database, so acknowledge ahead of it rather
            // than relying on the deferral inside RunVmCommandAsync further down.
            await EnsureAcknowledgedAsync(context);
            await CancelJoinAutoIfRunningAsync($"quit was called for {accountKey}");
            await CancelDcloneParkIfRunningAsync($"quit was called for {accountKey}");
            var followAutoCancel = await CancelFollowAutoIfRunningAsync($"quit was called for {accountKey}");
            QueueFollowAutoStopSignal(followAutoCancel.RunId);
            await RunVmCommandAsync(context, account, "quit_d2r", BuildAccountArgs(accountKey, account), TimeSpan.FromSeconds(210));
            return;
        }

        if (subcommand == "follow"
            && (context.GetBool("bind") is not null
                || context.GetBool("auto") is not null
                || context.GetInt("bind-in-game") is not null
                || !IsManualFollowRequest(context)))
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
                    context.GetBool("watch") == true,
                    TimeSpan.FromMinutes(Math.Max(context.GetInt("idle-minutes") ?? FollowAutoDefaultIdleMinutes, 1)));
                return;
            }

            if (context.GetBool("bind") is { } bindFlag)
            {
                await HandleFollowBindAsync(context, bindFlag);
                return;
            }

            if (context.GetInt("bind-in-game") is { } partyPosition)
            {
                await HandleFollowBindInGameAsync(context, partyPosition);
                return;
            }

            await StartFollowAutoAsync(
                context,
                Math.Max(context.GetInt("delay") ?? 0, 0),
                context.GetBool("watch") == true,
                TimeSpan.FromMinutes(Math.Max(context.GetInt("idle-minutes") ?? FollowAutoDefaultIdleMinutes, 1)));
            return;
        }

        if (subcommand == "follow" && IsManualFollowRequest(context) && !ShouldRunAll(context))
        {
            var (accountKey, account) = RequireAccount(context.GetRequiredString("account"));
            if (context.GetBool("watch") == true)
            {
                await RunWatchedFollowCommandAsync(context, accountKey, account, BuildMenuArgs(accountKey, account, null, context));
            }
            else
            {
                await RunVmCommandAsync(context, account, "menu_join_friend", BuildMenuArgs(accountKey, account, null, context), TimeSpan.FromSeconds(210), readyFirstIfNotMenuReady: true);
            }

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
                // Acknowledge before the cancel, which writes to the database; see the other
                // single-account quit branch above.
                await EnsureAcknowledgedAsync(context);
                await CancelJoinAutoIfRunningAsync($"quit was called for {singleAccountKey}");
                await CancelDcloneParkIfRunningAsync($"quit was called for {singleAccountKey}");
                var followAutoCancel = await CancelFollowAutoIfRunningAsync($"quit was called for {singleAccountKey}");
                QueueFollowAutoStopSignal(followAutoCancel.RunId);
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
            // Acknowledge before ResolveGameInput for the same argument-evaluation reason as the
            // join/create branches above: it reads the stored game from the database.
            case "join-game":
                await EnsureAcknowledgedAsync(context);
                var joinGameInput = ResolveGameInput(context);
                await RunVmCommandAsync(context, singleAccount, "menu_join_game", BuildMenuArgs(singleAccountKey, singleAccount, joinGameInput, context), TimeSpan.FromSeconds(210), readyFirstIfNotMenuReady: true);
                return;
            case "create-game":
                await EnsureAcknowledgedAsync(context);
                var createGameInput = ResolveGameInput(context);
                await RunVmCommandAsync(context, singleAccount, "menu_create_game", BuildMenuArgs(singleAccountKey, singleAccount, createGameInput, context), TimeSpan.FromSeconds(210), readyFirstIfNotMenuReady: true);
                return;
            case "follow":
                if (context.GetBool("watch") == true)
                {
                    await RunWatchedFollowCommandAsync(context, singleAccountKey, singleAccount, BuildMenuArgs(singleAccountKey, singleAccount, null, context));
                }
                else
                {
                    await RunVmCommandAsync(context, singleAccount, "menu_join_friend", BuildMenuArgs(singleAccountKey, singleAccount, null, context), TimeSpan.FromSeconds(210), readyFirstIfNotMenuReady: true);
                }

                return;
            case "save-exit":
                // See the save-exit-all timeout comment above: this is gate-wait headroom, not
                // expected automation time.
                await RunVmCommandAsync(context, singleAccount, "menu_save_exit", BuildAccountArgs(singleAccountKey, singleAccount), TimeSpan.FromSeconds(210));
                return;
            case "remote":
                var remoteUrl = _registry.GetAgentConfig(singleAccount.AgentId)?.RemoteUrl;
                await RespondWithMetricsAsync(
                    context,
                    string.IsNullOrWhiteSpace(remoteUrl)
                        ? $"No remoteUrl is configured for {singleAccountKey} ({singleAccount.AgentId})."
                        : $"{singleAccountKey} remote link: {remoteUrl}");
                return;
            case "screenshot":
                await RunScreenshotAsync(context, singleAccount, BuildAccountArgs(singleAccountKey, singleAccount));
                return;
        }
    }

    private static bool ShouldRunAll(SlashContext context)
    {
        return context.GetBool("all") ?? true;
    }

    private static bool IsManualFollowRequest(SlashContext context)
    {
        if (context.GetBool("all") is not null)
        {
            return true;
        }

        return context.HasOption("friend-row")
            || context.HasOption("character-slot");
    }

    private async Task QueueSaveExitAllAsync(SlashContext context)
    {
        // menu_save_exit's own automation (up to 3 rounds of Escape, click, wait up to ~12s,
        // retried while the in-game HUD stays visible) tops out around 40s - the
        // budget here almost entirely covers time spent waiting for the agent's command
        // gate, which a preceding create-game-all/join-all can still be holding for as long
        // as those commands' own 210s timeout allows. A shorter budget here doesn't make
        // save-exit faster; it just means the gate frees up, save-exit actually runs and
        // succeeds, and the result arrives after the host already gave up and discarded the
        // pending request - reported as a failure even though it worked.
        // After a leave everyone is parked warm at the character screen - the same state
        // ready-all ends in - so the completion follow-up offers the same set: Follow to
        // keep going, Quit/Sleep to wind down. Ready would be redundant.
        // A leave-all empties every parked game, which a running dclone park would immediately
        // undo by rebuilding all of them. Unlike follow-auto - which uses save-exit as a step of
        // its own loop and so must not be cancelled here - a park never issues one itself, so any
        // leave-all reaching this point is the operator ending the park.
        await CancelDcloneParkIfRunningAsync("leave was called for every account");
        await QueueAllCommandsAsync(
            context,
            "menu_save_exit",
            (accountKey, account) => BuildAccountArgs(accountKey, account),
            TimeSpan.FromSeconds(210),
            displayName: "leave",
            offerOnCompletion: QuickActions.Follow | QuickActions.Quit | QuickActions.Sleep);
    }

    private async Task QueueReadyAllAsync(SlashContext context)
    {
        // Once everyone is warmed to the character screen the natural next moves are to start
        // following or to wind down, so the completion follow-up offers Follow/Quit/Sleep
        // (not Ready again).
        await QueueAllCommandsAsync(
            context,
            "menu_ready",
            (accountKey, account) => BuildAccountArgs(accountKey, account),
            ReadyCommandTimeout,
            displayName: "ready",
            offerOnCompletion: QuickActions.Follow | QuickActions.Quit | QuickActions.Sleep);
    }

    private async Task QueueQuitAllAsync(SlashContext context, string cancelReason)
    {
        // Same gate-wait headroom reasoning as save-exit-all above, not yet applied here
        // until issue #24: quit_d2r's own work (focus, Alt+F4, 2s settle) is fast, but it
        // shares _commandGate with whatever a join-auto retry loop (or any other 210s-class
        // command) is mid-attempt on. A real run showed quit_d2r failing for 2/3 accounts
        // with "exceeded agent-side timeout of 25s" while join-auto was actively retrying a
        // join in the background - the gate was always going to free up, just not within 30s.
        //
        // Acknowledge first: the follow-auto cancel below writes to the database, and the
        // connectivity snapshot QueueAllCommandsAsync takes is not free either. Callers that
        // already deferred (the follow-auto stop buttons, which mean to replace their own
        // message) keep the acknowledgement they chose.
        await EnsureAcknowledgedAsync(context);
        await CancelJoinAutoIfRunningAsync(cancelReason);
        await CancelDcloneParkIfRunningAsync(cancelReason);
        var followAutoCancel = await CancelFollowAutoIfRunningAsync(cancelReason);
        QueueFollowAutoStopSignal(followAutoCancel.RunId);
        // After a quit the clients are cold, so the completion follow-up re-offers both
        // Ready (relaunch) and Follow - plus Sleep, since winding the host machine down is
        // the other natural next step once every client is closed (its quit-all leg is a
        // fast no-op at that point).
        await QueueAllCommandsAsync(
            context,
            "quit_d2r",
            (accountKey, account) => BuildAccountArgs(accountKey, account),
            TimeSpan.FromSeconds(210),
            displayName: "quit",
            offerOnCompletion: QuickActions.Follow | QuickActions.Ready | QuickActions.Sleep);
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
            // Deliberately a separate verb from stop rather than a flag on it. This is an
            // uncontrolled power cut - the guest gets no chance to flush anything - so it should
            // never be reachable by mistyping the ordinary stop.
            "turnoff" => "vm_turnoff",
            "reboot" => "vm_reboot",
            "snapshot" => "vm_snapshot",
            _ => throw new InvalidOperationException($"Unsupported VM subcommand: {context.SubcommandName}")
        };

        await DeferIfInteractiveAsync(context);
        var args = JsonSerializer.SerializeToElement(new
        {
            accountKey,
            vmName,
            snapshotName = context.GetString("name")
        });
        await QueueDiscordWork(context, $"vm {commandName}", async () =>
        {
            var result = await _hyperV.HandleCommandAsync(
                account,
                new CommandRequest(Guid.NewGuid().ToString("N"), commandName, args),
                CancellationToken.None);
            await ModifyOriginalResponseWithMetricsAsync(context, FormatCommandResult(result.Ok, result.Message));
        });
    }

    // Every branch here is a database round-trip and nothing else, which is exactly why this
    // handler never deferred - and exactly why it has to. A synchronous SQLite call is the one
    // kind of "instant" work that can block for seconds when the host is busy elsewhere, and
    // until it returns nothing has answered Discord.
    private async Task HandleGameAsync(SlashContext context)
    {
        await EnsureAcknowledgedAsync(context);
        switch (context.SubcommandName)
        {
            case "set":
                var game = _db.SetActiveGame(
                    context.GetRequiredString("name"),
                    BlankToNull(context.GetString("password")),
                    context.GetString("difficulty"),
                    BlankToNull(context.GetString("notes")),
                    context.ActorId);
                await SetInitialCommandResponseAsync(context, $"Stored current game:\n{FormatActiveGame(game)}", ephemeral: true);
                return;
            case "show":
                var stored = _db.GetActiveGame();
                await SetInitialCommandResponseAsync(
                    context,
                    stored is null ? "No current game is stored." : FormatActiveGame(stored),
                    ephemeral: true);
                return;
            case "clear":
                var cleared = _db.ClearActiveGame();
                await SetInitialCommandResponseAsync(
                    context,
                    cleared ? "Cleared the stored game." : "No current game was stored.",
                    ephemeral: true);
                return;
            default:
                // Now that this handler acknowledges up front, falling out of the switch would
                // leave the interaction deferred forever. Throwing reaches OnSlashCommandAsync,
                // which edits the deferred response into the error - same as HandleVmAsync.
                throw new InvalidOperationException(
                    $"Unsupported game subcommand: {context.SubcommandName}");
        }
    }

    private async Task HandleSystemAsync(SlashContext context)
    {
        var action = HostSystemPowerActions.ParseAction(context.SubcommandName);
        var requestedNode = BlankToNull(context.GetString("node"));
        var requestedAll = context.GetBool("all");
        if (requestedAll == true && requestedNode is not null)
        {
            await RespondWithMetricsAsync(
                context,
                "Pass either `node` or `all:true`, not both.");
            return;
        }

        var allNodes = HostSystemPowerActions.ResolveEveryNode(
            action,
            requestedAll,
            requestedNode is not null);

        string[] targets;
        if (allNodes)
        {
            // Queue workers first. If the master is also being powered down it must be
            // last or it could disappear before forwarding the worker commands.
            targets = GetOrderedOnlineNodeTargets();
        }
        else
        {
            var target = requestedNode ?? _config.NodeId;
            if (!_hyperV.IsKnownNode(target))
            {
                await RespondWithMetricsAsync(context, $"Unknown D2RHost node `{target}`.");
                return;
            }

            if (!_hyperV.IsNodeOnline(target))
            {
                await RespondWithMetricsAsync(
                    context,
                    $"D2RHost worker `{target}` is offline; {action.ToString().ToLowerInvariant()} was not queued.");
                return;
            }

            targets = [target];
        }

        // Surface the escape hatch only when the fleet-wide default actually widened the
        // action past the master, so a single-node fleet does not get told about a flag
        // that would change nothing.
        var narrowingHint = requestedAll is null && allNodes && targets.Length > 1
            ? $" Pass `all:false` to {action.ToString().ToLowerInvariant()} the master alone."
            : "";
        await RespondWithMetricsAsync(
            context,
            $"Queueing {action.ToString().ToLowerInvariant()} on {targets.Length} node(s): "
                + $"{string.Join(", ", targets)}. Each node first records and stops its configured Running VMs, then restores only that recorded set after resume or boot."
                + (allNodes ? FormatOfflineNodeSkipSuffix(targets) : "")
                + narrowingHint);
        await AnnounceSystemPowerActionAsync(context, action);

        var results = await QueueSystemActionsAsync(targets, action);
        var failures = results.Where(result => !result.Result.Ok).ToArray();
        if (failures.Length > 0)
        {
            await ModifyOriginalResponseWithMetricsAsync(
                context,
                $"Queued {action.ToString().ToLowerInvariant()} on {results.Count - failures.Length}/{results.Count} node(s). "
                    + "Skipped/failed: "
                    + string.Join("; ", failures.Select(failure => $"{failure.NodeId}: {failure.Result.Message}")));
        }
    }

    private async Task AnnounceSystemPowerActionAsync(SlashContext context, HostSystemPowerAction action)
    {
        if (context.Channel is not { } announceChannel)
        {
            return;
        }

        try
        {
            await announceChannel.SendMessageAsync(
                AppendMetrics(
                    context,
                    HostSystemPowerActions.FormatDiscordAnnouncement(
                        action,
                        context.ActorLabel)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not announce host system action {Action} in Discord.", action);
        }
    }

    private async Task HandleConfigAsync(SlashContext context)
    {
        switch (context.SubcommandName)
        {
            case "show":
                await RespondWithMetricsAsync(context, FormatRuntimeConfig());
                return;
            case "stagger":
                var seconds = context.GetRequiredInt("seconds");
                _config.StartAllDelaySeconds = seconds;
                _config.ClientStaggerSeconds = seconds;
                await SaveConfigAndRespawnAsync(
                    context,
                    $"Set all-client stagger to {seconds}s.");
                return;
            case "api":
                await HandleConfigApiAsync(context);
                return;
            case "notifications":
                var enabled = context.GetRequiredBool("enabled");
                var updatesEnabled = context.GetBool("updates-enabled");
                var channelText = BlankToNull(context.GetString("channel-id"));
                if (channelText is not null)
                {
                    _config.GuildChannel = ParseChannelId(channelText);
                }

                if ((enabled || updatesEnabled == true) && _config.GuildChannel is null)
                {
                    await RespondWithMetricsAsync(context, "channel-id is required when enabling notifications.");
                    return;
                }

                _config.GameSessionNotificationsEnabled = enabled;
                if (updatesEnabled is not null)
                {
                    _config.UpdateNotificationsEnabled = updatesEnabled.Value;
                }

                await SaveConfigAndRespawnAsync(
                    context,
                    FormatNotificationConfigSavedMessage());
                return;
            default:
                throw new InvalidOperationException($"Unsupported config subcommand: {context.SubcommandName}");
        }
    }

    /// <summary>
    /// Turns the HTTP command API on or off, minting a key the first time and only replacing an
    /// existing one when explicitly told to.
    /// </summary>
    /// <remarks>
    /// The key is shown exactly once, here, because only its hash is kept. Replacing it is
    /// gated behind <c>overwrite</c> for the obvious reason: whatever is already driving this host
    /// stops working the moment a new key is minted, and that should never be a side effect of
    /// re-running a command to check whether the API is on.
    ///
    /// Unlike the other `/d2r config` subcommands this saves without respawning. The response
    /// carries a secret the operator can never be shown again, and tearing the process down one
    /// second after sending it is a needless way to lose it.
    /// </remarks>
    private async Task HandleConfigApiAsync(SlashContext context)
    {
        await EnsureAcknowledgedAsync(context);

        if (!_config.IsMaster)
        {
            await SetInitialCommandResponseAsync(
                context,
                $"This node runs in {_config.Mode} mode. The HTTP command API is master-only: a worker "
                    + "relays commands for its own VMs but has no view of the fleet, so it cannot answer for one.",
                ephemeral: true);
            return;
        }

        var enabled = context.GetRequiredBool("enabled");
        var overwrite = context.GetBool("overwrite") == true;
        var hasKey = !string.IsNullOrWhiteSpace(_config.Api.KeyHash);

        if (!enabled)
        {
            // The key is deliberately left in place: turning the API off for the night should not
            // force every caller to be re-keyed in the morning. `overwrite` is how a key is retired.
            _config.Api.Enabled = false;
            HostConfigLoader.Save(_runtime.ConfigPath, _config);
            await SetInitialCommandResponseAsync(
                context,
                "HTTP command API disabled. Every /api request now returns 503. "
                    + (hasKey
                        ? "The existing key is kept, so re-enabling does not require re-keying callers."
                        : "No key is stored.")
                    + $"\nSaved `{_runtime.ConfigPath}`.",
                ephemeral: true);
            return;
        }

        if (hasKey && !overwrite)
        {
            _config.Api.Enabled = true;
            HostConfigLoader.Save(_runtime.ConfigPath, _config);
            await SetInitialCommandResponseAsync(
                context,
                $"HTTP command API enabled, still using the existing key `{_config.Api.KeyId}`. "
                    + "I cannot show that key again - only its hash is stored. "
                    + "To mint a replacement, run `/d2r config api enabled:true overwrite:true`; "
                    + "the current key stops working the moment you do.",
                ephemeral: true);
            return;
        }

        // Only Discord mints. Over HTTP this reply is the response body, so the replacement key
        // would go straight back to whoever holds the current one - a leaked key could rotate
        // itself and lock out the operator, which is the one thing re-keying exists to undo.
        // (A caller without a key never reaches here: the API is closed until one exists.)
        if (context.IsApi)
        {
            await SetInitialCommandResponseAsync(
                context,
                "API keys are only minted from Discord, so a key can never replace itself. "
                    + "Run `/d2r config api enabled:true overwrite:true` there.",
                ephemeral: true);
            return;
        }

        // Captured before the overwrite below: the "previous key" line names the key being retired,
        // and reading it back off the config afterwards would name the replacement instead.
        var replacedKeyId = _config.Api.KeyId;
        var generated = HostApiKey.Generate();
        _config.Api.Enabled = true;
        _config.Api.KeyHash = generated.Hash;
        _config.Api.KeyId = generated.KeyId;
        _config.Api.KeyCreatedUtc = DateTimeOffset.UtcNow;
        HostConfigLoader.Save(_runtime.ConfigPath, _config);
        _logger.LogWarning(
            "A new HTTP API key was minted ({KeyId}) and the API was enabled.",
            generated.KeyId);

        await SetInitialCommandResponseAsync(
            context,
            (hasKey
                ? $"Replaced the HTTP API key. The previous key (`{replacedKeyId}`) no longer works.\n\n"
                : "HTTP command API enabled.\n\n")
                + $"**Copy this now - it is shown once and only its hash is stored:**\n```\n{generated.Key}\n```\n"
                + $"Use it against `http://<this-host>:{_config.HttpPort}`:\n"
                + $"```\ncurl -s -H \"X-API-Key: {generated.Key}\" http://<this-host>:{_config.HttpPort}/api/commands\n```\n"
                + "`Authorization: Bearer <key>` works too. "
                + $"Saved `{_runtime.ConfigPath}`; the API is live now, no restart needed.",
            ephemeral: true);
    }

    private async Task HandleRestartAsync(SlashContext context)
    {
        await RespondWithMetricsAsync(
            context,
            "Respawning D2RHost. Startup self-update will run before Discord reconnects.");
        QueueHostRespawn();
    }

    private string FormatRuntimeConfig()
    {
        var stagger = _config.ClientStaggerSeconds ?? _config.StartAllDelaySeconds;
        var notifications = _config.GameSessionNotificationsEnabled
            ? $"enabled in {_config.GuildChannel?.ToString() ?? "(no channel)"}"
            : "disabled";
        var updateNotifications = _config.UpdateNotificationsEnabled
            ? $"enabled in {_config.GuildChannel?.ToString() ?? "(no channel)"}"
            : "disabled";
        return string.Join("\n", new[]
        {
            $"Config path: {_runtime.ConfigPath}",
            $"Mode: {_config.Mode}",
            $"Node: {_config.NodeId}",
            $"All-client stagger: {stagger}s",
            $"Session notifications: {notifications}",
            $"Update notifications: {updateNotifications}",
            $"HTTP command API: {FormatApiConfig()}"
        });
    }

    private string FormatApiConfig()
    {
        if (!_config.IsMaster)
        {
            return "unavailable on a worker node";
        }

        if (!_config.Api.Enabled)
        {
            return string.IsNullOrWhiteSpace(_config.Api.KeyHash)
                ? "disabled, no key minted"
                : $"disabled, key {_config.Api.KeyId} retained";
        }

        var created = _config.Api.KeyCreatedUtc is { } createdUtc
            ? $", minted {createdUtc:yyyy-MM-dd}"
            : "";
        return $"enabled on port {_config.HttpPort}, key {_config.Api.KeyId}{created}";
    }

    private string FormatNotificationConfigSavedMessage()
    {
        var channel = _config.GuildChannel?.ToString() ?? "(no channel)";
        return "Updated notification settings: "
            + $"game sessions {FormatEnabled(_config.GameSessionNotificationsEnabled)}, "
            + $"updates {FormatEnabled(_config.UpdateNotificationsEnabled)}, "
            + $"channel {channel}.";
    }

    private static string FormatEnabled(bool value)
    {
        return value ? "enabled" : "disabled";
    }

    private async Task SaveConfigAndRespawnAsync(SlashContext context, string message)
    {
        // Writing the config file is disk I/O on the gateway task, so acknowledge ahead of it.
        await EnsureAcknowledgedAsync(context);
        HostConfigLoader.Save(_runtime.ConfigPath, _config);
        await SetInitialCommandResponseAsync(
            context,
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

    private async Task RunWatchedFollowCommandAsync(
        SlashContext context,
        string accountKey,
        AccountConfig account,
        object args)
    {
        await EnsureAcknowledgedAsync(context);
        await QueueDiscordWork(context, "menu_join_friend", async () =>
        {
            var watchCts = new CancellationTokenSource();
            var entries = new[] { new KeyValuePair<string, AccountConfig>(accountKey, account) };
            var watchTask = RunGameAllWatchTickerAsync(context, "follow", accountKey, entries, watchCts.Token);

            try
            {
                await RunVmCommandDeferredAsync(
                    context,
                    account,
                    "menu_join_friend",
                    args,
                    TimeSpan.FromSeconds(210),
                    readyFirstIfNotMenuReady: true);
            }
            finally
            {
                watchCts.Cancel();
                await AwaitWatchTickerStopAsync(watchTask, "follow");
            }
        });
    }

    private async Task RunVmCommandAsync(
        SlashContext context,
        AccountConfig account,
        string commandName,
        object args,
        TimeSpan? timeout = null,
        bool readyFirstIfNotMenuReady = false)
    {
        // EnsureAcknowledgedAsync, not DeferAsync: a caller that had its own pre-flight work to do
        // (quit cancels follow-auto first) acknowledges before that work, and deferring twice
        // throws.
        await EnsureAcknowledgedAsync(context);
        await QueueDiscordWork(context, commandName, () => RunVmCommandDeferredAsync(
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
                await ModifyOriginalResponseWithMetricsAsync(
                    context,
                    "This client needed `/d2r ready` before menu automation, but ready did not return: "
                        + FormatExceptionWithAccountStatus(ex, account));
                return;
            }
        }

        if (readyResult?.Ok == false)
        {
            await ModifyOriginalResponseWithMetricsAsync(
                context,
                "This client needed `/d2r ready` before menu automation, but ready failed: "
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
            await ModifyOriginalResponseWithMetricsAsync(
                context,
                $"Command `{commandName}` did not return: {FormatExceptionWithAccountStatus(ex, account)}");
            return;
        }

        var prefix = readyResult is null
            ? ""
            : $"Ran `/d2r ready` before menu automation: {FormatCommandResult(readyResult.Ok, readyResult.Message)}\n";
        await ModifyOriginalResponseWithMetricsAsync(context, prefix + FormatCommandResult(result.Ok, result.Message));
    }

    private async Task RunScreenshotAsync(SlashContext context, AccountConfig account, object args)
    {
        await EnsureAcknowledgedAsync(context);
        await QueueDiscordWork(context, "screenshot", () => RunScreenshotDeferredAsync(context, account, args));
    }

    private async Task RunScreenshotDeferredAsync(SlashContext context, AccountConfig account, object args)
    {
        var result = await _registry.SendCommandAsync(account.AgentId, "screenshot", args, TimeSpan.FromSeconds(60));
        if (!result.Ok || result.Data is not { } data)
        {
            await ModifyOriginalResponseWithMetricsAsync(context, FormatCommandResult(result.Ok, result.Message));
            return;
        }

        if (!TryReadScreenshot(data, out var bytes, out var extension))
        {
            await ModifyOriginalResponseWithMetricsAsync(context, FormatCommandResult(false, "Screenshot result did not contain image data."));
            return;
        }

        var fileName = $"{account.AgentId}-screenshot.{extension}";
        if (context.Api is { } sink)
        {
            // An HTTP caller gets the image inline, base64 in the JSON body, rather than a
            // Discord attachment it has no way to fetch.
            sink.AttachFile(fileName, extension == "jpg" ? "image/jpeg" : "image/png", bytes);
            sink.SetMessage(result.Message);
            return;
        }

        await using var stream = new MemoryStream(bytes);
        await context.Interaction!.FollowupWithFileAsync(
            stream,
            fileName,
            AppendMetrics(context, result.Message),
            ephemeral: true);
        await ModifyOriginalResponseWithMetricsAsync(context, "Screenshot attached.");
    }

    /// <summary>
    /// Runs the slow half of a command off the gateway task.
    /// </summary>
    /// <remarks>
    /// Discord can afford fire-and-forget here because the handler already deferred and the real
    /// answer arrives later as a message edit. An HTTP caller has no later - its response body is
    /// serialized the moment the handler returns - so an API context waits for the work instead.
    /// This one branch is what makes every single-client command synchronous over HTTP without
    /// touching any of the commands themselves.
    /// </remarks>
    private Task QueueDiscordWork(SlashContext context, string operationName, Func<Task> work)
    {
        if (context.IsApi)
        {
            return RunCommandWorkAsync(context, operationName, work);
        }

        _ = Task.Run(() => RunCommandWorkAsync(context, operationName, work));
        return Task.CompletedTask;
    }

    private async Task RunCommandWorkAsync(SlashContext context, string operationName, Func<Task> work)
    {
        try
        {
            await work();
        }
        // An HTTP caller is awaiting this inline, so the failure goes back up to
        // ExecuteApiCommandAsync and becomes ok:false with the exception type as the error.
        // Answering it here would bury it in an ok:true "Command failed: ..." message instead.
        catch (Exception ex) when (!context.IsApi)
        {
            _logger.LogError(ex, "Discord command background work failed for {OperationName}.", operationName);
            await SetCommandResponseSafeAsync(context, $"Command failed: {ex.Message}");
        }
    }

    private async Task SetCommandResponseSafeAsync(SlashContext context, string content)
    {
        try
        {
            if (context.HasResponded)
            {
                await ModifyOriginalResponseWithMetricsAsync(context, content);
            }
            else
            {
                await RespondWithMetricsAsync(context, content);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not update Discord command response after background failure.");
        }
    }

    private Task SetInitialCommandResponseAsync(SlashContext context, string content, bool ephemeral)
    {
        return context.HasResponded
            ? ModifyOriginalResponseWithMetricsAsync(context, content)
            : RespondWithMetricsAsync(context, content, ephemeral);
    }

    private async Task QueueCreateGameAllAsync(SlashContext context, GameInput game, bool watch)
    {
        var connectivity = _registry.GetAccountConnectivity();
        var entries = connectivity.Online;
        var offlineEntries = connectivity.Offline;
        if (entries.Length == 0)
        {
            await SetInitialCommandResponseAsync(
                context,
                "No online accounts are available for create-game-all."
                    + FormatOfflineSkipSuffix(offlineEntries, connectivity.ConnectedUnaddressableAgents),
                ephemeral: true);
            return;
        }

        var creator = entries[0];
        var joiners = entries.Skip(1).ToArray();
        var staggerSeconds = _config.ClientStaggerSeconds ?? _config.StartAllDelaySeconds;
        var readyFirstCount = entries.Count(entry => ShouldRunReadyFirst(entry.Value));

        // SetInitialCommandResponseAsync, not RespondWithMetricsAsync: every caller acknowledges
        // before resolving the game name out of the database, so the interaction already has a
        // response to edit by the time this runs.
        await SetInitialCommandResponseAsync(
            context,
            $"Queued create-game-all for {entries.Length} online account(s). {creator.Key} will create {game.GameName}; "
                + $"{entries.Length} account(s) will warm up with {staggerSeconds}s stagger; "
                + $"{joiners.Length} joiner(s) will prepare Join Game while {creator.Key} creates."
                + FormatReadyFirstSuffix(readyFirstCount)
                + FormatOfflineSkipSuffix(offlineEntries, connectivity.ConnectedUnaddressableAgents),
            ephemeral: true);

        await StartGameSessionAsync(
            context,
            game.GameName,
            entries.Length,
            $"Queued create-game-all. {creator.Key} will create; {joiners.Length} account(s) will join.",
            creator.Value.AgentId);

        var watchCts = new CancellationTokenSource();
        Task? watchTask = null;
        if (watch)
        {
            watchTask = RunGameAllWatchTickerAsync(context, "create-game-all", game.GameName, entries, watchCts.Token);
        }

        var orchestration = Task.Run(async () =>
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
                // got created, the same way a manual /d2r game set would - not just whatever was
                // true before this run started.
                _db.SetActiveGame(game.GameName, game.Password, game.Difficulty, notes: "create-game-all", context.ActorId);

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
                await AwaitWatchTickerStopAsync(watchTask, "create-game-all");
            }
        });

        // Same reason as the all-client fan-out: an HTTP caller's response body closes when this
        // returns, so it waits for the create/join flow rather than being told only that it started.
        if (context.IsApi)
        {
            await orchestration;
        }
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
        // Watch output is a live Discord message and nothing else. With nowhere to post it, the
        // command still runs; only the diagnostics the caller opted into are unavailable.
        if (context.Channel is not { } channel)
        {
            return;
        }

        await RunGameAllWatchTickerAsync(
            channel,
            context.MetricsEnabled,
            label,
            gameName,
            () => entries,
            cancellationToken);
    }

    private async Task RunGameAllWatchTickerAsync(
        SlashContext context,
        string label,
        string gameName,
        Func<KeyValuePair<string, AccountConfig>[]> getEntries,
        CancellationToken cancellationToken)
    {
        if (context.Channel is not { } channel)
        {
            return;
        }

        await RunGameAllWatchTickerAsync(
            channel,
            context.MetricsEnabled,
            label,
            gameName,
            getEntries,
            cancellationToken);
    }

    private async Task RunGameAllWatchTickerAsync(
        IMessageChannel channel,
        bool metricsEnabled,
        string label,
        string gameName,
        Func<KeyValuePair<string, AccountConfig>[]> getEntries,
        CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var logPath = GetWatchLogPath(gameName, startedUtc);
        AppendWatchLogTick(logPath, new[] { FormatWatchHeader(label, gameName, startedUtc), "Starting..." });

        IUserMessage? message = null;
        try
        {
            message = await channel.SendMessageAsync(
                AppendMetrics(metricsEnabled, FormatWatchHeader(label, gameName, startedUtc) + "\nStarting..."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start {Label}-watch message.", label);
        }

        while (true)
        {
            var entries = getEntries();
            var lines = await Task.WhenAll(
                entries.Select(entry => FormatAccountWatchLineAsync(entry.Key, entry.Value, CancellationToken.None)));
            var content = string.Join("\n", new[] { FormatWatchHeader(label, gameName, startedUtc) }.Concat(lines));

            if (message is not null)
            {
                try
                {
                    await message.ModifyAsync(properties => properties.Content = AppendMetrics(metricsEnabled, content));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not update {Label}-watch message.", label);
                }
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

        await SendWatchLogAttachmentAsync(channel, metricsEnabled, gameName, logPath);
    }

    private async Task AwaitWatchTickerStopAsync(Task? watchTask, string label)
    {
        if (watchTask is null)
        {
            return;
        }

        try
        {
            await watchTask;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Label}-watch ticker stopped with an error.", label);
        }
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
        if (context.Channel is not { } channel)
        {
            return;
        }

        await SendWatchLogAttachmentAsync(
            channel,
            context.MetricsEnabled,
            gameName,
            logPath);
    }

    private async Task SendWatchLogAttachmentAsync(
        IMessageChannel channel,
        bool metricsEnabled,
        string gameName,
        string logPath)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        try
        {
            await channel.SendFileAsync(logPath, AppendMetrics(metricsEnabled, $"Full watch log for {gameName}."));
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

    // Shown only while degraded, like the settings segment. A bounded-call slot is released in a
    // finally block, so the only way to be short of them is a capture that never returned - the
    // one condition that makes a client refuse every menu click for the rest of the agent's life
    // while every other field on this line still reads healthy.
    private static bool TryReadDegradedBoundedCallSlots(string? json, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetInt(document.RootElement, "freeBoundedCallSlots", out var free)
                || !TryGetInt(document.RootElement, "boundedCallSlots", out var total)
                || total <= 0
                || free >= total)
            {
                return false;
            }

            value = $"{free}/{total}";
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
        if (!await ShouldRunReadyFirstLiveAsync(entry.Value))
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
        var connectivity = _registry.GetAccountConnectivity();
        var entries = connectivity.Online;
        var offlineEntries = connectivity.Offline;
        if (entries.Length == 0)
        {
            await SetInitialCommandResponseAsync(
                context,
                "No online accounts are available for join-all."
                    + FormatOfflineSkipSuffix(offlineEntries, connectivity.ConnectedUnaddressableAgents),
                ephemeral: true);
            return;
        }

        // So a later plain join-all/create-game-all (no flags) sees what this run actually
        // joined - the game already exists by definition, so this is true regardless of whether
        // every account's join below succeeds. This is a synchronous database write, which is why
        // both callers acknowledge before they get here.
        _db.SetActiveGame(game.GameName, game.Password, game.Difficulty, notes: "join-all", context.ActorId);

        var staggerSeconds = _config.ClientStaggerSeconds ?? _config.StartAllDelaySeconds;
        var readyFirstCount = entries.Count(entry => ShouldRunReadyFirst(entry.Value));
        await SetInitialCommandResponseAsync(
            context,
            $"Queued join-all for {entries.Length} online account(s) into {game.GameName} with {staggerSeconds}s stagger."
                + " Accounts will prepare Join Game first, then submit."
                + FormatReadyFirstSuffix(readyFirstCount)
                + FormatOfflineSkipSuffix(offlineEntries, connectivity.ConnectedUnaddressableAgents),
            ephemeral: true);

        await StartGameSessionAsync(
            context,
            game.GameName,
            entries.Length,
            $"Queued join-all for {entries.Length} account(s).",
            entries[0].Value.AgentId);

        var watchCts = new CancellationTokenSource();
        Task? watchTask = null;
        if (watch)
        {
            watchTask = RunGameAllWatchTickerAsync(context, "join-all", game.GameName, entries, watchCts.Token);
        }

        var orchestration = Task.Run(async () =>
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
                await AwaitWatchTickerStopAsync(watchTask, "join-all");
            }
        });

        // Same reason as the all-client fan-out: an HTTP caller's response body closes when this
        // returns, so it waits for the create/join flow rather than being told only that it started.
        if (context.IsApi)
        {
            await orchestration;
        }
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

    private async Task StartGameSessionAsync(
        SlashContext context,
        string gameName,
        int expected,
        string status,
        string representativeAgentId)
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
            _activeSessionMetricsEnabled = context.MetricsEnabled;
            _activeSessionMessage = await channel.SendMessageAsync(
                AppendMetrics(_activeSessionMetricsEnabled, await FormatGameSessionMessageAsync(status, detail: null)),
                components: BuildGameSessionActionComponents());
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

            var content = AppendMetrics(_activeSessionMetricsEnabled, await FormatGameSessionMessageAsync(status, detail));
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
            var content = AppendMetrics(_activeSessionMetricsEnabled, await FormatGameSessionMessageAsync(status, detail: null));
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
            var content = AppendMetrics(_activeSessionMetricsEnabled, await FormatGameSessionMessageAsync(status, detail));
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

    private IMessageChannel? GetUpdateNotificationChannel()
    {
        if (!_config.UpdateNotificationsEnabled || _config.GuildChannel is not { } channelId)
        {
            return null;
        }

        var channel = _client.GetChannel(channelId) as IMessageChannel;
        if (channel is null)
        {
            _logger.LogWarning("Update notifications are enabled, but channel {ChannelId} is not visible.", channelId);
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
            await FollowupWithMetricsAsync(context, message);
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
        string? displayName = null,
        bool watch = false,
        string? watchLabel = null,
        string? watchName = null,
        QuickActions offerOnCompletion = QuickActions.None)
    {
        var label = displayName ?? commandName;
        var connectivity = _registry.GetAccountConnectivity();
        var entries = connectivity.Online;
        var offlineEntries = connectivity.Offline;
        if (entries.Length == 0)
        {
            await SetInitialCommandResponseAsync(
                context,
                $"No online accounts are available for {label}."
                    + FormatOfflineSkipSuffix(offlineEntries, connectivity.ConnectedUnaddressableAgents),
                ephemeral: true);
            return;
        }

        var staggerSeconds = _config.ClientStaggerSeconds ?? _config.StartAllDelaySeconds;
        var readyFirstCount = readyFirstIfNotMenuReady
            ? entries.Count(entry => ShouldRunReadyFirst(entry.Value))
            : 0;
        await SetInitialCommandResponseAsync(
            context,
            $"Queued {entries.Length} online {label} command(s) with {staggerSeconds}s stagger."
                + FormatReadyFirstSuffix(readyFirstCount)
                + FormatOfflineSkipSuffix(offlineEntries, connectivity.ConnectedUnaddressableAgents),
            ephemeral: true);

        var tracker = new FanInCompletionTracker(entries.Length);
        var skippedAfterQueue = 0;
        var watchCts = watch ? new CancellationTokenSource() : null;
        var watchTask = watchCts is null
            ? null
            : RunGameAllWatchTickerAsync(
                context,
                watchLabel ?? label,
                watchName ?? label,
                entries,
                watchCts.Token);

        var dispatches = entries.Select((entry, index) => Task.Run(async () =>
            {
                var ok = true;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(index * staggerSeconds));
                    if (_registry.GetAgent(entry.Value.AgentId)?.Connected != true)
                    {
                        Interlocked.Increment(ref skippedAfterQueue);
                        await SendFollowupSafeAsync(
                            context,
                            $"{label} skipped for {entry.Key}: its node or VM agent went offline before dispatch.");
                        return;
                    }

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
                        watchCts?.Cancel();
                        await AwaitWatchTickerStopAsync(watchTask, watchLabel ?? label);
                        watchCts?.Dispose();
                        await SendQueueCompletionFollowupAsync(
                            context,
                            label,
                            entries.Length,
                            tracker.FailedCount,
                            Volatile.Read(ref skippedAfterQueue),
                            offerOnCompletion);
                    }
                }
            }))
            .ToArray();

        // Discord watches the follow-ups arrive; an HTTP caller has one response body and needs
        // the per-account outcomes to be in it, so it waits for the whole fan-out.
        if (context.IsApi)
        {
            await Task.WhenAll(dispatches);
        }
    }

    // join-all/create-game-all already signal completion with a checkmark/X reaction on their
    // public session message (see CompleteGameSessionAsync) - save-exit-all/start-all/quit-all
    // (all routed through QueueAllCommandsAsync) had no equivalent "everyone's
    // done" signal, only ad-hoc per-VM failure follow-ups, so a fully successful run looked
    // identical to one nobody had checked on yet. The reaction has to land on a fresh
    // non-ephemeral follow-up rather than the initial ack: that ack is sent ephemeral (visible
    // only to the invoker), and Discord does not support reacting to ephemeral messages.
    private async Task SendQueueCompletionFollowupAsync(
        SlashContext context,
        string label,
        int total,
        int failed,
        int skipped,
        QuickActions offer = QuickActions.None)
    {
        try
        {
            var succeeded = Math.Max(total - failed - skipped, 0);
            var skippedSuffix = skipped == 0 ? "" : $", {skipped} skipped offline";
            var message = failed == 0
                ? $"{label} complete: {succeeded} succeeded{skippedSuffix}."
                : $"{label} complete: {succeeded} succeeded, {failed} failed{skippedSuffix} (see above).";
            var components = offer != QuickActions.None
                ? BuildQuickStartComponents(offer)
                : null;
            var sent = await FollowupWithMetricsAsync(context, message, ephemeral: false, components);
            if (sent is null)
            {
                // An API caller already has this text in its response body; the reaction and the
                // quick-action buttons are Discord-only affordances with nothing to attach to.
                return;
            }

            await sent.AddReactionAsync(new Emoji(failed == 0 ? "✅" : "⛔"));
            if (components is not null)
            {
                await AdoptQuickButtonsHolderAsync(sent, offer);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not send queue-completion follow-up for {Label}.", label);
        }
    }

    // Makes a freshly sent message the one-and-only quick-start button holder, stripping the
    // buttons off whichever message held them before.
    private async Task AdoptQuickButtonsHolderAsync(IUserMessage message, QuickActions offer)
    {
        IUserMessage? previousHolder;
        bool previousIsStartupMessage;
        await _startupMessageLock.WaitAsync();
        try
        {
            previousHolder = _quickButtonsMessage;
            _quickButtonsMessage = message;
            _quickActionsOffered = offer;
            previousIsStartupMessage = previousHolder is not null
                && _startupMessage is { } startup
                && previousHolder.Id == startup.Id;
        }
        finally
        {
            _startupMessageLock.Release();
        }

        if (previousHolder is null || previousHolder.Id == message.Id)
        {
            return;
        }

        if (previousIsStartupMessage)
        {
            await RefreshStartupMessageAsync();
        }
        else
        {
            await StripComponentsSafeAsync(previousHolder, "superseded quick-start message");
        }
    }

    private (KeyValuePair<string, AccountConfig>[] Online, KeyValuePair<string, AccountConfig>[] Offline) GetAccountEntriesByConnectivity()
    {
        var connectivity = _registry.GetAccountConnectivity();
        return (connectivity.Online, connectivity.Offline);
    }

    private async Task<CommandResultInfo?> SendReadyIfNotMenuReadyAsync(
        AccountConfig account,
        object args,
        CancellationToken cancellationToken = default)
    {
        if (!await ShouldRunReadyFirstLiveAsync(account, cancellationToken))
        {
            return null;
        }

        try
        {
            var result = await _registry.SendCommandAsync(
                account.AgentId,
                "menu_ready",
                args,
                ReadyCommandTimeout,
                cancellationToken);
            return result.Ok || await ShouldRunReadyFirstLiveAsync(account, cancellationToken)
                ? result
                : ReadySucceededFromCurrentStatus(account, result.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller stopped; a status fallback would report a ready nobody is waiting for.
            throw;
        }
        catch (Exception ex)
        {
            var agent = _registry.GetAgent(account.AgentId);
            if (agent?.Connected != true || await ShouldRunReadyFirstLiveAsync(account))
            {
                throw;
            }

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

    private async Task<bool> ShouldRunReadyFirstLiveAsync(
        AccountConfig account,
        CancellationToken cancellationToken = default)
    {
        var agent = _registry.GetAgent(account.AgentId);
        if (agent?.Connected != true)
        {
            return false;
        }

        if (await TryGetLiveStatusJsonAsync(account, cancellationToken) is { } statusJson)
        {
            return MenuReadyPolicy.ShouldRunReadyFirstFromStatusJson(connected: true, statusJson);
        }

        return ShouldRunReadyFirst(account);
    }

    private async Task<string?> TryGetLiveStatusJsonAsync(
        AccountConfig account,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _registry.SendCommandAsync(
                account.AgentId,
                "status",
                args: null,
                TimeSpan.FromSeconds(20),
                cancellationToken);

            return result.Ok && result.Data is { } data
                ? data.GetRawText()
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live status preflight failed for {AgentId}; falling back to cached readiness.", account.AgentId);
            return null;
        }
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

    internal static string FormatOfflineSkipSuffix(
        IReadOnlyCollection<KeyValuePair<string, AccountConfig>> offlineEntries,
        IReadOnlyCollection<FleetUnaddressableAgent>? connectedUnaddressableAgents = null)
    {
        var unavailableAgentWarning =
            connectedUnaddressableAgents is null || connectedUnaddressableAgents.Count == 0
                ? ""
                : " Connected VM agent(s) not addressable by a fleet account: "
                    + $"{string.Join(", ", connectedUnaddressableAgents.Select(FormatUnaddressableAgent))}; "
                    + "they are not command targets. Check the owning node's `accounts` mappings "
                    + "and fleet-wide account-key uniqueness.";
        var offlineAccountWarning = offlineEntries.Count == 0
            ? ""
            : $" Skipped {offlineEntries.Count} offline account(s): "
                + $"{string.Join(", ", offlineEntries.Select(entry => $"{entry.Key} -> {entry.Value.AgentId}"))}.";
        return unavailableAgentWarning + offlineAccountWarning;
    }

    // Leads with where the bind actually lives rather than with a distribution count. "4/4 online
    // accounts" reads like full fleet coverage when the fleet has nine, and it buries the part
    // that now matters: the host holds the authoritative copy, so the accounts that were not
    // reachable during the command are already accounted for rather than missed.
    private static string FormatBindOwnershipSummary(int distributed, int online, int offline)
    {
        var pending = offline > 0
            ? $"; the {offline} offline VM(s) sync automatically when they reconnect."
            : ", which is the whole fleet.";
        return $"The host owns this bind and pushed it to {distributed}/{online} online VM(s){pending}";
    }

    private static string FormatUnaddressableAgent(FleetUnaddressableAgent entry) =>
        string.IsNullOrWhiteSpace(entry.NodeId)
            ? entry.Id
            : $"{entry.NodeId}/{entry.Id}";

    internal static string[] FormatAccountConnectivityHealthLines(
        FleetAccountConnectivitySnapshot connectivity)
    {
        var accountCount = connectivity.Online.Length + connectivity.Offline.Length;
        var lines = new List<string>
        {
            $"Accounts: {connectivity.Online.Length}/{accountCount} available"
        };
        if (connectivity.ConnectedUnaddressableAgents.Length > 0)
        {
            lines.Add(
                "Connected VM agents not addressable by a fleet account: "
                    + string.Join(
                        ", ",
                        connectivity.ConnectedUnaddressableAgents.Select(FormatUnaddressableAgent)));
        }

        return lines.ToArray();
    }

    private object BuildMenuArgs(
        string accountKey,
        AccountConfig account,
        GameInput? game,
        SlashContext context,
        long? followAutoRunId = null)
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
            friendRow = context.GetInt("friend-row"),
            partyPosition = context.GetInt("bind-in-game"),
            followAutoRunId
        };
    }

    private static object BuildFollowAutoMenuArgs(
        string accountKey,
        AccountConfig account,
        FollowAutoRunOptions options,
        long followAutoRunId)
    {
        return new
        {
            accountKey,
            displayName = account.DisplayName ?? accountKey,
            vmName = account.VmName ?? account.AgentId,
            gameName = (string?)null,
            password = (string?)null,
            difficulty = (string?)null,
            characterSlot = options.CharacterSlot ?? account.CharacterSlot,
            friendRow = options.FriendRow,
            partyPosition = (int?)null,
            followAutoRunId
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

    private static object BuildSaveExitArgs(
        string accountKey,
        AccountConfig account,
        long? followAutoRunId)
    {
        return followAutoRunId is > 0
            ? new
            {
                accountKey,
                displayName = account.DisplayName ?? accountKey,
                vmName = account.VmName ?? account.AgentId,
                followAutoRunId
            }
            : BuildAccountArgs(accountKey, account);
    }

    private GameInput ResolveGameInput(SlashContext context)
    {
        var stored = _db.GetActiveGame();
        var gameName = BlankToNull(context.GetString("name")) ?? stored?.Name;
        if (string.IsNullOrWhiteSpace(gameName))
        {
            throw new InvalidOperationException("Game name is required. Pass name or set it first with /d2r game set.");
        }

        return new GameInput(
            gameName,
            BlankToNull(context.GetString("password")) ?? stored?.Password,
            context.GetString("difficulty") ?? stored?.Difficulty);
    }

    // create-game-all with no name (issue #20, items 3 and 5), in priority order: explicit flags
    // always win; otherwise a template mints a fresh numbered name every call (it never reuses
    // /d2r game show's stored value - that's what would stop netrunner1 -> netrunner2 from advancing);
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
            await RespondWithMetricsAsync(
                context,
                "join-auto needs a template first - set one with /d2r template name:<x> password:<y>.");
            return;
        }

        // join-auto's whole output is a live monitor message plus per-attempt posts, so it is
        // one of the two commands that genuinely cannot run with nowhere to post. Refused up
        // front rather than started and then silently mute.
        if (context.Channel is not { } joinAutoChannel)
        {
            await RespondWithMetricsAsync(context, NoChannelRefusal("join-auto"));
            return;
        }

        // The mirror of the check in StartDcloneParkAsync: every parked bot would be walked out of
        // its game into the template's, and the park would rebuild each one as a drop.
        if (IsDcloneParkRunning())
        {
            await RespondWithMetricsAsync(
                context,
                "A dclone park is running and owns the same VMs. Stop it with `/d2r dclone stop:true` first.");
            return;
        }

        await _joinAutoLock.WaitAsync();
        CancellationTokenSource cts;
        try
        {
            if (_joinAutoCts is not null)
            {
                await RespondWithMetricsAsync(
                    context,
                    "join-auto is already running. Use /d2r join-auto stop:true first.");
                return;
            }

            cts = new CancellationTokenSource();
            _joinAutoCts = cts;
        }
        finally
        {
            _joinAutoLock.Release();
        }

        await RespondWithMetricsAsync(
            context,
            $"join-auto started{(delaySeconds > 0 ? $" with a {delaySeconds}s delay before each join attempt" : "")}. Updates will post in this channel until it's stopped.",
            ephemeral: true);

        await StartJoinAutoMonitorAsync(joinAutoChannel, context.MetricsEnabled);

        _ = Task.Run(() => RunJoinAutoLoopAsync(context, joinAutoChannel, delaySeconds, watch, idleTimeout, cts.Token));
    }

    private async Task StopJoinAutoAsync(SlashContext context)
    {
        var wasRunning = await CancelJoinAutoIfRunningAsync(reason: null);
        await RespondWithMetricsAsync(
            context,
            wasRunning ? "join-auto is stopping." : "join-auto is not running.");
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

    /// <summary>
    /// Starts a Diablo Clone park: every rostered bot creates its own game and stays in it, and
    /// the loop rebuilds any park that falls over until the operator stops it.
    /// </summary>
    /// <remarks>
    /// The point is coverage, not a party. Diablo Clone walks into one game at a time, so holding
    /// N games open at once is N times the chance of catching a walk, and the monitor exists to
    /// hand out the name/password of whichever game it walks into.
    /// </remarks>
    private async Task StartDcloneParkAsync(SlashContext context, bool watch, string? handOffNote = null)
    {
        // Acknowledge before anything else, for the reason spelled out on StartFollowAutoAsync:
        // Discord.NET runs this inline on the gateway task, and reading fleet connectivity plus
        // posting a monitor message does not fit inside the three-second interaction deadline.
        await EnsureAcknowledgedAsync(context);

        // follow-auto drives the same VMs towards one shared game; a park drives each of them to
        // its own. Whichever started first keeps the fleet - silently interleaving the two would
        // just make both look broken.
        if (IsFollowAutoRunning())
        {
            await SetInitialCommandResponseAsync(
                context,
                "follow-auto is running and owns the same VMs. Stop it with `/d2r follow auto:false`, "
                    + "or press Park on its monitor to switch straight over.",
                ephemeral: true);
            return;
        }

        // join-auto walks the same VMs into template games; the park would rebuild every one of
        // them as a drop and the two would take turns undoing each other.
        if (IsJoinAutoRunning())
        {
            await SetInitialCommandResponseAsync(
                context,
                "join-auto is running and owns the same VMs. Stop it with `/d2r join auto:false` first.",
                ephemeral: true);
            return;
        }

        var connectivity = _registry.GetAccountConnectivity();
        var maxBots = context.GetInt("bots");
        var difficulty = BlankToNull(context.GetString("difficulty")) ?? DcloneDefaultDifficulty;
        var run = new DcloneParkRun(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            difficulty,
            DateTimeOffset.UtcNow,
            maxBots);
        // A preview for the reply only. The loop admits the roster itself - on its first pass and
        // on every pass after - so a VM that connects between here and there is still parked.
        var initialCount = DcloneParkPolicy.SelectNewcomers(
            connectivity.Online.Select(entry => entry.Key),
            Array.Empty<string>(),
            maxBots).Count;

        CancellationTokenSource? cts = null;
        TaskCompletionSource? unwound = null;
        await _dcloneLock.WaitAsync();
        try
        {
            if (_dcloneCts is null)
            {
                cts = new CancellationTokenSource();
                unwound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _dcloneCts = cts;
                _dcloneRun = run;
                _dcloneUnwound = unwound.Task;
                _dcloneMetricsEnabled = context.MetricsEnabled;
            }
        }
        finally
        {
            _dcloneLock.Release();
        }

        if (cts is null || unwound is null)
        {
            await SetInitialCommandResponseAsync(
                context,
                "A dclone park is already running. Stop it with `/d2r dclone stop:true` first.",
                ephemeral: true);
            return;
        }

        var staggerSeconds = _config.ClientStaggerSeconds ?? _config.StartAllDelaySeconds;
        var game = FormatDifficultyLabel(difficulty);
        var opening = initialCount > 0
            ? $"Parking {initialCount} bot(s) in {initialCount} separate {game} game(s) with {staggerSeconds}s stagger. "
                + "Each game gets its own random name and one-character password."
            : $"Park mode is on, but no VM is online yet. Each one gets its own {game} game as it connects.";
        var growth = maxBots is { } cap
            ? $" VMs that come online later are parked too, up to {cap} bot(s) in total."
            : " VMs that come online later - another node waking, say - are parked as they connect.";
        var monitorNote = context.IsApi
            ? context.Channel is null
                ? " No Discord channel is configured, so read the games back from GET /api/dclone."
                : " The live game list is posted in the notification channel; GET /api/dclone returns the same data."
            : " I posted one live message in this channel that lists the games as they come up. Its Stop button "
                + "ends the park, and Private / Public switch the fleet to follow-auto.";
        await SetInitialCommandResponseAsync(
            context,
            (handOffNote ?? "")
                + opening
                + growth
                + monitorNote
                + (watch ? " Watch diagnostics are enabled." : "")
                + FormatOfflineSkipSuffix(connectivity.Offline, connectivity.ConnectedUnaddressableAgents),
            ephemeral: true);

        _ = Task.Run(() => RunDcloneParkLoopAsync(context, run, cts, unwound, watch));
    }

    private async Task StopDcloneParkAsync(SlashContext context)
    {
        await EnsureAcknowledgedAsync(context);

        // A monitor from an earlier park keeps its Stop button; pressing it must not reach into
        // whatever park is running now. Checked inside the same lock as the cancel, so a park
        // started between the two cannot be the one that gets stopped.
        var stopComponent = context.Interaction as SocketMessageComponent;
        var (cancel, _) = await CancelDcloneParkAsync(reason: null, expectedMonitorMessageId: stopComponent?.Message.Id);
        await SetInitialCommandResponseAsync(
            context,
            cancel switch
            {
                DcloneParkCancelOutcome.Stopped =>
                    "dclone park is stopping. The bots stay in their games - use `/d2r save-exit` or the Leave button to pull them out.",
                DcloneParkCancelOutcome.StaleMonitor =>
                    "That Stop control belongs to an older dclone monitor; the current park was left unchanged.",
                _ => "dclone park is not running."
            },
            ephemeral: true);
    }

    // Same "if you quit, it should stop auto if its running" precedent as join-auto and
    // follow-auto (issue #24) - wired into the same quit/leave call sites as theirs.
    private async Task<bool> CancelDcloneParkIfRunningAsync(string? reason)
    {
        return (await CancelDcloneParkAsync(reason)).Outcome == DcloneParkCancelOutcome.Stopped;
    }

    /// <summary>
    /// Cancels the running park and hands back the task that completes once it has fully unwound.
    /// With <paramref name="expectedMonitorMessageId"/>, only the park that posted that monitor is
    /// cancelled - a control on an older monitor answers
    /// <see cref="DcloneParkCancelOutcome.StaleMonitor"/> and touches nothing.
    /// </summary>
    private async Task<(DcloneParkCancelOutcome Outcome, Task? Unwound)> CancelDcloneParkAsync(
        string? reason,
        ulong? expectedMonitorMessageId = null,
        bool handedOff = false)
    {
        await _dcloneLock.WaitAsync();
        try
        {
            if (_dcloneCts is null)
            {
                return (DcloneParkCancelOutcome.NotRunning, null);
            }

            if (expectedMonitorMessageId is { } expectedId && _dcloneMonitorMessage?.Id != expectedId)
            {
                return (DcloneParkCancelOutcome.StaleMonitor, null);
            }

            _dcloneRun?.RecordStopReason(reason, handedOff);
            _dcloneCts.Cancel();
            _dcloneCts = null;
            return (DcloneParkCancelOutcome.Stopped, _dcloneUnwound);
        }
        finally
        {
            _dcloneLock.Release();
        }
    }

    private async Task RunDcloneParkLoopAsync(
        SlashContext context,
        DcloneParkRun run,
        CancellationTokenSource cts,
        TaskCompletionSource unwound,
        bool watch)
    {
        var cancellationToken = cts.Token;
        IUserMessage? monitor = null;
        CancellationTokenSource? watchCts = null;
        Task? watchTask = null;
        // Every first create and rebuild this run started. None of them is awaited by the loop - a
        // create takes minutes and the other bots' games must keep being watched meanwhile - but
        // all of them are awaited before the run reports itself unwound, so no park command can
        // land after a successor mode has taken the fleet.
        var parks = new List<Task>();

        try
        {
            // The minted credentials have to be readable somewhere or the park is worthless.
            // In Discord that is the monitor message, and a monitor that will not post ends the
            // run rather than opening games in secret. An API caller has a second way to read
            // them - GET /api/dclone - so there the park runs monitor-less instead.
            if (context.Channel is { } monitorChannel)
            {
                try
                {
                    var posted = await monitorChannel.SendMessageAsync(
                        AppendMetrics(_dcloneMetricsEnabled, FormatDcloneMonitorMessage(run, "Opening games...")),
                        components: BuildDcloneMonitorComponents(running: true));
                    monitor = posted;
                    await _dcloneLock.WaitAsync(CancellationToken.None);
                    try
                    {
                        if (ReferenceEquals(_dcloneRun, run))
                        {
                            _dcloneMonitorMessage = posted;
                        }
                    }
                    finally
                    {
                        _dcloneLock.Release();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not post the dclone monitor message; the park was not started.");
                    await SendFollowupSafeAsync(
                        context,
                        "dclone park did not start: I could not post the live game list in this channel, "
                            + $"and without it the game names would go nowhere. {ex.Message}");
                    return;
                }
            }
            else if (!context.IsApi)
            {
                _logger.LogError("No channel is available for the dclone monitor; the park was not started.");
                return;
            }

            if (watch)
            {
                watchCts = new CancellationTokenSource();
                watchTask = RunGameAllWatchTickerAsync(
                    context,
                    "dclone",
                    "dclone",
                    () => GetDcloneRosterEntries(run),
                    watchCts.Token);
            }

            while (true)
            {
                parks.RemoveAll(park => park.IsCompleted);
                AdmitDcloneNewcomers(run, parks, cancellationToken);
                await SweepDcloneParkAsync(run, parks, cancellationToken);
                await UpdateDcloneMonitorAsync(monitor, run, DescribeDcloneParkProgress(run));
                await Task.Delay(DcloneParkPolicy.CheckInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop, quit-all, leave-all, or a switch to follow-auto. Not a failure.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "dclone park loop failed.");
            await SendFollowupSafeAsync(context, $"dclone park stopped after an error: {ex.Message}");
        }
        finally
        {
            // A loop that crashed rather than being stopped still owns a live token, and its
            // in-flight creates would otherwise run on unobserved.
            cts.Cancel();
            watchCts?.Cancel();
            await AwaitWatchTickerStopAsync(watchTask, "dclone");
            watchCts?.Dispose();
            await AwaitDcloneParksAsync(parks);

            await _dcloneLock.WaitAsync();
            try
            {
                if (ReferenceEquals(_dcloneCts, cts))
                {
                    _dcloneCts = null;
                }

                if (ReferenceEquals(_dcloneRun, run))
                {
                    _dcloneRun = null;
                    _dcloneUnwound = null;
                }

                if (monitor is not null && _dcloneMonitorMessage?.Id == monitor.Id)
                {
                    _dcloneMonitorMessage = null;
                }
            }
            finally
            {
                _dcloneLock.Release();
            }

            cts.Dispose();
            await CompleteDcloneMonitorAsync(monitor, run);
            unwound.TrySetResult();
        }
    }

    private async Task AwaitDcloneParksAsync(IReadOnlyCollection<Task> parks)
    {
        if (parks.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(parks);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A dclone park attempt faulted while the park was stopping.");
        }
    }

    /// <summary>
    /// Brings every online VM the park does not hold yet into it and queues its first create.
    /// This is what makes the park a fleet mode rather than a snapshot: a worker node that wakes
    /// after the park started has its VMs parked on the next sweep, with no command from anyone.
    /// </summary>
    private void AdmitDcloneNewcomers(
        DcloneParkRun run,
        List<Task> parks,
        CancellationToken cancellationToken)
    {
        var online = GetAccountEntriesByConnectivity().Online;
        var admitted = run.AdmitNewcomers(online
            .Select(entry => (entry.Key, FormatAccountDisplayName(entry.Key, entry.Value), entry.Value.AgentId))
            .ToArray());
        if (admitted.Count == 0)
        {
            return;
        }

        if (run.SlotCount() > admitted.Count)
        {
            _logger.LogInformation(
                "dclone park admitted {Count} newly online VM(s): {Accounts}.",
                admitted.Count,
                string.Join(", ", admitted));
        }

        // Staggered like every other all-client fan-out: several VMs driving Battle.net and D2R
        // menus at the same instant is what the configured stagger exists to spread out.
        var stagger = TimeSpan.FromSeconds(Math.Max(_config.ClientStaggerSeconds ?? _config.StartAllDelaySeconds, 0));
        var nowUtc = DateTimeOffset.UtcNow;
        foreach (var accountKey in admitted)
        {
            var account = online.First(entry =>
                string.Equals(entry.Key, accountKey, StringComparison.OrdinalIgnoreCase)).Value;
            parks.Add(ParkOneBotAsync(
                run,
                accountKey,
                account,
                run.ReserveInitialParkDelay(nowUtc, stagger),
                cancellationToken));
        }
    }

    /// <summary>The fleet entries for the accounts this park holds, as they are configured right now.</summary>
    private KeyValuePair<string, AccountConfig>[] GetDcloneRosterEntries(DcloneParkRun run)
    {
        var accounts = _registry.Accounts;
        return run.Snapshot()
            .Where(slot => accounts.ContainsKey(slot.AccountKey))
            .Select(slot => new KeyValuePair<string, AccountConfig>(slot.AccountKey, accounts[slot.AccountKey]))
            .ToArray();
    }

    /// <summary>
    /// One poll of every rostered bot. Re-parks are started concurrently and never awaited under
    /// the poll: a create takes minutes, and the other bots' games should not stop being watched
    /// for that long.
    /// </summary>
    private async Task SweepDcloneParkAsync(
        DcloneParkRun run,
        List<Task> parks,
        CancellationToken cancellationToken)
    {
        var accounts = _registry.Accounts;
        var readings = await Task.WhenAll(run.Snapshot().Select(async slot =>
        {
            // A worker's accounts come from its last inventory. One the fleet no longer lists at
            // all cannot be polled, and must read as offline rather than keep a dead game listed.
            if (!accounts.TryGetValue(slot.AccountKey, out var account))
            {
                return (slot.AccountKey, Account: (AccountConfig?)null, Presence: DcloneParkPresence.Offline);
            }

            return (slot.AccountKey, Account: account, Presence: await ReadDcloneParkPresenceAsync(account, cancellationToken));
        }));

        var nowUtc = DateTimeOffset.UtcNow;
        foreach (var (accountKey, account, presence) in readings)
        {
            if (run.RegisterReadingAndTryBeginRepark(accountKey, presence, nowUtc) && account is not null)
            {
                parks.Add(ParkOneBotAsync(run, accountKey, account, TimeSpan.Zero, cancellationToken, alreadyArmed: true));
            }
        }
    }

    private async Task<DcloneParkPresence> ReadDcloneParkPresenceAsync(
        AccountConfig account,
        CancellationToken cancellationToken)
    {
        if (_registry.GetAgent(account.AgentId)?.Connected != true)
        {
            return DcloneParkPresence.Offline;
        }

        try
        {
            // status never takes the agent's command gate, so this poll cannot be blocked by a
            // sibling bot's in-flight create.
            var result = await _registry.SendCommandAsync(
                account.AgentId, "status", args: null, DcloneStatusTimeout, cancellationToken);
            return result.Ok && result.Data is { } data
                ? DcloneParkPolicy.Classify(connected: true, data.GetRawText())
                : DcloneParkPresence.NoEvidence;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "dclone status poll failed for {AgentId}.", account.AgentId);
            return DcloneParkPresence.NoEvidence;
        }
    }

    /// <summary>
    /// Pulls one client out of any game it is already in, warms it if it needs it, then creates a
    /// game only that bot is in. Every attempt mints a brand new name: the usual reason a create
    /// fails is that the name already exists on the realm, and retrying with the same one would
    /// fail identically.
    /// </summary>
    private async Task ParkOneBotAsync(
        DcloneParkRun run,
        string accountKey,
        AccountConfig account,
        TimeSpan startDelay,
        CancellationToken cancellationToken,
        bool alreadyArmed = false)
    {
        if (startDelay > TimeSpan.Zero)
        {
            await Task.Delay(startDelay, cancellationToken);
        }

        if (!alreadyArmed && !run.TryBeginInitialPark(accountKey))
        {
            return;
        }

        var lastFailure = "the create never ran";
        try
        {
            for (var attempt = 1; attempt <= DcloneParkPolicy.MaxCreateAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                run.MarkPreparing(
                    accountKey,
                    attempt == 1 ? "warming up" : $"create attempt {attempt} of {DcloneParkPolicy.MaxCreateAttempts}");

                // Every call is caught per attempt rather than by the outer handler: a leave, a
                // ready or a create that times out or throws is exactly the transient failure the
                // retry budget exists for, and letting one escape would spend the whole budget.
                try
                {
                    var accountArgs = BuildAccountArgs(accountKey, account);
                    var presence = await ReadDcloneParkPresenceAsync(account, cancellationToken);
                    switch (DcloneParkPolicy.DecidePreflight(presence))
                    {
                        case DcloneParkPreflight.Offline:
                            run.MarkOffline(accountKey, "VM agent offline");
                            return;
                        case DcloneParkPreflight.LeaveGameFirst:
                            // A rebuild that finds its bot still in a game before sending any create
                            // was armed by a misread, and that game is the one already handed out.
                            if (alreadyArmed && attempt == 1 && run.TryRestoreMisreadPark(accountKey))
                            {
                                _logger.LogInformation(
                                    "dclone rebuild for {AccountKey} cancelled: the bot is still in its game.",
                                    accountKey);
                                return;
                            }

                            // Save and Exit is blind Escape-and-click input, so it is only sent on a
                            // positive in-game reading - never on a load screen or a bad capture.
                            run.MarkPreparing(accountKey, "leaving the game it is already in");
                            var leave = await _registry.SendCommandAsync(
                                account.AgentId,
                                "menu_save_exit",
                                accountArgs,
                                DcloneSaveExitTimeout,
                                cancellationToken);
                            if (!leave.Ok)
                            {
                                lastFailure = $"could not leave the game it was already in: {leave.Message}";
                                continue;
                            }

                            break;
                    }

                    var ready = await SendReadyIfNotMenuReadyAsync(account, accountArgs, cancellationToken);
                    if (ready?.Ok == false)
                    {
                        lastFailure = $"ready failed: {ready.Message}";
                        continue;
                    }

                    var (gameName, password) = run.MintCredentials();
                    run.MarkPreparing(accountKey, $"creating {gameName}");
                    var result = await _registry.SendCommandAsync(
                        account.AgentId,
                        "menu_create_game",
                        BuildDcloneMenuArgs(accountKey, account, gameName, password, run.Difficulty),
                        DcloneCreateGameTimeout,
                        cancellationToken);
                    if (result.Ok)
                    {
                        run.MarkParked(accountKey, gameName, password, "holding the game open");
                        return;
                    }

                    lastFailure = result.Message;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "dclone park attempt failed for {AccountKey}.", accountKey);
                    lastFailure = FormatExceptionWithAccountStatus(ex, accountKey, account);
                }
            }

            run.MarkFailed(accountKey, lastFailure, DateTimeOffset.UtcNow);
        }
        finally
        {
            // Releases the latch on every exit, cancellation included, so a slot can never be
            // left armed with no attempt behind it - which would make it unpollable for the rest
            // of the run.
            run.EndParkAttempt(accountKey);
        }
    }

    private static object BuildDcloneMenuArgs(
        string accountKey,
        AccountConfig account,
        string gameName,
        string password,
        string difficulty)
    {
        return new
        {
            accountKey,
            displayName = account.DisplayName ?? accountKey,
            vmName = account.VmName ?? account.AgentId,
            gameName,
            password,
            difficulty,
            characterSlot = account.CharacterSlot,
            friendRow = (int?)null,
            partyPosition = (int?)null,
            followAutoRunId = (long?)null
        };
    }

    private static MessageComponent BuildDcloneMonitorComponents(bool running)
    {
        var builder = new ComponentBuilder();
        if (running)
        {
            // Park, Private and Public are the three things the fleet can be doing, so each mode's
            // monitor carries the other two. Same styles as follow-auto's own toggle, so a button
            // reads the same whichever monitor it is on.
            builder
                .WithButton("Stop", DcloneStopButtonId, ButtonStyle.Danger)
                .WithButton("Private", DclonePrivateButtonId, ButtonStyle.Secondary)
                .WithButton("Public", DclonePublicButtonId, ButtonStyle.Success);
        }

        return builder.Build();
    }

    private async Task UpdateDcloneMonitorAsync(IUserMessage? monitor, DcloneParkRun run, string status)
    {
        if (monitor is null)
        {
            return;
        }

        try
        {
            await monitor.ModifyAsync(properties =>
            {
                properties.Content = AppendMetrics(_dcloneMetricsEnabled, FormatDcloneMonitorMessage(run, status));
                properties.Components = BuildDcloneMonitorComponents(running: true);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update the dclone monitor message.");
        }
    }

    private async Task CompleteDcloneMonitorAsync(IUserMessage? monitor, DcloneParkRun run)
    {
        if (monitor is null)
        {
            return;
        }

        var parked = run.ParkedCount();
        var reason = run.StopReason is { } stopReason ? $" ({stopReason})" : "";
        // After a hand-off the games below are being emptied by follow-auto, and Leave/Quit act on
        // the whole fleet - pressing either would pull bots out of the leader's game it now owns.
        var offerExits = parked > 0 && !run.HandedOff;
        var status = run.HandedOff
            ? $"Park stopped{reason}. follow-auto now owns the fleet; these games are being left as each bot follows the leader."
            : parked > 0
                ? $"Park stopped{reason}. {parked} bot(s) are still sitting in the games listed above - "
                    + "Leave pulls them out, Quit closes the clients."
                : $"Park stopped{reason}. No bot is holding a game.";
        try
        {
            await monitor.ModifyAsync(properties =>
            {
                properties.Content = AppendMetrics(_dcloneMetricsEnabled, FormatDcloneMonitorMessage(run, status));
                // Reuses the game-session Leave/Quit pair rather than minting dclone-specific
                // buttons: they already do exactly this (save-exit-all / quit-all) everywhere else.
                properties.Components = offerExits
                    ? BuildGameSessionActionComponents()
                    : new ComponentBuilder().Build();
            });
            await monitor.AddReactionAsync(new Emoji(parked > 0 ? "✅" : "⛔"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not complete the dclone monitor message.");
        }
    }

    private static string DescribeDcloneParkProgress(DcloneParkRun run)
    {
        var slots = run.Snapshot();
        var parked = slots.Count(slot => slot.State == DcloneSlotState.Parked);
        if (slots.Count == 0)
        {
            return "No VM is online yet. Each one is parked in its own game as it connects.";
        }

        if (parked == slots.Count)
        {
            return "All bots are parked. Join any game below to hunt it.";
        }

        var working = slots.Count(slot =>
            slot.State is DcloneSlotState.Preparing or DcloneSlotState.Reparking);
        return working > 0
            ? $"{parked} game(s) open, {working} still coming up."
            : $"{parked} game(s) open; the rest need attention.";
    }

    private string FormatDcloneMonitorMessage(DcloneParkRun run, string status)
    {
        var slots = run.Snapshot();
        var parked = slots.Count(slot => slot.State == DcloneSlotState.Parked);
        var elapsed = FormatElapsed(DateTimeOffset.UtcNow - run.StartedUtc);
        var lines = new List<string>
        {
            $"**dclone park** - {parked}/{slots.Count} parked - {FormatDifficultyLabel(run.Difficulty)} - running {elapsed}",
            status
        };
        lines.AddRange(slots.Select(FormatDcloneSlotLine));
        lines.Add(run.MaxBots is { } cap
            ? $"VMs that come online are parked too, up to {cap} bot(s)."
            : "VMs that come online are parked too.");
        return string.Join("\n", lines);
    }

    // Parked bots lead with the credentials because that is the whole point of the message: the
    // operator is copying a name and a password into their own client, usually in a hurry.
    private static string FormatDcloneSlotLine(DcloneParkSlotSnapshot slot)
    {
        var reparks = slot.Reparks == 0 ? "" : $" [rebuilt {slot.Reparks}x]";
        if (slot.State == DcloneSlotState.Parked)
        {
            return $"`{slot.GameName}` / `{slot.Password}` - {slot.DisplayName}{reparks}";
        }

        return $"- {slot.DisplayName}: {FormatDcloneSlotState(slot.State)} - {slot.Detail}{reparks}";
    }

    private static string FormatDcloneSlotState(DcloneSlotState state)
    {
        return state switch
        {
            DcloneSlotState.Preparing => "opening",
            DcloneSlotState.Reparking => "rebuilding",
            DcloneSlotState.Failed => "failed",
            DcloneSlotState.Offline => "offline",
            _ => "parked"
        };
    }

    private static string FormatDifficultyLabel(string difficulty)
    {
        return string.IsNullOrWhiteSpace(difficulty)
            ? "Hell"
            : char.ToUpperInvariant(difficulty[0]) + difficulty[1..].ToLowerInvariant();
    }

    private async Task RunJoinAutoLoopAsync(
        SlashContext context,
        IMessageChannel channel,
        int delaySeconds,
        bool watch,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_gameTemplate is not { } template)
                {
                    await SendJoinAutoMessageAsync(channel, "join-auto stopped: the template was cleared.", context.MetricsEnabled);
                    await CompleteJoinAutoMonitorAsync(ok: true, "Stopped: the template was cleared.");
                    break;
                }

                var (gameName, password) = template.MintNext();
                var game = new GameInput(gameName, password, Difficulty: null);

                var outcome = await TryJoinAutoCycleAsync(channel, context, game, delaySeconds, watch, idleTimeout, cancellationToken);
                if (outcome == JoinAutoCycleOutcome.IdleTimedOut)
                {
                    await SendJoinAutoMessageAsync(channel, "join-auto: idle timeout detected, disabled.", context.MetricsEnabled);
                    await CompleteJoinAutoMonitorAsync(ok: false, $"Idle timeout - gave up joining {gameName} and disabled.");
                    break;
                }

                await SendJoinAutoMessageAsync(channel, $"join-auto: everyone is in {gameName}. Watching for someone to leave...", context.MetricsEnabled);
                await UpdateJoinAutoMonitorAsync("Everyone is in - watching for someone to leave.");

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                var baseline = await TryFetchFirstOnlineAccountPlayerCountAsync();
                await WaitForPlayerCountDropAsync(baseline, GetJoinAutoPlayerCountDropPollDelay, cancellationToken);

                await SendJoinAutoMessageAsync(channel, $"join-auto: player count dropped - leaving {gameName}.", context.MetricsEnabled);
                await UpdateJoinAutoMonitorAsync($"Player count dropped - leaving {gameName}...");
                await LeaveAllJoinAutoAsync(channel, "join-auto", metricsEnabled: context.MetricsEnabled);
                _joinAutoCyclesCompleted++;
                await UpdateJoinAutoMonitorAsync($"Left {gameName}. Advancing to the next game...", joined: 0);
            }
        }
        catch (OperationCanceledException)
        {
            var reasonText = _joinAutoStopReason is { } reason ? $"join-auto stopped: {reason}." : "join-auto stopped.";
            await SendJoinAutoMessageAsync(channel, reasonText, context.MetricsEnabled);
            await CompleteJoinAutoMonitorAsync(ok: true, reasonText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "join-auto loop failed.");
            await SendJoinAutoMessageAsync(channel, $"join-auto stopped unexpectedly: {ex.Message}", context.MetricsEnabled);
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

            var connectivity = _registry.GetAccountConnectivity();
            var entries = connectivity.Online;
            var offlineEntries = connectivity.Offline;
            if (entries.Length == 0)
            {
                await UpdateJoinAutoMonitorAsync($"No online accounts available (attempt {attempt}).", gameName: game.GameName, joined: 0, total: 0);
                if (watch)
                {
                    await SendJoinAutoMessageAsync(
                        channel,
                        $"join-auto: no online accounts available (attempt {attempt})."
                            + FormatOfflineSkipSuffix(offlineEntries, connectivity.ConnectedUnaddressableAgents),
                        context.MetricsEnabled);
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
                            + string.Join("; ", failed.Select(result => $"{result.AccountKey}: {result.Message}")),
                        context.MetricsEnabled);
                }
            }

            if (DateTime.UtcNow >= deadlineUtc)
            {
                return JoinAutoCycleOutcome.IdleTimedOut;
            }
        }
    }

    private Task SendJoinAutoMessageAsync(IMessageChannel channel, string content, bool metricsEnabled)
    {
        return channel.SendMessageAsync(AppendMetrics(metricsEnabled, content));
    }

    // Issue #25: capture a follow-bind fingerprint from one account's selected friend row, record it
    // on the host, then push it to every online account so follow-auto can recognize the same name
    // anywhere. Offline and future VMs are reconciled onto the host's copy by FollowTemplateStore.
    private async Task HandleFollowBindAsync(SlashContext context, bool bindFlag)
    {
        var (online, offlineAccounts) = GetAccountEntriesByConnectivity();

        if (!bindFlag)
        {
            await DeferIfInteractiveAsync(context);
            var followAutoCancel = await CancelFollowAutoIfRunningAsync("the follow-bind target was cleared");
            var stopSignals = await SignalFollowAutoStopAgentsAsync(followAutoCancel.RunId);

            // Record the unbind before pushing it: an agent that is offline right now must still
            // be cleared when it returns, or it would rejoin a later follow-auto run still holding
            // the fingerprint of a friend the operator deliberately unbound.
            _followTemplates.Clear();
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

            var stopSummary = stopSignals.Attempted > 0
                ? $" Follow-auto stop signal reached {stopSignals.Succeeded}/{stopSignals.Attempted} online agent(s)."
                : "";
            var clearPending = offlineAccounts.Length > 0
                ? $"; the {offlineAccounts.Length} offline VM(s) are cleared automatically when they reconnect."
                : ".";
            await ModifyOriginalResponseWithMetricsAsync(
                context,
                $"Follow-bind cleared, including any in-game leader bind. The host recorded the unbind and applied it to "
                    + $"{cleared}/{online.Length} online VM(s){clearPending}{stopSummary}");
            return;
        }

        var bindAccountKey = context.GetString("account");
        if (bindAccountKey is null)
        {
            await RespondWithMetricsAsync(
                context,
                "follow bind:true requires account (whose selected friend row to capture from).");
            return;
        }

        var (resolvedAccountKey, bindAccount) = RequireAccount(bindAccountKey);
        await DeferIfInteractiveAsync(context);

        CommandResultInfo captureResult;
        try
        {
            captureResult = await _registry.SendCommandAsync(
                bindAccount.AgentId, "menu_follow_bind", BuildMenuArgs(resolvedAccountKey, bindAccount, null, context), TimeSpan.FromSeconds(210));
        }
        catch (Exception ex)
        {
            await ModifyOriginalResponseWithMetricsAsync(
                context,
                $"follow bind:true failed to capture from {resolvedAccountKey}: {ex.Message}");
            return;
        }

        if (!captureResult.Ok
            || captureResult.Data is not { } data
            || !data.TryGetProperty("fingerprint", out var fingerprintProperty)
            || fingerprintProperty.GetString() is not { } fingerprint)
        {
            await ModifyOriginalResponseWithMetricsAsync(
                context,
                $"follow bind:true failed to capture from {resolvedAccountKey}: {captureResult.Message}");
            return;
        }

        var capturedFriendRow = TryGetInt(data, "friendRow", out var friendRow)
            ? friendRow
            : context.GetInt("friend-row") ?? 1;

        // The host takes ownership of the capture before distributing it, so the fingerprint
        // outlives both this command and this process. Everything below is the immediate,
        // operator-visible half of distribution; FollowTemplateStore's sweep is what reaches
        // agents that are offline now or join the fleet later.
        _followTemplates.SetFriendTemplate(fingerprint, resolvedAccountKey);
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

        // The vantage compared the fresh capture against every other visible friend row. A row that
        // already clears the match gate will tie or overtake the bound row at follow time - once
        // the list re-sorts, or once a friend who was offline (dim name) comes online (bright
        // name) - and the run then reports "ambiguous; not clicking a friend row this cycle"
        // forever. Say so now, while the operator is still standing at the bind.
        var collisionWarning = "";
        if (captureResult.Data is { } captureData
            && captureData.TryGetProperty("collidingRows", out var collidingProperty)
            && collidingProperty.ValueKind == JsonValueKind.Array)
        {
            var collidingRows = collidingProperty.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.Number)
                .Select(element => element.GetInt32())
                .ToArray();
            if (collidingRows.Length > 0)
            {
                collisionWarning =
                    $" WARNING: this name also matches friend row {string.Join(", ", collidingRows)} closely enough to be confused with it."
                        + " Follow-auto will refuse to click while two rows compete, which usually starts the moment one of those"
                        + " friends comes online and their name brightens. Bind a friend whose name looks less alike, or move the"
                        + " intended one somewhere the other is not adjacent.";
            }
        }

        var followBindMessage =
            $"Bound the friend at {resolvedAccountKey}'s friend row {capturedFriendRow}. "
            + FormatBindOwnershipSummary(distributed, online.Length, offlineAccounts.Length)
            + collisionWarning
            + " Use /d2r follow or the button below to start following.";
        if (distributionFailures.Count > 0)
        {
            followBindMessage += $" Template save failures: {string.Join("; ", distributionFailures)}";
        }

        await ModifyOriginalResponseWithMetricsAsync(
            context,
            followBindMessage,
            properties => properties.Components = BuildFollowBindActionComponents());
    }

    // Issue #25 follow-up ("bind-in-game"): capture the party-bar name at a visible position
    // from one account's in-game vantage and push the glyph mask to every online account.
    // Once distributed, the follow-auto pulse leaves games when THIS player is gone instead of
    // whenever the public-game player count wobbles. Repeat per alt: each bind APPENDS a
    // nametag, and each follow-auto run locks onto whichever bound nametag it spots first
    // (rolodex order on ties). Position 0 clears every bound nametag (the friend bind stays;
    // follow-auto falls back to count-drop behavior).
    private async Task HandleFollowBindInGameAsync(SlashContext context, int partyPosition)
    {
        var (online, offlineAccounts) = GetAccountEntriesByConnectivity();

        if (partyPosition == 0)
        {
            await DeferIfInteractiveAsync(context);

            // Record the empty rolodex before pushing it. Without this the host would still hold
            // the nametags and the next sweep would hand them straight back to every agent that
            // just cleared them.
            _followTemplates.ClearLeaderTemplates();
            var cleared = 0;
            foreach (var (accountKey, account) in online)
            {
                try
                {
                    await _registry.SendCommandAsync(account.AgentId, "follow_clear_leader_template", new { }, TimeSpan.FromSeconds(15));
                    cleared++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "follow_clear_leader_template failed for {AccountKey}.", accountKey);
                }
            }

            _followAutoLockedNametag = null;
            _followAutoLockedNametagOrdinal = null;
            var clearPending = offlineAccounts.Length > 0
                ? $"; the {offlineAccounts.Length} offline VM(s) are cleared automatically when they reconnect."
                : ".";
            await ModifyOriginalResponseWithMetricsAsync(
                context,
                $"All bound nametags cleared. The host recorded it and applied it to {cleared}/{online.Length} online VM(s)"
                    + $"{clearPending} Follow-auto will leave on player-count drops again.");
            return;
        }

        // The vantage defaults to the first online account because that is exactly the account
        // the follow-auto pulse samples from (TryFetchFirstOnlineAccountFollowPulseAsync) -
        // binding from the same viewpoint that will later do the checking. The position is
        // counted left-to-right across the portraits visible on THAT account's screen (a
        // character never appears in its own party bar).
        string vantageKey;
        AccountConfig vantageAccount;
        if (context.GetString("account") is { } requestedAccountKey)
        {
            (vantageKey, vantageAccount) = RequireAccount(requestedAccountKey);
        }
        else if (online.Length > 0)
        {
            (vantageKey, vantageAccount) = (online[0].Key, online[0].Value);
        }
        else
        {
            await RespondWithMetricsAsync(
                context,
                "follow bind-in-game needs at least one online account (or an explicit account) to capture from.");
            return;
        }

        await DeferIfInteractiveAsync(context);

        CommandResultInfo captureResult;
        try
        {
            captureResult = await _registry.SendCommandAsync(
                vantageAccount.AgentId,
                "menu_follow_bind_game",
                BuildMenuArgs(vantageKey, vantageAccount, null, context),
                TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            await ModifyOriginalResponseWithMetricsAsync(
                context,
                $"follow bind-in-game failed to capture from {vantageKey}: {ex.Message}");
            return;
        }

        if (!captureResult.Ok
            || captureResult.Data is not { } data
            || !data.TryGetProperty("fingerprint", out var fingerprintProperty)
            || fingerprintProperty.GetString() is not { } fingerprint)
        {
            await ModifyOriginalResponseWithMetricsAsync(
                context,
                $"follow bind-in-game failed to capture from {vantageKey}: {captureResult.Message}");
            return;
        }

        var visibleMembers = TryGetInt(data, "visibleMembers", out var visible) ? visible : partyPosition;
        var glyphSummary = TryGetInt(data, "glyphWidth", out var glyphWidth)
            && TryGetInt(data, "glyphHeight", out var glyphHeight)
            && TryGetInt(data, "glyphBits", out var glyphBits)
            ? $" Name box {glyphWidth}x{glyphHeight}px, {glyphBits} text pixels."
            : "";

        // Recorded before distribution for the same reason as the friend-row bind, and appended
        // rather than replaced so the host's rolodex tracks bind order exactly like each agent's.
        _followTemplates.AppendLeaderTemplate(fingerprint);
        var distributed = 0;
        var boundNametagCount = (int?)null;
        var distributionFailures = new List<string>();
        foreach (var (accountKey, account) in online)
        {
            try
            {
                var result = await _registry.SendCommandAsync(account.AgentId, "follow_set_leader_template", new { fingerprint, append = true }, TimeSpan.FromSeconds(15));
                if (result.Ok)
                {
                    distributed++;
                    if (result.Data is { } setData
                        && TryGetInt(setData, "templateCount", out var count)
                        && (boundNametagCount is null || string.Equals(accountKey, vantageKey, StringComparison.OrdinalIgnoreCase)))
                    {
                        boundNametagCount = count;
                    }
                }
                else
                {
                    distributionFailures.Add($"{accountKey}: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "follow_set_leader_template failed for {AccountKey}.", accountKey);
                distributionFailures.Add($"{accountKey}: {ex.Message}");
            }
        }

        // Patch the "bound a bot instead of yourself" hole. The operator's own character is the
        // one name visible from EVERY bot's party bar; a bot's own name never renders on its own
        // screen (D2R omits you from your own party bar). So the freshly distributed name is
        // checked against every other in-game account: if any of them reports it NOT visible,
        // that account is the character we actually captured - a bot, counted at the wrong slot
        // because the vantage's own character shifts the visible list. Refuse the bind instead
        // of silently following a bot. Accounts that can't check right now (mid-loading, not in
        // a game) return null and neither confirm nor disqualify - only an explicit "I'm in a
        // game and I don't see this name" disqualifies.
        var seenByOthers = new List<string>();
        var missingFromOthers = new List<string>();
        foreach (var (accountKey, account) in online)
        {
            if (string.Equals(accountKey, vantageKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Verification is keyed to the FRESH nametag specifically (passed as the active
            // fingerprint so the agent's scan short-circuits on it): another bound alt being
            // visible or absent says nothing about whether this capture grabbed a bot.
            var check = await TryFetchFollowPulseForAsync(accountKey, account, fingerprint);
            var freshMatch = check.Matches.FirstOrDefault(match => string.Equals(match.Fingerprint, fingerprint, StringComparison.Ordinal));
            if (freshMatch?.Present == true)
            {
                seenByOthers.Add(accountKey);
            }
            else if (freshMatch?.Present == false)
            {
                missingFromOthers.Add(accountKey);
            }
        }

        if (missingFromOthers.Count > 0)
        {
            // Roll back only the fresh capture - the operator's other bound alt nametags must
            // survive a single bad bind. The host's own copy rolls back first so the sweep does
            // not helpfully re-push the nametag we are in the middle of retracting.
            _followTemplates.RemoveLeaderTemplate(fingerprint);
            foreach (var (accountKey, account) in online)
            {
                try
                {
                    await _registry.SendCommandAsync(account.AgentId, "follow_remove_leader_template", new { fingerprint }, TimeSpan.FromSeconds(15));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "follow_remove_leader_template (bind rollback) failed for {AccountKey}.", accountKey);
                }
            }

            await ModifyOriginalResponseWithMetricsAsync(
                context,
                $"Position {partyPosition} on {vantageKey} captured a name that {string.Join(", ", missingFromOthers)} can't see in its own party bar - which means it's {missingFromOthers[0]}'s own character (a bot), not you.{glyphSummary} That nametag was removed everywhere; any previously bound nametags are untouched. "
                + $"From {vantageKey}'s screen your own character isn't listed, so every member after that slot shifts up one - pick the position where you actually see your character, or run `/d2r screenshot {vantageKey}` to read the party bar first.");
            return;
        }

        var verifiedSuffix = seenByOthers.Count > 0
            ? $" Verified visible from {seenByOthers.Count} other account(s), so this is a player everyone shares rather than a bot."
            : " WARNING: no other account could cross-check this capture right now (not in a game, or mid-load), so a"
                + " wrong-position capture of a bot's name would go unnoticed. If follow-auto later stays in a game"
                + " after you leave, re-run this bind while the bots are in your game.";

        // The bind list changed, so a running follow-auto re-resolves which alt it is
        // following - most likely onto this fresh capture, since the operator just bound the
        // alt they are playing.
        _followAutoLockedNametag = null;
        _followAutoLockedNametagOrdinal = null;

        // The vantage measured the fresh template against every OTHER visible name band at
        // capture time. A cross-match there means runtime pulses can keep reading this name
        // "present" off a different player after the real leader leaves - the "follow-auto
        // never leaves" failure - so pass the agent's finding on to the operator verbatim.
        var ambiguitySuffix = "";
        if (data.TryGetProperty("ambiguousSlots", out var ambiguousProperty)
            && ambiguousProperty.ValueKind == JsonValueKind.Array)
        {
            var ambiguousSlots = ambiguousProperty.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.Number)
                .Select(element => element.GetInt32())
                .ToArray();
            if (ambiguousSlots.Length > 0)
            {
                ambiguitySuffix =
                    $" WARNING: this name also matches the name under portrait slot {string.Join(", ", ambiguousSlots)}"
                        + " on the vantage's party bar - follow-auto may keep seeing the leader as present off that"
                        + " player after the real leader leaves. A more distinctive character name binds reliably.";
            }
        }

        var rolodexSuffix = boundNametagCount is { } totalBound && totalBound > 1
            ? $" {totalBound} nametags are now bound; each follow-auto run locks onto whichever one it spots first."
            : " Follow-auto now stays while this player is in the game and leaves when they leave.";
        var message =
            $"Bound the party-bar name at {vantageKey}'s position {partyPosition} of {visibleMembers} visible member(s). "
            + FormatBindOwnershipSummary(distributed, online.Length, offlineAccounts.Length)
            + $"{glyphSummary}{verifiedSuffix}{ambiguitySuffix}{rolodexSuffix}";
        if (distributionFailures.Count > 0)
        {
            message += $" Template save failures: {string.Join("; ", distributionFailures)}";
        }

        await ModifyOriginalResponseWithMetricsAsync(context, message);
    }

    private async Task StartFollowAutoAsync(
        SlashContext context,
        int delaySeconds,
        bool watch,
        TimeSpan idleTimeout,
        int? targetBotCount = null,
        FollowAutoPartyMode partyMode = FollowAutoPartyMode.Private,
        string? handOffNote = null)
    {
        // Acknowledge before anything else. Discord.NET runs this handler inline on the gateway
        // task and Discord discards the interaction if nothing answers within three seconds -
        // and this was the one command family that did its work first and answered second, over
        // a synchronous SQLite write (_db.ClearFollowAutoResumeIntent) that a host busy
        // power-cycling a guest can stall past that budget. Every other command already defers
        // first; follow only got away with it because the work looked cheap. The failure was not
        // confined to the slow call either: the gateway task processes dispatches one at a time,
        // so whatever queued behind it was reported as "The application did not respond" too.
        // Awaiting the deferral also moves the rest of this method off the gateway task.
        await EnsureAcknowledgedAsync(context);

        // The mirror of the check in StartDcloneParkAsync: a park has every VM sitting in its own
        // game, and follow-auto would immediately start pulling them all into one.
        if (IsDcloneParkRunning())
        {
            await SetInitialCommandResponseAsync(
                context,
                "A dclone park is running and owns the same VMs. Stop it with `/d2r dclone stop:true`, "
                    + "or press Private / Public on its monitor to switch straight over.",
                ephemeral: true);
            return;
        }

        // The other command whose output IS a Discord message: the monitor carries the live bot
        // count, the -1/+1 controls and the Public/Private toggle, and a run without it cannot be
        // steered at all.
        if (context.Channel is not { } followAutoChannel)
        {
            await SetInitialCommandResponseAsync(context, NoChannelRefusal("follow-auto"), ephemeral: true);
            return;
        }

        var bots = FollowAutoRosterPolicy.ClampTarget(
            targetBotCount ?? context.GetInt("bots") ?? FollowAutoRosterPolicy.DefaultBotCount);
        var options = new FollowAutoRunOptions(
            followAutoChannel,
            delaySeconds,
            watch,
            idleTimeout,
            context.MetricsEnabled,
            context.GetInt("character-slot"),
            context.GetInt("friend-row"),
            InitialRecoveryAccountKeys: [],
            TargetBotCount: bots,
            PartyMode: partyMode);
        var start = await TryBeginFollowAutoRunAsync();
        if (start is null)
        {
            await SetInitialCommandResponseAsync(
                context,
                "follow auto:true is already running. Use /d2r follow auto:false first.",
                ephemeral: true);
            return;
        }

        var queued = false;
        try
        {
            // An explicit new run supersedes any stale one-shot intent left by a reboot that never
            // completed. The active run will write a fresh intent if it later needs local recovery.
            _db.ClearFollowAutoResumeIntent();

            // Public starts from the same trimmed target the monitor will show, not the raw request.
            var startingBots = FollowAutoTargetControl.ClampTargetForMode(bots, partyMode);
            var delayNote = delaySeconds > 0 ? $", and a {delaySeconds}s delay between checks" : "";
            await SetInitialCommandResponseAsync(
                context,
                (handOffNote ?? "")
                    + (partyMode == FollowAutoPartyMode.Public
                        ? $"follow-auto started in public mode with {startingBots} bot(s){delayNote}: the game is held at "
                            + $"{FollowAutoPublicModePolicy.TargetPlayerCount} of {FollowAutoRosterPolicy.MaxPlayersPerGame} "
                            + "players so a real player can always join, and the bot count follows the live player count. "
                            + "I posted one live status message in this channel; Private hands the count back to the "
                            + "-1 / +1 buttons, and Park gives every bot its own game."
                        : $"follow-auto started with {startingBots} bot(s), {FormatPartySize(startingBots)} with the leader{delayNote}. "
                            + "I posted one live status message in this channel; its -1 / +1 buttons change the bot count mid-run, "
                            + $"Public holds the game at {FollowAutoPublicModePolicy.TargetPlayerCount} of "
                            + $"{FollowAutoRosterPolicy.MaxPlayersPerGame} players so a real player can always join, "
                            + "and Park gives every bot its own game.")
                    + (watch ? " Watch diagnostics are enabled." : ""),
                ephemeral: true);

            QueueFollowAutoRun(options, start);
            queued = true;
        }
        finally
        {
            if (!queued)
            {
                // An installed lease with no loop can never unwind itself.
                await _followAutoLifecycle.CompleteUnwindAsync(start);
            }
        }
    }

    /// <summary>
    /// Acknowledges an interaction that is about to do work before it can answer, so Discord's
    /// three-second deadline is met no matter how long that work takes. Safe to call on a path
    /// that may already have been acknowledged upstream.
    /// </summary>
    /// <remarks>
    /// A component interaction must not use <c>DeferAsync</c> here: that acknowledges as
    /// DeferredUpdateMessage, which makes the original response the clicked message itself, so
    /// the eventual reply would overwrite the follow-auto monitor or the quick-action prompt the
    /// button sits on. DeferLoadingAsync posts a separate ephemeral response instead - the same
    /// thing the RespondAsync these paths used to reach produced. Callers that deliberately want
    /// the clicked message replaced still defer themselves before getting here, and this then
    /// leaves their choice alone.
    /// </remarks>
    private static Task EnsureAcknowledgedAsync(SocketInteraction interaction)
    {
        return ChooseAcknowledgement(
            interaction.HasResponded,
            isComponent: interaction is SocketMessageComponent) switch
        {
            InteractionAcknowledgement.DeferAsNewEphemeralReply =>
                interaction.DeferAsync(ephemeral: true),
            InteractionAcknowledgement.DeferComponentWithoutClaimingItsMessage =>
                ((SocketMessageComponent)interaction).DeferLoadingAsync(ephemeral: true),
            _ => Task.CompletedTask
        };
    }

    /// <summary>
    /// The acknowledgement rule, split out from the Discord.NET call so it can be asserted: a
    /// component that has not answered yet must be deferred the way that leaves the message it
    /// was clicked on alone, and anything already answered must be left entirely alone.
    /// </summary>
    internal static InteractionAcknowledgement ChooseAcknowledgement(bool hasResponded, bool isComponent)
    {
        if (hasResponded)
        {
            return InteractionAcknowledgement.None;
        }

        return isComponent
            ? InteractionAcknowledgement.DeferComponentWithoutClaimingItsMessage
            : InteractionAcknowledgement.DeferAsNewEphemeralReply;
    }

    private static Task EnsureAcknowledgedAsync(SlashContext context)
    {
        // An API context has no interaction and no three-second deadline, so every
        // "acknowledge before doing work" call in the handlers becomes a no-op rather than
        // needing a branch of its own at each of the ~30 call sites.
        return context.Interaction is { } interaction
            ? EnsureAcknowledgedAsync(interaction)
            : Task.CompletedTask;
    }

    /// <summary>
    /// The explicit-defer counterpart of <see cref="EnsureAcknowledgedAsync(SlashContext)"/>, for
    /// the handlers that deliberately claim the interaction as a new ephemeral reply.
    /// </summary>
    private static Task DeferIfInteractiveAsync(SlashContext context)
    {
        return context.Interaction?.DeferAsync(ephemeral: true) ?? Task.CompletedTask;
    }

    private async Task<FollowAutoRunLease?> TryBeginFollowAutoRunAsync(
        long? requiredResumeGeneration = null,
        long? waitForPredecessorRunId = null)
    {
        return await _followAutoLifecycle.TryBeginAsync(
            () => requiredResumeGeneration is not { } required
                || IsExpectedFollowAutoResumeIntent(required, _db.GetFollowAutoResumeIntent()),
            () =>
            {
                // A run with no monitor message cannot consume this legacy process-global flag.
                // Initialization runs under the lifecycle state gate, so this reset cannot erase
                // a concurrent Stop belonging to the newly installed lease.
                SetFollowAutoStopActionsRequested(false);
                _followAutoLockedNametag = null;
                _followAutoLockedNametagOrdinal = null;
            },
            waitForActiveRunId: waitForPredecessorRunId);
    }

    private void QueueFollowAutoRun(FollowAutoRunOptions options, FollowAutoRunLease run)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RunFollowAutoLoopAsync(options, run);
            }
            finally
            {
                // Signal only after RunFollowAutoLoopAsync has returned completely. A waiting new
                // run cannot install while any predecessor monitor/global cleanup is still live.
                await _followAutoLifecycle.CompleteUnwindAsync(run);
            }
        });
    }

    private bool IsFollowAutoRunning()
    {
        return _followAutoLifecycle.IsRunning;
    }

    private bool IsDcloneParkRunning()
    {
        return Volatile.Read(ref _dcloneCts) is not null;
    }

    private bool IsJoinAutoRunning()
    {
        return Volatile.Read(ref _joinAutoCts) is not null;
    }

    private async Task StopFollowAutoAsync(SlashContext context)
    {
        var stopComponent = context.Interaction as SocketMessageComponent;
        var expectedRun = stopComponent is null
            ? null
            : await _followAutoLifecycle.TryCaptureMonitorRunAsync(stopComponent.Message.Id);

        await DeferIfInteractiveAsync(context);
        if (stopComponent is not null && expectedRun is null)
        {
            await ModifyOriginalResponseWithMetricsAsync(
                context,
                "That Stop control belongs to an older follow-auto monitor; the current run was left unchanged.");
            return;
        }

        var cancel = await CancelFollowAutoIfRunningAsync(
            reason: null,
            showPostStopActions: stopComponent is not null
                && string.Equals(stopComponent.Data.CustomId, FollowAutoStopButtonId, StringComparison.Ordinal),
            expectedRun: expectedRun);
        if (cancel.Rejected)
        {
            await ModifyOriginalResponseWithMetricsAsync(
                context,
                "That Stop control belongs to an older follow-auto monitor; the current run was left unchanged.");
            return;
        }

        var stopSignals = await SignalFollowAutoStopAgentsAsync(cancel.RunId);
        var stopSummary = stopSignals.Attempted > 0
            ? $" Stop signal reached {stopSignals.Succeeded}/{stopSignals.Attempted} online agent(s) to abort in-flight follow clicks."
            : "";
        await ModifyOriginalResponseWithMetricsAsync(
            context,
            (cancel.WasRunning ? "follow-auto stopped." : "follow-auto is not running.") + stopSummary);
    }

    private async Task HandleFollowAutoStopLeaveButtonAsync(SocketMessageComponent component)
    {
        await component.DeferAsync(ephemeral: true);
        await ClearFollowAutoStopActionButtonsAsync(component.Message);
        await QueueSaveExitAllAsync(SlashContext.FromComponent(component, "save-exit"));
    }

    private async Task HandleFollowAutoStopFollowButtonAsync(SocketMessageComponent component)
    {
        // No DeferAsync here, unlike its sibling stop-action buttons: those mean to replace their
        // own message, this one does not. EnsureAcknowledgedAsync picks the acknowledgement form
        // that leaves the message this button sits on alone, and it runs ahead of the button
        // strip below because that strip is a REST round-trip.
        await EnsureAcknowledgedAsync(component);
        await ClearFollowAutoStopActionButtonsAsync(component.Message);
        await StartFollowAutoAsync(
            SlashContext.FromComponent(component, "follow"),
            delaySeconds: 0,
            watch: false,
            TimeSpan.FromMinutes(FollowAutoDefaultIdleMinutes));
    }

    private async Task HandleFollowAutoStopQuitButtonAsync(SocketMessageComponent component)
    {
        await component.DeferAsync(ephemeral: true);
        await ClearFollowAutoStopActionButtonsAsync(component.Message);
        await QueueQuitAllAsync(SlashContext.FromComponent(component, "quit"), "follow-auto quit button was pressed");
    }

    /// <summary>
    /// The live monitor's -1 / +1 buttons. They only move the run's target; the active-game watcher
    /// yields to the run loop, which benches a leaver or lets the join path pick up a promoted VM,
    /// so a press never blocks the gateway on a client operation.
    /// </summary>
    private async Task HandleFollowAutoBotCountButtonAsync(SocketMessageComponent component, int delta)
    {
        await EnsureAcknowledgedAsync(component);

        // Validation and mutation are one lifecycle operation. A handler may resume after any
        // await, so checking IsRunning/message ID separately allowed an old monitor's click to
        // pass, let that run unwind, and then change the successor run's process-global target.
        var control = await _followAutoLifecycle.WithActiveRunAsync(
            _ =>
            {
                var monitor = _followAutoMonitorMessage;
                if (!IsCurrentFollowAutoMonitorMessage(component.Message.Id, monitor?.Id))
                {
                    return new FollowAutoBotCountControl(
                        FollowAutoBotCountControlOutcome.StaleMonitor);
                }

                if (_followAutoTarget.LocalRestartArmed)
                {
                    return new FollowAutoBotCountControl(
                        FollowAutoBotCountControlOutcome.LocalRestartArmed);
                }

                // The monitor stops rendering these buttons in public mode, but the message ID is
                // unchanged across that edit, so a client still showing the pre-switch components
                // can land a press here. Public mode derives the target; honouring the press would
                // be undone by the next pulse.
                if (_followAutoTarget.Mode == FollowAutoPartyMode.Public)
                {
                    return new FollowAutoBotCountControl(
                        FollowAutoBotCountControlOutcome.PublicMode);
                }

                // Each press moves a real client in or out of a live game and takes a cycle to
                // land, so a double-click has to be refused rather than queued.
                if (!_followAutoRosterGate.TryAdjust(DateTimeOffset.UtcNow, out var retryAfter))
                {
                    return new FollowAutoBotCountControl(
                        FollowAutoBotCountControlOutcome.RateLimited,
                        RetryAfter: retryAfter);
                }

                var adjustment = _followAutoTarget.TryAdjust(
                    delta,
                    current => delta <= 0 || Volatile.Read(ref _followAutoRosterAvailability)
                        .CanAddBot(current, _followAutoLivePlayers.Value));
                if (adjustment.Outcome == FollowAutoTargetAdjustmentOutcome.Changed)
                {
                    PublishFollowAutoTargetAdjustment(adjustment);
                }

                return new FollowAutoBotCountControl(
                    adjustment.Outcome switch
                    {
                        FollowAutoTargetAdjustmentOutcome.Changed => FollowAutoBotCountControlOutcome.Changed,
                        FollowAutoTargetAdjustmentOutcome.LocalRestartArmed => FollowAutoBotCountControlOutcome.LocalRestartArmed,
                        FollowAutoTargetAdjustmentOutcome.Refused => FollowAutoBotCountControlOutcome.Refused,
                        _ => FollowAutoBotCountControlOutcome.AtLimit
                    },
                    adjustment.PreviousTarget,
                    adjustment.Target,
                    Monitor: monitor);
            },
            new FollowAutoBotCountControl(FollowAutoBotCountControlOutcome.NotRunning));

        if (control.Outcome == FollowAutoBotCountControlOutcome.NotRunning)
        {
            await component.FollowupAsync("follow-auto is not running, so there is no bot count to change.", ephemeral: true);
            return;
        }

        if (control.Outcome == FollowAutoBotCountControlOutcome.StaleMonitor)
        {
            await component.FollowupAsync(
                "That bot-count control belongs to an older follow-auto monitor; use the buttons on the current monitor.",
                ephemeral: true);
            return;
        }

        if (control.Outcome == FollowAutoBotCountControlOutcome.LocalRestartArmed)
        {
            await component.FollowupAsync(
                "The bot count is locked while local host recovery is armed; the recorded run will resume with the target shown on the monitor.",
                ephemeral: true);
            return;
        }

        if (control.Outcome == FollowAutoBotCountControlOutcome.PublicMode)
        {
            await component.FollowupAsync(
                "Public mode sets the bot count from the live player count, holding the game at "
                    + $"{FollowAutoPublicModePolicy.TargetPlayerCount} of {FollowAutoRosterPolicy.MaxPlayersPerGame} "
                    + "players. Press Private first to set it by hand.",
                ephemeral: true);
            return;
        }

        if (control.Outcome == FollowAutoBotCountControlOutcome.RateLimited)
        {
            await component.FollowupAsync(
                $"The bot count changed moments ago; give it {Math.Ceiling(control.RetryAfter.TotalSeconds):N0}s to take effect first.",
                ephemeral: true);
            return;
        }

        if (control.Outcome == FollowAutoBotCountControlOutcome.Refused)
        {
            await component.FollowupAsync(
                FormatFollowAutoAddBotRefusal(control.PreviousTarget),
                ephemeral: true);
            return;
        }

        if (control.Outcome == FollowAutoBotCountControlOutcome.AtLimit)
        {
            await component.FollowupAsync(
                $"Bot count is already at its {(delta > 0 ? "maximum" : "minimum")} of {control.PreviousTarget}.",
                ephemeral: true);
            return;
        }

        var current = control.PreviousTarget;
        var target = control.Target;
        _logger.LogInformation(
            "follow-auto bot count changed from {Previous} to {Target} by button.", current, target);
        await UpdateFollowAutoMonitorAsync(
            target > current
                ? $"Bot count raised to {target}; the next cycle brings one more VM into the game."
                : $"Bot count lowered to {target}; one VM leaves the game and waits warm at the lobby.",
            expectedMonitor: control.Monitor);
        await component.FollowupAsync(
            $"Bot count set to {target} ({FormatPartySize(target)} with the leader).",
            ephemeral: true);
    }

    /// <summary>
    /// The live monitor's Public / Private toggle. Like the -1 / +1 buttons it only moves run
    /// state; the roster watch notices the new target on its next comparison and the run loop does
    /// the actual joining and leaving, so a press never blocks the gateway on a client operation.
    /// </summary>
    private async Task HandleFollowAutoPartyModeButtonAsync(SocketMessageComponent component)
    {
        await EnsureAcknowledgedAsync(component);

        // Same lifecycle reasoning as HandleFollowAutoBotCountButtonAsync: validate and mutate
        // inside one active-run operation so an old monitor's click cannot reconfigure a
        // successor run after this handler resumes from an await.
        var control = await _followAutoLifecycle.WithActiveRunAsync(
            _ =>
            {
                var monitor = _followAutoMonitorMessage;
                if (!IsCurrentFollowAutoMonitorMessage(component.Message.Id, monitor?.Id))
                {
                    return new FollowAutoPartyModeControl(
                        FollowAutoBotCountControlOutcome.StaleMonitor);
                }

                var requested = _followAutoTarget.Mode == FollowAutoPartyMode.Private
                    ? FollowAutoPartyMode.Public
                    : FollowAutoPartyMode.Private;
                var change = _followAutoTarget.TrySetMode(requested);
                if (change.Outcome == FollowAutoPartyModeChangeOutcome.Changed
                    && change.Target != change.PreviousTarget)
                {
                    // Switching into public mode trims a full-game target to public's ceiling.
                    // That is a real target move and the gateway's +1 availability view has to
                    // follow it, exactly as a button press does.
                    PublishFollowAutoTargetAdjustment(new FollowAutoTargetAdjustment(
                        FollowAutoTargetAdjustmentOutcome.Changed,
                        change.PreviousTarget,
                        change.Target));
                }

                return new FollowAutoPartyModeControl(
                    change.Outcome switch
                    {
                        FollowAutoPartyModeChangeOutcome.Changed => FollowAutoBotCountControlOutcome.Changed,
                        FollowAutoPartyModeChangeOutcome.LocalRestartArmed => FollowAutoBotCountControlOutcome.LocalRestartArmed,
                        _ => FollowAutoBotCountControlOutcome.AtLimit
                    },
                    change,
                    monitor);
            },
            new FollowAutoPartyModeControl(FollowAutoBotCountControlOutcome.NotRunning));

        switch (control.Outcome)
        {
            case FollowAutoBotCountControlOutcome.NotRunning:
                await component.FollowupAsync(
                    "follow-auto is not running, so there is no party mode to change.",
                    ephemeral: true);
                return;
            case FollowAutoBotCountControlOutcome.StaleMonitor:
                await component.FollowupAsync(
                    "That party-mode control belongs to an older follow-auto monitor; use the buttons on the current monitor.",
                    ephemeral: true);
                return;
            case FollowAutoBotCountControlOutcome.LocalRestartArmed:
                await component.FollowupAsync(
                    "The party mode is locked while local host recovery is armed; the recorded run will resume in the mode shown on the monitor.",
                    ephemeral: true);
                return;
            case FollowAutoBotCountControlOutcome.AtLimit:
                await component.FollowupAsync(
                    "The party mode did not change; the monitor is already showing the current one.",
                    ephemeral: true);
                return;
        }

        if (control.Change is not { } change)
        {
            return;
        }

        // A mode switch resets the shared throttle rather than consuming it: the operator has just
        // handed the target over to (or taken it back from) the live player count, and making the
        // first automatic correction wait out a previous press would leave the game visibly wrong.
        _followAutoRosterGate.Reset();
        _followAutoPublicMode.Reset();
        _logger.LogInformation(
            "follow-auto party mode changed from {Previous} to {Mode} by button; bot target {PreviousTarget} -> {Target}.",
            change.PreviousMode,
            change.Mode,
            change.PreviousTarget,
            change.Target);

        var trimmed = change.Target != change.PreviousTarget
            ? $" Bot target trimmed to {change.Target} to make room."
            : "";
        // Said out loud rather than left for the operator to notice from the missing button: the
        // hold is private mode's alone, so switching modes drops it.
        var joinDelayCleared = change.JoinDelayCleared
            ? $" The {FollowAutoJoinDelayPolicy.DelaySeconds}s join delay was turned off with the mode switch."
            : "";
        await UpdateFollowAutoMonitorAsync(
            change.Mode == FollowAutoPartyMode.Public
                ? $"Public mode: holding a {FollowAutoPublicModePolicy.TargetPlayerCount}-player game so a real player can always join.{trimmed} "
                    + "The bot count now follows the live player count."
                : $"Private mode: the bot count is back to {change.Target} and only the -1 / +1 buttons change it.{joinDelayCleared}",
            expectedMonitor: control.Monitor);
        await component.FollowupAsync(
            change.Mode == FollowAutoPartyMode.Public
                ? $"Public mode is on. The fleet holds the game at {FollowAutoPublicModePolicy.TargetPlayerCount} of "
                    + $"{FollowAutoRosterPolicy.MaxPlayersPerGame} players, so bots leave as real players arrive and come "
                    + $"back as they go. Bot target is {change.Target}; the -1 / +1 buttons are hidden while it is derived."
                : $"Private mode is on. The bot target stays at {change.Target} ({FormatPartySize(change.Target)} with the "
                    + "leader) until you press -1 / +1.",
            ephemeral: true);
    }

    /// <summary>
    /// The live monitor's +30s / -30s delay toggle. Like the other monitor controls it only moves
    /// run state - the run loop reads the flag when it is about to walk clients into a new game -
    /// so a press never blocks the gateway on a client operation.
    /// </summary>
    private async Task HandleFollowAutoJoinDelayButtonAsync(SocketMessageComponent component)
    {
        await EnsureAcknowledgedAsync(component);

        // Same lifecycle reasoning as the bot-count and party-mode buttons: validate and mutate
        // inside one active-run operation, so a click on an old monitor cannot reconfigure a
        // successor run after this handler resumes from an await.
        var control = await _followAutoLifecycle.WithActiveRunAsync(
            _ =>
            {
                var monitor = _followAutoMonitorMessage;
                if (!IsCurrentFollowAutoMonitorMessage(component.Message.Id, monitor?.Id))
                {
                    return new FollowAutoJoinDelayControl(
                        FollowAutoBotCountControlOutcome.StaleMonitor);
                }

                var change = _followAutoTarget.TryToggleJoinDelay();
                return new FollowAutoJoinDelayControl(
                    change.Outcome switch
                    {
                        FollowAutoJoinDelayChangeOutcome.Changed
                            => FollowAutoBotCountControlOutcome.Changed,
                        FollowAutoJoinDelayChangeOutcome.LocalRestartArmed
                            => FollowAutoBotCountControlOutcome.LocalRestartArmed,
                        _ => FollowAutoBotCountControlOutcome.PublicMode
                    },
                    change.Armed,
                    monitor);
            },
            new FollowAutoJoinDelayControl(FollowAutoBotCountControlOutcome.NotRunning));

        switch (control.Outcome)
        {
            case FollowAutoBotCountControlOutcome.NotRunning:
                await component.FollowupAsync(
                    "follow-auto is not running, so there is no join delay to change.",
                    ephemeral: true);
                return;
            case FollowAutoBotCountControlOutcome.StaleMonitor:
                await component.FollowupAsync(
                    "That delay control belongs to an older follow-auto monitor; use the buttons on the current monitor.",
                    ephemeral: true);
                return;
            case FollowAutoBotCountControlOutcome.LocalRestartArmed:
                await component.FollowupAsync(
                    "The join delay is locked while local host recovery is armed; the recorded run resumes without it.",
                    ephemeral: true);
                return;
            case FollowAutoBotCountControlOutcome.PublicMode:
                await component.FollowupAsync(
                    "The join delay is a private-mode control. Public mode reads the live player count off a client that "
                        + "is inside the game, so holding the fleet out of it would leave the mode blind. Press Private first.",
                    ephemeral: true);
                return;
        }

        _logger.LogInformation(
            "follow-auto join delay {State} by button.",
            control.Armed ? "armed" : "cleared");
        await UpdateFollowAutoMonitorAsync(
            control.Armed
                ? $"Join delay on: the fleet waits {FollowAutoJoinDelayPolicy.DelaySeconds}s before entering each new "
                    + "game, so the leader can reach the boss before it scales up."
                : "Join delay off: the fleet joins each new game as soon as the leader is found.",
            expectedMonitor: control.Monitor);
        await component.FollowupAsync(
            control.Armed
                ? $"Join delay is on. Bots wait {FollowAutoJoinDelayPolicy.DelaySeconds}s at the lobby before entering "
                    + "each new game. New games only - a bot rejoining a game the fleet is already in is not held - and it "
                    + "turns itself off when this run ends."
                : "Join delay is off. Bots join each new game as soon as the leader is found.",
            ephemeral: true);
    }

    /// <summary>
    /// Switches a running follow-auto to a dclone park, from the Park button on its monitor.
    /// </summary>
    /// <remarks>
    /// A Stop and a park start as one control. The run is captured before the acknowledgement can
    /// yield (see <see cref="FollowAutoLifecycle.TryCaptureMonitorRunAsync"/>) and cancelled by that
    /// exact lease, so a click on an old monitor cannot stop a successor. The bots are left where
    /// follow-auto had them - in the leader's game - and every park attempt leaves whatever game
    /// its bot is in before creating one. The park only starts once follow-auto has fully unwound,
    /// and that wait runs off the gateway task: Discord.NET awaits this handler there, and a run
    /// finishing its current command can take far longer than an interaction should hold it.
    /// </remarks>
    private async Task HandleFollowAutoParkButtonAsync(SocketMessageComponent component)
    {
        var expectedRun = await _followAutoLifecycle.TryCaptureMonitorRunAsync(component.Message.Id);
        await EnsureAcknowledgedAsync(component);
        var context = SlashContext.FromComponent(component, "dclone");
        if (expectedRun is null)
        {
            await SetInitialCommandResponseAsync(
                context,
                "That Park control belongs to an older follow-auto monitor; the current run was left unchanged.",
                ephemeral: true);
            return;
        }

        if (_followAutoTarget.LocalRestartArmed)
        {
            await SetInitialCommandResponseAsync(
                context,
                "Park is locked while local host recovery is armed; the recorded follow-auto run resumes first.",
                ephemeral: true);
            return;
        }

        var cancel = await CancelFollowAutoIfRunningAsync(
            "switched to park mode",
            showPostStopActions: false,
            expectedRun: expectedRun);
        if (cancel.Rejected || !cancel.WasRunning)
        {
            await SetInitialCommandResponseAsync(
                context,
                "follow-auto was already stopping, so there was nothing to switch. Start a park with `/d2r dclone`.",
                ephemeral: true);
            return;
        }

        QueueFollowAutoStopSignal(cancel.RunId);
        _ = Task.Run(() => RunCommandWorkAsync(context, "follow-auto to park switch", async () =>
        {
            if (!await WaitForFleetModeUnwindAsync(expectedRun.Unwound))
            {
                await SetInitialCommandResponseAsync(
                    context,
                    $"follow-auto was stopped but had not finished unwinding after {FleetModeSwitchUnwindTimeout.TotalMinutes:0} "
                        + "minutes, so the park was not started. Run `/d2r dclone` once its monitor shows it stopped.",
                    ephemeral: true);
                return;
            }

            await StartDcloneParkAsync(context, watch: false, handOffNote: "Switched from follow-auto. ");
        }));
    }

    /// <summary>
    /// Switches a running dclone park to follow-auto in <paramref name="mode"/>, from the Private or
    /// Public button on the park's monitor.
    /// </summary>
    /// <remarks>
    /// The park's bots are left in their own games on purpose. follow-auto's check already confirms
    /// a pending client sitting in some other game by its HUD and Save-and-Exits it before joining
    /// the leader, so emptying every park up front would only duplicate that. What must not happen
    /// is a park create landing after follow-auto has started, so the start waits for the park, and
    /// every create it had in flight, to finish unwinding.
    /// </remarks>
    private async Task HandleDcloneFollowButtonAsync(SocketMessageComponent component, FollowAutoPartyMode mode)
    {
        await EnsureAcknowledgedAsync(component);
        var context = SlashContext.FromComponent(component, "follow");
        var modeLabel = mode == FollowAutoPartyMode.Public ? "public" : "private";
        var (outcome, unwound) = await CancelDcloneParkAsync(
            $"switched to follow-auto, {modeLabel} mode",
            expectedMonitorMessageId: component.Message.Id,
            handedOff: true);
        if (outcome != DcloneParkCancelOutcome.Stopped || unwound is null)
        {
            await SetInitialCommandResponseAsync(
                context,
                outcome == DcloneParkCancelOutcome.StaleMonitor
                    ? "That control belongs to an older dclone monitor; the current park was left unchanged."
                    : "The park is not running, so there was nothing to switch. Start follow-auto with `/d2r follow auto:true`.",
                ephemeral: true);
            return;
        }

        _ = Task.Run(() => RunCommandWorkAsync(context, "park to follow-auto switch", async () =>
        {
            if (!await WaitForFleetModeUnwindAsync(unwound))
            {
                await SetInitialCommandResponseAsync(
                    context,
                    $"The park was stopped but had not finished unwinding after {FleetModeSwitchUnwindTimeout.TotalMinutes:0} "
                        + "minutes, so follow-auto was not started. Run `/d2r follow auto:true` once the park monitor shows it stopped.",
                    ephemeral: true);
                return;
            }

            await StartFollowAutoAsync(
                context,
                delaySeconds: 0,
                watch: false,
                TimeSpan.FromMinutes(FollowAutoDefaultIdleMinutes),
                partyMode: mode,
                handOffNote: "Switched from the dclone park. ");
        }));
    }

    private static async Task<bool> WaitForFleetModeUnwindAsync(Task unwound)
    {
        try
        {
            await unwound.WaitAsync(FleetModeSwitchUnwindTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void PublishFollowAutoTargetAdjustment(FollowAutoTargetAdjustment adjustment)
    {
        while (true)
        {
            var availability = Volatile.Read(ref _followAutoRosterAvailability);
            if (availability.TargetBotCount == adjustment.Target
                || availability.TargetBotCount != adjustment.PreviousTarget)
            {
                // The loop either already published the adjusted roster, or it published an older
                // in-flight roster after this button moved the target. The latter remains safely
                // target-mismatched until the next reconciliation and cannot authorize another +1.
                return;
            }

            var adjusted = availability.AfterTargetAdjustment(adjustment.Target);
            if (ReferenceEquals(
                Interlocked.CompareExchange(
                    ref _followAutoRosterAvailability,
                    adjusted,
                    availability),
                availability))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Fleet clients occupying slots in the current game: the ones the run is tracking, plus the
    /// ones it gave up on removing and is not tracking any more. Both are in there taking up room.
    /// </summary>
    private static int CountFleetClientsInGame(FollowAutoAccountState accountState)
    {
        return accountState.JoinedCount + accountState.StrandedInGameCount;
    }

    /// <summary>
    /// Feeds one in-game pulse to public mode and applies the target it asks for. Returns whether
    /// the target moved, so the caller can hand the run loop a roster reconciliation immediately
    /// instead of waiting a heartbeat for the watch to notice.
    /// </summary>
    /// <param name="botsInGame">
    /// Fleet clients the run believes are inside the sampled game.
    /// </param>
    /// <param name="allJoined">
    /// Whether every rostered bot is in that game. False restricts the sample to giving a slot
    /// back to a human - mid join the party bar lags this number and the subtraction undercounts
    /// the humans, which could only ever grow the roster into the slot public mode exists to hold
    /// open. Yields carry no such risk, and going silent instead is what used to freeze the whole
    /// mode behind one client that would not join.
    /// </param>
    private async Task<bool> TryApplyPublicPartyTargetAsync(
        FollowPulseSample sample,
        int botsInGame,
        bool allJoined = true)
    {
        var (current, mode) = _followAutoTarget.Snapshot;
        if (mode != FollowAutoPartyMode.Public)
        {
            return false;
        }

        var desired = _followAutoPublicMode.Observe(
            sample.PlayerCount,
            sample.PlayerCountFresh,
            sample.InGame,
            botsInGame,
            current,
            allJoined);
        if (desired is not { } target || target == current)
        {
            return false;
        }

        // The same throttle the -1 / +1 buttons use, and for the same reason: each step moves a
        // real client in or out of a live game and takes a cycle to land. It is load-bearing here
        // rather than merely polite - a client that has left is out of the joined set before every
        // vantage's party bar has caught up, and reading that gap as more humans would walk the
        // target down a step at a time. Fifteen seconds is far longer than a party bar takes to
        // settle. Both checks come after the target genuinely differs, so a no-op pulse never
        // spends the interval.
        if (_followAutoTarget.LocalRestartArmed
            || !_followAutoRosterGate.TryAdjust(DateTimeOffset.UtcNow, out _))
        {
            return false;
        }

        // Mode-checked rather than a plain adjust: the mode was read at the top of this method and
        // the gateway can flip it to Private in between, on its own task. Rechecking it inside the
        // control's lock is what stops a derived target from landing on top of that press.
        var adjustment = _followAutoTarget.TrySetTargetForMode(target, FollowAutoPartyMode.Public);
        if (adjustment.Outcome != FollowAutoTargetAdjustmentOutcome.Changed)
        {
            return false;
        }

        PublishFollowAutoTargetAdjustment(adjustment);
        // Recomputed from this sample rather than read back off the tracker, which a concurrent
        // mode switch can clear - the message would then report no player count for a change that
        // definitely happened. Observe only hands back a target for a usable sample, so the
        // fallback is unreachable; it is the exact inverse of the rule that chose the target.
        var humans = FollowAutoPublicModePolicy.CountHumans(
                sample.PlayerCount,
                sample.PlayerCountFresh,
                sample.InGame,
                botsInGame)
            ?? FollowAutoPublicModePolicy.TargetPlayerCount - adjustment.Target;
        _logger.LogInformation(
            "follow-auto public mode moved the bot target from {Previous} to {Target} for {Humans} real player(s).",
            adjustment.PreviousTarget,
            adjustment.Target,
            humans);
        await UpdateFollowAutoMonitorAsync(
            adjustment.Target < adjustment.PreviousTarget
                ? $"Public mode: {humans} real player(s) in the game, so the bot count drops to {adjustment.Target}; "
                    + $"{adjustment.PreviousTarget - adjustment.Target} bot(s) leave to keep a slot open."
                : $"Public mode: {humans} real player(s) in the game, so the bot count rises to {adjustment.Target}; "
                    + $"{adjustment.Target - adjustment.PreviousTarget} bot(s) come back in and a slot stays open.");
        return true;
    }

    // "a 8-player game" reads badly in the one case that matters most - the default, full game.
    internal static string FormatPartySize(int botCount)
    {
        var players = botCount + 1;
        return $"{(players == 8 ? "an" : "a")} {players}-player game";
    }

    internal static bool IsCurrentFollowAutoMonitorMessage(
        ulong pressedMessageId,
        ulong? currentMonitorMessageId)
    {
        return currentMonitorMessageId == pressedMessageId;
    }

    private string FormatFollowAutoAddBotRefusal(int target)
    {
        if (target >= FollowAutoRosterPolicy.MaxBotCount)
        {
            return $"Already at {FollowAutoRosterPolicy.MaxBotCount} bots - with the leader that is a full "
                + $"{FollowAutoRosterPolicy.MaxPlayersPerGame}-player game.";
        }

        var availability = Volatile.Read(ref _followAutoRosterAvailability);
        if (availability.TargetBotCount != target || availability.ConnectedBenchedCount <= 0)
        {
            return "No connected benched VM is available to add to the roster.";
        }

        return $"The game is full ({_followAutoLivePlayers.Value}/{FollowAutoRosterPolicy.MaxPlayersPerGame} players); "
            + "a freed slot belongs to whoever left it, not to a waiting bot.";
    }

    private async Task HandleFollowAutoStopSleepButtonAsync(SocketMessageComponent component)
    {
        await component.DeferAsync(ephemeral: true);
        await ClearFollowAutoStopActionButtonsAsync(component.Message);
        await RunQuitAllThenSleepAsync(SlashContext.FromComponent(component, "system"));
    }

    private async Task RunQuitAllThenSleepAsync(SlashContext context)
    {
        // Same reason as QueueQuitAllAsync: the cancel below is a database write, and this whole
        // method runs before anything answers the button that started it.
        await EnsureAcknowledgedAsync(context);
        await CancelJoinAutoIfRunningAsync("follow-auto sleep button was pressed");
        await CancelDcloneParkIfRunningAsync("the sleep button was pressed");
        var followAutoCancel = await CancelFollowAutoIfRunningAsync("follow-auto sleep button was pressed");
        QueueFollowAutoStopSignal(followAutoCancel.RunId);

        // Keep one node snapshot for the whole operation. A worker that appears while
        // client quits are already running was never reconciled and must not suddenly
        // become a sleep target at the end.
        var sleepTargets = GetOrderedOnlineNodeTargets();
        var targetNodeSet = sleepTargets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fleetConnectivity = _registry.GetAccountConnectivity();
        var fleetOnlineEntries = fleetConnectivity.Online;
        var fleetOfflineEntries = fleetConnectivity.Offline;
        var entries = fleetOnlineEntries
            .Where(entry => targetNodeSet.Contains(_hyperV.ResolveNodeId(entry.Value)))
            .ToArray();
        var offlineEntries = fleetOfflineEntries
            .Where(entry => targetNodeSet.Contains(_hyperV.ResolveNodeId(entry.Value)))
            .ToArray();
        var unaddressableAgents = fleetConnectivity.ConnectedUnaddressableAgents
            .Where(entry => targetNodeSet.Contains(
                string.IsNullOrWhiteSpace(entry.NodeId) ? _config.NodeId : entry.NodeId))
            .ToArray();
        var processedAccountKeys = entries
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allResults = new List<AccountCommandRunResult>();
        if (entries.Length == 0)
        {
            await SetInitialCommandResponseAsync(
                context,
                $"No online accounts are available to quit; checking once more before queueing fleet sleep."
                    + FormatOfflineSkipSuffix(offlineEntries, unaddressableAgents)
                    + FormatOfflineNodeSkipSuffix(sleepTargets),
                ephemeral: true);
        }
        else
        {
            var staggerSeconds = _config.ClientStaggerSeconds ?? _config.StartAllDelaySeconds;
            await SetInitialCommandResponseAsync(
                context,
                $"Quitting {entries.Length} online account(s) before fleet sleep with {staggerSeconds}s stagger."
                    + FormatOfflineSkipSuffix(offlineEntries, unaddressableAgents)
                    + FormatOfflineNodeSkipSuffix(sleepTargets),
                ephemeral: true);

            allResults.AddRange(await RunQuitAllForSleepAsync(entries, staggerSeconds));
            var failures = allResults.Where(result => !result.Ok).ToArray();
            if (failures.Length > 0)
            {
                await ModifyOriginalResponseWithMetricsAsync(
                    context,
                    $"Fleet sleep skipped: quit completed for {allResults.Count - failures.Length}/{allResults.Count} account(s). Failed: "
                        + string.Join("; ", failures.Select(result => $"{result.AccountKey}: {result.Message}")));
                return;
            }
        }

        // Reconcile accounts that became online on one of the captured target nodes
        // while the initial staggered quit pass was running. Already-processed agents
        // stay connected after D2R exits, so key them out explicitly.
        var lateEntries = GetAccountEntriesByConnectivity().Online
            .Where(entry => targetNodeSet.Contains(_hyperV.ResolveNodeId(entry.Value)))
            .Where(entry => processedAccountKeys.Add(entry.Key))
            .ToArray();
        if (lateEntries.Length > 0)
        {
            var lateResults = await RunQuitAllForSleepAsync(lateEntries, staggerSeconds: 0);
            allResults.AddRange(lateResults);
            var lateFailures = lateResults.Where(result => !result.Ok).ToArray();
            if (lateFailures.Length > 0)
            {
                await ModifyOriginalResponseWithMetricsAsync(
                    context,
                    $"Fleet sleep skipped: {allResults.Count - lateFailures.Length}/{allResults.Count} client quit(s) succeeded. Newly online failures: "
                        + string.Join("; ", lateFailures.Select(result => $"{result.AccountKey}: {result.Message}")));
                return;
            }
        }

        var completionPrefix = allResults.Count == 0
            ? "No client quits were needed."
            : $"Quit complete: {allResults.Count}/{allResults.Count} account(s) succeeded.";
        await ModifyOriginalResponseWithMetricsAsync(
            context,
            $"{completionPrefix} Queueing sleep on {sleepTargets.Length} captured online D2RHost node(s)."
                + FormatOfflineNodeSkipSuffix(sleepTargets));
        await AnnounceSystemPowerActionAsync(context, HostSystemPowerAction.Sleep);
        await QueueQuickSleepTargetsAsync(context, sleepTargets, completionPrefix);
    }

    private string[] GetOrderedOnlineNodeTargets()
    {
        return _hyperV.NodeIds(onlineOnly: true)
            .OrderBy(nodeId => _hyperV.IsLocalNode(nodeId) ? 1 : 0)
            .ThenBy(nodeId => nodeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task QueueQuickSleepTargetsAsync(
        SlashContext context,
        IReadOnlyList<string> targets,
        string prefix)
    {
        var results = await QueueSystemActionsAsync(targets, HostSystemPowerAction.Sleep);
        var failures = results.Where(result => !result.Result.Ok).ToArray();
        if (failures.Length == 0)
        {
            return;
        }

        await ModifyOriginalResponseWithMetricsAsync(
            context,
            $"{prefix} Sleep queued on {results.Count - failures.Length}/{results.Count} node(s). Skipped/failed: "
                + string.Join("; ", failures.Select(failure => $"{failure.NodeId}: {failure.Result.Message}")));
    }

    private async Task<List<(string NodeId, CommandResult Result)>> QueueSystemActionsAsync(
        IReadOnlyList<string> targets,
        HostSystemPowerAction action)
    {
        var results = new List<(string NodeId, CommandResult Result)>();
        foreach (var nodeId in targets)
        {
            if (_hyperV.IsLocalNode(nodeId) && results.Any(result => !result.Result.Ok))
            {
                results.Add((
                    nodeId,
                    CommandResult.Failure(
                        "Master kept online because at least one worker did not confirm the requested power action.")));
                continue;
            }

            results.Add((nodeId, await _hyperV.QueueSystemActionAsync(nodeId, action)));
        }

        return results;
    }

    private string FormatOfflineNodeSkipSuffix(IReadOnlyCollection<string> selectedNodes)
    {
        var selected = selectedNodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var offline = _hyperV.NodeIds(onlineOnly: false)
            .Where(nodeId => !selected.Contains(nodeId))
            .ToArray();
        return offline.Length == 0
            ? ""
            : $" D2RHost nodes not online at selection were skipped: {string.Join(", ", offline)}.";
    }

    private async Task<AccountCommandRunResult[]> RunQuitAllForSleepAsync(
        KeyValuePair<string, AccountConfig>[] entries,
        int staggerSeconds)
    {
        var tasks = entries.Select(async (entry, index) =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(index * staggerSeconds));
                var result = await _registry.SendCommandAsync(
                    entry.Value.AgentId,
                    "quit_d2r",
                    BuildAccountArgs(entry.Key, entry.Value),
                    TimeSpan.FromSeconds(210));
                return new AccountCommandRunResult(entry.Key, result.Ok, result.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Quit before host sleep failed for {AccountKey}.", entry.Key);
                return new AccountCommandRunResult(
                    entry.Key,
                    false,
                    FormatExceptionWithAccountStatus(ex, entry.Key, entry.Value));
            }
        });

        return await Task.WhenAll(tasks);
    }

    // Same "if you quit, it should stop auto if its running" precedent as join-auto (issue #24) -
    // wired into the same quit/quit-all call sites as CancelJoinAutoIfRunningAsync.
    private async Task<FollowAutoCancelResult> CancelFollowAutoIfRunningAsync(
        string? reason,
        bool showPostStopActions = false,
        FollowAutoRunLease? expectedRun = null)
    {
        var cancel = await _followAutoLifecycle.CancelAsync(
            reason,
            wasRunning => SetFollowAutoStopActionsRequested(wasRunning && showPostStopActions),
            () =>
            {
                // The lifecycle gate also covers local recovery's Arm + Save + restart queue.
                // Whichever operation wins, Stop cannot be followed by a rewritten resume row.
                ClearFollowAutoLocalRestartState();
            },
            expectedRun);

        return new FollowAutoCancelResult(cancel.WasRunning, cancel.RunId, cancel.Rejected);
    }

    private async Task<FollowAutoStopSignalResult> SignalFollowAutoStopAgentsAsync(long followAutoRunId)
    {
        if (followAutoRunId <= 0)
        {
            return new FollowAutoStopSignalResult(0, 0);
        }

        var (online, _) = GetAccountEntriesByConnectivity();
        var agentIds = online
            .Select(entry => entry.Value.AgentId)
            .Where(agentId => !string.IsNullOrWhiteSpace(agentId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (agentIds.Length == 0)
        {
            return new FollowAutoStopSignalResult(0, 0);
        }

        var results = await Task.WhenAll(agentIds.Select(async agentId =>
        {
            try
            {
                var result = await _registry.SendCommandAsync(
                    agentId,
                    "follow_stop_auto",
                    new { followAutoRunId },
                    TimeSpan.FromSeconds(5));
                if (!result.Ok)
                {
                    _logger.LogDebug("follow_stop_auto returned failure for {AgentId}: {Message}", agentId, result.Message);
                }

                return result.Ok;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "follow_stop_auto failed for {AgentId}.", agentId);
                return false;
            }
        }));

        return new FollowAutoStopSignalResult(agentIds.Length, results.Count(ok => ok));
    }

    private void QueueFollowAutoStopSignal(long followAutoRunId)
    {
        if (followAutoRunId <= 0)
        {
            return;
        }

        _ = Task.Run(() => SignalFollowAutoStopAgentsAsync(followAutoRunId));
    }

    private void SetFollowAutoStopActionsRequested(bool requested)
    {
        lock (_followAutoStopActionSync)
        {
            _followAutoStopActionsRequested = requested;
        }
    }

    private bool ConsumeFollowAutoStopActionsRequested()
    {
        lock (_followAutoStopActionSync)
        {
            var requested = _followAutoStopActionsRequested;
            _followAutoStopActionsRequested = false;
            return requested;
        }
    }

    private void StartFollowAutoStopActionWindow(IUserMessage message)
    {
        CancellationTokenSource cts;
        long promptId;
        lock (_followAutoStopActionSync)
        {
            _followAutoStopActionCts?.Cancel();
            cts = new CancellationTokenSource();
            _followAutoStopActionCts = cts;
            promptId = ++_followAutoStopActionPromptId;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FollowAutoStopActionWindow, cts.Token);
                await ClearFollowAutoStopActionButtonsIfCurrentAsync(message, promptId);
            }
            catch (OperationCanceledException)
            {
                // Button was pressed or a newer prompt replaced this one.
            }
        });
    }

    private async Task ClearFollowAutoStopActionButtonsIfCurrentAsync(IUserMessage message, long promptId)
    {
        lock (_followAutoStopActionSync)
        {
            if (promptId != _followAutoStopActionPromptId)
            {
                return;
            }

            _followAutoStopActionCts = null;
            _followAutoStopActionPromptId++;
        }

        await ClearFollowAutoStopActionButtonsOnMessageAsync(message);
    }

    private async Task ClearFollowAutoStopActionButtonsAsync(IUserMessage? message)
    {
        lock (_followAutoStopActionSync)
        {
            _followAutoStopActionCts?.Cancel();
            _followAutoStopActionCts = null;
            _followAutoStopActionPromptId++;
        }

        if (message is not null)
        {
            await ClearFollowAutoStopActionButtonsOnMessageAsync(message);
        }
    }

    private async Task ClearFollowAutoStopActionButtonsOnMessageAsync(IUserMessage message)
    {
        try
        {
            await message.ModifyAsync(properties =>
                properties.Components = BuildFollowAutoMonitorComponents(running: false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear follow-auto post-stop action buttons.");
        }
    }

    private async Task StartFollowAutoMonitorAsync(
        IMessageChannel channel,
        bool metricsEnabled,
        int targetBotCount,
        FollowAutoPartyMode partyMode,
        IReadOnlySet<string> incumbents,
        FollowAutoRunLease run)
    {
        _followAutoStartedUtc = DateTimeOffset.UtcNow;
        _followAutoGameNumber = 0;
        _followAutoGamesCompleted = 0;
        _followAutoJoined = 0;
        _followAutoTotal = 0;
        var target = FollowAutoTargetControl.ClampTargetForMode(targetBotCount, partyMode);
        _followAutoTarget.Reset(target, partyMode);
        // Seed from the same incumbent-aware roster the loop uses. A resumed run can have an
        // offline recovery incumbent occupying a target slot, which makes an online newcomer a
        // real connected bench even when online count equals target count.
        var onlineAccountKeys = GetAccountEntriesByConnectivity().Online
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var initialRoster = FollowAutoRosterPolicy.ResolveRoster(
            onlineAccountKeys,
            target,
            incumbents);
        Volatile.Write(
            ref _followAutoRosterAvailability,
            new FollowAutoRosterAvailability(
                target,
                onlineAccountKeys.Count,
                initialRoster.Benched.Count(onlineAccountKeys.Contains)));
        _followAutoLivePlayers.Reset();
        _followAutoPublicMode.Reset();
        _followAutoRosterGate.Reset();
        _followAutoMetricsEnabled = metricsEnabled;
        try
        {
            var monitorMessage = await channel.SendMessageAsync(
                AppendMetrics(_followAutoMetricsEnabled, FormatFollowAutoMonitorMessage("Starting follow-auto.")),
                components: BuildFollowAutoMonitorComponents(running: true));
            run.AssociateMonitorMessage(monitorMessage.Id);
            _followAutoMonitorMessage = monitorMessage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start follow-auto monitor message.");
            _followAutoMonitorMessage = null;
        }
    }

    private async Task UpdateFollowAutoMonitorAsync(
        string status,
        int? joined = null,
        int? total = null,
        IUserMessage? expectedMonitor = null)
    {
        var monitorMessage = _followAutoMonitorMessage;
        if (monitorMessage is null
            || (expectedMonitor is not null && monitorMessage.Id != expectedMonitor.Id))
        {
            return;
        }

        if (joined is not null)
        {
            _followAutoJoined = joined.Value;
        }

        if (total is not null)
        {
            _followAutoTotal = total.Value;
        }

        try
        {
            var content = FormatFollowAutoMonitorMessage(status);
            await monitorMessage.ModifyAsync(properties =>
            {
                properties.Content = AppendMetrics(_followAutoMetricsEnabled, content);
                properties.Components = BuildFollowAutoMonitorComponents(running: true);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update follow-auto monitor message.");
        }
    }

    private async Task CompleteFollowAutoMonitorAsync(
        bool ok,
        string status,
        bool allowPostStopActions = true)
    {
        var monitorMessage = _followAutoMonitorMessage;
        if (monitorMessage is null)
        {
            return;
        }

        var showStopActions = ConsumeFollowAutoStopActionsRequested()
            && allowPostStopActions
            && ok;
        try
        {
            var content = FormatFollowAutoMonitorMessage(status);
            await monitorMessage.ModifyAsync(properties =>
            {
                properties.Content = AppendMetrics(_followAutoMetricsEnabled, content);
                properties.Components = showStopActions
                    ? BuildFollowAutoStopActionComponents(botsInGame: _followAutoJoined > 0)
                    : BuildFollowAutoMonitorComponents(running: false);
            });
            if (showStopActions)
            {
                StartFollowAutoStopActionWindow(monitorMessage);
            }

            await monitorMessage.AddReactionAsync(new Emoji(ok ? "✅" : "⛔"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not complete follow-auto monitor message.");
        }
        finally
        {
            _followAutoMonitorMessage = null;
        }
    }

    private string FormatFollowAutoMonitorMessage(string status)
    {
        var elapsed = _followAutoStartedUtc is { } started ? DateTimeOffset.UtcNow - started : TimeSpan.Zero;
        var title = _followAutoGameNumber > 0
            ? $"follow-auto monitor - Game #{_followAutoGameNumber}"
            : "follow-auto monitor";
        var (target, mode) = _followAutoTarget.Snapshot;
        var availability = Volatile.Read(ref _followAutoRosterAvailability);
        var online = availability.OnlineAccountCount;
        var benchedCount = availability.ConnectedBenchedCount;
        var benched = benchedCount > 0 ? $", {benchedCount} benched" : "";
        // Rostered is deliberately its own number rather than being folded into the target: with
        // fewer VMs online than the target, "7 of 3 online" reads as a contradiction, when what is
        // actually true is that 7 are wanted and only 3 can be supplied right now.
        var rostered = Math.Max(online - benchedCount, 0);
        var lines = new List<string>
        {
            title,
            $"Status: {status}",
            $"Bots in game: {_followAutoJoined}/{_followAutoTotal}",
            // The target is what the buttons change, and it is not the same number as either of
            // the two above: joined lags it while a VM is still warming up, and total counts only
            // the roster. Spelling out the resulting party size avoids the bots-vs-players
            // ambiguity that makes "7" mean two different things.
            $"Bot target: {target} - {FormatPartySize(target)} with the leader ({rostered} of {online} VM(s) rostered{benched})",
            $"Party mode: {FormatFollowAutoPartyMode(mode, target)}"
        };

        // Only rendered while the hold is armed. It is off for most runs, where a permanent line
        // saying nothing is happening would be noise.
        if (_followAutoTarget.JoinDelayArmed)
        {
            lines.Add(
                $"Join delay: {FollowAutoJoinDelayPolicy.DelaySeconds}s head start before the fleet enters a new game");
        }

        lines.Add($"Games completed: {_followAutoGamesCompleted}");
        lines.Add($"Session elapsed: {FormatElapsed(elapsed)}");

        if (!string.IsNullOrWhiteSpace(_followTemplates.BoundAccountKey))
        {
            lines.Insert(1, $"Bound friend source: {_followTemplates.BoundAccountKey}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The one line that says which rule owns the bot count right now, and - in public mode - what
    /// the live game actually looks like, since there the target is a consequence rather than a
    /// setting. The last-vantage case is called out by name instead of being papered over: at that
    /// point the game IS full, and the monitor must not claim a slot is being held.
    /// </summary>
    private string FormatFollowAutoPartyMode(FollowAutoPartyMode mode, int target)
    {
        if (mode == FollowAutoPartyMode.Private)
        {
            return "private - the -1 / +1 buttons set the bot count";
        }

        var aim = $"holding {FollowAutoPublicModePolicy.TargetPlayerCount} of "
            + $"{FollowAutoRosterPolicy.MaxPlayersPerGame} players";
        if (_followAutoPublicMode.LastHumanCount is not { } humanCount)
        {
            return $"public - {aim} so a real player can always join; waiting on the first live player count";
        }

        if (FollowAutoPublicModePolicy.IsHoldingLastVantage(target, humanCount))
        {
            return $"public - {humanCount} real player(s) leaves no room to give back; keeping 1 client in as the "
                + "only vantage that can see this game end, so the game is currently full";
        }

        // Deliberately the count that was actually READ, not humans plus the target. Those differ
        // whenever the fleet cannot supply the target - fewer VMs online than it asks for, or a
        // client still on its way in - and this line would then assert a party size nobody is
        // sitting in. It could also print a negative number of held-open slots in the window
        // between a human arriving and the yield landing.
        var seen = _followAutoPublicMode.LastPlayerCount is { } players
            ? $"last read {players}/{FollowAutoRosterPolicy.MaxPlayersPerGame} in the game{FormatPublicModeReadingAge()}"
            : "no live count yet";
        return $"public - {humanCount} real player(s), {seen}; {aim}";
    }

    /// <summary>
    /// How long ago public mode last got a usable count, printed only once that is old enough to
    /// matter. Silence on a fresh reading keeps the common line short; naming the age on an old one
    /// is what stops the monitor asserting a party size the fleet stopped being able to see.
    /// </summary>
    internal static readonly TimeSpan PublicModeStaleReadingAge = TimeSpan.FromSeconds(90);

    private string FormatPublicModeReadingAge()
    {
        if (_followAutoPublicMode.LastReadUtc is not { } readUtc)
        {
            return "";
        }

        var age = DateTimeOffset.UtcNow - readUtc;
        return age < PublicModeStaleReadingAge
            ? ""
            : $" ({FormatElapsed(age)} ago - no fleet client has been able to count since)";
    }

    private async Task RunFollowAutoLoopAsync(
        FollowAutoRunOptions options,
        FollowAutoRunLease run)
    {
        var runId = run.RunId;
        var cancellationToken = run.Token;
        var channel = options.Channel;
        var delaySeconds = options.DelaySeconds;
        var watch = options.Watch;
        var idleTimeout = options.IdleTimeout;
        var accountState = new FollowAutoAccountState();
        accountState.BeginRecovery(options.InitialRecoveryAccountKeys);
        var warmupFailures = new FollowWarmupFailureTracker();
        // Separate ladder from warmupFailures: a follow check can fail on a client whose warmup
        // succeeded, and nothing used to escalate that. See FollowCheckFailureTracker.
        var checkFailureLadder = new FollowCheckFailureTracker();
        var idleDeadlineUtc = DateTimeOffset.UtcNow + idleTimeout;
        var midJoinRotation = 0;
        var currentGameActive = false;
        var isolatedAccountsResyncedThisGame = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Per-account rejoin attempts spent this game on "this vantage is verifiably at the menus".
        // Lives out here, not in the watch: each resync re-enters the watch with fresh locals, so a
        // counter held inside it could never cap anything.
        var outOfGameResyncsThisGame = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rosterRefreshPending = false;
        string? lastWaitingReport = null;
        var lastWaitingReportUtc = DateTimeOffset.MinValue;
        CancellationTokenSource? watchCts = null;
        Task? watchTask = null;
        try
        {
            if (watch)
            {
                watchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                watchTask = RunGameAllWatchTickerAsync(
                    channel,
                    options.MetricsEnabled,
                    "follow-auto",
                    "follow-auto",
                    () => GetAccountEntriesByConnectivity().Online,
                    watchCts.Token);
            }

            await StartFollowAutoMonitorAsync(
                channel,
                options.MetricsEnabled,
                options.TargetBotCount,
                options.PartyMode,
                accountState.Incumbents,
                run);
            if (!string.IsNullOrWhiteSpace(options.ResumeReason))
            {
                await UpdateFollowAutoMonitorAsync(
                    $"Resumed after host recovery: {options.ResumeReason}",
                    joined: accountState.JoinedCount,
                    total: accountState.CountExpectedAccounts(
                        GetAccountEntriesByConnectivity().Online.Select(entry => entry.Key).ToArray()));
            }

            // Sync before the first check rather than waiting for the periodic sweep: a VM brought
            // online moments before Follow was pressed is the single most likely one to be holding
            // a stale or missing bind, and one round of pushes here saves it from sitting out the
            // first game entirely.
            try
            {
                var startupSync = await _followTemplates.ReconcileAsync(cancellationToken);
                if (startupSync.DidWork)
                {
                    _logger.LogInformation(
                        "follow-auto start synced follow templates: repaired {Repaired}, failures {Failures}.",
                        startupSync.RepairedAccountList,
                        string.Join("; ", startupSync.Failures));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "follow-auto start follow-template sync failed.");
            }

            async Task DelayNextFollowCheckAsync(bool afterLeave = false)
            {
                // The rejoin after a leave must not inherit a configured pulse-pacing delay: that
                // delay paces idle leader-presence checks, not how fast the fleet rejoins once
                // everyone has left. The short post-leave settle exists exactly to make the rejoin
                // prompt, so always use it after a leave.
                var seconds = afterLeave
                    ? FollowAutoPostLeaveCheckSeconds
                    : (delaySeconds > 0 ? delaySeconds : FollowAutoDefaultCheckSeconds);
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (allOnline, _) = GetAccountEntriesByConnectivity();
                // The roster is resolved fresh every cycle against whoever is reachable right now,
                // so a worker node that connects mid-run contributes its VMs the moment they are
                // available - they fill whatever slots the target has left open, without
                // displacing a bot already in the leader's game.
                var roster = FollowAutoRosterPolicy.ResolveRoster(
                    allOnline.Select(entry => entry.Key),
                    _followAutoTarget.TargetBotCount,
                    accountState.Incumbents);
                var benchedNowJoined = await BenchFollowAutoAccountsAsync(
                    roster, accountState, options, runId, cancellationToken);
                var online = allOnline
                    .Where(entry => roster.ActiveSet.Contains(entry.Key))
                    .ToArray();
                var onlineAccountKeys = online
                    .Select(entry => entry.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var connectedAccountKeys = allOnline
                    .Select(entry => entry.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                // Read by the gateway task when it builds the monitor's buttons.
                Volatile.Write(
                    ref _followAutoRosterAvailability,
                    new FollowAutoRosterAvailability(
                        roster.TargetBotCount,
                        allOnline.Length,
                        roster.Benched.Count(connectedAccountKeys.Contains)));
                if (benchedNowJoined.Length > 0)
                {
                    // Save and Exit is positive evidence that these players left this same game.
                    // Release their known capacity immediately; a later fresh sample can still
                    // raise the count again if another player filled a slot.
                    _followAutoLivePlayers.RecordConfirmedDepartures(benchedNowJoined.Length);
                    await UpdateFollowAutoMonitorAsync(
                        $"Bot count lowered to {roster.TargetBotCount}: {string.Join(", ", benchedNowJoined)} left the game and "
                            + "will wait warm at the lobby.",
                        joined: accountState.JoinedCount,
                        total: accountState.CountExpectedAccounts(onlineAccountKeys));
                }

                // Connectivity and roster membership are different things. A joined account whose
                // bench leave failed is intentionally off the active roster but is still online
                // and must remain joined so the next cycle makes attempts two and three.
                var newlyOfflineJoinedAccounts = accountState.BeginRecoveryForOfflineJoined(connectedAccountKeys);
                if (currentGameActive)
                {
                    isolatedAccountsResyncedThisGame.UnionWith(newlyOfflineJoinedAccounts);
                }

                // Parked accounts are online and expected, but must not re-attempt the current
                // game: a full game's freed slot belongs to whoever left it (a human), never to
                // a waiting bot. They rejoin automatically once the fleet advances.
                var pending = online
                    .Where(entry => !accountState.Joined.Contains(entry.Key)
                        && !accountState.ParkedGameFull.Contains(entry.Key))
                    .ToArray();
                var expectedAccountCount = accountState.CountExpectedAccounts(onlineAccountKeys);
                if (accountState.Joined.Any(roster.BenchedSet.Contains))
                {
                    // BenchFollowAutoAccountsAsync deliberately kept at least one failed leaver
                    // joined. Nothing in the active join path can make progress until its bounded
                    // retries finish, and falling through with an empty pending set would classify
                    // the cycle as "no bound account" and stop the run. Advance directly to the
                    // next roster cycle so attempts two and three really execute.
                    rosterRefreshPending = false;
                    idleDeadlineUtc = DateTimeOffset.UtcNow + idleTimeout;
                    await DelayNextFollowCheckAsync();
                    continue;
                }

                if (rosterRefreshPending)
                {
                    rosterRefreshPending = false;
                    // A successful bench already posted a more specific update after the live
                    // counters changed. Promotions and fleet arrivals need this refresh so the
                    // monitor immediately exposes the newly valid +1 state/capacity.
                    if (benchedNowJoined.Length == 0)
                    {
                        await UpdateFollowAutoMonitorAsync(
                            currentGameActive
                                ? $"Game #{_followAutoGameNumber}: live roster reconciled after the bot target or connected fleet changed."
                                : "Roster reconciled after the bot target or connected fleet changed.",
                            joined: accountState.JoinedCount,
                            total: expectedAccountCount);
                    }
                }

                if (accountState.CanWatch(onlineAccountKeys))
                {
                    var initialPulse = await TryFetchFollowPulseAsync(rotation: 0, _followAutoLockedNametag, accountState.Joined);
                    await TryLockNametagFromSampleAsync(initialPulse);
                    // Feeds the +1 button: a full game's free slot belongs to whoever left it.
                    // Null and lower samples leave the per-game high-water alone, so a degraded or
                    // lagging vantage cannot reopen the button after any screen observed a full game.
                    _followAutoLivePlayers.Observe(
                        initialPulse.PlayerCount,
                        initialPulse.PlayerCountFresh);
                    // Every rostered bot is in the game at this point, so the party bar and the
                    // joined set describe the same population and the humans can be counted out
                    // of it. If this moves the target, the watch below sees its snapshot go stale
                    // on the first comparison and hands the loop straight back a reconciliation.
                    await TryApplyPublicPartyTargetAsync(initialPulse, CountFleetClientsInGame(accountState));
                    if (!currentGameActive)
                    {
                        _followAutoGameNumber++;
                        currentGameActive = true;
                        isolatedAccountsResyncedThisGame.Clear();
                        outOfGameResyncsThisGame.Clear();
                        var parkedNote = accountState.ParkedGameFullCount > 0
                            ? $" ({FormatParkedGameFullNote(accountState)})"
                            : "";
                        await UpdateFollowAutoMonitorAsync(
                            initialPulse.LeaderBound
                                ? $"Game #{_followAutoGameNumber}: all joinable accounts joined.{parkedNote} Watching for the bound leader to leave.{FormatBoundLeaderWatchDetail(initialPulse)}"
                                : $"Game #{_followAutoGameNumber}: all joinable accounts joined.{parkedNote} Watching for someone to leave...",
                            joined: accountState.JoinedCount,
                            total: expectedAccountCount);
                    }

                    var watchResult = await WaitForFollowAutoGameEndAsync(
                        initialPulse.PlayerCount,
                        GetFollowAutoPlayerCountDropPollDelay,
                        new FollowAutoRosterWatchSnapshot(roster.TargetBotCount, connectedAccountKeys),
                        accountState,
                        isolatedAccountsResyncedThisGame,
                        outOfGameResyncsThisGame,
                        cancellationToken);
                    if (watchResult.ReconcileRoster)
                    {
                        // This is not game advancement. Keep joined/parking/per-game resync state
                        // intact and immediately let the outer loop apply the new target or fleet
                        // connectivity snapshot.
                        rosterRefreshPending = true;
                        continue;
                    }

                    if (watchResult.IsolatedAccountKey is { } isolatedAccountKey)
                    {
                        // Once recovery starts, this account is no longer allowed to contribute
                        // to the all-joined decision. Remember the exact key before sending the
                        // leave: a timeout, ambiguous reply, or disconnect must still route it
                        // through normal menu recovery when it next becomes reachable.
                        accountState.BeginRecovery(isolatedAccountKey);
                        isolatedAccountsResyncedThisGame.Add(isolatedAccountKey);
                        if (!watchResult.AttemptTargetedLeave)
                        {
                            await UpdateFollowAutoMonitorAsync(
                                $"Game #{_followAutoGameNumber}: {watchResult.Reason} {isolatedAccountKey} is marked recovery-pending and must complete the normal menu recovery and rejoin before the all-joined watch resumes; the healthy accounts remain in the current game.",
                                joined: accountState.JoinedCount,
                                total: accountState.CountExpectedAccounts(onlineAccountKeys));
                            idleDeadlineUtc = DateTimeOffset.UtcNow + idleTimeout;
                            await DelayNextFollowCheckAsync();
                            continue;
                        }

                        await UpdateFollowAutoMonitorAsync(
                            $"Game #{_followAutoGameNumber}: {watchResult.Reason} Leaving only {isolatedAccountKey} so it can rejoin the current game.",
                            joined: accountState.JoinedCount,
                            total: accountState.CountExpectedAccounts(onlineAccountKeys));

                        var isolatedAccount = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            isolatedAccountKey
                        };
                        var resyncLeaveResults = await LeaveAllJoinAutoAsync(
                            channel,
                            "follow-auto",
                            postResult: false,
                            metricsEnabled: _followAutoMetricsEnabled,
                            onlyAccounts: isolatedAccount,
                            followAutoRunId: runId,
                            cancellationToken: cancellationToken);
                        var resyncLeave = resyncLeaveResults.FirstOrDefault();
                        if (resyncLeave is { Ok: true })
                        {
                            await UpdateFollowAutoMonitorAsync(
                                $"Game #{_followAutoGameNumber}: {isolatedAccountKey} left its divergent game and will rejoin the bound friend on the next cycle.",
                                joined: accountState.JoinedCount,
                                total: accountState.CountExpectedAccounts(onlineAccountKeys));
                        }
                        else
                        {
                            await UpdateFollowAutoMonitorAsync(
                                $"Game #{_followAutoGameNumber}: {isolatedAccountKey}'s leave was not confirmed ({resyncLeave?.Message ?? "the account was no longer online"}). It remains pending and the normal menu recovery path will retry it.",
                                joined: accountState.JoinedCount,
                                total: accountState.CountExpectedAccounts(onlineAccountKeys));
                        }

                        idleDeadlineUtc = DateTimeOffset.UtcNow + idleTimeout;
                        await DelayNextFollowCheckAsync(afterLeave: true);
                        continue;
                    }

                    await UpdateFollowAutoMonitorAsync(
                        $"Game #{_followAutoGameNumber}: {watchResult.Reason}. Leaving the bound friend's game...",
                        joined: accountState.JoinedCount,
                        total: expectedAccountCount);
                    // Scope the leave to the accounts actually in the game and still online.
                    // A game-full-parked account sits at the lobby - sending it save-exit would
                    // burn its command gate on a guaranteed failure and end the whole run on a
                    // phantom "leave failed".
                    var (leaveOnline, _) = GetAccountEntriesByConnectivity();
                    var joinedLeaveTargets = accountState.Joined
                        .Where(accountKey => leaveOnline.Any(entry =>
                            string.Equals(entry.Key, accountKey, StringComparison.OrdinalIgnoreCase)))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var leaveResults = await LeaveAllJoinAutoAsync(
                        channel,
                        "follow-auto",
                        postResult: false,
                        metricsEnabled: _followAutoMetricsEnabled,
                        onlyAccounts: joinedLeaveTargets,
                        followAutoRunId: runId,
                        cancellationToken: cancellationToken);
                    var leaveFailures = leaveResults.Where(result => !result.Ok).ToArray();
                    if (leaveFailures.Length > 0)
                    {
                        // A failed Save and Exit must not end the run. This ended it for years and
                        // the symptom never looked like what it was: the fleet stops advancing
                        // games, every client sits in the finished game, and the monitor's last
                        // line is frozen mid-sentence - which reads as "next game is not being
                        // detected" rather than "the run is over". One agent dropping its socket
                        // mid-command was enough, and that is a transient: hc1 was back online,
                        // in game, on the same version, minutes later.
                        //
                        // Every cause of a failed leave seen in practice is either transient or
                        // local to one client - an agent that disconnected mid-command, a guest
                        // mid-restart, a client wedged in a menu - and none of them are a reason
                        // to stop advancing games for the other six. The comment on the target
                        // scoping just above already names this hazard ("end the whole run on a
                        // phantom leave failed") and defends against exactly one cause of it;
                        // this defends against the rest.
                        //
                        // Advancing is safe without any retry here because the client that could
                        // not leave is recovered by the path that already exists for it: the game
                        // advance clears joined/stranded state, so the account rejoins the normal
                        // join scan, and FollowAutoCheckAsync's own "confirmed unexpected game by
                        // strict HUD globes; using Save and Exit" branch takes it out of the old
                        // game before it joins the new one. That is the same safety net the
                        // stale-game leave path below relies on for the identical failure.
                        await UpdateFollowAutoMonitorAsync(
                            $"Game #{_followAutoGameNumber}: leave failed for "
                                + string.Join("; ", leaveFailures.Select(result => $"{result.AccountKey}: {result.Message}"))
                                + ". Advancing anyway; those clients are taken out of the old game by the normal "
                                + "menu recovery before they join the next one.",
                            joined: accountState.JoinedCount,
                            total: expectedAccountCount);
                    }

                    _followAutoGamesCompleted++;
                    currentGameActive = false;
                    _followAutoLivePlayers.Reset();
                    _followAutoPublicMode.Reset();
                    isolatedAccountsResyncedThisGame.Clear();
                    outOfGameResyncsThisGame.Clear();
                    // The game the parked accounts were shut out of is over; they resume the
                    // normal join scan for the next one alongside everyone else.
                    accountState.ClearStrandedInGame();
                    var unparkedAccounts = accountState.ClearGameFullParking();
                    var unparkedNote = unparkedAccounts.Length > 0
                        ? $" {string.Join(", ", unparkedAccounts)} sat out that full game and will rejoin with the fleet."
                        : "";
                    await UpdateFollowAutoMonitorAsync(
                        $"Game #{_followAutoGameNumber}: all accounts left.{unparkedNote} Preparing Game #{_followAutoGameNumber + 1}...",
                        joined: 0,
                        total: online.Length);

                    accountState.ClearJoined();
                    idleDeadlineUtc = DateTimeOffset.UtcNow + idleTimeout;
                    await DelayNextFollowCheckAsync(afterLeave: true);
                    continue;
                }

                // Partial join in progress: some accounts are in the game, some aren't. The
                // all-joined watch above never runs in this state, so without this probe a
                // leader who moves on (typically because one bot wedged and the operator got
                // tired of waiting) leaves the joined majority stranded in the abandoned game
                // until the wedged bot's own recovery eventually completes the joined set -
                // and then the stale vantages force everyone, including the bot that just
                // correctly joined the NEW game, through a leave/rejoin churn. One pulse of a
                // joined vantage per cycle catches the departure early: leave the stale game,
                // clear those accounts, and let the normal scan rejoin everyone wherever the
                // leader actually is. See FollowAutoPulsePolicy.ClassifyMidJoinProbe for the
                // decision table.
                if (accountState.JoinedCount > 0 && online.Length > 0)
                {
                    var probe = await ProbeMidJoinLeaderPresenceAsync(accountState.Joined, midJoinRotation++);
                    // The same pulse public mode would have got from the all-joined watch, which
                    // is not running while anyone is still pending. Restricted to yields (see
                    // TryApplyPublicPartyTargetAsync): a human who walks in while the fleet is
                    // short a bot gets their slot given back now instead of after the fleet
                    // finishes assembling - which, if the missing bot is stuck, is never. The new
                    // target is picked up by the roster resolve at the top of the next cycle.
                    if (probe.Sample is { } midJoinSample)
                    {
                        await TryApplyPublicPartyTargetAsync(
                            midJoinSample,
                            CountFleetClientsInGame(accountState),
                            allJoined: false);
                    }

                    if (FollowAutoPulsePolicy.ShouldAbortStaleMidJoinGame(probe.LockedPresent, probe.ConfirmAgreed))
                    {
                        var partialGameNumber = currentGameActive
                            ? _followAutoGameNumber
                            : _followAutoGameNumber + 1;
                        var departureTiming = currentGameActive
                            ? "while an isolated account was rejoining"
                            : "before everyone joined";
                        var staleAccountKeys = accountState.Joined
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var staleJoinedCount = staleAccountKeys.Count;
                        // These accounts need a fresh menu check even when save-exit times out
                        // or one target disconnects before dispatch. Move every intended target
                        // to recovery first; command replies only improve the status message.
                        accountState.BeginRecovery(staleAccountKeys);
                        await UpdateFollowAutoMonitorAsync(
                            $"Game #{partialGameNumber}: the bound leader left {departureTiming} ({staleJoinedCount}/{expectedAccountCount} in game); leaving the stale game so the fleet can rescan.{probe.Detail}",
                            joined: accountState.JoinedCount,
                            total: accountState.CountExpectedAccounts(onlineAccountKeys));
                        var staleLeaveResults = await LeaveAllJoinAutoAsync(
                            channel,
                            "follow-auto",
                            postResult: false,
                            metricsEnabled: _followAutoMetricsEnabled,
                            onlyAccounts: staleAccountKeys,
                            followAutoRunId: runId,
                            cancellationToken: cancellationToken);

                        var staleLeaveFailures = staleLeaveResults.Where(result => !result.Ok).ToArray();
                        if (staleLeaveFailures.Length > 0)
                        {
                            // Unlike the all-joined leave, a failure here doesn't end the run.
                            // Every target is already recovery-pending, so its next normal
                            // follow check performs the same safe in-game recovery before join.
                            await UpdateFollowAutoMonitorAsync(
                                $"Game #{partialGameNumber}: stale-game leave failed for "
                                    + string.Join("; ", staleLeaveFailures.Select(result => $"{result.AccountKey}: {result.Message}"))
                                    + ". Those accounts remain pending for normal menu recovery.",
                                joined: accountState.JoinedCount,
                                total: accountState.CountExpectedAccounts(onlineAccountKeys));
                        }

                        if (currentGameActive)
                        {
                            _followAutoGamesCompleted++;
                            currentGameActive = false;
                            isolatedAccountsResyncedThisGame.Clear();
                            outOfGameResyncsThisGame.Clear();
                        }

                        // The bound leader advanced whether or not this partial game had ever
                        // reached the all-joined/"active" milestone. Do not carry a full-game
                        // high-water from the abandoned game into the next roster scan.
                        _followAutoLivePlayers.Reset();
                        _followAutoPublicMode.Reset();

                        // The game everyone was parked out of is being abandoned; the rescan
                        // targets wherever the leader went next, a fresh capacity situation.
                        accountState.ClearGameFullParking();
                        accountState.ClearStrandedInGame();

                        idleDeadlineUtc = DateTimeOffset.UtcNow + idleTimeout;
                        await DelayNextFollowCheckAsync(afterLeave: true);
                        continue;
                    }
                }

                var offlineRecoveryAccounts = accountState.GetOfflineRecoveryAccounts(onlineAccountKeys);
                if (pending.Length == 0 && offlineRecoveryAccounts.Length > 0)
                {
                    var waitingReport = "waiting for recovery account(s) to reconnect: "
                        + string.Join(", ", offlineRecoveryAccounts);
                    if (!string.Equals(waitingReport, lastWaitingReport, StringComparison.Ordinal)
                        || DateTimeOffset.UtcNow - lastWaitingReportUtc >= TimeSpan.FromMinutes(5))
                    {
                        await UpdateFollowAutoMonitorAsync(
                            $"Waiting: {waitingReport}. The online peers will not be treated as an all-joined fleet.",
                            joined: accountState.JoinedCount,
                            total: expectedAccountCount);
                        lastWaitingReport = waitingReport;
                        lastWaitingReportUtc = DateTimeOffset.UtcNow;
                    }

                    if (DateTimeOffset.UtcNow >= idleDeadlineUtc)
                    {
                        await CompleteFollowAutoMonitorAsync(ok: false, "Idle timeout detected; follow-auto disabled.");
                        break;
                    }

                    await DelayNextFollowCheckAsync();
                    continue;
                }

                // Every online account is parked on a full game and none ever made it inside:
                // there is no vantage to observe the game ending, so the fleet cannot un-park
                // itself. Without this guard the empty pending set would fall through to the
                // no-results scan below and end the run with a bogus "no fingerprint" stop.
                if (pending.Length == 0 && accountState.JoinedCount == 0 && accountState.ParkedGameFullCount > 0)
                {
                    var parkedStallReport = $"{FormatParkedGameFullNote(accountState)}. No fleet account is inside that game to watch it end, "
                        + "so they stay warm at the lobby; restart follow-auto (Stop, then Follow) if the leader has already moved on.";
                    if (!string.Equals(parkedStallReport, lastWaitingReport, StringComparison.Ordinal)
                        || DateTimeOffset.UtcNow - lastWaitingReportUtc >= TimeSpan.FromMinutes(5))
                    {
                        await UpdateFollowAutoMonitorAsync(
                            $"Waiting: {parkedStallReport}",
                            joined: 0,
                            total: expectedAccountCount);
                        lastWaitingReport = parkedStallReport;
                        lastWaitingReportUtc = DateTimeOffset.UtcNow;
                    }

                    if (DateTimeOffset.UtcNow >= idleDeadlineUtc)
                    {
                        await CompleteFollowAutoMonitorAsync(ok: false, "Idle timeout detected; follow-auto disabled.");
                        break;
                    }

                    await DelayNextFollowCheckAsync();
                    continue;
                }

                // The leader's head start, held here rather than anywhere earlier because this is
                // the line that actually walks clients into the game. Everything above is leaving,
                // watching, or bookkeeping, and a wait placed there would delay recovery work that
                // has nothing to do with monster density.
                if (FollowAutoJoinDelayPolicy.ShouldHoldBeforeJoin(
                        _followAutoTarget.JoinDelayArmed,
                        _followAutoTarget.Mode,
                        CountFleetClientsInGame(accountState)))
                {
                    // Announced before it happens. A fleet that is online, bound, and simply not
                    // joining for half a minute is otherwise indistinguishable from the stalls that
                    // have been misread as healthy here before.
                    await UpdateFollowAutoMonitorAsync(
                        $"Holding {pending.Length} client(s) at the lobby for {FollowAutoJoinDelayPolicy.DelaySeconds}s "
                            + "so the leader can reach the boss before the game scales up.",
                        joined: accountState.JoinedCount,
                        total: expectedAccountCount);
                    // Cancellable, so Stop stays responsive through the hold instead of taking up
                    // to half a minute to be noticed. A press that lands mid-hold takes effect on
                    // the next game rather than cutting this one short.
                    await Task.Delay(
                        TimeSpan.FromSeconds(FollowAutoJoinDelayPolicy.DelaySeconds),
                        cancellationToken);
                }

                var anyBound = false;
                var unboundReports = new List<string>();
                var checkFailures = new List<string>();
                var waitingReports = new List<string>();
                var checkResults = await Task.WhenAll(
                    pending.Select(entry => RunFollowAutoCheckEntryAsync(options, entry, runId, cancellationToken)));
                var pendingByAccount = pending.ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
                var recoveryRequests = new Dictionary<string, FollowAutoNodeRecoveryRequest>(
                    StringComparer.OrdinalIgnoreCase);
                var vmRecoveryRequests = new List<FollowAutoVmRecoveryRequest>();
                var clientRestartRequests = new List<FollowAutoClientRestartRequest>();
                foreach (var result in checkResults)
                {
                    if (!pendingByAccount.TryGetValue(result.AccountKey, out var failedEntry))
                    {
                        continue;
                    }

                    var nodeId = _hyperV.ResolveNodeId(failedEntry.Value);
                    if (result.WarmupOutcome == FollowWarmupOutcome.Failed)
                    {
                        var failure = warmupFailures.RecordFailure(result.AccountKey, nodeId);
                        if (failure.VmRecoveryRequested)
                        {
                            vmRecoveryRequests.Add(new FollowAutoVmRecoveryRequest(
                                nodeId,
                                result.AccountKey,
                                failedEntry.Value,
                                failure.ConsecutiveFailures));
                        }

                        if (failure.RecoveryRequested)
                        {
                            recoveryRequests[nodeId] = new FollowAutoNodeRecoveryRequest(
                                nodeId,
                                result.AccountKey,
                                failure.ConsecutiveFailures);
                        }
                    }
                    else
                    {
                        // This includes a successful menu_ready and the live preflight proving
                        // menu_ready was unnecessary. Failures later in the friend/join check do
                        // not count as desktop-to-lobby warmup failures.
                        warmupFailures.RecordSuccess(result.AccountKey);
                        // A client that warms up again is proof its settings file is good now, so
                        // a future corruption starts with a full repair budget instead of
                        // inheriting the spent one from this incident.
                        _settingsRepairs.RecordRecovered(result.AccountKey);
                    }

                    // Independent of the warmup ladder above. A check failure on a client whose
                    // warmup succeeded used to be reported and otherwise ignored, which is how a
                    // bot that dropped mid-session could sit at the lobby for an entire run.
                    //
                    // A local stall is fed to the same ladder for the same reason. It answers
                    // ok=true - the agent is healthy, reachable, and explaining itself clearly -
                    // so it used to count as a success and reset the ladder on every cycle. What
                    // it actually means is that the client refused to click because it could not
                    // rule out being in a game, and nothing outside that VM will ever change its
                    // mind. One such client never joins, the fleet never reaches all-joined, and
                    // the all-joined watch is the only thing that advances a game - so the whole
                    // run stops advancing while every status line still reads healthy.
                    var localStall = result.Outcome == FollowAutoCheckOutcome.Waiting
                        && result.LocalStall;
                    if (result.Outcome == FollowAutoCheckOutcome.CheckFailure || localStall)
                    {
                        var checkFailure = localStall
                            ? checkFailureLadder.RecordStall(result.AccountKey, DateTimeOffset.UtcNow)
                            : checkFailureLadder.RecordFailure(result.AccountKey);
                        if (checkFailure.ClientRestartRequested)
                        {
                            clientRestartRequests.Add(new FollowAutoClientRestartRequest(
                                result.AccountKey,
                                failedEntry.Value,
                                checkFailure.TotalFailures,
                                result.Message,
                                Stalled: localStall));
                        }
                        else if (checkFailure.VmRecoveryRequested)
                        {
                            vmRecoveryRequests.Add(new FollowAutoVmRecoveryRequest(
                                nodeId,
                                result.AccountKey,
                                failedEntry.Value,
                                checkFailure.TotalFailures));
                        }
                    }
                    else
                    {
                        checkFailureLadder.RecordSuccess(result.AccountKey);
                    }
                }

                foreach (var request in clientRestartRequests
                             .OrderBy(request => request.AccountKey, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    idleDeadlineUtc = DateTimeOffset.UtcNow + idleTimeout;
                    lastWaitingReport = null;
                    await UpdateFollowAutoMonitorAsync(
                        (request.Stalled
                            ? $"{request.AccountKey} has refused to click for {FollowCheckFailureTracker.StallEscalationWindow.TotalMinutes:N0}+ minutes "
                                + $"because its own in-game safety check keeps coming back inconclusive ({request.LastMessage})."
                            : $"{request.AccountKey} failed {request.TotalFailures} follow checks in a row ({request.LastMessage}).")
                            + " Restarting D2R on that client before considering a VM power cycle.",
                        joined: accountState.JoinedCount,
                        total: accountState.CountExpectedAccounts(onlineAccountKeys));

                    var restart = await RestartFollowAutoClientAsync(request, cancellationToken);
                    await UpdateFollowAutoMonitorAsync(
                        restart.Ok
                            ? $"{request.AccountKey}: {restart.Message} It rejoins on the next follow check; "
                                + $"{FollowCheckFailureTracker.EscalationThreshold} more consecutive failures escalate further."
                            : $"{request.AccountKey}: could not restart D2R: {restart.Message} The next "
                                + $"{FollowCheckFailureTracker.EscalationThreshold} consecutive failures escalate further.",
                        joined: accountState.JoinedCount,
                        total: accountState.CountExpectedAccounts(onlineAccountKeys));
                }

                foreach (var request in vmRecoveryRequests
                             .OrderBy(request => request.AccountKey, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Only this account's guest goes down, so only this account is held back
                    // from the all-joined calculation while it rebuilds.
                    accountState.BeginRecovery(request.AccountKey);
                    idleDeadlineUtc = DateTimeOffset.UtcNow + idleTimeout;
                    lastWaitingReport = null;
                    await UpdateFollowAutoMonitorAsync(
                        $"{request.AccountKey} failed desktop-to-lobby warmup {request.ConsecutiveFailures} consecutive "
                            + $"times. Powering its VM off and back on before considering a {request.NodeId} restart.",
                        joined: accountState.JoinedCount,
                        total: accountState.CountExpectedAccounts(onlineAccountKeys));

                    // Follow-auto is the authoritative escalation path, so unlike the watchdog it
                    // waits for the latch instead of yielding it. If the watchdog got there first
                    // this simply picks up after that cycle finishes, and the account's strikes are
                    // still on the table if the guest came back just as broken.
                    var latched = await WaitToBeginVmRecoveryAsync(request.AccountKey, cancellationToken);
                    CommandResult vmRecovery;
                    try
                    {
                        vmRecovery = latched
                            ? await RecoverVmAsync(request.AccountKey, request.Account, cancellationToken)
                            : CommandResult.Failure(
                                $"another power cycle of {request.AccountKey}'s VM was still running after "
                                    + $"{FollowAutoNodeRecoveryTimeout.TotalMinutes:N0} minutes; no second one was started.");
                    }
                    finally
                    {
                        if (latched)
                        {
                            EndVmRecovery(request.AccountKey);
                        }
                    }

                    if (vmRecovery.Ok)
                    {
                        warmupFailures.RecordVmRecovered(request.AccountKey, request.NodeId);
                        // Release the check-failure latch too. The guest this account runs on is
                        // new, so a client that fails again has earned a fresh ladder rather than
                        // being stuck one rung below the top for the rest of the run.
                        checkFailureLadder.RecordVmRecovered(request.AccountKey);
                    }
                    else if (latched)
                    {
                        // Keep the strikes: the next failure escalates straight to the node
                        // restart rather than retrying a cycle that has already proven impossible.
                        warmupFailures.RecordVmRecoveryUnavailable(request.AccountKey, request.NodeId);
                    }

                    // Deliberately nothing recorded when the latch was never taken. Losing the race
                    // to the watchdog is not proof this guest cannot be cycled - the watchdog was
                    // running the very same cycle - and RecordVmRecoveryUnavailable would mark the
                    // VM as already tried, escalating the next failure to a restart of the whole
                    // physical node over a race. Recording nothing leaves both the strikes and the
                    // not-yet-cycled flag, so the next failure asks for this VM again.
                    var vmRecoveryOutlook = vmRecovery.Ok || !latched
                        ? $"five new consecutive warmup failures escalate to restarting {request.NodeId}."
                        : $"the next warmup failure escalates to restarting {request.NodeId}.";
                    await UpdateFollowAutoMonitorAsync(
                        vmRecovery.Ok
                            ? $"{request.AccountKey}: {vmRecovery.Message} Follow-auto continues; {vmRecoveryOutlook}"
                            : $"{request.AccountKey}: VM power cycle failed: {vmRecovery.Message} Follow-auto "
                                + $"continues; {vmRecoveryOutlook}",
                        joined: accountState.JoinedCount,
                        total: accountState.CountExpectedAccounts(onlineAccountKeys));
                }

                if (recoveryRequests.Count > 0)
                {
                    foreach (var request in recoveryRequests.Values
                                 .OrderBy(request => _hyperV.IsLocalNode(request.NodeId) ? 1 : 0)
                                 .ThenBy(request => request.NodeId, StringComparer.OrdinalIgnoreCase))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var nodeAccounts = GetFollowAutoAccountsForNode(request.NodeId);
                        var nodeAccountKeys = SelectExpectedRecoveryAccountKeys(
                            nodeAccounts.Select(entry => entry.Key),
                            onlineAccountKeys,
                            accountState.Joined,
                            accountState.RecoveryPending,
                            accountState.ParkedGameFull);

                        // Mark every account this run currently expects on the physical node
                        // before its VM-agent sockets disappear. Deliberately omit configured
                        // accounts that were already offline: the VM lifecycle restores only VMs
                        // that were Running, so waiting for an intentionally-Off VM would deadlock
                        // recovery forever. Otherwise expected offline accounts are omitted from
                        // the all-joined calculation and healthy peers can advance without them.
                        accountState.BeginRecovery(nodeAccountKeys);
                        var resumeRecoveryAccountKeys = _hyperV.IsLocalNode(request.NodeId)
                            ? SelectFollowAutoResumeRecoveryAccountKeys(
                                onlineAccountKeys,
                                accountState.RecoveryPending)
                            : nodeAccountKeys;
                        idleDeadlineUtc = DateTimeOffset.UtcNow + idleTimeout;
                        lastWaitingReport = null;
                        await UpdateFollowAutoMonitorAsync(
                            $"{request.TriggerAccountKey} failed desktop-to-lobby warmup "
                                + $"{request.ConsecutiveFailures} consecutive times. Recording and stopping the Running VMs on "
                                + $"{request.NodeId}, then restarting that node.",
                            joined: accountState.JoinedCount,
                            total: accountState.CountExpectedAccounts(onlineAccountKeys));

                        var recovery = await RecoverFollowAutoNodeAsync(
                            request,
                            nodeAccountKeys,
                            resumeRecoveryAccountKeys,
                            options,
                            run,
                            cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!recovery.RestartQueued)
                        {
                            // The node never rebooted. Allow a fresh incident to accumulate
                            // instead of leaving a permanent latch that can never request again.
                            warmupFailures.ResetNode(request.NodeId);
                            await UpdateFollowAutoMonitorAsync(
                                $"Could not start recovery for {request.NodeId}: {recovery.Message} "
                                    + $"Follow-auto remains active; five new consecutive warmup failures are required before another restart attempt.",
                                joined: accountState.JoinedCount,
                                total: accountState.CountExpectedAccounts(onlineAccountKeys));
                            continue;
                        }

                        if (recovery.LocalRestartQueued)
                        {
                            // This loop is terminal once its own host restart is queued. Retire the
                            // static Stop button now: leaving it on an old monitor lets a click
                            // minutes later cancel whichever successor run happens to be current.
                            await CompleteFollowAutoMonitorAsync(
                                ok: true,
                                $"{recovery.Message} This run is closed; its exact recovery record will resume after D2RHost starts again unless an operator Stop clears it.",
                                allowPostStopActions: false);
                            cancellationToken.ThrowIfCancellationRequested();
                            return;
                        }

                        if (!recovery.RecoveryComplete)
                        {
                            await CompleteFollowAutoMonitorAsync(
                                ok: false,
                                $"Node recovery did not complete for {request.NodeId}: {recovery.Message}");
                            return;
                        }

                        warmupFailures.ResetNode(request.NodeId);
                        idleDeadlineUtc = DateTimeOffset.UtcNow + idleTimeout;
                        await UpdateFollowAutoMonitorAsync(
                            recovery.Message + " Resuming the active follow run.",
                            joined: accountState.JoinedCount,
                            total: accountState.CountExpectedAccounts(
                                GetAccountEntriesByConnectivity().Online.Select(entry => entry.Key).ToArray()));
                    }

                    // Every result in this batch predates at least one node restart. Re-sample
                    // fresh status rather than applying stale joined/waiting outcomes.
                    await DelayNextFollowCheckAsync();
                    continue;
                }

                foreach (var result in checkResults)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    switch (result.Outcome)
                    {
                        case FollowAutoCheckOutcome.Joined:
                            anyBound = true;
                            var completedRecovery = accountState.RecoveryPending.Contains(result.AccountKey);
                            accountState.MarkJoined(result.AccountKey);
                            // The monitor is about to show concrete progress. Clear the
                            // de-duplication key so an unchanged restriction/wait reason from
                            // another pending account can immediately replace that progress
                            // line instead of being suppressed for five minutes.
                            lastWaitingReport = null;
                            await UpdateFollowAutoMonitorAsync(
                                completedRecovery
                                    ? $"{result.AccountKey} completed recovery and rejoined the bound friend's game."
                                    : $"{result.AccountKey} joined the bound friend's game.",
                                joined: accountState.JoinedCount,
                                total: accountState.CountExpectedAccounts(onlineAccountKeys));
                            idleDeadlineUtc = DateTimeOffset.UtcNow + idleTimeout;
                            break;
                        case FollowAutoCheckOutcome.Waiting:
                            anyBound = true;
                            waitingReports.Add($"{result.AccountKey}: {result.Message}");
                            break;
                        case FollowAutoCheckOutcome.GameFull:
                            anyBound = true;
                            var (fullAttempt, parked) = accountState.RecordGameFullStrike(result.AccountKey);
                            if (parked)
                            {
                                // Progress-style monitor line (not a de-duplicated waiting
                                // report): parking is a state change the operator must see.
                                lastWaitingReport = null;
                                await UpdateFollowAutoMonitorAsync(
                                    $"{result.AccountKey}: the bound friend's game is still full after {fullAttempt} attempts. "
                                        + "Parked warm at the lobby - it will NOT take a freed slot in this game, and rejoins automatically when the fleet moves to the next one.",
                                    joined: accountState.JoinedCount,
                                    total: accountState.CountExpectedAccounts(onlineAccountKeys));
                            }
                            else
                            {
                                waitingReports.Add(
                                    $"{result.AccountKey}: game is full (attempt {fullAttempt}/{FollowAutoAccountState.MaxGameFullAttempts}, will park after {FollowAutoAccountState.MaxGameFullAttempts})");
                            }

                            idleDeadlineUtc = DateTimeOffset.UtcNow + idleTimeout;
                            break;
                        case FollowAutoCheckOutcome.Unbound:
                            unboundReports.Add($"{result.AccountKey}: {result.Message}");
                            break;
                        case FollowAutoCheckOutcome.CheckFailure:
                            checkFailures.Add($"{result.AccountKey}: {result.Message}");
                            break;
                    }
                }

                // An unbound VM used to be invisible whenever any other VM was bound: its report
                // only reached the operator through the all-unbound stop path below, so a client
                // that missed the bind - offline at the time, rebuilt, or added to the fleet
                // afterwards - sat out whole sessions in silence while the rest of the fleet ran.
                // Repair it from the host's authoritative copy and put it on the monitor either
                // way. ReconcileAsync is a no-op for agents that already agree, so this costs
                // nothing on the overwhelmingly common path where nothing diverged.
                if (unboundReports.Count > 0 && anyBound)
                {
                    var repair = await _followTemplates.ReconcileAsync(cancellationToken);
                    waitingReports.Add(repair.Repaired.Count > 0
                        ? $"unbound: {string.Join("; ", unboundReports)} - pushed the bound friend to {repair.RepairedAccountList}, joining on the next check"
                        : $"unbound: {string.Join("; ", unboundReports)}"
                            + (_followTemplates.State.Recorded
                                ? ""
                                : " (no bind is recorded on the host yet - run /d2r follow bind:true once so late VMs are synced automatically)"));
                }

                if (!anyBound)
                {
                    if (checkFailures.Count > 0 && unboundReports.Count == 0)
                    {
                        var waitingReport = "checks could not complete: " + string.Join("; ", checkFailures);
                        if (!string.Equals(waitingReport, lastWaitingReport, StringComparison.Ordinal)
                            || DateTimeOffset.UtcNow - lastWaitingReportUtc >= TimeSpan.FromMinutes(5))
                        {
                            await UpdateFollowAutoMonitorAsync(
                                $"Waiting: {waitingReport}",
                                joined: accountState.JoinedCount,
                                total: expectedAccountCount);
                            lastWaitingReport = waitingReport;
                            lastWaitingReportUtc = DateTimeOffset.UtcNow;
                        }

                        if (DateTimeOffset.UtcNow >= idleDeadlineUtc)
                        {
                            await CompleteFollowAutoMonitorAsync(ok: false, "Idle timeout detected; follow-auto disabled.");
                            break;
                        }

                        await DelayNextFollowCheckAsync();

                        continue;
                    }

                    var details = string.Join("; ", checkFailures.Concat(unboundReports));
                    var reason = checkFailures.Count > 0
                        ? "follow-auto stopped: no VM reported a usable follow-bind fingerprint. "
                        : "follow-auto stopped: no follow-bind fingerprint is set. ";
                    await CompleteFollowAutoMonitorAsync(ok: false, reason + details);
                    break;
                }

                if (waitingReports.Count > 0)
                {
                    if (accountState.ParkedGameFullCount > 0)
                    {
                        waitingReports.Add(FormatParkedGameFullNote(accountState));
                    }

                    var waitingReport = string.Join("; ", waitingReports);
                    if (!string.Equals(waitingReport, lastWaitingReport, StringComparison.Ordinal)
                        || DateTimeOffset.UtcNow - lastWaitingReportUtc >= TimeSpan.FromMinutes(5))
                    {
                        await UpdateFollowAutoMonitorAsync(
                            $"Waiting: {waitingReport}",
                            joined: accountState.JoinedCount,
                            total: expectedAccountCount);
                        lastWaitingReport = waitingReport;
                        lastWaitingReportUtc = DateTimeOffset.UtcNow;
                    }
                }

                if (DateTimeOffset.UtcNow >= idleDeadlineUtc)
                {
                    await CompleteFollowAutoMonitorAsync(ok: false, "Idle timeout detected; follow-auto disabled.");
                    break;
                }

                await DelayNextFollowCheckAsync();
            }
        }
        catch (OperationCanceledException)
        {
            var reasonText = run.StopReason is { } reason ? $"follow-auto stopped: {reason}." : "follow-auto stopped.";
            await CompleteFollowAutoMonitorAsync(ok: true, reasonText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "follow-auto loop crashed.");
            await CompleteFollowAutoMonitorAsync(ok: false, $"follow-auto stopped on an unexpected error: {ex.Message}");
        }
        finally
        {
            watchCts?.Cancel();
            await AwaitWatchTickerStopAsync(watchTask, "follow-auto");
            watchCts?.Dispose();
        }
    }

    private KeyValuePair<string, AccountConfig>[] GetFollowAutoAccountsForNode(string nodeId)
    {
        return _registry.Accounts
            .Where(entry => string.Equals(
                _hyperV.ResolveNodeId(entry.Value),
                nodeId,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Stops one account's VM, confirms Hyper-V reports it Off, starts it again, confirms it is
    /// Running, and waits for its agent to reconnect.
    /// </summary>
    /// <remarks>
    /// Two callers reach this, from opposite directions, and it is the right answer for both.
    /// Follow-auto escalates here for a guest whose client will not warm up - most often a D2R that
    /// cannot initialize its graphics device, which no amount of in-guest relaunching fixes because
    /// the guest's display driver is what is broken. The stuck-VM watchdog arrives here for a guest
    /// that stopped running its agent at all. Both want the same thing: a cooperative stop, a hard
    /// cut only if the guest ignores it, and a fresh boot - which is exactly the ladder below.
    /// Callers must hold this account's recovery latch (see <see cref="TryBeginVmRecovery"/>) so the
    /// two paths cannot power-cycle the same guest at once.
    /// </remarks>
    // First rung of the check-failure ladder. The client is reachable and answering - its warmup
    // keeps succeeding - so the cheapest thing that clears wedged lobby state is a fresh D2R
    // process. A failure here is not fatal: the ladder simply escalates on the next streak.
    private async Task<CommandResult> RestartFollowAutoClientAsync(
        FollowAutoClientRestartRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _registry.SendCommandAsync(
                request.Account.AgentId,
                "restart_d2r",
                BuildAccountArgs(request.AccountKey, request.Account),
                TimeSpan.FromSeconds(210),
                cancellationToken);

            return result.Ok
                ? CommandResult.Success($"restarted D2R after {request.TotalFailures} failed follow checks.")
                : CommandResult.Failure(result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "restart_d2r after repeated follow-check failures failed for {AccountKey}.", request.AccountKey);
            return CommandResult.Failure(ex.Message);
        }
    }

    private async Task<CommandResult> RecoverVmAsync(
        string accountKey,
        AccountConfig account,
        CancellationToken cancellationToken)
    {
        var vmName = ResolveVmName(account);
        if (string.IsNullOrWhiteSpace(vmName))
        {
            return CommandResult.Failure($"{accountKey} has no VM name or agent id to power-cycle.");
        }

        var args = JsonSerializer.SerializeToElement(new { accountKey, vmName });

        var policy = new VmHangRecoveryPolicy(BuildVmHangRecoveryOptions());
        var hardPowerCuts = 0;
        var notes = new List<string>();

        // Ask what the guest is before asking it to do anything. Stop-VM -Force is a *guest
        // cooperative* shutdown routed through the integration services, so on a guest wedged on
        // the Windows boot logo it is addressed to software that was never running. Issuing it
        // anyway is not merely useless, it is where the recovery died: Hyper-V leaves the VM in
        // Stopping and the cmdlet eventually errors, and a failed graceful stop used to return
        // straight out of here - so the hard cut that is the actual fix for a wedged boot was
        // never reached, and the guest sat powered on until someone noticed it by hand.
        //
        // When the hypervisor already reports no contact with the guest OS, that question is
        // settled without touching the guest, and the cheapest correct action is the plug pull.
        // VmHangRecoveryPolicy still owns the decision - a guest whose heartbeat answers is never
        // cut here, it goes down the ordinary graceful path below.
        var preStop = await SendVmPowerCommandAsync(account, "vm_status", args, cancellationToken);
        if (preStop.Ok)
        {
            var preState = TryReadVmPowerState(preStop, out var observedPreState) ? observedPreState : null;
            var preHeartbeat = TryReadVmHeartbeat(preStop);
            var preUptime = TryReadVmUptime(preStop) ?? TimeSpan.Zero;
            var preAssessment = policy.AssessBootHang(preState, preHeartbeat, preUptime, hardPowerCuts);
            if (preAssessment.Verdict == VmHangVerdict.HardPowerCut)
            {
                var straightToCut = await HardPowerCutAsync(account, args, vmName, preAssessment.Reason, cancellationToken);
                hardPowerCuts++;
                notes.Add(straightToCut.Message);
                if (!straightToCut.Ok)
                {
                    return CommandResult.Failure(
                        $"{vmName} looked wedged before the recovery began ({preAssessment.Reason}), and the power cut "
                            + $"failed: {straightToCut.Message}");
                }

                return await StartAfterPowerCutAsync(
                    accountKey,
                    account,
                    args,
                    vmName,
                    policy,
                    hardPowerCuts,
                    notes,
                    cancellationToken);
            }
        }

        var stop = await SendVmPowerCommandAsync(account, "vm_stop", args, cancellationToken);
        if (!stop.Ok)
        {
            // A graceful stop that errors is evidence, not a dead end. It is the same guest-
            // cooperative call failing for the same reason a wedged guest fails it, so hand the
            // question to the policy rather than abandoning the recovery here.
            var lastState = await ReadVmPowerStateAsync(account, args, cancellationToken);
            var stopFailVerdict = policy.AssessStuckShutdown(lastState, TimeSpan.Zero, hardPowerCuts);
            if (stopFailVerdict.Verdict != VmHangVerdict.HardPowerCut)
            {
                return CommandResult.Failure(
                    $"Stop-VM for {vmName} failed: {stop.Message}. No power cut was attempted because {stopFailVerdict.Reason}.");
            }

            var cutAfterFailedStop = await HardPowerCutAsync(
                account,
                args,
                vmName,
                $"Stop-VM itself failed ({stop.Message}), which a guest not running its integration services is expected to do",
                cancellationToken);
            hardPowerCuts++;
            notes.Add(cutAfterFailedStop.Message);
            if (!cutAfterFailedStop.Ok)
            {
                return CommandResult.Failure(
                    $"Stop-VM for {vmName} failed and so did the power cut: {cutAfterFailedStop.Message}");
            }

            return await StartAfterPowerCutAsync(
                accountKey,
                account,
                args,
                vmName,
                policy,
                hardPowerCuts,
                notes,
                cancellationToken);
        }

        // Stop-VM -Force returns when Hyper-V says the guest is down, but a guest that ignores
        // the shutdown request can leave it in Stopping. Confirm Off before starting: Start-VM
        // against a VM that is not actually Off fails, and that failure would be reported as a
        // completed recovery.
        var stopRequestedAt = DateTimeOffset.UtcNow;
        var off = await WaitForVmPowerStateAsync(account, args, "Off", cancellationToken);
        if (!off.Ok)
        {
            // A forced shutdown went unanswered, which means the guest is not running the
            // integration services it is routed through. Before this existed, that ended the
            // recovery and escalated to restarting the entire physical node - taking every healthy
            // sibling VM on it down to fix one wedged guest.
            var lastState = await ReadVmPowerStateAsync(account, args, cancellationToken);
            var verdict = policy.AssessStuckShutdown(
                lastState,
                DateTimeOffset.UtcNow - stopRequestedAt,
                hardPowerCuts);
            if (verdict.Verdict != VmHangVerdict.HardPowerCut)
            {
                return CommandResult.Failure(
                    $"{vmName} did not confirm Off after Stop-VM: {off.Message}. No power cut was attempted because {verdict.Reason}.");
            }

            var cut = await HardPowerCutAsync(account, args, vmName, verdict.Reason, cancellationToken);
            hardPowerCuts++;
            notes.Add(cut.Message);
            if (!cut.Ok)
            {
                return CommandResult.Failure(
                    $"{vmName} did not confirm Off after Stop-VM and the power cut also failed: {cut.Message}");
            }
        }

        return await StartAfterPowerCutAsync(
            accountKey,
            account,
            args,
            vmName,
            policy,
            hardPowerCuts,
            notes,
            cancellationToken);
    }

    /// <summary>
    /// Brings a guest back up once it is Off, and watches the boot it starts.
    /// </summary>
    /// <remarks>
    /// Shared by all three ways a recovery can reach Off - the ordinary graceful stop, a stop
    /// that failed outright, and a guest cut straight from Running because the hypervisor had
    /// already lost contact with it. They differ only in how the machine was powered down; what
    /// has to happen afterwards is identical, and a guest can wedge on the very next boot no
    /// matter which of them got it there.
    /// </remarks>
    private async Task<CommandResult> StartAfterPowerCutAsync(
        string accountKey,
        AccountConfig account,
        JsonElement args,
        string vmName,
        VmHangRecoveryPolicy policy,
        int hardPowerCuts,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var start = await SendVmPowerCommandAsync(account, "vm_start", args, cancellationToken);
        if (!start.Ok)
        {
            return CommandResult.Failure($"{vmName} is Off, but Start-VM failed: {start.Message}");
        }

        var running = await WaitForVmPowerStateAsync(account, args, "Running", cancellationToken);
        if (!running.Ok)
        {
            return CommandResult.Failure($"{vmName} did not confirm Running after Start-VM: {running.Message}");
        }

        // A VM can reach Running and still freeze partway through boot - typically on the Windows
        // logo, sometimes with the spinner under it - and nothing inside the guest can say so
        // because its agent never started. The loop below waits for that agent, and while it waits
        // it keeps asking Hyper-V whether the guest is alive at all. If the hypervisor has no
        // contact with it after the grace window, the boot is wedged and only a power cut clears
        // it. A guest whose heartbeat answers is never cut - see VmHangRecoveryPolicy.
        var poweredOnAt = DateTimeOffset.UtcNow;
        var deadline = poweredOnAt + FollowAutoNodeRecoveryTimeout;
        var lastAssessment = "the agent simply had not reconnected yet";
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsAccountOnline(accountKey))
            {
                return CommandResult.Success(
                    $"{vmName} was powered off, confirmed Off, started again, and its agent reconnected."
                        + FormatVmRecoveryNotes(notes));
            }

            var status = await SendVmPowerCommandAsync(account, "vm_status", args, cancellationToken);
            var powerState = status.Ok && TryReadVmPowerState(status, out var observed) ? observed : null;
            var heartbeat = status.Ok ? TryReadVmHeartbeat(status) : VmHeartbeatStatus.Unknown;
            var assessment = policy.AssessBootHang(
                powerState,
                heartbeat,
                DateTimeOffset.UtcNow - poweredOnAt,
                hardPowerCuts);
            lastAssessment = assessment.Reason;

            if (assessment.Verdict == VmHangVerdict.HardPowerCut)
            {
                var cut = await HardPowerCutAsync(account, args, vmName, assessment.Reason, cancellationToken);
                hardPowerCuts++;
                notes.Add(cut.Message);
                if (!cut.Ok)
                {
                    return CommandResult.Failure(
                        $"{vmName} appears wedged ({assessment.Reason}), and the power cut failed: {cut.Message}");
                }

                var restart = await SendVmPowerCommandAsync(account, "vm_start", args, cancellationToken);
                if (!restart.Ok)
                {
                    return CommandResult.Failure(
                        $"{vmName} was powered off after a wedged boot, but Start-VM failed: {restart.Message}");
                }

                var backUp = await WaitForVmPowerStateAsync(account, args, "Running", cancellationToken);
                if (!backUp.Ok)
                {
                    return CommandResult.Failure(
                        $"{vmName} did not confirm Running after its power cut: {backUp.Message}");
                }

                // The guest is booting from cold now, so its reconnect budget restarts with it.
                poweredOnAt = DateTimeOffset.UtcNow;
                deadline = poweredOnAt + FollowAutoNodeRecoveryTimeout;
                continue;
            }

            await Task.Delay(FollowAutoNodeRecoveryPollInterval, cancellationToken);
        }

        return CommandResult.Failure(
            $"{vmName} is Running again, but its agent did not reconnect within "
                + $"{FollowAutoNodeRecoveryTimeout.TotalMinutes:N0} minutes ({lastAssessment})."
                + FormatVmRecoveryNotes(notes));
    }

    private VmHangRecoveryOptions BuildVmHangRecoveryOptions()
    {
        var configured = _config.VmHangRecovery;
        return new VmHangRecoveryOptions(
            configured.Enabled,
            TimeSpan.FromSeconds(configured.HangSuspectedAfterSeconds),
            TimeSpan.FromSeconds(configured.NoEvidenceGraceSeconds),
            TimeSpan.FromSeconds(configured.SettleSeconds),
            configured.MaxHardPowerCuts);
    }

    /// <summary>
    /// Cuts a wedged guest's power outright, waits for Hyper-V to confirm Off, then holds for the
    /// configured settle window before the caller starts it again. The settle is not cosmetic:
    /// Hyper-V releases the guest's devices and memory asynchronously after a turn-off, and
    /// starting into that teardown is its own way to produce the wedged boot this is clearing.
    /// </summary>
    private async Task<CommandResult> HardPowerCutAsync(
        AccountConfig account,
        JsonElement args,
        string vmName,
        string reason,
        CancellationToken cancellationToken)
    {
        var options = BuildVmHangRecoveryOptions();
        var turnOff = await SendVmPowerCommandAsync(account, "vm_turnoff", args, cancellationToken);
        if (!turnOff.Ok)
        {
            // A worker node older than the build that added vm_turnoff answers "Unsupported worker
            // command" here. Say so plainly rather than leaving an operator to wonder why the
            // recovery stopped: that node needs updating before this can help it.
            return CommandResult.Failure(
                $"vm_turnoff for {vmName} failed: {turnOff.Message} "
                    + "(a worker node that predates hard power-cut support cannot run this; update that node).");
        }

        var off = await WaitForVmPowerStateAsync(account, args, "Off", cancellationToken);
        if (!off.Ok)
        {
            return CommandResult.Failure(
                $"{vmName} did not confirm Off even after a hard power cut: {off.Message}");
        }

        await Task.Delay(options.SettleBeforeRestart, cancellationToken);
        return CommandResult.Success(
            $"cut power to {vmName} because {reason}, confirmed Off, and settled "
                + $"{options.SettleBeforeRestart.TotalSeconds:N0}s before starting it again");
    }

    private async Task<string?> ReadVmPowerStateAsync(
        AccountConfig account,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        var status = await SendVmPowerCommandAsync(account, "vm_status", args, cancellationToken);
        return status.Ok && TryReadVmPowerState(status, out var state) ? state : null;
    }

    private static string FormatVmRecoveryNotes(IReadOnlyCollection<string> notes)
    {
        return notes.Count == 0 ? "" : " Along the way: " + string.Join("; ", notes) + ".";
    }

    private async Task<CommandResult> SendVmPowerCommandAsync(
        AccountConfig account,
        string command,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _hyperV.HandleCommandAsync(
                account,
                new CommandRequest(Guid.NewGuid().ToString("N"), command, args),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CommandResult.Failure($"{command} threw: {ex.Message}");
        }
    }

    private async Task<CommandResult> WaitForVmPowerStateAsync(
        AccountConfig account,
        JsonElement args,
        string expectedState,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + FollowAutoVmPowerStateTimeout;
        var lastObserved = "unknown";
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await SendVmPowerCommandAsync(account, "vm_status", args, cancellationToken);
            if (status.Ok && TryReadVmPowerState(status, out var state))
            {
                lastObserved = state;
                if (string.Equals(state, expectedState, StringComparison.OrdinalIgnoreCase))
                {
                    return CommandResult.Success($"state {state}");
                }
            }
            else if (!status.Ok)
            {
                lastObserved = status.Message;
            }

            await Task.Delay(FollowAutoVmPowerStatePollInterval, cancellationToken);
        }

        return CommandResult.Failure(
            $"still {lastObserved} after {FollowAutoVmPowerStateTimeout.TotalSeconds:N0}s");
    }

    /// <summary>
    /// Reads Hyper-V's Heartbeat integration-service status out of a <c>vm_status</c> result.
    /// A missing field is <see cref="VmHeartbeatStatus.Unknown"/>, not a failure: the service can be
    /// disabled per-VM, and every worker node older than the build that added it omits the field
    /// entirely. Unknown buys no speed in VmHangRecoveryPolicy, so an old worker degrades to the
    /// patient path rather than to a wrong decision.
    /// </summary>
    internal static VmHeartbeatStatus TryReadVmHeartbeat(CommandResult status)
    {
        if (!TryReadVmStatusRoot(status, out var root)
            || !root.TryGetProperty("Heartbeat", out var heartbeat)
            || heartbeat.ValueKind != JsonValueKind.String)
        {
            return VmHeartbeatStatus.Unknown;
        }

        return VmHangRecoveryPolicy.ParseHeartbeat(heartbeat.GetString());
    }

    /// <summary>
    /// Reads how long Hyper-V says the VM has been powered on out of a <c>vm_status</c> result.
    /// </summary>
    /// <remarks>
    /// This is the hypervisor's own measurement, not the guest's, which is the whole reason it is
    /// usable here: a guest wedged on the boot logo cannot report its uptime, and the host needs to
    /// know exactly that guest has been powered on long enough to have finished booting.
    ///
    /// ConvertTo-Json expands a TimeSpan into its properties rather than emitting a string, so
    /// <c>Ticks</c> is the shape to expect. The other forms are accepted because a worker node can
    /// be running a different PowerShell major version than the master, and a missing or
    /// unrecognized uptime must degrade to "unknown" - which the policy treats as no evidence -
    /// rather than to zero, which would read as a VM that just booted and suppress recovery forever.
    /// </remarks>
    internal static TimeSpan? TryReadVmUptime(CommandResult status)
    {
        if (!TryReadVmStatusRoot(status, out var root)
            || !root.TryGetProperty("Uptime", out var uptime))
        {
            return null;
        }

        switch (uptime.ValueKind)
        {
            case JsonValueKind.Object:
                if (uptime.TryGetProperty("Ticks", out var ticks)
                    && ticks.ValueKind == JsonValueKind.Number
                    && ticks.TryGetInt64(out var tickValue))
                {
                    return TimeSpan.FromTicks(Math.Max(0, tickValue));
                }

                if (uptime.TryGetProperty("TotalSeconds", out var totalSeconds)
                    && totalSeconds.ValueKind == JsonValueKind.Number
                    && totalSeconds.TryGetDouble(out var secondsValue)
                    && double.IsFinite(secondsValue))
                {
                    return TimeSpan.FromSeconds(Math.Max(0, secondsValue));
                }

                return null;
            case JsonValueKind.Number:
                return uptime.TryGetInt64(out var rawTicks)
                    ? TimeSpan.FromTicks(Math.Max(0, rawTicks))
                    : null;
            case JsonValueKind.String:
                return TimeSpan.TryParse(
                    uptime.GetString(),
                    CultureInfo.InvariantCulture,
                    out var parsed) && parsed >= TimeSpan.Zero
                    ? parsed
                    : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Unwraps the PowerShell <c>output</c> string in a <c>vm_status</c> result into its JSON
    /// object, collapsing the single-element array ConvertTo-Json emits for some shapes.
    /// </summary>
    private static bool TryReadVmStatusRoot(CommandResult status, out JsonElement root)
    {
        root = default;
        if (status.Data is null)
        {
            return false;
        }

        try
        {
            var json = JsonSerializer.SerializeToElement(status.Data);
            if (!json.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            using var document = JsonDocument.Parse(output.GetString() ?? "");
            var element = document.RootElement;
            if (element.ValueKind == JsonValueKind.Array)
            {
                if (element.GetArrayLength() == 0)
                {
                    return false;
                }

                element = element[0];
            }

            // The document is disposed on the way out of this method, so hand back a detached copy.
            root = element.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the VM state out of a <c>vm_status</c> result. Hyper-V's VMState serializes as a
    /// number through ConvertTo-Json, and worker nodes running an older build still emit it that
    /// way, so both shapes are accepted.
    /// </summary>
    internal static bool TryReadVmPowerState(CommandResult status, out string state)
    {
        state = "";
        if (status.Data is null)
        {
            return false;
        }

        try
        {
            var json = JsonSerializer.SerializeToElement(status.Data);
            if (!json.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            using var document = JsonDocument.Parse(output.GetString() ?? "");
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                {
                    return false;
                }

                root = root[0];
            }

            if (!root.TryGetProperty("State", out var stateProperty))
            {
                return false;
            }

            state = stateProperty.ValueKind switch
            {
                JsonValueKind.String => stateProperty.GetString() ?? "",
                JsonValueKind.Number => stateProperty.GetInt32() switch
                {
                    2 => "Running",
                    3 => "Off",
                    6 => "Starting",
                    _ => stateProperty.GetInt32().ToString()
                },
                _ => ""
            };
            return !string.IsNullOrWhiteSpace(state);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Data is whatever the owning node put there (a local anonymous object, or a
            // JsonElement relayed from a worker). An unreadable payload means "state unknown",
            // never a crashed follow-auto run.
            return false;
        }
    }

    private async Task<FollowAutoNodeRecoveryResult> RecoverFollowAutoNodeAsync(
        FollowAutoNodeRecoveryRequest request,
        IReadOnlyList<string> nodeAccountKeys,
        IReadOnlyList<string> resumeRecoveryAccountKeys,
        FollowAutoRunOptions options,
        FollowAutoRunLease run,
        CancellationToken cancellationToken)
    {
        var localNode = _hyperV.IsLocalNode(request.NodeId);
        if (localNode)
        {
            return await QueueFollowAutoLocalNodeRestartAsync(
                request,
                resumeRecoveryAccountKeys,
                options,
                run);
        }

        var previousConnectedAt = _registry.NodeSnapshot()
            .FirstOrDefault(node => string.Equals(node.Id, request.NodeId, StringComparison.OrdinalIgnoreCase))
            ?.ConnectedAt;
        var restartRequestedUtc = DateTimeOffset.UtcNow;

        CommandResult restart;
        try
        {
            restart = await _hyperV.QueueSystemActionAsync(
                request.NodeId,
                HostSystemPowerAction.Restart,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FollowAutoNodeRecoveryResult(
                RestartQueued: false,
                RecoveryComplete: false,
                LocalRestartQueued: false,
                ex.Message);
        }

        if (!restart.Ok)
        {
            return new FollowAutoNodeRecoveryResult(
                RestartQueued: false,
                RecoveryComplete: false,
                LocalRestartQueued: false,
                restart.Message);
        }

        var deadline = DateTimeOffset.UtcNow + FollowAutoNodeRecoveryTimeout;
        var observedNewConnection = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = _registry.NodeSnapshot()
                .FirstOrDefault(snapshot => string.Equals(
                    snapshot.Id,
                    request.NodeId,
                    StringComparison.OrdinalIgnoreCase));
            observedNewConnection |= HasNewWorkerConnection(
                node,
                previousConnectedAt,
                restartRequestedUtc);

            var onlineAccountKeys = GetAccountEntriesByConnectivity().Online
                .Select(entry => entry.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (observedNewConnection
                && AreRecoveryAccountsOnline(nodeAccountKeys, onlineAccountKeys))
            {
                return new FollowAutoNodeRecoveryResult(
                    RestartQueued: true,
                    RecoveryComplete: true,
                    LocalRestartQueued: false,
                    $"{request.NodeId} reconnected after restart and all {nodeAccountKeys.Count} node account(s) are online.");
            }

            await Task.Delay(FollowAutoNodeRecoveryPollInterval, cancellationToken);
        }

        var missingAccounts = nodeAccountKeys
            .Except(
                GetAccountEntriesByConnectivity().Online.Select(entry => entry.Key),
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var connectionDetail = observedNewConnection
            ? "the worker reconnected, but its VM agents did not all return"
            : "the worker never established a new connection generation";
        return new FollowAutoNodeRecoveryResult(
            RestartQueued: true,
            RecoveryComplete: false,
            LocalRestartQueued: false,
            $"Timed out after {FollowAutoNodeRecoveryTimeout.TotalMinutes:N0} minutes: {connectionDetail}"
                + (missingAccounts.Length == 0
                    ? "."
                    : $"; still offline: {string.Join(", ", missingAccounts)}."));
    }

    private async Task<FollowAutoNodeRecoveryResult> QueueFollowAutoLocalNodeRestartAsync(
        FollowAutoNodeRecoveryRequest request,
        IReadOnlyList<string> resumeRecoveryAccountKeys,
        FollowAutoRunOptions options,
        FollowAutoRunLease run)
    {
        var result = await _followAutoLifecycle.WithCurrentRunAsync(
            run,
            async activeToken =>
            {
                var idleMinutes = (int)Math.Clamp(
                    Math.Ceiling(options.IdleTimeout.TotalMinutes),
                    1,
                    int.MaxValue);
                var (resumeTargetBotCount, resumePartyMode) = _followAutoTarget.ArmLocalRestart();
                try
                {
                    _db.SaveFollowAutoResumeIntent(new FollowAutoResumeIntent(
                        options.Channel.Id,
                        options.DelaySeconds,
                        options.Watch,
                        idleMinutes,
                        options.MetricsEnabled,
                        options.CharacterSlot,
                        options.FriendRow,
                        resumeRecoveryAccountKeys
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        $"{request.TriggerAccountKey} reached {request.ConsecutiveFailures} consecutive warmup failures on {request.NodeId}",
                        resumeTargetBotCount,
                        resumePartyMode == FollowAutoPartyMode.Public,
                        run.RunId));

                    // Stop can publish cancellation while recovery owns the journal gate, then
                    // waits on that gate to clear the durable intent. Recheck immediately before
                    // queueing so a Stop already in flight cannot be followed by the restart.
                    activeToken.ThrowIfCancellationRequested();
                    var restart = await _hyperV.QueueSystemActionAsync(
                        request.NodeId,
                        HostSystemPowerAction.Restart,
                        activeToken);
                    // QueueSystemAction may return successfully even if its implementation did
                    // not observe cancellation. Stop publishes this token before it waits to
                    // clear the restart journal, so never report a resumable recovery afterwards.
                    activeToken.ThrowIfCancellationRequested();
                    if (!restart.Ok)
                    {
                        ClearFollowAutoLocalRestartState();
                        return new FollowAutoNodeRecoveryResult(
                            RestartQueued: false,
                            RecoveryComplete: false,
                            LocalRestartQueued: false,
                            restart.Message);
                    }

                    return new FollowAutoNodeRecoveryResult(
                        RestartQueued: true,
                        RecoveryComplete: false,
                        LocalRestartQueued: true,
                        restart.Message);
                }
                catch (OperationCanceledException)
                {
                    ClearFollowAutoLocalRestartState();
                    throw;
                }
                catch (Exception ex)
                {
                    ClearFollowAutoLocalRestartState();
                    return new FollowAutoNodeRecoveryResult(
                        RestartQueued: false,
                        RecoveryComplete: false,
                        LocalRestartQueued: false,
                        ex.Message);
                }
            });

        if (result.LocalRestartQueued)
        {
            run.Token.ThrowIfCancellationRequested();
            ScheduleFollowAutoLocalRestartFallback(run.RunId);
        }

        return result;
    }

    private void ClearFollowAutoLocalRestartState()
    {
        try
        {
            _db.ClearFollowAutoResumeIntent();
        }
        finally
        {
            // Never leave the live target frozen just because durable cleanup failed. The database
            // exception still propagates, but future controls and recovery attempts remain usable.
            _followAutoTarget.DisarmLocalRestart();
        }
    }

    private void ScheduleFollowAutoLocalRestartFallback(long recoveryGeneration)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // The host lifecycle gives shutdown.exe 30 seconds before declaring a late
                // restart failure and restoring the journaled VMs. If this process is still
                // alive after that recovery window, the reboot did not happen: consume the
                // durable one-shot here and resume in-process. A successful restart terminates
                // this process before the delay and the new process consumes the same intent.
                await Task.Delay(FollowAutoLocalRestartFallbackDelay);
                _logger.LogWarning(
                    "The queued local restart did not terminate D2RHost; attempting to resume the recorded follow-auto run in the existing process.");
                await TryResumeFollowAutoAsync(recoveryGeneration);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not resume follow-auto after a late local restart failure.");
            }
        });
    }

    internal static bool IsExpectedFollowAutoResumeIntent(
        long recoveryGeneration,
        FollowAutoResumeIntent? intent)
    {
        return intent?.RecoveryGeneration == recoveryGeneration;
    }

    internal static bool HasNewWorkerConnection(
        FleetNodeSnapshot? snapshot,
        DateTimeOffset? previousConnectedAt,
        DateTimeOffset restartRequestedUtc)
    {
        if (snapshot is not { Connected: true, ConnectedAt: { } connectedAt })
        {
            return false;
        }

        return connectedAt >= restartRequestedUtc
            && (previousConnectedAt is not { } previous || connectedAt > previous);
    }

    internal static bool AreRecoveryAccountsOnline(
        IReadOnlyCollection<string> recoveryAccountKeys,
        IReadOnlySet<string> onlineAccountKeys)
    {
        return recoveryAccountKeys.All(onlineAccountKeys.Contains);
    }

    internal static string[] SelectExpectedRecoveryAccountKeys(
        IEnumerable<string> nodeAccountKeys,
        IReadOnlySet<string> onlineAccountKeys,
        IReadOnlySet<string> joinedAccountKeys,
        IReadOnlySet<string> recoveryPendingAccountKeys,
        IReadOnlySet<string> parkedAccountKeys)
    {
        return nodeAccountKeys
            .Where(accountKey => onlineAccountKeys.Contains(accountKey)
                || joinedAccountKeys.Contains(accountKey)
                || recoveryPendingAccountKeys.Contains(accountKey)
                || parkedAccountKeys.Contains(accountKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string[] SelectFollowAutoResumeRecoveryAccountKeys(
        IReadOnlySet<string> onlineAccountKeys,
        IReadOnlySet<string> recoveryPendingAccountKeys)
    {
        return onlineAccountKeys
            .Concat(recoveryPendingAccountKeys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Replaces one account's corrupted Settings.json with a healthy fleet member's copy and marks
    /// its separate ready/relaunch phase pending. Returns null only when this account has no repair
    /// work; blocked, donor, and copy failures return a result while preserving the pending stage.
    /// </summary>
    /// <remarks>
    /// The transfer is master-side on purpose: the donor and the broken VM can sit on different
    /// physical nodes, and only the master can address both. The file rides the existing
    /// agent-command tunnel (master to worker to VM) as a command argument - a few KB of JSON.
    /// </remarks>
    private async Task<SettingsRepairAttempt?> TryRepairSettingsFromFleetAsync(
        string accountKey,
        AccountConfig account,
        string? statusJson,
        DateTimeOffset statusObservedUtc,
        DateTimeOffset? expectedTargetConnectedAt,
        DateTimeOffset? expectedTargetStatusReceivedAt,
        string? expectedIncidentToken,
        CancellationToken cancellationToken)
    {
        if (SettingsRepairPolicy.NeedsDonorSettings(statusJson))
        {
            if (expectedTargetConnectedAt is null)
            {
                return new SettingsRepairAttempt(
                    false,
                    accountKey,
                    DonorAccountKey: null,
                    "the reset-settings report was not received on the target agent's current connection.");
            }

            _settingsRepairs.ObserveCurrentStatus(accountKey, statusJson, statusObservedUtc);
        }

        if (_settingsRepairs.RecoveryFor(accountKey).Stage != SettingsRecoveryStage.RepairRequired)
        {
            return null;
        }

        var targetAgent = _registry.GetAgent(account.AgentId);
        if (!HasStatusFromCurrentAgentConnection(targetAgent)
            || expectedTargetConnectedAt is null
            || targetAgent!.ConnectedAt != expectedTargetConnectedAt)
        {
            return new SettingsRepairAttempt(
                false,
                accountKey,
                DonorAccountKey: null,
                "target agent reconnected or has not sent fresh status on its current connection; waiting before replacing settings.");
        }

        using var lease = _settingsRepairs.TryAcquireRepair(
            accountKey,
            DateTimeOffset.UtcNow,
            out var blockedReason);
        if (lease is null)
        {
            _logger.LogWarning("Settings repair for {AccountKey} skipped: {Reason}", accountKey, blockedReason);
            return new SettingsRepairAttempt(false, accountKey, DonorAccountKey: null, blockedReason);
        }

        var donorOrder = SettingsRepairPolicy.SelectDonorOrder(
            accountKey,
            _registry.Accounts.Select(entry =>
            {
                var agent = _registry.GetAgent(entry.Value.AgentId);
                return new SettingsDonorCandidate(
                    entry.Key,
                    agent?.Connected == true,
                    agent?.LastStatusJson,
                    agent?.ConnectedAt,
                    agent?.StatusReceivedAt);
            }),
            _config.SettingsDonorAccountKey);

        if (donorOrder.Count == 0)
        {
            return new SettingsRepairAttempt(
                false,
                accountKey,
                DonorAccountKey: null,
                "no healthy donor: every other client is offline, itself corrupt, or has not reached character select.");
        }

        var donorFailures = new List<string>();
        // Capped because this runs inside one account's follow-auto warmup check: an unbounded walk
        // of a large fleet could spend donorCount x SettingsExportCommandTimeout there, and a
        // healthy fleet answers on the first candidate. If the first few cannot export, the problem
        // is not "try more of them".
        foreach (var donorKey in donorOrder.Take(MaxSettingsDonorAttempts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_registry.Accounts.TryGetValue(donorKey, out var donor))
            {
                continue;
            }

            var donorAgent = _registry.GetAgent(donor.AgentId);
            if (!HasStatusFromCurrentAgentConnection(donorAgent)
                || SettingsRepairPolicy.ClassifyDonor(donorAgent!.LastStatusJson)
                    == SettingsRepairPolicy.DonorHealth.Unusable)
            {
                donorFailures.Add($"{donorKey}: no fresh healthy status on its current connection");
                continue;
            }

            string donorContent;
            try
            {
                var export = await _registry.SendCommandAsync(
                    donor.AgentId,
                    "settings_export",
                    args: null,
                    SettingsExportCommandTimeout,
                    cancellationToken,
                    expectedAgentConnectedAt: donorAgent.ConnectedAt);
                if (!export.Ok || export.Data is not { } exportData
                    || !TryGetString(exportData, "content", out donorContent))
                {
                    donorFailures.Add($"{donorKey}: {export.Message}");
                    continue;
                }
            }
            catch (Exception ex)
            {
                donorFailures.Add($"{donorKey}: {ex.Message}");
                continue;
            }

            CommandResultInfo repair;
            try
            {
                var currentTarget = _registry.GetAgent(account.AgentId);
                if (!HasStatusFromCurrentAgentConnection(currentTarget)
                    || currentTarget!.ConnectedAt != expectedTargetConnectedAt)
                {
                    return new SettingsRepairAttempt(
                        false,
                        accountKey,
                        donorKey,
                        "target agent reconnected while the donor was exporting; waiting for fresh target status without charging a repair attempt.");
                }

                if (currentTarget.StatusReceivedAt != expectedTargetStatusReceivedAt)
                {
                    var currentStatusJson = currentTarget.LastStatusJson;
                    if (SettingsRepairPolicy.NeedsReadyAfterRepair(currentStatusJson))
                    {
                        _settingsRepairs.ObserveCurrentStatus(
                            accountKey,
                            currentStatusJson,
                            DateTimeOffset.UtcNow);
                        return new SettingsRepairAttempt(
                            true,
                            accountKey,
                            donorKey,
                            "target reports that a settings copy already landed; continuing with ready instead of copying again.");
                    }

                    if (SettingsRepairPolicy.ClassifyDonor(currentStatusJson)
                        != SettingsRepairPolicy.DonorHealth.Unusable)
                    {
                        _settingsRepairs.RecordRecovered(accountKey);
                        return new SettingsRepairAttempt(
                            true,
                            accountKey,
                            donorKey,
                            "target recovered while the donor was exporting; cancelled the settings copy without charging an attempt.");
                    }

                    if (SettingsRepairPolicy.NeedsDonorSettings(currentStatusJson))
                    {
                        var currentIncidentToken = SettingsRepairPolicy.RepairEvidenceToken(currentStatusJson);
                        if (!string.Equals(currentIncidentToken, expectedIncidentToken, StringComparison.Ordinal))
                        {
                            _settingsRepairs.ObserveCurrentStatus(
                                accountKey,
                                currentStatusJson,
                                DateTimeOffset.UtcNow);
                            return new SettingsRepairAttempt(
                                false,
                                accountKey,
                                donorKey,
                                "target reported a different settings-reset incident while the donor was exporting; yielding before retry.");
                        }
                    }
                    // Unknown/NotRunning is expected after a failed post-quit copy. The confirmed
                    // incident stays authorized; only positive recovery/ready evidence cancels it.
                }

                // Acquisition, donor exports, and generation/precondition rejections are free.
                // The target agent reports a charge only after its durable Prepared journal lands.
                cancellationToken.ThrowIfCancellationRequested();
                repair = await _registry.SendCommandAsync(
                    account.AgentId,
                    "settings_repair",
                    new { settingsContent = donorContent, settingsSourceAgentId = donor.AgentId },
                    SettingsRepairCommandTimeout,
                    cancellationToken,
                    expectedAgentConnectedAt: expectedTargetConnectedAt);
            }
            catch (Exception ex)
            {
                return new SettingsRepairAttempt(false, accountKey, donorKey, ex.Message);
            }

            var attemptWasCharged = TryGetAgentChargedSettingsRepairAttempt(
                repair,
                out var durableAttemptCount);
            if (attemptWasCharged)
            {
                lease.RecordAgentChargedAttempt(
                    DateTimeOffset.UtcNow,
                    durableAttemptCount);
            }

            if (repair.Ok && !attemptWasCharged)
            {
                return new SettingsRepairAttempt(
                    false,
                    accountKey,
                    donorKey,
                    "target reported settings-repair success without confirming a durable attempt; refusing to advance recovery state.");
            }

            if (!repair.Ok)
            {
                return new SettingsRepairAttempt(false, accountKey, donorKey, repair.Message);
            }

            _logger.LogWarning(
                "Replaced {AccountKey}'s D2R settings from {DonorKey}: {Message}",
                accountKey,
                donorKey,
                repair.Message);
            lease.RecordRepairApplied(donorKey, DateTimeOffset.UtcNow);
            return new SettingsRepairAttempt(true, accountKey, donorKey, repair.Message, CopyApplied: true);
        }

        return new SettingsRepairAttempt(
            false,
            accountKey,
            DonorAccountKey: null,
            $"no fleet member could export its settings ({string.Join("; ", donorFailures)}).");
    }

    /// <summary>
    /// Takes accounts the roster no longer wants out of the current game: Save and Exit for any
    /// that are actually in it, then drop them from the run's state so the all-joined watch stops
    /// expecting them. Returns the accounts that had to leave a live game, for the monitor.
    /// </summary>
    /// <remarks>
    /// Benched clients are left warm at the lobby rather than quit. A later +1 then only has to
    /// join a game, which takes seconds, instead of cold-starting Battle.net and clicking through
    /// the intro and character select.
    /// </remarks>
    private async Task<string[]> BenchFollowAutoAccountsAsync(
        FollowAutoRoster roster,
        FollowAutoAccountState accountState,
        FollowAutoRunOptions options,
        long followAutoRunId,
        CancellationToken cancellationToken)
    {
        if (roster.Benched.Count == 0)
        {
            return [];
        }

        var benchedSet = roster.BenchedSet;
        var benchedInGame = accountState.Joined
            .Where(benchedSet.Contains)
            .OrderBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Benched accounts that are NOT in the game are dropped from run state immediately. That
        // matters most for recovery-pending ones: nothing is going to drive their recovery once
        // they are off the roster, so leaving them there would hold the all-joined watch forever.
        var benchedOutOfGame = benchedSet
            .Where(accountKey => !benchedInGame.Contains(accountKey, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        accountState.Bench(benchedOutOfGame);

        if (benchedInGame.Length == 0)
        {
            return [];
        }

        var leaveResults = await LeaveAllJoinAutoAsync(
            options.Channel,
            $"bot count lowered to {roster.TargetBotCount}",
            postResult: false,
            metricsEnabled: options.MetricsEnabled,
            onlyAccounts: benchedInGame.ToHashSet(StringComparer.OrdinalIgnoreCase),
            followAutoRunId: followAutoRunId,
            cancellationToken: cancellationToken);

        // Only confirmed leavers are dropped. An account whose Save and Exit failed is still
        // sitting in the leader's game, so it stays in the run's joined set and this runs again on
        // the next cycle - forgetting it here would leave a bot in the game that the run has
        // stopped tracking and nothing would ever take out.
        var left = leaveResults
            .Where(result => result.Ok)
            .Select(result => result.AccountKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        accountState.Bench(left);

        var retrying = new List<string>();
        var abandoned = new List<string>();
        foreach (var failure in leaveResults.Where(result => !result.Ok))
        {
            if (accountState.ShouldRetryBenchLeave(failure.AccountKey))
            {
                retrying.Add($"{failure.AccountKey} ({failure.Message})");
                continue;
            }

            // Out of attempts. Release it so the all-joined watch - which requires the joined set
            // to match the active roster exactly - is not held up by a client that cannot leave.
            accountState.Bench(new HashSet<string>([failure.AccountKey], StringComparer.OrdinalIgnoreCase));
            // Released, but almost certainly still sitting in the leader's game. Public mode has to
            // keep counting it: reading an untracked bot as one more real player would make the
            // fleet yield another slot to it, and then another, emptying the roster a step at a
            // time while the game it is measuring never actually gets any less full.
            accountState.MarkStrandedInGame(failure.AccountKey);
            abandoned.Add($"{failure.AccountKey} ({failure.Message})");
        }

        if (retrying.Count > 0)
        {
            _logger.LogWarning(
                "Benched account(s) could not leave the game; keeping them tracked so the next cycle retries: {Failures}",
                string.Join("; ", retrying));
            await UpdateFollowAutoMonitorAsync(
                $"Bot count lowered to {roster.TargetBotCount}, but {string.Join("; ", retrying)} could not leave "
                    + "the game yet; retrying on the next cycle.");
        }

        if (abandoned.Count > 0)
        {
            _logger.LogWarning(
                "Benched account(s) failed {Attempts} leave attempts and may still be in the game: {Failures}",
                FollowAutoAccountState.MaxBenchLeaveAttempts,
                string.Join("; ", abandoned));
            await UpdateFollowAutoMonitorAsync(
                $"{string.Join("; ", abandoned)} failed {FollowAutoAccountState.MaxBenchLeaveAttempts} leave attempts "
                    + "and may still be in the game. Follow-auto has stopped waiting on it; quit that client manually "
                    + "if it stays.");
        }

        return left.OrderBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<FollowAutoCheckResult> RunFollowAutoCheckEntryAsync(
        FollowAutoRunOptions options,
        KeyValuePair<string, AccountConfig> entry,
        long followAutoRunId,
        CancellationToken cancellationToken)
    {
        var accountKey = entry.Key;
        var account = entry.Value;
        var args = BuildFollowAutoMenuArgs(accountKey, account, options, followAutoRunId);
        CommandResultInfo? readyResult;
        try
        {
            readyResult = await SendReadyIfNotMenuReadyAsync(account, args);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "menu_ready before follow-auto failed for {AccountKey}.", accountKey);
            return new FollowAutoCheckResult(
                accountKey,
                FollowAutoCheckOutcome.CheckFailure,
                $"ready failed before follow-auto check: {FormatExceptionWithAccountStatus(ex, accountKey, account)}",
                FollowWarmupOutcome.Failed);
        }

        if (readyResult is { Ok: true }
            && !string.Equals(readyResult.CommandId, "menu-ready-status-fallback", StringComparison.Ordinal))
        {
            // Only an actual menu_ready command result is new proof. A null (cached already-ready)
            // or status fallback may predate another caller's gamma evidence and must not revoke
            // its donor-export lease.
            _settingsRepairs.RecordRecovered(accountKey);
        }

        if (readyResult?.Ok == false)
        {
            // A ready failure whose status says the client reset its own Settings.json is not a
            // warmup failure at all - relaunching, power-cycling the VM, and restarting the node
            // would each fail in turn, in that order, over many minutes. Replace the file from a
            // healthy fleet member and give ready one more attempt before recording a strike.
            var repairStatusJson = readyResult.Data?.GetRawText();
            var repairTarget = _registry.GetAgent(account.AgentId);
            var repair = await TryRepairSettingsFromFleetAsync(
                accountKey,
                account,
                repairStatusJson,
                DateTimeOffset.UtcNow,
                repairTarget is { Connected: true } ? repairTarget.ConnectedAt : null,
                repairTarget?.StatusReceivedAt,
                SettingsRepairPolicy.RepairEvidenceToken(repairStatusJson),
                cancellationToken);
            if (repair is { Ok: true })
            {
                var repairDescription = repair.CopyApplied
                    ? $"replaced corrupt D2R settings from {repair.DonorAccountKey}"
                    : repair.Message;
                try
                {
                    readyResult = await SendReadyIfNotMenuReadyAsync(account, args);
                }
                catch (Exception ex)
                {
                    return new FollowAutoCheckResult(
                        accountKey,
                        FollowAutoCheckOutcome.CheckFailure,
                        $"{repairDescription}, but the retried ready failed: "
                            + FormatExceptionWithAccountStatus(ex, accountKey, account),
                        FollowWarmupOutcome.Failed);
                }

                if (readyResult?.Ok == false)
                {
                    var retriedStatusJson = readyResult.Data?.GetRawText();
                    if (SettingsRepairPolicy.NeedsDonorSettings(retriedStatusJson))
                    {
                        _settingsRepairs.ObserveCurrentStatus(
                            accountKey,
                            retriedStatusJson,
                            DateTimeOffset.UtcNow);
                    }

                    return new FollowAutoCheckResult(
                        accountKey,
                        FollowAutoCheckOutcome.CheckFailure,
                        $"{repairDescription}, but the retried ready failed: "
                            + readyResult.Message,
                        FollowWarmupOutcome.Failed);
                }

                _logger.LogWarning(
                    "{AccountKey} completed settings recovery: {Description}.",
                    accountKey,
                    repairDescription);
                _settingsRepairs.RecordRecovered(accountKey);
            }
            else if (repair is not null)
            {
                return new FollowAutoCheckResult(
                    accountKey,
                    FollowAutoCheckOutcome.CheckFailure,
                    $"D2R reset this client's Settings.json (first-run gamma calibration screen) and it could not be "
                        + $"repaired from the fleet: {repair.Message} Original ready failure: {readyResult.Message}",
                    FollowWarmupOutcome.Failed);
            }
            else
            {
                return new FollowAutoCheckResult(
                    accountKey,
                    FollowAutoCheckOutcome.CheckFailure,
                    $"ready failed before follow-auto check: {readyResult.Message}",
                    FollowWarmupOutcome.Failed);
            }
        }

        CommandResultInfo result;
        try
        {
            result = await _registry.SendCommandAsync(
                account.AgentId, "menu_follow_auto_check", args, TimeSpan.FromSeconds(210), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "menu_follow_auto_check failed for {AccountKey}.", accountKey);
            return new FollowAutoCheckResult(accountKey, FollowAutoCheckOutcome.CheckFailure, ex.Message);
        }

        if (!result.Ok)
        {
            return new FollowAutoCheckResult(accountKey, FollowAutoCheckOutcome.CheckFailure, result.Message);
        }

        if (result.Data is not { } data || !TryGetBoolean(data, "bound", out var bound))
        {
            return new FollowAutoCheckResult(
                accountKey,
                FollowAutoCheckOutcome.CheckFailure,
                $"follow check returned no bound flag ({result.Message})");
        }

        if (!bound)
        {
            return new FollowAutoCheckResult(accountKey, FollowAutoCheckOutcome.Unbound, result.Message);
        }

        if (TryGetBoolean(data, "d2rReady", out var d2rReady) && !d2rReady)
        {
            return new FollowAutoCheckResult(
                accountKey,
                FollowAutoCheckOutcome.Waiting,
                result.Message,
                LocalStall: TryGetBoolean(data, "localStall", out var notReadyStall) && notReadyStall);
        }

        if (TryGetBoolean(data, "joined", out var didJoin) && didJoin)
        {
            return new FollowAutoCheckResult(accountKey, FollowAutoCheckOutcome.Joined, result.Message);
        }

        if (TryGetBoolean(data, "joinBlocked", out var joinBlocked) && joinBlocked
            && TryGetString(data, "joinBlockReason", out var joinBlockReason)
            && string.Equals(joinBlockReason, "gameIsFull", StringComparison.OrdinalIgnoreCase))
        {
            return new FollowAutoCheckResult(accountKey, FollowAutoCheckOutcome.GameFull, result.Message);
        }

        return new FollowAutoCheckResult(
            accountKey,
            FollowAutoCheckOutcome.Waiting,
            result.Message,
            LocalStall: TryGetBoolean(data, "localStall", out var localStall) && localStall);
    }

    // issue #24: "the join-auto feature should still make a game monitor like the other one
    // with the bot / player count / game name etc." Deliberately a separate message/state from
    // _activeSessionMessage (create-game-all/join-all's own monitor) rather than sharing it - one
    // persistent message edited for the entire join-auto run (confirmed with the user), not a
    // fresh one per game the way the other one is per-invocation, since a farming session can
    // advance through many numbered games over hours.
    private async Task StartJoinAutoMonitorAsync(IMessageChannel channel, bool metricsEnabled)
    {
        _joinAutoMonitorGameName = null;
        _joinAutoMonitorJoined = 0;
        _joinAutoMonitorTotal = 0;
        _joinAutoCyclesCompleted = 0;
        _joinAutoStartedUtc = DateTimeOffset.UtcNow;
        _joinAutoMetricsEnabled = metricsEnabled;
        try
        {
            _joinAutoMonitorMessage = await channel.SendMessageAsync(
                AppendMetrics(_joinAutoMetricsEnabled, await FormatJoinAutoMonitorMessageAsync("Starting...")));
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
            await _joinAutoMonitorMessage.ModifyAsync(properties => properties.Content = AppendMetrics(_joinAutoMetricsEnabled, content));
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
            await _joinAutoMonitorMessage.ModifyAsync(properties => properties.Content = AppendMetrics(_joinAutoMetricsEnabled, content));
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
            var result = await _registry.SendCommandAsync(
                entries[0].Value.AgentId,
                "status",
                args: null,
                TimeSpan.FromSeconds(6));
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

    private async Task<JoinResult[]> LeaveAllJoinAutoAsync(
        IMessageChannel channel,
        string label,
        bool postResult = true,
        bool metricsEnabled = true,
        IReadOnlySet<string>? onlyAccounts = null,
        long? followAutoRunId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (entries, _) = GetAccountEntriesByConnectivity();
        var unavailableResults = Array.Empty<JoinResult>();
        if (onlyAccounts is not null)
        {
            // Follow-auto's mid-join stale-game abort leaves only the accounts actually IN
            // the abandoned game; sending save-exit to a bot still at the lobby would just
            // burn its command gate on a guaranteed failure. Still return one result for every
            // requested key: an offline target is an unconfirmed leave, never silent success.
            entries = entries.Where(entry => onlyAccounts.Contains(entry.Key)).ToArray();
            var onlineKeys = entries
                .Select(entry => entry.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            unavailableResults = onlyAccounts
                .Where(accountKey => !onlineKeys.Contains(accountKey))
                .OrderBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase)
                .Select(accountKey => new JoinResult(
                    accountKey,
                    false,
                    "account is offline; leave could not be confirmed"))
                .ToArray();
        }

        if (entries.Length == 0 && unavailableResults.Length == 0)
        {
            if (postResult)
            {
                await SendJoinAutoMessageAsync(channel, $"{label}: no online accounts to leave with.", metricsEnabled);
            }

            return [];
        }

        var onlineLeaveResults = await Task.WhenAll(entries.Select(async entry =>
        {
            try
            {
                var result = await _registry.SendCommandAsync(
                    entry.Value.AgentId,
                    "menu_save_exit",
                    BuildSaveExitArgs(entry.Key, entry.Value, followAutoRunId),
                    TimeSpan.FromSeconds(210),
                    cancellationToken);
                return new JoinResult(entry.Key, result.Ok, result.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "join-auto leave failed for {AccountKey}.", entry.Key);
                return new JoinResult(entry.Key, false, FormatExceptionWithAccountStatus(ex, entry.Key, entry.Value));
            }
        }));
        var leaveResults = onlineLeaveResults
            .Concat(unavailableResults)
            .ToArray();

        var failed = leaveResults.Where(result => !result.Ok).ToArray();
        if (postResult)
        {
            await SendJoinAutoMessageAsync(channel, failed.Length == 0
                ? $"{label}: all accounts left."
                : $"{label}: leave failed for " + string.Join("; ", failed.Select(result => $"{result.AccountKey}: {result.Message}")),
                metricsEnabled);
        }

        return leaveResults;
    }

    // Polls independently of RunPartyMemberMonitorAsync's own tick - this needs to notice a drop
    // promptly while actively farming, not just whenever the next heartbeat happens to land.
    // The delay is dynamic: long/unknown game lengths stay gentle, while fast farming sessions
    // check more often so a one-minute game does not wait on a fifteen-second tail.
    // Only returns once a drop is actually detected; the only other way out is cancellation,
    // which throws OperationCanceledException through Task.Delay and is handled by the caller.
    private async Task WaitForPlayerCountDropAsync(
        int? baseline,
        Func<TimeSpan> pollDelay,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(pollDelay(), cancellationToken);
            var current = await TryFetchFirstOnlineAccountPlayerCountAsync();
            if (current is { } count && baseline is { } known && count < known)
            {
                return;
            }

            // Track the highest count observed, not just the first seed - the seed can race
            // players still joining/partying (or a cached pre-join count) and a low baseline
            // would make the real departure invisible. See FollowAutoPulsePolicy.RaiseCountBaseline.
            baseline = FollowAutoPulsePolicy.RaiseCountBaseline(baseline, current);
        }
    }

    // Issue #25 follow-up (bind-in-game): follow-auto's in-game wait. Pulses round-robin
    // across every online account - the rotated delay divides the base poll interval by the
    // vantage count, so each VM is still probed at the original cadence while the fleet as a
    // whole notices a change that much sooner, and a flagged leader absence gets confirmed by
    // the NEXT vantage in the rotation rather than the same screen twice.
    // FollowAutoPulsePolicy turns each sample into stay/rebaseline/confirm/leave - see that
    // class for the decision table and why count drops stop mattering while the bound leader
    // is verified present. Returns either a whole-game end reason or the one isolated account
    // that should be removed from the joined set and resynchronized.
    // Join-auto keeps using WaitForPlayerCountDropAsync above: its games are the accounts' own
    // private games, where "someone left" really does mean the game is over.
    private async Task<FollowAutoGameWatchResult> WaitForFollowAutoGameEndAsync(
        int? baseline,
        Func<TimeSpan> pollDelay,
        FollowAutoRosterWatchSnapshot rosterSnapshot,
        FollowAutoAccountState accountState,
        IReadOnlySet<string> isolatedAccountsResyncedThisGame,
        IDictionary<string, int> outOfGameResyncsThisGame,
        CancellationToken cancellationToken)
    {
        var singleVantageMissStreak = 0;
        var isolatedMissStreaks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var outOfGameStreaks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cappedOutOfGameReports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rotation = 0;
        var warnedCountDropWhileLeaderVisible = false;
        var lastLeaderVisibleReportUtc = DateTimeOffset.UtcNow;
        var nextDelay = GetFollowHeartbeat(pollDelay);
        while (true)
        {
            await Task.Delay(nextDelay, cancellationToken);
            var (connectedEntries, _) = GetAccountEntriesByConnectivity();
            var onlineAccountKeys = connectedEntries
                .Select(entry => entry.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (rosterSnapshot.RequiresReconciliation(
                    _followAutoTarget.TargetBotCount,
                    onlineAccountKeys))
            {
                return new FollowAutoGameWatchResult(
                    "the requested bot roster or connected fleet changed",
                    ReconcileRoster: true);
            }

            var disconnectedAccounts = accountState.BeginRecoveryForOfflineJoined(onlineAccountKeys);
            if (disconnectedAccounts.FirstOrDefault() is { } disconnectedAccountKey)
            {
                return new FollowAutoGameWatchResult(
                    $"{disconnectedAccountKey} disconnected while it was marked joined.",
                    disconnectedAccountKey,
                    AttemptTargetedLeave: false);
            }

            var sample = await TryFetchFollowPulseAsync(rotation++, _followAutoLockedNametag, accountState.Joined);
            await TryLockNametagFromSampleAsync(sample);
            _followAutoLivePlayers.Observe(sample.PlayerCount, sample.PlayerCountFresh);
            if (await TryApplyPublicPartyTargetAsync(sample, CountFleetClientsInGame(accountState)))
            {
                // Yield now rather than letting the next iteration's snapshot comparison catch it:
                // the run loop is what actually benches or promotes a client, and public mode's
                // whole promise is that the slot is given back promptly.
                return new FollowAutoGameWatchResult(
                    "public mode changed the bot count to hold a slot open",
                    ReconcileRoster: true);
            }

            // Before interpreting the leader signal, check whether this vantage is even in a
            // game. A bot dropped back to the lobby after its join was already confirmed (a
            // post-join "Connection Interrupted") reports nothing but null counts and null
            // nametag reads, which classify as Wait - so it used to sit out the whole game while
            // the monitor still counted it in. Nothing here can be confirmed by another VM: only
            // this client can see its own screen, so the guard is consecutive reads instead.
            if (sample.AccountKey is { } pulsedAccountKey)
            {
                outOfGameStreaks.TryGetValue(pulsedAccountKey, out var priorOutOfGameStreak);
                var outOfGameStreak = FollowAutoPulsePolicy.NextOutOfGameStreak(
                    priorOutOfGameStreak, sample.InGame);
                outOfGameStreaks[pulsedAccountKey] = outOfGameStreak;
                outOfGameResyncsThisGame.TryGetValue(pulsedAccountKey, out var priorResyncs);
                if (FollowAutoPulsePolicy.ShouldResyncOutOfGameVantage(outOfGameStreak, priorResyncs))
                {
                    outOfGameResyncsThisGame[pulsedAccountKey] = priorResyncs + 1;
                    return new FollowAutoGameWatchResult(
                        $"{pulsedAccountKey} is at the menus, not in the game it was counted in "
                            + $"({outOfGameStreak} consecutive checks; it was most likely dropped by a connection interruption after its join was confirmed).",
                        pulsedAccountKey,
                        // It is already out of the game - a save-exit would only burn its command
                        // gate on a guaranteed failure. The normal join path takes it from here.
                        AttemptTargetedLeave: false);
                }

                if (outOfGameStreak >= FollowAutoPulsePolicy.OutOfGameVantageResyncSamples
                    && cappedOutOfGameReports.Add(pulsedAccountKey))
                {
                    await UpdateFollowAutoMonitorAsync(
                        $"Game #{_followAutoGameNumber}: {pulsedAccountKey} keeps reading as out of the game but has already used its "
                            + $"{FollowAutoPulsePolicy.MaxOutOfGameResyncsPerGame} rejoin attempts this game, so it stays as-is. "
                            + "If it is visibly in the game, its screen is being misclassified - check the VM's resolution (1366x768) and reference images.");
                }
            }

            // Only the session-locked nametag can drive the leave decision. Before a lock
            // exists (first game, nothing spotted yet), presence reads null and Classify falls
            // back to count-drop semantics - none of the bound alt nametags may trigger a leave
            // until one of them has actually been SEEN in this run.
            var (lockedPresent, _, _) = GetLockedNametagPresence(sample);
            switch (FollowAutoPulsePolicy.Classify(sample.LeaderBound, lockedPresent, sample.PlayerCount, baseline))
            {
                case FollowAutoPulseAction.CountDropLeave:
                    // A missing nametag never leaves on one screen's word - it forces an
                    // independent second opinion first. A count drop did, and that asymmetry only
                    // held while every vantage sampled at roughly the same instant. Pulses
                    // round-robin across vantages, the baseline is fleet-wide, and a worker-relayed
                    // agent answers through an extra hop (master -> worker -> agent), so its count
                    // lags the locally-connected ones. A baseline raised by a fast vantage then
                    // reads as a drop on the next slow one, the fleet leaves, and follow-auto
                    // immediately rejoins the same game because the leader never went anywhere.
                    // Same rule as the nametag path: one screen is not enough to leave on.
                    var countConfirm = await ConfirmPlayerCountDropFromAnotherVantageAsync(
                        sample.AccountKey, baseline, accountState.Joined);
                    if (countConfirm.Confirmed)
                    {
                        return new FollowAutoGameWatchResult("player count dropped");
                    }

                    baseline = FollowAutoPulsePolicy.RaiseCountBaseline(
                        baseline, countConfirm.HighestSeen ?? sample.PlayerCount);
                    if (DateTimeOffset.UtcNow - lastLeaderVisibleReportUtc >= TimeSpan.FromSeconds(30))
                    {
                        lastLeaderVisibleReportUtc = DateTimeOffset.UtcNow;
                        await UpdateFollowAutoMonitorAsync(
                            $"Game #{_followAutoGameNumber}: {sample.AccountKey ?? "a vantage"} saw the player count drop, "
                                + $"but {countConfirm.Detail} - staying. Bind your character with `/d2r follow bind-in-game` "
                                + "so leaving is driven by your nametag instead of the player count.");
                    }

                    break;
                case FollowAutoPulseAction.RebaselineAndWait:
                    if (sample.AccountKey is { } visibleAccountKey)
                    {
                        isolatedMissStreaks.Remove(visibleAccountKey);
                    }

                    // A count drop with the locked nametag still visible is legitimate in a
                    // public game (a stranger left), but when the operator themselves left and
                    // this branch keeps swallowing it, the locked template is matching someone
                    // who stayed - a bot's name captured at the wrong bind position, or a name
                    // visually ambiguous with the leader's. Surface the tell once per game so
                    // a wrong lock self-diagnoses instead of reading as "won't follow".
                    if (!warnedCountDropWhileLeaderVisible
                        && sample.PlayerCount is { } seenCount
                        && baseline is { } knownBaseline
                        && seenCount < knownBaseline)
                    {
                        warnedCountDropWhileLeaderVisible = true;
                        await UpdateFollowAutoMonitorAsync(
                            $"Game #{_followAutoGameNumber}: player count dropped {knownBaseline}->{seenCount} but the locked nametag is still visible, so staying. If it was YOU who left, the bound nametag is matching someone else's name - rebind with `/d2r follow bind-in-game`.{FormatBoundLeaderWatchDetail(sample)}");
                    }

                    baseline = sample.PlayerCount ?? baseline;
                    singleVantageMissStreak = 0;
                    if (DateTimeOffset.UtcNow - lastLeaderVisibleReportUtc >= TimeSpan.FromSeconds(30))
                    {
                        lastLeaderVisibleReportUtc = DateTimeOffset.UtcNow;
                        await UpdateFollowAutoMonitorAsync(
                            $"Game #{_followAutoGameNumber}: watching for the bound leader to leave.{FormatBoundLeaderWatchDetail(sample)}");
                    }

                    break;
                case FollowAutoPulseAction.LeaderMissingHere:
                    var (online, _) = GetAccountEntriesByConnectivity();
                    // Count joined vantages only: a game-full-parked account is online but sits
                    // at the lobby, so it can neither confirm nor deny the leader's absence.
                    var joinedOnlineCount = online.Count(entry => accountState.Joined.Contains(entry.Key));
                    if (joinedOnlineCount > 1)
                    {
                        singleVantageMissStreak = 0;
                        var flaggerName = sample.AccountKey ?? "a VM";
                        var (agreed, confirmer, confirmDetail) = await ConfirmLeaderGoneFromAnotherVantageAsync(sample.AccountKey, accountState.Joined);
                        if (agreed == true)
                        {
                            return new FollowAutoGameWatchResult(
                                sample.AccountKey is { } flagger && confirmer is { } confirmedBy
                                    ? $"the bound leader left the game ({flagger} flagged it, {confirmedBy} confirmed)"
                                    : "the bound leader left the game");
                        }

                        if (agreed == false)
                        {
                            // A different VM still sees the leader (and no VM verified absence).
                            // One such split read can be transient; repeated split reads from the
                            // same account mean that account is probably in another game while
                            // the host's joined set still counts it as healthy.
                            baseline = sample.PlayerCount ?? baseline;
                            var isolatedMissStreak = 0;
                            var alreadyResyncedThisGame = false;
                            if (sample.AccountKey is { } isolatedAccountKey)
                            {
                                isolatedMissStreaks.TryGetValue(isolatedAccountKey, out var priorMissStreak);
                                isolatedMissStreak = FollowAutoPulsePolicy.NextIsolatedVantageMissStreak(
                                    priorMissStreak,
                                    lockedPresent,
                                    agreed);
                                isolatedMissStreaks[isolatedAccountKey] = isolatedMissStreak;
                                alreadyResyncedThisGame = isolatedAccountsResyncedThisGame.Contains(isolatedAccountKey);
                                if (FollowAutoPulsePolicy.ShouldResyncIsolatedVantage(
                                        isolatedMissStreak,
                                        alreadyResyncedThisGame))
                                {
                                    return new FollowAutoGameWatchResult(
                                        $"{isolatedAccountKey} missed the bound leader on {isolatedMissStreak} independently-confirmed checks while the other bots still saw it.{confirmDetail}",
                                        isolatedAccountKey);
                                }
                            }

                            var isolationDetail = sample.AccountKey is not null
                                ? alreadyResyncedThisGame
                                    ? " It already had one targeted resync this game, so it will not be cycled repeatedly."
                                    : $" Confirmed-isolation miss {isolatedMissStreak}/{FollowAutoPulsePolicy.IsolatedVantageResyncSamples}."
                                : "";
                            await UpdateFollowAutoMonitorAsync(
                                $"Game #{_followAutoGameNumber}: {flaggerName} lost sight of the bound leader, but {confirmer} still sees it.{isolationDetail} {confirmDetail}{FormatBoundLeaderWatchDetail(sample)}");
                        }
                        else
                        {
                            if (sample.AccountKey is { } inconclusiveAccountKey)
                            {
                                isolatedMissStreaks.Remove(inconclusiveAccountKey);
                            }

                            // agreed == null: no other vantage could get a clean read this
                            // instant. Don't leave on one screen's word - the next heartbeat
                            // pulse tries again - but say WHY on the monitor: a fleet that can
                            // never confirm (diverged lists, old agent builds) used to cycle
                            // here invisibly for entire sessions.
                            await UpdateFollowAutoMonitorAsync(
                                $"Game #{_followAutoGameNumber}: {flaggerName} lost sight of the bound leader and no other VM could verify it either way; staying until a vantage gets a clean read. {confirmDetail}{FormatBoundLeaderWatchDetail(sample)}");
                        }
                    }
                    else
                    {
                        // Only one VM online: no independent screen to confirm with, so require
                        // the lone vantage to miss the leader on two back-to-back scans.
                        if (++singleVantageMissStreak >= FollowAutoPulsePolicy.LeaderGoneConfirmationSamples)
                        {
                            return new FollowAutoGameWatchResult("the bound leader left the game");
                        }

                        await UpdateFollowAutoMonitorAsync(
                            $"Game #{_followAutoGameNumber}: bound leader not visible; re-checking before leaving.{FormatBoundLeaderWatchDetail(sample)}");
                        nextDelay = TimeSpan.FromSeconds(FollowAutoPulsePolicy.SingleVantageRescanSeconds);
                        continue;
                    }

                    break;
                default:
                    if (sample.AccountKey is { } unsampledAccountKey)
                    {
                        isolatedMissStreaks.Remove(unsampledAccountKey);
                    }

                    baseline = FollowAutoPulsePolicy.RaiseCountBaseline(baseline, sample.PlayerCount);
                    break;
            }

            nextDelay = GetFollowHeartbeat(pollDelay);
        }
    }

    private TimeSpan GetFollowHeartbeat(Func<TimeSpan> pollDelay)
    {
        var (online, _) = GetAccountEntriesByConnectivity();
        return FollowAutoPulsePolicy.GetHeartbeat(pollDelay(), online.Length);
    }

    // Forces an immediate leader check on EVERY online account other than the one that just
    // flagged the leader missing. All answers are collected (not just the first informative
    // one) and combined via FollowAutoPulsePolicy.CombineLeaderConfirmations: any independent
    // "gone" agrees with the flagger and wins - stopping at the first answer let a single VM
    // whose list diverged (answers null forever) or whose scene cross-matches the template
    // (answers present forever) sit in front of the queue and starve every leave, which is
    // exactly how watch-follow-auto-20260717-115637.log deadlocked for a whole session.
    // Returns (true, key) leave now; (false, key) still visible somewhere and nobody verified
    // absence -> transient; (null, null) nobody could check. Detail names every account's
    // answer for the monitor, so a stuck confirmation is readable instead of a silent cycle.
    // Side effect: an account whose scan works but whose stored list is missing the locked
    // nametag (offline during that bind, or an older overwrite-style agent) gets the locked
    // fingerprint re-sent, so list divergence heals mid-run instead of muting that VM forever.
    // The count-drop equivalent of ConfirmLeaderGoneFromAnotherVantageAsync. Asks every OTHER
    // joined vantage what it currently sees: if any of them still reads the baseline count, the
    // game has not emptied and the flagging vantage was simply sampled mid-join or through a
    // slower path. Only a fleet that unanimously sees fewer players leaves.
    //
    // Scoped to joined accounts for the same reason the nametag confirmation is: a client sitting
    // at the lobby can serve a cached count from the previous game, which is a fake vote either
    // way. An inconclusive answer (nobody could check) deliberately does NOT confirm - staying in
    // a game one cycle too long costs a few seconds, leaving wrongly costs a leave/rejoin churn.
    private async Task<(bool Confirmed, int? HighestSeen, string Detail)>
        ConfirmPlayerCountDropFromAnotherVantageAsync(
            string? flaggerKey,
            int? baseline,
            IReadOnlySet<string>? onlyAccounts)
    {
        var (online, _) = GetAccountEntriesByConnectivity();
        if (onlyAccounts is not null)
        {
            online = online.Where(entry => onlyAccounts.Contains(entry.Key)).ToArray();
        }

        int? highestSeen = null;
        var checkedAny = false;
        var details = new List<string>();
        foreach (var (accountKey, account) in online)
        {
            if (string.Equals(accountKey, flaggerKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sample = await TryFetchFollowPulseForAsync(accountKey, account, _followAutoLockedNametag);
            _followAutoLivePlayers.Observe(sample.PlayerCount, sample.PlayerCountFresh);
            if (sample.PlayerCount is not { } count)
            {
                continue;
            }

            checkedAny = true;
            highestSeen = FollowAutoPulsePolicy.RaiseCountBaseline(highestSeen, count);
            details.Add($"{accountKey}: {count}");
            if (baseline is { } known && count >= known)
            {
                return (false, highestSeen, $"{accountKey} still sees {count}");
            }
        }

        return checkedAny
            ? (true, highestSeen, $"every other vantage agrees ({string.Join(", ", details)})")
            : (false, highestSeen, "no other vantage could check this instant");
    }

    private async Task<(bool? Agreed, string? ByAccount, string Detail)> ConfirmLeaderGoneFromAnotherVantageAsync(
        string? flaggerKey,
        IReadOnlySet<string>? onlyAccounts = null)
    {
        var (online, _) = GetAccountEntriesByConnectivity();
        if (onlyAccounts is not null)
        {
            // Same vantage-scoping rationale as TryFetchFollowPulseAsync: only accounts in the
            // watched game can meaningfully confirm or deny the leader's absence.
            online = online.Where(entry => onlyAccounts.Contains(entry.Key)).ToArray();
        }

        var answers = new List<bool?>();
        string? goneBy = null;
        string? presentBy = null;
        var details = new List<string>();
        foreach (var (accountKey, account) in online)
        {
            if (string.Equals(accountKey, flaggerKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The confirming vantage must answer about the SAME nametag the flagger missed -
            // the session-locked one - not about any other bound alt that might coincidentally
            // be visible.
            var sample = await TryFetchFollowPulseForAsync(accountKey, account, _followAutoLockedNametag);
            _followAutoLivePlayers.Observe(sample.PlayerCount, sample.PlayerCountFresh);
            var lockedEntry = _followAutoLockedNametag is { } locked
                ? sample.Matches.FirstOrDefault(match => string.Equals(match.Fingerprint, locked, StringComparison.Ordinal))
                : null;
            var present = sample.LeaderBound ? lockedEntry?.Present : null;
            answers.Add(present);

            if (present == false)
            {
                goneBy ??= accountKey;
                details.Add($"{accountKey}: gone (score {FormatScore(lockedEntry?.Score)})");
            }
            else if (present == true)
            {
                presentBy ??= accountKey;
                var slotText = lockedEntry?.Slot is { } slot ? $" slot {slot}" : "";
                details.Add($"{accountKey}: still visible{slotText} (score {FormatScore(lockedEntry?.Score)})");
            }
            else if (lockedEntry is not null)
            {
                details.Add($"{accountKey}: couldn't score it this instant");
            }
            else if (sample.Matches.Count > 0 && _followAutoLockedNametag is { } missingNametag)
            {
                // The scan works but this agent's stored list lacks the locked nametag - it
                // can never confirm anything until the list is repaired, so repair it now.
                details.Add($"{accountKey}: locked nametag missing from its bound list, re-sent");
                try
                {
                    await _registry.SendCommandAsync(
                        account.AgentId,
                        "follow_set_leader_template",
                        new { fingerprint = missingNametag, append = true },
                        TimeSpan.FromSeconds(15));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Locked nametag re-send failed for {AccountKey}.", accountKey);
                }
            }
            else
            {
                details.Add($"{accountKey}: no nametag data (old agent build, or the sample failed)");
            }
        }

        var combined = FollowAutoPulsePolicy.CombineLeaderConfirmations(answers);
        var detail = details.Count > 0 ? $"Confirm answers - {string.Join("; ", details)}." : "";
        return combined switch
        {
            false => (true, goneBy, detail),
            true => (false, presentBy, detail),
            _ => (null, null, detail)
        };
    }

    private static string FormatScore(double? score)
    {
        return score is { } value ? value.ToString("0.000", CultureInfo.InvariantCulture) : "n/a";
    }

    // Multi-alt bind-in-game: resolves which bound nametag this run is following. The first
    // pulse that verifiably sees one locks it in for the whole run - the operator's alt for
    // the session - and later pulses only ever consult that entry. Highest score wins when a
    // pulse somehow sees several (prefix-squatting names, multiboxing); bind order breaks ties.
    private async Task TryLockNametagFromSampleAsync(FollowPulseSample sample)
    {
        if (_followAutoLockedNametag is not null || sample.Matches.Count == 0)
        {
            return;
        }

        var signals = sample.Matches
            .Select(match => new FollowAutoPulsePolicy.LeaderNametagSignal(match.Present, match.Score ?? 0.0))
            .ToArray();
        if (FollowAutoPulsePolicy.PickNametagLockIndex(signals) is not { } index)
        {
            return;
        }

        var match = sample.Matches[index];
        _followAutoLockedNametag = match.Fingerprint;
        _followAutoLockedNametagOrdinal = index + 1;

        var seenFrom = string.IsNullOrWhiteSpace(sample.AccountKey) ? "" : $" from {sample.AccountKey}";
        var slotText = match.Slot is { } slot ? $" slot {slot}" : "";
        var scoreText = match.Score is { } score
            ? $", score {score.ToString("0.000", CultureInfo.InvariantCulture)}"
            : "";
        // Math.Max: the mid-join probe can engage the lock while the FIRST game is still
        // forming, before _followAutoGameNumber has ever advanced past 0.
        await UpdateFollowAutoMonitorAsync(
            $"Game #{Math.Max(_followAutoGameNumber, 1)}: locked onto bound nametag #{index + 1} of {sample.Matches.Count} (seen{seenFrom}{slotText}{scoreText}). Following it for the rest of this run.");
    }

    private (bool? Present, int? Slot, double? Score) GetLockedNametagPresence(FollowPulseSample sample)
    {
        if (_followAutoLockedNametag is null)
        {
            return (null, null, null);
        }

        foreach (var match in sample.Matches)
        {
            if (string.Equals(match.Fingerprint, _followAutoLockedNametag, StringComparison.Ordinal))
            {
                return (match.Present, match.Slot, match.Score);
            }
        }

        // This agent's stored list doesn't contain the locked nametag (it was offline during
        // the bind): no information, never evidence of absence.
        return (null, null, null);
    }

    private TimeSpan GetJoinAutoPlayerCountDropPollDelay()
    {
        return PlayerCountDropPollPolicy.GetDelay(
            _joinAutoStartedUtc,
            _joinAutoCyclesCompleted + 1,
            DateTimeOffset.UtcNow);
    }

    private TimeSpan GetFollowAutoPlayerCountDropPollDelay()
    {
        return PlayerCountDropPollPolicy.GetDelay(
            _followAutoStartedUtc,
            _followAutoGameNumber,
            DateTimeOffset.UtcNow);
    }

    private async Task<int?> TryFetchFirstOnlineAccountPlayerCountAsync()
    {
        return (await TryFetchFollowPulseAsync(rotation: 0)).PlayerCount;
    }

    // One bound nametag's reading in a pulse, keyed by the serialized fingerprint itself so
    // the host can lock onto a nametag by content - agents whose stored lists diverged
    // (offline during a bind) can never be asked about the wrong list index.
    private sealed record FollowLeaderMatch(string Fingerprint, bool? Present, int? Slot, double? Score);

    // InGame is the sampling client's own screen, not the leader's: true/false only when the
    // agent verifiably classified an in-game or a menu screen, null whenever it could not tell
    // (load screen, degraded capture) or the agent predates the field. See
    // FollowAutoPulsePolicy.NextOutOfGameStreak for what the watch does with a false.
    private sealed record FollowPulseSample(
        int? PlayerCount,
        bool LeaderBound,
        bool? LeaderPresent,
        int? LeaderSlot,
        double? LeaderScore,
        string? AccountKey,
        IReadOnlyList<FollowLeaderMatch> Matches,
        bool? InGame = null,
        bool PlayerCountFresh = false);

    private static string FormatParkedGameFullNote(FollowAutoAccountState accountState)
    {
        var parked = accountState.ParkedGameFull
            .OrderBy(accountKey => accountKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return $"{string.Join(", ", parked)} {(parked.Length == 1 ? "is" : "are")} parked - the game reported full "
            + $"{FollowAutoAccountState.MaxGameFullAttempts} times; rejoining at the fleet's next game";
    }

    private string FormatBoundLeaderWatchDetail(FollowPulseSample sample)
    {
        if (!sample.LeaderBound)
        {
            return "";
        }

        var account = string.IsNullOrWhiteSpace(sample.AccountKey)
            ? ""
            : $" on {sample.AccountKey}";

        if (_followAutoLockedNametag is null)
        {
            return sample.Matches.Count > 0
                ? $" {sample.Matches.Count} bound nametag(s); waiting to spot one before locking on{account}."
                : $" Bound nametag check unavailable{account}.";
        }

        var (present, slot, score) = GetLockedNametagPresence(sample);
        var ordinal = _followAutoLockedNametagOrdinal is { } lockedOrdinal ? $" #{lockedOrdinal}" : "";
        var slotText = slot is { } presentSlot ? $" slot {presentSlot}" : "";
        var scoreText = score is { } matchScore
            ? $" score {matchScore.ToString("0.000", CultureInfo.InvariantCulture)}"
            : "";

        return present switch
        {
            true => $" Locked nametag{ordinal} currently visible{account}{slotText}{scoreText}.",
            false => $" Locked nametag{ordinal} not visible{account}{scoreText}.",
            _ => $" Locked nametag{ordinal} check unavailable{account}."
        };
    }

    // Samples the (rotation % online-count)th online account, so consecutive pulses take turns
    // across the fleet. LeaderPresent stays null whenever it wasn't actually verified (no fresh
    // sample, no leader bound, or the agent couldn't check) - the cached-status fallback can
    // only ever supply a count, and pretending it said anything about the leader would turn
    // "couldn't check" into a leave trigger.
    // Probes one JOINED vantage (rotating over the joined set) for the locked leader nametag
    // while the fleet is still in the join phase. Returns the locked nametag's presence from
    // that vantage and, when it read verified-absent, the cross-vantage confirmation verdict.
    // Mid-join sightings must engage the session lock too (TryLockNametagFromSampleAsync): a
    // run whose first game never reaches all-joined - exactly the wedged-bot case this probe
    // exists for - previously never locked at all, which would leave every probe blind
    // (no lock -> presence always null -> never evidence of absence).
    private async Task<MidJoinLeaderProbe> ProbeMidJoinLeaderPresenceAsync(
        IReadOnlySet<string> joined,
        int rotation)
    {
        var (online, _) = GetAccountEntriesByConnectivity();
        var joinedEntries = online.Where(entry => joined.Contains(entry.Key)).ToArray();
        if (joinedEntries.Length == 0)
        {
            return new MidJoinLeaderProbe(null, null, "", null);
        }

        var (accountKey, account) = joinedEntries[rotation % joinedEntries.Length];
        var sample = await TryFetchFollowPulseForAsync(accountKey, account, _followAutoLockedNametag);
        _followAutoLivePlayers.Observe(sample.PlayerCount, sample.PlayerCountFresh);
        await TryLockNametagFromSampleAsync(sample);
        var (lockedPresent, _, _) = GetLockedNametagPresence(sample);
        if (lockedPresent != false)
        {
            return new MidJoinLeaderProbe(lockedPresent, null, "", sample);
        }

        var flagger = sample.AccountKey ?? accountKey;
        var (agreed, confirmer, confirmDetail) = await ConfirmLeaderGoneFromAnotherVantageAsync(flagger, joined);
        var detail = agreed == true && confirmer is { } confirmedBy
            ? $" ({flagger} flagged it, {confirmedBy} confirmed)"
            : $" ({flagger} flagged it; {confirmDetail})";
        return new MidJoinLeaderProbe(false, agreed, detail, sample);
    }

    // onlyAccounts scopes the vantage rotation to accounts actually IN the watched game. A
    // game-full-parked (or otherwise pending) client sitting at the lobby is a poisonous
    // vantage: its fresh sample sees no party bar, and the status fallback reports a STALE
    // lastPartyMemberCount from a previous game - either could fake a count-drop leave or a
    // "leader gone" flag against a healthy game.
    private async Task<FollowPulseSample> TryFetchFollowPulseAsync(
        int rotation,
        string? activeFingerprint = null,
        IReadOnlySet<string>? onlyAccounts = null)
    {
        var (entries, _) = GetAccountEntriesByConnectivity();
        if (onlyAccounts is not null)
        {
            entries = entries.Where(entry => onlyAccounts.Contains(entry.Key)).ToArray();
        }

        if (entries.Length == 0)
        {
            return new FollowPulseSample(null, false, null, null, null, null, Array.Empty<FollowLeaderMatch>());
        }

        var (accountKey, account) = entries[rotation % entries.Length];
        return await TryFetchFollowPulseForAsync(accountKey, account, activeFingerprint);
    }

    private async Task<FollowPulseSample> TryFetchFollowPulseForAsync(string accountKey, AccountConfig account, string? activeFingerprint = null)
    {
        try
        {
            var sampleResult = await _registry.SendCommandAsync(
                account.AgentId,
                "sample_player_count",
                args: activeFingerprint is null ? null : new { fingerprint = activeFingerprint },
                TimeSpan.FromSeconds(15));
            if (sampleResult.Ok && sampleResult.Data is { } sampleData)
            {
                return new FollowPulseSample(
                    TryGetInt(sampleData, "playerCount", out var sampledPlayerCount) ? sampledPlayerCount : null,
                    TryGetBoolean(sampleData, "leaderBound", out var leaderBound) && leaderBound,
                    TryGetBoolean(sampleData, "leaderPresent", out var leaderPresent) ? leaderPresent : null,
                    TryGetInt(sampleData, "leaderSlot", out var leaderSlot) ? leaderSlot : null,
                    TryGetNullableDouble(sampleData, "leaderScore"),
                    accountKey,
                    ParseLeaderMatches(sampleData),
                    TryGetBoolean(sampleData, "inGame", out var inGame) ? inGame : null,
                    PlayerCountFresh: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch fresh player count sample from {AccountKey}; falling back to cached status.", accountKey);
        }

        try
        {
            var result = await _registry.SendCommandAsync(
                account.AgentId,
                "status",
                args: null,
                TimeSpan.FromSeconds(6));
            if (!result.Ok || result.Data is not { } data)
            {
                return new FollowPulseSample(null, false, null, null, null, accountKey, Array.Empty<FollowLeaderMatch>());
            }

            using var document = JsonDocument.Parse(data.GetRawText());
            return new FollowPulseSample(
                TryGetInt(document.RootElement, "lastPartyMemberCount", out var otherMembers) ? otherMembers + 1 : null,
                LeaderBound: false,
                LeaderPresent: null,
                LeaderSlot: null,
                LeaderScore: null,
                accountKey,
                Array.Empty<FollowLeaderMatch>());
        }
        catch (Exception)
        {
            return new FollowPulseSample(null, false, null, null, null, accountKey, Array.Empty<FollowLeaderMatch>());
        }
    }

    private static IReadOnlyList<FollowLeaderMatch> ParseLeaderMatches(JsonElement data)
    {
        if (!data.TryGetProperty("leaderMatches", out var matches) || matches.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<FollowLeaderMatch>();
        }

        var result = new List<FollowLeaderMatch>();
        foreach (var element in matches.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("fingerprint", out var fingerprintProperty)
                || fingerprintProperty.GetString() is not { Length: > 0 } fingerprint)
            {
                continue;
            }

            result.Add(new FollowLeaderMatch(
                fingerprint,
                TryGetBoolean(element, "present", out var present) ? present : null,
                TryGetInt(element, "slot", out var slot) ? slot : null,
                TryGetNullableDouble(element, "score")));
        }

        return result;
    }

    private (string AccountKey, AccountConfig Account) RequireAccount(string accountKey)
    {
        if (!_registry.Accounts.TryGetValue(accountKey, out var account))
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
        var nodes = _registry.NodeSnapshot();
        var connectedNodes = nodes.Count(node => node.Connected);
        var agents = _registry.Snapshot();
        var connected = agents.Count(agent => agent.Connected);
        var accountConnectivity = _registry.GetAccountConnectivity();
        var accountLines = FormatAccountConnectivityHealthLines(accountConnectivity);
        var masterVersion = GetHostVersionText();
        var nodeLines = nodes.Select(node =>
        {
            var label = string.IsNullOrWhiteSpace(node.DisplayName)
                ? node.Id
                : $"{node.Id} ({node.DisplayName})";
            // A worker's build was invisible from Discord entirely, so a node that had quietly
            // stopped taking updates looked identical to one that was current.
            var version = string.IsNullOrWhiteSpace(node.Version)
                ? (node.Connected ? ", version unknown" : "")
                : $", v{AgentVersion.Display(node.Version)}"
                    + (node.Connected && AgentVersion.IsDifferentBuild(node.Version, masterVersion)
                        ? $" (master is v{masterVersion})"
                        : "");
            return $"{(node.Connected ? "online " : "offline")} node {label}: {node.AgentsConnected}/{node.AgentsConfigured} agent(s){version}";
        });
        var agentLines = agents.Select(agent =>
        {
            var label = string.IsNullOrWhiteSpace(agent.DisplayName)
                ? agent.Id
                : $"{agent.Id} ({agent.DisplayName})";
            return $"{(agent.Connected ? "online " : "offline")} {label}";
        });
        return string.Join("\n", new[]
        {
            $"Nodes: {connectedNodes}/{nodes.Count} connected",
            $"Agents: {connected}/{agents.Count} connected"
        }
            .Concat(accountLines)
            .Append(_followTemplates.FormatHealthLine())
            .Concat(nodeLines)
            .Concat(agentLines));
    }

    private string FormatStartupMessageContent()
    {
        return $"{_startupIntro} Version: {GetHostVersionText()}\n{FormatHealth()}";
    }

    private string FormatAllAccountStatuses()
    {
        var lines = _registry.Accounts.Select(pair => FormatAccountStatusLine(pair.Key, pair.Value));
        return string.Join("\n", new[] { $"D2RHost version: {GetHostVersionText()}" }.Concat(lines));
    }

    private async Task<string> FormatAllAccountStatusesLiveAsync(CancellationToken cancellationToken)
    {
        var lines = await Task.WhenAll(_registry.Accounts.Select(
            pair => FormatAccountStatusLineLiveAsync(pair.Key, pair.Value, cancellationToken)));
        return string.Join("\n", new[] { $"D2RHost version: {GetHostVersionText()}" }.Concat(lines));
    }

    private static string GetHostVersionText()
    {
        return AgentVersion.Display(AgentVersion.Current());
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
        var entry = _registry.Accounts.FirstOrDefault(
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
        // Shown only while corrupt. "visible GammaCalibration" alone does not say what to do about
        // it, and this is the one state where relaunching, power-cycling, and restarting the node
        // are all guaranteed to fail.
        var settings = SettingsRepairPolicy.IsSettingsCorrupt(statusJson)
            ? ", settings RESET BY D2R (first-run gamma screen; needs a donor Settings.json)"
            : "";
        var boundedCalls = TryReadDegradedBoundedCallSlots(statusJson, out var boundedCallSlots)
            ? $", screen-sampling slots {boundedCallSlots} free (a client with none refuses to click)"
            : "";
        var version = string.IsNullOrWhiteSpace(agent.Version)
            ? ""
            : $", version {AgentVersion.Display(agent.Version)}";
        var lastSeen = agent.LastSeenAt?.ToLocalTime().ToString("G") ?? "unknown";
        return $"{name}: online{version}, Battle.net {battleNet}, D2R {d2r}{visible}{settings}{boundedCalls}{activity}{statusMode}{statusError}{processDiscovery}{input}{lastInput}{checkpoint}, seen {lastSeen}";
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

    private static string FormatDiscordUser(IUser user)
    {
        var username = string.IsNullOrWhiteSpace(user.Username)
            ? "unknown"
            : user.Username;
        return $"{username} ({user.Id})";
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

    /// <summary>
    /// Why a mode whose entire output is a live Discord message cannot start over HTTP on a host
    /// that has no channel to post it in. Names the fix rather than just the failure.
    /// </summary>
    private static string NoChannelRefusal(string modeName)
    {
        return $"{modeName} posts a live monitor message and cannot run without a Discord channel. "
            + "Set one with `/d2r config notifications enabled:true channel-id:<id>`, or start this mode from Discord.";
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

    private sealed record AccountCommandRunResult(string AccountKey, bool Ok, string Message);

    private sealed record FollowAutoCancelResult(bool WasRunning, long RunId, bool Rejected);

    private enum FollowAutoBotCountControlOutcome
    {
        Changed,
        NotRunning,
        StaleMonitor,
        LocalRestartArmed,
        RateLimited,
        Refused,
        AtLimit,
        PublicMode
    }

    private sealed record FollowAutoBotCountControl(
        FollowAutoBotCountControlOutcome Outcome,
        int PreviousTarget = 0,
        int Target = 0,
        TimeSpan RetryAfter = default,
        IUserMessage? Monitor = null);

    // Shares the bot-count outcome enum because the two buttons fail in exactly the same ways: no
    // run, a click on a superseded monitor, or a frozen target while host recovery is armed.
    private sealed record FollowAutoPartyModeControl(
        FollowAutoBotCountControlOutcome Outcome,
        FollowAutoPartyModeChange? Change = null,
        IUserMessage? Monitor = null);

    // Shares that same outcome enum, with PublicMode standing in for "this is a private-mode
    // control" - the toggle is hidden there, but a stale client can still land a press.
    private sealed record FollowAutoJoinDelayControl(
        FollowAutoBotCountControlOutcome Outcome,
        bool Armed = false,
        IUserMessage? Monitor = null);

    private sealed record FollowAutoStopSignalResult(int Attempted, int Succeeded);

    private enum FollowAutoCheckOutcome
    {
        Joined,
        Waiting,
        Unbound,
        CheckFailure,
        GameFull
    }

    private enum FollowWarmupOutcome
    {
        Succeeded,
        Failed
    }

    /// <summary>
    /// One account's answer to one follow-auto check.
    /// </summary>
    /// <param name="LocalStall">
    /// Set when the agent declined to act on its OWN screen - it could not rule out being in a
    /// game, so it refused to click. That is a different kind of wait from "the leader has not
    /// made a game yet": no other client, and no amount of waiting, resolves it, so it feeds the
    /// escalation ladder instead of clearing it. See VmOperations.DescribeInconclusiveInGameDetection.
    /// </param>
    private sealed record FollowAutoCheckResult(
        string AccountKey,
        FollowAutoCheckOutcome Outcome,
        string Message,
        FollowWarmupOutcome WarmupOutcome = FollowWarmupOutcome.Succeeded,
        bool LocalStall = false);

    private sealed record SettingsRepairAttempt(
        bool Ok,
        string AccountKey,
        string? DonorAccountKey,
        string Message,
        bool CopyApplied = false);

    private sealed record SettingsReadyAttempt(bool Ok, string Message);

    private sealed record FollowAutoRunOptions(
        IMessageChannel Channel,
        int DelaySeconds,
        bool Watch,
        TimeSpan IdleTimeout,
        bool MetricsEnabled,
        int? CharacterSlot,
        int? FriendRow,
        IReadOnlyList<string> InitialRecoveryAccountKeys,
        string? ResumeReason = null,
        int TargetBotCount = FollowAutoRosterPolicy.DefaultBotCount,
        FollowAutoPartyMode PartyMode = FollowAutoPartyMode.Private);

    private sealed record FollowAutoNodeRecoveryRequest(
        string NodeId,
        string TriggerAccountKey,
        int ConsecutiveFailures);

    private sealed record FollowAutoNodeRecoveryResult(
        bool RestartQueued,
        bool RecoveryComplete,
        bool LocalRestartQueued,
        string Message);

    private sealed record FollowAutoVmRecoveryRequest(
        string NodeId,
        string AccountKey,
        AccountConfig Account,
        int ConsecutiveFailures);

    private sealed record FollowAutoClientRestartRequest(
        string AccountKey,
        AccountConfig Account,
        int TotalFailures,
        string LastMessage,
        bool Stalled = false);

    /// <summary>
    /// One mid-join probe of a joined vantage: what it saw of the bound leader, and the raw pulse
    /// behind that verdict so public mode can count the party bar off the same sample instead of
    /// spending a second command on it.
    /// </summary>
    private sealed record MidJoinLeaderProbe(
        bool? LockedPresent,
        bool? ConfirmAgreed,
        string Detail,
        FollowPulseSample? Sample);

    private sealed record FollowAutoGameWatchResult(
        string Reason,
        string? IsolatedAccountKey = null,
        bool AttemptTargetedLeave = true,
        bool ReconcileRoster = false);

    /// <summary>
    /// One invocation of a <c>/d2r</c> command, from whichever door it came in: a Discord slash
    /// command, a button on one of the bot's own messages, or an authenticated HTTP request.
    /// </summary>
    /// <remarks>
    /// The command handlers only ever read options and hand text back, so making this the single
    /// thing they depend on is what lets the HTTP API reuse them verbatim instead of growing a
    /// second, drifting implementation of every command. An API-originated context has no
    /// interaction to answer, so its replies are collected into <see cref="Api"/> and returned as
    /// the HTTP response body; anything that genuinely needs a Discord channel (the live monitors)
    /// falls back to the configured notification channel and refuses cleanly when there is none.
    /// </remarks>
    private sealed class SlashContext
    {
        private SlashContext(
            SocketInteraction? interaction,
            string? groupName,
            string subcommandName,
            IReadOnlyDictionary<string, object?> options,
            ApiCommandSink? api = null,
            IMessageChannel? fallbackChannel = null)
        {
            Interaction = interaction;
            GroupName = groupName;
            SubcommandName = subcommandName;
            _options = options;
            Api = api;
            _fallbackChannel = fallbackChannel;
        }

        private readonly IReadOnlyDictionary<string, object?> _options;
        private readonly IMessageChannel? _fallbackChannel;

        public SocketInteraction? Interaction { get; }
        public ApiCommandSink? Api { get; }
        public string? GroupName { get; }
        public string SubcommandName { get; }
        public int OptionCount => _options.Count;
        public bool MetricsEnabled => GetBool("metric") ?? false;

        /// <summary>True when this came in over HTTP rather than from Discord.</summary>
        public bool IsApi => Api is not null;

        /// <summary>
        /// Where a live monitor or session message can be posted, or null when there is nowhere
        /// to post one - a headless master, or an API call on a host with no notification channel
        /// configured. Callers that need one must say so rather than assume.
        /// </summary>
        public IMessageChannel? Channel => Interaction?.Channel ?? _fallbackChannel;

        /// <summary>Who issued this, for database attribution. "api" for an HTTP caller.</summary>
        public string ActorId => Interaction?.User.Id.ToString() ?? ApiActorId;

        /// <summary>Who issued this, for human-readable announcements.</summary>
        public string ActorLabel => Interaction is { } interaction
            ? FormatDiscordUser(interaction.User)
            : "the HTTP API";

        /// <summary>
        /// An API context is treated as already answered: there is no three-second interaction
        /// deadline to beat and nothing to defer, so every "acknowledge first" path becomes a
        /// no-op instead of needing its own branch at each call site.
        /// </summary>
        public bool HasResponded => Interaction?.HasResponded ?? true;

        public static SlashContext From(SocketSlashCommand command)
        {
            var subcommand = command.Data.Options.FirstOrDefault();
            if (subcommand is null)
            {
                return new SlashContext(command, groupName: null, "", EmptyOptions());
            }

            if (subcommand.Type == ApplicationCommandOptionType.SubCommandGroup)
            {
                var nestedSubcommand = subcommand.Options.FirstOrDefault();
                return new SlashContext(
                    command,
                    subcommand.Name,
                    nestedSubcommand?.Name ?? "",
                    ToOptionValues(nestedSubcommand?.Options));
            }

            return new SlashContext(
                command,
                groupName: null,
                subcommand.Name,
                ToOptionValues(subcommand.Options));
        }

        public static SlashContext FromComponent(SocketMessageComponent component, string subcommandName)
        {
            return new SlashContext(component, groupName: null, subcommandName, EmptyOptions());
        }

        public static SlashContext FromApi(
            string? groupName,
            string subcommandName,
            IReadOnlyDictionary<string, object?> options,
            ApiCommandSink sink,
            IMessageChannel? fallbackChannel)
        {
            return new SlashContext(
                interaction: null,
                groupName,
                subcommandName,
                new Dictionary<string, object?>(options, StringComparer.OrdinalIgnoreCase),
                sink,
                fallbackChannel);
        }

        private static Dictionary<string, object?> EmptyOptions()
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object?> ToOptionValues(
            IEnumerable<SocketSlashCommandDataOption>? options)
        {
            var values = EmptyOptions();
            foreach (var option in options ?? Array.Empty<SocketSlashCommandDataOption>())
            {
                values[option.Name] = option.Value;
            }

            return values;
        }

        public string? GetString(string name)
        {
            return _options.TryGetValue(name, out var option)
                ? option?.ToString()
                : null;
        }

        public string GetRequiredString(string name)
        {
            return GetString(name)
                ?? throw new InvalidOperationException($"{name} is required.");
        }

        public int? GetInt(string name)
        {
            if (!_options.TryGetValue(name, out var option) || option is null)
            {
                return null;
            }

            return Convert.ToInt32(option, CultureInfo.InvariantCulture);
        }

        public bool HasOption(string name)
        {
            return _options.ContainsKey(name);
        }

        public int GetRequiredInt(string name)
        {
            return GetInt(name)
                ?? throw new InvalidOperationException($"{name} is required.");
        }

        public bool? GetBool(string name)
        {
            if (!_options.TryGetValue(name, out var option) || option is null)
            {
                return null;
            }

            return Convert.ToBoolean(option, CultureInfo.InvariantCulture);
        }

        public bool GetRequiredBool(string name)
        {
            return GetBool(name)
                ?? throw new InvalidOperationException($"{name} is required.");
        }
    }
}

/// <summary>
/// How an interaction should be acknowledged before its handler starts working. Discord discards
/// an interaction that nothing answers within three seconds, and Discord.NET runs handlers inline
/// on the gateway task, so a handler that works first and answers second can fail its own command
/// and every interaction queued behind it.
/// </summary>
internal enum InteractionAcknowledgement
{
    /// <summary>Already acknowledged upstream - acknowledging again throws.</summary>
    None,

    /// <summary>
    /// Defer as a new ephemeral reply, which is what a slash command's DeferAsync produces.
    /// </summary>
    DeferAsNewEphemeralReply,

    /// <summary>
    /// Defer a button press without making the clicked message the interaction's original
    /// response. A component's DeferAsync acknowledges as DeferredUpdateMessage, which would put
    /// the eventual reply on top of the follow-auto monitor or the quick-action prompt the button
    /// sits on; DeferLoadingAsync posts a separate ephemeral response instead.
    /// </summary>
    DeferComponentWithoutClaimingItsMessage
}
