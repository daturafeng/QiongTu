using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using QiongTu.Contracts;

namespace QiongTu.ImageProbe;

internal static class CasPositioningAuxAnalyzer
{
    private const string ProductParser = "qiongtu.cas-positioning-aux";
    private const string ProductParserVersion = "1.0.0";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static ImageProbeCasPositioningAuxResult Analyze(ImageProbeCasPositioningAuxRequestHeader header)
    {
        try
        {
            StdioEnvelope.ValidatePositioningAuxHeader(header);
            using var stream = FormalCasObject.OpenAndVerify(header);
            var payload = ReadBoundedPayload(stream);
            var assessment = ParseDjiMrk(payload, header.AssociationItemCount);
            using var secondVerification = FormalCasObject.OpenAndVerify(header);
            return CreateParsed(assessment);
        }
        catch (PositioningAuxParseException exception)
        {
            return CreateFailed(
                exception.Code,
                exception.SequenceState,
                exception.CoverageState,
                exception.StandardDeviationState,
                exception.RtkQualityState);
        }
        catch (ImageProbeProtocolException exception)
        {
            return CreateFailed(exception.Code, auxiliaryType: "unknown");
        }
        catch (CasImageStructureException exception)
        {
            return CreateFailed(exception.Code);
        }
        catch (UnauthorizedAccessException)
        {
            return CreateFailed("formal_object_unavailable");
        }
        catch (IOException)
        {
            return CreateFailed("formal_object_unavailable");
        }
        catch (CryptographicException)
        {
            return CreateFailed("formal_object_integrity_failed");
        }
        catch (DecoderFallbackException)
        {
            return CreateFailed("mrk_utf8_invalid");
        }
        catch (ArgumentException)
        {
            return CreateFailed("mrk_structure_invalid");
        }
        catch (InvalidOperationException)
        {
            return CreateFailed("mrk_structure_invalid");
        }
        catch (OverflowException)
        {
            return CreateFailed("mrk_arithmetic_overflow");
        }
    }

    public static ImageProbeCasPositioningAuxResult Failed(string reasonCode) =>
        CreateFailed(NormalizeReasonCode(reasonCode), auxiliaryType: "unknown");

    private static byte[] ReadBoundedPayload(FileStream stream)
    {
        if (stream.Length is <= 0 or > ImageProbeProtocol.MaximumPositioningAuxObjectBytes)
        {
            throw new PositioningAuxParseException("object_size_out_of_range");
        }

        var payload = new byte[checked((int)stream.Length)];
        stream.Position = 0;
        var offset = 0;
        while (offset < payload.Length)
        {
            var read = stream.Read(payload.AsSpan(offset));
            if (read == 0)
            {
                throw new PositioningAuxParseException("formal_object_unavailable");
            }

            offset += read;
        }

        return payload;
    }

    private static MrkAssessment ParseDjiMrk(byte[] payload, int associationItemCount)
    {
        ValidateTextByteBoundaries(payload);
        var text = StrictUtf8.GetString(payload);
        if (text.Length == 0)
        {
            throw new PositioningAuxParseException("mrk_empty");
        }

        using var reader = new StringReader(text);
        using var inventory = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInventory(inventory, ImageProbeProtocol.DjiMrkParserV1);
        AppendInventory(inventory, ImageProbeProtocol.DjiMrkQualityPolicyV1);

        var sequences = new HashSet<int>();
        var lineCount = 0;
        var fixedQualityCount = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (lineCount >= ImageProbeProtocol.MaximumPositioningAuxLineCount)
            {
                throw new PositioningAuxParseException("mrk_line_limit_exceeded");
            }

            if (lineCount == 0)
            {
                line = line.TrimStart('\ufeff');
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                throw new PositioningAuxParseException("mrk_empty_line");
            }

            lineCount++;
            var record = ParseLine(line);
            if (!sequences.Add(record.Sequence))
            {
                throw new PositioningAuxParseException("mrk_sequence_duplicate");
            }

            if (record.RtkQuality == 50)
            {
                fixedQualityCount++;
            }

            AppendInventory(inventory, record.CanonicalInventoryEntry);
        }

