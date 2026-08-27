using System.Collections.Concurrent;
using System.Threading.Channels;
using QiongTu.Contracts;

namespace QiongTu.Control;

public sealed record ImageImportCoordinatorOptions(
    int QueueCapacity = 128,
    FileAttributes RecoveredSourceFileAttributes = FileAttributes.Archive);

public sealed class ImageImportCoordinator : IAsyncDisposable
{
    private static readonly HashSet<string> SourceWorkStatuses = new(StringComparer.Ordinal)
    {
        "discovered",
        "staging",
        "source_locked",
        "source_missing",
        "source_unavailable",
        "source_changed"
    };

    private readonly ImageImportCatalog _catalog;
    private readonly ImageImportSourceSecurity _sourceSecurity;
    private readonly ImageImportSourceDiscovery _sourceDiscovery;
    private readonly ContentAddressedObjectStore _objectStore;
    private readonly Func<Stream, CancellationToken, Task<ObjectStageReceipt>> _stageSourceAsync;
    private readonly ImageImportCoordinatorOptions _options;
    private readonly Channel<string> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sessionCancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingOrActiveSessions = new(StringComparer.Ordinal);
    private readonly Task _worker;
    private int _queued;
    private int _active;

    public ImageImportCoordinator(
        ImageImportCatalog catalog,
        ImageImportSourceSecurity sourceSecurity,
        ImageImportSourceDiscovery sourceDiscovery,
        ContentAddressedObjectStore objectStore,
        ImageImportCoordinatorOptions? options = null)
        : this(catalog, sourceSecurity, sourceDiscovery, objectStore, null, options)
    {
    }

    internal ImageImportCoordinator(
        ImageImportCatalog catalog,
        ImageImportSourceSecurity sourceSecurity,
        ImageImportSourceDiscovery sourceDiscovery,
        ContentAddressedObjectStore objectStore,
        Func<Stream, CancellationToken, Task<ObjectStageReceipt>>? stageSourceAsync,
        ImageImportCoordinatorOptions? options = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _sourceSecurity = sourceSecurity ?? throw new ArgumentNullException(nameof(sourceSecurity));
        _sourceDiscovery = sourceDiscovery ?? throw new ArgumentNullException(nameof(sourceDiscovery));
        _objectStore = objectStore ?? throw new ArgumentNullException(nameof(objectStore));
        _stageSourceAsync = stageSourceAsync ?? ((stream, token) => _objectStore.StageAsync(stream, cancellationToken: token));
        _options = options ?? new ImageImportCoordinatorOptions();
        if (_options.QueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Image import queue capacity must be positive.");
        }

        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(ProcessQueueAsync);
    }

    public bool IsIdle => Volatile.Read(ref _queued) == 0 && Volatile.Read(ref _active) == 0;

    public async Task<ImageImportSession> StartAsync(
        string requestId,
        string importSessionId,
        string datasetVersionId,
        string sourceRoot,
        ControlDataPaths controlDataPaths,
        CancellationToken cancellationToken = default)
    {
        sourceRoot = NormalizeSelectedRoot(sourceRoot);
        var sourceRootKey = await _sourceSecurity.CreateSourceRootKeyAsync(sourceRoot, cancellationToken);
        var replay = _catalog.TryReplayPreparedStart(
            requestId,
            importSessionId,
            datasetVersionId,
            sourceRootKey,
            importSessionId);
        if (replay is not null)
        {
            _sourceSecurity.TryCommitPreparedRecoveryManifest(requestId, importSessionId);
            return replay;
        }

        _catalog.ValidateDatasetVersionForImport(datasetVersionId);

        var discovery = await _sourceDiscovery.DiscoverPreparedAsync(
            importSessionId,
            sourceRoot,
            controlDataPaths,
            cancellationToken);
        await _sourceSecurity.SavePreparedRecoveryManifestAsync(
            discovery.RecoveryManifest,
            requestId,
            cancellationToken);
        var catalogEntries = await ToCatalogEntriesAsync(
            importSessionId,
            discovery.Candidates,
            cancellationToken);

        ImageImportSession session;
        try
        {
            session = _catalog.StartPrepared(
                requestId,
                importSessionId,
                datasetVersionId,
                discovery.SourceRoot.SourceRootKey,
                importSessionId,
                catalogEntries);
        }
        catch
        {
            _sourceSecurity.DeletePreparedRecoveryManifest(requestId);
            throw;
        }

        _sourceSecurity.CommitPreparedRecoveryManifest(requestId, importSessionId);

        if (string.Equals(session.SourceEligibilityState, "dji_supported", StringComparison.Ordinal))
        {
            await EnqueueAsync(session.ImportSessionId, cancellationToken);
        }

        return _catalog.Get(new ImageImportGetParameters(session.ImportSessionId));
    }

