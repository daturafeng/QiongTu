import { access, readFile } from "node:fs/promises";
import { createConnection } from "node:net";
import type { Socket } from "node:net";
import { isAbsolute, join } from "node:path";
import { spawn, type ChildProcess, type SpawnOptions } from "node:child_process";
import { randomUUID } from "node:crypto";
import {
  CONTROL_API_VERSION,
  type ControlConnectionStatus,
  type ControlStatusReason
} from "@qiongtu/contracts";

const DISCOVERY_FILE_NAME = "control.json";
const PIPE_NAME_PATTERN = /^qiongtu-control-v1-[A-Za-z0-9_-]{8,64}$/u;
const MAX_PIPE_NAME_LENGTH = 96;
const MAX_RETRY_ATTEMPTS = 4;
const INITIAL_RETRY_DELAY_MS = 500;
const MAX_RETRY_DELAY_MS = 4_000;

export interface ControlDiscovery {
  readonly apiVersion: typeof CONTROL_API_VERSION;
  readonly endpointKind: "named-pipe";
  readonly pipeName: string;
  readonly processId: number;
  readonly startedAtUtc: string;
}

interface ControlManagerOptions {
  readonly discoveryFilePath: string;
  readonly executableCandidates: readonly string[];
  readonly connectTimeoutMs: number;
  readonly createPipeConnection?: (pipePath: string) => Socket;
  readonly spawnControlProcess?: (file: string, args: readonly string[], options: SpawnOptions) => ChildProcess;
  readonly now?: () => Date;
  readonly isProcessAlive?: (processId: number) => boolean;
  readonly allowStartControlProcess?: boolean;
}

type StatusListener = (status: ControlConnectionStatus) => void;

export function getControlDiscoveryFilePath(localAppDataDirectory: string): string {
  return join(localAppDataDirectory, "QiongTu", "runtime", DISCOVERY_FILE_NAME);
}

export function toWindowsPipePath(pipeName: string): string {
  return "\\\\.\\pipe\\" + pipeName;
}

export function parseControlDiscovery(value: unknown): ControlDiscovery | undefined {
  if (!isRecord(value)) {
    return undefined;
  }

  const pipeName = value.pipeName;
  const processId = value.processId;
  const startedAtUtc = value.startedAtUtc;
  if (value.apiVersion !== CONTROL_API_VERSION
    || value.endpointKind !== "named-pipe"
    || typeof pipeName !== "string"
    || pipeName.length > MAX_PIPE_NAME_LENGTH
    || !PIPE_NAME_PATTERN.test(pipeName)
    || typeof processId !== "number"
    || !Number.isInteger(processId)
    || processId <= 0
    || typeof startedAtUtc !== "string"
    || !Number.isFinite(Date.parse(startedAtUtc))
  ) {
    return undefined;
  }

  return {
    apiVersion: CONTROL_API_VERSION,
    endpointKind: "named-pipe",
    pipeName,
    processId,
    startedAtUtc
  };
}

export function createInitialControlStatus(now: Date): ControlConnectionStatus {
  return createStatus("not-connected", "not-started", "尚未连接本地控制服务。", 0, now);
}

export class ControlProcessManager {
  private readonly discoveryFilePath: string;
  private readonly executableCandidates: readonly string[];
  private readonly connectTimeoutMs: number;
  private readonly createPipeConnection: (pipePath: string) => Socket;
  private readonly spawnControlProcess: (file: string, args: readonly string[], options: SpawnOptions) => ChildProcess;
  private readonly now: () => Date;
  private readonly isProcessAlive: (processId: number) => boolean;
  private readonly allowStartControlProcess: boolean;
  private readonly listeners = new Set<StatusListener>();

  private socket: Socket | undefined;
  private reconnectTimer: NodeJS.Timeout | undefined;
  private retryAttempt = 0;
  private processStartAttempted = false;
  private status: ControlConnectionStatus;

