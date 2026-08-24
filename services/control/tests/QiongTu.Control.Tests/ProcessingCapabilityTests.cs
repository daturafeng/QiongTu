using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ProcessingCapabilityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task CapabilityReportIsPrivateAndStorageContainsOnlyRoles()
    {
        using var scope = new CapabilityScope();
        var registry = new WorkerRegistry();
        registry.Register(CpuWorker(scope.Root));
        var service = new ProcessingCapabilityService(
            registry,
            scope.Paths,
            new FakeHostResourceProbe(),
            new FakeNvidiaProbeClient(PresentNvidia()));

        var report = await service.CaptureAsync(CancellationToken.None);
        var json = JsonSerializer.Serialize(report, JsonOptions);

        Assert.AreEqual(ProcessingCapabilityService.SchemaVersion, report.SchemaVersion);
        Assert.IsFalse(report.Privacy.PathsIncluded);
        Assert.IsFalse(report.Privacy.TokensIncluded);
        Assert.IsFalse(report.Privacy.UserNameIncluded);
        Assert.IsFalse(report.Privacy.MachineNameIncluded);
        Assert.IsFalse(report.Privacy.EnvironmentIncluded);
        Assert.IsFalse(report.Privacy.CommandLineIncluded);
        CollectionAssert.AreEquivalent(
            new[] { "runtime", "state", "objects", "logs" },
            report.Storage.Select(item => item.Role).ToArray());
        Assert.IsTrue(report.Storage.All(item => item.Status == "present"));
        Assert.IsFalse(json.Contains(scope.Root, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(":\\", json);
        Assert.DoesNotContain("secret-token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.CommandLine, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PATH=", json, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task MissingNvidiaAllowsCpuWorkerAndDeniesCudaWorker()
    {
        using var scope = new CapabilityScope();
        var registry = new WorkerRegistry();
        registry.Register(CpuWorker(scope.Root));
        registry.Register(CudaWorker(scope.Root));
        var service = new ProcessingCapabilityService(
            registry,
            scope.Paths,
            new FakeHostResourceProbe(),
            new FakeNvidiaProbeClient(MissingNvidia()));

        var cpu = await service.CheckAsync("cpu-worker", CancellationToken.None);
        var cuda = await service.CheckAsync("cuda-worker", CancellationToken.None);

        Assert.AreEqual("allowed", cpu.Decision);
        Assert.AreEqual("denied", cuda.Decision);
        AssertReason(cuda, "missing", "nvidia_gpu_missing");
    }

    [TestMethod]
    public async Task UnknownNvidiaProbeProducesUnknownCudaAdmission()
    {
        using var scope = new CapabilityScope();
        var registry = new WorkerRegistry();
        registry.Register(CudaWorker(scope.Root));
        var service = new ProcessingCapabilityService(
            registry,
            scope.Paths,
            new FakeHostResourceProbe(),
            new FakeNvidiaProbeClient(NvidiaProbeResult.Unknown("nvidia_probe_timeout")));

        var admission = await service.CheckAsync("cuda-worker", CancellationToken.None);

        Assert.AreEqual("unknown", admission.Decision);
        AssertReason(admission, "unknown", "nvidia_probe_unknown");
        AssertReason(admission, "unknown", "cuda_probe_unknown");
    }

    [TestMethod]
    public async Task LowCudaDriverApiVersionIsIncompatible()
    {
        using var scope = new CapabilityScope();
        var registry = new WorkerRegistry();
        registry.Register(CudaWorker(scope.Root));
        var service = new ProcessingCapabilityService(
            registry,
            scope.Paths,
            new FakeHostResourceProbe(),
            new FakeNvidiaProbeClient(PresentNvidia(cudaDriverApiVersion: 11000)));

        var admission = await service.CheckAsync("cuda-worker", CancellationToken.None);

        Assert.AreEqual("denied", admission.Decision);
        AssertReason(admission, "incompatible", "cuda_driver_api_incompatible");
    }

    [TestMethod]
    public async Task InsufficientMemoryStorageAndGpuMemoryDenyAdmission()
    {
        using var scope = new CapabilityScope();
        var registry = new WorkerRegistry();
        registry.Register(CudaWorker(scope.Root));
        var host = new FakeHostResourceProbe(
            memory: new MemoryCapability("present", 8_000, 1_000),
            storageFactory: (role, _) => new StorageCapability(role, 16_000, 1_000, "fixed", "present"));
        var service = new ProcessingCapabilityService(
            registry,
            scope.Paths,
            host,
            new FakeNvidiaProbeClient(PresentNvidia(totalGpuMemoryBytes: 2_000, freeGpuMemoryBytes: 1_000)));

        var admission = await service.CheckAsync("cuda-worker", CancellationToken.None);

        Assert.AreEqual("denied", admission.Decision);
        AssertReason(admission, "insufficient", "available_memory_insufficient");
        AssertReason(admission, "insufficient", "available_storage_insufficient");
        AssertReason(admission, "insufficient", "gpu_memory_insufficient");
    }

    [TestMethod]
    public async Task WorkerSupervisorRejectsAdmissionBeforeLedgerOrProcessStart()
    {
        using var scope = new CapabilityScope();
        var registry = new WorkerRegistry();
        registry.Register(CpuWorker(scope.Root));
        var store = new WorkerRuntimeStore(scope.Paths.RuntimeDatabase);
        store.Initialize();
        using var supervisor = new WorkerSupervisor(
            registry,
            store,
            scope.Paths.LogDirectory,
            new FixedAdmissionGate(new WorkerAdmissionResult(
                "cpu-worker",
                "cpu",
                "denied",
                [new WorkerAdmissionBlockingReason("insufficient", "available_memory_insufficient", "not enough memory")])));

        var exception = await Assert.ThrowsAsync<ControlProtocolException>(() =>
            supervisor.StartAsync("cpu-worker", CancellationToken.None));

        Assert.AreEqual("worker_admission_denied", exception.Code);
        Assert.IsEmpty(store.List());
        Assert.AreEqual(0, supervisor.ActiveCount);
    }

    [TestMethod]
    public async Task IsolatedNvidiaProbeTimeoutReturnsUnknownWithinBoundedTime()
    {
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        Assert.IsTrue(File.Exists(ping), $"Expected fixed Windows test process at {ping}.");
        var client = new IsolatedNvidiaProbeClient(
            TimeSpan.FromMilliseconds(100),
            () =>
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = ping,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                startInfo.ArgumentList.Add("-n");
                startInfo.ArgumentList.Add("120");
                startInfo.ArgumentList.Add("127.0.0.1");
                return startInfo;
            });
        var stopwatch = Stopwatch.StartNew();

        var result = await client.CaptureAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.AreEqual("unknown", result.Status);
        Assert.AreEqual("unknown", result.CudaStatus);
        Assert.AreEqual("nvidia_probe_timeout", result.ReasonCode);
        Assert.IsLessThan(TimeSpan.FromSeconds(3), stopwatch.Elapsed);
    }

    [TestMethod]
    public void NvidiaNativeProbeReturnsOnlyBoundedLegalStates()
    {
        var result = NvidiaNativeProbe.Capture();

        Assert.AreEqual(HardwareProbeChildProtocol.SchemaVersion, result.SchemaVersion);
        Assert.Contains(result.Status, new[] { "present", "missing", "unknown" });
        Assert.Contains(result.CudaStatus, new[] { "present", "missing", "unknown" });
        Assert.IsLessThanOrEqualTo(NvidiaNativeProbe.MaximumGpuCount, result.Gpus.Count);
        foreach (var gpu in result.Gpus)
        {
            Assert.IsGreaterThanOrEqualTo(0, gpu.DeviceIndex);
            Assert.Contains(gpu.Status, new[] { "present", "unknown" });
            Assert.IsTrue(gpu.Name.Length is > 0 and <= NvidiaNativeProbe.MaximumGpuNameLength);
            Assert.IsTrue(gpu.TotalMemoryBytes is null or >= 0);
            Assert.IsTrue(gpu.FreeMemoryBytes is null or >= 0);
        }
    }

    [TestMethod]
    public async Task CapabilityAndAdmissionUseRealDispatcherPipePathWithoutLeaks()
    {
        await using var scope = await PipeCapabilityScope.StartAsync();

        using var reportResponse = await scope.SendAsync(ControlMethods.CapabilityGet, "capability-get", parameters: null);
        var reportRoot = Ok(reportResponse);
        var reportJson = reportRoot.GetRawText();
        Assert.IsLessThanOrEqualTo(NamedPipeControlServer.MaximumResponseBytes, Encoding.UTF8.GetByteCount(reportJson));
        Assert.AreEqual("missing", reportRoot.GetProperty("result").GetProperty("nvidia").GetProperty("status").GetString());
        Assert.DoesNotContain(scope.Root, reportJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", reportJson);
        Assert.DoesNotContain("secret-token", reportJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.CommandLine, reportJson, StringComparison.OrdinalIgnoreCase);

        using var admissionResponse = await scope.SendAsync(
            ControlMethods.WorkerAdmissionCheck,
            "worker-admission-check",
            new { workerType = "cuda-worker" });
        var admissionRoot = Ok(admissionResponse);
        Assert.AreEqual("denied", admissionRoot.GetProperty("result").GetProperty("decision").GetString());
        Assert.IsLessThanOrEqualTo(
            NamedPipeControlServer.MaximumResponseBytes,
            Encoding.UTF8.GetByteCount(admissionRoot.GetRawText()));
    }

    private static JsonElement Ok(JsonDocument document)
    {
        Assert.IsTrue(document.RootElement.GetProperty("ok").GetBoolean(), document.RootElement.GetRawText());
        return document.RootElement;
    }

    private static WorkerDefinition CpuWorker(string root) => new(
        "cpu-worker",
        SafePingPath(),
        ["-n", "2", "127.0.0.1"],
        root,
        new WorkerResourceRequirements(
            "cpu",
            MinimumLogicalProcessors: 1,
            MinimumAvailableMemoryBytes: 2_000,
            MinimumAvailableDiskBytes: 2_000,
            RequiresNvidia: false,
            MinimumCudaDriverApiVersion: null,
            MinimumTotalGpuMemoryBytes: null,
            MinimumFreeGpuMemoryBytes: null));

    private static WorkerDefinition CudaWorker(string root) => new(
        "cuda-worker",
        SafePingPath(),
        ["-n", "2", "127.0.0.1"],
        root,
        new WorkerResourceRequirements(
            "cuda",
            MinimumLogicalProcessors: 1,
            MinimumAvailableMemoryBytes: 2_000,
            MinimumAvailableDiskBytes: 2_000,
            RequiresNvidia: true,
            MinimumCudaDriverApiVersion: 12000,
            MinimumTotalGpuMemoryBytes: 8_000,
            MinimumFreeGpuMemoryBytes: 4_000));

    private static string SafePingPath() => Path.Combine(Environment.SystemDirectory, "ping.exe");

    private static NvidiaProbeResult MissingNvidia() => new(
        HardwareProbeChildProtocol.SchemaVersion,
        "missing",
        "missing",
        null,
        null,
        [],
        "nvidia_driver_libraries_missing");

    private static NvidiaProbeResult PresentNvidia(
        int cudaDriverApiVersion = 12000,
        long totalGpuMemoryBytes = 16_000,
        long freeGpuMemoryBytes = 12_000) => new(
        HardwareProbeChildProtocol.SchemaVersion,
        "present",
        "present",
        "555.55",
        cudaDriverApiVersion,
        [new NvidiaProbeGpu(0, "NVIDIA Test GPU", totalGpuMemoryBytes, freeGpuMemoryBytes, "present")],
        null);

    private static void AssertReason(
        WorkerAdmissionResult admission,
        string category,
        string code)
    {
        Assert.IsTrue(
            admission.BlockingReasons.Any(reason =>
                reason.Category == category && reason.Code == code),
            JsonSerializer.Serialize(admission, JsonOptions));
    }

    private sealed class FakeHostResourceProbe : IHostResourceProbe
    {
        private readonly MemoryCapability _memory;
        private readonly Func<string, string, StorageCapability> _storageFactory;

        public FakeHostResourceProbe(
            MemoryCapability? memory = null,
            Func<string, string, StorageCapability>? storageFactory = null)
        {
            _memory = memory ?? new MemoryCapability("present", 32_000, 24_000);
            _storageFactory = storageFactory ?? ((role, _) => new StorageCapability(role, 128_000, 96_000, "fixed", "present"));
        }

        public CapabilityHost CaptureHost() => new("present", "Windows", "x64", "x64", "console");

        public CpuCapability CaptureCpu() => new("present", 8, "x64");

        public MemoryCapability CaptureMemory() => _memory;

        public StorageCapability CaptureStorage(string role, string directoryPath) => _storageFactory(role, directoryPath);
    }

    private sealed class FakeNvidiaProbeClient(NvidiaProbeResult result) : INvidiaProbeClient
    {
        public Task<NvidiaProbeResult> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class FixedAdmissionGate(WorkerAdmissionResult result) : IWorkerAdmissionGate
    {
        public Task<WorkerAdmissionResult> CheckAsync(
            WorkerDefinition definition,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result with { WorkerType = definition.WorkerType });
        }
    }

    private sealed class CapabilityScope : IDisposable
    {
        public CapabilityScope()
        {
            Root = Path.Combine(Path.GetTempPath(), $"qiongtu-capability-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Paths = ControlDataPaths.Create(Root);
        }

        public string Root { get; }

        public ControlDataPaths Paths { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class PipeCapabilityScope : IAsyncDisposable
    {
        private readonly WorkerSupervisor _workers;
        private readonly ArtifactServer _artifactServer;
        private readonly NamedPipeControlServer _server;

        private PipeCapabilityScope(
            string root,
            string pipeName,
            WorkerSupervisor workers,
            ArtifactServer artifactServer,
            NamedPipeControlServer server)
        {
            Root = root;
            PipeName = pipeName;
            _workers = workers;
            _artifactServer = artifactServer;
            _server = server;
        }

        public string Root { get; }

        private string PipeName { get; }

        public static async Task<PipeCapabilityScope> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"qiongtu-capability-pipe-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = ControlDataPaths.Create(root);
            var registry = new WorkerRegistry();
            registry.Register(CpuWorker(root));
            registry.Register(CudaWorker(root));
            var capabilities = new ProcessingCapabilityService(
                registry,
                paths,
                new FakeHostResourceProbe(),
                new FakeNvidiaProbeClient(MissingNvidia()));
            var store = new WorkerRuntimeStore(paths.RuntimeDatabase);
            store.Initialize();
            var businessDatabase = new BusinessDatabase(paths.BusinessDatabase);
            businessDatabase.Initialize();
            var workers = new WorkerSupervisor(registry, store, paths.LogDirectory, capabilities);
            var roots = new ArtifactRootRegistry();
            roots.RegisterTrustedRoot("objects", paths.ObjectDirectory);
            var artifactServer = new ArtifactServer(roots);
            await artifactServer.StartAsync(CancellationToken.None);
            var pipeName = RuntimeDiscovery.CreatePipeName();
            var dispatcher = new ControlRequestDispatcher(
                pipeName,
                DateTimeOffset.UtcNow,
                artifactServer,
                workers,
                new BusinessCatalog(businessDatabase),
                capabilities,
                requestStop: () => { });
            var server = new NamedPipeControlServer(pipeName, dispatcher);
            server.Start();
            return new PipeCapabilityScope(root, pipeName, workers, artifactServer, server);
        }

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            await _artifactServer.DisposeAsync();
            _workers.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        public async Task<JsonDocument> SendAsync(string method, string requestId, object? parameters)
        {
            await using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(5_000);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new
                {
                    apiVersion = ContractVersions.ControlApiV1,
                    requestId,
                    method,
                    parameters
                },
                JsonOptions));
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(line);
            return JsonDocument.Parse(line);
        }
    }
}
