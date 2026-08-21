using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control;

public sealed class WorkerRuntimeStore
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public WorkerRuntimeStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public void Initialize()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA busy_timeout = 5000;
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS runtime_workers (
                    worker_id TEXT PRIMARY KEY,
                    worker_type TEXT NOT NULL,
                    state TEXT NOT NULL,
                    process_id INTEGER NULL,
                    started_at_utc TEXT NOT NULL,
                    ended_at_utc TEXT NULL,
                    exit_code INTEGER NULL,
                    executable_path TEXT NULL,
                    process_started_at_utc TEXT NULL
                );
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
            EnsureColumn(connection, "executable_path", "TEXT NULL");
            EnsureColumn(connection, "process_started_at_utc", "TEXT NULL");
        }
    }

    public void Upsert(
        WorkerSnapshot worker,
        string? executablePath = null,
        DateTimeOffset? processStartedAtUtc = null)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO runtime_workers (
                    worker_id, worker_type, state, process_id, started_at_utc, ended_at_utc, exit_code,
                    executable_path, process_started_at_utc
                ) VALUES (
                    $worker_id, $worker_type, $state, $process_id, $started_at_utc, $ended_at_utc, $exit_code,
                    $executable_path, $process_started_at_utc
                )
                ON CONFLICT(worker_id) DO UPDATE SET
                    state = excluded.state,
                    process_id = excluded.process_id,
                    ended_at_utc = excluded.ended_at_utc,
                    exit_code = excluded.exit_code,
                    executable_path = COALESCE(excluded.executable_path, runtime_workers.executable_path),
                    process_started_at_utc = COALESCE(excluded.process_started_at_utc, runtime_workers.process_started_at_utc);
                """;
            command.Parameters.AddWithValue("$worker_id", worker.WorkerId);
            command.Parameters.AddWithValue("$worker_type", worker.WorkerType);
            command.Parameters.AddWithValue("$state", worker.State);
            command.Parameters.AddWithValue("$process_id", (object?)worker.ProcessId ?? DBNull.Value);
            command.Parameters.AddWithValue("$started_at_utc", worker.StartedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$ended_at_utc", worker.EndedAtUtc?.ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$exit_code", (object?)worker.ExitCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$executable_path", executablePath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue(
                "$process_started_at_utc",
                processStartedAtUtc?.ToString("O") ?? (object)DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<WorkerSnapshot> List() => ListPersisted().Select(item => item.Snapshot).ToArray();

    internal IReadOnlyList<PersistedWorker> ListPersisted()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT worker_id, worker_type, state, process_id, started_at_utc, ended_at_utc, exit_code,
                       executable_path, process_started_at_utc
                FROM runtime_workers
                ORDER BY started_at_utc, worker_id;
                """;
            using var reader = command.ExecuteReader();
            var workers = new List<PersistedWorker>();
            while (reader.Read())
            {
                workers.Add(new PersistedWorker(
                    new WorkerSnapshot(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                        reader.IsDBNull(5)
                            ? null
                            : DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
                        reader.IsDBNull(6) ? null : reader.GetInt32(6)),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8)
                        ? null
                        : DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture)));
            }

            return workers;
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void EnsureColumn(SqliteConnection connection, string columnName, string definition)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(runtime_workers);";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                return;
            }
        }

        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE runtime_workers ADD COLUMN {columnName} {definition};";
        alter.ExecuteNonQuery();
    }
}

internal sealed record PersistedWorker(
    WorkerSnapshot Snapshot,
    string? ExecutablePath,
    DateTimeOffset? ProcessStartedAtUtc);
