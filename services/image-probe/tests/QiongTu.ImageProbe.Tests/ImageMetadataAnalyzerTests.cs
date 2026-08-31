using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MetadataExtractor.Formats.Exif.Makernotes;
using QiongTu.Contracts;

namespace QiongTu.ImageProbe.Tests;

[TestClass]
public sealed class ImageMetadataAnalyzerTests
{
    [TestMethod]
    public void Analyze_LegalExifGpsAndDjiXmp_ReturnsAllowlistedTypedFields()
    {
        var payload = SyntheticMetadataJpeg.Create(
            SyntheticMetadataJpeg.Exif(
                make: "DJI",
                model: "FC6310R",
                lensModel: "DJI 8.8mm",
                focalLengthMm: 8.8,
                localTime: "2026:08:30 10:11:12",
                latitude: 30.123456,
                longitude: 120.123456,
                altitude: 88.5,
                gpsUtc: "2026:08:30 02:11:12"),
            SyntheticMetadataJpeg.Xmp(
                ("GpsLatitude", "30.123456"),
                ("GpsLongitude", "120.123456"),
                ("AbsoluteAltitude", "88.55"),
                ("RelativeAltitude", "50.25"),
                ("GimbalYawDegree", "1.25"),
                ("FlightPitchDegree", "-2.5"),
                ("RtkFlag", "fixed"),
                ("RtkStdLon", "0.012"),
                ("RtkStdLat", "0.013"),
                ("RtkStdHgt", "0.021")));

        var result = AnalyzeFormalObject(payload);

        Assert.AreEqual("completed", result.Status, string.Join(',', result.ReasonCodes));
        Assert.AreEqual(ImageProbeProtocol.DjiMetadataMapV1, result.Parser.FieldMappingVersion);
        Assert.AreEqual(ImageProbeProtocol.MetadataConflictV1, result.Parser.ConflictPolicyVersion);
        Assert.AreEqual("2.9.3", StripBuildMetadata(result.Parser.MetadataExtractorVersion));
        AssertField(result, "camera.manufacturer", "exif", "present", textValue: "DJI");
        AssertField(result, "camera.model", "exif", "present", textValue: "FC6310R");
        AssertField(result, "camera.lens_model", "exif", "present", textValue: "DJI 8.8mm");
        AssertField(result, "camera.focal_length_mm", "exif", "present", numericValue: 8.8, unit: "mm");
        AssertField(result, "position.latitude_deg", "gps_exif", "present", numericValue: 30.123456, unit: "deg");
        AssertField(result, "position.relative_altitude_m", "dji_xmp", "present", numericValue: 50.25, unit: "m");
        AssertField(result, "pose.gimbal_yaw_deg", "dji_xmp", "present", numericValue: 1.25, unit: "deg");
        AssertField(result, "position.rtk_flag", "dji_xmp", "present", textValue: "fixed");
        Assert.IsTrue(result.Fields.All(field => field.SourceKind is "exif" or "gps_exif" or "dji_xmp" or "derived"));
        AssertPrivacy(result, coordinatesExpected: true);
    }

    [TestMethod]
    public void Analyze_ExifLocalTimeWithoutTrustedOffset_MarksUtcNotAssessable()
    {
        var payload = SyntheticMetadataJpeg.Create(
            SyntheticMetadataJpeg.Exif(
                make: "DJI",
                model: "FC6310R",
                lensModel: null,
                focalLengthMm: null,
                localTime: "2026:08:30 10:11:12",
                latitude: null,
                longitude: null,
                altitude: null,
                gpsUtc: null));

        var result = AnalyzeFormalObject(payload);

        Assert.AreEqual("completed", result.Status);
        AssertField(result, "capture.time_local", "exif", "present", textValue: "2026-08-30T10:11:12");
        AssertField(result, "capture.time_utc", "derived", "not_assessable");
        AssertPrivacy(result, coordinatesExpected: false);
    }

