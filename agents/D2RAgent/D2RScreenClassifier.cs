namespace D2RAgent;

internal static class D2RScreenClassifier
{
    public static bool IsCharacterButtonRegion(ScreenRegionStats stats)
    {
        return stats.AverageLuminance > 45
            && stats.GreyRatio > 0.35
            && stats.DarkRatio < 0.55;
    }

    public static bool IsCharacterMenuReady(
        ScreenRegionStats logo,
        ScreenRegionStats options,
        ScreenRegionStats cinematics)
    {
        var logoReady = logo.OrangeRatio > 0.05
            || (logo.OrangeRatio >= 0.04
                && logo.AverageLuminance > 35
                && logo.DarkRatio < 0.65);

        return logoReady
            && IsCharacterMenuButtonRegion(options)
            && IsCharacterMenuButtonRegion(cinematics);
    }

    public static bool IsConnectingToBattleNetDialogRegion(ScreenRegionStats dialog)
    {
        // The dialog's interior is a flat, near-black fill - not the lighter grey box it
        // looks like at a glance. The reliable signal is contrast: the same screen region
        // shows high-variance flame texture and a real orange ratio on the plain splash
        // (logo flicker), and goes flat/dark with no orange once the modal covers it.
        // Measured against real reference captures: plain splash at this region reads
        // orange=0.25/stdDev=75; the connecting dialog reads orange=0.00/stdDev=3.
        return dialog.OrangeRatio < 0.05
            && dialog.LuminanceStdDev < 20;
    }

    public static bool IsDiabloSplashScreen(ScreenRegionStats logo, ScreenRegionStats prompt)
    {
        var darkSplashBackdrop = logo.DarkRatio > 0.45
            && prompt.DarkRatio > 0.45;
        var logoHasFlameTexture = logo.OrangeRatio > 0.05
            && logo.LuminanceStdDev > 25;
        var promptHasPressAnyKeyTexture = prompt.OrangeRatio > 0.04
            || (prompt.RedRatio > 0.08 && prompt.LuminanceStdDev > 25);
        var classicSplash = logoHasFlameTexture && promptHasPressAnyKeyTexture;

        // A sparse sample grid can land between the thin prompt letters on the
        // 1366x768 post-intro splash. The logo region is much larger and more
        // stable, so accept strong logo evidence plus a dark/contrasty prompt band.
        var logoDominantSplash = logo.OrangeRatio > 0.08
            && logo.BrightRatio > 0.04
            && logo.LuminanceStdDev > 45
            && prompt.LuminanceStdDev > 20
            && prompt.DarkRatio > 0.55;

        return darkSplashBackdrop
            && (classicSplash || logoDominantSplash);
    }

    public static bool IsOnlineCharacterListRegion(ScreenRegionStats stats)
    {
        return stats.AverageLuminance > 30
            && stats.GreyRatio > 0.20
            && stats.DarkRatio < 0.80;
    }

    public static bool IsOfflineCharacterPanelRegion(ScreenRegionStats stats)
    {
        return stats.AverageLuminance < 32
            && stats.DarkRatio > 0.82
            && stats.GreyRatio < 0.18;
    }

    public static bool IsLobbyEntryButtonReady(ScreenRegionStats stats)
    {
        // The previous DarkRatio > 0.25 lower bound and AverageLuminance < 90 upper bound
        // were never satisfiable by the real button: docs/runbooks/assets/d2r-ui/1366x768/
        // snippets/{join,create}_game_button_text.png - the actual ready-state captures these
        // thresholds were supposed to recognize - measure DarkRatio=0.00 and AverageLuminance
        // up to 90.5 (bright label text on a light/grey panel, no dark pixels at all).
        return stats.AverageLuminance > 30
            && stats.AverageLuminance < 110
            && stats.GreyRatio > 0.30
            && stats.DarkRatio < 0.70;
    }

