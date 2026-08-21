import { describe, expect, it } from "vitest";
import {
  isLaunchReadinessEvent,
  LAUNCHER_API_VERSION,
  type LaunchReadinessEvent
} from "./index.js";

describe("LaunchReadinessEvent contract", () => {
  it("accepts a bounded versioned readiness event", () => {
    const event: LaunchReadinessEvent = {
      apiVersion: LAUNCHER_API_VERSION,
      nonce: "a".repeat(64),
      processId: 42,
      sequence: 1,
      stage: "main.started",
      timestampUtc: "2026-08-21T00:00:00.000Z"
    };

    expect(isLaunchReadinessEvent(event)).toBe(true);
  });

  it("rejects an invalid nonce or unrecognized stage", () => {
    expect(isLaunchReadinessEvent({
      apiVersion: LAUNCHER_API_VERSION,
      nonce: "not-a-secret",
      processId: 42,
      sequence: 1,
      stage: "device.disabled",
      timestampUtc: "2026-08-21T00:00:00.000Z"
    })).toBe(false);
  });
});
