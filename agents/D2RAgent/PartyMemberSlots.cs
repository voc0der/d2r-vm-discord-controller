using AgentCommon;

namespace D2RAgent;

// Party-member portrait slot geometry (issue #20, item 6), measured directly from
// 1366x768/rotw_ingame_party_members_1..7.png - the complete one-to-seven ladder - at the 1366x768
// reference resolution every other proportional UI coordinate in this codebase is measured against.
// D2R draws one portrait per OTHER party member (not counting yourself), filling in order with no
// gaps.
//
// The panel is SORTED, so a member's slot index is NOT stable. Adding a member inserts them in
// sorted position and pushes everyone below down a slot: measured across the ladder, Trinity sits
// in slot 2 at a two-member party and slot 7 at a full one, and Shar was inserted ABOVE Trinity
// rather than appended after it. The pre-RoTW version of this comment claimed the opposite
// ("never reordering"), which the ladder disproves. Nothing may cache "the leader was in slot N";
// the follow-auto pulse scans every visible band and scores all bound nametags against each,
// which is exactly why this is a documentation correction and not a live bug.
//
// REMEASURED 2026-08-21 FOR THE MODERN HUD (docs/runbooks/ui-state-catalog.md). The previous constants were taken from
// party_members_0..3.png, which were all captured in LEGACY graphics - the agent used to press G
// to switch to legacy immediately after entering a game, so every in-game screenshot the fleet
// ever took was legacy. Reign of the Warlock removed legacy graphics, and the modern HUD does not
// merely shift this panel, it ROTATES it: legacy laid the portraits out horizontally across the
// top (left = 190, 72 px pitch along X); modern stacks them VERTICALLY down the left screen edge
// (left = 16, 72.5 px pitch along Y). Nothing about the old numbers could have been salvaged by
// arithmetic, which is why they were not converted, only replaced.
//
// Per-slot vertical structure, offsets relative to the slot's top (the health bar's first row):
//
//   +0  .. +5    green health bar (x 16-55)
//   +6           gap
//   +7  .. +49   portrait, inside a bronze frame (x 16-58)
//   +50 .. +61   name text band, LEFT-aligned from x 17 (legacy centered it under the portrait)
//   +62 .. +71   gap before the next slot
//
// MaxSlots = 7 is now directly observed rather than extrapolated: the reference capture shows a
// full 8-player party as exactly 7 other portraits, and slot 7's name band ends at y 583 with
// clear space below it.
internal static class PartyMemberSlots
{
    public const int MaxSlots = 7;

    // Measured through the real sampler across the whole 1..7 member ladder plus every party-less
    // capture, each at rest and at +-2px of drift: occupied slots never drop below 0.40, and the
    // highest any empty position reaches is 0.28. 0.34 is the midpoint, giving 0.06 either way.
    //
    // That 0.28 deserves naming rather than rounding away: it is slot 7 of the 5-member capture,
    // where bright terrain shows through the empty column and passes the bronze test on scattered
    // pixels. It is also why the contiguity rule in CountOtherPartyMembers is load-bearing and not
    // just an optimization - the scan stops at the FIRST slot under the threshold, and across the
    // entire corpus every first-empty slot reads exactly 0.00, two full slots of margin away from
    // that outlier. A lone bright patch further down the column can never be reached, so it cannot
    // inflate a count.
    public const double FrameRatioThreshold = 0.34;

    private const double ReferenceWidth = 1366.0;
    private const double ReferenceHeight = 768.0;

    private const double SlotLeft = 16.0;
    private const double SlotWidth = 43.0;
    // Top of slot 1's health bar. Slot N's top is this plus (N-1) * SlotPitch.
    private const double SlotTop = 88.0;
    // Measured bar tops: 88, 160, 233, 305, 378, 450, 523 - alternating 72/73 px steps, so the
    // true pitch is 72.5 and the client rounds it per slot.
    private const double SlotPitch = 72.5;

