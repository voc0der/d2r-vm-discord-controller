using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

// Worker node builds were invisible from Discord: the health block showed a node's connected state and
// agent counts but never its version, so a node that had quietly stopped taking updates read
// exactly like a current one. Showing the version only helps if "is this node behind" is answered
// from the same string the node actually advertises.
public sealed class AgentVersionTests
{
    [Fact]
    public void ReportsTheInformationalVersionAgentsAdvertise()
    {
        var version = AgentVersion.Current();

        Assert.False(string.IsNullOrWhiteSpace(version));
        // Never the four-part AssemblyVersion rendering ("0.2.215.0") that the master's own node
        // line used to use - that formatting difference alone flagged an up-to-date fleet behind.
        Assert.DoesNotContain(version, ".0.0.0");
    }

    [Theory]
    [InlineData("0.2.214", "0.2.215")]
    [InlineData("0.2.215.0", "0.2.215")]
    public void DifferentBuildsAreReportedAsDifferent(string reported, string expected)
    {
        Assert.True(AgentVersion.IsDifferentBuild(reported, expected));
    }

    [Theory]
    [InlineData("0.2.215", "0.2.215")]
    [InlineData("0.2.215+abc1234", "0.2.215")]
    [InlineData(" 0.2.215 ", "0.2.215")]
    public void MatchingBuildsAreNotFlagged(string reported, string expected)
    {
        // CI publishes can append build metadata; a worker cut from the same tag as the master is
        // not behind, and saying so on every health line would train the warning to be ignored.
        Assert.False(AgentVersion.IsDifferentBuild(reported, expected));
    }

    [Theory]
    [InlineData(null, "0.2.215")]
    [InlineData("", "0.2.215")]
    [InlineData("0.2.215", null)]
    public void UnknownVersionsAreNeverCalledBehind(string? reported, string? expected)
    {
        // An agent too old to advertise a version is a different problem, and guessing "behind"
        // would pin a permanent false warning to health output that no update can ever clear.
        Assert.False(AgentVersion.IsDifferentBuild(reported, expected));
    }
}
