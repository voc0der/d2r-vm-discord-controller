using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

public sealed class D2RScreenClassifierTests
{
    [Fact]
    public void CharacterButtonRegionAcceptsCompressedGreyButton()
    {
        var stats = Stats(
            averageLuminance: 58,
            luminanceStdDev: 29,
            brightRatio: 0.04,
            greyRatio: 0.52,
            darkRatio: 0.39);

        Assert.True(D2RScreenClassifier.IsCharacterButtonRegion(stats));
    }

    [Fact]
    public void CharacterMenuReadyRequiresLogoAndMenuButtons()
    {
        var logo = Stats(
            averageLuminance: 35,
            luminanceStdDev: 48,
            darkRatio: 0.62,
            orangeRatio: 0.08);
        var options = Stats(averageLuminance: 55, greyRatio: 0.48, darkRatio: 0.42);
        var cinematics = Stats(averageLuminance: 52, greyRatio: 0.50, darkRatio: 0.45);

        Assert.True(D2RScreenClassifier.IsCharacterMenuReady(logo, options, cinematics));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void LobbyTabReadyRejectsCharacterSelectAnchors(bool characterButtonPairReady, bool characterMenuReady)
    {
        var tab = Stats(
            averageLuminance: 45,
            luminanceStdDev: 24,
            greyRatio: 0.42,
            darkRatio: 0.50);

        Assert.False(D2RScreenClassifier.IsLobbyTabReady(tab, characterButtonPairReady, characterMenuReady));
    }

    [Fact]
    public void LobbyTabReadyAcceptsGreyTabWhenCharacterAnchorsAreAbsent()
    {
        var tab = Stats(
            averageLuminance: 45,
            luminanceStdDev: 24,
            greyRatio: 0.42,
            darkRatio: 0.50);

        Assert.True(D2RScreenClassifier.IsLobbyTabReady(
            tab,
            characterButtonPairReady: false,
            characterMenuReady: false));
    }

    [Fact]
    public void OnlineCharacterListRejectsEmptyOfflinePanel()
    {
        var offlinePanel = Stats(
            averageLuminance: 22,
            luminanceStdDev: 9,
            greyRatio: 0.05,
            darkRatio: 0.95);

        Assert.False(D2RScreenClassifier.IsOnlineCharacterListRegion(offlinePanel));
        Assert.True(D2RScreenClassifier.IsOfflineCharacterPanelRegion(offlinePanel));
    }

    [Fact]
    public void OnlineCharacterListAcceptsPopulatedCharacterPanel()
    {
        var onlinePanel = Stats(
            averageLuminance: 38,
            luminanceStdDev: 30,
            greyRatio: 0.40,
            darkRatio: 0.57);

        Assert.True(D2RScreenClassifier.IsOnlineCharacterListRegion(onlinePanel));
        Assert.False(D2RScreenClassifier.IsOfflineCharacterPanelRegion(onlinePanel));
    }

    [Fact]
    public void InGameHudProfileAcceptsModern1366Capture()
    {
        var hud = Stats(averageLuminance: 38.9, luminanceStdDev: 42.5, darkRatio: 0.669);
        var health = Stats(averageLuminance: 50.7, redRatio: 0.572);
        var mana = Stats(averageLuminance: 38.5, blueRatio: 0.627);

        Assert.True(D2RScreenClassifier.IsInGameHudProfile(
            health,
            mana,
            hud,
            healthRedThreshold: 0.20,
            manaBlueThreshold: 0.18));
    }

    [Fact]
    public void InGameHudProfileAcceptsLegacy1366Capture()
    {
        var hud = Stats(averageLuminance: 70.8, luminanceStdDev: 60.2, darkRatio: 0.365);
        var health = Stats(averageLuminance: 23.1, redRatio: 0.830);
        var mana = Stats(averageLuminance: 8.2, blueRatio: 0.669);

        Assert.True(D2RScreenClassifier.IsInGameHudProfile(
            health,
            mana,
            hud,
            healthRedThreshold: 0.20,
            manaBlueThreshold: 0.18));
    }

    [Fact]
    public void InGameHudProfileRejectsLobbyCapture()
    {
        var hud = Stats(averageLuminance: 26.1, luminanceStdDev: 21.7, darkRatio: 0.816);
        var health = Stats(averageLuminance: 31.1, redRatio: 0.002);
        var mana = Stats(averageLuminance: 32.2, blueRatio: 0.0);

        Assert.False(D2RScreenClassifier.IsInGameHudProfile(
            health,
            mana,
            hud,
            healthRedThreshold: 0.20,
            manaBlueThreshold: 0.18));
    }

    [Fact]
    public void InGameHudFrameAcceptsBroadModern1366Capture()
    {
        var actionHud = Stats(averageLuminance: 36.7, luminanceStdDev: 39.2, brightRatio: 0.047, greyRatio: 0.216, darkRatio: 0.697);
        var bottomHud = Stats(averageLuminance: 29.9, luminanceStdDev: 32.7, darkRatio: 0.755, redRatio: 0.036, blueRatio: 0.027);
        var centerHud = Stats(averageLuminance: 35.5, luminanceStdDev: 35.5, brightRatio: 0.037, greyRatio: 0.221, darkRatio: 0.695);

        Assert.True(D2RScreenClassifier.IsInGameHudFrame(actionHud, bottomHud, centerHud));
    }

    [Fact]
    public void InGameHudFrameAcceptsVariablePotionAndGlobeColors()
    {
        var actionHud = Stats(averageLuminance: 37, luminanceStdDev: 36, brightRatio: 0.025, greyRatio: 0.18, darkRatio: 0.72);
        var bottomHud = Stats(averageLuminance: 30, luminanceStdDev: 34, darkRatio: 0.74);
        var centerHud = Stats(averageLuminance: 38, luminanceStdDev: 36, brightRatio: 0.030, greyRatio: 0.22, darkRatio: 0.68);

        Assert.True(D2RScreenClassifier.IsInGameHudFrame(actionHud, bottomHud, centerHud));
    }

    [Fact]
    public void InGameHudFrameRejectsLobbyCapture()
    {
        var actionHud = Stats(averageLuminance: 25.9, luminanceStdDev: 21.0, brightRatio: 0.008, greyRatio: 0.145, darkRatio: 0.818);
        var bottomHud = Stats(averageLuminance: 25.9, luminanceStdDev: 22.0, darkRatio: 0.811, redRatio: 0.013, blueRatio: 0.0);
        var centerHud = Stats(averageLuminance: 26.9, luminanceStdDev: 22.6, brightRatio: 0.013, greyRatio: 0.181, darkRatio: 0.805);

        Assert.False(D2RScreenClassifier.IsInGameHudFrame(actionHud, bottomHud, centerHud));
    }

    [Fact]
    public void InGameHudFrameRejectsCharacterSelectBottomBar()
    {
        var actionHud = Stats(averageLuminance: 38.8, luminanceStdDev: 20.2, brightRatio: 0.007, greyRatio: 0.459, darkRatio: 0.495);
        var bottomHud = Stats(averageLuminance: 39.4, luminanceStdDev: 25.6, brightRatio: 0.013, greyRatio: 0.431, darkRatio: 0.532);
        var centerHud = Stats(averageLuminance: 45.6, luminanceStdDev: 25.7, brightRatio: 0.015, greyRatio: 0.614, darkRatio: 0.313);

        Assert.False(D2RScreenClassifier.IsInGameHudFrame(actionHud, bottomHud, centerHud));
    }

    private static ScreenRegionStats Stats(
        double averageLuminance,
        double luminanceStdDev = 0,
        double brightRatio = 0,
        double greyRatio = 0,
        double darkRatio = 0,
        double orangeRatio = 0,
        double redRatio = 0,
        double blueRatio = 0)
    {
        return new ScreenRegionStats(
            averageLuminance,
            luminanceStdDev,
            brightRatio,
            greyRatio,
            darkRatio,
            orangeRatio,
            redRatio,
            blueRatio,
            Samples: 289);
    }
}
