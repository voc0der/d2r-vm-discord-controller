using D2RAgent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace D2RAgent.Tests;

// Loads the reference UI snippet crops under docs/runbooks/assets/ - the same images the
// classifier thresholds in D2RScreenClassifier were tuned against (see
// docs/runbooks/client-menu-flows.md) - and samples them the same way WindowsInput.SampleRegion
// samples the live screen, so a snippet is "the region" rather than a sub-rectangle of it.
internal static class ScreenSnippetLoader
{
    public static ScreenRegionStats Load(string snippetFileName, int sampleGrid = 9)
    {
        var path = Path.Combine(FindSnippetsDirectory(), snippetFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Snippet not found: {path}");
        }

        using var image = Image.Load<Rgba32>(path);
        return ScreenRegionStatsCalculator.FromPixels(EnumerateGridPixels(image, sampleGrid));
    }

    private static IEnumerable<(byte Red, byte Green, byte Blue)> EnumerateGridPixels(Image<Rgba32> image, int grid)
    {
        for (var yIndex = 0; yIndex < grid; yIndex++)
        {
            var y = Math.Clamp((int)Math.Round((yIndex + 0.5) * image.Height / grid), 0, image.Height - 1);
            for (var xIndex = 0; xIndex < grid; xIndex++)
            {
                var x = Math.Clamp((int)Math.Round((xIndex + 0.5) * image.Width / grid), 0, image.Width - 1);
                var pixel = image[x, y];
                yield return (pixel.R, pixel.G, pixel.B);
            }
        }
    }

    private static string FindSnippetsDirectory()
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

        return Path.Combine(directory.FullName, "docs", "runbooks", "assets", "d2r-ui", "1366x768", "snippets");
    }
}
