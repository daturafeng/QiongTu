using System.Globalization;
using System.Reflection;
using System.Text;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Exif.Makernotes;
using MetadataExtractor.Formats.Xmp;
using QiongTu.Contracts;
using MetadataDirectory = MetadataExtractor.Directory;

namespace QiongTu.ImageProbe;

internal static class ImageMetadataAnalyzer
{
    private const string ProductParser = "qiongtu.image-metadata";
    private const string ProductParserVersion = "1.0.0";
    private const string RequiredMetadataExtractorVersion = "2.9.3";
    private const int ExifTagOffsetTimeOriginal = 0x9011;
    private const double GpsConflictToleranceDegrees = 0.000001;
    private const double AltitudeConflictToleranceMeters = 0.20;
    private const double AttitudeConflictToleranceDegrees = 0.01;
    private static readonly string[] FieldInventory =
    [
        "capture.time_local",
        "capture.time_utc",
        "camera.manufacturer",
        "camera.model",
        "camera.lens_model",
        "camera.focal_length_mm",
        "position.latitude_deg",
        "position.longitude_deg",
        "position.absolute_altitude_m",
        "position.relative_altitude_m",
        "pose.gimbal_roll_deg",
        "pose.gimbal_pitch_deg",
        "pose.gimbal_yaw_deg",
        "pose.flight_roll_deg",
        "pose.flight_pitch_deg",
        "pose.flight_yaw_deg",
        "position.rtk_flag",
        "position.std_lon_m",
        "position.std_lat_m",
        "position.std_height_m"
    ];

    public static ImageProbeImageMetadataResult Analyze(ImageProbeCasImageRequestHeader header)
    {
        try
        {
            using var stream = FormalCasObject.OpenAndVerify(header);
            if (!IsRequiredMetadataExtractorVersion(MetadataExtractorVersion()))
            {
                return Blocked("metadata_extractor_version_mismatch");
            }

            var fields = ExtractFields(stream);
            return new ImageProbeImageMetadataResult(
                ImageProbeProtocol.ImageMetadataV1,
                ImageProbeProtocol.ImageMetadataProfile,
                "completed",
                "normalized_image_frame",
                fields,
                [],
                ParserIdentity(),
                Privacy(fields));
        }
        catch (ImageProcessingException)
        {
            return Blocked("metadata_unreadable");
        }
        catch (CasImageStructureException exception)
        {
            return Blocked(exception.Code);
        }
        catch (UnauthorizedAccessException)
        {
            return Blocked("formal_object_unavailable");
        }
        catch (IOException)
        {
            return Blocked("formal_object_unavailable");
        }
        catch (ArgumentException)
        {
            return Blocked("metadata_invalid");
        }
        catch (InvalidOperationException)
        {
            return Blocked("metadata_invalid");
        }
        catch (OverflowException)
        {
            return Blocked("metadata_overflow");
        }
    }

    public static ImageProbeImageMetadataResult Failed(string reasonCode) =>
        CreateTerminal("failed", NormalizeReasonCode(reasonCode));

    public static void ValidateHeader(ImageProbeCasImageRequestHeader header)
    {
        if (!string.Equals(header.SchemaVersion, ImageProbeProtocol.ImageMetadataV1, StringComparison.Ordinal) ||
            !string.Equals(header.Profile, ImageProbeProtocol.ImageMetadataProfile, StringComparison.Ordinal))
        {
            throw new ImageProbeProtocolException("unsupported_protocol");
        }

        if (header.ObjectKind != "normalized_image_frame")
        {
            throw new ImageProbeProtocolException("invalid_object_kind");
        }

        if (header.ExpectedByteLength is <= 0 or > ImageProbeProtocol.MaximumCasObjectBytes)
        {
            throw new ImageProbeProtocolException("object_size_out_of_range");
        }

        if (string.IsNullOrEmpty(header.FormalObjectRoot) ||
            header.FormalObjectRoot.Length > 32_767 ||
            header.FormalObjectRoot.IndexOf('\0') >= 0 ||
            !Path.IsPathFullyQualified(header.FormalObjectRoot) ||
            header.FormalObjectRoot.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new ImageProbeProtocolException("formal_object_root_invalid");
        }

        if (!IsLowercaseSha256(header.ExpectedSha256))
        {
            throw new ImageProbeProtocolException("expected_hash_invalid");
        }

        var expectedObjectKey = $"sha256/{header.ExpectedSha256[..2]}/{header.ExpectedSha256}";
        if (!string.Equals(header.ObjectKey, expectedObjectKey, StringComparison.Ordinal))
        {
            throw new ImageProbeProtocolException("object_key_invalid");
        }
    }

    internal static bool IsRequiredMetadataExtractorVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return false;
        }

        var metadataIndex = informationalVersion.IndexOf('+');
        var semanticVersion = metadataIndex >= 0
            ? informationalVersion[..metadataIndex]
            : informationalVersion;
        return string.Equals(semanticVersion, RequiredMetadataExtractorVersion, StringComparison.Ordinal);
    }

    internal static IReadOnlyList<ImageProbeImageMetadataField> ExtractFieldsForTests(
        IReadOnlyList<MetadataDirectory> directories)
    {
        var builder = new MetadataFieldBuilder();
        ExtractExif(directories, builder);
        ExtractGps(directories, builder);
        ExtractXmp(directories, builder);
        ExtractDjiMakerNote(directories, builder);
        builder.ApplyConflictPolicy();
        return builder.Build();
    }

    private static IReadOnlyList<ImageProbeImageMetadataField> ExtractFields(Stream stream)
    {
        stream.Position = 0;
        var directories = ImageMetadataReader.ReadMetadata(stream);
        return ExtractFieldsForTests(directories);
    }

    private static void ExtractExif(IReadOnlyList<MetadataDirectory> directories, MetadataFieldBuilder builder)
    {
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        if (ifd0 is not null)
        {
            builder.AddText("camera.manufacturer", "exif", "IFD0.Make", ifd0.GetString(ExifDirectoryBase.TagMake));
            builder.AddText("camera.model", "exif", "IFD0.Model", ifd0.GetString(ExifDirectoryBase.TagModel));
        }

        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (subIfd is null)
        {
            return;
        }

        if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var localCaptureTime))
        {
            builder.AddText(
                "capture.time_local",
                "exif",
                "ExifIFD.DateTimeOriginal",
                localCaptureTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
            if (TryGetExifUtc(subIfd, localCaptureTime, out var utcCaptureTime))
            {
                builder.AddText(
                    "capture.time_utc",
                    "exif",
                    "ExifIFD.DateTimeOriginal+ExifIFD.OffsetTimeOriginal",
                    utcCaptureTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.MarkUtcNotAssessableWhenMissing();
            }
        }
        else if (subIfd.ContainsTag(ExifDirectoryBase.TagDateTimeOriginal))
        {
            builder.AddAbnormal("capture.time_local", "exif", "ExifIFD.DateTimeOriginal", "text", null);
        }

        builder.AddText("camera.lens_model", "exif", "ExifIFD.LensModel", subIfd.GetString(ExifDirectoryBase.TagLensModel));
        if (subIfd.TryGetRational(ExifDirectoryBase.TagFocalLength, out var focalLength))
        {
            builder.AddNumber(
                "camera.focal_length_mm",
                "exif",
                "ExifIFD.FocalLength",
                focalLength.ToDouble(),
                "mm",
                value => value is > 0 and < 10_000);
        }
        else if (subIfd.ContainsTag(ExifDirectoryBase.TagFocalLength))
        {
            builder.AddAbnormal("camera.focal_length_mm", "exif", "ExifIFD.FocalLength", "number", "mm");
        }
    }

    private static void ExtractGps(IReadOnlyList<MetadataDirectory> directories, MetadataFieldBuilder builder)
    {
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        if (gps is null)
        {
            return;
        }

        if (gps.TryGetGeoLocation(out var location))
        {
            builder.AddNumber("position.latitude_deg", "gps_exif", "GPS.GPSLatitude", location.Latitude, "deg", value => value is >= -90 and <= 90);
            builder.AddNumber("position.longitude_deg", "gps_exif", "GPS.GPSLongitude", location.Longitude, "deg", value => value is >= -180 and <= 180);
        }
        else if (gps.ContainsTag(GpsDirectory.TagLatitude) || gps.ContainsTag(GpsDirectory.TagLongitude))
        {
            builder.AddAbnormal("position.latitude_deg", "gps_exif", "GPS.GPSLatitude", "number", "deg");
            builder.AddAbnormal("position.longitude_deg", "gps_exif", "GPS.GPSLongitude", "number", "deg");
        }

        if (gps.TryGetRational(GpsDirectory.TagAltitude, out var altitude))
        {
            var sign = IsBelowSeaLevel(gps.GetObject(GpsDirectory.TagAltitudeRef)) ? -1 : 1;
            builder.AddNumber("position.absolute_altitude_m", "gps_exif", "GPS.GPSAltitude", sign * altitude.ToDouble(), "m", value => value is >= -12_000 and <= 100_000);
        }
        else if (gps.ContainsTag(GpsDirectory.TagAltitude))
        {
            builder.AddAbnormal("position.absolute_altitude_m", "gps_exif", "GPS.GPSAltitude", "number", "m");
        }

        if (gps.TryGetGpsDate(out var gpsDate))
        {
            builder.AddText(
                "capture.time_utc",
                "gps_exif",
                "GPS.GPSDateStamp+GPS.GPSTimeStamp",
                DateTime.SpecifyKind(gpsDate, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }
        else if (gps.ContainsTag(GpsDirectory.TagDateStamp) || gps.ContainsTag(GpsDirectory.TagTimeStamp))
        {
            builder.AddAbnormal("capture.time_utc", "gps_exif", "GPS.GPSDateStamp+GPS.GPSTimeStamp", "text", null);
        }
    }

    private static void ExtractXmp(IReadOnlyList<MetadataDirectory> directories, MetadataFieldBuilder builder)
    {
        foreach (var xmp in directories.OfType<XmpDirectory>())
        {
            foreach (var property in xmp.GetXmpProperties())
            {
                var propertyName = CanonicalXmpPropertyName(property.Key);
                var sourceDetail = "drone-dji:" + propertyName;
                switch (propertyName)
                {
                    case "GpsLatitude":
                        builder.AddNumber("position.latitude_deg", "dji_xmp", sourceDetail, property.Value, "deg", value => value is >= -90 and <= 90);
                        break;
                    case "GpsLongitude":
                        builder.AddNumber("position.longitude_deg", "dji_xmp", sourceDetail, property.Value, "deg", value => value is >= -180 and <= 180);
                        break;
                    case "AbsoluteAltitude":
                        builder.AddNumber("position.absolute_altitude_m", "dji_xmp", sourceDetail, property.Value, "m", value => value is >= -12_000 and <= 100_000);
                        break;
                    case "RelativeAltitude":
                        builder.AddNumber("position.relative_altitude_m", "dji_xmp", sourceDetail, property.Value, "m", value => value is >= -12_000 and <= 100_000);
                        break;
                    case "GimbalRollDegree":
                        builder.AddNumber("pose.gimbal_roll_deg", "dji_xmp", sourceDetail, property.Value, "deg", IsPlausibleAttitude);
                        break;
                    case "GimbalPitchDegree":
                        builder.AddNumber("pose.gimbal_pitch_deg", "dji_xmp", sourceDetail, property.Value, "deg", IsPlausibleAttitude);
                        break;
                    case "GimbalYawDegree":
                        builder.AddNumber("pose.gimbal_yaw_deg", "dji_xmp", sourceDetail, property.Value, "deg", IsPlausibleAttitude);
                        break;
                    case "FlightRollDegree":
                        builder.AddNumber("pose.flight_roll_deg", "dji_xmp", sourceDetail, property.Value, "deg", IsPlausibleAttitude);
                        break;
                    case "FlightPitchDegree":
                        builder.AddNumber("pose.flight_pitch_deg", "dji_xmp", sourceDetail, property.Value, "deg", IsPlausibleAttitude);
                        break;
                    case "FlightYawDegree":
                        builder.AddNumber("pose.flight_yaw_deg", "dji_xmp", sourceDetail, property.Value, "deg", IsPlausibleAttitude);
                        break;
                    case "RtkFlag":
                        builder.AddText("position.rtk_flag", "dji_xmp", sourceDetail, property.Value);
                        break;
                    case "RtkStdLon":
                    case "RtkStdLongitude":
                        builder.AddNumber("position.std_lon_m", "dji_xmp", sourceDetail, property.Value, "m", IsNonNegativeReasonableStandardDeviation);
                        break;
                    case "RtkStdLat":
                    case "RtkStdLatitude":
                        builder.AddNumber("position.std_lat_m", "dji_xmp", sourceDetail, property.Value, "m", IsNonNegativeReasonableStandardDeviation);
                        break;
                    case "RtkStdHgt":
                    case "RtkStdHeight":
                        builder.AddNumber("position.std_height_m", "dji_xmp", sourceDetail, property.Value, "m", IsNonNegativeReasonableStandardDeviation);
                        break;
                }
            }
        }
    }

    private static void ExtractDjiMakerNote(IReadOnlyList<MetadataDirectory> directories, MetadataFieldBuilder builder)
    {
        foreach (var dji in directories.OfType<DjiMakernoteDirectory>())
        {
            AddMakerNoteNumber(builder, "pose.flight_pitch_deg", "DjiMakernote.AircraftPitch", dji.GetAircraftPitch());
            AddMakerNoteNumber(builder, "pose.flight_yaw_deg", "DjiMakernote.AircraftYaw", dji.GetAircraftYaw());
            AddMakerNoteNumber(builder, "pose.flight_roll_deg", "DjiMakernote.AircraftRoll", dji.GetAircraftRoll());
            AddMakerNoteNumber(builder, "pose.gimbal_pitch_deg", "DjiMakernote.CameraPitch", dji.GetCameraPitch());
            AddMakerNoteNumber(builder, "pose.gimbal_yaw_deg", "DjiMakernote.CameraYaw", dji.GetCameraYaw());
            AddMakerNoteNumber(builder, "pose.gimbal_roll_deg", "DjiMakernote.CameraRoll", dji.GetCameraRoll());
        }
    }

    private static void AddMakerNoteNumber(
        MetadataFieldBuilder builder,
        string fieldName,
        string sourceDetail,
        float? value)
    {
        if (value is not null)
        {
            builder.AddNumber(fieldName, "exif", sourceDetail, value.Value, "deg", IsPlausibleAttitude);
        }
    }

    private static bool TryGetExifUtc(
        ExifSubIfdDirectory subIfd,
        DateTime localCaptureTime,
        out DateTime utcCaptureTime)
    {
        utcCaptureTime = default;
        var offsetText = subIfd.GetString(ExifTagOffsetTimeOriginal);
        if (!string.IsNullOrWhiteSpace(offsetText))
        {
            var trimmed = offsetText.Trim();
            if (trimmed.Length == 6 &&
                (trimmed[0] == '+' || trimmed[0] == '-') &&
                TimeSpan.TryParseExact(trimmed[1..], "hh\\:mm", CultureInfo.InvariantCulture, out var unsignedOffset))
            {
                var offset = trimmed[0] == '-' ? -unsignedOffset : unsignedOffset;
                utcCaptureTime = DateTime.SpecifyKind(localCaptureTime - offset, DateTimeKind.Utc);
                return true;
            }
        }

        if (subIfd.ContainsTag(ExifDirectoryBase.TagTimeZoneOffset))
        {
            try
            {
                var offsets = subIfd.GetInt32Array(ExifDirectoryBase.TagTimeZoneOffset);
                if (offsets is { Length: > 0 })
                {
                    utcCaptureTime = DateTime.SpecifyKind(localCaptureTime - TimeSpan.FromHours(offsets[0]), DateTimeKind.Utc);
                    return true;
                }
            }
            catch (MetadataException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsBelowSeaLevel(object? altitudeRef) => altitudeRef switch
    {
        byte value => value == 1,
        sbyte value => value == 1,
        short value => value == 1,
        ushort value => value == 1,
        int value => value == 1,
        uint value => value == 1,
        byte[] { Length: > 0 } values => values[0] == 1,
        _ => false
    };

    private static string CanonicalXmpPropertyName(string key)
    {
        var separator = key.LastIndexOfAny([':', '/', '#']);
        return separator >= 0 ? key[(separator + 1)..] : key;
    }

    private static bool IsPlausibleAttitude(double value) => value is >= -360 and <= 360;

    private static bool IsNonNegativeReasonableStandardDeviation(double value) => value is >= 0 and <= 10_000;

    private static ImageProbeImageMetadataResult Blocked(string reasonCode) =>
        CreateTerminal("blocked", NormalizeReasonCode(reasonCode));

    private static ImageProbeImageMetadataResult CreateTerminal(string status, string reasonCode) =>
        new(
            ImageProbeProtocol.ImageMetadataV1,
            ImageProbeProtocol.ImageMetadataProfile,
            status,
            "normalized_image_frame",
            [],
            [reasonCode],
            ParserIdentity(),
            EmptyPrivacy());

    private static ImageProbeImageMetadataParserIdentity ParserIdentity() =>
        new(
            ProductParser,
            ProductParserVersion,
            MetadataExtractorVersion(),
            ImageProbeProtocol.DjiMetadataMapV1,
            ImageProbeProtocol.MetadataConflictV1);

    private static string MetadataExtractorVersion() =>
        typeof(ImageMetadataReader).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ImageMetadataReader).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static ImageProbePrivacy EmptyPrivacy() =>
        new(false, false, false, false, false, false, false, false);

    private static ImageProbePrivacy Privacy(IReadOnlyList<ImageProbeImageMetadataField> fields) =>
        new(false, false, false, false, false, false, fields.Any(IsCoordinateField), false);

    private static bool IsCoordinateField(ImageProbeImageMetadataField field) =>
        field.FieldName.StartsWith("position.", StringComparison.Ordinal) &&
        field.FieldState is "present" or "conflict";

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string NormalizeReasonCode(string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 64 ||
            reasonCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            return "metadata_probe_failed";
        }

        return reasonCode;
    }

    private sealed class MetadataFieldBuilder
    {
        private readonly List<ImageProbeImageMetadataField> _fields = [];
        private bool _utcNotAssessableFromLocalTime;

        public void AddText(string fieldName, string sourceKind, string sourceDetail, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = value.Trim().Normalize(NormalizationForm.FormKC);
            if (normalized.Length > ImageProbeProtocol.MaximumMetadataTextBytes)
            {
                AddAbnormal(fieldName, sourceKind, sourceDetail, "text", null);
                return;
            }

            _fields.Add(new ImageProbeImageMetadataField(fieldName, sourceKind, sourceDetail, "present", "text", normalized, null, null, null));
        }

        public void AddNumber(
            string fieldName,
            string sourceKind,
            string sourceDetail,
            string? value,
            string unit,
            Func<double, bool> isInRange)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = value.Trim().TrimStart('+');
            if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                AddAbnormal(fieldName, sourceKind, sourceDetail, "number", unit);
                return;
            }

            AddNumber(fieldName, sourceKind, sourceDetail, parsed, unit, isInRange);
        }

        public void AddNumber(
            string fieldName,
            string sourceKind,
            string sourceDetail,
            double value,
            string unit,
            Func<double, bool> isInRange)
        {
            if (!double.IsFinite(value) || !isInRange(value))
            {
                AddAbnormal(fieldName, sourceKind, sourceDetail, "number", unit);
                return;
            }

            _fields.Add(new ImageProbeImageMetadataField(fieldName, sourceKind, sourceDetail, "present", "number", null, value, null, unit));
        }

        public void AddAbnormal(string fieldName, string sourceKind, string sourceDetail, string valueType, string? unit) =>
            _fields.Add(new ImageProbeImageMetadataField(fieldName, sourceKind, sourceDetail, "abnormal", valueType, null, null, null, unit));

        public void MarkUtcNotAssessableWhenMissing()
        {
            _utcNotAssessableFromLocalTime = true;
        }

        public void ApplyConflictPolicy()
        {
            ApplyNumericConflict("position.latitude_deg", "gps_exif", "dji_xmp", GpsConflictToleranceDegrees);
            ApplyNumericConflict("position.longitude_deg", "gps_exif", "dji_xmp", GpsConflictToleranceDegrees);
            ApplyNumericConflict("position.absolute_altitude_m", "gps_exif", "dji_xmp", AltitudeConflictToleranceMeters);
            ApplyNumericConflict("pose.gimbal_roll_deg", "exif", "dji_xmp", AttitudeConflictToleranceDegrees);
            ApplyNumericConflict("pose.gimbal_pitch_deg", "exif", "dji_xmp", AttitudeConflictToleranceDegrees);
            ApplyNumericConflict("pose.gimbal_yaw_deg", "exif", "dji_xmp", AttitudeConflictToleranceDegrees);
            ApplyNumericConflict("pose.flight_roll_deg", "exif", "dji_xmp", AttitudeConflictToleranceDegrees);
            ApplyNumericConflict("pose.flight_pitch_deg", "exif", "dji_xmp", AttitudeConflictToleranceDegrees);
            ApplyNumericConflict("pose.flight_yaw_deg", "exif", "dji_xmp", AttitudeConflictToleranceDegrees);
        }

        public IReadOnlyList<ImageProbeImageMetadataField> Build()
        {
            foreach (var fieldName in FieldInventory)
            {
                if (_fields.Any(field => field.FieldName == fieldName))
                {
                    continue;
                }

                _fields.Add(fieldName == "capture.time_utc" && _utcNotAssessableFromLocalTime
                    ? Terminal(fieldName, "timezone_missing", "not_assessable")
                    : Terminal(fieldName, "field_missing", "missing"));
            }

            return _fields
                .OrderBy(field => Array.IndexOf(FieldInventory, field.FieldName))
                .ThenBy(field => field.SourceKind, StringComparer.Ordinal)
                .ThenBy(field => field.SourceDetail, StringComparer.Ordinal)
                .ToArray();
        }

        private void ApplyNumericConflict(string fieldName, string leftSource, string rightSource, double tolerance)
        {
            var left = _fields.FirstOrDefault(field => IsPresentNumber(field, fieldName, leftSource));
            var right = _fields.FirstOrDefault(field => IsPresentNumber(field, fieldName, rightSource));
            if (left is null || right is null ||
                Math.Abs(left.NumericValue!.Value - right.NumericValue!.Value) <= tolerance)
            {
                return;
            }

            Replace(left, left with { FieldState = "conflict" });
            Replace(right, right with { FieldState = "conflict" });
        }

        private static bool IsPresentNumber(ImageProbeImageMetadataField field, string fieldName, string sourceKind) =>
            field.FieldName == fieldName &&
            field.SourceKind == sourceKind &&
            field.FieldState == "present" &&
            field.NumericValue is not null;

        private void Replace(ImageProbeImageMetadataField existing, ImageProbeImageMetadataField replacement)
        {
            var index = _fields.IndexOf(existing);
            if (index >= 0)
            {
                _fields[index] = replacement;
            }
        }

        private static ImageProbeImageMetadataField Terminal(string fieldName, string sourceDetail, string fieldState) =>
            new(fieldName, "derived", sourceDetail, fieldState, "none", null, null, null, null);
    }
}
