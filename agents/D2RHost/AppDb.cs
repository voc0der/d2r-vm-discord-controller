using Microsoft.Data.Sqlite;

namespace D2RHost;

public sealed class AppDb
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    public AppDb(HostConfig config)
    {
        var directory = Path.GetDirectoryName(config.DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = config.DatabasePath
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

    private void Initialize()
    {
        lock (_lock)
        {
            using var connection = OpenConnection();
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
                """;
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
