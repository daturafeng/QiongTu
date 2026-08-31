using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using ImageMagick;
using QiongTu.Contracts;

namespace QiongTu.ImageProbe.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CasImageAnalyzerTests
{
    private static readonly byte[] ValidJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAADAAQDAREAAhEBAxEB/8QAFAABAAAAAAAAAAAAAAAAAAAACP/EABQQAQAAAAAAAAAAAAAAAAAAAAD/xAAVAQEBAAAAAAAAAAAAAAAAAAAHCf/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/ADoDFU3/2Q==");
    private static readonly byte[] AuxiliaryJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAABAAIDAREAAhEBAxEB/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/xAAVAQEBAAAAAAAAAAAAAAAAAAAGCf/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/AD3VTB3/2Q==");
    private static readonly byte[] ValidMpo = CreateMpo(ValidJpeg, AuxiliaryJpeg);
    private static readonly byte[] ValidTiff = Convert.FromBase64String(
        "SUkqAIAAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAAAICAAAAPAAABAwABAAAABQAAAAEBAwABAAAABAAAAAIBAwADAAAAOgEAAAMBAwABAAAAAQAAAAYBAwABAAAAAgAAAAoBAwABAAAAAQAAABEBBAABAAAACAAAABIBAwABAAAAAQAAABUBAwABAAAAAwAAABYBAwABAAAABAAAABcBBAABAAAAeAAAABwBAwABAAAAAQAAACkBAwACAAAAAAABAD4BBQACAAAAcAEAAD8BBQAGAAAAQAEAAAAAAAAQABAAEACF61EAAACAAMP1qAAAAAACzcxMAAAAAAHNzEwAAACAAM3MTAAAAAACj8L1AAAAABA3GqAAAAAAAiuHCgAAACAA");
    private static readonly byte[] ValidMultiPageTiff = CreateClassicRgbTiff(pageCount: 2, bitsPerSample: 16);

