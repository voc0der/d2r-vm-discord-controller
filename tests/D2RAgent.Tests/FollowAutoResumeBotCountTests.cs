using D2RHost;
using Microsoft.Data.Sqlite;
using Xunit;

namespace D2RAgent.Tests;

// A follow-auto run that restarts its physical node records a resume intent and picks the run back
// up afterwards. The bot count has to survive that: an operator who set 4 bots must not find a full
// 8-player game waiting after a node reboot they never asked for.
public sealed class FollowAutoResumeBotCountTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "d2r-followauto-botcount-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TheChosenBotCountSurvivesARestart()
    {
        var db = new AppDb(CreateConfig());

        db.SaveFollowAutoResumeIntent(NewIntent(targetBotCount: 4));

        Assert.Equal(4, db.GetFollowAutoResumeIntent()!.TargetBotCount);
    }

    [Fact]
    public void RewritingTheIntentUpdatesTheBotCount()
    {
        var db = new AppDb(CreateConfig());
        db.SaveFollowAutoResumeIntent(NewIntent(targetBotCount: 4));

        db.SaveFollowAutoResumeIntent(NewIntent(targetBotCount: 6));

        Assert.Equal(6, db.GetFollowAutoResumeIntent()!.TargetBotCount);
    }

    [Fact]
    public void RestartJournalUsesTheFrozenTargetAndLaterButtonChangesAreRejected()
    {
        var db = new AppDb(CreateConfig());
        var target = new FollowAutoTargetControl();
        target.Reset(4);

        var frozen = target.ArmLocalRestart();
        db.SaveFollowAutoResumeIntent(NewIntent(frozen.TargetBotCount));
        var lateAdjustment = target.TryAdjust(1, _ => true);

        Assert.Equal(FollowAutoTargetAdjustmentOutcome.LocalRestartArmed, lateAdjustment.Outcome);
        Assert.Equal(4, target.TargetBotCount);
        Assert.Equal(4, db.GetFollowAutoResumeIntent()!.TargetBotCount);
    }

    // Hosts that ran before these columns existed have a database without them. Adding them must
    // not wipe a recorded resume or throw on read - the row simply reports safe defaults.
    [Fact]
    public void ADatabaseFromAnOlderHostGainsTheColumnAndReadsTheDefault()
    {
        var config = CreateConfig();
        Directory.CreateDirectory(_directory);
        SeedLegacyResumeRow(config.DatabasePath);

        var db = new AppDb(config);

        var intent = db.GetFollowAutoResumeIntent();
        Assert.NotNull(intent);
        Assert.Equal(12345UL, intent.ChannelId);
        Assert.Equal(FollowAutoRosterPolicy.DefaultBotCount, intent.TargetBotCount);
        Assert.Equal(0, intent.RecoveryGeneration);
    }

    // A resumed run that came back in private mode would quietly refill the slot the operator was
    // holding open, so the mode is journaled beside the count.
    [Fact]
    public void TheRestartJournalKeepsPublicMode()
    {
        var db = new AppDb(CreateConfig());

        db.SaveFollowAutoResumeIntent(NewIntent(targetBotCount: 3) with { PublicMode = true });

        var intent = db.GetFollowAutoResumeIntent();
        Assert.NotNull(intent);
        Assert.True(intent.PublicMode);
        Assert.Equal(3, intent.TargetBotCount);
    }

    [Fact]
    public void ADatabaseFromAnOlderHostResumesInPrivateMode()
    {
        var config = CreateConfig();
        Directory.CreateDirectory(_directory);
        SeedLegacyResumeRow(config.DatabasePath);

        var intent = new AppDb(config).GetFollowAutoResumeIntent();

        Assert.NotNull(intent);
        Assert.False(intent.PublicMode);
    }

    [Fact]
    public void OpeningTheDatabaseTwiceDoesNotReAddTheColumn()
    {
        var config = CreateConfig();
        _ = new AppDb(config);

        var second = new AppDb(config);

        second.SaveFollowAutoResumeIntent(NewIntent(targetBotCount: 5));
        Assert.Equal(5, second.GetFollowAutoResumeIntent()!.TargetBotCount);
    }

    private static FollowAutoResumeIntent NewIntent(int targetBotCount)
    {
        return new FollowAutoResumeIntent(
            ChannelId: 12345,
            DelaySeconds: 0,
            Watch: false,
            IdleMinutes: 60,
            MetricsEnabled: false,
            CharacterSlot: null,
            FriendRow: null,
            RecoveryAccountKeys: ["hc1"],
            Reason: "node restart",
            TargetBotCount: targetBotCount);
    }

    // Builds the exact follow_auto_resume table an older host created, with a row in it.
    private static void SeedLegacyResumeRow(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            create table follow_auto_resume (
              id text primary key,
              channel_id text not null,
              delay_seconds integer not null,
              watch integer not null,
              idle_minutes integer not null,
              metrics_enabled integer not null,
              character_slot integer,
              friend_row integer,
              recovery_account_keys text not null,
              reason text not null,
              recorded_utc text not null
            );
            insert into follow_auto_resume values (
              'current', '12345', 0, 0, 60, 0, null, null, '["hc1"]', 'node restart', '2026-08-02T12:00:00Z');
            """;
        command.ExecuteNonQuery();
    }

    private HostConfig CreateConfig()
    {
        return new HostConfig
        {
            DisableDiscord = true,
            DatabasePath = Path.Combine(_directory, "host.sqlite")
        };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
