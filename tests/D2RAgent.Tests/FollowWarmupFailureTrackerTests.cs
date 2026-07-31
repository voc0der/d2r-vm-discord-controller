using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FollowWarmupFailureTrackerTests
{
    [Fact]
    public void FifthConsecutiveFailureRequestsRecoveryExactlyOnce()
    {
        var tracker = new FollowWarmupFailureTracker();

        for (var attempt = 1; attempt < FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            var result = tracker.RecordFailure("hc1", "server-a");
            Assert.Equal(attempt, result.ConsecutiveFailures);
            Assert.False(result.RecoveryRequested);
        }

        var threshold = tracker.RecordFailure("hc1", "server-a");
        var afterThreshold = tracker.RecordFailure("hc1", "server-a");

        Assert.Equal(FollowWarmupFailureTracker.RecoveryThreshold, threshold.ConsecutiveFailures);
        Assert.True(threshold.RecoveryRequested);
        Assert.Equal(FollowWarmupFailureTracker.RecoveryThreshold + 1, afterThreshold.ConsecutiveFailures);
        Assert.False(afterThreshold.RecoveryRequested);
    }

    [Fact]
    public void SuccessfulOrAlreadyReadyWarmupResetsThatAccountsStreak()
    {
        var tracker = new FollowWarmupFailureTracker();
        for (var attempt = 1; attempt < FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            tracker.RecordFailure("hc1", "server-a");
        }

        // The caller uses this same success path both for a successful menu_ready result and for
        // a live readiness preflight that proves menu_ready was unnecessary.
        tracker.RecordSuccess("hc1");

        for (var attempt = 1; attempt < FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            var result = tracker.RecordFailure("hc1", "server-a");
            Assert.Equal(attempt, result.ConsecutiveFailures);
            Assert.False(result.RecoveryRequested);
        }

        Assert.True(tracker.RecordFailure("hc1", "server-a").RecoveryRequested);
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
        Assert.False(firstFailureForAnotherAccount.RecoveryRequested);
        Assert.True(tracker.RecordFailure("hc1", "server-a").RecoveryRequested);
    }

    [Fact]
    public async Task SimultaneousThresholdFailuresOnOneNodeCoalesceToOneRecoveryRequest()
    {
        var tracker = new FollowWarmupFailureTracker();
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
        for (var attempt = 1; attempt <= FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            tracker.RecordFailure("hc1", "server-a");
        }

        for (var attempt = 1; attempt < FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            tracker.RecordFailure("hc2", "server-a");
            tracker.RecordFailure("hc3", "server-b");
        }

        tracker.ResetNode("SERVER-A");

        Assert.Equal(1, tracker.RecordFailure("hc1", "server-a").ConsecutiveFailures);
        Assert.Equal(1, tracker.RecordFailure("hc2", "server-a").ConsecutiveFailures);
        Assert.True(tracker.RecordFailure("hc3", "server-b").RecoveryRequested);

        for (var attempt = 2; attempt < FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            Assert.False(tracker.RecordFailure("hc1", "server-a").RecoveryRequested);
        }

        Assert.True(tracker.RecordFailure("hc1", "server-a").RecoveryRequested);
    }

    [Fact]
    public void AccountSuccessDoesNotClearAnOwningNodesRecoveryLatch()
    {
        var tracker = new FollowWarmupFailureTracker();
        for (var attempt = 1; attempt <= FollowWarmupFailureTracker.RecoveryThreshold; attempt++)
        {
            tracker.RecordFailure("hc1", "server-a");
        }

        tracker.RecordSuccess("hc1");

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
        Assert.False(moved.RecoveryRequested);
    }
}
