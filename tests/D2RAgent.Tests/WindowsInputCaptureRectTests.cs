using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// WindowsInput.ComputeCaptureRect is the part of the v0.2.93 BitBlt rewrite most likely to have
// a silent, hard-to-notice bug: an off-by-one here doesn't throw, it just captures a region one
// pixel away from the intended one, or clamps a corner/edge sample to the wrong local coordinate.
// Pulled out of SampleRegion specifically so this math can be pinned down without a Windows host,
// same reasoning as ScreenRegionStatsCalculator being split out for the classifier thresholds.
public sealed class WindowsInputCaptureRectTests
{
    private const int ScreenWidth = 1366;
    private const int ScreenHeight = 768;

    [Fact]
    public void CenteredRegionWellWithinScreenIsNotClamped()
    {
        var rect = WindowsInput.ComputeCaptureRect(683, 384, 100, 80, ScreenWidth, ScreenHeight);

        Assert.Equal(633, rect.Left);
        Assert.Equal(344, rect.Top);
        Assert.Equal(100, rect.Width);
        Assert.Equal(80, rect.Height);
    }

    [Fact]
    public void RegionExtendingPastLeftEdgeClampsToScreenStart()
    {
        var rect = WindowsInput.ComputeCaptureRect(10, 384, 100, 80, ScreenWidth, ScreenHeight);

        Assert.Equal(0, rect.Left);
        Assert.Equal(60, rect.Width); // only the on-screen half of the requested width survives
    }

    [Fact]
    public void RegionExtendingPastRightEdgeClampsToScreenEnd()
    {
        var rect = WindowsInput.ComputeCaptureRect(ScreenWidth - 10, 384, 100, 80, ScreenWidth, ScreenHeight);

        Assert.Equal(ScreenWidth, rect.Left + rect.Width);
        Assert.Equal(60, rect.Width);
    }

    [Fact]
    public void RegionExtendingPastTopEdgeClampsToScreenStart()
    {
        var rect = WindowsInput.ComputeCaptureRect(683, 10, 100, 80, ScreenWidth, ScreenHeight);

        Assert.Equal(0, rect.Top);
        Assert.Equal(50, rect.Height);
    }

    [Fact]
    public void RegionExtendingPastBottomEdgeClampsToScreenEnd()
    {
        var rect = WindowsInput.ComputeCaptureRect(683, ScreenHeight - 10, 100, 80, ScreenWidth, ScreenHeight);

        Assert.Equal(ScreenHeight, rect.Top + rect.Height);
        Assert.Equal(50, rect.Height);
    }

    [Fact]
    public void RegionLargerThanTheWholeScreenCapturesExactlyTheScreen()
    {
        var rect = WindowsInput.ComputeCaptureRect(683, 384, 5000, 5000, ScreenWidth, ScreenHeight);

        Assert.Equal(0, rect.Left);
        Assert.Equal(0, rect.Top);
        Assert.Equal(ScreenWidth, rect.Width);
        Assert.Equal(ScreenHeight, rect.Height);
    }

    [Fact]
    public void CenterAtTopLeftCornerStaysInBounds()
    {
        var rect = WindowsInput.ComputeCaptureRect(0, 0, 100, 80, ScreenWidth, ScreenHeight);

        Assert.Equal(0, rect.Left);
        Assert.Equal(0, rect.Top);
        Assert.Equal(50, rect.Width);
        Assert.Equal(40, rect.Height);
    }

    [Fact]
    public void CenterAtBottomRightCornerStaysInBounds()
    {
        var rect = WindowsInput.ComputeCaptureRect(ScreenWidth, ScreenHeight, 100, 80, ScreenWidth, ScreenHeight);

        Assert.Equal(ScreenWidth, rect.Left + rect.Width);
        Assert.Equal(ScreenHeight, rect.Top + rect.Height);
        Assert.Equal(50, rect.Width);
        Assert.Equal(40, rect.Height);
    }

    [Fact]
    public void DegenerateRegionStillProducesAtLeastOnePixel()
    {
        var rect = WindowsInput.ComputeCaptureRect(683, 384, 0.001, 0.001, ScreenWidth, ScreenHeight);

        Assert.True(rect.Width >= 1);
        Assert.True(rect.Height >= 1);
    }

    [Fact]
    public void ScreenSmallerThanRequestedRegionCapturesTheWholeTinyScreen()
    {
        var rect = WindowsInput.ComputeCaptureRect(5, 5, 100, 100, screenWidth: 10, screenHeight: 10);

        Assert.Equal(0, rect.Left);
        Assert.Equal(0, rect.Top);
        Assert.Equal(10, rect.Width);
        Assert.Equal(10, rect.Height);
    }

    [Fact]
    public void RectStaysWithinScreenBoundsAcrossManyOffsetsAndSizes()
    {
        // No single hand-picked case for the general invariant: regardless of where the region
        // is centered or how big it is, the resulting rect must never claim pixels outside the
        // screen, and must never be empty.
        for (var centerX = -200; centerX <= ScreenWidth + 200; centerX += 137)
        {
            for (var centerY = -200; centerY <= ScreenHeight + 200; centerY += 97)
            {
                var rect = WindowsInput.ComputeCaptureRect(centerX, centerY, 220, 90, ScreenWidth, ScreenHeight);

                Assert.True(rect.Left >= 0 && rect.Left < ScreenWidth, $"Left {rect.Left} out of bounds at center ({centerX},{centerY}).");
                Assert.True(rect.Top >= 0 && rect.Top < ScreenHeight, $"Top {rect.Top} out of bounds at center ({centerX},{centerY}).");
                Assert.True(rect.Left + rect.Width <= ScreenWidth, $"Right edge out of bounds at center ({centerX},{centerY}).");
                Assert.True(rect.Top + rect.Height <= ScreenHeight, $"Bottom edge out of bounds at center ({centerX},{centerY}).");
                Assert.True(rect.Width >= 1 && rect.Height >= 1, $"Empty capture rect at center ({centerX},{centerY}).");
            }
        }
    }
}