    public async Task<ImageImportSession> ResumeAsync(
        string requestId,
        string importSessionId,
        string? sourceRoot = null,
        ControlDataPaths? controlDataPaths = null,
        CancellationToken cancellationToken = default)
    {
        string? sourceRootKey = null;
        ImageImportSourceDiscoveryResult? reselection = null;
        if (sourceRoot is not null)
        {
            if (controlDataPaths is null)
            {
                throw new ArgumentNullException(nameof(controlDataPaths));
            }

            sourceRoot = NormalizeSelectedRoot(sourceRoot);
            sourceRootKey = await _sourceSecurity.CreateSourceRootKeyAsync(sourceRoot, cancellationToken);
        }

        var replay = _catalog.TryReplayPreparedResume(requestId, importSessionId, sourceRootKey);
        if (replay is not null)
        {
            _sourceSecurity.TryCommitPreparedRecoveryManifest(requestId, importSessionId);
            if (string.Equals(replay.Status, "ready", StringComparison.Ordinal))
            {
                await EnqueueAsync(replay.ImportSessionId, cancellationToken);
            }

            return replay;
        }

        var existingSession = _catalog.TryGetSession(importSessionId)
            ?? throw new BusinessCatalogException(
                "image_import_session_not_found",
                "The image import session was not found.");
        if (existingSession.Status is "completed" or "cancelled" or "failed")
        {
            return _catalog.ResumePrepared(requestId, importSessionId, sourceRootKey);
        }

        if (sourceRoot is not null)
        {
            reselection = await _sourceDiscovery.DiscoverPreparedAsync(
                importSessionId,
                sourceRoot,
                controlDataPaths!,
                cancellationToken);
            await ValidateReselectionAsync(importSessionId, reselection.Candidates, cancellationToken);
            await _sourceSecurity.SavePreparedRecoveryManifestAsync(
                reselection.RecoveryManifest,
                requestId,
                cancellationToken);
        }

        ImageImportSession session;
        try
        {
            session = _catalog.ResumePrepared(requestId, importSessionId, sourceRootKey);
        }
        catch
        {
            if (reselection is not null)
            {
                _sourceSecurity.DeletePreparedRecoveryManifest(requestId);
            }

            throw;
        }

        if (reselection is not null)
        {
            _sourceSecurity.CommitPreparedRecoveryManifest(requestId, importSessionId);
        }

        if (string.Equals(session.Status, "ready", StringComparison.Ordinal))
        {
            await EnqueueAsync(session.ImportSessionId, cancellationToken);
        }

        return session;
    }

    public ImageImportSession Cancel(string requestId, string importSessionId)
    {
        if (_sessionCancellations.TryGetValue(importSessionId, out var cancellation))
        {
            cancellation.Cancel();
        }

        return _catalog.Cancel(requestId, new ImageImportCancelParameters(importSessionId));
    }

    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        var stagingRecovery = await _objectStore.RecoverStagedAsync(cancellationToken);
        var stagedById = stagingRecovery.Recoverable.ToDictionary(stage => stage.StageId, StringComparer.Ordinal);
        var workItems = _catalog.ListIncompleteWorkItems();
        foreach (var item in workItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.Equals(item.Status, "publishing", StringComparison.Ordinal))
                {
                    await CompletePublishingWorkItemAsync(item, stagedById, cancellationToken);
                    continue;
                }

