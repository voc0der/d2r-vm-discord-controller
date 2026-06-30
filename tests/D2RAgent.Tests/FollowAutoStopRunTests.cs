using System.Text.Json;
using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FollowAutoStopRunTests
{
    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, true)]
    [InlineData(2, 1, false)]
    public void FollowAutoRunStopsOnlyWhenStopSignalCoversThatRun(long runId, long stoppedThroughRunId, bool expected)
    {
        Assert.Equal(expected, VmOperations.IsFollowAutoRunStopped(runId, stoppedThroughRunId));
    }

    [Fact]
    public void FollowAutoRunWithoutRunIdIsNotStoppedByRunScopedSignal()
    {
        Assert.False(VmOperations.IsFollowAutoRunStopped(null, stoppedThroughRunId: 12));
    }

    [Theory]
    [InlineData("""{"followAutoRunId":42}""", 42)]
    [InlineData("""{"followAutoRunId":"43"}""", 43)]
    public void MenuCommandArgsParsesFollowAutoRunId(string json, long expected)
    {
        using var document = JsonDocument.Parse(json);

        var args = MenuCommandArgs.From(document.RootElement);

        Assert.Equal(expected, args.FollowAutoRunId);
    }
}
