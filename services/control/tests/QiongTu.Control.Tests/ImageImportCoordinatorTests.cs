using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ImageImportCoordinatorTests
{
    [TestMethod]
    public async Task ImportsAvailableAndDuplicateEntriesThroughCasWithoutLeakingPaths()
    {
        await using var scope = new CoordinatorScope();
        scope.SeedProjectDatasetVersion("dataset-version-dji", "dji_supported");
        await File.WriteAllTextAsync(Path.Combine(scope.SourceRoot, "DJI_0001.JPG"), "same-content");
        await File.WriteAllTextAsync(Path.Combine(scope.SourceRoot, "DJI_0002.JPG"), "same-content");

        var session = await scope.Coordinator.StartAsync(
            "request-start",
            "session-normal",
            "dataset-version-dji",
            scope.SourceRoot,
            scope.ControlPaths);
        await scope.Coordinator.WaitUntilIdleAsync();

        var completed = scope.Catalog.Get(new ImageImportGetParameters(session.ImportSessionId));
        var entries = scope.Catalog.ListEntries(new ImageImportEntryListParameters(session.ImportSessionId, 10, null));
        var json = JsonSerializer.Serialize(new { completed, entries }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.AreEqual("completed", completed.Status);
        Assert.AreEqual(1, completed.AvailableEntryCount);
        Assert.AreEqual(1, completed.DuplicateEntryCount);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE object_kind='source_image' AND storage_state='available';"));
        CollectionAssert.AreEquivalent(new[] { "available", "duplicate" }, entries.Items.Select(item => item.Status).ToArray());
        Assert.DoesNotContain(scope.SourceRoot, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256/", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stageReceiptId", json, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task SourceDisconnectIsRetryableAndDoesNotCompleteSession()
    {
        await using var scope = new CoordinatorScope();
        scope.SeedProjectDatasetVersion("dataset-version-dji", "dji_supported");
        var source = Path.Combine(scope.SourceRoot, "DJI_0001.JPG");
        await File.WriteAllTextAsync(source, "removed-before-copy");
        scope.RecreateCoordinator(File.GetAttributes(source));
        var discovery = await scope.Discovery.DiscoverAsync("session-missing", scope.SourceRoot, scope.ControlPaths);
        var session = scope.Catalog.StartPrepared(
            "request-missing",
            "session-missing",
            "dataset-version-dji",
            discovery.SourceRoot.SourceRootKey,
            "session-missing");
        await scope.RegisterDiscoveryAsync(session.ImportSessionId, discovery);
        Directory.Delete(scope.SourceRoot, recursive: true);

        await scope.Coordinator.ResumeAsync("request-resume-missing", session.ImportSessionId);
        await scope.Coordinator.WaitUntilIdleAsync();

        var current = scope.Catalog.Get(new ImageImportGetParameters(session.ImportSessionId));
        var entries = scope.Catalog.ListEntries(new ImageImportEntryListParameters(session.ImportSessionId, 10, null)).Items;

        Assert.AreEqual("awaiting_source", current.Status);
        Assert.AreEqual(1, current.TotalEntryCount);
        Assert.AreEqual(0, current.AvailableEntryCount);
        Assert.AreEqual(0, current.FailedEntryCount);
        Assert.IsTrue(entries.Any(entry => entry.Status == "source_unavailable"));
    }

    [TestMethod]
    public async Task CancelledStagedEntryIsNotPublishedDuringRecovery()
    {
        await using var scope = new CoordinatorScope();
        scope.SeedProjectDatasetVersion("dataset-version-dji", "dji_supported");
        var session = scope.Catalog.StartPrepared("request-cancel", "session-cancel", "dataset-version-dji", Sha('a'), "manifest-cancel");
        var entry = scope.Catalog.RegisterDiscoveredEntry(new ImageImportDiscoveredEntry(
            session.ImportSessionId,
            Sha('1'),
            "DJI_0001.JPG",
            0,
            12,
            DateTimeOffset.Parse("2026-08-24T00:00:00Z"),
            null));
        var stage = await scope.Store.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("partial-stage")));
        scope.Catalog.RecordStageReceipt(new ImageImportStageReceipt(entry.ImportEntryId, stage.StageId, stage.Sha256, stage.ByteLength, stage.CreatedAtUtc));
        scope.Catalog.Cancel("request-cancel-final", new ImageImportCancelParameters(session.ImportSessionId));

        await scope.Coordinator.RecoverAsync();

        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE object_kind='source_image';"));
        Assert.IsEmpty(Directory.GetFiles(scope.Store.PublishedDirectory, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task SameContentAcrossSessionsReusesCasAndFileObjectWithoutMarkingCrossSessionDuplicate()
    {
        await using var scope = new CoordinatorScope();
        scope.SeedProjectDatasetVersion("dataset-version-dji-1", "dji_supported");
        scope.SeedProjectDatasetVersion("dataset-version-dji-2", "dji_supported");
        await File.WriteAllTextAsync(Path.Combine(scope.SourceRoot, "DJI_0001.JPG"), "cross-session-same-content");

        var first = await scope.Coordinator.StartAsync(
            "request-cross-1",
            "session-cross-1",
            "dataset-version-dji-1",
            scope.SourceRoot,
            scope.ControlPaths);
        await scope.Coordinator.WaitUntilIdleAsync();
        var second = await scope.Coordinator.StartAsync(
            "request-cross-2",
            "session-cross-2",
            "dataset-version-dji-2",
            scope.SourceRoot,
            scope.ControlPaths);
        await scope.Coordinator.WaitUntilIdleAsync();

        var firstEntry = scope.Catalog.ListEntries(new ImageImportEntryListParameters(first.ImportSessionId, 10, null)).Items.Single();
        var secondEntry = scope.Catalog.ListEntries(new ImageImportEntryListParameters(second.ImportSessionId, 10, null)).Items.Single();

        Assert.AreEqual("available", firstEntry.Status);
        Assert.AreEqual("available", secondEntry.Status);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE object_kind='source_image' AND storage_state='available';"));
        Assert.HasCount(1, Directory.GetFiles(scope.Store.PublishedDirectory, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task RecoverPublishesMatchingStagedEntry()
    {
        await using var scope = new CoordinatorScope();
        scope.SeedProjectDatasetVersion("dataset-version-dji", "dji_supported");
        var session = scope.Catalog.StartPrepared("request-staged", "session-staged", "dataset-version-dji", Sha('a'), "manifest-staged");
        var entry = scope.Catalog.RegisterDiscoveredEntry(new ImageImportDiscoveredEntry(
            session.ImportSessionId,
            Sha('1'),
            "DJI_0001.JPG",
            0,
            12,
            DateTimeOffset.Parse("2026-08-24T00:00:00Z"),
            null));
        var stage = await scope.Store.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("recover-staged")));
        scope.Catalog.RecordStageReceipt(new ImageImportStageReceipt(entry.ImportEntryId, stage.StageId, stage.Sha256, stage.ByteLength, stage.CreatedAtUtc));

        await scope.Coordinator.RecoverAsync();

        var current = scope.Catalog.Get(new ImageImportGetParameters(session.ImportSessionId));
        Assert.AreEqual("completed", current.Status);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE content_hash='" + stage.Sha256 + "';"));
        Assert.HasCount(1, Directory.GetFiles(scope.Store.PublishedDirectory, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task RecoverCompletesPublishingEntryWhenObjectWasAlreadyPublished()
    {
        await using var scope = new CoordinatorScope();
        scope.SeedProjectDatasetVersion("dataset-version-dji", "dji_supported");
        var session = scope.Catalog.StartPrepared("request-publishing", "session-publishing", "dataset-version-dji", Sha('a'), "manifest-publishing");
        var entry = scope.Catalog.RegisterDiscoveredEntry(new ImageImportDiscoveredEntry(
            session.ImportSessionId,
            Sha('1'),
            "DJI_0001.JPG",
            0,
            12,
            DateTimeOffset.Parse("2026-08-24T00:00:00Z"),
            null));
        var stage = await scope.Store.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("published-before-business")));
        scope.Catalog.RecordStageReceipt(new ImageImportStageReceipt(entry.ImportEntryId, stage.StageId, stage.Sha256, stage.ByteLength, stage.CreatedAtUtc));
        scope.Catalog.MarkPublishing(entry.ImportEntryId, stage.Sha256, stage.ByteLength);
        await scope.Store.PublishAsync(stage);

        await scope.Coordinator.RecoverAsync();

        var current = scope.Catalog.Get(new ImageImportGetParameters(session.ImportSessionId));
        Assert.AreEqual("completed", current.Status);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE content_hash='" + stage.Sha256 + "';"));
    }

    [TestMethod]
    public async Task OrphanStageIsRecoveredButNotAutoPublished()
    {
        await using var scope = new CoordinatorScope();
        var stage = await scope.Store.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("orphan-stage")));

        await scope.Coordinator.RecoverAsync();

        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_objects;"));
        Assert.IsEmpty(Directory.GetFiles(scope.Store.PublishedDirectory, "*", SearchOption.AllDirectories));
        Assert.IsTrue(Directory.Exists(Path.Combine(scope.Store.StagingDirectory, stage.StageId)));
    }

    private static string Sha(char value) => new(value, 64);

    private sealed class CoordinatorScope : IAsyncDisposable
    {
        private readonly TestProtector _protector = new();
        private readonly BusinessDatabase _database;

        public CoordinatorScope()
        {
            Root = Path.Combine(Path.GetTempPath(), $"qiongtu-image-import-coordinator-{Guid.NewGuid():N}");
            SourceRoot = Path.Combine(Root, "source");
            Directory.CreateDirectory(SourceRoot);
            ControlPaths = ControlDataPaths.Create(Path.Combine(Root, "control"));
            _database = new BusinessDatabase(Path.Combine(Root, "business.db"));
            _database.Initialize();
            Catalog = new ImageImportCatalog(_database);
            Security = new ImageImportSourceSecurity(
                Path.Combine(Root, "locators"),
                _protector,
                () => Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
            Discovery = new ImageImportSourceDiscovery(Security);
            Store = new ContentAddressedObjectStore(ControlPaths.ObjectDirectory);
            Coordinator = CreateCoordinator(FileAttributes.Archive);
        }

        public string Root { get; }

        public string SourceRoot { get; }

        public ControlDataPaths ControlPaths { get; }

        public ImageImportCatalog Catalog { get; }

        public ImageImportSourceSecurity Security { get; }

        public ImageImportSourceDiscovery Discovery { get; }

        public ContentAddressedObjectStore Store { get; }

        public ImageImportCoordinator Coordinator { get; private set; }

        public void RecreateCoordinator(FileAttributes recoveredAttributes)
        {
            Coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Coordinator = CreateCoordinator(recoveredAttributes);
        }

        public async Task RegisterDiscoveryAsync(string sessionId, ImageImportSourceDiscoveryResult discovery)
        {
            var entries = new List<ImageImportDiscoveredEntry>();
            for (var index = 0; index < discovery.Candidates.Count; index++)
            {
                var candidate = discovery.Candidates[index];
                var identity = candidate.Snapshot.Identity is null
                    ? null
                    : await Security.CreateSourceIdentityKeyAsync(candidate.SourceItemKey, candidate.Snapshot.Identity);
                entries.Add(new ImageImportDiscoveredEntry(
                    sessionId,
                    candidate.SourceItemKey,
                    candidate.LeafDisplayName,
                    index,
                    candidate.Snapshot.Length,
                    candidate.Snapshot.LastWriteTimeUtc,
                    identity));
            }

            Catalog.RegisterDiscoveredEntries(entries);
        }

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

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }

        private ImageImportCoordinator CreateCoordinator(FileAttributes recoveredAttributes) =>
            new(Catalog, Security, Discovery, Store, new ImageImportCoordinatorOptions(RecoveredSourceFileAttributes: recoveredAttributes));
    }

    private sealed class TestProtector : IImageImportSecretProtector
    {
        private static readonly byte[] Secret = Encoding.UTF8.GetBytes("qiongtu-coordinator-test-protector");

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
