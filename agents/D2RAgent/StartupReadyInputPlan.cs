using AgentCommon;

namespace D2RAgent;

internal enum StartupReadyInputAction
{
    FocusD2R,
    ClickWindowCenter,
    ClickIntroPoint,
    SendWindowClickIntroPoint,
    PressStartupSkipKey,
    PressStartKey,
    SendWindowStartupSkipKey,
    SendWindowReadyBurst,
    PressEscapeKey,
    SendWindowEscapeKey
}

internal sealed record StartupReadyInputPlan(
    int IntroClickCount,
    int IntroClickDelayMs,
    int TitleScreenKeyPressCount,
    int TitleScreenKeyPressDelayMs)
{
    private const int MaxIntroClickCount = 120;
    private const int MaxTitleScreenKeyPressCount = 30;

    // Escape (PressEscapeKey/SendWindowEscapeKey/SendWindowReadyBurst's includeEscape) and
    // Space+Enter (PressStartKey/SendWindowReadyBurst) used to be in these plans because, under
    // the pre-v0.2.93 detection delays, the bursts rarely all landed - by the time GDI sampling
    // unblocked, the client had usually already moved on. Now that detection is fast, every send
    // in a burst reliably reaches the client, and Escape-then-Enter is exactly D2R's open-then-
    // confirm-exit-dialog sequence: watch-lmrwii244-20260625-205519.log showed it quitting D2R
    // outright on 2 VMs ("frame NotRunning" right after a burst). User confirmed directly in a
    // live VM that G alone clears the intro/title sequence just as fast as the old combination -
    // G only ever toggles legacy graphics, so it can't open or confirm anything. These plans now
    // send only G (both delivery mechanisms, scan-code and window-targeted, for the same
    // redundancy the old plans had against an unfocused/not-yet-existing window), plus the
    // pre-existing focus/click actions, which were never implicated in this failure mode.
    public static readonly IReadOnlyList<StartupReadyInputAction> IntroActions =
    [
        StartupReadyInputAction.FocusD2R,
        StartupReadyInputAction.ClickIntroPoint,
        StartupReadyInputAction.PressStartupSkipKey,
        StartupReadyInputAction.SendWindowClickIntroPoint,
        StartupReadyInputAction.SendWindowStartupSkipKey
    ];

    public static readonly IReadOnlyList<StartupReadyInputAction> TitleActions =
    [
        StartupReadyInputAction.FocusD2R,
        StartupReadyInputAction.PressStartupSkipKey,
        StartupReadyInputAction.SendWindowStartupSkipKey
    ];

    public static readonly IReadOnlyList<StartupReadyInputAction> SplashActions =
    [
        StartupReadyInputAction.FocusD2R,
        StartupReadyInputAction.ClickIntroPoint,
        StartupReadyInputAction.PressStartupSkipKey,
        StartupReadyInputAction.SendWindowClickIntroPoint,
        StartupReadyInputAction.SendWindowStartupSkipKey
    ];

    public static readonly IReadOnlyList<StartupReadyInputAction> BurstActions =
    [
        StartupReadyInputAction.FocusD2R,
        StartupReadyInputAction.ClickIntroPoint,
        StartupReadyInputAction.PressStartupSkipKey,
        StartupReadyInputAction.SendWindowClickIntroPoint,
        StartupReadyInputAction.SendWindowStartupSkipKey
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
