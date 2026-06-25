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

    // watch-lmrwii244-20260625-205519.log: now that v0.2.93 made detection fast, every send in a
    // burst reliably reaches the client - Escape-then-Enter (the old plans, "for redundancy")
    // is exactly D2R's open-then-confirm-exit-dialog sequence, and it quit the game outright on
    // 2 VMs. User confirmed directly in a live VM that G alone clears intro/title just as fast.
    // These plans must never include PressEscapeKey/SendWindowEscapeKey/PressStartKey/
    // SendWindowReadyBurst again - only the two G-pressing actions, plus the click/focus actions
    // that were never implicated.
    [Fact]
    public void StartupPlansOnlySendTheSafeSkipKeyNeverEscapeOrEnter()
    {
        Assert.Equal(
            [
                StartupReadyInputAction.FocusD2R,
                StartupReadyInputAction.ClickIntroPoint,
                StartupReadyInputAction.PressStartupSkipKey,
                StartupReadyInputAction.SendWindowClickIntroPoint,
                StartupReadyInputAction.SendWindowStartupSkipKey
            ],
            StartupReadyInputPlan.IntroActions);

        Assert.Equal(
            [
                StartupReadyInputAction.FocusD2R,
                StartupReadyInputAction.PressStartupSkipKey,
                StartupReadyInputAction.SendWindowStartupSkipKey
            ],
            StartupReadyInputPlan.TitleActions);

        Assert.Equal(
            [
                StartupReadyInputAction.FocusD2R,
                StartupReadyInputAction.ClickIntroPoint,
                StartupReadyInputAction.PressStartupSkipKey,
                StartupReadyInputAction.SendWindowClickIntroPoint,
                StartupReadyInputAction.SendWindowStartupSkipKey
            ],
            StartupReadyInputPlan.SplashActions);

        Assert.Equal(
            [
                StartupReadyInputAction.FocusD2R,
                StartupReadyInputAction.ClickIntroPoint,
                StartupReadyInputAction.PressStartupSkipKey,
                StartupReadyInputAction.SendWindowClickIntroPoint,
                StartupReadyInputAction.SendWindowStartupSkipKey
            ],
            StartupReadyInputPlan.BurstActions);

        foreach (var plan in new[]
                 {
                     StartupReadyInputPlan.IntroActions,
                     StartupReadyInputPlan.TitleActions,
                     StartupReadyInputPlan.SplashActions,
                     StartupReadyInputPlan.BurstActions
                 })
        {
            Assert.DoesNotContain(StartupReadyInputAction.PressEscapeKey, plan);
            Assert.DoesNotContain(StartupReadyInputAction.SendWindowEscapeKey, plan);
            Assert.DoesNotContain(StartupReadyInputAction.PressStartKey, plan);
            Assert.DoesNotContain(StartupReadyInputAction.SendWindowReadyBurst, plan);
        }
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
