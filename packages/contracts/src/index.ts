export const DESKTOP_API_VERSION = "qiongtu.desktop-api.v1" as const;
export const CONTROL_API_VERSION = "qiongtu.control-api.v1" as const;
export const LAUNCHER_API_VERSION = "qiongtu.launcher-api.v1" as const;
export const WORKER_CONTRACT_VERSION = "qiongtu.worker-contract.v1" as const;
export const CONTROL_STATUS_CHANNEL = "qiongtu:control-status" as const;
export const CONTROL_STATUS_CHANGED_CHANNEL = "qiongtu:control-status-changed" as const;

export type ControlConnectionState =
  | "not-connected"
  | "connecting"
  | "starting"
  | "reconnecting"
  | "connected"
  | "unavailable";

export type ControlEndpointKind = "named-pipe";

export type ControlStatusReason =
  | "discovery-missing"
  | "discovery-invalid"
  | "pipe-unreachable"
  | "process-not-found"
  | "process-started"
  | "process-start-failed"
  | "connected"
  | "disconnected"
  | "not-started";

export interface ControlConnectionStatus {
  readonly apiVersion: typeof CONTROL_API_VERSION;
  readonly state: ControlConnectionState;
  readonly endpointKind: ControlEndpointKind;
  readonly reason: ControlStatusReason;
  readonly detail: string;
  readonly retryAttempt: number;
  readonly checkedAt: string;
  readonly nextRetryDelayMs?: number;
}

export interface QiongTuDesktopBridge {
  readonly apiVersion: typeof DESKTOP_API_VERSION;
  getAppVersion(): Promise<string>;
  getControlStatus(): Promise<ControlConnectionStatus>;
  onControlStatusChanged(listener: (status: ControlConnectionStatus) => void): () => void;
}

export interface WorkerEnvelope<TPayload = unknown> {
  readonly contractVersion: typeof WORKER_CONTRACT_VERSION;
  readonly messageId: string;
  readonly messageType: string;
  readonly payload: TPayload;
}

export const CONTROL_METHOD_PROJECT_CREATE = "project.create" as const;
export const CONTROL_METHOD_PROJECT_LIST = "project.list" as const;
export const CONTROL_METHOD_PROJECT_GET = "project.get" as const;
export const CONTROL_METHOD_PROJECT_CONFIRM_CRS = "project.confirm-crs" as const;
export const CONTROL_METHOD_CRS_RECOMMEND = "crs.recommend" as const;
export const CONTROL_METHOD_DATASET_CREATE = "dataset.create" as const;
export const CONTROL_METHOD_DATASET_VERSION_CREATE = "dataset-version.create" as const;
export const CONTROL_METHOD_DATASET_VERSION_LIST = "dataset-version.list" as const;
export const CONTROL_METHOD_DATASET_VERSION_GET = "dataset-version.get" as const;
export const CONTROL_METHOD_RESULT_LIST = "result.list" as const;
export const CONTROL_METHOD_RESULT_LINEAGE = "result.lineage" as const;
export const CONTROL_METHOD_CAPABILITY_GET = "capability.get" as const;
export const CONTROL_METHOD_WORKER_ADMISSION_CHECK = "worker.admission.check" as const;
export const CONTROL_METHOD_IMAGE_IMPORT_START = "image-import.start" as const;
export const CONTROL_METHOD_IMAGE_IMPORT_RESUME = "image-import.resume" as const;
export const CONTROL_METHOD_IMAGE_IMPORT_CANCEL = "image-import.cancel" as const;
export const CONTROL_METHOD_IMAGE_IMPORT_GET = "image-import.get" as const;
export const CONTROL_METHOD_IMAGE_IMPORT_LIST = "image-import.list" as const;
export const CONTROL_METHOD_IMAGE_IMPORT_ENTRY_LIST = "image-import-entry.list" as const;
export const CONTROL_METHOD_IMAGE_IMPORT_PREFLIGHT_START = "image-import-preflight.start" as const;
export const CONTROL_METHOD_IMAGE_IMPORT_PREFLIGHT_GET = "image-import-preflight.get" as const;
export const CONTROL_METHOD_IMAGE_IMPORT_PREFLIGHT_ITEM_LIST = "image-import-preflight-item.list" as const;

export const PROJECT_DATASET_CONTROL_METHODS = [
  CONTROL_METHOD_PROJECT_CREATE,
  CONTROL_METHOD_PROJECT_LIST,
  CONTROL_METHOD_PROJECT_GET,
  CONTROL_METHOD_PROJECT_CONFIRM_CRS,
  CONTROL_METHOD_CRS_RECOMMEND,
  CONTROL_METHOD_DATASET_CREATE,
  CONTROL_METHOD_DATASET_VERSION_CREATE,
  CONTROL_METHOD_DATASET_VERSION_LIST,
  CONTROL_METHOD_DATASET_VERSION_GET,
  CONTROL_METHOD_RESULT_LIST,
  CONTROL_METHOD_RESULT_LINEAGE
] as const;

export type ProjectDatasetControlMethod = typeof PROJECT_DATASET_CONTROL_METHODS[number];

export const PROCESSING_CAPABILITY_CONTROL_METHODS = [
  CONTROL_METHOD_CAPABILITY_GET,
  CONTROL_METHOD_WORKER_ADMISSION_CHECK
] as const;

export type ProcessingCapabilityControlMethod = typeof PROCESSING_CAPABILITY_CONTROL_METHODS[number];

export const IMAGE_IMPORT_CONTROL_METHODS = [
  CONTROL_METHOD_IMAGE_IMPORT_START,
  CONTROL_METHOD_IMAGE_IMPORT_RESUME,
  CONTROL_METHOD_IMAGE_IMPORT_CANCEL,
  CONTROL_METHOD_IMAGE_IMPORT_GET,
  CONTROL_METHOD_IMAGE_IMPORT_LIST,
  CONTROL_METHOD_IMAGE_IMPORT_ENTRY_LIST,
  CONTROL_METHOD_IMAGE_IMPORT_PREFLIGHT_START,
  CONTROL_METHOD_IMAGE_IMPORT_PREFLIGHT_GET,
  CONTROL_METHOD_IMAGE_IMPORT_PREFLIGHT_ITEM_LIST
] as const;

export type ImageImportControlMethod = typeof IMAGE_IMPORT_CONTROL_METHODS[number];

export type JsonPrimitive = string | number | boolean | null;
export type JsonValue = JsonPrimitive | JsonObject | readonly JsonValue[];
export interface JsonObject {
  readonly [key: string]: JsonValue;
}

export type CapabilityStatus = "present" | "missing" | "unknown";
export type WorkerAdmissionDecision = "allowed" | "denied" | "unknown";
export type WorkerAdmissionBlockingCategory = "missing" | "unknown" | "incompatible" | "insufficient";

export interface WorkerAdmissionCheckParameters {
  readonly workerType: string;
}

export interface CapabilityHost {
  readonly status: CapabilityStatus;
  readonly operatingSystem: string;
  readonly architecture: string;
  readonly processArchitecture: string;
  readonly sessionKind: string;
}