        if (lineCount == 0)
        {
            throw new PositioningAuxParseException("mrk_empty");
        }

        if (lineCount != associationItemCount)
        {
            throw new PositioningAuxParseException(
                "mrk_coverage_mismatch",
                sequenceState: "contiguous",
                coverageState: "failed",
                standardDeviationState: "non_negative",
                rtkQualityState: "not_assessed");
        }

        if (sequences.Min() != 1 || sequences.Max() != lineCount || sequences.Count != lineCount)
        {
            throw new PositioningAuxParseException(
                "mrk_sequence_gap",
                sequenceState: "failed",
                coverageState: "failed",
                standardDeviationState: "non_negative",
                rtkQualityState: "not_assessed");
        }

        var rtkQualityState = fixedQualityCount == lineCount
            ? "all_q50"
            : fixedQualityCount == 0
                ? "non_q50"
                : "mixed_q";
        var qualityState = rtkQualityState == "all_q50" ? "passed" : "warning";
        return new MrkAssessment(
            qualityState,
            "contiguous",
            "complete",
            "non_negative",
            rtkQualityState,
            Convert.ToHexString(inventory.GetHashAndReset()).ToLowerInvariant());
    }

    private static void ValidateTextByteBoundaries(ReadOnlySpan<byte> payload)
    {
        var lineBytes = 0;
        var previousWasCarriageReturn = false;
        foreach (var value in payload)
        {
            if (value == 0)
            {
                throw new PositioningAuxParseException("mrk_text_contains_nul");
            }

            if (value == (byte)'\r')
            {
                if (lineBytes > ImageProbeProtocol.MaximumPositioningAuxLineBytes)
                {
                    throw new PositioningAuxParseException("mrk_line_length_exceeded");
                }

                lineBytes = 0;
                previousWasCarriageReturn = true;
                continue;
            }

            if (value == (byte)'\n')
            {
                if (!previousWasCarriageReturn &&
                    lineBytes > ImageProbeProtocol.MaximumPositioningAuxLineBytes)
                {
                    throw new PositioningAuxParseException("mrk_line_length_exceeded");
                }

                lineBytes = 0;
                previousWasCarriageReturn = false;
                continue;
            }

            previousWasCarriageReturn = false;
            lineBytes++;
            if (lineBytes > ImageProbeProtocol.MaximumPositioningAuxLineBytes)
            {
                throw new PositioningAuxParseException("mrk_line_length_exceeded");
            }
        }
    }

    private static MrkRecord ParseLine(string line)
    {
        var tokens = line.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length != 13)
        {
            throw new PositioningAuxParseException("mrk_field_count_invalid");
        }

        if (!TryPositiveInteger(tokens[0], out var sequence))
        {
            throw new PositioningAuxParseException("mrk_sequence_invalid");
        }

        if (!TryFiniteNumber(tokens[1], out var gpsSeconds) ||
            !TryBracketedPositiveInteger(tokens[2], out var gpsWeek) ||
            !TryNumberWithSuffix(tokens[3], "N", out var northing) ||
            !TryNumberWithSuffix(tokens[4], "E", out var easting) ||
            !TryNumberWithSuffix(tokens[5], "V", out var vertical) ||
            !TryNumberWithSuffix(tokens[6], "Lat", out var latitude) ||
            !TryNumberWithSuffix(tokens[7], "Lon", out var longitude) ||
            !TryNumberWithSuffix(tokens[8], "Ellh", out var ellipsoidHeight) ||
            !TryNumberWithSuffix(tokens[9], string.Empty, out var standardDeviationX) ||
            !TryNumberWithSuffix(tokens[10], string.Empty, out var standardDeviationY) ||
            !TryFiniteNumber(tokens[11], out var standardDeviationZ) ||
            !TryNumberWithSuffix(tokens[12], "Q", out var rtkQuality))
        {
            throw new PositioningAuxParseException("mrk_numeric_invalid");
        }

        if (standardDeviationX < 0 || standardDeviationY < 0 || standardDeviationZ < 0)
        {
            throw new PositioningAuxParseException(
                "mrk_standard_deviation_negative",
                sequenceState: "not_assessed",
                coverageState: "not_assessed",
                standardDeviationState: "failed",
                rtkQualityState: "not_assessed");
        }

        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{sequence}|{gpsSeconds:R}|{gpsWeek}|{northing:R}|{easting:R}|{vertical:R}|{latitude:R}|{longitude:R}|{ellipsoidHeight:R}|{standardDeviationX:R}|{standardDeviationY:R}|{standardDeviationZ:R}|{rtkQuality:R}");
        return new MrkRecord(sequence, rtkQuality, canonical);
    }

    private static bool TryPositiveInteger(string value, out int parsed) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed > 0;

    private static bool TryBracketedPositiveInteger(string value, out int parsed)
    {
        parsed = 0;
        return value.Length > 2 &&
               value[0] == '[' &&
               value[^1] == ']' &&
               TryPositiveInteger(value[1..^1], out parsed);
    }

    private static bool TryNumberWithSuffix(string value, string suffix, out double parsed)
    {
        parsed = default;
        var comma = value.LastIndexOf(',');
        return comma > 0 &&
               string.Equals(value[(comma + 1)..], suffix, StringComparison.Ordinal) &&
               TryFiniteNumber(value[..comma], out parsed);
    }

    private static bool TryFiniteNumber(string value, out double parsed) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) &&
        double.IsFinite(parsed);

    private static void AppendInventory(IncrementalHash inventory, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        inventory.AppendData(bytes);
        inventory.AppendData([0]);
    }

    private static ImageProbeCasPositioningAuxResult CreateParsed(MrkAssessment assessment) =>
        new(
            ImageProbeProtocol.CasPositioningAuxV1,
            ImageProbeProtocol.CasPositioningAuxProfile,
            "parsed",
            assessment.QualityState,
            "positioning_aux",
            "mrk",
            assessment.SequenceState,
            assessment.CoverageState,
            assessment.StandardDeviationState,
            assessment.RtkQualityState,
            assessment.CanonicalInventoryHash,
            [],
            ParserIdentity(),
            EmptyPrivacy());

    private static ImageProbeCasPositioningAuxResult CreateFailed(
        string reasonCode,
        string sequenceState = "failed",
        string coverageState = "failed",
        string standardDeviationState = "failed",
        string rtkQualityState = "failed",
        string auxiliaryType = "mrk") =>
        new(
            ImageProbeProtocol.CasPositioningAuxV1,
            ImageProbeProtocol.CasPositioningAuxProfile,
            "failed",
            "failed",
            "positioning_aux",
            auxiliaryType,
            sequenceState,
            coverageState,
            standardDeviationState,
            rtkQualityState,
            "unavailable",
            [NormalizeReasonCode(reasonCode)],
            ParserIdentity(),
            EmptyPrivacy());

    private static ImageProbeCasPositioningAuxParserIdentity ParserIdentity() =>
        new(
            ProductParser,
            ProductParserVersion,
            ImageProbeProtocol.DjiMrkParserV1,
            ImageProbeProtocol.DjiMrkQualityPolicyV1);

    private static ImageProbePrivacy EmptyPrivacy() =>
        new(false, false, false, false, false, false, false, false);

    private static string NormalizeReasonCode(string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 64 ||
            reasonCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            return "positioning_aux_probe_failed";
        }

        return reasonCode;
    }

    private sealed record MrkRecord(int Sequence, double RtkQuality, string CanonicalInventoryEntry);

    private sealed record MrkAssessment(
        string QualityState,
        string SequenceState,
        string CoverageState,
        string StandardDeviationState,
        string RtkQualityState,
        string CanonicalInventoryHash);

    private sealed class PositioningAuxParseException : Exception
    {
        public PositioningAuxParseException(
            string code,
            string sequenceState = "failed",
            string coverageState = "failed",
            string standardDeviationState = "failed",
            string rtkQualityState = "failed")
        {
            Code = code;
            SequenceState = sequenceState;
            CoverageState = coverageState;
            StandardDeviationState = standardDeviationState;
            RtkQualityState = rtkQualityState;
        }

        public string Code { get; }

        public string SequenceState { get; }

        public string CoverageState { get; }

        public string StandardDeviationState { get; }

        public string RtkQualityState { get; }
    }
}
