using System.Buffers.Binary;
using System.Diagnostics;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ImageCasProbeTests
{
    [TestMethod]
    public async Task AnalyzeAsync_VerifiesFormalCasAndRunsRealProbeWithOnlyFixedArgument()
    {
        var root = CreateRoot();
        try
        {
            var store = new ContentAddressedObjectStore(Path.Combine(root, "objects"));
            var published = await PublishAsync(store, CreateClassicRgbTiff());
            var observedArguments = new List<string>();
            var client = new IsolatedImageCasProbeClient(
                new ImageCasProbeOptions(Timeout: TimeSpan.FromSeconds(20)),
                () => CreateDevelopmentProbeStartInfo(observedArguments));

            var result = await client.AnalyzeAsync(
                store,
                published,
                "source_image",
                CancellationToken.None);

            Assert.AreEqual("completed", result.Status, string.Join(',', result.ReasonCodes));
            Assert.AreEqual("tiff", result.Container);
            Assert.HasCount(1, result.Frames);
            CollectionAssert.AreEqual(new[] { ImageProbeProtocol.StdioArgument }, observedArguments);
            AssertPrivacy(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AnalyzeAsync_TamperedFormalObjectIsRejectedBeforeChildStarts()
    {
        var root = CreateRoot();
        try
        {
            var store = new ContentAddressedObjectStore(Path.Combine(root, "objects"));
            var published = await PublishAsync(store, CreateClassicRgbTiff());
            var objectPath = Path.Combine(
                store.PublishedDirectory,
                published.ObjectKey.Replace('/', Path.DirectorySeparatorChar));
            await File.AppendAllTextAsync(objectPath, "tampered");
            var starts = 0;
            var client = new IsolatedImageCasProbeClient(
                startInfoFactory: () =>
                {
                    starts++;
                    return CreateDevelopmentProbeStartInfo();
                });

            var exception = await Assert.ThrowsAsync<ImageCasProbeException>(() =>
                client.AnalyzeAsync(store, published, "source_image", CancellationToken.None));

            Assert.AreEqual("object_formal_integrity_failed", exception.Code);
            Assert.AreEqual(0, starts);
            Assert.DoesNotContain(objectPath, exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(published.Sha256, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AnalyzeAsync_NonSourceImageIsRejectedBeforeChildStarts()
    {
        var root = CreateRoot();
        try
        {
            var store = new ContentAddressedObjectStore(Path.Combine(root, "objects"));
            var published = await PublishAsync(store, CreateClassicRgbTiff());
            var starts = 0;
            var client = new IsolatedImageCasProbeClient(
                startInfoFactory: () =>
                {
                    starts++;
                    return CreateDevelopmentProbeStartInfo();
                });

            var exception = await Assert.ThrowsAsync<ImageCasProbeException>(() =>
                client.AnalyzeAsync(store, published, "formal_output", CancellationToken.None));

            Assert.AreEqual("cas_image_object_kind_invalid", exception.Code);
            Assert.AreEqual(0, starts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AnalyzeAsync_BlockedImageDoesNotPoisonNextControlProbe()
    {
        var root = CreateRoot();
        try
        {
            var store = new ContentAddressedObjectStore(Path.Combine(root, "objects"));
            var malformed = await PublishAsync(store, "not an image"u8.ToArray());
            var valid = await PublishAsync(store, CreateClassicRgbTiff());
            var client = new IsolatedImageCasProbeClient(
                new ImageCasProbeOptions(Timeout: TimeSpan.FromSeconds(20)),
                CreateDevelopmentProbeStartInfo);

            var blocked = await client.AnalyzeAsync(
                store,
                malformed,
                "source_image",
                CancellationToken.None);
            var completed = await client.AnalyzeAsync(
                store,
                valid,
                "source_image",
                CancellationToken.None);

            Assert.AreEqual("blocked", blocked.Status);
            CollectionAssert.Contains(blocked.ReasonCodes.ToArray(), "unsupported_image_container");
            Assert.AreEqual("completed", completed.Status, string.Join(',', completed.ReasonCodes));
            AssertPrivacy(blocked);
            AssertPrivacy(completed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AnalyzeAsync_TimeoutKillsChildAndReturnsStableCode()
    {
        var root = CreateRoot();
        try
        {
            var store = new ContentAddressedObjectStore(Path.Combine(root, "objects"));
            var published = await PublishAsync(store, CreateClassicRgbTiff());
            var client = new IsolatedImageCasProbeClient(
                new ImageCasProbeOptions(Timeout: TimeSpan.FromMilliseconds(150)),
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

            var exception = await Assert.ThrowsAsync<ImageCasProbeException>(() =>
                client.AnalyzeAsync(store, published, "source_image", CancellationToken.None));

            Assert.AreEqual("cas_image_probe_timeout", exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AnalyzeAsync_OversizedChildOutputReturnsStableCodeOnly()
    {
        var root = CreateRoot();
        try
        {
            var store = new ContentAddressedObjectStore(Path.Combine(root, "objects"));
            var published = await PublishAsync(store, CreateClassicRgbTiff());
            var client = new IsolatedImageCasProbeClient(
                new ImageCasProbeOptions(
                    Timeout: TimeSpan.FromSeconds(20),
                    MaximumOutputBytes: 128),
                CreateDevelopmentProbeStartInfo);

            var exception = await Assert.ThrowsAsync<ImageCasProbeException>(() =>
                client.AnalyzeAsync(store, published, "source_image", CancellationToken.None));

            Assert.AreEqual("cas_image_probe_output_limit_exceeded", exception.Code);
            Assert.DoesNotContain(published.Sha256, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ProductStartInfo_UsesFixedProbePathAndOnlyStdioArgument()
    {
        var startInfo = IsolatedImageCasProbeClient.CreateProductStartInfo();

        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "image-probe", "QiongTu.ImageProbe.exe")),
            startInfo.FileName);
        CollectionAssert.AreEqual(
            new[] { ImageProbeProtocol.StdioArgument },
            startInfo.ArgumentList.ToArray());
    }

    private static async Task<PublishedObject> PublishAsync(
        ContentAddressedObjectStore store,
        byte[] bytes)
    {
        var stage = await store.StageAsync(new MemoryStream(bytes, writable: false));
        return await store.PublishAsync(stage);
    }

    private static byte[] CreateClassicRgbTiff()
    {
        const int ifdOffset = 8;
        const int entryCount = 10;
        const int ifdLength = 2 + (entryCount * 12) + 4;
        var bitsOffset = ifdOffset + ifdLength;
        var pixelOffset = bitsOffset + 6;
        var bytes = new byte[pixelOffset + 6];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), ifdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(ifdOffset, 2), entryCount);
        var entry = ifdOffset + 2;
        WriteEntry(bytes, ref entry, 256, 4, 1, 2);
        WriteEntry(bytes, ref entry, 257, 4, 1, 1);
        WriteEntry(bytes, ref entry, 258, 3, 3, checked((uint)bitsOffset));
        WriteEntry(bytes, ref entry, 259, 3, 1, 1);
        WriteEntry(bytes, ref entry, 262, 3, 1, 2);
        WriteEntry(bytes, ref entry, 273, 4, 1, checked((uint)pixelOffset));
        WriteEntry(bytes, ref entry, 274, 3, 1, 1);
        WriteEntry(bytes, ref entry, 277, 3, 1, 3);
        WriteEntry(bytes, ref entry, 278, 4, 1, 1);
        WriteEntry(bytes, ref entry, 279, 4, 1, 6);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry, 4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bitsOffset, 2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bitsOffset + 2, 2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bitsOffset + 4, 2), 8);
        bytes[pixelOffset] = 255;
        bytes[pixelOffset + 1] = 0;
        bytes[pixelOffset + 2] = 0;
        bytes[pixelOffset + 3] = 0;
        bytes[pixelOffset + 4] = 255;
        bytes[pixelOffset + 5] = 0;
        return bytes;
    }

    private static void WriteEntry(
        byte[] bytes,
        ref int offset,
        ushort tag,
        ushort type,
        uint count,
        uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2, 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4, 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8, 4), value);
        offset += 12;
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
            if (File.Exists(Path.Combine(directory.FullName, "QiongTu.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root could not be located.");
    }

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), $"qiongtu-cas-probe-control-{Guid.NewGuid():N}");

    private static void AssertPrivacy(ImageProbeCasImageResult result)
    {
        Assert.IsFalse(result.Privacy.PathsIncluded);
        Assert.IsFalse(result.Privacy.ContentHashesIncluded);
        Assert.IsFalse(result.Privacy.ObjectKeysIncluded);
        Assert.IsFalse(result.Privacy.RawMetadataIncluded);
        Assert.IsFalse(result.Privacy.SerialNumbersIncluded);
        Assert.IsFalse(result.Privacy.CoordinatesIncluded);
    }
}
