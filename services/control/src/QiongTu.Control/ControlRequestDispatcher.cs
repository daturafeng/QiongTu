using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control;

public sealed class ControlRequestDispatcher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _pipeName;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly ArtifactServer _artifactServer;
    private readonly WorkerSupervisor _workers;
    private readonly BusinessCatalog _catalog;
    private readonly ProcessingCapabilityService _capabilities;
    private readonly Action _requestStop;
    private readonly ImageImportCoordinator? _imageImports;
    private readonly ImageImportCatalog? _imageImportCatalog;
    private readonly ControlDataPaths? _controlDataPaths;
    private readonly ImageImportPreflightCoordinator? _imageImportPreflights;
    private readonly ImageImportPreflightCatalog? _imageImportPreflightCatalog;

    internal ControlRequestDispatcher(
        string pipeName,
        DateTimeOffset startedAtUtc,
        ArtifactServer artifactServer,
        WorkerSupervisor workers,
        BusinessCatalog catalog,
        ProcessingCapabilityService capabilities,
        Action requestStop,
        ImageImportCoordinator? imageImports = null,
        ImageImportCatalog? imageImportCatalog = null,
        ControlDataPaths? controlDataPaths = null,
        ImageImportPreflightCoordinator? imageImportPreflights = null,
        ImageImportPreflightCatalog? imageImportPreflightCatalog = null)
    {
        _pipeName = pipeName;
        _startedAtUtc = startedAtUtc;
        _artifactServer = artifactServer;
        _workers = workers;
        _catalog = catalog;
        _capabilities = capabilities;
        _requestStop = requestStop;
        _imageImports = imageImports;
        _imageImportCatalog = imageImportCatalog;
        _controlDataPaths = controlDataPaths;
        _imageImportPreflights = imageImportPreflights;
        _imageImportPreflightCatalog = imageImportPreflightCatalog;
    }

    public async Task<ControlResponse> DispatchAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        if (request.ApiVersion != ContractVersions.ControlApiV1)
        {
            return Failure(request, "unsupported_api_version", "The control API version is not supported.");
        }

        if (!IsValidRequestId(request.RequestId))
        {
            return Failure(request, "invalid_request_id", "A bounded request identifier is required.");
        }

        try
        {
            object result = request.Method switch
            {
                ControlMethods.Hello or ControlMethods.Status => CreateStatus(),
                ControlMethods.ArtifactSession => _artifactServer.CreateSession(),
                ControlMethods.WorkerStart => await StartWorkerAsync(
                    ReadParameters<WorkerStartParameters>(request),
                    cancellationToken),
                ControlMethods.WorkerList => _workers.List(),
                ControlMethods.WorkerCancel => await CancelWorkerAsync(request, cancellationToken),
                ControlMethods.StopIfIdle => StopIfIdle(),
                ControlMethods.ProjectCreate => _catalog.CreateProject(
                    request.RequestId,
                    ReadParameters<ProjectCreateParameters>(request)),
                ControlMethods.ProjectList => _catalog.ListProjects(
                    ReadOptionalParameters(request, new ProjectListParameters(null, null))),
                ControlMethods.ProjectGet => _catalog.GetProject(ReadParameters<ProjectGetParameters>(request)),
                ControlMethods.ProjectConfirmCrs => _catalog.ConfirmCrs(
                    request.RequestId,
                    ReadParameters<ProjectConfirmCrsParameters>(request)),
                ControlMethods.CrsRecommend => _catalog.RecommendCrs(ReadParameters<CrsRecommendParameters>(request)),
                ControlMethods.DatasetCreate => _catalog.CreateDataset(
                    request.RequestId,
                    ReadParameters<DatasetCreateParameters>(request)),
                ControlMethods.DatasetVersionCreate => _catalog.CreateDatasetVersion(
                    request.RequestId,
                    ReadParameters<DatasetVersionCreateParameters>(request)),
                ControlMethods.DatasetVersionList => _catalog.ListDatasetVersions(
                    ReadParameters<DatasetVersionListParameters>(request)),
                ControlMethods.DatasetVersionGet => _catalog.GetDatasetVersion(
                    ReadParameters<DatasetVersionGetParameters>(request)),
                ControlMethods.ResultList => _catalog.ListResults(ReadParameters<ResultListParameters>(request)),
                ControlMethods.ResultLineage => _catalog.GetResultLineage(
                    ReadParameters<ResultLineageParameters>(request)),
                ControlMethods.CapabilityGet => await GetCapabilitiesAsync(request, cancellationToken),
                ControlMethods.WorkerAdmissionCheck => await CheckWorkerAdmissionAsync(request, cancellationToken),
                ControlMethods.ImageImportStart => await StartImageImportAsync(request, cancellationToken),
                ControlMethods.ImageImportResume => await ResumeImageImportAsync(request, cancellationToken),
                ControlMethods.ImageImportCancel => CancelImageImport(request),
                ControlMethods.ImageImportGet => RequireImageImportCatalog().Get(
                    ReadParameters<ImageImportGetParameters>(request)),
                ControlMethods.ImageImportList => RequireImageImportCatalog().List(
                    ReadOptionalParameters(request, new ImageImportListParameters(null, null, null))),
                ControlMethods.ImageImportEntryList => RequireImageImportCatalog().ListEntries(
                    ReadParameters<ImageImportEntryListParameters>(request)),
                ControlMethods.ImageImportPreflightStart => await StartImageImportPreflightAsync(
                    request,
                    cancellationToken),
                ControlMethods.ImageImportPreflightGet => RequireImageImportPreflightCatalog().Get(
                    ReadParameters<ImageImportPreflightGetParameters>(request)),
                ControlMethods.ImageImportPreflightItemList => RequireImageImportPreflightCatalog().ListItems(
                    ReadParameters<ImageImportPreflightItemListParameters>(request)),
                _ => throw new ControlProtocolException("method_not_found", "The requested control method is not available.")
            };
            return new ControlResponse(
                ContractVersions.ControlApiV1,
                request.RequestId,
                true,
                result,
                null);
        }
        catch (ControlProtocolException exception)
        {
            return Failure(request, exception.Code, exception.Message, exception.Details);
        }
        catch (BusinessCatalogException exception)
        {
            return Failure(request, exception.Code, exception.Message);
        }
        catch (ImageImportSourceDiscoveryException exception)
        {
            return Failure(request, exception.Code, exception.Message);
        }
        catch (ImageImportSourceSecurityException exception)
        {
            return Failure(request, exception.Code, exception.Message);
        }
        catch (ImageSourcePreflightProbeException exception)
        {
            return Failure(request, exception.Code, exception.Message);
        }
        catch (ObjectStoreException exception)
        {
            return Failure(request, exception.Code, exception.Message);
        }
        catch (JsonException)
        {
            return Failure(request, "invalid_parameters", "The request parameters are invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(request, "request_cancelled", "The control request was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            return Failure(request, "control_operation_failed", "The control operation could not be completed safely.");
        }
        catch (SqliteException)
        {
            return Failure(request, "catalog_operation_failed", "The business catalog operation could not be completed safely.");
        }
    }

    private ControlRuntimeStatus CreateStatus() => new(
        ContractVersions.ControlApiV1,
        Environment.ProcessId,
        _pipeName,
        _artifactServer.BaseUrl,
        _workers.ActiveCount,
        _startedAtUtc);

    private async Task<WorkerSnapshot> StartWorkerAsync(
        WorkerStartParameters parameters,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parameters.WorkerType) || parameters.WorkerType.Length > 128)
        {
            throw new ControlProtocolException("invalid_worker_type", "A bounded worker type is required.");
        }

        return await _workers.StartAsync(parameters.WorkerType, cancellationToken);
    }

    private async Task<ProcessingCapabilityReport> GetCapabilitiesAsync(
        ControlRequest request,
        CancellationToken cancellationToken)
    {
        RequireEmptyParameters(request);
        return await _capabilities.CaptureAsync(cancellationToken);
    }

    private async Task<WorkerAdmissionResult> CheckWorkerAdmissionAsync(
        ControlRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = ReadParameters<WorkerAdmissionCheckParameters>(request);
        if (string.IsNullOrWhiteSpace(parameters.WorkerType) || parameters.WorkerType.Length > 128)
        {
            throw new ControlProtocolException("invalid_worker_type", "A bounded worker type is required.");
        }

        return await _capabilities.CheckAsync(parameters.WorkerType, cancellationToken);
    }

    private async Task<WorkerSnapshot> CancelWorkerAsync(
        ControlRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = ReadParameters<WorkerCancelParameters>(request);
        return await _workers.CancelAsync(parameters.WorkerId, cancellationToken);
    }

    private async Task<ImageImportSession> StartImageImportAsync(
        ControlRequest request,
        CancellationToken cancellationToken)
    {
        var paths = _controlDataPaths ?? throw new ControlProtocolException(
            "image_import_unavailable",
            "Image import is not configured for this control process.");
        var parameters = ReadParameters<ImageImportStartParameters>(request);
        return await RequireImageImportCoordinator().StartAsync(
            request.RequestId,
            CreateImportSessionId(request.RequestId),
            parameters.DatasetVersionId,
            parameters.SourceRootPath,
            paths,
            cancellationToken);
    }

    private async Task<ImageImportSession> ResumeImageImportAsync(
        ControlRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = ReadParameters<ImageImportResumeParameters>(request);
        return await RequireImageImportCoordinator().ResumeAsync(
            request.RequestId,
            parameters.ImportSessionId,
            parameters.SourceRootPath,
            _controlDataPaths,
            cancellationToken);
    }

    private ImageImportSession CancelImageImport(ControlRequest request)
    {
        var parameters = ReadParameters<ImageImportCancelParameters>(request);
        return RequireImageImportCoordinator().Cancel(request.RequestId, parameters.ImportSessionId);
    }

    private async Task<ImageImportPreflightRun> StartImageImportPreflightAsync(
        ControlRequest request,
        CancellationToken cancellationToken)
    {
        return await RequireImageImportPreflightCoordinator().StartAsync(
            request.RequestId,
            ReadParameters<ImageImportPreflightStartParameters>(request),
            cancellationToken);
    }

    private ImageImportCoordinator RequireImageImportCoordinator() =>
        _imageImports ?? throw new ControlProtocolException(
            "image_import_unavailable",
            "Image import is not configured for this control process.");

    private ImageImportCatalog RequireImageImportCatalog() =>
        _imageImportCatalog ?? throw new ControlProtocolException(
            "image_import_unavailable",
            "Image import is not configured for this control process.");

    private ImageImportPreflightCoordinator RequireImageImportPreflightCoordinator() =>
        _imageImportPreflights ?? throw new ControlProtocolException(
            "image_import_preflight_unavailable",
            "Image import source preflight is not configured for this control process.");

    private ImageImportPreflightCatalog RequireImageImportPreflightCatalog() =>
        _imageImportPreflightCatalog ?? throw new ControlProtocolException(
            "image_import_preflight_unavailable",
            "Image import source preflight is not configured for this control process.");

    private static string CreateImportSessionId(string requestId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(requestId));
        return "image-import-session-" + Convert.ToHexString(digest).ToLowerInvariant()[..32];
    }

    private object StopIfIdle()
    {
        if (_workers.ActiveCount != 0 ||
            (_imageImports is not null && !_imageImports.IsIdle) ||
            (_imageImportPreflights is not null && !_imageImportPreflights.IsIdle))
        {
            throw new ControlProtocolException("control_busy", "The control process still owns active workers.");
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            _requestStop();
        });
        return new { accepted = true };
    }

    private static T ReadParameters<T>(ControlRequest request)
    {
        if (request.Parameters is null)
        {
            throw new ControlProtocolException("invalid_parameters", "Request parameters are required.");
        }

        return request.Parameters.Value.Deserialize<T>(SerializerOptions)
            ?? throw new ControlProtocolException("invalid_parameters", "Request parameters are invalid.");
    }

    private static bool IsValidRequestId(string? requestId) =>
        requestId is { Length: > 0 and <= 128 } &&
        requestId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private static T ReadOptionalParameters<T>(ControlRequest request, T defaultValue)
    {
        if (request.Parameters is null || request.Parameters.Value.ValueKind is JsonValueKind.Null)
        {
            return defaultValue;
        }

        return request.Parameters.Value.Deserialize<T>(SerializerOptions)
            ?? throw new ControlProtocolException("invalid_parameters", "Request parameters are invalid.");
    }

    private static void RequireEmptyParameters(ControlRequest request)
    {
        if (request.Parameters is null || request.Parameters.Value.ValueKind is JsonValueKind.Null)
        {
            return;
        }

        if (request.Parameters.Value.ValueKind == JsonValueKind.Object &&
            !request.Parameters.Value.EnumerateObject().Any())
        {
            return;
        }

        throw new ControlProtocolException("invalid_parameters", "This request does not accept parameters.");
    }

    private static ControlResponse Failure(
        ControlRequest request,
        string code,
        string message,
        object? details = null) => new(
        ContractVersions.ControlApiV1,
        request.RequestId,
        false,
        null,
        new ControlError(code, message, details));
}
