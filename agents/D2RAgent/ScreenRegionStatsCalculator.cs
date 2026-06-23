namespace D2RAgent;

// The pixel-classification thresholds here are the exact math WindowsInput.SampleRegion uses
// against the live screen, pulled out so it can run against any pixel source - a live desktop
// or a decoded PNG snippet in a test - with identical results for identical input.
internal static class ScreenRegionStatsCalculator
{
    public static ScreenRegionStats FromPixels(IEnumerable<(byte Red, byte Green, byte Blue)> pixels)
    {
        var count = 0;
        var luminanceSum = 0.0;
        var luminanceSquaredSum = 0.0;
        var bright = 0;
        var grey = 0;
        var dark = 0;
        var orange = 0;
        var redPixels = 0;
        var bluePixels = 0;

        foreach (var (red, green, blue) in pixels)
        {
            var luminance = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);

            count++;
            luminanceSum += luminance;
            luminanceSquaredSum += luminance * luminance;

            if (luminance > 130)
            {
                bright++;
            }

            if (luminance < 35)
            {
                dark++;
            }

            if (red > 110
                && green > 45
                && blue < 45
                && red > green * 1.25)
            {
                orange++;
            }

            if (red > 95
                && green < 75
                && blue < 75
                && red > green * 1.40
                && red > blue * 1.40)
            {
                redPixels++;
            }

            if (blue > 80
                && blue > green * 1.05
                && blue > red * 1.35)
            {
                bluePixels++;
            }

            if (Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) < 45
                && luminance is > 35 and < 170)
            {
                grey++;
            }
        }

        if (count == 0)
        {
            return new ScreenRegionStats(0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var average = luminanceSum / count;
        var variance = Math.Max((luminanceSquaredSum / count) - (average * average), 0);
        return new ScreenRegionStats(
            average,
            Math.Sqrt(variance),
            (double)bright / count,
            (double)grey / count,
            (double)dark / count,
            (double)orange / count,
            (double)redPixels / count,
            (double)bluePixels / count,
            count);
    }
}
