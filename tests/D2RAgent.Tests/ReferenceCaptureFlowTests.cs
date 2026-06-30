using Xunit;

namespace D2RAgent.Tests;

// Runs the real VmOperations decision-tree priority order (DiabloSplash ->
// ConnectingToBattleNet -> OfflineCharacterScreen -> CharacterScreen -> LobbyOrGame -> InGame ->
// LobbyOrGame -> Unknown, replicated in ReferenceCaptureClassifier) against every full-page
// reference capture under docs/runbooks/assets/d2r-ui/1366x768/ - not hand-picked synthetic
// stats, the actual pixels VmOperations would sample from a live D2R window at this
// resolution. This is the closest thing to an end-to-end test of the detection flow without
// a Windows host, and it's what caught a real bug while it was being built: the first draft
// of IsCharacterScreenOffline here was missing the IsCharacterMenuReady gate the production
// code requires first, which made the lobby and in-game captures misclassify as
// OfflineCharacterScreen (their dark decorative right-edge border alone satisfies the
// empty-panel thresholds; the menu-chrome gate is what excludes them in production).
public sealed class ReferenceCaptureFlowTests
{
    [Theory]
    // Splash / title family
    [InlineData("post_intro_splash_screen.png", ReferenceVisibleState.DiabloSplash)]
    [InlineData("d2r_splash_logging_in.png", ReferenceVisibleState.ConnectingToBattleNet)]
    // Pre-splash cinematic frames must not be mistaken for the real splash/title -
    // Escape is only safe to send while we're confident we're still in the cinematic
    // (see SendReadyIntroClick), so a false DiabloSplash/CharacterScreen match here would
    // cut that window short.
    [InlineData("intro_one_phase1.png", ReferenceVisibleState.Unknown)]
    [InlineData("intro_one_phase2.png", ReferenceVisibleState.Unknown)]
    [InlineData("intro_one_phase3.png", ReferenceVisibleState.Unknown)]
    [InlineData("intro_one_phase4.png", ReferenceVisibleState.Unknown)]
    [InlineData("intro_two_phase1.png", ReferenceVisibleState.Unknown)]
    [InlineData("intro_two_phase2.png", ReferenceVisibleState.Unknown)]
    [InlineData("intro_two_phase3.png", ReferenceVisibleState.Unknown)]
    [InlineData("intro_three_phase1.png", ReferenceVisibleState.Unknown)]
    // The last two frames of the "Diablo II" title burning in are visually almost
    // identical to the real splash (flame letters on black) and do match DiabloSplash -
    // a known, accepted overlap: both branches send the same safe non-Escape splash
    // burst, so misreading "still animating in" as "fully on the splash" isn't harmful.
    [InlineData("intro_three_phase2.png", ReferenceVisibleState.DiabloSplash)]
    [InlineData("intro_three_phase3.png", ReferenceVisibleState.DiabloSplash)]
    [InlineData("load_screen_phase_1.png", ReferenceVisibleState.Unknown)]
    [InlineData("load_screen_phase_2.png", ReferenceVisibleState.Unknown)]
    [InlineData("loading_splash_after_intro_videos.png", ReferenceVisibleState.Unknown)]
    // Character select
    [InlineData("char_screen_act1.png", ReferenceVisibleState.CharacterScreen)]
    [InlineData("char_screen_act2.png", ReferenceVisibleState.CharacterScreen)]
    [InlineData("char_screen_act3.png", ReferenceVisibleState.CharacterScreen)]
    [InlineData("char_screen_act4.png", ReferenceVisibleState.CharacterScreen)]
    [InlineData("char_screen_act5.png", ReferenceVisibleState.CharacterScreen)]
    [InlineData("char_screen_not_selected.png", ReferenceVisibleState.CharacterScreen)]
    [InlineData("character_screen_but_offline.png", ReferenceVisibleState.OfflineCharacterScreen)]
    // Lobby
    [InlineData("lobby_create_game_screen.png", ReferenceVisibleState.LobbyOrGame)]
    [InlineData("lobby_create_game_filled.png", ReferenceVisibleState.LobbyOrGame)]
    [InlineData("lobby_create_game_terror_zones_not_available.png", ReferenceVisibleState.LobbyOrGame)]
    [InlineData("lobby_join_game_screen.png", ReferenceVisibleState.LobbyOrGame)]
    [InlineData("lobby_join_game_screen_difficulty_dropdown.png", ReferenceVisibleState.LobbyOrGame)]
    [InlineData("lobby_click_party_icon.png", ReferenceVisibleState.LobbyOrGame)]
    [InlineData("lobby_click_party_icon_hover_friends_tab.png", ReferenceVisibleState.LobbyOrGame)]
    [InlineData("lobby_hover_party_icon_chat.png", ReferenceVisibleState.LobbyOrGame)]
    [InlineData("lobby_friends_tab_party.png", ReferenceVisibleState.LobbyOrGame)]
    [InlineData("lobby_right_click_friend_join_game_available.png", ReferenceVisibleState.LobbyOrGame)]
    [InlineData("lobby_right_click_friend_nojoin_game_available.png", ReferenceVisibleState.LobbyOrGame)]
    // In game
    [InlineData("sitting_in_town.png", ReferenceVisibleState.InGame)]
    [InlineData("sitting_in_town2.png", ReferenceVisibleState.InGame)]
    [InlineData("sitting_in_town3_lowestgfx.png", ReferenceVisibleState.InGame)]
    [InlineData("sitting_in_town_again.png", ReferenceVisibleState.InGame)]
    [InlineData("just_landed_in_game_checkforhealthandmanaglobes.png", ReferenceVisibleState.InGame)]
    [InlineData("low_graphics_mode_generic.png", ReferenceVisibleState.InGame)]
    [InlineData("legacy_gfx_ingame_save_and_exit_hightlighted.png", ReferenceVisibleState.InGame)]
    [InlineData("legacy_gfx_ingame_save_and_exit_not_hightlighted.png", ReferenceVisibleState.InGame)]
    // Modern-graphics Save and Exit dims the action bar enough that IsInGameHudFrame's
    // brightness checks miss even though the corner globes are still visible - unlike
    // legacy graphics above. Documented, not (yet) treated as a bug - see the runbook.
    [InlineData("modern_gfx_ingame_save_and_exit_hovered.png", ReferenceVisibleState.Unknown)]
    [InlineData("modern_gfx_ingame_save_and_exit_not_hightlighted.png", ReferenceVisibleState.Unknown)]
    // Error dialogs overlay the lobby but aren't part of this state machine - they're
    // handled by their own dedicated detector (IsGameEntryErrorDialogOpen), not modeled
    // here, so Unknown is the correct, expected result rather than a gap.
    [InlineData("cant_join_hell.png", ReferenceVisibleState.Unknown)]
    [InlineData("game_exists_name.png", ReferenceVisibleState.Unknown)]
    [InlineData("game_password_doesnt_match.png", ReferenceVisibleState.Unknown)]
    public void RealCaptureClassifiesAsExpectedState(string capture, ReferenceVisibleState expected)
    {
        Assert.Equal(expected, ReferenceCaptureClassifier.Classify(capture));
    }

