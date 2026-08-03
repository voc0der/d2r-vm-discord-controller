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
            Reason: "hc1 reached five warmup failures",
            TargetBotCount: 4,
            RecoveryGeneration: 4242);

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
        Assert.Equal(expected.TargetBotCount, actual.TargetBotCount);
        Assert.Equal(expected.RecoveryGeneration, actual.RecoveryGeneration);

        restartedDatabase.ClearFollowAutoResumeIntent();

        Assert.Null(new AppDb(config).GetFollowAutoResumeIntent());
    }

    [Fact]
    public void DelayedFallbackCannotConsumeAReplacementRunsIntent()
    {
        var original = NewIntent(recoveryGeneration: 41);
        var replacement = NewIntent(recoveryGeneration: 42);

        Assert.True(DiscordBot.IsExpectedFollowAutoResumeIntent(41, original));
        Assert.False(DiscordBot.IsExpectedFollowAutoResumeIntent(41, replacement));
        Assert.False(DiscordBot.IsExpectedFollowAutoResumeIntent(41, null));
    }

    [Fact]
    public void ConditionalConsumeLeavesANewerRecoveryGenerationIntact()
    {
        Directory.CreateDirectory(_directory);
        var db = new AppDb(new HostConfig
        {
            DisableDiscord = true,
            DatabasePath = Path.Combine(_directory, "host.sqlite")
        });
        db.SaveFollowAutoResumeIntent(NewIntent(recoveryGeneration: 42));

        Assert.False(db.ClearFollowAutoResumeIntent(expectedRecoveryGeneration: 41));
        Assert.Equal(42, db.GetFollowAutoResumeIntent()!.RecoveryGeneration);
        Assert.True(db.ClearFollowAutoResumeIntent(expectedRecoveryGeneration: 42));
        Assert.Null(db.GetFollowAutoResumeIntent());
    }

    private static FollowAutoResumeIntent NewIntent(long recoveryGeneration)
    {
        return new FollowAutoResumeIntent(
            ChannelId: 1,
            DelaySeconds: 0,
            Watch: false,
            IdleMinutes: 30,
            MetricsEnabled: false,
            CharacterSlot: null,
            FriendRow: null,
            RecoveryAccountKeys: [],
            Reason: "test",
            RecoveryGeneration: recoveryGeneration);
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
