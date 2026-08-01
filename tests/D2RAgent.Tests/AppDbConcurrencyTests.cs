using D2RHost;
using Microsoft.Data.Sqlite;
using Xunit;

namespace D2RAgent.Tests;

/// <summary>
/// The host database is written from Discord command handlers, which Discord.NET runs on the
/// gateway task under a three-second acknowledgement deadline. These cover the settings that keep
/// a single write from spending that budget on disk.
/// </summary>
public sealed class AppDbConcurrencyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "d2r-appdb-concurrency-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DatabaseIsOpenedInWriteAheadLoggingMode()
    {
        var config = CreateConfig();

        var db = new AppDb(config);

        Assert.Equal("wal", db.JournalMode);
        Assert.Equal("wal", ReadJournalMode(config.DatabasePath));
    }

    [Fact]
    public void ConvertsARollbackJournalDatabaseLeftByAnOlderBuild()
    {
        var config = CreateConfig();
        Directory.CreateDirectory(_directory);
        using (var legacy = Open(config.DatabasePath))
        {
            using var create = legacy.CreateCommand();
            create.CommandText = "create table legacy (x integer); pragma journal_mode = delete;";
            create.ExecuteNonQuery();
        }

        // The switch to WAL needs an exclusive lock, so it only works once the process that wrote
        // the file is really gone - pooling keeps a disposed connection's handle open, which is
        // exactly the "someone else still has it open" case that makes the pragma a silent no-op.
        SqliteConnection.ClearAllPools();
        Assert.Equal("delete", ReadJournalMode(config.DatabasePath));
        SqliteConnection.ClearAllPools();

        var db = new AppDb(config);

        Assert.Equal("wal", db.JournalMode);
        Assert.Equal("wal", ReadJournalMode(config.DatabasePath));
    }

    /// <summary>
    /// The switch to WAL needs an exclusive lock, and when it cannot get one SQLite reports the
    /// refusal as success - the connection that ran the pragma answers "wal" either way. So
    /// <see cref="AppDb.JournalMode"/> must agree with what an independent connection reads out
    /// of the file, whichever mode that turns out to be; startup logging is the only thing that
    /// would tell an operator the host is running without WAL.
    /// </summary>
    [Fact]
    public void ReportedJournalModeMatchesTheFileEvenWhileAnotherConnectionHoldsIt()
    {
        var config = CreateConfig();
        Directory.CreateDirectory(_directory);
        using var holder = Open(config.DatabasePath);
        using (var create = holder.CreateCommand())
        {
            create.CommandText = "create table legacy (x integer); pragma journal_mode = delete;";
            create.ExecuteNonQuery();
        }

        var db = new AppDb(config);

        Assert.Equal(ReadJournalMode(config.DatabasePath), db.JournalMode);
    }

    /// <summary>
    /// The reason WAL matters here: agent status arrives continuously and every arrival writes,
    /// so a command handler's own write must not queue behind whoever is currently reading. Under
    /// the rollback journal this throws SQLITE_BUSY once busy_timeout expires.
    /// </summary>
    [Fact]
    public void WriteSucceedsWhileAnotherConnectionHoldsAnOpenRead()
    {
        var config = CreateConfig();
        var db = new AppDb(config);
        db.UpsertAgentStatus("D2R_1", kind: "vm", connected: true, payloadJson: "{}");

        // An un-consumed reader keeps a shared lock on the database for as long as it stays open.
        // Under the rollback journal that blocks the write below until it gives up; WAL is what
        // lets the two coexist.
        using var reader = Open(config.DatabasePath);
        using var select = reader.CreateCommand();
        select.CommandText = "select agent_id from agent_status";
        using var open = select.ExecuteReader();
        Assert.True(open.Read());

        db.UpsertAgentStatus("D2R_8", kind: "vm", connected: true, payloadJson: "{}");

        Assert.Contains(db.GetAgentStatuses(), status => status.AgentId == "D2R_8");
    }

    private HostConfig CreateConfig()
    {
        return new HostConfig
        {
            DisableDiscord = true,
            DatabasePath = Path.Combine(_directory, "host.sqlite")
        };
    }

    private static string ReadJournalMode(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "pragma journal_mode;";
        return (command.ExecuteScalar() as string ?? "").ToLowerInvariant();
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        return connection;
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
