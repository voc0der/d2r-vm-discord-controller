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
//
// Pulses round-robin across every online account: each VM is still probed at the original
// PlayerCountDropPollPolicy cadence, but with N vantages taking turns the fleet notices a
// change N times sooner, and the scan that confirms a flagged absence naturally comes from a
// different VM's screen than the one that flagged it.
public static class FollowAutoPulsePolicy
{
    // Two consecutive missing samples before leaving: absorbs a one-pulse transient without
    // adding meaningful latency (the confirm scan runs after ConfirmResampleDelaySeconds or
    // the rotated poll delay, whichever is shorter). Loading screens don't consume this
    // allowance - they fail the in-game check and arrive here as LeaderPresent=null.
    public const int LeaderGoneConfirmationSamples = 2;

    public const int ConfirmResampleDelaySeconds = 2;

    // Floor for the divided rotation delay: below this the extra host<->agent chatter buys
    // nothing (an agent-side scan plus round-trip already costs a meaningful fraction of it).
    public const int MinRotatedPollSeconds = 1;

    // The base PlayerCountDropPollPolicy delay is how often one vantage can reasonably be
    // probed; dividing it by the online vantage count keeps that per-VM cadence while the
    // rotation multiplies the fleet-wide sampling rate.
    public static TimeSpan GetRotatedPollDelay(TimeSpan baseDelay, int vantageCount)
    {
        if (vantageCount <= 1)
        {
            return baseDelay;
        }

        var divided = TimeSpan.FromTicks(baseDelay.Ticks / vantageCount);
        var floor = TimeSpan.FromSeconds(MinRotatedPollSeconds);
        return divided >= floor ? divided : floor;
    }

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
        // semantics rather than never leaving. "Couldn't check" is not evidence of absence,
        // but with rotating vantages it must not erase another vantage's evidence either - one
        // VM sitting in a loading screen (or stuck outside the game) takes its turn between
        // the flagging scan and the confirming scan, so a bound leader's missing streak is
        // preserved across unverifiable pulses and only an actual sighting resets it.
        return new FollowAutoPulseDecision(
            playerCount is { } count && baseline is { } known && count < known
                ? FollowAutoPulseAction.Leave
                : FollowAutoPulseAction.Wait,
            leaderBound ? priorLeaderMissingStreak : 0);
    }
}
