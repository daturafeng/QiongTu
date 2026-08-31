using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace QiongTu.Control;

internal sealed class ImageInspectionCoordinator : IAsyncDisposable
{
    private const int MaximumStateStepsPerDispatch = 8;

    private readonly ImageFrameCatalog _catalog;
    private readonly ContentAddressedObjectStore _objectStore;
    private readonly IImageCasProbeClient _probeClient;
    private readonly Func<string, CancellationToken, Task>? _onCompletedImage;
    private readonly Channel<string> _queue;
    private readonly ConcurrentDictionary<string, byte> _pendingOrActive = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private int _queued;
    private int _active;

    public ImageInspectionCoordinator(
        ImageFrameCatalog catalog,
        ContentAddressedObjectStore objectStore,
        IImageCasProbeClient probeClient,
        Func<string, CancellationToken, Task>? onCompletedImage = null,
        int queueCapacity = 128)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _objectStore = objectStore ?? throw new ArgumentNullException(nameof(objectStore));
        _probeClient = probeClient ?? throw new ArgumentNullException(nameof(probeClient));
        _onCompletedImage = onCompletedImage;
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        }

        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _worker = Task.Run(ProcessQueueAsync);
    }

    internal bool IsIdle => Volatile.Read(ref _queued) == 0 && Volatile.Read(ref _active) == 0;

    public Task EnqueueImportEntryAsync(string importEntryId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var run = _catalog.EnsureRun(importEntryId);
        if (run.Status is not ("completed" or "blocked"))
        {
            _ = TryQueue(run.InspectionRunId);
        }

        return Task.CompletedTask;
    }

    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        _ = await _objectStore.RecoverStagedAsync(cancellationToken);
        foreach (var importEntryId in _catalog.ListRecoverableImportEntryIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = _catalog.EnsureRun(importEntryId);
            if (run.Status == "probing")
            {
                _catalog.MarkInterrupted(run.InspectionRunId);
            }

            await EnqueueImportEntryAsync(importEntryId, cancellationToken);
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
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var inspectionRunId in _queue.Reader.ReadAllAsync(_stop.Token))
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _active);
            try
            {
                await ProcessRunAsync(inspectionRunId, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                TryMarkInterrupted(inspectionRunId);
            }
            catch (ImageCasProbeException exception)
            {
                TryBlock(inspectionRunId, exception.Code);
            }
            catch (ObjectStoreException exception)
            {
                TryBlock(inspectionRunId, exception.Code);
            }
            catch (BusinessCatalogException exception) when (IsSafeBlockingCode(exception.Code))
            {
                TryBlock(inspectionRunId, exception.Code);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or SqliteException)
            {
                TryMarkInterrupted(inspectionRunId);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
                _pendingOrActive.TryRemove(inspectionRunId, out _);
                TryQueueOnePersistedCandidate();
            }
        }
    }

    private bool TryQueue(string inspectionRunId)
    {
        if (!_pendingOrActive.TryAdd(inspectionRunId, 0))
        {
            return false;
        }

        Interlocked.Increment(ref _queued);
        if (_queue.Writer.TryWrite(inspectionRunId))
        {
            return true;
        }

        Interlocked.Decrement(ref _queued);
        _pendingOrActive.TryRemove(inspectionRunId, out _);
        return false;
    }

    private void TryQueueOnePersistedCandidate()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        foreach (var importEntryId in _catalog.ListRecoverableImportEntryIds())
        {
            var run = _catalog.EnsureRun(importEntryId);
            if (TryQueue(run.InspectionRunId))
            {
                return;
            }
        }
    }

    private async Task ProcessRunAsync(string inspectionRunId, CancellationToken cancellationToken)
    {
        for (var step = 0; step < MaximumStateStepsPerDispatch; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = _catalog.GetRun(inspectionRunId);
            switch (run.Status)
            {
                case "completed":
                case "blocked":
                    return;
                case "pending":
                    _catalog.BeginProbe(inspectionRunId);
                    continue;
                case "interrupted" when run.FrameInventoryJson is null:
                    _catalog.BeginProbe(inspectionRunId);
                    continue;
                case "probing":
                    await ProbeAndPlanAsync(run, cancellationToken);
                    continue;
                case "staged":
                    _catalog.MarkPublishing(inspectionRunId);
                    continue;
                case "interrupted" when run.NormalizedStageId is not null:
                    _catalog.MarkPublishing(inspectionRunId);
                    continue;
                case "publishing":
                    await PublishNormalizedFrameAsync(run, cancellationToken);
                    continue;
                case "interrupted" when run.NormalizedContentSha256 is not null:
                    await ResumePublishedOrReusableAsync(run, cancellationToken);
                    continue;
                case "recording":
                    var completion = _catalog.CompleteManifest(inspectionRunId);
                    if (completion.ImageId is not null && _onCompletedImage is not null)
                    {
                        await _onCompletedImage(completion.ImageId, cancellationToken);
                    }

                    return;
                default:
                    _catalog.Block(inspectionRunId, "image_inspection_state_conflict");
                    return;
            }
        }

        _catalog.MarkInterrupted(inspectionRunId);
    }

    private async Task ProbeAndPlanAsync(ImageInspectionRunSnapshot run, CancellationToken cancellationToken)
    {
        var sourceObject = new PublishedObject(
            run.SourceSha256,
            run.SourceByteLength,
            run.SourceObjectKey,
            Deduplicated: true);
        var result = await _probeClient.AnalyzeAsync(
            _objectStore,
            sourceObject,
            "source_image",
            cancellationToken);
        if (result.Status != "completed")
        {
            _catalog.Block(run.InspectionRunId, result.ReasonCodes.FirstOrDefault() ?? "cas_image_blocked");
            return;
        }

        var primaryFrame = ImageFrameCatalog.SelectPrimaryFrame(result);
        if (primaryFrame is null)
        {
            _catalog.Block(run.InspectionRunId, "primary_frame_not_found");
            return;
        }

        var inventoryJson = ImageFrameCatalog.SerializeInventory(result);
        var inventorySha256 = ImageFrameCatalog.InventorySha256(inventoryJson);
        switch (result.Container)
        {
            case "jpeg":
                _catalog.RecordReusableProbe(
                    run.InspectionRunId,
                    result,
                    primaryFrame,
                    inventoryJson,
                    inventorySha256,
                    "reuse_source_object");
                return;
            case "tiff":
                _catalog.RecordReusableProbe(
                    run.InspectionRunId,
                    result,
                    primaryFrame,
                    inventoryJson,
                    inventorySha256,
                    "reuse_source_tiff_page");
                return;
            case "mpo":
                if (primaryFrame.ByteOffset < 0 || primaryFrame.ByteLength <= 0)
                {
                    _catalog.Block(run.InspectionRunId, "primary_frame_not_extractable");
                    return;
                }

                var stage = await _objectStore.StagePublishedRangeAsync(
                    sourceObject,
                    primaryFrame.ByteOffset,
                    primaryFrame.ByteLength,
                    cancellationToken);
                _catalog.RecordStagedProbe(
                    run.InspectionRunId,
                    result,
                    primaryFrame,
                    inventoryJson,
                    inventorySha256,
                    stage);
                return;
            default:
                _catalog.Block(run.InspectionRunId, "unsupported_image_container");
                return;
        }
    }

    private async Task PublishNormalizedFrameAsync(
        ImageInspectionRunSnapshot run,
        CancellationToken cancellationToken)
    {
        if (run.NormalizedContentSha256 is null || run.NormalizedContentByteLength is null ||
            run.NormalizedStageId is null || run.NormalizedStageSha256 is null ||
            run.NormalizedStageByteLength is null || run.NormalizedStageCreatedAtUtc is null)
        {
            _catalog.Block(run.InspectionRunId, "image_stage_receipt_missing");
            return;
        }

        var published = await _objectStore.FindPublishedAsync(
            run.NormalizedContentSha256,
            run.NormalizedContentByteLength.Value,
            cancellationToken);
        if (published is null)
        {
            var stage = new ObjectStageReceipt(
                run.NormalizedStageId,
                run.NormalizedStageSha256,
                run.NormalizedStageByteLength.Value,
                run.NormalizedStageCreatedAtUtc.Value);
            published = await _objectStore.PublishAsync(stage, cancellationToken);
        }

        _catalog.MarkRecording(run.InspectionRunId, published);
    }

    private async Task ResumePublishedOrReusableAsync(
        ImageInspectionRunSnapshot run,
        CancellationToken cancellationToken)
    {
        if (run.NormalizedContentSha256 is null || run.NormalizedContentByteLength is null)
        {
            _catalog.BeginProbe(run.InspectionRunId);
            return;
        }

        var published = await _objectStore.FindPublishedAsync(
            run.NormalizedContentSha256,
            run.NormalizedContentByteLength.Value,
            cancellationToken);
        if (published is null)
        {
            if (run.NormalizedStageId is not null)
            {
                _catalog.MarkPublishing(run.InspectionRunId);
                return;
            }

            _catalog.Block(run.InspectionRunId, "object_formal_missing");
            return;
        }

        _catalog.MarkRecording(run.InspectionRunId, published);
    }

    private void TryMarkInterrupted(string inspectionRunId)
    {
        try
        {
            _catalog.MarkInterrupted(inspectionRunId);
        }
        catch (BusinessCatalogException)
        {
        }
    }

    private void TryBlock(string inspectionRunId, string code)
    {
        try
        {
            _catalog.Block(inspectionRunId, IsSafeCode(code) ? code : "image_inspection_failed");
        }
        catch (BusinessCatalogException)
        {
            TryMarkInterrupted(inspectionRunId);
        }
    }

    private static bool IsSafeBlockingCode(string code) =>
        code is "image_manifest_conflict" or "image_probe_identity_conflict" or
            "image_normalized_identity_conflict" or "primary_frame_not_extractable" or
            "primary_frame_not_found" or "image_stage_receipt_missing";

    private static bool IsSafeCode(string code) =>
        !string.IsNullOrWhiteSpace(code) && code.Length <= 128 &&
        code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
