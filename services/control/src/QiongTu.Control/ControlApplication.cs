using QiongTu.Contracts;

namespace QiongTu.Control;

public sealed class ControlApplication
{
    private readonly ControlDataPaths _paths;
    private readonly WorkerRegistry _workerRegistry;

    public ControlApplication(ControlDataPaths paths, WorkerRegistry workerRegistry)
    {
        _paths = paths;
        _workerRegistry = workerRegistry;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var instanceLock = new FileStream(
            _paths.LockFile,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var startedAtUtc = DateTimeOffset.UtcNow;
        var pipeName = RuntimeDiscovery.CreatePipeName();
        var businessDatabase = new BusinessDatabase(_paths.BusinessDatabase);
        businessDatabase.Initialize();
        var store = new WorkerRuntimeStore(_paths.RuntimeDatabase);
        store.Initialize();
        using var workers = new WorkerSupervisor(_workerRegistry, store, _paths.LogDirectory);
        workers.ReconcilePersistedWorkers();

        var roots = new ArtifactRootRegistry();
        roots.RegisterTrustedRoot("objects", _paths.ObjectDirectory);
        await using var artifactServer = new ArtifactServer(roots);
        await artifactServer.StartAsync(cancellationToken);

        using var internalShutdown = new CancellationTokenSource();
        using var linkedShutdown = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            internalShutdown.Token);
        var dispatcher = new ControlRequestDispatcher(
            pipeName,
            startedAtUtc,
            artifactServer,
            workers,
            internalShutdown.Cancel);
        await using var pipeServer = new NamedPipeControlServer(pipeName, dispatcher);
        pipeServer.Start();

        var discovery = new ControlDiscovery(
            ContractVersions.ControlApiV1,
            "named-pipe",
            Environment.ProcessId,
            pipeName,
            startedAtUtc);
        await RuntimeDiscovery.WriteAtomicallyAsync(
            _paths.DiscoveryFile,
            discovery,
            linkedShutdown.Token);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, linkedShutdown.Token);
        }
        catch (OperationCanceledException) when (linkedShutdown.IsCancellationRequested)
        {
        }
        finally
        {
            RuntimeDiscovery.DeleteIfOwned(_paths.DiscoveryFile, Environment.ProcessId);
        }
    }
}
