using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace QiongTu.Control;

internal static class HardwareProbeChildProtocol
{
    public const string Argument = "--hardware-probe-child";
    public const string SchemaVersion = "qiongtu.nvidia-probe.v1";
    public const int MaximumOutputCharacters = 32 * 1024;
}

internal sealed record NvidiaProbeGpu(
    int DeviceIndex,
    string Name,
    long? TotalMemoryBytes,
    long? FreeMemoryBytes,
    string Status);

internal sealed record NvidiaProbeResult(
    string SchemaVersion,
    string Status,
    string CudaStatus,
    string? DriverVersion,
    int? CudaDriverApiVersion,
    IReadOnlyList<NvidiaProbeGpu> Gpus,
    string? ReasonCode)
{
    public static NvidiaProbeResult Unknown(string reasonCode) => new(
        HardwareProbeChildProtocol.SchemaVersion,
        "unknown",
        "unknown",
        null,
        null,
        [],
        reasonCode);
}

internal interface INvidiaProbeClient
{
    Task<NvidiaProbeResult> CaptureAsync(CancellationToken cancellationToken);
}

internal sealed class IsolatedNvidiaProbeClient : INvidiaProbeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeSpan _timeout;
    private readonly Func<ProcessStartInfo> _startInfoFactory;

    public IsolatedNvidiaProbeClient(
        TimeSpan? timeout = null,
        Func<ProcessStartInfo>? startInfoFactory = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _startInfoFactory = startInfoFactory ?? CreateSelfProbeStartInfo;
    }

    public async Task<NvidiaProbeResult> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = _startInfoFactory() };
        try
        {
            if (!process.Start())
            {
                return NvidiaProbeResult.Unknown("nvidia_probe_start_failed");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return NvidiaProbeResult.Unknown("nvidia_probe_start_failed");
        }

        var stdoutTask = ReadBoundedAsync(
            process.StandardOutput,
            HardwareProbeChildProtocol.MaximumOutputCharacters,
            cancellationToken);
        var stderrTask = ReadBoundedAsync(process.StandardError, 4 * 1024, cancellationToken);
        var exitTask = process.WaitForExitAsync(cancellationToken);
        var allTasks = Task.WhenAll(exitTask, stdoutTask, stderrTask);
        var timeoutTask = Task.Delay(_timeout, cancellationToken);
        var completed = await Task.WhenAny(allTasks, timeoutTask);
        if (completed != allTasks)
        {
            TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();
            await ObserveAsync(allTasks);
            return NvidiaProbeResult.Unknown("nvidia_probe_timeout");
        }

        try
        {
            await allTasks;
        }
        catch (InvalidDataException)
        {
            TryKill(process);
            return NvidiaProbeResult.Unknown("nvidia_probe_output_too_large");
        }

        if (process.ExitCode != 0)
        {
            return NvidiaProbeResult.Unknown("nvidia_probe_child_failed");
        }

        try
        {
            var report = JsonSerializer.Deserialize<NvidiaProbeResult>(stdoutTask.Result, JsonOptions);
            return Validate(report)
                ? report!
                : NvidiaProbeResult.Unknown("nvidia_probe_response_invalid");
        }
        catch (JsonException)
        {
            return NvidiaProbeResult.Unknown("nvidia_probe_response_invalid");
        }
    }

    private static ProcessStartInfo CreateSelfProbeStartInfo()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("The current control executable path is unavailable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(processPath),
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            var entryAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
            if (string.IsNullOrWhiteSpace(entryAssemblyName))
            {
                throw new InvalidOperationException("The framework-dependent control assembly path is unavailable.");
            }

            var entryAssemblyPath = Path.Combine(AppContext.BaseDirectory, $"{entryAssemblyName}.dll");
            if (!File.Exists(entryAssemblyPath))
            {
                throw new InvalidOperationException("The framework-dependent control assembly file is unavailable.");
            }

            startInfo.ArgumentList.Add(Path.GetFullPath(entryAssemblyPath));
        }

        startInfo.ArgumentList.Add(HardwareProbeChildProtocol.Argument);
        return startInfo;
    }

    private static async Task<string> ReadBoundedAsync(
        TextReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 4 * 1024));
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return builder.ToString();
            }

            if (builder.Length + read > maximumCharacters)
            {
                throw new InvalidDataException("The hardware probe output exceeded its bound.");
            }

            builder.Append(buffer, 0, read);
        }
    }

    private static bool Validate(NvidiaProbeResult? report) =>
        report is not null &&
        report.SchemaVersion == HardwareProbeChildProtocol.SchemaVersion &&
        report.Status is "present" or "missing" or "unknown" &&
        report.CudaStatus is "present" or "missing" or "unknown" &&
        report.Gpus.Count <= NvidiaNativeProbe.MaximumGpuCount &&
        report.Gpus.All(gpu =>
            gpu.DeviceIndex >= 0 &&
            gpu.Name.Length is > 0 and <= NvidiaNativeProbe.MaximumGpuNameLength &&
            gpu.Status is "present" or "unknown" &&
            (gpu.TotalMemoryBytes is null or >= 0) &&
            (gpu.FreeMemoryBytes is null or >= 0));

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or InvalidDataException)
        {
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
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}