export interface CpuCapability {
  readonly status: CapabilityStatus;
  readonly logicalProcessorCount?: number;
  readonly architecture?: string;
}

export interface MemoryCapability {
  readonly status: CapabilityStatus;
  readonly totalBytes?: number;
  readonly availableBytes?: number;
}

export interface StorageCapability {
  readonly role: string;
  readonly totalBytes?: number;
  readonly availableBytes?: number;
  readonly driveType: string;
  readonly status: CapabilityStatus;
}

export interface GpuCapability {
  readonly index: number;
  readonly name: string;
  readonly totalMemoryBytes?: number;
  readonly freeMemoryBytes?: number;
  readonly status: CapabilityStatus;
}

export interface NvidiaCapability {
  readonly status: CapabilityStatus;
  readonly cudaStatus: CapabilityStatus;
  readonly driverVersion?: string;
  readonly cudaDriverApiVersion?: string;
  readonly reasonCode?: string;
  readonly gpus: readonly GpuCapability[];
}

export interface WorkerAdmissionBlockingReason {
  readonly category: WorkerAdmissionBlockingCategory;
  readonly code: string;
  readonly message: string;
  readonly requiredValues?: Readonly<Record<string, number>>;
  readonly availableValues?: Readonly<Record<string, number>>;
}

export interface WorkerAdmissionResult {
  readonly workerType: string;
  readonly profile: string;
  readonly decision: WorkerAdmissionDecision;
  readonly blockingReasons: readonly WorkerAdmissionBlockingReason[];
}

export interface CapabilityPrivacy {
  readonly pathsIncluded: false;
  readonly tokensIncluded: false;
  readonly userNameIncluded: false;
  readonly machineNameIncluded: false;
  readonly environmentIncluded: false;
  readonly commandLineIncluded: false;
}

export interface ProcessingCapabilityReport {
  readonly schemaVersion: string;
  readonly requirementsVersion: string;
  readonly capturedAt: string;
  readonly durationMs: number;
  readonly host: CapabilityHost;
  readonly cpu: CpuCapability;
  readonly memory: MemoryCapability;
  readonly storage: readonly StorageCapability[];
  readonly nvidia: NvidiaCapability;
  readonly workerAdmissions: readonly WorkerAdmissionResult[];
  readonly privacy: CapabilityPrivacy;
}

export interface PageRequest {
  readonly pageSize?: number;
  readonly cursor?: string;
}

export interface PageResult<TItem> {
  readonly items: readonly TItem[];
  readonly nextCursor?: string;
}

export interface Wgs84Bounds {
  readonly westLongitude: number;
  readonly southLatitude: number;
  readonly eastLongitude: number;
  readonly northLatitude: number;
}

export interface CrsDefinitionInput {
  readonly authority?: string;
  readonly code?: string;
  readonly name: string;
  readonly wkt?: string;
  readonly projjson?: string;
  readonly crsType?: string;
  readonly horizontalUnit: string;
  readonly verticalReference?: string;
  readonly axisOrder: string;
}

export interface CrsSnapshot extends CrsDefinitionInput {
  readonly capturedAtUtc: string;
}

export type CrsRecommendationStatus = "recommended" | "not-recommended";

export interface CrsRecommendation {
  readonly status: CrsRecommendationStatus;
  readonly inputBounds?: Wgs84Bounds;
  readonly suggestedCrs?: CrsSnapshot;
  readonly reasonCode?: string;
}

export interface ProjectCreateParameters {
  readonly name: string;
  readonly description?: string;
  readonly defaultCrs?: CrsDefinitionInput;
}

export type ProjectListParameters = PageRequest;

export interface ProjectGetParameters {
  readonly projectId: string;
}

export interface ProjectConfirmCrsParameters {
  readonly projectId: string;
  readonly expectedUpdatedAtUtc: string;
  readonly crs: CrsDefinitionInput;
}

export interface CrsRecommendParameters {
  readonly bounds: Wgs84Bounds | null;
}

export type ProjectSpatialConfigurationStatus = "pending" | "suggested" | "confirmed" | "insufficient_metadata";

export interface Project {
  readonly projectId: string;
  readonly name: string;
  readonly description?: string;
  readonly spatialConfigurationStatus: ProjectSpatialConfigurationStatus;
  readonly lifecycleState: string;
  readonly defaultCrs?: CrsSnapshot;
  readonly suggestedCrs?: CrsSnapshot;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
}

export interface DatasetCreateParameters {
  readonly projectId: string;
  readonly name: string;
  readonly description?: string;
}

export interface Dataset {
  readonly datasetId: string;
  readonly projectId: string;
  readonly name: string;
  readonly description?: string;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
}

export interface DatasetVersionCreateParameters {
  readonly datasetId: string;
  readonly parentVersionId?: string;
}

export interface DatasetVersionListParameters extends PageRequest {
  readonly datasetId: string;
}

export interface DatasetVersionGetParameters {
  readonly datasetVersionId: string;
}

export interface DatasetVersion {
  readonly datasetVersionId: string;
  readonly datasetId: string;
  readonly versionNumber: number;
  readonly parentVersionId?: string;
  readonly lifecycleState: string;
  readonly sourceEligibilityState: string;
  readonly qualityGateState: string;
  readonly contentManifestSha256?: string;
  readonly warningAcknowledgedAtUtc?: string;
  readonly createdAtUtc: string;
  readonly sealedAtUtc?: string;
}

export interface ResultListParameters extends PageRequest {
  readonly projectId?: string;
  readonly datasetVersionId?: string;
}

export interface ResultLineageParameters {
  readonly resultId: string;
}

export interface QualityReportSummary {
  readonly qualityReportId: string;
  readonly reportType: string;
  readonly versionNumber: number;
  readonly lifecycleState: string;
  readonly schemaVersion: string;
  readonly summarySeverity: string;
  readonly summary?: JsonObject;
  readonly blockingCount: number;
  readonly warningCount: number;
  readonly infoCount: number;
  readonly createdAtUtc: string;
  readonly finalizedAtUtc?: string;
}

export interface ResultFile {
  readonly resultFileId: string;
  readonly resultId: string;
  readonly fileObjectId: string;
  readonly fileRole: string;
  readonly relativePath: string;
  readonly isRequired: boolean;
  readonly byteLengthSnapshot: number;
  readonly contentHashSnapshot: string;
  readonly objectKey: string;
  readonly mediaType?: string;
}

export interface ResultSummary {
  readonly resultId: string;
  readonly resultSeriesId: string;
  readonly versionNumber: number;
  readonly sourceDatasetVersionId: string;
  readonly sourceProcessingJobId: string;
  readonly sourceJobExecutionId: string;
  readonly sourceResultId?: string;
  readonly resultKind: string;
  readonly lifecycleState: string;
  readonly crs?: CrsSnapshot;
  readonly verticalReference?: string;
  readonly localOrigin?: JsonObject;
  readonly axisConvention?: string;
  readonly unit?: string;
  readonly bounds?: JsonObject;
  readonly resolutionDensity?: JsonObject;
  readonly engineVersion?: string;
  readonly converterVersion?: string;
  readonly parameterSha256: string;
  readonly accuracyLevel: string;
  readonly qualityReport?: QualityReportSummary;
  readonly createdAtUtc: string;
  readonly publishedAtUtc?: string;
  readonly supersededByResultId?: string;
}

