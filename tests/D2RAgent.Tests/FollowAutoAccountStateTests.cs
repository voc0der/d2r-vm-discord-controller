using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FollowAutoAccountStateTests
{
    [Fact]
    public void IsolatedAccountRemainsPendingWhileOfflineUntilItActuallyRejoins()
    {
        var state = JoinedFleet("hc1", "hc2", "hc3", "hc4");

        state.BeginRecovery("hc4");

        Assert.Equal(3, state.JoinedCount);
        Assert.Contains("hc4", state.RecoveryPending);
        Assert.False(state.CanWatch(AccountSet("hc1", "hc2", "hc3")));
        Assert.Equal(new[] { "hc4" }, state.GetOfflineRecoveryAccounts(AccountSet("hc1", "hc2", "hc3")));
        Assert.Equal(4, state.CountExpectedAccounts(new[] { "hc1", "hc2", "hc3" }));

        Assert.False(state.CanWatch(AccountSet("hc1", "hc2", "hc3", "hc4")));
        state.MarkJoined("hc4");

        Assert.Empty(state.RecoveryPending);
        Assert.True(state.CanWatch(AccountSet("hc1", "hc2", "hc3", "hc4")));
    }

    [Fact]
    public void StaleCleanupMovesEveryIntendedAccountToRecoveryBeforeLeaveResultsExist()
    {
        var state = JoinedFleet("hc1", "hc2", "hc3", "hc4");

        state.BeginRecovery(["hc1", "hc2", "hc3", "hc4"]);

        Assert.Equal(0, state.JoinedCount);
        Assert.True(AccountSet("hc1", "hc2", "hc3", "hc4").SetEquals(state.RecoveryPending));
        Assert.False(state.CanWatch(AccountSet("hc1", "hc2", "hc3")));
        Assert.Equal(new[] { "hc4" }, state.GetOfflineRecoveryAccounts(AccountSet("hc1", "hc2", "hc3")));
    }

    [Fact]
    public void AnotherAccountJoiningDoesNotClearTheAccountThatIsRecovering()
    {
        var state = JoinedFleet("hc1", "hc2", "hc3", "hc4");

        state.BeginRecovery("hc4");
        state.MarkJoined("hc3");

        Assert.Contains("hc4", state.RecoveryPending);
        Assert.False(state.CanWatch(AccountSet("hc1", "hc2", "hc3", "hc4")));

        state.MarkJoined("HC4");

        Assert.Empty(state.RecoveryPending);
        Assert.True(state.CanWatch(AccountSet("hc1", "hc2", "hc3", "hc4")));
    }

    [Fact]
    public void JoinedAccountDisconnectMovesThatExactAccountToRecovery()
    {
        var state = JoinedFleet("hc1", "hc2", "hc3", "hc4");

        var disconnected = state.BeginRecoveryForOfflineJoined(AccountSet("hc1", "hc2", "hc3"));

        Assert.Equal(new[] { "hc4" }, disconnected);
        Assert.Equal(3, state.JoinedCount);
        Assert.Contains("hc4", state.RecoveryPending);
        Assert.False(state.CanWatch(AccountSet("hc1", "hc2", "hc3")));

        state.MarkJoined("hc4");

        Assert.Empty(state.RecoveryPending);
        Assert.True(state.CanWatch(AccountSet("hc1", "hc2", "hc3", "hc4")));
    }

    private static FollowAutoAccountState JoinedFleet(params string[] accountKeys)
    {
        var state = new FollowAutoAccountState();
        foreach (var accountKey in accountKeys)
        {
            state.MarkJoined(accountKey);
        }

        return state;
    }

    private static HashSet<string> AccountSet(params string[] accountKeys)
    {
        return accountKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
