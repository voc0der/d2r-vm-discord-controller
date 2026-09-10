using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// A bot that disconnects mid-session and never rejoins is the exact shape this ladder exists for:
// menu_ready keeps succeeding (so the warmup ladder correctly stays at zero and never acts) while
// menu_follow_auto_check keeps failing. Before this tracker nothing counted those failures, and
// the host reported "checks could not complete" every ~3.5 minutes for an entire session without
// ever attempting a recovery.
public sealed class FollowCheckFailureTrackerTests
{
    [Fact]
    public void OneOrTwoFailuresDoNotEscalate()
    {
        var tracker = new FollowCheckFailureTracker();

        var first = tracker.RecordFailure("hc3");
        var second = tracker.RecordFailure("hc3");

        Assert.Equal(1, first.ConsecutiveFailures);
        Assert.Equal(2, second.ConsecutiveFailures);
        Assert.False(first.ClientRestartRequested);
        Assert.False(second.ClientRestartRequested);
        Assert.False(first.VmRecoveryRequested);
        Assert.False(second.VmRecoveryRequested);
    }

    [Fact]
    public void TheThirdConsecutiveFailureRestartsTheClient()
    {
        var tracker = new FollowCheckFailureTracker();

        tracker.RecordFailure("hc3");
        tracker.RecordFailure("hc3");
        var third = tracker.RecordFailure("hc3");

        Assert.True(third.ClientRestartRequested);
        Assert.False(third.VmRecoveryRequested);
        Assert.Equal(3, third.TotalFailures);
    }

    // The whole point of the ladder: it must keep climbing. A client that fails, gets restarted,
    // and fails again is not allowed to loop on restarts forever.
    [Fact]
    public void RepeatedStreaksClimbFromClientRestartsToAVmPowerCycle()
    {
        var tracker = new FollowCheckFailureTracker();
        var restarts = 0;
        var vmCycles = 0;

        for (var failure = 0; failure < 9; failure++)
        {
            var result = tracker.RecordFailure("hc3");
            if (result.ClientRestartRequested)
            {
                restarts++;
            }

            if (result.VmRecoveryRequested)
            {
                vmCycles++;
            }
        }

        Assert.Equal(FollowCheckFailureTracker.MaxClientRestarts, restarts);
        Assert.Equal(1, vmCycles);
    }

    // Latched like the warmup ladder's node request: the recovery coordinator owns the account
    // once a power cycle is in flight, and stacking cycles on a guest that is already rebuilding
    // would make things worse, not better.
    [Fact]
    public void TheVmRequestIsNotRepeatedWhileRecoveryIsAlreadyInFlight()
    {
        var tracker = new FollowCheckFailureTracker();
        for (var failure = 0; failure < 9; failure++)
        {
            tracker.RecordFailure("hc3");
        }

        var vmCyclesAfterEscalation = 0;
        for (var failure = 0; failure < 12; failure++)
        {
            if (tracker.RecordFailure("hc3").VmRecoveryRequested)
            {
                vmCyclesAfterEscalation++;
            }
        }

        Assert.Equal(0, vmCyclesAfterEscalation);
    }

    [Fact]
    public void ARecoveredVmGivesTheAccountAFreshLadder()
    {
        var tracker = new FollowCheckFailureTracker();
        for (var failure = 0; failure < 9; failure++)
        {
            tracker.RecordFailure("hc3");
        }

        tracker.RecordVmRecovered("hc3");

        tracker.RecordFailure("hc3");
        tracker.RecordFailure("hc3");
        var third = tracker.RecordFailure("hc3");

        Assert.True(third.ClientRestartRequested);
    }

    [Fact]
    public void AUsableCheckOutcomeClearsTheStreak()
    {
        var tracker = new FollowCheckFailureTracker();
        tracker.RecordFailure("hc3");
        tracker.RecordFailure("hc3");

        tracker.RecordSuccess("hc3");

        Assert.Equal(0, tracker.GetConsecutiveFailures("hc3"));
        Assert.False(tracker.RecordFailure("hc3").ClientRestartRequested);
    }

    // The counter is consecutive on purpose, but the escalation must still be reachable for a
    // client that alternates. Two failures, one success, two failures is genuinely healthy-ish
    // churn and must not restart anything.
    [Fact]
    public void AnIntermittentClientDoesNotEscalateOnScatteredFailures()
    {
        var tracker = new FollowCheckFailureTracker();

        for (var cycle = 0; cycle < 6; cycle++)
        {
            Assert.False(tracker.RecordFailure("hc3").ClientRestartRequested);
            Assert.False(tracker.RecordFailure("hc3").ClientRestartRequested);
            tracker.RecordSuccess("hc3");
        }
    }

