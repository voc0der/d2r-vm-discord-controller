using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

// The stuck-load-screen watchdog (VmOperations.QuitIfStuckLoadScreenAsync) only quits D2R
// after a multi-minute streak of Unknown frames AND a fresh confirmation that every
// D2RScreenClassifier.LoadScreenSurroundRegion reads near-black. These tests run that exact
// region set and threshold against every full-page reference capture through the same
// sampling math as the live path (FullCaptureRegionSampler mirrors WindowsInput.SampleRegion),
// so the confirm-vs-reject decision for each known screen is pinned here, not eyeballed.
//
// The safety-critical rows are the in-game ones: a wrong quit in a live Hardcore game is the
// one outcome this watchdog must never produce, so every in-game capture - including the
// dimmed modern Save and Exit pause and the dark night-time party_glitch scenes - must reject.
// The screens that legitimately confirm are the game-load screens themselves, the post-intro
// loading splash, and the black cinematic frames; all of them are states where a client that
// has sat unrecognized for minutes is genuinely wedged and a relaunch is the correct recovery.
public sealed class StuckLoadScreenSurroundTests
{
    [Theory]
    // The watchdog's actual targets: black surround, artwork only in the centered panel.
    [InlineData("load_screen_phase_1.png", true)]
    [InlineData("load_screen_phase_2.png", true)]
    [InlineData("loading_splash_after_intro_videos.png", true)]
    // Black cinematic frames also confirm - a client wedged mid-intro for minutes (the ready
    // loop clears a healthy intro in seconds) deserves the same relaunch.
    [InlineData("intro_one_phase1.png", true)]
    [InlineData("intro_one_phase2.png", true)]
    [InlineData("intro_one_phase3.png", true)]
    [InlineData("intro_one_phase4.png", true)]
    [InlineData("intro_three_phase1.png", true)]
    [InlineData("intro_three_phase2.png", true)]
    [InlineData("intro_three_phase3.png", true)]
    // Confirms on pixels, but classifies ConnectingToBattleNet - a recognized state resets the
    // unknown streak, so the watchdog never reaches the pixel confirmation here.
    [InlineData("d2r_splash_logging_in.png", true)]
    // Brighter cinematic frames reject on their lit regions.
    [InlineData("intro_two_phase1.png", false)]
    [InlineData("intro_two_phase2.png", false)]
    [InlineData("intro_two_phase3.png", false)]
    // The flame splash's press-any-key band pushes bottom-center dark ratio to 0.96 - the
    // closest reject in the whole suite; a threshold loosened past it shows up here first.
    [InlineData("post_intro_splash_screen.png", false)]
    // In game - must always reject (Hardcore safety; see class comment).
    [InlineData("sitting_in_town.png", false)]
    [InlineData("sitting_in_town2.png", false)]
    [InlineData("sitting_in_town3_lowestgfx.png", false)]
    [InlineData("sitting_in_town_again.png", false)]
    [InlineData("just_landed_in_game_checkforhealthandmanaglobes.png", false)]
    [InlineData("low_graphics_mode_generic.png", false)]
    [InlineData("legacy_gfx_ingame_save_and_exit_hightlighted.png", false)]
    [InlineData("legacy_gfx_ingame_save_and_exit_not_hightlighted.png", false)]
    [InlineData("modern_gfx_ingame_save_and_exit_hovered.png", false)]
    [InlineData("modern_gfx_ingame_save_and_exit_not_hightlighted.png", false)]
    [InlineData("party_glitch_hc1_present.png", false)]
    [InlineData("party_glitch_missing_hc1.png", false)]
    [InlineData("party_glitch_missing_hc2.png", false)]
    [InlineData("party_glitch_missing_hc3.png", false)]
    [InlineData("party_glitch_missing_hc4.png", false)]
    [InlineData("party_members_0.png", false)]
    [InlineData("party_members_1.png", false)]
    [InlineData("party_members_2.png", false)]
    [InlineData("party_members_3.png", false)]
    // Menus and dialogs - all have their own recovery paths and must reject.
    [InlineData("char_screen_act1.png", false)]
    [InlineData("char_screen_act2.png", false)]
    [InlineData("char_screen_act3.png", false)]
    [InlineData("char_screen_act4.png", false)]
    [InlineData("char_screen_act5.png", false)]
    [InlineData("char_screen_not_selected.png", false)]
    [InlineData("character_screen_but_offline.png", false)]
    [InlineData("lobby_create_game_screen.png", false)]
    [InlineData("lobby_create_game_filled.png", false)]
    [InlineData("lobby_create_game_terror_zones_not_available.png", false)]
    [InlineData("lobby_join_game_screen.png", false)]
    [InlineData("lobby_join_game_screen_difficulty_dropdown.png", false)]
    [InlineData("lobby_click_party_icon.png", false)]
    [InlineData("lobby_click_party_icon_hover_friends_tab.png", false)]
    [InlineData("lobby_hover_party_icon_chat.png", false)]
    [InlineData("lobby_friends_tab_party.png", false)]
    [InlineData("lobby_right_click_friend_join_game_available.png", false)]
    [InlineData("lobby_right_click_friend_nojoin_game_available.png", false)]
    [InlineData("battlenet_reconnect_cannot_connect.png", false)]
    [InlineData("battlenet_reconnect_connecting.png", false)]
    [InlineData("battlenet_reconnect_failed_to_authenticate.png", false)]
    // Game-entry error dialogs classify Unknown and can sit for minutes if unhandled, so they
    // are exactly the screens the pixel confirmation exists to protect - they have their own
    // dedicated detector/recovery (IsGameEntryErrorDialogOpen) and must not be quit instead.
    [InlineData("cant_join_hell.png", false)]
    [InlineData("game_exists_name.png", false)]
    [InlineData("game_password_doesnt_match.png", false)]
    public void SurroundConfirmationMatchesKnownScreens(string capture, bool expectedConfirmed)
    {
        Assert.Equal(expectedConfirmed, AllSurroundRegionsBlack(capture));
    }