    [TestMethod]
    public void Analyze_ExifLocalTimeWithOffsetTimeOriginal_EmitsUtc()
    {
        var payload = SyntheticMetadataJpeg.Create(
            SyntheticMetadataJpeg.Exif(
                make: "DJI",
                model: "FC6310R",
                lensModel: null,
                focalLengthMm: null,
                localTime: "2026:08:30 10:11:12",
                latitude: null,
                longitude: null,
                altitude: null,
                gpsUtc: null,
                offsetTimeOriginal: "+08:00"));

        var result = AnalyzeFormalObject(payload);

        Assert.AreEqual("completed", result.Status);
        AssertField(result, "capture.time_utc", "exif", "present", textValue: "2026-08-30T02:11:12Z");
    }

    [TestMethod]
    public void Analyze_ExifAndDjiXmpGpsConflict_RetainsBothSourcesAsConflict()
    {
        var payload = SyntheticMetadataJpeg.Create(
            SyntheticMetadataJpeg.Exif(
                make: "DJI",
                model: "FC6310R",
                lensModel: null,
                focalLengthMm: null,
                localTime: null,
                latitude: 30.000000,
                longitude: 120.000000,
                altitude: 10.0,
                gpsUtc: null),
            SyntheticMetadataJpeg.Xmp(
                ("GpsLatitude", "30.010000"),
                ("GpsLongitude", "120.010000"),
                ("AbsoluteAltitude", "11.00")));

        var result = AnalyzeFormalObject(payload);

        Assert.AreEqual("completed", result.Status);
        AssertField(result, "position.latitude_deg", "gps_exif", "conflict", numericValue: 30.0, unit: "deg");
        AssertField(result, "position.latitude_deg", "dji_xmp", "conflict", numericValue: 30.010000, unit: "deg");
        AssertField(result, "position.absolute_altitude_m", "gps_exif", "conflict", numericValue: 10.0, unit: "m");
        AssertField(result, "position.absolute_altitude_m", "dji_xmp", "conflict", numericValue: 11.00, unit: "m");
    }

    [TestMethod]
    public void ExtractFields_DjiMakernoteUsesExifSourceKindAndFixedDetails()
    {
        var dji = new DjiMakernoteDirectory();
        dji.Set(DjiMakernoteDirectory.TagCameraYaw, 7.5f);
        dji.Set(DjiMakernoteDirectory.TagAircraftRoll, -1.25f);

        var fields = ImageMetadataAnalyzer.ExtractFieldsForTests([dji]);

        AssertField(fields, "pose.gimbal_yaw_deg", "exif", "present", numericValue: 7.5, unit: "deg");
        AssertField(fields, "pose.flight_roll_deg", "exif", "present", numericValue: -1.25, unit: "deg");
        Assert.IsTrue(fields.Where(field => field.FieldState == "missing").All(field => field.SourceKind == "derived"));
    }

