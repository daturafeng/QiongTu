using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.ImageProbe;

public static class Program
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        var casRequest = false;
        if (!args.SequenceEqual([ImageProbeProtocol.StdioArgument], StringComparer.Ordinal))
        {
            await WriteFailureAsync("invalid_invocation", casRequest);
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
            casRequest = string.Equals(dispatch.Profile, ImageProbeProtocol.CasImageProfile, StringComparison.Ordinal) ||
                         string.Equals(dispatch.SchemaVersion, ImageProbeProtocol.CasImageV1, StringComparison.Ordinal);

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

            if (casRequest)
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

            throw new ImageProbeProtocolException("unsupported_protocol");
        }
        catch (ImageProbeProtocolException exception)
        {
            await WriteFailureAsync(exception.Code, casRequest);
            return 2;
        }
        catch (IOException)
        {
            await WriteFailureAsync("probe_io_failed", casRequest);
            return 1;
        }
        catch (JsonException)
        {
            await WriteFailureAsync("header_json_invalid", casRequest);
            return 1;
        }
        catch (InvalidOperationException)
        {
            await WriteFailureAsync("probe_invalid_operation", casRequest);
            return 1;
        }
        catch (ArgumentException)
        {
            await WriteFailureAsync("probe_argument_invalid", casRequest);
            return 1;
        }
        catch (OverflowException)
        {
            await WriteFailureAsync("probe_overflow", casRequest);
            return 1;
        }
    }

    private static Task WriteResultAsync(ImageProbeSourcePreflightResult result) =>
        WriteBoundedJsonAsync(result);

    private static Task WriteResultAsync(ImageProbeCasImageResult result) =>
        WriteBoundedJsonAsync(result);

    private static Task WriteFailureAsync(string reasonCode, bool casRequest) =>
        casRequest
            ? WriteBoundedJsonAsync(CasImageAnalyzer.Failed(reasonCode))
            : WriteBoundedJsonAsync(SourcePreflightAnalyzer.Failed(reasonCode));

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
}
