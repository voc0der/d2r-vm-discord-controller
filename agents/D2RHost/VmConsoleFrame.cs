namespace D2RHost;

/// <summary>
/// What one capture of a guest's console framebuffer looks like, reduced to the few numbers a
/// boot-logo decision needs.
/// </summary>
public sealed record VmConsoleFrameStats(
    int Width,
    int Height,
    double NearBlackRatio,
    double LogoCyanRatio,
    double CyanFillOfBox,
    double CyanCentroidX,
    double CyanCentroidY,
    double CyanBoxWidthRatio,
    double CyanBoxHeightRatio);

/// <summary>
/// Turns the raw RGB565 framebuffer Hyper-V hands back from
/// <c>GetVirtualSystemThumbnailImage</c> into <see cref="VmConsoleFrameStats"/>.
/// </summary>
/// <remarks>
/// This exists because the guest cannot be asked. Everything the fleet normally reads about a VM
/// comes from software running inside it, and the failure this serves is one where that software
/// never started - so the only witness left is the screen, which the hypervisor will hand over
/// without the guest's cooperation.
///
/// The reduction happens on the node rather than the master: a 256x192 frame is 96 KB of RGB565,
/// and shipping that per VM per sweep would put megabytes an hour through the agent transport to
/// answer a question that fits in six doubles.
/// </remarks>
public static class VmConsoleFrame
{
    // Anything this dark is "unlit background" rather than content. The boot screen is essentially
    // all of it; a running game, even a dark one, is not.
    private const int NearBlackChannelCeiling = 24;

    // The Windows boot logo is a flat cyan with red at or near zero and blue at least as strong as
    // green. Deliberately loose on the exact hue - the logo's colour has changed between Windows
    // versions and themes - and strict on the *shape* of the colour: no red, and blue-dominant.
    private const int LogoCyanRedCeiling = 60;
    private const int LogoCyanGreenFloor = 120;
    private const int LogoCyanBlueFloor = 180;

    public static VmConsoleFrameStats FromRgb565(ReadOnlySpan<byte> data, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive.");
        }

        var expected = checked(width * height * 2);
        if (data.Length < expected)
        {
            throw new ArgumentException(
                $"RGB565 frame needs {expected} bytes for {width}x{height}, got {data.Length}.",
                nameof(data));
        }

        var total = width * height;
        var nearBlack = 0;
        var cyan = 0;
        long cyanSumX = 0;
        long cyanSumY = 0;
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 2;
                var pixel = data[i] | (data[i + 1] << 8);

                // RGB565: bits 15-11 red, 10-5 green, 4-0 blue, each scaled back up to 0-255.
                var r = ((pixel >> 11) & 0x1F) * 255 / 31;
                var g = ((pixel >> 5) & 0x3F) * 255 / 63;
                var b = (pixel & 0x1F) * 255 / 31;

                if (r < NearBlackChannelCeiling && g < NearBlackChannelCeiling && b < NearBlackChannelCeiling)
                {
                    nearBlack++;
                }

                if (r < LogoCyanRedCeiling && g > LogoCyanGreenFloor && b > LogoCyanBlueFloor && b >= g)
                {
                    cyan++;
                    cyanSumX += x;
                    cyanSumY += y;
                    if (x < minX) { minX = x; }
                    if (x > maxX) { maxX = x; }
                    if (y < minY) { minY = y; }
                    if (y > maxY) { maxY = y; }
                }
            }
        }

        if (cyan == 0)
        {
            return new VmConsoleFrameStats(width, height, (double)nearBlack / total, 0, 0, 0, 0, 0, 0);
        }

        var boxWidth = maxX - minX + 1;
        var boxHeight = maxY - minY + 1;
        return new VmConsoleFrameStats(
            width,
            height,
            (double)nearBlack / total,
            (double)cyan / total,
            (double)cyan / (boxWidth * boxHeight),
            (double)cyanSumX / cyan / width,
            (double)cyanSumY / cyan / height,
            (double)boxWidth / width,
            (double)boxHeight / height);
    }
}
