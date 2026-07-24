using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

public sealed class MenuClickSafetyTests
{
    [Fact]
    public void InitialMenuPrepDoesNotEvaluateInGameSafetyGate()
    {
        var evaluated = false;

        var skip = VmOperations.ShouldSkipMenuClickForInGameSafety(
            guardAgainstInGame: false,
            mightAlreadyBeInGame: () =>
            {
                evaluated = true;
                return true;
            });

        Assert.False(skip);
        Assert.False(evaluated);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RecoveryMenuClicksHonorInGameSafetyGate(bool mightAlreadyBeInGame, bool expectedSkip)
    {
        var skip = VmOperations.ShouldSkipMenuClickForInGameSafety(
            guardAgainstInGame: true,
            mightAlreadyBeInGame: () => mightAlreadyBeInGame);

        Assert.Equal(expectedSkip, skip);
    }

    [Fact]
    public async Task FollowAutoUnexpectedGameTogglesLegacyThenRechecksThenLeaves()
    {
        var actions = new List<string>();

        var outcome = await VmOperations.RunFollowAutoInGameRecoveryAsync(
            detectBeforeToggle: () =>
            {
                actions.Add("detect-before");
                return true;
            },
            toggleLegacyGraphics: () => actions.Add("toggle-legacy"),
            waitForLegacyGraphics: _ =>
            {
                actions.Add("wait-for-legacy");
                return Task.CompletedTask;
            },
            detectAfterToggle: () =>
            {
                actions.Add("detect-after");
                return true;
            },
            leaveGame: _ =>
            {
                actions.Add("leave-game");
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(VmOperations.FollowAutoInGameRecoveryOutcome.LeftGame, outcome);
        Assert.Equal(
            ["detect-before", "toggle-legacy", "wait-for-legacy", "detect-after", "leave-game"],
            actions);
    }

    [Fact]
    public async Task FollowAutoUnexpectedGameDoesNotLeaveWhenFreshDetectionIsInconclusive()
    {
        var leaveAttempted = false;

        var outcome = await VmOperations.RunFollowAutoInGameRecoveryAsync(
            detectBeforeToggle: () => null,
            toggleLegacyGraphics: () => { },
            waitForLegacyGraphics: _ => Task.CompletedTask,
            detectAfterToggle: () => null,
            leaveGame: _ =>
            {
                leaveAttempted = true;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(VmOperations.FollowAutoInGameRecoveryOutcome.DetectionInconclusive, outcome);
        Assert.False(leaveAttempted);
    }

    [Fact]
    public async Task FollowAutoLobbyStateDoesNotToggleOrLeave()
    {
        var toggleAttempted = false;
        var leaveAttempted = false;

        var outcome = await VmOperations.RunFollowAutoInGameRecoveryAsync(
            detectBeforeToggle: () => false,
            toggleLegacyGraphics: () => toggleAttempted = true,
            waitForLegacyGraphics: _ => Task.CompletedTask,
            detectAfterToggle: () => throw new InvalidOperationException("A lobby state should not be rechecked."),
            leaveGame: _ =>
            {
                leaveAttempted = true;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(VmOperations.FollowAutoInGameRecoveryOutcome.NotInGame, outcome);
        Assert.False(toggleAttempted);
        Assert.False(leaveAttempted);
    }
}
