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

    // Reign of the Warlock removed legacy graphics, and with it the normalization step that
    // used to press G and re-detect before authorizing an exit. The safety property that step
    // protected is asserted directly here instead: only a STRICT globe match (HudProfile) or a
    // positively identified pause menu may lead to a click.
    [Fact]
    public async Task FollowAutoInGameHudLeavesViaSaveAndExit()
    {
        var actions = new List<string>();

        var outcome = await VmOperations.RunFollowAutoInGameRecoveryAsync(
            detectInGameMatch: () =>
            {
                actions.Add("detect");
                return VmOperations.InGameHudMatchKind.HudProfile;
            },
            leaveGame: _ =>
            {
                actions.Add("leave-game");
                return Task.FromResult(true);
            },
            leaveOpenPauseMenu: _ => throw new InvalidOperationException(
                "The direct open-menu leave path should not run for an ordinary in-game HUD."),
            CancellationToken.None);

        Assert.Equal(VmOperations.FollowAutoInGameRecoveryOutcome.LeftGame, outcome);
        Assert.Equal(["detect", "leave-game"], actions);
    }

    [Fact]
    public async Task FollowAutoUnexpectedGameDoesNotLeaveWhenFreshDetectionIsInconclusive()
    {
        var leaveAttempted = false;

        var outcome = await VmOperations.RunFollowAutoInGameRecoveryAsync(
            detectInGameMatch: () => null,
            leaveGame: _ =>
            {
                leaveAttempted = true;
                return Task.FromResult(true);
            },
            leaveOpenPauseMenu: _ => throw new InvalidOperationException(
                "An inconclusive HUD must not authorize an open-menu click."),
            CancellationToken.None);

        Assert.Equal(VmOperations.FollowAutoInGameRecoveryOutcome.DetectionInconclusive, outcome);
        Assert.False(leaveAttempted);
    }

    [Fact]
    public async Task FollowAutoLobbyStateDoesNotLeave()
    {
        var leaveAttempted = false;

        var outcome = await VmOperations.RunFollowAutoInGameRecoveryAsync(
            detectInGameMatch: () => VmOperations.InGameHudMatchKind.None,
            leaveGame: _ =>
            {
                leaveAttempted = true;
                return Task.FromResult(true);
            },
            leaveOpenPauseMenu: _ => throw new InvalidOperationException(
                "A lobby state must not authorize an open-menu click."),
            CancellationToken.None);

        Assert.Equal(VmOperations.FollowAutoInGameRecoveryOutcome.NotInGame, outcome);
        Assert.False(leaveAttempted);
    }

    // The broad Frame fallback matches ordinary outdoor scenery (sitting_in_town*.png), so it
    // must never reach a click: acting on one would send Escape and a fixed-coordinate click
    // into the live world. Before RoTW this was enforced by requiring a fresh LegacyProfile
    // after normalization; now it is enforced by the match kind directly.
    [Fact]
    public async Task FollowAutoBroadFrameMatchNeverAuthorizesAClick()
    {
        var leaveAttempted = false;

        var outcome = await VmOperations.RunFollowAutoInGameRecoveryAsync(
            detectInGameMatch: () => VmOperations.InGameHudMatchKind.Frame,
            leaveGame: _ =>
            {
                leaveAttempted = true;
                return Task.FromResult(true);
            },
            leaveOpenPauseMenu: _ => throw new InvalidOperationException(
                "A broad frame match must not authorize an open-menu click."),
            CancellationToken.None);

        Assert.Equal(VmOperations.FollowAutoInGameRecoveryOutcome.DetectionInconclusive, outcome);
        Assert.False(leaveAttempted);
    }

    [Fact]
    public async Task FollowAutoOpenPauseMenuClicksDirectlyWithoutTheEscapeFlow()
    {
        var actions = new List<string>();

        var outcome = await VmOperations.RunFollowAutoInGameRecoveryAsync(
            detectInGameMatch: () =>
            {
                actions.Add("detect");
                return VmOperations.InGameHudMatchKind.SaveAndExitMenu;
            },
            leaveGame: _ =>
            {
                actions.Add("ordinary-leave");
                return Task.FromResult(true);
            },
            leaveOpenPauseMenu: _ =>
            {
                actions.Add("direct-open-menu-leave");
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(VmOperations.FollowAutoInGameRecoveryOutcome.LeftGame, outcome);
        Assert.Equal(["detect", "direct-open-menu-leave"], actions);
    }

    [Fact]
    public async Task FollowAutoFailedLeaveIsReportedRatherThanSwallowed()
    {
        var outcome = await VmOperations.RunFollowAutoInGameRecoveryAsync(
            detectInGameMatch: () => VmOperations.InGameHudMatchKind.HudProfile,
            leaveGame: _ => Task.FromResult(false),
            leaveOpenPauseMenu: _ => Task.FromResult(true),
            CancellationToken.None);

        Assert.Equal(VmOperations.FollowAutoInGameRecoveryOutcome.LeaveFailed, outcome);
    }
}
