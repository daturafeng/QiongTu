import { describe, expect, it } from "vitest";
import {
  CONTROL_API_VERSION,
  isControlConnectionStatus,
  type ControlConnectionStatus
} from "./index.js";

describe("ControlConnectionStatus contract", () => {
  it("accepts the sanitized named pipe status shape", () => {
    const status: ControlConnectionStatus = {
      apiVersion: CONTROL_API_VERSION,
      state: "reconnecting",
      endpointKind: "named-pipe",
      reason: "pipe-unreachable",
      detail: "控制服务发现文件存在，但命名管道不可连接。",
      retryAttempt: 2,
      checkedAt: "2026-08-20T00:00:00.000Z",
      nextRetryDelayMs: 1_000
    };

    expect(isControlConnectionStatus(status)).toBe(true);
  });

  it("rejects payloads that include unsupported connection states", () => {
    expect(isControlConnectionStatus({
      apiVersion: CONTROL_API_VERSION,
      state: "terminated",
      endpointKind: "named-pipe",
      reason: "connected",
      detail: "bad",
      retryAttempt: 0,
      checkedAt: "2026-08-20T00:00:00.000Z"
    })).toBe(false);
  });
});