    // Sampling the whole portrait box would mostly land on the character art inside the frame,
    // which differs per character and isn't a reliable signal. A thin strip across just the top
    // edge of the frame border stays clear of that art while still measuring well clear of the
    // threshold (see FrameRatioThreshold).
    //
    // Deliberately the frame and NOT the green health bar above it, even though the bar is the
    // more distinctive color (it reads a saturated green nothing else on that column produces).
    // The bar's length tracks that member's current HP, so a wounded member would shrink it below
    // any ratio threshold and a dead one removes it. The frame is constant regardless of HP or
    // which character is in the slot. Every reference measurement here is at full HP, so a bar
    // -based check would have looked perfect and failed the first time somebody took a hit.
    // The border itself is only 3 px: rows +7..+9 read 0.95-0.98 bronze across all seven distinct
    // characters in the reference capture, while every row from +10 to +46 is character art that
    // ranges 0.09-0.33 depending on who is in the slot, and only the 2 px bottom border at
    // +47..+48 comes back to 0.95. So the top border is the one character-independent signal, and
    // the interior deliberately is not trusted.
    //
    // The band and grid below are chosen together, because both samplers floor a region at
    // `max(sizeInPixels, sampleGrid)` per axis - a 3 px band on the default 9-grid is silently
    // inflated to 9 px, dragging in the health-bar gap above and portrait art below and collapsing
    // an occupied slot to 0.32, a hair over the threshold.
    //
    // These values were picked by sweeping band height, grid size and offset over the real sampler
    // math and scoring each candidate on its WORST result across all seven slots AND +-2 px of
    // vertical drift, not on its best at one exact alignment. That matters because SlotPitch is
    // 72.5: odd-numbered slots land on a half-pixel and round differently from even ones, so a
    // configuration tuned on slot 1 alone can score 0.25 on slot 4. The winning combination holds
    // every occupied slot at 0.40 or better everywhere in -2..+2 px, against a 0.30 threshold,
    // while every empty position reads exactly 0.00 - that is 245 solo measurements (7 slots x 7
    // party-less captures x 5 drift offsets) without a single non-zero. Pinned by
    // PartyMemberCountReferenceTests.
    private const double FrameBandCenterOffset = 8.0;
    private const double EdgeHeight = 6.0;

    // Must be passed to every SamplePartyFrameRatio call for this geometry; see EdgeHeight.
    public const int FrameSampleGrid = 5;

    public static double EdgeWidthRatio => SlotWidth / ReferenceWidth;

    public static double EdgeHeightRatio => EdgeHeight / ReferenceHeight;

    public static UiPoint GetSlotTopEdgeCenter(int slotIndexOneBased)
    {
        return new UiPoint(
            (SlotLeft + (SlotWidth / 2)) / ReferenceWidth,
            (SlotTopFor(slotIndexOneBased) + FrameBandCenterOffset) / ReferenceHeight);
    }

    // D2R draws each member's character name in near-white text under their portrait, LEFT-aligned
    // from x 17 - not centered on the portrait the way the legacy HUD drew it. Measured across all
    // seven named slots: text occupies rows +51..+60 and starts at x 17 in every one, running as
    // far right as x 60 for the longest name on file ("Odysseus"). The band below covers both plus
    // a row of margin either side.
    //
    // Unlike legacy - where this text sat over the black pillarbox bar and had a constant backdrop -
    // the modern band sits over the live game world, so its background moves. Measured on the
    // party-less captures the backdrop is dark (mean luminance 10-22) but does spike locally, so
    // the fingerprint comparison's existing dark-on-dark skipping matters more here than it did.
    private const double NameBandOffset = 50.0;
    private const double NameBandHeight = 12.0;
    private const double NameBandLeft = 16.0;
    private const double NameBandWidth = 51.0;

    public static double NameBandWidthRatio => NameBandWidth / ReferenceWidth;

    public static double NameBandHeightRatio => NameBandHeight / ReferenceHeight;

    public static UiPoint GetSlotNameBandCenter(int slotIndexOneBased)
    {
        var top = SlotTopFor(slotIndexOneBased) + NameBandOffset;
        return new UiPoint(
            (NameBandLeft + (NameBandWidth / 2)) / ReferenceWidth,
            (top + (NameBandHeight / 2)) / ReferenceHeight);
    }

    private static double SlotTopFor(int slotIndexOneBased)
    {
        if (slotIndexOneBased < 1 || slotIndexOneBased > MaxSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndexOneBased), slotIndexOneBased, $"Must be between 1 and {MaxSlots}.");
        }

        return SlotTop + ((slotIndexOneBased - 1) * SlotPitch);
    }
}
