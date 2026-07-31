namespace D2RAgent;

/// <summary>
/// Pixel gates for the narrowly-scoped Battle.net install-location repair flow. The primary
/// Install and Play buttons are intentionally not distinguished: they are visually identical,
/// so repair authorization comes only from the two-button Installation Required modal.
/// </summary>
internal static class BattleNetScreenClassifier
{
    public static bool IsInstallationRequiredModal(
        ScreenRegionStats continueButton,
        ScreenRegionStats cancelButton)
    {
        return continueButton.Samples > 0
            && continueButton.BlueRatio > 0.75
            && continueButton.AverageLuminance > 80
            && continueButton.DarkRatio < 0.15
            && cancelButton.Samples > 0
            && cancelButton.GreyRatio > 0.65
            && cancelButton.AverageLuminance > 50
            && cancelButton.DarkRatio < 0.20
            && cancelButton.LuminanceStdDev > 30;
    }

    public static bool IsPrimaryActionReady(ScreenRegionStats actionButton)
    {
        return actionButton.Samples > 0
            && actionButton.BlueRatio > 0.80
            && actionButton.AverageLuminance > 75
            && actionButton.DarkRatio < 0.15;
    }

    public static bool IsInstallLocationConfirmation(
        ScreenRegionStats startInstallButton,
        ScreenRegionStats title)
    {
        return startInstallButton.Samples > 0
            && startInstallButton.BlueRatio > 0.70
            && startInstallButton.AverageLuminance > 80
            && startInstallButton.DarkRatio < 0.15
            && title.Samples > 0
            && title.BrightRatio > 0.15
            && title.LuminanceStdDev > 60
            && title.DarkRatio > 0.55;
    }
}
