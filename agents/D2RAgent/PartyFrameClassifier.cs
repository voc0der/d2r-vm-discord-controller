namespace D2RAgent;

// Color thresholds for the bronze portrait-frame border D2R draws around each OTHER party
// member's HUD icon (issue #20, item 6) - measured directly from
// 1366x768/rotw_ingame_party_members_7.png. Deliberately keyed on the frame border, not the
// health bar above it: the bar's fill color and length track that member's current HP (green when
// healthy, shrinking and recoloring as they take damage, gone if they're dead), so it is not a
// reliable "is someone here" signal. The frame itself is constant regardless of HP or which
// character is in the slot.
//
// RECALIBRATED 2026-08-21 FOR THE MODERN HUD. The previous window required `red > 110`, measured
// from the legacy HUD's brighter gold frame. The modern frame is a much darker bronze: across the
// 903 border pixels in the reference capture, red runs 18-102 (p5 54, p50 70, p95 98) and never
// reaches 110. The old thresholds therefore scored 0.00 on a fully populated party - party
// counting could only ever have returned 0 once legacy graphics went away.
//
// Measured envelope, with the window below chosen to sit outside it on every axis: red 18-102,
// green 16-87, blue 13-54, red > green in 900/903 and green > blue in 902/903, red - blue 2-51
// (p5 26, p50 35). The lower bounds deliberately exclude the handful of near-black anti-aliased
// pixels at the strip's ends rather than stretching to cover them - at a 0.3 area threshold they
// cost nothing, and reaching down to them would start admitting ordinary dark terrain.
//
// This is intentionally separate from ScreenRegionStatsCalculator's OrangeRatio: that threshold
// (blue < 45) is tuned for the in-game HUD's more saturated orange and misses this duller bronze,
// so reusing it would have under-detected.
internal static class PartyFrameClassifier
{
    public static bool IsFrameColor(byte red, byte green, byte blue)
    {
        return red >= 40 && red <= 115
            && green >= 30 && green <= 100
            && blue >= 10 && blue <= 75
            && red > green
            && green > blue
            && red - blue >= 18;
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
