using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// End-to-end check of PartyMemberSlots + PartyFrameClassifier against real reference screenshots,
// sampled through FullCaptureRegionSampler exactly the way WindowsInput.SamplePartyFrameRatio
// samples the live screen. This is what actually proves the geometry+threshold combination reads
// back the right count from a real capture, not just that the isolated unit math is internally
// consistent.
//
// The corpus covers the complete range - 0 through 7 other members - so "fills in order with no
// gaps" is an observation at every count rather than an assumption carried over from the legacy
// captures. The old party_members_0..3.png set is dropped rather than re-pointed at the new
// constants: it was captured in legacy graphics, whose panel ran horizontally across a pillarboxed
// viewport instead of vertically down the left screen edge, so none of its geometry transfers.
//
// Capture names count OTHER members. The operator's originals were named for TOTAL players in the
// game, so 2player-rotw.png (you plus one) became rotw_ingame_party_members_1.png.
public sealed class PartyMemberCountReferenceTests
{
    private const string FullParty = "rotw_ingame_party_members_7.png";

    // Every party-less modern in-game capture in the corpus. These are the real false-positive
    // risk: ordinary Act 1 outdoor terrain behind the party column, including a torch-lit town and
    // the lowest graphics preset.
    [Theory]
    [InlineData("sitting_in_town.png")]
    [InlineData("sitting_in_town2.png")]
    [InlineData("sitting_in_town3_lowestgfx.png")]
    [InlineData("sitting_in_town_again.png")]
    [InlineData("just_landed_in_game_checkforhealthandmanaglobes.png")]
    [InlineData("follow_auto_pending_modern_ingame.png")]
    [InlineData("rotw_ingame_save_and_exit_menu.png")]
    public void SoloCapturesReportNoOtherPartyMembers(string fileName)
    {
        Assert.Equal(0, CountOtherPartyMembers(fileName));
    }

