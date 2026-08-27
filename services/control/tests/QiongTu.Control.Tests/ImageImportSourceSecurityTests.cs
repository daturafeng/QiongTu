using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ImageImportSourceSecurityTests
{
    [TestMethod]
    public async Task ProtectedManifestRoundTripsWithoutPlaintextPaths()
    {
        using var scope = new ImportSourceSecurityScope();
        var sourceRoot = Path.Combine(scope.Root, "Drone Card");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "DCIM"));
        var relativePath = Path.Combine("DCIM", "DJI_0001.JPG");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, relativePath), "image-bytes");
        var security = scope.CreateSecurity();
        var rootKey = await security.CreateSourceRootKeyAsync(sourceRoot);
        var itemKey = await security.CreateSourceItemKeyAsync(sourceRoot, relativePath);

        StringAssert.Matches(rootKey, new System.Text.RegularExpressions.Regex("^[a-f0-9]{64}$"));
        StringAssert.Matches(itemKey, new System.Text.RegularExpressions.Regex("^[a-f0-9]{64}$"));

        await security.SaveRecoveryManifestAsync(
            new ImageImportSourceRecoveryManifest(
                "session-1",
                rootKey,
                sourceRoot,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [itemKey] = relativePath
                }));

        var manifestPath = security.GetRecoveryManifestPath("session-1");
        var manifestText = await File.ReadAllTextAsync(manifestPath);
        Assert.IsFalse(manifestText.Contains(sourceRoot, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(manifestText.Contains("DCIM", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(manifestText.Contains("DJI_0001.JPG", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(Path.GetFileName(manifestPath).Contains("Drone Card", StringComparison.OrdinalIgnoreCase));

        var reloaded = await scope.CreateSecurity().LoadRecoveryManifestAsync("session-1");

        Assert.AreEqual(Path.GetFullPath(sourceRoot), reloaded.AbsoluteSourceRoot);
        Assert.AreEqual(rootKey, reloaded.SourceRootKey);
        Assert.AreEqual(relativePath, reloaded.RelativePathBySourceItemKey[itemKey]);
    }

    [TestMethod]
    public async Task InstallHmacKeyIsProtectedAndStableAcrossInstances()
    {
        using var scope = new ImportSourceSecurityScope();
        var sourceRoot = Path.Combine(scope.Root, "source");
        Directory.CreateDirectory(sourceRoot);
        var first = scope.CreateSecurity();

        var firstKey = await first.CreateSourceRootKeyAsync(sourceRoot);
        var secondKey = await scope.CreateSecurity(() => throw new InvalidOperationException("key should be loaded"))
            .CreateSourceRootKeyAsync(sourceRoot);

        Assert.AreEqual(firstKey, secondKey);
        var keyFile = await File.ReadAllTextAsync(first.KeyFilePath);
        Assert.IsFalse(keyFile.Contains(Convert.ToBase64String(scope.InstallKey), StringComparison.Ordinal));
        Assert.AreNotEqual(firstKey, await first.CreateSourceRootKeyAsync(Path.Combine(scope.Root, "other")));
    }

    [TestMethod]
    public async Task ProtectedManifestTamperingIsRejected()
    {
        using var scope = new ImportSourceSecurityScope();
        var sourceRoot = Path.Combine(scope.Root, "source");
        Directory.CreateDirectory(sourceRoot);
        var security = scope.CreateSecurity();
        var rootKey = await security.CreateSourceRootKeyAsync(sourceRoot);
        var itemKey = await security.CreateSourceItemKeyAsync(sourceRoot, "DJI_0001.JPG");
        await security.SaveRecoveryManifestAsync(
            new ImageImportSourceRecoveryManifest(
                "session-2",
                rootKey,
                sourceRoot,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [itemKey] = "DJI_0001.JPG"
                }));
        var manifestPath = security.GetRecoveryManifestPath("session-2");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        var protectedPayload = node["protectedPayload"]!.GetValue<string>();
        node["protectedPayload"] = protectedPayload[..^2] + (protectedPayload[^2] == 'A' ? 'B' : 'A') + protectedPayload[^1];
        await File.WriteAllTextAsync(manifestPath, node.ToJsonString());

        var exception = await Assert.ThrowsAsync<ImageImportSourceSecurityException>(
            () => security.LoadRecoveryManifestAsync("session-2"));

        Assert.AreEqual("source_locator_manifest_protection_failed", exception.Code);
        Assert.IsFalse(exception.Message.Contains(sourceRoot, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task PathNormalizationKeepsWindowsCaseAndUnicodeKeysStable()
    {
        using var scope = new ImportSourceSecurityScope();
        var sourceRoot = Path.Combine(scope.Root, "source");
        Directory.CreateDirectory(sourceRoot);
        var security = scope.CreateSecurity();

        var first = await security.CreateSourceItemKeyAsync(sourceRoot.ToUpperInvariant(), "Cafe\u0301.JPG");
        var second = await security.CreateSourceItemKeyAsync(sourceRoot.ToLowerInvariant(), "Caf\u00e9.jpg");
        var movedRoot = await security.CreateSourceItemKeyAsync(Path.Combine(scope.Root, "remounted"), "Caf\u00e9.jpg");

        Assert.AreEqual(first, second);
        Assert.AreEqual(first, movedRoot, "Opaque item keys must remain stable when a removable source root is reselected at a new mount path.");
    }

    [TestMethod]
    public void WindowsCurrentUserDpapiRoundTripsWithoutReturningPlaintext()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows CurrentUser DPAPI is only available on Windows.");
        }

        var protector = new ImageImportSourceSecurity.WindowsCurrentUserDpapiProtector();
        var plaintext = Encoding.UTF8.GetBytes("private-import-locator");
        var protectedData = protector.Protect(plaintext);
        var recovered = protector.Unprotect(protectedData);

        CollectionAssert.AreEqual(plaintext, recovered);
        CollectionAssert.AreNotEqual(plaintext, protectedData);
    }

    [TestMethod]
    public async Task PreparedManifestCommitsToSessionManifestAndProtectsSnapshots()
    {
        using var scope = new ImportSourceSecurityScope();
        var sourceRoot = Path.Combine(scope.Root, "Source With Spaces");
        Directory.CreateDirectory(sourceRoot);
        var security = scope.CreateSecurity();
        var rootKey = await security.CreateSourceRootKeyAsync(sourceRoot);
        var itemKey = await security.CreateSourceItemKeyAsync(sourceRoot, Path.Combine("DCIM", "DJI_0002.JPG"));
        var snapshot = new ImageImportSourceSnapshot(
            123,
            new DateTimeOffset(2026, 8, 24, 1, 2, 3, TimeSpan.Zero),
            FileAttributes.Archive | FileAttributes.ReadOnly,
            "identity-material-that-must-stay-protected");
        var manifest = new ImageImportSourceRecoveryManifest(
            "session-prepared",
            rootKey,
            sourceRoot,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [itemKey] = Path.Combine("DCIM", "DJI_0002.JPG")
            },
            new Dictionary<string, ImageImportSourceSnapshot>(StringComparer.Ordinal)
            {
                [itemKey] = snapshot
            });
        var preparationId = Path.Combine(scope.Root, "prep-id-with-local-path-shape");

        await security.SavePreparedRecoveryManifestAsync(manifest, preparationId);

        Assert.IsFalse(File.Exists(security.GetRecoveryManifestPath("session-prepared")));
        var preparedFiles = Directory.GetFiles(scope.Storage, "image-import-prepared-*.locator.v1.json");
        Assert.HasCount(1, preparedFiles);
        Assert.IsFalse(Path.GetFileName(preparedFiles[0]).Contains("prep-id", StringComparison.OrdinalIgnoreCase));
        var preparedText = await File.ReadAllTextAsync(preparedFiles[0]);
        Assert.IsFalse(preparedText.Contains(sourceRoot, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(preparedText.Contains("DJI_0002.JPG", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(preparedText.Contains(snapshot.Identity!, StringComparison.OrdinalIgnoreCase));

        security.CommitPreparedRecoveryManifest(preparationId, "session-prepared");
        Assert.IsFalse(File.Exists(preparedFiles[0]));
        var loaded = await security.LoadRecoveryManifestAsync("session-prepared");

        Assert.AreEqual(sourceRoot, loaded.AbsoluteSourceRoot);
        Assert.AreEqual(Path.Combine("DCIM", "DJI_0002.JPG"), loaded.RelativePathBySourceItemKey[itemKey]);
        Assert.IsNotNull(loaded.SnapshotBySourceItemKey);
        Assert.AreEqual(snapshot, loaded.SnapshotBySourceItemKey[itemKey]);
    }

    [TestMethod]
    public async Task ConcurrentFirstUseAcrossInstancesKeepsOneInstallHmacKey()
    {
        using var scope = new ImportSourceSecurityScope();
        var sourceRoot = Path.Combine(scope.Root, "source");
        Directory.CreateDirectory(sourceRoot);
        var protector = new TwoPartyGateProtector();
        var keyFactoryCalls = 0;
        byte[] KeyFactory()
        {
            var value = (byte)Interlocked.Increment(ref keyFactoryCalls);
            return Enumerable.Repeat(value, 32).ToArray();
        }

        var first = new ImageImportSourceSecurity(scope.Storage, protector, KeyFactory);
        var second = new ImageImportSourceSecurity(scope.Storage, protector, KeyFactory);

        var keys = await Task.WhenAll(
            Task.Run(() => first.CreateSourceRootKeyAsync(sourceRoot)),
            Task.Run(() => second.CreateSourceRootKeyAsync(sourceRoot)));
        var reloaded = await new ImageImportSourceSecurity(
                scope.Storage,
                protector,
                () => throw new InvalidOperationException("The persisted key should be reused."))
            .CreateSourceRootKeyAsync(sourceRoot);

        Assert.AreEqual(keys[0], keys[1], "Concurrent first-use instances must not keep different in-memory HMAC keys.");
        Assert.AreEqual(keys[0], reloaded, "The key that concurrent callers use must match the persisted install key.");
    }

    [TestMethod]
    public async Task KeyFileIoFailuresReturnSanitizedStructuredErrors()
    {
        using var scope = new ImportSourceSecurityScope();
        var security = scope.CreateSecurity();
        Directory.CreateDirectory(security.KeyFilePath);
        var sourceRoot = Path.Combine(scope.Root, "source");
        Directory.CreateDirectory(sourceRoot);

        var exception = await Assert.ThrowsAsync<ImageImportSourceSecurityException>(
            () => security.CreateSourceRootKeyAsync(sourceRoot));

        Assert.IsFalse(exception.Message.Contains(scope.Root, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(exception.ToString().Contains(scope.Root, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ImportSourceSecurityScope : IDisposable
    {
        private readonly AuthenticatedTestProtector _protector;

        public ImportSourceSecurityScope()
        {
            Root = Path.Combine(Path.GetTempPath(), $"qiongtu-import-security-{Guid.NewGuid():N}");
            Storage = Path.Combine(Root, "locators");
            Directory.CreateDirectory(Storage);
            InstallKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            _protector = new AuthenticatedTestProtector();
        }

        public string Root { get; }

        public string Storage { get; }

        public byte[] InstallKey { get; }

        public ImageImportSourceSecurity CreateSecurity(Func<byte[]>? keyFactory = null) =>
            new(Storage, _protector, keyFactory ?? (() => InstallKey.ToArray()));

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class AuthenticatedTestProtector : IImageImportSecretProtector
    {
        private static readonly byte[] Secret = Encoding.UTF8.GetBytes("qiongtu-test-protector");

        public byte[] Protect(byte[] plaintext)
        {
            using var hmac = new HMACSHA256(Secret);
            var tag = hmac.ComputeHash(plaintext);
            var protectedData = new byte[tag.Length + plaintext.Length];
            Buffer.BlockCopy(tag, 0, protectedData, 0, tag.Length);
            for (var index = 0; index < plaintext.Length; index++)
            {
                protectedData[tag.Length + index] = (byte)(plaintext[index] ^ 0xa5);
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
                plaintext[index] = (byte)(protectedData[32 + index] ^ 0xa5);
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

    private sealed class TwoPartyGateProtector : IImageImportSecretProtector
    {
        private readonly Barrier _barrier = new(2);
        private readonly AuthenticatedTestProtector _inner = new();
        private int _protectCalls;

        public byte[] Protect(byte[] plaintext)
        {
            if (plaintext.Length == 32 && Interlocked.Increment(ref _protectCalls) <= 2 &&
                !_barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The concurrent key initialization test did not reach both participants.");
            }

            return _inner.Protect(plaintext);
        }

        public byte[] Unprotect(byte[] protectedData) => _inner.Unprotect(protectedData);
    }
}
