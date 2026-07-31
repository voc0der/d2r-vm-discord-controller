namespace D2RHost;

/// <summary>
/// Tracks consecutive outer <c>menu_ready</c> failures during one follow-auto run and turns the
/// fifth failure into one recovery request for the physical node that owns the account.
/// </summary>
/// <remarks>
/// The node latch deliberately outlives an individual account's next success. Once recovery has
/// been requested, only the recovery coordinator knows when that node is usable again, so it must
/// call <see cref="ResetNode"/> after recovery completes. Failures for separate accounts remain
/// independent even when those accounts share a node.
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
    /// The account's new consecutive-failure count and whether this call acquired the owning
    /// node's recovery latch. Once latched, later failures on the same node do not request a
    /// duplicate recovery until <see cref="ResetNode"/> is called.
    /// </returns>
    public FollowWarmupFailureResult RecordFailure(string accountKey, string nodeId)
    {
        accountKey = RequireKey(accountKey, nameof(accountKey));
        nodeId = RequireKey(nodeId, nameof(nodeId));

        lock (_sync)
        {
            var priorFailures = 0;
            if (_accountFailures.TryGetValue(accountKey, out var prior)
                && string.Equals(prior.NodeId, nodeId, StringComparison.OrdinalIgnoreCase))
            {
                priorFailures = prior.ConsecutiveFailures;
            }

            // An account moved to another node starts a new physical-host failure incident. Its
            // old node latch, if any, stays set until that node's recovery coordinator clears it.
            var consecutiveFailures = priorFailures == int.MaxValue
                ? int.MaxValue
                : priorFailures + 1;
            _accountFailures[accountKey] = new AccountFailureState(nodeId, consecutiveFailures);

            var recoveryRequested = consecutiveFailures >= RecoveryThreshold
                && _recoveryLatchedNodes.Add(nodeId);
            return new FollowWarmupFailureResult(consecutiveFailures, recoveryRequested);
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

    private sealed record AccountFailureState(string NodeId, int ConsecutiveFailures);
}

internal readonly record struct FollowWarmupFailureResult(
    int ConsecutiveFailures,
    bool RecoveryRequested);
