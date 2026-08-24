using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using global::QiongTu.Control;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class BusinessCatalogTests
{
    [TestMethod]
    public void CreateGetListProjectAndIdempotentReplayPreserveSingleRow()
    {
        using var scope = new CatalogScope();

        var created = scope.Catalog.CreateProject(
            "request-project-1",
            new ProjectCreateParameters(" Pending Project ", "  first flight  ", null));
        var replayed = scope.Catalog.CreateProject(
            "request-project-1",
            new ProjectCreateParameters("Pending Project", "first flight", null));
        var fetched = scope.Catalog.GetProject(new ProjectGetParameters(created.ProjectId));
        var listed = scope.Catalog.ListProjects(new ProjectListParameters(10, null));

        Assert.AreEqual(created.ProjectId, replayed.ProjectId);
        Assert.AreEqual(created.ProjectId, fetched.ProjectId);
        Assert.AreEqual("Pending Project", fetched.Name);
        Assert.AreEqual("first flight", fetched.Description);
        Assert.AreEqual("pending", fetched.SpatialConfigurationStatus);
        Assert.IsNull(fetched.DefaultCrs);
        Assert.IsTrue(listed.Items.Any(item => item.ProjectId == created.ProjectId));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM projects;"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM catalog_mutations WHERE request_id='request-project-1';"));
    }

    [TestMethod]
    public void IdempotencyRejectsReusedRequestIdWithDifferentParametersOrMethod()
    {
        using var scope = new CatalogScope();
        var project = scope.Catalog.CreateProject("request-conflict", new ProjectCreateParameters("Project", null, null));

        var parameterConflict = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.CreateProject("request-conflict", new ProjectCreateParameters("Project changed", null, null)));
        var methodConflict = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.CreateDataset("request-conflict", new DatasetCreateParameters(project.ProjectId, "Dataset", null)));

        Assert.AreEqual("idempotency_conflict", parameterConflict.Code);
        Assert.AreEqual("idempotency_conflict", methodConflict.Code);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM projects;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM datasets;"));
    }

    [TestMethod]
    public void ConservativeCrsRecommendationHandlesSingleUtmAndUnavailableRanges()
    {
        using var scope = new CatalogScope();

        var recommended = scope.Catalog.RecommendCrs(new CrsRecommendParameters(new Wgs84Bounds(114.10, 29.70, 114.20, 29.80)));

        Assert.AreEqual("recommended", recommended.Status);
        Assert.AreEqual("single_wgs84_utm_zone", recommended.ReasonCode);
        Assert.IsNotNull(recommended.SuggestedCrs);
        Assert.AreEqual("EPSG", recommended.SuggestedCrs.Authority);
        Assert.AreEqual("32650", recommended.SuggestedCrs.Code);
        Assert.AreEqual("metre", recommended.SuggestedCrs.HorizontalUnit);
        Assert.AreEqual("unknown", recommended.SuggestedCrs.VerticalReference);
        Assert.AreEqual("east-north", recommended.SuggestedCrs.AxisOrder);

        AssertUnavailable(scope, new Wgs84Bounds(113.90, 29.70, 114.10, 29.80), "crosses_utm_zone");
        AssertUnavailable(scope, new Wgs84Bounds(114.10, -0.10, 114.20, 0.10), "crosses_equator");
        AssertUnavailable(scope, new Wgs84Bounds(179.80, 10.00, -179.80, 10.10), "crosses_antimeridian");
        AssertUnavailable(scope, new Wgs84Bounds(10.00, 84.10, 10.10, 84.20), "outside_utm_latitude");

        var southernAtEquator = scope.Catalog.RecommendCrs(
            new CrsRecommendParameters(new Wgs84Bounds(114.10, -1.00, 114.20, 0.00)));
        Assert.AreEqual("32750", southernAtEquator.SuggestedCrs?.Code);

        var invalid = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.RecommendCrs(new CrsRecommendParameters(new Wgs84Bounds(-181.00, 10.00, 10.00, 10.10))));
        Assert.AreEqual("invalid_wgs84_bounds", invalid.Code);
    }

    [TestMethod]
    public void ConfirmCrsUsesOptimisticConcurrencyAndDoesNotRewriteResultCrs()
    {
        using var scope = new CatalogScope();
        var project = scope.Catalog.CreateProject("request-project-crs", new ProjectCreateParameters("CRS Project", null, null));
        scope.Execute(
            "UPDATE projects SET updated_at_utc='2026-08-22T00:00:00.0000000Z' WHERE project_id=$project_id;",
            ("$project_id", project.ProjectId));
        scope.SeedPublishedResultGraph(project.ProjectId);
        var originalResultCrsId = scope.Scalar<string>("SELECT crs_id FROM results WHERE result_id='result-target';");

        var confirmed = scope.Catalog.ConfirmCrs(
            "request-confirm-crs",
            new ProjectConfirmCrsParameters(
                project.ProjectId,
                "2026-08-22T00:00:00.0000000Z",
                new CrsDefinitionInput("EPSG", "32649", "WGS 84 / UTM zone 49N", null, null, "projected", "metre", "unknown", "east-north")));
        var staleConflict = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.ConfirmCrs(
                "request-confirm-crs-stale",
                new ProjectConfirmCrsParameters(
                    project.ProjectId,
                    "2026-08-22T00:00:00.0000000Z",
                    new CrsDefinitionInput("EPSG", "32650", "WGS 84 / UTM zone 50N", null, null, "projected", "metre", "unknown", "east-north"))));

        Assert.AreEqual("confirmed", confirmed.SpatialConfigurationStatus);
        Assert.IsNotNull(confirmed.DefaultCrs);
        Assert.AreEqual("32649", confirmed.DefaultCrs.Code);
        Assert.AreEqual("project_concurrency_conflict", staleConflict.Code);
        Assert.AreEqual(originalResultCrsId, scope.Scalar<string>("SELECT crs_id FROM results WHERE result_id='result-target';"));
    }

    [TestMethod]
    public void AuthorityCrsWithNullVerticalReferenceRejectsConflictingSnapshot()
    {
        using var scope = new CatalogScope();
        var first = new CrsDefinitionInput(
            "EPSG", "32650", "WGS 84 / UTM zone 50N", null, null,
            "projected", "metre", null, "east-north");
        var conflicting = first with { AxisOrder = "north-east" };

        _ = scope.Catalog.CreateProject(
            "crs-null-vertical-first",
            new ProjectCreateParameters("First", null, first));
        var exception = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.CreateProject(
                "crs-null-vertical-conflict",
                new ProjectCreateParameters("Second", null, conflicting)));

        Assert.AreEqual("crs_identity_conflict", exception.Code);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM crs_definitions;"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM projects;"));
    }

    [TestMethod]
    public void DatasetAndVersionCreationRejectCrossDatasetParentAndSupportsConcurrentVersionNumbers()
    {
        using var scope = new CatalogScope();
        var project = scope.Catalog.CreateProject("request-project-datasets", new ProjectCreateParameters("Dataset Project", null, null));
        var dataset = scope.Catalog.CreateDataset("request-dataset-1", new DatasetCreateParameters(project.ProjectId, "Flight A", null));
        var otherDataset = scope.Catalog.CreateDataset("request-dataset-2", new DatasetCreateParameters(project.ProjectId, "Flight B", null));

        var version1 = scope.Catalog.CreateDatasetVersion("request-version-1", new DatasetVersionCreateParameters(dataset.DatasetId, null));
        var version2 = scope.Catalog.CreateDatasetVersion(
            "request-version-2",
            new DatasetVersionCreateParameters(dataset.DatasetId, version1.DatasetVersionId));
        var otherVersion = scope.Catalog.CreateDatasetVersion("request-version-other", new DatasetVersionCreateParameters(otherDataset.DatasetId, null));
        var mismatch = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.CreateDatasetVersion(
                "request-version-cross-parent",
                new DatasetVersionCreateParameters(dataset.DatasetId, otherVersion.DatasetVersionId)));

        var concurrentVersions = Enumerable.Range(0, 8)
            .Select(index => Task.Run(() => scope.Catalog.CreateDatasetVersion(
                $"request-version-concurrent-{index}",
                new DatasetVersionCreateParameters(dataset.DatasetId, null))))
            .ToArray();
        Task.WaitAll(concurrentVersions);

        var versionNumbers = scope.Catalog.ListDatasetVersions(
                new DatasetVersionListParameters(dataset.DatasetId, 50, null))
            .Items
            .Select(item => item.VersionNumber)
            .Order()
            .ToArray();

        Assert.AreEqual(1, version1.VersionNumber);
        Assert.AreEqual(2, version2.VersionNumber);
        Assert.AreEqual("parent_dataset_version_mismatch", mismatch.Code);
        CollectionAssert.AreEqual(Enumerable.Range(1, 10).ToArray(), versionNumbers);
        Assert.AreEqual(10, versionNumbers.Distinct().Count());
    }

    [TestMethod]
    public void KeysetPaginationDoesNotSkipRowsWithIdenticalTimestamps()
    {
        using var scope = new CatalogScope();
        scope.SeedProjectsWithSameCreatedAt("2026-08-23T00:00:00.0000000Z", 5);

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = scope.Catalog.ListProjects(new ProjectListParameters(2, cursor));
            seen.AddRange(page.Items.Select(item => item.ProjectId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        CollectionAssert.AreEqual(
            new[] { "project-page-5", "project-page-4", "project-page-3", "project-page-2", "project-page-1" },
            seen);
        Assert.AreEqual(seen.Count, seen.Distinct().Count());
    }

    [TestMethod]
    public void ResultListAndLineageReturnBoundedSafeAuthorityChain()
    {
        using var scope = new CatalogScope();
        scope.SeedPublishedResultGraph("project-lineage");

        var list = scope.Catalog.ListResults(new ResultListParameters("project-lineage", null, 10, null));
        var lineage = scope.Catalog.GetResultLineage(new ResultLineageParameters("result-target"));
        var lineageJson = JsonSerializer.Serialize(lineage, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.IsTrue(list.Items.Any(item => item.ResultId == "result-target"));
        Assert.AreEqual("result-target", lineage.Target.ResultId);
        Assert.AreEqual("series-target", lineage.Series.ResultSeriesId);
        Assert.AreEqual("project-lineage", lineage.Project.ProjectId);
        Assert.AreEqual("dataset-lineage", lineage.SourceDatasetVersion.DatasetId);
        Assert.AreEqual("job-lineage", lineage.SourceProcessingJob.ProcessingJobId);
        Assert.AreEqual("execution-lineage", lineage.SourceJobExecution.JobExecutionId);
        Assert.HasCount(1, lineage.DirectDependencies);
        Assert.AreEqual("result-source", lineage.DirectDependencies[0].DependsOnResultId);
        Assert.HasCount(1, lineage.AvailableFiles);
        Assert.AreEqual("sha256/bb/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", lineage.AvailableFiles[0].ObjectKey);
        Assert.AreEqual("quality-report-final", lineage.FinalQualityReports.Single().QualityReportId);
        Assert.DoesNotContain(":\\", lineageJson);
        Assert.DoesNotContain("token", lineageJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("staging", lineageJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quarantine", lineageJson, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void StructuredErrorsCoverNotFoundInvalidCursorAndInvalidPageSize()
    {
        using var scope = new CatalogScope();
        var notFound = Assert.Throws<BusinessCatalogException>(() => scope.Catalog.GetResultLineage(new ResultLineageParameters("missing-result")));
        var invalidCursor = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.ListProjects(new ProjectListParameters(10, "not-a-valid-cursor")));
        var invalidPageSize = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.ListProjects(new ProjectListParameters(0, null)));
        var oversizedPage = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.ListProjects(new ProjectListParameters(BusinessCatalog.MaximumPageSize + 1, null)));
        var invalidRequestId = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.CreateProject("bad\nrequest", new ProjectCreateParameters("Project", null, null)));

        Assert.AreEqual("result_not_found", notFound.Code);
        Assert.AreEqual("invalid_cursor", invalidCursor.Code);
        Assert.AreEqual("invalid_page_size", invalidPageSize.Code);
        Assert.AreEqual("invalid_page_size", oversizedPage.Code);
        Assert.AreEqual("invalid_parameters", invalidRequestId.Code);
    }

    [TestMethod]
    public void VersionTwoDatabaseUpgradesToVersionThreeWithCatalogMutationTableAndCrsIdentityIndex()
    {
        using var scope = new DatabaseOnlyScope();
        CreateDatabaseAtVersion(scope.DatabasePath, 2);

        new BusinessDatabase(scope.DatabasePath).Initialize();

        using var connection = OpenRaw(scope.DatabasePath);
        Assert.AreEqual(3L, Scalar<long>(connection, "PRAGMA user_version;"));
        Assert.AreEqual(1L, Scalar<long>(connection, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='catalog_mutations';"));
        Assert.AreEqual(1L, Scalar<long>(connection, "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='ux_crs_definitions_identity_expr';"));
        Execute(connection,
            """
            INSERT INTO crs_definitions(crs_id,authority,code,name,horizontal_unit,vertical_reference,axis_order,created_at_utc)
            VALUES('crs-upgrade-1','EPSG','32648','WGS 84 / UTM zone 48N','metre',NULL,'east-north','2026-08-23T00:00:00Z');
            """);
        Assert.Throws<SqliteException>(() => Execute(connection,
            """
            INSERT INTO crs_definitions(crs_id,authority,code,name,horizontal_unit,vertical_reference,axis_order,created_at_utc)
            VALUES('crs-upgrade-2','EPSG','32648','Duplicate display name is still same identity','metre',NULL,'east-north','2026-08-23T00:00:01Z');
            """));
    }

    [TestMethod]
    public void AmbiguousAuthorityCrsBlocksVersionThreeUpgradeWithoutChangingVersionTwoData()
    {
        using var scope = new DatabaseOnlyScope();
        CreateDatabaseAtVersion(scope.DatabasePath, 2);
        using (var connection = OpenRaw(scope.DatabasePath))
        {
            Execute(connection,
                """
                INSERT INTO crs_definitions(crs_id,authority,code,name,horizontal_unit,vertical_reference,axis_order,created_at_utc)
                VALUES('crs-ambiguous-1','EPSG','32650','First','metre',NULL,'east-north','2026-08-23T00:00:00Z');
                INSERT INTO crs_definitions(crs_id,authority,code,name,horizontal_unit,vertical_reference,axis_order,created_at_utc)
                VALUES('crs-ambiguous-2','EPSG','32650','Second','metre',NULL,'north-east','2026-08-23T00:00:01Z');
                """);
        }

        var exception = Assert.Throws<BusinessDatabaseException>(() =>
            new BusinessDatabase(scope.DatabasePath).Initialize());

        Assert.AreEqual("business_database_migration_failed", exception.Code);
        using var preserved = OpenRaw(scope.DatabasePath);
        Assert.AreEqual(2L, Scalar<long>(preserved, "PRAGMA user_version;"));
        Assert.AreEqual(2L, Scalar<long>(preserved, "SELECT count(*) FROM schema_migrations;"));
        Assert.AreEqual(2L, Scalar<long>(preserved, "SELECT count(*) FROM crs_definitions WHERE authority='EPSG' AND code='32650';"));
        Assert.AreEqual(0L, Scalar<long>(preserved,
            "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='ux_crs_definitions_authority_identity_expr';"));
    }

    private static void AssertUnavailable(CatalogScope scope, Wgs84Bounds bounds, string reasonCode)
    {
        var recommendation = scope.Catalog.RecommendCrs(new CrsRecommendParameters(bounds));
        Assert.AreEqual("not-recommended", recommendation.Status);
        Assert.AreEqual(reasonCode, recommendation.ReasonCode);
        Assert.IsNull(recommendation.SuggestedCrs);
    }

    private static void CreateDatabaseAtVersion(string databasePath, int version)
    {
        using var connection = OpenRaw(databasePath);
        Execute(connection,
            """
            CREATE TABLE schema_migrations(
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                sql_sha256 TEXT NOT NULL CHECK(length(sql_sha256)=64),
                applied_at_utc TEXT NOT NULL
            );
            """);

        for (var migrationVersion = 1; migrationVersion <= version; migrationVersion++)
        {
            var (name, sql) = ReadMigration(migrationVersion);
            Execute(connection, sql);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO schema_migrations(version,name,sql_sha256,applied_at_utc)
                VALUES($version,$name,$checksum,'2026-08-23T00:00:00Z');
                """;
            command.Parameters.AddWithValue("$version", migrationVersion);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$checksum", Sha256Hex(sql));
            command.ExecuteNonQuery();
        }

        Execute(connection, $"PRAGMA user_version = {version};");
    }

    private static (string Name, string Sql) ReadMigration(int version)
    {
        var assembly = typeof(BusinessDatabase).Assembly;
        var prefix = $"{version:0000}_";
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.Contains(".Migrations.Business.", StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.Ordinal)
                && name[(name.LastIndexOf(".Migrations.Business.", StringComparison.Ordinal) + ".Migrations.Business.".Length)..].StartsWith(prefix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var fileName = resourceName[(resourceName.LastIndexOf(".Migrations.Business.", StringComparison.Ordinal) + ".Migrations.Business.".Length)..];
        return (fileName, reader.ReadToEnd());
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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

    private sealed class CatalogScope : DatabaseOnlyScope
    {
        private readonly BusinessDatabase _database;

        public CatalogScope()
        {
            _database = new BusinessDatabase(DatabasePath);
            _database.Initialize();
            Catalog = new BusinessCatalog(_database);
        }

        public BusinessCatalog Catalog { get; }

        public void Execute(string sql, params (string Name, object? Value)[] parameters)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            command.ExecuteNonQuery();
        }

        public T Scalar<T>(string sql)
        {
            using var connection = _database.OpenConnection();
            return BusinessCatalogTests.Scalar<T>(connection, sql);
        }

        public void SeedProjectsWithSameCreatedAt(string createdAtUtc, int count)
        {
            using var connection = _database.OpenConnection();
            for (var index = 1; index <= count; index++)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO projects(
                        project_id, name, description, default_crs_id, suggested_crs_id,
                        spatial_configuration_state, lifecycle_state, created_at_utc, updated_at_utc)
                    VALUES($project_id, $name, NULL, NULL, NULL, 'pending', 'active', $created_at_utc, $created_at_utc);
                    """;
                command.Parameters.AddWithValue("$project_id", $"project-page-{index}");
                command.Parameters.AddWithValue("$name", $"Project {index}");
                command.Parameters.AddWithValue("$created_at_utc", createdAtUtc);
                command.ExecuteNonQuery();
            }
        }

        public void SeedPublishedResultGraph(string projectId)
        {
            using var connection = _database.OpenConnection();
            BusinessCatalogTests.Execute(connection, string.Format(System.Globalization.CultureInfo.InvariantCulture, PublishedResultGraphSql, projectId));
        }
    }

    private class DatabaseOnlyScope : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"qiongtu-catalog-tests-{Guid.NewGuid():N}");

        public DatabaseOnlyScope()
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

    private const string PublishedResultGraphSql =
        """
        INSERT OR IGNORE INTO crs_definitions(crs_id,authority,code,name,horizontal_unit,vertical_reference,axis_order,created_at_utc)
        VALUES('crs-lineage','EPSG','32648','WGS 84 / UTM zone 48N','metre','unknown','east-north','2026-08-23T00:00:00Z');

        INSERT OR IGNORE INTO projects(project_id,name,default_crs_id,spatial_configuration_state,lifecycle_state,created_at_utc,updated_at_utc)
        VALUES('{0}','Lineage Project','crs-lineage','confirmed','active','2026-08-23T00:00:00Z','2026-08-23T00:00:00Z');

        INSERT OR IGNORE INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
        VALUES('dataset-lineage','{0}','Lineage Dataset','active','2026-08-23T00:00:01Z','2026-08-23T00:00:01Z');

        INSERT OR IGNORE INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc)
        VALUES('dataset-version-lineage','dataset-lineage',1,'draft','dji_supported','passed','2026-08-23T00:00:02Z');

        INSERT OR IGNORE INTO processing_jobs(processing_job_id,project_id,dataset_version_id,job_type,requested_outputs_json,parameter_profile,parameter_schema_version,parameters_json,parameter_sha256,lifecycle_state,recovery_state,created_at_utc,submitted_at_utc,started_at_utc,ended_at_utc)
        VALUES('job-lineage','{0}','dataset-version-lineage','photogrammetry','["dom"]','standard','v1','{{}}','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','succeeded','not_applicable','2026-08-23T00:00:03Z','2026-08-23T00:00:03Z','2026-08-23T00:00:04Z','2026-08-23T00:10:00Z');

        INSERT OR IGNORE INTO job_executions(job_execution_id,processing_job_id,attempt_number,execution_mode,worker_type,worker_version,engine_name,engine_version,parameter_sha256,lifecycle_state,checkpoint_compatibility_state,started_at_utc,ended_at_utc)
        VALUES('execution-lineage','job-lineage',1,'full','photogrammetry','v1','engine','1.0','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','succeeded','unavailable','2026-08-23T00:00:04Z','2026-08-23T00:10:00Z');

        INSERT OR IGNORE INTO result_series(result_series_id,project_id,dataset_version_id,series_kind,name,created_at_utc)
        VALUES('series-source','{0}','dataset-version-lineage','aerotriangulation','AT','2026-08-23T00:10:00Z');

        INSERT OR IGNORE INTO result_series(result_series_id,project_id,dataset_version_id,series_kind,name,parent_series_id,created_at_utc)
        VALUES('series-target','{0}','dataset-version-lineage','dom','DOM','series-source','2026-08-23T00:11:00Z');

        INSERT OR IGNORE INTO results(result_id,result_series_id,version_number,source_dataset_version_id,source_processing_job_id,source_job_execution_id,result_kind,lifecycle_state,crs_id,unit,bounds_json,parameter_sha256,accuracy_level,created_at_utc)
        VALUES('result-source','series-source',1,'dataset-version-lineage','job-lineage','execution-lineage','aerotriangulation','candidate','crs-lineage','metre','{{"westLongitude":114.1,"southLatitude":29.7,"eastLongitude":114.2,"northLatitude":29.8}}','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','georeferenced_visualization','2026-08-23T00:10:01Z');

        INSERT OR IGNORE INTO results(result_id,result_series_id,version_number,source_dataset_version_id,source_processing_job_id,source_job_execution_id,source_result_id,result_kind,lifecycle_state,crs_id,unit,bounds_json,parameter_sha256,accuracy_level,created_at_utc)
        VALUES('result-target','series-target',1,'dataset-version-lineage','job-lineage','execution-lineage','result-source','dom','candidate','crs-lineage','metre','{{"westLongitude":114.1,"southLatitude":29.7,"eastLongitude":114.2,"northLatitude":29.8}}','cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','georeferenced_visualization','2026-08-23T00:11:01Z');

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
        VALUES('quality-report-final','result_validation',1,'draft','result-target','execution-lineage','v1','none','{{"blocking":0,"warning":0,"info":1}}','2026-08-23T00:11:03Z');

        INSERT OR IGNORE INTO quality_findings(quality_finding_id,quality_report_id,sort_index,check_code,severity,conclusion)
        VALUES('quality-finding-final','quality-report-final',0,'result.readable','info','passed');

        UPDATE quality_reports SET lifecycle_state='final', finalized_at_utc='2026-08-23T00:11:04Z' WHERE quality_report_id='quality-report-final';
        UPDATE results SET lifecycle_state='published', published_at_utc='2026-08-23T00:12:00Z' WHERE result_id='result-target';
        """;
}
