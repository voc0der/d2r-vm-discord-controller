using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

// /d2r template (issue #20, item 5): create-game-all mints a new numbered game every call;
// join-all reads back whatever was most recently minted without advancing it. These pin that
// split contract, including under real concurrency, independent of Discord/DiscordBot.
public sealed class GameNameTemplateTests
{
    [Fact]
    public void CurrentBeforeAnyMintStartsAtNumberOne()
    {
        var template = new GameNameTemplate("netrunner", "q");

        Assert.Equal(("netrunner1", "q"), template.Current());
        Assert.Equal(("netrunner1", "q"), template.Current());
    }

    [Fact]
    public void MintNextAdvancesSequentiallyAndCurrentReflectsTheLastMint()
    {
        var template = new GameNameTemplate("netrunner", "q");

        Assert.Equal(("netrunner1", "q"), template.MintNext());
        Assert.Equal(("netrunner2", "q"), template.MintNext());
        Assert.Equal(("netrunner2", "q"), template.Current());
        Assert.Equal(("netrunner3", "q"), template.MintNext());
    }

    [Fact]
    public void NullPasswordPassesThroughAsNoPassword()
    {
        var template = new GameNameTemplate("netrunner", null);

        Assert.Equal(("netrunner1", null), template.MintNext());
        Assert.Equal(("netrunner1", null), template.Current());
    }

    [Fact]
    public void ConcurrentMintNextCallsProduceEveryNumberExactlyOnceWithNoGapsOrDuplicates()
    {
        const int total = 64;
        var template = new GameNameTemplate("netrunner", "q");
        using var allReady = new CountdownEvent(total);
        using var release = new ManualResetEventSlim(false);
        var minted = new string[total];
        var threads = new Thread[total];

        for (var i = 0; i < total; i++)
        {
            var slot = i;
            threads[i] = new Thread(() =>
            {
                allReady.Signal();
                release.Wait(TimeSpan.FromSeconds(10));
                minted[slot] = template.MintNext().Name;
            });
            threads[i].Start();
        }

        Assert.True(allReady.Wait(TimeSpan.FromSeconds(10)), "Threads did not all start in time.");
        release.Set();

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "A mint thread did not finish in time.");
        }

        var expected = Enumerable.Range(1, total).Select(n => $"netrunner{n}").ToHashSet();
        Assert.Equal(expected, minted.ToHashSet());
    }
}
