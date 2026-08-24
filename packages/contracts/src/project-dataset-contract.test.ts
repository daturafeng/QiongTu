import { describe, expect, it } from "vitest";
import {
  CONTROL_METHOD_CRS_RECOMMEND,
  CONTROL_METHOD_DATASET_CREATE,
  CONTROL_METHOD_DATASET_VERSION_CREATE,
  CONTROL_METHOD_DATASET_VERSION_GET,
  CONTROL_METHOD_DATASET_VERSION_LIST,
  CONTROL_METHOD_PROJECT_CONFIRM_CRS,
  CONTROL_METHOD_PROJECT_CREATE,
  CONTROL_METHOD_PROJECT_GET,
  CONTROL_METHOD_PROJECT_LIST,
  CONTROL_METHOD_RESULT_LINEAGE,
  CONTROL_METHOD_RESULT_LIST,
  isCrsDefinitionInput,
  isCrsRecommendation,
  isDatasetVersion,
  isPageResult,
  isProject,
  isResultFile,
  isResultLineage,
  isResultSummary,
  type CrsDefinitionInput,
  type CrsRecommendation,
  type CrsRecommendParameters,
  type CrsSnapshot,
  type DatasetVersion,
  type PageResult,
  type Project,
  type ResultLineage,
  type ResultSummary
} from "./index.js";

const capturedAtUtc = "2026-08-24T00:00:00.000Z";
const createdAtUtc = "2026-08-24T00:00:01.000Z";
const submittedAtUtc = "2026-08-24T00:00:02.000Z";
const startedAtUtc = "2026-08-24T00:00:03.000Z";
const endedAtUtc = "2026-08-24T00:10:00.000Z";
const publishedAtUtc = "2026-08-24T00:11:00.000Z";
const objectSha256 = "a".repeat(64);
const parameterSha256 = "c".repeat(64);

const wgs84UtmInput: CrsDefinitionInput = {
  authority: "EPSG",
  code: "32650",
  name: "WGS 84 / UTM zone 50N",
  crsType: "projected",
  horizontalUnit: "metre",
  verticalReference: "unknown",
  axisOrder: "east-north"
};

const wgs84UtmCrs: CrsSnapshot = {
  ...wgs84UtmInput,
  capturedAtUtc
};

const project: Project = {
  projectId: "project_01",
  name: "Test project",
  spatialConfigurationStatus: "confirmed",
  lifecycleState: "active",
  defaultCrs: wgs84UtmCrs,
  createdAtUtc,
  updatedAtUtc: submittedAtUtc
};

const datasetVersion: DatasetVersion = {
  datasetVersionId: "dataset_version_01",
  datasetId: "dataset_01",
  versionNumber: 1,
  lifecycleState: "sealed",
  sourceEligibilityState: "dji_supported",
  qualityGateState: "passed",
  contentManifestSha256: "b".repeat(64),
  warningAcknowledgedAtUtc: submittedAtUtc,
  createdAtUtc,
  sealedAtUtc: submittedAtUtc
};

const qualityReport = {
  qualityReportId: "quality_report_01",
  reportType: "result_validation",
  versionNumber: 1,
  lifecycleState: "final",
  schemaVersion: "v1",
  summarySeverity: "warning",
  summary: {
    blocking: 0,
    warning: 1,
    info: 2
  },
  blockingCount: 0,
  warningCount: 1,
  infoCount: 2,
  createdAtUtc,
  finalizedAtUtc: publishedAtUtc
} as const;

const resultSummary: ResultSummary = {
  resultId: "result_01",
  resultSeriesId: "series_01",
  versionNumber: 1,
  sourceDatasetVersionId: datasetVersion.datasetVersionId,
  sourceProcessingJobId: "job_01",
  sourceJobExecutionId: "execution_01",
  sourceResultId: "result_source_01",
  resultKind: "mesh_3d_tiles",
  lifecycleState: "published",
  crs: wgs84UtmCrs,
  verticalReference: "unknown",
  localOrigin: {
    longitude: 116.2,
    latitude: 39.8,
    height: 42
  },
  axisConvention: "east-north-up",
  unit: "metre",
  bounds: {
    westLongitude: 116.10,
    southLatitude: 39.70,
    eastLongitude: 116.30,
    northLatitude: 39.90
  },
  resolutionDensity: {
    groundSampleDistanceMetres: 0.05
  },
  engineVersion: "engine 1.0",
  converterVersion: "tiles 1.0",
  parameterSha256,
  accuracyLevel: "georeferenced_visualization",
  qualityReport,
  createdAtUtc,
  publishedAtUtc,
  supersededByResultId: "result_02"
};

