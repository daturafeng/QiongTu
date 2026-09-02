import { describe, expect, it } from "vitest";
import {
  CONTROL_METHOD_POSITIONING_AUX_FILE_LIST,
  CONTROL_METHOD_POSITIONING_AUX_IMPORT_CANCEL,
  CONTROL_METHOD_POSITIONING_AUX_IMPORT_GET,
  CONTROL_METHOD_POSITIONING_AUX_IMPORT_RESUME,
  isPageResult,
  isPositioningAuxFile,
  isPositioningAuxFileListParameters,
  isPositioningAuxImportCancelParameters,
  isPositioningAuxImportGetParameters,
  isPositioningAuxImportResumeParameters,
  isPositioningAuxImportRun,
  type PageResult,
  type PositioningAuxFile,
  type PositioningAuxImportRun
} from "./index.js";

const createdAtUtc = "2026-08-31T00:00:00.000Z";
const updatedAtUtc = "2026-08-31T00:00:01.000Z";
const completedAtUtc = "2026-08-31T00:00:02.000Z";

const privacy = {
  pathsIncluded: false,
  locatorsIncluded: false,
  sourceKeysIncluded: false,
  hashesIncluded: false,
  objectKeysIncluded: false,
  stageReceiptsIncluded: false,
  rawRecordsIncluded: false,
  coordinatesIncluded: false,
  timestampsIncluded: false,
  ownerSampleStatisticsIncluded: false
} as const;

const run: PositioningAuxImportRun = {
  runId: "positioning-aux-run_01",
  importSessionId: "image-import-session_01",
  datasetVersionId: "dataset-version_01",
  status: "completed",
  totalFileCount: 2,
  completedFileCount: 2,
  failedFileCount: 0,
  associationProfile: "positioning-aux-association.v1",
  associationPolicyVersion: "positioning-aux-association-policy.v1",
  parserProfile: "cas-positioning-aux.v1",
  parserName: "QiongTu.ImageProbe",
  parserVersion: "1.0.0",
  createdAtUtc,
  startedAtUtc: createdAtUtc,
  updatedAtUtc,
  completedAtUtc,
  privacy
};

const mrkFile: PositioningAuxFile = {
  positioningAuxFileId: "positioning-aux-file_01",
  runId: run.runId,
  datasetVersionId: run.datasetVersionId,
  auxType: "mrk",
  retentionState: "retained",
  parseState: "parsed",
  qualityState: "passed",
  parserProfile: "cas-positioning-aux.v1",
  parserName: "QiongTu.ImageProbe",
  parserVersion: "1.0.0",
  usageState: "used",
  reasonCode: "mrk_records_cover_private_group",
  createdAtUtc,
  updatedAtUtc,
  retainedAtUtc: updatedAtUtc,
  parsedAtUtc: completedAtUtc,
  qualityCheckedAtUtc: completedAtUtc,
  privacy
};

const navFile: PositioningAuxFile = {
  ...mrkFile,
  positioningAuxFileId: "positioning-aux-file_02",
  auxType: "nav",
  parseState: "unsupported",
  qualityState: "not_checked",
  usageState: "not_recorded",
  reasonCode: "rinex_parser_not_approved"
};

describe("positioning auxiliary control contract", () => {
  it("exports the OpenSpec 3.4 method names", () => {
    expect([
      CONTROL_METHOD_POSITIONING_AUX_IMPORT_GET,
      CONTROL_METHOD_POSITIONING_AUX_IMPORT_RESUME,
      CONTROL_METHOD_POSITIONING_AUX_IMPORT_CANCEL,
      CONTROL_METHOD_POSITIONING_AUX_FILE_LIST
    ]).toEqual([
      "positioning-aux-import.get",
      "positioning-aux-import.resume",
      "positioning-aux-import.cancel",
      "positioning-aux-file.list"
    ]);
  });

  it("validates get, resume, cancel, and file list parameters", () => {
    expect(isPositioningAuxImportGetParameters({ runId: run.runId })).toBe(true);
    expect(isPositioningAuxImportResumeParameters({
      runId: run.runId,
      sourceRootPath: "D:\\flight-01"
    })).toBe(true);
    expect(isPositioningAuxImportCancelParameters({ runId: run.runId })).toBe(true);
    expect(isPositioningAuxFileListParameters({
      datasetVersionId: run.datasetVersionId,
      pageSize: 50,
      cursor: "opaque"
    })).toBe(true);
    expect(isPositioningAuxFileListParameters({
      runId: run.runId,
      pageSize: 20
    })).toBe(true);
    expect(isPositioningAuxImportResumeParameters({
      runId: run.runId,
      sourceRootPath: "relative\\flight-01"
    })).toBe(false);
    expect(isPositioningAuxFileListParameters({
      datasetVersionId: run.datasetVersionId,
      pageSize: 51
    })).toBe(false);
  });

  it("accepts sanitized run and file pages", () => {
    const page: PageResult<PositioningAuxFile> = {
      items: [mrkFile, navFile],
      nextCursor: "opaque"
    };

    expect(isPositioningAuxImportRun(run)).toBe(true);
    expect(isPositioningAuxFile(mrkFile)).toBe(true);
    expect(isPositioningAuxFile(navFile)).toBe(true);
    expect(isPageResult(page, isPositioningAuxFile)).toBe(true);
  });

  it("rejects invalid retained, parsed, quality, and usage states", () => {
    expect(isPositioningAuxImportRun({
      ...run,
      status: "running"
    })).toBe(false);
    expect(isPositioningAuxFile({
      ...mrkFile,
      auxType: "rinex"
    })).toBe(false);
    expect(isPositioningAuxFile({
      ...mrkFile,
      parseState: "not-attempted"
    })).toBe(false);
    expect(isPositioningAuxFile({
      ...mrkFile,
      qualityState: "unchecked"
    })).toBe(false);
    expect(isPositioningAuxFile({
      ...mrkFile,
      usageState: "pending"
    })).toBe(false);
    expect(isPositioningAuxFile({
      ...mrkFile,
      retentionState: "retained",
      parseState: "unsupported",
      qualityState: "not_checked",
      usageState: "used"
    })).toBe(false);
  });

  it("rejects paths, locators, keys, hashes, raw records, coordinates, timestamps, and owner statistics", () => {
    const privateFields = [
      { absolutePath: "D:\\private\\flight.MRK" },
      { sourceLocator: "protected-local" },
      { sourceEntryKey: "a".repeat(64) },
      { contentHash: "a".repeat(64) },
      { objectKey: "sha256/aa/" + "a".repeat(64) },
      { stageReceiptId: "stage_01" },
      { rawRecords: ["private"] },
      { latitude: 1 },
      { captureTimestamp: "2026-08-31T00:00:00.000Z" },
      { ownerSampleStatistics: { count: 1 } }
    ];

    for (const extra of privateFields) {
      expect(isPositioningAuxFile({ ...mrkFile, ...extra })).toBe(false);
    }
  });

  it("requires all privacy flags to remain false while allowing entity timestamps", () => {
    expect(isPositioningAuxImportRun({
      ...run,
      privacy: {
        ...privacy,
        timestampsIncluded: true
      }
    })).toBe(false);
    expect(isPositioningAuxFile({
      ...mrkFile,
      createdAtUtc,
      updatedAtUtc,
      privacy
    })).toBe(true);
  });
});
