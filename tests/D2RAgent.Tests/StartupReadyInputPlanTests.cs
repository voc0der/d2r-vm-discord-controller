using AgentCommon;
using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

public sealed class StartupReadyInputPlanTests
{
    [Fact]
    public void DefaultPlanRestoresBruteForceIntroAndTitleSkipping()
    {
        var plan = StartupReadyInputPlan.FromConfig(new D2RUiAutomationConfig());

        Assert.Equal(80, plan.IntroClickCount);
        Assert.Equal(250, plan.IntroClickDelayMs);
        Assert.Equal(6, plan.TitleScreenKeyPressCount);
        Assert.Equal(500, plan.TitleScreenKeyPressDelayMs);
        Assert.Equal(23, plan.EstimatedTimeoutSeconds);
    }

    [Fact]
    public void BurstPlanIncludesMouseKeyboardAndWindowMessageFallbacks()
    {
        Assert.Equal(
            [
                StartupReadyInputAction.FocusD2R,
                StartupReadyInputAction.ClickWindowCenter,
                StartupReadyInputAction.PressEscapeKey,
                StartupReadyInputAction.SendWindowEscapeKey,
                StartupReadyInputAction.ClickIntroPoint,
                StartupReadyInputAction.SendWindowClickIntroPoint,
                StartupReadyInputAction.PressStartupSkipKey,
                StartupReadyInputAction.PressStartKey,
                StartupReadyInputAction.SendWindowStartupSkipKey
            ],
            StartupReadyInputPlan.IntroActions);

        Assert.Equal(
            [
                StartupReadyInputAction.FocusD2R,
                StartupReadyInputAction.ClickWindowCenter,
                StartupReadyInputAction.PressStartupSkipKey,
                StartupReadyInputAction.PressStartKey,
                StartupReadyInputAction.SendWindowStartupSkipKey,
                StartupReadyInputAction.SendWindowReadyBurst
            ],
            StartupReadyInputPlan.TitleActions);

        Assert.Equal(
            [
                StartupReadyInputAction.FocusD2R,
                StartupReadyInputAction.ClickWindowCenter,
                StartupReadyInputAction.ClickIntroPoint,
                StartupReadyInputAction.SendWindowClickIntroPoint,
                StartupReadyInputAction.PressStartupSkipKey,
                StartupReadyInputAction.PressStartKey,
                StartupReadyInputAction.SendWindowStartupSkipKey,
                StartupReadyInputAction.SendWindowReadyBurst
            ],
            StartupReadyInputPlan.SplashActions);

        Assert.Equal(
            [
                StartupReadyInputAction.FocusD2R,
                StartupReadyInputAction.ClickWindowCenter,
                StartupReadyInputAction.ClickIntroPoint,
                StartupReadyInputAction.SendWindowClickIntroPoint,
                StartupReadyInputAction.PressStartupSkipKey,
                StartupReadyInputAction.PressStartKey,
                StartupReadyInputAction.SendWindowStartupSkipKey,
                StartupReadyInputAction.SendWindowReadyBurst
            ],
            StartupReadyInputPlan.BurstActions);
    }

    [Fact]
    public void PlanClampsBadConfigWithoutDisablingStartupFallbacks()
    {
        var plan = StartupReadyInputPlan.FromConfig(new D2RUiAutomationConfig
        {
            IntroClickCount = 999,
            IntroClickDelayMs = 1,
            TitleScreenKeyPressCount = 999,
            TitleScreenKeyPressDelayMs = 1
        });

        Assert.Equal(120, plan.IntroClickCount);
        Assert.Equal(50, plan.IntroClickDelayMs);
        Assert.Equal(30, plan.TitleScreenKeyPressCount);
        Assert.Equal(50, plan.TitleScreenKeyPressDelayMs);
    }
}
