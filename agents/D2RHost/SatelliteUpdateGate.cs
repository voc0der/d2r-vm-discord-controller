namespace D2RHost;

/// <summary>
/// Decides which satellite is allowed to be offered <c>self_update</c>, shared by every path that
/// makes such an offer.
/// </summary>
/// <remarks>
/// There are two offer paths - the authentication hook in <see cref="AgentRegistry"/> and the
/// five-minute sweep in <see cref="FleetAgentUpdater"/> - and they used to keep separate
/// "already offered" sets. Nothing stopped both from firing for the same satellite at the same
/// reported version, and after a master restart that is precisely what lines up: the satellite
/// reconnects and is offered an update immediately, then the sweep's first pass runs before the
/// satellite has finished restarting and still sees the old version, so it offers again.
///
/// The result was two updaters unpacking the same release over the same directory at once, which
/// is how a node could report two "update started" messages and still be running the old build.
/// </remarks>
public sealed class SatelliteUpdateGate
{
    private readonly HashSet<string> _offered = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    /// Claims the right to offer an update, or returns false if this satellite has already been
    /// offered one at this version or has an offer in flight right now.
    /// </summary>
    public bool TryBeginOffer(string agentId, string? version)
    {
        lock (_lock)
        {
            // In-flight is tracked per satellite rather than per version because a satellite
            // keeps reporting its old version for the whole time it is updating and restarting.
            if (!_inFlight.Add(agentId))
            {
                return false;
            }

            if (!_offered.Add(BuildKey(agentId, version)))
            {
                _inFlight.Remove(agentId);
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Releases the in-flight claim. When <paramref name="retryable"/> is set the offer is also
    /// forgotten, so a satellite whose command failed outright is asked again next time rather
    /// than being written off until its version changes - which, for a failed update, is never.
    /// </summary>
    public void CompleteOffer(string agentId, string? version, bool retryable)
    {
        lock (_lock)
        {
            _inFlight.Remove(agentId);
            if (retryable)
            {
                _offered.Remove(BuildKey(agentId, version));
            }
        }
    }

    private static string BuildKey(string agentId, string? version)
    {
        return $"{agentId}|{version ?? "(unknown)"}";
    }
}
