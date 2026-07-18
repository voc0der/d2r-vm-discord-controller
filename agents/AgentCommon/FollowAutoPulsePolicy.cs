namespace AgentCommon;

public enum FollowAutoPulseAction
{
    // Keep polling; nothing on this sample indicates the current game is over.
    Wait,

    // Leader verified in the game: keep polling and move the player-count baseline to the
    // current count, so joins/leaves by strangers stop looking like a leave trigger later.
    RebaselineAndWait,

    // This vantage cannot see the bound leader. The host forces an immediate second opinion
    // from a different VM before acting (see WaitForFollowAutoGameEndAsync) - one screen's
    // word is not enough to leave on.
    LeaderMissingHere,

    // No leader signal available (none bound, or this pulse couldn't check) and the player
    // count dropped below the baseline: fall back to the original count-drop leave trigger.
    CountDropLeave
}

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
// Pulses round-robin across every online account: each VM is probed on a divided heartbeat, so
// the fleet notices a change several times faster than any one VM could. When a VM flags the
// leader missing, the host does not wait for the next scheduled pulse to confirm - it forces an
// immediate check on a *different* VM, and only leaves if that independent vantage agrees. This
// class covers the per-sample classification and the heartbeat math; the two-vantage
// confirmation itself lives in the host because it makes live agent calls.
public static class FollowAutoPulsePolicy
{
    // Floor for the divided heartbeat: below this the extra host<->agent chatter buys nothing
    // (an agent-side scan plus round-trip already costs a meaningful fraction of a second).
    public const int MinHeartbeatSeconds = 1;

    // Follow-auto polls at a fraction of the shared count-drop cadence because leaving promptly
    // when the leader goes matters more here than sparing the VMs a screen grab. The base
    // PlayerCountDropPollPolicy delay (6-15s) is meant for one vantage watching a private game;
    // dividing it speeds every VM up, and dividing again by the vantage count keeps each VM's
    // own load roughly constant while the fleet-wide sample rate multiplies. Bump the divisor
    // down toward 1 to make it gentler, up to make it more aggressive.
    public const int HeartbeatSpeedupDivisor = 2;

    // Single-vantage fallback only. With two or more VMs online a flagged absence is confirmed
    // instantly by a different VM, so this streak never applies; with exactly one VM there is no
    // independent screen to ask, so the lone vantage must miss the leader on two back-to-back
    // scans (SingleVantageRescanSeconds apart) before leaving.
    public const int LeaderGoneConfirmationSamples = 2;

    public const int SingleVantageRescanSeconds = 1;

    public static TimeSpan GetHeartbeat(TimeSpan baseDelay, int vantageCount)
    {
        var faster = TimeSpan.FromTicks(baseDelay.Ticks / HeartbeatSpeedupDivisor);
        var perVantage = vantageCount > 1
            ? TimeSpan.FromTicks(faster.Ticks / vantageCount)
            : faster;
        var floor = TimeSpan.FromSeconds(MinHeartbeatSeconds);
        return perVantage < floor ? floor : perVantage;
    }

    // Interprets a single pulse. Note LeaderMissingHere is a *flag*, not a leave: the host must
    // get an independent VM to agree before ending the game. A bound-but-unverifiable pulse
    // (leaderPresent == null: loading screen, capture failure, incomparable template) is not
    // evidence of absence, so it falls through to count-drop semantics rather than flagging.
    public static FollowAutoPulseAction Classify(bool leaderBound, bool? leaderPresent, int? playerCount, int? baseline)
    {
        if (leaderBound && leaderPresent is { } present)
        {
            return present ? FollowAutoPulseAction.RebaselineAndWait : FollowAutoPulseAction.LeaderMissingHere;
        }

        return playerCount is { } count && baseline is { } known && count < known
            ? FollowAutoPulseAction.CountDropLeave
            : FollowAutoPulseAction.Wait;
    }

