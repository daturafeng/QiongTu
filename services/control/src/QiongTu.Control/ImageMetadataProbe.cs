using System.Diagnostics;
using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.Control;

internal sealed class IsolatedImageMetadataProbeClient : IImageMetadataProbeClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IsolatedImageCasProbeClient _runner;

    public IsolatedImageMetadataProbeClient(
        ImageCasProbeOptions? options = null,
        Func<ProcessStartInfo>? startInfoFactory = null)
    {
        _runner = new IsolatedImageCasProbeClient(options, startInfoFactory);
    }

    public async Task<ImageProbeImageMetadataResult> AnalyzeAsync(
        ContentAddressedObjectStore objectStore,
        PublishedObject normalizedObject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectStore);
        ArgumentNullException.ThrowIfNull(normalizedObject);
        PublishedObject? verified;
        try
        {
            verified = await objectStore.FindPublishedAsync(
                normalizedObject.Sha256,
                normalizedObject.ByteLength,
                cancellationToken);
        }
        catch (ObjectStoreException exception)
        {
            throw new ImageCasProbeException(
                exception.Code,
                "The normalized image frame failed integrity verification.",
                exception);
        }

        if (verified is null ||
            !string.Equals(verified.ObjectKey, normalizedObject.ObjectKey, StringComparison.Ordinal))
        {
            throw new ImageCasProbeException(
                "formal_object_unavailable",
                "The normalized image frame is unavailable.");
        }

        var header = new ImageProbeCasImageRequestHeader(
            ImageProbeProtocol.ImageMetadataV1,
            ImageProbeProtocol.ImageMetadataProfile,
            "normalized_image_frame",
            objectStore.PublishedDirectory,
            verified.ObjectKey,
            verified.Sha256,
            verified.ByteLength);
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, SerializerOptions);
        if (headerBytes.Length == 0 || headerBytes.Length > ImageProbeProtocol.MaximumCasHeaderBytes)
        {
            throw new ImageCasProbeException(
                "image_metadata_probe_header_limit_exceeded",
                "The image metadata request exceeds its protocol limit.");
        }

        var request = new byte[headerBytes.Length + 1];
        headerBytes.CopyTo(request, 0);
        request[^1] = (byte)'\n';
        var privateRuntimeRoot = Path.Combine(
            Path.GetTempPath(),
            "QiongTu",
            "image-metadata-host",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateRuntimeRoot);
        try
        {
            var output = await _runner.RunRawProbeAsync(request, privateRuntimeRoot, cancellationToken);
            var result = Deserialize(output);
            ValidateResult(result);
            return result;
        }
        finally
        {
            TryDeleteDirectory(privateRuntimeRoot);
        }
    }

    internal static void ValidateResult(ImageProbeImageMetadataResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ReasonCodes is null || result.Fields is null || result.Parser is null || result.Privacy is null ||
            result.SchemaVersion != ImageProbeProtocol.ImageMetadataV1 ||
            result.Profile != ImageProbeProtocol.ImageMetadataProfile ||
            result.ObjectKind != "normalized_image_frame" ||
            result.Status is not ("completed" or "blocked") ||
            result.ReasonCodes.Count > ImageProbeProtocol.MaximumReasonCodes ||
            result.ReasonCodes.Any(code => !IsSafeToken(code, 128)) ||
            result.Parser.ProductParser != ImageMetadataCatalog.ProductParser ||
            result.Parser.ProductParserVersion != ImageMetadataCatalog.ProductParserVersion ||
            !IsExpectedMetadataExtractorVersion(result.Parser.MetadataExtractorVersion) ||
            result.Parser.FieldMappingVersion != ImageMetadataCatalog.FieldMappingVersion ||
            result.Parser.ConflictPolicyVersion != ImageMetadataCatalog.ConflictPolicyVersion ||
            result.Privacy.PathsIncluded || result.Privacy.LocatorsIncluded ||
            result.Privacy.ContentHashesIncluded || result.Privacy.ObjectKeysIncluded ||
            result.Privacy.RawMetadataIncluded || result.Privacy.SerialNumbersIncluded ||
            result.Privacy.OwnerSampleStatisticsIncluded)
        {
            throw InvalidResponse();
        }

        if (result.Status == "blocked")
        {
            if (result.Fields.Count != 0 || result.ReasonCodes.Count == 0 || result.Privacy.CoordinatesIncluded)
            {
                throw InvalidResponse();
            }

            return;
        }

        if (result.ReasonCodes.Count != 0 || result.Fields.Count is < 20 or > 64)
        {
            throw InvalidResponse();
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in result.Fields)
        {
            ValidateField(field, keys);
        }

        if (!ImageMetadataCatalog.RequiredFieldNames.SetEquals(result.Fields.Select(field => field.FieldName)))
        {
            throw InvalidResponse();
        }

        var includesCoordinates = result.Fields.Any(field =>
            field.FieldName.StartsWith("position.", StringComparison.Ordinal) &&
            field.FieldState is "present" or "conflict");
        if (result.Privacy.CoordinatesIncluded != includesCoordinates)
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateField(ImageProbeImageMetadataField field, HashSet<string> keys)
    {
        if (field is null ||
            !ImageMetadataCatalog.RequiredFieldNames.Contains(field.FieldName) ||
            field.SourceKind is not ("exif" or "gps_exif" or "dji_xmp" or "derived") ||
            field.FieldState is not ("present" or "missing" or "conflict" or "abnormal" or "not_assessable") ||
            !IsSafeSourceDetail(field.SourceKind, field.SourceDetail) ||
            !keys.Add(field.FieldName + "\n" + field.SourceKind))
        {
            throw InvalidResponse();
        }

        var hasText = field.TextValue is not null;
        var hasNumber = field.NumericValue is not null;
        var hasBoolean = field.BooleanValue is not null;
        var valueCount = (hasText ? 1 : 0) + (hasNumber ? 1 : 0) + (hasBoolean ? 1 : 0);
        var requiresValue = field.FieldState is "present" or "conflict";
        if ((requiresValue && valueCount != 1) || (!requiresValue && valueCount != 0))
        {
            throw InvalidResponse();
        }

        if (!requiresValue)
        {
            if (field.FieldState is "missing" or "not_assessable" &&
                (field.ValueType != "none" || field.Unit is not null))
            {
                throw InvalidResponse();
            }

            if (field.FieldState == "abnormal" &&
                field.ValueType is not ("text" or "number" or "boolean" or "none"))
            {
                throw InvalidResponse();
            }

            return;
        }

        switch (field.ValueType)
        {
            case "text" when hasText && !hasNumber && !hasBoolean && field.Unit is null:
                if (field.TextValue!.Length == 0 ||
                    field.TextValue.Length > ImageProbeProtocol.MaximumMetadataTextBytes ||
                    field.TextValue.Any(character => char.IsControl(character)))
                {
                    throw InvalidResponse();
                }

                break;
            case "number" when !hasText && hasNumber && !hasBoolean && field.Unit is "deg" or "m" or "mm":
                if (!double.IsFinite(field.NumericValue!.Value))
                {
                    throw InvalidResponse();
                }

                break;
            case "boolean" when !hasText && !hasNumber && hasBoolean && field.Unit is null:
                break;
            default:
                throw InvalidResponse();
        }
    }

    private static bool IsSafeSourceDetail(string sourceKind, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => char.IsControl(character)) ||
            value.Contains("serial", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.IndexOfAny(['\\', '/']) >= 0)
        {
            return false;
        }

        return sourceKind switch
        {
            "exif" => value.StartsWith("IFD0.", StringComparison.Ordinal) ||
                      value.StartsWith("ExifIFD.", StringComparison.Ordinal) ||
                      value.StartsWith("DjiMakernote.", StringComparison.Ordinal),
            "gps_exif" => value.StartsWith("GPS.", StringComparison.Ordinal),
            "dji_xmp" => value.StartsWith("drone-dji:", StringComparison.Ordinal),
            "derived" => value is "field_missing" or "timezone_missing" or "image-metadata.v1:missing",
            _ => false
        };
    }

    private static ImageProbeImageMetadataResult Deserialize(ReadOnlySpan<byte> output)
    {
        try
        {
            return JsonSerializer.Deserialize<ImageProbeImageMetadataResult>(output, SerializerOptions)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new ImageCasProbeException(
                "image_metadata_probe_response_invalid",
                "The isolated image metadata probe returned an invalid response.",
                exception);
        }
    }

    private static bool IsExpectedMetadataExtractorVersion(string version)
    {
        var metadataIndex = version.IndexOf('+');
        var semanticVersion = metadataIndex >= 0 ? version[..metadataIndex] : version;
        return semanticVersion == ImageMetadataCatalog.MetadataExtractorVersion;
    }

    private static bool IsSafeToken(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static ImageCasProbeException InvalidResponse() =>
        new(
            "image_metadata_probe_response_invalid",
            "The isolated image metadata probe returned a result outside the fixed allowlist.");

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
