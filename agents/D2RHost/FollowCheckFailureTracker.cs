namespace D2RHost;

/// <summary>
/// Tracks consecutive <c>menu_follow_auto_check</c> failures for one account during a follow-auto
/// run and escalates: first a D2R restart on that client, then a power cycle of its VM.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <see cref="FollowWarmupFailureTracker"/> deliberately does not cover it.
/// That ladder counts desktop-to-lobby warmup failures, and a check failure is not one: the
/// client answered <c>menu_ready</c>, so warmup genuinely succeeded and the warmup counter is
/// correctly reset. The gap was that nothing else was counting. A client whose warmup keeps
/// succeeding but whose follow check keeps failing - the exact shape of a bot that disconnects
/// mid-session and never rejoins - produced a monitor line every cycle and no action, forever.
/// </para>
/// <para>
/// The observed case was a check that ran past the 205s agent-side command timeout. The host saw
/// <c>ok=false</c>, recorded a check failure, reset the warmup strikes, waited, and repeated -
/// roughly one no-op every three and a half minutes for the entire session while the other five
/// bots played. The agent-side budget in <c>FriendJoinRetryBudget</c> stops that specific timeout
/// at its source; this ladder is what makes any persistent check failure recoverable, including
/// the ones nobody has seen yet.
/// </para>
/// <para>
/// A D2R restart comes first because the failures this ladder sees happen on a client that is
/// reachable and answering. That points at wedged UI state - a modal nobody dismissed, a lobby
/// that stopped responding to clicks - which a client restart clears in about a minute, against
/// several for a VM cycle. Only when restarts have not helped is the guest itself suspect.
/// </para>
/// </remarks>
internal sealed class FollowCheckFailureTracker
{
    /// <summary>
    /// Consecutive check failures before acting. Three cycles is roughly five to ten minutes of
    /// real time, which clears transient causes - a game the leader was mid-creating, one slow
    /// lobby - without letting a genuinely stuck client sit for a whole session.
    /// </summary>
    internal const int EscalationThreshold = 3;

    /// <summary>
    /// Client restarts attempted before escalating to the VM. Two, because a restart that did not
    /// fix it twice is evidence the problem is not in the D2R process.
    /// </summary>
    internal const int MaxClientRestarts = 2;

    private readonly object _sync = new();
    private readonly Dictionary<string, AccountCheckState> _states =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records one failed follow check and reports what, if anything, should be done about it.
    /// </summary>
    public FollowCheckFailureResult RecordFailure(string accountKey)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            if (!_states.TryGetValue(accountKey, out var state))
            {
                state = new AccountCheckState();
                _states[accountKey] = state;
            }

            state.ConsecutiveFailures++;
            var totalFailures = state.TotalFailures + 1;
            state.TotalFailures = totalFailures;

            if (state.ConsecutiveFailures < EscalationThreshold)
            {
                return new FollowCheckFailureResult(state.ConsecutiveFailures, totalFailures, false, false);
            }

            // The streak has been acted on. Start a fresh one so the next escalation needs
            // another full threshold of failures rather than firing on every subsequent cycle.
            state.ConsecutiveFailures = 0;

            if (state.ClientRestartsUsed < MaxClientRestarts)
            {
                state.ClientRestartsUsed++;
                return new FollowCheckFailureResult(EscalationThreshold, totalFailures, true, false);
            }

            // Latch the VM request the way the warmup ladder latches its node request: the
            // recovery coordinator owns the account from here, and re-requesting on the next
            // streak would stack power cycles on a guest that is already being rebuilt.
            if (!state.VmRecoveryRequested)
            {
                state.VmRecoveryRequested = true;
                return new FollowCheckFailureResult(EscalationThreshold, totalFailures, false, true);
            }

            return new FollowCheckFailureResult(EscalationThreshold, totalFailures, false, false);
        }
    }

    /// <summary>
    /// Records a check that produced any usable outcome - joined, waiting, game-full, or unbound.
    /// Only a clean result clears the ladder; a client that alternates between failing and
    /// answering must still escalate rather than resetting itself forever.
    /// </summary>
    public void RecordSuccess(string accountKey)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            _states.Remove(accountKey);
        }
    }

    /// <summary>
    /// Clears the VM latch after that account's recovery has completed, so a client that fails
    /// again later can escalate again instead of being stuck one step below the ladder's top.
    /// </summary>
    public void RecordVmRecovered(string accountKey)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            if (_states.TryGetValue(accountKey, out var state))
            {
                state.VmRecoveryRequested = false;
                state.ClientRestartsUsed = 0;
                state.ConsecutiveFailures = 0;
            }
        }
    }

    public int GetConsecutiveFailures(string accountKey)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            return _states.TryGetValue(accountKey, out var state) ? state.ConsecutiveFailures : 0;
        }
    }

    private static string RequireKey(string accountKey)
    {
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            throw new ArgumentException("An account key is required.", nameof(accountKey));
        }

        return accountKey;
    }

    private sealed class AccountCheckState
    {
        public int ConsecutiveFailures { get; set; }

        public int TotalFailures { get; set; }

        public int ClientRestartsUsed { get; set; }

        public bool VmRecoveryRequested { get; set; }
    }
}

internal sealed record FollowCheckFailureResult(
    int ConsecutiveFailures,
    int TotalFailures,
    bool ClientRestartRequested,
    bool VmRecoveryRequested);
