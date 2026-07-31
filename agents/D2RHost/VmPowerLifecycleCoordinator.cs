using AgentCommon;

namespace D2RHost;

/// <summary>
/// The small Hyper-V surface needed to make a physical-host power transition safe for its VMs.
/// Kept behind an interface so the persistence and state-machine behavior can be tested without
/// a Windows Hyper-V installation.
/// </summary>
public interface ILocalVmPowerOperations
{
    Task<VmPowerStateResult> GetPowerStateAsync(string vmName, CancellationToken cancellationToken);

    Task<CommandResult> StopAsync(string vmName, CancellationToken cancellationToken);

    Task<CommandResult> StartAsync(string vmName, CancellationToken cancellationToken);
}

public sealed record VmPowerStateResult(bool Ok, string? State, string Message)
{
    public static VmPowerStateResult Success(string state) => new(true, state, state);

    public static VmPowerStateResult Failure(string message) => new(false, null, message);
}

public sealed record VmPowerPreparationResult(
    bool Ok,
    IReadOnlyList<string> StoppedVmNames,
    string Message,
    bool RetryRestorePending = false);

public sealed record VmPowerRestoreResult(
    IReadOnlyList<string> RestoredVmNames,
    IReadOnlyList<string> AlreadyRunningVmNames,
    IReadOnlyList<string> PendingVmNames,
    IReadOnlyList<string> Failures)
{
    public bool Complete => PendingVmNames.Count == 0;
}

/// <summary>
/// Turns off only configured VMs that are confirmed Running, persists that exact set before the
/// first stop, and later starts only the persisted set. Unknown/transitional Hyper-V states are
/// never forced in either direction.
/// </summary>
public sealed class VmPowerLifecycleCoordinator
{
    private const string RunningState = "Running";
    private const string OffState = "Off";

    private readonly HostConfig _config;
    private readonly AppDb _db;
    private readonly ILocalVmPowerOperations _hyperV;
    private readonly ILogger<VmPowerLifecycleCoordinator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _hostPowerTransitionArmed;

    public VmPowerLifecycleCoordinator(
        HostConfig config,
        AppDb db,
        ILocalVmPowerOperations hyperV,
        ILogger<VmPowerLifecycleCoordinator> logger)
    {
        _config = config;
        _db = db;
        _hyperV = hyperV;
        _logger = logger;
    }

