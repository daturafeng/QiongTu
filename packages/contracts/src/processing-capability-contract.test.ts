import { describe, expect, it } from "vitest";
import {
  CONTROL_METHOD_CAPABILITY_GET,
  CONTROL_METHOD_WORKER_ADMISSION_CHECK,
  isProcessingCapabilityReport,
  isWorkerAdmissionCheckParameters,
  isWorkerAdmissionResult,
  type ProcessingCapabilityReport,
  type WorkerAdmissionResult
} from "./index.js";

const capturedAt = "2026-08-24T00:00:00.000Z";

const allowedAdmission: WorkerAdmissionResult = {
  workerType: "photogrammetry-cpu",
  profile: "standard",
  decision: "allowed",
  blockingReasons: []
};

const deniedAdmission: WorkerAdmissionResult = {
  workerType: "gaussian-cuda",
  profile: "standard",
  decision: "denied",
  blockingReasons: [
    {
      category: "missing",
      code: "cuda_missing",
      message: "CUDA driver API is not available."
    },
    {
      category: "insufficient",
      code: "vram_below_minimum",
      message: "Available GPU memory is below the configured worker threshold."
    }
  ]
};

const report: ProcessingCapabilityReport = {
  schemaVersion: "qiongtu.processing-capability.v1",
  requirementsVersion: "qiongtu.worker-requirements.v1",
  capturedAt,
  durationMs: 125,
  host: {
    status: "present",
    operatingSystem: "Windows",
    architecture: "x64",
    processArchitecture: "x64",
    sessionKind: "console"
  },
  cpu: {
    status: "present",
    logicalProcessorCount: 16,
    architecture: "x64"
  },
  memory: {
    status: "present",
    totalBytes: 64 * 1024 * 1024 * 1024,
    availableBytes: 48 * 1024 * 1024 * 1024
  },
  storage: [
    {
      role: "objects",
      totalBytes: 2_000_000_000_000,
      availableBytes: 1_500_000_000_000,
      driveType: "fixed",
      status: "present"
    },
    {
      role: "temp",
      totalBytes: 1_000_000_000_000,
      availableBytes: 800_000_000_000,
      driveType: "fixed",
      status: "present"
    }
  ],
  nvidia: {
    status: "present",
    cudaStatus: "missing",
    driverVersion: "551.86",
    reasonCode: "cuda_driver_api_missing",
    gpus: [
      {
        index: 0,
        name: "NVIDIA GeForce RTX",
        totalMemoryBytes: 12 * 1024 * 1024 * 1024,
        freeMemoryBytes: 8 * 1024 * 1024 * 1024,
        status: "present"
      }
    ]
  },
  workerAdmissions: [allowedAdmission, deniedAdmission],
  privacy: {
    pathsIncluded: false,
    tokensIncluded: false,
    userNameIncluded: false,
    machineNameIncluded: false,
    environmentIncluded: false,
    commandLineIncluded: false
  }
};

describe("processing capability contract", () => {
  it("exports the OpenSpec task 2.6 method names unchanged", () => {
    expect(CONTROL_METHOD_CAPABILITY_GET).toBe("capability.get");
    expect(CONTROL_METHOD_WORKER_ADMISSION_CHECK).toBe("worker.admission.check");
  });

  it("accepts a sanitized capability report where NVIDIA is present but CUDA is missing", () => {
    expect(isProcessingCapabilityReport(report)).toBe(true);
  });

  it("accepts bounded worker admission parameters and decisions", () => {
    expect(isWorkerAdmissionCheckParameters({ workerType: "gaussian-cuda" })).toBe(true);
    expect(isWorkerAdmissionResult(allowedAdmission)).toBe(true);
    expect(isWorkerAdmissionResult(deniedAdmission)).toBe(true);
    expect(isWorkerAdmissionResult({
      workerType: "unknown-worker",
      profile: "standard",
      decision: "unknown",
      blockingReasons: [
        {
          category: "unknown",
          code: "probe_timeout",
          message: "The bounded hardware probe timed out."
        },
        {
          category: "incompatible",
          code: "driver_below_matrix",
          message: "The detected NVIDIA driver does not satisfy the compatibility matrix."
        }
      ]
    })).toBe(true);
  });

  it("rejects paths, tokens, usernames, machine ids, environment, command lines, GPU UUIDs, and PCI ids", () => {
    expect(isProcessingCapabilityReport({
      ...report,
      userName: "alice"
    })).toBe(false);
    expect(isProcessingCapabilityReport({
      ...report,
      machineName: "WORKSTATION-01"
    })).toBe(false);
    expect(isProcessingCapabilityReport({
      ...report,
      environment: { PATH: "secret" }
    })).toBe(false);
    expect(isProcessingCapabilityReport({
      ...report,
      commandLine: "QiongTu.Control.exe --runtime-dir D:\\private"
    })).toBe(false);
    expect(isProcessingCapabilityReport({
      ...report,
      storage: [
        {
          ...report.storage[0],
          path: "D:\\projects\\secret"
        }
      ]
    })).toBe(false);
    expect(isProcessingCapabilityReport({
      ...report,
      nvidia: {
        ...report.nvidia,
        gpus: [
          {
            ...report.nvidia.gpus[0],
            uuid: "GPU-secret",
            pciBusId: "0000:01:00.0"
          }
        ]
      }
    })).toBe(false);
    expect(isWorkerAdmissionResult({
      ...deniedAdmission,
      blockingReasons: [
        {
          ...deniedAdmission.blockingReasons[0],
          accessToken: "do-not-leak"
        }
      ]
    })).toBe(false);
  });

  it("rejects unbounded arrays, unbounded strings, unsupported statuses, and allowed decisions with blockers", () => {
    expect(isWorkerAdmissionCheckParameters({ workerType: "x".repeat(129) })).toBe(false);
    expect(isProcessingCapabilityReport({
      ...report,
      storage: Array.from({ length: 9 }, (_, index) => ({
        role: "volume-" + index,
        totalBytes: 1,
        availableBytes: 1,
        driveType: "fixed",
        status: "present"
      }))
    })).toBe(false);
    expect(isProcessingCapabilityReport({
      ...report,
      nvidia: {
        ...report.nvidia,
        cudaStatus: "incompatible"
      }
    })).toBe(false);
    expect(isWorkerAdmissionResult({
      workerType: "photogrammetry-cpu",
      profile: "standard",
      decision: "allowed",
      blockingReasons: [
        {
          category: "missing",
          code: "unexpected_blocker",
          message: "Allowed admissions must not carry blockers."
        }
      ]
    })).toBe(false);
    expect(isWorkerAdmissionResult({
      ...allowedAdmission,
      profile: ""
    })).toBe(false);
    expect(isProcessingCapabilityReport({
      ...report,
      requirementsVersion: ""
    })).toBe(false);
  });
});
