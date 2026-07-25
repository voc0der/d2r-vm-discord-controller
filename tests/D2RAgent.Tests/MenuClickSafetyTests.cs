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
    public async Task FollowAutoModernGameTogglesOnceThenRequiresLegacyBeforeLeaving()
    {
        var actions = new List<string>();

        var outcome = await VmOperations.RunFollowAutoInGameRecoveryAsync(
            detectBeforeNormalization: () =>
            {
                actions.Add("detect-before");
                return VmOperations.InGameHudMatchKind.ModernProfile;
            },
            toggleLegacyGraphics: () => actions.Add("toggle-legacy"),
            waitForLegacyGraphics: _ =>
            {
                actions.Add("wait-for-legacy");
                return Task.CompletedTask;
            },
            detectAfterNormalization: () =>
            {
                actions.Add("detect-after");
                return VmOperations.InGameHudMatchKind.LegacyProfile;
            },
            leaveGame: _ =>
            {
                actions.Add("leave-game");
                return Task.FromResult(true);
            },
            leaveOpenModernPauseMenu: _ => throw new InvalidOperationException(
                "The direct open-menu leave path should not run for a normal modern HUD."),
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
            detectBeforeNormalization: () => null,
            toggleLegacyGraphics: () => { },
            waitForLegacyGraphics: _ => Task.CompletedTask,
            detectAfterNormalization: () => null,
            leaveGame: _ =>
            {
                leaveAttempted = true;
                return Task.FromResult(true);
            },
            leaveOpenModernPauseMenu: _ => throw new InvalidOperationException(
                "An inconclusive HUD must not authorize an open-menu click."),
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
            detectBeforeNormalization: () => VmOperations.InGameHudMatchKind.None,
            toggleLegacyGraphics: () => toggleAttempted = true,
            waitForLegacyGraphics: _ => Task.CompletedTask,
            detectAfterNormalization: () => throw new InvalidOperationException("A lobby state should not be rechecked."),
            leaveGame: _ =>
            {
                leaveAttempted = true;
                return Task.FromResult(true);
            },
            leaveOpenModernPauseMenu: _ => throw new InvalidOperationException(
                "A lobby state must not authorize an open-menu click."),
            CancellationToken.None);

        Assert.Equal(VmOperations.FollowAutoInGameRecoveryOutcome.NotInGame, outcome);
        Assert.False(toggleAttempted);
        Assert.False(leaveAttempted);
    }

    [Fact]
    public async Task FollowAutoLegacyGameDoesNotToggleButFreshlyRechecksBeforeLeaving()
    {
        var actions = new List<string>();

        var outcome = await VmOperations.RunFollowAutoInGameRecoveryAsync(
            detectBeforeNormalization: () =>
            {
                actions.Add("detect-before");
                return VmOperations.InGameHudMatchKind.LegacyProfile;
            },
            toggleLegacyGraphics: () => actions.Add("toggle-legacy"),
            waitForLegacyGraphics: _ =>
            {
                actions.Add("wait-for-legacy");
                return Task.CompletedTask;
            },
            detectAfterNormalization: () =>
            {
                actions.Add("detect-after");
                return VmOperations.InGameHudMatchKind.LegacyProfile;
            },
            leaveGame: _ =>
            {
                actions.Add("leave-game");
                return Task.FromResult(true);
            },
            leaveOpenModernPauseMenu: _ => throw new InvalidOperationException(
                "The direct open-menu leave path should not run for a legacy HUD."),
            CancellationToken.None);

        Assert.Equal(VmOperations.FollowAutoInGameRecoveryOutcome.LeftGame, outcome);
        Assert.Equal(["detect-before", "detect-after", "leave-game"], actions);
    }

    [Theory]
    [InlineData((int)VmOperations.InGameHudMatchKind.ModernProfile)]
    [InlineData((int)VmOperations.InGameHudMatchKind.Frame)]
    [InlineData((int)VmOperations.InGameHudMatchKind.ModernSaveAndExitMenu)]
    public async Task FollowAutoDoesNotUseOrdinarySaveAndExitWithoutFreshLegacyProfile(
        int afterNormalizationValue)
    {
        var leaveAttempted = false;
        var afterNormalization = (VmOperations.InGameHudMatchKind)afterNormalizationValue;

        var outcome = await VmOperations.RunFollowAutoInGameRecoveryAsync(
            detectBeforeNormalization: () => VmOperations.InGameHudMatchKind.ModernProfile,
            toggleLegacyGraphics: () => { },
            waitForLegacyGraphics: _ => Task.CompletedTask,
            detectAfterNormalization: () => afterNormalization,
            leaveGame: _ =>
            {
                leaveAttempted = true;
                return Task.FromResult(true);
            },
            leaveOpenModernPauseMenu: _ => throw new InvalidOperationException(
                "Only an initially confirmed open pause menu authorizes its direct click path."),
            CancellationToken.None);

        Assert.Equal(VmOperations.FollowAutoInGameRecoveryOutcome.DetectionInconclusive, outcome);
        Assert.False(leaveAttempted);
    }

    [Fact]
    public async Task FollowAutoOpenModernPauseMenuClicksDirectlyWithoutTogglingOrPressingEscapeFlow()
    {
        var actions = new List<string>();

        var outcome = await VmOperations.RunFollowAutoInGameRecoveryAsync(
            detectBeforeNormalization: () =>
            {
                actions.Add("detect-before");
                return VmOperations.InGameHudMatchKind.ModernSaveAndExitMenu;
            },
            toggleLegacyGraphics: () => actions.Add("toggle-legacy"),
            waitForLegacyGraphics: _ =>
            {
                actions.Add("wait-for-legacy");
                return Task.CompletedTask;
            },
            detectAfterNormalization: () =>
            {
                actions.Add("detect-after");
                return VmOperations.InGameHudMatchKind.LegacyProfile;
            },
            leaveGame: _ =>
            {
                actions.Add("ordinary-leave");
                return Task.FromResult(true);
            },
            leaveOpenModernPauseMenu: _ =>
            {
                actions.Add("direct-open-menu-leave");
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(VmOperations.FollowAutoInGameRecoveryOutcome.LeftGame, outcome);
        Assert.Equal(["detect-before", "direct-open-menu-leave"], actions);
    }

    [Fact]
    public async Task FollowAutoFreshNoHudResultPreservesNotInGameOutcome()
    {
        var leaveAttempted = false;

        var outcome = await VmOperations.RunFollowAutoInGameRecoveryAsync(
            detectBeforeNormalization: () => VmOperations.InGameHudMatchKind.ModernProfile,
            toggleLegacyGraphics: () => { },
            waitForLegacyGraphics: _ => Task.CompletedTask,
            detectAfterNormalization: () => VmOperations.InGameHudMatchKind.None,
            leaveGame: _ =>
            {
                leaveAttempted = true;
                return Task.FromResult(true);
            },
            leaveOpenModernPauseMenu: _ => Task.FromResult(true),
            CancellationToken.None);

        Assert.Equal(VmOperations.FollowAutoInGameRecoveryOutcome.NotInGame, outcome);
        Assert.False(leaveAttempted);
    }
}
