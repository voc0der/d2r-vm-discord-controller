namespace AgentCommon;

public enum FollowAutoPulseAction
{
    // Keep polling; nothing indicates the current game is over.
    Wait,

    // Leader verified in the game: keep polling and move the player-count baseline to the
    // current count, so joins/leaves by strangers stop looking like a leave trigger later.
    RebaselineAndWait,

    // Leader missing on this sample but not yet confirmed - resample quickly before acting,
    // because a single miss can be a transient (something briefly drawn over the name band).
    ConfirmLeaderGone,

    // The current game is over for us: tell all accounts to leave.
    Leave
}

public sealed record FollowAutoPulseDecision(FollowAutoPulseAction Action, int LeaderMissingStreak);

// Issue #25 follow-up (bind-in-game): decides what one follow-auto in-game pulse means. Split
// out of the DiscordBot loop for the same reason as PlayerCountDropPollPolicy - the decision
// table is the part worth regression-testing, and it needs no Discord or agent plumbing to test.
//
// The problem this solves: in a public game the player count shifts constantly for reasons that
// have nothing to do with the operator ("count dropped" used to be the only leave signal, so a
// stranger leaving made every bot leave, then follow-auto immediately rejoined them into the
// same game because the bound friend was still in it). When a leader fingerprint is bound, the
// leave signal becomes "the leader's name is no longer in the party bar", and count drops while
// the leader is verified present just move the baseline.
public static class FollowAutoPulsePolicy
{
    // Two consecutive missing samples before leaving: absorbs a one-pulse transient without
    // adding meaningful latency (the confirm resample runs after ConfirmResampleDelaySeconds,
    // not a full poll interval). Loading screens don't need this allowance at all - they fail
    // the in-game check and arrive here as LeaderPresent=null, which resets the streak.
    public const int LeaderGoneConfirmationSamples = 2;

    public const int ConfirmResampleDelaySeconds = 2;

    public static FollowAutoPulseDecision Decide(
        bool leaderBound,
        bool? leaderPresent,
        int? playerCount,
        int? baseline,
        int priorLeaderMissingStreak)
    {
        if (leaderBound && leaderPresent is { } present)
        {
            if (present)
            {
                return new FollowAutoPulseDecision(FollowAutoPulseAction.RebaselineAndWait, 0);
            }

            var streak = priorLeaderMissingStreak + 1;
            return new FollowAutoPulseDecision(
                streak >= LeaderGoneConfirmationSamples ? FollowAutoPulseAction.Leave : FollowAutoPulseAction.ConfirmLeaderGone,
                streak);
        }

        // No leader bound, or this pulse could not check (not visibly in a game, sample
        // failure, or an incomparable template): fall back to the original count-drop
        // semantics rather than never leaving. The streak resets because "couldn't check" is
        // not evidence of absence.
        return new FollowAutoPulseDecision(
            playerCount is { } count && baseline is { } known && count < known
                ? FollowAutoPulseAction.Leave
                : FollowAutoPulseAction.Wait,
            0);
    }
}