    // Classify() only reports the winning state - it stops at the first priority match, so it
    // never surfaces whether a capture's pixels also coincidentally satisfy a different state's
    // check, just got pre-empted by priority order. That's exactly how sitting_in_town.png's
    // lobby-menu overlap went uncaught: nothing asserted the *raw* lobby check on existing
    // InGame captures, so a coincidental match would have stayed silent. This runs the same
    // theory data and explicitly records the lobby-overlap status for every InGame capture, so
    // a newly-introduced or newly-disappeared overlap on any of them shows up as a result
    // change here instead of staying invisible until a live run happens to hit it.
    [Theory]
    [InlineData("sitting_in_town.png", true)]
    [InlineData("sitting_in_town2.png", true)]
    [InlineData("sitting_in_town3_lowestgfx.png", false)]
    [InlineData("sitting_in_town_again.png", true)]
    [InlineData("just_landed_in_game_checkforhealthandmanaglobes.png", false)]
    [InlineData("low_graphics_mode_generic.png", false)]
    [InlineData("legacy_gfx_ingame_save_and_exit_hightlighted.png", false)]
    [InlineData("legacy_gfx_ingame_save_and_exit_not_hightlighted.png", false)]
    public void InGameCaptureLobbyOverlapMatchesKnownStatus(string capture, bool expectedLobbyOverlap)
    {
        Assert.Equal(ReferenceVisibleState.InGame, ReferenceCaptureClassifier.Classify(capture));
        Assert.Equal(
            expectedLobbyOverlap,
            ReferenceCaptureClassifier.IsAnyLobbyEntryMenuVisibleIgnoringInGameOverlap(capture));
    }

