using System.Reflection;
using System.Globalization;
using System.Text;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using QiongTu.Contracts;

namespace QiongTu.ImageProbe;

public static class SourcePreflightAnalyzer
{
    private const string ProductParserVersion = "1.0.0";
    private static readonly ImageProbePrivacy Privacy = new(
        PathsIncluded: false,
        LocatorsIncluded: false,
        ContentHashesIncluded: false,
        ObjectKeysIncluded: false,
        RawMetadataIncluded: false,
        SerialNumbersIncluded: false,
        CoordinatesIncluded: false,
        OwnerSampleStatisticsIncluded: false);
    private static readonly ImageProbeParserIdentity Parser = new(
        "qiongtu.source-preflight",
        ProductParserVersion,
        typeof(ImageMetadataReader).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ImageMetadataReader).Assembly.GetName().Version?.ToString()
            ?? "unknown");

    public static ImageProbeSourcePreflightResult Analyze(
        ImageProbeRequestHeader header,
        ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (payload.Length != header.PayloadByteLength)
        {
            throw new ImageProbeProtocolException("payload_length_mismatch");
        }

        return header.CandidateKind switch
        {
            "image_candidate" => AnalyzeImage(payload),
            "positioning_aux_candidate" => AnalyzeSidecar(
                header.FormatHint,
                header.AssociationItemCount,
                payload),
            _ => throw new ImageProbeProtocolException("invalid_candidate_kind")
        };
    }

    public static ImageProbeSourcePreflightResult Failed(string reasonCode) =>
        Create(
            status: "failed",
            candidateKind: "unknown",
            containerHint: "unknown",
            evidenceState: "read_failed",
            evidenceKinds: [],
            reasonCodes: [NormalizeReasonCode(reasonCode)]);

    private static ImageProbeSourcePreflightResult AnalyzeImage(ReadOnlySpan<byte> payload)
    {
        var containerHint = ImageContainerHint.Detect(payload);
        var evidenceKinds = new SortedSet<string>(StringComparer.Ordinal);
        var reasonCodes = new SortedSet<string>(StringComparer.Ordinal);
        string? make = null;
        string? model = null;

        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            var directories = ImageMetadataReader.ReadMetadata(stream);
            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            make = NormalizeMetadataValue(ifd0?.GetString(ExifDirectoryBase.TagMake));
            model = NormalizeMetadataValue(ifd0?.GetString(ExifDirectoryBase.TagModel));
            if (make is not null)
            {
                evidenceKinds.Add("exif_make");
            }

            if (model is not null)
            {
                evidenceKinds.Add("exif_model");
            }
        }
        catch (Exception exception) when (exception is ImageProcessingException
            or IOException
            or ArgumentException
            or InvalidOperationException)
        {
            reasonCodes.Add("metadata_unreadable_or_truncated");
        }

        var hasDjiXmp = XmpEvidence.ContainsDjiNamespace(payload);
        if (hasDjiXmp)
        {
            evidenceKinds.Add("dji_xmp_namespace");
        }

        var makeIsDji = IsDjiManufacturer(make);
        var makeIsOther = make is not null && !makeIsDji;
        if (makeIsDji)
        {
            evidenceKinds.Add("dji_exif_manufacturer");
        }
        else if (makeIsOther)
        {
            evidenceKinds.Add("other_exif_manufacturer");
        }

        string evidenceState;
        if (makeIsOther && hasDjiXmp)
        {
            evidenceState = "conflict";
            reasonCodes.Add("manufacturer_xmp_conflict");
        }
        else if (makeIsDji || hasDjiXmp)
        {
            evidenceState = "supports_dji";
        }
        else if (makeIsOther)
        {
            evidenceState = "out_of_scope";
            reasonCodes.Add("other_manufacturer");
        }
        else
        {
            evidenceState = "unconfirmed";
            reasonCodes.Add("dji_evidence_missing");
        }

        return Create(
            "completed",
            "image_candidate",
            containerHint,
            evidenceState,
            evidenceKinds,
            reasonCodes);
    }

    private static ImageProbeSourcePreflightResult AnalyzeSidecar(
        string? formatHint,
        int? associationItemCount,
        ReadOnlySpan<byte> payload)
    {
        var normalizedHint = formatHint?.ToLowerInvariant();
        var evidenceKinds = new SortedSet<string>(StringComparer.Ordinal);
        var reasonCodes = new SortedSet<string>(StringComparer.Ordinal);
        var supported = normalizedHint is "mrk" or "nav" or "obs" or "rtk";
        if (!supported)
        {
            reasonCodes.Add("unsupported_sidecar_hint");
            return Create(
                "completed",
                "positioning_aux_candidate",
                "not_image",
                "unconfirmed",
                evidenceKinds,
                reasonCodes);
        }

        if (normalizedHint == "mrk")
        {
            var mrk = SidecarEvidence.InspectDjiImageLogMrk(payload, associationItemCount);
            if (mrk.LayoutValid)
            {
                evidenceKinds.Add("dji_mrk_13_field_layout");
                if (mrk.CoverageValid)
                {
                    evidenceKinds.Add("dji_mrk_batch_coverage");
                    return Create(
                        "completed",
                        "positioning_aux_candidate",
                        "not_image",
                        "supports_dji",
                        evidenceKinds,
                        reasonCodes);
                }

                reasonCodes.Add(associationItemCount is null
                    ? "sidecar_batch_association_required"
                    : "sidecar_batch_coverage_mismatch");
                return Create(
                    "completed",
                    "positioning_aux_candidate",
                    "not_image",
                    "unconfirmed",
                    evidenceKinds,
                    reasonCodes);
            }
        }

        if (normalizedHint is "nav" or "obs" && SidecarEvidence.IsRinex(payload))
        {
            evidenceKinds.Add("rinex_header");
            reasonCodes.Add("sidecar_not_manufacturer_specific");
        }
        else if (normalizedHint == "rtk" && SidecarEvidence.IsRtcm3(payload))
        {
            evidenceKinds.Add("rtcm3_frame_header");
            reasonCodes.Add("sidecar_not_manufacturer_specific");
        }
        else
        {
            reasonCodes.Add("sidecar_header_unrecognized");
        }

        return Create(
            "completed",
            "positioning_aux_candidate",
            "not_image",
            "unconfirmed",
            evidenceKinds,
            reasonCodes);
    }

    private static ImageProbeSourcePreflightResult Create(
        string status,
        string candidateKind,
        string containerHint,
        string evidenceState,
        IEnumerable<string> evidenceKinds,
        IEnumerable<string> reasonCodes) =>
        new(
            ImageProbeProtocol.SourcePreflightV1,
            ImageProbeProtocol.SourcePreflightProfile,
            status,
            candidateKind,
            containerHint,
            evidenceState,
            evidenceKinds.Take(ImageProbeProtocol.MaximumEvidenceKinds).ToArray(),
            reasonCodes.Take(ImageProbeProtocol.MaximumReasonCodes).ToArray(),
            Parser,
            Privacy);

    private static string? NormalizeMetadataValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Normalize(NormalizationForm.FormKC);
        return normalized.Length <= 256 ? normalized : normalized[..256];
    }

    private static bool IsDjiManufacturer(string? value) =>
        value is not null &&
        (value.Equals("DJI", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("DJI TECHNOLOGY", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("SZ DJI", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeReasonCode(string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 64 ||
            reasonCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            return "probe_failed";
        }

        return reasonCode;
    }
}

internal static class ImageContainerHint
{
    public static string Detect(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 4 &&
            ((payload[0] == (byte)'I' && payload[1] == (byte)'I' && payload[2] is 42 or 43 && payload[3] == 0) ||
             (payload[0] == (byte)'M' && payload[1] == (byte)'M' && payload[2] == 0 && payload[3] is 42 or 43)))
        {
            return payload[0] == (byte)'I' ?
                payload[2] == 43 ? "bigtiff" : "tiff" :
                payload[3] == 43 ? "bigtiff" : "tiff";
        }

        if (payload.Length < 2 || payload[0] != 0xff || payload[1] != 0xd8)
        {
            return "unknown";
        }

        return ContainsJpegAppIdentifier(payload, 0xe2, "MPF\0"u8)
            ? "mpo_hint"
            : "jpeg_hint";
    }

    internal static bool ContainsJpegAppIdentifier(
        ReadOnlySpan<byte> payload,
        byte markerCode,
        ReadOnlySpan<byte> identifier)
    {
        var offset = 2;
        while (offset + 4 <= payload.Length)
        {
            if (payload[offset] != 0xff)
            {
                return false;
            }

            while (offset < payload.Length && payload[offset] == 0xff)
            {
                offset++;
            }

            if (offset >= payload.Length)
            {
                return false;
            }

            var marker = payload[offset++];
            if (marker is 0xda or 0xd9)
            {
                return false;
            }

            if (marker is 0x01 or >= 0xd0 and <= 0xd7)
            {
                continue;
            }

            if (offset + 2 > payload.Length)
            {
                return false;
            }

            var segmentLength = (payload[offset] << 8) | payload[offset + 1];
            if (segmentLength < 2 || offset + segmentLength > payload.Length)
            {
                return false;
            }

            var content = payload.Slice(offset + 2, segmentLength - 2);
            if (marker == markerCode && content.StartsWith(identifier))
            {
                return true;
            }

            offset += segmentLength;
        }

        return false;
    }
}

internal static class XmpEvidence
{
    private static ReadOnlySpan<byte> XmpIdentifier => "http://ns.adobe.com/xap/1.0/\0"u8;

    public static bool ContainsDjiNamespace(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != 0xff || payload[1] != 0xd8)
        {
            return false;
        }

        var offset = 2;
        while (offset + 4 <= payload.Length)
        {
            if (payload[offset] != 0xff)
            {
                return false;
            }

            while (offset < payload.Length && payload[offset] == 0xff)
            {
                offset++;
            }

            if (offset >= payload.Length)
            {
                return false;
            }

            var marker = payload[offset++];
            if (marker is 0xda or 0xd9)
            {
                return false;
            }

            if (marker is 0x01 or >= 0xd0 and <= 0xd7)
            {
                continue;
            }

            if (offset + 2 > payload.Length)
            {
                return false;
            }

            var segmentLength = (payload[offset] << 8) | payload[offset + 1];
            if (segmentLength < 2 || offset + segmentLength > payload.Length)
            {
                return false;
            }

            var content = payload.Slice(offset + 2, segmentLength - 2);
            if (marker == 0xe1 && content.StartsWith(XmpIdentifier))
            {
                var xmp = content[XmpIdentifier.Length..];
                if (xmp.IndexOf("drone-dji:"u8) >= 0 ||
                    xmp.IndexOf("http://www.dji.com/drone-dji/1.0/"u8) >= 0)
                {
                    return true;
                }
            }

            offset += segmentLength;
        }

        return false;
    }
}