                if (string.Equals(item.Status, "staged", StringComparison.Ordinal))
                {
                    if (item.StageReceipt is not null && stagedById.ContainsKey(item.StageReceipt.StageId))
                    {
                        await PublishStagedWorkItemAsync(item with { StageReceipt = stagedById[item.StageReceipt.StageId] }, cancellationToken);
                    }
                    else
                    {
                        _catalog.ResetEntryForSourceRetry(item.ImportEntryId, "object_stage_missing");
                    }
                }
            }
            catch (BusinessCatalogException)
            {
                throw;
            }
            catch (Exception exception) when (exception is ObjectStoreException or IOException or InvalidOperationException)
            {
                MarkSafeEntryError(item, ErrorCode(exception));
            }
        }

        foreach (var sessionId in _catalog.ListIncompleteWorkItems()
                     .Select(item => item.ImportSessionId)
                     .Distinct(StringComparer.Ordinal))
        {
            var session = _catalog.TryGetSession(sessionId);
            if (session is not null &&
                string.Equals(session.SourceEligibilityState, "dji_supported", StringComparison.Ordinal) &&
                session.Status is not ("completed" or "cancelled" or "failed" or "awaiting_source_preflight"))
            {
                await EnqueueAsync(sessionId, cancellationToken);
            }
        }
    }

    public async Task WaitUntilIdleAsync(CancellationToken cancellationToken = default)
    {
        while (Volatile.Read(ref _queued) > 0 || Volatile.Read(ref _active) > 0)
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _stop.Cancel();
        foreach (var cancellation in _sessionCancellations.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        try
        {
            await _worker;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private async Task<IReadOnlyList<ImageImportDiscoveredEntry>> ToCatalogEntriesAsync(
        string importSessionId,
        IReadOnlyList<ImageImportDiscoveredItem> candidates,
        CancellationToken cancellationToken)
    {
        var entries = new List<ImageImportDiscoveredEntry>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var identity = candidate.Snapshot.Identity is null
                ? null
                : await _sourceSecurity.CreateSourceIdentityKeyAsync(
                    candidate.SourceItemKey,
                    candidate.Snapshot.Identity,
                    cancellationToken);
            entries.Add(new ImageImportDiscoveredEntry(
                importSessionId,
                candidate.SourceItemKey,
                candidate.LeafDisplayName,
                index,
                candidate.Snapshot.Length,
                candidate.Snapshot.LastWriteTimeUtc,
                identity));
        }

        return entries;
    }

    private async Task ValidateReselectionAsync(
        string importSessionId,
        IReadOnlyList<ImageImportDiscoveredItem> candidates,
        CancellationToken cancellationToken)
    {
        var bindings = _catalog.ListSourceBindings(importSessionId)
            .ToDictionary(binding => binding.SourceEntryKey, StringComparer.Ordinal);
        if (bindings.Count != candidates.Count)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_reselection_mismatch",
                "The selected source does not match the existing import session.");
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!bindings.TryGetValue(candidate.SourceItemKey, out var binding) ||
                binding.ByteLengthSnapshot != candidate.Snapshot.Length ||
                binding.SourceLastWriteTimeUtc != candidate.Snapshot.LastWriteTimeUtc)
            {
                throw new ImageImportSourceDiscoveryException(
                    "source_reselection_mismatch",
                    "The selected source does not match the existing import session.");
            }

            if (binding.SourceIdentityKey is not null)
            {
                if (candidate.Snapshot.Identity is null)
                {
                    throw new ImageImportSourceDiscoveryException(
                        "source_reselection_identity_unavailable",
                        "The selected source identity could not be verified.");
                }

                var identityKey = await _sourceSecurity.CreateSourceIdentityKeyAsync(
                    candidate.SourceItemKey,
                    candidate.Snapshot.Identity,
                    cancellationToken);
                if (!string.Equals(identityKey, binding.SourceIdentityKey, StringComparison.Ordinal))
                {
                    throw new ImageImportSourceDiscoveryException(
                        "source_reselection_mismatch",
                        "The selected source does not match the existing import session.");
                }
            }
        }
    }

    private async Task EnqueueAsync(string importSessionId, CancellationToken cancellationToken)
    {
        if (!_pendingOrActiveSessions.TryAdd(importSessionId, 0))
        {
            return;
        }

        Interlocked.Increment(ref _queued);
        try
        {
            await _queue.Writer.WriteAsync(importSessionId, cancellationToken);
        }
        catch
        {
            Interlocked.Decrement(ref _queued);
            _pendingOrActiveSessions.TryRemove(importSessionId, out _);
            throw;
        }
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var sessionId in _queue.Reader.ReadAllAsync(_stop.Token))
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _active);
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var sessionCancellation = _sessionCancellations.AddOrUpdate(
                    sessionId,
                    _ => linked,
                    (_, existing) =>
                    {
                        existing.Dispose();
                        return linked;
                    });
                await ProcessSessionAsync(sessionId, sessionCancellation.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (exception is BusinessCatalogException
                or ImageImportSourceDiscoveryException
                or ImageImportSourceSecurityException
                or ObjectStoreException
                or IOException
                or InvalidOperationException)
            {
                try
                {
                    _catalog.MarkAwaitingSource(sessionId, ErrorCode(exception));
                }
                catch (BusinessCatalogException)
                {
                }
            }
            finally
            {
                _sessionCancellations.TryRemove(sessionId, out _);
                _pendingOrActiveSessions.TryRemove(sessionId, out _);
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private async Task ProcessSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        ImageImportSourceRecoveryManifest manifest;
        try
        {
            manifest = await _sourceSecurity.LoadRecoveryManifestAsync(sessionId, cancellationToken);
        }
        catch (ImageImportSourceSecurityException exception)
        {
            _catalog.MarkAwaitingSource(sessionId, exception.Code);
            return;
        }

        foreach (var item in _catalog.ListIncompleteWorkItems(sessionId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = _catalog.TryGetSession(item.ImportSessionId);
            if (session is null || string.Equals(session.Status, "cancelled", StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                if (SourceWorkStatuses.Contains(item.Status))
                {
                    await StageSourceWorkItemAsync(manifest, item, cancellationToken);
                    continue;
                }

                if (string.Equals(item.Status, "staged", StringComparison.Ordinal))
                {
                    await PublishStagedWorkItemAsync(item, cancellationToken);
                    continue;
                }

                if (string.Equals(item.Status, "publishing", StringComparison.Ordinal))
                {
                    await CompletePublishingWorkItemAsync(item, null, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is ImageImportSourceDiscoveryException
                or ImageImportSourceSecurityException
                or ObjectStoreException
                or IOException
                or InvalidOperationException)
            {
                MarkSafeEntryError(item, ErrorCode(exception));
            }
        }
    }

    private async Task StageSourceWorkItemAsync(
        ImageImportSourceRecoveryManifest manifest,
        ImageImportWorkItem item,
        CancellationToken cancellationToken)
    {
        _catalog.MarkStaging(item.ImportEntryId);
        var snapshot = SnapshotFor(manifest, item);
        var stage = await _sourceDiscovery.ReadSourceItemAsync(
            manifest,
            item.SourceEntryKey,
            snapshot,
            _stageSourceAsync,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _catalog.RecordStageReceipt(new ImageImportStageReceipt(
            item.ImportEntryId,
            stage.StageId,
            stage.Sha256,
            stage.ByteLength,
            stage.CreatedAtUtc));
        await PublishStagedWorkItemAsync(item with { StageReceipt = stage }, cancellationToken);
    }

    private async Task PublishStagedWorkItemAsync(ImageImportWorkItem item, CancellationToken cancellationToken)
    {
        var stage = item.StageReceipt ?? throw new InvalidOperationException("The import work item has no stage receipt.");
        cancellationToken.ThrowIfCancellationRequested();
        _catalog.MarkPublishing(item.ImportEntryId, stage.Sha256, stage.ByteLength);
        cancellationToken.ThrowIfCancellationRequested();
        var published = await _objectStore.PublishAsync(stage, cancellationToken);
        _catalog.CompletePublishedEntry(item.ImportEntryId, published.Sha256, published.ByteLength);
    }

    private async Task CompletePublishingWorkItemAsync(
        ImageImportWorkItem item,
        IReadOnlyDictionary<string, ObjectStageReceipt>? recoveredStages,
        CancellationToken cancellationToken)
    {
        if (item.ExpectedContentHash is not null && item.ExpectedByteLength is not null)
        {
            var published = await _objectStore.FindPublishedAsync(
                item.ExpectedContentHash,
                item.ExpectedByteLength.Value,
                cancellationToken);
            if (published is not null)
            {
                _catalog.CompletePublishedEntry(item.ImportEntryId, published.Sha256, published.ByteLength);
                return;
            }
        }

        if (item.StageReceipt is not null &&
            (recoveredStages is null || recoveredStages.TryGetValue(item.StageReceipt.StageId, out var _)))
        {
            var stage = recoveredStages is null ? item.StageReceipt : recoveredStages[item.StageReceipt.StageId];
            var published = await _objectStore.PublishAsync(stage, cancellationToken);
            _catalog.CompletePublishedEntry(item.ImportEntryId, published.Sha256, published.ByteLength);
            return;
        }

        _catalog.ResetEntryForSourceRetry(item.ImportEntryId, "object_stage_missing");
    }

    private ImageImportSourceSnapshot SnapshotFor(
        ImageImportSourceRecoveryManifest manifest,
        ImageImportWorkItem item)
    {
        if (manifest.SnapshotBySourceItemKey is not null &&
            manifest.SnapshotBySourceItemKey.TryGetValue(item.SourceEntryKey, out var snapshot))
        {
            return snapshot;
        }

        return new ImageImportSourceSnapshot(
            item.ByteLengthSnapshot,
            item.SourceLastWriteTimeUtc ?? DateTimeOffset.MinValue,
            _options.RecoveredSourceFileAttributes,
            null);
    }

    private void MarkSafeEntryError(ImageImportWorkItem item, string code)
    {
        try
        {
            _catalog.MarkEntryError(item.ImportEntryId, code);
        }
        catch (BusinessCatalogException)
        {
        }
    }

    private static string ErrorCode(Exception exception) => exception switch
    {
        ImageImportSourceDiscoveryException source => source.Code,
        ImageImportSourceSecurityException source => source.Code,
        ObjectStoreException store => store.Code,
        BusinessCatalogException catalog => catalog.Code,
        OperationCanceledException => "cancelled_by_user",
        _ => "image_import_entry_failed"
    };

    private static string NormalizeSelectedRoot(string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) ||
            sourceRoot.Length > 32_767 ||
            sourceRoot.Any(character => character < ' '))
        {
            throw new ImageImportSourceDiscoveryException(
                "source_root_invalid",
                "The selected import source root is invalid.");
        }

        try
        {
            var fullPath = Path.GetFullPath(sourceRoot);
            if (!Path.IsPathFullyQualified(fullPath))
            {
                throw new ImageImportSourceDiscoveryException(
                    "source_root_invalid",
                    "The selected import source root must be an absolute path.");
            }

            return fullPath;
        }
        catch (ImageImportSourceDiscoveryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_root_invalid",
                "The selected import source root is invalid.");
        }
    }
}