export interface ResultDependency {
  readonly resultId: string;
  readonly dependsOnResultId: string;
  readonly dependencyKind: string;
}

export interface ResultSeriesSummary {
  readonly resultSeriesId: string;
  readonly projectId: string;
  readonly datasetVersionId?: string;
  readonly seriesKind: string;
  readonly name: string;
  readonly parentSeriesId?: string;
  readonly createdAtUtc: string;
}

export interface ProcessingJobSummary {
  readonly processingJobId: string;
  readonly jobType: string;
  readonly parameterProfile: string;
  readonly parameterSchemaVersion: string;
  readonly parameterSha256: string;
  readonly lifecycleState: string;
  readonly createdAtUtc: string;
  readonly submittedAtUtc: string;
  readonly startedAtUtc?: string;
  readonly endedAtUtc?: string;
}

export interface JobExecutionSummary {
  readonly jobExecutionId: string;
  readonly processingJobId: string;
  readonly attemptNumber: number;
  readonly executionMode: string;
  readonly workerType: string;
  readonly workerVersion: string;
  readonly engineName?: string;
  readonly engineVersion?: string;
  readonly parameterSha256: string;
  readonly lifecycleState: string;
  readonly startedAtUtc?: string;
  readonly endedAtUtc?: string;
}

export interface ResultLineage {
  readonly target: ResultSummary;
  readonly series: ResultSeriesSummary;
  readonly project: Project;
  readonly sourceDatasetVersion: DatasetVersion;
  readonly sourceProcessingJob: ProcessingJobSummary;
  readonly sourceJobExecution: JobExecutionSummary;
  readonly directDependencies: readonly ResultDependency[];
  readonly availableFiles: readonly ResultFile[];
  readonly finalQualityReports: readonly QualityReportSummary[];
}

export interface ImageImportStartParameters {
  readonly datasetVersionId: string;
  readonly sourceRootPath: string;
}

export interface ImageImportResumeParameters {
  readonly importSessionId: string;
  readonly sourceRootPath?: string;
}

export interface ImageImportCancelParameters {
  readonly importSessionId: string;
}

export interface ImageImportGetParameters {
  readonly importSessionId: string;
}

export interface ImageImportListParameters extends PageRequest {
  readonly datasetVersionId?: string;
}

export interface ImageImportEntryListParameters extends PageRequest {
  readonly importSessionId: string;
}

export interface ImageImportPrivacy {
  readonly pathsIncluded: false;
  readonly hashesIncluded: false;
  readonly objectKeysIncluded: false;
  readonly stageReceiptsIncluded: false;
  readonly quarantineIncluded: false;
  readonly sourceLocatorsIncluded: false;
}

export interface ImageImportSession {
  readonly importSessionId: string;
  readonly datasetVersionId: string;
  readonly sourceEligibilityState: string;
  readonly status: string;
  readonly totalEntryCount: number;
  readonly availableEntryCount: number;
  readonly duplicateEntryCount: number;
  readonly failedEntryCount: number;
  readonly cancelledEntryCount: number;
  readonly lastErrorCode?: string;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
  readonly completedAtUtc?: string;
  readonly cancelledAtUtc?: string;
  readonly privacy: ImageImportPrivacy;
}

export interface ImageImportEntry {
  readonly importEntryId: string;
  readonly importSessionId: string;
  readonly datasetVersionId: string;
  readonly sortIndex: number;
  readonly displayName: string;
  readonly byteLengthSnapshot?: number;
  readonly sourceLastWriteTimeUtc?: string;
  readonly status: string;
  readonly failureCode?: string;
  readonly canonicalEntryId?: string;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
  readonly terminalAtUtc?: string;
  readonly privacy: ImageImportPrivacy;
}

export interface ImageImportPreflightStartParameters {
  readonly importSessionId: string;
}

export interface ImageImportPreflightGetParameters {
  readonly preflightRunId: string;
}

export interface ImageImportPreflightItemListParameters extends PageRequest {
  readonly preflightRunId: string;
}

export interface ImageImportPreflightPrivacy {
  readonly pathsIncluded: false;
  readonly locatorsIncluded: false;
  readonly sourceKeysIncluded: false;
  readonly hashesIncluded: false;
  readonly objectKeysIncluded: false;
  readonly stageReceiptsIncluded: false;
  readonly quarantineIncluded: false;
  readonly rawMetadataIncluded: false;
  readonly serialNumbersIncluded: false;
  readonly coordinatesIncluded: false;
  readonly ownerSampleStatisticsIncluded: false;
}

export interface ImageImportPreflightRun {
  readonly preflightRunId: string;
  readonly importSessionId: string;
  readonly datasetVersionId: string;
  readonly sourceEligibilityState: "pending" | "dji_supported" | "out_of_scope" | "unconfirmed";
  readonly status: "queued" | "running" | "completed" | "failed" | "interrupted";
  readonly decision?: "dji_supported" | "out_of_scope" | "unconfirmed";
  readonly decisionReasonCode?: string;
  readonly parserProfile: string;
  readonly parserVersion: string;
  readonly policyVersion: string;
  readonly totalItemCount: number;
  readonly imageCandidateCount: number;
  readonly sidecarCandidateCount: number;
  readonly completedItemCount: number;
  readonly supportsDjiItemCount: number;
  readonly outOfScopeItemCount: number;
  readonly unconfirmedItemCount: number;
  readonly conflictItemCount: number;
  readonly failedItemCount: number;
  readonly blockingImageCount: number;
  readonly lastErrorCode?: string;
  readonly createdAtUtc: string;
  readonly startedAtUtc?: string;
  readonly updatedAtUtc: string;
  readonly completedAtUtc?: string;
  readonly privacy: ImageImportPreflightPrivacy;
}

export interface ImageImportPreflightItem {
  readonly preflightItemId: string;
  readonly preflightRunId: string;
  readonly importSessionId: string;
  readonly datasetVersionId: string;
  readonly sortIndex: number;
  readonly displayName: string;
  readonly candidateKind: "image_candidate" | "positioning_aux_candidate";
  readonly formatHint?: "jpg" | "jpeg" | "mpo" | "tif" | "tiff" | "mrk" | "nav" | "obs" | "rtk";
  readonly status: "queued" | "running" | "completed" | "failed";
  readonly containerHint?: "jpeg_hint" | "mpo_hint" | "tiff" | "bigtiff" | "not_image" | "unknown";
  readonly evidenceState?: "supports_dji" | "out_of_scope" | "unconfirmed" | "conflict" | "read_failed";
  readonly evidenceKinds: readonly string[];
  readonly reasonCodes: readonly string[];
  readonly failureCode?: string;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
  readonly completedAtUtc?: string;
  readonly privacy: ImageImportPreflightPrivacy;
}