    // Mid-join leader watch. The all-joined watch (Classify above) only starts once EVERY
    // online account has joined - so when one bot wedges (a stuck load screen) and the
    // operator moves on to the next game, the joined majority used to sit stranded in the
    // abandoned game with nobody watching: the wedged bot would eventually recover, join the
    // NEW game, complete the joined set, and only then would the watch notice the stale
    // vantages missing the leader - forcing everyone (including the correctly-joined
    // recovered bot) through a leave/rejoin churn. This classifies one probe of a JOINED
    // vantage taken between join attempts, so a leader departure aborts the stale game
    // promptly instead:
    // - locked nametag verified present -> stay
    // - verified absent AND another vantage independently confirmed gone -> leave now
    // - anything else -> stay and re-probe next cycle. Deliberately NO single-vantage
    //   miss-streak fallback here, unlike the all-joined watch: per-vantage nametag score
    //   variance is normal (one vantage locks at 0.65 while another scores the same name
    //   0.53), so a lone joined vantage that chronically under-scores the locked nametag
    //   plus null confirmations from still-in-menus bots would false-abort every couple of
    //   cycles - a leave/rejoin churn loop worse than the stranding this probe fixes. The
    //   confirm re-asks every vantage each cycle, so the abort fires the moment a second
    //   screen can actually verify; until then the joined-majority case (the one observed
    //   live) is covered, and a single stranded bot falls back to the old eventual recovery.
    public static bool ShouldAbortStaleMidJoinGame(bool? lockedPresent, bool? confirmAgreed)
    {
        return lockedPresent == false && confirmAgreed == true;
    }

    // Count-drop semantics compare against the highest player count actually observed, not just
    // the first seed: the seed pulse races the party still forming right after the last join
    // (and its cached-status fallback can serve a count from the PREVIOUS game), and a baseline
    // stuck below the real in-game count makes the leader's eventual departure read as "no
    // drop" forever - watch-follow-auto-20260717-105143.log sat for 90s+ on exactly that. Only
    // a verified-present leader may ever move the baseline DOWN (RebaselineAndWait); with no
    // leader signal the baseline only rises.
    public static int? RaiseCountBaseline(int? baseline, int? playerCount)
    {
        if (playerCount is not { } count)
        {
            return baseline;
        }

        return baseline is { } known && known >= count ? baseline : count;
    }

    // Cross-VM confirmation: the flagger already missed the leader, and these are every OTHER
    // vantage's answers about the same locked nametag. Any explicit "gone" wins - that is the
    // two-independent-screens agreement a leave requires - and a conflicting "still present"
    // from a third screen must not veto it: a vantage whose scene cross-matches the template
    // (or whose stored list diverged) would otherwise block every leave forever. "Present" is
    // only the answer when nobody verified absence; all-null means nobody could check, and a
    // null (mid-load, missing entry) never counts as gone.
    public static bool? CombineLeaderConfirmations(IReadOnlyList<bool?> answers)
    {
        var sawPresent = false;
        foreach (var answer in answers)
        {
            if (answer == false)
            {
                return false;
            }

            sawPresent |= answer == true;
        }

        return sawPresent ? true : null;
    }

    // One bound nametag's reading from a single pulse: whether that nametag was verifiably seen
    // (null = this pulse couldn't check it) and the best match score it reached.
    public readonly record struct LeaderNametagSignal(bool? Present, double Score);

    // Multi-alt bind-in-game: the operator can bind one nametag per alt, and the first game of a
    // follow-auto run decides which alt is actually being followed. Picks the verifiably-present
    // nametag with the highest match score; ties keep the earliest bind (rolodex order). Returns
    // null when nothing is verifiably present so the caller keeps waiting (count-drop semantics)
    // instead of locking onto a guess - a nametag must be SEEN before it can drive a leave.
    public static int? PickNametagLockIndex(IReadOnlyList<LeaderNametagSignal> signals)
    {
        int? bestIndex = null;
        var bestScore = double.MinValue;
        for (var i = 0; i < signals.Count; i++)
        {
            if (signals[i].Present == true && signals[i].Score > bestScore)
            {
                bestIndex = i;
                bestScore = signals[i].Score;
            }
        }

        return bestIndex;
    }
}
