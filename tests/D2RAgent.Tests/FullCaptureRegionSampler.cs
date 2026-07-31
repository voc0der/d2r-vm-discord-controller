using AgentCommon;
using D2RAgent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace D2RAgent.Tests;

// Samples a region from a full-page reference screenshot exactly the way
// WindowsInput.SampleRegion samples the live D2R window - same center/ratio/grid math,
// same clamping - so a test built on these stats is checking the real detector logic
// against a real capture, not a hand-picked synthetic ScreenRegionStats. This is what
// lets the classifier flow tests stand in for VmOperations' detection functions, which
// can't run outside a Windows host because they go through WindowsInput/Win32 directly.
internal static class FullCaptureRegionSampler
{
    private static readonly Dictionary<string, Image<Rgba32>> Cache = new();

    public static ScreenRegionStats Sample(
        string fileName,
        UiPoint center,
        double widthRatio,
        double heightRatio,
        int sampleGrid = 9)
    {
        var image = LoadCached(fileName);
        return SampleWithinBounds(
            image,
            boundsLeft: 0,
            boundsTop: 0,
            boundsWidth: image.Width,
            boundsHeight: image.Height,
            center,
            widthRatio,
            heightRatio,
            sampleGrid);
    }

    // Battle.net's historical ready capture includes the VMConnect frame and desktop around
    // the launcher. Production resolves launcher coordinates against the Battle.net client
    // rectangle, so reference tests need the same option rather than silently treating the
    // whole outer screenshot as the coordinate space.
    public static ScreenRegionStats SampleWithinBounds(
        string fileName,
        int boundsLeft,
        int boundsTop,
        int boundsWidth,
        int boundsHeight,
        UiPoint center,
        double widthRatio,
        double heightRatio,
        int sampleGrid = 9)
    {
        return SampleWithinBounds(
            LoadCached(fileName),
            boundsLeft,
            boundsTop,
            boundsWidth,
            boundsHeight,
            center,
            widthRatio,
            heightRatio,
            sampleGrid);
    }

