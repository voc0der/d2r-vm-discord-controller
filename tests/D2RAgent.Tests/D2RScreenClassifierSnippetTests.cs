using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// Each of these snippets (docs/runbooks/assets/d2r-ui/1366x768/snippets/) is the literal
// reference capture a D2RScreenClassifier threshold was tuned against - see
// docs/runbooks/client-menu-flows.md's "Visual Anchors" section and the call sites in
// VmOperations.cs. If a future threshold change stops recognizing the exact image it was
// built from, that's a real regression, not a flaky VM.
public sealed class D2RScreenClassifierSnippetTests
{
    [Fact]
    public void CharacterPlayButtonTextIsRecognizedAsCharacterButtonRegion()
    {
        var stats = ScreenSnippetLoader.Load("character_play_button_text.png");
        Assert.True(D2RScreenClassifier.IsCharacterButtonRegion(stats));
    }

    [Fact]
    public void CharacterLobbyButtonTextIsRecognizedAsCharacterButtonRegion()
    {
        var stats = ScreenSnippetLoader.Load("character_lobby_button_text.png");
        Assert.True(D2RScreenClassifier.IsCharacterButtonRegion(stats));
    }

    [Fact]
    public void OfflineEmptyCharacterPanelIsRecognized()
    {
        var stats = ScreenSnippetLoader.Load("character_offline_empty_panel.png");
        Assert.True(D2RScreenClassifier.IsOfflineCharacterPanelRegion(stats));
    }

    [Fact]
    public void LobbyJoinGameTabTextIsRecognizedAsLobbyTabReady()
    {
        var stats = ScreenSnippetLoader.Load("lobby_join_game_tab_text.png");
        Assert.True(D2RScreenClassifier.IsLobbyTabReady(stats, characterButtonPairReady: false, characterMenuReady: false));
    }

    [Fact]
    public void LobbyCreateGameTabTextIsRecognizedAsLobbyTabReady()
    {
        var stats = ScreenSnippetLoader.Load("lobby_create_game_tab_text.png");
        Assert.True(D2RScreenClassifier.IsLobbyTabReady(stats, characterButtonPairReady: false, characterMenuReady: false));
    }

    [Fact]
    public void JoinGameButtonTextIsRecognizedAsLobbyEntryButtonReady()
    {
        var stats = ScreenSnippetLoader.Load("join_game_button_text.png");
        Assert.True(D2RScreenClassifier.IsLobbyEntryButtonReady(stats));
    }

    [Fact]
    public void CreateGameButtonTextIsRecognizedAsLobbyEntryButtonReady()
    {
        var stats = ScreenSnippetLoader.Load("create_game_button_text.png");
        Assert.True(D2RScreenClassifier.IsLobbyEntryButtonReady(stats));
    }

    [Fact]
    public void ModernHealthAndManaGlobesAreRecognizedAsInGameHudProfile()
    {
        var health = ScreenSnippetLoader.Load("modern_health_globe.png");
        var mana = ScreenSnippetLoader.Load("modern_mana_globe.png");

        // The action-bar HUD evidence isn't part of either globe snippet - stand in a
        // trivially-passing region so this test isolates the globe color thresholds
        // (healthRedThreshold/manaBlueThreshold, exactly as VmOperations.SampleInGameHudEvidence
        // calls IsInGameHudProfile for the modern HUD profile) rather than re-deriving HUD bar
        // pixel evidence we don't have a snippet for.
        var passingHud = new ScreenRegionStats(
            AverageLuminance: 60,
            LuminanceStdDev: 40,
            BrightRatio: 0.1,
            GreyRatio: 0.1,
            DarkRatio: 0.1,
            OrangeRatio: 0,
            RedRatio: 0,
            BlueRatio: 0,
            Samples: 81);

        Assert.True(D2RScreenClassifier.IsInGameHudProfile(
            health, mana, passingHud, healthRedThreshold: 0.20, manaBlueThreshold: 0.18));
    }
}
