using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// The retry loop in ClickFriendJoinOptionUntilEnteredGameAsync renews its own deadline on every
// reselect. Nothing capped that, so a client caught in an error-dialog or connection-interrupt
// storm ran until the 205s agent-side command timeout killed menu_follow_auto_check outright -
// replacing a diagnosable "still waiting, here is why" with a bare ok=false the host could not
// act on. Every limit here exists to make sure the loop always returns its own answer first.
public sealed class FriendJoinRetryBudgetTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheBudgetMustExpireWellInsideTheAgentSideCommandTimeout()
    {
        // The host sends menu_follow_auto_check with a 210s timeout and AgentRegistry subtracts
        // 5s, so the agent kills the command at 205s. Anything at or above that reintroduces the
        // exact failure this class exists to prevent.
        Assert.True(FriendJoinRetryBudget.OverallBudget < TimeSpan.FromSeconds(205));
    }

    [Fact]
    public void ARenewedDeadlineIsClampedToTheOverallBudget()
    {
        var budget = new FriendJoinRetryBudget(Start);

        var clamped = budget.ClampDeadline(Start + TimeSpan.FromSeconds(600));

        Assert.Equal(budget.HardDeadlineUtc, clamped);
    }

    [Fact]
    public void ADeadlineInsideTheBudgetIsLeftAlone()
    {
        var budget = new FriendJoinRetryBudget(Start);
        var requested = Start + TimeSpan.FromSeconds(30);

        Assert.Equal(requested, budget.ClampDeadline(requested));
    }

    [Fact]
    public void ErrorDialogsAreRetriedUpToTheCapAndThenRefused()
    {
        var budget = new FriendJoinRetryBudget(Start);

        for (var attempt = 0; attempt < FriendJoinRetryBudget.MaxDialogRetries; attempt++)
        {
            Assert.True(budget.TryConsume(FriendJoinRetryReason.ErrorDialog, Start, out _));
        }

        Assert.False(budget.TryConsume(FriendJoinRetryReason.ErrorDialog, Start, out var reason));
        Assert.Contains("error dialogs", reason);
    }

    [Fact]
    public void ConnectionInterruptionsAreRetriedUpToTheCapAndThenRefused()
    {
        var budget = new FriendJoinRetryBudget(Start);

        for (var attempt = 0; attempt < FriendJoinRetryBudget.MaxConnectionRetries; attempt++)
        {
            Assert.True(budget.TryConsume(FriendJoinRetryReason.ConnectionInterrupted, Start, out _));
        }

        Assert.False(budget.TryConsume(FriendJoinRetryReason.ConnectionInterrupted, Start, out var reason));
        Assert.Contains("connection was interrupted", reason);
    }

    // Menu and character-screen returns have no cap of their own, so without the overall cap they
    // were an unbounded loop with no ceiling at all.
    [Fact]
    public void UncappedReasonsAreStillBoundedByTheTotalRetryCap()
    {
        var budget = new FriendJoinRetryBudget(Start);

        for (var attempt = 0; attempt < FriendJoinRetryBudget.MaxTotalRetries; attempt++)
        {
            Assert.True(budget.TryConsume(FriendJoinRetryReason.ReturnedToMenu, Start, out _));
        }

        Assert.False(budget.TryConsume(FriendJoinRetryReason.ReturnedToMenu, Start, out var reason));
        Assert.Contains("reselected the friend game", reason);
    }

    // A mixture of causes must not buy a bigger budget than any single cause would.
    [Fact]
    public void MixedReasonsShareTheTotalCap()
    {
        var budget = new FriendJoinRetryBudget(Start);
        var reasons = new[]
        {
            FriendJoinRetryReason.ErrorDialog,
            FriendJoinRetryReason.ConnectionInterrupted,
            FriendJoinRetryReason.ReturnedToMenu,
            FriendJoinRetryReason.ReturnedToCharacterScreen,
            FriendJoinRetryReason.OfflineCharacterScreen,
            FriendJoinRetryReason.ReturnedToMenu
        };

        foreach (var reason in reasons)
        {
            Assert.True(budget.TryConsume(reason, Start, out _));
        }

        Assert.False(budget.TryConsume(FriendJoinRetryReason.ReturnedToMenu, Start, out _));
    }

    // Even a single slow attempt must not outlive the budget: the wall clock is checked
    // independently of how few retries were actually spent.
    [Fact]
    public void ASlowAttemptIsRefusedOnTheWallClockEvenWithRetriesLeft()
    {
        var budget = new FriendJoinRetryBudget(Start);

        var refused = budget.TryConsume(
            FriendJoinRetryReason.ErrorDialog,
            Start + FriendJoinRetryBudget.OverallBudget,
            out var reason);

        Assert.False(refused);
        Assert.Equal(1, budget.DialogRetries);
        Assert.Contains("game-entry budget", reason);
    }

    // A gameEntryStartTimeoutSeconds larger than the budget is clamped rather than honored. That
    // is deliberate: such a config could never have run to completion anyway, because the agent
    // kills the whole command at 205s. Clamping trades an unreachable per-attempt window for a
    // reported result.
    [Fact]
    public void AConfiguredTimeoutLargerThanTheBudgetIsClamped()
    {
        var budget = new FriendJoinRetryBudget(Start);

        var clamped = budget.ClampDeadline(Start + TimeSpan.FromSeconds(300));

        Assert.Equal(Start + FriendJoinRetryBudget.OverallBudget, clamped);
    }

    [Fact]
    public void IsExhaustedReportsTheWallClockCeiling()
    {
        var budget = new FriendJoinRetryBudget(Start);

        Assert.False(budget.IsExhausted(Start));
        Assert.True(budget.IsExhausted(Start + FriendJoinRetryBudget.OverallBudget));
    }

    // The refusal message is what the operator reads on the monitor, so the count it names has to
    // be the attempt that actually hit the wall, not the last one that was allowed.
    [Fact]
    public void TheRefusalNamesTheAttemptThatHitTheCap()
    {
        var budget = new FriendJoinRetryBudget(Start);
        for (var attempt = 0; attempt < FriendJoinRetryBudget.MaxDialogRetries; attempt++)
        {
            budget.TryConsume(FriendJoinRetryReason.ErrorDialog, Start, out _);
        }

        budget.TryConsume(FriendJoinRetryReason.ErrorDialog, Start, out var reason);

        Assert.Contains((FriendJoinRetryBudget.MaxDialogRetries + 1).ToString(), reason);
    }
}