export type LaunchReadinessStage =
  | "main.started"
  | "app.ready"
  | "control.connecting"
  | "control.connected"
  | "control.unavailable"
  | "browser-window.creating"
  | "renderer.loaded"
  | "ready-to-show"
  | "renderer.failed"
  | "gpu-process.failed"
  | "existing-instance";

export interface LaunchReadinessEvent {
  readonly apiVersion: typeof LAUNCHER_API_VERSION;
  readonly nonce: string;
  readonly processId: number;
  readonly sequence: number;
  readonly stage: LaunchReadinessStage;
  readonly timestampUtc: string;
}

function isRecord(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === "object" && value !== null;
}

function isString(value: unknown): value is string {
  return typeof value === "string";
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.length > 0;
}

function isBoundedString(value: unknown, maximumLength: number): value is string {
  return typeof value === "string" && value.length > 0 && value.length <= maximumLength;
}

function hasAsciiControlCharacter(value: string): boolean {
  for (let index = 0; index < value.length; index += 1) {
    if (value.charCodeAt(index) <= 0x1f) {
      return true;
    }
  }

  return false;
}

function isOptionalString(value: unknown): value is string | undefined {
  return value === undefined || isString(value);
}

function isOptionalBoundedString(value: unknown, maximumLength: number): value is string | undefined {
  return value === undefined || isBoundedString(value, maximumLength);
}

function isIsoDateTime(value: unknown): value is string {
  return typeof value === "string" && Number.isFinite(Date.parse(value));
}

function isNonNegativeInteger(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= 0;
}

function isPositiveInteger(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value > 0;
}

function isOptionalPageSize(value: unknown): value is number | undefined {
  return value === undefined || (isPositiveInteger(value) && value <= 50);
}

function isArrayOf<TItem>(value: unknown, guard: (item: unknown) => item is TItem): value is readonly TItem[] {
  return Array.isArray(value) && value.every(guard);
}

function isBoundedArrayOf<TItem>(
  value: unknown,
  maximumLength: number,
  guard: (item: unknown) => item is TItem
): value is readonly TItem[] {
  return Array.isArray(value) && value.length <= maximumLength && value.every(guard);
}

function isFiniteNumberInRange(value: unknown, min: number, max: number): value is number {
  return typeof value === "number" && Number.isFinite(value) && value >= min && value <= max;
}

function isJsonValue(value: unknown): value is JsonValue {
  if (value === null || typeof value === "string" || typeof value === "boolean") {
    return true;
  }

  if (typeof value === "number") {
    return Number.isFinite(value);
  }

  if (Array.isArray(value)) {
    return value.every(isJsonValue);
  }

  if (isRecord(value)) {
    return Object.values(value).every(isJsonValue);
  }

  return false;
}

function isJsonObject(value: unknown): value is JsonObject {
  return isRecord(value) && isJsonValue(value);
}

function hasSensitiveContractProperty(value: unknown, seen = new Set<unknown>()): boolean {
  if (!isRecord(value)) {
    return false;
  }

  if (seen.has(value)) {
    return false;
  }

  seen.add(value);

  for (const [key, item] of Object.entries(value)) {
    const normalizedKey = key.toLowerCase();
    if (normalizedKey === "path"
      || normalizedKey === "absolutepath"
      || normalizedKey === "localpath"
      || normalizedKey === "sourcepath"
      || normalizedKey === "databasepath"
      || normalizedKey === "username"
      || normalizedKey === "user"
      || normalizedKey === "machinename"
      || normalizedKey === "machine"
      || normalizedKey === "environment"
      || normalizedKey === "environmentvariables"
      || normalizedKey === "commandline"
      || normalizedKey === "command"
      || normalizedKey === "uuid"
      || normalizedKey === "pci"
      || normalizedKey === "token"
      || normalizedKey.endsWith("token")
      || normalizedKey.includes("sqlite")
      || normalizedKey.includes("database")) {
      return true;
    }

    if (isRecord(item) && hasSensitiveContractProperty(item, seen)) {
      return true;
    }

    if (Array.isArray(item) && item.some((arrayItem) => hasSensitiveContractProperty(arrayItem, seen))) {
      return true;
    }
  }

  return false;
}

function hasImageImportPrivateProperty(value: unknown, seen = new Set<unknown>()): boolean {
  if (!isRecord(value)) {
    return false;
  }

  if (seen.has(value)) {
    return false;
  }

  seen.add(value);

  for (const [key, item] of Object.entries(value)) {
    const normalizedKey = key.toLowerCase();
    if (normalizedKey.includes("hash")
      || normalizedKey.includes("object")
      || normalizedKey.includes("stage")
      || normalizedKey.includes("quarantine")
      || normalizedKey.includes("path")
      || normalizedKey.includes("locator")
      || normalizedKey.includes("rootkey")
      || normalizedKey === "sourceentrykey"
      || normalizedKey === "sourceidentitykey") {
      return true;
    }

    if (isRecord(item) && hasImageImportPrivateProperty(item, seen)) {
      return true;
    }

    if (Array.isArray(item) && item.some((arrayItem) => hasImageImportPrivateProperty(arrayItem, seen))) {
      return true;
    }
  }

  return false;
}

function hasImageImportPreflightPrivateProperty(value: unknown, seen = new Set<unknown>()): boolean {
  if (!isRecord(value)) {
    return false;
  }

  if (seen.has(value)) {
    return false;
  }

  seen.add(value);
  for (const [key, item] of Object.entries(value)) {
    const normalizedKey = key.toLowerCase();
    if (normalizedKey.includes("path")
      || normalizedKey.includes("locator")
      || normalizedKey.includes("rootkey")
      || normalizedKey.includes("sourcekey")
      || normalizedKey.includes("sourceentrykey")
      || normalizedKey.includes("sourceidentitykey")
      || normalizedKey.includes("hash")
      || normalizedKey.includes("objectkey")
      || normalizedKey.includes("stage")
      || normalizedKey.includes("quarantine")
      || normalizedKey.includes("rawmetadata")
      || normalizedKey.includes("metadatadump")
      || normalizedKey.includes("serial")
      || normalizedKey.includes("coordinate")
      || normalizedKey === "gps"
      || normalizedKey.includes("latitude")
      || normalizedKey.includes("longitude")
      || normalizedKey.includes("altitude")
      || normalizedKey.includes("owner")
      || normalizedKey.includes("samplestatistics")
      || normalizedKey.includes("database")
      || normalizedKey.includes("sqlite")
      || normalizedKey.includes("commandline")
      || normalizedKey.includes("environment")
      || normalizedKey.includes("token")
      || normalizedKey === "user"
      || normalizedKey === "machine") {
      return true;
    }

    if (isRecord(item) && hasImageImportPreflightPrivateProperty(item, seen)) {
      return true;
    }

    if (Array.isArray(item) && item.some(arrayItem => hasImageImportPreflightPrivateProperty(arrayItem, seen))) {
      return true;
    }
  }

  return false;
}

