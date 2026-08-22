using AgentCommon;
using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// The friend context menu's "Join Game" point is arithmetic, not observation: row position plus
// the offset measured once for row 1. When the menu does not open where that predicts, the click
// lands on whatever the Friends pane draws underneath - and at the shipped geometry, low rows
// predict a point inside the Add Friend button, which opens a modal that blocks every later click
// until a human dismisses it. lobby_stray_add_friend_modal.png is that outcome in the wild.
public sealed class FriendContextMenuProbeTests
{
    private const string StuckLobby = "lobby_follow_stuck_friends_panel_open.png";
    private const string MenuWithJoinGame = "lobby_right_click_friend_join_game_available.png";
    private const string MenuWithoutJoinGame = "lobby_right_click_friend_nojoin_game_available.png";

    private static readonly D2RUiAutomationConfig Defaults = new();

    private static ScreenRegionStats Sample(string capture, UiPoint point)
    {
        return FullCaptureRegionSampler.Sample(
            capture,
            point,
            FriendContextMenuProbe.RegionWidthRatio,
            FriendContextMenuProbe.RegionHeightRatio,
            FriendContextMenuProbe.RegionSampleGrid);
    }

    // The landmine itself, pinned against a real capture. Row 7's predicted Join Game point is
    // (0.278, 0.638) = (380, 490) at 1366x768; the Add Friend button spans roughly y 478-526.
    // The pre-existing guard only rejected points outside the pane horizontally, so this passed.
    [Fact]
    public void TheRowSevenJoinPointLandsInsideTheAddFriendButton()
    {
        var point = D2RUiCoordinateCatalog.GetFriendContextJoinGamePoint(Defaults, 7);

        Assert.True(VmOperations.IsFriendContextJoinPointInLeftPane(point));

        var y = point.Y * 768;
        Assert.InRange(y, 478, 526);
    }

    // Every row from 6 down is at or past the bottom of the scrollable friends list, so a blind
    // click there is never safe. Documented so re-measuring the row geometry cannot quietly
    // reintroduce the collision without this failing.
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    public void LowRowsPredictJoinPointsBelowTheScrollableFriendList(int row, bool belowList)
    {
        var point = D2RUiCoordinateCatalog.GetFriendContextJoinGamePoint(Defaults, row);

        // The friend rows themselves end where the "Recently Played With" header begins, ~y=418.
        Assert.Equal(belowList, point.Y * 768 > 418);
    }

    [Fact]
    public void AnUnchangedRegionIsNotAnOpenMenu()
    {
        var point = D2RUiCoordinateCatalog.GetFriendContextJoinGamePoint(Defaults, 7);
        var stats = Sample(StuckLobby, point);

        Assert.False(FriendContextMenuProbe.MenuAppeared(stats, stats));
    }

    // The safety-critical direction: without evidence on either side the caller must not click.
    [Fact]
    public void AnUnreadableSampleIsNeverTreatedAsAnOpenMenu()
    {
        var stats = Sample(StuckLobby, D2RUiCoordinateCatalog.GetFriendContextJoinGamePoint(Defaults, 7));
        var empty = new ScreenRegionStats(0, 0, 0, 0, 0, 0, 0, 0, 0);

        Assert.False(FriendContextMenuProbe.MenuAppeared(null, stats));
        Assert.False(FriendContextMenuProbe.MenuAppeared(stats, null));
        Assert.False(FriendContextMenuProbe.MenuAppeared(null, null));
        Assert.False(FriendContextMenuProbe.MenuAppeared(empty, stats));
        Assert.False(FriendContextMenuProbe.MenuAppeared(stats, empty));
    }

    // A menu row drawn over the Add Friend button is exactly the case the probe has to catch, so
    // it is measured against the real button art rather than a synthetic value.
    [Fact]
    public void AMenuRowDrawnOverTheAddFriendButtonIsDetected()
    {
        var addFriendButton = Sample(
            StuckLobby,
            D2RUiCoordinateCatalog.GetFriendContextJoinGamePoint(Defaults, 7));
        var menuRow = Sample(MenuWithJoinGame, new UiPoint(0.278, 247.0 / 768.0));

        Assert.True(FriendContextMenuProbe.MenuAppeared(addFriendButton, menuRow));
    }

    // The menu's height depends on its contents: a friend with no Join Game option gets a menu
    // one row shorter (compare the two reference captures). That shorter menu stops above the
    // predicted point, so the region does not change and the probe must refuse - this is the
    // mechanism that produced the stray Add Friend modal.
    [Fact]
    public void AMenuThatStopsShortOfThePredictedPointIsRefused()
    {
        var point = new UiPoint(0.278, 0.344);
        var withJoin = Sample(MenuWithJoinGame, point);
        var withoutJoin = Sample(MenuWithoutJoinGame, point);

        Assert.False(FriendContextMenuProbe.MenuAppeared(withoutJoin, withJoin));
    }

    // Margin check: the thresholds must sit well clear of both the noise floor (an unchanged
    // region reads 0.0) and the smallest real change the probe has to catch.
    [Fact]
    public void TheThresholdsClearTheSmallestRealMenuChange()
    {
        var addFriendButton = Sample(
            StuckLobby,
            D2RUiCoordinateCatalog.GetFriendContextJoinGamePoint(Defaults, 7));
        var menuRow = Sample(MenuWithJoinGame, new UiPoint(0.278, 247.0 / 768.0));

        var luminanceDelta = Math.Abs(menuRow.AverageLuminance - addFriendButton.AverageLuminance);
        var stdDevDelta = Math.Abs(menuRow.LuminanceStdDev - addFriendButton.LuminanceStdDev);

        Assert.True(
            luminanceDelta >= FriendContextMenuProbe.MinimumLuminanceDelta
                || stdDevDelta >= FriendContextMenuProbe.MinimumStdDevDelta,
            $"measured dLum={luminanceDelta:F2} dSd={stdDevDelta:F2} against thresholds "
                + $"{FriendContextMenuProbe.MinimumLuminanceDelta}/{FriendContextMenuProbe.MinimumStdDevDelta}");
    }

    [Fact]
    public void TheProbeRegionStaysInsideTheFriendsPane()
    {
        var point = D2RUiCoordinateCatalog.GetFriendContextJoinGamePoint(Defaults, 1);
        var halfWidth = FriendContextMenuProbe.RegionWidthRatio / 2;

        Assert.True(point.X - halfWidth > 0.05);
        Assert.True(point.X + halfWidth < 0.45);
    }
}
