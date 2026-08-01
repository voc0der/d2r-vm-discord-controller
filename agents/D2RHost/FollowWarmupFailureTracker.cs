namespace D2RHost;

/// <summary>
/// Tracks consecutive outer <c>menu_ready</c> failures during one follow-auto run and escalates
/// on the fifth: first a power cycle of that account's own VM, and only if five more failures
/// follow a restart of the physical node that owns it.
/// </summary>
/// <remarks>
/// <para>
/// VM first, node second, because one wedged guest (a client that cannot initialize its graphics
/// device, a hung Battle.net install) is the common case and rebooting the whole physical host
/// for it takes every healthy sibling VM down with it. A guest that comes back broken a second
/// time is evidence the problem is below the guest, which is what the node restart is for.
/// </para>
/// <para>
/// The node latch deliberately outlives an individual account's next success. Once recovery has
/// been requested, only the recovery coordinator knows when that node is usable again, so it must
/// call <see cref="ResetNode"/> after recovery completes. Failures for separate accounts remain
/// independent even when those accounts share a node.
/// </para>
/// </remarks>
internal sealed class FollowWarmupFailureTracker
{
    internal const int RecoveryThreshold = 5;

    private readonly object _sync = new();
    private readonly Dictionary<string, AccountFailureState> _accountFailures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _recoveryLatchedNodes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records one failed outer <c>menu_ready</c> attempt for an account.
    /// </summary>
    /// <returns>
    /// The account's new consecutive-failure count, whether its VM should be power-cycled, and
    /// whether this call acquired the owning node's recovery latch. Once latched, later failures
    /// on the same node do not request a duplicate recovery until <see cref="ResetNode"/> is
    /// called.
    /// </returns>
    public FollowWarmupFailureResult RecordFailure(string accountKey, string nodeId)
    {
        accountKey = RequireKey(accountKey, nameof(accountKey));
        nodeId = RequireKey(nodeId, nameof(nodeId));

        lock (_sync)
        {
            var priorFailures = 0;
            var vmCycled = false;
            if (_accountFailures.TryGetValue(accountKey, out var prior)
                && string.Equals(prior.NodeId, nodeId, StringComparison.OrdinalIgnoreCase))
            {
                priorFailures = prior.ConsecutiveFailures;
                vmCycled = prior.VmCycled;
            }

            // An account moved to another node starts a new physical-host failure incident. Its
            // old node latch, if any, stays set until that node's recovery coordinator clears it.
            var consecutiveFailures = priorFailures == int.MaxValue
                ? int.MaxValue
                : priorFailures + 1;
            _accountFailures[accountKey] = new AccountFailureState(nodeId, consecutiveFailures, vmCycled);

            if (consecutiveFailures < RecoveryThreshold)
            {
                return new FollowWarmupFailureResult(consecutiveFailures, false, false);
            }

            // The VM cycle is per account and needs no node latch: it takes down exactly one
            // guest, so two accounts on the same node can each get one without interfering.
            if (!vmCycled)
            {
                return new FollowWarmupFailureResult(consecutiveFailures, true, false);
            }

            return new FollowWarmupFailureResult(
                consecutiveFailures,
                false,
                _recoveryLatchedNodes.Add(nodeId));
        }
    }

    /// <summary>
    /// Records that the account's VM was power-cycled. The strike count restarts so the client
    /// gets a full fresh budget on the rebuilt guest, but the incident remembers the cycle: the
    /// next time this account exhausts that budget it escalates to the node instead of cycling
    /// the same VM again.
    /// </summary>
    public void RecordVmRecovered(string accountKey, string nodeId)
    {
        MarkVmCycled(accountKey, nodeId, resetFailures: true);
    }

    /// <summary>
    /// Records that the account's VM could not be power-cycled (no Hyper-V mapping, worker
    /// offline, PowerShell refused). The strike count is deliberately kept, so the very next
    /// failure escalates to the node restart instead of retrying an impossible VM cycle.
    /// </summary>
    public void RecordVmRecoveryUnavailable(string accountKey, string nodeId)
    {
        MarkVmCycled(accountKey, nodeId, resetFailures: false);
    }

    private void MarkVmCycled(string accountKey, string nodeId, bool resetFailures)
    {
        accountKey = RequireKey(accountKey, nameof(accountKey));
        nodeId = RequireKey(nodeId, nameof(nodeId));

        lock (_sync)
        {
            var failures = 0;
            if (_accountFailures.TryGetValue(accountKey, out var prior)
                && string.Equals(prior.NodeId, nodeId, StringComparison.OrdinalIgnoreCase))
            {
                failures = resetFailures ? 0 : prior.ConsecutiveFailures;
            }

            _accountFailures[accountKey] = new AccountFailureState(nodeId, failures, VmCycled: true);
        }
    }

    /// <summary>
    /// Records a successful warmup, including a live preflight that proves the client was already
    /// ready. This resets only the named account; it cannot declare a latched node recovered.
    /// </summary>
    public void RecordSuccess(string accountKey)
    {
        accountKey = RequireKey(accountKey, nameof(accountKey));
        lock (_sync)
        {
            _accountFailures.Remove(accountKey);
        }
    }

    /// <summary>
    /// Clears the recovery latch and every account strike associated with a node after recovery.
    /// </summary>
    public void ResetNode(string nodeId)
    {
        nodeId = RequireKey(nodeId, nameof(nodeId));
        lock (_sync)
        {
            _recoveryLatchedNodes.Remove(nodeId);
            foreach (var accountKey in _accountFailures
                         .Where(pair => string.Equals(pair.Value.NodeId, nodeId, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _accountFailures.Remove(accountKey);
            }
        }
    }

    private static string RequireKey(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty key is required.", parameterName);
        }

        return value.Trim();
    }

    private sealed record AccountFailureState(string NodeId, int ConsecutiveFailures, bool VmCycled);
}

internal readonly record struct FollowWarmupFailureResult(
    int ConsecutiveFailures,
    bool VmRecoveryRequested,
    bool RecoveryRequested);
