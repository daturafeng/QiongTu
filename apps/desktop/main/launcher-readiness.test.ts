import { Socket } from "node:net";
import { describe, expect, it, vi } from "vitest";
import { LAUNCHER_API_VERSION } from "@qiongtu/contracts";
import {
  createLauncherReadinessReporter,
  LAUNCH_NONCE_ENVIRONMENT_KEY,
  LAUNCH_PIPE_ENVIRONMENT_KEY
} from "./launcher-readiness.js";

describe("launcher readiness reporting", () => {
  it("stays disabled for invalid configuration and consumes environment credentials", async () => {
    const environment: NodeJS.ProcessEnv = {
      [LAUNCH_PIPE_ENVIRONMENT_KEY]: "..\\unsafe",
      [LAUNCH_NONCE_ENVIRONMENT_KEY]: "bad"
    };
    const reporter = createLauncherReadinessReporter({ environment });

    expect(reporter.enabled).toBe(false);
    expect(await reporter.report("main.started")).toBe(false);
    expect(environment[LAUNCH_PIPE_ENVIRONMENT_KEY]).toBeUndefined();
    expect(environment[LAUNCH_NONCE_ENVIRONMENT_KEY]).toBeUndefined();
  });

  it("sends only bounded fixed-shape events with a monotonic sequence", async () => {
    const environment: NodeJS.ProcessEnv = {
      [LAUNCH_PIPE_ENVIRONMENT_KEY]: "qiongtu-launch-v1-Abc123_45",
      [LAUNCH_NONCE_ENVIRONMENT_KEY]: "a".repeat(64)
    };
    const lines: string[] = [];
    const socket = new Socket();
    vi.spyOn(socket, "write").mockImplementation(((line: string, _encoding: string, callback: (error?: Error) => void) => {
      lines.push(line);
      callback();
      return true;
    }) as typeof socket.write);
    const reporter = createLauncherReadinessReporter({
      environment,
      processId: 42,
      now: () => new Date("2026-08-21T00:00:00.000Z"),
      createPipeConnection: () => {
        queueMicrotask(() => socket.emit("connect"));
        return socket;
      }
    });

    expect(await reporter.report("main.started")).toBe(true);
    expect(await reporter.report("app.ready")).toBe(true);
    const events = lines.map((line) => JSON.parse(line) as Record<string, unknown>);
    expect(events).toEqual([
      {
        apiVersion: LAUNCHER_API_VERSION,
        nonce: "a".repeat(64),
        processId: 42,
        sequence: 1,
        stage: "main.started",
        timestampUtc: "2026-08-21T00:00:00.000Z"
      },
      {
        apiVersion: LAUNCHER_API_VERSION,
        nonce: "a".repeat(64),
        processId: 42,
        sequence: 2,
        stage: "app.ready",
        timestampUtc: "2026-08-21T00:00:00.000Z"
      }
    ]);
    expect(lines.join("\n")).not.toMatch(/control|token|image|project|path/iu);
    reporter.disconnect();
  });
});
