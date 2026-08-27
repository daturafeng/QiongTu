import { describe, expect, it } from "vitest";
import {
  CONTROL_METHOD_IMAGE_IMPORT_PREFLIGHT_GET,
  CONTROL_METHOD_IMAGE_IMPORT_PREFLIGHT_ITEM_LIST,
  CONTROL_METHOD_IMAGE_IMPORT_PREFLIGHT_START,
  isImageImportPreflightItem,
  isImageImportPreflightRun,
  isImageImportPreflightStartParameters,
  isPageResult,
  type ImageImportPreflightItem,
  type ImageImportPreflightRun,
  type PageResult
} from "./index.js";

const privacy = {
  pathsIncluded: false,
  locatorsIncluded: false,
  sourceKeysIncluded: false,
  hashesIncluded: false,
  objectKeysIncluded: false,
  stageReceiptsIncluded: false,
  quarantineIncluded: false,
  rawMetadataIncluded: false,
  serialNumbersIncluded: false,
  coordinatesIncluded: false,
  ownerSampleStatisticsIncluded: false
} as const;

const run: ImageImportPreflightRun = {
  preflightRunId: "source-preflight-run_01",
  importSessionId: "image-import-session_01",
  datasetVersionId: "dataset-version_01",
  sourceEligibilityState: "dji_supported",
  status: "completed",
  decision: "dji_supported",
  decisionReasonCode: "all_image_candidates_confirmed_dji",
  parserProfile: "source-preflight.v1",
  parserVersion: "1.0.0",
  policyVersion: "dji-source-policy.v1",
  totalItemCount: 1,
  imageCandidateCount: 1,
  sidecarCandidateCount: 0,
  completedItemCount: 1,
  supportsDjiItemCount: 1,
  outOfScopeItemCount: 0,
  unconfirmedItemCount: 0,
  conflictItemCount: 0,
  failedItemCount: 0,
  blockingImageCount: 0,
  createdAtUtc: "2026-08-27T00:00:00.000Z",
  startedAtUtc: "2026-08-27T00:00:01.000Z",
  updatedAtUtc: "2026-08-27T00:00:02.000Z",
  completedAtUtc: "2026-08-27T00:00:02.000Z",
  privacy
};

const item: ImageImportPreflightItem = {
  preflightItemId: "source-preflight-item_01",
  preflightRunId: run.preflightRunId,
  importSessionId: run.importSessionId,
  datasetVersionId: run.datasetVersionId,
  sortIndex: 0,
  displayName: "DJI_0001.JPG",
  candidateKind: "image_candidate",
  formatHint: "jpg",
  status: "completed",
  containerHint: "jpeg_hint",
  evidenceState: "supports_dji",
  evidenceKinds: ["dji_exif_manufacturer"],
  reasonCodes: [],
  createdAtUtc: "2026-08-27T00:00:00.000Z",
  updatedAtUtc: "2026-08-27T00:00:02.000Z",
  completedAtUtc: "2026-08-27T00:00:02.000Z",
  privacy
};

describe("image import preflight contract", () => {
  it("exports the fixed OpenSpec 3.2b methods", () => {
    expect([
      CONTROL_METHOD_IMAGE_IMPORT_PREFLIGHT_START,
      CONTROL_METHOD_IMAGE_IMPORT_PREFLIGHT_GET,
      CONTROL_METHOD_IMAGE_IMPORT_PREFLIGHT_ITEM_LIST
    ]).toEqual([
      "image-import-preflight.start",
      "image-import-preflight.get",
      "image-import-preflight-item.list"
    ]);
  });

  it("accepts only an opaque import session identifier as start input", () => {
    expect(isImageImportPreflightStartParameters({ importSessionId: run.importSessionId })).toBe(true);
    expect(isImageImportPreflightStartParameters({
      importSessionId: run.importSessionId,
      sourceRootPath: "D:\\private"
    })).toBe(false);
  });

  it("accepts bounded sanitized run and item pages", () => {
    const page: PageResult<ImageImportPreflightItem> = { items: [item], nextCursor: "opaque" };
    expect(isImageImportPreflightRun(run)).toBe(true);
    expect(isImageImportPreflightItem(item)).toBe(true);
    expect(isPageResult(page, isImageImportPreflightItem)).toBe(true);
  });

  it("rejects paths, source keys, hashes, raw metadata, serials, coordinates, and owner statistics", () => {
    const privateFields = [
      { absolutePath: "D:\\private\\DJI_0001.JPG" },
      { sourceEntryKey: "a".repeat(64) },
      { contentHash: "a".repeat(64) },
      { rawMetadata: { make: "DJI" } },
      { serialNumber: "private" },
      { latitude: 1 },
      { ownerSampleStatistics: { count: 1 } }
    ];
    for (const extra of privateFields) {
      expect(isImageImportPreflightItem({ ...item, ...extra })).toBe(false);
    }
  });

  it("requires all privacy flags to remain false", () => {
    expect(isImageImportPreflightRun({
      ...run,
      privacy: { ...privacy, rawMetadataIncluded: true }
    })).toBe(false);
  });
});