    [Fact]
    public void AccountsEscalateIndependently()
    {
        var tracker = new FollowCheckFailureTracker();

        tracker.RecordFailure("hc3");
        tracker.RecordFailure("hc3");
        tracker.RecordFailure("hc1");

        Assert.True(tracker.RecordFailure("hc3").ClientRestartRequested);
        Assert.Equal(1, tracker.GetConsecutiveFailures("hc1"));
    }

    [Fact]
    public void TotalFailuresKeepCountingAcrossStreaksForTheOperatorMessage()
    {
        var tracker = new FollowCheckFailureTracker();

        FollowCheckFailureResult? last = null;
        for (var failure = 0; failure < 7; failure++)
        {
            last = tracker.RecordFailure("hc3");
        }

        Assert.Equal(7, last!.TotalFailures);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyAccountKeyIsRejected(string accountKey)
    {
        var tracker = new FollowCheckFailureTracker();

        Assert.Throws<ArgumentException>(() => tracker.RecordFailure(accountKey));
    }

    // The stall this ladder now covers: hc6_alt answered ok=true every cycle with "suspected this
    // pending client was already in a game, but a strict in-game HUD profile could not be
    // confirmed; waiting without clicking" while sitting visibly at the lobby. Every one of those
    // answers cleared the ladder, so nothing ever escalated and the fleet ran a bot short - the
    // leader's game held six players against a target of seven for the rest of the session.
    [Fact]
    public void AStallThatKeepsAnsweringOkStillClimbsTheLadder()
    {
        var tracker = new FollowCheckFailureTracker();
        var start = DateTimeOffset.UtcNow;

        Assert.False(tracker.RecordStall("hc6_alt", start).ClientRestartRequested);
        Assert.False(tracker.RecordStall("hc6_alt", start + TimeSpan.FromSeconds(5)).ClientRestartRequested);

        var escalated = tracker.RecordStall(
            "hc6_alt",
            start + FollowCheckFailureTracker.StallEscalationWindow);

        Assert.True(escalated.ClientRestartRequested);
    }

    // The check cycle is five seconds and a stall answers almost instantly, so the count alone
    // would restart a client fifteen seconds after one bad sample. Both halves have to be met.
    [Fact]
    public void ABurstOfStallsInsideTheWindowIsNotYetStuck()
    {
        var tracker = new FollowCheckFailureTracker();
        var start = DateTimeOffset.UtcNow;

        for (var cycle = 0; cycle < 12; cycle++)
        {
            var result = tracker.RecordStall("hc6_alt", start + TimeSpan.FromSeconds(5 * cycle));
            Assert.False(result.ClientRestartRequested);
            Assert.False(result.VmRecoveryRequested);
        }
    }

    // A stall that resolves itself must leave no residue: the window restarts from the next one.
    [Fact]
    public void ProgressClearsTheStallStreak()
    {
        var tracker = new FollowCheckFailureTracker();
        var start = DateTimeOffset.UtcNow;

        tracker.RecordStall("hc6_alt", start);
        tracker.RecordStall("hc6_alt", start + TimeSpan.FromSeconds(5));
        tracker.RecordSuccess("hc6_alt");

        Assert.Equal(0, tracker.GetConsecutiveStalls("hc6_alt"));
        Assert.False(tracker
            .RecordStall("hc6_alt", start + FollowCheckFailureTracker.StallEscalationWindow)
            .ClientRestartRequested);
    }

    // Stalls and failures are the same question - is this client ever going to make progress? - so
    // they share the rungs. A client that alternates must not collect two recovery budgets.
    [Fact]
    public void StallsAndFailuresShareTheSameRungs()
    {
        var tracker = new FollowCheckFailureTracker();
        var start = DateTimeOffset.UtcNow;
        var restarts = 0;
        var vmCycles = 0;

        for (var round = 0; round < 4; round++)
        {
            var at = start + TimeSpan.FromMinutes(4 * round);
            tracker.RecordStall("hc6_alt", at);
            tracker.RecordStall("hc6_alt", at + TimeSpan.FromSeconds(5));
            var stalled = tracker.RecordStall("hc6_alt", at + FollowCheckFailureTracker.StallEscalationWindow);
            if (stalled.ClientRestartRequested)
            {
                restarts++;
            }

            if (stalled.VmRecoveryRequested)
            {
                vmCycles++;
            }
        }

        Assert.Equal(FollowCheckFailureTracker.MaxClientRestarts, restarts);
        Assert.Equal(1, vmCycles);
    }

    [Fact]
    public void AVmRecoveryClearsTheStallStreakToo()
    {
        var tracker = new FollowCheckFailureTracker();
        var start = DateTimeOffset.UtcNow;

        tracker.RecordStall("hc6_alt", start);
        tracker.RecordStall("hc6_alt", start + TimeSpan.FromSeconds(5));
        tracker.RecordVmRecovered("hc6_alt");

        Assert.Equal(0, tracker.GetConsecutiveStalls("hc6_alt"));
    }
}
