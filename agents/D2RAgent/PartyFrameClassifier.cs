namespace D2RAgent;

// Color thresholds for the gold portrait-frame border D2R draws above each OTHER party member's
// HUD icon (issue #20, item 6) - measured directly from 0/1/2/3_party_members.png reference
// screenshots. Deliberately keyed on the frame border, not the health bar drawn above it: the
// bar's fill color and length track that member's current HP (green when healthy, shrinking and
// recoloring as they take damage, gone if they're dead), so it is not a reliable "is someone
// here" signal. The frame itself is constant regardless of HP or which character is in the slot.
//
// This is intentionally separate from ScreenRegionStatsCalculator's OrangeRatio: that threshold
// (blue < 45) is tuned for the in-game HUD's more saturated orange and misses this duller gold-tan
// (blue ran 50-90 across every measured frame pixel), so reusing it would have under-detected.
internal static class PartyFrameClassifier
{
    public static bool IsFrameColor(byte red, byte green, byte blue)
    {
        return red > 110 && red < 200
            && green > 80 && green < 170
            && blue > 15 && blue < 100
            && red > green
            && green > blue
            && red - blue > 40;
    }

    public static double FrameRatio(IEnumerable<(byte Red, byte Green, byte Blue)> pixels)
    {
        var count = 0;
        var matches = 0;
        foreach (var (red, green, blue) in pixels)
        {
            count++;
            if (IsFrameColor(red, green, blue))
            {
                matches++;
            }
        }

        return count == 0 ? 0 : (double)matches / count;
    }
}
