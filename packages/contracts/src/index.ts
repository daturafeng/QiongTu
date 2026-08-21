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
