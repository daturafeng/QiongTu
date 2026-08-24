using System.Diagnostics;
using QiongTu.Contracts;

namespace QiongTu.Control;

internal interface IWorkerAdmissionGate
{
    Task<WorkerAdmissionResult> CheckAsync(
        WorkerDefinition definition,
        CancellationToken cancellationToken);
}

internal sealed class ProcessingCapabilityService : IWorkerAdmissionGate
{
    public const string SchemaVersion = "qiongtu.processing-capability.v1";
    public const string RequirementsVersion = "qiongtu.worker-requirements.v1";

    private readonly WorkerRegistry _registry;
    private readonly ControlDataPaths _paths;
    private readonly IHostResourceProbe _hostProbe;
    private readonly INvidiaProbeClient _nvidiaProbe;

    public ProcessingCapabilityService(
        WorkerRegistry registry,
        ControlDataPaths paths,
        IHostResourceProbe? hostProbe = null,
        INvidiaProbeClient? nvidiaProbe = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _hostProbe = hostProbe ?? new WindowsHostResourceProbe();
        _nvidiaProbe = nvidiaProbe ?? new IsolatedNvidiaProbeClient();
    }

    public async Task<ProcessingCapabilityReport> CaptureAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var host = _hostProbe.CaptureHost();
        var cpu = _hostProbe.CaptureCpu();
        var memory = _hostProbe.CaptureMemory();
        var storage = new[]
        {
            _hostProbe.CaptureStorage("runtime", _paths.RuntimeDirectory),
            _hostProbe.CaptureStorage("state", _paths.StateDirectory),
            _hostProbe.CaptureStorage("objects", _paths.ObjectDirectory),
            _hostProbe.CaptureStorage("logs", _paths.LogDirectory)
        };
        var nvidia = ToContract(await CaptureNvidiaSafelyAsync(cancellationToken));
        var admissions = _registry.List()
            .Select(definition => CheckCore(
                definition,
                cpu,
                memory,
                _hostProbe.CaptureStorage("worker", definition.WorkingDirectory),
                nvidia))
            .ToArray();
        stopwatch.Stop();

