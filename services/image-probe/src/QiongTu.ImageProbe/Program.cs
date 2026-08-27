using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.ImageProbe;

public static class Program
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        if (!args.SequenceEqual([ImageProbeProtocol.StdioArgument], StringComparer.Ordinal))
        {
            await WriteFailureAsync("invalid_invocation");
            return 2;
        }

        try
        {
            var input = Console.OpenStandardInput();
            var headerBytes = await StdioEnvelope.ReadHeaderLineAsync(
                input,
                ImageProbeProtocol.MaximumHeaderBytes,
                CancellationToken.None);
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
        catch (ImageProbeProtocolException exception)
        {
            await WriteFailureAsync(exception.Code);
            return 2;
        }
        catch (IOException)
        {
            await WriteFailureAsync("probe_io_failed");
            return 1;
        }
        catch (JsonException)
        {
            await WriteFailureAsync("header_json_invalid");
            return 1;
        }
        catch (InvalidOperationException)
        {
            await WriteFailureAsync("probe_invalid_operation");
            return 1;
        }
        catch (ArgumentException)
        {
            await WriteFailureAsync("probe_argument_invalid");
            return 1;
        }
        catch (OverflowException)
        {
            await WriteFailureAsync("probe_overflow");
            return 1;
        }
    }

    private static Task WriteResultAsync(ImageProbeSourcePreflightResult result) =>
        WriteBoundedJsonAsync(result);

    private static Task WriteFailureAsync(string reasonCode) =>
        WriteBoundedJsonAsync(SourcePreflightAnalyzer.Failed(reasonCode));

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
}