  public constructor(options: ControlManagerOptions) {
    this.discoveryFilePath = options.discoveryFilePath;
    this.executableCandidates = options.executableCandidates;
    this.connectTimeoutMs = options.connectTimeoutMs;
    this.createPipeConnection = options.createPipeConnection ?? createConnection;
    this.spawnControlProcess = options.spawnControlProcess ?? spawn;
    this.now = options.now ?? (() => new Date());
    this.isProcessAlive = options.isProcessAlive ?? isCurrentUserProcessAlive;
    this.allowStartControlProcess = options.allowStartControlProcess ?? true;
    this.status = createInitialControlStatus(this.now());
  }

  public getStatus(): ControlConnectionStatus {
    return this.status;
  }

  public onStatusChanged(listener: StatusListener): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  public async start(): Promise<void> {
    this.retryAttempt = 0;
    this.processStartAttempted = false;
    await this.connectOrStart();
  }

  public disconnectOnly(): void {
    if (this.reconnectTimer !== undefined) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = undefined;
    }

    if (this.socket !== undefined) {
      this.socket.removeAllListeners();
      this.socket.destroy();
      this.socket = undefined;
    }

    this.setStatus(createStatus(
      "not-connected",
      "disconnected",
      "桌面应用已断开控制管道；不会终止仍可能管理 Worker 的控制服务。",
      this.retryAttempt,
      this.now()
    ));
  }

  private async connectOrStart(): Promise<void> {
    this.setStatus(createStatus(
      this.retryAttempt === 0 ? "connecting" : "reconnecting",
      "not-started",
      this.retryAttempt === 0 ? "正在查找本地控制服务发现文件。" : "正在按有界退避重连本地控制服务。",
      this.retryAttempt,
      this.now()
    ));

    const discovery = await this.readDiscovery();
    let discoveredProcessAlive = false;
    if (discovery !== undefined) {
      const connected = await this.tryConnect(discovery);
      if (connected) {
        return;
      }
      discoveredProcessAlive = this.isProcessAlive(discovery.processId);
    }

    const shouldStart = this.allowStartControlProcess
      && !this.processStartAttempted
      && (discovery === undefined || !discoveredProcessAlive);
    if (shouldStart) {
      this.processStartAttempted = true;
      const started = await this.tryStartControlProcess();
      if (started) {
        this.scheduleReconnect("process-started", "已启动本地控制服务，等待其发布当前用户命名管道。");
        return;
      }
    }

    this.scheduleReconnect(
      discovery === undefined ? this.status.reason : "pipe-unreachable",
      discovery === undefined
        ? "未找到可用控制服务发现文件或发现文件无效。"
        : "发现文件存在，但当前用户命名管道暂不可连接。"
    );
  }

  private async readDiscovery(): Promise<ControlDiscovery | undefined> {
    try {
      const raw = await readFile(this.discoveryFilePath, "utf8");
      const discovery = parseControlDiscoveryText(raw);
      if (discovery === undefined) {
        this.setStatus(createStatus(
          "not-connected",
          "discovery-invalid",
          "控制服务发现文件无效；未向界面暴露路径、进程号或管道名。",
          this.retryAttempt,
          this.now()
        ));
      }
      return discovery;
    } catch (error: unknown) {
      this.setStatus(createStatus(
        "not-connected",
        isFileMissingError(error) ? "discovery-missing" : "discovery-invalid",
        isFileMissingError(error)
          ? "尚未发现本地控制服务。"
          : "无法读取控制服务发现文件；细节已脱敏。",
        this.retryAttempt,
        this.now()
      ));
      return undefined;
    }
  }

  private async tryConnect(discovery: ControlDiscovery): Promise<boolean> {
    this.setStatus(createStatus(
      "connecting",
      "not-started",
      "正在连接当前用户命名管道。",
      this.retryAttempt,
      this.now()
    ));

    const pipePath = toWindowsPipePath(discovery.pipeName);
    try {
      const socket = await connectWithTimeout(this.createPipeConnection, pipePath, this.connectTimeoutMs);
      await performHelloHandshake(socket, discovery, this.connectTimeoutMs);
      this.socket = socket;
      this.socket.once("close", () => {
        this.socket = undefined;
        this.processStartAttempted = false;
        this.scheduleReconnect("disconnected", "控制管道已断开，正在等待安全重连。");
      });
      this.setStatus(createStatus(
        "connected",
        "connected",
        "已连接本地控制服务当前用户命名管道。",
        this.retryAttempt,
        this.now()
      ));
      this.retryAttempt = 0;
      this.processStartAttempted = false;
      return true;
    } catch {
      this.setStatus(createStatus(
        "not-connected",
        "pipe-unreachable",
        "控制服务发现文件存在，但命名管道不可连接。",
        this.retryAttempt,
        this.now()
      ));
      return false;
    }
  }

  private async tryStartControlProcess(): Promise<boolean> {
    const executablePath = await findFirstExistingFile(this.executableCandidates);
    if (executablePath === undefined) {
      this.setStatus(createStatus(
        "unavailable",
        "process-not-found",
        "未找到已发布的 QiongTu.Control 可执行文件；未尝试 dotnet run 或终止任何进程。",
        this.retryAttempt,
        this.now()
      ));
      return false;
    }

    try {
      this.setStatus(createStatus(
        "starting",
        "process-started",
        "正在启动独立控制服务；启动参数固定且不透传用户输入。",
        this.retryAttempt,
        this.now()
      ));
      const child = this.spawnControlProcess(executablePath, [], {
        detached: true,
        stdio: "ignore",
        windowsHide: true
      });
      child.unref();
      return true;
    } catch {
      this.setStatus(createStatus(
        "unavailable",
        "process-start-failed",
        "控制服务启动失败；错误详情未暴露给 renderer。",
        this.retryAttempt,
        this.now()
      ));
      return false;
    }
  }

  private scheduleReconnect(reason: ControlStatusReason, detail: string): void {
    if (this.reconnectTimer !== undefined) {
      return;
    }

    if (this.retryAttempt >= MAX_RETRY_ATTEMPTS) {
      this.setStatus(createStatus("unavailable", reason, detail, this.retryAttempt, this.now()));
      return;
    }

    this.retryAttempt += 1;
    const nextDelay = Math.min(INITIAL_RETRY_DELAY_MS * (2 ** (this.retryAttempt - 1)), MAX_RETRY_DELAY_MS);
    this.setStatus(createStatus(
      "reconnecting",
      reason,
      detail,
      this.retryAttempt,
      this.now(),
      nextDelay
    ));
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = undefined;
      void this.connectOrStart();
    }, nextDelay);
  }

  private setStatus(status: ControlConnectionStatus): void {
    this.status = status;
    for (const listener of this.listeners) {
      listener(status);
    }
  }
}

