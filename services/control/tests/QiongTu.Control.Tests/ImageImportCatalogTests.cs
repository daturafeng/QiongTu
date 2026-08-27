using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ImageImportCatalogTests
{
    [TestMethod]
    public void StartIsIdempotentAndWaitsForSourcePreflightWhenDatasetIsNotDjiSupported()
    {
        using var scope = new CatalogScope();
        scope.SeedProjectDatasetVersion("dataset-version-pending", "pending");

        var first = scope.StartPrepared("request-start", "session-pending", "dataset-version-pending", Sha('a'), "manifest_01");
        var replay = scope.StartPrepared("request-start", "session-pending", "dataset-version-pending", Sha('a'), "manifest_01");
        var conflict = Assert.Throws<BusinessCatalogException>(() =>
            scope.StartPrepared("request-start", "session-pending", "dataset-version-pending", Sha('a'), "manifest_02"));

        Assert.AreEqual(first.ImportSessionId, replay.ImportSessionId);
        Assert.AreEqual("awaiting_source_preflight", first.Status);
        Assert.AreEqual("pending", first.SourceEligibilityState);
        Assert.AreEqual("idempotency_conflict", conflict.Code);
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM image_import_entries;"));
        AssertSanitized(first);
    }

    [TestMethod]
    public void EntriesPublishToAvailableAndDuplicateWithoutLeakingStorageDetails()
    {
        using var scope = new CatalogScope();
        scope.SeedProjectDatasetVersion("dataset-version-dji", "dji_supported");
        var session = scope.StartPrepared("request-import", "session-import", "dataset-version-dji", Sha('b'), "manifest_01");

        var entry1 = scope.Catalog.RegisterDiscoveredEntry(new ImageImportDiscoveredEntry(
            session.ImportSessionId,
            Sha('1'),
            "DJI_0001.JPG",
            0,
            12,
            DateTimeOffset.Parse("2026-08-24T00:00:00Z"),
            Sha('c')));
        var entry2 = scope.Catalog.RegisterDiscoveredEntry(new ImageImportDiscoveredEntry(
            session.ImportSessionId,
            Sha('2'),
            "DJI_0002.JPG",
            1,
            12,
            DateTimeOffset.Parse("2026-08-24T00:00:01Z"),
            Sha('d')));

        scope.Catalog.RecordStageReceipt(new ImageImportStageReceipt(entry1.ImportEntryId, "stage_01", Sha('e'), 12));
        scope.Catalog.MarkPublishing(entry1.ImportEntryId, Sha('e'), 12);
        var available = scope.Catalog.CompletePublishedEntry(entry1.ImportEntryId, Sha('e'), 12, "image/jpeg");
        scope.Catalog.RecordStageReceipt(new ImageImportStageReceipt(entry2.ImportEntryId, "stage_02", Sha('e'), 12));
        scope.Catalog.MarkPublishing(entry2.ImportEntryId, Sha('e'), 12);
        var duplicate = scope.Catalog.CompletePublishedEntry(entry2.ImportEntryId, Sha('e'), 12, "image/jpeg");

        var completed = scope.Catalog.Get(new ImageImportGetParameters(session.ImportSessionId));
        var entries = scope.Catalog.ListEntries(new ImageImportEntryListParameters(session.ImportSessionId, 10, null));
        var entryJson = JsonSerializer.Serialize(entries, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.AreEqual("available", available.Status);
        Assert.AreEqual("duplicate", duplicate.Status);
        Assert.AreEqual(available.ImportEntryId, duplicate.CanonicalEntryId);
        Assert.AreEqual("completed", completed.Status);
        Assert.AreEqual(2, completed.TotalEntryCount);
        Assert.AreEqual(1, completed.AvailableEntryCount);
        Assert.AreEqual(1, completed.DuplicateEntryCount);
        Assert.HasCount(2, entries.Items);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE object_kind='source_image' AND storage_state='available';"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM images;"), "3.1a must not write authoritative image rows before container/EXIF/DJI parsing.");
        AssertSanitized(available);
        AssertSanitized(duplicate);
        Assert.DoesNotContain("sha256/", entryJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fileObjectId", entryJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stageReceiptId", entryJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quarantineId", entryJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", entryJson, StringComparison.Ordinal);
    }

    [TestMethod]
    public void KeysetCursorsAreBoundToScopeAndPageSize()
    {
        using var scope = new CatalogScope();
        scope.SeedProjectDatasetVersion("dataset-version-dji", "dji_supported");
        var session1 = scope.StartPrepared("request-import-1", "session-import-1", "dataset-version-dji", Sha('b'), "manifest_01");
        var session2 = scope.StartPrepared("request-import-2", "session-import-2", "dataset-version-dji", Sha('c'), "manifest_02");
        for (var index = 0; index < 3; index++)
        {
            scope.Catalog.RegisterDiscoveredEntry(new ImageImportDiscoveredEntry(
                session1.ImportSessionId,
                Sha((char)('0' + index)),
                $"DJI_000{index}.JPG",
                index,
                index,
                null,
                null));
        }

        var firstEntryPage = scope.Catalog.ListEntries(new ImageImportEntryListParameters(session1.ImportSessionId, 2, null));
        var secondEntryPage = scope.Catalog.ListEntries(new ImageImportEntryListParameters(session1.ImportSessionId, 2, firstEntryPage.NextCursor));
        var invalidScope = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.ListEntries(new ImageImportEntryListParameters(session2.ImportSessionId, 2, firstEntryPage.NextCursor)));
        var invalidPage = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.List(new ImageImportListParameters(null, 51, null)));

        Assert.HasCount(2, firstEntryPage.Items);
        Assert.HasCount(1, secondEntryPage.Items);
        Assert.AreEqual("invalid_cursor", invalidScope.Code);
        Assert.AreEqual("invalid_page_size", invalidPage.Code);
    }

    [TestMethod]
    [DataRow("source_locked")]
    [DataRow("source_missing")]
    [DataRow("source_unavailable")]
    [DataRow("source_changed")]
    public void RetryableSourceStatusesDoNotCountAsTerminalCompletion(string errorCode)
    {
        using var scope = new CatalogScope();
        scope.SeedProjectDatasetVersion("dataset-version-dji", "dji_supported");
        var sessionId = "session-retryable-" + errorCode.Replace('_', '-');
        var session = scope.StartPrepared("request-retryable-" + errorCode, sessionId, "dataset-version-dji", Sha('b'), "manifest_01");
        var entry = scope.Catalog.RegisterDiscoveredEntry(new ImageImportDiscoveredEntry(
            session.ImportSessionId,
            Sha('1'),
            "DJI_0001.JPG",
            0,
            12,
            null,
            Sha('c')));

        scope.Catalog.MarkEntryError(entry.ImportEntryId, errorCode);
        var current = scope.Catalog.Get(new ImageImportGetParameters(session.ImportSessionId));
        var work = scope.Catalog.ListIncompleteWorkItems(session.ImportSessionId).Single();

        Assert.AreNotEqual("completed", current.Status);
        Assert.AreEqual(0, current.FailedEntryCount);
        Assert.AreEqual(errorCode, work.Status);
        Assert.AreEqual(entry.ImportEntryId, work.ImportEntryId);
        Assert.AreEqual(Sha('1'), work.SourceEntryKey);
        Assert.AreEqual(Sha('c'), work.SourceIdentityKey);
    }

    [TestMethod]
    public void DatabaseConstraintsRejectPathLikeEntriesNonDraftDatasetsAndTerminalRollback()
    {
        using var scope = new CatalogScope();
        scope.SeedProjectDatasetVersion("dataset-version-dji", "dji_supported");
        var session = scope.StartPrepared("request-import", "session-import", "dataset-version-dji", Sha('b'), "manifest_01");
        var entry = scope.Catalog.RegisterDiscoveredEntry(new ImageImportDiscoveredEntry(
            session.ImportSessionId,
            Sha('1'),
            "DJI_0001.JPG",
            0,
            12,
            null,
            null));

        Assert.Throws<BusinessCatalogException>(() => scope.Catalog.RegisterDiscoveredEntry(new ImageImportDiscoveredEntry(
            session.ImportSessionId,
            Sha('2'),
            "folder/DJI_0002.JPG",
            1,
            12,
            null,
            null)));

        scope.Catalog.Cancel("request-cancel", new ImageImportCancelParameters(session.ImportSessionId));
        Assert.Throws<SqliteException>(() => scope.Execute("UPDATE image_import_entries SET status='discovered' WHERE import_entry_id=$id;", ("$id", entry.ImportEntryId)));
        Assert.Throws<SqliteException>(() => scope.Execute("UPDATE image_import_sessions SET status='ready' WHERE import_session_id=$id;", ("$id", session.ImportSessionId)));

        scope.SeedProjectDatasetVersion("dataset-version-sealed", "dji_supported", sealedVersion: true);
        var notDraft = Assert.Throws<BusinessCatalogException>(() =>
            scope.StartPrepared("request-sealed", "session-sealed", "dataset-version-sealed", Sha('f'), "manifest_03"));
        Assert.AreEqual("dataset_version_not_draft", notDraft.Code);
    }

    [TestMethod]
    public void VersionThreeDatabaseUpgradesThroughCurrentSchemaWithImageImportLedger()
    {
        using var scope = new DatabaseOnlyScope();
        CreateDatabaseAtVersion(scope.DatabasePath, 3);

        new BusinessDatabase(scope.DatabasePath).Initialize();

        using var connection = OpenRaw(scope.DatabasePath);
        Assert.AreEqual((long)BusinessDatabase.CurrentSchemaVersion, Scalar<long>(connection, "PRAGMA user_version;"));
        Assert.AreEqual(1L, Scalar<long>(connection, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='image_import_sessions';"));
        Assert.AreEqual(1L, Scalar<long>(connection, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='image_import_entries';"));
    }

    private static void AssertSanitized(ImageImportSession session)
    {
        Assert.IsFalse(session.Privacy.PathsIncluded);
        Assert.IsFalse(session.Privacy.HashesIncluded);
        Assert.IsFalse(session.Privacy.ObjectKeysIncluded);
        Assert.IsFalse(session.Privacy.StageReceiptsIncluded);
        Assert.IsFalse(session.Privacy.QuarantineIncluded);
        Assert.IsFalse(session.Privacy.SourceLocatorsIncluded);
        var json = JsonSerializer.Serialize(session, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("contentHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fileObjectId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stageReceiptId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quarantineId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("absolutePath", json, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertSanitized(ImageImportEntry entry)
    {
        Assert.IsFalse(entry.Privacy.PathsIncluded);
        Assert.IsFalse(entry.Privacy.HashesIncluded);
        Assert.IsFalse(entry.Privacy.ObjectKeysIncluded);
        Assert.IsFalse(entry.Privacy.StageReceiptsIncluded);
        Assert.IsFalse(entry.Privacy.QuarantineIncluded);
        Assert.IsFalse(entry.Privacy.SourceLocatorsIncluded);
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("contentHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fileObjectId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stageReceiptId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quarantineId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("absolutePath", json, StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateDatabaseAtVersion(string databasePath, int version)
    {
        using var connection = OpenRaw(databasePath);
        Execute(connection,
            """
            CREATE TABLE schema_migrations(
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                sql_sha256 TEXT NOT NULL CHECK(length(sql_sha256)=64),
                applied_at_utc TEXT NOT NULL
            );
            """);

        for (var migrationVersion = 1; migrationVersion <= version; migrationVersion++)
        {
            var (name, sql) = ReadMigration(migrationVersion);
            Execute(connection, sql);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO schema_migrations(version,name,sql_sha256,applied_at_utc)
                VALUES($version,$name,$checksum,'2026-08-24T00:00:00Z');
                """;
            command.Parameters.AddWithValue("$version", migrationVersion);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$checksum", Sha256Hex(sql));
            command.ExecuteNonQuery();
        }

        Execute(connection, $"PRAGMA user_version = {version};");
    }

    private static (string Name, string Sql) ReadMigration(int version)
    {
        var assembly = typeof(BusinessDatabase).Assembly;
        var prefix = $"{version:0000}_";
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.Contains(".Migrations.Business.", StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.Ordinal)
                && name[(name.LastIndexOf(".Migrations.Business.", StringComparison.Ordinal) + ".Migrations.Business.".Length)..].StartsWith(prefix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var fileName = resourceName[(resourceName.LastIndexOf(".Migrations.Business.", StringComparison.Ordinal) + ".Migrations.Business.".Length)..];
        return (fileName, reader.ReadToEnd());
    }

    private static string Sha(char value) => new(value, 64);

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static SqliteConnection OpenRaw(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private class DatabaseOnlyScope : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"qiongtu-image-import-tests-{Guid.NewGuid():N}");

        public DatabaseOnlyScope()
        {
            Directory.CreateDirectory(_root);
            DatabasePath = Path.Combine(_root, "qiongtu.db");
        }

        public string DatabasePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CatalogScope : DatabaseOnlyScope
    {
        private readonly BusinessDatabase _database;

        public CatalogScope()
        {
            _database = new BusinessDatabase(DatabasePath);
            _database.Initialize();
            Catalog = new ImageImportCatalog(_database);
        }

        public ImageImportCatalog Catalog { get; }

        public ImageImportSession StartPrepared(
            string requestId,
            string importSessionId,
            string datasetVersionId,
            string sourceRootKey,
            string sourceLocatorManifestId) =>
            Catalog.StartPrepared(requestId, importSessionId, datasetVersionId, sourceRootKey, sourceLocatorManifestId);

        public void SeedProjectDatasetVersion(string datasetVersionId, string sourceEligibilityState, bool sealedVersion = false)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR IGNORE INTO projects(project_id,name,spatial_configuration_state,lifecycle_state,created_at_utc,updated_at_utc)
                VALUES('project-import','Project','pending','active','2026-08-24T00:00:00Z','2026-08-24T00:00:00Z');
                INSERT OR IGNORE INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
                VALUES('dataset-import','project-import','Dataset','active','2026-08-24T00:00:00Z','2026-08-24T00:00:00Z');
                INSERT INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc,sealed_at_utc)
                VALUES($dataset_version_id,'dataset-import',
                    (SELECT COALESCE(MAX(version_number),0)+1 FROM dataset_versions WHERE dataset_id='dataset-import'),
                    $lifecycle_state,$source_eligibility_state,'not_run','2026-08-24T00:00:00Z',$sealed_at_utc);
                """;
            command.Parameters.AddWithValue("$dataset_version_id", datasetVersionId);
            command.Parameters.AddWithValue("$source_eligibility_state", sourceEligibilityState);
            command.Parameters.AddWithValue("$lifecycle_state", sealedVersion ? "sealed" : "draft");
            command.Parameters.AddWithValue("$sealed_at_utc", sealedVersion ? "2026-08-24T00:00:01Z" : DBNull.Value);
            command.ExecuteNonQuery();
        }

        public void Execute(string sql, params (string Name, object? Value)[] parameters)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            command.ExecuteNonQuery();
        }

        public T Scalar<T>(string sql)
        {
            using var connection = _database.OpenConnection();
            return ImageImportCatalogTests.Scalar<T>(connection, sql);
        }
    }
}
