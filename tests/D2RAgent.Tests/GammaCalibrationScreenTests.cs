using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// D2R shows its first-run Gamma Calibration screen on the way from the intro videos to character
// select when it has decided Settings.json is unusable and rewritten it from defaults. The client
// never advances past it, so before this detector existed the whole VM read as a generic Unknown
// frame: menu_ready timed out, follow-auto power-cycled the guest, then restarted the physical
// node, and none of it could work because the problem was a file.
//
// The anchor is the greyscale ramp, sampled as five patches left to right. Deliberately NOT the
// Diablo head above it - this screen's own instruction is "adjust so the logo is barely visible",
// so logo brightness is the one thing here guaranteed to move.
public sealed class GammaCalibrationScreenTests
{
    private const string GammaCapture = "gamma_calibration_settings_reset.png";

    [Fact]
    public void TheGammaScreenIsRecognizedByBothDetectionChains()
    {
        Assert.True(ReferenceCaptureClassifier.IsGammaCalibrationScreen(GammaCapture));
        Assert.Equal(ReferenceVisibleState.GammaCalibration, ReferenceCaptureClassifier.Classify(GammaCapture));
        Assert.Equal(ReferenceReadyState.GammaCalibration, ReferenceCaptureClassifier.ClassifyReady(GammaCapture));
    }

    // The ready loop must not treat this as a screen it can nudge past: its bursts include
    // Enter/Space, which is the Continue button, and clicking Continue commits the defaults D2R
    // just invented for the settings file - including a resolution every pixel classifier in this
    // repo is calibrated against.
    [Fact]
    public void TheGammaScreenIsNotMistakenForAnyStateTheReadyLoopWouldNudge()
    {
        Assert.False(ReferenceCaptureClassifier.IsDiabloSplashScreen(GammaCapture));
        Assert.False(ReferenceCaptureClassifier.IsCharacterScreenReady(GammaCapture));
        Assert.False(ReferenceCaptureClassifier.IsCharacterScreenOffline(GammaCapture));
        Assert.False(ReferenceCaptureClassifier.IsAnyLobbyEntryMenuVisible(GammaCapture));
        Assert.False(ReferenceCaptureClassifier.IsInGameReady(GammaCapture));
    }

    // Every other full-page capture in the asset library, at once: a false positive here quits a
    // healthy client and overwrites its settings file, so the whole library is the guard rather
    // than a hand-picked list. Measured margins are enormous - not one other capture is even
    // monotonic across the five ramp patches, and the widest span any of them produces is 40.4
    // against this screen's 222.2.
    [Fact]
    public void NoOtherReferenceCaptureLooksLikeTheGammaScreen()
    {
        var falsePositives = ScreenSnippetLoader.EnumerateFullCaptureNames()
            .Where(capture => !string.Equals(capture, GammaCapture, StringComparison.OrdinalIgnoreCase))
            .Where(ReferenceCaptureClassifier.IsGammaCalibrationScreen)
            .ToArray();

        Assert.Empty(falsePositives);
    }

    // The ramp's absolute brightness moves with the gamma slider; its shape does not. These rows
    // are the measured patch luminances from the captured screen remapped across the full slider
    // range (gamma 0.35 to 3.0) - the detector has to hold at every one of them, because a client
    // that lands here inherits whatever gamma the reset settings file specified.
    [Theory]
    [InlineData(0.35, 0.1, 5.0, 28.7, 87.2, 190.0)]
    [InlineData(0.50, 0.4, 16.2, 54.7, 120.0, 207.0)]
    [InlineData(0.70, 2.2, 35.6, 84.7, 148.6, 220.0)]
    [InlineData(1.00, 7.8, 63.2, 117.7, 174.7, 230.0)]
    [InlineData(1.50, 23.7, 100.6, 152.3, 198.1, 238.0)]
    [InlineData(2.00, 41.7, 126.2, 173.1, 210.9, 242.0)]
    [InlineData(2.50, 59.1, 145.3, 187.0, 219.1, 245.0)]
    [InlineData(3.00, 75.2, 159.7, 197.0, 224.6, 246.0)]
    public void TheRampIsRecognizedAtEveryGammaSetting(
        double gamma,
        double first,
        double second,
        double third,
        double fourth,
        double fifth)
    {
        _ = gamma;
        Assert.True(D2RScreenClassifier.IsGammaCalibrationScreen(
            [Patch(first), Patch(second), Patch(third), Patch(fourth), Patch(fifth)],
            BlackFlank(),
            BlackFlank()));
    }

    [Fact]
    public void ANonMonotonicRampIsRejected()
    {
        // The fourth patch dips below the third: real scenery that happens to brighten left to
        // right is never this clean, and the ramp is drawn, not lit.
        Assert.False(D2RScreenClassifier.IsGammaCalibrationScreen(
            [Patch(7.8), Patch(63.2), Patch(117.7), Patch(110.0), Patch(230.0)],
            BlackFlank(),
            BlackFlank()));
    }

    [Fact]
    public void AShallowGradientIsRejected()
    {
        // Monotonic but nearly flat - the shape of a dim scene lit from one side, not a ramp.
        Assert.False(D2RScreenClassifier.IsGammaCalibrationScreen(
            [Patch(30.0), Patch(33.0), Patch(36.0), Patch(39.0), Patch(42.0)],
            BlackFlank(),
            BlackFlank()));
    }

    [Fact]
    public void ARampWithoutBlackFlanksIsRejected()
    {
        var litFlank = new ScreenRegionStats(60.0, 20.0, 0.0, 0.5, 0.10, 0.0, 0.0, 0.0, 81);
        Assert.False(D2RScreenClassifier.IsGammaCalibrationScreen(
            [Patch(7.8), Patch(63.2), Patch(117.7), Patch(174.7), Patch(230.0)],
            litFlank,
            BlackFlank()));
        Assert.False(D2RScreenClassifier.IsGammaCalibrationScreen(
            [Patch(7.8), Patch(63.2), Patch(117.7), Patch(174.7), Patch(230.0)],
            BlackFlank(),
            litFlank));
    }

    // A bounded-sampling timeout returns empty stats. "Could not read the screen" must never be
    // able to satisfy a check that closes a live client and rewrites its settings - the same rule
    // the stuck-load-screen watchdog follows.
    [Fact]
    public void EmptySamplesNeverConfirm()
    {
        var empty = ScreenRegionStatsCalculator.FromPixels([]);
        Assert.False(D2RScreenClassifier.IsGammaCalibrationScreen(
            [empty, empty, empty, empty, empty],
            BlackFlank(),
            BlackFlank()));
        Assert.False(D2RScreenClassifier.IsGammaCalibrationScreen(
            [Patch(7.8), Patch(63.2), empty, Patch(174.7), Patch(230.0)],
            BlackFlank(),
            BlackFlank()));
        Assert.False(D2RScreenClassifier.IsGammaCalibrationScreen(
            [Patch(7.8), Patch(63.2), Patch(117.7), Patch(174.7), Patch(230.0)],
            empty,
            BlackFlank()));
    }

    [Fact]
    public void TooFewPatchesNeverConfirm()
    {
        Assert.False(D2RScreenClassifier.IsGammaCalibrationScreen(
            [Patch(7.8), Patch(117.7), Patch(230.0)],
            BlackFlank(),
            BlackFlank()));
    }

    private static ScreenRegionStats Patch(double luminance)
    {
        return new ScreenRegionStats(luminance, 11.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 81);
    }

    private static ScreenRegionStats BlackFlank()
    {
        return new ScreenRegionStats(0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 81);
    }
}
