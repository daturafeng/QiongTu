using System.Diagnostics;
using System.Text;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ImageSourcePreflightProbeTests
{
    [TestMethod]
    public async Task AnalyzeAsync_StreamsBoundedBytesToRealProbeWithoutPathArguments()
    {
        var payload = CreateDjiXmpJpeg();
        var observedArguments = new List<string>();
        var client = new IsolatedImageSourcePreflightProbeClient(
            new ImageSourcePreflightProbeOptions(Timeout: TimeSpan.FromSeconds(10)),
            () => CreateDevelopmentProbeStartInfo(observedArguments));

        var result = await client.AnalyzeAsync(
            new MemoryStream(payload, writable: false),
            "image_candidate",
            null,
            null,
            CancellationToken.None);

        Assert.AreEqual("supports_dji", result.EvidenceState);
        Assert.IsFalse(result.Privacy.PathsIncluded);
        Assert.IsFalse(result.Privacy.RawMetadataIncluded);
        CollectionAssert.AreEqual(
            new[] { ImageProbeProtocol.StdioArgument },
            observedArguments);
    }

    [TestMethod]
    public async Task AnalyzeAsync_ReadsAtMostProtocolPayloadLimit()
    {
        var payload = new byte[ImageProbeProtocol.MaximumPayloadBytes + 1024];
        payload[0] = 0xff;
        payload[1] = 0xd8;
        using var source = new MemoryStream(payload, writable: false);
        var client = new IsolatedImageSourcePreflightProbeClient(
            new ImageSourcePreflightProbeOptions(Timeout: TimeSpan.FromSeconds(10)),
            CreateDevelopmentProbeStartInfo);

        var result = await client.AnalyzeAsync(
            source,
            "image_candidate",
            null,
            null,
            CancellationToken.None);

        Assert.AreEqual(ImageProbeProtocol.MaximumPayloadBytes + 1, source.Position);
        Assert.AreEqual("unconfirmed", result.EvidenceState);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "evidence_read_limit_exceeded");
    }

    [TestMethod]
    public async Task AnalyzeAsync_DjiEvidenceInTruncatedPrefixCannotPassTheInputLimit()
    {
        var djiPrefix = CreateDjiXmpJpeg();
        var payload = new byte[ImageProbeProtocol.MaximumPayloadBytes + 1];
        djiPrefix.CopyTo(payload, 0);
        using var source = new MemoryStream(payload, writable: false);
        var client = new IsolatedImageSourcePreflightProbeClient(
            new ImageSourcePreflightProbeOptions(Timeout: TimeSpan.FromSeconds(10)),
            CreateDevelopmentProbeStartInfo);

        var result = await client.AnalyzeAsync(
            source,
            "image_candidate",
            null,
            null,
            CancellationToken.None);

        Assert.AreEqual("unconfirmed", result.EvidenceState);
        CollectionAssert.Contains(result.EvidenceKinds.ToArray(), "dji_xmp_namespace");
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "evidence_read_limit_exceeded");
    }

    [TestMethod]
    public async Task AnalyzeAsync_TimeoutKillsProcessTreeAndReturnsStableCode()
    {
        var client = new IsolatedImageSourcePreflightProbeClient(
            new ImageSourcePreflightProbeOptions(Timeout: TimeSpan.FromMilliseconds(150)),
            () =>
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "ping.exe")
                };
                startInfo.ArgumentList.Add("-n");
                startInfo.ArgumentList.Add("30");
                startInfo.ArgumentList.Add("127.0.0.1");
                return startInfo;
            });

        var exception = await Assert.ThrowsAsync<ImageSourcePreflightProbeException>(() =>
            client.AnalyzeAsync(
                new MemoryStream([0xff, 0xd8, 0xff, 0xd9], writable: false),
                "image_candidate",
                null,
                null,
                CancellationToken.None));

        Assert.AreEqual("image_probe_timeout", exception.Code);
    }

    [TestMethod]
    public async Task AnalyzeAsync_OversizedChildOutputReturnsOnlyTheStableLimitCode()
    {
        var client = new IsolatedImageSourcePreflightProbeClient(
            new ImageSourcePreflightProbeOptions(
                Timeout: TimeSpan.FromSeconds(10),
                MaximumOutputBytes: 128),
            CreateDevelopmentProbeStartInfo);

        var exception = await Assert.ThrowsAsync<ImageSourcePreflightProbeException>(() =>
            client.AnalyzeAsync(
                new MemoryStream(SourcePreflightSyntheticFixture.DjiXmp(), writable: false),
                "image_candidate",
                null,
                null,
                CancellationToken.None));

        Assert.AreEqual("image_probe_output_limit_exceeded", exception.Code);
        Assert.DoesNotContain(
            SourcePreflightSyntheticFixture.PrivateDeviceMarker,
            exception.Message,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void ProductStartInfo_UsesOnlyFixedChildPathAndProtocolArgument()
    {
        var startInfo = IsolatedImageSourcePreflightProbeClient.CreateProductStartInfo();

        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "image-probe", "QiongTu.ImageProbe.exe")),
            startInfo.FileName);
        CollectionAssert.AreEqual(
            new[] { ImageProbeProtocol.StdioArgument },
            startInfo.ArgumentList.ToArray());
    }

    [TestMethod]
    public async Task AnalyzeAsync_UsesVerifiedReadOnlyLocatorHandleAndLeavesSourceUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), $"qiongtu-preflight-source-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "candidate.jpeg");
            var payload = CreateDjiXmpJpeg();
            await File.WriteAllBytesAsync(sourcePath, payload);
            var originalWriteTime = File.GetLastWriteTimeUtc(sourcePath);
            var security = new ImageImportSourceSecurity(Path.Combine(root, "locators"));
            var discovery = new ImageImportSourceDiscovery(security);
            var controlPaths = ControlDataPaths.Create(Path.Combine(root, "control"));
            var discovered = await discovery.DiscoverAsync(
                "image-import-session-preflight",
                sourceRoot,
                controlPaths);
            var item = discovered.Candidates.Single();
            var manifest = await security.LoadRecoveryManifestAsync("image-import-session-preflight");
            var client = new IsolatedImageSourcePreflightProbeClient(
                new ImageSourcePreflightProbeOptions(Timeout: TimeSpan.FromSeconds(10)),
                CreateDevelopmentProbeStartInfo);
            var probe = new ImageSourcePreflightProbe(discovery, client);

            var result = await probe.AnalyzeAsync(
                manifest,
                item.SourceItemKey,
                item.Snapshot,
                "image_candidate",
                null);

            Assert.AreEqual("supports_dji", result.EvidenceState);
            Assert.AreEqual(originalWriteTime, File.GetLastWriteTimeUtc(sourcePath));
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(sourcePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AnalyzeAsync_RejectsSourceChangedAfterDiscoveryBeforeStartingProbe()
    {
        var root = Path.Combine(Path.GetTempPath(), $"qiongtu-preflight-changed-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var sourcePath = Path.Combine(sourceRoot, "candidate.jpeg");
            await File.WriteAllBytesAsync(sourcePath, CreateDjiXmpJpeg());
            var security = new ImageImportSourceSecurity(Path.Combine(root, "locators"));
            var discovery = new ImageImportSourceDiscovery(security);
            var discovered = await discovery.DiscoverAsync(
                "image-import-session-preflight-changed",
                sourceRoot,
                ControlDataPaths.Create(Path.Combine(root, "control")));
            var item = discovered.Candidates.Single();
            var manifest = await security.LoadRecoveryManifestAsync("image-import-session-preflight-changed");
            await File.WriteAllTextAsync(sourcePath, "changed after discovery");
            var probe = new ImageSourcePreflightProbe(
                discovery,
                new IsolatedImageSourcePreflightProbeClient(
                    new ImageSourcePreflightProbeOptions(Timeout: TimeSpan.FromSeconds(10)),
                    CreateDevelopmentProbeStartInfo));

            var exception = await Assert.ThrowsAsync<ImageImportSourceDiscoveryException>(() =>
                probe.AnalyzeAsync(
                    manifest,
                    item.SourceItemKey,
                    item.Snapshot,
                    "image_candidate",
                    null));

            Assert.AreEqual("source_changed", exception.Code);
            Assert.IsFalse(exception.Message.Contains(sourcePath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProcessStartInfo CreateDevelopmentProbeStartInfo() =>
        CreateDevelopmentProbeStartInfo(null);

    private static ProcessStartInfo CreateDevelopmentProbeStartInfo(ICollection<string>? observedArguments)
    {
        var executablePath = Path.Combine(
            FindRepositoryRoot(),
            "services",
            "image-probe",
            "src",
            "QiongTu.ImageProbe",
            "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net10.0",
            "win-x64",
            "QiongTu.ImageProbe.exe");
        var startInfo = new ProcessStartInfo { FileName = executablePath };
        startInfo.ArgumentList.Add(ImageProbeProtocol.StdioArgument);
        observedArguments?.Add(ImageProbeProtocol.StdioArgument);
        return startInfo;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root could not be located for the image probe test.");
    }

    private static byte[] CreateDjiXmpJpeg()
    {
        var identifier = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
        var xmp = Encoding.UTF8.GetBytes(
            "<rdf:Description xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" xmlns:drone-dji=\"http://www.dji.com/drone-dji/1.0/\" drone-dji:GimbalYawDegree=\"1\"/>");
        var segmentLength = identifier.Length + xmp.Length + 2;
        using var stream = new MemoryStream();
        stream.Write([0xff, 0xd8, 0xff, 0xe1, (byte)(segmentLength >> 8), (byte)segmentLength]);
        stream.Write(identifier);
        stream.Write(xmp);
        stream.Write([0xff, 0xd9]);
        return stream.ToArray();
    }
}
