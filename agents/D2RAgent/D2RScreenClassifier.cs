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

    public static bool IsLobbyTabRegion(ScreenRegionStats stats)
    {
        return stats.AverageLuminance > 28
            && stats.GreyRatio > 0.25
            && stats.DarkRatio < 0.80;
    }

    public static bool IsLobbyTabReady(
        ScreenRegionStats tab,
        bool characterButtonPairReady,
        bool characterMenuReady)
    {
        return !characterButtonPairReady
            && !characterMenuReady
            && IsLobbyTabRegion(tab);
    }

    private static bool IsCharacterMenuButtonRegion(ScreenRegionStats stats)
    {
        return stats.AverageLuminance > 40
            && stats.GreyRatio > 0.35
            && stats.DarkRatio < 0.65;
    }
}
