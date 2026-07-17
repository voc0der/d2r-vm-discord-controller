using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

// End-to-end check of PartyMemberSlots' name-band geometry + PartyNameFingerprint against the
// real party_members_0..3.png reference screenshots, sampled through
// FullCaptureRegionSampler.CapturePixelRegion exactly the way WindowsInput.CapturePixelRegion
// samples the live screen - the same "prove it against a real capture" role
// PartyMemberCountReferenceTests plays for the portrait-count classifier.
//
// The references happen to exercise every hard case the matcher was calibrated on:
// - "Netrunner" sits in slot 1 of all three populated screenshots (same-name, same-slot match);
// - "Skeleton" sits in slot 2 of party_members_2 on the staggered LOWER name baseline rendered
//   with thinner strokes, and in slot 3 of party_members_3 on the upper baseline rendered bold -
//   the same name at a different slot, different baseline, different stroke weight, and a
//   different scene background (worst measured same-name score, 0.757);
// - "Position" is present in party_members_3 but absent from party_members_2 (the chat line in
//   that capture literally reads "Position left our world"), which is exactly the leader-left
//   scenario the follow-auto pulse needs to detect.
public sealed class PartyNameFingerprintReferenceTests
{
    [Theory]
    [InlineData("party_members_1.png", 1, 65, 8, 217)] // Netrunner
    [InlineData("party_members_2.png", 1, 65, 8, 217)] // Netrunner
    [InlineData("party_members_3.png", 1, 65, 8, 217)] // Netrunner
    [InlineData("party_members_2.png", 2, 57, 9, 156)] // Skeleton, staggered thin rendering
    [InlineData("party_members_3.png", 3, 57, 10, 190)] // Skeleton, upper bold rendering
    [InlineData("party_members_3.png", 2, 54, 11, 178)] // Position, staggered
    public void GlyphBoxesMatchTheMeasuredReferenceCaptures(
        string fileName, int slot, int expectedWidth, int expectedHeight, int expectedBits)
    {
        var template = CaptureTemplate(fileName, slot);

        Assert.NotNull(template);
        Assert.Equal(expectedWidth, template.Width);
        Assert.Equal(expectedHeight, template.Height);
        Assert.Equal(expectedBits, template.BitCount);
    }

    [Theory]
    [InlineData("party_members_0.png", 1)]
    [InlineData("party_members_0.png", 2)]
    [InlineData("party_members_1.png", 2)]
    [InlineData("party_members_2.png", 3)]
    public void EmptySlotsProduceNoGlyphBoxAtAll(string fileName, int slot)
    {
        Assert.Null(CaptureTemplate(fileName, slot));
    }

    [Theory]
    [InlineData("party_members_1.png", 1, "party_members_2.png", 1)] // Netrunner across captures
    [InlineData("party_members_1.png", 1, "party_members_3.png", 1)]
    [InlineData("party_members_3.png", 1, "party_members_2.png", 1)]
    [InlineData("party_members_2.png", 2, "party_members_3.png", 3)] // Skeleton: thin lower vs bold upper
    [InlineData("party_members_3.png", 3, "party_members_2.png", 2)]
    public void TheSameNameMatchesAcrossSlotsBaselinesAndStrokeWeights(
        string templateFile, int templateSlot, string bandFile, int bandSlot)
    {
        var template = CaptureTemplate(templateFile, templateSlot);
        var band = CaptureBand(bandFile, bandSlot);

        Assert.NotNull(template);
        Assert.True(
            PartyNameFingerprint.IsMatch(template, band),
            $"{templateFile} slot {templateSlot} scored {template!.BestScoreIn(band):F3} in {bandFile} slot {bandSlot}, below {PartyNameFingerprint.MatchThreshold}.");
    }

