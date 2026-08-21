using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ControlLifecycleIntegrationTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task ReconnectingClientObservesTheSameControlAndWorkerProcess()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"qiongtu-lifecycle-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        var paths = ControlDataPaths.Create(testRoot);
        var store = new WorkerRuntimeStore(paths.RuntimeDatabase);
        store.Initialize();
        var registry = new WorkerRegistry();
        registry.Register(new WorkerDefinition(
            "lifecycle-probe",
            Path.Combine(Environment.SystemDirectory, "ping.exe"),
            ["-n", "120", "127.0.0.1"],
            paths.RuntimeDirectory));

        using var workers = new WorkerSupervisor(registry, store, paths.LogDirectory);
        var roots = new ArtifactRootRegistry();
        roots.RegisterTrustedRoot("objects", paths.ObjectDirectory);
        await using var artifactServer = new ArtifactServer(roots);
        await artifactServer.StartAsync(CancellationToken.None);
        var pipeName = RuntimeDiscovery.CreatePipeName();
        var stopRequested = false;
        var dispatcher = new ControlRequestDispatcher(
            pipeName,
            DateTimeOffset.UtcNow,
            artifactServer,
            workers,
            () => stopRequested = true);
        await using var server = new NamedPipeControlServer(pipeName, dispatcher);
        server.Start();

        string? workerId = null;
        try
        {
            using (var firstClient = await ConnectAsync(pipeName))
            {
                using var start = await SendAsync(
                    firstClient,
                    ControlMethods.WorkerStart,
                    new WorkerStartParameters("lifecycle-probe"));
                Assert.IsTrue(start.RootElement.GetProperty("ok").GetBoolean());
                var result = start.RootElement.GetProperty("result");
                workerId = result.GetProperty("workerId").GetString();
                Assert.IsFalse(string.IsNullOrWhiteSpace(workerId));
                Assert.IsGreaterThan(0, result.GetProperty("processId").GetInt32());
            }

            using (var secondClient = await ConnectAsync(pipeName))
            {
                using var status = await SendAsync(secondClient, ControlMethods.Status, parameters: null);
                Assert.AreEqual(Environment.ProcessId, status.RootElement.GetProperty("result").GetProperty("processId").GetInt32());
                Assert.AreEqual(1, status.RootElement.GetProperty("result").GetProperty("activeWorkerCount").GetInt32());

                using var workersResult = await SendAsync(secondClient, ControlMethods.WorkerList, parameters: null);
                var runningWorker = workersResult.RootElement.GetProperty("result")[0];
                Assert.AreEqual(workerId, runningWorker.GetProperty("workerId").GetString());
                Assert.AreEqual(WorkerSupervisor.RunningState, runningWorker.GetProperty("state").GetString());

                using var busyStop = await SendAsync(secondClient, ControlMethods.StopIfIdle, parameters: null);
                Assert.IsFalse(busyStop.RootElement.GetProperty("ok").GetBoolean());
                Assert.AreEqual("control_busy", busyStop.RootElement.GetProperty("error").GetProperty("code").GetString());

                using var unknownWorker = await SendAsync(
                    secondClient,
                    ControlMethods.WorkerStart,
                    new WorkerStartParameters("arbitrary-executable"));
                Assert.IsFalse(unknownWorker.RootElement.GetProperty("ok").GetBoolean());
                Assert.AreEqual("worker_not_registered", unknownWorker.RootElement.GetProperty("error").GetProperty("code").GetString());

                using var cancelled = await SendAsync(
                    secondClient,
                    ControlMethods.WorkerCancel,
                    new WorkerCancelParameters(workerId!));
                Assert.IsTrue(cancelled.RootElement.GetProperty("ok").GetBoolean());
                Assert.AreEqual(WorkerSupervisor.CancelledState, cancelled.RootElement.GetProperty("result").GetProperty("state").GetString());
            }

            Assert.IsFalse(stopRequested);
        }
        finally
        {
            if (workerId is not null)
            {
                var remaining = workers.List().SingleOrDefault(item => item.WorkerId == workerId);
                if (remaining?.State is WorkerSupervisor.RunningState or WorkerSupervisor.CancellingState)
                {
                    await workers.CancelAsync(workerId, CancellationToken.None);
                }
            }

            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(5_000);
        return client;
    }

    private static async Task<JsonDocument> SendAsync(
        NamedPipeClientStream client,
        string method,
        object? parameters)
    {
        var request = new
        {
            apiVersion = ContractVersions.ControlApiV1,
            requestId = Guid.NewGuid().ToString("N"),
            method,
            parameters
        };
        var json = JsonSerializer.Serialize(request, SerializerOptions);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await writer.WriteLineAsync(json);
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(line);
        return JsonDocument.Parse(line);
    }
}
