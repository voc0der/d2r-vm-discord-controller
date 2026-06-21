using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class D2RUiCoordinateTests
{
    private const int BaselineWidth = 1366;
    private const int BaselineHeight = 768;

    [Theory]
    [InlineData("Create Game", 963, 454, 1128, 496)]
    [InlineData("Join Game", 963, 460, 1128, 502)]
    public void FinalEntryButtonsLandInsideBaselineCaptureButton(string buttonName, int minX, int minY, int maxX, int maxY)
    {
        var ui = new D2RUiAutomationConfig();
        var point = buttonName == "Create Game"
            ? ui.CreateGameButton
            : ui.JoinGameButton;

        var x = (int)Math.Round(point.X * BaselineWidth);
        var y = (int)Math.Round(point.Y * BaselineHeight);

        Assert.InRange(x, minX, maxX);
        Assert.InRange(y, minY, maxY);
    }
}
