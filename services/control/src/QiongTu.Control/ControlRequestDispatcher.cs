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

    internal ControlRequestDispatcher(
        string pipeName,
        DateTimeOffset startedAtUtc,
        ArtifactServer artifactServer,
        WorkerSupervisor workers,
        BusinessCatalog catalog,
        ProcessingCapabilityService capabilities,
        Action requestStop)
    {
        _pipeName = pipeName;
        _startedAtUtc = startedAtUtc;
        _artifactServer = artifactServer;
        _workers = workers;
        _catalog = catalog;
        _capabilities = capabilities;
        _requestStop = requestStop;
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
            return Failure(request, exception.Code, exception.Message);
        }
        catch (BusinessCatalogException exception)
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

    private object StopIfIdle()
    {
        if (_workers.ActiveCount != 0)
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

    private static ControlResponse Failure(ControlRequest request, string code, string message) => new(
        ContractVersions.ControlApiV1,
        request.RequestId,
        false,
        null,
        new ControlError(code, message));
}
