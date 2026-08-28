using System.Collections.Concurrent;
using System.Threading.Channels;
using QiongTu.Contracts;

namespace QiongTu.Control;

internal sealed record ImageImportPreflightCoordinatorOptions(int QueueCapacity = 32);

internal sealed class ImageImportPreflightCoordinator : IAsyncDisposable
{
    private readonly ImageImportPreflightCatalog _catalog;
    private readonly ImageImportSourceSecurity _sourceSecurity;
    private readonly ImageImportSourceDiscovery _sourceDiscovery;
    private readonly ImageSourcePreflightProbe _probe;
    private readonly ImageImportCoordinator _imageImports;
    private readonly ControlDataPaths _controlDataPaths;
    private readonly Channel<string> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, byte> _pendingOrActiveRuns = new(StringComparer.Ordinal);
    private readonly Task _worker;
    private int _queued;
    private int _active;

    public ImageImportPreflightCoordinator(
        ImageImportPreflightCatalog catalog,
        ImageImportSourceSecurity sourceSecurity,
        ImageImportSourceDiscovery sourceDiscovery,
        ImageSourcePreflightProbe probe,
        ImageImportCoordinator imageImports,
        ControlDataPaths controlDataPaths,
        ImageImportPreflightCoordinatorOptions? options = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _sourceSecurity = sourceSecurity ?? throw new ArgumentNullException(nameof(sourceSecurity));
        _sourceDiscovery = sourceDiscovery ?? throw new ArgumentNullException(nameof(sourceDiscovery));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _imageImports = imageImports ?? throw new ArgumentNullException(nameof(imageImports));
        _controlDataPaths = controlDataPaths ?? throw new ArgumentNullException(nameof(controlDataPaths));
        var effectiveOptions = options ?? new ImageImportPreflightCoordinatorOptions();
        if (effectiveOptions.QueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Source preflight queue capacity must be positive.");
        }

        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(effectiveOptions.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(ProcessQueueAsync);
    }

    public bool IsIdle => Volatile.Read(ref _queued) == 0 && Volatile.Read(ref _active) == 0;

    public async Task<ImageImportPreflightRun> StartAsync(
        string requestId,
        ImageImportPreflightStartParameters parameters,
        CancellationToken cancellationToken = default)
    {
        var started = _catalog.Start(requestId, parameters);
        var run = _catalog.Get(new ImageImportPreflightGetParameters(started.PreflightRunId));
        if (run.Status == "interrupted")
        {
            run = _catalog.RefreshInterruptedSourceBinding(run.PreflightRunId);
        }

        if (run.Status is "queued" or "interrupted")
        {
            await EnqueueAsync(run.PreflightRunId, cancellationToken);
        }

        return run;
    }

    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        _catalog.InterruptRunningRuns();
        foreach (var runId in _catalog.ListRecoverableRunIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnqueueAsync(runId, cancellationToken);
        }
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
            try
            {
                await ProcessRunAsync(runId, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                TryInterrupt(runId, "control_stopped");
                throw;
            }
            catch (Exception exception) when (IsExpectedOperationalFailure(exception))
            {
                TryInterrupt(runId, ErrorCode(exception));
            }
            finally
            {
                _pendingOrActiveRuns.TryRemove(runId, out _);
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private async Task ProcessRunAsync(string runId, CancellationToken cancellationToken)
    {
        var binding = _catalog.GetRunBinding(runId);
        if (binding.Status is "completed" or "failed")
        {
            return;
        }

        _catalog.MarkRunning(runId);
        var manifest = await _sourceSecurity.LoadRecoveryManifestAsync(
            binding.ImportSessionId,
            cancellationToken);
        ValidateManifestBinding(binding, manifest);

        var sidecarDiscovery = await _sourceDiscovery.DiscoverPreflightSidecarsAsync(
            manifest,
            _controlDataPaths,
            cancellationToken);
        manifest = sidecarDiscovery.RecoveryManifest;
        var sidecars = new List<SourcePreflightSidecarCandidate>(sidecarDiscovery.Candidates.Count);
        foreach (var candidate in sidecarDiscovery.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identityKey = candidate.Snapshot.Identity is null
                ? null
                : await _sourceSecurity.CreateSourceIdentityKeyAsync(
                    candidate.SourceItemKey,
                    candidate.Snapshot.Identity,
                    cancellationToken);
            sidecars.Add(new SourcePreflightSidecarCandidate(
                candidate.SourceItemKey,
                candidate.LeafDisplayName,
                FormatHint(candidate.LeafDisplayName),
                candidate.Snapshot with { Identity = identityKey }));
        }

        _catalog.AddSidecarItems(runId, sidecars);
        var allItems = _catalog.ListWorkItems(runId, includeCompleted: true);
        var association = BuildAssociationState(allItems, manifest);

        foreach (var item in allItems.Where(item => item.Status == "queued"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _catalog.MarkItemRunning(item.ItemId);
            try
            {
                var expectedSnapshot = await ResolveExpectedSnapshotAsync(
                    item,
                    manifest,
                    cancellationToken);
                var associationItemCount = AssociationItemCount(item, association);
                var result = await _probe.AnalyzeAsync(
                    manifest,
                    item.SourceEntryKey,
                    expectedSnapshot,
                    item.CandidateKind,
                    item.FormatHint,
                    associationItemCount,
                    cancellationToken);

                if (IsStrongMrkResult(item, result, association))
                {
                    association.StrongMrkGroups.Add(GroupFor(item.SourceEntryKey, manifest));
                }

                result = ApplyStrongMrkCoverage(item, result, association, manifest);
                _catalog.CompleteItem(item.ItemId, result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedOperationalFailure(exception))
            {
                _catalog.CompleteItemReadFailure(item.ItemId, ErrorCode(exception));
            }
        }

        var completed = _catalog.CommitDecision(runId);
        if (completed.Decision == "dji_supported")
        {
            await _imageImports.EnqueueApprovedSessionAsync(
                completed.ImportSessionId,
                cancellationToken);
        }
    }

    private async Task<ImageImportSourceSnapshot> ResolveExpectedSnapshotAsync(
        SourcePreflightWorkItem item,
        ImageImportSourceRecoveryManifest manifest,
        CancellationToken cancellationToken)
    {
        if (manifest.SnapshotBySourceItemKey is null ||
            !manifest.SnapshotBySourceItemKey.TryGetValue(item.SourceEntryKey, out var snapshot) ||
            item.ByteLengthSnapshot != snapshot.Length ||
            item.SourceLastWriteTimeUtc != snapshot.LastWriteTimeUtc)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_reselection_mismatch",
                "The selected source no longer matches the source preflight ledger.");
        }

        if (item.SourceIdentityKey is not null)
        {
            if (snapshot.Identity is null)
            {
                throw new ImageImportSourceDiscoveryException(
                    "source_reselection_identity_unavailable",
                    "The selected source identity could not be verified for source preflight.");
            }

            var identityKey = await _sourceSecurity.CreateSourceIdentityKeyAsync(
                item.SourceEntryKey,
                snapshot.Identity,
                cancellationToken);
            if (!string.Equals(identityKey, item.SourceIdentityKey, StringComparison.Ordinal))
            {
                throw new ImageImportSourceDiscoveryException(
                    "source_reselection_mismatch",
                    "The selected source no longer matches the source preflight ledger.");
            }
        }

        return snapshot;
    }

    private static AssociationState BuildAssociationState(
        IReadOnlyList<SourcePreflightWorkItem> items,
        ImageImportSourceRecoveryManifest manifest)
    {
        var imageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var mrkCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var strongMrkGroups = new HashSet<string>(StringComparer.Ordinal);
        var groupBySourceEntryKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var group = GroupFor(item.SourceEntryKey, manifest);
            groupBySourceEntryKey[item.SourceEntryKey] = group;
            if (item.CandidateKind == "image_candidate")
            {
                imageCounts[group] = imageCounts.GetValueOrDefault(group) + 1;
            }
            else if (item.FormatHint == "mrk")
            {
                mrkCounts[group] = mrkCounts.GetValueOrDefault(group) + 1;
                if (item.Status == "completed" &&
                    item.EvidenceState == "supports_dji" &&
                    item.EvidenceKinds.Contains("dji_mrk_batch_coverage", StringComparer.Ordinal))
                {
                    strongMrkGroups.Add(group);
                }
            }
        }

        return new AssociationState(imageCounts, mrkCounts, strongMrkGroups, groupBySourceEntryKey);
    }

    private static int? AssociationItemCount(
        SourcePreflightWorkItem item,
        AssociationState association)
    {
        if (item.FormatHint != "mrk")
        {
            return null;
        }

        var group = association.GroupBySourceEntryKey[item.SourceEntryKey];
        return association.MrkCounts.GetValueOrDefault(group) == 1 &&
               association.ImageCounts.TryGetValue(group, out var imageCount) && imageCount > 0
            ? imageCount
            : null;
    }

    private static bool IsStrongMrkResult(
        SourcePreflightWorkItem item,
        ImageProbeSourcePreflightResult result,
        AssociationState association)
    {
        if (item.FormatHint != "mrk" || result.EvidenceState != "supports_dji" ||
            !result.EvidenceKinds.Contains("dji_mrk_batch_coverage", StringComparer.Ordinal))
        {
            return false;
        }

        var group = association.GroupBySourceEntryKey[item.SourceEntryKey];
        return association.MrkCounts.GetValueOrDefault(group) == 1 &&
               association.ImageCounts.GetValueOrDefault(group) > 0;
    }

    private static ImageProbeSourcePreflightResult ApplyStrongMrkCoverage(
        SourcePreflightWorkItem item,
        ImageProbeSourcePreflightResult result,
        AssociationState association,
        ImageImportSourceRecoveryManifest manifest)
    {
        if (item.CandidateKind != "image_candidate" ||
            result.EvidenceState != "unconfirmed" ||
            result.ContainerHint is not ("jpeg_hint" or "mpo_hint" or "tiff" or "bigtiff") ||
            result.ReasonCodes.Any(reason => reason != "dji_evidence_missing") ||
            !association.StrongMrkGroups.Contains(GroupFor(item.SourceEntryKey, manifest)))
        {
            return result;
        }

        return result with
        {
            EvidenceState = "supports_dji",
            EvidenceKinds = result.EvidenceKinds
                .Append("dji_mrk_batch_coverage")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            ReasonCodes = []
        };
    }

    private static void ValidateManifestBinding(
        SourcePreflightRunBinding binding,
        ImageImportSourceRecoveryManifest manifest)
    {
        if (!string.Equals(manifest.SessionId, binding.ImportSessionId, StringComparison.Ordinal) ||
            !string.Equals(manifest.SessionId, binding.SourceLocatorManifestId, StringComparison.Ordinal) ||
            !string.Equals(manifest.SourceRootKey, binding.SourceRootKey, StringComparison.Ordinal))
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_manifest_binding_mismatch",
                "The protected source locator does not match the source preflight ledger.");
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
                "A source preflight item is missing from the protected source locator.");
        }

