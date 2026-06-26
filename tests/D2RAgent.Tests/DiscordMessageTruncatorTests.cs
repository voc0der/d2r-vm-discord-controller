using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class DiscordMessageTruncatorTests
{
    [Fact]
    public void ContentAtOrUnderTheLimitPassesThroughUnchanged()
    {
        var content = new string('x', 2000);

        Assert.Equal(content, DiscordMessageTruncator.Truncate(content));
    }

    [Fact]
    public void ContentOverTheLimitIsTruncatedWithASuffixAndStaysAtOrUnderTheLimit()
    {
        var content = new string('x', 2500);

        var result = DiscordMessageTruncator.Truncate(content);

        Assert.True(result.Length <= DiscordMessageTruncator.DiscordContentLimit);
        Assert.EndsWith("... (truncated)", result);
        Assert.StartsWith(new string('x', 100), result);
    }

    [Fact]
    public void RespectsACustomLimit()
    {
        var content = new string('x', 50);

        var result = DiscordMessageTruncator.Truncate(content, limit: 20);

        Assert.Equal(20, result.Length);
        Assert.EndsWith("... (truncated)", result);
    }

    [Fact]
    public void EmptyContentPassesThroughUnchanged()
    {
        Assert.Equal(string.Empty, DiscordMessageTruncator.Truncate(string.Empty));
    }
}
