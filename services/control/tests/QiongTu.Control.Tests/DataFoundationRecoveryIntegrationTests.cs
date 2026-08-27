using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class DataFoundationRecoveryIntegrationTests
{
    [TestMethod]
    public void ProcessingJobSealsDatasetVersionAndNewEditsCreateMonotonicVersion()
    {
        using var scope = IntegrationScope.Create();
        var project = scope.Catalog.CreateProject(
            "recovery-project-create",
            new ProjectCreateParameters("Recovery project", null, null));
        var dataset = scope.Catalog.CreateDataset(
            "recovery-dataset-create",
            new DatasetCreateParameters(project.ProjectId, "Flight 2026-08-24", null));
        var version1 = scope.Catalog.CreateDatasetVersion(
            "recovery-dataset-version-v1",
            new DatasetVersionCreateParameters(dataset.DatasetId, null));

        scope.InsertRegisteredFileObject("source-image-v1", "source_image", Hex('a'), 100, "image/jpeg");
        scope.InsertRegisteredFileObject("source-image-v1-extra", "source_image", Hex('b'), 101, "image/jpeg");
        scope.InsertImageManifest(version1.DatasetVersionId, "image-v1", "source-image-v1");
        scope.InsertProcessingJobWithExecution(project.ProjectId, version1.DatasetVersionId, "v1");
        scope.InsertCandidateResult(project.ProjectId, version1.DatasetVersionId, "v1-result", "job-v1", "execution-v1");

        var sealedVersion = scope.Catalog.GetDatasetVersion(new DatasetVersionGetParameters(version1.DatasetVersionId));
        Assert.AreEqual("sealed", sealedVersion.LifecycleState);
        Assert.IsNotNull(sealedVersion.SealedAtUtc);

        AssertSqliteFailure("sealed dataset version is immutable", () =>
            scope.Execute(
                "UPDATE dataset_versions SET metadata_snapshot_json = '{\"changed\":true}' WHERE dataset_version_id = $id;",
                ("$id", version1.DatasetVersionId)));
        AssertSqliteFailure("sealed dataset image manifest is immutable", () =>
            scope.Execute(
                "UPDATE images SET camera_model = 'changed' WHERE image_id = 'image-v1';"));
        AssertSqliteFailure("sealed dataset image frames are immutable", () =>
            scope.Execute(
                "DELETE FROM image_frames WHERE image_frame_id = 'frame-image-v1';"));
        AssertSqliteFailure("sealed dataset image manifest is immutable", () =>
            scope.InsertImageManifest(version1.DatasetVersionId, "image-v1-extra", "source-image-v1-extra"));

        var version2 = scope.Catalog.CreateDatasetVersion(
            "recovery-dataset-version-v2",
            new DatasetVersionCreateParameters(dataset.DatasetId, version1.DatasetVersionId));
        Assert.AreEqual(2, version2.VersionNumber);
        Assert.AreEqual(version1.DatasetVersionId, version2.ParentVersionId);
        Assert.AreEqual("draft", version2.LifecycleState);

        var resultDatasetVersion = scope.Scalar<string>(
            "SELECT source_dataset_version_id FROM results WHERE result_id = 'result-v1-result';");
        Assert.AreEqual(version1.DatasetVersionId, resultDatasetVersion);
        AssertSqliteFailure("result lineage is immutable", () =>
            scope.Execute(
                "UPDATE results SET source_dataset_version_id = $version2 WHERE result_id = 'result-v1-result';",
                ("$version2", version2.DatasetVersionId)));
        Assert.AreEqual(
            version1.DatasetVersionId,
            scope.Scalar<string>("SELECT source_dataset_version_id FROM results WHERE result_id = 'result-v1-result';"));

        var otherDataset = scope.Catalog.CreateDataset(
            "recovery-other-dataset-create",
            new DatasetCreateParameters(project.ProjectId, "Other flight", null));
        var mismatch = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.CreateDatasetVersion(
                "recovery-cross-dataset-parent",
                new DatasetVersionCreateParameters(otherDataset.DatasetId, version1.DatasetVersionId)));
        Assert.AreEqual("parent_dataset_version_mismatch", mismatch.Code);
    }

    [TestMethod]
    public async Task PublishedObjectsAndResultsProtectReferencesAndHideTransientNamespaces()
    {
        await using var scope = await IntegrationScope.CreateWithArtifactServerAsync();
        var project = scope.Catalog.CreateProject(
            "published-project-create",
            new ProjectCreateParameters("Published project", null, null));
        var dataset = scope.Catalog.CreateDataset(
            "published-dataset-create",
            new DatasetCreateParameters(project.ProjectId, "Published flight", null));
        var version1 = scope.Catalog.CreateDatasetVersion(
            "published-dataset-version-v1",
            new DatasetVersionCreateParameters(dataset.DatasetId, null));
        scope.InsertProcessingJobWithExecution(project.ProjectId, version1.DatasetVersionId, "published");

        var formalObject = await scope.StagePublishAndRegisterFileObjectAsync(
            "file-formal-primary",
            "formal_output",
            "image/tiff",
            "formal DOM bytes"u8.ToArray());
        var reportObject = await scope.StagePublishAndRegisterFileObjectAsync(
            "file-final-report",
            "quality_report",
            "application/json",
            "{\"summary\":\"ok\"}"u8.ToArray());
        await scope.StageOnlyAsync("staging-only bytes"u8.ToArray());
        var quarantined = await scope.StageAndQuarantineAsync("quarantine-only bytes"u8.ToArray());

        scope.SeedPublishedResultWithDependencyAndFinalReport(
            project.ProjectId,
            version1.DatasetVersionId,
            formalObject,
            reportObject);

        var lineage = scope.Catalog.GetResultLineage(new ResultLineageParameters("result-target-published"));
        Assert.AreEqual("published", lineage.Target.LifecycleState);
        Assert.AreEqual(version1.DatasetVersionId, lineage.SourceDatasetVersion.DatasetVersionId);
        Assert.AreEqual("result-source-published", lineage.DirectDependencies.Single().DependsOnResultId);
        Assert.AreEqual(formalObject.ObjectKey, lineage.AvailableFiles.Single().ObjectKey);
        Assert.AreEqual("quality-report-target-final", lineage.FinalQualityReports.Single().QualityReportId);

        AssertSqliteFailure(null, () =>
            scope.Execute(
                "DELETE FROM dataset_versions WHERE dataset_version_id = $id;",
                ("$id", version1.DatasetVersionId)));
        AssertSqliteFailure("published result files are immutable", () =>
            scope.Execute(
                "DELETE FROM result_files WHERE result_file_id = 'result-file-target-primary';"));
        AssertSqliteFailure("published result files are immutable", () =>
            scope.Execute(
                "UPDATE result_files SET relative_path = 'changed.tif' WHERE result_file_id = 'result-file-target-primary';"));
        AssertSqliteFailure("published result dependencies are immutable", () =>
            scope.Execute(
                "DELETE FROM result_dependencies WHERE result_id = 'result-target-published';"));
        AssertSqliteFailure("published result dependencies are immutable", () =>
            scope.Execute(
                "INSERT INTO result_dependencies(result_id, depends_on_result_id, dependency_kind) VALUES('result-target-published', 'result-source-published', 'validated_against');"));
        AssertSqliteFailure("final quality report is immutable", () =>
            scope.Execute(
                "UPDATE quality_reports SET summary_json = '{\"changed\":true}' WHERE quality_report_id = 'quality-report-target-final';"));
        AssertSqliteFailure("final quality report cannot be deleted", () =>
            scope.Execute(
                "DELETE FROM quality_reports WHERE quality_report_id = 'quality-report-target-final';"));
        AssertSqliteFailure("final quality report findings are immutable", () =>
            scope.Execute(
                "INSERT INTO quality_findings(quality_finding_id, quality_report_id, sort_index, check_code, severity, conclusion) VALUES('quality-finding-late', 'quality-report-target-final', 1, 'late.mutation', 'info', 'passed');"));
        AssertSqliteFailure("available file object identity is immutable", () =>
            scope.Execute(
                "UPDATE file_objects SET content_hash = '0000000000000000000000000000000000000000000000000000000000000000', object_key = 'sha256/00/0000000000000000000000000000000000000000000000000000000000000000' WHERE file_object_id = 'file-formal-primary';"));
        AssertSqliteFailure(null, () =>
            scope.Execute(
                "DELETE FROM file_objects WHERE file_object_id = 'file-formal-primary';"));

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", scope.ArtifactSession.AccessToken);
        using var publishedResponse = await client.GetAsync($"{scope.ArtifactSession.BaseUrl}/artifacts/objects/{formalObject.ObjectKey}");
        Assert.AreEqual(HttpStatusCode.OK, publishedResponse.StatusCode);
        CollectionAssert.AreEqual("formal DOM bytes"u8.ToArray(), await publishedResponse.Content.ReadAsByteArrayAsync());

        using var stagingResponse = await client.GetAsync($"{scope.ArtifactSession.BaseUrl}/artifacts/objects/../staging/{scope.StagingOnly.StageId}/payload");
        Assert.AreNotEqual(HttpStatusCode.OK, stagingResponse.StatusCode);
        using var stagingAliasResponse = await client.GetAsync($"{scope.ArtifactSession.BaseUrl}/artifacts/objects/staging/{scope.StagingOnly.StageId}/payload");
        Assert.AreNotEqual(HttpStatusCode.OK, stagingAliasResponse.StatusCode);
        using var quarantineResponse = await client.GetAsync($"{scope.ArtifactSession.BaseUrl}/artifacts/objects/../quarantine/{quarantined.QuarantineId}/payload");
        Assert.AreNotEqual(HttpStatusCode.OK, quarantineResponse.StatusCode);
    }

    private static void AssertSqliteFailure(string? expectedMessageFragment, Action action)
    {
        var exception = Assert.Throws<SqliteException>(action);
        if (expectedMessageFragment is not null)
        {
            StringAssert.Contains(exception.Message, expectedMessageFragment);
        }
    }

    private static string Hex(char value) => new(value, 64);

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ObjectKeyFor(string sha256) => $"sha256/{sha256[..2]}/{sha256}";

    private sealed class IntegrationScope : IAsyncDisposable, IDisposable
    {
        private readonly string _root;
        private ArtifactServer? _artifactServer;

        private IntegrationScope(string root, BusinessDatabase database, BusinessCatalog catalog, ContentAddressedObjectStore store)
        {
            _root = root;
            Database = database;
            Catalog = catalog;
            Store = store;
        }

        private BusinessDatabase Database { get; }

        public BusinessCatalog Catalog { get; }

        public ContentAddressedObjectStore Store { get; }

        public ArtifactSession ArtifactSession { get; private set; } = new(string.Empty, string.Empty);

        public ObjectStageReceipt StagingOnly { get; private set; } = new(string.Empty, string.Empty, 0, DateTimeOffset.UnixEpoch);

        public static IntegrationScope Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"qiongtu-data-foundation-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = ControlDataPaths.Create(root);
            var database = new BusinessDatabase(paths.BusinessDatabase);
            database.Initialize();
            var catalog = new BusinessCatalog(database);
            var store = new ContentAddressedObjectStore(paths.ObjectDirectory);
            return new IntegrationScope(root, database, catalog, store);
        }

        public static async Task<IntegrationScope> CreateWithArtifactServerAsync()
        {
            var scope = Create();
            var roots = new ArtifactRootRegistry();
            roots.RegisterTrustedRoot("objects", scope.Store.PublishedDirectory);
            scope._artifactServer = new ArtifactServer(roots);
            await scope._artifactServer.StartAsync(CancellationToken.None);
            scope.ArtifactSession = scope._artifactServer.CreateSession();
            return scope;
        }

        public async Task<PublishedObject> StagePublishAndRegisterFileObjectAsync(
            string fileObjectId,
            string objectKind,
            string mediaType,
            byte[] bytes)
        {
            var published = await Store.PublishAsync(await Store.StageAsync(new MemoryStream(bytes)));
            Assert.AreEqual(Sha256Hex(bytes), published.Sha256);
            Assert.AreEqual(ObjectKeyFor(published.Sha256), published.ObjectKey);
            InsertAvailableFileObject(fileObjectId, objectKind, published.Sha256, published.ByteLength, mediaType, published.ObjectKey);
            return published;
        }

        public async Task<ObjectStageReceipt> StageOnlyAsync(byte[] bytes)
        {
            StagingOnly = await Store.StageAsync(new MemoryStream(bytes));
            return StagingOnly;
        }

        public async Task<QuarantinedObject> StageAndQuarantineAsync(byte[] bytes) =>
            await Store.AbandonAsync(await Store.StageAsync(new MemoryStream(bytes)));

        public void InsertRegisteredFileObject(
            string fileObjectId,
            string objectKind,
            string contentHash,
            long byteLength,
            string mediaType)
        {
            Execute(
                """
                INSERT INTO file_objects(file_object_id, object_kind, hash_algorithm, content_hash, byte_length, media_type, object_key, storage_state, original_display_name, created_at_utc, available_at_utc)
                VALUES($file_object_id, $object_kind, 'sha256', $content_hash, $byte_length, $media_type, NULL, 'registered', NULL, $created_at_utc, NULL);
                """,
                ("$file_object_id", fileObjectId),
                ("$object_kind", objectKind),
                ("$content_hash", contentHash),
                ("$byte_length", byteLength),
                ("$media_type", mediaType),
                ("$created_at_utc", Now("00")));
        }

        public void InsertAvailableFileObject(
            string fileObjectId,
            string objectKind,
            string contentHash,
            long byteLength,
            string mediaType,
            string objectKey)
        {
            Execute(
                """
                INSERT INTO file_objects(file_object_id, object_kind, hash_algorithm, content_hash, byte_length, media_type, object_key, storage_state, original_display_name, created_at_utc, available_at_utc)
                VALUES($file_object_id, $object_kind, 'sha256', $content_hash, $byte_length, $media_type, $object_key, 'available', NULL, $created_at_utc, $available_at_utc);
                """,
                ("$file_object_id", fileObjectId),
                ("$object_kind", objectKind),
                ("$content_hash", contentHash),
                ("$byte_length", byteLength),
                ("$media_type", mediaType),
                ("$object_key", objectKey),
                ("$created_at_utc", Now("01")),
                ("$available_at_utc", Now("01")));
        }

        public void InsertImageManifest(string datasetVersionId, string imageId, string sourceFileObjectId)
        {
            Execute(
                """
                INSERT INTO images(
                    image_id, dataset_version_id, source_file_object_id, normalized_file_object_id,
                    import_source_key, sort_index, content_container, primary_frame_index, width, height,
                    capture_time_utc, manufacturer, camera_model, lens_model, image_state, metadata_state,
                    duplicate_of_image_id, raw_metadata_json, created_at_utc)
                VALUES(
                    $image_id, $dataset_version_id, $source_file_object_id, NULL,
                    $import_source_key, $sort_index, 'jpeg', 0, 4000, 3000,
                    $capture_time_utc, 'DJI', 'FC-test', NULL, 'processing_input', 'parsed',
                    NULL, '{"manufacturer":"DJI"}', $created_at_utc);
                """,
                ("$image_id", imageId),
                ("$dataset_version_id", datasetVersionId),
                ("$source_file_object_id", sourceFileObjectId),
                ("$import_source_key", $"{imageId}.JPG"),
                ("$sort_index", imageId.EndsWith("extra", StringComparison.Ordinal) ? 1 : 0),
                ("$capture_time_utc", Now("02")),
                ("$created_at_utc", Now("02")));
            Execute(
                """
                INSERT INTO image_frames(image_frame_id, image_id, frame_index, frame_role, width, height, decode_state, normalized_file_object_id, metadata_json)
                VALUES($frame_id, $image_id, 0, 'primary_photogrammetry', 4000, 3000, 'decoded', NULL, '{"role":"primary"}');
                """,
                ("$frame_id", $"frame-{imageId}"),
                ("$image_id", imageId));
            Execute(
                """
                INSERT INTO image_metadata_fields(image_metadata_field_id, image_id, field_name, field_value_json, source_kind, field_state, source_detail)
                VALUES($metadata_id, $image_id, 'manufacturer', '"DJI"', 'exif', 'present', 'EXIF Make');
                """,
                ("$metadata_id", $"metadata-{imageId}"),
                ("$image_id", imageId));
        }

        public void InsertProcessingJobWithExecution(string projectId, string datasetVersionId, string suffix)
        {
            Execute(
                """
                INSERT INTO processing_jobs(
                    processing_job_id, project_id, dataset_version_id, job_type, requested_outputs_json,
                    parameter_profile, parameter_schema_version, parameters_json, parameter_sha256,
                    lifecycle_state, recovery_state, priority, created_at_utc, submitted_at_utc, started_at_utc, ended_at_utc)
                VALUES(
                    $job_id, $project_id, $dataset_version_id, 'photogrammetry', '["dom"]',
                    'standard', 'v1', '{}', $parameter_sha256,
                    'succeeded', 'not_applicable', 0, $created_at_utc, $submitted_at_utc, $started_at_utc, $ended_at_utc);
                """,
                ("$job_id", $"job-{suffix}"),
                ("$project_id", projectId),
                ("$dataset_version_id", datasetVersionId),
                ("$parameter_sha256", Hex('c')),
                ("$created_at_utc", Now("03")),
                ("$submitted_at_utc", Now("04")),
                ("$started_at_utc", Now("05")),
                ("$ended_at_utc", Now("06")));
            Execute(
                """
                INSERT INTO job_executions(
                    job_execution_id, processing_job_id, attempt_number, execution_mode, worker_type, worker_version,
                    engine_name, engine_version, parameter_sha256, lifecycle_state, checkpoint_compatibility_state,
                    started_at_utc, ended_at_utc)
                VALUES(
                    $execution_id, $job_id, 1, 'full', 'photogrammetry', 'worker-v1',
                    'integration-engine', '1.0', $parameter_sha256, 'succeeded', 'unavailable',
                    $started_at_utc, $ended_at_utc);
                """,
                ("$execution_id", $"execution-{suffix}"),
                ("$job_id", $"job-{suffix}"),
                ("$parameter_sha256", Hex('c')),
                ("$started_at_utc", Now("05")),
                ("$ended_at_utc", Now("06")));
        }

        public void InsertCandidateResult(
            string projectId,
            string datasetVersionId,
            string suffix,
            string processingJobId,
            string jobExecutionId)
        {
            Execute(
                """
                INSERT INTO result_series(result_series_id, project_id, dataset_version_id, series_kind, name, parent_series_id, created_at_utc)
                VALUES($series_id, $project_id, $dataset_version_id, 'dom', $name, NULL, $created_at_utc);
                """,
                ("$series_id", $"series-{suffix}"),
                ("$project_id", projectId),
                ("$dataset_version_id", datasetVersionId),
                ("$name", $"DOM {suffix}"),
                ("$created_at_utc", Now("07")));
            Execute(
                """
                INSERT INTO results(
                    result_id, result_series_id, version_number, source_dataset_version_id, source_processing_job_id,
                    source_job_execution_id, source_result_id, result_kind, lifecycle_state, crs_id, vertical_reference,
                    local_origin_json, axis_convention, unit, bounds_json, resolution_or_density_json, engine_version,
                    converter_version, parameter_sha256, accuracy_level, created_at_utc, published_at_utc, superseded_by_result_id)
                VALUES(
                    $result_id, $series_id, 1, $dataset_version_id, $job_id,
                    $execution_id, NULL, 'dom', 'candidate', NULL, 'unknown',
                    NULL, 'east-north-up', 'metre', '{"westLongitude":114.1,"southLatitude":29.7,"eastLongitude":114.2,"northLatitude":29.8}', NULL, 'engine-1.0',
                    NULL, $parameter_sha256, 'georeferenced_visualization', $created_at_utc, NULL, NULL);
                """,
                ("$result_id", $"result-{suffix}"),
                ("$series_id", $"series-{suffix}"),
                ("$dataset_version_id", datasetVersionId),
                ("$job_id", processingJobId),
                ("$execution_id", jobExecutionId),
                ("$parameter_sha256", Hex('c')),
                ("$created_at_utc", Now("08")));
        }

        public void SeedPublishedResultWithDependencyAndFinalReport(
            string projectId,
            string datasetVersionId,
            PublishedObject formalObject,
            PublishedObject reportObject)
        {
            InsertResultSeries(projectId, datasetVersionId, "series-source-published", "aerotriangulation", "AT", null, "07");
            InsertResultSeries(projectId, datasetVersionId, "series-target-published", "dom", "DOM", "series-source-published", "08");
            InsertResult("result-source-published", "series-source-published", datasetVersionId, "aerotriangulation", null, "candidate", "09");
            InsertResult("result-target-published", "series-target-published", datasetVersionId, "dom", "result-source-published", "candidate", "10");
            Execute(
                "INSERT INTO result_dependencies(result_id, depends_on_result_id, dependency_kind) VALUES('result-target-published', 'result-source-published', 'derived_from');");
            Execute(
                """
                INSERT INTO result_files(result_file_id, result_id, file_object_id, file_role, relative_path, is_required, byte_length_snapshot, content_hash_snapshot)
                VALUES('result-file-target-primary', 'result-target-published', 'file-formal-primary', 'primary', 'dom.tif', 1, $byte_length, $content_hash);
                """,
                ("$byte_length", formalObject.ByteLength),
                ("$content_hash", formalObject.Sha256));
            Execute(
                """
                INSERT INTO quality_reports(
                    quality_report_id, report_type, version_number, lifecycle_state, dataset_version_id, processing_job_id,
                    job_execution_id, result_id, created_by_execution_id, report_file_object_id, schema_version,
                    summary_severity, summary_json, created_at_utc, finalized_at_utc)
                VALUES(
                    'quality-report-target-final', 'result_validation', 1, 'draft', NULL, NULL,
                    NULL, 'result-target-published', 'execution-published', 'file-final-report', 'v1',
                    'none', '{"blocking":0,"warning":0,"info":1}', $created_at_utc, NULL);
                """,
                ("$created_at_utc", Now("11")));
            Execute(
                """
                INSERT INTO quality_findings(quality_finding_id, quality_report_id, sort_index, check_code, severity, conclusion)
                VALUES('quality-finding-target-final', 'quality-report-target-final', 0, 'result.readable', 'info', 'passed');
                """);
            Execute(
                """
                UPDATE quality_reports
                SET lifecycle_state = 'final', finalized_at_utc = $finalized_at_utc
                WHERE quality_report_id = 'quality-report-target-final';
                """,
                ("$finalized_at_utc", Now("12")));
            Execute(
                """
                UPDATE results
                SET lifecycle_state = 'published', published_at_utc = $published_at_utc
                WHERE result_id = 'result-target-published';
                """,
                ("$published_at_utc", Now("13")));

            _ = reportObject;
        }

        public int Execute(string sql, params (string Name, object? Value)[] parameters)
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            }

            return command.ExecuteNonQuery();
        }

        public T Scalar<T>(string sql)
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async ValueTask DisposeAsync()
        {
            if (_artifactServer is not null)
            {
                await _artifactServer.DisposeAsync();
            }

            Dispose();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private void InsertResultSeries(
            string projectId,
            string datasetVersionId,
            string seriesId,
            string seriesKind,
            string name,
            string? parentSeriesId,
            string second)
        {
            Execute(
                """
                INSERT INTO result_series(result_series_id, project_id, dataset_version_id, series_kind, name, parent_series_id, created_at_utc)
                VALUES($series_id, $project_id, $dataset_version_id, $series_kind, $name, $parent_series_id, $created_at_utc);
                """,
                ("$series_id", seriesId),
                ("$project_id", projectId),
                ("$dataset_version_id", datasetVersionId),
                ("$series_kind", seriesKind),
                ("$name", name),
                ("$parent_series_id", parentSeriesId),
                ("$created_at_utc", Now(second)));
        }

        private void InsertResult(
            string resultId,
            string seriesId,
            string datasetVersionId,
            string resultKind,
            string? sourceResultId,
            string lifecycleState,
            string second)
        {
            Execute(
                """
                INSERT INTO results(
                    result_id, result_series_id, version_number, source_dataset_version_id, source_processing_job_id,
                    source_job_execution_id, source_result_id, result_kind, lifecycle_state, crs_id, vertical_reference,
                    local_origin_json, axis_convention, unit, bounds_json, resolution_or_density_json, engine_version,
                    converter_version, parameter_sha256, accuracy_level, created_at_utc, published_at_utc, superseded_by_result_id)
                VALUES(
                    $result_id, $series_id, 1, $dataset_version_id, 'job-published',
                    'execution-published', $source_result_id, $result_kind, $lifecycle_state, NULL, 'unknown',
                    NULL, 'east-north-up', 'metre', '{"westLongitude":114.1,"southLatitude":29.7,"eastLongitude":114.2,"northLatitude":29.8}', NULL, 'engine-1.0',
                    NULL, $parameter_sha256, 'georeferenced_visualization', $created_at_utc, NULL, NULL);
                """,
                ("$result_id", resultId),
                ("$series_id", seriesId),
                ("$dataset_version_id", datasetVersionId),
                ("$source_result_id", sourceResultId),
                ("$result_kind", resultKind),
                ("$lifecycle_state", lifecycleState),
                ("$parameter_sha256", Hex('c')),
                ("$created_at_utc", Now(second)));
        }

        private static string Now(string second) => $"2026-08-24T00:00:{second}Z";
    }
}