    [TestMethod]
    public void Analyze_UnknownAndSerialXmpProperties_AreNotReturned()
    {
        var payload = SyntheticMetadataJpeg.Create(
            SyntheticMetadataJpeg.Xmp(
                ("GimbalYawDegree", "2.5"),
                ("SerialNumber", "SN-PRIVATE-001"),
                ("CameraSerialNumber", "CAMERA-SECRET"),
                ("UnknownPrivateProperty", "owner-sample-private-value")));

        var result = AnalyzeFormalObject(payload);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.AreEqual("completed", result.Status);
        AssertField(result, "pose.gimbal_yaw_deg", "dji_xmp", "present", numericValue: 2.5, unit: "deg");
        Assert.DoesNotContain("SN-PRIVATE-001", json, StringComparison.Ordinal);
        Assert.DoesNotContain("CAMERA-SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("owner-sample-private-value", json, StringComparison.Ordinal);
        Assert.IsFalse(result.Fields.Any(field => field.SourceDetail.Contains("Serial", StringComparison.OrdinalIgnoreCase)));
        AssertPrivacy(result, coordinatesExpected: false);
    }

    [TestMethod]
    public void Analyze_AbnormalValues_DoNotEchoRejectedValues()
    {
        var longFlag = new string('x', ImageProbeProtocol.MaximumMetadataTextBytes + 1);
        var payload = SyntheticMetadataJpeg.Create(
            SyntheticMetadataJpeg.Xmp(
                ("GpsLatitude", "Infinity"),
                ("AbsoluteAltitude", "9999999"),
                ("RtkStdLon", "-1"),
                ("RtkFlag", longFlag)));

        var result = AnalyzeFormalObject(payload);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.AreEqual("completed", result.Status);
        AssertField(result, "position.latitude_deg", "dji_xmp", "abnormal", unit: "deg");
        AssertField(result, "position.absolute_altitude_m", "dji_xmp", "abnormal", unit: "m");
        AssertField(result, "position.std_lon_m", "dji_xmp", "abnormal", unit: "m");
        AssertField(result, "position.rtk_flag", "dji_xmp", "abnormal");
        Assert.DoesNotContain("Infinity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("9999999", json, StringComparison.Ordinal);
        Assert.DoesNotContain(longFlag, json, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Analyze_FormalObjectIntegrityFailure_BlocksWithoutFields()
    {
        var original = SyntheticMetadataJpeg.Create(SyntheticMetadataJpeg.Xmp(("GimbalYawDegree", "2.5")));
        var root = CreateFormalObject(original, out var header);
        try
        {
            var objectPath = Path.Combine(root, header.ObjectKey.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllBytes(objectPath, SyntheticMetadataJpeg.Create(SyntheticMetadataJpeg.Xmp(("GimbalYawDegree", "3.5"))));

            var result = ImageMetadataAnalyzer.Analyze(header);

            Assert.AreEqual("blocked", result.Status);
            CollectionAssert.Contains(result.ReasonCodes.ToArray(), "formal_object_integrity_failed");
            Assert.HasCount(0, result.Fields);
            AssertPrivacy(result, coordinatesExpected: false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ResultSerialization_DoesNotContainFormalPathHashOrObjectKey()
    {
        var payload = SyntheticMetadataJpeg.Create(
            SyntheticMetadataJpeg.Exif(
                make: "DJI",
                model: "FC6310R",
                lensModel: null,
                focalLengthMm: null,
                localTime: null,
                latitude: null,
                longitude: null,
                altitude: null,
                gpsUtc: null));
        var root = CreateFormalObject(payload, out var header);
        try
        {
            var result = ImageMetadataAnalyzer.Analyze(header);
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.DoesNotContain(root, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(header.ExpectedSha256, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(header.ObjectKey, json, StringComparison.OrdinalIgnoreCase);
            AssertPrivacy(result, coordinatesExpected: false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ImageProbeImageMetadataResult AnalyzeFormalObject(byte[] bytes)
    {
        var root = CreateFormalObject(bytes, out var header);
        try
        {
            return ImageMetadataAnalyzer.Analyze(header);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateFormalObject(byte[] bytes, out ImageProbeCasImageRequestHeader header)
    {
        var root = Path.Combine(Path.GetTempPath(), $"qiongtu-metadata-probe-{Guid.NewGuid():N}");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var objectKey = $"sha256/{hash[..2]}/{hash}";
        var path = Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        header = new ImageProbeCasImageRequestHeader(
            ImageProbeProtocol.ImageMetadataV1,
            ImageProbeProtocol.ImageMetadataProfile,
            "normalized_image_frame",
            root,
            objectKey,
            hash,
            bytes.LongLength);
        ImageMetadataAnalyzer.ValidateHeader(header);
        return root;
    }

    private static void AssertField(
        ImageProbeImageMetadataResult result,
        string fieldName,
        string sourceKind,
        string fieldState,
        string? textValue = null,
        double? numericValue = null,
        string? unit = null)
    {
        var field = result.Fields.SingleOrDefault(candidate =>
            candidate.FieldName == fieldName &&
            candidate.SourceKind == sourceKind &&
            candidate.FieldState == fieldState);
        Assert.IsNotNull(
            field,
            $"Expected field {fieldName}/{sourceKind}/{fieldState}. Actual: {string.Join("; ", result.Fields.Where(candidate => candidate.FieldName == fieldName).Select(candidate => $"{candidate.SourceKind}/{candidate.FieldState}/{candidate.NumericValue}/{candidate.TextValue}"))}");
        Assert.AreEqual(textValue, field.TextValue);
        if (numericValue is not null)
        {
            Assert.IsNotNull(field.NumericValue);
            Assert.AreEqual(numericValue.Value, field.NumericValue.Value, Math.Max(Math.Abs(numericValue.Value) * 0.0000001, 0.0000001));
        }
        else
        {
            Assert.IsNull(field.NumericValue);
        }

        Assert.IsNull(field.BooleanValue);
        Assert.AreEqual(unit, field.Unit);
    }

    private static void AssertField(
        IReadOnlyList<ImageProbeImageMetadataField> fields,
        string fieldName,
        string sourceKind,
        string fieldState,
        double? numericValue = null,
        string? unit = null)
    {
        var field = fields.SingleOrDefault(candidate =>
            candidate.FieldName == fieldName &&
            candidate.SourceKind == sourceKind &&
            candidate.FieldState == fieldState);
        Assert.IsNotNull(
            field,
            $"Expected field {fieldName}/{sourceKind}/{fieldState}. Actual: {string.Join("; ", fields.Where(candidate => candidate.FieldName == fieldName).Select(candidate => $"{candidate.SourceKind}/{candidate.FieldState}/{candidate.NumericValue}/{candidate.TextValue}"))}");
        if (numericValue is not null)
        {
            Assert.IsNotNull(field.NumericValue);
            Assert.AreEqual(numericValue.Value, field.NumericValue.Value, Math.Max(Math.Abs(numericValue.Value) * 0.0000001, 0.0000001));
        }

        Assert.AreEqual(unit, field.Unit);
    }

    private static void AssertPrivacy(ImageProbeImageMetadataResult result, bool coordinatesExpected)
    {
        Assert.IsFalse(result.Privacy.PathsIncluded);
        Assert.IsFalse(result.Privacy.LocatorsIncluded);
        Assert.IsFalse(result.Privacy.ContentHashesIncluded);
        Assert.IsFalse(result.Privacy.ObjectKeysIncluded);
        Assert.IsFalse(result.Privacy.RawMetadataIncluded);
        Assert.IsFalse(result.Privacy.SerialNumbersIncluded);
        Assert.AreEqual(coordinatesExpected, result.Privacy.CoordinatesIncluded);
        Assert.IsFalse(result.Privacy.OwnerSampleStatisticsIncluded);
    }

    private static string StripBuildMetadata(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }
}

internal static class SyntheticMetadataJpeg
{
    public static byte[] Create(params byte[][] appSegments)
    {
        using var stream = new MemoryStream();
        stream.Write([0xff, 0xd8]);
        foreach (var segment in appSegments)
        {
            stream.Write(segment);
        }

        stream.Write([0xff, 0xd9]);
        return stream.ToArray();
    }

    public static byte[] Xmp(params (string Name, string Value)[] properties)
    {
        var xml = new StringBuilder();
        xml.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">");
        xml.Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");
        xml.Append("<rdf:Description xmlns:drone-dji=\"http://www.dji.com/drone-dji/1.0/\"");
        foreach (var (name, value) in properties)
        {
            xml.Append(" drone-dji:");
            xml.Append(name);
            xml.Append("=\"");
            xml.Append(System.Security.SecurityElement.Escape(value));
            xml.Append('"');
        }

        xml.Append("/></rdf:RDF></x:xmpmeta>");
        return AppSegment(
            0xe1,
            Combine(
                "http://ns.adobe.com/xap/1.0/\0"u8.ToArray(),
                Encoding.UTF8.GetBytes(xml.ToString())));
    }

    public static byte[] Exif(
        string make,
        string model,
        string? lensModel,
        double? focalLengthMm,
        string? localTime,
        double? latitude,
        double? longitude,
        double? altitude,
        string? gpsUtc,
        string? offsetTimeOriginal = null)
    {
        var tiff = new TiffBuilder();
        var ifd0Entries = new List<TiffEntry>
        {
            tiff.Ascii(0x010f, make),
            tiff.Ascii(0x0110, model)
        };

        var exifEntries = new List<TiffEntry>();
        if (localTime is not null)
        {
            exifEntries.Add(tiff.Ascii(0x9003, localTime));
        }

        if (offsetTimeOriginal is not null)
        {
            exifEntries.Add(tiff.Ascii(0x9011, offsetTimeOriginal));
        }

        if (lensModel is not null)
        {
            exifEntries.Add(tiff.Ascii(0xa434, lensModel));
        }

        if (focalLengthMm is not null)
        {
            exifEntries.Add(tiff.Rational(0x920a, focalLengthMm.Value));
        }

        var gpsEntries = new List<TiffEntry>();
        if (latitude is not null)
        {
            gpsEntries.Add(tiff.Ascii(0x0001, latitude.Value < 0 ? "S" : "N"));
            gpsEntries.Add(tiff.RationalArray(0x0002, DegreesToDms(latitude.Value)));
        }

        if (longitude is not null)
        {
            gpsEntries.Add(tiff.Ascii(0x0003, longitude.Value < 0 ? "W" : "E"));
            gpsEntries.Add(tiff.RationalArray(0x0004, DegreesToDms(longitude.Value)));
        }

        if (altitude is not null)
        {
            gpsEntries.Add(tiff.Byte(0x0005, altitude.Value < 0 ? (byte)1 : (byte)0));
            gpsEntries.Add(tiff.Rational(0x0006, Math.Abs(altitude.Value)));
        }

        if (gpsUtc is not null)
        {
            var date = DateTime.ParseExact(gpsUtc, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
            gpsEntries.Add(tiff.RationalArray(0x0007, [((uint)date.Hour, 1U), ((uint)date.Minute, 1U), ((uint)date.Second, 1U)]));
            gpsEntries.Add(tiff.Ascii(0x001d, date.ToString("yyyy:MM:dd", CultureInfo.InvariantCulture)));
        }

        return AppSegment(0xe1, Combine("Exif\0\0"u8.ToArray(), tiff.Build(ifd0Entries, exifEntries, gpsEntries)));
    }

    private static (uint Numerator, uint Denominator)[] DegreesToDms(double value)
    {
        var absolute = Math.Abs(value);
        var degrees = Math.Floor(absolute);
        var minutesFull = (absolute - degrees) * 60;
        var minutes = Math.Floor(minutesFull);
        var seconds = (minutesFull - minutes) * 60;
        return
        [
            ((uint)degrees, 1),
            ((uint)minutes, 1),
            ((uint)Math.Round(seconds * 1_000_000), 1_000_000)
        ];
    }

    private static byte[] AppSegment(byte marker, byte[] payload)
    {
        var segmentLength = checked(payload.Length + 2);
        var result = new byte[checked(segmentLength + 2)];
        result[0] = 0xff;
        result[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2, 2), checked((ushort)segmentLength));
        payload.CopyTo(result.AsSpan(4));
        return result;
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

    private sealed record TiffEntry(ushort Tag, ushort Type, uint Count, byte[] Data);

    private sealed class TiffBuilder
    {
        private const ushort TypeByte = 1;
        private const ushort TypeAscii = 2;
        private const ushort TypeLong = 4;
        private const ushort TypeRational = 5;

        public TiffEntry Ascii(ushort tag, string value) =>
            new(tag, TypeAscii, checked((uint)Encoding.ASCII.GetByteCount(value + "\0")), Encoding.ASCII.GetBytes(value + "\0"));

        public TiffEntry Byte(ushort tag, byte value) => new(tag, TypeByte, 1, [value]);

        public TiffEntry Rational(ushort tag, double value) =>
            RationalArray(tag, [(checked((uint)Math.Round(value * 1_000_000)), 1_000_000U)]);

        public TiffEntry RationalArray(ushort tag, (uint Numerator, uint Denominator)[] values)
        {
            var data = new byte[checked(values.Length * 8)];
            for (var index = 0; index < values.Length; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(index * 8, 4), values[index].Numerator);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(index * 8 + 4, 4), values[index].Denominator);
            }

            return new TiffEntry(tag, TypeRational, checked((uint)values.Length), data);
        }

        public byte[] Build(
            IReadOnlyList<TiffEntry> ifd0Entries,
            IReadOnlyList<TiffEntry> exifEntries,
            IReadOnlyList<TiffEntry> gpsEntries)
        {
            var ifd0 = new List<TiffEntry>(ifd0Entries);
            if (exifEntries.Count > 0)
            {
                ifd0.Add(new TiffEntry(0x8769, TypeLong, 1, new byte[4]));
            }

            if (gpsEntries.Count > 0)
            {
                ifd0.Add(new TiffEntry(0x8825, TypeLong, 1, new byte[4]));
            }

            ifd0.Sort((left, right) => left.Tag.CompareTo(right.Tag));
            var ifd0Offset = 8;
            var ifd0Length = IfdLength(ifd0.Count);
            var directoryOffset = ifd0Offset + ifd0Length;
            var exifOffset = exifEntries.Count > 0 ? directoryOffset : 0;
            if (exifEntries.Count > 0)
            {
                directoryOffset += IfdLength(exifEntries.Count);
            }

            var gpsOffset = gpsEntries.Count > 0 ? directoryOffset : 0;
            if (gpsEntries.Count > 0)
            {
                directoryOffset += IfdLength(gpsEntries.Count);
            }

            var dataOffset = directoryOffset;
            var data = new List<byte>();
            var bytes = new byte[dataOffset];
            bytes[0] = (byte)'I';
            bytes[1] = (byte)'I';
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 42);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), checked((uint)ifd0Offset));
            WriteIfd(bytes, ifd0Offset, ifd0, ref dataOffset, data, exifOffset, gpsOffset);
            if (exifEntries.Count > 0)
            {
                WriteIfd(bytes, exifOffset, exifEntries, ref dataOffset, data, 0, 0);
            }

            if (gpsEntries.Count > 0)
            {
                WriteIfd(bytes, gpsOffset, gpsEntries, ref dataOffset, data, 0, 0);
            }

            return Combine(bytes, data.ToArray());
        }

        private static int IfdLength(int entryCount) => checked(2 + (entryCount * 12) + 4);

        private static void WriteIfd(
            byte[] bytes,
            int offset,
            IReadOnlyList<TiffEntry> entries,
            ref int dataOffset,
            List<byte> data,
            int exifOffset,
            int gpsOffset)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), checked((ushort)entries.Count));
            var cursor = offset + 2;
            foreach (var entry in entries)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(cursor, 2), entry.Tag);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(cursor + 2, 2), entry.Type);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(cursor + 4, 4), entry.Count);
                if (entry.Tag == 0x8769)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(cursor + 8, 4), checked((uint)exifOffset));
                }
                else if (entry.Tag == 0x8825)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(cursor + 8, 4), checked((uint)gpsOffset));
                }
                else if (entry.Data.Length <= 4)
                {
                    entry.Data.CopyTo(bytes.AsSpan(cursor + 8));
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(cursor + 8, 4), checked((uint)dataOffset));
                    data.AddRange(entry.Data);
                    dataOffset += entry.Data.Length;
                }

                cursor += 12;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(cursor, 4), 0);
        }
    }
}