export function parseControlDiscoveryText(raw: string): ControlDiscovery | undefined {
  try {
    const normalized = raw.charCodeAt(0) === 0xfeff ? raw.slice(1) : raw;
    const parsed: unknown = JSON.parse(normalized);
    return parseControlDiscovery(parsed);
  } catch {
    return undefined;
  }
}

async function performHelloHandshake(
  socket: Socket,
  discovery: ControlDiscovery,
  timeoutMs: number
): Promise<void> {
  const requestId = randomUUID();
  const response = new Promise<void>((resolve, reject) => {
    let buffered = "";
    let settled = false;
    const finish = (error?: Error): void => {
      if (settled) {
        return;
      }

      settled = true;
      clearTimeout(timeout);
      socket.off("data", onData);
      socket.off("error", onError);
      if (error === undefined) {
        resolve();
      } else {
        reject(error);
      }
    };
    const onError = (): void => {
      finish(new Error("Control pipe handshake failed."));
    };
    const onData = (chunk: Buffer): void => {
      buffered += chunk.toString("utf8");
      if (buffered.length > 64 * 1024) {
        finish(new Error("Control pipe handshake response exceeded the size limit."));
        return;
      }

      const lineEnd = buffered.indexOf("\n");
      if (lineEnd < 0) {
        return;
      }

      try {
        const value: unknown = JSON.parse(buffered.slice(0, lineEnd).trimEnd());
        if (!isValidHelloResponse(value, requestId, discovery)) {
          finish(new Error("Control pipe handshake response was invalid."));
          return;
        }

        finish();
      } catch {
        finish(new Error("Control pipe handshake response was invalid JSON."));
      }
    };
    const timeout = setTimeout(() => {
      finish(new Error("Control pipe handshake timed out."));
    }, timeoutMs);
    socket.on("data", onData);
    socket.once("error", onError);
  });

  socket.write(JSON.stringify({
    apiVersion: CONTROL_API_VERSION,
    requestId,
    method: "control.hello",
    parameters: null
  }) + "\n");
  try {
    await response;
  } catch (error: unknown) {
    socket.destroy();
    throw error;
  }
}

