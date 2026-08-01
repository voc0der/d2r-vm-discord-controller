using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// The ladder under test: five consecutive warmup failures power-cycle that account's own VM, and
// only an account that has already had its VM cycled in the same incident escalates to restarting
// the physical node. One wedged guest must not take its healthy siblings down with it.
public sealed class FollowWarmupFailureTrackerTests
{
    [Fact]
    public void FifthConsecutiveFailureRequestsAVmCycleNotANodeRestart()
    {
        var tracker = new FollowWarmupFailureTracker();

        for (var attempt = 1; attempt < FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            var result = tracker.RecordFailure("hc1", "server-a");
            Assert.Equal(attempt, result.ConsecutiveFailures);
            Assert.False(result.VmRecoveryRequested);
            Assert.False(result.RecoveryRequested);
        }

        var threshold = tracker.RecordFailure("hc1", "server-a");

        Assert.Equal(FollowWarmupFailureTracker.RecoveryThreshold, threshold.ConsecutiveFailures);
        Assert.True(threshold.VmRecoveryRequested);
        Assert.False(threshold.RecoveryRequested);
    }

    [Fact]
    public void FiveMoreFailuresAfterAVmCycleEscalateToTheNode()
    {
        var tracker = new FollowWarmupFailureTracker();
        CycleVm(tracker, "hc1", "server-a");

        for (var attempt = 1; attempt < FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            var result = tracker.RecordFailure("hc1", "server-a");
            Assert.Equal(attempt, result.ConsecutiveFailures);
            Assert.False(result.RecoveryRequested);
        }

        var escalation = tracker.RecordFailure("hc1", "server-a");
        var afterEscalation = tracker.RecordFailure("hc1", "server-a");

        Assert.True(escalation.RecoveryRequested);
        Assert.False(escalation.VmRecoveryRequested);
        Assert.False(afterEscalation.RecoveryRequested);
    }

    // A VM that could not be cycled (no Hyper-V mapping, worker offline, PowerShell refused) keeps
    // its strikes, so the very next failure escalates instead of retrying an impossible cycle.
    [Fact]
    public void AnUnavailableVmCycleEscalatesOnTheNextFailure()
    {
        var tracker = new FollowWarmupFailureTracker();
        for (var attempt = 1; attempt <= FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            tracker.RecordFailure("hc1", "server-a");
        }

        tracker.RecordVmRecoveryUnavailable("hc1", "server-a");

        Assert.True(tracker.RecordFailure("hc1", "server-a").RecoveryRequested);
    }

    [Fact]
    public void SuccessfulOrAlreadyReadyWarmupResetsThatAccountsStreakAndItsVmStage()
    {
        var tracker = new FollowWarmupFailureTracker();
        CycleVm(tracker, "hc1", "server-a");

        // The caller uses this same success path both for a successful menu_ready result and for
        // a live readiness preflight that proves menu_ready was unnecessary.
        tracker.RecordSuccess("hc1");

        for (var attempt = 1; attempt < FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            var result = tracker.RecordFailure("hc1", "server-a");
            Assert.Equal(attempt, result.ConsecutiveFailures);
            Assert.False(result.VmRecoveryRequested);
        }

        // A healthy client since the last cycle means the next incident starts at the VM stage
        // again rather than jumping straight to a node restart.
        Assert.True(tracker.RecordFailure("hc1", "server-a").VmRecoveryRequested);
    }

    [Fact]
    public void AccountsAccumulateFailuresIndependently()
    {
        var tracker = new FollowWarmupFailureTracker();

        for (var attempt = 1; attempt < FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            Assert.Equal(attempt, tracker.RecordFailure("hc1", "server-a").ConsecutiveFailures);
        }

        var firstFailureForAnotherAccount = tracker.RecordFailure("hc2", "server-b");

        Assert.Equal(1, firstFailureForAnotherAccount.ConsecutiveFailures);
        Assert.False(firstFailureForAnotherAccount.VmRecoveryRequested);
        Assert.True(tracker.RecordFailure("hc1", "server-a").VmRecoveryRequested);
    }

