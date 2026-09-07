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
[Collection(BoundedCallCollection.Name)]
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

    // The action blocks on a gate rather than a fixed sleep so the hang lasts exactly as long as
    // the assertion needs, and the abandoned call's semaphore slot is handed back before this
    // test returns. See BoundedCallSlots for why that matters to the saturation test below.
    [Fact]
    public void DoesNotBlockPastTheTimeoutWhenTheActionHangs()
    {
        using var release = new ManualResetEventSlim(false);
        var slotsBefore = BoundedCallSlots.Available;
        var stopwatch = Stopwatch.StartNew();

        var result = VmOperations.TryRunBounded(
            () =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                return true;
            },
            TimeoutMs);

        stopwatch.Stop();

        Assert.False(result);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 2000,
            $"TryRunBounded should give up around {TimeoutMs}ms, not wait out the hung action; took {stopwatch.ElapsedMilliseconds}ms.");

        release.Set();
        BoundedCallSlots.WaitForAtLeast(slotsBefore);
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
        using var release = new ManualResetEventSlim(false);
        var slotsBefore = BoundedCallSlots.Available;
        var stopwatch = Stopwatch.StartNew();

        var result = VmOperations.TryRunBounded(
            () =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                return "done";
            },
            TimeoutMs,
            "fallback");

        stopwatch.Stop();

        Assert.Equal("fallback", result);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 2000,
            $"TryRunBounded should give up around {TimeoutMs}ms, not wait out the hung action; took {stopwatch.ElapsedMilliseconds}ms.");

        release.Set();
        BoundedCallSlots.WaitForAtLeast(slotsBefore);
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

    // watch-kfwuq5-20260625-191907.log: ThreadPool.ThreadCount on a live VM climbed monotonically
    // from 7 to 98 over ~2 minutes once GDI calls started hanging instead of merely being slow -
    // every TryRunBounded call abandons its background thread forever if the action never
    // returns, since Task.Wait(timeoutMs) only stops waiting, it doesn't cancel anything. This
    // pins the fix: once MaxConcurrentBoundedCalls calls are genuinely still in flight (not just
    // "recently called" - actually still running, still holding their slot), a new call must
    // fail fast with the fallback instead of spawning yet another thread that will never come
    // back either. Without this, the 33rd call would block for its own full timeout *and* leak
    // another thread, same as the live failure.
    [Fact]
    public async Task FailsFastInsteadOfSpawningAnotherThreadOnceConcurrencyCapIsSaturated()
    {
        using var release = new ManualResetEventSlim(false);

        // The cap under test is process-global, so this saturates whatever share of it is actually
        // free rather than assuming the whole thing is. Demanding all 32 made this test assert a
        // property no single test can hold - the semaphore is static, so one slot held anywhere in
        // the test host failed it before it began, and then failed every test after it. Run
        // 34168441446 failed three that way at 25, 27 and 27 of 32. The behaviour being pinned is
        // "once the cap is saturated, the next call fails fast instead of spawning another
        // thread", and that is just as true of a cap with some slots already spoken for.
        var slotsBefore = BoundedCallSlots.Available;

        // The pool's default thread-injection rate is gradual under sudden burst load, and
        // SetMinThreads only raises the target the pool grows toward - it doesn't force immediate
        // creation. Without forcing real threads to exist first, this test saw up to 12 of the
        // saturating calls never start even after a 10s wait. Queuing and waiting on trivial work
        // items forces genuine creation, since each can only complete by actually running on a
        // distinct thread.
        const int warmupThreads = VmOperations.MaxConcurrentBoundedCalls + 8;
        ThreadPool.GetMinThreads(out var minWorkerThreads, out var minCompletionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(minWorkerThreads, warmupThreads), minCompletionPortThreads);
        using (var warmupStarted = new CountdownEvent(warmupThreads))
        using (var warmupRelease = new ManualResetEventSlim(false))
        {
            var warmupTasks = new Task[warmupThreads];
            for (var i = 0; i < warmupThreads; i++)
            {
                warmupTasks[i] = Task.Run(() =>
                {
                    warmupStarted.Signal();
                    warmupRelease.Wait(TimeSpan.FromSeconds(10));
                });
            }

            Assert.True(warmupStarted.Wait(TimeSpan.FromSeconds(15)), "Thread-pool warmup did not reach the expected thread count in time.");
            warmupRelease.Set();
            var warmupAll = Task.WhenAll(warmupTasks);
            var warmupWinner = await Task.WhenAny(warmupAll, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(warmupAll, warmupWinner); // "Thread-pool warmup tasks did not finish."
        }

        // The warmup above queues and drains its own pool work, so re-read the free capacity rather
        // than trusting the count taken before it.
        var capacity = BoundedCallSlots.Available;
        Assert.True(
            capacity > 0,
            "No bounded-call slots were free, so there is no cap left to saturate in this test host.");

        using var allStarted = new CountdownEvent(capacity);

        // Dedicated OS threads for the dispatch side, not Task.Run, so the 32 saturating calls
        // don't have to compete with their own 32 *inner* TryRunBounded-spawned tasks for pool
        // capacity on the dispatch side too.
        // The saturating calls use a much longer timeoutMs than TimeoutMs (250ms) deliberately:
        // TryRunBounded only returns once task.Wait(timeoutMs) is satisfied, and the slot is only
        // released inside that nested task's own finally, after the action itself returns. A
        // short timeoutMs would let TryRunBounded give up and return well before the action (and
        // its Release()) actually finishes, making the dispatch thread's Join() below complete
        // without proving the slot was freed - which is exactly what leaked saturation into the
        // next test the first time this was written with TimeoutMs here instead.
        const int saturatingCallTimeoutMs = 25_000;
        var dispatchThreads = new Thread[capacity];
        for (var i = 0; i < capacity; i++)
        {
            var thread = new Thread(() => VmOperations.TryRunBounded(
                () =>
                {
                    allStarted.Signal();
                    release.Wait(TimeSpan.FromSeconds(20));
                    return true;
                },
                saturatingCallTimeoutMs))
            {
                IsBackground = true
            };
            dispatchThreads[i] = thread;
            thread.Start();
        }

        Assert.True(
            allStarted.Wait(TimeSpan.FromSeconds(10)),
            $"All {capacity} saturating calls should have started and acquired a slot within 10s. Remaining: {allStarted.CurrentCount}.");

        var stopwatch = Stopwatch.StartNew();
        var result = VmOperations.TryRunBounded(() => true, 2000);
        stopwatch.Stop();

        release.Set();
        foreach (var thread in dispatchThreads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Saturating calls should release their slots once unblocked.");
        }

        BoundedCallSlots.WaitForAtLeast(slotsBefore);

        Assert.False(result);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 500,
            $"A call made while the cap is saturated should fail fast, not wait near its own 2000ms timeout; took {stopwatch.ElapsedMilliseconds}ms.");
    }
}