    private static ScreenRegionStats SampleWithinBounds(
        Image<Rgba32> image,
        int boundsLeft,
        int boundsTop,
        int boundsWidth,
        int boundsHeight,
        UiPoint center,
        double widthRatio,
        double heightRatio,
        int sampleGrid)
    {
        if (boundsWidth <= 0
            || boundsHeight <= 0
            || boundsLeft < 0
            || boundsTop < 0
            || boundsLeft + boundsWidth > image.Width
            || boundsTop + boundsHeight > image.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundsWidth),
                $"Sample bounds {boundsLeft},{boundsTop} {boundsWidth}x{boundsHeight} exceed capture {image.Width}x{image.Height}.");
        }

        var centerX = boundsLeft + (center.X * boundsWidth);
        var centerY = boundsTop + (center.Y * boundsHeight);
        var regionWidth = Math.Max(boundsWidth * widthRatio, sampleGrid);
        var regionHeight = Math.Max(boundsHeight * heightRatio, sampleGrid);
        var grid = Math.Clamp(sampleGrid, 3, 51);

        return ScreenRegionStatsCalculator.FromPixels(
            EnumerateGridPixels(
                image,
                centerX,
                centerY,
                regionWidth,
                regionHeight,
                grid,
                image.Width,
                image.Height));
    }

    // Same capture math as Sample above, but through PartyFrameClassifier instead of
    // ScreenRegionStatsCalculator - mirrors WindowsInput.SamplePartyFrameRatio the same way
    // Sample mirrors WindowsInput.SampleRegion (issue #20, item 6).
    public static double SamplePartyFrameRatio(
        string fileName,
        UiPoint center,
        double widthRatio,
        double heightRatio,
        int sampleGrid = 9)
    {
        var image = LoadCached(fileName);
        var width = image.Width;
        var height = image.Height;

        var centerX = center.X * width;
        var centerY = center.Y * height;
        var regionWidth = Math.Max(width * widthRatio, sampleGrid);
        var regionHeight = Math.Max(height * heightRatio, sampleGrid);
        var grid = Math.Clamp(sampleGrid, 3, 51);

        return PartyFrameClassifier.FrameRatio(
            EnumerateGridPixels(image, centerX, centerY, regionWidth, regionHeight, grid, width, height));
    }

    // Mirrors WindowsInput.CapturePixelRegion (issue #25 bind-in-game) the same way the methods
    // above mirror SampleRegion/SamplePartyFrameRatio: identical ComputeCaptureRect math, then
    // every native pixel of the rect in row-major RGB order rather than a sparse sample grid.
    public static WindowsInput.CapturedPixelRegion CapturePixelRegion(
        string fileName,
        UiPoint center,
        double widthRatio,
        double heightRatio)
    {
        var image = LoadCached(fileName);
        var rect = WindowsInput.ComputeCaptureRect(
            center.X * image.Width,
            center.Y * image.Height,
            Math.Max(image.Width * widthRatio, 1),
            Math.Max(image.Height * heightRatio, 1),
            image.Width,
            image.Height);

        var rgb = new byte[rect.Width * rect.Height * 3];
        var index = 0;
        for (var y = 0; y < rect.Height; y++)
        {
            for (var x = 0; x < rect.Width; x++)
            {
                var pixel = image[rect.Left + x, rect.Top + y];
                rgb[index++] = pixel.R;
                rgb[index++] = pixel.G;
                rgb[index++] = pixel.B;
            }
        }

        return new WindowsInput.CapturedPixelRegion(rect.Width, rect.Height, rgb);
    }

    // Mirrors WindowsInput.CaptureFingerprintGrid the same way Sample mirrors SampleRegion: a
    // rectangular columns x rows grid rather than the square sample grid, and the raw RGB triples
    // a FriendFingerprint is built from. Without this the friend-row fingerprint geometry could
    // only ever be checked by eye against a screenshot.
    public static FriendFingerprint SampleFriendRowFingerprint(
        string fileName,
        UiPoint center,
        double widthRatio,
        double heightRatio,
        int gridColumns,
        int gridRows)
    {
        var image = LoadCached(fileName);
        var width = image.Width;
        var height = image.Height;
        var columns = Math.Clamp(gridColumns, 1, 64);
        var rows = Math.Clamp(gridRows, 1, 64);

        var centerX = center.X * width;
        var centerY = center.Y * height;
        var regionWidth = Math.Max(width * widthRatio, columns);
        var regionHeight = Math.Max(height * heightRatio, rows);

        var samples = new byte[columns * rows * 3];
        var index = 0;
        for (var yIndex = 0; yIndex < rows; yIndex++)
        {
            var y = Math.Clamp(
                (int)Math.Round(centerY - (regionHeight / 2) + ((yIndex + 0.5) * regionHeight / rows)),
                0,
                height - 1);

            for (var xIndex = 0; xIndex < columns; xIndex++)
            {
                var x = Math.Clamp(
                    (int)Math.Round(centerX - (regionWidth / 2) + ((xIndex + 0.5) * regionWidth / columns)),
                    0,
                    width - 1);
                var pixel = image[x, y];
                samples[index++] = pixel.R;
                samples[index++] = pixel.G;
                samples[index++] = pixel.B;
            }
        }

        return new FriendFingerprint(columns, rows, samples);
    }

    private static IEnumerable<(byte Red, byte Green, byte Blue)> EnumerateGridPixels(
        Image<Rgba32> image,
        double centerX,
        double centerY,
        double regionWidth,
        double regionHeight,
        int grid,
        int screenWidth,
        int screenHeight)
    {
        for (var yIndex = 0; yIndex < grid; yIndex++)
        {
            var y = Math.Clamp(
                (int)Math.Round(centerY - (regionHeight / 2) + ((yIndex + 0.5) * regionHeight / grid)),
                0,
                screenHeight - 1);

            for (var xIndex = 0; xIndex < grid; xIndex++)
            {
                var x = Math.Clamp(
                    (int)Math.Round(centerX - (regionWidth / 2) + ((xIndex + 0.5) * regionWidth / grid)),
                    0,
                    screenWidth - 1);

                var pixel = image[x, y];
                yield return (pixel.R, pixel.G, pixel.B);
            }
        }
    }

    private static Image<Rgba32> LoadCached(string fileName)
    {
        if (Cache.TryGetValue(fileName, out var cached))
        {
            return cached;
        }

        var image = Image.Load<Rgba32>(FindCapturePath(fileName));
        Cache[fileName] = image;
        return image;
    }

    private static string FindCapturePath(string fileName)
    {
        var assetsDir = FindAssetsDirectory();
        var nested = Path.Combine(assetsDir, "1366x768", fileName);
        if (File.Exists(nested))
        {
            return nested;
        }

        var flat = Path.Combine(assetsDir, fileName);
        if (File.Exists(flat))
        {
            return flat;
        }

        throw new FileNotFoundException($"Reference capture not found: {fileName}");
    }

    private static string FindAssetsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "D2ROps.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate repo root (D2ROps.sln) from test base directory.");
        }

        return Path.Combine(directory.FullName, "docs", "runbooks", "assets", "d2r-ui");
    }
}