    // Two guests on one node each get their own power cycle - unlike the node restart, cycling one
    // VM does not touch the other, so there is nothing to coalesce.
    [Fact]
    public void TwoAccountsOnOneNodeEachGetTheirOwnVmCycle()
    {
        var tracker = new FollowWarmupFailureTracker();
        for (var attempt = 1; attempt <= FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            var first = tracker.RecordFailure("hc1", "server-a");
            var second = tracker.RecordFailure("hc2", "server-a");
            if (attempt == FollowWarmupFailureTracker.RecoveryThreshold)
            {
                Assert.True(first.VmRecoveryRequested);
                Assert.True(second.VmRecoveryRequested);
            }
        }
    }

    [Fact]
    public async Task SimultaneousThresholdFailuresOnOneNodeCoalesceToOneNodeRecoveryRequest()
    {
        var tracker = new FollowWarmupFailureTracker();
        CycleVm(tracker, "hc1", "server-a");
        CycleVm(tracker, "hc2", "server-a");
        for (var attempt = 1; attempt < FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            tracker.RecordFailure("hc1", "server-a");
            tracker.RecordFailure("hc2", "server-a");
        }

        using var gate = new ManualResetEventSlim(initialState: false);
        var first = Task.Run(() =>
        {
            gate.Wait();
            return tracker.RecordFailure("hc1", "server-a");
        });
        var second = Task.Run(() =>
        {
            gate.Wait();
            return tracker.RecordFailure("hc2", "server-a");
        });

        gate.Set();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result =>
            Assert.Equal(FollowWarmupFailureTracker.RecoveryThreshold, result.ConsecutiveFailures));
        Assert.Single(results, result => result.RecoveryRequested);
    }

    [Fact]
    public void ResetNodeClearsItsLatchAndEveryAccountStrikeButLeavesOtherNodesAlone()
    {
        var tracker = new FollowWarmupFailureTracker();
        CycleVm(tracker, "hc1", "server-a");
        for (var attempt = 1; attempt <= FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            tracker.RecordFailure("hc1", "server-a");
        }

        CycleVm(tracker, "hc3", "server-b");
        for (var attempt = 1; attempt < FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            tracker.RecordFailure("hc2", "server-a");
            tracker.RecordFailure("hc3", "server-b");
        }

        tracker.ResetNode("SERVER-A");

        Assert.Equal(1, tracker.RecordFailure("hc1", "server-a").ConsecutiveFailures);
        Assert.Equal(1, tracker.RecordFailure("hc2", "server-a").ConsecutiveFailures);
        Assert.True(tracker.RecordFailure("hc3", "server-b").RecoveryRequested);

        // Recovery cleared this node's history entirely, so hc1 starts the ladder over at the VM
        // stage instead of inheriting a spent one.
        for (var attempt = 2; attempt < FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            Assert.False(tracker.RecordFailure("hc1", "server-a").VmRecoveryRequested);
        }

        Assert.True(tracker.RecordFailure("hc1", "server-a").VmRecoveryRequested);
    }

    [Fact]
    public void AccountSuccessDoesNotClearAnOwningNodesRecoveryLatch()
    {
        var tracker = new FollowWarmupFailureTracker();
        CycleVm(tracker, "hc1", "server-a");
        for (var attempt = 1; attempt <= FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            tracker.RecordFailure("hc1", "server-a");
        }

        tracker.RecordSuccess("hc1");

        CycleVm(tracker, "hc2", "server-a");
        FollowWarmupFailureResult secondAccount = default;
        for (var attempt = 1; attempt <= FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            secondAccount = tracker.RecordFailure("hc2", "server-a");
        }

        Assert.Equal(FollowWarmupFailureTracker.RecoveryThreshold, secondAccount.ConsecutiveFailures);
        Assert.False(secondAccount.RecoveryRequested);
    }

    [Fact]
    public void MovingAnAccountToAnotherNodeStartsANewFailureIncident()
    {
        var tracker = new FollowWarmupFailureTracker();
        for (var attempt = 1; attempt < FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            tracker.RecordFailure("hc1", "server-a");
        }

        var moved = tracker.RecordFailure("HC1", " server-b ");

        Assert.Equal(1, moved.ConsecutiveFailures);
        Assert.False(moved.VmRecoveryRequested);
        Assert.False(moved.RecoveryRequested);
    }

    private static void CycleVm(FollowWarmupFailureTracker tracker, string accountKey, string nodeId)
    {
        for (var attempt = 1; attempt <= FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            tracker.RecordFailure(accountKey, nodeId);
        }

        tracker.RecordVmRecovered(accountKey, nodeId);
    }
}