    public static bool IsCannotJoinCurrentCharacterDialog(
        ScreenRegionStats cancelButton,
        ScreenRegionStats switchCharactersButton,
        ScreenRegionStats topBorder,
        ScreenRegionStats body)
    {
        // Unlike D2R's generic one-button OK dialog, this join restriction has two bright
        // buttons at fixed positions: Cancel on the left and Switch Characters on the right.
        // The old generic detector sampled the gap between them as if it were an OK button,
        // then repeatedly clicked that inert gap and left follow-auto wedged on the modal.
        // Requiring both button faces distinguishes this state from the generic dialog and
        // ordinary lobby chrome while the border/body pair confirms that a modal is present.
        static bool IsDialogButton(ScreenRegionStats stats)
        {
            return stats.AverageLuminance > 45
                && stats.LuminanceStdDev > 25
                && stats.GreyRatio > 0.45
                && stats.DarkRatio < 0.45;
        }

        return IsDialogButton(cancelButton)
            && IsDialogButton(switchCharactersButton)
            && topBorder.AverageLuminance > 28
            && topBorder.GreyRatio > 0.25
            && topBorder.DarkRatio < 0.75
            && body.AverageLuminance < 40
            && body.DarkRatio > 0.75;
    }

    // Distinguishes the "Game is full" OK dialog from every other message D2R renders in the
    // same generic one-button box (same border, same body, same OK button at 0.500/0.539).
    // The box geometry alone can't tell them apart - the text CONTENT is the only difference,
    // and "Game is full" is the only variant whose message is a single line short enough to
    // leave both sides of the text row empty. Measured on the reference captures with a
    // 17-point grid (the sparse 9-grid can land between the thin glyphs):
    //   game_is_full.png:                 center std=45.7 bright=0.055; flanks std=3.3
    //                                     bright=0.000 dark=1.00; second line std=3.2
    //   game_password_doesnt_match.png:   flanks std=41.4-44.4 bright>=0.048
    //   game_exists_name.png:             flanks std=42.0-45.1 bright>=0.042
    //   game_no_longer_available (2-line): right flank std=55.1, second line std=25.8
    //   cant_join_hell.png (2-button):     flanks std=55.0-67.3 (also caught earlier by the
    //                                      two-button dialog check)
    // Callers must confirm the generic dialog geometry first; this only inspects the text.
    public static bool IsGameFullDialogTextBand(
        ScreenRegionStats textCenter,
        ScreenRegionStats textLeftFlank,
        ScreenRegionStats textRightFlank,
        ScreenRegionStats secondLine)
    {
        static bool IsEmptyBodyBand(ScreenRegionStats stats)
        {
            return stats.Samples > 0
                && stats.LuminanceStdDev < 15
                && stats.BrightRatio < 0.02
                && stats.DarkRatio > 0.95;
        }

        return textCenter.LuminanceStdDev > 20
            && textCenter.BrightRatio > 0.02
            && IsEmptyBodyBand(textLeftFlank)
            && IsEmptyBodyBand(textRightFlank)
            && IsEmptyBodyBand(secondLine);
    }

    public static bool IsLobbyTabReady(
        ScreenRegionStats tab,
        bool characterButtonPairReady,
        bool characterMenuReady)
    {
        return !characterButtonPairReady
            && !characterMenuReady
            && tab.AverageLuminance > 28
            && tab.GreyRatio > 0.25
            && tab.DarkRatio < 0.80;
    }

    // Distinguishes which lobby tab is active (Create Game vs Join Game), not just whether
    // the lobby is visible at all - IsLobbyTabReady already covers that. Thresholds were
    // measured against every docs/runbooks/assets/d2r-ui/1366x768/lobby_*.png capture plus
    // every non-lobby capture in that directory (char screens, in-game, splash/loading),
    // sampled at center (0.673,0.071) width 0.12 height 0.04 for Create, (0.766,0.071) same
    // size for Join - zero mismatches across all 49 reference captures with these bounds.
    //
    // LuminanceStdDev > 30 on the Create tab is the key guard against two real false
    // positives the unguarded lum/grey/dark thresholds alone produced: char_screen_act5.png's
    // bright Act5 background (std=26.9, lower than every real lobby capture's 42.8) and the
    // in-game "sitting_in_town*.png" captures, where the same screen coordinates land on a
    // flat decorative UI border (std=3-4) instead of tab text on a dark background. A real
    // active tab always has high-contrast text-on-dark texture; a coincidentally bright/grey
    // region elsewhere on screen usually doesn't.
    public static bool IsLobbyCreateTabActive(ScreenRegionStats stats)
    {
        return stats.AverageLuminance > 40
            && stats.LuminanceStdDev > 30
            && stats.GreyRatio > 0.45
            && stats.DarkRatio < 0.50;
    }

