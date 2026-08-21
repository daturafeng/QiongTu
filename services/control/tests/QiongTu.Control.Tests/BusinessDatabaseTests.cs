using Microsoft.Data.Sqlite;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class BusinessDatabaseTests
{
    [TestMethod]
    public void NewDatabaseAppliesEmbeddedSchemaAndRequiredPragmas()
    {
        using var scope = new DatabaseScope();
        var database = new BusinessDatabase(scope.DatabasePath);

        database.Initialize();

        using var connection = database.OpenConnection();
        Assert.AreEqual(1L, Scalar<long>(connection, "PRAGMA user_version;"));
        Assert.AreEqual(1L, Scalar<long>(connection, "SELECT count(*) FROM schema_migrations;"));
        Assert.AreEqual(1L, Scalar<long>(connection, "PRAGMA foreign_keys;"));
        Assert.AreEqual(5_000L, Scalar<long>(connection, "PRAGMA busy_timeout;"));
        Assert.AreEqual(2L, Scalar<long>(connection, "PRAGMA synchronous;"));
        Assert.AreEqual("wal", Scalar<string>(connection, "PRAGMA journal_mode;").ToLowerInvariant());
        Assert.AreEqual("ok", Scalar<string>(connection, "PRAGMA quick_check;"));

        var requiredTables = new[]
        {
            "projects", "datasets", "dataset_versions", "file_objects", "images", "image_frames",
            "image_metadata_fields", "positioning_aux_files", "positioning_aux_usage", "processing_jobs", "job_executions",
            "job_events", "result_series", "results", "result_files", "result_dependencies",
            "quality_reports", "quality_findings"
        };
        foreach (var table in requiredTables)
        {
            Assert.AreEqual(
                1L,
                Scalar<long>(connection, $"SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='{table}';"),
                $"Missing authoritative table {table}.");
        }
    }

    [TestMethod]
    public async Task RepeatedAndConcurrentInitializationIsIdempotent()
    {
        using var scope = new DatabaseScope();
        var first = new BusinessDatabase(scope.DatabasePath);
        first.Initialize();
        string appliedAt;
        using (var connection = first.OpenConnection())
        {
            appliedAt = Scalar<string>(connection, "SELECT applied_at_utc FROM schema_migrations WHERE version=1;");
        }

        first.Initialize();
        await Task.WhenAll(
            Task.Run(() => new BusinessDatabase(scope.DatabasePath).Initialize()),
            Task.Run(() => new BusinessDatabase(scope.DatabasePath).Initialize()),
            Task.Run(() => new BusinessDatabase(scope.DatabasePath).Initialize()));

        using var finalConnection = first.OpenConnection();
        Assert.AreEqual(1L, Scalar<long>(finalConnection, "SELECT count(*) FROM schema_migrations;"));
        Assert.AreEqual(appliedAt, Scalar<string>(finalConnection, "SELECT applied_at_utc FROM schema_migrations WHERE version=1;"));
    }

    [TestMethod]
    public void FutureVersionAndMigrationDriftAreRejectedBeforeWrites()
    {
        using var futureScope = new DatabaseScope();
        var future = new BusinessDatabase(futureScope.DatabasePath);
        using (var connection = OpenRaw(futureScope.DatabasePath))
        {
            Execute(connection, "CREATE TABLE future_data(id INTEGER PRIMARY KEY);");
            Execute(connection, "PRAGMA user_version = 2;");
        }

        var futureException = Assert.Throws<BusinessDatabaseException>(future.Initialize);
        Assert.AreEqual("business_database_future_version", futureException.Code);
        using (var connection = OpenRaw(futureScope.DatabasePath))
        {
            Assert.AreEqual(1L, Scalar<long>(connection, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='future_data';"));
            Assert.AreEqual(0L, Scalar<long>(connection, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='schema_migrations';"));
        }

        using var driftScope = new DatabaseScope();
        var drift = new BusinessDatabase(driftScope.DatabasePath);
        drift.Initialize();
        using (var connection = drift.OpenConnection())
        {
            Execute(connection, $"UPDATE schema_migrations SET sql_sha256='{new string('0', 64)}' WHERE version=1;");
        }

        var driftException = Assert.Throws<BusinessDatabaseException>(drift.Initialize);
        Assert.AreEqual("business_database_migration_drift", driftException.Code);
    }

    [TestMethod]
    public void FailedMigrationRollsBackWithoutPartialSchemaOrLedgerEntry()
    {
        using var scope = new DatabaseScope();
        var brokenMigration = BusinessMigration.Create(
            1,
            "0001_broken.sql",
            "CREATE TABLE rolled_back(id INTEGER PRIMARY KEY); THIS IS NOT SQL;");
        var database = new BusinessDatabase(scope.DatabasePath, [brokenMigration]);

        var exception = Assert.Throws<BusinessDatabaseException>(database.Initialize);

        Assert.AreEqual("business_database_migration_failed", exception.Code);
        using var connection = database.OpenConnection();
        Assert.AreEqual(0L, Scalar<long>(connection, "SELECT count(*) FROM schema_migrations;"));
        Assert.AreEqual(0L, Scalar<long>(connection, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='rolled_back';"));
        Assert.AreEqual(0L, Scalar<long>(connection, "PRAGMA user_version;"));
    }

    [TestMethod]
    public void ValidDomainGraphPersistsAndDatabaseRejectsBrokenLineageAndMutation()
    {
        using var scope = new DatabaseScope();
        var database = new BusinessDatabase(scope.DatabasePath);
        database.Initialize();
        using var connection = database.OpenConnection();
        Execute(connection, ValidDomainGraphSql);

        Assert.AreEqual("sealed", Scalar<string>(connection,
            "SELECT lifecycle_state FROM dataset_versions WHERE dataset_version_id='dataset-version-1';"));
        Assert.AreEqual(1L, Scalar<long>(connection, "SELECT count(*) FROM results WHERE lifecycle_state='published';"));
        Assert.AreEqual(1L, Scalar<long>(connection, "SELECT count(*) FROM quality_reports WHERE lifecycle_state='final';"));
        Assert.AreEqual(1L, Scalar<long>(connection, "SELECT count(*) FROM positioning_aux_usage WHERE usage_state='used';"));

        AssertSqlRejected(connection,
            "INSERT INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc) VALUES('bad','missing','Bad','active','t','t');");
        AssertSqlRejected(connection, "UPDATE projects SET lifecycle_state='invalid' WHERE project_id='project-1';");
        AssertSqlRejected(connection, "UPDATE dataset_versions SET quality_gate_state='passed' WHERE dataset_version_id='dataset-version-1';");
        AssertSqlRejected(connection, "DELETE FROM images WHERE image_id='image-1';");
        AssertSqlRejected(connection,
            $"INSERT INTO images(image_id,dataset_version_id,source_file_object_id,import_source_key,sort_index,content_container,image_state,metadata_state,created_at_utc) VALUES('image-2','dataset-version-1','source-file','late',2,'jpeg','imported','parsed','t');");
        AssertSqlRejected(connection, "UPDATE results SET bounds_json='{}' WHERE result_id='result-1';");
        AssertSqlRejected(connection, "UPDATE results SET lifecycle_state='candidate' WHERE result_id='result-1';");
        AssertSqlRejected(connection, "DELETE FROM result_files WHERE result_file_id='result-file-1';");
        AssertSqlRejected(connection, "UPDATE quality_reports SET summary_json='{\"changed\":true}' WHERE quality_report_id='quality-report-1';");
        AssertSqlRejected(connection, "UPDATE quality_reports SET lifecycle_state='draft' WHERE quality_report_id='quality-report-1';");
        AssertSqlRejected(connection, "DELETE FROM quality_findings WHERE quality_finding_id='finding-1';");
        AssertSqlRejected(connection, "UPDATE processing_jobs SET parameters_json='{\"changed\":true}' WHERE processing_job_id='job-1';");
        AssertSqlRejected(connection, "UPDATE job_executions SET attempt_number=2 WHERE job_execution_id='execution-1';");
    }

    [TestMethod]
    public void CorruptDatabaseIsPreservedAndReported()
    {
        using var scope = new DatabaseScope();
        var bytes = new byte[] { 0x51, 0x54, 0x00, 0xff, 0x19 };
        File.WriteAllBytes(scope.DatabasePath, bytes);
        var database = new BusinessDatabase(scope.DatabasePath);

        var exception = Assert.Throws<BusinessDatabaseException>(database.Initialize);

        Assert.AreEqual("business_database_integrity_failed", exception.Code);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(scope.DatabasePath));
    }

    private static void AssertSqlRejected(SqliteConnection connection, string sql) =>
        Assert.Throws<SqliteException>(() => Execute(connection, sql));

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenRaw(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(
            command.ExecuteScalar()!,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class DatabaseScope : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"qiongtu-business-db-{Guid.NewGuid():N}");

        public DatabaseScope()
        {
            Directory.CreateDirectory(_root);
            DatabasePath = Path.Combine(_root, "qiongtu.db");
        }

        public string DatabasePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }

    private const string ValidDomainGraphSql =
        """
        INSERT INTO crs_definitions(crs_id,authority,code,name,horizontal_unit,axis_order,created_at_utc)
        VALUES('crs-1','EPSG','32648','WGS 84 / UTM zone 48N','metre','east-north','2026-08-21T00:00:00Z');
        INSERT INTO projects(project_id,name,default_crs_id,spatial_configuration_state,lifecycle_state,created_at_utc,updated_at_utc)
        VALUES('project-1','Project','crs-1','confirmed','active','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
        VALUES('dataset-1','project-1','Flight 1','active','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO file_objects(file_object_id,object_kind,hash_algorithm,content_hash,byte_length,media_type,object_key,storage_state,created_at_utc,available_at_utc)
        VALUES('source-file','source_image','sha256','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',100,'image/jpeg','sha256/aa/source','available','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO file_objects(file_object_id,object_kind,hash_algorithm,content_hash,byte_length,media_type,object_key,storage_state,created_at_utc,available_at_utc)
        VALUES('position-file','positioning_aux','sha256','dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',80,'text/plain','sha256/dd/position','available','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc)
        VALUES('dataset-version-1','dataset-1',1,'draft','dji_supported','passed','2026-08-21T00:00:00Z');
        INSERT INTO images(image_id,dataset_version_id,source_file_object_id,import_source_key,sort_index,content_container,primary_frame_index,width,height,manufacturer,camera_model,image_state,metadata_state,created_at_utc)
        VALUES('image-1','dataset-version-1','source-file','DJI_0001.JPG',1,'jpeg',0,4000,3000,'DJI','FC-test','processing_input','parsed','2026-08-21T00:00:00Z');
        INSERT INTO image_frames(image_frame_id,image_id,frame_index,frame_role,width,height,decode_state)
        VALUES('frame-1','image-1',0,'primary_photogrammetry',4000,3000,'decoded');
        INSERT INTO image_metadata_fields(image_metadata_field_id,image_id,field_name,field_value_json,source_kind,field_state)
        VALUES('metadata-1','image-1','gps.latitude','29.0','gps_exif','present');
        INSERT INTO positioning_aux_files(positioning_aux_file_id,dataset_version_id,file_object_id,auxiliary_type,retention_state,parse_state,quality_state,parser_name,parser_version,created_at_utc)
        VALUES('positioning-1','dataset-version-1','position-file','MRK','retained','parsed','passed','dji-mrk','v1','2026-08-21T00:00:00Z');
        INSERT INTO processing_jobs(processing_job_id,project_id,dataset_version_id,job_type,requested_outputs_json,parameter_profile,parameter_schema_version,parameters_json,parameter_sha256,lifecycle_state,recovery_state,created_at_utc,submitted_at_utc)
        VALUES('job-1','project-1','dataset-version-1','photogrammetry','["dom"]','standard','v1','{}','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','succeeded','not_applicable','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO job_executions(job_execution_id,processing_job_id,attempt_number,execution_mode,worker_type,worker_version,engine_name,engine_version,parameter_sha256,lifecycle_state,checkpoint_compatibility_state,started_at_utc,ended_at_utc)
        VALUES('execution-1','job-1',1,'full','photogrammetry','v1','engine','1.0','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','succeeded','unavailable','2026-08-21T00:00:00Z','2026-08-21T01:00:00Z');
        INSERT INTO positioning_aux_usage(positioning_aux_usage_id,positioning_aux_file_id,job_execution_id,usage_state,evidence_json,recorded_at_utc)
        VALUES('positioning-usage-1','positioning-1','execution-1','used','{"records":1}','2026-08-21T01:00:00Z');
        INSERT INTO file_objects(file_object_id,object_kind,hash_algorithm,content_hash,byte_length,media_type,object_key,storage_state,created_at_utc,available_at_utc)
        VALUES('output-file','formal_output','sha256','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',200,'image/tiff','sha256/bb/output','available','2026-08-21T01:00:00Z','2026-08-21T01:00:00Z');
        INSERT INTO result_series(result_series_id,project_id,dataset_version_id,series_kind,name,created_at_utc)
        VALUES('series-1','project-1','dataset-version-1','dom','DOM','2026-08-21T01:00:00Z');
        INSERT INTO results(result_id,result_series_id,version_number,source_dataset_version_id,source_processing_job_id,source_job_execution_id,result_kind,lifecycle_state,crs_id,unit,bounds_json,parameter_sha256,accuracy_level,created_at_utc)
        VALUES('result-1','series-1',1,'dataset-version-1','job-1','execution-1','dom','candidate','crs-1','metre','{"west":1}','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','georeferenced_visualization','2026-08-21T01:00:00Z');
        INSERT INTO result_files(result_file_id,result_id,file_object_id,file_role,relative_path,is_required,byte_length_snapshot,content_hash_snapshot)
        VALUES('result-file-1','result-1','output-file','primary','dom.tif',1,200,'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');
        INSERT INTO quality_reports(quality_report_id,report_type,version_number,lifecycle_state,result_id,created_by_execution_id,schema_version,summary_severity,summary_json,created_at_utc)
        VALUES('quality-report-1','result_validation',1,'draft','result-1','execution-1','v1','none','{}','2026-08-21T01:00:00Z');
        INSERT INTO quality_findings(quality_finding_id,quality_report_id,sort_index,check_code,severity,conclusion)
        VALUES('finding-1','quality-report-1',0,'result.readable','info','passed');
        UPDATE quality_reports SET lifecycle_state='final', finalized_at_utc='2026-08-21T01:01:00Z' WHERE quality_report_id='quality-report-1';
        UPDATE results SET lifecycle_state='published', published_at_utc='2026-08-21T01:02:00Z' WHERE result_id='result-1';
        """;
}