describe("project dataset control contract", () => {
  it("exports the OpenSpec task 2.5 method names unchanged", () => {
    expect([
      CONTROL_METHOD_PROJECT_CREATE,
      CONTROL_METHOD_PROJECT_LIST,
      CONTROL_METHOD_PROJECT_GET,
      CONTROL_METHOD_PROJECT_CONFIRM_CRS,
      CONTROL_METHOD_CRS_RECOMMEND,
      CONTROL_METHOD_DATASET_CREATE,
      CONTROL_METHOD_DATASET_VERSION_CREATE,
      CONTROL_METHOD_DATASET_VERSION_LIST,
      CONTROL_METHOD_DATASET_VERSION_GET,
      CONTROL_METHOD_RESULT_LIST,
      CONTROL_METHOD_RESULT_LINEAGE
    ]).toEqual([
      "project.create",
      "project.list",
      "project.get",
      "project.confirm-crs",
      "crs.recommend",
      "dataset.create",
      "dataset-version.create",
      "dataset-version.list",
      "dataset-version.get",
      "result.list",
      "result.lineage"
    ]);
  });

  it("keeps CRS input free of client-generated capturedAtUtc", () => {
    expect(isCrsDefinitionInput(wgs84UtmInput)).toBe(true);
    expect(isCrsDefinitionInput({
      ...wgs84UtmInput,
      capturedAtUtc
    })).toBe(false);
  });

  it("accepts recommended and null-bounds not-recommended CRS responses", () => {
    const nullBoundsRequest: CrsRecommendParameters = {
      bounds: null
    };
    const recommended: CrsRecommendation = {
      status: "recommended",
      inputBounds: {
        westLongitude: 116.10,
        southLatitude: 39.70,
        eastLongitude: 116.30,
        northLatitude: 39.90
      },
      suggestedCrs: wgs84UtmCrs
    };
    const notRecommended: CrsRecommendation = {
      status: "not-recommended",
      reasonCode: "insufficient_location_metadata"
    };

    expect(nullBoundsRequest.bounds).toBeNull();
    expect(isCrsRecommendation(recommended)).toBe(true);
    expect(isCrsRecommendation(notRecommended)).toBe(true);
  });

  it("accepts all database-backed project spatial states", () => {
    for (const spatialConfigurationStatus of ["pending", "suggested", "confirmed", "insufficient_metadata"] as const) {
      expect(isProject({
        ...project,
        spatialConfigurationStatus
      })).toBe(true);
    }
  });

  it("rejects dataset-version fields that are not present in the current schema", () => {
    expect(isDatasetVersion(datasetVersion)).toBe(true);
    expect(isDatasetVersion({
      ...datasetVersion,
      name: "removed",
      updatedAtUtc: submittedAtUtc,
      wgs84Bounds: {
        westLongitude: 116.10,
        southLatitude: 39.70,
        eastLongitude: 116.30,
        northLatitude: 39.90
      }
    })).toBe(false);
  });

  it("accepts bounded keyset pages for project summaries", () => {
    const page: PageResult<Project> = {
      items: [project],
      nextCursor: "opaque-cursor"
    };

    expect(isPageResult(page, isProject)).toBe(true);
  });

  it("accepts a schema-backed result lineage summary with published sha256 object keys", () => {
    const lineage: ResultLineage = {
      target: resultSummary,
      series: {
        resultSeriesId: "series_01",
        projectId: project.projectId,
        datasetVersionId: datasetVersion.datasetVersionId,
        seriesKind: "mesh_3d_tiles",
        name: "Mesh tiles",
        parentSeriesId: "series_source_01",
        createdAtUtc
      },
      project,
      sourceDatasetVersion: datasetVersion,
      sourceProcessingJob: {
        processingJobId: "job_01",
        jobType: "tileset_conversion",
        parameterProfile: "standard",
        parameterSchemaVersion: "v1",
        parameterSha256,
        lifecycleState: "succeeded",
        createdAtUtc,
        submittedAtUtc,
        startedAtUtc,
        endedAtUtc
      },
      sourceJobExecution: {
        jobExecutionId: "execution_01",
        processingJobId: "job_01",
        attemptNumber: 1,
        executionMode: "full",
        workerType: "tileset",
        workerVersion: "worker 1.0",
        engineName: "engine",
        engineVersion: "1.0",
        parameterSha256,
        lifecycleState: "succeeded",
        startedAtUtc,
        endedAtUtc
      },
      directDependencies: [
        {
          resultId: resultSummary.resultId,
          dependsOnResultId: "result_source_01",
          dependencyKind: "derived_from"
        }
      ],
      availableFiles: [
        {
          resultFileId: "result_file_01",
          resultId: resultSummary.resultId,
          fileObjectId: "file_object_01",
          fileRole: "tileset_json",
          relativePath: "tileset.json",
          isRequired: true,
          objectKey: "sha256/aa/" + objectSha256,
          contentHashSnapshot: objectSha256,
          byteLengthSnapshot: 512,
          mediaType: "application/json"
        }
      ],
      finalQualityReports: [qualityReport]
    };

    expect(isResultLineage(lineage)).toBe(true);
  });

  it("rejects response payloads that expose absolute paths, tokens, staging, or quarantine", () => {
    expect(isResultSummary({
      ...resultSummary,
      absolutePath: "D:\\private\\project\\tileset.json"
    })).toBe(false);

    expect(isResultSummary({
      ...resultSummary,
      updatedAtUtc: submittedAtUtc
    })).toBe(false);

    expect(isResultSummary({
      ...resultSummary,
      qualityReport: {
        ...qualityReport,
        evidenceLevel: "not-a-public-contract-field"
      }
    })).toBe(false);

    expect(isResultFile({
      resultFileId: "result_file_02",
      resultId: resultSummary.resultId,
      fileObjectId: "file_object_02",
      fileRole: "metadata",
      relativePath: "staging/private-token.bin",
      isRequired: false,
      objectKey: "staging/private-token/quarantine.bin",
      contentHashSnapshot: objectSha256,
      byteLengthSnapshot: 99,
      mediaType: "application/octet-stream",
      accessToken: "do-not-leak"
    })).toBe(false);

    expect(isResultFile({
      resultFileId: "result_file_03",
      resultId: resultSummary.resultId,
      fileObjectId: "file_object_03",
      fileRole: "metadata",
      relativePath: "metadata/private.bin",
      isRequired: false,
      objectKey: "quarantine/aa/private.bin",
      contentHashSnapshot: objectSha256,
      byteLengthSnapshot: 99,
      mediaType: "application/octet-stream"
    })).toBe(false);
  });
});
