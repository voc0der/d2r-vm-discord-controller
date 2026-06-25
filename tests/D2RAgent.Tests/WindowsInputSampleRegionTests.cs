using System.Diagnostics;
using AgentCommon;
using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// Every other test in this project goes through ReferenceCaptureClassifier/FullCaptureRegionSampler -
// a Windows-free replica that reads pixels from a decoded PNG, never the real WindowsInput.SampleRegion
// Win32 path (GetDC/CreateCompatibleDC/BitBlt/GetPixel). That meant the v0.2.93 BitBlt rewrite (fixing
// GDI capture blocking on dwm.exe - see pixel-classifier-catalog.md) had zero coverage of the actual
// code it changed: a P/Invoke signature mismatch, a marshaling bug, or an off-by-one in the local-
// bitmap coordinate math would compile fine and pass every existing test while being completely broken
// on a real Windows machine. .github/workflows/ci.yml's "windows" job runs on windows-latest and
// already builds this solution on real Windows - it just never ran the test suite there. This class
// exercises the real Win32 path; everywhere else still doesn't need a Windows host, so this is the
// one place that does.
public sealed class WindowsInputSampleRegionTests
{
    [Fact]
    public void SampleRegionCapturesRealPixelsViaBitBlt()
    {
        if (!OperatingSystem.IsWindows())
        {
            // No Windows desktop to capture from - everything here is exercised by the
            // "windows" CI job instead, which runs this same suite on windows-latest.
            return;
        }

        var input = new WindowsInput();
        var stopwatch = Stopwatch.StartNew();
        var stats = input.SampleRegion(new UiPoint(0.5, 0.5), 0.5, 0.5, sampleGrid: 9);
        stopwatch.Stop();

        Assert.Equal(81, stats.Samples); // 9x9 grid - confirms BitBlt+GetPixel actually ran, not a silently-empty capture
        Assert.True(
            stopwatch.ElapsedMilliseconds < 5000,
            $"A single BitBlt-backed region capture should be fast on a real desktop, not block on the compositor; took {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public void SampleRegionHandlesEdgeOfScreenWithoutThrowing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var input = new WindowsInput();

        // Center near the top-left corner with a region wide enough to extend past the
        // screen edge in both directions - exercises the capture-rect clamping (left/top/
        // right/bottom in SampleRegion) and the local-coordinate clamping in
        // EnumerateGridPixels, both new in the BitBlt rewrite.
        var stats = input.SampleRegion(new UiPoint(0.0, 0.0), 0.2, 0.2, sampleGrid: 9);

        Assert.Equal(81, stats.Samples);
    }
}