internal static class SidecarEvidence
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static MrkInspection InspectDjiImageLogMrk(
        ReadOnlySpan<byte> payload,
        int? expectedImageCount)
    {
        var text = TryReadText(payload, 64 * 1024);
        if (text is null)
        {
            return new MrkInspection(false, false);
        }

        var lines = text.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || lines.Length > 100_000)
        {
            return new MrkInspection(false, false);
        }

        var sequences = new HashSet<int>();
        foreach (var line in lines)
        {
            var tokens = line.TrimStart('\ufeff')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length != 13 ||
                !TryPositiveInteger(tokens[0], out var sequence) ||
                !TryFiniteNumber(tokens[1]) ||
                !TryBracketedPositiveInteger(tokens[2]) ||
                !TryNumberWithSuffix(tokens[3], "N") ||
                !TryNumberWithSuffix(tokens[4], "E") ||
                !TryNumberWithSuffix(tokens[5], "V") ||
                !TryNumberWithSuffix(tokens[6], "Lat") ||
                !TryNumberWithSuffix(tokens[7], "Lon") ||
                !TryNumberWithSuffix(tokens[8], "Ellh") ||
                !TryNumberWithSuffix(tokens[9], string.Empty) ||
                !TryNumberWithSuffix(tokens[10], string.Empty) ||
                !TryFiniteNumber(tokens[11]) ||
                !TryNumberWithSuffix(tokens[12], "Q"))
            {
                return new MrkInspection(false, false);
            }

            if (!sequences.Add(sequence))
            {
                return new MrkInspection(true, false);
            }
        }

        var coverageValid = expectedImageCount is not null &&
                            sequences.Count == expectedImageCount &&
                            sequences.Count > 0 &&
                            sequences.Min() == 1 &&
                            sequences.Max() == expectedImageCount;
        return new MrkInspection(true, coverageValid);
    }

    public static bool IsRinex(ReadOnlySpan<byte> payload)
    {
        var text = TryReadText(payload, 16 * 1024);
        return text is not null &&
               text.Contains("RINEX VERSION / TYPE", StringComparison.Ordinal) &&
               text.Contains("END OF HEADER", StringComparison.Ordinal);
    }

    public static bool IsRtcm3(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6 || payload[0] != 0xd3 || (payload[1] & 0xfc) != 0)
        {
            return false;
        }

        var messageLength = ((payload[1] & 0x03) << 8) | payload[2];
        return messageLength > 0 && messageLength + 6 <= payload.Length;
    }

    private static string? TryReadText(ReadOnlySpan<byte> payload, int maximumBytes)
    {
        if (payload.IsEmpty)
        {
            return null;
        }

        var inspected = payload[..Math.Min(payload.Length, maximumBytes)];
        if (inspected.Contains((byte)0))
        {
            return null;
        }

        try
        {
            return StrictUtf8.GetString(inspected);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool TryPositiveInteger(string value) =>
        TryPositiveInteger(value, out _);

    private static bool TryPositiveInteger(string value, out int parsed) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed > 0;

    private static bool TryBracketedPositiveInteger(string value) =>
        value.Length > 2 &&
        value[0] == '[' &&
        value[^1] == ']' &&
        TryPositiveInteger(value[1..^1]);

    private static bool TryNumberWithSuffix(string value, string suffix)
    {
        var comma = value.LastIndexOf(',');
        return comma > 0 &&
               string.Equals(value[(comma + 1)..], suffix, StringComparison.Ordinal) &&
               TryFiniteNumber(value[..comma]);
    }

    private static bool TryFiniteNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
        double.IsFinite(parsed);

    internal sealed record MrkInspection(bool LayoutValid, bool CoverageValid);
}
