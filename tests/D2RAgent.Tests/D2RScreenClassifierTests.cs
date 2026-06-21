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
