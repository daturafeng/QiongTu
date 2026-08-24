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
    public const string ProjectCreate = "project.create";
    public const string ProjectList = "project.list";
    public const string ProjectGet = "project.get";
    public const string ProjectConfirmCrs = "project.confirm-crs";
    public const string CrsRecommend = "crs.recommend";
    public const string DatasetCreate = "dataset.create";
    public const string DatasetVersionCreate = "dataset-version.create";
    public const string DatasetVersionList = "dataset-version.list";
    public const string DatasetVersionGet = "dataset-version.get";
    public const string ResultList = "result.list";
    public const string ResultLineage = "result.lineage";
    public const string CapabilityGet = "capability.get";
    public const string WorkerAdmissionCheck = "worker.admission.check";
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

public sealed record WorkerAdmissionCheckParameters(string WorkerType);

public sealed record CapabilityHost(
    string Status,
    string OperatingSystem,
    string Architecture,
    string ProcessArchitecture,
    string SessionKind);

public sealed record CpuCapability(
    string Status,
    int? LogicalProcessorCount,
    string? Architecture);

public sealed record MemoryCapability(
    string Status,
    long? TotalBytes,
    long? AvailableBytes);

public sealed record StorageCapability(
    string Role,
    long? TotalBytes,
    long? AvailableBytes,
    string DriveType,
    string Status);

public sealed record GpuCapability(
    int Index,
    string Name,
    long? TotalMemoryBytes,
    long? FreeMemoryBytes,
    string Status);

public sealed record NvidiaCapability(
    string Status,
    string CudaStatus,
    string? DriverVersion,
    string? CudaDriverApiVersion,
    string? ReasonCode,
    IReadOnlyList<GpuCapability> Gpus);

public sealed record WorkerAdmissionBlockingReason(
    string Category,
    string Code,
    string Message);

public sealed record WorkerAdmissionResult(
    string WorkerType,
    string Profile,
    string Decision,
    IReadOnlyList<WorkerAdmissionBlockingReason> BlockingReasons);

public sealed record CapabilityPrivacy(
    bool PathsIncluded,
    bool TokensIncluded,
    bool UserNameIncluded,
    bool MachineNameIncluded,
    bool EnvironmentIncluded,
    bool CommandLineIncluded);

public sealed record ProcessingCapabilityReport(
    string SchemaVersion,
    string RequirementsVersion,
    DateTimeOffset CapturedAt,
    int DurationMs,
    CapabilityHost Host,
    CpuCapability Cpu,
    MemoryCapability Memory,
    IReadOnlyList<StorageCapability> Storage,
    NvidiaCapability Nvidia,
    IReadOnlyList<WorkerAdmissionResult> WorkerAdmissions,
    CapabilityPrivacy Privacy);

public sealed record PageRequest(int? PageSize, string? Cursor);

public sealed record PageResult<TItem>(
    IReadOnlyList<TItem> Items,
    string? NextCursor);

public sealed record Wgs84Bounds(
    double WestLongitude,
    double SouthLatitude,
    double EastLongitude,
    double NorthLatitude);

public sealed record CrsDefinitionInput(
    string? Authority,
    string? Code,
    string Name,
    string? Wkt,
    string? Projjson,
    string? CrsType,
    string HorizontalUnit,
    string? VerticalReference,
    string AxisOrder);

public sealed record CrsSnapshot(
    string? Authority,
    string? Code,
    string Name,
    string? Wkt,
    string? Projjson,
    string? CrsType,
    string HorizontalUnit,
    string? VerticalReference,
    string AxisOrder,
    DateTimeOffset CapturedAtUtc);

public sealed record CrsRecommendation(
    string Status,
    Wgs84Bounds? InputBounds,
    CrsSnapshot? SuggestedCrs,
    string? ReasonCode);

public sealed record ProjectCreateParameters(
    string Name,
    string? Description,
    CrsDefinitionInput? DefaultCrs);

public sealed record ProjectListParameters(
    int? PageSize,
    string? Cursor);

public sealed record ProjectGetParameters(string ProjectId);