        return (Path.GetDirectoryName(relativePath) ?? string.Empty)
            .Replace('\\', '/')
            .Normalize();
    }

    private static string FormatHint(string displayName) =>
        Path.GetExtension(displayName).TrimStart('.').ToLowerInvariant();

    private void TryInterrupt(string runId, string failureCode)
    {
        try
        {
            _catalog.InterruptRun(runId, failureCode);
        }
        catch (Exception exception) when (IsExpectedOperationalFailure(exception))
        {
        }
    }

    private static bool IsExpectedOperationalFailure(Exception exception) => exception is
        BusinessCatalogException or
        ImageImportSourceDiscoveryException or
        ImageImportSourceSecurityException or
        ImageSourcePreflightProbeException or
        Microsoft.Data.Sqlite.SqliteException or
        IOException or
        InvalidOperationException or
        UnauthorizedAccessException;

    private static string ErrorCode(Exception exception) => exception switch
    {
        BusinessCatalogException catalog => catalog.Code,
        ImageImportSourceDiscoveryException discovery => discovery.Code,
        ImageImportSourceSecurityException security => security.Code,
        ImageSourcePreflightProbeException probe => probe.Code,
        Microsoft.Data.Sqlite.SqliteException => "source_preflight_catalog_failed",
        OperationCanceledException => "source_preflight_cancelled",
        _ => "source_preflight_operation_failed"
    };

    private sealed class AssociationState
    {
        public AssociationState(
            IReadOnlyDictionary<string, int> imageCounts,
            IReadOnlyDictionary<string, int> mrkCounts,
            HashSet<string> strongMrkGroups,
            IReadOnlyDictionary<string, string> groupBySourceEntryKey)
        {
            ImageCounts = imageCounts;
            MrkCounts = mrkCounts;
            StrongMrkGroups = strongMrkGroups;
            GroupBySourceEntryKey = groupBySourceEntryKey;
        }

        public IReadOnlyDictionary<string, int> ImageCounts { get; }

        public IReadOnlyDictionary<string, int> MrkCounts { get; }

        public HashSet<string> StrongMrkGroups { get; }

        public IReadOnlyDictionary<string, string> GroupBySourceEntryKey { get; }
    }
}
