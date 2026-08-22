using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// Pins PartyFrameClassifier against colors measured directly from
// 1366x768/rotw_ingame_party_members_7.png - both real frame-border pixels (must classify true)
// and real background pixels from the same column on a party-less capture (must classify false) -
// plus the exact threshold boundaries, so a future tweak can see exactly what it widens or narrows.
//
// Every value here was replaced when Reign of the Warlock retired legacy graphics. The old set was
// sampled from the legacy HUD's bright gold frame (red 141-146); the modern frame is a much darker
// bronze whose red never reaches 110, so the pre-RoTW thresholds scored 0.00 on a fully populated
// party. Party counting could only ever have returned 0 once legacy went away.
public sealed class PartyFrameClassifierTests
{
    // Sampled directly from the bronze frame border across all seven slots in
    // rotw_ingame_party_members_7.png. Spans the measured range: red 18-102 (p5 54, p50 70,
    // p95 98), green 16-87, blue 13-54.
    public static readonly TheoryData<byte, byte, byte> MeasuredFrameColors = new()
    {
        { 102, 86, 54 },
        { 100, 84, 53 },
        { 100, 87, 52 },
        { 96, 82, 48 },
        { 85, 69, 43 },
        { 70, 56, 37 },
        { 59, 44, 28 },
    };

    // Sampled from the same seven strip positions on sitting_in_town.png, a modern in-game capture
    // with nobody in the party - so these are exactly the pixels that would be misread as a
    // portrait frame. The whole negative set (six party-less modern captures x 7 slots) scores
    // 0.00 on FrameRatio except one slot at 0.01, against 0.96-0.97 for a real portrait.
    public static readonly TheoryData<byte, byte, byte> MeasuredBackgroundColors = new()
    {
        { 38, 33, 23 },
        { 36, 32, 22 },
        { 35, 31, 22 },
        { 31, 29, 20 },
        { 30, 28, 20 },
        { 27, 30, 18 },
        { 1, 1, 1 },
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
    [InlineData(39, 30, 12)] // red >= 40 fails
    [InlineData(116, 100, 70)] // red <= 115 fails
    [InlineData(60, 29, 11)] // green >= 30 fails
    [InlineData(110, 101, 70)] // green <= 100 fails
    [InlineData(60, 40, 9)] // blue >= 10 fails
    [InlineData(110, 90, 76)] // blue <= 75 fails
    [InlineData(60, 70, 30)] // red > green fails
    [InlineData(60, 40, 40)] // green > blue fails
    [InlineData(60, 50, 43)] // red - blue >= 18 fails (17)
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
            (102, 86, 54), // frame
            (70, 56, 37), // frame
            (38, 33, 23), // background
            (1, 1, 1), // background
        };

        Assert.Equal(0.5, PartyFrameClassifier.FrameRatio(pixels));
    }
}
