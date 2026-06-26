using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// Pins PartyFrameClassifier against colors measured directly from the 0/1/2/3_party_members.png
// reference screenshots (issue #20, item 6) - both real frame-border pixels (must classify true)
// and real background pixels from the same screenshots (must classify false) - plus the exact
// threshold boundaries, so a future tweak can see exactly what it widens or narrows.
public sealed class PartyFrameClassifierTests
{
    // Sampled directly from the gold frame border in 1/2/3_party_members.png.
    public static readonly TheoryData<byte, byte, byte> MeasuredFrameColors = new()
    {
        { 144, 136, 66 },
        { 141, 116, 62 },
        { 141, 126, 59 },
        { 144, 132, 68 },
        { 146, 121, 66 },
        { 146, 134, 70 },
        { 142, 102, 27 },
    };

    // Sampled directly from the forest/dirt/rock background in 0_party_members.png and from the
    // areas immediately surrounding the frames in the other three.
    public static readonly TheoryData<byte, byte, byte> MeasuredBackgroundColors = new()
    {
        { 15, 29, 4 },
        { 48, 38, 15 },
        { 58, 53, 43 },
        { 33, 38, 15 },
        { 19, 33, 7 },
        { 1, 1, 1 },
        { 27, 41, 7 },
    };

    [Theory]
    [MemberData(nameof(MeasuredFrameColors))]
    public void ClassifiesMeasuredFrameColorsAsFrame(byte red, byte green, byte blue)
    {
        Assert.True(PartyFrameClassifier.IsFrameColor(red, green, blue));
    }

    [Theory]
    [MemberData(nameof(MeasuredBackgroundColors))]
    public void ClassifiesMeasuredBackgroundColorsAsNotFrame(byte red, byte green, byte blue)
    {
        Assert.False(PartyFrameClassifier.IsFrameColor(red, green, blue));
    }

    // Each case fails exactly one of IsFrameColor's nine && conditions, with the rest of the
    // tuple chosen so every other condition still passes - pins which boundary moved if this
    // ever needs to change, instead of just "still returns false."
    [Theory]
    [InlineData(110, 100, 50)] // red > 110 fails (110 is not > 110)
    [InlineData(200, 100, 50)] // red < 200 fails
    [InlineData(160, 80, 50)] // green > 80 fails
    [InlineData(180, 170, 50)] // green < 170 fails
    [InlineData(160, 100, 15)] // blue > 15 fails
    [InlineData(180, 150, 100)] // blue < 100 fails
    [InlineData(120, 130, 50)] // red > green fails
    [InlineData(160, 90, 90)] // green > blue fails
    [InlineData(130, 100, 95)] // red - blue > 40 fails (35)
    public void RejectsColorsThatFailExactlyOneThreshold(byte red, byte green, byte blue)
    {
        Assert.False(PartyFrameClassifier.IsFrameColor(red, green, blue));
    }

    [Fact]
    public void FrameRatioOfEmptySequenceIsZero()
    {
        Assert.Equal(0, PartyFrameClassifier.FrameRatio([]));
    }

    [Fact]
    public void FrameRatioCountsMatchesProportionally()
    {
        var pixels = new (byte Red, byte Green, byte Blue)[]
        {
            (144, 136, 66), // frame
            (141, 116, 62), // frame
            (15, 29, 4), // background
            (1, 1, 1), // background
        };

        Assert.Equal(0.5, PartyFrameClassifier.FrameRatio(pixels));
    }
}
