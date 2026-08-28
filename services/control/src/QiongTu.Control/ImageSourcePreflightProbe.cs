using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using QiongTu.Contracts;

namespace QiongTu.Control;

internal sealed class ImageSourcePreflightProbeException : IOException
{
    public ImageSourcePreflightProbeException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed record ImageSourcePreflightProbeOptions(
    TimeSpan? Timeout = null,
    int MaximumPayloadBytes = ImageProbeProtocol.MaximumPayloadBytes,
    int MaximumOutputBytes = ImageProbeProtocol.MaximumOutputBytes,
    int MaximumErrorBytes = 8 * 1024,
    long MaximumProcessMemoryBytes = 256L * 1024 * 1024)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(15);
}

internal interface IImageSourcePreflightProbeClient
{
    Task<ImageProbeSourcePreflightResult> AnalyzeAsync(
        Stream source,
        string candidateKind,
        string? formatHint,
        int? associationItemCount,
        CancellationToken cancellationToken);
}

internal sealed class IsolatedImageSourcePreflightProbeClient : IImageSourcePreflightProbeClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<ProcessStartInfo> _startInfoFactory;
    private readonly ImageSourcePreflightProbeOptions _options;

    public IsolatedImageSourcePreflightProbeClient(
        ImageSourcePreflightProbeOptions? options = null,
        Func<ProcessStartInfo>? startInfoFactory = null)
    {
        _options = options ?? new ImageSourcePreflightProbeOptions();
        ValidateOptions(_options);
        _startInfoFactory = startInfoFactory ?? CreateProductStartInfo;
    }

    public async Task<ImageProbeSourcePreflightResult> AnalyzeAsync(
        Stream source,
        string candidateKind,
        string? formatHint,
        int? associationItemCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The image source must be readable.", nameof(source));
        }

        var boundedInput = await ReadBoundedPrefixAsync(
            source,
            _options.MaximumPayloadBytes,
            cancellationToken);
        if (boundedInput.Payload.Length == 0)
        {
            throw new ImageSourcePreflightProbeException(
                "image_probe_empty_input",
                "The image source preflight input is empty.");
        }

        var header = new ImageProbeRequestHeader(
            ImageProbeProtocol.SourcePreflightV1,
            ImageProbeProtocol.SourcePreflightProfile,
            candidateKind,
            formatHint,
            associationItemCount,
            boundedInput.Payload.Length);
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, SerializerOptions);
        if (headerBytes.Length == 0 || headerBytes.Length > ImageProbeProtocol.MaximumHeaderBytes)
        {
            throw new ImageSourcePreflightProbeException(
                "image_probe_header_limit_exceeded",
                "The image source preflight request header exceeds its protocol limit.");
        }

        using var process = new Process { StartInfo = PrepareStartInfo(_startInfoFactory()) };
        try
        {
            if (!process.Start())
            {
                throw new ImageSourcePreflightProbeException(
                    "image_probe_start_failed",
                    "The isolated image source preflight process could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception
            or InvalidOperationException
            or IOException)
        {
            throw new ImageSourcePreflightProbeException(
                "image_probe_start_failed",
                "The isolated image source preflight process could not be started.",
                exception);
        }

        using var job = WindowsChildProcessJob.TryCreateAndAssign(
            process,
            _options.MaximumProcessMemoryBytes);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.EffectiveTimeout);
        var outputTask = ReadBoundedAsync(
            process.StandardOutput.BaseStream,
            _options.MaximumOutputBytes,
            "image_probe_output_limit_exceeded",
            timeout.Token);
        var errorTask = ReadBoundedAsync(
            process.StandardError.BaseStream,
            _options.MaximumErrorBytes,
            "image_probe_error_limit_exceeded",
            timeout.Token);

        try
        {
            await process.StandardInput.BaseStream.WriteAsync(headerBytes, timeout.Token);
            await process.StandardInput.BaseStream.WriteAsync(new byte[] { (byte)'\n' }, timeout.Token);
            await process.StandardInput.BaseStream.WriteAsync(boundedInput.Payload, timeout.Token);
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
            throw new ImageSourcePreflightProbeException(
                "image_probe_timeout",
                "The isolated image source preflight process exceeded its time limit.");
        }
        catch (IOException exception) when (process.HasExited)
        {
            var earlyOutput = await AwaitBoundedAfterChildExitAsync(outputTask);
            var earlyError = await AwaitBoundedAfterChildExitAsync(errorTask);
            var failureCode = ClassifyChildFailure(earlyOutput, earlyError);
            throw new ImageSourcePreflightProbeException(
                failureCode,
                $"The isolated image source preflight process closed its input early ({failureCode}).",
                exception);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            TryKill(process);
            throw new ImageSourcePreflightProbeException(
                "image_probe_transport_failed",
                "The isolated image source preflight process transport failed.",
                exception);
        }

        byte[] output;
        byte[] error;
        try
        {
            output = await outputTask;
            error = await errorTask;
        }
        catch (ImageSourcePreflightProbeException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var failureCode = ClassifyChildFailure(output, error);
            throw new ImageSourcePreflightProbeException(
                failureCode,
                $"The isolated image source preflight process returned a failure status ({failureCode}).");
        }

        ImageProbeSourcePreflightResult result;
        try
        {
            result = JsonSerializer.Deserialize<ImageProbeSourcePreflightResult>(output, SerializerOptions)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new ImageSourcePreflightProbeException(
                "image_probe_response_invalid",
                "The isolated image source preflight process returned an invalid response.",
                exception);
        }

        ValidateResult(result, candidateKind);
        return boundedInput.LimitExceeded
            ? ApplyInputLimit(result)
            : result;
    }

    internal static ProcessStartInfo CreateProductStartInfo()
    {
        var executablePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "image-probe",
            "QiongTu.ImageProbe.exe"));
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath
        };
        startInfo.ArgumentList.Add(ImageProbeProtocol.StdioArgument);
        return startInfo;
    }

    private static ProcessStartInfo PrepareStartInfo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (string.IsNullOrWhiteSpace(startInfo.FileName))
        {
            throw new ImageSourcePreflightProbeException(
                "image_probe_path_missing",
                "The fixed image source preflight executable path is unavailable.");
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        var utf8WithoutBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        startInfo.StandardInputEncoding = utf8WithoutBom;
        startInfo.StandardOutputEncoding = utf8WithoutBom;
        startInfo.StandardErrorEncoding = utf8WithoutBom;
        startInfo.Environment.Clear();
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        startInfo.Environment["DOTNET_GCHeapHardLimit"] = "0x10000000";
        return startInfo;
    }

    private static async Task<BoundedProbeInput> ReadBoundedPrefixAsync(
        Stream source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maximumBytes, 128 * 1024));
        try
        {
            using var output = new MemoryStream(Math.Min(maximumBytes, 1024 * 1024));
            while (output.Length < maximumBytes)
            {
                var remaining = maximumBytes - (int)output.Length;
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            var limitExceeded = output.Length == maximumBytes &&
                                await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0;
            return new BoundedProbeInput(output.ToArray(), limitExceeded);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ImageProbeSourcePreflightResult ApplyInputLimit(
        ImageProbeSourcePreflightResult result)
    {
        var reasons = result.ReasonCodes
            .Append("evidence_read_limit_exceeded")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(ImageProbeProtocol.MaximumReasonCodes)
            .ToArray();
        return result.EvidenceState == "supports_dji"
            ? result with { EvidenceState = "unconfirmed", ReasonCodes = reasons }
            : result with { ReasonCodes = reasons };
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        string limitCode,
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
                throw new ImageSourcePreflightProbeException(
                    limitCode,
                    "The isolated image source preflight process exceeded an output limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void ValidateResult(ImageProbeSourcePreflightResult result, string candidateKind)
    {
        if (!string.Equals(result.SchemaVersion, ImageProbeProtocol.SourcePreflightV1, StringComparison.Ordinal) ||
            !string.Equals(result.Profile, ImageProbeProtocol.SourcePreflightProfile, StringComparison.Ordinal) ||
            !string.Equals(result.CandidateKind, candidateKind, StringComparison.Ordinal) ||
            result.Status != "completed" ||
            result.Parser.ProductParser != "qiongtu.source-preflight" ||
            result.Parser.ProductParserVersion != "1.0.0" ||
            !IsExpectedMetadataExtractorVersion(result.Parser.MetadataExtractorVersion) ||
            result.EvidenceState is not ("supports_dji" or "out_of_scope" or "unconfirmed" or "conflict") ||
            result.EvidenceKinds.Count > ImageProbeProtocol.MaximumEvidenceKinds ||
            result.ReasonCodes.Count > ImageProbeProtocol.MaximumReasonCodes ||
            result.Privacy.PathsIncluded ||
            result.Privacy.LocatorsIncluded ||
            result.Privacy.ContentHashesIncluded ||
            result.Privacy.ObjectKeysIncluded ||
            result.Privacy.RawMetadataIncluded ||
            result.Privacy.SerialNumbersIncluded ||
            result.Privacy.CoordinatesIncluded ||
            result.Privacy.OwnerSampleStatisticsIncluded)
        {
            throw new ImageSourcePreflightProbeException(
                "image_probe_response_invalid",
                "The isolated image source preflight process returned an invalid response.");
        }
    }

    private static bool IsExpectedMetadataExtractorVersion(string version) =>
        version == "2.9.3" || version.StartsWith("2.9.3+", StringComparison.Ordinal);

    private sealed record BoundedProbeInput(byte[] Payload, bool LimitExceeded);

    private static async Task<byte[]> AwaitBoundedAfterChildExitAsync(Task<byte[]> task)
    {
        try
        {
            return await task;
        }
        catch (Exception exception) when (exception is IOException
            or OperationCanceledException
            or ImageSourcePreflightProbeException)
        {
            return [];
        }
    }

    private static string ClassifyChildFailure(ReadOnlySpan<byte> output, ReadOnlySpan<byte> error)
    {
        try
        {
            var result = JsonSerializer.Deserialize<ImageProbeSourcePreflightResult>(output, SerializerOptions);
            var reason = result?.ReasonCodes.FirstOrDefault();
            if (result?.Status == "failed" && IsSafeCode(reason))
            {
                return "image_probe_child_" + reason;
            }
        }
        catch (JsonException)
        {
            // Fall through to the bounded stderr category. Raw child output is never returned.
        }

        var errorText = System.Text.Encoding.UTF8.GetString(error);
        if (errorText.Contains("hostpolicy.dll", StringComparison.OrdinalIgnoreCase))
        {
            return "image_probe_runtime_dependency_missing";
        }

        if (errorText.Contains("Out of memory", StringComparison.OrdinalIgnoreCase) ||
            errorText.Contains("GC heap", StringComparison.OrdinalIgnoreCase))
        {
            return "image_probe_memory_limit_exceeded";
        }

        if (errorText.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase) ||
            errorText.Contains("Fatal error", StringComparison.OrdinalIgnoreCase))
        {
            return "image_probe_process_crashed";
        }

        return error.IsEmpty
            ? "image_probe_process_failed"
            : "image_probe_process_stderr";
    }

    private static bool IsSafeCode(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static void ValidateOptions(ImageSourcePreflightProbeOptions options)
    {
        if (options.EffectiveTimeout <= TimeSpan.Zero ||
            options.MaximumPayloadBytes is <= 0 or > ImageProbeProtocol.MaximumPayloadBytes ||
            options.MaximumOutputBytes is <= 0 or > ImageProbeProtocol.MaximumOutputBytes ||
            options.MaximumErrorBytes <= 0 ||
            options.MaximumProcessMemoryBytes < 64L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Image source preflight limits are invalid.");
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
        catch (Exception exception) when (exception is InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
            // Best-effort cleanup. The Windows job closes immediately after this scope.
        }
    }
}

internal sealed class ImageSourcePreflightProbe
{
    private readonly ImageImportSourceDiscovery _sourceDiscovery;
    private readonly IImageSourcePreflightProbeClient _probeClient;

    public ImageSourcePreflightProbe(
        ImageImportSourceDiscovery sourceDiscovery,
        IImageSourcePreflightProbeClient? probeClient = null)
    {
        _sourceDiscovery = sourceDiscovery ?? throw new ArgumentNullException(nameof(sourceDiscovery));
        _probeClient = probeClient ?? new IsolatedImageSourcePreflightProbeClient();
    }

    public Task<ImageProbeSourcePreflightResult> AnalyzeAsync(
        ImageImportSourceRecoveryManifest manifest,
        string sourceItemKey,
        ImageImportSourceSnapshot expectedSnapshot,
        string candidateKind,
        string? formatHint,
        int? associationItemCount = null,
        CancellationToken cancellationToken = default) =>
        _sourceDiscovery.ReadSourceItemAsync(
            manifest,
            sourceItemKey,
            expectedSnapshot,
            (stream, token) => _probeClient.AnalyzeAsync(
                stream,
                candidateKind,
                formatHint,
                associationItemCount,
                token),
            cancellationToken);
}

internal sealed class WindowsChildProcessJob : IDisposable
{
    private readonly SafeJobHandle? _handle;

    private WindowsChildProcessJob(SafeJobHandle? handle)
    {
        _handle = handle;
    }

    public static WindowsChildProcessJob TryCreateAndAssign(Process process, long maximumProcessMemoryBytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsChildProcessJob(null);
        }

        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new ImageSourcePreflightProbeException(
                "image_probe_job_failed",
                "The isolated image source preflight process could not be constrained.",
                new Win32Exception(error));
        }

        try
        {
            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose |
                                 JobObjectLimitProcessMemory |
                                 JobObjectLimitActiveProcess,
                    ActiveProcessLimit = 1
                },
                ProcessMemoryLimit = checked((nuint)maximumProcessMemoryBytes)
            };
            if (!SetInformationJobObject(
                    handle,
                    JobObjectExtendedLimitInformationClass,
                    ref information,
                    Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return new WindowsChildProcessJob(handle);
        }
        catch (Win32Exception exception)
        {
            handle.Dispose();
            TryTerminate(process);
            throw new ImageSourcePreflightProbeException(
                "image_probe_job_failed",
                "The isolated image source preflight process could not be constrained.",
                exception);
        }
    }

    public void Dispose() => _handle?.Dispose();

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or Win32Exception
            or NotSupportedException)
        {
            // Best effort before reporting the inability to establish a job boundary.
        }
    }

    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int jobObjectInformationClass,
        ref JobObjectExtendedLimitInformation information,
        int informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
