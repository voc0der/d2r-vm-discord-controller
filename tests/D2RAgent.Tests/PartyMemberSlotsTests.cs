using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// Pins PartyMemberSlots' geometry against measurements taken directly from
// 1366x768/rotw_ingame_party_members_7.png.
//
// Reign of the Warlock removed legacy graphics, and the modern HUD does not merely shift this
// panel - it ROTATES it. The previous version of this file pinned a horizontal row across the top
// of a pillarboxed legacy viewport (slot 1 box (190,26)-(248,77), 72px pitch along X). Modern
// stacks the portraits vertically down the left screen edge instead, so every assertion here is
// about Y spacing and a shared X, exactly inverting what it used to assert.
//
// Measured health-bar tops: 88, 160, 233, 305, 378, 450, 523 - alternating 72/73px steps, so the
// true pitch is 72.5 and the client rounds per slot.
public sealed class PartyMemberSlotsTests
{
    private const double Tolerance = 1e-9;
    private const double Pitch = 72.5;

    [Fact]
    public void Slot1CenterMatchesTheMeasuredReferenceBox()
    {
        // Portrait box x 16-58, slot top (health bar's first row) y 88. The frame band centers 8px
        // below the slot top, so the strip centers on x 16+43/2 and y 88+8.
        var center = PartyMemberSlots.GetSlotTopEdgeCenter(1);

        Assert.Equal(37.5 / 1366.0, center.X, Tolerance);
        Assert.Equal(96.0 / 768.0, center.Y, Tolerance);
    }

    [Fact]
    public void ConsecutiveSlotsAreSpacedByTheMeasured72Point5PixelVerticalPitch()
    {
        const double expectedPitch = Pitch / 768.0;

        for (var slot = 1; slot < PartyMemberSlots.MaxSlots; slot++)
        {
            var current = PartyMemberSlots.GetSlotTopEdgeCenter(slot);
            var next = PartyMemberSlots.GetSlotTopEdgeCenter(slot + 1);
            Assert.Equal(expectedPitch, next.Y - current.Y, Tolerance);
        }
    }

    // The inverse of the pre-RoTW assertion: legacy shared a Y because it was one horizontal row;
    // modern shares an X because it is one vertical column.
    [Fact]
    public void EverySlotSharesTheSameXCenterSinceTheyreOneVerticalColumn()
    {
        var firstX = PartyMemberSlots.GetSlotTopEdgeCenter(1).X;

        for (var slot = 2; slot <= PartyMemberSlots.MaxSlots; slot++)
        {
            Assert.Equal(firstX, PartyMemberSlots.GetSlotTopEdgeCenter(slot).X, Tolerance);
        }
    }

    [Fact]
    public void EdgeSizeRatiosMatchTheMeasuredBoxWidthAndSixPixelEdgeHeight()
    {
        Assert.Equal(43.0 / 1366.0, PartyMemberSlots.EdgeWidthRatio, Tolerance);
        Assert.Equal(6.0 / 768.0, PartyMemberSlots.EdgeHeightRatio, Tolerance);
        Assert.Equal(5, PartyMemberSlots.FrameSampleGrid);
    }

    // The frame strip must stay clear of the green health bar above it (rows +0..+5), because the
    // bar's length tracks HP and would make a wounded member read as absent.
    [Fact]
    public void FrameStripStaysBelowTheHealthBar()
    {
        var stripCenter = PartyMemberSlots.GetSlotTopEdgeCenter(1).Y * 768.0;

        Assert.True(stripCenter > 94.5, $"The frame band centers at y {stripCenter}; the health bar occupies 88-93, so the band must be weighted below it.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(int.MaxValue)]
    public void OutOfRangeSlotIndexThrows(int slotIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PartyMemberSlots.GetSlotTopEdgeCenter(slotIndex));
    }

    // No longer an extrapolation: the reference capture shows a full 8-player party as exactly
    // seven other portraits, with clear space below slot 7.
    [Fact]
    public void MaxSlotsIsSevenOtherPartyMembers()
    {
        Assert.Equal(7, PartyMemberSlots.MaxSlots);
        PartyMemberSlots.GetSlotTopEdgeCenter(PartyMemberSlots.MaxSlots);
    }

    // Name-band geometry, measured across all seven named slots: text occupies rows +51..+60 and
    // is LEFT-aligned from x 17 in every one (legacy centered it under the portrait instead),
    // running as far right as x 60 for the longest name on file ("Odysseus").
    [Fact]
    public void NameBandIsLeftAlignedAndCoversTheMeasuredTextRows()
    {
        var center = PartyMemberSlots.GetSlotNameBandCenter(1);

        Assert.Equal(41.5 / 1366.0, center.X, Tolerance);
        Assert.Equal(144.0 / 768.0, center.Y, Tolerance);
        Assert.Equal(51.0 / 1366.0, PartyMemberSlots.NameBandWidthRatio, Tolerance);
        Assert.Equal(12.0 / 768.0, PartyMemberSlots.NameBandHeightRatio, Tolerance);
    }

    // The band must clear the portrait above it (which ends at +49) and the next slot's health
    // bar below it (which starts at +72.5), or portrait art and bar pixels leak into every mask.
    [Fact]
    public void NameBandSitsBetweenThePortraitAndTheNextSlot()
    {
        var center = PartyMemberSlots.GetSlotNameBandCenter(1);
        var top = (center.Y - (PartyMemberSlots.NameBandHeightRatio / 2)) * 768.0;
        var bottom = (center.Y + (PartyMemberSlots.NameBandHeightRatio / 2)) * 768.0;

        Assert.True(top >= 88 + 50, $"Band starts at {top}, must clear the portrait ending at +49.");
        Assert.True(bottom <= 88 + Pitch, $"Band ends at {bottom}, must clear the next slot's bar at {88 + Pitch}.");
    }

    [Fact]
    public void NameBandsAreSpacedByTheSameVerticalPitchAndShareAnX()
    {
        for (var slot = 1; slot < PartyMemberSlots.MaxSlots; slot++)
        {
            var current = PartyMemberSlots.GetSlotNameBandCenter(slot);
            var next = PartyMemberSlots.GetSlotNameBandCenter(slot + 1);
            Assert.Equal(Pitch / 768.0, next.Y - current.Y, Tolerance);
            Assert.Equal(current.X, next.X, Tolerance);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(9)]
    public void OutOfRangeNameBandSlotIndexThrows(int slotIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PartyMemberSlots.GetSlotNameBandCenter(slotIndex));
    }
}
