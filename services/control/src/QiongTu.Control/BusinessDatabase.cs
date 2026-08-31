using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace QiongTu.Control;

public sealed class BusinessDatabase
{
    public const int CurrentSchemaVersion = 9;

    private const string MigrationResourceSegment = ".Migrations.Business.";
    private readonly string _connectionString;
    private readonly IReadOnlyList<BusinessMigration> _migrations;
    private readonly object _gate = new();

    public BusinessDatabase(string databasePath)
        : this(databasePath, LoadEmbeddedMigrations())
    {
    }

    internal BusinessDatabase(string databasePath, IReadOnlyList<BusinessMigration> migrations)
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
        _migrations = migrations.OrderBy(item => item.Version).ToArray();
        ValidateMigrationCatalog(_migrations);
    }

    public void Initialize()
    {
        lock (_gate)
        {
            try
            {
                using var connection = OpenConnection();
                EnsureHealthy(connection);
                RejectFutureUserVersion(connection);
                EnsureMigrationLedger(connection);
                ValidateAppliedMigrations(connection);
                ApplyPendingMigrations(connection);
                ValidateAppliedMigrations(connection);
                EnsureHealthy(connection);
            }
            catch (BusinessDatabaseException)
            {
                throw;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
            {
                throw new BusinessDatabaseException(
                    "business_database_integrity_failed",
                    "Business database integrity could not be verified; the original database was left unchanged.",
                    ex);
            }
            catch (SqliteException ex)
            {
                throw new BusinessDatabaseException(
                    "business_database_open_failed",
                    "Business database initialization failed; no automatic rebuild was attempted.",
                    ex);
            }
        }
    }

    internal SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA foreign_keys = ON;");
            Execute(connection, "PRAGMA busy_timeout = 5000;");
            Scalar(connection, "PRAGMA journal_mode = WAL;");
            Execute(connection, "PRAGMA synchronous = FULL;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void ApplyPendingMigrations(SqliteConnection connection)
    {
        foreach (var migration in _migrations)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var existing = ReadAppliedMigration(connection, transaction, migration.Version);
                if (existing is not null)
                {
                    ValidateAppliedMigration(existing, migration);
                    transaction.Commit();
                    continue;
                }

                Execute(connection, migration.Sql, transaction);
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO schema_migrations(version, name, sql_sha256, applied_at_utc)
                    VALUES($version, $name, $sql_sha256, $applied_at_utc);
                    """;
                insert.Parameters.AddWithValue("$version", migration.Version);
                insert.Parameters.AddWithValue("$name", migration.Name);
                insert.Parameters.AddWithValue("$sql_sha256", migration.SqlSha256);
                insert.Parameters.AddWithValue("$applied_at_utc", DateTimeOffset.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();

                Execute(connection, $"PRAGMA user_version = {migration.Version};", transaction);
                transaction.Commit();
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
            {
                transaction.Rollback();
                throw new BusinessDatabaseException(
                    "business_database_migration_failed",
                    $"Business database migration {migration.Version} ({migration.Name}) failed and was rolled back.",
                    ex);
            }
        }
    }

    private static void EnsureMigrationLedger(SqliteConnection connection)
    {
        Execute(
            connection,
            """
            CREATE TABLE IF NOT EXISTS schema_migrations(
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                sql_sha256 TEXT NOT NULL CHECK(length(sql_sha256) = 64),
                applied_at_utc TEXT NOT NULL
            );
            """);
    }

    private static void RejectFutureUserVersion(SqliteConnection connection)
    {
        var userVersion = Convert.ToInt32(
            Scalar(connection, "PRAGMA user_version;"),
            System.Globalization.CultureInfo.InvariantCulture);
        if (userVersion > CurrentSchemaVersion)
        {
            throw new BusinessDatabaseException(
                "business_database_future_version",
                $"Business database version {userVersion} is newer than supported version {CurrentSchemaVersion}.");
        }
    }

    private void ValidateAppliedMigrations(SqliteConnection connection)
    {
        var known = _migrations.ToDictionary(item => item.Version);
        var applied = ReadAppliedMigrations(connection);
        foreach (var row in applied)
        {
            if (!known.TryGetValue(row.Version, out var migration))
            {
                throw new BusinessDatabaseException(
                    "business_database_future_version",
                    $"Business database contains unknown migration version {row.Version}.");
            }

            ValidateAppliedMigration(row, migration);
        }

        var userVersion = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), System.Globalization.CultureInfo.InvariantCulture);
        var latestApplied = applied.Select(item => item.Version).DefaultIfEmpty(0).Max();
        if (userVersion > CurrentSchemaVersion || latestApplied > CurrentSchemaVersion)
        {
            throw new BusinessDatabaseException(
                "business_database_future_version",
                $"Business database version {Math.Max(userVersion, latestApplied)} is newer than supported version {CurrentSchemaVersion}.");
        }

        if (userVersion != latestApplied)
        {
            throw new BusinessDatabaseException(
                "business_database_version_mismatch",
                $"Business database user_version {userVersion} does not match applied migration version {latestApplied}.");
        }
    }

    private static IReadOnlyList<AppliedBusinessMigration> ReadAppliedMigrations(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT version, name, sql_sha256
            FROM schema_migrations
            ORDER BY version;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<AppliedBusinessMigration>();
        while (reader.Read())
        {
            rows.Add(new AppliedBusinessMigration(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private static AppliedBusinessMigration? ReadAppliedMigration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT version, name, sql_sha256 FROM schema_migrations WHERE version = $version;";
        command.Parameters.AddWithValue("$version", version);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AppliedBusinessMigration(reader.GetInt32(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static void ValidateAppliedMigration(AppliedBusinessMigration row, BusinessMigration migration)
    {
        if (!string.Equals(row.Name, migration.Name, StringComparison.Ordinal) ||
            !string.Equals(row.SqlSha256, migration.SqlSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessDatabaseException(
                "business_database_migration_drift",
                $"Business database migration {row.Version} no longer matches the embedded catalog.");
        }
    }

    private static void EnsureHealthy(SqliteConnection connection)
    {
        var quickCheck = Convert.ToString(Scalar(connection, "PRAGMA quick_check;"), System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessDatabaseException(
                "business_database_integrity_failed",
                $"Business database quick_check failed: {quickCheck}");
        }
    }

    private static IReadOnlyList<BusinessMigration> LoadEmbeddedMigrations()
    {
        var assembly = typeof(BusinessDatabase).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(MigrationResourceSegment, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (resources.Length == 0)
        {
            throw new BusinessDatabaseException("business_database_migration_catalog_empty", "No embedded business database migrations were found.");
        }

        var migrations = new List<BusinessMigration>();
        foreach (var resource in resources)
        {
            var fileName = resource[(resource.LastIndexOf(MigrationResourceSegment, StringComparison.Ordinal) + MigrationResourceSegment.Length)..];
            var separator = fileName.IndexOf('_', StringComparison.Ordinal);
            if (separator <= 0 || !int.TryParse(fileName[..separator], out var version))
            {
                throw new BusinessDatabaseException("business_database_migration_catalog_invalid", $"Invalid migration resource name: {resource}");
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new BusinessDatabaseException("business_database_migration_catalog_invalid", $"Cannot open migration resource: {resource}");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            migrations.Add(BusinessMigration.Create(version, fileName, reader.ReadToEnd()));
        }

        return migrations;
    }

    private static void ValidateMigrationCatalog(IReadOnlyList<BusinessMigration> migrations)
    {
        if (migrations.Count == 0)
        {
            throw new BusinessDatabaseException("business_database_migration_catalog_empty", "No business database migrations were provided.");
        }

        for (var index = 0; index < migrations.Count; index++)
        {
            var expectedVersion = index + 1;
            if (migrations[index].Version != expectedVersion)
            {
                throw new BusinessDatabaseException(
                    "business_database_migration_catalog_invalid",
                    $"Business database migrations must be contiguous from 1; expected {expectedVersion}, got {migrations[index].Version}.");
            }
        }

        if (migrations[^1].Version != CurrentSchemaVersion)
        {
            throw new BusinessDatabaseException(
                "business_database_migration_catalog_invalid",
                $"Business database migration catalog ends at {migrations[^1].Version}, expected {CurrentSchemaVersion}.");
        }
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

internal sealed record BusinessMigration(int Version, string Name, string SqlSha256, string Sql)
{
    public static BusinessMigration Create(int version, string name, string sql)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
        return new BusinessMigration(version, name, hash, sql);
    }
}

internal sealed record AppliedBusinessMigration(int Version, string Name, string SqlSha256);

public sealed class BusinessDatabaseException : InvalidOperationException
{
    public BusinessDatabaseException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public BusinessDatabaseException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
