import { ChildProcess } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { Socket } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it, vi } from "vitest";
import {
  CONTROL_API_VERSION,
  isControlConnectionStatus
} from "@qiongtu/contracts";
import {
  createInitialControlStatus,
  ControlProcessManager,
  getControlDiscoveryFilePath,
  parseControlDiscovery,
  parseControlDiscoveryText,
  toWindowsPipePath
} from "./control-manager.js";

describe("control service discovery", () => {
  it("accepts only the versioned current-user named pipe boundary", () => {
    const discovery = parseControlDiscovery({
      apiVersion: CONTROL_API_VERSION,
      endpointKind: "named-pipe",
      pipeName: "qiongtu-control-v1-Abc123_45",
      processId: 42,
      startedAtUtc: "2026-08-20T00:00:00.000Z"
    });

    expect(discovery?.pipeName).toBe("qiongtu-control-v1-Abc123_45");
    expect(toWindowsPipePath("qiongtu-control-v1-Abc123_45")).toBe("\\\\.\\pipe\\qiongtu-control-v1-Abc123_45");
  });

  it("rejects paths and unversioned pipe names from discovery", () => {
    expect(parseControlDiscovery({
      apiVersion: CONTROL_API_VERSION,
      endpointKind: "named-pipe",
      pipeName: "..\\unsafe",
      processId: 42,
      startedAtUtc: "2026-08-20T00:00:00.000Z"
    })).toBeUndefined();

    expect(parseControlDiscovery({
      apiVersion: CONTROL_API_VERSION,
      endpointKind: "named-pipe",
      pipeName: "qiongtu-control-v1-Abc123_45",
      processId: 42,
      startedAtUtc: "not-a-date"
    })).toBeUndefined();
  });

  it("accepts a legacy single UTF-8 BOM while the control service migrates to BOM-free JSON", () => {
    const discovery = parseControlDiscoveryText(`\ufeff${JSON.stringify({
      apiVersion: CONTROL_API_VERSION,
      endpointKind: "named-pipe",
      pipeName: "qiongtu-control-v1-Abc123_45",
      processId: 42,
      startedAtUtc: "2026-08-20T00:00:00.000Z"
    })}`);

    expect(discovery?.processId).toBe(42);
  });

  it("keeps renderer status sanitized", () => {
    const status = createInitialControlStatus(new Date("2026-08-20T00:00:00.000Z"));

    expect(isControlConnectionStatus(status)).toBe(true);
    expect(JSON.stringify(status)).not.toContain("QiongTu.Control.exe");
  });

  it("uses the local app data discovery filename", () => {
    expect(getControlDiscoveryFilePath("C:/Users/admin/AppData/Local")).toMatch(/QiongTu[\\/]runtime[\\/]control\.json$/u);
  });

  it("starts a fixed executable when a discovery process is stale", async () => {
    const testRoot = await mkdtemp(join(tmpdir(), "qiongtu-control-manager-"));
    const discoveryFilePath = join(testRoot, "control.json");
    const child = new ChildProcess();
    const unref = vi.spyOn(child, "unref").mockImplementation(() => undefined);
    const spawnControlProcess = vi.fn(() => child);
    await writeFile(discoveryFilePath, JSON.stringify({
      apiVersion: CONTROL_API_VERSION,
      endpointKind: "named-pipe",
      pipeName: "qiongtu-control-v1-Abc123_45",
      processId: 42,
      startedAtUtc: "2026-08-20T00:00:00.000Z"
    }));

    const manager = new ControlProcessManager({
      discoveryFilePath,
      executableCandidates: [process.execPath],
      connectTimeoutMs: 50,
      isProcessAlive: () => false,
      spawnControlProcess
    });
    try {
      await manager.start();
      expect(spawnControlProcess).toHaveBeenCalledWith(process.execPath, [], expect.objectContaining({
        detached: true,
        stdio: "ignore",
        windowsHide: true
      }));
      expect(unref).toHaveBeenCalledOnce();
    } finally {
      manager.disconnectOnly();
      await rm(testRoot, { recursive: true, force: true });
    }
  });

  it("tries the authenticated pipe before PID probing and does not spawn when hello succeeds", async () => {
    const testRoot = await mkdtemp(join(tmpdir(), "qiongtu-control-manager-"));
    const discoveryFilePath = join(testRoot, "control.json");
    const spawnControlProcess = vi.fn(() => new ChildProcess());
    const socket = new Socket();
    vi.spyOn(socket, "write").mockImplementation((data) => {
      const request = JSON.parse(String(data)) as { requestId: string };
      queueMicrotask(() => socket.emit("data", Buffer.from(`${JSON.stringify({
        apiVersion: CONTROL_API_VERSION,
        requestId: request.requestId,
        ok: true,
        result: {
          apiVersion: CONTROL_API_VERSION,
          processId: 42,
          pipeName: "qiongtu-control-v1-Abc123_45",
          artifactBaseUrl: "http://127.0.0.1:12345"
        }
      })}\n`)));
      return true;
    });
    await writeFile(discoveryFilePath, JSON.stringify({
      apiVersion: CONTROL_API_VERSION,
      endpointKind: "named-pipe",
      pipeName: "qiongtu-control-v1-Abc123_45",
      processId: 42,
      startedAtUtc: "2026-08-20T00:00:00.000Z"
    }));

    const manager = new ControlProcessManager({
      discoveryFilePath,
      executableCandidates: [process.execPath],
      connectTimeoutMs: 50,
      createPipeConnection: () => {
        queueMicrotask(() => socket.emit("connect"));
        return socket;
      },
      isProcessAlive: () => false,
      spawnControlProcess
    });
    try {
      await manager.start();
      expect(manager.getStatus().state).toBe("connected");
      expect(spawnControlProcess).not.toHaveBeenCalled();
    } finally {
      manager.disconnectOnly();
      await rm(testRoot, { recursive: true, force: true });
    }
  });

  it("keeps explicit smoke discovery connect-only when the file is unavailable", async () => {
    const testRoot = await mkdtemp(join(tmpdir(), "qiongtu-control-manager-"));
    const spawnControlProcess = vi.fn(() => new ChildProcess());
    const manager = new ControlProcessManager({
      discoveryFilePath: join(testRoot, "missing.json"),
      executableCandidates: [process.execPath],
      connectTimeoutMs: 50,
      allowStartControlProcess: false,
      spawnControlProcess
    });
    try {
      await manager.start();
      expect(spawnControlProcess).not.toHaveBeenCalled();
      expect(manager.getStatus().state).toBe("reconnecting");
    } finally {
      manager.disconnectOnly();
      await rm(testRoot, { recursive: true, force: true });
    }
  });
});
