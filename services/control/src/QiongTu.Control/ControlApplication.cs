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
        var businessCatalog = new BusinessCatalog(businessDatabase);
        var store = new WorkerRuntimeStore(_paths.RuntimeDatabase);
        store.Initialize();
        var capabilityService = new ProcessingCapabilityService(_workerRegistry, _paths);
        using var workers = new WorkerSupervisor(
            _workerRegistry,
            store,
            _paths.LogDirectory,
            capabilityService);
        workers.ReconcilePersistedWorkers();

        var objectStore = new ContentAddressedObjectStore(_paths.ObjectDirectory);
        var imageImportCatalog = new ImageImportCatalog(businessDatabase);
        var imageImportSourceSecurity = new ImageImportSourceSecurity(
            Path.Combine(_paths.StateDirectory, "image-import-locators"));
        var imageImportSourceDiscovery = new ImageImportSourceDiscovery(imageImportSourceSecurity);
        await using var imageImportCoordinator = new ImageImportCoordinator(
            imageImportCatalog,
            imageImportSourceSecurity,
            imageImportSourceDiscovery,
            objectStore);
        await imageImportCoordinator.RecoverAsync(cancellationToken);

        var roots = new ArtifactRootRegistry();
        roots.RegisterTrustedRoot("objects", objectStore.PublishedDirectory);
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
            businessCatalog,
            capabilityService,
            internalShutdown.Cancel,
            imageImportCoordinator,
            imageImportCatalog,
            _paths);
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
