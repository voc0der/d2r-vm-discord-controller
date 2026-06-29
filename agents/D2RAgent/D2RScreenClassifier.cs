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

    private static bool IsCharacterMenuButtonRegion(ScreenRegionStats stats)
    {
        return stats.AverageLuminance > 40
            && stats.GreyRatio > 0.35
            && stats.DarkRatio < 0.65;
    }
}
