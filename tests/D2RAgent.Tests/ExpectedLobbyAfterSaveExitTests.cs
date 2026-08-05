using D2RAgent;
using Xunit;

namespace D2RAgent.Tests;

public sealed class ExpectedLobbyAfterSaveExitTests
{
    private const long RunId = 41;
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProcessStartedUtc = Now.AddHours(-1);

    [Fact]
    public void MatchingRunAndProcessCanSkipTheFollowAutoLobbyClassifiers()
    {
        Assert.True(VmOperations.ShouldTrustExpectedLobbyAfterSaveExit(
            FreshExpectation(),
            RunId,
            ProcessStartedUtc,
            VmOperations.D2RActivityState.Unknown,
            Now));
        Assert.False(VmOperations.ShouldRunFollowAutoScreenClassifiers(
            usedExpectedPostSaveExitLobby: true));
    }

    [Fact]
    public void OrdinaryUnknownStateStillRunsTheFollowAutoLobbyClassifiers()
    {
        Assert.True(VmOperations.ShouldRunFollowAutoScreenClassifiers(
            usedExpectedPostSaveExitLobby: false));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-30)]
    public void ExpiredExpectationCannotSkipTheLobbyClassifiers(int secondsFromNow)
    {
        Assert.False(VmOperations.ShouldTrustExpectedLobbyAfterSaveExit(
            FreshExpectation(Now.AddSeconds(secondsFromNow)),
            RunId,
            ProcessStartedUtc,
            VmOperations.D2RActivityState.Unknown,
            Now));
    }

    [Fact]
    public void MissingExpectationCannotSkipTheLobbyClassifiers()
    {
        Assert.False(VmOperations.ShouldTrustExpectedLobbyAfterSaveExit(
            expectation: null,
            RunId,
            ProcessStartedUtc,
            VmOperations.D2RActivityState.Unknown,
            Now));
    }

    [Fact]
    public void DifferentFollowAutoRunCannotConsumeTheExpectation()
    {
        Assert.False(VmOperations.ShouldTrustExpectedLobbyAfterSaveExit(
            FreshExpectation(),
            followAutoRunId: RunId + 1,
            ProcessStartedUtc,
            VmOperations.D2RActivityState.Unknown,
            Now));
    }

    [Fact]
    public void MissingFollowAutoRunCannotConsumeTheExpectation()
    {
        Assert.False(VmOperations.ShouldTrustExpectedLobbyAfterSaveExit(
            FreshExpectation(),
            followAutoRunId: null,
            ProcessStartedUtc,
            VmOperations.D2RActivityState.Unknown,
            Now));
    }

    [Fact]
    public void DifferentD2RProcessGenerationCannotConsumeTheExpectation()
    {
        Assert.False(VmOperations.ShouldTrustExpectedLobbyAfterSaveExit(
            FreshExpectation(),
            RunId,
            ProcessStartedUtc.AddSeconds(2),
            VmOperations.D2RActivityState.Unknown,
            Now));
    }

    [Fact]
    public void MissingD2RProcessGenerationCannotConsumeTheExpectation()
    {
        Assert.False(VmOperations.ShouldTrustExpectedLobbyAfterSaveExit(
            FreshExpectation(),
            RunId,
            processStartedUtc: null,
            VmOperations.D2RActivityState.Unknown,
            Now));
    }

    [Fact]
    public void CharacterScreenEvidenceOverridesAFreshExpectation()
    {
        Assert.False(VmOperations.ShouldTrustExpectedLobbyAfterSaveExit(
            FreshExpectation(),
            RunId,
            ProcessStartedUtc,
            VmOperations.D2RActivityState.CharacterScreenIdle,
            Now));
    }

    [Fact]
    public void StatusCanReportTheSameProcessExpectationWithoutConsumingARunId()
    {
        Assert.True(VmOperations.ShouldReportExpectedLobbyAfterSaveExit(
            FreshExpectation(),
            ProcessStartedUtc,
            VmOperations.D2RActivityState.Unknown,
            Now));
    }

    private static VmOperations.ExpectedLobbyAfterSaveExit FreshExpectation(
        DateTimeOffset? expiresUtc = null)
    {
        return new VmOperations.ExpectedLobbyAfterSaveExit(
            RunId,
            ProcessStartedUtc,
            expiresUtc ?? Now.Add(VmOperations.ExpectedLobbyAfterSaveExitWindow));
    }
}