    // A full 8-player party is 7 OTHER members, which is also PartyMemberSlots.MaxSlots - so this
    // capture pins the top of the range and confirms MaxSlots by observation rather than by
    // extrapolating the pitch, which is how slots 4-7 used to be justified.
    [Fact]
    public void FullPartyCaptureReportsSevenOtherMembers()
    {
        Assert.Equal(7, CountOtherPartyMembers(FullParty));
        Assert.Equal(PartyMemberSlots.MaxSlots, CountOtherPartyMembers(FullParty));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void PartialPartyCapturesReportTheirExactCount(int members)
    {
        Assert.Equal(members, CountOtherPartyMembers(CaptureFor(members)));
    }

    // "D2R fills slots in order with no gaps" is what lets CountOtherPartyMembers stop at the first
    // miss instead of scanning all seven every heartbeat. Across the whole ladder the occupied
    // slots must be the LEADING ones, with a hard boundary: everything up to the count scores above
    // the threshold and everything after it well below, no stragglers further down the column.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void OccupiedSlotsAreTheLeadingOnesWithNoGaps(int occupied)
    {
        var fileName = CaptureFor(occupied);
        for (var slot = 1; slot <= PartyMemberSlots.MaxSlots; slot++)
        {
            var ratio = FrameRatio(fileName, slot);
            if (slot <= occupied)
            {
                Assert.True(
                    ratio >= PartyMemberSlots.FrameRatioThreshold,
                    $"{fileName} slot {slot} should be occupied but scored {ratio:F2}.");
            }
            else
            {
                Assert.True(
                    ratio < PartyMemberSlots.FrameRatioThreshold,
                    $"{fileName} slot {slot} should be empty but scored {ratio:F2}.");
            }
        }
    }

    // The margin, measured rather than asserted loosely: every occupied slot scores far above the
    // threshold and every position on a solo screen far below it. If a future capture lands near
    // 0.3 this is the test that will say so.
    // The measured margin either side of the threshold, across every capture in the ladder.
    // Occupied slots never drop below 0.40; empty ones read 0.00 almost everywhere, the sole
    // exception being slot 7 of the 5-member capture at 0.28, where bright terrain shows through
    // the empty column. If a future capture narrows either side this is the test that says so.
    [Fact]
    public void OccupiedAndEmptySlotsSitFarEitherSideOfTheThreshold()
    {
        for (var members = 1; members <= PartyMemberSlots.MaxSlots; members++)
        {
            var capture = CaptureFor(members);
            for (var slot = 1; slot <= PartyMemberSlots.MaxSlots; slot++)
            {
                var ratio = FrameRatio(capture, slot);
                if (slot <= members)
                {
                    Assert.True(
                        ratio >= 0.40,
                        $"{capture} slot {slot} scored {ratio:F2}; occupied slots never measure below 0.40.");
                }
                else
                {
                    Assert.True(
                        ratio <= 0.28,
                        $"{capture} slot {slot} scored {ratio:F2}; empty positions never measure above 0.28.");
                }
            }
        }
    }

    // The property most likely to break on a live VM. The frame border is only 3px tall, so the
    // band and grid are chosen so enough always-frame rows stay inside the sample grid no matter
    // which way it slides. Verified by re-sampling every occupied slot with the center pushed off
    // by whole pixels: every occupied slot holds 0.40 or better through -2..+2 against a 0.30 threshold.
    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OccupiedSlotsStayAboveThresholdAcrossTwoPixelsOfVerticalDrift(int driftPixels)
    {
        for (var slot = 1; slot <= PartyMemberSlots.MaxSlots; slot++)
        {
            var ratio = DriftedFrameRatio(FullParty, slot, driftPixels);
            Assert.True(
                ratio >= PartyMemberSlots.FrameRatioThreshold,
                $"Slot {slot} scored {ratio:F2} at {driftPixels:+#;-#;0}px drift, under the {PartyMemberSlots.FrameRatioThreshold} threshold.");
        }
    }

    // The other half of the same budget: drift must not manufacture members out of terrain either.
    [Theory]
    [InlineData(-2)]
    [InlineData(0)]
    [InlineData(2)]
    public void EmptyPositionsStayBelowThresholdAcrossTheSameDrift(int driftPixels)
    {
        for (var slot = 1; slot <= PartyMemberSlots.MaxSlots; slot++)
        {
            var ratio = DriftedFrameRatio("sitting_in_town.png", slot, driftPixels);
            Assert.True(
                ratio < PartyMemberSlots.FrameRatioThreshold,
                $"Empty slot {slot} scored {ratio:F2} at {driftPixels:+#;-#;0}px drift, over the {PartyMemberSlots.FrameRatioThreshold} threshold.");
        }
    }

    private static double DriftedFrameRatio(string fileName, int slot, int driftPixels)
    {
        var center = PartyMemberSlots.GetSlotTopEdgeCenter(slot);
        return FullCaptureRegionSampler.SamplePartyFrameRatio(
            fileName,
            new AgentCommon.UiPoint(center.X, center.Y + (driftPixels / 768.0)),
            PartyMemberSlots.EdgeWidthRatio,
            PartyMemberSlots.EdgeHeightRatio,
            PartyMemberSlots.FrameSampleGrid);
    }

    // The margin that actually gates the count. CountOtherPartyMembers stops at the FIRST slot
    // under the threshold, so only that slot's score can change the answer - a bright patch further
    // down the column is never reached. Across the whole corpus every first-empty slot reads
    // exactly 0.00, against an occupied floor of 0.40, which is a far wider gap than the 0.28
    // worst-case empty reading suggests. This is why contiguity is load-bearing rather than an
    // optimization, and why removing the early exit would make detection strictly worse.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void TheFirstEmptySlotIsTheOneThatGatesTheCountAndReadsFlatZero(int members)
    {
        var ratio = FrameRatio(CaptureFor(members), members + 1);

        Assert.True(
            ratio < 0.05,
            $"First empty slot ({members + 1}) of the {members}-member capture scored {ratio:F2}; it gates the count and must read flat zero.");
    }

    internal static string CaptureFor(int otherMembers) => $"rotw_ingame_party_members_{otherMembers}.png";

    private static double FrameRatio(string fileName, int slot)
    {
        return FullCaptureRegionSampler.SamplePartyFrameRatio(
            fileName,
            PartyMemberSlots.GetSlotTopEdgeCenter(slot),
            PartyMemberSlots.EdgeWidthRatio,
            PartyMemberSlots.EdgeHeightRatio,
            PartyMemberSlots.FrameSampleGrid);
    }

    // Mirrors the "scan slots in order, stop at the first miss" shape the real heartbeat consumer
    // uses - D2R fills slots top-to-bottom with no gaps.
    private static int CountOtherPartyMembers(string fileName)
    {
        for (var slot = 1; slot <= PartyMemberSlots.MaxSlots; slot++)
        {
            if (FrameRatio(fileName, slot) < PartyMemberSlots.FrameRatioThreshold)
            {
                return slot - 1;
            }
        }

        return PartyMemberSlots.MaxSlots;
    }
}