function isRelativeObjectKey(value: unknown): value is string {
  if (!isNonEmptyString(value)) {
    return false;
  }

  return !/^[a-z]:[\\/]/iu.test(value)
    && !value.startsWith("/")
    && !value.startsWith("\\\\")
    && !value.toLowerCase().startsWith("file:")
    && !value.includes("\\")
    && !value.split("/").some((part) => part === "" || part === "." || part === "..")
    && !value.startsWith("staging/")
    && !value.startsWith("quarantine/");
}

function isPublishedSha256ObjectKey(value: unknown): value is string {
  return typeof value === "string" && /^sha256\/[a-f0-9]{2}\/[a-f0-9]{64}$/u.test(value);
}

function isSha256(value: unknown): value is string {
  return typeof value === "string" && /^[a-f0-9]{64}$/u.test(value);
}

function isProjectSpatialConfigurationStatus(value: unknown): value is ProjectSpatialConfigurationStatus {
  return value === "pending" || value === "suggested" || value === "confirmed" || value === "insufficient_metadata";
}

function isCrsRecommendationStatus(value: unknown): value is CrsRecommendationStatus {
  return value === "recommended" || value === "not-recommended";
}

function isCapabilityStatus(value: unknown): value is CapabilityStatus {
  return value === "present" || value === "missing" || value === "unknown";
}

function isWorkerAdmissionDecision(value: unknown): value is WorkerAdmissionDecision {
  return value === "allowed" || value === "denied" || value === "unknown";
}

function isWorkerAdmissionBlockingCategory(value: unknown): value is WorkerAdmissionBlockingCategory {
  return value === "missing" || value === "unknown" || value === "incompatible" || value === "insufficient";
}

function isImageImportPrivacy(value: unknown): value is ImageImportPrivacy {
  return isRecord(value)
    && value.pathsIncluded === false
    && value.hashesIncluded === false
    && value.objectKeysIncluded === false
    && value.stageReceiptsIncluded === false
    && value.quarantineIncluded === false
    && value.sourceLocatorsIncluded === false;
}

function isImageImportPreflightPrivacy(value: unknown): value is ImageImportPreflightPrivacy {
  return isRecord(value)
    && value.pathsIncluded === false
    && value.locatorsIncluded === false
    && value.sourceKeysIncluded === false
    && value.hashesIncluded === false
    && value.objectKeysIncluded === false
    && value.stageReceiptsIncluded === false
    && value.quarantineIncluded === false
    && value.rawMetadataIncluded === false
    && value.serialNumbersIncluded === false
    && value.coordinatesIncluded === false
    && value.ownerSampleStatisticsIncluded === false;
}

function isOptionalResourceValues(value: unknown): value is Readonly<Record<string, number>> | undefined {
  if (value === undefined) {
    return true;
  }

  if (!isRecord(value)) {
    return false;
  }

  const entries = Object.entries(value);
  return entries.length <= 8 && entries.every(([key, item]) =>
    /^[A-Za-z][A-Za-z0-9]{0,63}$/u.test(key) && isNonNegativeInteger(item));
}

function isControlConnectionState(value: unknown): value is ControlConnectionState {
  return value === "not-connected"
    || value === "connecting"
    || value === "starting"
    || value === "reconnecting"
    || value === "connected"
    || value === "unavailable";
}

function isControlStatusReason(value: unknown): value is ControlStatusReason {
  return value === "discovery-missing"
    || value === "discovery-invalid"
    || value === "pipe-unreachable"
    || value === "process-not-found"
    || value === "process-started"
    || value === "process-start-failed"
    || value === "connected"
    || value === "disconnected"
    || value === "not-started";
}

function isLaunchReadinessStage(value: unknown): value is LaunchReadinessStage {
  return value === "main.started"
    || value === "app.ready"
    || value === "control.connecting"
    || value === "control.connected"
    || value === "control.unavailable"
    || value === "browser-window.creating"
    || value === "renderer.loaded"
    || value === "ready-to-show"
    || value === "renderer.failed"
    || value === "gpu-process.failed"
    || value === "existing-instance";
}

export function isWorkerAdmissionCheckParameters(value: unknown): value is WorkerAdmissionCheckParameters {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isBoundedString(value.workerType, 128);
}

export function isCapabilityHost(value: unknown): value is CapabilityHost {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isCapabilityStatus(value.status)
    && isBoundedString(value.operatingSystem, 128)
    && isBoundedString(value.architecture, 32)
    && isBoundedString(value.processArchitecture, 32)
    && isBoundedString(value.sessionKind, 32);
}

export function isCpuCapability(value: unknown): value is CpuCapability {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isCapabilityStatus(value.status)
    && (value.logicalProcessorCount === undefined || isPositiveInteger(value.logicalProcessorCount))
    && isOptionalBoundedString(value.architecture, 32);
}

export function isMemoryCapability(value: unknown): value is MemoryCapability {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isCapabilityStatus(value.status)
    && (value.totalBytes === undefined || isNonNegativeInteger(value.totalBytes))
    && (value.availableBytes === undefined || isNonNegativeInteger(value.availableBytes));
}

export function isStorageCapability(value: unknown): value is StorageCapability {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isBoundedString(value.role, 64)
    && (value.totalBytes === undefined || isNonNegativeInteger(value.totalBytes))
    && (value.availableBytes === undefined || isNonNegativeInteger(value.availableBytes))
    && isBoundedString(value.driveType, 32)
    && isCapabilityStatus(value.status)
    && value.path === undefined;
}

export function isGpuCapability(value: unknown): value is GpuCapability {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isNonNegativeInteger(value.index)
    && isBoundedString(value.name, 128)
    && (value.totalMemoryBytes === undefined || isNonNegativeInteger(value.totalMemoryBytes))
    && (value.freeMemoryBytes === undefined || isNonNegativeInteger(value.freeMemoryBytes))
    && isCapabilityStatus(value.status)
    && value.uuid === undefined
    && value.pciBusId === undefined
    && value.pci === undefined;
}

export function isNvidiaCapability(value: unknown): value is NvidiaCapability {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isCapabilityStatus(value.status)
    && isCapabilityStatus(value.cudaStatus)
    && isOptionalBoundedString(value.driverVersion, 64)
    && isOptionalBoundedString(value.cudaDriverApiVersion, 64)
    && isOptionalBoundedString(value.reasonCode, 128)
    && isBoundedArrayOf(value.gpus, 16, isGpuCapability);
}

export function isWorkerAdmissionBlockingReason(value: unknown): value is WorkerAdmissionBlockingReason {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isWorkerAdmissionBlockingCategory(value.category)
    && isBoundedString(value.code, 128)
    && isBoundedString(value.message, 512)
    && isOptionalResourceValues(value.requiredValues)
    && isOptionalResourceValues(value.availableValues);
}

export function isWorkerAdmissionResult(value: unknown): value is WorkerAdmissionResult {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isBoundedString(value.workerType, 128)
    && isBoundedString(value.profile, 128)
    && isWorkerAdmissionDecision(value.decision)
    && isBoundedArrayOf(value.blockingReasons, 16, isWorkerAdmissionBlockingReason)
    && (value.decision === "allowed" ? value.blockingReasons.length === 0 : true);
}

