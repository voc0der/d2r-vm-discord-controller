using System.Diagnostics;
using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

// Regression coverage for the v0.2.39 -> v0.2.46 focus-steal bug: startup bursts dropped
// foreground/focus negotiation entirely because SetForegroundWindow/AttachThreadInput could
// hang for tens of seconds on a live VM, which left D2R never focused and every SendInput
// click landing on whatever window already had focus instead. v0.2.46 brought the focus
// attempt back but bounded to one detection cycle via VmOperations.TryRunBounded. These tests
// pin the bounding behavior itself, independent of the actual Win32 focus call.
public sealed class TryRunBoundedTests
{
    private const int TimeoutMs = 250;

    [Fact]
    public void ReturnsTrueWhenActionCompletesQuicklyAndSucceeds()
    {
        Assert.True(VmOperations.TryRunBounded(() => true, TimeoutMs));
    }

    [Fact]
    public void ReturnsFalseWhenActionCompletesQuicklyButFails()
    {
        Assert.False(VmOperations.TryRunBounded(() => false, TimeoutMs));
    }

    [Fact]
    public void DoesNotBlockPastTheTimeoutWhenTheActionHangs()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = VmOperations.TryRunBounded(
            () =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(5));
                return true;
            },
            TimeoutMs);

        stopwatch.Stop();

        Assert.False(result);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 2000,
            $"TryRunBounded should give up around {TimeoutMs}ms, not wait out the hung action; took {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public void ReturnsFalseInsteadOfThrowingWhenTheActionThrows()
    {
        var result = VmOperations.TryRunBounded(
            () => throw new InvalidOperationException("simulated Win32 failure"),
            TimeoutMs);

        Assert.False(result);
    }

    // Generic overload added for the classifier-breakdown diagnostics (watch-xy4wiew2-
    // 20260625-132336.log: ComputeVisibleStateClassifierBreakdown's ~25-35 unbounded GDI
    // region samples froze a deadline-boundary checkpoint for 1m19s) - same bounding shape as
    // the bool overload above, with a caller-supplied fallback instead of false.
    [Fact]
    public void GenericOverloadReturnsResultWhenActionCompletesQuickly()
    {
        Assert.Equal("done", VmOperations.TryRunBounded(() => "done", TimeoutMs, "fallback"));
    }

    [Fact]
    public void GenericOverloadReturnsFallbackWhenTheActionHangs()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = VmOperations.TryRunBounded(
            () =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(5));
                return "done";
            },
            TimeoutMs,
            "fallback");

        stopwatch.Stop();

        Assert.Equal("fallback", result);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 2000,
            $"TryRunBounded should give up around {TimeoutMs}ms, not wait out the hung action; took {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public void GenericOverloadReturnsFallbackInsteadOfThrowingWhenTheActionThrows()
    {
        var result = VmOperations.TryRunBounded(
            () => throw new InvalidOperationException("simulated Win32 failure"),
            TimeoutMs,
            "fallback");

        Assert.Equal("fallback", result);
    }
}
