using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ContentAddressedObjectStoreTests
{
    [TestMethod]
    public async Task StagesSourceReadOnlyAndPublishesOnlyAfterVerification()
    {
        using var scope = new ObjectStoreScope();
        var sourcePath = Path.Combine(scope.Root, "DJI_0001.JPG");
        var sourceBytes = Encoding.UTF8.GetBytes("immutable-drone-source");
        await File.WriteAllBytesAsync(sourcePath, sourceBytes);
        var sourceLastWrite = File.GetLastWriteTimeUtc(sourcePath);
        var expected = Sha256(sourceBytes);
        var store = new ContentAddressedObjectStore(scope.StoreRoot);

        var stage = await store.StageFileAsync(sourcePath, expected.ToUpperInvariant());

        CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(sourcePath));
        Assert.AreEqual(sourceLastWrite, File.GetLastWriteTimeUtc(sourcePath));
        Assert.AreEqual(expected, stage.Sha256);
        Assert.AreEqual(sourceBytes.LongLength, stage.ByteLength);
        Assert.IsEmpty(Directory.GetFiles(store.PublishedDirectory, "*", SearchOption.AllDirectories));
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(store.StagingDirectory, stage.StageId, "stage.json"));
        Assert.IsFalse(manifestBytes.Length >= 3 && manifestBytes[0] == 0xef && manifestBytes[1] == 0xbb && manifestBytes[2] == 0xbf);
        var manifestText = Encoding.UTF8.GetString(manifestBytes);
        Assert.IsFalse(manifestText.Contains(sourcePath, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(manifestText.Contains(Path.GetFileName(sourcePath), StringComparison.OrdinalIgnoreCase));

        var published = await store.PublishAsync(stage);

        Assert.IsFalse(published.Deduplicated);
        Assert.AreEqual($"sha256/{expected[..2]}/{expected}", published.ObjectKey);
        CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(ResolvePublished(store, published.ObjectKey)));
        Assert.IsFalse(Directory.Exists(Path.Combine(store.StagingDirectory, stage.StageId)));
        CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(sourcePath));
    }

    [TestMethod]
    public async Task ExpectedChecksumMismatchIsQuarantinedWithoutFormalObject()
    {
        using var scope = new ObjectStoreScope();
        var sourcePath = Path.Combine(scope.Root, "source.bin");
        var bytes = Encoding.UTF8.GetBytes("actual-content");
        await File.WriteAllBytesAsync(sourcePath, bytes);
        var store = new ContentAddressedObjectStore(scope.StoreRoot);

        var exception = await Assert.ThrowsAsync<ObjectStoreException>(
            () => store.StageFileAsync(sourcePath, new string('0', 64)));

        Assert.AreEqual("object_checksum_mismatch", exception.Code);
        Assert.IsNotNull(exception.QuarantineId);
        Assert.IsFalse(exception.Message.Contains(sourcePath, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(exception.Message.Contains(Path.GetFileName(sourcePath), StringComparison.OrdinalIgnoreCase));
        Assert.IsEmpty(Directory.GetFiles(store.PublishedDirectory, "*", SearchOption.AllDirectories));
        Assert.IsTrue(File.Exists(Path.Combine(store.QuarantineDirectory, exception.QuarantineId, "payload")));
        Assert.IsTrue(File.Exists(Path.Combine(store.QuarantineDirectory, exception.QuarantineId, "failure.json")));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(sourcePath));
    }

    [TestMethod]
    public async Task RestartRecoversCompleteStageAndQuarantinesTamperedStage()
    {
        using var scope = new ObjectStoreScope();
        var firstStore = new ContentAddressedObjectStore(scope.StoreRoot);
        var valid = await firstStore.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("recoverable")));
        var tampered = await firstStore.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("tampered")));
        await File.AppendAllTextAsync(
            Path.Combine(firstStore.StagingDirectory, tampered.StageId, "payload"),
            "changed");
        var incompleteStageId = new string('f', 32);
        var incompleteDirectory = Path.Combine(firstStore.StagingDirectory, incompleteStageId);
        Directory.CreateDirectory(incompleteDirectory);
        await File.WriteAllTextAsync(Path.Combine(incompleteDirectory, "payload"), "incomplete");

        var restartedStore = new ContentAddressedObjectStore(scope.StoreRoot);
        var recovery = await restartedStore.RecoverStagedAsync();

        Assert.HasCount(1, recovery.Recoverable);
        Assert.AreEqual(valid.StageId, recovery.Recoverable[0].StageId);
        Assert.HasCount(2, recovery.Quarantined);
        CollectionAssert.AreEquivalent(
            new[] { tampered.StageId, incompleteStageId },
            recovery.Quarantined.Select(item => item.StageId).ToArray());
        Assert.IsFalse(Directory.Exists(Path.Combine(restartedStore.StagingDirectory, tampered.StageId)));
        Assert.IsEmpty(Directory.GetFiles(restartedStore.PublishedDirectory, "*", SearchOption.AllDirectories));

        var published = await restartedStore.PublishAsync(recovery.Recoverable[0]);
        Assert.IsTrue(File.Exists(ResolvePublished(restartedStore, published.ObjectKey)));
    }

    [TestMethod]
    public async Task ConcurrentIdenticalPublicationKeepsOneFormalObject()
    {
        using var scope = new ObjectStoreScope();
        var bytes = RandomNumberGenerator.GetBytes(256 * 1024);
        var store = new ContentAddressedObjectStore(scope.StoreRoot);
        var first = await store.StageAsync(new MemoryStream(bytes));
        var second = await store.StageAsync(new MemoryStream(bytes));

        var published = await Task.WhenAll(store.PublishAsync(first), store.PublishAsync(second));

        Assert.AreEqual(1, published.Count(item => !item.Deduplicated));
        Assert.AreEqual(1, published.Count(item => item.Deduplicated));
        Assert.AreEqual(published[0].ObjectKey, published[1].ObjectKey);
        Assert.HasCount(1, Directory.GetFiles(store.PublishedDirectory, "*", SearchOption.AllDirectories));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(ResolvePublished(store, published[0].ObjectKey)));
    }

    [TestMethod]
    public async Task FormalContentConflictNeverOverwritesExistingBytes()
    {
        using var scope = new ObjectStoreScope();
        var bytes = Encoding.UTF8.GetBytes("valid-stage");
        var store = new ContentAddressedObjectStore(scope.StoreRoot);
        var stage = await store.StageAsync(new MemoryStream(bytes));
        var key = $"sha256/{stage.Sha256[..2]}/{stage.Sha256}";
        var target = ResolvePublished(store, key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var conflictingBytes = Encoding.UTF8.GetBytes("corrupt-formal-object");
        await File.WriteAllBytesAsync(target, conflictingBytes);

        var exception = await Assert.ThrowsAsync<ObjectStoreException>(() => store.PublishAsync(stage));

        Assert.AreEqual("object_formal_conflict", exception.Code);
        Assert.IsNotNull(exception.QuarantineId);
        CollectionAssert.AreEqual(conflictingBytes, await File.ReadAllBytesAsync(target));
        Assert.IsTrue(File.Exists(Path.Combine(store.QuarantineDirectory, exception.QuarantineId, "payload")));
    }

    [TestMethod]
    public async Task CancelledStageLeavesNoPartialStageOrFormalObject()
    {
        using var scope = new ObjectStoreScope();
        var store = new ContentAddressedObjectStore(scope.StoreRoot);
        using var cancellation = new CancellationTokenSource();
        await using var source = new CancellingStream(cancellation);

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.StageAsync(source, cancellationToken: cancellation.Token));

        Assert.IsEmpty(Directory.GetDirectories(store.StagingDirectory));
        Assert.IsEmpty(Directory.GetFiles(store.PublishedDirectory, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task LargeNonSeekableStreamIsHashedAndPublishedIncrementally()
    {
        using var scope = new ObjectStoreScope();
        const long byteLength = 32L * 1024 * 1024;
        const byte value = 0x5a;
        var expected = HashRepeatedByte(value, byteLength);
        var store = new ContentAddressedObjectStore(scope.StoreRoot);
        await using var source = new RepeatingByteStream(value, byteLength);

        var stage = await store.StageAsync(source, expected);
        var published = await store.PublishAsync(stage);

        Assert.AreEqual(byteLength, published.ByteLength);
        Assert.AreEqual(expected, published.Sha256);
        Assert.AreEqual(byteLength, new FileInfo(ResolvePublished(store, published.ObjectKey)).Length);
    }

    [TestMethod]
    public async Task ArtifactServerCanReadPublishedButNotStagingOrQuarantine()
    {
        using var scope = new ObjectStoreScope();
        var store = new ContentAddressedObjectStore(scope.StoreRoot);
        var stage = await store.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("viewer-object")));
        var futureKey = $"sha256/{stage.Sha256[..2]}/{stage.Sha256}";
        var registry = new ArtifactRootRegistry();
        registry.RegisterTrustedRoot("objects", store.PublishedDirectory);
        await using var server = new ArtifactServer(registry);
        await server.StartAsync(CancellationToken.None);
        var session = server.CreateSession();
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", session.AccessToken);

        using var beforePublish = await client.GetAsync($"{session.BaseUrl}/artifacts/objects/{futureKey}");
        Assert.AreEqual(HttpStatusCode.NotFound, beforePublish.StatusCode);
        using var stagingTraversal = await client.GetAsync(
            $"{session.BaseUrl}/artifacts/objects/%2e%2e/staging/{stage.StageId}/payload");
        Assert.AreNotEqual(HttpStatusCode.OK, stagingTraversal.StatusCode);

        var published = await store.PublishAsync(stage);
        using var afterPublish = await client.GetAsync($"{session.BaseUrl}/artifacts/objects/{published.ObjectKey}");
        Assert.AreEqual(HttpStatusCode.OK, afterPublish.StatusCode);
        Assert.AreEqual("viewer-object", await afterPublish.Content.ReadAsStringAsync());
    }

    private static string ResolvePublished(ContentAddressedObjectStore store, string objectKey) =>
        Path.Combine(store.PublishedDirectory, objectKey.Replace('/', Path.DirectorySeparatorChar));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string HashRepeatedByte(byte value, long byteLength)
    {
        var buffer = new byte[128 * 1024];
        Array.Fill(buffer, value);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var remaining = byteLength;
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, buffer.Length);
            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class ObjectStoreScope : IDisposable
    {
        public ObjectStoreScope()
        {
            Root = Path.Combine(Path.GetTempPath(), $"qiongtu-object-store-{Guid.NewGuid():N}");
            StoreRoot = Path.Combine(Root, "objects");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string StoreRoot { get; }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class CancellingStream : Stream
    {
        private readonly CancellationTokenSource _cancellation;
        private bool _returnedData;

        public CancellingStream(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_returnedData)
            {
                _returnedData = true;
                buffer.Span[0] = 0x51;
                _cancellation.Cancel();
                return ValueTask.FromResult(1);
            }

            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RepeatingByteStream : Stream
    {
        private readonly byte _value;
        private long _remaining;

        public RepeatingByteStream(byte value, long byteLength)
        {
            _value = value;
            _remaining = byteLength;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_remaining == 0)
            {
                return ValueTask.FromResult(0);
            }

            var count = (int)Math.Min(_remaining, buffer.Length);
            buffer.Span[..count].Fill(_value);
            _remaining -= count;
            return ValueTask.FromResult(count);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