export function isCapabilityPrivacy(value: unknown): value is CapabilityPrivacy {
  return isRecord(value)
    && value.pathsIncluded === false
    && value.tokensIncluded === false
    && value.userNameIncluded === false
    && value.machineNameIncluded === false
    && value.environmentIncluded === false
    && value.commandLineIncluded === false;
}

export function isProcessingCapabilityReport(value: unknown): value is ProcessingCapabilityReport {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isBoundedString(value.schemaVersion, 64)
    && isBoundedString(value.requirementsVersion, 64)
    && isIsoDateTime(value.capturedAt)
    && isNonNegativeInteger(value.durationMs)
    && isCapabilityHost(value.host)
    && isCpuCapability(value.cpu)
    && isMemoryCapability(value.memory)
    && isBoundedArrayOf(value.storage, 8, isStorageCapability)
    && isNvidiaCapability(value.nvidia)
    && isBoundedArrayOf(value.workerAdmissions, 64, isWorkerAdmissionResult)
    && isCapabilityPrivacy(value.privacy);
}

export function isPageRequest(value: unknown): value is PageRequest {
  return isRecord(value)
    && isOptionalPageSize(value.pageSize)
    && isOptionalString(value.cursor);
}

export function isPageResult<TItem>(
  value: unknown,
  itemGuard: (item: unknown) => item is TItem
): value is PageResult<TItem> {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isArrayOf(value.items, itemGuard)
    && isOptionalString(value.nextCursor);
}

export function isWgs84Bounds(value: unknown): value is Wgs84Bounds {
  return isRecord(value)
    && isFiniteNumberInRange(value.westLongitude, -180, 180)
    && isFiniteNumberInRange(value.eastLongitude, -180, 180)
    && isFiniteNumberInRange(value.southLatitude, -90, 90)
    && isFiniteNumberInRange(value.northLatitude, -90, 90)
    && value.southLatitude <= value.northLatitude;
}

export function isCrsDefinitionInput(value: unknown): value is CrsDefinitionInput {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && value.capturedAtUtc === undefined
    && isOptionalString(value.authority)
    && isOptionalString(value.code)
    && isNonEmptyString(value.name)
    && isOptionalString(value.wkt)
    && isOptionalString(value.projjson)
    && isOptionalString(value.crsType)
    && isNonEmptyString(value.horizontalUnit)
    && isOptionalString(value.verticalReference)
    && isNonEmptyString(value.axisOrder)
    && (isNonEmptyString(value.authority) || isNonEmptyString(value.wkt) || isNonEmptyString(value.projjson));
}

export function isCrsSnapshot(value: unknown): value is CrsSnapshot {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isOptionalString(value.authority)
    && isOptionalString(value.code)
    && isNonEmptyString(value.name)
    && isOptionalString(value.wkt)
    && isOptionalString(value.projjson)
    && isOptionalString(value.crsType)
    && isNonEmptyString(value.horizontalUnit)
    && isOptionalString(value.verticalReference)
    && isNonEmptyString(value.axisOrder)
    && (isNonEmptyString(value.authority) || isNonEmptyString(value.wkt) || isNonEmptyString(value.projjson))
    && isIsoDateTime(value.capturedAtUtc);
}

export function isCrsRecommendation(value: unknown): value is CrsRecommendation {
  if (!isRecord(value) || hasSensitiveContractProperty(value)) {
    return false;
  }

  const suggestedCrs = value.suggestedCrs;
  const inputBounds = value.inputBounds;
  return isCrsRecommendationStatus(value.status)
    && (inputBounds === undefined || isWgs84Bounds(inputBounds))
    && (suggestedCrs === undefined || isCrsSnapshot(suggestedCrs))
    && isOptionalString(value.reasonCode)
    && (value.status === "recommended"
      ? suggestedCrs !== undefined && inputBounds !== undefined
      : value.reasonCode !== undefined);
}

export function isProject(value: unknown): value is Project {
  if (!isRecord(value) || hasSensitiveContractProperty(value)) {
    return false;
  }

  const defaultCrs = value.defaultCrs;
  const suggestedCrs = value.suggestedCrs;
  return isNonEmptyString(value.projectId)
    && isNonEmptyString(value.name)
    && isOptionalString(value.description)
    && isProjectSpatialConfigurationStatus(value.spatialConfigurationStatus)
    && isNonEmptyString(value.lifecycleState)
    && (defaultCrs === undefined || isCrsSnapshot(defaultCrs))
    && (suggestedCrs === undefined || isCrsSnapshot(suggestedCrs))
    && isIsoDateTime(value.createdAtUtc)
    && isIsoDateTime(value.updatedAtUtc);
}

export function isDataset(value: unknown): value is Dataset {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isNonEmptyString(value.datasetId)
    && isNonEmptyString(value.projectId)
    && isNonEmptyString(value.name)
    && isOptionalString(value.description)
    && isIsoDateTime(value.createdAtUtc)
    && isIsoDateTime(value.updatedAtUtc);
}

export function isDatasetVersion(value: unknown): value is DatasetVersion {
  if (!isRecord(value) || hasSensitiveContractProperty(value)) {
    return false;
  }

  const wgs84Bounds = value.wgs84Bounds;
  return isNonEmptyString(value.datasetVersionId)
    && isNonEmptyString(value.datasetId)
    && isPositiveInteger(value.versionNumber)
    && isOptionalString(value.parentVersionId)
    && isNonEmptyString(value.lifecycleState)
    && isNonEmptyString(value.sourceEligibilityState)
    && isNonEmptyString(value.qualityGateState)
    && value.name === undefined
    && value.description === undefined
    && wgs84Bounds === undefined
    && value.updatedAtUtc === undefined
    && (value.contentManifestSha256 === undefined || isSha256(value.contentManifestSha256))
    && (value.warningAcknowledgedAtUtc === undefined || isIsoDateTime(value.warningAcknowledgedAtUtc))
    && isIsoDateTime(value.createdAtUtc)
    && (value.sealedAtUtc === undefined || isIsoDateTime(value.sealedAtUtc));
}

export function isQualityReportSummary(value: unknown): value is QualityReportSummary {
  const summary = isRecord(value) ? value.summary : undefined;
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isNonEmptyString(value.qualityReportId)
    && isNonEmptyString(value.reportType)
    && isPositiveInteger(value.versionNumber)
    && isNonEmptyString(value.lifecycleState)
    && isNonEmptyString(value.schemaVersion)
    && isNonEmptyString(value.summarySeverity)
    && (summary === undefined || isJsonObject(summary))
    && value.evidenceLevel === undefined
    && isNonNegativeInteger(value.blockingCount)
    && isNonNegativeInteger(value.warningCount)
    && isNonNegativeInteger(value.infoCount)
    && isIsoDateTime(value.createdAtUtc)
    && (value.finalizedAtUtc === undefined || isIsoDateTime(value.finalizedAtUtc));
}

