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

    [Fact]
    public void GameFullParksOnlyAfterTheConfiguredAttemptsAndFreesTheWatch()
    {
        var state = JoinedFleet("hc1", "hc2", "hc3");
        var online = AccountSet("hc1", "hc2", "hc3", "hc4");

        for (var attempt = 1; attempt < FollowAutoAccountState.MaxGameFullAttempts; attempt++)
        {
            var (reportedAttempt, parked) = state.RecordGameFullStrike("hc4");
            Assert.Equal(attempt, reportedAttempt);
            Assert.False(parked);
            // Still allowed to retry: not parked, so the pending scan keeps including it and
            // the all-joined watch stays blocked on it.
            Assert.Empty(state.ParkedGameFull);
            Assert.False(state.CanWatch(online));
        }

        var (finalAttempt, parkedNow) = state.RecordGameFullStrike("hc4");

        Assert.Equal(FollowAutoAccountState.MaxGameFullAttempts, finalAttempt);
        Assert.True(parkedNow);
        Assert.Contains("hc4", state.ParkedGameFull);
        // Parking must not freeze the fleet: the three joined accounts form a watchable game.
        Assert.True(state.CanWatch(online));
        // Parked accounts remain expected, so the monitor keeps showing 3/4.
        Assert.Equal(4, state.CountExpectedAccounts(online));
    }

    [Fact]
    public void GameFullParkingSupersedesRecoveryPendingSoTheWatchIsNotHeldHostage()
    {
        var state = JoinedFleet("hc1", "hc2", "hc3", "hc4");
        state.BeginRecovery("hc4");

        for (var attempt = 1; attempt <= FollowAutoAccountState.MaxGameFullAttempts; attempt++)
        {
            state.RecordGameFullStrike("hc4");
        }

        Assert.Empty(state.RecoveryPending);
        Assert.Contains("hc4", state.ParkedGameFull);
        Assert.True(state.CanWatch(AccountSet("hc1", "hc2", "hc3", "hc4")));
    }

    [Fact]
    public void GameAdvancingUnparksEveryoneWithAFreshRetryBudget()
    {
        var state = JoinedFleet("hc1", "hc2");
        for (var attempt = 1; attempt <= FollowAutoAccountState.MaxGameFullAttempts; attempt++)
        {
            state.RecordGameFullStrike("hc4");
        }

        var unparked = state.ClearGameFullParking();

        Assert.Equal(new[] { "hc4" }, unparked);
        Assert.Empty(state.ParkedGameFull);
        // The next game is a fresh capacity situation: strike count restarts at 1.
        var (attemptAfterClear, parkedAfterClear) = state.RecordGameFullStrike("hc4");
        Assert.Equal(1, attemptAfterClear);
        Assert.False(parkedAfterClear);
    }

    [Fact]
    public void JoiningResetsStrikesButExplicitRecoveryKeepsThemSoAFullGameReparksImmediately()
    {
        var state = new FollowAutoAccountState();
        for (var attempt = 1; attempt <= FollowAutoAccountState.MaxGameFullAttempts; attempt++)
        {
            state.RecordGameFullStrike("hc4");
        }

        // Recovery pulls the account back into the join path, but its strikes survive: if the
        // game still reads full on the very next attempt it re-parks instead of getting a
        // fresh retry budget against the same full game.
        state.BeginRecovery("hc4");
        Assert.Empty(state.ParkedGameFull);
        var (_, reparked) = state.RecordGameFullStrike("hc4");
        Assert.True(reparked);

        // Actually joining is the real reset: the account got in, so nothing is full anymore.
        state.MarkJoined("hc4");
        Assert.Empty(state.ParkedGameFull);
        var (attemptAfterJoin, parkedAfterJoin) = state.RecordGameFullStrike("hc4");
        Assert.Equal(1, attemptAfterJoin);
        Assert.False(parkedAfterJoin);
    }

    [Fact]
    public void EveryOnlineAccountParkedMeansNoWatchableGame()
    {
        var state = new FollowAutoAccountState();
        for (var attempt = 1; attempt <= FollowAutoAccountState.MaxGameFullAttempts; attempt++)
        {
            state.RecordGameFullStrike("hc1");
            state.RecordGameFullStrike("hc2");
        }

        Assert.Equal(2, state.ParkedGameFullCount);
        Assert.False(state.CanWatch(AccountSet("hc1", "hc2")));
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