    [TestMethod]
    public void Analyze_ValidJpeg_CrossChecksStructureAndNativeDecoder()
    {
        var result = AnalyzeFormalObject(ValidJpeg);

        Assert.AreEqual("completed", result.Status, string.Join(',', result.ReasonCodes));
        Assert.AreEqual("jpeg", result.Container);
        Assert.AreEqual("validated", result.StructureState);
        Assert.AreEqual("decoded", result.DecodeState);
        Assert.HasCount(1, result.Frames);
        Assert.AreEqual(4, result.Frames[0].Width);
        Assert.AreEqual(3, result.Frames[0].Height);
        Assert.AreEqual(8, result.Frames[0].BitsPerChannel);
        Assert.AreEqual(1, result.Frames[0].Orientation);
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_JpegExifOrientation_IsBoundedAndPreserved()
    {
        var result = AnalyzeFormalObject(AddJpegExifOrientation(ValidJpeg, 6));

        Assert.AreEqual("completed", result.Status, string.Join(',', result.ReasonCodes));
        Assert.AreEqual(6, result.Frames[0].Orientation);
        Assert.AreEqual(4, result.Frames[0].Width);
        Assert.AreEqual(3, result.Frames[0].Height);
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_ConflictingJpegExifOrientation_IsBlocked()
    {
        var conflicting = AddJpegExifOrientation(AddJpegExifOrientation(ValidJpeg, 6), 3);

        var result = AnalyzeFormalObject(conflicting);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "jpeg_orientation_conflict");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_ValidMpo_ValidatesEveryDeclaredJpegRange()
    {
        var result = AnalyzeFormalObject(ValidMpo);

        Assert.AreEqual("completed", result.Status, string.Join(',', result.ReasonCodes));
        Assert.AreEqual("mpo", result.Container);
        Assert.HasCount(2, result.Frames);
        Assert.AreEqual("mp_primary_image", result.Frames[0].FrameKind);
        Assert.AreEqual("mp_auxiliary_image", result.Frames[1].FrameKind);
        Assert.AreEqual(0, result.Frames[0].ByteOffset);
        Assert.AreEqual(ValidMpo.Length - AuxiliaryJpeg.Length, result.Frames[1].ByteOffset);
        Assert.AreEqual(4, result.Frames[0].Width);
        Assert.AreEqual(2, result.Frames[1].Width);
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_StandardMpfGapBetweenImages_IsNotDecodedAsPayload()
    {
        var withGap = AddMpfInterImageGap(ValidMpo, 32);

        var result = AnalyzeFormalObject(withGap);

        Assert.AreEqual("completed", result.Status, string.Join(',', result.ReasonCodes));
        Assert.AreEqual("mpo", result.Container);
        Assert.HasCount(2, result.Frames);
        Assert.AreEqual(32, result.Frames[1].ByteOffset - result.Frames[0].ByteLength);
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_ValidClassicTiff_CrossChecksIfdAndDecodedPage()
    {
        var result = AnalyzeFormalObject(ValidTiff);

        Assert.AreEqual("completed", result.Status, string.Join(',', result.ReasonCodes));
        Assert.AreEqual("tiff", result.Container);
        Assert.HasCount(1, result.Frames);
        Assert.AreEqual(5, result.Frames[0].Width);
        Assert.AreEqual(4, result.Frames[0].Height);
        Assert.AreEqual(16, result.Frames[0].BitsPerChannel);
        Assert.AreEqual("decoded", result.Frames[0].DecodeState);
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_ClassicMultiPage16BitTiff_PreservesEveryPageAndDepth()
    {
        var result = AnalyzeFormalObject(ValidMultiPageTiff);

        Assert.AreEqual("completed", result.Status, string.Join(',', result.ReasonCodes));
        Assert.AreEqual("tiff", result.Container);
        Assert.HasCount(2, result.Frames);
        Assert.IsTrue(result.Frames.All(frame => frame.BitsPerChannel == 16));
        Assert.IsTrue(result.Frames.All(frame => frame.DecodeState == "decoded"));
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_TiffUnsupportedPhotometric_ReturnsStableBlockedReason()
    {
        var malformed = CreateClassicRgbTiff(pageCount: 1, bitsPerSample: 8);
        WriteTiffIfdEntryValue(malformed, pageIndex: 0, entryIndex: 4, value: 4);

        var result = AnalyzeFormalObject(malformed);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "tiff_photometric_not_supported");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_TiffUnsupportedCompression_ReturnsStableBlockedReason()
    {
        var malformed = CreateClassicRgbTiff(pageCount: 1, bitsPerSample: 8);
        WriteTiffIfdEntryValue(malformed, pageIndex: 0, entryIndex: 3, value: 99);

        var result = AnalyzeFormalObject(malformed);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "tiff_compression_not_supported");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_JpegDeclaredPixelBomb_ReturnsStableBlockedReason()
    {
        var malformed = ValidJpeg.ToArray();
        var sof = FindMarker(malformed, 0xc0);
        BinaryPrimitives.WriteUInt16BigEndian(malformed.AsSpan(sof + 5, 2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16BigEndian(malformed.AsSpan(sof + 7, 2), ushort.MaxValue);

        var result = AnalyzeFormalObject(malformed);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "frame_pixel_limit_exceeded");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_TiffDeclaredPixelBomb_ReturnsStableBlockedReason()
    {
        var malformed = CreateClassicRgbTiff(pageCount: 1, bitsPerSample: 8);
        WriteTiffIfdEntryValue(malformed, pageIndex: 0, entryIndex: 0, value: 1_000_001);
        WriteTiffIfdEntryValue(malformed, pageIndex: 0, entryIndex: 1, value: 1_000);

        var result = AnalyzeFormalObject(malformed);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "frame_pixel_limit_exceeded");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_TiffIfdCycle_ReturnsStableBlockedReason()
    {
        var malformed = CreateClassicRgbTiff(pageCount: 1, bitsPerSample: 8);
        const int nextIfdOffset = 8 + 2 + (10 * 12);
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(nextIfdOffset, 4), 8);

        var result = AnalyzeFormalObject(malformed);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "tiff_ifd_cycle");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_TruncatedJpeg_ReturnsStableStructureFailure()
    {
        var result = AnalyzeFormalObject(ValidJpeg[..^1]);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "jpeg_scan_truncated");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_MpfRangeOutsideObject_ReturnsStableBlockedReason()
    {
        var malformed = ValidMpo.ToArray();
        var secondOffsetField = FindMpfEntryTable(malformed) + 16 + 8;
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(secondOffsetField, 4), uint.MaxValue);

        var result = AnalyzeFormalObject(malformed);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "mpf_range_out_of_bounds");
        Assert.HasCount(0, result.Frames);
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_MpfRangesOverlap_ReturnsStableBlockedReason()
    {
        var malformed = ValidMpo.ToArray();
        var secondOffsetField = FindMpfEntryTable(malformed) + 16 + 8;
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(secondOffsetField, 4), 1);

        var result = AnalyzeFormalObject(malformed);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "mpf_ranges_overlap");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_MpfTruncatedSecondFrame_ReturnsStableBlockedReason()
    {
        var malformed = CreateMpo(ValidJpeg, AuxiliaryJpeg[..^1]);

        var result = AnalyzeFormalObject(malformed);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "jpeg_scan_truncated");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_MpfEntryTableOutOfBounds_ReturnsStableBlockedReason()
    {
        var malformed = ValidMpo.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            malformed.AsSpan(FindMpfEntriesOffsetField(malformed), 4),
            uint.MaxValue);

        var result = AnalyzeFormalObject(malformed);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "mpf_entries_out_of_bounds");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_MpfTrailingPayload_ReturnsStableBlockedReason()
    {
        var malformed = ValidMpo.Concat("<svg/>"u8.ToArray()).ToArray();

        var result = AnalyzeFormalObject(malformed);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "mpf_unreferenced_trailing_data");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_MpfNonJpegImageFormat_ReturnsStableBlockedReason()
    {
        var malformed = ValidMpo.ToArray();
        var secondAttribute = FindMpfEntryTable(malformed) + 16;
        BinaryPrimitives.WriteUInt32LittleEndian(
            malformed.AsSpan(secondAttribute, 4),
            0x0700_0000 | 0x020003);

        var result = AnalyzeFormalObject(malformed);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "mpf_image_format_not_supported");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_MpfUnknownImageType_ReturnsStableBlockedReason()
    {
        var malformed = ValidMpo.ToArray();
        var secondAttribute = FindMpfEntryTable(malformed) + 16;
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(secondAttribute, 4), 0x0000ff);

        var result = AnalyzeFormalObject(malformed);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "mpf_type_not_supported");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_MpfVersionOtherThan0100_ReturnsStableBlockedReason()
    {
        var malformed = ValidMpo.ToArray();
        "9999"u8.CopyTo(malformed.AsSpan(FindMpfVersionValue(malformed), 4));

        var result = AnalyzeFormalObject(malformed);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "mpf_version_invalid");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_JpegDeferredHeightMarker_IsExplicitlyUnsupported()
    {
        var malformed = ValidJpeg.ToArray();
        var sof = FindMarker(malformed, 0xc0);
        malformed[sof + 5] = 0;
        malformed[sof + 6] = 0;

        var result = AnalyzeFormalObject(malformed);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "jpeg_dnl_not_supported");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_MpoAggregateMetadataOverBudget_ReturnsStableBlockedReason()
    {
        var metadataHeavyPrimary = AddJpegApplicationSegments(ValidJpeg, 129);
        var metadataHeavyAuxiliary = AddJpegApplicationSegments(AuxiliaryJpeg, 129);

        var result = AnalyzeFormalObject(CreateMpo(metadataHeavyPrimary, metadataHeavyAuxiliary));

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "jpeg_metadata_limit_exceeded");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_BigTiffHeader_IsRecognizedAndExplicitlyBlocked()
    {
        var bigTiffHeader = new byte[]
        {
            (byte)'I', (byte)'I', 43, 0, 8, 0, 0, 0,
            16, 0, 0, 0, 0, 0, 0, 0
        };

        var result = AnalyzeFormalObject(bigTiffHeader);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "bigtiff_not_supported");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_UnsupportedContent_DoesNotInvokeAnAutomaticCoder()
    {
        var disguisedSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"1\" height=\"1\"/></svg>"u8.ToArray();

        var result = AnalyzeFormalObject(disguisedSvg);

        Assert.AreEqual("blocked", result.Status);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "unsupported_image_container");
        Assert.AreEqual("not_decoded", result.DecodeState);
        AssertPrivacy(result);
    }

    [TestMethod]
    public void NativePolicy_DeniesNonAllowlistedCodersAndDelegates()
    {
        using var runtime = CasImageAnalyzer.CreateNativeRuntimeForTests();
        var forbiddenInputs = new[]
        {
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl1sAAAAASUVORK5CYII="),
            "%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\n%%EOF"u8.ToArray(),
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"1\" height=\"1\"/></svg>"u8.ToArray()
        };

        foreach (var input in forbiddenInputs)
        {
            _ = Assert.Throws<MagickPolicyErrorException>(() =>
            {
                using var image = new MagickImage(input);
            });
        }
    }

    [TestMethod]
    [DataRow("14.16.0", true)]
    [DataRow("14.16.0+build", true)]
    [DataRow("14.16.0.1", false)]
    [DataRow("14.16.0-preview", false)]
    [DataRow("114.16.0", false)]
    [DataRow(null, false)]
    public void NativeDecoderVersion_RequiresExactPinnedSemanticVersion(string? version, bool expected)
    {
        Assert.AreEqual(expected, CasImageAnalyzer.IsRequiredNativeDecoderVersion(version));
    }

    [TestMethod]
    public void ValidateCasHeader_RejectsNonCanonicalObjectKey()
    {
        var hash = new string('a', 64);
        var header = new ImageProbeCasImageRequestHeader(
            ImageProbeProtocol.CasImageV1,
            ImageProbeProtocol.CasImageProfile,
            "source_image",
            Path.GetFullPath(Path.GetTempPath()),
            "../staging/payload",
            hash,
            1);

        var exception = Assert.Throws<ImageProbeProtocolException>(() =>
            StdioEnvelope.ValidateCasHeader(header));

        Assert.AreEqual("object_key_invalid", exception.Code);
    }

    [TestMethod]
    public void ValidateCasHeader_RejectsNetworkFormalObjectRoot()
    {
        var hash = new string('a', 64);
        var header = new ImageProbeCasImageRequestHeader(
            ImageProbeProtocol.CasImageV1,
            ImageProbeProtocol.CasImageProfile,
            "source_image",
            "\\\\server\\share\\published",
            $"sha256/aa/{hash}",
            hash,
            1);

        var exception = Assert.Throws<ImageProbeProtocolException>(() =>
            StdioEnvelope.ValidateCasHeader(header));

        Assert.AreEqual("formal_object_root_invalid", exception.Code);
    }

    [TestMethod]
    public void ResultSerialization_DoesNotContainFormalPathHashOrObjectKey()
    {
        var root = CreateFormalObject(ValidJpeg, out var header);
        try
        {
            var result = CasImageAnalyzer.Analyze(header);
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.DoesNotContain(root, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(header.ExpectedSha256, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(header.ObjectKey, json, StringComparison.OrdinalIgnoreCase);
            AssertPrivacy(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ImageProbeCasImageResult AnalyzeFormalObject(byte[] bytes)
    {
        var root = CreateFormalObject(bytes, out var header);
        try
        {
            return CasImageAnalyzer.Analyze(header);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateFormalObject(byte[] bytes, out ImageProbeCasImageRequestHeader header)
    {
        var root = Path.Combine(Path.GetTempPath(), $"qiongtu-cas-probe-{Guid.NewGuid():N}");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var objectKey = $"sha256/{hash[..2]}/{hash}";
        var path = Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        header = new ImageProbeCasImageRequestHeader(
            ImageProbeProtocol.CasImageV1,
            ImageProbeProtocol.CasImageProfile,
            "source_image",
            root,
            objectKey,
            hash,
            bytes.LongLength);
        StdioEnvelope.ValidateCasHeader(header);
        return root;
    }

    private static byte[] CreateMpo(byte[] primary, byte[] auxiliary)
    {
        const int tiffHeaderLength = 8;
        const int ifdLength = 2 + (3 * 12) + 4;
        const int mpEntryOffset = tiffHeaderLength + ifdLength;
        const int mpEntryBytes = 2 * 16;
        var tiff = new byte[mpEntryOffset + mpEntryBytes];
        tiff[0] = (byte)'I';
        tiff[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4, 4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8, 2), 3);
        WriteIfdEntry(tiff, 10, 0xb000, 7, 4, 0x30303130);
        WriteIfdEntry(tiff, 22, 0xb001, 4, 1, 2);
        WriteIfdEntry(tiff, 34, 0xb002, 7, mpEntryBytes, mpEntryOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(46, 4), 0);

        var app2PayloadLength = 4 + tiff.Length;
        var app2LengthField = checked((ushort)(app2PayloadLength + 2));
        var app2 = new byte[2 + 2 + app2PayloadLength];
        app2[0] = 0xff;
        app2[1] = 0xe2;
        BinaryPrimitives.WriteUInt16BigEndian(app2.AsSpan(2, 2), app2LengthField);
        "MPF\0"u8.CopyTo(app2.AsSpan(4, 4));
        tiff.CopyTo(app2, 8);

        var firstLength = checked(primary.Length + app2.Length);
        var mpBase = 2 + 2 + 2 + 4;
        WriteMpEntry(app2, 8 + mpEntryOffset, firstLength, 0);
        WriteMpEntry(app2, 8 + mpEntryOffset + 16, auxiliary.Length, firstLength - mpBase);

        var result = new byte[firstLength + auxiliary.Length];
        primary.AsSpan(0, 2).CopyTo(result);
        app2.CopyTo(result, 2);
        primary.AsSpan(2).CopyTo(result.AsSpan(2 + app2.Length));
        auxiliary.CopyTo(result, firstLength);
        return result;
    }

    private static byte[] CreateClassicRgbTiff(int pageCount, ushort bitsPerSample)
    {
        const int firstIfdOffset = 8;
        const int entryCount = 10;
        const int ifdLength = 2 + (entryCount * 12) + 4;
        const int width = 2;
        const int height = 1;
        var bytesPerSample = bitsPerSample / 8;
        var pixelByteCount = checked(width * height * 3 * bytesPerSample);
        var dataStart = checked(firstIfdOffset + (pageCount * ifdLength));
        var pageDataLength = checked(6 + pixelByteCount);
        var bytes = new byte[checked(dataStart + (pageCount * pageDataLength))];
        bytes[0] = (byte)'I';
        bytes[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), firstIfdOffset);

        for (var page = 0; page < pageCount; page++)
        {
            var ifdOffset = firstIfdOffset + (page * ifdLength);
            var bitsOffset = dataStart + (page * pageDataLength);
            var pixelOffset = bitsOffset + 6;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(ifdOffset, 2), entryCount);
            var entry = ifdOffset + 2;
            WriteIfdEntry(bytes, entry, 256, 4, 1, width);
            entry += 12;
            WriteIfdEntry(bytes, entry, 257, 4, 1, height);
            entry += 12;
            WriteIfdEntry(bytes, entry, 258, 3, 3, checked((uint)bitsOffset));
            entry += 12;
            WriteIfdEntry(bytes, entry, 259, 3, 1, 1);
            entry += 12;
            WriteIfdEntry(bytes, entry, 262, 3, 1, 2);
            entry += 12;
            WriteIfdEntry(bytes, entry, 273, 4, 1, checked((uint)pixelOffset));
            entry += 12;
            WriteIfdEntry(bytes, entry, 274, 3, 1, 1);
            entry += 12;
            WriteIfdEntry(bytes, entry, 277, 3, 1, 3);
            entry += 12;
            WriteIfdEntry(bytes, entry, 278, 4, 1, height);
            entry += 12;
            WriteIfdEntry(bytes, entry, 279, 4, 1, checked((uint)pixelByteCount));
            entry += 12;
            var nextIfd = page + 1 < pageCount ? ifdOffset + ifdLength : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry, 4), checked((uint)nextIfd));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bitsOffset, 2), bitsPerSample);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bitsOffset + 2, 2), bitsPerSample);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bitsOffset + 4, 2), bitsPerSample);
            for (var index = 0; index < pixelByteCount; index++)
            {
                bytes[pixelOffset + index] = (byte)((page + 1) * 31 + index);
            }
        }

        return bytes;
    }

    private static void WriteIfdEntry(
        byte[] bytes,
        int offset,
        ushort tag,
        ushort type,
        uint count,
        uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2, 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4, 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8, 4), value);
    }

    private static void WriteMpEntry(byte[] app2, int offset, int size, int dataOffset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(app2.AsSpan(offset, 4), 0x020003);
        BinaryPrimitives.WriteUInt32LittleEndian(app2.AsSpan(offset + 4, 4), checked((uint)size));
        BinaryPrimitives.WriteUInt32LittleEndian(app2.AsSpan(offset + 8, 4), checked((uint)dataOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(app2.AsSpan(offset + 12, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(app2.AsSpan(offset + 14, 2), 0);
    }

    private static int FindMpfEntryTable(byte[] bytes)
    {
        var mpf = bytes.AsSpan().IndexOf("MPF\0"u8);
        Assert.IsGreaterThanOrEqualTo(0, mpf);
        var tiffBase = mpf + 4;
        var ifdOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(tiffBase + 4, 4)));
        var thirdEntry = tiffBase + ifdOffset + 2 + (2 * 12);
        var entryOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(thirdEntry + 8, 4)));
        return tiffBase + entryOffset;
    }

    private static int FindMpfVersionValue(byte[] bytes)
    {
        var mpf = bytes.AsSpan().IndexOf("MPF\0"u8);
        Assert.IsGreaterThanOrEqualTo(0, mpf);
        var tiffBase = mpf + 4;
        var ifdOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(tiffBase + 4, 4)));
        return checked(tiffBase + ifdOffset + 2 + 8);
    }

    private static int FindMpfEntriesOffsetField(byte[] bytes)
    {
        var mpf = bytes.AsSpan().IndexOf("MPF\0"u8);
        Assert.IsGreaterThanOrEqualTo(0, mpf);
        var tiffBase = mpf + 4;
        var ifdOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(tiffBase + 4, 4)));
        return checked(tiffBase + ifdOffset + 2 + (2 * 12) + 8);
    }

    private static int FindMarker(byte[] bytes, byte marker)
    {
        for (var index = 0; index < bytes.Length - 1; index++)
        {
            if (bytes[index] == 0xff && bytes[index + 1] == marker)
            {
                return index;
            }
        }

        Assert.Fail($"JPEG marker 0x{marker:x2} was not found.");
        return -1;
    }

    private static byte[] AddJpegApplicationSegments(byte[] jpeg, int count)
    {
        const int payloadLength = ushort.MaxValue - 2;
        const int segmentLength = 2 + 2 + payloadLength;
        var result = new byte[checked(jpeg.Length + (count * segmentLength))];
        jpeg.AsSpan(0, 2).CopyTo(result);
        var writeOffset = 2;
        for (var index = 0; index < count; index++)
        {
            result[writeOffset] = 0xff;
            result[writeOffset + 1] = 0xe1;
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(writeOffset + 2, 2), ushort.MaxValue);
            result.AsSpan(writeOffset + 4, payloadLength).Fill((byte)(index + 1));
            writeOffset += segmentLength;
        }

        jpeg.AsSpan(2).CopyTo(result.AsSpan(writeOffset));
        return result;
    }

    private static void WriteTiffIfdEntryValue(byte[] bytes, int pageIndex, int entryIndex, uint value)
    {
        const int firstIfdOffset = 8;
        const int entryCount = 10;
        const int ifdLength = 2 + (entryCount * 12) + 4;
        var valueOffset = firstIfdOffset + (pageIndex * ifdLength) + 2 + (entryIndex * 12) + 8;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(valueOffset, 4), value);
    }

    private static byte[] AddJpegExifOrientation(byte[] jpeg, ushort orientation)
    {
        const int tiffLength = 8 + 2 + 12 + 4;
        var payload = new byte[6 + tiffLength];
        "Exif\0\0"u8.CopyTo(payload);
        var tiff = payload.AsSpan(6);
        tiff[0] = (byte)'I';
        tiff[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[2..4], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff[4..8], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[8..10], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[10..12], 274);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[12..14], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff[14..18], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[18..20], orientation);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff[22..26], 0);

        var segment = new byte[4 + payload.Length];
        segment[0] = 0xff;
        segment[1] = 0xe1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2, 2), checked((ushort)(payload.Length + 2)));
        payload.CopyTo(segment, 4);
        var result = new byte[jpeg.Length + segment.Length];
        jpeg.AsSpan(0, 2).CopyTo(result);
        segment.CopyTo(result, 2);
        jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + segment.Length));
        return result;
    }

    private static byte[] AddMpfInterImageGap(byte[] mpo, int gapLength)
    {
        var secondImageOffset = mpo.Length - AuxiliaryJpeg.Length;
        var result = new byte[checked(mpo.Length + gapLength)];
        mpo.AsSpan(0, secondImageOffset).CopyTo(result);
        result.AsSpan(secondImageOffset, gapLength).Fill(0xa5);
        mpo.AsSpan(secondImageOffset).CopyTo(result.AsSpan(secondImageOffset + gapLength));

        var secondOffsetField = FindMpfEntryTable(result) + 16 + 8;
        var relativeOffset = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(secondOffsetField, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(secondOffsetField, 4),
            checked(relativeOffset + (uint)gapLength));
        return result;
    }

    private static void AssertPrivacy(ImageProbeCasImageResult result)
    {
        Assert.IsFalse(result.Privacy.PathsIncluded);
        Assert.IsFalse(result.Privacy.LocatorsIncluded);
        Assert.IsFalse(result.Privacy.ContentHashesIncluded);
        Assert.IsFalse(result.Privacy.ObjectKeysIncluded);
        Assert.IsFalse(result.Privacy.RawMetadataIncluded);
        Assert.IsFalse(result.Privacy.SerialNumbersIncluded);
        Assert.IsFalse(result.Privacy.CoordinatesIncluded);
        Assert.IsFalse(result.Privacy.OwnerSampleStatisticsIncluded);
    }
}
