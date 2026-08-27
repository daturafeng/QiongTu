using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class LineageIntegrityIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task PublishedLineageThroughRealPipeRejectsSnapshotTamperingAndRemainsAuthoritative()
    {
        await using var scope = await LineagePipeScope.StartAsync();
        var graph = await scope.SeedPublishedLineageGraphAsync();

        using var firstResponse = await scope.SendAsync(
            ControlMethods.ResultLineage,
            "lineage-integrity-target",
            new { resultId = graph.TargetResultId });
        var firstLineage = Ok(firstResponse).GetProperty("result");
        var firstLineageJson = firstLineage.GetRawText();

        AssertTargetLineage(firstLineage, graph);
        AssertSanitized(firstResponse, scope.Root);

        using var notFound = await scope.SendAsync(
            ControlMethods.ResultLineage,
            "lineage-integrity-not-found",
            new { resultId = "result-missing" });
        Error(notFound, "result_not_found");
        Assert.AreEqual(JsonValueKind.Null, notFound.RootElement.GetProperty("result").ValueKind);
        AssertSanitized(notFound, scope.Root);

        scope.AssertSqliteRejectsInTransaction(
            """
            UPDATE result_dependencies
            SET depends_on_result_id = 'result-unrelated'
            WHERE result_id = 'result-target' AND depends_on_result_id = 'result-source';
            """,
            "published result dependencies are immutable");

        scope.AssertSqliteRejectsInTransaction(
            """
            UPDATE result_files
            SET byte_length_snapshot = byte_length_snapshot + 1
            WHERE result_file_id = 'result-file-primary';
            """,
            "published result files are immutable");

        using var afterTamperResponse = await scope.SendAsync(
            ControlMethods.ResultLineage,
            "lineage-integrity-after-rejected-tamper",
            new { resultId = graph.TargetResultId });
        var afterTamperLineage = Ok(afterTamperResponse).GetProperty("result");

        Assert.AreEqual(firstLineageJson, afterTamperLineage.GetRawText());
        AssertTargetLineage(afterTamperLineage, graph);
        AssertSanitized(afterTamperResponse, scope.Root);
        Assert.AreEqual(1L, scope.Scalar<long>(
            "SELECT count(*) FROM result_dependencies WHERE result_id = 'result-target' AND depends_on_result_id = 'result-source';"));
        Assert.AreEqual(graph.PrimaryObject.Sha256, scope.Scalar<string>(
            "SELECT content_hash_snapshot FROM result_files WHERE result_file_id = 'result-file-primary';"));
        Assert.AreEqual(graph.PrimaryObject.ByteLength, scope.Scalar<long>(
            "SELECT byte_length_snapshot FROM result_files WHERE result_file_id = 'result-file-primary';"));
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

    private static void AssertTargetLineage(JsonElement lineage, PublishedLineageGraph graph)
    {
        Assert.AreEqual(graph.TargetResultId, lineage.GetProperty("target").GetProperty("resultId").GetString());
        Assert.AreEqual("published", lineage.GetProperty("target").GetProperty("lifecycleState").GetString());
        Assert.AreEqual("series-target", lineage.GetProperty("series").GetProperty("resultSeriesId").GetString());
        Assert.AreEqual("project-lineage", lineage.GetProperty("project").GetProperty("projectId").GetString());
        Assert.AreEqual("dataset-version-lineage", lineage.GetProperty("sourceDatasetVersion").GetProperty("datasetVersionId").GetString());
        Assert.AreEqual("job-lineage", lineage.GetProperty("sourceProcessingJob").GetProperty("processingJobId").GetString());
        Assert.AreEqual("execution-lineage", lineage.GetProperty("sourceJobExecution").GetProperty("jobExecutionId").GetString());

        var dependencies = lineage.GetProperty("directDependencies").EnumerateArray().ToArray();
        Assert.HasCount(1, dependencies, lineage.GetProperty("directDependencies").GetRawText());
        Assert.AreEqual(graph.SourceResultId, dependencies[0].GetProperty("dependsOnResultId").GetString());
        Assert.AreEqual("derived_from", dependencies[0].GetProperty("dependencyKind").GetString());
        Assert.IsFalse(dependencies.Any(item => item.GetProperty("dependsOnResultId").GetString() == "result-camera"));
        Assert.IsFalse(dependencies.Any(item => item.GetProperty("dependsOnResultId").GetString() == "result-unrelated"));

        var files = lineage.GetProperty("availableFiles").EnumerateArray().ToArray();
        Assert.HasCount(1, files, lineage.GetProperty("availableFiles").GetRawText());
        Assert.AreEqual("result-file-primary", files[0].GetProperty("resultFileId").GetString());
        Assert.AreEqual("dom/formal-output.tif", files[0].GetProperty("relativePath").GetString());
        Assert.AreEqual(graph.PrimaryObject.ObjectKey, files[0].GetProperty("objectKey").GetString());
        Assert.AreEqual(graph.PrimaryObject.ByteLength, files[0].GetProperty("byteLengthSnapshot").GetInt64());
        Assert.AreEqual(graph.PrimaryObject.Sha256, files[0].GetProperty("contentHashSnapshot").GetString());
        StringAssert.StartsWith(files[0].GetProperty("objectKey").GetString(), "sha256/");

        var reports = lineage.GetProperty("finalQualityReports").EnumerateArray().ToArray();
        Assert.HasCount(1, reports, lineage.GetProperty("finalQualityReports").GetRawText());
        Assert.AreEqual("quality-report-final", reports[0].GetProperty("qualityReportId").GetString());
        Assert.AreEqual("final", reports[0].GetProperty("lifecycleState").GetString());
        Assert.AreEqual("none", reports[0].GetProperty("summarySeverity").GetString());
    }

    private static void AssertSanitized(JsonDocument response, string root)
    {
        var raw = response.RootElement.GetRawText();
        Assert.DoesNotContain(root, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("staging", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quarantine", raw, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LineagePipeScope : IAsyncDisposable
    {
        private readonly WorkerSupervisor _workers;
        private readonly ArtifactServer _artifactServer;
        private readonly NamedPipeControlServer _server;
        private readonly BusinessDatabase _database;
        private readonly ContentAddressedObjectStore _objectStore;

        private LineagePipeScope(
            string root,
            string pipeName,
            BusinessDatabase database,
            ContentAddressedObjectStore objectStore,
            WorkerSupervisor workers,
            ArtifactServer artifactServer,
            NamedPipeControlServer server)
        {
            Root = root;
            PipeName = pipeName;
            _database = database;
            _objectStore = objectStore;
            _workers = workers;
            _artifactServer = artifactServer;
            _server = server;
        }

        public string Root { get; }

        private string PipeName { get; }

        public static async Task<LineagePipeScope> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"qiongtu-lineage-integrity-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = ControlDataPaths.Create(root);
            var runtimeStore = new WorkerRuntimeStore(paths.RuntimeDatabase);
            runtimeStore.Initialize();
            var database = new BusinessDatabase(paths.BusinessDatabase);
            database.Initialize();
            var objectStore = new ContentAddressedObjectStore(paths.ObjectDirectory);
            var registry = new WorkerRegistry();
            var capabilities = new ProcessingCapabilityService(registry, paths);
            var workers = new WorkerSupervisor(registry, runtimeStore, paths.LogDirectory, capabilities);
            var roots = new ArtifactRootRegistry();
            roots.RegisterTrustedRoot("objects", objectStore.PublishedDirectory);
            var artifactServer = new ArtifactServer(roots);
            await artifactServer.StartAsync(CancellationToken.None);
            var pipeName = RuntimeDiscovery.CreatePipeName();
            var dispatcher = new ControlRequestDispatcher(
                pipeName,
                DateTimeOffset.UtcNow,
                artifactServer,
                workers,
                new BusinessCatalog(database),
                capabilities,
                requestStop: () => { });
            var server = new NamedPipeControlServer(pipeName, dispatcher);
            server.Start();
            return new LineagePipeScope(root, pipeName, database, objectStore, workers, artifactServer, server);
        }

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            await _artifactServer.DisposeAsync();
            _workers.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }

        public async Task<JsonDocument> SendAsync(string method, string requestId, object? parameters)
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5_000);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new { apiVersion = ContractVersions.ControlApiV1, requestId, method, parameters },
                JsonOptions));
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(line);
            return JsonDocument.Parse(line);
        }

        public async Task<PublishedLineageGraph> SeedPublishedLineageGraphAsync()
        {
            var primaryObject = await PublishObjectAsync("authoritative-dom-formal-output");
            var sourceObject = await PublishObjectAsync("authoritative-aerotriangulation-output");
            var reportObject = await PublishObjectAsync("final-quality-report");
            var stagedOnly = await _objectStore.StageAsync(new MemoryStream(Encoding.UTF8.GetBytes("not-yet-published-staging")));
            var quarantined = await _objectStore.AbandonAsync(stagedOnly);
            Assert.IsTrue(Directory.Exists(Path.Combine(_objectStore.QuarantineDirectory, quarantined.QuarantineId)));
            Assert.IsTrue(File.Exists(Path.Combine(
                _objectStore.PublishedDirectory,
                primaryObject.ObjectKey.Replace('/', Path.DirectorySeparatorChar))));

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                Execute(connection, transaction, SeedAuthorityGraphSql);
                InsertFileObject(connection, transaction, "file-primary", "formal_output", primaryObject, "image/tiff");
                InsertFileObject(connection, transaction, "file-source", "formal_output", sourceObject, "application/json");
                InsertFileObject(connection, transaction, "file-report", "quality_report", reportObject, "application/json");
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO result_files(result_file_id,result_id,file_object_id,file_role,relative_path,is_required,byte_length_snapshot,content_hash_snapshot)
                    VALUES('result-file-source','result-source','file-source','metadata','at/source.json',1,$source_length,$source_hash);
                    INSERT INTO result_files(result_file_id,result_id,file_object_id,file_role,relative_path,is_required,byte_length_snapshot,content_hash_snapshot)
                    VALUES('result-file-primary','result-target','file-primary','primary','dom/formal-output.tif',1,$primary_length,$primary_hash);
                    INSERT INTO quality_reports(quality_report_id,report_type,version_number,lifecycle_state,result_id,created_by_execution_id,report_file_object_id,schema_version,summary_severity,summary_json,created_at_utc)
                    VALUES('quality-report-final','result_validation',1,'draft','result-target','execution-lineage','file-report','v1','none','{"blocking":0,"warning":0,"info":1}','2026-08-23T00:11:03Z');
                    INSERT INTO quality_findings(quality_finding_id,quality_report_id,sort_index,check_code,severity,conclusion)
                    VALUES('quality-finding-final','quality-report-final',0,'result.readable','info','passed');
                    UPDATE quality_reports
                    SET lifecycle_state='final', finalized_at_utc='2026-08-23T00:11:04Z'
                    WHERE quality_report_id='quality-report-final';
                    UPDATE results
                    SET lifecycle_state='published', published_at_utc='2026-08-23T00:12:00Z'
                    WHERE result_id='result-target';
                    """,
                    ("$source_length", sourceObject.ByteLength),
                    ("$source_hash", sourceObject.Sha256),
                    ("$primary_length", primaryObject.ByteLength),
                    ("$primary_hash", primaryObject.Sha256));
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            return new PublishedLineageGraph("result-target", "result-source", primaryObject, sourceObject, reportObject);
        }

        public void AssertSqliteRejectsInTransaction(string sql, string expectedMessage)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            var exception = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
            StringAssert.Contains(exception.Message, expectedMessage);
            transaction.Rollback();
        }

        public T Scalar<T>(string sql)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        private async Task<PublishedObject> PublishObjectAsync(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var stage = await _objectStore.StageAsync(new MemoryStream(bytes), Sha256(bytes));
            return await _objectStore.PublishAsync(stage);
        }

        private static void InsertFileObject(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string fileObjectId,
            string objectKind,
            PublishedObject published,
            string mediaType)
        {
            Execute(
                connection,
                transaction,
                """
                INSERT INTO file_objects(file_object_id,object_kind,hash_algorithm,content_hash,byte_length,media_type,object_key,storage_state,created_at_utc,available_at_utc)
                VALUES($file_object_id,$object_kind,'sha256',$content_hash,$byte_length,$media_type,$object_key,'available','2026-08-23T00:11:02Z','2026-08-23T00:11:02Z');
                """,
                ("$file_object_id", fileObjectId),
                ("$object_kind", objectKind),
                ("$content_hash", published.Sha256),
                ("$byte_length", published.ByteLength),
                ("$media_type", mediaType),
                ("$object_key", published.ObjectKey));
        }

        private static void Execute(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql,
            params (string Name, object? Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            }

            command.ExecuteNonQuery();
        }

        private static string Sha256(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed record PublishedLineageGraph(
        string TargetResultId,
        string SourceResultId,
        PublishedObject PrimaryObject,
        PublishedObject SourceObject,
        PublishedObject ReportObject);

    private const string SeedAuthorityGraphSql =
        """
        INSERT INTO crs_definitions(crs_id,authority,code,name,horizontal_unit,vertical_reference,axis_order,crs_type,captured_at_utc,created_at_utc)
        VALUES('crs-lineage','EPSG','32648','WGS 84 / UTM zone 48N','metre','unknown','east-north','projected','2026-08-23T00:00:00Z','2026-08-23T00:00:00Z');
        INSERT INTO projects(project_id,name,default_crs_id,spatial_configuration_state,lifecycle_state,created_at_utc,updated_at_utc)
        VALUES('project-lineage','Lineage Project','crs-lineage','confirmed','active','2026-08-23T00:00:00Z','2026-08-23T00:00:00Z');
        INSERT INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
        VALUES('dataset-lineage','project-lineage','Lineage Dataset','active','2026-08-23T00:00:01Z','2026-08-23T00:00:01Z');
        INSERT INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc)
        VALUES('dataset-version-lineage','dataset-lineage',1,'draft','dji_supported','passed','2026-08-23T00:00:02Z');
        INSERT INTO processing_jobs(processing_job_id,project_id,dataset_version_id,job_type,requested_outputs_json,parameter_profile,parameter_schema_version,parameters_json,parameter_sha256,lifecycle_state,recovery_state,created_at_utc,submitted_at_utc,started_at_utc,ended_at_utc)
        VALUES('job-lineage','project-lineage','dataset-version-lineage','photogrammetry','["dom","aerotriangulation"]','standard','v1','{}','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','succeeded','not_applicable','2026-08-23T00:00:03Z','2026-08-23T00:00:03Z','2026-08-23T00:00:04Z','2026-08-23T00:10:00Z');
        INSERT INTO job_executions(job_execution_id,processing_job_id,attempt_number,execution_mode,worker_type,worker_version,engine_name,engine_version,parameter_sha256,lifecycle_state,checkpoint_compatibility_state,started_at_utc,ended_at_utc)
        VALUES('execution-lineage','job-lineage',1,'full','photogrammetry','v1','engine','1.0','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','succeeded','unavailable','2026-08-23T00:00:04Z','2026-08-23T00:10:00Z');
        INSERT INTO result_series(result_series_id,project_id,dataset_version_id,series_kind,name,created_at_utc)
        VALUES('series-camera','project-lineage','dataset-version-lineage','aerotriangulation','Camera AT','2026-08-23T00:09:00Z');
        INSERT INTO result_series(result_series_id,project_id,dataset_version_id,series_kind,name,parent_series_id,created_at_utc)
        VALUES('series-source','project-lineage','dataset-version-lineage','aerotriangulation','AT','series-camera','2026-08-23T00:10:00Z');
        INSERT INTO result_series(result_series_id,project_id,dataset_version_id,series_kind,name,parent_series_id,created_at_utc)
        VALUES('series-target','project-lineage','dataset-version-lineage','dom','DOM','series-source','2026-08-23T00:11:00Z');
        INSERT INTO result_series(result_series_id,project_id,dataset_version_id,series_kind,name,created_at_utc)
        VALUES('series-unrelated','project-lineage','dataset-version-lineage','dsm','Unrelated DSM','2026-08-23T00:11:30Z');
        INSERT INTO results(result_id,result_series_id,version_number,source_dataset_version_id,source_processing_job_id,source_job_execution_id,result_kind,lifecycle_state,crs_id,unit,bounds_json,parameter_sha256,accuracy_level,created_at_utc)
        VALUES('result-camera','series-camera',1,'dataset-version-lineage','job-lineage','execution-lineage','aerotriangulation','candidate','crs-lineage','metre','{"westLongitude":114.1,"southLatitude":29.7,"eastLongitude":114.2,"northLatitude":29.8}','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','georeferenced_visualization','2026-08-23T00:09:01Z');
        INSERT INTO results(result_id,result_series_id,version_number,source_dataset_version_id,source_processing_job_id,source_job_execution_id,source_result_id,result_kind,lifecycle_state,crs_id,unit,bounds_json,parameter_sha256,accuracy_level,created_at_utc)
        VALUES('result-source','series-source',1,'dataset-version-lineage','job-lineage','execution-lineage','result-camera','aerotriangulation','candidate','crs-lineage','metre','{"westLongitude":114.1,"southLatitude":29.7,"eastLongitude":114.2,"northLatitude":29.8}','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','georeferenced_visualization','2026-08-23T00:10:01Z');
        INSERT INTO results(result_id,result_series_id,version_number,source_dataset_version_id,source_processing_job_id,source_job_execution_id,source_result_id,result_kind,lifecycle_state,crs_id,unit,bounds_json,parameter_sha256,accuracy_level,created_at_utc)
        VALUES('result-target','series-target',1,'dataset-version-lineage','job-lineage','execution-lineage','result-source','dom','candidate','crs-lineage','metre','{"westLongitude":114.1,"southLatitude":29.7,"eastLongitude":114.2,"northLatitude":29.8}','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','georeferenced_visualization','2026-08-23T00:11:01Z');
        INSERT INTO results(result_id,result_series_id,version_number,source_dataset_version_id,source_processing_job_id,source_job_execution_id,result_kind,lifecycle_state,crs_id,unit,bounds_json,parameter_sha256,accuracy_level,created_at_utc)
        VALUES('result-unrelated','series-unrelated',1,'dataset-version-lineage','job-lineage','execution-lineage','dsm','candidate','crs-lineage','metre','{"westLongitude":114.1,"southLatitude":29.7,"eastLongitude":114.2,"northLatitude":29.8}','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','georeferenced_visualization','2026-08-23T00:11:31Z');
        INSERT INTO result_dependencies(result_id,depends_on_result_id,dependency_kind)
        VALUES('result-source','result-camera','derived_from');
        INSERT INTO result_dependencies(result_id,depends_on_result_id,dependency_kind)
        VALUES('result-target','result-source','derived_from');
        """;
}