export function isResultFile(value: unknown): value is ResultFile {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isNonEmptyString(value.resultFileId)
    && isNonEmptyString(value.resultId)
    && isNonEmptyString(value.fileObjectId)
    && isNonEmptyString(value.fileRole)
    && isRelativeObjectKey(value.relativePath)
    && typeof value.isRequired === "boolean"
    && isNonNegativeInteger(value.byteLengthSnapshot)
    && isSha256(value.contentHashSnapshot)
    && isPublishedSha256ObjectKey(value.objectKey)
    && isOptionalString(value.mediaType);
}

export function isResultSummary(value: unknown): value is ResultSummary {
  if (!isRecord(value) || hasSensitiveContractProperty(value)) {
    return false;
  }

  const crs = value.crs;
  const localOrigin = value.localOrigin;
  const bounds = value.bounds;
  const resolutionDensity = value.resolutionDensity;
  const qualityReport = value.qualityReport;
  return isNonEmptyString(value.resultId)
    && isNonEmptyString(value.resultSeriesId)
    && isPositiveInteger(value.versionNumber)
    && isNonEmptyString(value.sourceDatasetVersionId)
    && isNonEmptyString(value.sourceProcessingJobId)
    && isNonEmptyString(value.sourceJobExecutionId)
    && isOptionalString(value.sourceResultId)
    && isNonEmptyString(value.resultKind)
    && isNonEmptyString(value.lifecycleState)
    && (crs === undefined || isCrsSnapshot(crs))
    && isOptionalString(value.verticalReference)
    && (localOrigin === undefined || isJsonObject(localOrigin))
    && isOptionalString(value.axisConvention)
    && isOptionalString(value.unit)
    && (bounds === undefined || isJsonObject(bounds))
    && (resolutionDensity === undefined || isJsonObject(resolutionDensity))
    && isOptionalString(value.engineVersion)
    && isOptionalString(value.converterVersion)
    && isSha256(value.parameterSha256)
    && isNonEmptyString(value.accuracyLevel)
    && (qualityReport === undefined || isQualityReportSummary(qualityReport))
    && isIsoDateTime(value.createdAtUtc)
    && value.updatedAtUtc === undefined
    && (value.publishedAtUtc === undefined || isIsoDateTime(value.publishedAtUtc))
    && isOptionalString(value.supersededByResultId);
}

export function isResultDependency(value: unknown): value is ResultDependency {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isNonEmptyString(value.resultId)
    && isNonEmptyString(value.dependsOnResultId)
    && isNonEmptyString(value.dependencyKind);
}

export function isResultSeriesSummary(value: unknown): value is ResultSeriesSummary {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isNonEmptyString(value.resultSeriesId)
    && isNonEmptyString(value.projectId)
    && isOptionalString(value.datasetVersionId)
    && isNonEmptyString(value.seriesKind)
    && isNonEmptyString(value.name)
    && isOptionalString(value.parentSeriesId)
    && isIsoDateTime(value.createdAtUtc);
}

export function isProcessingJobSummary(value: unknown): value is ProcessingJobSummary {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isNonEmptyString(value.processingJobId)
    && isNonEmptyString(value.jobType)
    && isNonEmptyString(value.parameterProfile)
    && isNonEmptyString(value.parameterSchemaVersion)
    && isSha256(value.parameterSha256)
    && isNonEmptyString(value.lifecycleState)
    && isIsoDateTime(value.createdAtUtc)
    && isIsoDateTime(value.submittedAtUtc)
    && (value.startedAtUtc === undefined || isIsoDateTime(value.startedAtUtc))
    && (value.endedAtUtc === undefined || isIsoDateTime(value.endedAtUtc));
}

export function isJobExecutionSummary(value: unknown): value is JobExecutionSummary {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isNonEmptyString(value.jobExecutionId)
    && isNonEmptyString(value.processingJobId)
    && isPositiveInteger(value.attemptNumber)
    && isNonEmptyString(value.executionMode)
    && isNonEmptyString(value.workerType)
    && isNonEmptyString(value.workerVersion)
    && isOptionalString(value.engineName)
    && isOptionalString(value.engineVersion)
    && isSha256(value.parameterSha256)
    && isNonEmptyString(value.lifecycleState)
    && (value.startedAtUtc === undefined || isIsoDateTime(value.startedAtUtc))
    && (value.endedAtUtc === undefined || isIsoDateTime(value.endedAtUtc));
}

export function isResultLineage(value: unknown): value is ResultLineage {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && isResultSummary(value.target)
    && isResultSeriesSummary(value.series)
    && isProject(value.project)
    && isDatasetVersion(value.sourceDatasetVersion)
    && isProcessingJobSummary(value.sourceProcessingJob)
    && isJobExecutionSummary(value.sourceJobExecution)
    && isArrayOf(value.directDependencies, isResultDependency)
    && isArrayOf(value.availableFiles, isResultFile)
    && isArrayOf(value.finalQualityReports, isQualityReportSummary);
}

export function isImageImportStartParameters(value: unknown): value is ImageImportStartParameters {
  return isRecord(value)
    && isNonEmptyString(value.datasetVersionId)
    && isBoundedString(value.sourceRootPath, 32_767)
    && !hasAsciiControlCharacter(value.sourceRootPath)
    && (/^[A-Za-z]:[\\/]/u.test(value.sourceRootPath) || /^\\\\[^\\]/u.test(value.sourceRootPath));
}

export function isImageImportResumeParameters(value: unknown): value is ImageImportResumeParameters {
  return isRecord(value)
    && isBoundedString(value.importSessionId, 128)
    && (value.sourceRootPath === undefined || (
      isBoundedString(value.sourceRootPath, 32_767)
      && !hasAsciiControlCharacter(value.sourceRootPath)
      && (/^[A-Za-z]:[\\/]/u.test(value.sourceRootPath) || /^\\\\[^\\]/u.test(value.sourceRootPath))));
}

export function isImageImportSession(value: unknown): value is ImageImportSession {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && !hasImageImportPrivateProperty({
      ...value,
      privacy: undefined
    })
    && isNonEmptyString(value.importSessionId)
    && isNonEmptyString(value.datasetVersionId)
    && isNonEmptyString(value.sourceEligibilityState)
    && isNonEmptyString(value.status)
    && isNonNegativeInteger(value.totalEntryCount)
    && isNonNegativeInteger(value.availableEntryCount)
    && isNonNegativeInteger(value.duplicateEntryCount)
    && isNonNegativeInteger(value.failedEntryCount)
    && isNonNegativeInteger(value.cancelledEntryCount)
    && isOptionalBoundedString(value.lastErrorCode, 128)
    && isIsoDateTime(value.createdAtUtc)
    && isIsoDateTime(value.updatedAtUtc)
    && (value.completedAtUtc === undefined || isIsoDateTime(value.completedAtUtc))
    && (value.cancelledAtUtc === undefined || isIsoDateTime(value.cancelledAtUtc))
    && isImageImportPrivacy(value.privacy);
}