internal static class NvidiaNativeProbe
{
    public const int MaximumGpuCount = 16;
    public const int MaximumGpuNameLength = 128;
    private const int Success = 0;
    private const int NvmlDriverNotLoaded = 9;
    private const int CudaNoDevice = 100;

    public static NvidiaProbeResult Capture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NvidiaProbeResult(
                HardwareProbeChildProtocol.SchemaVersion,
                "missing",
                "missing",
                null,
                null,
                [],
                "windows_nvidia_driver_unavailable");
        }

        var cuda = CaptureCudaDriver();
        var nvmlPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "nvml.dll");
        if (!NativeLibrary.TryLoad(nvmlPath, out var nvmlLibrary))
        {
            return new NvidiaProbeResult(
                HardwareProbeChildProtocol.SchemaVersion,
                cuda.Status == "present" ? "unknown" : "missing",
                cuda.Status,
                null,
                cuda.DriverApiVersion,
                [],
                cuda.Status == "present" ? "nvml_unavailable" : "nvidia_driver_libraries_missing");
        }

        try
        {
            return CaptureNvml(nvmlLibrary, cuda);
        }
        catch (Exception exception) when (exception is not AccessViolationException)
        {
            return new NvidiaProbeResult(
                HardwareProbeChildProtocol.SchemaVersion,
                "unknown",
                cuda.Status,
                null,
                cuda.DriverApiVersion,
                [],
                "nvml_probe_failed");
        }
        finally
        {
            NativeLibrary.Free(nvmlLibrary);
        }
    }

    private static NvidiaProbeResult CaptureNvml(IntPtr library, CudaDriverSnapshot cuda)
    {
        if (!TryGetDelegate(library, ["nvmlInit_v2", "nvmlInit"], out NvmlInit initialize) ||
            !TryGetDelegate(library, ["nvmlShutdown"], out NvmlShutdown shutdown) ||
            !TryGetDelegate(library, ["nvmlDeviceGetCount_v2", "nvmlDeviceGetCount"], out NvmlDeviceGetCount getCount) ||
            !TryGetDelegate(library, ["nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetHandleByIndex"], out NvmlDeviceGetHandle getHandle) ||
            !TryGetDelegate(library, ["nvmlDeviceGetName"], out NvmlDeviceGetName getName) ||
            !TryGetDelegate(library, ["nvmlDeviceGetMemoryInfo"], out NvmlDeviceGetMemoryInfo getMemory) ||
            !TryGetDelegate(library, ["nvmlSystemGetDriverVersion"], out NvmlSystemGetDriverVersion getDriverVersion))
        {
            return new NvidiaProbeResult(
                HardwareProbeChildProtocol.SchemaVersion,
                "unknown",
                cuda.Status,
                null,
                cuda.DriverApiVersion,
                [],
                "nvml_exports_missing");
        }

        var initializeResult = initialize();
        if (initializeResult != Success)
        {
            return new NvidiaProbeResult(
                HardwareProbeChildProtocol.SchemaVersion,
                initializeResult == NvmlDriverNotLoaded ? "missing" : "unknown",
                cuda.Status,
                null,
                cuda.DriverApiVersion,
                [],
                initializeResult == NvmlDriverNotLoaded ? "nvidia_driver_not_loaded" : "nvml_initialize_failed");
        }

        try
        {
            var driverBuffer = new StringBuilder(96);
            var driverVersion = getDriverVersion(driverBuffer, (uint)driverBuffer.Capacity) == Success
                ? Sanitize(driverBuffer.ToString(), 64)
                : null;
            uint count = 0;
            if (getCount(ref count) != Success)
            {
                return new NvidiaProbeResult(
                    HardwareProbeChildProtocol.SchemaVersion,
                    "unknown",
                    cuda.Status,
                    driverVersion,
                    cuda.DriverApiVersion,
                    [],
                    "nvml_device_count_failed");
            }

            if (count == 0)
            {
                return new NvidiaProbeResult(
                    HardwareProbeChildProtocol.SchemaVersion,
                    "missing",
                    cuda.Status,
                    driverVersion,
                    cuda.DriverApiVersion,
                    [],
                    "nvidia_gpu_not_detected");
            }

            var gpus = new List<NvidiaProbeGpu>();
            var boundedCount = (int)Math.Min(count, MaximumGpuCount);
            for (var index = 0; index < boundedCount; index++)
            {
                if (getHandle((uint)index, out var device) != Success)
                {
                    gpus.Add(new NvidiaProbeGpu(index, $"NVIDIA GPU {index}", null, null, "unknown"));
                    continue;
                }

                var nameBuffer = new StringBuilder(MaximumGpuNameLength + 1);
                var name = getName(device, nameBuffer, (uint)nameBuffer.Capacity) == Success
                    ? Sanitize(nameBuffer.ToString(), MaximumGpuNameLength)
                    : $"NVIDIA GPU {index}";
                var memory = new NvmlMemory();
                var memoryStatus = getMemory(device, ref memory) == Success;
                gpus.Add(new NvidiaProbeGpu(
                    index,
                    name,
                    memoryStatus ? CheckedLong(memory.Total) : null,
                    memoryStatus ? CheckedLong(memory.Free) : null,
                    memoryStatus ? "present" : "unknown"));
            }

            return new NvidiaProbeResult(
                HardwareProbeChildProtocol.SchemaVersion,
                "present",
                cuda.Status,
                driverVersion,
                cuda.DriverApiVersion,
                gpus,
                count > MaximumGpuCount ? "gpu_list_truncated" : cuda.ReasonCode);
        }
        finally
        {
            _ = shutdown();
        }
    }

    private static CudaDriverSnapshot CaptureCudaDriver()
    {
        var cudaPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "nvcuda.dll");
        if (!NativeLibrary.TryLoad(cudaPath, out var cudaLibrary))
        {
            return new CudaDriverSnapshot("missing", null, "cuda_driver_library_missing");
        }

        try
        {
            if (!TryGetDelegate(cudaLibrary, ["cuInit"], out CudaInitialize initialize) ||
                !TryGetDelegate(cudaLibrary, ["cuDriverGetVersion"], out CudaDriverGetVersion getVersion) ||
                !TryGetDelegate(cudaLibrary, ["cuDeviceGetCount"], out CudaDeviceGetCount getCount))
            {
                return new CudaDriverSnapshot("unknown", null, "cuda_driver_exports_missing");
            }

            var initializeResult = initialize(0);
            if (initializeResult == CudaNoDevice)
            {
                return new CudaDriverSnapshot("missing", null, "cuda_device_not_detected");
            }

            if (initializeResult != Success)
            {
                return new CudaDriverSnapshot("unknown", null, "cuda_driver_initialize_failed");
            }

            if (getVersion(out var version) != Success || getCount(out var count) != Success)
            {
                return new CudaDriverSnapshot("unknown", null, "cuda_driver_query_failed");
            }

            return count > 0
                ? new CudaDriverSnapshot("present", version, null)
                : new CudaDriverSnapshot("missing", version, "cuda_device_not_detected");
        }
        catch (Exception exception) when (exception is not AccessViolationException)
        {
            return new CudaDriverSnapshot("unknown", null, "cuda_driver_probe_failed");
        }
        finally
        {
            NativeLibrary.Free(cudaLibrary);
        }
    }

    private static bool TryGetDelegate<T>(
        IntPtr library,
        IReadOnlyList<string> names,
        out T function)
        where T : Delegate
    {
        foreach (var name in names)
        {
            if (NativeLibrary.TryGetExport(library, name, out var address))
            {
                function = Marshal.GetDelegateForFunctionPointer<T>(address);
                return true;
            }
        }

        function = null!;
        return false;
    }

    private static string Sanitize(string value, int maximumLength)
    {
        var normalized = new string(value
            .Where(character => !char.IsControl(character))
            .ToArray())
            .Trim();
        if (normalized.Length == 0)
        {
            return "NVIDIA GPU";
        }

        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static long CheckedLong(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    private sealed record CudaDriverSnapshot(string Status, int? DriverApiVersion, string? ReasonCode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlInit();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlShutdown();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetCount(ref uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetHandle(uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetMemoryInfo(IntPtr device, ref NvmlMemory memory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlSystemGetDriverVersion(StringBuilder version, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CudaInitialize(uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CudaDriverGetVersion(out int driverVersion);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CudaDeviceGetCount(out int count);
}
