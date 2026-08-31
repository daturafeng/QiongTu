using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control;

internal interface IImageMetadataProbeClient
{
    Task<ImageProbeImageMetadataResult> AnalyzeAsync(
        ContentAddressedObjectStore objectStore,
        PublishedObject normalizedObject,
        CancellationToken cancellationToken);
}

internal sealed class ImageMetadataCoordinator : IAsyncDisposable
{
    private const int MaximumStateStepsPerDispatch = 4;
    private readonly ImageMetadataCatalog _catalog;
    private readonly ContentAddressedObjectStore _objectStore;
    private readonly IImageMetadataProbeClient _probeClient;
    private readonly Channel<string> _queue;
    private readonly ConcurrentDictionary<string, byte> _pendingOrActive = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private int _queued;
    private int _active;

    public ImageMetadataCoordinator(
        ImageMetadataCatalog catalog,
        ContentAddressedObjectStore objectStore,
        IImageMetadataProbeClient probeClient,
        int queueCapacity = 128)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _objectStore = objectStore ?? throw new ArgumentNullException(nameof(objectStore));
        _probeClient = probeClient ?? throw new ArgumentNullException(nameof(probeClient));
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

    public Task EnqueueImageAsync(string imageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var run = _catalog.EnsureRun(imageId);
        if (run.Status is not ("completed" or "blocked"))
        {
            _ = TryQueue(run.MetadataRunId);
        }

        return Task.CompletedTask;
    }

    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        foreach (var imageId in _catalog.ListRecoverableImageIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = _catalog.EnsureRun(imageId);
            if (run.Status == "parsing")
            {
                _catalog.MarkInterrupted(run.MetadataRunId);
            }

            await EnqueueImageAsync(imageId, cancellationToken);
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
        await foreach (var metadataRunId in _queue.Reader.ReadAllAsync(_stop.Token))
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _active);
            try
            {
                await ProcessRunAsync(metadataRunId, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                TryMarkInterrupted(metadataRunId);
            }
            catch (ImageCasProbeException exception)
            {
                TryBlock(metadataRunId, exception.Code);
            }
            catch (ObjectStoreException exception)
            {
                TryBlock(metadataRunId, exception.Code);
            }
            catch (BusinessCatalogException exception) when (IsSafeBlockingCode(exception.Code))
            {
                TryBlock(metadataRunId, exception.Code);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or SqliteException)
            {
                TryMarkInterrupted(metadataRunId);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
                _pendingOrActive.TryRemove(metadataRunId, out _);
                TryQueueOnePersistedCandidate();
            }
        }
    }

    private bool TryQueue(string metadataRunId)
    {
        if (!_pendingOrActive.TryAdd(metadataRunId, 0))
        {
            return false;
        }

        Interlocked.Increment(ref _queued);
        if (_queue.Writer.TryWrite(metadataRunId))
        {
            return true;
        }

        Interlocked.Decrement(ref _queued);
        _pendingOrActive.TryRemove(metadataRunId, out _);
        return false;
    }

    private void TryQueueOnePersistedCandidate()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        foreach (var imageId in _catalog.ListRecoverableImageIds(limit: 1))
        {
            var run = _catalog.EnsureRun(imageId);
            if (TryQueue(run.MetadataRunId))
            {
                return;
            }
        }
    }

    private async Task ProcessRunAsync(string metadataRunId, CancellationToken cancellationToken)
    {
        for (var step = 0; step < MaximumStateStepsPerDispatch; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = _catalog.GetRun(metadataRunId);
            switch (run.Status)
            {
                case "completed":
                case "blocked":
                    return;
                case "pending":
                case "interrupted":
                    _catalog.BeginParsing(metadataRunId);
                    continue;
                case "parsing":
                    await ParseAndRecordAsync(run, cancellationToken);
                    return;
                default:
                    _catalog.Block(metadataRunId, "image_metadata_state_conflict");
                    return;
            }
        }

        _catalog.MarkInterrupted(metadataRunId);
    }

    private async Task ParseAndRecordAsync(ImageMetadataRunSnapshot run, CancellationToken cancellationToken)
    {
        var normalizedObject = new PublishedObject(
            run.NormalizedSha256,
            run.NormalizedByteLength,
            run.NormalizedObjectKey,
            Deduplicated: true);
        var result = await _probeClient.AnalyzeAsync(
            _objectStore,
            normalizedObject,
            cancellationToken);
        if (result.Status != "completed")
        {
            _catalog.Block(run.MetadataRunId, result.ReasonCodes.FirstOrDefault() ?? "image_metadata_probe_blocked");
            return;
        }

        _catalog.Complete(run.MetadataRunId, result.Fields.Select(ToCatalogField).ToArray());
    }

    private static ImageMetadataCatalogField ToCatalogField(ImageProbeImageMetadataField field)
    {
        if (field.FieldState is not ("present" or "conflict"))
        {
            if (field.TextValue is not null || field.NumericValue is not null || field.BooleanValue is not null)
            {
                throw new BusinessCatalogException(
                    "image_metadata_value_type_invalid",
                    "The metadata probe returned a value for a non-value metadata state.");
            }

            return new ImageMetadataCatalogField(
                field.FieldName,
                null,
                field.SourceKind,
                field.FieldState,
                field.SourceDetail);
        }

        string? valueJson = field.ValueType switch
        {
            "text" when field.TextValue is not null && field.NumericValue is null && field.BooleanValue is null =>
                JsonSerializer.Serialize(field.TextValue),
            "number" when field.TextValue is null && field.NumericValue is not null && field.BooleanValue is null =>
                JsonSerializer.Serialize(field.NumericValue.Value),
            "boolean" when field.TextValue is null && field.NumericValue is null && field.BooleanValue is not null =>
                JsonSerializer.Serialize(field.BooleanValue.Value),
            _ => throw new BusinessCatalogException(
                "image_metadata_value_type_invalid",
                "The metadata probe returned an inconsistent typed value.")
        };
        return new ImageMetadataCatalogField(
            field.FieldName,
            valueJson,
            field.SourceKind,
            field.FieldState,
            field.SourceDetail);
    }

    private void TryMarkInterrupted(string metadataRunId)
    {
        try
        {
            _catalog.MarkInterrupted(metadataRunId);
        }
        catch (BusinessCatalogException)
        {
        }
    }

    private void TryBlock(string metadataRunId, string code)
    {
        try
        {
            _catalog.Block(metadataRunId, IsSafeCode(code) ? code : "image_metadata_failed");
        }
        catch (BusinessCatalogException)
        {
            TryMarkInterrupted(metadataRunId);
        }
    }

    private static bool IsSafeBlockingCode(string code) =>
        code is "image_metadata_field_count_invalid" or "image_metadata_field_invalid" or
            "image_metadata_value_state_invalid" or "image_metadata_value_limit_exceeded" or
            "image_metadata_value_type_invalid" or "image_metadata_value_json_invalid" or
            "image_metadata_fields_incomplete" or "image_metadata_inventory_conflict" or
            "image_metadata_source_invalid";

    private static bool IsSafeCode(string code) =>
        !string.IsNullOrWhiteSpace(code) && code.Length <= 128 &&
        code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