    // Same corpus as RealCaptureClassifiesAsExpectedState, but through ClassifyReady - the
    // ready loop's own decision tree (VmOperations.DetectReadyScreenStateScreenOnly), not the
    // status detector's. Before this, the ready loop never evaluated lobby/in-game at all, so
    // a bot already sitting at the lobby or in a live game would read Unknown forever and the
    // loop would keep sending click/key bursts - the exact issue this whole change fixes.
    [Theory]
    // Splash / title family - identical to Classify(), none of these reach the new checks.
    [InlineData("post_intro_splash_screen.png", ReferenceReadyState.DiabloSplash)]
    [InlineData("d2r_splash_logging_in.png", ReferenceReadyState.ConnectingToBattleNet)]
    [InlineData("intro_one_phase1.png", ReferenceReadyState.Unknown)]
    [InlineData("intro_one_phase2.png", ReferenceReadyState.Unknown)]
    [InlineData("intro_one_phase3.png", ReferenceReadyState.Unknown)]
    [InlineData("intro_one_phase4.png", ReferenceReadyState.Unknown)]
    [InlineData("intro_two_phase1.png", ReferenceReadyState.Unknown)]
    [InlineData("intro_two_phase2.png", ReferenceReadyState.Unknown)]
    [InlineData("intro_two_phase3.png", ReferenceReadyState.Unknown)]
    [InlineData("intro_three_phase1.png", ReferenceReadyState.Unknown)]
    [InlineData("intro_three_phase2.png", ReferenceReadyState.DiabloSplash)]
    [InlineData("intro_three_phase3.png", ReferenceReadyState.DiabloSplash)]
    [InlineData("load_screen_phase_1.png", ReferenceReadyState.Unknown)]
    [InlineData("load_screen_phase_2.png", ReferenceReadyState.Unknown)]
    [InlineData("loading_splash_after_intro_videos.png", ReferenceReadyState.Unknown)]
    // Character select - act1 and not_selected pass the full button-pair check, but
    // act2-5 only pass the menu-chrome check (no online character data sampled at those
    // coordinates in those captures), so ClassifyReady reports CharacterMenu for them where
    // Classify() folds both into CharacterScreen via an OR. Both are accepted "ready" states
    // for the ready loop (see IsReadyScreenState), so this distinction doesn't change ready
    // loop behavior - it's surfaced here because ClassifyReady mirrors the real priority order
    // exactly, not because the difference matters operationally.
    [InlineData("char_screen_act1.png", ReferenceReadyState.CharacterScreen)]
    [InlineData("char_screen_act2.png", ReferenceReadyState.CharacterMenu)]
    [InlineData("char_screen_act3.png", ReferenceReadyState.CharacterMenu)]
    [InlineData("char_screen_act4.png", ReferenceReadyState.CharacterMenu)]
    [InlineData("char_screen_act5.png", ReferenceReadyState.CharacterMenu)]
    [InlineData("char_screen_not_selected.png", ReferenceReadyState.CharacterScreen)]
    [InlineData("character_screen_but_offline.png", ReferenceReadyState.OfflineCharacterScreen)]
    // Lobby - every lobby sub-state (Join tab, Create tab, party drawer open/closed, Friends
    // tab open, friend context menu open) reaches LobbyOrGame the same way, because none of
    // these captures satisfy any character-screen check first and the lobby check itself
    // doesn't care which sub-state it's in (IsGameEntryMenuVisible only needs one tab active
    // plus the entry button or form panel).
    [InlineData("lobby_create_game_screen.png", ReferenceReadyState.LobbyOrGame)]
    [InlineData("lobby_create_game_filled.png", ReferenceReadyState.LobbyOrGame)]
    [InlineData("lobby_create_game_terror_zones_not_available.png", ReferenceReadyState.LobbyOrGame)]
    [InlineData("lobby_join_game_screen.png", ReferenceReadyState.LobbyOrGame)]
    [InlineData("lobby_join_game_screen_difficulty_dropdown.png", ReferenceReadyState.LobbyOrGame)]
    [InlineData("lobby_click_party_icon.png", ReferenceReadyState.LobbyOrGame)]
    [InlineData("lobby_click_party_icon_hover_friends_tab.png", ReferenceReadyState.LobbyOrGame)]
    [InlineData("lobby_hover_party_icon_chat.png", ReferenceReadyState.LobbyOrGame)]
    [InlineData("lobby_friends_tab_party.png", ReferenceReadyState.LobbyOrGame)]
    [InlineData("lobby_right_click_friend_join_game_available.png", ReferenceReadyState.LobbyOrGame)]
    [InlineData("lobby_right_click_friend_nojoin_game_available.png", ReferenceReadyState.LobbyOrGame)]
    // In game - all of these pass strict modern/legacy HUD globe evidence directly, so the
    // missing broad Frame-kind fallback (deliberately not ported - see class doc comment)
    // doesn't change any of their results versus Classify().
    [InlineData("sitting_in_town.png", ReferenceReadyState.InGame)]
    [InlineData("sitting_in_town2.png", ReferenceReadyState.InGame)]
    [InlineData("sitting_in_town3_lowestgfx.png", ReferenceReadyState.InGame)]
    [InlineData("sitting_in_town_again.png", ReferenceReadyState.InGame)]
    [InlineData("just_landed_in_game_checkforhealthandmanaglobes.png", ReferenceReadyState.InGame)]
    [InlineData("low_graphics_mode_generic.png", ReferenceReadyState.InGame)]
    [InlineData("legacy_gfx_ingame_save_and_exit_hightlighted.png", ReferenceReadyState.InGame)]
    [InlineData("legacy_gfx_ingame_save_and_exit_not_hightlighted.png", ReferenceReadyState.InGame)]
    // Same documented gap as Classify(): modern-graphics Save and Exit dims the action bar
    // past every in-game threshold (strict and broad alike) and doesn't coincidentally overlap
    // the lobby check either, so Unknown is correct here, not a regression.
    [InlineData("modern_gfx_ingame_save_and_exit_hovered.png", ReferenceReadyState.Unknown)]
    [InlineData("modern_gfx_ingame_save_and_exit_not_hightlighted.png", ReferenceReadyState.Unknown)]
    // Error dialogs overlay the lobby but aren't part of this state machine.
    [InlineData("cant_join_hell.png", ReferenceReadyState.Unknown)]
    [InlineData("game_exists_name.png", ReferenceReadyState.Unknown)]
    [InlineData("game_password_doesnt_match.png", ReferenceReadyState.Unknown)]
    public void RealCaptureClassifiesAsExpectedReadyState(string capture, ReferenceReadyState expected)
    {
        Assert.Equal(expected, ReferenceCaptureClassifier.ClassifyReady(capture));
    }
}
