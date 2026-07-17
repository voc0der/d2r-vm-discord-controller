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
    public static readonly LoadScreenSurroundRegion[] LoadScreenSurroundRegions =
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

    private static bool IsCharacterMenuButtonRegion(ScreenRegionStats stats)
    {
        return stats.AverageLuminance > 40
            && stats.GreyRatio > 0.35
            && stats.DarkRatio < 0.65;
    }
}

internal readonly record struct LoadScreenSurroundRegion(
    double CenterX,
    double CenterY,
    double WidthRatio,
    double HeightRatio);
