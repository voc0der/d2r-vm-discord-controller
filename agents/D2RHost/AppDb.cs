using AgentCommon;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace D2RHost;

public sealed class AppDb
{
    // Long enough to ride out another process finishing a write, short enough that a caller on a
    // Discord interaction's three-second clock still fails fast rather than hanging on a
    // database some abandoned process never released.
    private static readonly TimeSpan BusyTimeout = TimeSpan.FromSeconds(2);

    private readonly string _connectionString;
    private readonly object _lock = new();

    /// <summary>
    /// The journal mode the database file actually ended up in, which is "wal" unless the switch
    /// could not be made. Anything else means writes still fsync twice per commit and readers
    /// block writers - worth saying out loud at startup rather than discovering from a command
    /// that took three seconds to answer.
    /// </summary>
    public string JournalMode { get; private set; } = "unknown";

    public AppDb(HostConfig config)
    {
        var directory = Path.GetDirectoryName(config.DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // DefaultTimeout caps Microsoft.Data.Sqlite's own retry loop, which sits on top of
        // busy_timeout and defaults to 30 seconds - so without this a contended write blocks its
        // caller for half a minute no matter what the pragma says. It only bounds time spent
        // waiting for a lock, never a statement that is genuinely running.
        // Floored at one second deliberately: DefaultTimeout is expressed in whole seconds and
        // zero means "wait forever", so a sub-second BusyTimeout here would turn the cap off
        // rather than tighten it.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = config.DatabasePath,
            DefaultTimeout = Math.Max(1, (int)BusyTimeout.TotalSeconds)
        }.ToString();

        Initialize();
    }

    public void UpsertAgentStatus(
        string agentId,
        string kind,
        bool connected,
        string payloadJson)
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                insert into agent_status (agent_id, kind, connected, last_seen_utc, payload_json)
                values ($agent_id, $kind, $connected, $last_seen_utc, $payload_json)
                on conflict(agent_id) do update set
                  kind = excluded.kind,
                  connected = excluded.connected,
                  last_seen_utc = excluded.last_seen_utc,
                  payload_json = excluded.payload_json
                """;
            command.Parameters.AddWithValue("$agent_id", agentId);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$connected", connected ? 1 : 0);
            command.Parameters.AddWithValue("$last_seen_utc", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$payload_json", payloadJson);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<PersistedAgentStatus> GetAgentStatuses()
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                select agent_id, kind, connected, last_seen_utc, payload_json
                from agent_status
                """;

