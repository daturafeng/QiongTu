using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ImageImportFaultInjectionTests
{
    [TestMethod]
    public async Task ActiveStagingCancellationDoesNotMutateSourceOrPublishAndCoordinatorRemainsUsable()
    {
        await using var scope = new FaultInjectionScope();
        scope.SeedProjectDatasetVersion("dataset-version-cancel", "dji_supported");
        scope.SeedProjectDatasetVersion("dataset-version-after-cancel", "dji_supported");

        var sourceRoot = scope.CreateSourceRoot("source-cancel");
        var sourcePath = Path.Combine(sourceRoot, "DJI_0001.JPG");
        await File.WriteAllBytesAsync(sourcePath, Encoding.UTF8.GetBytes("cancel-staging-source"));
        var fixedLastWrite = new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(sourcePath, fixedLastWrite);
        File.SetAttributes(sourcePath, FileAttributes.Archive);
        var before = SourceStamp.Capture(sourcePath);

        var stagingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stagingCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stagingCalls = 0;

        scope.RecreateCoordinator(async (stream, token) =>
        {
            if (Interlocked.Increment(ref stagingCalls) == 1)
            {
                var buffer = new byte[1];
                Assert.AreEqual(1, await stream.ReadAsync(buffer.AsMemory(0, 1), token));
                stagingStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    stagingCancelled.TrySetResult();
                    throw;
                }
            }

            return await scope.Store.StageAsync(stream, cancellationToken: token);
        });

        var session = await scope.Coordinator.StartAsync(
            "request-cancel-start",
            "session-cancel-active",
            "dataset-version-cancel",
            sourceRoot,
            scope.ControlPaths);

        await WaitForAsync(stagingStarted.Task);
        var cancelled = scope.Coordinator.Cancel("request-cancel", session.ImportSessionId);
        await WaitForAsync(stagingCancelled.Task);
        await scope.Coordinator.WaitUntilIdleAsync();

        var after = SourceStamp.Capture(sourcePath);
        var cancelledEntry = scope.Catalog.ListEntries(new ImageImportEntryListParameters(session.ImportSessionId, 10, null)).Items.Single();
        var current = scope.Catalog.Get(new ImageImportGetParameters(session.ImportSessionId));

        Assert.AreEqual(before, after);
        Assert.AreEqual("cancelled", cancelled.Status);
        Assert.AreEqual("cancelled", current.Status);
        Assert.AreEqual("cancelled", cancelledEntry.Status);
        Assert.AreEqual("cancelled_by_user", cancelledEntry.FailureCode);
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE object_kind='source_image';"));
        Assert.IsEmpty(Directory.GetFiles(scope.Store.PublishedDirectory, "*", SearchOption.AllDirectories));

        var usableRoot = scope.CreateSourceRoot("source-after-cancel");
        await File.WriteAllTextAsync(Path.Combine(usableRoot, "DJI_0002.JPG"), "coordinator-still-usable");
        var usable = await scope.Coordinator.StartAsync(
            "request-after-cancel",
            "session-after-cancel",
            "dataset-version-after-cancel",
            usableRoot,
            scope.ControlPaths);
        await scope.Coordinator.WaitUntilIdleAsync();

        var usableCurrent = scope.Catalog.Get(new ImageImportGetParameters(usable.ImportSessionId));
        Assert.AreEqual("completed", usableCurrent.Status);
        Assert.AreEqual(1, usableCurrent.AvailableEntryCount);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE object_kind='source_image' AND storage_state='available';"));
    }

    [TestMethod]
    public async Task InjectedObjectStoreDiskFullMapsToStorageFullWithoutSourceMutation()
    {
        await using var scope = new FaultInjectionScope();
        scope.SeedProjectDatasetVersion("dataset-version-storage-full", "dji_supported");

        var sourceRoot = scope.CreateSourceRoot("source-storage-full");
        var sourcePath = Path.Combine(sourceRoot, "DJI_0001.JPG");
        await File.WriteAllBytesAsync(sourcePath, Encoding.UTF8.GetBytes("disk-full-source"));
        File.SetLastWriteTimeUtc(sourcePath, new DateTime(2026, 8, 24, 0, 1, 0, DateTimeKind.Utc));
        File.SetAttributes(sourcePath, FileAttributes.Archive);
        var before = SourceStamp.Capture(sourcePath);

        scope.RecreateCoordinator((_, _) => throw new ObjectStoreException(
            "object_store_disk_full",
            "Injected controlled object-store write failure."));

        var session = await scope.Coordinator.StartAsync(
            "request-storage-full",
            "session-storage-full",
            "dataset-version-storage-full",
            sourceRoot,
            scope.ControlPaths);
        await scope.Coordinator.WaitUntilIdleAsync();

        var entry = scope.Catalog.ListEntries(new ImageImportEntryListParameters(session.ImportSessionId, 10, null)).Items.Single();
        var current = scope.Catalog.Get(new ImageImportGetParameters(session.ImportSessionId));

        Assert.AreEqual(before, SourceStamp.Capture(sourcePath));
        Assert.AreEqual("completed", current.Status);
        Assert.AreEqual(1, current.FailedEntryCount);
        Assert.AreEqual("storage_full", entry.Status);
        Assert.AreEqual("object_store_disk_full", entry.FailureCode);
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE object_kind='source_image';"));
        Assert.IsEmpty(Directory.GetFiles(scope.Store.PublishedDirectory, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task ObjectStoreSourceReadFailureMapsToRetryableAwaitingSource()
    {
        await using var scope = new FaultInjectionScope();
        scope.SeedProjectDatasetVersion("dataset-version-source-read", "dji_supported");

        var sourceRoot = scope.CreateSourceRoot("source-read-failure");
        var sourcePath = Path.Combine(sourceRoot, "DJI_0001.JPG");
        await File.WriteAllBytesAsync(sourcePath, Encoding.UTF8.GetBytes("source-read-failure"));
        File.SetLastWriteTimeUtc(sourcePath, new DateTime(2026, 8, 24, 0, 2, 0, DateTimeKind.Utc));
        File.SetAttributes(sourcePath, FileAttributes.Archive);
        var before = SourceStamp.Capture(sourcePath);

        scope.RecreateCoordinator((_, token) =>
            scope.Store.StageAsync(new ThrowingReadStream(), cancellationToken: token));

        var session = await scope.Coordinator.StartAsync(
            "request-source-read",
            "session-source-read",
            "dataset-version-source-read",
            sourceRoot,
            scope.ControlPaths);
        await scope.Coordinator.WaitUntilIdleAsync();

        var entry = scope.Catalog.ListEntries(new ImageImportEntryListParameters(session.ImportSessionId, 10, null)).Items.Single();
        var current = scope.Catalog.Get(new ImageImportGetParameters(session.ImportSessionId));

        Assert.AreEqual(before, SourceStamp.Capture(sourcePath));
        Assert.AreEqual("awaiting_source", current.Status);
        Assert.AreEqual("object_source_read_failed", current.LastErrorCode);
        Assert.AreEqual(0, current.FailedEntryCount);
        Assert.AreEqual("source_unavailable", entry.Status);
        Assert.AreEqual("object_source_read_failed", entry.FailureCode);
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE object_kind='source_image';"));
        Assert.IsEmpty(Directory.GetFiles(scope.Store.PublishedDirectory, "*", SearchOption.AllDirectories));
    }

    private static async Task WaitForAsync(Task task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (completed != task)
        {
            Assert.Fail("Timed out waiting for deterministic fault-injection synchronization.");
        }

        await task;
    }

    private static string Sha(char value) => new(value, 64);

    private sealed record SourceStamp(long Length, DateTime LastWriteUtc, FileAttributes Attributes)
    {
        public static SourceStamp Capture(string path)
        {
            var info = new FileInfo(path);
            return new SourceStamp(info.Length, info.LastWriteTimeUtc, File.GetAttributes(path));
        }
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("Injected source read failure.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("Injected source read failure.");

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class FaultInjectionScope : IAsyncDisposable
    {
        private readonly TestProtector _protector = new();
        private readonly BusinessDatabase _database;

        public FaultInjectionScope()
        {
            Root = Path.Combine(Path.GetTempPath(), $"qiongtu-image-import-faults-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
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
            Coordinator = CreateCoordinator(null);
        }

        public string Root { get; }

        public ControlDataPaths ControlPaths { get; }

        public ImageImportCatalog Catalog { get; }

        public ImageImportSourceSecurity Security { get; }

        public ImageImportSourceDiscovery Discovery { get; }

        public ContentAddressedObjectStore Store { get; }

        public ImageImportCoordinator Coordinator { get; private set; }

        public string CreateSourceRoot(string name)
        {
            var sourceRoot = Path.Combine(Root, name);
            Directory.CreateDirectory(sourceRoot);
            return sourceRoot;
        }

        public void RecreateCoordinator(Func<Stream, CancellationToken, Task<ObjectStageReceipt>> stageSourceAsync)
        {
            Coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Coordinator = CreateCoordinator(stageSourceAsync);
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
            RemoveReadOnlyAttributes(Root);
            Directory.Delete(Root, recursive: true);
        }

        private ImageImportCoordinator CreateCoordinator(Func<Stream, CancellationToken, Task<ObjectStageReceipt>>? stageSourceAsync) =>
            new(Catalog, Security, Discovery, Store, stageSourceAsync, new ImageImportCoordinatorOptions());

        private static void RemoveReadOnlyAttributes(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }
        }
    }

    private sealed class TestProtector : IImageImportSecretProtector
    {
        private static readonly byte[] Secret = Encoding.UTF8.GetBytes("qiongtu-fault-injection-test-protector");

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