    public async Task<VmPowerPreparationResult> PrepareForHostPowerActionAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_hostPowerTransitionArmed)
            {
                return new VmPowerPreparationResult(
                    false,
                    [],
                    "A host power transition is already prepared on this node; its VM resume journal is still armed.");
            }

            var leftover = _db.GetPendingVmResume(_config.NodeId);
            if (leftover.Count > 0)
            {
                var restore = await RestorePendingCoreAsync(cancellationToken);
                if (!restore.Complete)
                {
                    return new VmPowerPreparationResult(
                        false,
                        [],
                        "A previous host power transition still has VM restore work pending: "
                            + string.Join(", ", restore.PendingVmNames)
                            + FormatFailures(restore.Failures),
                        RetryRestorePending: true);
                }
            }

            var configuredVmNames = GetConfiguredLocalVmNames();
            var runningVmNames = new List<string>();
            var stateFailures = new List<string>();
            foreach (var vmName in configuredVmNames)
            {
                var state = await _hyperV.GetPowerStateAsync(vmName, cancellationToken);
                if (!state.Ok || string.IsNullOrWhiteSpace(state.State))
                {
                    stateFailures.Add($"{vmName}: {state.Message}");
                    continue;
                }

                if (IsState(state.State, RunningState))
                {
                    runningVmNames.Add(vmName);
                }
                else if (!IsState(state.State, OffState))
                {
                    stateFailures.Add(
                        $"{vmName}: state is {state.State}; only Running VMs are stopped and Off VMs are left alone");
                }
            }

            if (stateFailures.Count > 0)
            {
                return new VmPowerPreparationResult(
                    false,
                    [],
                    "VM state preflight failed; the host power action was not queued. "
                        + string.Join("; ", stateFailures));
            }

            // This is deliberately committed before the first Stop-VM. If the host process dies
            // midway through the stop pass, startup still knows every VM it intended to stop.
            _db.ReplacePendingVmResume(_config.NodeId, runningVmNames);

            var stopped = new List<string>();
            foreach (var vmName in runningVmNames)
            {
                var freshState = await _hyperV.GetPowerStateAsync(vmName, cancellationToken);
                if (!freshState.Ok || string.IsNullOrWhiteSpace(freshState.State))
                {
                    return await RollBackPreparationAsync(
                        stopped,
                        $"Could not re-check {vmName} before stopping it: {freshState.Message}",
                        cancellationToken);
                }

                if (IsState(freshState.State, OffState))
                {
                    // It changed independently after preflight, so it is not a VM we turned off
                    // and must not be brought back up later.
                    _db.RemovePendingVmResume(_config.NodeId, vmName);
                    continue;
                }

                if (!IsState(freshState.State, RunningState))
                {
                    return await RollBackPreparationAsync(
                        stopped,
                        $"{vmName} changed to {freshState.State} before shutdown; no transition was forced",
                        cancellationToken);
                }

                var stop = await _hyperV.StopAsync(vmName, cancellationToken);
                if (!stop.Ok)
                {
                    return await RollBackPreparationAsync(
                        stopped,
                        $"Could not stop {vmName}: {stop.Message}",
                        cancellationToken);
                }

                var stoppedState = await _hyperV.GetPowerStateAsync(vmName, cancellationToken);
                if (!stoppedState.Ok || !IsState(stoppedState.State, OffState))
                {
                    return await RollBackPreparationAsync(
                        stopped,
                        $"{vmName} did not confirm Off after Stop-VM (state: {stoppedState.State ?? "unknown"}; {stoppedState.Message})",
                        cancellationToken);
                }

                stopped.Add(vmName);
            }

            var message = stopped.Count == 0
                ? "No configured VMs were Running; no VM stop was needed."
                : $"Confirmed {stopped.Count} VM(s) Off and recorded them for resume: {string.Join(", ", stopped)}.";
            // Keep the in-memory arm even when the journal is empty. A second host command in
            // the short Queue delay is still a competing physical transition and must not race
            // the one already accepted.
            _hostPowerTransitionArmed = true;
            return new VmPowerPreparationResult(true, stopped, message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VmPowerRestoreResult> RestorePendingAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await RestorePendingCoreAsync(cancellationToken);
        }
        finally
        {
            // This method is called only when the queued transition has returned/failed, or by
            // startup recovery in a fresh coordinator. Either way the old transition no longer
            // owns the journal and a future command may prepare normally.
            _hostPowerTransitionArmed = false;
            _gate.Release();
        }
    }

    public IReadOnlyList<string> GetPendingVmNames()
    {
        return _db.GetPendingVmResume(_config.NodeId);
    }

    private async Task<VmPowerPreparationResult> RollBackPreparationAsync(
        IReadOnlyList<string> stopped,
        string failure,
        CancellationToken cancellationToken)
    {
        var restore = await RestorePendingCoreAsync(cancellationToken);
        _hostPowerTransitionArmed = false;
        var rollback = restore.Complete
            ? " Any VMs already stopped were restored."
            : " VM rollback is still pending for: " + string.Join(", ", restore.PendingVmNames)
                + FormatFailures(restore.Failures);
        return new VmPowerPreparationResult(
            false,
            stopped,
            failure + ". The host power action was not queued." + rollback,
            RetryRestorePending: !restore.Complete);
    }

    private async Task<VmPowerRestoreResult> RestorePendingCoreAsync(CancellationToken cancellationToken)
    {
        var pending = _db.GetPendingVmResume(_config.NodeId);
        var restored = new List<string>();
        var alreadyRunning = new List<string>();
        var failures = new List<string>();

        foreach (var vmName in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await _hyperV.GetPowerStateAsync(vmName, cancellationToken);
            if (!state.Ok || string.IsNullOrWhiteSpace(state.State))
            {
                failures.Add($"{vmName}: {state.Message}");
                continue;
            }

            if (IsState(state.State, RunningState))
            {
                _db.RemovePendingVmResume(_config.NodeId, vmName);
                alreadyRunning.Add(vmName);
                continue;
            }

            if (!IsState(state.State, OffState))
            {
                failures.Add($"{vmName}: state is {state.State}; start was not forced");
                continue;
            }

            var start = await _hyperV.StartAsync(vmName, cancellationToken);
            if (!start.Ok)
            {
                failures.Add($"{vmName}: {start.Message}");
                continue;
            }

            var startedState = await _hyperV.GetPowerStateAsync(vmName, cancellationToken);
            if (!startedState.Ok || !IsState(startedState.State, RunningState))
            {
                failures.Add(
                    $"{vmName}: did not confirm Running after Start-VM (state: {startedState.State ?? "unknown"}; {startedState.Message})");
                continue;
            }

            _db.RemovePendingVmResume(_config.NodeId, vmName);
            restored.Add(vmName);
        }

        var stillPending = _db.GetPendingVmResume(_config.NodeId);
        if (restored.Count > 0 || alreadyRunning.Count > 0)
        {
            _logger.LogInformation(
                "VM restore pass completed on node {NodeId}: started {Restored}; already running {AlreadyRunning}; pending {Pending}.",
                _config.NodeId,
                string.Join(", ", restored),
                string.Join(", ", alreadyRunning),
                string.Join(", ", stillPending));
        }

        if (failures.Count > 0)
        {
            _logger.LogWarning(
                "VM restore pass on node {NodeId} left work pending: {Failures}",
                _config.NodeId,
                string.Join("; ", failures));
        }

        return new VmPowerRestoreResult(restored, alreadyRunning, stillPending, failures);
    }

    private string[] GetConfiguredLocalVmNames()
    {
        return _config.Accounts.Values
            .Where(account => string.IsNullOrWhiteSpace(account.NodeId)
                || string.Equals(account.NodeId, _config.NodeId, StringComparison.OrdinalIgnoreCase))
            .Select(account => account.VmName?.Trim())
            .Where(vmName => !string.IsNullOrWhiteSpace(vmName))
            .Select(vmName => vmName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(vmName => vmName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsState(string? actual, string expected)
    {
        return string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatFailures(IReadOnlyCollection<string> failures)
    {
        return failures.Count == 0 ? "." : ". " + string.Join("; ", failures);
    }
}

/// <summary>
/// Hyper-V can become ready a little after D2RHost starts as a Windows scheduled task. Retry a
/// persisted restore in the background instead of abandoning the VM rolodex after one early miss.
/// </summary>
public sealed class VmPowerRestoreService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly VmPowerLifecycleCoordinator _coordinator;
    private readonly ILogger<VmPowerRestoreService> _logger;

    public VmPowerRestoreService(
        VmPowerLifecycleCoordinator coordinator,
        ILogger<VmPowerRestoreService> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested
            && _coordinator.GetPendingVmNames().Count > 0)
        {
            try
            {
                var result = await _coordinator.RestorePendingAsync(stoppingToken);
                if (result.Complete)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Persisted VM restore pass failed; retrying.");
            }

            await Task.Delay(RetryDelay, stoppingToken);
        }
    }
}
