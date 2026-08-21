import { createConnection, type Socket } from "node:net";
import {
  LAUNCHER_API_VERSION,
  type LaunchReadinessEvent,
  type LaunchReadinessStage
} from "@qiongtu/contracts";

export const LAUNCH_PIPE_ENVIRONMENT_KEY = "QIONGTU_LAUNCH_PIPE_NAME";
export const LAUNCH_NONCE_ENVIRONMENT_KEY = "QIONGTU_LAUNCH_NONCE";

const PIPE_NAME_PATTERN = /^qiongtu-launch-v1-[A-Za-z0-9_-]{8,64}$/u;
const NONCE_PATTERN = /^[a-f0-9]{64}$/u;
const CONNECTION_TIMEOUT_MS = 2_000;
const MAX_MESSAGE_BYTES = 4 * 1024;

interface LauncherReadinessOptions {
  readonly environment?: NodeJS.ProcessEnv;
  readonly createPipeConnection?: (pipePath: string) => Socket;
  readonly now?: () => Date;
  readonly processId?: number;
}

export interface LauncherReadinessReporter {
  readonly enabled: boolean;
  report(stage: LaunchReadinessStage): Promise<boolean>;
  disconnect(): void;
}

export function createLauncherReadinessReporter(
  options: LauncherReadinessOptions = {}
): LauncherReadinessReporter {
  const environment = options.environment ?? process.env;
  const pipeName = environment[LAUNCH_PIPE_ENVIRONMENT_KEY];
  const nonce = environment[LAUNCH_NONCE_ENVIRONMENT_KEY];
  Reflect.deleteProperty(environment, LAUNCH_PIPE_ENVIRONMENT_KEY);
  Reflect.deleteProperty(environment, LAUNCH_NONCE_ENVIRONMENT_KEY);

  if (typeof pipeName !== "string"
    || !PIPE_NAME_PATTERN.test(pipeName)
    || typeof nonce !== "string"
    || !NONCE_PATTERN.test(nonce)
  ) {
    return new DisabledLauncherReadinessReporter();
  }

  return new NamedPipeLauncherReadinessReporter(
    pipeName,
    nonce,
    options.processId ?? process.pid,
    options.createPipeConnection ?? createConnection,
    options.now ?? (() => new Date())
  );
}

class DisabledLauncherReadinessReporter implements LauncherReadinessReporter {
  public readonly enabled = false;

  public report(): Promise<boolean> {
    return Promise.resolve(false);
  }

  public disconnect(): void {
  }
}

class NamedPipeLauncherReadinessReporter implements LauncherReadinessReporter {
  public readonly enabled = true;
  private readonly pipePath: string;
  private readonly nonce: string;
  private readonly processId: number;
  private readonly createPipeConnection: (pipePath: string) => Socket;
  private readonly now: () => Date;
  private socketPromise: Promise<Socket> | undefined;
  private delivery: Promise<boolean> = Promise.resolve(true);
  private socket: Socket | undefined;
  private sequence = 0;
  private disconnected = false;

  public constructor(
    pipeName: string,
    nonce: string,
    processId: number,
    createPipeConnection: (pipePath: string) => Socket,
    now: () => Date
  ) {
    this.pipePath = `\\\\.\\pipe\\${pipeName}`;
    this.nonce = nonce;
    this.processId = processId;
    this.createPipeConnection = createPipeConnection;
    this.now = now;
  }

  public report(stage: LaunchReadinessStage): Promise<boolean> {
    this.delivery = this.delivery.then(async () => {
      if (this.disconnected) {
        return false;
      }

      try {
        const socket = await this.getSocket();
        const event: LaunchReadinessEvent = {
          apiVersion: LAUNCHER_API_VERSION,
          nonce: this.nonce,
          processId: this.processId,
          sequence: ++this.sequence,
          stage,
          timestampUtc: this.now().toISOString()
        };
        const line = `${JSON.stringify(event)}\n`;
        if (Buffer.byteLength(line, "utf8") > MAX_MESSAGE_BYTES) {
          return false;
        }

        await writeLine(socket, line);
        return true;
      } catch {
        return false;
      }
    });
    return this.delivery;
  }

  public disconnect(): void {
    this.disconnected = true;
    this.socket?.removeAllListeners();
    this.socket?.destroy();
    this.socket = undefined;
    this.socketPromise = undefined;
  }

  private getSocket(): Promise<Socket> {
    this.socketPromise ??= connectWithTimeout(
      this.createPipeConnection,
      this.pipePath,
      CONNECTION_TIMEOUT_MS
    ).then((socket) => {
      this.socket = socket;
      return socket;
    });
    return this.socketPromise;
  }
}

function connectWithTimeout(
  createPipeConnection: (pipePath: string) => Socket,
  pipePath: string,
  timeoutMs: number
): Promise<Socket> {
  return new Promise<Socket>((resolve, reject) => {
    const socket = createPipeConnection(pipePath);
    let settled = false;
    const finish = (error?: Error): void => {
      if (settled) {
        return;
      }

      settled = true;
      clearTimeout(timeout);
      socket.off("connect", onConnect);
      socket.off("error", onError);
      if (error === undefined) {
        resolve(socket);
      } else {
        socket.destroy();
        reject(error);
      }
    };
    const onConnect = (): void => {
      finish();
    };
    const onError = (): void => {
      finish(new Error("Launcher readiness pipe connection failed."));
    };
    const timeout = setTimeout(() => {
      finish(new Error("Launcher readiness pipe connection timed out."));
    }, timeoutMs);
    socket.once("connect", onConnect);
    socket.once("error", onError);
  });
}

function writeLine(socket: Socket, line: string): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    socket.write(line, "utf8", (error) => {
      if (error === null || error === undefined) {
        resolve();
      } else {
        reject(error);
      }
    });
  });
}