    // The Join tab's inactive-state luminance is lower than Create's (measured ~24 vs ~28),
    // so the false-positive shape here is different: char_screen_act5.png's Join-tab region
    // reads grey=0.75/dark=0.235 - high luminance, low dark ratio, unlike any real inactive
    // OR active Join tab capture (active: dark 0.35-0.50; inactive: dark > 0.90). The
    // AverageLuminance<48 and DarkRatio band (0.35-0.50) together exclude it without needing
    // a LuminanceStdDev guard like the Create tab.
    public static bool IsLobbyJoinTabActive(ScreenRegionStats stats)
    {
        return stats.AverageLuminance < 48
            && stats.GreyRatio > 0.40
            && stats.DarkRatio > 0.35
            && stats.DarkRatio < 0.50;
    }

    public static bool IsFriendsDrawerHeaderVisible(ScreenRegionStats stats)
    {
        return stats.AverageLuminance > 35
            && stats.GreyRatio > 0.45
            && stats.DarkRatio < 0.50;
    }

    public static bool IsFriendRowNameVisible(ScreenRegionStats stats)
    {
        return stats.AverageLuminance > 24
            && stats.GreyRatio > 0.18
            && stats.DarkRatio < 0.85;
    }

    public static bool IsLowGreyFriendRowNameVisible(ScreenRegionStats stats)
    {
        return stats.AverageLuminance > 32
            && stats.GreyRatio > 0.04
            && stats.DarkRatio < 0.93;
    }

    public static bool IsFriendRowMarkerVisible(ScreenRegionStats stats)
    {
        return stats.LuminanceStdDev > 18
            && stats.DarkRatio < 0.95
            && (stats.BrightRatio > 0.02 || stats.GreyRatio > 0.12 || stats.OrangeRatio > 0.02);
    }

    public static bool IsInGameHudProfile(
        ScreenRegionStats health,
        ScreenRegionStats mana,
        ScreenRegionStats hud,
        double healthRedThreshold,
        double manaBlueThreshold)
    {
        return health.RedRatio > healthRedThreshold
            && mana.BlueRatio > manaBlueThreshold
            && hud.AverageLuminance > 35
            && hud.LuminanceStdDev > 25
            && hud.DarkRatio < 0.80;
    }

    // D2R dims the whole scene while the Escape menu is open. That drops the globes and action
    // bar below the normal HUD-profile thresholds, but both globe colors remain visible and the
    // menu contributes distinctive, centered grey buttons. Requiring the entire combination
    // avoids treating generic dark menus, loading art, or coincidental red/blue scenery as an
    // in-game pause.
    //
    // Only the top three rows - Options, Save and Exit, Return to Game - are required. Reign of
    // the Warlock appends Loot Filter and Chronicle below a divider without moving those three:
    // measured on the reference captures, all three occupy identical scanlines before and after
    // the expansion (rows 271-292, 321-342, 371-392 at 1366x768). Gating on the two new rows as
    // well would buy nothing and would blind the detector on any client that has not taken the
    // expansion yet, so they are reported separately by HasExpansionPauseMenuRows instead.
    public static bool IsSaveAndExitMenu(
        ScreenRegionStats health,
        ScreenRegionStats mana,
        ScreenRegionStats optionsButton,
        ScreenRegionStats saveAndExitButton,
        ScreenRegionStats returnToGameButton)
    {
        return health.RedRatio > 0.12
            && mana.BlueRatio > 0.20
            && IsPauseMenuButton(optionsButton)
            && IsPauseMenuButton(saveAndExitButton)
            && IsPauseMenuButton(returnToGameButton);
    }

    // Distinguishes Reign of the Warlock's five-row pause menu from the pre-expansion three-row
    // one. Purely observational - nothing clicks these rows and no flow branches on the result -
    // but it is what lets a status/telemetry line say which menu a client is actually rendering,
    // which is the difference between "this VM never took the expansion" and "the anchors moved".
    //
    // The separation is not marginal. On the pre-expansion captures these two bands are the
    // dimmed game world with no menu chrome at all (modern_gfx_* and save_and_exit_resurrected:
    // std 3.9-5.7, dark 1.00); on the RoTW capture they are lit button faces (std 42.0-48.4,
    // dark 0.35-0.36). Requiring both rows keeps a bright patch of scenery behind one band from
    // being read as an expansion menu.
    public static bool HasExpansionPauseMenuRows(
        ScreenRegionStats lootFilterButton,
        ScreenRegionStats chronicleButton)
    {
        static bool IsExpansionRow(ScreenRegionStats stats)
        {
            return stats.Samples > 0
                && stats.LuminanceStdDev > 25
                && stats.GreyRatio > 0.40
                && stats.DarkRatio < 0.60;
        }

        return IsExpansionRow(lootFilterButton)
            && IsExpansionRow(chronicleButton);
    }

