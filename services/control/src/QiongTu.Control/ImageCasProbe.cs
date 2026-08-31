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
    private static readonly HashSet<string> KnownProbeReasonCodes = new(StringComparer.Ordinal)
    {
        "bigtiff_not_supported",
        "container_header_truncated",
        "formal_object_integrity_failed",
        "formal_object_namespace_invalid",
        "formal_object_reparse_detected",
        "formal_object_root_invalid",
        "formal_object_unavailable",
        "frame_pixel_limit_exceeded",
        "header_json_invalid",
        "header_too_large",
        "invalid_header",
        "invalid_invocation",
        "invalid_object_kind",
        "jpeg_app2_truncated",
        "jpeg_dimensions_invalid",
        "jpeg_dnl_not_supported",
        "jpeg_eoi_missing",
        "jpeg_marker_limit_exceeded",
        "jpeg_marker_order_invalid",
        "jpeg_marker_prefix_missing",
        "jpeg_marker_truncated",
        "jpeg_metadata_limit_exceeded",
        "jpeg_range_length_mismatch",
        "jpeg_range_out_of_bounds",
        "jpeg_required_marker_missing",
        "jpeg_scan_truncated",
        "jpeg_segment_length_invalid",
        "jpeg_segment_out_of_bounds",
        "jpeg_segment_truncated",
        "jpeg_sof_conflict",
        "jpeg_sof_invalid",
        "jpeg_sof_missing",
        "jpeg_soi_missing",
        "jpeg_stuffed_byte_outside_scan",
        "jpeg_trailing_data",
        "jpeg_truncated",
        "mpf_dependency_invalid",
        "mpf_entries_invalid",
        "mpf_entries_out_of_bounds",
        "mpf_header_invalid",
        "mpf_ifd_entry_limit_exceeded",
        "mpf_ifd_out_of_bounds",
        "mpf_image_count_invalid",
        "mpf_image_format_not_supported",
        "mpf_multiple_indexes",
        "mpf_primary_offset_invalid",
        "mpf_range_out_of_bounds",
        "mpf_ranges_overlap",
        "mpf_type_not_supported",
        "mpf_unreferenced_trailing_data",
        "mpf_version_invalid",
        "native_decode_failed",
        "native_decoder_version_mismatch",
        "native_policy_blocked",
        "native_resource_limit_exceeded",
        "object_key_invalid",
        "object_size_out_of_range",
        "parser_decoder_dimension_disagreement",
        "parser_decoder_frame_count_disagreement",
        "probe_argument_invalid",
        "probe_invalid_operation",
        "probe_io_failed",
        "probe_output_limit_exceeded",
        "probe_overflow",
        "expected_hash_invalid",
        "structure_arithmetic_overflow",
        "tiff_bits_invalid",
        "tiff_compression_invalid",
        "tiff_compression_not_supported",
        "tiff_duplicate_tag",
        "tiff_field_type_invalid",
        "tiff_header_invalid",
        "tiff_height_invalid",
        "tiff_ifd_cycle",
        "tiff_ifd_entry_limit_exceeded",
        "tiff_ifd_out_of_bounds",
        "tiff_metadata_limit_exceeded",
        "tiff_orientation_invalid",
        "tiff_page_limit_exceeded",
        "tiff_page_missing",
        "tiff_photometric_invalid",
        "tiff_photometric_not_supported",
        "tiff_pixel_range_out_of_bounds",
        "tiff_pixel_ranges_invalid",
        "tiff_pixel_ranges_overlap",
        "tiff_value_out_of_bounds",
        "tiff_width_invalid",
        "total_pixel_limit_exceeded",
        "trailing_input",
        "unsupported_image_container",
        "unsupported_protocol"
    };

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
            return await RunProbeAsync(
                request,
                objectKind,
                verified.ByteLength,
                privateRuntimeRoot,
                cancellationToken);
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
        long objectByteLength,
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
            var inputTask = WriteRequestAsync(
                process.StandardInput.BaseStream,
                request,
                timeout.Token);

            try
            {
                var exitTask = process.WaitForExitAsync(timeout.Token);
                var pending = new HashSet<Task> { exitTask, inputTask, outputTask, errorTask };
                while (!exitTask.IsCompleted)
                {
                    var completed = await Task.WhenAny(pending);
                    pending.Remove(completed);
                    if (completed == exitTask)
                    {
                        await exitTask;
                        break;
                    }

                    await completed;
                }

                await exitTask;
                await inputTask;
            }
            catch (ImageCasProbeException)
            {
                TryKill(process);
                throw;
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

            ValidateResult(result, objectKind, objectByteLength);
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

    private static async Task WriteRequestAsync(
        Stream stream,
        byte[] request,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(request, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Close();
    }

    internal static void ValidateResult(
        ImageProbeCasImageResult result,
        string objectKind,
        long objectByteLength)
    {
        if (result is null)
        {
            throw new ImageCasProbeException(
                "cas_image_probe_response_invalid",
                "The isolated CAS image probe returned an invalid response.");
        }

        var valid = objectByteLength is > 0 and <= ImageProbeProtocol.MaximumCasObjectBytes &&
                    result.Frames is not null &&
                    result.ReasonCodes is not null &&
                    result.Parser is not null &&
                    result.Privacy is not null &&
                    result.SchemaVersion == ImageProbeProtocol.CasImageV1 &&
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
                    !result.Privacy.OwnerSampleStatisticsIncluded &&
                    result.ReasonCodes.All(reason =>
                        reason is not null && KnownProbeReasonCodes.Contains(reason)) &&
                    result.ReasonCodes.Distinct(StringComparer.Ordinal).Count() == result.ReasonCodes.Count;
        if (!valid || !ValidateFrames(result, objectByteLength))
        {
            throw new ImageCasProbeException(
                "cas_image_probe_response_invalid",
                "The isolated CAS image probe returned an invalid response.");
        }
    }

    private static bool ValidateFrames(ImageProbeCasImageResult result, long objectByteLength)
    {
        if (result.Status == "blocked")
        {
            return result.Frames.Count == 0 &&
                   result.ReasonCodes.Count > 0 &&
                   result.StructureState == "blocked" &&
                   result.DecodeState == "not_decoded";
        }

        if (result.Status != "completed" ||
            result.Frames.Count == 0 ||
            result.ReasonCodes.Count != 0 ||
            result.StructureState != "validated" ||
            result.DecodeState != "decoded" ||
            result.Container is not ("jpeg" or "mpo" or "tiff"))
        {
            return false;
        }

        try
        {
            long totalPixels = 0;
            for (var index = 0; index < result.Frames.Count; index++)
            {
                var frame = result.Frames[index];
                if (frame is null ||
                    frame.FrameIndex != index ||
                    frame.FrameKind is not ("jpeg" or "mp_primary_image" or "mp_auxiliary_image" or "tiff_page") ||
                    frame.ByteOffset < 0 || frame.ByteLength < 0 ||
                    frame.Width <= 0 || frame.Height <= 0 || frame.BitsPerChannel is <= 0 or > 64 ||
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

            if (totalPixels > ImageProbeProtocol.MaximumCasTotalPixels)
            {
                return false;
            }

            return result.Container switch
            {
                "jpeg" => ValidateJpegFrames(result.Frames, objectByteLength),
                "mpo" => ValidateMpoFrames(result.Frames, objectByteLength),
                "tiff" => ValidateTiffFrames(result.Frames, objectByteLength),
                _ => false
            };
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool ValidateJpegFrames(
        IReadOnlyList<ImageProbeCasImageFrame> frames,
        long objectByteLength) =>
        frames.Count == 1 &&
        frames[0].FrameKind == "jpeg" &&
        frames[0].ByteOffset == 0 &&
        frames[0].ByteLength == objectByteLength &&
        frames[0].Orientation is null;

    private static bool ValidateMpoFrames(
        IReadOnlyList<ImageProbeCasImageFrame> frames,
        long objectByteLength)
    {
        if (frames.Count < 2 ||
            frames[0].FrameKind != "mp_primary_image" ||
            frames[0].ByteOffset != 0 ||
            frames.Any(frame => frame.ByteLength <= 0 || frame.Orientation is not null) ||
            frames.Skip(1).Any(frame => frame.FrameKind != "mp_auxiliary_image"))
        {
            return false;
        }

        var ranges = frames
            .Select(frame => (frame.ByteOffset, End: checked(frame.ByteOffset + frame.ByteLength)))
            .OrderBy(range => range.ByteOffset)
            .ToArray();
        if (ranges[0].ByteOffset != 0 || ranges[^1].End != objectByteLength ||
            ranges.Any(range => range.End > objectByteLength))
        {
            return false;
        }

        for (var index = 1; index < ranges.Length; index++)
        {
            if (ranges[index].ByteOffset < ranges[index - 1].End)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateTiffFrames(
        IReadOnlyList<ImageProbeCasImageFrame> frames,
        long objectByteLength) =>
        frames.All(frame =>
            frame.FrameKind == "tiff_page" &&
            frame.ByteLength == 0 &&
            frame.ByteOffset >= 0 &&
            frame.ByteOffset < objectByteLength &&
            frame.Orientation is >= 1 and <= 8);

    private static string ClassifyChildFailure(ReadOnlySpan<byte> output, ReadOnlySpan<byte> error)
    {
        try
        {
            var result = JsonSerializer.Deserialize<ImageProbeCasImageResult>(output, SerializerOptions);
            var reason = result?.ReasonCodes.FirstOrDefault();
            if (result?.Status == "failed" &&
                IsSafeCode(reason) &&
                KnownProbeReasonCodes.Contains(reason!))
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
