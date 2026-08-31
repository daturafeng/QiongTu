using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ImageImportDatabaseHardeningTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow("folder/manifest")]
    [DataRow("folder\\manifest")]
    [DataRow("C:manifest")]
    [DataRow(".")]
    [DataRow("..")]
    public void SourceLocatorManifestIdRejectsPathLikeValuesAtDatabaseBoundary(string pathLikeManifestId)
    {
        using var scope = new DatabaseScope();
        scope.SeedProjectDatasetVersion("dataset-version-dji", "dji_supported");

        Assert.Throws<SqliteException>(() => scope.Execute(
            """
            INSERT INTO image_import_sessions(
                import_session_id, dataset_version_id, source_root_key, source_locator_manifest_id,
                status, created_at_utc, updated_at_utc)
            VALUES(
                'session-path-like', 'dataset-version-dji', $source_root_key, $manifest_id,
                'ready', '2026-08-24T00:00:00Z', '2026-08-24T00:00:00Z');
            """,
            ("$source_root_key", Sha('a')),
            ("$manifest_id", pathLikeManifestId)));
    }

    [TestMethod]
    public void AvailableEntryObjectSnapshotAndReceiptAreImmutableAfterTerminalState()
    {
        using var scope = new DatabaseScope();
        var ids = scope.SeedAvailableAndDuplicateImportGraph();

        Assert.Throws<SqliteException>(() => scope.Execute(
            """
            UPDATE image_import_entries
            SET file_object_id = 'file-object-b',
                stage_receipt_id = 'stage-b',
                stage_receipt_sha256 = $hash_b,
                stage_receipt_byte_length = 22,
                expected_content_hash = $hash_b,
                expected_byte_length = 22,
                expected_object_key = $key_b,
                updated_at_utc = '2026-08-24T00:10:00Z'
            WHERE import_entry_id = $entry_id;
            """,
            ("$entry_id", ids.AvailableEntryId),
            ("$hash_b", Sha('b')),
            ("$key_b", ObjectKey(Sha('b')))));
    }

    [TestMethod]
    public void DuplicateEntryCanonicalSnapshotAndReceiptAreImmutableAfterTerminalState()
    {
        using var scope = new DatabaseScope();
        var ids = scope.SeedAvailableAndDuplicateImportGraph(includeAlternateCanonical: true);

        Assert.Throws<SqliteException>(() => scope.Execute(
            """
            UPDATE image_import_entries
            SET canonical_entry_id = 'entry-available-alt',
                stage_receipt_id = 'stage-duplicate-edited',
                updated_at_utc = '2026-08-24T00:10:00Z'
            WHERE import_entry_id = $entry_id;
            """,
            ("$entry_id", ids.DuplicateEntryId)));
    }

    [TestMethod]
    [DataRow("publishing")]
    [DataRow("available")]
    [DataRow("duplicate")]
    public void PublishingAvailableAndDuplicateEntriesRequireExpectedObjectSnapshotAndStageReceipt(string status)
    {
        using var scope = new DatabaseScope();
        scope.SeedProjectDatasetVersion("dataset-version-dji", "dji_supported");
        scope.SeedImportSession("session-requires-snapshot", "dataset-version-dji");
        scope.SeedSourceImageFileObject("file-object-a", Sha('a'), 11);
        if (status == "duplicate")
        {
            scope.SeedTerminalAvailableEntry("entry-canonical", "session-requires-snapshot", "dataset-version-dji", Sha('1'), "file-object-a", Sha('a'), 11, sortIndex: 0);
        }

        Assert.Throws<SqliteException>(() => scope.Execute(
            """
            INSERT INTO image_import_entries(
                import_entry_id, import_session_id, dataset_version_id, source_entry_key,
                display_name, sort_index, status, file_object_id, canonical_entry_id,
                created_at_utc, updated_at_utc, terminal_at_utc)
            VALUES(
                'entry-missing-snapshot', 'session-requires-snapshot', 'dataset-version-dji', $source_entry_key,
                'DJI_missing_snapshot.JPG', 1, $status, $file_object_id, $canonical_entry_id,
                '2026-08-24T00:00:00Z', '2026-08-24T00:00:00Z', $terminal_at_utc);
            """,
            ("$source_entry_key", Sha('2')),
            ("$status", status),
            ("$file_object_id", status == "publishing" ? null : "file-object-a"),
            ("$canonical_entry_id", status == "duplicate" ? "entry-canonical" : null),
            ("$terminal_at_utc", status == "publishing" ? null : "2026-08-24T00:00:01Z")));
    }

    [TestMethod]
    public void PublishingEntryRequiresExpectedSnapshotToMatchStageReceipt()
    {
        using var scope = new DatabaseScope();
        scope.SeedProjectDatasetVersion("dataset-version-dji", "dji_supported");
        scope.SeedImportSession("session-stage-mismatch", "dataset-version-dji");

        Assert.Throws<SqliteException>(() => scope.Execute(
            """
            INSERT INTO image_import_entries(
                import_entry_id, import_session_id, dataset_version_id, source_entry_key,
                display_name, sort_index, status,
                stage_receipt_id, stage_receipt_sha256, stage_receipt_byte_length, stage_receipt_created_at_utc,
                expected_content_hash, expected_byte_length, expected_object_key,
                created_at_utc, updated_at_utc)
            VALUES(
                'entry-stage-mismatch', 'session-stage-mismatch', 'dataset-version-dji', $source_entry_key,
                'DJI_stage_mismatch.JPG', 0, 'publishing',
                'stage-a', $stage_hash, 11, '2026-08-24T00:00:00Z',
                $expected_hash, 11, $expected_key,
                '2026-08-24T00:00:00Z', '2026-08-24T00:00:00Z');
            """,
            ("$source_entry_key", Sha('1')),
            ("$stage_hash", Sha('a')),
            ("$expected_hash", Sha('b')),
            ("$expected_key", ObjectKey(Sha('b')))));
    }

    [TestMethod]
    public async Task MalformedEntryListCursorReturnsStructuredInvalidCursor()
    {
        await using var scope = await DispatcherScope.StartAsync();
        var response = await scope.DispatchAsync(
            ControlMethods.ImageImportEntryList,
            "entry-list-invalid-cursor",
            new ImageImportEntryListParameters("session-any", 10, "not-a-valid-cursor"));

        Assert.IsFalse(response.Ok);
        Assert.IsNull(response.Result);
        Assert.IsNotNull(response.Error);
        Assert.AreEqual("invalid_cursor", response.Error!.Code);
    }

    private static string Sha(char value) => new(value, 64);

    private static string ObjectKey(string sha256) => $"sha256/{sha256[..2]}/{sha256}";

    private sealed record SeededImportIds(string AvailableEntryId, string DuplicateEntryId);

    private sealed class DatabaseScope : IDisposable
    {
        private readonly string _root;
        private readonly BusinessDatabase _database;

        public DatabaseScope()
        {
            _root = Path.Combine(Path.GetTempPath(), $"qiongtu-image-import-db-hardening-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            _database = new BusinessDatabase(Path.Combine(_root, "business.db"));
            _database.Initialize();
        }

        public void SeedProjectDatasetVersion(string datasetVersionId, string sourceEligibilityState)
        {
            Execute(
                """
                INSERT OR IGNORE INTO projects(project_id,name,spatial_configuration_state,lifecycle_state,created_at_utc,updated_at_utc)
                VALUES('project-import','Project','pending','active','2026-08-24T00:00:00Z','2026-08-24T00:00:00Z');
                INSERT OR IGNORE INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
                VALUES('dataset-import','project-import','Dataset','active','2026-08-24T00:00:00Z','2026-08-24T00:00:00Z');
                INSERT INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc,sealed_at_utc)
                VALUES($dataset_version_id,'dataset-import',
                    (SELECT COALESCE(MAX(version_number),0)+1 FROM dataset_versions WHERE dataset_id='dataset-import'),
                    'draft',$source_eligibility_state,'not_run','2026-08-24T00:00:00Z',NULL);
                """,
                ("$dataset_version_id", datasetVersionId),
                ("$source_eligibility_state", sourceEligibilityState));
        }

        public void SeedImportSession(string importSessionId, string datasetVersionId)
        {
            Execute(
                """
                INSERT INTO image_import_sessions(
                    import_session_id, dataset_version_id, source_root_key, source_locator_manifest_id,
                    status, created_at_utc, updated_at_utc)
                VALUES(
                    $import_session_id, $dataset_version_id, $source_root_key, 'manifest-safe',
                    'ready', '2026-08-24T00:00:00Z', '2026-08-24T00:00:00Z');
                """,
                ("$import_session_id", importSessionId),
                ("$dataset_version_id", datasetVersionId),
                ("$source_root_key", Sha('0')));
        }

        public void SeedSourceImageFileObject(string fileObjectId, string hash, long byteLength)
        {
            Execute(
                """
                INSERT INTO file_objects(
                    file_object_id, object_kind, hash_algorithm, content_hash, byte_length,
                    media_type, object_key, storage_state, created_at_utc, available_at_utc)
                VALUES(
                    $file_object_id, 'source_image', 'sha256', $hash, $byte_length,
                    'image/jpeg', $object_key, 'available', '2026-08-24T00:00:00Z', '2026-08-24T00:00:00Z');
                INSERT INTO file_object_roles(file_object_id, object_role, created_at_utc)
                VALUES($file_object_id, 'source_image', '2026-08-24T00:00:00Z');
                """,
                ("$file_object_id", fileObjectId),
                ("$hash", hash),
                ("$byte_length", byteLength),
                ("$object_key", ObjectKey(hash)));
        }

        public void SeedTerminalAvailableEntry(
            string importEntryId,
            string importSessionId,
            string datasetVersionId,
            string sourceEntryKey,
            string fileObjectId,
            string hash,
            long byteLength,
            int sortIndex)
        {
            Execute(
                """
                INSERT INTO image_import_entries(
                    import_entry_id, import_session_id, dataset_version_id, source_entry_key,
                    display_name, sort_index, status,
                    stage_receipt_id, stage_receipt_sha256, stage_receipt_byte_length, stage_receipt_created_at_utc,
                    expected_content_hash, expected_byte_length, expected_object_key,
                    file_object_id, created_at_utc, updated_at_utc, terminal_at_utc)
                VALUES(
                    $import_entry_id, $import_session_id, $dataset_version_id, $source_entry_key,
                    $display_name, $sort_index, 'available',
                    $stage_receipt_id, $hash, $byte_length, '2026-08-24T00:00:00Z',
                    $hash, $byte_length, $object_key,
                    $file_object_id, '2026-08-24T00:00:00Z', '2026-08-24T00:00:00Z', '2026-08-24T00:00:01Z');
                """,
                ("$import_entry_id", importEntryId),
                ("$import_session_id", importSessionId),
                ("$dataset_version_id", datasetVersionId),
                ("$source_entry_key", sourceEntryKey),
                ("$display_name", $"DJI_{sortIndex:D4}.JPG"),
                ("$sort_index", sortIndex),
                ("$stage_receipt_id", $"stage-{importEntryId}"),
                ("$hash", hash),
                ("$byte_length", byteLength),
                ("$object_key", ObjectKey(hash)),
                ("$file_object_id", fileObjectId));
        }

        public SeededImportIds SeedAvailableAndDuplicateImportGraph(bool includeAlternateCanonical = false)
        {
            SeedProjectDatasetVersion("dataset-version-dji", "dji_supported");
            SeedImportSession("session-terminal", "dataset-version-dji");
            SeedSourceImageFileObject("file-object-a", Sha('a'), 11);
            SeedSourceImageFileObject("file-object-b", Sha('b'), 22);
            SeedTerminalAvailableEntry("entry-available", "session-terminal", "dataset-version-dji", Sha('1'), "file-object-a", Sha('a'), 11, 0);
            if (includeAlternateCanonical)
            {
                SeedTerminalAvailableEntry("entry-available-alt", "session-terminal", "dataset-version-dji", Sha('3'), "file-object-a", Sha('a'), 11, 2);
            }

            Execute(
                """
                INSERT INTO image_import_entries(
                    import_entry_id, import_session_id, dataset_version_id, source_entry_key,
                    display_name, sort_index, status,
                    stage_receipt_id, stage_receipt_sha256, stage_receipt_byte_length, stage_receipt_created_at_utc,
                    expected_content_hash, expected_byte_length, expected_object_key,
                    file_object_id, canonical_entry_id, created_at_utc, updated_at_utc, terminal_at_utc)
                VALUES(
                    'entry-duplicate', 'session-terminal', 'dataset-version-dji', $source_entry_key,
                    'DJI_DUPLICATE.JPG', 1, 'duplicate',
                    'stage-duplicate', $hash, 11, '2026-08-24T00:00:00Z',
                    $hash, 11, $object_key,
                    'file-object-a', 'entry-available', '2026-08-24T00:00:00Z', '2026-08-24T00:00:00Z', '2026-08-24T00:00:01Z');
                """,
                ("$source_entry_key", Sha('2')),
                ("$hash", Sha('a')),
                ("$object_key", ObjectKey(Sha('a'))));
            return new SeededImportIds("entry-available", "entry-duplicate");
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

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class DispatcherScope : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WorkerSupervisor _workers;
        private readonly ArtifactServer _artifactServer;
        private readonly BusinessDatabase _database;
        private readonly ImageImportCoordinator _imageImports;

        private DispatcherScope(
            string root,
            WorkerSupervisor workers,
            ArtifactServer artifactServer,
            BusinessDatabase database,
            ImageImportCoordinator imageImports,
            ControlRequestDispatcher dispatcher)
        {
            _root = root;
            _workers = workers;
            _artifactServer = artifactServer;
            _database = database;
            _imageImports = imageImports;
            Dispatcher = dispatcher;
        }

        private ControlRequestDispatcher Dispatcher { get; }

        public static async Task<DispatcherScope> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"qiongtu-image-import-dispatcher-hardening-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = ControlDataPaths.Create(Path.Combine(root, "control"));
            var runtimeStore = new WorkerRuntimeStore(paths.RuntimeDatabase);
            runtimeStore.Initialize();
            var database = new BusinessDatabase(paths.BusinessDatabase);
            database.Initialize();
            var businessCatalog = new BusinessCatalog(database);
            var imageImportCatalog = new ImageImportCatalog(database);
            var sourceSecurity = new ImageImportSourceSecurity(
                Path.Combine(paths.StateDirectory, "image-import-locators"),
                new PassthroughProtector(),
                () => Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
            var sourceDiscovery = new ImageImportSourceDiscovery(sourceSecurity);
            var objectStore = new ContentAddressedObjectStore(paths.ObjectDirectory);
            var imageImports = new ImageImportCoordinator(imageImportCatalog, sourceSecurity, sourceDiscovery, objectStore);
            var registry = new WorkerRegistry();
            var capabilities = new ProcessingCapabilityService(registry, paths);
            var workers = new WorkerSupervisor(registry, runtimeStore, paths.LogDirectory, capabilities);
            var artifactServer = new ArtifactServer(new ArtifactRootRegistry());
            await artifactServer.StartAsync(CancellationToken.None);
            var dispatcher = new ControlRequestDispatcher(
                RuntimeDiscovery.CreatePipeName(),
                DateTimeOffset.UtcNow,
                artifactServer,
                workers,
                businessCatalog,
                capabilities,
                requestStop: () => { },
                imageImports,
                imageImportCatalog,
                paths);
            return new DispatcherScope(root, workers, artifactServer, database, imageImports, dispatcher);
        }

        public async Task<ControlResponse> DispatchAsync(string method, string requestId, object parameters)
        {
            return await Dispatcher.DispatchAsync(
                new ControlRequest(
                    ContractVersions.ControlApiV1,
                    requestId,
                    method,
                    JsonSerializer.SerializeToElement(parameters, JsonOptions)),
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await _imageImports.DisposeAsync();
            await _artifactServer.DisposeAsync();
            _workers.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class PassthroughProtector : IImageImportSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] protectedData) => protectedData.ToArray();
    }
}
