using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

// Lowering the bot count takes a client out of the leader's game with Save and Exit. The failure
// case is the interesting one: dropping a client that did not actually leave means a bot sits in
// the game that the run has stopped tracking, and nothing will ever take it out - but keeping it
// tracked forever is worse, because the all-joined watch requires the joined set to match the
// active roster exactly, so one wedged client would stall the whole run.
public sealed class FollowAutoBenchLeaveTests
{
    [Fact]
    public void AConfirmedLeaveIsForgottenImmediately()
    {
        var state = new FollowAutoAccountState();
        state.MarkJoined("hc6");

        state.Bench(Set("hc6"));

        Assert.DoesNotContain("hc6", state.Joined);
    }

    [Fact]
    public void AFailedLeaveIsRetriedBeforeBeingGivenUpOn()
    {
        var state = new FollowAutoAccountState();
        state.MarkJoined("hc6");

        // Two failures still ask for a retry; the third gives up so the run can move on.
        Assert.True(state.ShouldRetryBenchLeave("hc6"));
        Assert.True(state.ShouldRetryBenchLeave("hc6"));
        Assert.False(state.ShouldRetryBenchLeave("hc6"));
    }

    [Fact]
    public void RetriesAreCountedPerAccount()
    {
        var state = new FollowAutoAccountState();

        Assert.True(state.ShouldRetryBenchLeave("hc6"));
        Assert.True(state.ShouldRetryBenchLeave("hc6"));
        Assert.True(state.ShouldRetryBenchLeave("hc7"));
        Assert.False(state.ShouldRetryBenchLeave("hc6"));
        Assert.True(state.ShouldRetryBenchLeave("hc7"));
    }

    // A client that leaves on a later attempt must not carry its strikes into the next time it is
    // benched, hours and several games later.
    [Fact]
    public void ASuccessfulLeaveClearsTheRetryCount()
    {
        var state = new FollowAutoAccountState();
        Assert.True(state.ShouldRetryBenchLeave("hc6"));
        Assert.True(state.ShouldRetryBenchLeave("hc6"));

        state.Bench(Set("hc6"));

        Assert.True(state.ShouldRetryBenchLeave("hc6"));
        Assert.True(state.ShouldRetryBenchLeave("hc6"));
    }

    // The watch runs only when joined equals the active roster. A benched client still tracked as
    // joined therefore holds it - which is exactly why the retry above is bounded.
    [Fact]
    public void AStillTrackedBenchedClientHoldsTheWatchUntilItIsReleased()
    {
        var state = new FollowAutoAccountState();
        state.MarkJoined("hc1");
        state.MarkJoined("hc2");
        state.MarkJoined("hc6");
        var activeRoster = new[] { "hc1", "hc2" };

        Assert.False(state.CanWatch(activeRoster));

        state.Bench(Set("hc6"));

        Assert.True(state.CanWatch(activeRoster));
    }

    // Benched accounts that never made it into the game are dropped straight away. A benched
    // account left recovery-pending would block the watch with nothing left to drive its recovery.
    [Fact]
    public void BenchingClearsRecoveryAndParkingState()
    {
        var state = new FollowAutoAccountState();
        state.BeginRecovery("hc6");
        state.MarkJoined("hc7");
        state.RecordGameFullStrike("hc7");

        state.Bench(Set("hc6", "hc7"));

        Assert.DoesNotContain("hc6", state.RecoveryPending);
        Assert.DoesNotContain("hc7", state.Joined);
        Assert.DoesNotContain("hc7", state.ParkedGameFull);
        Assert.True(state.CanWatch(["hc1"]) == false || state.RecoveryPending.Count == 0);
    }

    private static IReadOnlySet<string> Set(params string[] accountKeys)
    {
        return accountKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
