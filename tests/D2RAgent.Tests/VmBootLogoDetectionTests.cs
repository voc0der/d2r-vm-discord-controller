using D2RHost;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace D2RAgent.Tests;

// Built against two real captures taken from the same Hyper-V host, in the same sweep, of two VMs
// that had been powered on for the same 64 minutes:
//
//   boot-logo-wedged.png - D2R_6, frozen on the Windows boot logo for over an hour
//   in-game-healthy.png  - D2R_7, healthy and in a game
//
// Hyper-V reported these two identically: Heartbeat OK, KvpOSName "Windows 10 Pro", Status
// "Operating normally", 21% CPU. Every host-side health signal agreed they were both fine. That is
// what this detector exists for, and why the screen is not a redundant input - it is the only one
// that tells them apart.
public sealed class VmBootLogoDetectionTests
{
    private static VmConsoleFrameStats LoadFrame(string fileName)
    {
        var path = Path.Combine(FindRepoRoot(), "docs", "runbooks", "assets", "vm-console", fileName);
        using var image = Image.Load<Rgba32>(path);

        // Round-tripped through RGB565 rather than read as RGB, because RGB565 is what Hyper-V's
        // thumbnail API actually hands back - so the fixture exercises the same quantisation the
        // live path sees, not a cleaner version of it.
        var data = new byte[image.Width * image.Height * 2];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var p = image[x, y];
                var packed = ((p.R >> 3) << 11) | ((p.G >> 2) << 5) | (p.B >> 3);
                var i = ((y * image.Width) + x) * 2;
                data[i] = (byte)(packed & 0xFF);
                data[i + 1] = (byte)((packed >> 8) & 0xFF);
            }
        }

        return VmConsoleFrame.FromRgb565(data, image.Width, image.Height);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "D2ROps.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repo root.");
    }

    private static VmBootLogoPolicy NewPolicy(bool enabled = true, int maxCuts = 2)
    {
        return new VmBootLogoPolicy(new VmBootLogoOptions(
            Enabled: enabled,
            StuckAfter: TimeSpan.FromMinutes(5),
            MaxHardPowerCuts: maxCuts));
    }

    [Fact]
    public void TheWedgedGuestsConsoleIsRecognisedAsTheWindowsBootLogo()
    {
        Assert.True(VmBootLogoPolicy.IsWindowsBootLogo(LoadFrame("boot-logo-wedged.png")));
    }

    // The healthy frame is dark - it is a night-time Diablo scene - so darkness alone could never
    // have carried this decision. It scores zero on the logo colour, which is what does.
    [Fact]
    public void AHealthyInGameConsoleIsNotMistakenForTheBootLogo()
    {
        var healthy = LoadFrame("in-game-healthy.png");

        Assert.False(VmBootLogoPolicy.IsWindowsBootLogo(healthy));
        Assert.Equal(0, healthy.LogoCyanRatio);
        Assert.True(
            healthy.NearBlackRatio > 0.5,
            $"the healthy frame is genuinely dark ({healthy.NearBlackRatio:P0} near-black), so the detector "
                + "cannot be leaning on brightness alone.");
    }

    // The separation the thresholds sit inside, asserted so a later tweak that collapses it fails
    // here rather than in production.
    [Fact]
    public void TheTwoFramesAreSeparatedByAWideMarginOnBothAxes()
    {
        var wedged = LoadFrame("boot-logo-wedged.png");
        var healthy = LoadFrame("in-game-healthy.png");

        Assert.True(wedged.NearBlackRatio - healthy.NearBlackRatio > 0.2);
        Assert.True(wedged.LogoCyanRatio > 0.005);
        Assert.True(wedged.CyanFillOfBox > 0.6);
        Assert.InRange(wedged.CyanCentroidX, 0.4, 0.6);
    }

    // The bound that stops this power-cutting the fleet: every healthy VM shows this logo on every
    // boot, so a sighting on its own must never act.
    [Fact]
    public void SeeingTheLogoIsNotEnoughOnItsOwn()
    {
        var assessment = NewPolicy().Assess(
            LoadFrame("boot-logo-wedged.png"),
            TimeSpan.FromSeconds(10),
            hardPowerCutsUsed: 0);

        Assert.Equal(VmBootLogoVerdict.KeepWatching, assessment.Verdict);
    }

    [Fact]
    public void ALogoThatOutlastsARealBootIsCut()
    {
        var assessment = NewPolicy().Assess(
            LoadFrame("boot-logo-wedged.png"),
            TimeSpan.FromMinutes(30),
            hardPowerCutsUsed: 0);

        Assert.Equal(VmBootLogoVerdict.HardPowerCut, assessment.Verdict);
    }

    [Fact]
    public void AHealthyConsoleIsNeverCutHoweverLongItHasBeenWatched()
    {
        var assessment = NewPolicy().Assess(
            LoadFrame("in-game-healthy.png"),
            TimeSpan.FromHours(6),
            hardPowerCutsUsed: 0);

        Assert.Equal(VmBootLogoVerdict.NotBootLogo, assessment.Verdict);
    }

    [Fact]
    public void CutsAreBoundedSoAGuestThatKeepsReturningToTheLogoReachesAPerson()
    {
        var assessment = NewPolicy(maxCuts: 2).Assess(
            LoadFrame("boot-logo-wedged.png"),
            TimeSpan.FromMinutes(30),
            hardPowerCutsUsed: 2);

        Assert.Equal(VmBootLogoVerdict.GiveUp, assessment.Verdict);
    }

    [Fact]
    public void ADisabledWatchdogNeverActs()
    {
        var assessment = NewPolicy(enabled: false).Assess(
            LoadFrame("boot-logo-wedged.png"),
            TimeSpan.FromHours(2),
            hardPowerCutsUsed: 0);

        Assert.Equal(VmBootLogoVerdict.NotBootLogo, assessment.Verdict);
    }

    [Fact]
    public void AFrameShorterThanItsDimensionsIsRejectedRatherThanMisread()
    {
        Assert.Throws<ArgumentException>(() => VmConsoleFrame.FromRgb565(new byte[10], 256, 192));
    }
}
