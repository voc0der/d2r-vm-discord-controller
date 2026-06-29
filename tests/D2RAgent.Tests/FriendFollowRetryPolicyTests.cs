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
    public void FollowRetriesDoNotReselectForTerminalWaitResults(int waitResult)
    {
        Assert.False(VmOperations.ShouldReselectFriendGameBeforeRetry((VmOperations.GameEntryWaitResult)waitResult));
    }
}
