using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// Pins PartyMemberSlots' geometry against measurements taken directly from
// 0/1/2/3_party_members.png (1366x768): slot 1's box was (190,26)-(248,77) in every one of the
// three references that had a slot 1 at all, and the pitch between consecutive slots' left
// edges was a consistent 72px (262-190 and 334-262) - issue #20, item 6's "spacing can be
// mathematically deduced" from 1-3 known points.
public sealed class PartyMemberSlotsTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void Slot1CenterMatchesTheMeasuredReferenceBox()
    {
        // Box (190,26)-(248,77) at 1366x768; top-edge strip is the box's horizontal center
        // (190 + 58/2 = 219) and 6px down from the box's top (26 + 6/2 = 29).
        var center = PartyMemberSlots.GetSlotTopEdgeCenter(1);

        Assert.Equal(219.0 / 1366.0, center.X, Tolerance);
        Assert.Equal(29.0 / 768.0, center.Y, Tolerance);
    }

    [Fact]
    public void ConsecutiveSlotsAreSpacedByTheMeasured72PixelPitch()
    {
        const double expectedPitch = 72.0 / 1366.0;

        for (var slot = 1; slot < PartyMemberSlots.MaxSlots; slot++)
        {
            var current = PartyMemberSlots.GetSlotTopEdgeCenter(slot);
            var next = PartyMemberSlots.GetSlotTopEdgeCenter(slot + 1);
            Assert.Equal(expectedPitch, next.X - current.X, Tolerance);
        }
    }

    [Fact]
    public void EverySlotSharesTheSameYCenterSinceTheyreOneHorizontalRow()
    {
        var firstY = PartyMemberSlots.GetSlotTopEdgeCenter(1).Y;

        for (var slot = 2; slot <= PartyMemberSlots.MaxSlots; slot++)
        {
            Assert.Equal(firstY, PartyMemberSlots.GetSlotTopEdgeCenter(slot).Y, Tolerance);
        }
    }

    [Fact]
    public void EdgeSizeRatiosMatchTheMeasuredBoxWidthAndSixPixelEdgeHeight()
    {
        Assert.Equal(58.0 / 1366.0, PartyMemberSlots.EdgeWidthRatio, Tolerance);
        Assert.Equal(6.0 / 768.0, PartyMemberSlots.EdgeHeightRatio, Tolerance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(int.MaxValue)]
    public void OutOfRangeSlotIndexThrows(int slotIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PartyMemberSlots.GetSlotTopEdgeCenter(slotIndex));
    }

    [Fact]
    public void MaxSlotsIsEight()
    {
        Assert.Equal(8, PartyMemberSlots.MaxSlots);
        // Should not throw - 8 is in range.
        PartyMemberSlots.GetSlotTopEdgeCenter(PartyMemberSlots.MaxSlots);
    }

    // Name-band geometry (issue #25 bind-in-game), measured from party_members_1..3.png: names
    // center on the portrait center (x 219 + 72 per slot, +-1px across every named slot) and
    // render on y 81-90 or, staggered, y 93-103.
    [Fact]
    public void NameBandCentersOnThePortraitCenterAndCoversBothBaselines()
    {
        var center = PartyMemberSlots.GetSlotNameBandCenter(1);

        Assert.Equal(219.0 / 1366.0, center.X, Tolerance);
        Assert.Equal(92.0 / 768.0, center.Y, Tolerance);
        Assert.Equal(72.0 / 1366.0, PartyMemberSlots.NameBandWidthRatio, Tolerance);
        Assert.Equal(28.0 / 768.0, PartyMemberSlots.NameBandHeightRatio, Tolerance);
    }

    // The game's chat log starts at y 106 (measured in party_members_2.png, where a "[Game] ..."
    // line renders in the same near-white as name text). The band must end exactly there or
    // chat glyphs leak into every captured mask.
    [Fact]
    public void NameBandStopsExactlyWhereTheChatLogStarts()
    {
        var bandBottom = PartyMemberSlots.GetSlotNameBandCenter(1).Y + (PartyMemberSlots.NameBandHeightRatio / 2);

        Assert.Equal(106.0 / 768.0, bandBottom, Tolerance);
    }

    [Fact]
    public void NameBandsTileTheFullSlotPitchWithoutOverlap()
    {
        for (var slot = 1; slot < PartyMemberSlots.MaxSlots; slot++)
        {
            var current = PartyMemberSlots.GetSlotNameBandCenter(slot);
            var next = PartyMemberSlots.GetSlotNameBandCenter(slot + 1);
            Assert.Equal(PartyMemberSlots.NameBandWidthRatio, next.X - current.X, Tolerance);
            Assert.Equal(current.Y, next.Y, Tolerance);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void OutOfRangeNameBandSlotIndexThrows(int slotIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PartyMemberSlots.GetSlotNameBandCenter(slotIndex));
    }
}
