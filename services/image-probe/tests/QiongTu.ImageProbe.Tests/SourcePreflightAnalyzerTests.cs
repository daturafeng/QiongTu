using System.Text;
using QiongTu.Contracts;

namespace QiongTu.ImageProbe.Tests;

[TestClass]
public sealed class SourcePreflightAnalyzerTests
{
    [TestMethod]
    public void Analyze_DjiExif_ReturnsDjiEvidenceWithoutPrivateValues()
    {
        var payload = SyntheticJpeg.WithExif("DJI", "FC6310R");

        var result = SourcePreflightAnalyzer.Analyze(Header(payload.Length), payload);

        Assert.AreEqual("completed", result.Status);
        Assert.AreEqual("supports_dji", result.EvidenceState);
        Assert.AreEqual("jpeg_hint", result.ContainerHint);
        CollectionAssert.Contains(result.EvidenceKinds.ToArray(), "dji_exif_manufacturer");
        AssertPrivacy(result);
        var serialized = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.IsFalse(serialized.Contains("FC6310R", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Analyze_OtherManufacturer_ReturnsOutOfScope()
    {
        var payload = SyntheticJpeg.WithExif("Other Camera Corp", "Generic-1");

        var result = SourcePreflightAnalyzer.Analyze(Header(payload.Length), payload);

        Assert.AreEqual("out_of_scope", result.EvidenceState);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "other_manufacturer");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_DjiXmpWithOtherManufacturer_ReturnsConflict()
    {
        var payload = SyntheticJpeg.WithSegments(
            SyntheticJpeg.ExifSegment("Other Camera Corp", "Generic-1"),
            SyntheticJpeg.XmpSegment());

        var result = SourcePreflightAnalyzer.Analyze(Header(payload.Length), payload);

        Assert.AreEqual("conflict", result.EvidenceState);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "manufacturer_xmp_conflict");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_DjiXmpWithoutExif_ReturnsDjiEvidence()
    {
        var payload = SyntheticJpeg.WithSegments(SyntheticJpeg.XmpSegment());

        var result = SourcePreflightAnalyzer.Analyze(Header(payload.Length), payload);

        Assert.AreEqual("supports_dji", result.EvidenceState);
        CollectionAssert.Contains(result.EvidenceKinds.ToArray(), "dji_xmp_namespace");
    }

    [TestMethod]
    public void Analyze_UnreadableExifWithDjiXmp_RemainsUnconfirmed()
    {
        var payload = SyntheticJpeg.WithSegments(
            SyntheticJpeg.MalformedExifSegment(),
            SyntheticJpeg.XmpSegment());

        var result = SourcePreflightAnalyzer.Analyze(Header(payload.Length), payload);

        Assert.AreEqual("unconfirmed", result.EvidenceState);
        CollectionAssert.Contains(result.EvidenceKinds.ToArray(), "dji_xmp_namespace");
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "metadata_unreadable_or_truncated");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_FilenameIsNotPartOfProtocolAndCannotPass()
    {
        var payload = new byte[] { 0xff, 0xd8, 0xff, 0xd9 };

        var result = SourcePreflightAnalyzer.Analyze(Header(payload.Length), payload);

        Assert.AreEqual("unconfirmed", result.EvidenceState);
        Assert.IsFalse(result.EvidenceKinds.Contains("dji_exif_manufacturer", StringComparer.Ordinal));
    }

    [TestMethod]
    public void Analyze_DjiMrk13FieldLayout_ReturnsDjiEvidence()
    {
        var payload = Encoding.UTF8.GetBytes(
            "1\t12345.123456\t[2200]\t1,N\t-2,E\t3,V\t30.12345678,Lat\t120.12345678,Lon\t100.123,Ellh\t0.001000,\t0.001000,\t0.002000\t50,Q\r\n");
        var header = Header(payload.Length, "positioning_aux_candidate", "mrk", associationItemCount: 1);

        var result = SourcePreflightAnalyzer.Analyze(header, payload);

        Assert.AreEqual("supports_dji", result.EvidenceState);
        CollectionAssert.Contains(result.EvidenceKinds.ToArray(), "dji_mrk_13_field_layout");
        CollectionAssert.Contains(result.EvidenceKinds.ToArray(), "dji_mrk_batch_coverage");
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_RinexSidecarRemainsManufacturerUnconfirmed()
    {
        var payload = Encoding.UTF8.GetBytes(
            "     3.04           OBSERVATION DATA    M                   RINEX VERSION / TYPE\r\n                                                            END OF HEADER\r\n");
        var header = Header(payload.Length, "positioning_aux_candidate", "obs");

        var result = SourcePreflightAnalyzer.Analyze(header, payload);

        Assert.AreEqual("unconfirmed", result.EvidenceState);
        CollectionAssert.Contains(result.EvidenceKinds.ToArray(), "rinex_header");
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "sidecar_not_manufacturer_specific");
    }

    [TestMethod]
    public void Analyze_RtcmSidecarRemainsManufacturerUnconfirmed()
    {
        var payload = new byte[] { 0xd3, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 };
        var header = Header(payload.Length, "positioning_aux_candidate", "rtk");

        var result = SourcePreflightAnalyzer.Analyze(header, payload);

        Assert.AreEqual("unconfirmed", result.EvidenceState);
        CollectionAssert.Contains(result.EvidenceKinds.ToArray(), "rtcm3_frame_header");
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "sidecar_not_manufacturer_specific");
    }

    [TestMethod]
    public void Analyze_ArbitraryDjiTextIsNotManufacturerEvidence()
    {
        var payload = Encoding.UTF8.GetBytes("DJI D-RTK generic text without the documented MRK layout\r\n");
        var header = Header(payload.Length, "positioning_aux_candidate", "mrk");

        var result = SourcePreflightAnalyzer.Analyze(header, payload);

        Assert.AreEqual("unconfirmed", result.EvidenceState);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "sidecar_header_unrecognized");
    }

    [TestMethod]
    public void Analyze_DjiMrkWithoutCompleteCoverageRemainsUnconfirmed()
    {
        var payload = Encoding.UTF8.GetBytes(
            "1\t12345.123456\t[2200]\t1,N\t-2,E\t3,V\t30.12345678,Lat\t120.12345678,Lon\t100.123,Ellh\t0.001000,\t0.001000,\t0.002000\t50,Q\r\n");
        var header = Header(payload.Length, "positioning_aux_candidate", "mrk", associationItemCount: 2);

        var result = SourcePreflightAnalyzer.Analyze(header, payload);

        Assert.AreEqual("unconfirmed", result.EvidenceState);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "sidecar_batch_coverage_mismatch");
    }

