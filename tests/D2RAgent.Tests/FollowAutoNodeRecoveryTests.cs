using D2RHost;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FollowAutoNodeRecoveryTests
{
    private static readonly DateTimeOffset RestartRequested =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameWorkerConnectionDoesNotCountAsRecovery()
    {
        var connectedAt = RestartRequested.AddMinutes(-10);
        var snapshot = Node(connected: true, connectedAt);

        Assert.False(DiscordBot.HasNewWorkerConnection(snapshot, connectedAt, RestartRequested));
    }

    [Fact]
    public void DisconnectedWorkerDoesNotCountEvenWithANewerConnectionTimestamp()
    {
        var previous = RestartRequested.AddMinutes(-10);
        var snapshot = Node(connected: false, RestartRequested.AddMinutes(1));

        Assert.False(DiscordBot.HasNewWorkerConnection(snapshot, previous, RestartRequested));
    }

    [Fact]
    public void NewConnectedAtGenerationCountsAsWorkerRecovery()
    {
        var previous = RestartRequested.AddMinutes(-10);
        var snapshot = Node(connected: true, RestartRequested.AddMinutes(1));

        Assert.True(DiscordBot.HasNewWorkerConnection(snapshot, previous, RestartRequested));
    }

    [Fact]
    public void ConnectionGenerationThatPredatesRestartRequestDoesNotCountAsRecovery()
    {
        var previous = RestartRequested.AddMinutes(-10);
        var snapshot = Node(connected: true, RestartRequested.AddSeconds(-1));

        Assert.False(DiscordBot.HasNewWorkerConnection(snapshot, previous, RestartRequested));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void MissingPriorGenerationRequiresAConnectionAtOrAfterTheRestartRequest(
        int connectedOffsetSeconds,
        bool expected)
    {
        var snapshot = Node(
            connected: true,
            RestartRequested.AddSeconds(connectedOffsetSeconds));

        Assert.Equal(
            expected,
            DiscordBot.HasNewWorkerConnection(snapshot, previousConnectedAt: null, RestartRequested));
    }

    [Fact]
    public void EveryRecoveryAccountMustBeOnlineBeforeFollowResumes()
    {
        var expected = new[] { "hc1", "HC2" };

        Assert.False(DiscordBot.AreRecoveryAccountsOnline(
            expected,
            new HashSet<string>(["hc1"], StringComparer.OrdinalIgnoreCase)));
        Assert.True(DiscordBot.AreRecoveryAccountsOnline(
            expected,
            new HashSet<string>(["HC1", "hc2", "hc3"], StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void RecoveryWaitsForRunExpectedAccountsButNotConfiguredVmsThatWereAlreadyOff()
    {
        var selected = DiscordBot.SelectExpectedRecoveryAccountKeys(
            nodeAccountKeys: ["hc1", "hc2", "hc3", "hc4", "hc-off"],
            onlineAccountKeys: AccountSet("HC1"),
            joinedAccountKeys: AccountSet("hc2"),
            recoveryPendingAccountKeys: AccountSet("HC3"),
            parkedAccountKeys: AccountSet("hc4"));

        Assert.Equal(["hc1", "hc2", "hc3", "hc4"], selected);
        Assert.DoesNotContain("hc-off", selected, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalHostResumePreservesTheWholeActiveFleetAcrossNodes()
    {
        var selected = DiscordBot.SelectFollowAutoResumeRecoveryAccountKeys(
            onlineAccountKeys: AccountSet("local-1", "remote-1"),
            recoveryPendingAccountKeys: AccountSet("remote-reconnecting", "REMOTE-1"));

        Assert.Equal(["local-1", "remote-1", "remote-reconnecting"], selected);
    }

    private static FleetNodeSnapshot Node(bool connected, DateTimeOffset? connectedAt)
    {
        return new FleetNodeSnapshot(
            "server-b",
            DisplayName: null,
            IsLocal: false,
            Connected: connected,
            HostName: null,
            Version: null,
            LastSeenAt: null,
            AgentsConnected: 0,
            AgentsConfigured: 2,
            ConnectedAt: connectedAt);
    }

    private static HashSet<string> AccountSet(params string[] accountKeys)
    {
        return accountKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
