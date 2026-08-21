using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.Control;

public sealed class ControlRequestDispatcher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly ArtifactServer _artifactServer;
    private readonly WorkerSupervisor _workers;
    private readonly Action _requestStop;

    public ControlRequestDispatcher(
        string pipeName,
        DateTimeOffset startedAtUtc,
        ArtifactServer artifactServer,
        WorkerSupervisor workers,
        Action requestStop)
    {
        _pipeName = pipeName;
        _startedAtUtc = startedAtUtc;
        _artifactServer = artifactServer;
        _workers = workers;
        _requestStop = requestStop;
    }

    public async Task<ControlResponse> DispatchAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        if (request.ApiVersion != ContractVersions.ControlApiV1)
        {
            return Failure(request, "unsupported_api_version", "The control API version is not supported.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128)
        {
            return Failure(request, "invalid_request_id", "A bounded request identifier is required.");
        }

        try
        {
            object result = request.Method switch
            {
                ControlMethods.Hello or ControlMethods.Status => CreateStatus(),
                ControlMethods.ArtifactSession => _artifactServer.CreateSession(),
                ControlMethods.WorkerStart => StartWorker(ReadParameters<WorkerStartParameters>(request)),
                ControlMethods.WorkerList => _workers.List(),
                ControlMethods.WorkerCancel => await CancelWorkerAsync(request, cancellationToken),
                ControlMethods.StopIfIdle => StopIfIdle(),
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
    }

    private ControlRuntimeStatus CreateStatus() => new(
        ContractVersions.ControlApiV1,
        Environment.ProcessId,
        _pipeName,
        _artifactServer.BaseUrl,
        _workers.ActiveCount,
        _startedAtUtc);

    private WorkerSnapshot StartWorker(WorkerStartParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.WorkerType) || parameters.WorkerType.Length > 128)
        {
            throw new ControlProtocolException("invalid_worker_type", "A bounded worker type is required.");
        }

        return _workers.Start(parameters.WorkerType);
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

    private static ControlResponse Failure(ControlRequest request, string code, string message) => new(
        ContractVersions.ControlApiV1,
        request.RequestId,
        false,
        null,
        new ControlError(code, message));
}
