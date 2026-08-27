using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ImageImportAcceptanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task CoordinatorStreamsAtLeast32MiBThroughStagePublishWithoutMutatingSource()
    {
        await using var scope = new AcceptanceScope();
        scope.SeedProjectDatasetVersion("dataset-version-large", "dji_supported");
        var sourcePath = Path.Combine(scope.SourceRoot, "DJI_LARGE_0001.JPG");
        await WriteDeterministicFileAsync(sourcePath, 32L * 1024 * 1024);
        File.SetLastWriteTimeUtc(sourcePath, new DateTime(2026, 8, 26, 1, 2, 3, DateTimeKind.Utc));
        var before = SourceFileState.Capture(sourcePath);

        var session = await scope.Coordinator.StartAsync(
            "accept-large-start",
            "accept-large-session",
            "dataset-version-large",
            scope.SourceRoot,
            scope.ControlPaths);
        await scope.WaitForSessionStatusAsync(session.ImportSessionId, "completed");

        var completed = scope.Catalog.Get(new ImageImportGetParameters(session.ImportSessionId));
        var entry = scope.Catalog.ListEntries(new ImageImportEntryListParameters(session.ImportSessionId, 10, null)).Items.Single();
        var responseJson = SerializeForWire(new { completed, entry });

        Assert.AreEqual("completed", completed.Status);
        Assert.AreEqual("available", entry.Status);
        Assert.AreEqual(before.Length, scope.Scalar<long>("SELECT byte_length FROM file_objects WHERE object_kind='source_image';"));
        Assert.AreEqual(before.Length, scope.Scalar<long>("SELECT stage_receipt_byte_length FROM image_import_entries WHERE import_session_id='accept-large-session';"));
        Assert.AreEqual(before.Length, scope.Scalar<long>("SELECT expected_byte_length FROM image_import_entries WHERE import_session_id='accept-large-session';"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE object_kind='source_image' AND storage_state='available';"));
        Assert.HasCount(1, Directory.GetFiles(scope.Store.PublishedDirectory, "*", SearchOption.AllDirectories));
        Assert.AreEqual(before.Length, new FileInfo(Directory.GetFiles(scope.Store.PublishedDirectory, "*", SearchOption.AllDirectories).Single()).Length);
        before.AssertUnchanged(sourcePath);
        AssertPathPrivacy(responseJson, scope.Root, scope.SourceRoot, sourcePath);
    }

    [TestMethod]
    public async Task PendingEligibilitySessionCanReselectMovedSourceRootAndCompleteWithoutPathDisclosure()
    {
        await using var scope = new AcceptanceScope();
        scope.SeedProjectDatasetVersion("dataset-version-reselect", "pending");
        var sourcePath = Path.Combine(scope.SourceRoot, "DJI_RESELECT_0001.JPG");
        await File.WriteAllTextAsync(sourcePath, "same-volume-reselect");
        File.SetLastWriteTimeUtc(sourcePath, new DateTime(2026, 8, 26, 2, 3, 4, DateTimeKind.Utc));
        var before = SourceFileState.Capture(sourcePath);

        var session = await scope.Coordinator.StartAsync(
            "accept-reselect-start",
            "accept-reselect-session",
            "dataset-version-reselect",
            scope.SourceRoot,
            scope.ControlPaths);
        Assert.AreEqual("awaiting_source_preflight", session.Status);
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE object_kind='source_image';"));

        var movedRoot = Path.Combine(scope.Root, "source-moved");
        Directory.Move(scope.SourceRoot, movedRoot);
        var movedPath = Path.Combine(movedRoot, Path.GetFileName(sourcePath));
        scope.SetSourceEligibility("dataset-version-reselect", "dji_supported");

        var resumed = await scope.Coordinator.ResumeAsync(
            "accept-reselect-resume",
            session.ImportSessionId,
            movedRoot,
            scope.ControlPaths);
        await scope.WaitForSessionStatusAsync(resumed.ImportSessionId, "completed");

        var completed = scope.Catalog.Get(new ImageImportGetParameters(resumed.ImportSessionId));
        var entry = scope.Catalog.ListEntries(new ImageImportEntryListParameters(resumed.ImportSessionId, 10, null)).Items.Single();
        var dbText = scope.JoinedImportText();
        var responseJson = SerializeForWire(new { completed, entry });

        Assert.AreEqual("completed", completed.Status);
        Assert.AreEqual("available", entry.Status);
        before.AssertUnchanged(movedPath);
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM images;"));
        AssertPathPrivacy(dbText, scope.Root, scope.SourceRoot, movedRoot, sourcePath, movedPath);
        AssertPathPrivacy(responseJson, scope.Root, scope.SourceRoot, movedRoot, sourcePath, movedPath);
    }

    [TestMethod]
    public async Task TamperedStagePayloadAndManifestAreQuarantinedAndReturnedToRetryableAwaitingSource()
    {
        await using var scope = new AcceptanceScope();
        scope.SeedProjectDatasetVersion("dataset-version-tamper", "dji_supported");
        var session = scope.Catalog.StartPrepared(
            "accept-tamper-start",
            "accept-tamper-session",
            "dataset-version-tamper",
            Sha('a'),
            "accept-tamper-session");
        var payloadEntry = scope.Catalog.RegisterDiscoveredEntry(new ImageImportDiscoveredEntry(
            session.ImportSessionId,
            Sha('1'),
            "DJI_PAYLOAD_TAMPER.JPG",
            0,
            14,
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            null));
        var manifestEntry = scope.Catalog.RegisterDiscoveredEntry(new ImageImportDiscoveredEntry(
            session.ImportSessionId,
            Sha('2'),
            "DJI_MANIFEST_TAMPER.JPG",
            1,
            15,
            DateTimeOffset.Parse("2026-08-26T00:00:01Z"),
            null));
        var payloadStage = await scope.Store.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("payload-tamper")));
        var manifestStage = await scope.Store.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("manifest-tamper")));
        scope.Catalog.RecordStageReceipt(new ImageImportStageReceipt(payloadEntry.ImportEntryId, payloadStage.StageId, payloadStage.Sha256, payloadStage.ByteLength, payloadStage.CreatedAtUtc));
        scope.Catalog.RecordStageReceipt(new ImageImportStageReceipt(manifestEntry.ImportEntryId, manifestStage.StageId, manifestStage.Sha256, manifestStage.ByteLength, manifestStage.CreatedAtUtc));
        await File.AppendAllTextAsync(Path.Combine(scope.Store.StagingDirectory, payloadStage.StageId, "payload"), "changed");
        await File.WriteAllTextAsync(Path.Combine(scope.Store.StagingDirectory, manifestStage.StageId, "stage.json"), "{}");

        scope.RecreateCoordinator();
        await scope.Coordinator.RecoverAsync();
        await scope.Coordinator.WaitUntilIdleAsync();

        var current = scope.Catalog.Get(new ImageImportGetParameters(session.ImportSessionId));
        var entries = scope.Catalog.ListEntries(new ImageImportEntryListParameters(session.ImportSessionId, 10, null)).Items;
        var responseJson = SerializeForWire(new { current, entries });

        Assert.AreEqual("awaiting_source", current.Status);
        CollectionAssert.AreEquivalent(new[] { "source_unavailable", "source_unavailable" }, entries.Select(item => item.Status).ToArray());
        Assert.IsTrue(entries.All(item => item.FailureCode == "object_stage_missing"));
        Assert.AreEqual(2L, scope.Scalar<long>("SELECT count(*) FROM image_import_entries WHERE terminal_at_utc IS NULL;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE object_kind='source_image';"));
        Assert.IsEmpty(Directory.GetFiles(scope.Store.PublishedDirectory, "*", SearchOption.AllDirectories));
        Assert.HasCount(2, Directory.GetDirectories(scope.Store.QuarantineDirectory));
        AssertPathPrivacy(responseJson, scope.Root);
    }

    [TestMethod]
    public async Task PublishingEntryWithCompleteStageIsCompletedByStartupRecovery()
    {
        await using var scope = new AcceptanceScope();
        scope.SeedProjectDatasetVersion("dataset-version-publishing", "dji_supported");
        var session = scope.Catalog.StartPrepared(
            "accept-publishing-start",
            "accept-publishing-session",
            "dataset-version-publishing",
            Sha('b'),
            "accept-publishing-session");
        var entry = scope.Catalog.RegisterDiscoveredEntry(new ImageImportDiscoveredEntry(
            session.ImportSessionId,
            Sha('3'),
            "DJI_PUBLISHING.JPG",
            0,
            18,
            DateTimeOffset.Parse("2026-08-26T00:00:02Z"),
            null));
        var stage = await scope.Store.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("publishing-recover")));
        scope.Catalog.RecordStageReceipt(new ImageImportStageReceipt(entry.ImportEntryId, stage.StageId, stage.Sha256, stage.ByteLength, stage.CreatedAtUtc));
        scope.Catalog.MarkPublishing(entry.ImportEntryId, stage.Sha256, stage.ByteLength);

        scope.RecreateCoordinator();
        await scope.Coordinator.RecoverAsync();
        await scope.Coordinator.WaitUntilIdleAsync();

        var completed = scope.Catalog.Get(new ImageImportGetParameters(session.ImportSessionId));
        var currentEntry = scope.Catalog.ListEntries(new ImageImportEntryListParameters(session.ImportSessionId, 10, null)).Items.Single();

        Assert.AreEqual("completed", completed.Status);
        Assert.AreEqual("available", currentEntry.Status);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE object_kind='source_image' AND storage_state='available';"));
        Assert.HasCount(1, Directory.GetFiles(scope.Store.PublishedDirectory, "*", SearchOption.AllDirectories));
        Assert.IsFalse(Directory.Exists(Path.Combine(scope.Store.StagingDirectory, stage.StageId)));
        AssertPathPrivacy(SerializeForWire(new { completed, currentEntry }), scope.Root);
    }

    [TestMethod]
    public async Task OwnerPrivateSamplePreflightDiscoveryDoesNotCopyHashParseOrExposePaths()
    {
        var ownerRoot = Environment.GetEnvironmentVariable("QIONGTU_OWNER_SAMPLE");
        if (string.IsNullOrWhiteSpace(ownerRoot))
        {
            Assert.Inconclusive("QIONGTU_OWNER_SAMPLE is not set; owner private sample acceptance is run separately by the controller.");
        }

        ownerRoot = Path.GetFullPath(ownerRoot);
        if (!Directory.Exists(ownerRoot))
        {
            Assert.Fail("QIONGTU_OWNER_SAMPLE must point to an existing private sample directory.");
        }

        await using var scope = new AcceptanceScope(new ImageImportSourceDiscoveryOptions(MaximumEntries: 1_000_000, MaximumCandidates: 100_000, MaximumDepth: 64));
        scope.SeedProjectDatasetVersion("dataset-version-owner-private", "pending");
        var before = CaptureCandidateStates(ownerRoot);

        var session = await scope.Coordinator.StartAsync(
            "accept-owner-private-start",
            "accept-owner-private-session",
            "dataset-version-owner-private",
            ownerRoot,
            scope.ControlPaths);
        var current = scope.Catalog.Get(new ImageImportGetParameters(session.ImportSessionId));
        var entries = scope.Catalog.ListEntries(new ImageImportEntryListParameters(session.ImportSessionId, ImageImportCatalog.MaximumPageSize, null));
        var manifestPath = scope.Security.GetRecoveryManifestPath(session.ImportSessionId);
        var manifestText = await File.ReadAllTextAsync(manifestPath);
        var dbText = scope.JoinedImportText();
        var responseJson = SerializeForWire(new { current, entries });

        if (before.Count == 0)
        {
            Assert.Inconclusive("QIONGTU_OWNER_SAMPLE contains no current 3.1 candidate files.");
        }

        Assert.AreEqual("awaiting_source_preflight", current.Status);
        Assert.AreEqual(before.Count, current.TotalEntryCount);
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE object_kind='source_image';"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM images;"));
        Assert.IsEmpty(Directory.GetDirectories(scope.Store.StagingDirectory));
        Assert.IsEmpty(Directory.GetFiles(scope.Store.PublishedDirectory, "*", SearchOption.AllDirectories));
        foreach (var item in before)
        {
            item.Value.AssertUnchanged(item.Key);
        }

        AssertPathPrivacy(manifestText, ownerRoot);
        AssertPathPrivacy(dbText, ownerRoot);
        AssertPathPrivacy(responseJson, ownerRoot);
        Assert.DoesNotContain("sha256/", responseJson, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteDeterministicFileAsync(string path, long byteLength)
    {
        var buffer = new byte[1024 * 1024];
        for (var index = 0; index < buffer.Length; index++)
        {
            buffer[index] = (byte)(index % 251);
        }

        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous);
        var remaining = byteLength;
        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            await stream.WriteAsync(buffer.AsMemory(0, count));
            remaining -= count;
        }
    }

    private static IReadOnlyDictionary<string, SourceFileState> CaptureCandidateStates(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => ImageImportSourceDiscovery.DefaultCandidateExtensions.Contains(Path.GetExtension(path)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, SourceFileState.Capture, StringComparer.OrdinalIgnoreCase);
    }

    private static string SerializeForWire(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private static void AssertPathPrivacy(string text, params string[] paths)
    {
        Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\?\\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("stageReceiptId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quarantineId", text, StringComparison.OrdinalIgnoreCase);
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            Assert.DoesNotContain(path, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Sha(char value) => new(value, 64);

    private sealed class AcceptanceScope : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly BusinessDatabase _database;

        public AcceptanceScope(ImageImportSourceDiscoveryOptions? discoveryOptions = null)
        {
            Root = Path.Combine(Path.GetTempPath(), $"qiongtu-image-import-acceptance-{Guid.NewGuid():N}");
            SourceRoot = Path.Combine(Root, "source");
            Directory.CreateDirectory(SourceRoot);
            ControlPaths = ControlDataPaths.Create(Path.Combine(Root, "control"));
            _databasePath = Path.Combine(Root, "business.db");
            _database = new BusinessDatabase(_databasePath);
            _database.Initialize();
            Catalog = new ImageImportCatalog(_database);
            Security = new ImageImportSourceSecurity(
                Path.Combine(Root, "locators"),
                new TestProtector(),
                () => Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
            Discovery = new ImageImportSourceDiscovery(Security, discoveryOptions);
            Store = new ContentAddressedObjectStore(ControlPaths.ObjectDirectory);
            Coordinator = CreateCoordinator();
        }

        public string Root { get; }

        public string SourceRoot { get; }

        public ControlDataPaths ControlPaths { get; }

        public ImageImportCatalog Catalog { get; }

        public ImageImportSourceSecurity Security { get; }

        public ImageImportSourceDiscovery Discovery { get; }

        public ContentAddressedObjectStore Store { get; }

        public ImageImportCoordinator Coordinator { get; private set; }

        public void RecreateCoordinator()
        {
            Coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Coordinator = CreateCoordinator();
        }

        public void SeedProjectDatasetVersion(string datasetVersionId, string sourceEligibilityState)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR IGNORE INTO projects(project_id,name,spatial_configuration_state,lifecycle_state,created_at_utc,updated_at_utc)
                VALUES('project-import','Project','pending','active','2026-08-26T00:00:00Z','2026-08-26T00:00:00Z');
                INSERT OR IGNORE INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
                VALUES('dataset-import','project-import','Dataset','active','2026-08-26T00:00:00Z','2026-08-26T00:00:00Z');
                INSERT INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc,sealed_at_utc)
                VALUES($dataset_version_id,'dataset-import',
                    (SELECT COALESCE(MAX(version_number),0)+1 FROM dataset_versions WHERE dataset_id='dataset-import'),
                    'draft',$source_eligibility_state,'not_run','2026-08-26T00:00:00Z',NULL);
                """;
            command.Parameters.AddWithValue("$dataset_version_id", datasetVersionId);
            command.Parameters.AddWithValue("$source_eligibility_state", sourceEligibilityState);
            command.ExecuteNonQuery();
        }

        public void SetSourceEligibility(string datasetVersionId, string sourceEligibilityState)
        {
            Execute(
                "UPDATE dataset_versions SET source_eligibility_state=$source_eligibility_state WHERE dataset_version_id=$dataset_version_id;",
                ("$source_eligibility_state", sourceEligibilityState),
                ("$dataset_version_id", datasetVersionId));
        }

        public async Task WaitForSessionStatusAsync(string importSessionId, string status)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            ImageImportSession? last = null;
            while (!timeout.IsCancellationRequested)
            {
                last = Catalog.Get(new ImageImportGetParameters(importSessionId));
                if (string.Equals(last.Status, status, StringComparison.Ordinal))
                {
                    return;
                }

                try
                {
                    await Task.Delay(25, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    break;
                }
            }

            var entries = Catalog.ListEntries(new ImageImportEntryListParameters(importSessionId, ImageImportCatalog.MaximumPageSize, null));
            Assert.Fail($"Image import session {importSessionId} did not reach {status}. Last session: {SerializeForWire(last!)} Entries: {SerializeForWire(entries)}");
        }

        public string JoinedImportText()
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT group_concat(value, char(10))
                FROM (
                    SELECT import_session_id AS value FROM image_import_sessions
                    UNION ALL SELECT dataset_version_id FROM image_import_sessions
                    UNION ALL SELECT source_root_key FROM image_import_sessions
                    UNION ALL SELECT source_locator_manifest_id FROM image_import_sessions
                    UNION ALL SELECT status FROM image_import_sessions
                    UNION ALL SELECT import_entry_id FROM image_import_entries
                    UNION ALL SELECT source_entry_key FROM image_import_entries
                    UNION ALL SELECT display_name FROM image_import_entries
                    UNION ALL SELECT status FROM image_import_entries
                    UNION ALL SELECT COALESCE(failure_code, '') FROM image_import_entries
                    UNION ALL SELECT request_id FROM catalog_mutations
                    UNION ALL SELECT method FROM catalog_mutations
                    UNION ALL SELECT parameters_sha256 FROM catalog_mutations
                    UNION ALL SELECT response_json FROM catalog_mutations
                );
                """;
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        public T Scalar<T>(string sql)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private void Execute(string sql, params (string Name, object? Value)[] parameters)
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

        private ImageImportCoordinator CreateCoordinator() => new(Catalog, Security, Discovery, Store);
    }

    private sealed record SourceFileState(long Length, DateTime LastWriteTimeUtc, FileAttributes Attributes)
    {
        public static SourceFileState Capture(string path)
        {
            var info = new FileInfo(path);
            return new SourceFileState(info.Length, info.LastWriteTimeUtc, info.Attributes);
        }

        public void AssertUnchanged(string path)
        {
            var current = Capture(path);
            Assert.AreEqual(Length, current.Length, $"Source length changed for {Path.GetFileName(path)}.");
            Assert.AreEqual(LastWriteTimeUtc, current.LastWriteTimeUtc, $"Source mtime changed for {Path.GetFileName(path)}.");
            Assert.AreEqual(Attributes, current.Attributes, $"Source attributes changed for {Path.GetFileName(path)}.");
        }
    }

    private sealed class TestProtector : IImageImportSecretProtector
    {
        private static readonly byte[] Secret = Encoding.UTF8.GetBytes("qiongtu-image-import-acceptance-protector");

        public byte[] Protect(byte[] plaintext)
        {
            using var hmac = new HMACSHA256(Secret);
            var tag = hmac.ComputeHash(plaintext);
            var protectedData = new byte[tag.Length + plaintext.Length];
            Buffer.BlockCopy(tag, 0, protectedData, 0, tag.Length);
            for (var index = 0; index < plaintext.Length; index++)
            {
                protectedData[tag.Length + index] = (byte)(plaintext[index] ^ 0x5a);
            }

            return protectedData;
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            if (protectedData.Length < 32)
            {
                throw new CryptographicException("protected payload too short");
            }

            var plaintext = new byte[protectedData.Length - 32];
            for (var index = 0; index < plaintext.Length; index++)
            {
                plaintext[index] = (byte)(protectedData[32 + index] ^ 0x5a);
            }

            using var hmac = new HMACSHA256(Secret);
            var expected = hmac.ComputeHash(plaintext);
            if (!CryptographicOperations.FixedTimeEquals(expected, protectedData.AsSpan(0, 32)))
            {
                throw new CryptographicException("protected payload authentication failed");
            }

            return plaintext;
        }
    }
}
