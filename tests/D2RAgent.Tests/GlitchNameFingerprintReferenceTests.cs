using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

// Regression coverage for watch-follow-auto-20260717-124344.log. The old partial-window Dice
// score mistook Position for Glitch on three of four VMs after Glitch left, so the lone correct
// "gone" vantage could never obtain the independent confirmation required to make everyone
// follow. These are the operator's captures from immediately before and after that departure.
//
// These captures are LEGACY graphics, a mode Reign of the Warlock removed, so they are the one
// place left that deliberately does not use PartyMemberSlots. The bug this pins was in
// PartyNameFingerprint's scoring - a partial-window Dice score that let a short name match inside
// a longer one - not in where the band was sampled from, so the regression is still worth running:
// the legacy band coordinates are pinned locally below as historical constants and the scorer is
// exercised exactly as before. Retiring the captures would retire the only test that covers a real
// incident. There is no modern equivalent yet, because that needs a before/after pair of captures
// around a departure and only a single full-party modern capture exists.
public sealed class GlitchNameFingerprintReferenceTests
{
    // The pre-RoTW legacy name-band geometry, frozen here because PartyMemberSlots no longer
    // describes it: portraits ran left-to-right from x 190 at a 72px pitch inside a pillarboxed
    // 4:3 viewport, 58px wide, with names centered under each portrait across y 78-106.
    private const double LegacySlotLeft = 190.0;
    private const double LegacySlotWidth = 58.0;
    private const double LegacySlotPitch = 72.0;
    private const double LegacyNameBandCenterY = 92.0;
    private const double LegacyNameBandWidth = 72.0;
    private const double LegacyNameBandHeight = 28.0;
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
        var left = LegacySlotLeft + ((slot - 1) * LegacySlotPitch);
        var center = new UiPoint(
            (left + (LegacySlotWidth / 2)) / 1366.0,
            LegacyNameBandCenterY / 768.0);
        var region = FullCaptureRegionSampler.CapturePixelRegion(
            fileName,
            center,
            LegacyNameBandWidth / 1366.0,
            LegacyNameBandHeight / 768.0);
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
