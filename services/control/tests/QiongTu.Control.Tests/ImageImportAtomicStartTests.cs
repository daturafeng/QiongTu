using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ImageImportAtomicStartTests
{
    private const string PlaintextPath = @"C:\raw\secret-flight\DJI_9999.JPG";

    [TestMethod]
    public void StartPreparedRollsBackSessionEntriesAndMutationWhenDiscoveredEntryInsertFailsThenSameRequestCanCommit()
    {
        using var scope = new CatalogScope();
        scope.SeedProjectDatasetVersion("dataset-version-dji", "dji_supported");

        var failed = Assert.Throws<SqliteException>(() =>
            scope.Catalog.StartPrepared(
                "request-atomic-start",
                "session-atomic-start",
                "dataset-version-dji",
                Sha('a'),
                "manifest_01",
                new[]
                {
                    Entry("session-atomic-start", Sha('1'), "DJI_0001.JPG", 0, 101),
                    Entry("session-atomic-start", Sha('2'), "DJI_0002.JPG", 0, 102)
                }));

        Assert.Contains(failed.SqliteErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture), "19");
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM image_import_sessions;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM image_import_entries;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM catalog_mutations WHERE request_id = 'request-atomic-start';"));

        var committed = scope.Catalog.StartPrepared(
            "request-atomic-start",
            "session-atomic-start",
            "dataset-version-dji",
            Sha('a'),
            "manifest_01",
            new[]
            {
                Entry("session-atomic-start", Sha('1'), "DJI_0001.JPG", 0, 101),
                Entry("session-atomic-start", Sha('2'), "DJI_0002.JPG", 1, 102)
            });
        var entries = scope.Catalog.ListEntries(new ImageImportEntryListParameters(committed.ImportSessionId, 10, null));

        Assert.AreEqual("session-atomic-start", committed.ImportSessionId);
        Assert.AreEqual("ready", committed.Status);
        Assert.AreEqual(2, committed.TotalEntryCount);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM image_import_sessions;"));
        Assert.AreEqual(2L, scope.Scalar<long>("SELECT count(*) FROM image_import_entries;"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM catalog_mutations WHERE request_id = 'request-atomic-start';"));
        Assert.HasCount(2, entries.Items);
        AssertSanitized(committed);
        AssertDatabaseDoesNotContainPlaintextPath(scope);
    }

    [TestMethod]
    public void SuccessfulStartReplayReturnsOriginalSessionAndDoesNotReplaceDiscoveredEntries()
    {
        using var scope = new CatalogScope();
        scope.SeedProjectDatasetVersion("dataset-version-dji", "dji_supported");

        var first = scope.Catalog.StartPrepared(
            "request-idempotent-start",
            "session-idempotent-start",
            "dataset-version-dji",
            Sha('b'),
            "manifest_02",
            new[]
            {
                Entry("session-idempotent-start", Sha('3'), "DJI_0101.JPG", 0, 201),
                Entry("session-idempotent-start", Sha('4'), "DJI_0102.JPG", 1, 202)
            });
        var replay = scope.Catalog.StartPrepared(
            "request-idempotent-start",
            "session-idempotent-start",
            "dataset-version-dji",
            Sha('b'),
            "manifest_02",
            new[]
            {
                Entry("session-idempotent-start", Sha('5'), "DJI_CHANGED.JPG", 0, 999),
                Entry("session-idempotent-start", Sha('6'), "DJI_EXTRA.JPG", 2, 1000)
            });
        var entries = scope.Catalog.ListEntries(new ImageImportEntryListParameters(first.ImportSessionId, 10, null));
        var replayJson = JsonSerializer.Serialize(replay, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.AreEqual(first.ImportSessionId, replay.ImportSessionId);
        Assert.AreEqual(first.TotalEntryCount, replay.TotalEntryCount);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM image_import_sessions;"));
        Assert.AreEqual(2L, scope.Scalar<long>("SELECT count(*) FROM image_import_entries;"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM catalog_mutations WHERE request_id = 'request-idempotent-start';"));
        Assert.HasCount(2, entries.Items);
        CollectionAssert.AreEqual(new[] { "DJI_0101.JPG", "DJI_0102.JPG" }, entries.Items.Select(entry => entry.DisplayName).ToArray());
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM image_import_entries WHERE display_name IN ('DJI_CHANGED.JPG','DJI_EXTRA.JPG');"));
        AssertSanitized(replay);
        Assert.DoesNotContain(PlaintextPath, replayJson, StringComparison.OrdinalIgnoreCase);
        AssertDatabaseDoesNotContainPlaintextPath(scope);
    }

    private static ImageImportDiscoveredEntry Entry(
        string importSessionId,
        string sourceEntryKey,
        string displayName,
        int sortIndex,
        long byteLength) =>
        new(
            importSessionId,
            sourceEntryKey,
            displayName,
            sortIndex,
            byteLength,
            DateTimeOffset.Parse("2026-08-24T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Sha('c'));

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
        Assert.DoesNotContain(@":\", json, StringComparison.Ordinal);
    }

    private static void AssertDatabaseDoesNotContainPlaintextPath(CatalogScope scope)
    {
        var text = scope.Scalar<string>(
            """
            SELECT COALESCE(group_concat(value, char(10)), '')
            FROM (
                SELECT import_session_id AS value FROM image_import_sessions
                UNION ALL SELECT dataset_version_id FROM image_import_sessions
                UNION ALL SELECT source_locator_manifest_id FROM image_import_sessions
                UNION ALL SELECT status FROM image_import_sessions
                UNION ALL SELECT import_entry_id FROM image_import_entries
                UNION ALL SELECT source_entry_key FROM image_import_entries
                UNION ALL SELECT display_name FROM image_import_entries
                UNION ALL SELECT source_identity_key FROM image_import_entries WHERE source_identity_key IS NOT NULL
                UNION ALL SELECT status FROM image_import_entries
                UNION ALL SELECT response_json FROM catalog_mutations
            );
            """);

        Assert.DoesNotContain(PlaintextPath, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\raw", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"secret-flight", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@":\", text, StringComparison.Ordinal);
    }

    private static string Sha(char value) => new(value, 64);

    private class DatabaseOnlyScope : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"qiongtu-image-import-atomic-start-tests-{Guid.NewGuid():N}");

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

        public void SeedProjectDatasetVersion(string datasetVersionId, string sourceEligibilityState)
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
                    'draft',$source_eligibility_state,'not_run','2026-08-24T00:00:00Z',NULL);
                """;
            command.Parameters.AddWithValue("$dataset_version_id", datasetVersionId);
            command.Parameters.AddWithValue("$source_eligibility_state", sourceEligibilityState);
            command.ExecuteNonQuery();
        }

        public T Scalar<T>(string sql)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
