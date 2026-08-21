using System.Text.Json;

namespace QiongTu.Contracts;

public static class ControlMethods
{
    public const string Hello = "control.hello";
    public const string Status = "control.status";
    public const string StopIfIdle = "control.stop-if-idle";
    public const string ArtifactSession = "artifact.session";
    public const string WorkerStart = "worker.start";
    public const string WorkerList = "worker.list";
    public const string WorkerCancel = "worker.cancel";
}

public sealed record ControlRequest(
    string ApiVersion,
    string RequestId,
    string Method,
    JsonElement? Parameters);

public sealed record ControlResponse(
    string ApiVersion,
    string RequestId,
    bool Ok,
    object? Result,
    ControlError? Error);

public sealed record ControlError(string Code, string Message);

public sealed record ControlDiscovery(
    string ApiVersion,
    string EndpointKind,
    int ProcessId,
    string PipeName,
    DateTimeOffset StartedAtUtc);

public sealed record ControlRuntimeStatus(
    string ApiVersion,
    int ProcessId,
    string PipeName,
    string ArtifactBaseUrl,
    int ActiveWorkerCount,
    DateTimeOffset StartedAtUtc);

public sealed record ArtifactSession(string BaseUrl, string AccessToken);

public sealed record WorkerStartParameters(string WorkerType);

public sealed record WorkerCancelParameters(string WorkerId);

public sealed record WorkerSnapshot(
    string WorkerId,
    string WorkerType,
    string State,
    int? ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    int? ExitCode);