    // A stats record that never sampled anything (the TryRunBounded timeout fallback shape on
    // the live path) must read as "not confirmed" - degraded sampling looking exactly like a
    // black screen is how the v0.2.93 DWM stall class would otherwise kill healthy clients.
    [Fact]
    public void EmptySampleNeverConfirms()
    {
        var empty = ScreenRegionStatsCalculator.FromPixels([]);
        Assert.False(D2RScreenClassifier.IsLoadScreenSurroundRegion(empty));
    }

    // Documents the overlap that broke the v0.2.193 watchdog on a real wedge: the load
    // screen's doorway artwork brightens as loading progresses, and a brighter frame crosses
    // IsDiabloSplashScreen's thresholds (the phase_2 reference already reads prompt orange
    // 0.062 vs the 0.04 floor and logo orange 0.025 vs 0.05; brightening the artwork panel
    // 1.3x flips the whole check true - measured against the reference, and confirmed by a
    // live screenshot of a wedged client with a brighter door). The frozen frame then
    // classifies DiabloSplash, a recognized state, which is why the watchdog must key on the
    // black surround alone and never on classification. These rows pin the raw splash-check
    // status of the captured frames: phase_2 sits one brightness step from true, so if a
    // splash-threshold change ever makes a captured load screen raw-match, this fails first
    // and the surround-only watchdog design note in pixel-classifier-catalog.md applies.
    [Theory]
    [InlineData("load_screen_phase_1.png", false)]
    [InlineData("load_screen_phase_2.png", false)]
    [InlineData("loading_splash_after_intro_videos.png", false)]
    [InlineData("post_intro_splash_screen.png", true)]
    public void LoadScreenSplashOverlapMatchesKnownStatus(string capture, bool expectedRawSplashMatch)
    {
        Assert.Equal(expectedRawSplashMatch, ReferenceCaptureClassifier.IsDiabloSplashScreen(capture));
    }

    private static bool AllSurroundRegionsBlack(string capture)
    {
        foreach (var region in D2RScreenClassifier.LoadScreenSurroundRegions)
        {
            var stats = FullCaptureRegionSampler.Sample(
                capture,
                new UiPoint(region.CenterX, region.CenterY),
                region.WidthRatio,
                region.HeightRatio);
            if (!D2RScreenClassifier.IsLoadScreenSurroundRegion(stats))
            {
                return false;
            }
        }

        return true;
    }
}