function isValidHelloResponse(
  value: unknown,
  requestId: string,
  discovery: ControlDiscovery
): boolean {
  if (!isRecord(value) || value.apiVersion !== CONTROL_API_VERSION || value.requestId !== requestId || value.ok !== true) {
    return false;
  }

  const result = value.result;
  return isRecord(result)
    && result.apiVersion === CONTROL_API_VERSION
    && result.processId === discovery.processId
    && result.pipeName === discovery.pipeName
    && typeof result.artifactBaseUrl === "string"
    && result.artifactBaseUrl.startsWith("http://127.0.0.1:");
}

function createStatus(
  state: ControlConnectionStatus["state"],
  reason: ControlStatusReason,
  detail: string,
  retryAttempt: number,
  checkedAt: Date,
  nextRetryDelayMs?: number
): ControlConnectionStatus {
  const baseStatus = {
    apiVersion: CONTROL_API_VERSION,
    state,
    endpointKind: "named-pipe",
    reason,
    detail,
    retryAttempt,
    checkedAt: checkedAt.toISOString()
  } satisfies Omit<ControlConnectionStatus, "nextRetryDelayMs">;

  if (nextRetryDelayMs === undefined) {
    return baseStatus;
  }

  return {
    ...baseStatus,
    nextRetryDelayMs
  };
}

function isRecord(value: unknown): value is Readonly<Record<string, unknown>> {
  return typeof value === "object" && value !== null;
}

function isFileMissingError(error: unknown): boolean {
  return isRecord(error) && error.code === "ENOENT";
}

function isCurrentUserProcessAlive(processId: number): boolean {
  try {
    process.kill(processId, 0);
    return true;
  } catch (error: unknown) {
    return isRecord(error) && error.code === "EPERM";
  }
}

async function findFirstExistingFile(candidates: readonly string[]): Promise<string | undefined> {
  for (const candidate of candidates) {
    if (!isAbsolute(candidate)) {
      continue;
    }

    try {
      await access(candidate);
      return candidate;
    } catch {
      // Keep discovery sanitized: absence is reported without exposing local paths.
    }
  }

  return undefined;
}

function connectWithTimeout(
  createPipeConnection: (pipePath: string) => Socket,
  pipePath: string,
  timeoutMs: number
): Promise<Socket> {
  return new Promise<Socket>((resolve, reject) => {
    const socket = createPipeConnection(pipePath);
    let settled = false;

    const timeout = setTimeout(() => {
      if (!settled) {
        settled = true;
        socket.destroy();
        reject(new Error("Control pipe connection timed out."));
      }
    }, timeoutMs);

    socket.once("connect", () => {
      if (!settled) {
        settled = true;
        clearTimeout(timeout);
        resolve(socket);
      }
    });

    socket.once("error", (error) => {
      if (!settled) {
        settled = true;
        clearTimeout(timeout);
        socket.destroy();
        reject(error);
      }
    });
  });
}