export function isImageImportEntry(value: unknown): value is ImageImportEntry {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && !hasImageImportPrivateProperty({
      ...value,
      privacy: undefined
    })
    && isNonEmptyString(value.importEntryId)
    && isNonEmptyString(value.importSessionId)
    && isNonEmptyString(value.datasetVersionId)
    && isNonNegativeInteger(value.sortIndex)
    && isBoundedString(value.displayName, 255)
    && !value.displayName.includes("/")
    && !value.displayName.includes("\\")
    && !value.displayName.includes(":")
    && (value.byteLengthSnapshot === undefined || isNonNegativeInteger(value.byteLengthSnapshot))
    && (value.sourceLastWriteTimeUtc === undefined || isIsoDateTime(value.sourceLastWriteTimeUtc))
    && isNonEmptyString(value.status)
    && isOptionalBoundedString(value.failureCode, 128)
    && isOptionalString(value.canonicalEntryId)
    && isIsoDateTime(value.createdAtUtc)
    && isIsoDateTime(value.updatedAtUtc)
    && (value.terminalAtUtc === undefined || isIsoDateTime(value.terminalAtUtc))
    && isImageImportPrivacy(value.privacy);
}

export function isImageImportPreflightStartParameters(
  value: unknown
): value is ImageImportPreflightStartParameters {
  return isRecord(value)
    && !hasImageImportPreflightPrivateProperty(value)
    && isBoundedString(value.importSessionId, 128);
}

export function isImageImportPreflightGetParameters(
  value: unknown
): value is ImageImportPreflightGetParameters {
  return isRecord(value)
    && !hasImageImportPreflightPrivateProperty(value)
    && isBoundedString(value.preflightRunId, 128);
}

export function isImageImportPreflightItemListParameters(
  value: unknown
): value is ImageImportPreflightItemListParameters {
  return isRecord(value)
    && !hasImageImportPreflightPrivateProperty(value)
    && isBoundedString(value.preflightRunId, 128)
    && isOptionalPageSize(value.pageSize)
    && (value.cursor === undefined || isBoundedString(value.cursor, 512));
}

export function isImageImportPreflightRun(value: unknown): value is ImageImportPreflightRun {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && !hasImageImportPreflightPrivateProperty({ ...value, privacy: undefined })
    && isBoundedString(value.preflightRunId, 128)
    && isBoundedString(value.importSessionId, 128)
    && isBoundedString(value.datasetVersionId, 128)
    && ["pending", "dji_supported", "out_of_scope", "unconfirmed"].includes(value.sourceEligibilityState as string)
    && ["queued", "running", "completed", "failed", "interrupted"].includes(value.status as string)
    && (value.decision === undefined || ["dji_supported", "out_of_scope", "unconfirmed"].includes(value.decision as string))
    && isOptionalBoundedString(value.decisionReasonCode, 128)
    && isBoundedString(value.parserProfile, 128)
    && isBoundedString(value.parserVersion, 128)
    && isBoundedString(value.policyVersion, 128)
    && isNonNegativeInteger(value.totalItemCount)
    && isNonNegativeInteger(value.imageCandidateCount)
    && isNonNegativeInteger(value.sidecarCandidateCount)
    && isNonNegativeInteger(value.completedItemCount)
    && isNonNegativeInteger(value.supportsDjiItemCount)
    && isNonNegativeInteger(value.outOfScopeItemCount)
    && isNonNegativeInteger(value.unconfirmedItemCount)
    && isNonNegativeInteger(value.conflictItemCount)
    && isNonNegativeInteger(value.failedItemCount)
    && isNonNegativeInteger(value.blockingImageCount)
    && isOptionalBoundedString(value.lastErrorCode, 128)
    && isIsoDateTime(value.createdAtUtc)
    && (value.startedAtUtc === undefined || isIsoDateTime(value.startedAtUtc))
    && isIsoDateTime(value.updatedAtUtc)
    && (value.completedAtUtc === undefined || isIsoDateTime(value.completedAtUtc))
    && isImageImportPreflightPrivacy(value.privacy);
}

export function isImageImportPreflightItem(value: unknown): value is ImageImportPreflightItem {
  return isRecord(value)
    && !hasSensitiveContractProperty(value)
    && !hasImageImportPreflightPrivateProperty({ ...value, privacy: undefined })
    && isBoundedString(value.preflightItemId, 128)
    && isBoundedString(value.preflightRunId, 128)
    && isBoundedString(value.importSessionId, 128)
    && isBoundedString(value.datasetVersionId, 128)
    && isNonNegativeInteger(value.sortIndex)
    && isBoundedString(value.displayName, 255)
    && !value.displayName.includes("/")
    && !value.displayName.includes("\\")
    && !value.displayName.includes(":")
    && ["image_candidate", "positioning_aux_candidate"].includes(value.candidateKind as string)
    && (value.formatHint === undefined || ["jpg", "jpeg", "mpo", "tif", "tiff", "mrk", "nav", "obs", "rtk"].includes(value.formatHint as string))
    && ["queued", "running", "completed", "failed"].includes(value.status as string)
    && (value.containerHint === undefined || ["jpeg_hint", "mpo_hint", "tiff", "bigtiff", "not_image", "unknown"].includes(value.containerHint as string))
    && (value.evidenceState === undefined || ["supports_dji", "out_of_scope", "unconfirmed", "conflict", "read_failed"].includes(value.evidenceState as string))
    && isArrayOf(value.evidenceKinds, item => isBoundedString(item, 128))
    && value.evidenceKinds.length <= 16
    && isArrayOf(value.reasonCodes, item => isBoundedString(item, 128))
    && value.reasonCodes.length <= 16
    && isOptionalBoundedString(value.failureCode, 128)
    && isIsoDateTime(value.createdAtUtc)
    && isIsoDateTime(value.updatedAtUtc)
    && (value.completedAtUtc === undefined || isIsoDateTime(value.completedAtUtc))
    && isImageImportPreflightPrivacy(value.privacy);
}

export function isControlConnectionStatus(value: unknown): value is ControlConnectionStatus {
  if (!isRecord(value)) {
    return false;
  }

  const nextRetryDelay = value.nextRetryDelayMs;
  return value.apiVersion === CONTROL_API_VERSION
    && isControlConnectionState(value.state)
    && value.endpointKind === "named-pipe"
    && isControlStatusReason(value.reason)
    && typeof value.detail === "string"
    && typeof value.retryAttempt === "number"
    && Number.isInteger(value.retryAttempt)
    && value.retryAttempt >= 0
    && typeof value.checkedAt === "string"
    && (nextRetryDelay === undefined
      || (typeof nextRetryDelay === "number" && Number.isInteger(nextRetryDelay) && nextRetryDelay >= 0));
}

export function isLaunchReadinessEvent(value: unknown): value is LaunchReadinessEvent {
  return isRecord(value)
    && value.apiVersion === LAUNCHER_API_VERSION
    && typeof value.nonce === "string"
    && /^[a-f0-9]{64}$/u.test(value.nonce)
    && typeof value.processId === "number"
    && Number.isInteger(value.processId)
    && value.processId > 0
    && typeof value.sequence === "number"
    && Number.isInteger(value.sequence)
    && value.sequence > 0
    && isLaunchReadinessStage(value.stage)
    && typeof value.timestampUtc === "string"
    && Number.isFinite(Date.parse(value.timestampUtc));
}
