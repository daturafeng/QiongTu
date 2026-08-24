using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control;

public sealed class BusinessCatalog
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 50;
    public const int MaximumBoundedChildren = 50;
    public const int MaximumCatalogPayloadBytes = NamedPipeControlServer.MaximumResponseBytes - (4 * 1024);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly BusinessDatabase _database;
    private readonly int _maximumResponseBytes;

    public BusinessCatalog(BusinessDatabase database, int maximumResponseBytes = MaximumCatalogPayloadBytes)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        if (maximumResponseBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        }

        _maximumResponseBytes = maximumResponseBytes;
    }

    public Project CreateProject(string requestId, ProjectCreateParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var normalized = parameters with
        {
            Name = NormalizeName(parameters.Name, nameof(parameters.Name)),
            Description = NormalizeOptionalText(parameters.Description, nameof(parameters.Description), 2_000),
            DefaultCrs = parameters.DefaultCrs is null ? null : NormalizeCrsInput(parameters.DefaultCrs)
        };

        return ExecuteIdempotent(requestId, ControlMethods.ProjectCreate, normalized, (connection, transaction) =>
        {
            var now = UtcNowText();
            var projectId = NewId("project");
            var crsId = normalized.DefaultCrs is null ? null : InsertOrReuseCrs(connection, transaction, normalized.DefaultCrs);
            using var command = Command(
                connection,
                transaction,
                """
                INSERT INTO projects(
                    project_id, name, description, default_crs_id, suggested_crs_id,
                    spatial_configuration_state, lifecycle_state, created_at_utc, updated_at_utc)
                VALUES(
                    $project_id, $name, $description, $default_crs_id, NULL,
                    $spatial_state, 'active', $created_at_utc, $updated_at_utc);
                """);
            Add(command, "$project_id", projectId);
            Add(command, "$name", normalized.Name);
            Add(command, "$description", normalized.Description);
            Add(command, "$default_crs_id", crsId);
            Add(command, "$spatial_state", crsId is null ? "pending" : "confirmed");
            Add(command, "$created_at_utc", now);
            Add(command, "$updated_at_utc", now);
            command.ExecuteNonQuery();
            return ReadProject(connection, transaction, projectId);
        });
    }

    public PageResult<Project> ListProjects(ProjectListParameters? parameters = null)
    {
        var page = NormalizePage(parameters?.PageSize, parameters?.Cursor);
        var cursor = DecodeCursor(page.Cursor, ControlMethods.ProjectList, string.Empty);
        using var connection = _database.OpenConnection();
        using var command = Command(
            connection,
            null,
            """
            SELECT p.project_id, p.created_at_utc
            FROM projects p
            WHERE ($cursor_created_at_utc IS NULL OR
                   p.created_at_utc < $cursor_created_at_utc OR
                   (p.created_at_utc = $cursor_created_at_utc AND p.project_id < $cursor_id))
            ORDER BY p.created_at_utc DESC, p.project_id DESC
            LIMIT $limit;
            """);
        Add(command, "$cursor_created_at_utc", cursor?.CreatedAtUtc);
        Add(command, "$cursor_id", cursor?.Id);
        Add(command, "$limit", page.PageSize + 1);
        var identities = new List<(string Id, string CreatedAtUtc)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                identities.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var rows = new List<(Project Item, string CreatedAtUtc, string Id)>();
        foreach (var identity in identities)
        {
            var project = ReadProject(connection, null, identity.Id);
            rows.Add((project, identity.CreatedAtUtc, project.ProjectId));
        }

        var result = ToPage(rows, page.PageSize, ControlMethods.ProjectList, string.Empty);
        EnsureResponseWithinLimit(result);
        return result;
    }

    public Project GetProject(ProjectGetParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        using var connection = _database.OpenConnection();
        var result = ReadProject(connection, null, NormalizeId(parameters.ProjectId, nameof(parameters.ProjectId)));
        EnsureResponseWithinLimit(result);
        return result;
    }

    public CrsRecommendation RecommendCrs(CrsRecommendParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var result = parameters.Bounds is null
            ? new CrsRecommendation("not-recommended", null, null, "insufficient_location_metadata")
            : RecommendCrs(parameters.Bounds);
        EnsureResponseWithinLimit(result);
        return result;
    }

    public Project ConfirmCrs(string requestId, ProjectConfirmCrsParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var normalized = parameters with
        {
            ProjectId = NormalizeId(parameters.ProjectId, nameof(parameters.ProjectId)),
            ExpectedUpdatedAtUtc = NormalizeRequiredText(parameters.ExpectedUpdatedAtUtc, nameof(parameters.ExpectedUpdatedAtUtc), 64),
            Crs = NormalizeCrsInput(parameters.Crs)
        };

        return ExecuteIdempotent(requestId, ControlMethods.ProjectConfirmCrs, normalized, (connection, transaction) =>
        {
            var currentUpdatedAt = ScalarString(connection, transaction, "SELECT updated_at_utc FROM projects WHERE project_id = $project_id;", ("$project_id", normalized.ProjectId));
            if (currentUpdatedAt is null)
            {
                throw new BusinessCatalogException("project_not_found", "The project was not found.");
            }

            if (!string.Equals(currentUpdatedAt, normalized.ExpectedUpdatedAtUtc, StringComparison.Ordinal))
            {
                throw new BusinessCatalogException("project_concurrency_conflict", "The project has changed since the caller last read it.");
            }

            var crsId = InsertOrReuseCrs(connection, transaction, normalized.Crs);
            var updatedAt = UtcNowText();
            using var update = Command(
                connection,
                transaction,
                """
                UPDATE projects
                SET default_crs_id = $default_crs_id,
                    spatial_configuration_state = 'confirmed',
                    updated_at_utc = $updated_at_utc
                WHERE project_id = $project_id AND updated_at_utc = $expected_updated_at_utc;
                """);
            Add(update, "$default_crs_id", crsId);
            Add(update, "$updated_at_utc", updatedAt);
            Add(update, "$project_id", normalized.ProjectId);
            Add(update, "$expected_updated_at_utc", normalized.ExpectedUpdatedAtUtc);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new BusinessCatalogException("project_concurrency_conflict", "The project CRS confirmation could not be applied because the project changed.");
            }

            return ReadProject(connection, transaction, normalized.ProjectId);
        });
    }

    public Dataset CreateDataset(string requestId, DatasetCreateParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var normalized = parameters with
        {
            ProjectId = NormalizeId(parameters.ProjectId, nameof(parameters.ProjectId)),
            Name = NormalizeName(parameters.Name, nameof(parameters.Name)),
            Description = NormalizeOptionalText(parameters.Description, nameof(parameters.Description), 2_000)
        };

        return ExecuteIdempotent(requestId, ControlMethods.DatasetCreate, normalized, (connection, transaction) =>
        {
            EnsureExists(connection, transaction, "projects", "project_id", normalized.ProjectId, "project_not_found", "The project was not found.");
            var now = UtcNowText();
            var datasetId = NewId("dataset");
            using var command = Command(
                connection,
                transaction,
                """
                INSERT INTO datasets(dataset_id, project_id, name, description, lifecycle_state, created_at_utc, updated_at_utc)
                VALUES($dataset_id, $project_id, $name, $description, 'active', $created_at_utc, $updated_at_utc);
                """);
            Add(command, "$dataset_id", datasetId);
            Add(command, "$project_id", normalized.ProjectId);
            Add(command, "$name", normalized.Name);
            Add(command, "$description", normalized.Description);
            Add(command, "$created_at_utc", now);
            Add(command, "$updated_at_utc", now);
            command.ExecuteNonQuery();
            return ReadDataset(connection, transaction, datasetId);
        });
    }

    public DatasetVersion CreateDatasetVersion(string requestId, DatasetVersionCreateParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var normalized = parameters with
        {
            DatasetId = NormalizeId(parameters.DatasetId, nameof(parameters.DatasetId)),
            ParentVersionId = NormalizeOptionalId(parameters.ParentVersionId, nameof(parameters.ParentVersionId))
        };

        return ExecuteIdempotent(requestId, ControlMethods.DatasetVersionCreate, normalized, (connection, transaction) =>
        {
            EnsureExists(connection, transaction, "datasets", "dataset_id", normalized.DatasetId, "dataset_not_found", "The dataset was not found.");
            if (normalized.ParentVersionId is not null)
            {
                var parentDatasetId = ScalarString(
                    connection,
                    transaction,
                    "SELECT dataset_id FROM dataset_versions WHERE dataset_version_id = $dataset_version_id;",
                    ("$dataset_version_id", normalized.ParentVersionId));
                if (parentDatasetId is null)
                {
                    throw new BusinessCatalogException("parent_dataset_version_not_found", "The parent dataset version was not found.");
                }

                if (!string.Equals(parentDatasetId, normalized.DatasetId, StringComparison.Ordinal))
                {
                    throw new BusinessCatalogException("parent_dataset_version_mismatch", "The parent version must belong to the same dataset.");
                }
            }

            var nextVersionNumber = Convert.ToInt32(Scalar(
                connection,
                transaction,
                "SELECT COALESCE(MAX(version_number), 0) + 1 FROM dataset_versions WHERE dataset_id = $dataset_id;",
                ("$dataset_id", normalized.DatasetId)), CultureInfo.InvariantCulture);
            var now = UtcNowText();
            var datasetVersionId = NewId("dataset-version");
            using var command = Command(
                connection,
                transaction,
                """
                INSERT INTO dataset_versions(
                    dataset_version_id, dataset_id, version_number, parent_version_id,
                    lifecycle_state, source_eligibility_state, quality_gate_state, created_at_utc)
                VALUES(
                    $dataset_version_id, $dataset_id, $version_number, $parent_version_id,
                    'draft', 'pending', 'not_run', $created_at_utc);
                """);
            Add(command, "$dataset_version_id", datasetVersionId);
            Add(command, "$dataset_id", normalized.DatasetId);
            Add(command, "$version_number", nextVersionNumber);
            Add(command, "$parent_version_id", normalized.ParentVersionId);
            Add(command, "$created_at_utc", now);
            command.ExecuteNonQuery();
            return ReadDatasetVersion(connection, transaction, datasetVersionId);
        });
    }

    public PageResult<DatasetVersion> ListDatasetVersions(DatasetVersionListParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var datasetId = NormalizeId(parameters.DatasetId, nameof(parameters.DatasetId));
        var page = NormalizePage(parameters.PageSize, parameters.Cursor);
        var cursor = DecodeCursor(page.Cursor, ControlMethods.DatasetVersionList, datasetId);
        using var connection = _database.OpenConnection();
        using var command = Command(
            connection,
            null,
            """
            SELECT dv.dataset_version_id, dv.created_at_utc
            FROM dataset_versions dv
            WHERE dv.dataset_id = $dataset_id
              AND ($cursor_created_at_utc IS NULL OR
                   dv.created_at_utc < $cursor_created_at_utc OR
                   (dv.created_at_utc = $cursor_created_at_utc AND dv.dataset_version_id < $cursor_id))
            ORDER BY dv.created_at_utc DESC, dv.dataset_version_id DESC
            LIMIT $limit;
            """);
        Add(command, "$dataset_id", datasetId);
        Add(command, "$cursor_created_at_utc", cursor?.CreatedAtUtc);
        Add(command, "$cursor_id", cursor?.Id);
        Add(command, "$limit", page.PageSize + 1);
        var identities = new List<(string Id, string CreatedAtUtc)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                identities.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var rows = new List<(DatasetVersion Item, string CreatedAtUtc, string Id)>();
        foreach (var identity in identities)
        {
            var version = ReadDatasetVersion(connection, null, identity.Id);
            rows.Add((version, identity.CreatedAtUtc, version.DatasetVersionId));
        }

        var result = ToPage(rows, page.PageSize, ControlMethods.DatasetVersionList, datasetId);
        EnsureResponseWithinLimit(result);
        return result;
    }

    public DatasetVersion GetDatasetVersion(DatasetVersionGetParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        using var connection = _database.OpenConnection();
        var result = ReadDatasetVersion(connection, null, NormalizeId(parameters.DatasetVersionId, nameof(parameters.DatasetVersionId)));
        EnsureResponseWithinLimit(result);
        return result;
    }

    public PageResult<ResultSummary> ListResults(ResultListParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var projectId = NormalizeOptionalId(parameters.ProjectId, nameof(parameters.ProjectId));
        var datasetVersionId = NormalizeOptionalId(parameters.DatasetVersionId, nameof(parameters.DatasetVersionId));
        if (projectId is null && datasetVersionId is null)
        {
            throw new BusinessCatalogException("result_filter_required", "A projectId or datasetVersionId filter is required.");
        }

        var page = NormalizePage(parameters.PageSize, parameters.Cursor);
        var cursorScope = $"{projectId ?? string.Empty}\n{datasetVersionId ?? string.Empty}";
        var cursor = DecodeCursor(page.Cursor, ControlMethods.ResultList, cursorScope);
        using var connection = _database.OpenConnection();
        using var command = Command(
            connection,
            null,
            """
            SELECT r.result_id, r.created_at_utc
            FROM results r
            JOIN result_series s ON s.result_series_id = r.result_series_id
            WHERE r.lifecycle_state <> 'deleted'
              AND ($project_id IS NULL OR s.project_id = $project_id)
              AND ($dataset_version_id IS NULL OR r.source_dataset_version_id = $dataset_version_id)
              AND ($cursor_created_at_utc IS NULL OR
                   r.created_at_utc < $cursor_created_at_utc OR
                   (r.created_at_utc = $cursor_created_at_utc AND r.result_id < $cursor_id))
            ORDER BY r.created_at_utc DESC, r.result_id DESC
            LIMIT $limit;
            """);
        Add(command, "$project_id", projectId);
        Add(command, "$dataset_version_id", datasetVersionId);
        Add(command, "$cursor_created_at_utc", cursor?.CreatedAtUtc);
        Add(command, "$cursor_id", cursor?.Id);
        Add(command, "$limit", page.PageSize + 1);
        var identities = new List<(string Id, string CreatedAtUtc)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                identities.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var rows = new List<(ResultSummary Item, string CreatedAtUtc, string Id)>();
        foreach (var identity in identities)
        {
            var result = ReadResult(connection, null, identity.Id);
            rows.Add((result, identity.CreatedAtUtc, result.ResultId));
        }

        var pageResult = ToPage(rows, page.PageSize, ControlMethods.ResultList, cursorScope);
        EnsureResponseWithinLimit(pageResult);
        return pageResult;
    }

    public ResultLineage GetResultLineage(ResultLineageParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var resultId = NormalizeId(parameters.ResultId, nameof(parameters.ResultId));
        using var connection = _database.OpenConnection();
        var result = ReadResult(connection, null, resultId);
        var series = ReadResultSeries(connection, null, result.ResultSeriesId);
        var lineage = new ResultLineage(
            result,
            series,
            ReadProject(connection, null, series.ProjectId),
            ReadDatasetVersion(connection, null, result.SourceDatasetVersionId),
            ReadProcessingJob(connection, null, result.SourceProcessingJobId),
            ReadJobExecution(connection, null, result.SourceJobExecutionId),
            ReadResultDependencies(connection, null, resultId),
            ReadResultFiles(connection, null, resultId),
            ReadFinalQualityReports(connection, null, resultId));
        EnsureResponseWithinLimit(lineage);
        return lineage;
    }

    private T ExecuteIdempotent<T>(string requestId, string method, object normalizedParameters, Func<SqliteConnection, SqliteTransaction, T> operation)
    {
        requestId = NormalizeIdentifier(requestId, "requestId", 128);
        var parameterHash = Sha256Hex(JsonSerializer.SerializeToUtf8Bytes(normalizedParameters, SerializerOptions));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var committed = false;
        try
        {
            var existing = ReadMutation(connection, transaction, requestId);
            if (existing is not null)
            {
                if (!string.Equals(existing.Method, method, StringComparison.Ordinal) ||
                    !string.Equals(existing.ParametersSha256, parameterHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new BusinessCatalogException("idempotency_conflict", "The requestId was already used with a different method or parameters.");
                }

                var replay = JsonSerializer.Deserialize<T>(existing.ResponseJson, SerializerOptions)
                    ?? throw new BusinessCatalogException("idempotency_replay_failed", "The saved idempotent response could not be read.");
                transaction.Commit();
                committed = true;
                EnsureResponseWithinLimit(replay);
                return replay;
            }

            var response = operation(connection, transaction);
            var responseJson = SerializeResponse(response);
            using var insert = Command(connection, transaction, "INSERT INTO catalog_mutations(request_id, method, parameters_sha256, response_json, completed_at_utc) VALUES($request_id, $method, $parameters_sha256, $response_json, $completed_at_utc);");
            Add(insert, "$request_id", requestId);
            Add(insert, "$method", method);
            Add(insert, "$parameters_sha256", parameterHash);
            Add(insert, "$response_json", responseJson);
            Add(insert, "$completed_at_utc", UtcNowText());
            insert.ExecuteNonQuery();
            transaction.Commit();
            committed = true;
            return response;
        }
        catch
        {
            if (!committed)
            {
                transaction.Rollback();
            }

            throw;
        }
    }

    private string SerializeResponse<T>(T response)
    {
        var json = JsonSerializer.Serialize(response, SerializerOptions);
        if (Encoding.UTF8.GetByteCount(json) > _maximumResponseBytes)
        {
            throw new BusinessCatalogException("response_too_large", "The catalog response exceeds the control protocol size limit.");
        }

        return json;
    }

    private void EnsureResponseWithinLimit<T>(T response) => _ = SerializeResponse(response);

    private static Project ReadProject(SqliteConnection connection, SqliteTransaction? transaction, string projectId)
    {
        using var command = Command(connection, transaction, """
            SELECT p.project_id, p.name, p.description, p.spatial_configuration_state,
                   p.lifecycle_state, p.created_at_utc, p.updated_at_utc,
                   dc.crs_id, dc.authority, dc.code, dc.name, dc.wkt, dc.projjson,
                   dc.crs_type, dc.horizontal_unit, dc.vertical_reference, dc.axis_order,
                   dc.captured_at_utc, dc.created_at_utc,
                   sc.crs_id, sc.authority, sc.code, sc.name, sc.wkt, sc.projjson,
                   sc.crs_type, sc.horizontal_unit, sc.vertical_reference, sc.axis_order,
                   sc.captured_at_utc, sc.created_at_utc
            FROM projects p
            LEFT JOIN crs_definitions dc ON dc.crs_id = p.default_crs_id
            LEFT JOIN crs_definitions sc ON sc.crs_id = p.suggested_crs_id
            WHERE p.project_id = $project_id;
            """);
        Add(command, "$project_id", projectId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException("project_not_found", "The project was not found.");
        }

        return new Project(
            reader.GetString(0),
            reader.GetString(1),
            StringOrNull(reader, 2),
            reader.GetString(3),
            reader.GetString(4),
            ReadCrsOrNull(reader, 7),
            ReadCrsOrNull(reader, 19),
            ParseTime(reader.GetString(5)),
            ParseTime(reader.GetString(6)));
    }

    private static Dataset ReadDataset(SqliteConnection connection, SqliteTransaction? transaction, string datasetId)
    {
        using var command = Command(connection, transaction, "SELECT dataset_id, project_id, name, description, created_at_utc, updated_at_utc FROM datasets WHERE dataset_id = $dataset_id;");
        Add(command, "$dataset_id", datasetId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException("dataset_not_found", "The dataset was not found.");
        }

        return new Dataset(reader.GetString(0), reader.GetString(1), reader.GetString(2), StringOrNull(reader, 3), ParseTime(reader.GetString(4)), ParseTime(reader.GetString(5)));
    }

    private static Dataset ReadDatasetForVersion(SqliteConnection connection, SqliteTransaction? transaction, string datasetVersionId)
    {
        using var command = Command(connection, transaction, "SELECT d.dataset_id FROM datasets d JOIN dataset_versions dv ON dv.dataset_id = d.dataset_id WHERE dv.dataset_version_id = $dataset_version_id;");
        Add(command, "$dataset_version_id", datasetVersionId);
        var datasetId = command.ExecuteScalar() as string ?? throw new BusinessCatalogException("dataset_not_found", "The dataset was not found.");
        return ReadDataset(connection, transaction, datasetId);
    }

    private static DatasetVersion ReadDatasetVersion(SqliteConnection connection, SqliteTransaction? transaction, string datasetVersionId)
    {
        using var command = Command(connection, transaction, """
            SELECT dv.dataset_version_id, dv.dataset_id, dv.version_number, dv.parent_version_id,
                   dv.lifecycle_state, dv.source_eligibility_state, dv.quality_gate_state,
                   dv.content_manifest_sha256, dv.warning_acknowledged_at_utc,
                   dv.created_at_utc, dv.sealed_at_utc
            FROM dataset_versions dv
            WHERE dv.dataset_version_id = $dataset_version_id;
            """);
        Add(command, "$dataset_version_id", datasetVersionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException("dataset_version_not_found", "The dataset version was not found.");
        }

        return new DatasetVersion(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            StringOrNull(reader, 3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            StringOrNull(reader, 7),
            ParseOptionalTime(StringOrNull(reader, 8)),
            ParseTime(reader.GetString(9)),
            ParseOptionalTime(StringOrNull(reader, 10)));
    }

    private static ResultSummary ReadResult(SqliteConnection connection, SqliteTransaction? transaction, string resultId)
    {
        using var command = Command(connection, transaction, """
            SELECT r.result_id, r.result_series_id, r.version_number,
                   r.source_dataset_version_id, r.source_processing_job_id,
                   r.source_job_execution_id, r.source_result_id, r.result_kind, r.lifecycle_state,
                   c.crs_id, c.authority, c.code, c.name, c.wkt, c.projjson,
                   c.crs_type, c.horizontal_unit, c.vertical_reference, c.axis_order,
                   c.captured_at_utc, c.created_at_utc,
                   r.vertical_reference, r.local_origin_json, r.axis_convention, r.unit,
                   r.bounds_json, r.resolution_or_density_json, r.engine_version,
                   r.converter_version, r.parameter_sha256, r.accuracy_level,
                   r.created_at_utc, r.published_at_utc, r.superseded_by_result_id
            FROM results r
            LEFT JOIN crs_definitions c ON c.crs_id = r.crs_id
            WHERE r.result_id = $result_id AND r.lifecycle_state <> 'deleted';
            """);
        Add(command, "$result_id", resultId);
        ResultSummary summary;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw new BusinessCatalogException("result_not_found", "The result was not found or is not visible.");
            }

            summary = new ResultSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                StringOrNull(reader, 6),
                reader.GetString(7),
                reader.GetString(8),
                ReadCrsOrNull(reader, 9),
                StringOrNull(reader, 21),
                ParseJson(StringOrNull(reader, 22)),
                StringOrNull(reader, 23),
                StringOrNull(reader, 24),
                ParseJson(StringOrNull(reader, 25)),
                ParseJson(StringOrNull(reader, 26)),
                StringOrNull(reader, 27),
                StringOrNull(reader, 28),
                reader.GetString(29),
                reader.GetString(30),
                null,
                ParseTime(reader.GetString(31)),
                ParseOptionalTime(StringOrNull(reader, 32)),
                StringOrNull(reader, 33));
        }

        return summary with { QualityReport = ReadLatestFinalQualityReport(connection, transaction, resultId) };
    }

    private static ResultSeriesSummary ReadResultSeries(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string resultSeriesId)
    {
        using var command = Command(connection, transaction, """
            SELECT result_series_id, project_id, dataset_version_id, series_kind,
                   name, parent_series_id, created_at_utc
            FROM result_series
            WHERE result_series_id = $result_series_id;
            """);
        Add(command, "$result_series_id", resultSeriesId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException("result_series_not_found", "The authoritative result series was not found.");
        }

        return new ResultSeriesSummary(
            reader.GetString(0),
            reader.GetString(1),
            StringOrNull(reader, 2),
            reader.GetString(3),
            reader.GetString(4),
            StringOrNull(reader, 5),
            ParseTime(reader.GetString(6)));
    }

    private static ProcessingJobSummary ReadProcessingJob(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string processingJobId)
    {
        using var command = Command(connection, transaction, """
            SELECT processing_job_id, job_type, parameter_profile, parameter_schema_version,
                   parameter_sha256, lifecycle_state, created_at_utc, submitted_at_utc,
                   started_at_utc, ended_at_utc
            FROM processing_jobs
            WHERE processing_job_id = $processing_job_id;
            """);
        Add(command, "$processing_job_id", processingJobId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException("processing_job_not_found", "The authoritative source processing job was not found.");
        }

        return new ProcessingJobSummary(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            ParseTime(reader.GetString(6)),
            ParseTime(reader.GetString(7)),
            ParseOptionalTime(StringOrNull(reader, 8)),
            ParseOptionalTime(StringOrNull(reader, 9)));
    }

    private static JobExecutionSummary ReadJobExecution(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string jobExecutionId)
    {
        using var command = Command(connection, transaction, """
            SELECT job_execution_id, processing_job_id, attempt_number, execution_mode,
                   worker_type, worker_version, engine_name, engine_version,
                   parameter_sha256, lifecycle_state, started_at_utc, ended_at_utc
            FROM job_executions
            WHERE job_execution_id = $job_execution_id;
            """);
        Add(command, "$job_execution_id", jobExecutionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException("job_execution_not_found", "The authoritative source job execution was not found.");
        }

        return new JobExecutionSummary(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            StringOrNull(reader, 6),
            StringOrNull(reader, 7),
            reader.GetString(8),
            reader.GetString(9),
            ParseOptionalTime(StringOrNull(reader, 10)),
            ParseOptionalTime(StringOrNull(reader, 11)));
    }

    private static IReadOnlyList<ResultDependency> ReadResultDependencies(SqliteConnection connection, SqliteTransaction? transaction, string resultId)
    {
        using var command = Command(connection, transaction, "SELECT result_id, depends_on_result_id, dependency_kind FROM result_dependencies WHERE result_id = $result_id ORDER BY dependency_kind, depends_on_result_id LIMIT $limit;");
        Add(command, "$result_id", resultId);
        Add(command, "$limit", MaximumBoundedChildren + 1);
        using var reader = command.ExecuteReader();
        var rows = new List<ResultDependency>();
        while (reader.Read())
        {
            if (rows.Count >= MaximumBoundedChildren)
            {
                throw new BusinessCatalogException("lineage_too_large", "The result has too many direct dependencies for one bounded response.");
            }

            rows.Add(new ResultDependency(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private static IReadOnlyList<ResultFile> ReadResultFiles(SqliteConnection connection, SqliteTransaction? transaction, string resultId)
    {
        using var command = Command(connection, transaction, """
            SELECT rf.result_file_id, rf.result_id, rf.file_object_id, rf.file_role,
                   rf.relative_path, rf.is_required, rf.byte_length_snapshot,
                   rf.content_hash_snapshot, f.object_key, f.media_type
            FROM result_files rf
            JOIN file_objects f ON f.file_object_id = rf.file_object_id
            WHERE rf.result_id = $result_id AND f.storage_state = 'available' AND f.object_key IS NOT NULL
            ORDER BY rf.file_role, rf.relative_path
            LIMIT $limit;
            """);
        Add(command, "$result_id", resultId);
        Add(command, "$limit", MaximumBoundedChildren + 1);
        using var reader = command.ExecuteReader();
        var rows = new List<ResultFile>();
        while (reader.Read())
        {
            if (rows.Count >= MaximumBoundedChildren)
            {
                throw new BusinessCatalogException("lineage_too_large", "The result has too many available files for one bounded response.");
            }

            rows.Add(new ResultFile(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5) == 1,
                reader.GetInt64(6),
                reader.GetString(7),
                reader.GetString(8),
                StringOrNull(reader, 9)));
        }

        return rows;
    }

    private static QualityReportSummary? ReadLatestFinalQualityReport(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string resultId) => ReadFinalQualityReports(connection, transaction, resultId, 1, rejectOverflow: false).SingleOrDefault();

    private static IReadOnlyList<QualityReportSummary> ReadFinalQualityReports(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string resultId,
        int maximumCount = MaximumBoundedChildren,
        bool rejectOverflow = true)
    {
        using var command = Command(connection, transaction, """
            SELECT q.quality_report_id, q.report_type, q.version_number, q.lifecycle_state,
                   q.schema_version, q.summary_severity, q.summary_json,
                   COALESCE(SUM(CASE WHEN f.severity = 'blocking' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN f.severity = 'warning' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN f.severity = 'info' THEN 1 ELSE 0 END), 0),
                   q.created_at_utc, q.finalized_at_utc
            FROM quality_reports q
            LEFT JOIN quality_findings f ON f.quality_report_id = q.quality_report_id
            WHERE q.result_id = $result_id AND q.lifecycle_state = 'final'
            GROUP BY q.quality_report_id
            ORDER BY q.version_number DESC, q.finalized_at_utc DESC, q.quality_report_id DESC
            LIMIT $limit;
            """);
        Add(command, "$result_id", resultId);
        Add(command, "$limit", maximumCount + 1);
        using var reader = command.ExecuteReader();
        var rows = new List<QualityReportSummary>();
        while (reader.Read())
        {
            if (rows.Count >= maximumCount)
            {
                if (rejectOverflow)
                {
                    throw new BusinessCatalogException("lineage_too_large", "The result has too many final quality reports for one bounded response.");
                }

                break;
            }

            rows.Add(new QualityReportSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                ParseJson(StringOrNull(reader, 6)),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                ParseTime(reader.GetString(10)),
                ParseOptionalTime(StringOrNull(reader, 11))));
        }

        return rows;
    }

    private static string InsertOrReuseCrs(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CrsDefinitionInput crs)
    {
        using (var select = Command(connection, transaction, """
            SELECT crs_id
            FROM crs_definitions
            WHERE COALESCE(authority, '') = $authority
              AND COALESCE(code, '') = $code
              AND COALESCE(wkt, '') = $wkt
              AND COALESCE(projjson, '') = $projjson
              AND COALESCE(crs_type, '') = $crs_type
              AND horizontal_unit = $horizontal_unit
              AND COALESCE(vertical_reference, '') = $vertical
              AND axis_order = $axis_order
            ORDER BY created_at_utc, crs_id
            LIMIT 1;
            """))
        {
            Add(select, "$authority", crs.Authority ?? string.Empty);
            Add(select, "$code", crs.Code ?? string.Empty);
            Add(select, "$wkt", crs.Wkt ?? string.Empty);
            Add(select, "$projjson", crs.Projjson ?? string.Empty);
            Add(select, "$crs_type", crs.CrsType ?? string.Empty);
            Add(select, "$horizontal_unit", crs.HorizontalUnit);
            Add(select, "$vertical", crs.VerticalReference ?? string.Empty);
            Add(select, "$axis_order", crs.AxisOrder);
            var existing = select.ExecuteScalar() as string;
            if (existing is not null)
            {
                return existing;
            }
        }

        if (crs.Authority is not null)
        {
            using var conflicting = Command(connection, transaction, """
                SELECT crs_id
                FROM crs_definitions
                WHERE authority = $authority AND code = $code
                  AND COALESCE(vertical_reference, '') = $vertical
                LIMIT 1;
                """);
            Add(conflicting, "$authority", crs.Authority);
            Add(conflicting, "$code", crs.Code);
            Add(conflicting, "$vertical", crs.VerticalReference ?? string.Empty);
            if (conflicting.ExecuteScalar() is not null)
            {
                throw new BusinessCatalogException(
                    "crs_identity_conflict",
                    "The authority CRS identity already exists with a different definition snapshot.");
            }
        }

        var crsId = NewId("crs");
        var capturedAtUtc = UtcNowText();
        using var insert = Command(connection, transaction, """
            INSERT INTO crs_definitions(
                crs_id, authority, code, name, wkt, projjson, horizontal_unit,
                vertical_reference, axis_order, crs_type, captured_at_utc, created_at_utc)
            VALUES(
                $crs_id, $authority, $code, $name, $wkt, $projjson, $horizontal_unit,
                $vertical, $axis_order, $crs_type, $captured_at_utc, $created_at_utc);
            """);
        Add(insert, "$crs_id", crsId);
        Add(insert, "$authority", crs.Authority);
        Add(insert, "$code", crs.Code);
        Add(insert, "$name", crs.Name);
        Add(insert, "$wkt", crs.Wkt);
        Add(insert, "$projjson", crs.Projjson);
        Add(insert, "$horizontal_unit", crs.HorizontalUnit);
        Add(insert, "$vertical", crs.VerticalReference);
        Add(insert, "$axis_order", crs.AxisOrder);
        Add(insert, "$crs_type", crs.CrsType);
        Add(insert, "$captured_at_utc", capturedAtUtc);
        Add(insert, "$created_at_utc", capturedAtUtc);
        insert.ExecuteNonQuery();
        return crsId;
    }

    private static CrsSnapshot? ReadCrsOrNull(SqliteDataReader reader, int offset)
    {
        if (reader.IsDBNull(offset))
        {
            return null;
        }

        return new CrsSnapshot(
            StringOrNull(reader, offset + 1),
            StringOrNull(reader, offset + 2),
            reader.GetString(offset + 3),
            StringOrNull(reader, offset + 4),
            StringOrNull(reader, offset + 5),
            StringOrNull(reader, offset + 6),
            reader.GetString(offset + 7),
            StringOrNull(reader, offset + 8),
            reader.GetString(offset + 9),
            ParseTime(StringOrNull(reader, offset + 10) ?? reader.GetString(offset + 11)));
    }

    private static CrsDefinitionInput NormalizeCrsInput(CrsDefinitionInput crs)
    {
        ArgumentNullException.ThrowIfNull(crs);
        var authority = NormalizeOptionalText(crs.Authority, nameof(crs.Authority), 32);
        var code = NormalizeOptionalText(crs.Code, nameof(crs.Code), 64);
        var wkt = NormalizeOptionalText(crs.Wkt, nameof(crs.Wkt), 16_384);
        var projjson = NormalizeOptionalText(crs.Projjson, nameof(crs.Projjson), 16_384);
        if (authority is null && wkt is null && projjson is null)
        {
            throw new BusinessCatalogException("invalid_crs", "A CRS authority, WKT, or PROJJSON definition is required.");
        }

        if ((authority is null) != (code is null))
        {
            throw new BusinessCatalogException("invalid_crs", "CRS authority and code must be provided together.");
        }

        return new CrsDefinitionInput(
            authority,
            code,
            NormalizeRequiredText(crs.Name, nameof(crs.Name), 200),
            wkt,
            projjson,
            NormalizeOptionalText(crs.CrsType, nameof(crs.CrsType), 64),
            NormalizeRequiredText(crs.HorizontalUnit, nameof(crs.HorizontalUnit), 64),
            NormalizeOptionalText(crs.VerticalReference, nameof(crs.VerticalReference), 128),
            NormalizeRequiredText(crs.AxisOrder, nameof(crs.AxisOrder), 128));
    }

    private static CrsRecommendation RecommendCrs(Wgs84Bounds bounds)
    {
        ValidateBounds(bounds);
        if (bounds.WestLongitude > bounds.EastLongitude)
        {
            return Unavailable(bounds, "crosses_antimeridian");
        }

        if (bounds.SouthLatitude < -80 || bounds.NorthLatitude > 84)
        {
            return Unavailable(bounds, "outside_utm_latitude");
        }

        if (bounds.SouthLatitude < 0 && bounds.NorthLatitude > 0)
        {
            return Unavailable(bounds, "crosses_equator");
        }

        var westZone = UTMZone(bounds.WestLongitude, false);
        var eastZone = UTMZone(bounds.EastLongitude, true);
        if (westZone != eastZone)
        {
            return Unavailable(bounds, "crosses_utm_zone");
        }

        var northern = bounds.SouthLatitude >= 0;
        var code = ((northern ? 32600 : 32700) + westZone).ToString(CultureInfo.InvariantCulture);
        var suffix = northern ? "N" : "S";
        var crs = new CrsSnapshot(
            "EPSG",
            code,
            $"WGS 84 / UTM zone {westZone}{suffix}",
            null,
            null,
            "projected",
            "metre",
            "unknown",
            "east-north",
            DateTimeOffset.UtcNow);
        return new CrsRecommendation("recommended", bounds, crs, "single_wgs84_utm_zone");
    }

    private static CrsRecommendation Unavailable(Wgs84Bounds bounds, string reason) =>
        new("not-recommended", bounds, null, reason);

    private static void ValidateBounds(Wgs84Bounds bounds)
    {
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        if (!Finite(bounds.WestLongitude) || !Finite(bounds.SouthLatitude) || !Finite(bounds.EastLongitude) || !Finite(bounds.NorthLatitude) ||
            bounds.WestLongitude < -180 || bounds.WestLongitude > 180 || bounds.EastLongitude < -180 || bounds.EastLongitude > 180 ||
            bounds.SouthLatitude < -90 || bounds.NorthLatitude > 90 || bounds.SouthLatitude > bounds.NorthLatitude)
        {
            throw new BusinessCatalogException("invalid_wgs84_bounds", "WGS84 bounds are invalid.");
        }
    }

    private static int UTMZone(double longitude, bool eastBound)
    {
        var adjusted = eastBound && longitude > -180 ? Math.BitDecrement(longitude) : longitude;
        return adjusted >= 180 ? 60 : Math.Clamp((int)Math.Floor((adjusted + 180.0) / 6.0) + 1, 1, 60);
    }

    private static BusinessPage NormalizePage(int? requestedPageSize, string? requestedCursor)
    {
        var pageSize = requestedPageSize ?? DefaultPageSize;
        if (pageSize is <= 0 or > MaximumPageSize)
        {
            throw new BusinessCatalogException(
                "invalid_page_size",
                $"Page size must be between 1 and {MaximumPageSize}.");
        }

        return new BusinessPage(pageSize, NormalizeOptionalText(requestedCursor, "cursor", 512));
    }

    private static PageResult<T> ToPage<T>(
        List<(T Item, string CreatedAtUtc, string Id)> rows,
        int pageSize,
        string method,
        string scope)
    {
        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var last = rows[pageSize - 1];
            nextCursor = EncodeCursor(last.CreatedAtUtc, last.Id, method, scope);
            rows.RemoveRange(pageSize, rows.Count - pageSize);
        }

        return new PageResult<T>(rows.Select(row => row.Item).ToArray(), nextCursor);
    }

    private static string EncodeCursor(string createdAtUtc, string id, string method, string scope)
    {
        var json = JsonSerializer.Serialize(new BusinessCatalogCursor(1, method, scope, createdAtUtc, id), SerializerOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static BusinessCatalogCursor? DecodeCursor(string? cursor, string method, string scope)
    {
        if (cursor is null)
        {
            return null;
        }

        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var decoded = JsonSerializer.Deserialize<BusinessCatalogCursor>(json, SerializerOptions) ?? throw new JsonException();
            if (decoded.Version != 1 || !string.Equals(decoded.Method, method, StringComparison.Ordinal) ||
                !string.Equals(decoded.Scope, scope, StringComparison.Ordinal))
            {
                throw new BusinessCatalogException("invalid_cursor", "The page cursor does not belong to this list and filter.");
            }

            return decoded with
            {
                CreatedAtUtc = NormalizeRequiredText(decoded.CreatedAtUtc, "cursor.createdAtUtc", 64),
                Id = NormalizeId(decoded.Id, "cursor.id")
            };
        }
        catch (Exception ex) when (ex is FormatException or JsonException or BusinessCatalogException)
        {
            throw new BusinessCatalogException("invalid_cursor", "The page cursor is invalid.", ex);
        }
    }

    private static Wgs84Bounds? ParseBoundsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var west = ReadDouble(root, "westLongitude", "west");
            var south = ReadDouble(root, "southLatitude", "south");
            var east = ReadDouble(root, "eastLongitude", "east");
            var north = ReadDouble(root, "northLatitude", "north");
            return west is null || south is null || east is null || north is null ? null : new Wgs84Bounds(west.Value, south.Value, east.Value, north.Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new BusinessCatalogException(
                "catalog_data_invalid",
                "The authoritative catalog contains an invalid JSON snapshot.",
                exception);
        }
    }

    private static double? ReadDouble(JsonElement element, string preferred, string fallback)
    {
        if (element.TryGetProperty(preferred, out var value) || element.TryGetProperty(fallback, out value))
        {
            return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
        }

        return null;
    }

    private static CatalogMutation? ReadMutation(SqliteConnection connection, SqliteTransaction transaction, string requestId)
    {
        using var command = Command(connection, transaction, "SELECT method, parameters_sha256, response_json FROM catalog_mutations WHERE request_id = $request_id;");
        Add(command, "$request_id", requestId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new CatalogMutation(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    private static void EnsureExists(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string id, string code, string message)
    {
        using var command = Command(connection, transaction, $"SELECT 1 FROM {table} WHERE {column} = $id;");
        Add(command, "$id", id);
        if (command.ExecuteScalar() is null)
        {
            throw new BusinessCatalogException(code, message);
        }
    }

    private static string NormalizeId(string value, string fieldName) => NormalizeIdentifier(value, fieldName, 128);

    private static string? NormalizeOptionalId(string? value, string fieldName) =>
        value is null ? null : NormalizeIdentifier(value, fieldName, 128);

    private static string NormalizeIdentifier(string value, string fieldName, int maximumLength)
    {
        var normalized = NormalizeRequiredText(value, fieldName, maximumLength);
        if (normalized.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
        {
            throw new BusinessCatalogException(
                "invalid_parameters",
                $"{fieldName} contains unsupported identifier characters.");
        }

        return normalized;
    }

    private static string NormalizeName(string value, string fieldName) => NormalizeRequiredText(value, fieldName, 200);

    private static string NormalizeRequiredText(string value, string fieldName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new BusinessCatalogException("invalid_parameters", $"{fieldName} is required.");
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new BusinessCatalogException("invalid_parameters", $"{fieldName} exceeds the maximum length.");
        }

        return normalized;
    }

    private static string? NormalizeOptionalText(string? value, string fieldName, int maximumLength)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length > maximumLength)
        {
            throw new BusinessCatalogException("invalid_parameters", $"{fieldName} exceeds the maximum length.");
        }

        return normalized;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static object? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql);
        foreach (var (name, value) in parameters)
        {
            Add(command, name, value);
        }

        return command.ExecuteScalar();
    }

    private static string? ScalarString(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters) => Scalar(connection, transaction, sql, parameters) as string;

    private static string? StringOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ParseOptionalTime(string? value) => value is null ? null : ParseTime(value);

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static string UtcNowText() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed class BusinessCatalogException : InvalidOperationException
{
    public BusinessCatalogException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public BusinessCatalogException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed record BusinessPage(int PageSize, string? Cursor);

internal sealed record BusinessCatalogCursor(
    int Version,
    string Method,
    string Scope,
    string CreatedAtUtc,
    string Id);

internal sealed record CatalogMutation(string Method, string ParametersSha256, string ResponseJson);