    private static bool IsPauseMenuButton(ScreenRegionStats stats)
    {
        return stats.AverageLuminance > 60
            && stats.LuminanceStdDev > 40
            && stats.GreyRatio > 0.60
            && stats.DarkRatio < 0.35;
    }

    public static bool IsInGameHudFrame(
        ScreenRegionStats actionHud,
        ScreenRegionStats bottomHud,
        ScreenRegionStats centerHud)
    {
        var actionBarVisible = actionHud.LuminanceStdDev > 30
            && actionHud.DarkRatio < 0.85
            && (actionHud.BrightRatio > 0.020 || actionHud.GreyRatio > 0.16);
        var bottomHudVisible = bottomHud.LuminanceStdDev > 28
            && bottomHud.DarkRatio < 0.85;
        var centerHudVisible = centerHud.LuminanceStdDev > 32
            && centerHud.DarkRatio < 0.80
            && (centerHud.BrightRatio > 0.025 || centerHud.GreyRatio > 0.20);

        return actionBarVisible
            && bottomHudVisible
            && centerHudVisible;
    }

    public static bool IsGameEntryMenuVisible(
        bool tabReady,
        bool entryButtonReady,
        bool formPanelReady)
    {
        return tabReady
            ? entryButtonReady || formPanelReady
            : entryButtonReady && formPanelReady;
    }

    // The game-load screens (load_screen_phase_1/2.png) and the post-intro loading splash
    // (loading_splash_after_intro_videos.png) draw artwork only inside a centered panel
    // (roughly x 0.30-0.71, y 0.25-0.75 at 1366x768); everything outside it is a literal
    // black fill. These regions all sit well clear of that panel, and every one of them must
    // read near-black for the stuck-load-screen watchdog to consider quitting the client.
    // The bottom-center region doubles as the in-game guard: it overlaps the HUD/action bar,
    // which reads bright on every in-game reference capture (dark ratio 0.28-0.90, never
    // >= 0.98) - even the dimmed modern-graphics Save and Exit captures and the night-time
    // party_glitch captures fail here, so a live game can never satisfy the full set.
    public static readonly ScreenSampleRegion[] LoadScreenSurroundRegions =
    [
        new(0.10, 0.10, 0.14, 0.12),
        new(0.90, 0.10, 0.14, 0.12),
        new(0.10, 0.90, 0.14, 0.12),
        new(0.50, 0.08, 0.24, 0.10),
        new(0.50, 0.92, 0.24, 0.10)
    ];

    // Calibrated against every full-page reference capture (see StuckLoadScreenSurroundTests):
    // the black fill reads lum 0.0/dark 1.00 exactly; the closest non-load screens are the
    // modern Save and Exit pause (top-left lum 9.7-9.9 but top-right/bottom-center well over)
    // and the post-intro flame splash (bottom-center dark 0.96). Samples > 0 matters on the
    // live path: a TryRunBounded timeout fallback never sampled anything, and "couldn't read
    // the screen" must not count as "screen is black" (the v0.2.93 DWM stall would otherwise
    // look exactly like a stuck load screen and get healthy clients killed).
    public static bool IsLoadScreenSurroundRegion(ScreenRegionStats stats)
    {
        return stats.Samples > 0
            && stats.AverageLuminance < 10
            && stats.DarkRatio >= 0.98;
    }

