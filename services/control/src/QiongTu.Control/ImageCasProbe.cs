using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.Control;

internal sealed class ImageCasProbeException : IOException
{
    public ImageCasProbeException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed record ImageCasProbeOptions(
    TimeSpan? Timeout = null,
    int MaximumOutputBytes = ImageProbeProtocol.MaximumCasOutputBytes,
    int MaximumErrorBytes = 8 * 1024,
    long MaximumProcessMemoryBytes = 2L * 1024 * 1024 * 1024)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(60);
}

internal interface IImageCasProbeClient
{
    Task<ImageProbeCasImageResult> AnalyzeAsync(
        ContentAddressedObjectStore objectStore,
        PublishedObject sourceObject,
        string objectKind,
        CancellationToken cancellationToken);
}

internal sealed class IsolatedImageCasProbeClient : IImageCasProbeClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<ProcessStartInfo> _startInfoFactory;
    private readonly ImageCasProbeOptions _options;

    public IsolatedImageCasProbeClient(
        ImageCasProbeOptions? options = null,
        Func<ProcessStartInfo>? startInfoFactory = null)
    {
        _options = options ?? new ImageCasProbeOptions();
        ValidateOptions(_options);
        _startInfoFactory = startInfoFactory ?? CreateProductStartInfo;
    }

    public async Task<ImageProbeCasImageResult> AnalyzeAsync(
        ContentAddressedObjectStore objectStore,
        PublishedObject sourceObject,
        string objectKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectStore);
        ArgumentNullException.ThrowIfNull(sourceObject);
        if (objectKind != "source_image")
        {
            throw new ImageCasProbeException(
                "cas_image_object_kind_invalid",
                "Only a formally published source image can be inspected.");
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
                "The formal source image failed integrity verification.",
                exception);
        }

        if (verified is null ||
            !string.Equals(verified.ObjectKey, sourceObject.ObjectKey, StringComparison.Ordinal))
        {
            throw new ImageCasProbeException(
                "formal_object_unavailable",
                "The formal source image is unavailable.");
        }

        var header = new ImageProbeCasImageRequestHeader(
            ImageProbeProtocol.CasImageV1,
            ImageProbeProtocol.CasImageProfile,
            objectKind,
            objectStore.PublishedDirectory,
            verified.ObjectKey,
            verified.Sha256,
            verified.ByteLength);
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, SerializerOptions);
        if (headerBytes.Length == 0 || headerBytes.Length > ImageProbeProtocol.MaximumCasHeaderBytes)
        {
            throw new ImageCasProbeException(
                "cas_image_probe_header_limit_exceeded",
                "The CAS image probe request exceeds its protocol limit.");
        }

        var request = new byte[headerBytes.Length + 1];
        headerBytes.CopyTo(request, 0);
        request[^1] = (byte)'\n';
        var privateRuntimeRoot = Path.Combine(
            Path.GetTempPath(),
            "QiongTu",
            "image-probe-host",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateRuntimeRoot);
        try
        {
            return await RunProbeAsync(request, objectKind, privateRuntimeRoot, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(privateRuntimeRoot);
        }
    }

    internal static ProcessStartInfo CreateProductStartInfo()
    {
        var executablePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "image-probe",
            "QiongTu.ImageProbe.exe"));
        var startInfo = new ProcessStartInfo { FileName = executablePath };
        startInfo.ArgumentList.Add(ImageProbeProtocol.StdioArgument);
        return startInfo;
    }

    private async Task<ImageProbeCasImageResult> RunProbeAsync(
        byte[] request,
        string objectKind,
        string privateRuntimeRoot,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = PrepareStartInfo(_startInfoFactory(), privateRuntimeRoot)
        };
        try
        {
            if (!process.Start())
            {
                throw new ImageCasProbeException(
                    "cas_image_probe_start_failed",
                    "The isolated CAS image probe could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            throw new ImageCasProbeException(
                "cas_image_probe_start_failed",
                "The isolated CAS image probe could not be started.",
                exception);
        }

        WindowsChildProcessJob? job = null;
        try
        {
            job = WindowsChildProcessJob.TryCreateAndAssign(process, _options.MaximumProcessMemoryBytes);
        }
        catch (ImageSourcePreflightProbeException exception)
        {
            TryKill(process);
            throw new ImageCasProbeException(
                "cas_image_probe_job_failed",
                "The isolated CAS image probe could not be constrained.",
                exception);
        }

        using (job)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_options.EffectiveTimeout);
            var outputTask = ReadBoundedAsync(
                process.StandardOutput.BaseStream,
                _options.MaximumOutputBytes,
                "cas_image_probe_output_limit_exceeded",
                timeout.Token);
            var errorTask = ReadBoundedAsync(
                process.StandardError.BaseStream,
                _options.MaximumErrorBytes,
                "cas_image_probe_error_limit_exceeded",
                timeout.Token);

            try
            {
                await process.StandardInput.BaseStream.WriteAsync(request, timeout.Token);
                await process.StandardInput.BaseStream.FlushAsync(timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw;
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw new ImageCasProbeException(
                    "cas_image_probe_timeout",
                    "The isolated CAS image probe exceeded its time limit.");
            }
            catch (IOException exception) when (process.HasExited)
            {
                var earlyOutput = await AwaitBoundedAfterChildExitAsync(outputTask);
                var earlyError = await AwaitBoundedAfterChildExitAsync(errorTask);
                var code = ClassifyChildFailure(earlyOutput, earlyError);
                throw new ImageCasProbeException(
                    code,
                    $"The isolated CAS image probe closed its input early ({code}).",
                    exception);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                TryKill(process);
                throw new ImageCasProbeException(
                    "cas_image_probe_transport_failed",
                    "The isolated CAS image probe transport failed.",
                    exception);
            }

            byte[] output;
            byte[] error;
            try
            {
                output = await outputTask;
                error = await errorTask;
            }
            catch (ImageCasProbeException)
            {
                TryKill(process);
                throw;
            }

            if (process.ExitCode != 0)
            {
                var code = ClassifyChildFailure(output, error);
                throw new ImageCasProbeException(
                    code,
                    $"The isolated CAS image probe returned a failure status ({code}).");
            }

            ImageProbeCasImageResult result;
            try
            {
                result = JsonSerializer.Deserialize<ImageProbeCasImageResult>(output, SerializerOptions)
                    ?? throw new JsonException();
            }
            catch (JsonException exception)
            {
                throw new ImageCasProbeException(
                    "cas_image_probe_response_invalid",
                    "The isolated CAS image probe returned an invalid response.",
                    exception);
            }

            ValidateResult(result, objectKind);
            return result;
        }
    }

    private static ProcessStartInfo PrepareStartInfo(ProcessStartInfo startInfo, string privateRuntimeRoot)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (string.IsNullOrWhiteSpace(startInfo.FileName))
        {
            throw new ImageCasProbeException(
                "cas_image_probe_path_missing",
                "The fixed CAS image probe executable path is unavailable.");
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        startInfo.StandardInputEncoding = utf8;
        startInfo.StandardOutputEncoding = utf8;
        startInfo.StandardErrorEncoding = utf8;
        startInfo.Environment.Clear();
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        startInfo.Environment["DOTNET_GCHeapHardLimit"] = "0x80000000";
        var bundleDirectory = Path.Combine(privateRuntimeRoot, "bundle");
        var tempDirectory = Path.Combine(privateRuntimeRoot, "temp");
        startInfo.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = bundleDirectory;
        startInfo.Environment["TEMP"] = tempDirectory;
        startInfo.Environment["TMP"] = tempDirectory;
        Directory.CreateDirectory(bundleDirectory);
        Directory.CreateDirectory(tempDirectory);
        return startInfo;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        string code,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 4096));
        var buffer = new byte[Math.Min(4096, maximumBytes + 1)];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                throw new ImageCasProbeException(
                    code,
                    "The isolated CAS image probe exceeded a process output limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void ValidateResult(ImageProbeCasImageResult result, string objectKind)
    {
        var valid = result.SchemaVersion == ImageProbeProtocol.CasImageV1 &&
                    result.Profile == ImageProbeProtocol.CasImageProfile &&
                    result.ObjectKind == objectKind &&
                    result.Status is "completed" or "blocked" &&
                    result.Container is "jpeg" or "mpo" or "tiff" or "unknown" &&
                    result.StructureState is "validated" or "blocked" &&
                    result.DecodeState is "decoded" or "not_decoded" &&
                    result.Frames.Count <= ImageProbeProtocol.MaximumCasFrameCount &&
                    result.ReasonCodes.Count <= ImageProbeProtocol.MaximumReasonCodes &&
                    result.Parser.ProductParser == "qiongtu.cas-image" &&
                    result.Parser.ProductParserVersion == "1.0.0" &&
                    result.Parser.NativeDecoder == "magick.net-q16-x64" &&
                    result.Parser.NativeDecoderVersion == "14.16.0" &&
                    !result.Privacy.PathsIncluded &&
                    !result.Privacy.LocatorsIncluded &&
                    !result.Privacy.ContentHashesIncluded &&
                    !result.Privacy.ObjectKeysIncluded &&
                    !result.Privacy.RawMetadataIncluded &&
                    !result.Privacy.SerialNumbersIncluded &&
                    !result.Privacy.CoordinatesIncluded &&
                    !result.Privacy.OwnerSampleStatisticsIncluded;
        if (!valid || !ValidateFrames(result))
        {
            throw new ImageCasProbeException(
                "cas_image_probe_response_invalid",
                "The isolated CAS image probe returned an invalid response.");
        }
    }

    private static bool ValidateFrames(ImageProbeCasImageResult result)
    {
        if (result.Status == "completed" && (result.Frames.Count == 0 || result.ReasonCodes.Count != 0) ||
            result.Status == "blocked" && (result.Frames.Count != 0 || result.ReasonCodes.Count == 0))
        {
            return false;
        }

        long totalPixels = 0;
        for (var index = 0; index < result.Frames.Count; index++)
        {
            var frame = result.Frames[index];
            if (frame.FrameIndex != index ||
                frame.FrameKind is not ("jpeg" or "mp_primary_image" or "mp_auxiliary_image" or "tiff_page") ||
                frame.ByteOffset < 0 || frame.ByteLength < 0 ||
                frame.Width <= 0 || frame.Height <= 0 || frame.BitsPerChannel <= 0 ||
                frame.DecodeState != "decoded" ||
                frame.Orientation is < 1 or > 8)
            {
                return false;
            }

            var pixels = checked((long)frame.Width * frame.Height);
            if (pixels > ImageProbeProtocol.MaximumCasPixelsPerFrame)
            {
                return false;
            }

            totalPixels = checked(totalPixels + pixels);
        }

        return totalPixels <= ImageProbeProtocol.MaximumCasTotalPixels;
    }

    private static string ClassifyChildFailure(ReadOnlySpan<byte> output, ReadOnlySpan<byte> error)
    {
        try
        {
            var result = JsonSerializer.Deserialize<ImageProbeCasImageResult>(output, SerializerOptions);
            var reason = result?.ReasonCodes.FirstOrDefault();
            if (result?.Status == "failed" && IsSafeCode(reason))
            {
                return "cas_image_probe_child_" + reason;
            }
        }
        catch (JsonException)
        {
            // Raw child output is never returned to the caller.
        }

        var errorText = Encoding.UTF8.GetString(error);
        if (errorText.Contains("Out of memory", StringComparison.OrdinalIgnoreCase) ||
            errorText.Contains("GC heap", StringComparison.OrdinalIgnoreCase))
        {
            return "cas_image_probe_memory_limit_exceeded";
        }

        if (errorText.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase) ||
            errorText.Contains("Fatal error", StringComparison.OrdinalIgnoreCase))
        {
            return "cas_image_probe_process_crashed";
        }

        return error.IsEmpty
            ? "cas_image_probe_process_failed"
            : "cas_image_probe_process_stderr";
    }

    private static async Task<byte[]> AwaitBoundedAfterChildExitAsync(Task<byte[]> task)
    {
        try
        {
            return await task;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ImageCasProbeException)
        {
            return [];
        }
    }

    private static bool IsSafeCode(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static void ValidateOptions(ImageCasProbeOptions options)
    {
        if (options.EffectiveTimeout <= TimeSpan.Zero ||
            options.MaximumOutputBytes is <= 0 or > ImageProbeProtocol.MaximumCasOutputBytes ||
            options.MaximumErrorBytes <= 0 ||
            options.MaximumProcessMemoryBytes < 512L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "CAS image probe limits are invalid.");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Best effort. Closing the Windows job also terminates the process tree.
        }
    }

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
            // The short-lived child has already exited; a later runtime cleanup can remove the directory.
        }
    }
}
