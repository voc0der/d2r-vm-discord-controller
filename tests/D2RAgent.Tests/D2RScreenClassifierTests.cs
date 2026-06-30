using AgentCommon;
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

    [Fact]
    public void CharacterMenuReadyAcceptsLowOrangeLogoWhenMenuButtonsAreReady()
    {
        var logo = Stats(
            averageLuminance: 48,
            luminanceStdDev: 30,
            darkRatio: 0.40,
            orangeRatio: 0.04);
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

    [Theory]
    [InlineData("lobby_hover_party_icon_chat.png", false)]
    [InlineData("lobby_click_party_icon.png", true)]
    [InlineData("lobby_friends_tab_party.png", true)]
    public void FriendsDrawerHeaderVisibleMatchesReferenceCaptures(string capture, bool expected)
    {
        var stats = FullCaptureRegionSampler.Sample(
            capture,
            D2RUiCoordinateCatalog.GetPoint(new D2RUiAutomationConfig(), D2RUiCoordinateTarget.FriendsAccordionHeader),
            widthRatio: 0.200,
            heightRatio: 0.022);

        Assert.Equal(expected, D2RScreenClassifier.IsFriendsDrawerHeaderVisible(stats));
    }

    [Theory]
    [InlineData("lobby_hover_party_icon_chat.png", false)]
    [InlineData("lobby_click_party_icon.png", false)]
    [InlineData("lobby_friends_tab_party.png", true)]
    public void FriendRowNameVisibleMatchesReferenceCaptures(string capture, bool expected)
    {
        var region = D2RUiCoordinateCatalog.GetFriendRowFingerprintRegion(new D2RUiAutomationConfig(), row: 1);
        var stats = FullCaptureRegionSampler.Sample(
            capture,
            region.Center,
            region.WidthRatio,
            region.HeightRatio);

        Assert.Equal(expected, D2RScreenClassifier.IsFriendRowNameVisible(stats));
    }

    [Theory]
    [InlineData(35, 0.14, 0.85)]
    [InlineData(39, 0.06, 0.91)]
    public void LowGreyFriendRowNameVisibleAcceptsLiveExpandedFollowEvidence(
        double averageLuminance,
        double greyRatio,
        double darkRatio)
    {
        var stats = Stats(
            averageLuminance: averageLuminance,
            greyRatio: greyRatio,
            darkRatio: darkRatio);

        Assert.True(D2RScreenClassifier.IsLowGreyFriendRowNameVisible(stats));
    }

    [Theory]
    [InlineData("lobby_hover_party_icon_chat.png", false)]
    [InlineData("lobby_friends_tab_party.png", true)]
    public void FriendRowMarkerVisibleMatchesReferenceCaptures(string capture, bool expected)
    {
        var stats = FullCaptureRegionSampler.Sample(
            capture,
            FriendRowMarkerPoint(row: 1),
            widthRatio: 0.035,
            heightRatio: 0.032);

        Assert.Equal(expected, D2RScreenClassifier.IsFriendRowMarkerVisible(stats));
    }

    [Theory]
    [InlineData("lobby_hover_party_icon_chat.png", false)]
    [InlineData("lobby_click_party_icon.png", false)]
    [InlineData("lobby_friends_tab_party.png", true)]
    public void FriendRowExpandedEvidenceMatchesReferenceCaptures(string capture, bool expected)
    {
        var actual = IsFriendRowExpandedEvidence(capture, row: 1);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CollapsedFriendsDrawerDoesNotLookExpandedAcrossScannedRows()
    {
        var anyExpandedRow = Enumerable.Range(1, 3)
            .Any(row => IsFriendRowExpandedEvidence("lobby_click_party_icon.png", row));

        Assert.False(anyExpandedRow);
    }

    [Theory]
    [InlineData("lobby_click_party_icon.png", false)]
    [InlineData("lobby_friends_tab_party.png", true)]
    [InlineData("lobby_right_click_friend_join_game_available.png", true)]
    public void FriendsListExpandedVisibleRowCountMatchesReferenceCaptures(string capture, bool expected)
    {
        var visibleRows = Enumerable.Range(1, 3)
            .Count(row => IsFriendRowExpandedEvidence(capture, row));

        Assert.Equal(expected, VmOperations.IsFriendsListExpandedByVisibleRows(visibleRows));
    }

    [Fact]
    public void FriendRowExpandedEvidenceAcceptsLiveRowTwoLowGreyText()
    {
        var nameStats = Stats(averageLuminance: 39, greyRatio: 0.06, darkRatio: 0.91);
        var markerStats = Stats(averageLuminance: 39, luminanceStdDev: 24, greyRatio: 0.14, darkRatio: 0.81);

        var actual = D2RScreenClassifier.IsLowGreyFriendRowNameVisible(nameStats)
            && D2RScreenClassifier.IsFriendRowMarkerVisible(markerStats);

        Assert.True(actual);
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
    public void InGameHudFrameAcceptsReportedCreatorInGameCapture()
    {
        var actionHud = Stats(averageLuminance: 38.8, luminanceStdDev: 42.3, brightRatio: 0.030, greyRatio: 0.170, darkRatio: 0.617);
        var bottomHud = Stats(averageLuminance: 30.0, luminanceStdDev: 29.5, darkRatio: 0.679);
        var centerHud = Stats(averageLuminance: 35.0, luminanceStdDev: 39.7, brightRatio: 0.062, greyRatio: 0.160, darkRatio: 0.778);

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

    [Fact]
    public void ConnectingToBattleNetDialogRejectsPlainSplashFlameTexture()
    {
        // Measured from docs/runbooks/assets/d2r-ui/1366x768/post_intro_splash_screen.png at
        // the dialog sample region: flickering flame letters give high luminance variance and
        // a real orange ratio there.
        var dialog = Stats(
            averageLuminance: 108.9,
            luminanceStdDev: 74.9,
            brightRatio: 0.42,
            greyRatio: 0.00,
            darkRatio: 0.25,
            orangeRatio: 0.25);

        Assert.False(D2RScreenClassifier.IsConnectingToBattleNetDialogRegion(dialog));
    }

    [Fact]
    public void ConnectingToBattleNetDialogAcceptsActualDialogCapture()
    {
        // Measured from docs/runbooks/assets/d2r-ui/1366x768/d2r_splash_logging_in.png at the
        // same sample region: the modal's flat near-black interior has no orange and almost no
        // luminance variance, unlike the flame texture it's covering.
        var dialog = Stats(
            averageLuminance: 16.0,
            luminanceStdDev: 3.4,
            brightRatio: 0.00,
            greyRatio: 0.00,
            darkRatio: 1.00,
            orangeRatio: 0.00);

        Assert.True(D2RScreenClassifier.IsConnectingToBattleNetDialogRegion(dialog));
    }

    [Fact]
    public void DiabloSplashScreenAcceptsPostIntroSplashCaptureStats()
    {
        var logo = Stats(
            averageLuminance: 49.8,
            luminanceStdDev: 71.8,
            brightRatio: 0.185,
            darkRatio: 0.654,
            orangeRatio: 0.173,
            redRatio: 0.136);
        var prompt = Stats(
            averageLuminance: 36.3,
            luminanceStdDev: 43.1,
            brightRatio: 0.074,
            darkRatio: 0.667,
            orangeRatio: 0.111,
            redRatio: 0.173);

        Assert.True(D2RScreenClassifier.IsDiabloSplashScreen(logo, prompt));
    }

    [Fact]
    public void DiabloSplashScreenAcceptsSparsePromptSampleFromPostIntroSplash()
    {
        var logo = Stats(
            averageLuminance: 49.8,
            luminanceStdDev: 71.8,
            brightRatio: 0.185,
            darkRatio: 0.60,
            orangeRatio: 0.20,
            redRatio: 0.16);
        var sparsePrompt = Stats(
            averageLuminance: 30.0,
            luminanceStdDev: 36.0,
            darkRatio: 0.76,
            orangeRatio: 0.00,
            redRatio: 0.00);

        Assert.True(D2RScreenClassifier.IsDiabloSplashScreen(logo, sparsePrompt));
    }

    [Fact]
    public void DiabloSplashScreenRejectsBlackIntroFrame()
    {
        var logo = Stats(averageLuminance: 1.0, luminanceStdDev: 0.7, darkRatio: 1.0);
        var prompt = Stats(averageLuminance: 1.2, luminanceStdDev: 1.2, darkRatio: 1.0);

        Assert.False(D2RScreenClassifier.IsDiabloSplashScreen(logo, prompt));
    }

    [Fact]
    public void DiabloSplashScreenRejectsCharacterSelectBackground()
    {
        var logo = Stats(
            averageLuminance: 41.3,
            luminanceStdDev: 29.8,
            greyRatio: 0.383,
            darkRatio: 0.593);
        var prompt = Stats(
            averageLuminance: 34.7,
            luminanceStdDev: 23.4,
            greyRatio: 0.210,
            darkRatio: 0.679);

        Assert.False(D2RScreenClassifier.IsDiabloSplashScreen(logo, prompt));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void GameEntryMenuVisibleRequiresCoherentMenuEvidence(
        bool tabReady,
        bool entryButtonReady,
        bool formPanelReady,
        bool expected)
    {
        Assert.Equal(expected, D2RScreenClassifier.IsGameEntryMenuVisible(
            tabReady,
            entryButtonReady,
            formPanelReady));
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

    private static UiPoint FriendRowMarkerPoint(int row)
    {
        var rowPoint = D2RUiCoordinateCatalog.GetFriendRowPoint(new D2RUiAutomationConfig(), row);
        return new UiPoint(Math.Clamp(rowPoint.X - 0.090, 0, 1), rowPoint.Y);
    }

    private static bool IsFriendRowExpandedEvidence(string capture, int row)
    {
        var nameRegion = D2RUiCoordinateCatalog.GetFriendRowFingerprintRegion(new D2RUiAutomationConfig(), row);
        var nameStats = FullCaptureRegionSampler.Sample(
            capture,
            nameRegion.Center,
            nameRegion.WidthRatio,
            nameRegion.HeightRatio);
        var markerStats = FullCaptureRegionSampler.Sample(
            capture,
            FriendRowMarkerPoint(row),
            widthRatio: 0.035,
            heightRatio: 0.032);

        var nameVisible = D2RScreenClassifier.IsFriendRowNameVisible(nameStats)
            || (row > 1 && D2RScreenClassifier.IsLowGreyFriendRowNameVisible(nameStats));
        return nameVisible && D2RScreenClassifier.IsFriendRowMarkerVisible(markerStats);
    }
}
