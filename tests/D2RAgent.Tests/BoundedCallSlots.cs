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
    /// Blocks until every bounded-call slot is free again. The timeout is generous because this is
    /// only ever reached after the work holding those slots has been told to finish: a slow thread
    /// pool should delay this, never fail it.
    /// </summary>
    public static void WaitForAll(TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (VmOperations.AvailableBoundedCallSlots != VmOperations.MaxConcurrentBoundedCalls
            && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.Equal(VmOperations.MaxConcurrentBoundedCalls, VmOperations.AvailableBoundedCallSlots);
    }
}