        return new ProcessingCapabilityReport(
            SchemaVersion,
            RequirementsVersion,
            DateTimeOffset.UtcNow,
            (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue),
            host,
            cpu,
            memory,
            storage,
            nvidia,
            admissions,
            new CapabilityPrivacy(
                PathsIncluded: false,
                TokensIncluded: false,
                UserNameIncluded: false,
                MachineNameIncluded: false,
                EnvironmentIncluded: false,
                CommandLineIncluded: false));
    }

    public async Task<WorkerAdmissionResult> CheckAsync(
        string workerType,
        CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(workerType, out var definition))
        {
            throw new ControlProtocolException(
                "worker_not_registered",
                "The requested worker type is not registered.");
        }

        return await CheckAsync(definition, cancellationToken);
    }

    public async Task<WorkerAdmissionResult> CheckAsync(
        WorkerDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.ResourceRequirements is null)
        {
            return new WorkerAdmissionResult(definition.WorkerType, "default", "allowed", []);
        }

        var cpu = _hostProbe.CaptureCpu();
        var memory = _hostProbe.CaptureMemory();
        var storage = _hostProbe.CaptureStorage("worker", definition.WorkingDirectory);
        var nvidia = definition.ResourceRequirements.RequiresNvidia
            ? ToContract(await CaptureNvidiaSafelyAsync(cancellationToken))
            : new NvidiaCapability("missing", "missing", null, null, "not_required", []);
        return CheckCore(definition, cpu, memory, storage, nvidia);
    }

    private async Task<NvidiaProbeResult> CaptureNvidiaSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _nvidiaProbe.CaptureAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidOperationException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return NvidiaProbeResult.Unknown("nvidia_probe_unavailable");
        }
    }

    private static WorkerAdmissionResult CheckCore(
        WorkerDefinition definition,
        CpuCapability cpu,
        MemoryCapability memory,
        StorageCapability storage,
        NvidiaCapability nvidia)
    {
        var requirements = definition.ResourceRequirements;
        var profile = requirements?.Profile ?? "default";
        if (requirements is null)
        {
            return new WorkerAdmissionResult(definition.WorkerType, profile, "allowed", []);
        }

        var reasons = new List<WorkerAdmissionBlockingReason>();
        CheckMinimum(
            reasons,
            "cpu_probe_unknown",
            "logical_processors_insufficient",
            cpu.Status,
            cpu.LogicalProcessorCount,
            requirements.MinimumLogicalProcessors,
            "logical processors");
        CheckMinimum(
            reasons,
            "memory_probe_unknown",
            "available_memory_insufficient",
            memory.Status,
            memory.AvailableBytes,
            requirements.MinimumAvailableMemoryBytes,
            "available memory bytes");
        CheckMinimum(
            reasons,
            "storage_probe_unknown",
            "available_storage_insufficient",
            storage.Status,
            storage.AvailableBytes,
            requirements.MinimumAvailableDiskBytes,
            "available worker-volume bytes");

        if (requirements.RequiresNvidia)
        {
            CheckNvidia(reasons, requirements, nvidia);
        }

        var decision = reasons.Any(reason => reason.Category is "missing" or "incompatible" or "insufficient")
            ? "denied"
            : reasons.Any(reason => reason.Category == "unknown")
                ? "unknown"
                : "allowed";
        return new WorkerAdmissionResult(definition.WorkerType, profile, decision, reasons);
    }

    private static void CheckNvidia(
        ICollection<WorkerAdmissionBlockingReason> reasons,
        WorkerResourceRequirements requirements,
        NvidiaCapability nvidia)
    {
        if (nvidia.Status == "missing")
        {
            reasons.Add(Reason("missing", "nvidia_gpu_missing", "A compatible NVIDIA GPU was not detected."));
            return;
        }

        if (nvidia.Status == "unknown")
        {
            reasons.Add(Reason("unknown", "nvidia_probe_unknown", "NVIDIA GPU capability could not be determined."));
        }

        if (requirements.MinimumCudaDriverApiVersion is not null && nvidia.CudaStatus == "missing")
        {
            reasons.Add(Reason("missing", "cuda_driver_api_missing", "The NVIDIA CUDA Driver API is unavailable."));
        }
        else if (requirements.MinimumCudaDriverApiVersion is not null && nvidia.CudaStatus == "unknown")
        {
            reasons.Add(Reason("unknown", "cuda_probe_unknown", "CUDA Driver API compatibility could not be determined."));
        }

        if (requirements.MinimumCudaDriverApiVersion is not null && nvidia.CudaStatus == "present")
        {
            var actual = ParseCudaVersion(nvidia.CudaDriverApiVersion);
            if (actual is null)
            {
                reasons.Add(Reason("unknown", "cuda_version_unknown", "The CUDA Driver API version could not be read."));
            }
            else if (actual < requirements.MinimumCudaDriverApiVersion)
            {
                reasons.Add(Reason(
                    "incompatible",
                    "cuda_driver_api_incompatible",
                    $"CUDA Driver API {FormatCudaVersion(requirements.MinimumCudaDriverApiVersion.Value)} or newer is required; {FormatCudaVersion(actual.Value)} is available."));
            }
        }

        CheckGpuMemory(reasons, requirements, nvidia.Gpus);
    }

    private static void CheckGpuMemory(
        ICollection<WorkerAdmissionBlockingReason> reasons,
        WorkerResourceRequirements requirements,
        IReadOnlyList<GpuCapability> gpus)
    {
        var minimumTotal = requirements.MinimumTotalGpuMemoryBytes ?? 0;
        var minimumFree = requirements.MinimumFreeGpuMemoryBytes ?? 0;
        if (minimumTotal == 0 && minimumFree == 0)
        {
            return;
        }

        if (gpus.Any(gpu =>
                gpu.Status == "present" &&
                gpu.TotalMemoryBytes >= minimumTotal &&
                gpu.FreeMemoryBytes >= minimumFree))
        {
            return;
        }

        if (gpus.Count == 0 || gpus.Any(gpu =>
                gpu.Status == "unknown" ||
                gpu.TotalMemoryBytes is null ||
                gpu.FreeMemoryBytes is null))
        {
            reasons.Add(Reason("unknown", "gpu_memory_unknown", "GPU memory availability could not be determined."));
            return;
        }

        var maximumTotal = gpus.Max(gpu => gpu.TotalMemoryBytes ?? 0);
        var maximumFree = gpus.Max(gpu => gpu.FreeMemoryBytes ?? 0);
        reasons.Add(Reason(
            "insufficient",
            "gpu_memory_insufficient",
            $"A GPU with at least {minimumTotal} total and {minimumFree} free bytes is required; the observed maxima are {maximumTotal} total and {maximumFree} free bytes."));
    }

    private static void CheckMinimum(
        ICollection<WorkerAdmissionBlockingReason> reasons,
        string unknownCode,
        string insufficientCode,
        string status,
        long? actual,
        long minimum,
        string resourceName)
    {
        if (minimum == 0)
        {
            return;
        }

        if (status != "present" || actual is null)
        {
            reasons.Add(Reason("unknown", unknownCode, $"{resourceName} could not be determined."));
        }
        else if (actual < minimum)
        {
            reasons.Add(Reason(
                "insufficient",
                insufficientCode,
                $"At least {minimum} {resourceName} are required; {actual} are available."));
        }
    }

    private static WorkerAdmissionBlockingReason Reason(string category, string code, string message) =>
        new(category, code, message);

    private static NvidiaCapability ToContract(NvidiaProbeResult result) => new(
        result.Status,
        result.CudaStatus,
        result.DriverVersion,
        result.CudaDriverApiVersion is null ? null : FormatCudaVersion(result.CudaDriverApiVersion.Value),
        result.ReasonCode,
        result.Gpus.Select(gpu => new GpuCapability(
            gpu.DeviceIndex,
            gpu.Name,
            gpu.TotalMemoryBytes,
            gpu.FreeMemoryBytes,
            gpu.Status)).ToArray());

    private static string FormatCudaVersion(int version) =>
        $"{version / 1000}.{version % 1000 / 10}";

    private static int? ParseCudaVersion(string? value)
    {
        if (value is null || !Version.TryParse(value, out var version) ||
            version.Major < 0 || version.Minor is < 0 or > 99)
        {
            return null;
        }

        return checked((version.Major * 1000) + (version.Minor * 10));
    }
}
