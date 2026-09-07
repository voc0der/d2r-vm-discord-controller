using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

/// <summary>
/// Helper for tests that deliberately hang a <see cref="VmOperations.TryRunBounded"/> action.
/// </summary>
/// <remarks>
/// <para>
/// <c>TryRunBounded</c> takes its semaphore slot synchronously on the calling thread and releases
/// it in the <c>finally</c> of the <c>Task.Run</c> body - which is the whole point, since a call
/// that gives up waiting must keep holding its slot until the abandoned work actually finishes.
/// The consequence for tests is that a test asserting a hang returns while its background action
/// is still running and still holding a slot, on nobody's schedule but the thread pool's.
/// </para>
/// <para>
/// Those tests used to hang on a fixed <c>Thread.Sleep(5s)</c>, so the slot came back whenever the
/// pool got round to it. FailsFastInsteadOfSpawningAnotherThreadOnceConcurrencyCapIsSaturated then
/// tried to out-wait that with a fixed delay plus a drain loop, and on a loaded CI runner lost the
/// race anyway - it saw 27 of 32 slots and failed on a pool that was merely slow, not broken
/// (run 32546672295).
/// </para>
/// <para>
/// The fix is to stop racing. A hang under test is now released explicitly, and the test waits for
/// its own slot to come back before returning, so every test starts from a pristine semaphore no
/// matter how loaded the runner is. Waiting also means the gate is not disposed while a background
/// action is still blocked on it.
/// </para>
/// </remarks>
internal static class BoundedCallSlots
{
    /// <summary>
    /// Raises the pool's floor once per test host so a released action is actually scheduled.
    /// </summary>
    /// <remarks>
    /// TryRunBounded takes its slot on the calling thread but releases it inside the Task.Run
    /// body, so a slot only comes back once that body gets a thread. The pool injects threads
    /// gradually under burst load and the saturation test deliberately blocks a poolful of them,
    /// so raising the floor lets a released action get a thread on demand instead of at the
    /// throttled rate.
    /// </remarks>
    static BoundedCallSlots()
    {
        const int headroom = 8;
        ThreadPool.GetMinThreads(out var minWorkerThreads, out var minCompletionPortThreads);
        ThreadPool.SetMinThreads(
            Math.Max(minWorkerThreads, VmOperations.MaxConcurrentBoundedCalls + headroom),
            minCompletionPortThreads);
    }

    /// <summary>
    /// Slots free right now. Capture this before making a bounded call, and wait for it to come
    /// back afterwards with <see cref="WaitForAtLeast"/>.
    /// </summary>
    public static int Available => VmOperations.AvailableBoundedCallSlots;

    /// <summary>
    /// Blocks until at least <paramref name="expected"/> slots are free again.
    /// </summary>
    /// <remarks>
    /// Deliberately relative, and that is the whole fix. This used to assert the process-global
    /// "all 32 slots are free", which is not a property any single test can hold: the semaphore is
    /// static, so a slot held anywhere in the test host failed whichever test happened to look
    /// next, and then failed every test after it as the shortfall cascaded. CI run 34168441446
    /// failed three tests that way at 25, 27 and 27 of 32, each after waiting the full window -
    /// slots that were genuinely never coming back, not a pool that was merely slow. Raising that
    /// window from 30s to 120s (v0.2.247) only made the same failure take four minutes longer,
    /// which is what proved the earlier "slow runner" reading wrong.
    ///
    /// A test can only be responsible for the slots it took, so that is what it asserts: the
    /// baseline it started from, restored. A leak in the code under test still fails this - the
    /// slot it took never comes back - while a slot held by anything else no longer does.
    /// </remarks>
    public static void WaitForAtLeast(int expected, TimeSpan? timeout = null)
    {
        var window = timeout ?? TimeSpan.FromSeconds(30);
        var deadline = DateTimeOffset.UtcNow + window;
        while (Available < expected && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.True(
            Available >= expected,
            $"Expected at least {expected} bounded-call slots to be free again, but only {Available} were "
                + $"after waiting {window.TotalSeconds:N0}s. The call under test did not return its slot.");
    }
}
