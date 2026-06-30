using AgentCommon;
using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FriendContextJoinMenuTests
{
    [Fact]
    public void FriendContextJoinPointIsBoundedToLeftPane()
    {
        var ui = new D2RUiAutomationConfig();
        var friendContextJoin = D2RUiCoordinateCatalog.GetFriendContextJoinGamePoint(ui, friendRow: 2);
        var rightPaneJoin = D2RUiCoordinateCatalog.GetPoint(ui, D2RUiCoordinateTarget.JoinGameButton);

        Assert.True(VmOperations.IsFriendContextJoinPointInLeftPane(friendContextJoin));
        Assert.False(VmOperations.IsFriendContextJoinPointInLeftPane(rightPaneJoin));
    }
}
