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

    /// <summary>
    /// Consecutive local stalls before acting. A stall is an <c>ok=true</c> answer, so unlike a
    /// failure it also has to clear a wall-clock window (<see cref="StallEscalationWindow"/>)
    /// before it counts as stuck.
    /// </summary>
    internal const int StallEscalationThreshold = 3;

    /// <summary>
    /// How long an account must stall continuously before the ladder acts on it.
    /// </summary>
    /// <remarks>
    /// The check cycle is five seconds by default and a stall answers almost instantly, so a
    /// count alone would escalate a client roughly fifteen seconds after its first bad sample -
    /// far too fast for a guest that is merely busy. Three minutes is longer than any transient
    /// this has been seen to survive (a load spike, one slow capture) and still short enough that
    /// the rest of the fleet is not left standing in a game waiting on it.
    /// </remarks>
    internal static readonly TimeSpan StallEscalationWindow = TimeSpan.FromMinutes(3);

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
    /// Records a check that answered <c>ok=true</c> but reported that this client refused to act
    /// on its own screen - the in-game safety check could not decide, so it did not click. Returns
    /// what, if anything, should be done about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the gap that let one bot sit at the lobby for a whole session while the other six
    /// played. A stall is a successful check by every measure the host had: the agent answered,
    /// promptly, with <c>ok=true</c> and a perfectly clear explanation of why it was not clicking.
    /// So it landed in <see cref="RecordSuccess"/>, which CLEARS the ladder - every cycle, forever.
    /// The client never joined, the fleet never reached all-joined, and because the all-joined
    /// watch is the only thing that advances a game, the run stopped advancing entirely.
    /// </para>
    /// <para>
    /// Only stalls the agent itself marks local are counted (see the <c>localStall</c> flag on
    /// menu_follow_auto_check). Waiting on the world - the bound friend offline, no joinable game
    /// yet, D2R still starting - is the normal between-games state of every account in the fleet
    /// at once, and restarting clients over it would be a self-inflicted outage.
    /// </para>
    /// <para>
    /// Stalls and failures share the ladder's rungs (<see cref="MaxClientRestarts"/>, then the VM)
    /// because they are the same question - "is this client ever going to make progress?" - and a
    /// client that alternates between the two must not get twice the recovery budget.
    /// </para>
    /// </remarks>
    public FollowCheckFailureResult RecordStall(string accountKey, DateTimeOffset nowUtc)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            if (!_states.TryGetValue(accountKey, out var state))
            {
                state = new AccountCheckState();
                _states[accountKey] = state;
            }

            state.StallingSinceUtc ??= nowUtc;
            state.ConsecutiveStalls++;
            var totalFailures = state.TotalFailures + 1;
            state.TotalFailures = totalFailures;

            if (state.ConsecutiveStalls < StallEscalationThreshold
                || nowUtc - state.StallingSinceUtc.Value < StallEscalationWindow)
            {
                return new FollowCheckFailureResult(state.ConsecutiveStalls, totalFailures, false, false);
            }

            // Acted on: restart both halves of the streak so the next rung needs another full
            // window rather than firing on every cycle from here on.
            var streak = state.ConsecutiveStalls;
            state.ConsecutiveStalls = 0;
            state.StallingSinceUtc = null;

            if (state.ClientRestartsUsed < MaxClientRestarts)
            {
                state.ClientRestartsUsed++;
                return new FollowCheckFailureResult(streak, totalFailures, true, false);
            }

            if (!state.VmRecoveryRequested)
            {
                state.VmRecoveryRequested = true;
                return new FollowCheckFailureResult(streak, totalFailures, false, true);
            }

            return new FollowCheckFailureResult(streak, totalFailures, false, false);
        }
    }

    /// <summary>
    /// Records a check that produced real progress - joined, game-full, or unbound - or an
    /// ordinary wait on something outside this client. Only a clean result clears the ladder; a
    /// client that alternates between failing and answering must still escalate rather than
    /// resetting itself forever.
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
                state.ConsecutiveStalls = 0;
                state.StallingSinceUtc = null;
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

    /// <summary>Consecutive local stalls currently recorded for an account, for the monitor.</summary>
    public int GetConsecutiveStalls(string accountKey)
    {
        accountKey = RequireKey(accountKey);
        lock (_sync)
        {
            return _states.TryGetValue(accountKey, out var state) ? state.ConsecutiveStalls : 0;
        }
    }

    private sealed class AccountCheckState
    {
        public int ConsecutiveFailures { get; set; }

        public int ConsecutiveStalls { get; set; }

        /// <summary>When the current stall streak began, for the wall-clock half of the rule.</summary>
        public DateTimeOffset? StallingSinceUtc { get; set; }

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
