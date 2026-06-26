namespace AgentCommon;

// Tracks N independent, concurrently-running operations and tells the caller exactly once -
// on whichever call happens to be the last one in - that all of them are done, along with how
// many failed. Built for "queue a command to every VM, then post a single done/failed summary"
// flows, where without this the only signal was per-failure follow-ups and a fully successful
// run looked identical to one nobody had checked on yet.
public sealed class FanInCompletionTracker
{
    private int _remaining;
    private int _failedCount;

    public FanInCompletionTracker(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _remaining = count;
    }

    public int FailedCount => _failedCount;

    // Call once per completed operation. Returns true for exactly one caller - whichever one
    // observes the remaining count hit zero - so exactly one place sends the summary.
    // Interlocked.Decrement is a full memory barrier, so that one caller is guaranteed to see
    // every other caller's FailedCount increment, even though they ran on different threads.
    public bool Complete(bool ok)
    {
        if (!ok)
        {
            Interlocked.Increment(ref _failedCount);
        }

        return Interlocked.Decrement(ref _remaining) == 0;
    }
}
