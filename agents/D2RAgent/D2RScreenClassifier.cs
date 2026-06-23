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
        return logo.OrangeRatio > 0.05
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