            using var reader = command.ExecuteReader();
            var statuses = new List<PersistedAgentStatus>();
            while (reader.Read())
            {
                var lastSeenAt = DateTimeOffset.TryParse(reader.GetString(3), out var parsedLastSeenAt)
                    ? parsedLastSeenAt
                    : (DateTimeOffset?)null;
                statuses.Add(new PersistedAgentStatus(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2) != 0,
                    lastSeenAt,
                    reader.GetString(4)));
            }

            return statuses;
        }
    }

    public void MarkAgentDisconnected(string agentId)
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                update agent_status
                set connected = 0
                where agent_id = $agent_id
                """;
            command.Parameters.AddWithValue("$agent_id", agentId);
            command.ExecuteNonQuery();
        }
    }

    public void MarkAllAgentsDisconnected()
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "update agent_status set connected = 0 where connected != 0";
            command.ExecuteNonQuery();
        }
    }

    public void InsertCommandHistory(
        string commandId,
        string agentId,
        string commandName,
        bool ok,
        string message)
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                insert into command_history (command_id, agent_id, command, ok, message, created_utc)
                values ($command_id, $agent_id, $command, $ok, $message, $created_utc)
                """;
            command.Parameters.AddWithValue("$command_id", commandId);
            command.Parameters.AddWithValue("$agent_id", agentId);
            command.Parameters.AddWithValue("$command", commandName);
            command.Parameters.AddWithValue("$ok", ok ? 1 : 0);
            command.Parameters.AddWithValue("$message", message);
            command.Parameters.AddWithValue("$created_utc", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public ActiveGame SetActiveGame(
        string name,
        string? password,
        string? difficulty,
        string? notes,
        string updatedBy)
    {
        var updatedUtc = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                insert into active_game (id, name, password, difficulty, notes, updated_by, updated_utc)
                values ('current', $name, $password, $difficulty, $notes, $updated_by, $updated_utc)
                on conflict(id) do update set
                  name = excluded.name,
                  password = excluded.password,
                  difficulty = excluded.difficulty,
                  notes = excluded.notes,
                  updated_by = excluded.updated_by,
                  updated_utc = excluded.updated_utc
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$password", (object?)password ?? DBNull.Value);
            command.Parameters.AddWithValue("$difficulty", (object?)difficulty ?? DBNull.Value);
            command.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated_by", updatedBy);
            command.Parameters.AddWithValue("$updated_utc", updatedUtc.ToString("O"));
            command.ExecuteNonQuery();
        }

        return new ActiveGame(name, password, difficulty, notes, updatedBy, updatedUtc);
    }

    public ActiveGame? GetActiveGame()
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                select name, password, difficulty, notes, updated_by, updated_utc
                from active_game
                where id = 'current'
                """;

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new ActiveGame(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)));
        }
    }

    public bool ClearActiveGame()
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "delete from active_game where id = 'current'";
            return command.ExecuteNonQuery() > 0;
        }
    }

    // The authoritative copy of what every VM agent's follow-template.txt and leader-template.txt
    // are supposed to contain. Before this existed the fingerprints lived only in the agents that
    // happened to be online at bind time, which left the host unable to repair a VM that joined
    // the fleet later - and unable to answer "what is even bound?" after a restart.
    public void SaveFollowTemplates(FollowTemplateState state)
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                insert into follow_template (
                  id, friend_fingerprint, leader_fingerprints, bound_account_key,
                  friend_recorded, leader_recorded, updated_utc)
                values (
                  'current', $friend_fingerprint, $leader_fingerprints, $bound_account_key,
                  $friend_recorded, $leader_recorded, $updated_utc)
                on conflict(id) do update set
                  friend_fingerprint = excluded.friend_fingerprint,
                  leader_fingerprints = excluded.leader_fingerprints,
                  bound_account_key = excluded.bound_account_key,
                  friend_recorded = excluded.friend_recorded,
                  leader_recorded = excluded.leader_recorded,
                  updated_utc = excluded.updated_utc
                """;
            command.Parameters.AddWithValue("$friend_fingerprint", (object?)state.FriendFingerprint ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$leader_fingerprints",
                state.LeaderFingerprints.Count == 0
                    ? DBNull.Value
                    : PartyNameFingerprintList.Serialize(state.LeaderFingerprints));
            command.Parameters.AddWithValue("$bound_account_key", (object?)state.BoundAccountKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$friend_recorded", state.FriendRecorded ? 1 : 0);
            command.Parameters.AddWithValue("$leader_recorded", state.LeaderRecorded ? 1 : 0);
            command.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public FollowTemplateState GetFollowTemplates()
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                select friend_fingerprint, leader_fingerprints, bound_account_key,
                       friend_recorded, leader_recorded
                from follow_template
                where id = 'current'
                """;

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return FollowTemplateState.NotRecorded;
            }

            return new FollowTemplateState(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                PartyNameFingerprintList.Normalize(reader.IsDBNull(1) ? null : reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                FriendRecorded: !reader.IsDBNull(3) && reader.GetInt64(3) != 0,
                LeaderRecorded: !reader.IsDBNull(4) && reader.GetInt64(4) != 0);
        }
    }

    public void ClearFollowTemplates()
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "delete from follow_template where id = 'current'";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Atomically replaces the local node's VM-resume rolodex. The rows are written before any
    /// VM is stopped, so they survive sleep, shutdown, restart, or an interrupted stop pass.
    /// </summary>
    public void ReplacePendingVmResume(string nodeId, IEnumerable<string> vmNames)
    {
        var normalized = vmNames
            .Where(vmName => !string.IsNullOrWhiteSpace(vmName))
            .Select(vmName => vmName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_lock)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "delete from host_vm_resume where node_id = $node_id";
                delete.Parameters.AddWithValue("$node_id", nodeId);
                delete.ExecuteNonQuery();
            }

            foreach (var vmName in normalized)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    insert into host_vm_resume (node_id, vm_name, recorded_utc)
                    values ($node_id, $vm_name, $recorded_utc)
                    """;
                insert.Parameters.AddWithValue("$node_id", nodeId);
                insert.Parameters.AddWithValue("$vm_name", vmName);
                insert.Parameters.AddWithValue("$recorded_utc", DateTimeOffset.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<string> GetPendingVmResume(string nodeId)
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                select vm_name
                from host_vm_resume
                where node_id = $node_id
                order by vm_name collate nocase
                """;
            command.Parameters.AddWithValue("$node_id", nodeId);

            using var reader = command.ExecuteReader();
            var vmNames = new List<string>();
            while (reader.Read())
            {
                vmNames.Add(reader.GetString(0));
            }

            return vmNames;
        }
    }

    public void RemovePendingVmResume(string nodeId, string vmName)
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                delete from host_vm_resume
                where node_id = $node_id and vm_name = $vm_name collate nocase
                """;
            command.Parameters.AddWithValue("$node_id", nodeId);
            command.Parameters.AddWithValue("$vm_name", vmName);
            command.ExecuteNonQuery();
        }
    }

    public void SaveFollowAutoResumeIntent(FollowAutoResumeIntent intent)
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                insert into follow_auto_resume (
                  id, channel_id, delay_seconds, watch, idle_minutes, metrics_enabled,
                  character_slot, friend_row, recovery_account_keys, reason, recorded_utc,
                  target_bot_count, recovery_generation)
                values (
                  'current', $channel_id, $delay_seconds, $watch, $idle_minutes, $metrics_enabled,
                  $character_slot, $friend_row, $recovery_account_keys, $reason, $recorded_utc,
                  $target_bot_count, $recovery_generation)
                on conflict(id) do update set
                  channel_id = excluded.channel_id,
                  delay_seconds = excluded.delay_seconds,
                  watch = excluded.watch,
                  idle_minutes = excluded.idle_minutes,
                  metrics_enabled = excluded.metrics_enabled,
                  character_slot = excluded.character_slot,
                  friend_row = excluded.friend_row,
                  recovery_account_keys = excluded.recovery_account_keys,
                  reason = excluded.reason,
                  recorded_utc = excluded.recorded_utc,
                  target_bot_count = excluded.target_bot_count,
                  recovery_generation = excluded.recovery_generation
                """;
            command.Parameters.AddWithValue("$channel_id", intent.ChannelId.ToString());
            command.Parameters.AddWithValue("$delay_seconds", intent.DelaySeconds);
            command.Parameters.AddWithValue("$watch", intent.Watch ? 1 : 0);
            command.Parameters.AddWithValue("$idle_minutes", intent.IdleMinutes);
            command.Parameters.AddWithValue("$metrics_enabled", intent.MetricsEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$character_slot", (object?)intent.CharacterSlot ?? DBNull.Value);
            command.Parameters.AddWithValue("$friend_row", (object?)intent.FriendRow ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$recovery_account_keys",
                JsonSerializer.Serialize(intent.RecoveryAccountKeys));
            command.Parameters.AddWithValue("$reason", intent.Reason);
            command.Parameters.AddWithValue("$recorded_utc", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$target_bot_count", intent.TargetBotCount);
            command.Parameters.AddWithValue("$recovery_generation", intent.RecoveryGeneration);
            command.ExecuteNonQuery();
        }
    }

    public FollowAutoResumeIntent? GetFollowAutoResumeIntent()
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                select channel_id, delay_seconds, watch, idle_minutes, metrics_enabled,
                       character_slot, friend_row, recovery_account_keys, reason, target_bot_count,
                       recovery_generation
                from follow_auto_resume
                where id = 'current'
                """;

            using var reader = command.ExecuteReader();
            if (!reader.Read()
                || !ulong.TryParse(reader.GetString(0), out var channelId))
            {
                return null;
            }

            IReadOnlyList<string> recoveryAccountKeys;
            try
            {
                recoveryAccountKeys = JsonSerializer.Deserialize<string[]>(reader.GetString(7)) ?? [];
            }
            catch (JsonException)
            {
                recoveryAccountKeys = [];
            }

            return new FollowAutoResumeIntent(
                channelId,
                reader.GetInt32(1),
                reader.GetInt64(2) != 0,
                reader.GetInt32(3),
                reader.GetInt64(4) != 0,
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                recoveryAccountKeys,
                reader.GetString(8),
                reader.IsDBNull(9) ? FollowAutoRosterPolicy.DefaultBotCount : reader.GetInt32(9),
                reader.IsDBNull(10) ? 0 : reader.GetInt64(10));
        }
    }

    public void ClearFollowAutoResumeIntent()
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "delete from follow_auto_resume where id = 'current'";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Consumes only the resume row that belongs to the caller's recovery generation. A delayed
    /// fallback must never erase a replacement row written by a later run.
    /// </summary>
    public bool ClearFollowAutoResumeIntent(long expectedRecoveryGeneration)
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                delete from follow_auto_resume
                where id = 'current' and recovery_generation = $recovery_generation
                """;
            command.Parameters.AddWithValue("$recovery_generation", expectedRecoveryGeneration);
            return command.ExecuteNonQuery() == 1;
        }
    }

    private void Initialize()
    {
        lock (_lock)
        {
            using var connection = OpenConnection();

            // journal_mode is a property of the file, not of the connection, so this converts a
            // database an older build created in rollback mode once and every later connection
            // inherits it. It has to run before any table statement opens a transaction.
            //
            // Switching to WAL needs a brief exclusive lock, so it is a no-op whenever anything
            // else already has this file open - and SQLite reports that as success, returning
            // "wal" while leaving the file in rollback mode. Read the mode back on a separate
            // statement instead of trusting the assignment, and record what actually took so a
            // host that quietly failed to convert can say so rather than looking healthy.
            using (var journal = connection.CreateCommand())
            {
                journal.CommandText = "pragma journal_mode = wal;";
                journal.ExecuteScalar();
            }

            JournalMode = ReadEffectiveJournalMode();

            using var command = connection.CreateCommand();
            command.CommandText = """
                create table if not exists agent_status (
                  agent_id text primary key,
                  kind text not null,
                  connected integer not null,
                  last_seen_utc text not null,
                  payload_json text not null
                );

                create table if not exists command_history (
                  id integer primary key autoincrement,
                  command_id text not null,
                  agent_id text not null,
                  command text not null,
                  ok integer not null,
                  message text not null,
                  created_utc text not null
                );

                create table if not exists active_game (
                  id text primary key,
                  name text not null,
                  password text,
                  difficulty text,
                  notes text,
                  updated_by text not null,
                  updated_utc text not null
                );

                create table if not exists follow_template (
                  id text primary key,
                  friend_fingerprint text,
                  leader_fingerprints text,
                  bound_account_key text,
                  friend_recorded integer not null default 0,
                  leader_recorded integer not null default 0,
                  updated_utc text not null
                );

                create table if not exists host_vm_resume (
                  node_id text not null,
                  vm_name text not null collate nocase,
                  recorded_utc text not null,
                  primary key (node_id, vm_name)
                );

                create table if not exists follow_auto_resume (
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
                """;
            command.ExecuteNonQuery();
        }

        // Added after the table shipped, so an existing database needs the column bolted on. A
        // resume that lost the operator's chosen bot count would silently put the fleet back to a
        // full game after a node restart.
        AddColumnIfMissing(
            "follow_auto_resume",
            "target_bot_count",
            $"integer not null default {FollowAutoRosterPolicy.DefaultBotCount}");
        // A delayed in-process fallback must only consume the exact restart record that scheduled
        // it. Without a generation, an older timer can see and erase a later run's replacement row.
        AddColumnIfMissing(
            "follow_auto_resume",
            "recovery_generation",
            "integer not null default 0");
    }

    private void AddColumnIfMissing(string table, string column, string definition)
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
            using var existing = connection.CreateCommand();
            existing.CommandText = $"select 1 from pragma_table_info('{table}') where name = $column";
            existing.Parameters.AddWithValue("$column", column);
            if (existing.ExecuteScalar() is not null)
            {
                return;
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = $"alter table {table} add column {column} {definition}";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Reads the journal mode out of the file itself. It has to be a connection that has not run
    /// the assignment: the one that did reports the mode it asked for even when the switch was
    /// refused, and a pooled handle reports whatever it was told last, so this one opts out of
    /// the pool to get an honest answer.
    /// </summary>
    private string ReadEffectiveJournalMode()
    {
        try
        {
            var verifyConnectionString = new SqliteConnectionStringBuilder(_connectionString)
            {
                Pooling = false
            }.ToString();
            using var connection = new SqliteConnection(verifyConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "pragma journal_mode;";
            return (command.ExecuteScalar() as string ?? "unknown").ToLowerInvariant();
        }
        catch (SqliteException)
        {
            // This value only feeds a startup log line. Opening a second connection to answer a
            // diagnostic question must never be the reason the host fails to start.
            return "unknown";
        }
    }

    /// <summary>
    /// Every public method here opens its own connection under <see cref="_lock"/>, so the cost
    /// of a single write is paid by whoever is waiting on that lock - including Discord command
    /// handlers, which have three seconds to acknowledge before Discord reports "The application
    /// did not respond". Under the rollback journal SQLite fsyncs twice per write, which on a
    /// host busy power-cycling a guest is exactly the stall that budget cannot absorb. WAL plus
    /// synchronous=normal removes the per-write fsync (a power loss can cost the last few
    /// commits, never the file), and busy_timeout replaces an instant SQLITE_BUSY throw with a
    /// bounded wait when another process - a host mid-self-update - still holds the database.
    /// </summary>
    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragmas = connection.CreateCommand();
        pragmas.CommandText = $"pragma busy_timeout = {(int)BusyTimeout.TotalMilliseconds}; pragma synchronous = normal;";
        pragmas.ExecuteNonQuery();
        return connection;
    }
}

public sealed record PersistedAgentStatus(
    string AgentId,
    string Kind,
    bool Connected,
    DateTimeOffset? LastSeenAt,
    string PayloadJson);

public sealed record FollowAutoResumeIntent(
    ulong ChannelId,
    int DelaySeconds,
    bool Watch,
    int IdleMinutes,
    bool MetricsEnabled,
    int? CharacterSlot,
    int? FriendRow,
    IReadOnlyList<string> RecoveryAccountKeys,
    string Reason,
    // Carried across the restart so a resumed run keeps the party size the operator chose. A row
    // written before this column existed reads as the default.
    int TargetBotCount = FollowAutoRosterPolicy.DefaultBotCount,
    // Identifies the in-process run that scheduled a local-restart fallback. It is deliberately
    // ignored by normal startup resume; only a still-alive predecessor timer uses it to prove the
    // durable row has not since been replaced by another run.
    long RecoveryGeneration = 0);

/// <summary>
/// The fleet-wide follow bind: the friend-row fingerprint every agent matches in its friends
/// drawer, the in-game nametag rolodex, and the account the friend row was captured from.
/// </summary>
/// <remarks>
/// The two "recorded" flags separate "the host has never owned this half of the bind" from "the
/// host owns an explicitly empty one", and the distinction is load-bearing in two places.
///
/// On the first start after this feature ships, agents already hold working templates from an
/// older bind while the host's table is empty; treating that as an authoritative empty state would
/// push a clear to every VM and destroy a working bind on upgrade. And the halves are tracked
/// separately because they are bound by separate commands: re-running the friend-row bind must
/// leave an in-game nametag rolodex the host has never recorded completely alone, exactly as it
/// did before the host kept a copy at all. Only a recorded half reconciles.
/// </remarks>
public sealed record FollowTemplateState(
    string? FriendFingerprint,
    IReadOnlyList<string> LeaderFingerprints,
    string? BoundAccountKey,
    bool FriendRecorded,
    bool LeaderRecorded)
{
    public static FollowTemplateState NotRecorded { get; } =
        new(null, [], null, FriendRecorded: false, LeaderRecorded: false);

    public bool Recorded => FriendRecorded || LeaderRecorded;

    public bool HasFriendTemplate => !string.IsNullOrWhiteSpace(FriendFingerprint);

    public string FriendDigest => FollowTemplateDigest.OfFriendTemplate(FriendFingerprint);

    public string LeaderDigest => FollowTemplateDigest.OfLeaderList(LeaderFingerprints);
}
