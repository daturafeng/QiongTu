using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class BusinessCatalogControlIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task ProjectCreateRetryGetListAndIdempotencyConflictUseRealPipe()
    {
        await using var scope = await PipeCatalogScope.StartAsync();
        var requestId = "project-create-retry";
        var createParams = new { name = " Mapping Project ", description = "  retry-safe creation  ", defaultCrs = (object?)null };

        using var first = await scope.SendAsync(ControlMethods.ProjectCreate, requestId, createParams);
        using var retry = await scope.SendAsync(ControlMethods.ProjectCreate, requestId, createParams);
        var created = Ok(first).GetProperty("result");
        var replayed = Ok(retry).GetProperty("result");
        var projectId = created.GetProperty("projectId").GetString();

        Assert.IsFalse(string.IsNullOrWhiteSpace(projectId));
        Assert.AreEqual(projectId, replayed.GetProperty("projectId").GetString());
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM projects;"));

        using var get = await scope.SendAsync(ControlMethods.ProjectGet, "project-get-created", new { projectId });
        Assert.AreEqual(projectId, Ok(get).GetProperty("result").GetProperty("projectId").GetString());

        using var list = await scope.SendAsync(ControlMethods.ProjectList, "project-list-created", new { pageSize = 10, cursor = (string?)null });
        var items = Ok(list).GetProperty("result").GetProperty("items");
        Assert.AreEqual(1, items.GetArrayLength());
        Assert.AreEqual(projectId, items[0].GetProperty("projectId").GetString());

        using var conflict = await scope.SendAsync(
            ControlMethods.ProjectCreate,
            requestId,
            new { name = "Different Project", description = "retry-safe creation", defaultCrs = (object?)null });
        Error(conflict, "idempotency_conflict");
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM projects;"));
    }

    [TestMethod]
    public async Task CrsRecommendCoversAvailableAndNullBoundsUnavailable()
    {
        await using var scope = await PipeCatalogScope.StartAsync();

        using var availableResponse = await scope.SendAsync(
            ControlMethods.CrsRecommend,
            "crs-recommend-available",
            new
            {
                bounds = new
                {
                    westLongitude = 114.10,
                    southLatitude = 29.70,
                    eastLongitude = 114.20,
                    northLatitude = 29.80
                }
            });
        var available = Ok(availableResponse).GetProperty("result");
        AssertRecommendationAvailable(available);
        Assert.AreEqual("EPSG", available.GetProperty("suggestedCrs").GetProperty("authority").GetString());
        Assert.AreEqual("32650", available.GetProperty("suggestedCrs").GetProperty("code").GetString());
        Assert.AreEqual("unknown", available.GetProperty("suggestedCrs").GetProperty("verticalReference").GetString());

        using var unavailableResponse = await scope.SendAsync(
            ControlMethods.CrsRecommend,
            "crs-recommend-null-bounds",
            new { bounds = (object?)null });
        var unavailable = Ok(unavailableResponse).GetProperty("result");
        AssertRecommendationUnavailable(unavailable);
        Assert.AreEqual(JsonValueKind.Null, unavailable.GetProperty("suggestedCrs").ValueKind);
    }

    [TestMethod]
    public async Task ProjectConfirmCrsUsesPipeIdempotencyAndOptimisticConcurrency()
    {
        await using var scope = await PipeCatalogScope.StartAsync();
        using var createdResponse = await scope.SendAsync(
            ControlMethods.ProjectCreate,
            "project-confirm-create",
            new { name = "CRS Project", description = (string?)null, defaultCrs = (object?)null });
        var created = Ok(createdResponse).GetProperty("result");
        var projectId = created.GetProperty("projectId").GetString();
        var originalUpdatedAtUtc = created.GetProperty("updatedAtUtc").GetString();
        var parameters = new
        {
            projectId,
            expectedUpdatedAtUtc = originalUpdatedAtUtc,
            crs = new
            {
                authority = "EPSG",
                code = "32650",
                name = "WGS 84 / UTM zone 50N",
                wkt = (string?)null,
                projjson = (string?)null,
                crsType = "projected",
                horizontalUnit = "metre",
                verticalReference = "unknown",
                axisOrder = "east-north"
            }
        };

        using var confirmedResponse = await scope.SendAsync(
            ControlMethods.ProjectConfirmCrs,
            "project-confirm-crs",
            parameters);
        using var replayedResponse = await scope.SendAsync(
            ControlMethods.ProjectConfirmCrs,
            "project-confirm-crs",
            parameters);
        var confirmed = Ok(confirmedResponse).GetProperty("result");
        var replayed = Ok(replayedResponse).GetProperty("result");

        Assert.AreEqual("confirmed", confirmed.GetProperty("spatialConfigurationStatus").GetString());
        Assert.AreEqual("32650", confirmed.GetProperty("defaultCrs").GetProperty("code").GetString());
        Assert.AreEqual(JsonValueKind.String, confirmed.GetProperty("defaultCrs").GetProperty("capturedAtUtc").ValueKind);
        Assert.AreEqual(confirmed.GetProperty("updatedAtUtc").GetString(), replayed.GetProperty("updatedAtUtc").GetString());

        using var staleResponse = await scope.SendAsync(
            ControlMethods.ProjectConfirmCrs,
            "project-confirm-stale",
            parameters);
        Error(staleResponse, "project_concurrency_conflict");
    }

    [TestMethod]
    public async Task DatasetVersionLifecycleAndErrorCodesUseRealPipe()
    {
        await using var scope = await PipeCatalogScope.StartAsync();
        var projectId = await scope.CreateProjectAsync("dataset-project", "Dataset Project");
        var datasetId = await scope.CreateDatasetAsync(projectId, "Flight A");

        var version1Id = await scope.CreateDatasetVersionAsync(datasetId, "dataset-version-create-1", null);
        using var version2Response = await scope.SendAsync(
            ControlMethods.DatasetVersionCreate,
            "dataset-version-create-2",
            new { datasetId, parentVersionId = version1Id });
        var version2 = Ok(version2Response).GetProperty("result");
        var version2Id = version2.GetProperty("datasetVersionId").GetString();

        Assert.AreEqual(2, version2.GetProperty("versionNumber").GetInt32());
        Assert.AreEqual(version1Id, version2.GetProperty("parentVersionId").GetString());

        using var list = await scope.SendAsync(ControlMethods.DatasetVersionList, "dataset-version-list", new { datasetId, pageSize = 10, cursor = (string?)null });
        var items = Ok(list).GetProperty("result").GetProperty("items");
        CollectionAssert.AreEquivalent(
            new[] { version1Id, version2Id },
            items.EnumerateArray().Select(item => item.GetProperty("datasetVersionId").GetString()).ToArray());

        using var get = await scope.SendAsync(ControlMethods.DatasetVersionGet, "dataset-version-get", new { datasetVersionId = version2Id });
        Assert.AreEqual(version2Id, Ok(get).GetProperty("result").GetProperty("datasetVersionId").GetString());

        using var invalidCursor = await scope.SendAsync(ControlMethods.DatasetVersionList, "dataset-version-invalid-cursor", new { datasetId, pageSize = 10, cursor = "not-a-valid-cursor" });
        Error(invalidCursor, "invalid_cursor");

        using var invalidPageSize = await scope.SendAsync(ControlMethods.DatasetVersionList, "dataset-version-invalid-page-size", new { datasetId, pageSize = 0, cursor = (string?)null });
        Error(invalidPageSize, "invalid_page_size");
    }

    [TestMethod]
    public async Task CursorFromDifferentListOrFilterIsRejected()
    {
        await using var scope = await PipeCatalogScope.StartAsync();
        var projectId = await scope.CreateProjectAsync("cursor-project", "Cursor Project");
        await scope.CreateProjectAsync("cursor-other-project", "Other Project");
        var datasetId = await scope.CreateDatasetAsync(projectId, "Cursor Flight");
        await scope.CreateDatasetVersionAsync(datasetId, "cursor-version-1", null);
        await scope.CreateDatasetVersionAsync(datasetId, "cursor-version-2", null);

        using var projectPage = await scope.SendAsync(ControlMethods.ProjectList, "project-list-cursor-source", new { pageSize = 1, cursor = (string?)null });
        var cursor = Ok(projectPage).GetProperty("result").GetProperty("nextCursor").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(cursor));

        using var rejected = await scope.SendAsync(ControlMethods.DatasetVersionList, "dataset-list-cross-cursor", new { datasetId, pageSize = 1, cursor });
        Error(rejected, "invalid_cursor");
    }

    [TestMethod]
    public async Task ResultLineageListGetNotFoundAndSanitizedResponseUseSeededAuthorityDb()
    {
        await using var scope = await PipeCatalogScope.StartAsync();
        scope.SeedPublishedResultGraph();

        using var listByProject = await scope.SendAsync(ControlMethods.ResultList, "result-list-project", new { projectId = "project-lineage", datasetVersionId = (string?)null, pageSize = 10, cursor = (string?)null });
        AssertContainsResult(Ok(listByProject).GetProperty("result").GetProperty("items"), "result-target");

        using var listByDataset = await scope.SendAsync(ControlMethods.ResultList, "result-list-dataset", new { projectId = (string?)null, datasetVersionId = "dataset-version-lineage", pageSize = 10, cursor = (string?)null });
        AssertContainsResult(Ok(listByDataset).GetProperty("result").GetProperty("items"), "result-target");

        using var lineageResponse = await scope.SendAsync(ControlMethods.ResultLineage, "result-lineage-target", new { resultId = "result-target" });
        var lineage = Ok(lineageResponse).GetProperty("result");
        Assert.AreEqual("result-target", lineage.GetProperty("target").GetProperty("resultId").GetString());
        Assert.AreEqual("series-target", lineage.GetProperty("series").GetProperty("resultSeriesId").GetString());
        Assert.AreEqual("project-lineage", lineage.GetProperty("project").GetProperty("projectId").GetString());
        Assert.AreEqual("dataset-lineage", lineage.GetProperty("sourceDatasetVersion").GetProperty("datasetId").GetString());
        Assert.AreEqual("job-lineage", lineage.GetProperty("sourceProcessingJob").GetProperty("processingJobId").GetString());
        Assert.AreEqual("execution-lineage", lineage.GetProperty("sourceJobExecution").GetProperty("jobExecutionId").GetString());
        Assert.IsTrue(lineage.GetProperty("directDependencies").EnumerateArray().Any(item => item.GetProperty("dependsOnResultId").GetString() == "result-source"));
        Assert.AreEqual(1, lineage.GetProperty("availableFiles").GetArrayLength());
        Assert.AreEqual("sha256/bb/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", lineage.GetProperty("availableFiles")[0].GetProperty("objectKey").GetString());
        Assert.AreEqual("quality-report-final", lineage.GetProperty("finalQualityReports")[0].GetProperty("qualityReportId").GetString());

        var raw = lineageResponse.RootElement.GetRawText();
        Assert.DoesNotContain(":\\", raw);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("staging", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quarantine", raw, StringComparison.OrdinalIgnoreCase);

        using var notFound = await scope.SendAsync(ControlMethods.ResultLineage, "result-lineage-not-found", new { resultId = "missing-result" });
        Error(notFound, "result_not_found");
    }

    [TestMethod]
    public async Task CatalogResponseLimitRollsBackMutationAndPipeReturnsBoundedError()
    {
        await using var scope = await PipeCatalogScope.StartAsync(maximumCatalogResponseBytes: 256);
        using var response = await scope.SendAsync(
            ControlMethods.ProjectCreate,
            "bounded-project-create",
            new
            {
                name = "Bounded Project",
                description = new string('x', 500),
                defaultCrs = (object?)null
            });

        Error(response, "response_too_large");
        Assert.IsLessThanOrEqualTo(
            NamedPipeControlServer.MaximumResponseBytes,
            Encoding.UTF8.GetByteCount(response.RootElement.GetRawText()));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM projects;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM catalog_mutations;"));
    }

    [TestMethod]
    public async Task PipeRejectsControlCharactersInRequestId()
    {
        await using var scope = await PipeCatalogScope.StartAsync();
        using var response = await scope.SendAsync(
            ControlMethods.ProjectList,
            "bad\nrequest",
            new { pageSize = 10, cursor = (string?)null });

        Error(response, "invalid_request_id");
    }

    private static JsonElement Ok(JsonDocument document)
    {
        Assert.IsTrue(document.RootElement.GetProperty("ok").GetBoolean(), document.RootElement.GetRawText());
        return document.RootElement;
    }

    private static void Error(JsonDocument document, string expectedCode)
    {
        Assert.IsFalse(document.RootElement.GetProperty("ok").GetBoolean(), document.RootElement.GetRawText());
        Assert.AreEqual(expectedCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static void AssertRecommendationAvailable(JsonElement recommendation)
    {
        Assert.AreEqual("recommended", recommendation.GetProperty("status").GetString());
        Assert.AreEqual("single_wgs84_utm_zone", recommendation.GetProperty("reasonCode").GetString());
    }

    private static void AssertRecommendationUnavailable(JsonElement recommendation)
    {
        Assert.AreEqual("not-recommended", recommendation.GetProperty("status").GetString());
    }

    private static void AssertContainsResult(JsonElement items, string resultId) =>
        Assert.IsTrue(items.EnumerateArray().Any(item => item.GetProperty("resultId").GetString() == resultId), items.GetRawText());

    private sealed class PipeCatalogScope : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WorkerSupervisor _workers;
        private readonly ArtifactServer _artifactServer;
        private readonly NamedPipeControlServer _server;
        private readonly BusinessDatabase _database;

        private PipeCatalogScope(string root, string pipeName, BusinessDatabase database, WorkerSupervisor workers, ArtifactServer artifactServer, NamedPipeControlServer server)
        {
            _root = root;
            PipeName = pipeName;
            _database = database;
            _workers = workers;
            _artifactServer = artifactServer;
            _server = server;
        }

        private string PipeName { get; }

        public static async Task<PipeCatalogScope> StartAsync(int? maximumCatalogResponseBytes = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"qiongtu-catalog-pipe-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = ControlDataPaths.Create(root);
            var runtimeStore = new WorkerRuntimeStore(paths.RuntimeDatabase);
            runtimeStore.Initialize();
            var database = new BusinessDatabase(paths.BusinessDatabase);
            database.Initialize();
            var workers = new WorkerSupervisor(new WorkerRegistry(), runtimeStore, paths.LogDirectory);
            var roots = new ArtifactRootRegistry();
            roots.RegisterTrustedRoot("objects", paths.ObjectDirectory);
            var artifactServer = new ArtifactServer(roots);
            await artifactServer.StartAsync(CancellationToken.None);
            var pipeName = RuntimeDiscovery.CreatePipeName();
            var catalog = maximumCatalogResponseBytes is null
                ? new BusinessCatalog(database)
                : new BusinessCatalog(database, maximumCatalogResponseBytes.Value);
            var capabilities = new ProcessingCapabilityService(new WorkerRegistry(), paths);
            var dispatcher = new ControlRequestDispatcher(
                pipeName,
                DateTimeOffset.UtcNow,
                artifactServer,
                workers,
                catalog,
                capabilities,
                requestStop: () => { });
            var server = new NamedPipeControlServer(pipeName, dispatcher);
            server.Start();
            return new PipeCatalogScope(root, pipeName, database, workers, artifactServer, server);
        }

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            await _artifactServer.DisposeAsync();
            _workers.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }

        public async Task<JsonDocument> SendAsync(string method, string requestId, object? parameters)
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5_000);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { apiVersion = ContractVersions.ControlApiV1, requestId, method, parameters }, JsonOptions));
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(line);
            return JsonDocument.Parse(line);
        }

        public async Task<string> CreateProjectAsync(string requestId, string name)
        {
            using var response = await SendAsync(ControlMethods.ProjectCreate, requestId, new { name, description = (string?)null, defaultCrs = (object?)null });
            return Ok(response).GetProperty("result").GetProperty("projectId").GetString() ?? throw new AssertFailedException("project.create did not return projectId.");
        }

        public async Task<string> CreateDatasetAsync(string projectId, string name)
        {
            using var response = await SendAsync(ControlMethods.DatasetCreate, $"dataset-create-{Guid.NewGuid():N}", new { projectId, name, description = (string?)null });
            return Ok(response).GetProperty("result").GetProperty("datasetId").GetString() ?? throw new AssertFailedException("dataset.create did not return datasetId.");
        }

        public async Task<string> CreateDatasetVersionAsync(string datasetId, string requestId, string? parentVersionId)
        {
            using var response = await SendAsync(ControlMethods.DatasetVersionCreate, requestId, new { datasetId, parentVersionId });
            return Ok(response).GetProperty("result").GetProperty("datasetVersionId").GetString() ?? throw new AssertFailedException("dataset-version.create did not return datasetVersionId.");
        }

        public T Scalar<T>(string sql)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        public void SeedPublishedResultGraph()
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = PublishedResultGraphSql;
            command.ExecuteNonQuery();
        }
    }

    private const string PublishedResultGraphSql =
        """
        INSERT OR IGNORE INTO crs_definitions(crs_id,authority,code,name,horizontal_unit,vertical_reference,axis_order,crs_type,captured_at_utc,created_at_utc)
        VALUES('crs-lineage','EPSG','32648','WGS 84 / UTM zone 48N','metre','unknown','east-north','projected','2026-08-23T00:00:00Z','2026-08-23T00:00:00Z');
        INSERT OR IGNORE INTO projects(project_id,name,default_crs_id,spatial_configuration_state,lifecycle_state,created_at_utc,updated_at_utc)
        VALUES('project-lineage','Lineage Project','crs-lineage','confirmed','active','2026-08-23T00:00:00Z','2026-08-23T00:00:00Z');
        INSERT OR IGNORE INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
        VALUES('dataset-lineage','project-lineage','Lineage Dataset','active','2026-08-23T00:00:01Z','2026-08-23T00:00:01Z');
        INSERT OR IGNORE INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc)
        VALUES('dataset-version-lineage','dataset-lineage',1,'draft','dji_supported','passed','2026-08-23T00:00:02Z');
        INSERT OR IGNORE INTO processing_jobs(processing_job_id,project_id,dataset_version_id,job_type,requested_outputs_json,parameter_profile,parameter_schema_version,parameters_json,parameter_sha256,lifecycle_state,recovery_state,created_at_utc,submitted_at_utc,started_at_utc,ended_at_utc)
        VALUES('job-lineage','project-lineage','dataset-version-lineage','photogrammetry','["dom"]','standard','v1','{}','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','succeeded','not_applicable','2026-08-23T00:00:03Z','2026-08-23T00:00:03Z','2026-08-23T00:00:04Z','2026-08-23T00:10:00Z');
        INSERT OR IGNORE INTO job_executions(job_execution_id,processing_job_id,attempt_number,execution_mode,worker_type,worker_version,engine_name,engine_version,parameter_sha256,lifecycle_state,checkpoint_compatibility_state,started_at_utc,ended_at_utc)
        VALUES('execution-lineage','job-lineage',1,'full','photogrammetry','v1','engine','1.0','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','succeeded','unavailable','2026-08-23T00:00:04Z','2026-08-23T00:10:00Z');
        INSERT OR IGNORE INTO result_series(result_series_id,project_id,dataset_version_id,series_kind,name,created_at_utc)
        VALUES('series-source','project-lineage','dataset-version-lineage','aerotriangulation','AT','2026-08-23T00:10:00Z');
        INSERT OR IGNORE INTO result_series(result_series_id,project_id,dataset_version_id,series_kind,name,parent_series_id,created_at_utc)
        VALUES('series-target','project-lineage','dataset-version-lineage','dom','DOM','series-source','2026-08-23T00:11:00Z');
        INSERT OR IGNORE INTO results(result_id,result_series_id,version_number,source_dataset_version_id,source_processing_job_id,source_job_execution_id,result_kind,lifecycle_state,crs_id,unit,bounds_json,parameter_sha256,accuracy_level,created_at_utc)
        VALUES('result-source','series-source',1,'dataset-version-lineage','job-lineage','execution-lineage','aerotriangulation','candidate','crs-lineage','metre','{"westLongitude":114.1,"southLatitude":29.7,"eastLongitude":114.2,"northLatitude":29.8}','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','georeferenced_visualization','2026-08-23T00:10:01Z');
        INSERT OR IGNORE INTO results(result_id,result_series_id,version_number,source_dataset_version_id,source_processing_job_id,source_job_execution_id,source_result_id,result_kind,lifecycle_state,crs_id,unit,bounds_json,parameter_sha256,accuracy_level,created_at_utc)
        VALUES('result-target','series-target',1,'dataset-version-lineage','job-lineage','execution-lineage','result-source','dom','candidate','crs-lineage','metre','{"westLongitude":114.1,"southLatitude":29.7,"eastLongitude":114.2,"northLatitude":29.8}','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','georeferenced_visualization','2026-08-23T00:11:01Z');
        INSERT OR IGNORE INTO result_dependencies(result_id,depends_on_result_id,dependency_kind)
        VALUES('result-target','result-source','derived_from');
        INSERT OR IGNORE INTO file_objects(file_object_id,object_kind,hash_algorithm,content_hash,byte_length,media_type,object_key,storage_state,created_at_utc,available_at_utc)
        VALUES('file-available','formal_output','sha256','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',200,'image/tiff','sha256/bb/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','available','2026-08-23T00:11:02Z','2026-08-23T00:11:02Z');
        INSERT OR IGNORE INTO file_objects(file_object_id,object_kind,hash_algorithm,content_hash,byte_length,media_type,object_key,storage_state,created_at_utc,available_at_utc)
        VALUES('file-quarantined','formal_output','sha256','dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',99,'application/octet-stream','sha256/dd/dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd','available','2026-08-23T00:11:02Z','2026-08-23T00:11:02Z');
        INSERT OR IGNORE INTO result_files(result_file_id,result_id,file_object_id,file_role,relative_path,is_required,byte_length_snapshot,content_hash_snapshot)
        VALUES('result-file-available','result-target','file-available','primary','dom.tif',1,200,'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');
        INSERT OR IGNORE INTO result_files(result_file_id,result_id,file_object_id,file_role,relative_path,is_required,byte_length_snapshot,content_hash_snapshot)
        VALUES('result-file-quarantined','result-target','file-quarantined','metadata','excluded.bin',0,99,'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd');
        UPDATE file_objects SET storage_state='quarantined' WHERE file_object_id='file-quarantined';
        INSERT OR IGNORE INTO quality_reports(quality_report_id,report_type,version_number,lifecycle_state,result_id,created_by_execution_id,schema_version,summary_severity,summary_json,created_at_utc)
        VALUES('quality-report-final','result_validation',1,'draft','result-target','execution-lineage','v1','none','{"blocking":0,"warning":0,"info":1}','2026-08-23T00:11:03Z');
        INSERT OR IGNORE INTO quality_findings(quality_finding_id,quality_report_id,sort_index,check_code,severity,conclusion)
        VALUES('quality-finding-final','quality-report-final',0,'result.readable','info','passed');
        UPDATE quality_reports SET lifecycle_state='final', finalized_at_utc='2026-08-23T00:11:04Z' WHERE quality_report_id='quality-report-final';
        UPDATE results SET lifecycle_state='published', published_at_utc='2026-08-23T00:12:00Z' WHERE result_id='result-target';
        """;
}
