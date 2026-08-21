import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { CONTROL_API_VERSION } from "@qiongtu/contracts";
import { ControlStatus } from "./ControlStatus.js";

describe("ControlStatus", () => {
  it("shows a disconnected state with explanatory text", () => {
    render(
      <ControlStatus
        status={{
          apiVersion: CONTROL_API_VERSION,
          state: "not-connected",
          endpointKind: "named-pipe",
          reason: "not-started",
          detail: "The control lifecycle is not implemented yet.",
          retryAttempt: 0,
          checkedAt: "2026-08-20T00:00:00.000Z"
        }}
      />
    );

    expect(screen.getByText("本地控制服务：未连接")).toBeInTheDocument();
    expect(screen.getByText("The control lifecycle is not implemented yet.")).toBeInTheDocument();
    expect(screen.getByText(/边界：named-pipe/u)).toBeInTheDocument();
  });
});