    [TestMethod]
    public void Analyze_DjiMrkWithDuplicateSequenceRemainsUnconfirmed()
    {
        var line = "1\t12345.123456\t[2200]\t1,N\t-2,E\t3,V\t30.12345678,Lat\t120.12345678,Lon\t100.123,Ellh\t0.001000,\t0.001000,\t0.002000\t50,Q\r\n";
        var payload = Encoding.UTF8.GetBytes(line + line);
        var header = Header(payload.Length, "positioning_aux_candidate", "mrk", associationItemCount: 2);

        var result = SourcePreflightAnalyzer.Analyze(header, payload);

        Assert.AreEqual("unconfirmed", result.EvidenceState);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "sidecar_batch_coverage_mismatch");
    }

    [TestMethod]
    public void Analyze_MpoHeaderReportsOnlyHint()
    {
        var payload = SyntheticJpeg.WithSegments(SyntheticJpeg.App2MpfSegment());

        var result = SourcePreflightAnalyzer.Analyze(Header(payload.Length), payload);

        Assert.AreEqual("mpo_hint", result.ContainerHint);
        Assert.AreEqual("unconfirmed", result.EvidenceState);
    }

    private static ImageProbeRequestHeader Header(
        int length,
        string candidateKind = "image_candidate",
        string? formatHint = null,
        int? associationItemCount = null) =>
        new(
            ImageProbeProtocol.SourcePreflightV1,
            ImageProbeProtocol.SourcePreflightProfile,
            candidateKind,
            formatHint,
            associationItemCount,
            length);

    private static void AssertPrivacy(ImageProbeSourcePreflightResult result)
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

internal static class SyntheticJpeg
{
    public static byte[] WithExif(string make, string model) =>
        WithSegments(ExifSegment(make, model));

    public static byte[] WithSegments(params byte[][] segments)
    {
        using var stream = new MemoryStream();
        stream.Write([0xff, 0xd8]);
        foreach (var segment in segments)
        {
            stream.Write(segment);
        }

        stream.Write([0xff, 0xd9]);
        return stream.ToArray();
    }

    public static byte[] ExifSegment(string make, string model)
    {
        var makeBytes = NullTerminatedAscii(make);
        var modelBytes = NullTerminatedAscii(model);
        const int ifdOffset = 8;
        const int entryCount = 2;
        var dataOffset = ifdOffset + 2 + (entryCount * 12) + 4;
        using var tiff = new MemoryStream();
        tiff.Write("II"u8);
        WriteUInt16(tiff, 42);
        WriteUInt32(tiff, ifdOffset);
        WriteUInt16(tiff, entryCount);
        WriteAsciiEntry(tiff, 0x010f, makeBytes, dataOffset);
        WriteAsciiEntry(tiff, 0x0110, modelBytes, dataOffset + makeBytes.Length);
        WriteUInt32(tiff, 0);
        tiff.Write(makeBytes);
        tiff.Write(modelBytes);
        return AppSegment(0xe1, Combine("Exif\0\0"u8.ToArray(), tiff.ToArray()));
    }

    public static byte[] XmpSegment()
    {
        var identifier = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
        var xml = Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:drone-dji=\"http://www.dji.com/drone-dji/1.0/\" drone-dji:GimbalYawDegree=\"1\"/></rdf:RDF></x:xmpmeta>");
        return AppSegment(0xe1, Combine(identifier, xml));
    }

    public static byte[] MalformedExifSegment() =>
        AppSegment(0xe1, Combine(
            "Exif\0\0"u8.ToArray(),
            new byte[] { (byte)'I', (byte)'I', 42, 0, 0xff, 0xff, 0xff, 0x7f }));

    public static byte[] App2MpfSegment() =>
        AppSegment(0xe2, "MPF\0II*\0"u8.ToArray());

    private static byte[] AppSegment(byte marker, byte[] payload)
    {
        var segmentLength = checked(payload.Length + 2);
        return Combine(
            [0xff, marker, (byte)(segmentLength >> 8), (byte)segmentLength],
            payload);
    }

    private static byte[] NullTerminatedAscii(string value) =>
        Encoding.ASCII.GetBytes(value + "\0");

    private static void WriteAsciiEntry(Stream stream, ushort tag, byte[] value, int dataOffset)
    {
        WriteUInt16(stream, tag);
        WriteUInt16(stream, 2);
        WriteUInt32(stream, value.Length);
        if (value.Length <= 4)
        {
            stream.Write(value);
            for (var index = value.Length; index < 4; index++)
            {
                stream.WriteByte(0);
            }
        }
        else
        {
            WriteUInt32(stream, dataOffset);
        }
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
    }

    private static void WriteUInt32(Stream stream, int value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }

    private static byte[] Combine(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
