using AgentCommon;

namespace D2RAgent;

internal enum StartupReadyInputAction
{
    FocusD2R,
    ClickIntroPoint,
    SendWindowClickIntroPoint,
    PressStartupSkipKey,
    PressStartKey,
    SendWindowStartupSkipKey,
    SendWindowReadyBurst
}

internal sealed record StartupReadyInputPlan(
    int IntroClickCount,
    int IntroClickDelayMs,
    int TitleScreenKeyPressCount,
    int TitleScreenKeyPressDelayMs)
{
    private const int MaxIntroClickCount = 120;
    private const int MaxTitleScreenKeyPressCount = 30;

    public static readonly IReadOnlyList<StartupReadyInputAction> BurstActions =
    [
        StartupReadyInputAction.FocusD2R,
        StartupReadyInputAction.ClickIntroPoint,
        StartupReadyInputAction.SendWindowClickIntroPoint,
        StartupReadyInputAction.PressStartupSkipKey,
        StartupReadyInputAction.PressStartKey,
        StartupReadyInputAction.SendWindowStartupSkipKey,
        StartupReadyInputAction.SendWindowReadyBurst
    ];

    public static StartupReadyInputPlan FromConfig(D2RUiAutomationConfig config)
    {
        return new StartupReadyInputPlan(
            Math.Clamp(config.IntroClickCount, 0, MaxIntroClickCount),
            Math.Clamp(config.IntroClickDelayMs, 50, 1000),
            Math.Clamp(config.TitleScreenKeyPressCount, 0, MaxTitleScreenKeyPressCount),
            Math.Clamp(config.TitleScreenKeyPressDelayMs, 50, 2000));
    }

    public int EstimatedTimeoutSeconds
    {
        get
        {
            var totalMs = (IntroClickCount * IntroClickDelayMs)
                + (TitleScreenKeyPressCount * TitleScreenKeyPressDelayMs);
            return Math.Max(1, (int)Math.Ceiling(totalMs / 1000.0));
        }
    }
}
