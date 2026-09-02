using System.Diagnostics;
using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.Control;

internal interface IPositioningAuxProbeClient
{
    Task<ImageProbeCasPositioningAuxResult> AnalyzeMrkAsync(
        ContentAddressedObjectStore objectStore,
        PublishedObject sourceObject,
        int associationItemCount,
        CancellationToken cancellationToken);
}

internal sealed class IsolatedPositioningAuxProbeClient : IPositioningAuxProbeClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> KnownReasonCodes = new(StringComparer.Ordinal)
    {
        "expected_hash_invalid",
        "formal_object_integrity_failed",
        "formal_object_namespace_invalid",
        "formal_object_reparse_detected",
        "formal_object_root_invalid",
        "formal_object_unavailable",
        "header_json_invalid",
        "header_too_large",
        "invalid_header",
        "invalid_invocation",
        "invalid_object_kind",
        "mrk_arithmetic_overflow",
        "mrk_coverage_mismatch",
        "mrk_empty",
        "mrk_empty_line",
        "mrk_field_count_invalid",
        "mrk_line_length_exceeded",
        "mrk_line_limit_exceeded",
        "mrk_numeric_invalid",
        "mrk_sequence_duplicate",
        "mrk_sequence_gap",
        "mrk_sequence_invalid",
        "mrk_standard_deviation_negative",
        "mrk_structure_invalid",
        "mrk_text_contains_nul",
        "mrk_utf8_invalid",
        "object_key_invalid",
        "object_size_out_of_range",
        "positioning_aux_probe_failed",
        "probe_argument_invalid",
        "probe_invalid_operation",
        "probe_io_failed",
        "probe_output_limit_exceeded",
        "probe_overflow",
        "trailing_input",
        "unsupported_protocol"
    };

    private readonly IsolatedImageCasProbeClient _runner;

    public IsolatedPositioningAuxProbeClient(
        ImageCasProbeOptions? options = null,
        Func<ProcessStartInfo>? startInfoFactory = null)
    {
        _runner = new IsolatedImageCasProbeClient(options, startInfoFactory);
    }

    public async Task<ImageProbeCasPositioningAuxResult> AnalyzeMrkAsync(
        ContentAddressedObjectStore objectStore,
        PublishedObject sourceObject,
        int associationItemCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectStore);
        ArgumentNullException.ThrowIfNull(sourceObject);
        if (associationItemCount <= 0)
        {
            throw new ImageCasProbeException(
                "positioning_aux_association_invalid",
                "The positioning auxiliary association count is invalid.");
        }

        PublishedObject? verified;
        try
        {
            verified = await objectStore.FindPublishedAsync(
                sourceObject.Sha256,
                sourceObject.ByteLength,
                cancellationToken);
        }
        catch (ObjectStoreException exception)
        {
            throw new ImageCasProbeException(
                exception.Code,
                "The positioning auxiliary object failed integrity verification.",
                exception);
        }

        if (verified is null ||
            !string.Equals(verified.ObjectKey, sourceObject.ObjectKey, StringComparison.Ordinal))
        {
            throw new ImageCasProbeException(
                "formal_object_unavailable",
                "The positioning auxiliary object is unavailable.");
        }

        var header = new ImageProbeCasPositioningAuxRequestHeader(
            ImageProbeProtocol.CasPositioningAuxV1,
            ImageProbeProtocol.CasPositioningAuxProfile,
            "positioning_aux",
            "mrk",
            associationItemCount,
            objectStore.PublishedDirectory,
            verified.ObjectKey,
            verified.Sha256,
            verified.ByteLength);
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, SerializerOptions);
        if (headerBytes.Length == 0 || headerBytes.Length > ImageProbeProtocol.MaximumCasHeaderBytes)
        {
            throw new ImageCasProbeException(
                "positioning_aux_probe_header_limit_exceeded",
                "The positioning auxiliary request exceeds its protocol limit.");
        }

        var request = new byte[headerBytes.Length + 1];
        headerBytes.CopyTo(request, 0);
        request[^1] = (byte)'\n';
        var privateRuntimeRoot = Path.Combine(
            Path.GetTempPath(),
            "QiongTu",
            "positioning-aux-host",
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

    internal static void ValidateResult(ImageProbeCasPositioningAuxResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ReasonCodes is null || result.Parser is null || result.Privacy is null ||
            result.SchemaVersion != ImageProbeProtocol.CasPositioningAuxV1 ||
            result.Profile != ImageProbeProtocol.CasPositioningAuxProfile ||
            result.ObjectKind != "positioning_aux" || result.AuxiliaryType != "mrk" ||
            result.ParseState is not ("parsed" or "failed") ||
            result.QualityState is not ("passed" or "warning" or "failed") ||
            result.SequenceState is not ("contiguous" or "failed" or "not_assessed") ||
            result.CoverageState is not ("complete" or "failed" or "not_assessed") ||
            result.StandardDeviationState is not ("non_negative" or "failed" or "not_assessed") ||
            result.RtkQualityState is not ("all_q50" or "non_q50" or "mixed_q" or "failed" or "not_assessed") ||
            result.Parser.ProductParser != "qiongtu.cas-positioning-aux" ||
            result.Parser.ProductParserVersion != "1.0.0" ||
            result.Parser.AuxiliaryParserVersion != ImageProbeProtocol.DjiMrkParserV1 ||
            result.Parser.QualityPolicyVersion != ImageProbeProtocol.DjiMrkQualityPolicyV1 ||
            result.Privacy.PathsIncluded || result.Privacy.LocatorsIncluded ||
            result.Privacy.ContentHashesIncluded || result.Privacy.ObjectKeysIncluded ||
            result.Privacy.RawMetadataIncluded || result.Privacy.SerialNumbersIncluded ||
            result.Privacy.CoordinatesIncluded || result.Privacy.OwnerSampleStatisticsIncluded ||
            result.ReasonCodes.Count > ImageProbeProtocol.MaximumReasonCodes ||
            result.ReasonCodes.Any(code => !KnownReasonCodes.Contains(code)))
        {
            throw InvalidResponse();
        }

        if (result.ParseState == "parsed")
        {
            if (result.QualityState is not ("passed" or "warning") ||
                result.SequenceState != "contiguous" || result.CoverageState != "complete" ||
                result.StandardDeviationState != "non_negative" ||
                result.RtkQualityState is not ("all_q50" or "non_q50" or "mixed_q") ||
                !IsSha256(result.CanonicalInventoryHash) || result.ReasonCodes.Count != 0)
            {
                throw InvalidResponse();
            }

            return;
        }

        if (result.QualityState != "failed" || result.CanonicalInventoryHash != "unavailable" ||
            result.ReasonCodes.Count != 1)
        {
            throw InvalidResponse();
        }
    }

    private static ImageProbeCasPositioningAuxResult Deserialize(ReadOnlySpan<byte> output)
    {
        try
        {
            return JsonSerializer.Deserialize<ImageProbeCasPositioningAuxResult>(output, SerializerOptions)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new ImageCasProbeException(
                "positioning_aux_probe_response_invalid",
                "The isolated positioning auxiliary probe returned an invalid response.",
                exception);
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static ImageCasProbeException InvalidResponse() =>
        new(
            "positioning_aux_probe_response_invalid",
            "The isolated positioning auxiliary probe returned a result outside the fixed allowlist.");

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