public sealed record ProjectConfirmCrsParameters(
    string ProjectId,
    string ExpectedUpdatedAtUtc,
    CrsDefinitionInput Crs);

public sealed record CrsRecommendParameters(Wgs84Bounds? Bounds);

public sealed record Project(
    string ProjectId,
    string Name,
    string? Description,
    string SpatialConfigurationStatus,
    string LifecycleState,
    CrsSnapshot? DefaultCrs,
    CrsSnapshot? SuggestedCrs,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record DatasetCreateParameters(
    string ProjectId,
    string Name,
    string? Description);

public sealed record Dataset(
    string DatasetId,
    string ProjectId,
    string Name,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record DatasetVersionCreateParameters(
    string DatasetId,
    string? ParentVersionId);

public sealed record DatasetVersionListParameters(
    string DatasetId,
    int? PageSize,
    string? Cursor);

public sealed record DatasetVersionGetParameters(string DatasetVersionId);

public sealed record DatasetVersion(
    string DatasetVersionId,
    string DatasetId,
    int VersionNumber,
    string? ParentVersionId,
    string LifecycleState,
    string SourceEligibilityState,
    string QualityGateState,
    string? ContentManifestSha256,
    DateTimeOffset? WarningAcknowledgedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? SealedAtUtc);

public sealed record ResultListParameters(
    string? ProjectId,
    string? DatasetVersionId,
    int? PageSize,
    string? Cursor);

public sealed record ResultLineageParameters(string ResultId);

public sealed record QualityReportSummary(
    string QualityReportId,
    string ReportType,
    int VersionNumber,
    string LifecycleState,
    string SchemaVersion,
    string SummarySeverity,
    JsonElement? Summary,
    int BlockingCount,
    int WarningCount,
    int InfoCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? FinalizedAtUtc);

public sealed record ResultFile(
    string ResultFileId,
    string ResultId,
    string FileObjectId,
    string FileRole,
    string RelativePath,
    bool IsRequired,
    long ByteLengthSnapshot,
    string ContentHashSnapshot,
    string ObjectKey,
    string? MediaType);

public sealed record ResultSummary(
    string ResultId,
    string ResultSeriesId,
    int VersionNumber,
    string SourceDatasetVersionId,
    string SourceProcessingJobId,
    string SourceJobExecutionId,
    string? SourceResultId,
    string ResultKind,
    string LifecycleState,
    CrsSnapshot? Crs,
    string? VerticalReference,
    JsonElement? LocalOrigin,
    string? AxisConvention,
    string? Unit,
    JsonElement? Bounds,
    JsonElement? ResolutionDensity,
    string? EngineVersion,
    string? ConverterVersion,
    string ParameterSha256,
    string AccuracyLevel,
    QualityReportSummary? QualityReport,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    string? SupersededByResultId);

public sealed record ResultDependency(
    string ResultId,
    string DependsOnResultId,
    string DependencyKind);

public sealed record ResultSeriesSummary(
    string ResultSeriesId,
    string ProjectId,
    string? DatasetVersionId,
    string SeriesKind,
    string Name,
    string? ParentSeriesId,
    DateTimeOffset CreatedAtUtc);

public sealed record ProcessingJobSummary(
    string ProcessingJobId,
    string JobType,
    string ParameterProfile,
    string ParameterSchemaVersion,
    string ParameterSha256,
    string LifecycleState,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset SubmittedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc);

public sealed record JobExecutionSummary(
    string JobExecutionId,
    string ProcessingJobId,
    int AttemptNumber,
    string ExecutionMode,
    string WorkerType,
    string WorkerVersion,
    string? EngineName,
    string? EngineVersion,
    string ParameterSha256,
    string LifecycleState,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc);

public sealed record ResultLineage(
    ResultSummary Target,
    ResultSeriesSummary Series,
    Project Project,
    DatasetVersion SourceDatasetVersion,
    ProcessingJobSummary SourceProcessingJob,
    JobExecutionSummary SourceJobExecution,
    IReadOnlyList<ResultDependency> DirectDependencies,
    IReadOnlyList<ResultFile> AvailableFiles,
    IReadOnlyList<QualityReportSummary> FinalQualityReports);
