using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

// Regression coverage for watch-follow-auto-20260717-124344.log. The old partial-window Dice
// score mistook Position for Glitch on three of four VMs after Glitch left, so the lone correct
// "gone" vantage could never obtain the independent confirmation required to make everyone
// follow. These are the operator's captures from immediately before and after that departure.
public sealed class GlitchNameFingerprintReferenceTests
{
    [Fact]
    public void GlitchMatchesItsBoundCapture()
    {
        var glitch = CaptureTemplate("party_glitch_hc1_present.png", 2);
        var presentBand = CaptureBand("party_glitch_hc1_present.png", 2);

        Assert.Equal(40, glitch.Width);
        Assert.Equal(9, glitch.Height);
        Assert.Equal(138, glitch.BitCount);
        Assert.Equal(1.0, glitch.BestScoreIn(presentBand), 9);
        Assert.True(PartyNameFingerprint.IsMatch(glitch, presentBand));
    }

    [Theory]
    [InlineData("party_glitch_missing_hc1.png")]
    [InlineData("party_glitch_missing_hc2.png")]
    [InlineData("party_glitch_missing_hc3.png")]
    [InlineData("party_glitch_missing_hc4.png")]
    public void DepartedGlitchDoesNotMatchAnyRemainingName(string fileName)
    {
        var glitch = CaptureTemplate("party_glitch_hc1_present.png", 2);

        for (var slot = 1; slot <= 3; slot++)
        {
            var band = CaptureBand(fileName, slot);
            var score = glitch.BestScoreIn(band);
            Assert.False(
                PartyNameFingerprint.IsMatch(glitch, band),
                $"{fileName} slot {slot} falsely matched departed Glitch at {score:F3}. Threshold: {PartyNameFingerprint.MatchThreshold:F2}.");
            Assert.True(
                PartyNameFingerprint.MatchThreshold - score >= 0.09,
                $"{fileName} slot {slot} leaves less than 0.09 false-match margin: {score:F3} vs {PartyNameFingerprint.MatchThreshold:F2}.");
        }
    }

    private static PartyNameFingerprint CaptureBand(string fileName, int slot)
    {
        var region = FullCaptureRegionSampler.CapturePixelRegion(
            fileName,
            PartyMemberSlots.GetSlotNameBandCenter(slot),
            PartyMemberSlots.NameBandWidthRatio,
            PartyMemberSlots.NameBandHeightRatio);
        var mask = PartyNameFingerprint.FromPixels(region.Rgb, region.Width, region.Height);
        Assert.NotNull(mask);
        return mask;
    }

    private static PartyNameFingerprint CaptureTemplate(string fileName, int slot)
    {
        var template = CaptureBand(fileName, slot).CropToGlyphBox();
        Assert.NotNull(template);
        return template;
    }
}
