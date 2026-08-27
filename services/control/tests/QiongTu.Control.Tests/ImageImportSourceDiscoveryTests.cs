using System.Security.Cryptography;
using System.Text;
using System.ComponentModel;
using System.Diagnostics;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ImageImportSourceDiscoveryTests
{
    [TestMethod]
    public async Task DiscoversOnlyFixedCandidateSuffixesWithStableSafeResults()
    {
        using var scope = new ImportSourceDiscoveryScope();
        await File.WriteAllTextAsync(Path.Combine(scope.SourceRoot, "B.JPG"), "b");
        await File.WriteAllTextAsync(Path.Combine(scope.SourceRoot, "ignored.txt"), "ignored");
        Directory.CreateDirectory(Path.Combine(scope.SourceRoot, "nested"));
        await File.WriteAllTextAsync(Path.Combine(scope.SourceRoot, "nested", "A.jpeg"), "a");
        await File.WriteAllTextAsync(Path.Combine(scope.SourceRoot, "nested", "C.mpo"), "c");

        var result = await scope.Discovery.DiscoverAsync("session-stable", scope.SourceRoot, scope.ControlPaths);

        Assert.HasCount(3, result.Candidates);
        CollectionAssert.AreEqual(
            new[] { "B.JPG", "A.jpeg", "C.mpo" },
            result.Candidates.Select(item => item.LeafDisplayName).ToArray());
        Assert.IsTrue(result.Candidates.All(item => item.SourceRootKey == result.SourceRoot.SourceRootKey));
        Assert.IsTrue(result.Candidates.All(item => item.Snapshot.Length == 1));
        Assert.IsTrue(result.Candidates.All(item => !string.IsNullOrWhiteSpace(item.SourceItemKey)));
        Assert.IsFalse(result.Candidates.Any(item => item.LeafDisplayName.Contains(scope.SourceRoot, StringComparison.OrdinalIgnoreCase)));

        var manifestText = await File.ReadAllTextAsync(scope.Security.GetRecoveryManifestPath("session-stable"));
        Assert.IsFalse(manifestText.Contains(scope.SourceRoot, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(manifestText.Contains("nested", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(manifestText.Contains("A.jpeg", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task BlocksMutualOverlapWithControlDirectories()
    {
        using var scope = new ImportSourceDiscoveryScope();
        var broadSource = Path.GetDirectoryName(scope.ControlPaths.RuntimeDirectory)!;

        var exception = await Assert.ThrowsAsync<ImageImportSourceDiscoveryException>(
            () => scope.Discovery.DiscoverAsync("session-overlap", broadSource, scope.ControlPaths));

        Assert.AreEqual("source_control_path_overlap", exception.Code);
        Assert.IsFalse(exception.Message.Contains(broadSource, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task EnforcesCandidateAndEntryLimitsWithoutSilentTruncation()
    {
        using var scope = new ImportSourceDiscoveryScope(
            new ImageImportSourceDiscoveryOptions(MaximumEntries: 10, MaximumCandidates: 1, MaximumDepth: 64));
        await File.WriteAllTextAsync(Path.Combine(scope.SourceRoot, "A.JPG"), "a");
        await File.WriteAllTextAsync(Path.Combine(scope.SourceRoot, "B.JPG"), "b");

        var exception = await Assert.ThrowsAsync<ImageImportSourceDiscoveryException>(
            () => scope.Discovery.DiscoverAsync("session-limit", scope.SourceRoot, scope.ControlPaths));

        Assert.AreEqual("source_candidate_limit_exceeded", exception.Code);
    }

    [TestMethod]
    public async Task EnforcesDepthLimit()
    {
        using var scope = new ImportSourceDiscoveryScope(
            new ImageImportSourceDiscoveryOptions(MaximumEntries: 10, MaximumCandidates: 10, MaximumDepth: 0));
        Directory.CreateDirectory(Path.Combine(scope.SourceRoot, "nested"));

        var exception = await Assert.ThrowsAsync<ImageImportSourceDiscoveryException>(
            () => scope.Discovery.DiscoverAsync("session-depth", scope.SourceRoot, scope.ControlPaths));

        Assert.AreEqual("source_scan_depth_limit_exceeded", exception.Code);
    }

    [TestMethod]
    public async Task CopyUsesReadOnlyHandleAndKeepsSourceUnmodified()
    {
        using var scope = new ImportSourceDiscoveryScope();
        var sourcePath = Path.Combine(scope.SourceRoot, "DJI_0001.JPG");
        var bytes = Encoding.UTF8.GetBytes("immutable-source");
        await File.WriteAllBytesAsync(sourcePath, bytes);
        var originalWriteTime = File.GetLastWriteTimeUtc(sourcePath);
        var result = await scope.Discovery.DiscoverAsync("session-copy", scope.SourceRoot, scope.ControlPaths);
        var item = result.Candidates.Single();
        var manifest = await scope.Security.LoadRecoveryManifestAsync("session-copy");
        await using var destination = new MemoryStream();

        var copy = await scope.Discovery.CopySourceItemAsync(manifest, item.SourceItemKey, item.Snapshot, destination);

        CollectionAssert.AreEqual(bytes, destination.ToArray());
        Assert.AreEqual(bytes.Length, copy.BytesCopied);
        Assert.AreEqual(item.SourceItemKey, copy.SourceItemKey);
        Assert.AreEqual(originalWriteTime, File.GetLastWriteTimeUtc(sourcePath));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(sourcePath));
    }

    [TestMethod]
    public async Task CopyRejectsChangedSourceSnapshot()
    {
        using var scope = new ImportSourceDiscoveryScope();
        var sourcePath = Path.Combine(scope.SourceRoot, "DJI_0001.JPG");
        await File.WriteAllTextAsync(sourcePath, "before");
        var result = await scope.Discovery.DiscoverAsync("session-changed", scope.SourceRoot, scope.ControlPaths);
        var item = result.Candidates.Single();
        var manifest = await scope.Security.LoadRecoveryManifestAsync("session-changed");
        await File.WriteAllTextAsync(sourcePath, "after-change");
        await using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<ImageImportSourceDiscoveryException>(
            () => scope.Discovery.CopySourceItemAsync(manifest, item.SourceItemKey, item.Snapshot, destination));

        Assert.AreEqual("source_changed", exception.Code);
        Assert.IsFalse(exception.Message.Contains(sourcePath, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task CopyReportsLockedSourceAsRetryable()
    {
        using var scope = new ImportSourceDiscoveryScope();
        var sourcePath = Path.Combine(scope.SourceRoot, "DJI_0001.JPG");
        await File.WriteAllTextAsync(sourcePath, "locked");
        var result = await scope.Discovery.DiscoverAsync("session-locked", scope.SourceRoot, scope.ControlPaths);
        var item = result.Candidates.Single();
        var manifest = await scope.Security.LoadRecoveryManifestAsync("session-locked");
        await using var locked = new FileStream(sourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<ImageImportSourceDiscoveryException>(
            () => scope.Discovery.CopySourceItemAsync(manifest, item.SourceItemKey, item.Snapshot, destination));

        Assert.AreEqual("source_locked", exception.Code);
    }

    [TestMethod]
    public async Task ReparseRootIsRejectedWithoutFollowing()
    {
        using var scope = new ImportSourceDiscoveryScope();
        await File.WriteAllTextAsync(Path.Combine(scope.SourceRoot, "target.JPG"), "target");
        var linkPath = Path.Combine(scope.Root, "linked-source");
        if (!TryCreateDirectoryJunction(linkPath, scope.SourceRoot))
        {
            Assert.Inconclusive("Could not create a directory junction or symbolic link in this environment.");
        }

        try
        {
            var rootException = await Assert.ThrowsAsync<ImageImportSourceDiscoveryException>(
                () => scope.Discovery.DiscoverAsync("session-reparse-root", linkPath, scope.ControlPaths));
            Assert.AreEqual("source_root_reparse_point", rootException.Code);
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }
        }
    }

    [TestMethod]
    public async Task NestedReparseEntryIsSkippedAndDoesNotExposeTargetContent()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows reparse point behavior is required for this test.");
        }

        using var scope = new ImportSourceDiscoveryScope();
        await File.WriteAllTextAsync(Path.Combine(scope.SourceRoot, "NORMAL.JPG"), "normal");
        var outsideTarget = Path.Combine(scope.Root, "outside-target");
        Directory.CreateDirectory(outsideTarget);
        await File.WriteAllTextAsync(Path.Combine(outsideTarget, "LEAK.JPG"), "must-not-be-discovered");
        var linkPath = Path.Combine(scope.SourceRoot, "linked-outside");
        if (!TryCreateDirectoryJunction(linkPath, outsideTarget))
        {
            Assert.Inconclusive("Could not create a directory junction or symbolic link in this environment.");
        }

        try
        {
            var result = await scope.Discovery.DiscoverAsync("session-reparse-skip", scope.SourceRoot, scope.ControlPaths);

            Assert.HasCount(1, result.Candidates);
            Assert.AreEqual("NORMAL.JPG", result.Candidates[0].LeafDisplayName);
            var manifestText = await File.ReadAllTextAsync(scope.Security.GetRecoveryManifestPath("session-reparse-skip"));
            Assert.IsFalse(manifestText.Contains("LEAK.JPG", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(manifestText.Contains(outsideTarget, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }
        }
    }

    [TestMethod]
    public async Task UnicodeAndCasePathConflictsAreExplicit()
    {
        using var scope = new ImportSourceDiscoveryScope();
        await File.WriteAllTextAsync(Path.Combine(scope.SourceRoot, "Caf\u00e9.JPG"), "one");
        await File.WriteAllTextAsync(Path.Combine(scope.SourceRoot, "Cafe\u0301.jpg"), "two");

        var exception = await Assert.ThrowsAsync<ImageImportSourceDiscoveryException>(
            () => scope.Discovery.DiscoverAsync("session-unicode", scope.SourceRoot, scope.ControlPaths));

        Assert.AreEqual("source_path_normalization_conflict", exception.Code);
    }

    private sealed class ImportSourceDiscoveryScope : IDisposable
    {
        private readonly TestProtector _protector = new();

        public ImportSourceDiscoveryScope(ImageImportSourceDiscoveryOptions? options = null)
        {
            Root = Path.Combine(Path.GetTempPath(), $"qiongtu-import-discovery-{Guid.NewGuid():N}");
            SourceRoot = Path.Combine(Root, "source");
            Directory.CreateDirectory(SourceRoot);
            Security = new ImageImportSourceSecurity(
                Path.Combine(Root, "locators"),
                _protector,
                () => Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
            Discovery = new ImageImportSourceDiscovery(Security, options);
            ControlPaths = ControlDataPaths.Create(Path.Combine(Root, "control-data"));
        }

        public string Root { get; }

        public string SourceRoot { get; }

        public ImageImportSourceSecurity Security { get; }

        public ImageImportSourceDiscovery Discovery { get; }

        public ControlDataPaths ControlPaths { get; }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TestProtector : IImageImportSecretProtector
    {
        private static readonly byte[] Secret = Encoding.UTF8.GetBytes("qiongtu-discovery-test-protector");

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

    private static bool TryCreateDirectoryJunction(string linkPath, string targetPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(linkPath);
        psi.ArgumentList.Add(targetPath);

        try
        {
            using var process = Process.Start(psi);
            if (process is not null && process.WaitForExit(5000) && process.ExitCode == 0)
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