    // D2R's first-run "Gamma Calibration" screen, which it shows on the way from the intro
    // videos to character select when it has decided Settings.json is unusable and rewritten it
    // from defaults. The client never advances past it on its own, so recognizing it is what
    // separates "this VM's settings file is corrupt" from a generic Unknown frame.
    //
    // The anchor is the 11-swatch greyscale ramp across the middle of the screen, sampled as
    // five patches left to right. Deliberately NOT the Diablo head above it: that logo is
    // rendered at whatever the current gamma is (the screen's instruction is literally "adjust
    // so the logo is barely visible"), so its brightness is the one thing on this screen
    // guaranteed to vary. The ramp varies too, but its SHAPE does not - it stays monotonic with
    // a wide span at every gamma setting. Measured on gamma_calibration_settings_reset.png
    // remapped from gamma 0.35 to 3.0: patch luminances stay strictly increasing throughout and
    // the span never drops below 170 (222 at the captured setting).
    //
    // Margins against every other reference capture (90 of them, see
    // GammaCalibrationScreenTests): not one is monotonic across these five patches at all, and
    // the widest span any of them produces is 40.4 against this screen's 222.2. The black flanks
    // either side of the ramp are the secondary confirmation - 32 captures have them (intro and
    // load screens, which are mostly black), but none of those has the ramp.
    public const double GammaRampPatchWidthRatio = 0.035;
    public const double GammaRampPatchHeightRatio = 0.045;
    private const double GammaRampCenterY = 0.657;

    public static readonly ScreenSampleRegion[] GammaCalibrationRampPatches =
    [
        new(0.285, GammaRampCenterY, GammaRampPatchWidthRatio, GammaRampPatchHeightRatio),
        new(0.395, GammaRampCenterY, GammaRampPatchWidthRatio, GammaRampPatchHeightRatio),
        new(0.500, GammaRampCenterY, GammaRampPatchWidthRatio, GammaRampPatchHeightRatio),
        new(0.610, GammaRampCenterY, GammaRampPatchWidthRatio, GammaRampPatchHeightRatio),
        new(0.720, GammaRampCenterY, GammaRampPatchWidthRatio, GammaRampPatchHeightRatio)
    ];

    // The ramp spans x 0.250-0.750 exactly; these sit clear of both ends on the same row.
    public static readonly ScreenSampleRegion GammaCalibrationLeftFlank = new(0.10, GammaRampCenterY, 0.14, 0.06);
    public static readonly ScreenSampleRegion GammaCalibrationRightFlank = new(0.90, GammaRampCenterY, 0.14, 0.06);

    public static bool IsGammaCalibrationScreen(
        IReadOnlyList<ScreenRegionStats> rampPatches,
        ScreenRegionStats leftFlank,
        ScreenRegionStats rightFlank)
    {
        // Samples > 0 everywhere for the same reason the stuck-load-screen watchdog demands it:
        // a bounded-sampling timeout returns empty stats, and "couldn't read the screen" must
        // never be able to satisfy a check that quits and rewrites a live client's settings.
        if (rampPatches.Count < 4 || rampPatches.Any(patch => patch.Samples <= 0))
        {
            return false;
        }

        for (var index = 1; index < rampPatches.Count; index++)
        {
            if (rampPatches[index].AverageLuminance - rampPatches[index - 1].AverageLuminance <= GammaRampMinStep)
            {
                return false;
            }
        }

        var span = rampPatches[^1].AverageLuminance - rampPatches[0].AverageLuminance;
        return span > GammaRampMinSpan
            && rampPatches[0].AverageLuminance < GammaRampMaxDarkEnd
            && rampPatches[^1].AverageLuminance > GammaRampMinBrightEnd
            && IsGammaCalibrationFlank(leftFlank)
            && IsGammaCalibrationFlank(rightFlank);
    }

    // A step floor rather than 0 keeps a shallow gradient (a dim scene lit from one side) from
    // reading as a ramp, but stays under the 4.9 minimum measured at the darkest gamma remap.
    private const double GammaRampMinStep = 2.0;
    private const double GammaRampMinSpan = 100.0;
    private const double GammaRampMaxDarkEnd = 110.0;
    private const double GammaRampMinBrightEnd = 140.0;

    private static bool IsGammaCalibrationFlank(ScreenRegionStats stats)
    {
        return stats.Samples > 0
            && stats.AverageLuminance < 12
            && stats.DarkRatio >= 0.95;
    }

    private static bool IsCharacterMenuButtonRegion(ScreenRegionStats stats)
    {
        return stats.AverageLuminance > 40
            && stats.GreyRatio > 0.35
            && stats.DarkRatio < 0.65;
    }
}

internal readonly record struct ScreenSampleRegion(
    double CenterX,
    double CenterY,
    double WidthRatio,
    double HeightRatio);
