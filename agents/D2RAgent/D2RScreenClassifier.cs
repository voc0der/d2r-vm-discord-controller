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
        ScreenRegionStats leftGlobe,
        ScreenRegionStats rightGlobe,
        ScreenRegionStats actionHud,
        ScreenRegionStats bottomHud)
    {
        var broadGlobePair = leftGlobe.RedRatio > 0.045
            && rightGlobe.BlueRatio > 0.045;
        var bottomHudColors = bottomHud.RedRatio > 0.025
            && bottomHud.BlueRatio > 0.020;
        var actionBarVisible = actionHud.LuminanceStdDev > 30
            && actionHud.DarkRatio < 0.85
            && (actionHud.BrightRatio > 0.020 || actionHud.GreyRatio > 0.16);
        var bottomHudVisible = bottomHud.LuminanceStdDev > 30
            && bottomHud.DarkRatio < 0.85;

        return actionBarVisible
            && bottomHudVisible
            && (broadGlobePair || bottomHudColors);
    }

    private static bool IsCharacterMenuButtonRegion(ScreenRegionStats stats)
    {
        return stats.AverageLuminance > 40
            && stats.GreyRatio > 0.35
            && stats.DarkRatio < 0.65;
    }
}
