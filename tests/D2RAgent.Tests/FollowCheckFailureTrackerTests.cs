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
}
