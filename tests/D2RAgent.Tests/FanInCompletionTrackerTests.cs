using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

// save-exit-all/start-all/quit-all (DiscordBot.QueueAllCommandsAsync) fire one
// task per VM and historically had no "everyone's done" signal, only ad-hoc per-VM failure
// follow-ups - a fully successful run looked identical to one nobody had checked on yet.
// FanInCompletionTracker is the fix: exactly one of the N completing callers must be told "you're
// last" so exactly one summary gets sent. These tests pin that "exactly once, with the right
// tally" contract under real concurrency, independent of Discord/DiscordBot.
public sealed class FanInCompletionTrackerTests
{
    [Fact]
    public void ThirdCompletionOfThreeReturnsTrueWithCorrectFailedCount()
    {
        var tracker = new FanInCompletionTracker(3);

        Assert.False(tracker.Complete(ok: true));
        Assert.False(tracker.Complete(ok: false));
        Assert.True(tracker.Complete(ok: true));

        Assert.Equal(1, tracker.FailedCount);
    }

    [Fact]
    public void SingleCompletionImmediatelyReturnsTrue()
    {
        var tracker = new FanInCompletionTracker(1);

        Assert.True(tracker.Complete(ok: true));
        Assert.Equal(0, tracker.FailedCount);
    }

    [Fact]
    public void ThrowsForNegativeCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FanInCompletionTracker(-1));
    }

    [Fact]
    public void ExactlyOneConcurrentCallerObservesCompletionWithCorrectFailedTally()
    {
        const int total = 50;
        const int failing = 21;

        var tracker = new FanInCompletionTracker(total);
        using var release = new ManualResetEventSlim(false);
        using var allReady = new CountdownEvent(total);

        var outcomes = Enumerable.Range(0, total).Select(i => i < failing).ToArray();
        var trueCount = 0;
        var threads = new Thread[total];

        for (var i = 0; i < total; i++)
        {
            var shouldFail = outcomes[i];
            threads[i] = new Thread(() =>
            {
                allReady.Signal();
                release.Wait(TimeSpan.FromSeconds(10));

                if (tracker.Complete(ok: !shouldFail))
                {
                    Interlocked.Increment(ref trueCount);
                }
            });
            threads[i].Start();
        }

        Assert.True(allReady.Wait(TimeSpan.FromSeconds(10)), "Threads did not all start in time.");
        release.Set();

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "A completion thread did not finish in time.");
        }

        Assert.Equal(1, trueCount);
        Assert.Equal(failing, tracker.FailedCount);
    }
}
