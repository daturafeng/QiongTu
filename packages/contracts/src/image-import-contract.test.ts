import { describe, expect, it } from "vitest";
import {
  CONTROL_METHOD_IMAGE_IMPORT_CANCEL,
  CONTROL_METHOD_IMAGE_IMPORT_ENTRY_LIST,
  CONTROL_METHOD_IMAGE_IMPORT_GET,
  CONTROL_METHOD_IMAGE_IMPORT_LIST,
  CONTROL_METHOD_IMAGE_IMPORT_RESUME,
  CONTROL_METHOD_IMAGE_IMPORT_START,
  isImageImportEntry,
  isImageImportResumeParameters,
  isImageImportSession,
  isImageImportStartParameters,
  isPageResult,
  type ImageImportEntry,
  type ImageImportSession,
  type ImageImportStartParameters,
  type PageResult
} from "./index.js";

const createdAtUtc = "2026-08-24T00:00:00.000Z";
const updatedAtUtc = "2026-08-24T00:00:01.000Z";
const terminalAtUtc = "2026-08-24T00:00:02.000Z";

const privacy = {
  pathsIncluded: false,
  hashesIncluded: false,
  objectKeysIncluded: false,
  stageReceiptsIncluded: false,
  quarantineIncluded: false,
  sourceLocatorsIncluded: false
} as const;

const session: ImageImportSession = {
  importSessionId: "image-import-session_01",
  datasetVersionId: "dataset-version_01",
  sourceEligibilityState: "dji_supported",
  status: "ready",
  totalEntryCount: 2,
  availableEntryCount: 1,
  duplicateEntryCount: 1,
  failedEntryCount: 0,
  cancelledEntryCount: 0,
  createdAtUtc,
  updatedAtUtc,
  privacy
};

const entry: ImageImportEntry = {
  importEntryId: "image-import-entry_01",
  importSessionId: session.importSessionId,
  datasetVersionId: session.datasetVersionId,
  sortIndex: 0,
  displayName: "DJI_0001.JPG",
  byteLengthSnapshot: 42,
  sourceLastWriteTimeUtc: createdAtUtc,
  status: "available",
  createdAtUtc,
  updatedAtUtc,
  terminalAtUtc,
  privacy
};

describe("image import control contract", () => {
  it("exports the OpenSpec 3.1a method names unchanged", () => {
    expect([
      CONTROL_METHOD_IMAGE_IMPORT_START,
      CONTROL_METHOD_IMAGE_IMPORT_RESUME,
      CONTROL_METHOD_IMAGE_IMPORT_CANCEL,
      CONTROL_METHOD_IMAGE_IMPORT_GET,
      CONTROL_METHOD_IMAGE_IMPORT_LIST,
      CONTROL_METHOD_IMAGE_IMPORT_ENTRY_LIST
    ]).toEqual([
      "image-import.start",
      "image-import.resume",
      "image-import.cancel",
      "image-import.get",
      "image-import.list",
      "image-import-entry.list"
    ]);
  });

  it("accepts a Windows source selection only as a control input", () => {
    const start: ImageImportStartParameters = {
      datasetVersionId: session.datasetVersionId,
      sourceRootPath: "D:\\images\\flight-01"
    };

    expect(isImageImportStartParameters(start)).toBe(true);
    expect(isImageImportResumeParameters({
      importSessionId: session.importSessionId,
      sourceRootPath: "E:\\DCIM"
    })).toBe(true);
    expect(isImageImportStartParameters({
      ...start,
      sourceRootPath: "relative\\flight-01"
    })).toBe(false);
  });

  it("accepts sanitized session and entry pages", () => {
    const sessionPage: PageResult<ImageImportSession> = {
      items: [session],
      nextCursor: "opaque"
    };
    const entryPage: PageResult<ImageImportEntry> = {
      items: [entry]
    };

    expect(isImageImportSession(session)).toBe(true);
    expect(isImageImportEntry(entry)).toBe(true);
    expect(isPageResult(sessionPage, isImageImportSession)).toBe(true);
    expect(isPageResult(entryPage, isImageImportEntry)).toBe(true);
  });

  it("rejects responses that expose hashes, object keys, stage receipts, quarantine, locators, or paths", () => {
    expect(isImageImportSession({
      ...session,
      sourceRootKey: "a".repeat(64)
    })).toBe(false);
    expect(isImageImportSession({
      ...session,
      sourceLocatorManifestId: "manifest_01"
    })).toBe(false);
    expect(isImageImportEntry({
      ...entry,
      contentHash: "a".repeat(64)
    })).toBe(false);
    expect(isImageImportEntry({
      ...entry,
      fileObjectId: "file-object-01"
    })).toBe(false);
    expect(isImageImportEntry({
      ...entry,
      stageReceiptId: "stage-01"
    })).toBe(false);
    expect(isImageImportEntry({
      ...entry,
      quarantineId: "quarantine-01"
    })).toBe(false);
    expect(isImageImportEntry({
      ...entry,
      displayName: "folder/DJI_0001.JPG"
    })).toBe(false);
    expect(isImageImportEntry({
      ...entry,
      absolutePath: "D:\\source\\DJI_0001.JPG"
    })).toBe(false);
  });

  it("requires every image import response privacy flag to be false", () => {
    expect(isImageImportSession({
      ...session,
      privacy: {
        ...privacy,
        hashesIncluded: true
      }
    })).toBe(false);
  });
});
