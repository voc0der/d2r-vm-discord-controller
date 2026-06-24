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
                StartupReadyInputAction.PressEscapeKey,
                StartupReadyInputAction.ClickIntroPoint,
                StartupReadyInputAction.PressStartupSkipKey,
                StartupReadyInputAction.PressStartKey,
                StartupReadyInputAction.SendWindowEscapeKey,
                StartupReadyInputAction.SendWindowClickIntroPoint,
                StartupReadyInputAction.SendWindowReadyBurst,
                StartupReadyInputAction.SendWindowStartupSkipKey
            ],
            StartupReadyInputPlan.IntroActions);

        Assert.Equal(
            [
                StartupReadyInputAction.PressStartupSkipKey,
                StartupReadyInputAction.PressStartKey,
                StartupReadyInputAction.SendWindowReadyBurst,
                StartupReadyInputAction.SendWindowStartupSkipKey
            ],
            StartupReadyInputPlan.TitleActions);

        Assert.Equal(
            [
                StartupReadyInputAction.ClickIntroPoint,
                StartupReadyInputAction.PressStartupSkipKey,
                StartupReadyInputAction.PressStartKey,
                StartupReadyInputAction.SendWindowClickIntroPoint,
                StartupReadyInputAction.SendWindowReadyBurst,
                StartupReadyInputAction.SendWindowStartupSkipKey
            ],
            StartupReadyInputPlan.SplashActions);

        Assert.Equal(
            [
                StartupReadyInputAction.ClickIntroPoint,
                StartupReadyInputAction.PressStartupSkipKey,
                StartupReadyInputAction.PressStartKey,
                StartupReadyInputAction.SendWindowClickIntroPoint,
                StartupReadyInputAction.SendWindowReadyBurst,
                StartupReadyInputAction.SendWindowStartupSkipKey
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