    [Theory]
    [InlineData("party_members_1.png", 1, "party_members_2.png", 2)] // Netrunner vs Skeleton
    [InlineData("party_members_1.png", 1, "party_members_3.png", 2)] // Netrunner vs Position
    [InlineData("party_members_1.png", 1, "party_members_3.png", 3)] // Netrunner vs Skeleton
    [InlineData("party_members_2.png", 2, "party_members_1.png", 1)] // Skeleton vs Netrunner
    [InlineData("party_members_2.png", 2, "party_members_3.png", 2)] // Skeleton vs Position
    [InlineData("party_members_3.png", 3, "party_members_1.png", 1)] // Skeleton vs Netrunner
    [InlineData("party_members_3.png", 2, "party_members_1.png", 1)] // Position vs Netrunner
    [InlineData("party_members_3.png", 2, "party_members_2.png", 2)] // Position vs Skeleton
    public void DifferentNamesNeverMatch(
        string templateFile, int templateSlot, string bandFile, int bandSlot)
    {
        var template = CaptureTemplate(templateFile, templateSlot);
        var band = CaptureBand(bandFile, bandSlot);

        Assert.NotNull(template);
        Assert.False(
            PartyNameFingerprint.IsMatch(template, band),
            $"{templateFile} slot {templateSlot} scored {template!.BestScoreIn(band):F3} in {bandFile} slot {bandSlot}, at/above {PartyNameFingerprint.MatchThreshold}.");
    }

    // The real leave-detection flow: "Position" was bound as the leader while present
    // (party_members_3), then left (party_members_2, whose chat line announces it). Scanning
    // every visible member band the way SampleLeaderPresence does must find no match.
    [Fact]
    public void ADepartedLeaderIsAbsentFromEveryVisibleBand()
    {
        var leader = CaptureTemplate("party_members_3.png", 2);
        Assert.NotNull(leader);

        for (var slot = 1; slot <= 2; slot++)
        {
            var band = CaptureBand("party_members_2.png", slot);
            Assert.False(
                PartyNameFingerprint.IsMatch(leader, band),
                $"Departed leader scored {leader!.BestScoreIn(band):F3} in slot {slot}.");
        }
    }

    // Documents the calibration margin MatchThreshold (0.65) rests on so a future threshold or
    // classifier change that erodes it fails loudly: in this original reference set the worst
    // same-name pair measures 0.757 and the best different-name pair measures 0.526. The Glitch
    // regression captures add the fleet-wide different-name ceiling of 0.551.
    [Fact]
    public void ThresholdKeepsAMarginToBothFailureDirections()
    {
        var worstSameName = Math.Min(
            CaptureTemplate("party_members_2.png", 2)!.BestScoreIn(CaptureBand("party_members_3.png", 3)),
            CaptureTemplate("party_members_3.png", 3)!.BestScoreIn(CaptureBand("party_members_2.png", 2)));

        var bestDifferentName = 0.0;
        var namedSlots = new (string File, int Slot, string Name)[]
        {
            ("party_members_1.png", 1, "Netrunner"),
            ("party_members_2.png", 1, "Netrunner"),
            ("party_members_2.png", 2, "Skeleton"),
            ("party_members_3.png", 1, "Netrunner"),
            ("party_members_3.png", 2, "Position"),
            ("party_members_3.png", 3, "Skeleton"),
        };
        foreach (var a in namedSlots)
        {
            foreach (var b in namedSlots)
            {
                if (a.Name == b.Name)
                {
                    continue;
                }

                bestDifferentName = Math.Max(
                    bestDifferentName,
                    CaptureTemplate(a.File, a.Slot)!.BestScoreIn(CaptureBand(b.File, b.Slot)));
            }
        }

        Assert.True(
            worstSameName - PartyNameFingerprint.MatchThreshold >= 0.1,
            $"Same-name floor {worstSameName:F3} is within 0.1 of the {PartyNameFingerprint.MatchThreshold} threshold.");
        Assert.True(
            PartyNameFingerprint.MatchThreshold - bestDifferentName >= 0.09,
            $"Different-name ceiling {bestDifferentName:F3} is within 0.09 of the {PartyNameFingerprint.MatchThreshold} threshold.");
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

    private static PartyNameFingerprint? CaptureTemplate(string fileName, int slot)
    {
        return CaptureBand(fileName, slot).CropToGlyphBox();
    }
}
