using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.ImageProbe;

public static class Program
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        var responseKind = "source-preflight";
        if (!args.SequenceEqual([ImageProbeProtocol.StdioArgument], StringComparer.Ordinal))
        {
            await WriteFailureAsync("invalid_invocation", responseKind);
            return 2;
        }

        try
        {
            var input = Console.OpenStandardInput();
            var headerBytes = await StdioEnvelope.ReadHeaderLineAsync(
                input,
                ImageProbeProtocol.MaximumCasHeaderBytes,
                CancellationToken.None);
            var dispatch = JsonSerializer.Deserialize<ImageProbeDispatchHeader>(headerBytes, SerializerOptions)
                ?? throw new ImageProbeProtocolException("invalid_header");
            if (string.Equals(dispatch.Profile, ImageProbeProtocol.CasImageProfile, StringComparison.Ordinal) ||
                string.Equals(dispatch.SchemaVersion, ImageProbeProtocol.CasImageV1, StringComparison.Ordinal))
            {
                responseKind = "cas-image";
            }
            else if (string.Equals(dispatch.Profile, ImageProbeProtocol.ImageMetadataProfile, StringComparison.Ordinal) ||
                     string.Equals(dispatch.SchemaVersion, ImageProbeProtocol.ImageMetadataV1, StringComparison.Ordinal))
            {
                responseKind = "image-metadata";
            }
            else if (string.Equals(dispatch.Profile, ImageProbeProtocol.CasPositioningAuxProfile, StringComparison.Ordinal) ||
                     string.Equals(dispatch.SchemaVersion, ImageProbeProtocol.CasPositioningAuxV1, StringComparison.Ordinal))
            {
                responseKind = "cas-positioning-aux";
            }

            if (string.Equals(dispatch.Profile, ImageProbeProtocol.SourcePreflightProfile, StringComparison.Ordinal) &&
                string.Equals(dispatch.SchemaVersion, ImageProbeProtocol.SourcePreflightV1, StringComparison.Ordinal))
            {
                if (headerBytes.Length > ImageProbeProtocol.MaximumHeaderBytes)
                {
                    throw new ImageProbeProtocolException("header_too_large");
                }

                var header = JsonSerializer.Deserialize<ImageProbeRequestHeader>(headerBytes, SerializerOptions)
                    ?? throw new ImageProbeProtocolException("invalid_header");
                StdioEnvelope.ValidateHeader(header);
                var payload = new byte[header.PayloadByteLength];
                await StdioEnvelope.ReadExactlyAsync(input, payload, CancellationToken.None);
                if (await StdioEnvelope.HasTrailingDataAsync(input, CancellationToken.None))
                {
                    throw new ImageProbeProtocolException("trailing_input");
                }

                var result = SourcePreflightAnalyzer.Analyze(header, payload);
                await WriteResultAsync(result);
                return 0;
            }

            if (responseKind == "cas-image")
            {
                var header = JsonSerializer.Deserialize<ImageProbeCasImageRequestHeader>(headerBytes, SerializerOptions)
                    ?? throw new ImageProbeProtocolException("invalid_header");
                StdioEnvelope.ValidateCasHeader(header);
                if (await StdioEnvelope.HasTrailingDataAsync(input, CancellationToken.None))
                {
                    throw new ImageProbeProtocolException("trailing_input");
                }

                var result = CasImageAnalyzer.Analyze(header);
                if (JsonSerializer.SerializeToUtf8Bytes(result, SerializerOptions).Length >
                    ImageProbeProtocol.MaximumCasOutputBytes)
                {
                    await WriteResultAsync(CasImageAnalyzer.Failed("probe_output_limit_exceeded"));
                    return 1;
                }

                await WriteResultAsync(result);
                return result.Status == "failed" ? 1 : 0;
            }

            if (responseKind == "image-metadata")
            {
                var header = JsonSerializer.Deserialize<ImageProbeCasImageRequestHeader>(headerBytes, SerializerOptions)
                    ?? throw new ImageProbeProtocolException("invalid_header");
                ImageMetadataAnalyzer.ValidateHeader(header);
                if (await StdioEnvelope.HasTrailingDataAsync(input, CancellationToken.None))
                {
                    throw new ImageProbeProtocolException("trailing_input");
                }

                var result = ImageMetadataAnalyzer.Analyze(header);
                if (JsonSerializer.SerializeToUtf8Bytes(result, SerializerOptions).Length >
                    ImageProbeProtocol.MaximumMetadataOutputBytes)
                {
                    await WriteResultAsync(ImageMetadataAnalyzer.Failed("probe_output_limit_exceeded"));
                    return 1;
                }

                await WriteResultAsync(result);
                return result.Status == "failed" ? 1 : 0;
            }

            if (responseKind == "cas-positioning-aux")
            {
                var header = JsonSerializer.Deserialize<ImageProbeCasPositioningAuxRequestHeader>(headerBytes, SerializerOptions)
                    ?? throw new ImageProbeProtocolException("invalid_header");
                StdioEnvelope.ValidatePositioningAuxHeader(header);
                if (await StdioEnvelope.HasTrailingDataAsync(input, CancellationToken.None))
                {
                    throw new ImageProbeProtocolException("trailing_input");
                }

                var result = CasPositioningAuxAnalyzer.Analyze(header);
                if (JsonSerializer.SerializeToUtf8Bytes(result, SerializerOptions).Length >
                    ImageProbeProtocol.MaximumPositioningAuxOutputBytes)
                {
                    await WriteResultAsync(CasPositioningAuxAnalyzer.Failed("probe_output_limit_exceeded"));
                    return 1;
                }

                await WriteResultAsync(result);
                return result.ParseState == "failed" ? 1 : 0;
            }

            throw new ImageProbeProtocolException("unsupported_protocol");
        }
        catch (ImageProbeProtocolException exception)
        {
            await WriteFailureAsync(exception.Code, responseKind);
            return 2;
        }
        catch (IOException)
        {
            await WriteFailureAsync("probe_io_failed", responseKind);
            return 1;
        }
        catch (JsonException)
        {
            await WriteFailureAsync("header_json_invalid", responseKind);
            return 1;
        }
        catch (InvalidOperationException)
        {
            await WriteFailureAsync("probe_invalid_operation", responseKind);
            return 1;
        }
        catch (ArgumentException)
        {
            await WriteFailureAsync("probe_argument_invalid", responseKind);
            return 1;
        }
        catch (OverflowException)
        {
            await WriteFailureAsync("probe_overflow", responseKind);
            return 1;
        }
    }

    private static Task WriteResultAsync(ImageProbeSourcePreflightResult result) =>
        WriteBoundedJsonAsync(result);

    private static Task WriteResultAsync(ImageProbeCasImageResult result) =>
        WriteBoundedJsonAsync(result);

    private static Task WriteResultAsync(ImageProbeImageMetadataResult result) =>
        WriteBoundedJsonAsync(result);

    private static Task WriteResultAsync(ImageProbeCasPositioningAuxResult result) =>
        WriteBoundedJsonAsync(result);

    private static Task WriteFailureAsync(string reasonCode, string responseKind) =>
        responseKind switch
        {
            "cas-image" => WriteBoundedJsonAsync(CasImageAnalyzer.Failed(reasonCode)),
            "image-metadata" => WriteBoundedJsonAsync(ImageMetadataAnalyzer.Failed(reasonCode)),
            "cas-positioning-aux" => WriteBoundedJsonAsync(CasPositioningAuxAnalyzer.Failed(reasonCode)),
            _ => WriteBoundedJsonAsync(SourcePreflightAnalyzer.Failed(reasonCode))
        };

    private static async Task WriteBoundedJsonAsync(ImageProbeSourcePreflightResult result)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, SerializerOptions);
        if (bytes.Length > ImageProbeProtocol.MaximumOutputBytes)
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(
                SourcePreflightAnalyzer.Failed("probe_output_limit_exceeded"),
                SerializerOptions);
        }

        var output = Console.OpenStandardOutput();
        await output.WriteAsync(bytes);
        await output.FlushAsync();
    }

    private static async Task WriteBoundedJsonAsync(ImageProbeCasImageResult result)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, SerializerOptions);
        if (bytes.Length > ImageProbeProtocol.MaximumCasOutputBytes)
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(
                CasImageAnalyzer.Failed("probe_output_limit_exceeded"),
                SerializerOptions);
        }

        var output = Console.OpenStandardOutput();
        await output.WriteAsync(bytes);
        await output.FlushAsync();
    }

    private static async Task WriteBoundedJsonAsync(ImageProbeImageMetadataResult result)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, SerializerOptions);
        if (bytes.Length > ImageProbeProtocol.MaximumMetadataOutputBytes)
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(
                ImageMetadataAnalyzer.Failed("probe_output_limit_exceeded"),
                SerializerOptions);
        }

        var output = Console.OpenStandardOutput();
        await output.WriteAsync(bytes);
        await output.FlushAsync();
    }

    private static async Task WriteBoundedJsonAsync(ImageProbeCasPositioningAuxResult result)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, SerializerOptions);
        if (bytes.Length > ImageProbeProtocol.MaximumPositioningAuxOutputBytes)
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(
                CasPositioningAuxAnalyzer.Failed("probe_output_limit_exceeded"),
                SerializerOptions);
        }

        var output = Console.OpenStandardOutput();
        await output.WriteAsync(bytes);
        await output.FlushAsync();
    }
}
