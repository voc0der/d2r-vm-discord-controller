using System.Text.Json;
using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FriendFollowRetryPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void FollowRetriesReselectFriendGameBeforeRetrying(int waitResult)
    {
        Assert.True(VmOperations.ShouldReselectFriendGameBeforeRetry((VmOperations.GameEntryWaitResult)waitResult));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(7)]
    public void FollowRetriesDoNotReselectForTerminalWaitResults(int waitResult)
    {
        Assert.False(VmOperations.ShouldReselectFriendGameBeforeRetry((VmOperations.GameEntryWaitResult)waitResult));
    }

    [Fact]
    public void CurrentCharacterRestrictionReturnsVisibleWaitingStateForNextFollowCycle()
    {
        var result = VmOperations.CurrentCharacterCannotJoinFollowResult(dismissed: true);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.True(result.Ok);
        Assert.Contains("You cannot join the game with your current character", result.Message);
        Assert.Contains("will keep retrying", result.Message);
        Assert.True(data.GetProperty("bound").GetBoolean());
        Assert.False(data.GetProperty("joined").GetBoolean());
        Assert.True(data.GetProperty("joinBlocked").GetBoolean());
        Assert.Equal("currentCharacterCannotJoin", data.GetProperty("joinBlockReason").GetString());
        Assert.True(data.GetProperty("dialogDismissed").GetBoolean());
    }
}
