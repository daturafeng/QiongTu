using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

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
        Assert.AreEqual((long)BusinessDatabase.CurrentSchemaVersion, Scalar<long>(connection, "PRAGMA user_version;"));
        Assert.AreEqual((long)BusinessDatabase.CurrentSchemaVersion, Scalar<long>(connection, "SELECT count(*) FROM schema_migrations;"));
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
            "quality_reports", "quality_findings", "image_import_sessions", "image_import_entries",
            "file_object_roles", "image_inspection_runs", "image_frame_lineage", "image_metadata_runs",
            "positioning_aux_import_runs", "positioning_aux_import_items"
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
        Assert.AreEqual((long)BusinessDatabase.CurrentSchemaVersion, Scalar<long>(finalConnection, "SELECT count(*) FROM schema_migrations;"));
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
            Execute(connection, $"PRAGMA user_version = {BusinessDatabase.CurrentSchemaVersion + 1};");
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
        var baselineMigration = BusinessMigration.Create(
            1,
            "0001_baseline.sql",
            "CREATE TABLE baseline(id INTEGER PRIMARY KEY);");
        var secondMigration = BusinessMigration.Create(
            2,
            "0002_second.sql",
            "CREATE TABLE second(id INTEGER PRIMARY KEY);");
        var brokenMigration = BusinessMigration.Create(
            3,
            "0003_broken.sql",
            "CREATE TABLE rolled_back(id INTEGER PRIMARY KEY); THIS IS NOT SQL;");
        var fourthMigration = BusinessMigration.Create(
            4,
            "0004_never_reached.sql",
            "CREATE TABLE never_reached(id INTEGER PRIMARY KEY);");
        var fifthMigration = BusinessMigration.Create(
            5,
            "0005_never_reached.sql",
            "CREATE TABLE also_never_reached(id INTEGER PRIMARY KEY);");
        var sixthMigration = BusinessMigration.Create(
            6,
            "0006_never_reached.sql",
            "CREATE TABLE still_never_reached(id INTEGER PRIMARY KEY);");
        var seventhMigration = BusinessMigration.Create(
            7,
            "0007_never_reached.sql",
            "CREATE TABLE latest_never_reached(id INTEGER PRIMARY KEY);");
        var eighthMigration = BusinessMigration.Create(
            8,
            "0008_never_reached.sql",
            "CREATE TABLE metadata_never_reached(id INTEGER PRIMARY KEY);");
        var ninthMigration = BusinessMigration.Create(
            9,
            "0009_never_reached.sql",
            "CREATE TABLE support_disposition_never_reached(id INTEGER PRIMARY KEY);");
        var tenthMigration = BusinessMigration.Create(
            10,
            "0010_never_reached.sql",
            "CREATE TABLE positioning_aux_never_reached(id INTEGER PRIMARY KEY);");
        var database = new BusinessDatabase(
            scope.DatabasePath,
            [baselineMigration, secondMigration, brokenMigration, fourthMigration, fifthMigration, sixthMigration, seventhMigration, eighthMigration, ninthMigration, tenthMigration]);

        var exception = Assert.Throws<BusinessDatabaseException>(database.Initialize);

        Assert.AreEqual("business_database_migration_failed", exception.Code);
        using var connection = database.OpenConnection();
        Assert.AreEqual(2L, Scalar<long>(connection, "SELECT count(*) FROM schema_migrations;"));
        Assert.AreEqual(0L, Scalar<long>(connection, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='rolled_back';"));
        Assert.AreEqual(2L, Scalar<long>(connection, "PRAGMA user_version;"));
    }

    [TestMethod]
    public void VersionOneDatabaseAppliesNewerMigrationsWithoutRewritingFirstMigration()
    {
        using var scope = new DatabaseScope();
        CreateVersionOneDatabase(scope.DatabasePath);

        new BusinessDatabase(scope.DatabasePath).Initialize();

        using var upgraded = OpenRaw(scope.DatabasePath);
        Assert.AreEqual((long)BusinessDatabase.CurrentSchemaVersion, Scalar<long>(upgraded, "PRAGMA user_version;"));
        Assert.AreEqual((long)BusinessDatabase.CurrentSchemaVersion, Scalar<long>(upgraded, "SELECT count(*) FROM schema_migrations;"));
        Assert.AreEqual(1L, Scalar<long>(upgraded, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='projects';"));
        AssertSqlRejected(upgraded,
            "INSERT INTO file_objects(file_object_id,object_kind,hash_algorithm,content_hash,byte_length,object_key,storage_state,created_at_utc) VALUES('invalid-v2-key','formal_output','sha256','ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff',1,'staging/not-formal','available','t');");
    }

    [TestMethod]
    public void VersionSevenDatabaseWithUnprovenMetadataIsPreservedAndRejected()
    {
        using var scope = new DatabaseScope();
        CreateDatabaseAtVersion(scope.DatabasePath, 7);
        using (var connection = OpenRaw(scope.DatabasePath))
        {
            Execute(connection, "PRAGMA foreign_keys=OFF;");
            Execute(connection,
                "INSERT INTO image_metadata_fields(image_metadata_field_id,image_id,field_name,source_kind,field_state,source_detail) VALUES('legacy-field','legacy-image','camera.model','exif','present','IFD0.Model');");
        }

        var exception = Assert.Throws<BusinessDatabaseException>(
            () => new BusinessDatabase(scope.DatabasePath).Initialize());

        Assert.AreEqual("business_database_migration_failed", exception.Code);
        using var preserved = OpenRaw(scope.DatabasePath);
        Assert.AreEqual(7L, Scalar<long>(preserved, "PRAGMA user_version;"));
        Assert.AreEqual(7L, Scalar<long>(preserved, "SELECT count(*) FROM schema_migrations;"));
        Assert.AreEqual(1L, Scalar<long>(preserved, "SELECT count(*) FROM image_metadata_fields WHERE image_metadata_field_id='legacy-field';"));
        Assert.AreEqual(0L, Scalar<long>(preserved,
            "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='image_metadata_runs';"));
    }

    [TestMethod]
    public void VersionEightBlockedVendorPayloadGetsDeterministicSupportDisposition()
    {
        using var scope = new DatabaseScope();
        CreateDatabaseAtVersion(scope.DatabasePath, 8);
        using (var connection = OpenRaw(scope.DatabasePath))
        {
            Execute(connection,
                """
                INSERT INTO projects(project_id,name,spatial_configuration_state,lifecycle_state,created_at_utc,updated_at_utc)
                VALUES('upgrade-project','Upgrade','pending','active','t','t');
                INSERT INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
                VALUES('upgrade-dataset','upgrade-project','Upgrade','active','t','t');
                INSERT INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc)
                VALUES('upgrade-version','upgrade-dataset',1,'draft','dji_supported','not_run','t');
                INSERT INTO file_objects(
                    file_object_id,object_kind,hash_algorithm,content_hash,byte_length,object_key,
                    storage_state,created_at_utc,available_at_utc)
                VALUES(
                    'upgrade-source','source_image','sha256','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',1,
                    'sha256/aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','available','t','t');
                INSERT INTO file_object_roles(file_object_id,object_role,created_at_utc)
                VALUES('upgrade-source','source_image','t');
                INSERT INTO image_import_sessions(
                    import_session_id,dataset_version_id,source_root_key,source_locator_manifest_id,status,
                    total_entry_count,available_entry_count,created_at_utc,updated_at_utc,completed_at_utc)
                VALUES(
                    'upgrade-session','upgrade-version','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                    'upgrade-manifest','completed',1,1,'t','t','t');
                INSERT INTO image_import_entries(
                    import_entry_id,import_session_id,dataset_version_id,source_entry_key,display_name,sort_index,
                    byte_length_snapshot,status,stage_receipt_id,stage_receipt_sha256,stage_receipt_byte_length,
                    stage_receipt_created_at_utc,expected_content_hash,expected_byte_length,expected_object_key,
                    file_object_id,created_at_utc,updated_at_utc,terminal_at_utc)
                VALUES(
                    'upgrade-entry','upgrade-session','upgrade-version',
                    'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','UPGRADE.JPG',0,1,
                    'available','upgrade-stage','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',1,
                    't','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',1,
                    'sha256/aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    'upgrade-source','t','t','t');
                INSERT INTO image_inspection_runs(
                    inspection_run_id,import_entry_id,dataset_version_id,source_file_object_id,status,
                    parser_schema,parser_profile,product_parser,product_parser_version,native_decoder,
                    native_decoder_version,main_frame_policy_version,failure_code,
                    created_at_utc,updated_at_utc,completed_at_utc)
                VALUES(
                    'upgrade-inspection','upgrade-entry','upgrade-version','upgrade-source','blocked',
                    'qiongtu.image-probe.cas-image.v1','cas-image.v1','qiongtu.cas-image','1.0.0',
                    'magick.net-q16-x64','14.16.0','photogrammetry-main-frame.v1',
                    'mpf_unreferenced_trailing_data','t','t','t');
                """);
            Assert.AreEqual(1L, Scalar<long>(connection, "SELECT count(*) FROM image_inspection_runs WHERE status='blocked';"));
            Assert.AreEqual(
                "mpf_unreferenced_trailing_data",
                Scalar<string>(connection, "SELECT failure_code FROM image_inspection_runs;"));
        }

        new BusinessDatabase(scope.DatabasePath).Initialize();

        using var upgraded = OpenRaw(scope.DatabasePath);
        Assert.AreEqual((long)BusinessDatabase.CurrentSchemaVersion, Scalar<long>(upgraded, "PRAGMA user_version;"));
        Assert.AreEqual(1L, Scalar<long>(upgraded, "SELECT count(*) FROM image_inspection_runs WHERE status='blocked';"));
        Assert.AreEqual(
            ImageInspectionSupportPolicy.UnsupportedVendorPayload,
            Scalar<string>(upgraded, "SELECT support_disposition FROM image_inspection_runs;"));
        Assert.AreEqual(
            ImageInspectionSupportPolicy.Version,
            Scalar<string>(upgraded, "SELECT support_policy_version FROM image_inspection_runs;"));
        AssertSqlRejected(upgraded,
            "UPDATE image_inspection_runs SET support_disposition='image_not_processable' WHERE inspection_run_id='upgrade-inspection';");
    }

    [TestMethod]
    public void InvalidVersionOneAvailableObjectBlocksUpgradeWithoutChangingExistingData()
    {
        using var scope = new DatabaseScope();
        CreateVersionOneDatabase(
            scope.DatabasePath,
            connection => Execute(connection,
                "INSERT INTO file_objects(file_object_id,object_kind,hash_algorithm,content_hash,byte_length,object_key,storage_state,created_at_utc) VALUES('legacy-invalid','formal_output','sha256','eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',1,'legacy/wrong-key','available','t');"));

        var exception = Assert.Throws<BusinessDatabaseException>(
            () => new BusinessDatabase(scope.DatabasePath).Initialize());

        Assert.AreEqual("business_database_migration_failed", exception.Code);
        using var preserved = OpenRaw(scope.DatabasePath);
        Assert.AreEqual(1L, Scalar<long>(preserved, "PRAGMA user_version;"));
        Assert.AreEqual(1L, Scalar<long>(preserved, "SELECT count(*) FROM schema_migrations;"));
        Assert.AreEqual("legacy/wrong-key", Scalar<string>(preserved,
            "SELECT object_key FROM file_objects WHERE file_object_id='legacy-invalid';"));
        Assert.AreEqual(0L, Scalar<long>(preserved,
            "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='migration_0002_available_object_guard';"));
    }

    [TestMethod]
    [DataRow("positioning_aux_files")]
    [DataRow("positioning_aux_usage")]
    public void VersionNineLegacyPositioningAuxRecordsBlockUpgradeAndArePreserved(string legacyTable)
    {
        using var scope = new DatabaseScope();
        CreateDatabaseAtVersion(scope.DatabasePath, 9);
        using (var connection = OpenRaw(scope.DatabasePath))
        {
            Execute(connection, "PRAGMA foreign_keys=OFF;");
            if (legacyTable == "positioning_aux_files")
            {
                Execute(connection,
                    """
                    INSERT INTO positioning_aux_files(
                        positioning_aux_file_id,dataset_version_id,file_object_id,auxiliary_type,
                        retention_state,parse_state,quality_state,created_at_utc)
                    VALUES('legacy-positioning','legacy-version','legacy-file','MRK','retained','parsed','passed','t');
                    """);
            }
            else
            {
                Execute(connection,
                    """
                    INSERT INTO projects(project_id,name,spatial_configuration_state,lifecycle_state,created_at_utc,updated_at_utc)
                    VALUES('legacy-project','Legacy','pending','active','t','t');
                    INSERT INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
                    VALUES('legacy-dataset','legacy-project','Legacy','active','t','t');
                    INSERT INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc)
                    VALUES('legacy-version','legacy-dataset',1,'draft','dji_supported','not_run','t');
                    INSERT INTO file_objects(file_object_id,object_kind,hash_algorithm,content_hash,byte_length,object_key,storage_state,created_at_utc,available_at_utc)
                    VALUES('legacy-file','positioning_aux','sha256','eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',1,'sha256/ee/eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee','available','t','t');
                    INSERT INTO positioning_aux_files(
                        positioning_aux_file_id,dataset_version_id,file_object_id,auxiliary_type,
                        retention_state,parse_state,quality_state,created_at_utc)
                    VALUES('legacy-positioning','legacy-version','legacy-file','MRK','retained','parsed','passed','t');
                    INSERT INTO processing_jobs(
                        processing_job_id,project_id,dataset_version_id,job_type,requested_outputs_json,
                        parameter_profile,parameter_schema_version,parameters_json,parameter_sha256,
                        lifecycle_state,recovery_state,created_at_utc,submitted_at_utc)
                    VALUES(
                        'legacy-job','legacy-project','legacy-version','photogrammetry','[]',
                        'standard','v1','{}','ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff',
                        'succeeded','not_applicable','t','t');
                    INSERT INTO job_executions(
                        job_execution_id,processing_job_id,attempt_number,execution_mode,worker_type,
                        worker_version,parameter_sha256,lifecycle_state,checkpoint_compatibility_state)
                    VALUES(
                        'legacy-execution','legacy-job',1,'full','photogrammetry','v1',
                        'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff','succeeded','unavailable');
                    INSERT INTO positioning_aux_usage(
                        positioning_aux_usage_id,positioning_aux_file_id,job_execution_id,usage_state,evidence_json,recorded_at_utc)
                    VALUES('legacy-usage','legacy-positioning','legacy-execution','used','{"legacy":true}','t');
                    """);
            }
        }

        var exception = Assert.Throws<BusinessDatabaseException>(
            () => new BusinessDatabase(scope.DatabasePath).Initialize());

        Assert.AreEqual("business_database_migration_failed", exception.Code);
        using var preserved = OpenRaw(scope.DatabasePath);
        Assert.AreEqual(9L, Scalar<long>(preserved, "PRAGMA user_version;"));
        Assert.AreEqual(9L, Scalar<long>(preserved, "SELECT count(*) FROM schema_migrations;"));
        Assert.AreEqual(1L, Scalar<long>(preserved, $"SELECT count(*) FROM {legacyTable};"));
        Assert.AreEqual(0L, Scalar<long>(preserved,
            "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='positioning_aux_import_runs';"));
    }

    [TestMethod]
    public void PositioningAuxImportRunRequiresCompletedDjiSupportedSourceGate()
    {
        using var scope = new DatabaseScope();
        var database = new BusinessDatabase(scope.DatabasePath);
        database.Initialize();
        using var connection = database.OpenConnection();
        Execute(connection,
            """
            INSERT INTO projects(project_id,name,spatial_configuration_state,lifecycle_state,created_at_utc,updated_at_utc)
            VALUES('gate-project','Gate','pending','active','t','t');
            INSERT INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
            VALUES('gate-dataset','gate-project','Gate','active','t','t');
            INSERT INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc)
            VALUES('gate-version','gate-dataset',1,'draft','pending','not_run','t');
            INSERT INTO image_import_sessions(
                import_session_id,dataset_version_id,source_root_key,source_locator_manifest_id,status,
                total_entry_count,created_at_utc,updated_at_utc)
            VALUES(
                'gate-session','gate-version','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                'gate-manifest','awaiting_source_preflight',0,'t','t');
            INSERT INTO source_preflight_runs(
                source_preflight_run_id,import_session_id,dataset_version_id,source_root_key_snapshot,
                source_locator_manifest_id_snapshot,parser_profile,parser_version,policy_version,status,
                total_item_count,image_candidate_count,sidecar_candidate_count,completed_item_count,
                supports_dji_item_count,out_of_scope_item_count,unconfirmed_item_count,conflict_item_count,
                failed_item_count,blocking_image_count,created_at_utc,started_at_utc,updated_at_utc)
            VALUES(
                'gate-preflight','gate-session','gate-version',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                'gate-manifest','source-preflight.v1','1.0.0','dji-source-gate.v1','running',
                0,0,0,0,0,0,0,0,0,0,'t','t','t');
            UPDATE source_preflight_runs
            SET status='completed',decision='unconfirmed',decision_reason_code='insufficient_evidence',
                evidence_summary_json='{"decision":"unconfirmed"}',updated_at_utc='t2',completed_at_utc='t2'
            WHERE source_preflight_run_id='gate-preflight';
            UPDATE dataset_versions
            SET source_eligibility_state='unconfirmed',
                source_evidence_json='{"decision":"unconfirmed"}',
                source_eligibility_run_id='gate-preflight',
                source_eligibility_decided_at_utc='t2'
            WHERE dataset_version_id='gate-version';
            """);

        AssertSqlRejected(connection,
            """
            INSERT INTO positioning_aux_import_runs(
                positioning_aux_import_run_id,import_session_id,dataset_version_id,source_preflight_run_id,
                association_policy_version,parser_profile,parser_version,status,total_item_count,
                created_at_utc,updated_at_utc)
            VALUES(
                'gate-positioning-run','gate-session','gate-version','gate-preflight',
                'positioning-aux-import.v1','cas-positioning-aux.v1','1.0.0','pending',0,'t','t');
            """);
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
        Assert.AreEqual(1L, Scalar<long>(connection, "SELECT count(*) FROM positioning_aux_import_runs WHERE status='completed';"));
        Assert.AreEqual(2L, Scalar<long>(connection, "SELECT count(*) FROM positioning_aux_import_items WHERE status='completed';"));
        Assert.AreEqual(1L, Scalar<long>(connection, "SELECT count(*) FROM positioning_aux_files WHERE auxiliary_type='nav' AND parse_state='unsupported' AND quality_state='not_checked';"));
        Assert.AreEqual(1L, Scalar<long>(connection, "SELECT count(*) FROM positioning_aux_usage WHERE usage_state='used';"));

        AssertSqlRejected(connection,
            "INSERT INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc) VALUES('bad','missing','Bad','active','t','t');");
        AssertSqlRejected(connection,
            "INSERT INTO file_objects(file_object_id,object_kind,hash_algorithm,content_hash,byte_length,object_key,storage_state,created_at_utc) VALUES('bad-key','formal_output','sha256','eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',1,'staging/not-formal','available','t');");
        AssertSqlRejected(connection, "UPDATE projects SET lifecycle_state='invalid' WHERE project_id='project-1';");
        AssertSqlRejected(connection, "UPDATE dataset_versions SET quality_gate_state='passed' WHERE dataset_version_id='dataset-version-1';");
        AssertSqlRejected(connection, "DELETE FROM images WHERE image_id='image-1';");
        AssertSqlRejected(connection, "UPDATE image_frames SET width=3999 WHERE image_frame_id='frame-1';");
        AssertSqlRejected(connection, "DELETE FROM image_frame_lineage WHERE image_frame_lineage_id='lineage-1';");
        AssertSqlRejected(connection, "DELETE FROM file_object_roles WHERE file_object_id='source-file' AND object_role='source_image';");
        AssertSqlRejected(connection, "UPDATE image_inspection_runs SET status='blocked' WHERE inspection_run_id='inspection-1';");
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
        AssertSqlRejected(connection, "UPDATE positioning_aux_import_runs SET status='running' WHERE positioning_aux_import_run_id='positioning-import-run-1';");
        AssertSqlRejected(connection, "UPDATE positioning_aux_import_items SET display_name='changed.mrk' WHERE positioning_aux_import_item_id='positioning-import-item-1';");
        AssertSqlRejected(connection, "UPDATE positioning_aux_files SET parser_version='changed' WHERE positioning_aux_file_id='positioning-1';");
        AssertSqlRejected(connection,
            """
            INSERT INTO positioning_aux_usage(
                positioning_aux_usage_id,positioning_aux_file_id,job_execution_id,usage_state,
                evidence_schema,use_role,content_hash_snapshot,parse_inventory_sha256_snapshot,evidence_json,recorded_at_utc)
            VALUES(
                'positioning-usage-unsupported','positioning-nav-1','execution-1','used',
                'positioning-aux-usage.v1','positioning_aux',
                '9999999999999999999999999999999999999999999999999999999999999999',
                NULL,'{"records":1}','2026-08-21T01:00:00Z');
            """);
        AssertSqlRejected(connection, "UPDATE positioning_aux_usage SET usage_state='rejected' WHERE positioning_aux_usage_id='positioning-usage-1';");
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

    private static void CreateVersionOneDatabase(
        string databasePath,
        Action<SqliteConnection>? seed = null)
    {
        var assembly = typeof(BusinessDatabase).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.Business.0001_initial.sql", StringComparison.Ordinal));
        string migrationSql;
        using (var stream = assembly.GetManifestResourceStream(resourceName)!)
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            migrationSql = reader.ReadToEnd();
        }

        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(migrationSql))).ToLowerInvariant();
        using var connection = OpenRaw(databasePath);
        Execute(connection,
            "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY,name TEXT NOT NULL,sql_sha256 TEXT NOT NULL CHECK(length(sql_sha256)=64),applied_at_utc TEXT NOT NULL);");
        Execute(connection, migrationSql);
        using (var register = connection.CreateCommand())
        {
            register.CommandText =
                "INSERT INTO schema_migrations(version,name,sql_sha256,applied_at_utc) VALUES(1,'0001_initial.sql',$checksum,'2026-08-21T00:00:00Z');";
            register.Parameters.AddWithValue("$checksum", checksum);
            register.ExecuteNonQuery();
        }

        Execute(connection, "PRAGMA user_version = 1;");
        seed?.Invoke(connection);
    }

    private static void CreateDatabaseAtVersion(string databasePath, int version)
    {
        var assembly = typeof(BusinessDatabase).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Migrations.Business.", StringComparison.Ordinal) &&
                           name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Take(version)
            .ToArray();
        Assert.HasCount(version, resources);

        using var connection = OpenRaw(databasePath);
        Execute(connection,
            "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY,name TEXT NOT NULL,sql_sha256 TEXT NOT NULL CHECK(length(sql_sha256)=64),applied_at_utc TEXT NOT NULL);");
        for (var index = 0; index < resources.Length; index++)
        {
            var resource = resources[index];
            string sql;
            using (var stream = assembly.GetManifestResourceStream(resource)!)
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                sql = reader.ReadToEnd();
            }

            Execute(connection, sql);
            var fileName = resource[(resource.LastIndexOf(".Migrations.Business.", StringComparison.Ordinal) + ".Migrations.Business.".Length)..];
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
            using var register = connection.CreateCommand();
            register.CommandText =
                "INSERT INTO schema_migrations(version,name,sql_sha256,applied_at_utc) VALUES($version,$name,$checksum,'2026-08-21T00:00:00Z');";
            register.Parameters.AddWithValue("$version", index + 1);
            register.Parameters.AddWithValue("$name", fileName);
            register.Parameters.AddWithValue("$checksum", checksum);
            register.ExecuteNonQuery();
        }

        Execute(connection, $"PRAGMA user_version={version};");
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
        VALUES('source-file','source_image','sha256','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',100,'image/jpeg','sha256/aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','available','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO file_objects(file_object_id,object_kind,hash_algorithm,content_hash,byte_length,media_type,object_key,storage_state,created_at_utc,available_at_utc)
        VALUES('position-file','positioning_aux','sha256','dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',80,'text/plain','sha256/dd/dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd','available','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO file_objects(file_object_id,object_kind,hash_algorithm,content_hash,byte_length,media_type,object_key,storage_state,created_at_utc,available_at_utc)
        VALUES('position-file-nav','positioning_aux','sha256','9999999999999999999999999999999999999999999999999999999999999999',64,'application/octet-stream','sha256/99/9999999999999999999999999999999999999999999999999999999999999999','available','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc)
        VALUES('dataset-version-1','dataset-1',1,'draft','pending','passed','2026-08-21T00:00:00Z');
        INSERT INTO file_object_roles(file_object_id,object_role,created_at_utc)
        VALUES('source-file','source_image','2026-08-21T00:00:00Z');
        INSERT INTO file_object_roles(file_object_id,object_role,created_at_utc)
        VALUES('source-file','normalized_image_frame','2026-08-21T00:00:00Z');
        INSERT INTO file_object_roles(file_object_id,object_role,created_at_utc)
        VALUES('position-file','positioning_aux','2026-08-21T00:00:00Z');
        INSERT INTO file_object_roles(file_object_id,object_role,created_at_utc)
        VALUES('position-file-nav','positioning_aux','2026-08-21T00:00:00Z');
        INSERT INTO image_import_sessions(
            import_session_id,dataset_version_id,source_root_key,source_locator_manifest_id,status,
            total_entry_count,available_entry_count,created_at_utc,updated_at_utc)
        VALUES(
            'import-session-1','dataset-version-1','eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',
            'manifest-1','awaiting_source_preflight',1,0,'2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO image_import_entries(
            import_entry_id,import_session_id,dataset_version_id,source_entry_key,display_name,sort_index,
            byte_length_snapshot,status,created_at_utc,updated_at_utc)
        VALUES(
            'import-entry-1','import-session-1','dataset-version-1',
            'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff','DJI_0001.JPG',1,
            100,'awaiting_source_preflight','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO source_preflight_runs(
            source_preflight_run_id,import_session_id,dataset_version_id,source_root_key_snapshot,
            source_locator_manifest_id_snapshot,parser_profile,parser_version,policy_version,status,
            total_item_count,image_candidate_count,sidecar_candidate_count,completed_item_count,
            supports_dji_item_count,out_of_scope_item_count,unconfirmed_item_count,conflict_item_count,
            failed_item_count,blocking_image_count,created_at_utc,started_at_utc,updated_at_utc)
        VALUES(
            'source-preflight-1','import-session-1','dataset-version-1',
            'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',
            'manifest-1','source-preflight.v1','1.0.0','dji-source-gate.v1','running',
            3,1,2,3,3,0,0,0,0,0,'2026-08-21T00:00:00Z','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO source_preflight_items(
            source_preflight_item_id,source_preflight_run_id,import_session_id,dataset_version_id,import_entry_id,
            source_entry_key,display_name,sort_index,candidate_kind,format_hint,byte_length_snapshot,
            source_identity_key,status,container_hint,evidence_state,evidence_json,parser_profile,parser_version,
            created_at_utc,updated_at_utc,completed_at_utc)
        VALUES(
            'source-preflight-image-1','source-preflight-1','import-session-1','dataset-version-1','import-entry-1',
            'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff','DJI_0001.JPG',1,'image_candidate','jpg',100,
            'abababababababababababababababababababababababababababababababab','completed','jpeg_hint','supports_dji',
            '{"category":"dji"}','source-preflight.v1','1.0.0',
            '2026-08-21T00:00:00Z','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO source_preflight_items(
            source_preflight_item_id,source_preflight_run_id,import_session_id,dataset_version_id,import_entry_id,
            source_entry_key,display_name,sort_index,candidate_kind,format_hint,byte_length_snapshot,
            source_identity_key,status,container_hint,evidence_state,evidence_json,parser_profile,parser_version,
            created_at_utc,updated_at_utc,completed_at_utc)
        VALUES(
            'source-preflight-sidecar-1','source-preflight-1','import-session-1','dataset-version-1',NULL,
            '1212121212121212121212121212121212121212121212121212121212121212','DJI_0001.MRK',2,
            'positioning_aux_candidate','mrk',80,
            '3434343434343434343434343434343434343434343434343434343434343434','completed','not_image','supports_dji',
            '{"category":"dji_mrk"}','source-preflight.v1','1.0.0',
            '2026-08-21T00:00:00Z','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO source_preflight_items(
            source_preflight_item_id,source_preflight_run_id,import_session_id,dataset_version_id,import_entry_id,
            source_entry_key,display_name,sort_index,candidate_kind,format_hint,byte_length_snapshot,
            source_identity_key,status,container_hint,evidence_state,evidence_json,parser_profile,parser_version,
            created_at_utc,updated_at_utc,completed_at_utc)
        VALUES(
            'source-preflight-sidecar-2','source-preflight-1','import-session-1','dataset-version-1',NULL,
            '5656565656565656565656565656565656565656565656565656565656565656','DJI_0001.NAV',3,
            'positioning_aux_candidate','nav',64,
            '7878787878787878787878787878787878787878787878787878787878787878','completed','not_image','supports_dji',
            '{"category":"rinex_candidate"}','source-preflight.v1','1.0.0',
            '2026-08-21T00:00:00Z','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        UPDATE source_preflight_runs
        SET status='completed',decision='dji_supported',decision_reason_code='dji_evidence_consistent',
            evidence_summary_json='{"decision":"dji_supported"}',
            updated_at_utc='2026-08-21T00:00:01Z',completed_at_utc='2026-08-21T00:00:01Z'
        WHERE source_preflight_run_id='source-preflight-1';
        UPDATE dataset_versions
        SET source_eligibility_state='dji_supported',
            source_evidence_json='{"decision":"dji_supported"}',
            source_eligibility_run_id='source-preflight-1',
            source_eligibility_decided_at_utc='2026-08-21T00:00:01Z'
        WHERE dataset_version_id='dataset-version-1';
        UPDATE image_import_sessions
        SET status='completed',available_entry_count=1,updated_at_utc='2026-08-21T00:00:01Z',
            completed_at_utc='2026-08-21T00:00:01Z'
        WHERE import_session_id='import-session-1';
        UPDATE image_import_entries
        SET status='available',stage_receipt_id='stage-1',
            stage_receipt_sha256='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            stage_receipt_byte_length=100,stage_receipt_created_at_utc='2026-08-21T00:00:00Z',
            expected_content_hash='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            expected_byte_length=100,
            expected_object_key='sha256/aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            file_object_id='source-file',updated_at_utc='2026-08-21T00:00:01Z',
            terminal_at_utc='2026-08-21T00:00:01Z'
        WHERE import_entry_id='import-entry-1';
        INSERT INTO image_inspection_runs(
            inspection_run_id,import_entry_id,dataset_version_id,source_file_object_id,status,
            parser_schema,parser_profile,product_parser,product_parser_version,native_decoder,native_decoder_version,
            main_frame_policy_version,content_container,primary_frame_index,frame_count,
            frame_inventory_json,frame_inventory_sha256,normalization_action,
            normalized_content_sha256,normalized_content_byte_length,normalized_object_key,
            created_at_utc,updated_at_utc)
        VALUES(
            'inspection-1','import-entry-1','dataset-version-1','source-file','recording',
            'qiongtu.image-probe.cas-image.v1','cas-image.v1','qiongtu.cas-image','1.0.0',
            'magick.net-q16-x64','14.16.0','photogrammetry-main-frame.v1','jpeg',0,1,
            '{}','1111111111111111111111111111111111111111111111111111111111111111','reuse_source_object',
            'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',100,
            'sha256/aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
            '2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO images(
            image_id,dataset_version_id,source_file_object_id,normalized_file_object_id,import_source_key,sort_index,
            content_container,primary_frame_index,width,height,manufacturer,camera_model,image_state,metadata_state,created_at_utc,
            import_entry_id,inspection_run_id,parser_schema,parser_profile,product_parser,product_parser_version,
            native_decoder,native_decoder_version,main_frame_policy_version,frame_inventory_sha256)
        VALUES(
            'image-1','dataset-version-1','source-file','source-file',
            'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff',1,'jpeg',0,4000,3000,
            'DJI','FC-test','processing_input','parsed','2026-08-21T00:00:00Z','import-entry-1','inspection-1',
            'qiongtu.image-probe.cas-image.v1','cas-image.v1','qiongtu.cas-image','1.0.0',
            'magick.net-q16-x64','14.16.0','photogrammetry-main-frame.v1',
            '1111111111111111111111111111111111111111111111111111111111111111');
        INSERT INTO image_frames(
            image_frame_id,image_id,frame_index,frame_role,width,height,decode_state,normalized_file_object_id,
            frame_kind,byte_offset,byte_length,bits_per_channel,orientation,effective_width,effective_height,normalization_action)
        VALUES(
            'frame-1','image-1',0,'primary_photogrammetry',4000,3000,'decoded','source-file',
            'jpeg',0,100,8,1,4000,3000,'reuse_source_object');
        INSERT INTO image_frame_lineage(
            image_frame_lineage_id,image_frame_id,source_file_object_id,normalized_file_object_id,source_frame_index,
            normalization_action,parser_schema,parser_profile,product_parser,product_parser_version,native_decoder,native_decoder_version,
            main_frame_policy_version,byte_offset,byte_length,source_content_hash_snapshot,source_byte_length_snapshot,
            normalized_content_hash_snapshot,normalized_byte_length_snapshot,lineage_sha256,created_at_utc)
        VALUES(
            'lineage-1','frame-1','source-file','source-file',0,'reuse_source_object',
            'qiongtu.image-probe.cas-image.v1','cas-image.v1','qiongtu.cas-image','1.0.0','magick.net-q16-x64','14.16.0',
            'photogrammetry-main-frame.v1',0,100,
            'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',100,
            'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',100,
            '2222222222222222222222222222222222222222222222222222222222222222','2026-08-21T00:00:00Z');
        UPDATE image_inspection_runs
        SET status='completed',image_id='image-1',updated_at_utc='2026-08-21T00:00:01Z',completed_at_utc='2026-08-21T00:00:01Z'
        WHERE inspection_run_id='inspection-1';
        INSERT INTO positioning_aux_import_runs(
            positioning_aux_import_run_id,import_session_id,dataset_version_id,source_preflight_run_id,
            association_policy_version,parser_profile,parser_version,status,total_item_count,
            completed_item_count,failed_item_count,created_at_utc,started_at_utc,updated_at_utc)
        VALUES(
            'positioning-import-run-1','import-session-1','dataset-version-1','source-preflight-1',
            'positioning-aux-import.v1','cas-positioning-aux.v1','1.0.0','running',2,0,0,
            '2026-08-21T00:00:00Z','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO positioning_aux_import_items(
            positioning_aux_import_item_id,positioning_aux_import_run_id,source_preflight_item_id,
            source_entry_key,display_name,sort_index,auxiliary_type,byte_length_snapshot,
            source_identity_key,association_item_count,status,stage_id,stage_sha256,
            stage_byte_length,stage_created_at_utc,expected_content_hash,expected_byte_length,
            expected_object_key,file_object_id,created_at_utc,updated_at_utc)
        VALUES(
            'positioning-import-item-1','positioning-import-run-1','source-preflight-sidecar-1',
            '1212121212121212121212121212121212121212121212121212121212121212','DJI_0001.MRK',
            2,'mrk',80,'3434343434343434343434343434343434343434343434343434343434343434',
            1,'publishing','positioning-stage-1',
            'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',80,
            '2026-08-21T00:00:00Z','dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',
            80,'sha256/dd/dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',
            'position-file','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO positioning_aux_files(
            positioning_aux_file_id,dataset_version_id,import_session_id,positioning_aux_import_item_id,
            source_preflight_item_id,file_object_id,auxiliary_type,association_policy_version,
            association_evidence_json,retention_state,parse_state,quality_state,parser_schema,
            parser_profile,parser_name,parser_version,parse_inventory_sha256,parsed_summary_json,
            created_at_utc,updated_at_utc,parsed_at_utc)
        VALUES(
            'positioning-1','dataset-version-1','import-session-1','positioning-import-item-1',
            'source-preflight-sidecar-1','position-file','mrk','positioning-aux-import.v1',
            '{"associatedImages":1}','retained','parsed','passed','qiongtu.image-probe.cas-positioning-aux.v1',
            'cas-positioning-aux.v1','dji-mrk','1.0.0',
            '5555555555555555555555555555555555555555555555555555555555555555',
            '{"recordCount":1}','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        UPDATE positioning_aux_import_items
        SET status='retained',positioning_aux_file_id='positioning-1',updated_at_utc='2026-08-21T00:00:01Z'
        WHERE positioning_aux_import_item_id='positioning-import-item-1';
        UPDATE positioning_aux_import_items
        SET status='completed',updated_at_utc='2026-08-21T00:00:02Z',terminal_at_utc='2026-08-21T00:00:02Z'
        WHERE positioning_aux_import_item_id='positioning-import-item-1';
        INSERT INTO positioning_aux_import_items(
            positioning_aux_import_item_id,positioning_aux_import_run_id,source_preflight_item_id,
            source_entry_key,display_name,sort_index,auxiliary_type,byte_length_snapshot,
            source_identity_key,association_item_count,status,stage_id,stage_sha256,
            stage_byte_length,stage_created_at_utc,expected_content_hash,expected_byte_length,
            expected_object_key,file_object_id,created_at_utc,updated_at_utc)
        VALUES(
            'positioning-import-item-2','positioning-import-run-1','source-preflight-sidecar-2',
            '5656565656565656565656565656565656565656565656565656565656565656','DJI_0001.NAV',
            3,'nav',64,'7878787878787878787878787878787878787878787878787878787878787878',
            1,'publishing','positioning-stage-2',
            '9999999999999999999999999999999999999999999999999999999999999999',64,
            '2026-08-21T00:00:00Z','9999999999999999999999999999999999999999999999999999999999999999',
            64,'sha256/99/9999999999999999999999999999999999999999999999999999999999999999',
            'position-file-nav','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO positioning_aux_files(
            positioning_aux_file_id,dataset_version_id,import_session_id,positioning_aux_import_item_id,
            source_preflight_item_id,file_object_id,auxiliary_type,association_policy_version,
            association_evidence_json,retention_state,parse_state,quality_state,parser_schema,
            parser_profile,parser_name,parser_version,created_at_utc,updated_at_utc)
        VALUES(
            'positioning-nav-1','dataset-version-1','import-session-1','positioning-import-item-2',
            'source-preflight-sidecar-2','position-file-nav','nav','positioning-aux-import.v1',
            '{"associatedImages":1}','retained','unsupported','not_checked',
            'qiongtu.image-probe.cas-positioning-aux.v1','cas-positioning-aux.v1','rinex-candidate','1.0.0',
            '2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        UPDATE positioning_aux_import_items
        SET status='retained',positioning_aux_file_id='positioning-nav-1',updated_at_utc='2026-08-21T00:00:01Z'
        WHERE positioning_aux_import_item_id='positioning-import-item-2';
        UPDATE positioning_aux_import_items
        SET status='completed',updated_at_utc='2026-08-21T00:00:02Z',terminal_at_utc='2026-08-21T00:00:02Z'
        WHERE positioning_aux_import_item_id='positioning-import-item-2';
        UPDATE positioning_aux_import_runs
        SET status='completed',completed_item_count=2,updated_at_utc='2026-08-21T00:00:03Z',
            completed_at_utc='2026-08-21T00:00:03Z'
        WHERE positioning_aux_import_run_id='positioning-import-run-1';
        INSERT INTO processing_jobs(processing_job_id,project_id,dataset_version_id,job_type,requested_outputs_json,parameter_profile,parameter_schema_version,parameters_json,parameter_sha256,lifecycle_state,recovery_state,created_at_utc,submitted_at_utc)
        VALUES('job-1','project-1','dataset-version-1','photogrammetry','["dom"]','standard','v1','{}','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','succeeded','not_applicable','2026-08-21T00:00:00Z','2026-08-21T00:00:00Z');
        INSERT INTO job_executions(job_execution_id,processing_job_id,attempt_number,execution_mode,worker_type,worker_version,engine_name,engine_version,parameter_sha256,lifecycle_state,checkpoint_compatibility_state,started_at_utc,ended_at_utc)
        VALUES('execution-1','job-1',1,'full','photogrammetry','v1','engine','1.0','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','succeeded','unavailable','2026-08-21T00:00:00Z','2026-08-21T01:00:00Z');
        INSERT INTO positioning_aux_usage(
            positioning_aux_usage_id,positioning_aux_file_id,job_execution_id,usage_state,
            evidence_schema,use_role,content_hash_snapshot,parse_inventory_sha256_snapshot,
            evidence_json,recorded_at_utc)
        VALUES(
            'positioning-usage-1','positioning-1','execution-1','used',
            'positioning-aux-usage.v1','positioning_aux',
            'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',
            '5555555555555555555555555555555555555555555555555555555555555555',
            '{"records":1}','2026-08-21T01:00:00Z');
        INSERT INTO file_objects(file_object_id,object_kind,hash_algorithm,content_hash,byte_length,media_type,object_key,storage_state,created_at_utc,available_at_utc)
        VALUES('output-file','formal_output','sha256','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',200,'image/tiff','sha256/bb/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','available','2026-08-21T01:00:00Z','2026-08-21T01:00:00Z');
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
