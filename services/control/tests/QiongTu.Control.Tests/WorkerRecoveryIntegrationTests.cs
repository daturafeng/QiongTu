using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class WorkerRecoveryIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task DesktopClientDisconnectDoesNotTerminateWorkerAndReconnectCanCancel()
    {
        await using var scope = await RecoveryPipeScope.StartAsync();
        string? workerId = null;
        int? processId = null;

        try
        {
            await using (var firstClient = await ConnectAsync(scope.PipeName))
            {
                using var started = await SendAsync(
                    firstClient,
                    ControlMethods.WorkerStart,
                    "start-ping-worker",
                    new WorkerStartParameters(RecoveryScope.WorkerType));
                var startResult = Ok(started).GetProperty("result");
                workerId = startResult.GetProperty("workerId").GetString();
                processId = startResult.GetProperty("processId").GetInt32();

                Assert.IsFalse(string.IsNullOrWhiteSpace(workerId));
                Assert.IsGreaterThan(0, processId.Value);
                Assert.IsTrue(IsProcessAlive(processId.Value));
            }

            await WaitUntilAsync(
                () => processId is not null && IsProcessAlive(processId.Value),
                "the real ping worker should remain alive after the first desktop client disconnects");

            await using (var secondClient = await ConnectAsync(scope.PipeName))
            {
                using var workers = await SendAsync(
                    secondClient,
                    ControlMethods.WorkerList,
                    "list-after-reconnect",
                    parameters: null);
                var listed = Ok(workers).GetProperty("result").EnumerateArray().Single();
                Assert.AreEqual(workerId, listed.GetProperty("workerId").GetString());
                Assert.AreEqual(processId, listed.GetProperty("processId").GetInt32());
                Assert.AreEqual(WorkerSupervisor.RunningState, listed.GetProperty("state").GetString());

                using var cancelled = await SendAsync(
                    secondClient,
                    ControlMethods.WorkerCancel,
                    "cancel-after-reconnect",
                    new WorkerCancelParameters(workerId!));
                var cancelResult = Ok(cancelled).GetProperty("result");
                Assert.AreEqual(workerId, cancelResult.GetProperty("workerId").GetString());
                Assert.AreEqual(WorkerSupervisor.CancelledState, cancelResult.GetProperty("state").GetString());
            }

            await WaitUntilAsync(
                () => processId is not null && !IsProcessAlive(processId.Value),
                "the cancelled worker process should exit");
        }
        finally
        {
            if (workerId is not null)
            {
                await scope.TryCancelAsync(workerId);
            }

            if (processId is not null)
            {
                await KillProcessTreeIfAliveAsync(processId.Value);
            }
        }
    }

    [TestMethod]
    public async Task RebuiltSupervisorReconcilesPersistedRunningWorkerAndCanCancelIt()
    {
        using var scope = new RecoveryScope();
        WorkerSupervisor? firstSupervisor = null;
        WorkerSupervisor? secondSupervisor = null;
        string? workerId = null;
        int? processId = null;

        try
        {
            firstSupervisor = scope.CreateSupervisor();
            var started = await firstSupervisor.StartAsync(RecoveryScope.WorkerType, CancellationToken.None)
                .WaitAsync(WorkerTimeout);
            workerId = started.WorkerId;
            processId = started.ProcessId;
            Assert.AreEqual(WorkerSupervisor.RunningState, started.State);
            Assert.IsNotNull(processId);
            Assert.IsTrue(IsProcessAlive(processId.Value));

            firstSupervisor.Dispose();
            firstSupervisor = null;

            secondSupervisor = scope.CreateSupervisor();
            secondSupervisor.ReconcilePersistedWorkers();

            var reconciled = await WaitForWorkerStateAsync(
                secondSupervisor,
                workerId,
                WorkerSupervisor.RunningState,
                "the rebuilt supervisor should attach the persisted live worker");
            Assert.AreEqual(workerId, reconciled.WorkerId);
            Assert.AreEqual(processId, reconciled.ProcessId);

            var cancelled = await secondSupervisor.CancelAsync(workerId, CancellationToken.None)
                .WaitAsync(WorkerTimeout);
            Assert.AreEqual(workerId, cancelled.WorkerId);
            Assert.AreEqual(processId, cancelled.ProcessId);
            Assert.AreEqual(WorkerSupervisor.CancelledState, cancelled.State);

            await WaitUntilAsync(
                () => processId is not null && !IsProcessAlive(processId.Value),
                "the reconciled worker should be terminated by cancellation");
        }
        finally
        {
            if (workerId is not null)
            {
                if (secondSupervisor is not null)
                {
                    await TryCancelAsync(secondSupervisor, workerId);
                }
                else if (firstSupervisor is not null)
                {
                    await TryCancelAsync(firstSupervisor, workerId);
                }
            }

            firstSupervisor?.Dispose();
            secondSupervisor?.Dispose();

            if (processId is not null)
            {
                await KillProcessTreeIfAliveAsync(processId.Value);
            }
        }
    }

    [TestMethod]
    public async Task ReconcileMarksPersistedExitedWorkerAsLost()
    {
        using var scope = new RecoveryScope();
        WorkerSupervisor? supervisor = null;
        int? processId = null;

        try
        {
            var exited = StartFixedPingProcess(scope.Paths.RuntimeDirectory, pingCount: 1);
            processId = exited.Id;
            var processStartedAtUtc = new DateTimeOffset(exited.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var executablePath = Path.GetFullPath(exited.MainModule?.FileName ?? RecoveryScope.PingPath);
            await exited.WaitForExitAsync().WaitAsync(WorkerTimeout);
            exited.Dispose();

            var workerId = $"exited-{Guid.NewGuid():N}";
            scope.Store.Upsert(
                new WorkerSnapshot(
                    workerId,
                    RecoveryScope.WorkerType,
                    WorkerSupervisor.RunningState,
                    processId,
                    DateTimeOffset.UtcNow,
                    null,
                    null),
                executablePath,
                processStartedAtUtc);

            supervisor = scope.CreateSupervisor();
            supervisor.ReconcilePersistedWorkers();

            var lost = await WaitForWorkerStateAsync(
                supervisor,
                workerId,
                WorkerSupervisor.LostState,
                "an exited persisted process should reconcile to lost");
            Assert.AreEqual(processId, lost.ProcessId);
            Assert.IsNotNull(lost.EndedAtUtc);
        }
        finally
        {
            supervisor?.Dispose();
            if (processId is not null)
            {
                await KillProcessTreeIfAliveAsync(processId.Value);
            }
        }
    }

    [TestMethod]
    public async Task ReconcileDoesNotAttachOrKillSamePidWhenExecutableIdentityDiffers()
    {
        using var scope = new RecoveryScope();
        WorkerSupervisor? supervisor = null;
        Process? impersonatingProcess = null;

        try
        {
            impersonatingProcess = StartMismatchedExecutableProcess(scope.Paths.RuntimeDirectory);
            var processStartedAtUtc = new DateTimeOffset(impersonatingProcess.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var impersonatingProcessId = impersonatingProcess.Id;
            Assert.IsTrue(IsProcessAlive(impersonatingProcessId));

            var workerId = $"identity-mismatch-{Guid.NewGuid():N}";
            scope.Store.Upsert(
                new WorkerSnapshot(
                    workerId,
                    RecoveryScope.WorkerType,
                    WorkerSupervisor.RunningState,
                    impersonatingProcessId,
                    DateTimeOffset.UtcNow,
                    null,
                    null),
                RecoveryScope.PingPath,
                processStartedAtUtc);

            supervisor = scope.CreateSupervisor();
            supervisor.ReconcilePersistedWorkers();

            var lost = await WaitForWorkerStateAsync(
                supervisor,
                workerId,
                WorkerSupervisor.LostState,
                "a matching PID with a different executable identity should reconcile to lost");
            Assert.AreEqual(impersonatingProcessId, lost.ProcessId);
            Assert.IsTrue(
                IsProcessAlive(impersonatingProcessId),
                "reconcile must not kill a same-PID process that does not match the registered executable identity");
        }
        finally
        {
            supervisor?.Dispose();
            if (impersonatingProcess is not null)
            {
                await KillProcessTreeIfAliveAsync(impersonatingProcess.Id);
                impersonatingProcess.Dispose();
            }
        }
    }

    private static JsonElement Ok(JsonDocument document)
    {
        Assert.IsTrue(document.RootElement.GetProperty("ok").GetBoolean(), document.RootElement.GetRawText());
        return document.RootElement;
    }

    private static Process StartFixedPingProcess(string workingDirectory, int pingCount)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = RecoveryScope.PingPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add(pingCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("127.0.0.1");

        var process = Process.Start(startInfo);
        Assert.IsNotNull(process);
        _ = DrainAsync(process.StandardOutput);
        _ = DrainAsync(process.StandardError);
        return process;
    }

    private static Process StartMismatchedExecutableProcess(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        Assert.IsTrue(File.Exists(startInfo.FileName), $"Expected fixed Windows mismatch process at {startInfo.FileName}.");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 120");

        var process = Process.Start(startInfo);
        Assert.IsNotNull(process);
        _ = DrainAsync(process.StandardOutput);
        _ = DrainAsync(process.StandardError);
        return process;
    }

    private static async Task DrainAsync(TextReader reader)
    {
        try
        {
            var buffer = new char[1024];
            while (await reader.ReadAsync(buffer) != 0)
            {
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync((int)ShortTimeout.TotalMilliseconds).WaitAsync(ShortTimeout);
        return client;
    }

    private static async Task<JsonDocument> SendAsync(
        NamedPipeClientStream client,
        string method,
        string requestId,
        object? parameters)
    {
        var request = new
        {
            apiVersion = ContractVersions.ControlApiV1,
            requestId,
            method,
            parameters
        };
        var json = JsonSerializer.Serialize(request, JsonOptions);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        using var reader = new StreamReader(
            client,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        await writer.WriteLineAsync(json).WaitAsync(ShortTimeout);
        var line = await reader.ReadLineAsync().WaitAsync(WorkerTimeout);
        Assert.IsNotNull(line);
        return JsonDocument.Parse(line);
    }

    private static async Task<WorkerSnapshot> WaitForWorkerStateAsync(
        WorkerSupervisor supervisor,
        string workerId,
        string expectedState,
        string because)
    {
        WorkerSnapshot? last = null;
        await WaitUntilAsync(
            () =>
            {
                last = supervisor.List().SingleOrDefault(item => item.WorkerId == workerId);
                return last?.State == expectedState;
            },
            because);
        Assert.IsNotNull(last);
        return last;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string because)
    {
        using var timeout = new CancellationTokenSource(WorkerTimeout);
        while (!timeout.IsCancellationRequested)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(PollInterval, timeout.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        Assert.Fail($"Timed out waiting for {because}.");
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task TryCancelAsync(WorkerSupervisor supervisor, string workerId)
    {
        try
        {
            await supervisor.CancelAsync(workerId, CancellationToken.None).WaitAsync(WorkerTimeout);
        }
        catch (ControlProtocolException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (TimeoutException)
        {
        }
    }

    private static async Task KillProcessTreeIfAliveAsync(int processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(WorkerTimeout);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            process?.Dispose();
        }
    }

    private sealed class RecoveryScope : IDisposable
    {
        public const string WorkerType = "worker-recovery-ping";
        public static readonly string PingPath = Path.Combine(Environment.SystemDirectory, "ping.exe");

        public RecoveryScope()
        {
            Assert.IsTrue(File.Exists(PingPath), $"Expected fixed Windows test process at {PingPath}.");
            Root = Path.Combine(Path.GetTempPath(), $"qiongtu-worker-recovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Paths = ControlDataPaths.Create(Root);
            Store = new WorkerRuntimeStore(Paths.RuntimeDatabase);
            Store.Initialize();
            Registry = new WorkerRegistry();
            Registry.Register(new WorkerDefinition(
                WorkerType,
                PingPath,
                ["-n", "120", "127.0.0.1"],
                Paths.RuntimeDirectory,
                new WorkerResourceRequirements(
                    "worker-recovery-fixed-minimum",
                    MinimumLogicalProcessors: 1,
                    MinimumAvailableMemoryBytes: 1,
                    MinimumAvailableDiskBytes: 1,
                    RequiresNvidia: false,
                    MinimumCudaDriverApiVersion: null,
                    MinimumTotalGpuMemoryBytes: null,
                    MinimumFreeGpuMemoryBytes: null)));
            Capabilities = new ProcessingCapabilityService(Registry, Paths);
        }

        public string Root { get; }

        public ControlDataPaths Paths { get; }

        public WorkerRuntimeStore Store { get; }

        public WorkerRegistry Registry { get; }

        public ProcessingCapabilityService Capabilities { get; }

        public WorkerSupervisor CreateSupervisor() => new(
            Registry,
            Store,
            Paths.LogDirectory,
            Capabilities);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class RecoveryPipeScope : IAsyncDisposable
    {
        private readonly RecoveryScope _scope;
        private readonly WorkerSupervisor _workers;
        private readonly ArtifactServer _artifactServer;
        private readonly NamedPipeControlServer _server;

        private RecoveryPipeScope(
            RecoveryScope scope,
            WorkerSupervisor workers,
            ArtifactServer artifactServer,
            NamedPipeControlServer server,
            string pipeName)
        {
            _scope = scope;
            _workers = workers;
            _artifactServer = artifactServer;
            _server = server;
            PipeName = pipeName;
        }

        public string PipeName { get; }

        public static async Task<RecoveryPipeScope> StartAsync()
        {
            var scope = new RecoveryScope();
            var workers = scope.CreateSupervisor();
            var roots = new ArtifactRootRegistry();
            roots.RegisterTrustedRoot("objects", scope.Paths.ObjectDirectory);
            var artifactServer = new ArtifactServer(roots);
            await artifactServer.StartAsync(CancellationToken.None).WaitAsync(WorkerTimeout);
            var pipeName = RuntimeDiscovery.CreatePipeName();
            var businessDatabase = new BusinessDatabase(scope.Paths.BusinessDatabase);
            businessDatabase.Initialize();
            var dispatcher = new ControlRequestDispatcher(
                pipeName,
                DateTimeOffset.UtcNow,
                artifactServer,
                workers,
                new BusinessCatalog(businessDatabase),
                scope.Capabilities,
                requestStop: () => { });
            var server = new NamedPipeControlServer(pipeName, dispatcher);
            server.Start();
            return new RecoveryPipeScope(scope, workers, artifactServer, server, pipeName);
        }

        public async Task TryCancelAsync(string workerId) => await WorkerRecoveryIntegrationTests.TryCancelAsync(_workers, workerId);

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            await _artifactServer.DisposeAsync();
            _workers.Dispose();
            _scope.Dispose();
        }
    }
}
