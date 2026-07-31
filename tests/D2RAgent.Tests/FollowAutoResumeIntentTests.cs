using D2RHost;
using Microsoft.Data.Sqlite;
using Xunit;

namespace D2RAgent.Tests;

public sealed class FollowAutoResumeIntentTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "d2r-follow-resume-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void IntentSurvivesHostDatabaseReconstructionAndCanBeConsumed()
    {
        Directory.CreateDirectory(_directory);
        var config = new HostConfig
        {
            DisableDiscord = true,
            DatabasePath = Path.Combine(_directory, "host.sqlite")
        };
        var expected = new FollowAutoResumeIntent(
            ChannelId: 123456789,
            DelaySeconds: 7,
            Watch: true,
            IdleMinutes: 45,
            MetricsEnabled: true,
            CharacterSlot: 2,
            FriendRow: 3,
            RecoveryAccountKeys: ["hc1", "hc2"],
            Reason: "hc1 reached five warmup failures");

        new AppDb(config).SaveFollowAutoResumeIntent(expected);
        var restartedDatabase = new AppDb(config);
        var actual = Assert.IsType<FollowAutoResumeIntent>(
            restartedDatabase.GetFollowAutoResumeIntent());

        Assert.Equal(expected.ChannelId, actual.ChannelId);
        Assert.Equal(expected.DelaySeconds, actual.DelaySeconds);
        Assert.Equal(expected.Watch, actual.Watch);
        Assert.Equal(expected.IdleMinutes, actual.IdleMinutes);
        Assert.Equal(expected.MetricsEnabled, actual.MetricsEnabled);
        Assert.Equal(expected.CharacterSlot, actual.CharacterSlot);
        Assert.Equal(expected.FriendRow, actual.FriendRow);
        Assert.Equal(expected.RecoveryAccountKeys, actual.RecoveryAccountKeys);
        Assert.Equal(expected.Reason, actual.Reason);

        restartedDatabase.ClearFollowAutoResumeIntent();

        Assert.Null(new AppDb(config).GetFollowAutoResumeIntent());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
