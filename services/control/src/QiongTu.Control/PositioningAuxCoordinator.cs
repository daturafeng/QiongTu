using System.Collections.Concurrent;
using System.Threading.Channels;
using QiongTu.Contracts;

namespace QiongTu.Control;

internal sealed record PositioningAuxCoordinatorOptions(int QueueCapacity = 32);

internal sealed class PositioningAuxCoordinator : IAsyncDisposable
{
    private readonly PositioningAuxCatalog _catalog;
    private readonly ImageImportPreflightCatalog _preflightCatalog;
    private readonly ImageImportSourceSecurity _sourceSecurity;
    private readonly ImageImportSourceDiscovery _sourceDiscovery;
    private readonly ContentAddressedObjectStore _objectStore;
    private readonly IPositioningAuxProbeClient _probe;
    private readonly ControlDataPaths _controlDataPaths;
    private readonly Func<Stream, CancellationToken, Task<ObjectStageReceipt>> _stageSourceAsync;
    private readonly Channel<string> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runCancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingOrActiveRuns = new(StringComparer.Ordinal);
    private readonly Task _worker;
    private int _queued;
    private int _active;

    internal PositioningAuxCoordinator(
        PositioningAuxCatalog catalog,
        ImageImportPreflightCatalog preflightCatalog,
        ImageImportSourceSecurity sourceSecurity,
        ImageImportSourceDiscovery sourceDiscovery,
        ContentAddressedObjectStore objectStore,
        IPositioningAuxProbeClient probe,
        ControlDataPaths controlDataPaths,
        Func<Stream, CancellationToken, Task<ObjectStageReceipt>>? stageSourceAsync = null,
        PositioningAuxCoordinatorOptions? options = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _preflightCatalog = preflightCatalog ?? throw new ArgumentNullException(nameof(preflightCatalog));
        _sourceSecurity = sourceSecurity ?? throw new ArgumentNullException(nameof(sourceSecurity));
        _sourceDiscovery = sourceDiscovery ?? throw new ArgumentNullException(nameof(sourceDiscovery));
        _objectStore = objectStore ?? throw new ArgumentNullException(nameof(objectStore));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _controlDataPaths = controlDataPaths ?? throw new ArgumentNullException(nameof(controlDataPaths));
        _stageSourceAsync = stageSourceAsync ?? ((stream, token) => _objectStore.StageAsync(stream, cancellationToken: token));
        var effectiveOptions = options ?? new PositioningAuxCoordinatorOptions();
        if (effectiveOptions.QueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The positioning auxiliary queue capacity must be positive.");
        }

        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(effectiveOptions.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(ProcessQueueAsync);
    }

    internal bool IsIdle => Volatile.Read(ref _queued) == 0 && Volatile.Read(ref _active) == 0;

    internal async Task EnqueueApprovedSessionAsync(
        string importSessionId,
        CancellationToken cancellationToken = default)
    {
        var preflight = _preflightCatalog.TryGetForSession(importSessionId)
            ?? throw new BusinessCatalogException(
                "positioning_aux_source_preflight_missing",
                "The positioning auxiliary import has no source preflight run.");
        RequireApprovedPreflight(preflight);
        var run = await EnsureRunAsync(preflight, cancellationToken);
        if (run.Status is "pending" or "interrupted")
        {
            await EnqueueAsync(run.RunId, cancellationToken);
        }
    }

    internal async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        _catalog.InterruptRunningRuns();
        _ = await _objectStore.RecoverStagedAsync(cancellationToken);

        foreach (var preflightRunId in _catalog.ListApprovedPreflightRunIdsWithoutAuxRun())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var binding = _preflightCatalog.GetRunBinding(preflightRunId);
                var preflight = _preflightCatalog.TryGetForSession(binding.ImportSessionId);
                if (preflight is not null)
                {
                    await EnsureRunAsync(preflight, cancellationToken);
                }
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                // The protected locator may be temporarily unavailable. The approved
                // preflight remains authoritative and will be retried on the next recovery.
            }
        }

        foreach (var runId in _catalog.ListRecoverableRunIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnqueueAsync(runId, cancellationToken);
        }
    }

    internal async Task<PositioningAuxImportRun> ResumeAsync(
        string requestId,
        PositioningAuxImportResumeParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var current = _catalog.Get(new PositioningAuxImportGetParameters(parameters.RunId));
        if (current.Status is "completed" or "blocked" or "cancelled" or "running")
        {
            return _catalog.Resume(requestId, parameters);
        }

        var selectedRoot = NormalizeSelectedRoot(parameters.SourceRootPath);
        var preflight = _preflightCatalog.TryGetForSession(current.ImportSessionId)
            ?? throw new BusinessCatalogException(
                "positioning_aux_source_preflight_missing",
                "The positioning auxiliary import has no source preflight run.");
        RequireApprovedPreflight(preflight);
        var binding = _preflightCatalog.GetRunBinding(preflight.PreflightRunId);
        var rootKey = await _sourceSecurity.CreateSourceRootKeyAsync(selectedRoot, cancellationToken);
        if (!string.Equals(rootKey, binding.SourceRootKey, StringComparison.Ordinal))
        {
            throw new ImageImportSourceDiscoveryException(
                "source_reselection_mismatch",
                "The selected source no longer matches the approved source preflight.");
        }

        var imageDiscovery = await _sourceDiscovery.DiscoverPreparedAsync(
            current.ImportSessionId,
            selectedRoot,
            _controlDataPaths,
            cancellationToken);
        var completeDiscovery = await _sourceDiscovery.DiscoverPreflightSidecarsPreparedAsync(
            imageDiscovery.RecoveryManifest,
            _controlDataPaths,
            cancellationToken);
        await ValidateCompleteReselectionAsync(preflight, completeDiscovery.RecoveryManifest, cancellationToken);
        await _sourceSecurity.SavePreparedRecoveryManifestAsync(
            completeDiscovery.RecoveryManifest,
            requestId,
            cancellationToken);

        PositioningAuxImportRun resumed;
        try
        {
            resumed = _catalog.Resume(requestId, parameters);
        }
        catch
        {
            _sourceSecurity.DeletePreparedRecoveryManifest(requestId);
            throw;
        }

        _sourceSecurity.CommitPreparedRecoveryManifest(requestId, current.ImportSessionId);
        var ensured = await EnsureRunAsync(preflight, cancellationToken);
        if (ensured.RunId != resumed.RunId)
        {
            throw new BusinessCatalogException(
                "positioning_aux_import_binding_conflict",
                "The resumed positioning auxiliary run no longer matches its approved source preflight.");
        }

        if (resumed.Status == "running")
        {
            await EnqueueAsync(resumed.RunId, cancellationToken);
        }

        return resumed;
    }

    internal PositioningAuxImportRun Cancel(
        string requestId,
        PositioningAuxImportCancelParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (_runCancellations.TryGetValue(parameters.RunId, out var cancellation))
        {
            cancellation.Cancel();
        }

        return _catalog.Cancel(requestId, parameters);
    }

    internal async Task WaitUntilIdleAsync(CancellationToken cancellationToken = default)
    {
        while (!IsIdle)
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _stop.Cancel();
        foreach (var cancellation in _runCancellations.Values)
        {
            cancellation.Cancel();
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
            foreach (var cancellation in _runCancellations.Values)
            {
                cancellation.Dispose();
            }

            _stop.Dispose();
        }
    }

    private async Task<PositioningAuxImportRun> EnsureRunAsync(
        ImageImportPreflightRun preflight,
        CancellationToken cancellationToken)
    {
        RequireApprovedPreflight(preflight);
        var binding = _preflightCatalog.GetRunBinding(preflight.PreflightRunId);
        var manifest = await _sourceSecurity.LoadRecoveryManifestAsync(preflight.ImportSessionId, cancellationToken);
        ValidateManifestBinding(binding, manifest);
        var associations = BuildAssociationBindings(preflight.PreflightRunId, manifest);
        return _catalog.EnsureRunForCompletedPreflight(preflight.PreflightRunId, associations);
    }

    private IReadOnlyList<PositioningAuxAssociationBinding> BuildAssociationBindings(
        string preflightRunId,
        ImageImportSourceRecoveryManifest manifest)
    {
        var items = _preflightCatalog.ListWorkItems(preflightRunId, includeCompleted: true);
        var groupByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var imageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var mrkCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.Status != "completed")
            {
                throw new BusinessCatalogException(
                    "positioning_aux_source_preflight_incomplete",
                    "The approved source preflight contains a non-terminal item.");
            }

            var group = GroupFor(item.SourceEntryKey, manifest);
            groupByKey[item.SourceEntryKey] = group;
            if (item.CandidateKind == "image_candidate")
            {
                imageCounts[group] = imageCounts.GetValueOrDefault(group) + 1;
            }
            else if (item.CandidateKind == "positioning_aux_candidate" && item.FormatHint == "mrk")
            {
                mrkCounts[group] = mrkCounts.GetValueOrDefault(group) + 1;
            }
        }

        var associations = new List<PositioningAuxAssociationBinding>();
        foreach (var item in items.Where(item =>
                     item.CandidateKind == "positioning_aux_candidate" &&
                     item.FormatHint is "mrk" or "nav" or "obs" or "rtk"))
        {
            var group = groupByKey[item.SourceEntryKey];
            if (!imageCounts.TryGetValue(group, out var imageCount) || imageCount <= 0 ||
                (item.FormatHint == "mrk" && mrkCounts.GetValueOrDefault(group) != 1))
            {
                throw new BusinessCatalogException(
                    "positioning_aux_association_ambiguous",
                    "A positioning auxiliary candidate cannot be bound to exactly one supported image group.");
            }

            associations.Add(new PositioningAuxAssociationBinding(item.ItemId, item.SourceEntryKey, imageCount));
        }

        return associations;
    }

    private async Task ValidateCompleteReselectionAsync(
        ImageImportPreflightRun preflight,
        ImageImportSourceRecoveryManifest manifest,
        CancellationToken cancellationToken)
    {
        var binding = _preflightCatalog.GetRunBinding(preflight.PreflightRunId);
        ValidateManifestBinding(binding, manifest);
        var items = _preflightCatalog.ListWorkItems(preflight.PreflightRunId, includeCompleted: true);
        var expectedKeys = items.Select(item => item.SourceEntryKey).ToHashSet(StringComparer.Ordinal);
        if (!expectedKeys.SetEquals(manifest.RelativePathBySourceItemKey.Keys))
        {
            throw new ImageImportSourceDiscoveryException(
                "source_reselection_mismatch",
                "The selected source candidate inventory changed after source preflight.");
        }

        foreach (var item in items)
        {
            if (manifest.SnapshotBySourceItemKey is null ||
                !manifest.SnapshotBySourceItemKey.TryGetValue(item.SourceEntryKey, out var snapshot) ||
                snapshot.Length != item.ByteLengthSnapshot ||
                snapshot.LastWriteTimeUtc != item.SourceLastWriteTimeUtc ||
                snapshot.Identity is null || item.SourceIdentityKey is null)
            {
                throw new ImageImportSourceDiscoveryException(
                    "source_reselection_mismatch",
                    "The selected source no longer matches the approved source inventory.");
            }

            var identityKey = await _sourceSecurity.CreateSourceIdentityKeyAsync(
                item.SourceEntryKey,
                snapshot.Identity,
                cancellationToken);
            if (!string.Equals(identityKey, item.SourceIdentityKey, StringComparison.Ordinal))
            {
                throw new ImageImportSourceDiscoveryException(
                    "source_reselection_mismatch",
                    "The selected source identity changed after source preflight.");
            }
        }

        _ = BuildAssociationBindings(preflight.PreflightRunId, manifest);
    }

    private async Task EnqueueAsync(string runId, CancellationToken cancellationToken)
    {
        if (!_pendingOrActiveRuns.TryAdd(runId, 0))
        {
            return;
        }

        Interlocked.Increment(ref _queued);
        try
        {
            await _queue.Writer.WriteAsync(runId, cancellationToken);
        }
        catch
        {
            Interlocked.Decrement(ref _queued);
            _pendingOrActiveRuns.TryRemove(runId, out _);
            throw;
        }
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var runId in _queue.Reader.ReadAllAsync(_stop.Token))
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _active);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _runCancellations[runId] = cancellation;
            try
            {
                await ProcessRunAsync(runId, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                TryInterruptFirstIncomplete(runId, "positioning_aux_cancelled");
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                TryInterruptFirstIncomplete(runId, ErrorCode(exception));
            }
            finally
            {
                _runCancellations.TryRemove(runId, out _);
                _pendingOrActiveRuns.TryRemove(runId, out _);
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private async Task ProcessRunAsync(string runId, CancellationToken cancellationToken)
    {
        var run = _catalog.MarkRunning(runId);
        var manifest = await _sourceSecurity.LoadRecoveryManifestAsync(run.ImportSessionId, cancellationToken);
        var binding = _preflightCatalog.TryGetForSession(run.ImportSessionId)
            ?? throw new BusinessCatalogException(
                "positioning_aux_source_preflight_missing",
                "The positioning auxiliary import has no source preflight run.");
        RequireApprovedPreflight(binding);
        ValidateManifestBinding(_preflightCatalog.GetRunBinding(binding.PreflightRunId), manifest);

        var interruptedItems = new List<string>();
        foreach (var initial in _catalog.ListIncompleteWorkItems(runId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ProcessItemAsync(manifest, initial, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsOperationalFailure(exception))
            {
                TryInterruptItem(initial.ItemId, ErrorCode(exception));
                interruptedItems.Add(initial.ItemId);
                TryMarkRunning(runId);
            }
        }

        if (interruptedItems.Count > 0)
        {
            TryInterruptItem(interruptedItems[0], "positioning_aux_retry_required");
        }
    }

    private async Task ProcessItemAsync(
        ImageImportSourceRecoveryManifest manifest,
        PositioningAuxImportWorkItem item,
        CancellationToken cancellationToken)
    {
        if (item.Status == "interrupted" && item.PositioningAuxFileId is not null)
        {
            await ParseRetainedMrkAsync(item, cancellationToken);
            return;
        }

        if (item.Status is "pending" or "staging" or "interrupted")
        {
            item = await StageSourceAsync(manifest, item, cancellationToken);
        }

        if (item.Status == "staged")
        {
            item = _catalog.MarkPublishing(
                item.ItemId,
                item.StageReceipt!.Sha256,
                item.StageReceipt.ByteLength);
        }

        if (item.Status == "publishing")
        {
            await PublishAndRetainAsync(item, cancellationToken);
            item = CurrentIncompleteItem(item.RunId, item.ItemId) ?? item;
        }

        if (item.AuxiliaryType == "mrk" && item.Status is "retained" or "parsing")
        {
            await ParseRetainedMrkAsync(item, cancellationToken);
        }
    }

    private async Task<PositioningAuxImportWorkItem> StageSourceAsync(
        ImageImportSourceRecoveryManifest manifest,
        PositioningAuxImportWorkItem item,
        CancellationToken cancellationToken)
    {
        item = _catalog.MarkStaging(item.ItemId);
        var snapshot = await SnapshotForAsync(manifest, item, cancellationToken);
        var stage = await _sourceDiscovery.ReadSourceItemAsync(
            manifest,
            item.SourceEntryKey,
            snapshot,
            _stageSourceAsync,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return _catalog.RecordStageReceipt(new PositioningAuxStageReceipt(
            item.ItemId,
            stage.StageId,
            stage.Sha256,
            stage.ByteLength,
            stage.CreatedAtUtc));
    }

    private async Task PublishAndRetainAsync(
        PositioningAuxImportWorkItem item,
        CancellationToken cancellationToken)
    {
        PublishedObject? published = null;
        if (item.ExpectedContentHash is not null && item.ExpectedByteLength is not null)
        {
            published = await _objectStore.FindPublishedAsync(
                item.ExpectedContentHash,
                item.ExpectedByteLength.Value,
                cancellationToken);
        }

        if (published is null)
        {
            var stage = item.StageReceipt ?? throw new ObjectStoreException(
                "object_stage_missing",
                "The positioning auxiliary stage receipt is unavailable.");
            published = await _objectStore.PublishAsync(stage, cancellationToken);
        }

        _catalog.CompletePublishedRetention(
            item.ItemId,
            published.Sha256,
            published.ByteLength,
            MediaType(item.AuxiliaryType));
    }

    private async Task ParseRetainedMrkAsync(
        PositioningAuxImportWorkItem item,
        CancellationToken cancellationToken)
    {
        if (item.AuxiliaryType != "mrk")
        {
            return;
        }

        if (item.Status != "parsing")
        {
            item = _catalog.BeginParsing(item.ItemId);
        }

        if (item.ExpectedContentHash is null || item.ExpectedByteLength is null || item.ExpectedObjectKey is null)
        {
            throw new ObjectStoreException(
                "formal_object_unavailable",
                "The retained positioning auxiliary object identity is unavailable.");
        }

        var published = new PublishedObject(
            item.ExpectedContentHash,
            item.ExpectedByteLength.Value,
            item.ExpectedObjectKey,
            Deduplicated: true);
        var result = await _probe.AnalyzeMrkAsync(
            _objectStore,
            published,
            item.AssociationItemCount,
            cancellationToken);
        if (result.ParseState == "failed")
        {
            _catalog.BlockItem(item.ItemId, result.ReasonCodes.FirstOrDefault() ?? "positioning_aux_parse_failed");
            return;
        }

        _catalog.CompleteParsedMrk(item.ItemId, result);
    }

    private async Task<ImageImportSourceSnapshot> SnapshotForAsync(
        ImageImportSourceRecoveryManifest manifest,
        PositioningAuxImportWorkItem item,
        CancellationToken cancellationToken)
    {
        if (manifest.SnapshotBySourceItemKey is null ||
            !manifest.SnapshotBySourceItemKey.TryGetValue(item.SourceEntryKey, out var snapshot) ||
            snapshot.Length != item.ByteLengthSnapshot ||
            snapshot.LastWriteTimeUtc != item.SourceLastWriteTimeUtc ||
            snapshot.Identity is null)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_reselection_mismatch",
                "The positioning auxiliary source no longer matches its approved snapshot.");
        }

        var identityKey = await _sourceSecurity.CreateSourceIdentityKeyAsync(
            item.SourceEntryKey,
            snapshot.Identity,
            cancellationToken);
        if (!string.Equals(identityKey, item.SourceIdentityKey, StringComparison.Ordinal))
        {
            throw new ImageImportSourceDiscoveryException(
                "source_reselection_mismatch",
                "The positioning auxiliary source identity changed after approval.");
        }

        return snapshot;
    }

    private PositioningAuxImportWorkItem? CurrentIncompleteItem(string runId, string itemId) =>
        _catalog.ListIncompleteWorkItems(runId).SingleOrDefault(item => item.ItemId == itemId);

    private void TryInterruptFirstIncomplete(string runId, string failureCode)
    {
        var item = _catalog.ListIncompleteWorkItems(runId).FirstOrDefault();
        if (item is not null)
        {
            TryInterruptItem(item.ItemId, failureCode);
        }
    }

    private void TryInterruptItem(string itemId, string failureCode)
    {
        try
        {
            _catalog.MarkItemInterrupted(itemId, failureCode);
        }
        catch (BusinessCatalogException)
        {
        }
    }

    private void TryMarkRunning(string runId)
    {
        try
        {
            _catalog.MarkRunning(runId);
        }
        catch (BusinessCatalogException)
        {
        }
    }

    private static void RequireApprovedPreflight(ImageImportPreflightRun preflight)
    {
        if (preflight.Status != "completed" || preflight.Decision != "dji_supported" ||
            preflight.SourceEligibilityState != "dji_supported")
        {
            throw new BusinessCatalogException(
                "positioning_aux_source_gate_not_satisfied",
                "Positioning auxiliary import requires a completed dji_supported source preflight.");
        }
    }

    private static void ValidateManifestBinding(
        SourcePreflightRunBinding binding,
        ImageImportSourceRecoveryManifest manifest)
    {
        if (manifest.SessionId != binding.ImportSessionId ||
            manifest.SessionId != binding.SourceLocatorManifestId ||
            manifest.SourceRootKey != binding.SourceRootKey)
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_manifest_binding_mismatch",
                "The protected source locator does not match the approved source preflight.");
        }
    }

    private static string GroupFor(
        string sourceEntryKey,
        ImageImportSourceRecoveryManifest manifest)
    {
        if (!manifest.RelativePathBySourceItemKey.TryGetValue(sourceEntryKey, out var relativePath))
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_item_missing",
                "An approved source item is absent from the protected source locator.");
        }

        return (Path.GetDirectoryName(relativePath) ?? string.Empty)
            .Replace('\\', '/')
            .Normalize();
    }

    private static string NormalizeSelectedRoot(string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || sourceRoot.Length > 32_767 ||
            sourceRoot.Any(character => character < ' '))
        {
            throw new ImageImportSourceDiscoveryException("source_root_invalid", "The selected source root is invalid.");
        }

        try
        {
            var fullPath = Path.GetFullPath(sourceRoot);
            if (!Path.IsPathFullyQualified(fullPath))
            {
                throw new ImageImportSourceDiscoveryException(
                    "source_root_invalid",
                    "The selected source root must be absolute.");
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
                "The selected source root is invalid.",
                exception);
        }
    }

    private static string MediaType(string auxiliaryType) => auxiliaryType switch
    {
        "mrk" => "text/plain",
        "nav" or "obs" => "application/rinex",
        _ => "application/octet-stream"
    };

    private static bool IsOperationalFailure(Exception exception) => exception is
        BusinessCatalogException or
        ImageImportSourceDiscoveryException or
        ImageImportSourceSecurityException or
        ImageCasProbeException or
        ObjectStoreException or
        IOException or
        InvalidOperationException or
        UnauthorizedAccessException;

    private static string ErrorCode(Exception exception) => exception switch
    {
        BusinessCatalogException catalog => catalog.Code,
        ImageImportSourceDiscoveryException discovery => discovery.Code,
        ImageImportSourceSecurityException security => security.Code,
        ImageCasProbeException probe => probe.Code,
        ObjectStoreException store => store.Code,
        OperationCanceledException => "positioning_aux_cancelled",
        _ => "positioning_aux_operation_failed"
    };
}
