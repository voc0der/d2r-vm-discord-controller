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
    /// This is the difference between "slow" and "failed" here. TryRunBounded takes its slot on
    /// the calling thread but releases it inside the Task.Run body, so a slot only comes back when
    /// that body gets a thread. The pool injects new threads gradually under burst load, and the
    /// saturation test deliberately blocks 32 of them, so on a loaded runner the release body can
    /// sit queued for a long time while these waits count against a deadline the runner cannot
    /// meet. Raising the floor lets the pool create threads on demand instead of at its throttled
    /// rate. The saturation test already does this for itself; doing it here covers every waiter.
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
    /// Blocks until every bounded-call slot is free again. The timeout is generous because this is
    /// only ever reached after the work holding those slots has been told to finish: a slow thread
    /// pool should delay this, never fail it.
    /// </summary>
    /// <remarks>
    /// The 30s this used to allow was not generous enough to keep that promise. CI run
    /// 34159650860 failed twice in one job at 26 and 28 of 32 slots, each after waiting the full
    /// 30s, on a change that touches none of this - the third recorded instance of the same flake
    /// (see the class remarks for run 32546672295 at 27 of 32). Nothing here is timing under test:
    /// the loop exits the moment the slots come back, so a healthy run pays nothing for a longer
    /// ceiling, and only a genuine leak - a slot never released at all - waits it out and fails.
    /// </remarks>
    public static void WaitForAll(TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(2));
        while (VmOperations.AvailableBoundedCallSlots != VmOperations.MaxConcurrentBoundedCalls
            && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        // Named rather than bare so a future failure says how far short it fell and how long it
        // waited, which is what distinguishes a leak from a runner that never got there.
        Assert.True(
            VmOperations.AvailableBoundedCallSlots == VmOperations.MaxConcurrentBoundedCalls,
            $"Expected all {VmOperations.MaxConcurrentBoundedCalls} bounded-call slots to be free, "
                + $"but {VmOperations.AvailableBoundedCallSlots} were after waiting "
                + $"{(timeout ?? TimeSpan.FromMinutes(2)).TotalSeconds:N0}s for the abandoned actions to finish.");
    }
}
