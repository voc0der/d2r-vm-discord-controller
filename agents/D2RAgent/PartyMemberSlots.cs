using AgentCommon;

namespace D2RAgent;

// Party-member portrait slot geometry (issue #20, item 6), measured directly from
// 0/1/2/3_party_members.png reference screenshots at the 1366x768 reference resolution every
// other proportional UI coordinate in this codebase is measured against. D2R lays one portrait
// per OTHER party member (not counting yourself) left-to-right in a single row, filling slot 1
// first and never reordering or leaving gaps - confirmed by the 1/2/3-member references all
// landing on the exact same slot 1 box (190,26)-(248,77) at the same 72px pitch between slots.
//
// Slots 4-8 are extrapolated from that confirmed pitch, not directly observed - a max party in
// D2R is 8, but only 0-3 references exist so far. If party member detection looks wrong above 3,
// capture 4_party_members.png etc. and re-check these constants before assuming the detection
// logic itself is broken.
internal static class PartyMemberSlots
{
    public const int MaxSlots = 8;

    // Present slots measured 0.44-0.59 on this ratio across all 6 confirmed-present samples
    // (slots 1-3 in 1/2/3_party_members.png); every absent slot measured exactly 0.0. 0.3 sits
    // comfortably in the gap with margin both ways for a real VM's capture/scaling jitter.
    public const double FrameRatioThreshold = 0.3;

    private const double ReferenceWidth = 1366.0;
    private const double ReferenceHeight = 768.0;
    private const double SlotLeft = 190.0;
    private const double SlotTop = 26.0;
    private const double SlotWidth = 58.0;
    private const double SlotPitch = 72.0;

    // Sampling the whole ~58x51 portrait box would mostly land on the character art inside the
    // frame, which differs per character and isn't a reliable signal. A thin strip across just
    // the top edge of the gold frame border stays clear of that art while still measuring well
    // clear of the threshold (see FrameRatioThreshold).
    private const double EdgeHeight = 6.0;

    public static double EdgeWidthRatio => SlotWidth / ReferenceWidth;

    public static double EdgeHeightRatio => EdgeHeight / ReferenceHeight;

    public static UiPoint GetSlotTopEdgeCenter(int slotIndexOneBased)
    {
        if (slotIndexOneBased < 1 || slotIndexOneBased > MaxSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndexOneBased), slotIndexOneBased, $"Must be between 1 and {MaxSlots}.");
        }

        var left = SlotLeft + ((slotIndexOneBased - 1) * SlotPitch);
        var centerX = (left + (SlotWidth / 2)) / ReferenceWidth;
        var centerY = (SlotTop + (EdgeHeight / 2)) / ReferenceHeight;
        return new UiPoint(centerX, centerY);
    }
}
