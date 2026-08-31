using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QiongTu.Control;

internal sealed record ImageMetadataRunSnapshot(
    string MetadataRunId,
    string ImageId,
    string NormalizedFileObjectId,
    string Status,
    string NormalizedSha256,
    long NormalizedByteLength,
    string NormalizedObjectKey,
    string? FieldInventorySha256,
    int? FieldCount,
    string? FailureCode);

internal sealed record ImageMetadataCatalogField(
    string FieldName,
    string? FieldValueJson,
    string SourceKind,
    string FieldState,
    string SourceDetail);

internal sealed record ImageMetadataCompletion(
    string Status,
    string? FieldInventorySha256,
    int? FieldCount,
    bool ReusedExisting);

internal sealed class ImageMetadataCatalog
{
    internal const string ParserSchema = "qiongtu.image-probe.image-metadata.v1";
    internal const string ParserProfile = "image-metadata.v1";
    internal const string ProductParser = "qiongtu.image-metadata";
    internal const string ProductParserVersion = "1.0.0";
    internal const string MetadataExtractorVersion = "2.9.3";
    internal const string FieldMappingVersion = "dji-metadata-map.v1";
    internal const string ConflictPolicyVersion = "metadata-conflict.v1";

    internal static readonly IReadOnlySet<string> RequiredFieldNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "capture.time_local",
        "capture.time_utc",
        "camera.manufacturer",
        "camera.model",
        "camera.lens_model",
        "camera.focal_length_mm",
        "position.latitude_deg",
        "position.longitude_deg",
        "position.absolute_altitude_m",
        "position.relative_altitude_m",
        "pose.gimbal_roll_deg",
        "pose.gimbal_pitch_deg",
        "pose.gimbal_yaw_deg",
        "pose.flight_roll_deg",
        "pose.flight_pitch_deg",
        "pose.flight_yaw_deg",
        "position.rtk_flag",
        "position.std_lon_m",
        "position.std_lat_m",
        "position.std_height_m"
    };

    private static readonly IReadOnlySet<string> AllowedSourceKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "exif", "gps_exif", "dji_xmp", "derived"
    };

    private static readonly IReadOnlySet<string> AllowedFieldStates = new HashSet<string>(StringComparer.Ordinal)
    {
        "present", "missing", "conflict", "abnormal", "not_assessable"
    };

    private readonly BusinessDatabase _database;

    public ImageMetadataCatalog(BusinessDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public ImageMetadataRunSnapshot EnsureRun(string imageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = TryReadRun(connection, transaction, imageId, byImage: true);
        if (existing is not null)
        {
            transaction.Commit();
            return existing;
        }

        using var source = Command(
            connection,
            transaction,
            """
            SELECT i.normalized_file_object_id, f.content_hash, f.byte_length, f.object_key
            FROM images i
            JOIN image_inspection_runs ir ON ir.inspection_run_id = i.inspection_run_id
            JOIN file_objects f ON f.file_object_id = i.normalized_file_object_id
            JOIN file_object_roles r ON r.file_object_id = f.file_object_id AND r.object_role = 'normalized_image_frame'
            WHERE i.image_id = $image_id AND ir.status = 'completed' AND f.storage_state = 'available';
            """);
        Add(source, "$image_id", imageId);
        using var reader = source.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException(
                "image_metadata_source_invalid",
                "Image metadata requires a completed image manifest and an available normalized object.");
        }

        var normalizedFileObjectId = reader.GetString(0);
        var normalizedSha256 = reader.GetString(1);
        var normalizedByteLength = reader.GetInt64(2);
        var normalizedObjectKey = reader.GetString(3);
        reader.Close();

        var metadataRunId = NewId("image-metadata");
        var now = UtcNowText();
        using var insert = Command(
            connection,
            transaction,
            """
            INSERT INTO image_metadata_runs(
                metadata_run_id, image_id, normalized_file_object_id, status,
                parser_schema, parser_profile, product_parser, product_parser_version,
                metadata_extractor_version, field_mapping_version, conflict_policy_version,
                normalized_content_hash_snapshot, normalized_byte_length_snapshot,
                created_at_utc, updated_at_utc)
            VALUES(
                $metadata_run_id, $image_id, $normalized_file_object_id, 'pending',
                $parser_schema, $parser_profile, $product_parser, $product_parser_version,
                $metadata_extractor_version, $field_mapping_version, $conflict_policy_version,
                $normalized_sha256, $normalized_byte_length, $created_at_utc, $updated_at_utc);
            """);
        Add(insert, "$metadata_run_id", metadataRunId);
        Add(insert, "$image_id", imageId);
        Add(insert, "$normalized_file_object_id", normalizedFileObjectId);
        Add(insert, "$parser_schema", ParserSchema);
        Add(insert, "$parser_profile", ParserProfile);
        Add(insert, "$product_parser", ProductParser);
        Add(insert, "$product_parser_version", ProductParserVersion);
        Add(insert, "$metadata_extractor_version", MetadataExtractorVersion);
        Add(insert, "$field_mapping_version", FieldMappingVersion);
        Add(insert, "$conflict_policy_version", ConflictPolicyVersion);
        Add(insert, "$normalized_sha256", normalizedSha256);
        Add(insert, "$normalized_byte_length", normalizedByteLength);
        Add(insert, "$created_at_utc", now);
        Add(insert, "$updated_at_utc", now);
        insert.ExecuteNonQuery();
        transaction.Commit();

        return new ImageMetadataRunSnapshot(
            metadataRunId,
            imageId,
            normalizedFileObjectId,
            "pending",
            normalizedSha256,
            normalizedByteLength,
            normalizedObjectKey,
            null,
            null,
            null);
    }

    public IReadOnlyList<string> ListRecoverableImageIds(int limit = 256)
    {
        if (limit is <= 0 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.image_id
            FROM images i
            JOIN image_inspection_runs ir ON ir.inspection_run_id = i.inspection_run_id AND ir.status = 'completed'
            LEFT JOIN image_metadata_runs mr ON mr.image_id = i.image_id
            WHERE mr.metadata_run_id IS NULL OR mr.status IN ('pending', 'parsing', 'interrupted')
            ORDER BY i.created_at_utc, i.image_id
            LIMIT $limit;
            """;
        Add(command, "$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public ImageMetadataRunSnapshot GetRun(string metadataRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataRunId);
        using var connection = _database.OpenConnection();
        return TryReadRun(connection, null, metadataRunId, byImage: false)
            ?? throw new BusinessCatalogException("image_metadata_run_not_found", "The image metadata run does not exist.");
    }

    public ImageMetadataRunSnapshot BeginParsing(string metadataRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataRunId);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var run = ReadRun(connection, transaction, metadataRunId);
        if (run.Status is "completed" or "blocked" or "parsing")
        {
            transaction.Commit();
            return run;
        }

        if (run.Status is not ("pending" or "interrupted"))
        {
            throw new BusinessCatalogException("image_metadata_state_conflict", "The image metadata run cannot begin parsing from its current state.");
        }

        using var update = Command(
            connection,
            transaction,
            """
            UPDATE image_metadata_runs
            SET status = 'parsing', failure_code = NULL, updated_at_utc = $updated_at_utc
            WHERE metadata_run_id = $metadata_run_id;
            """);
        Add(update, "$updated_at_utc", UtcNowText());
        Add(update, "$metadata_run_id", metadataRunId);
        update.ExecuteNonQuery();
        transaction.Commit();
        return run with { Status = "parsing", FailureCode = null };
    }

    public ImageMetadataCompletion Complete(
        string metadataRunId,
        IReadOnlyList<ImageMetadataCatalogField> fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataRunId);
        ArgumentNullException.ThrowIfNull(fields);
        var canonicalFields = ValidateAndCanonicalizeFields(fields);
        var inventorySha256 = InventorySha256(canonicalFields);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var run = ReadRun(connection, transaction, metadataRunId);
        if (run.Status == "completed")
        {
            if (!string.Equals(run.FieldInventorySha256, inventorySha256, StringComparison.Ordinal) ||
                run.FieldCount != canonicalFields.Count)
            {
                throw new BusinessCatalogException(
                    "image_metadata_inventory_conflict",
                    "The authoritative image metadata inventory conflicts with the repeated result.");
            }

            transaction.Commit();
            return new ImageMetadataCompletion("completed", run.FieldInventorySha256, run.FieldCount, ReusedExisting: true);
        }

        if (run.Status != "parsing")
        {
            throw new BusinessCatalogException("image_metadata_state_conflict", "The image metadata run is not ready to record fields.");
        }

        var now = UtcNowText();
        foreach (var field in canonicalFields)
        {
            using var insert = Command(
                connection,
                transaction,
                """
                INSERT INTO image_metadata_fields(
                    image_metadata_field_id, image_id, field_name, field_value_json,
                    source_kind, field_state, source_detail, metadata_run_id)
                VALUES(
                    $field_id, $image_id, $field_name, $field_value_json,
                    $source_kind, $field_state, $source_detail, $metadata_run_id);
                """);
            Add(insert, "$field_id", NewId("metadata-field"));
            Add(insert, "$image_id", run.ImageId);
            Add(insert, "$field_name", field.FieldName);
            Add(insert, "$field_value_json", field.FieldValueJson);
            Add(insert, "$source_kind", field.SourceKind);
            Add(insert, "$field_state", field.FieldState);
            Add(insert, "$source_detail", field.SourceDetail);
            Add(insert, "$metadata_run_id", run.MetadataRunId);
            insert.ExecuteNonQuery();
        }

        var metadataState = DetermineMetadataState(canonicalFields);
        using (var updateImage = Command(
            connection,
            transaction,
            """
            UPDATE images
            SET capture_time_utc = $capture_time_utc,
                manufacturer = $manufacturer,
                camera_model = $camera_model,
                lens_model = $lens_model,
                metadata_state = $metadata_state,
                raw_metadata_json = NULL
            WHERE image_id = $image_id;
            """))
        {
            Add(updateImage, "$capture_time_utc", ReadUnambiguousText(canonicalFields, "capture.time_utc"));
            Add(updateImage, "$manufacturer", ReadUnambiguousText(canonicalFields, "camera.manufacturer"));
            Add(updateImage, "$camera_model", ReadUnambiguousText(canonicalFields, "camera.model"));
            Add(updateImage, "$lens_model", ReadUnambiguousText(canonicalFields, "camera.lens_model"));
            Add(updateImage, "$metadata_state", metadataState);
            Add(updateImage, "$image_id", run.ImageId);
            updateImage.ExecuteNonQuery();
        }

        using (var complete = Command(
            connection,
            transaction,
            """
            UPDATE image_metadata_runs
            SET status = 'completed', field_inventory_sha256 = $inventory_sha256,
                field_count = $field_count, failure_code = NULL,
                updated_at_utc = $updated_at_utc, completed_at_utc = $completed_at_utc
            WHERE metadata_run_id = $metadata_run_id;
            """))
        {
            Add(complete, "$inventory_sha256", inventorySha256);
            Add(complete, "$field_count", canonicalFields.Count);
            Add(complete, "$updated_at_utc", now);
            Add(complete, "$completed_at_utc", now);
            Add(complete, "$metadata_run_id", run.MetadataRunId);
            complete.ExecuteNonQuery();
        }

        transaction.Commit();
        return new ImageMetadataCompletion("completed", inventorySha256, canonicalFields.Count, ReusedExisting: false);
    }

    public void MarkInterrupted(string metadataRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataRunId);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE image_metadata_runs
            SET status = 'interrupted', updated_at_utc = $updated_at_utc
            WHERE metadata_run_id = $metadata_run_id AND status = 'parsing';
            """;
        Add(command, "$updated_at_utc", UtcNowText());
        Add(command, "$metadata_run_id", metadataRunId);
        command.ExecuteNonQuery();
    }

    public void Block(string metadataRunId, string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataRunId);
        ValidateReasonCode(failureCode);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var run = ReadRun(connection, transaction, metadataRunId);
        if (run.Status == "blocked")
        {
            transaction.Commit();
            return;
        }

        if (run.Status == "completed")
        {
            throw new BusinessCatalogException("image_metadata_state_conflict", "Completed image metadata cannot be blocked.");
        }

        using (var count = Command(
            connection,
            transaction,
            "SELECT count(*) FROM image_metadata_fields WHERE metadata_run_id = $metadata_run_id;"))
        {
            Add(count, "$metadata_run_id", metadataRunId);
            if (Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                throw new BusinessCatalogException("image_metadata_partial_fields", "A blocked metadata run cannot retain partial fields.");
            }
        }

        using (var image = Command(
            connection,
            transaction,
            "UPDATE images SET metadata_state = 'abnormal', raw_metadata_json = NULL WHERE image_id = $image_id;"))
        {
            Add(image, "$image_id", run.ImageId);
            image.ExecuteNonQuery();
        }

        var now = UtcNowText();
        using (var block = Command(
            connection,
            transaction,
            """
            UPDATE image_metadata_runs
            SET status = 'blocked', field_inventory_sha256 = NULL, field_count = NULL,
                failure_code = $failure_code, updated_at_utc = $updated_at_utc,
                completed_at_utc = $completed_at_utc
            WHERE metadata_run_id = $metadata_run_id;
            """))
        {
            Add(block, "$failure_code", failureCode);
            Add(block, "$updated_at_utc", now);
            Add(block, "$completed_at_utc", now);
            Add(block, "$metadata_run_id", metadataRunId);
            block.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    internal static string InventorySha256(IReadOnlyList<ImageMetadataCatalogField> canonicalFields)
    {
        var json = JsonSerializer.Serialize(canonicalFields, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    internal static IReadOnlyList<ImageMetadataCatalogField> CanonicalizeForProbeValidation(
        IReadOnlyList<ImageMetadataCatalogField> fields) =>
        ValidateAndCanonicalizeFields(fields);

    private static IReadOnlyList<ImageMetadataCatalogField> ValidateAndCanonicalizeFields(
        IReadOnlyList<ImageMetadataCatalogField> fields)
    {
        if (fields.Count is < 20 or > 64)
        {
            throw new BusinessCatalogException("image_metadata_field_count_invalid", "The metadata field inventory is outside its bounded size.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<ImageMetadataCatalogField>(fields.Count);
        foreach (var field in fields)
        {
            if (!RequiredFieldNames.Contains(field.FieldName) ||
                !AllowedSourceKinds.Contains(field.SourceKind) ||
                !AllowedFieldStates.Contains(field.FieldState) ||
                string.IsNullOrWhiteSpace(field.SourceDetail) || field.SourceDetail.Length > 128 ||
                !keys.Add(field.FieldName + "\n" + field.SourceKind))
            {
                throw new BusinessCatalogException("image_metadata_field_invalid", "The metadata field inventory is invalid or contains duplicate source identities.");
            }

            var requiresValue = field.FieldState is "present" or "conflict";
            if (requiresValue != (field.FieldValueJson is not null))
            {
                throw new BusinessCatalogException("image_metadata_value_state_invalid", "The metadata field value does not match its state.");
            }

            var normalizedValueJson = field.FieldValueJson;
            if (field.FieldValueJson is { } valueJson)
            {
                if (valueJson.Length > 1024)
                {
                    throw new BusinessCatalogException("image_metadata_value_limit_exceeded", "A metadata field value exceeds its size limit.");
                }

                try
                {
                    using var document = JsonDocument.Parse(valueJson);
                    if (document.RootElement.ValueKind is not (
                        JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                    {
                        throw new BusinessCatalogException("image_metadata_value_type_invalid", "Metadata values must be scalar JSON values.");
                    }

                    normalizedValueJson = JsonSerializer.Serialize(document.RootElement);
                }
                catch (JsonException exception)
                {
                    throw new BusinessCatalogException("image_metadata_value_json_invalid", "A metadata field value is not valid JSON.", exception);
                }
            }

            normalized.Add(field with { FieldValueJson = normalizedValueJson });
        }

        if (!RequiredFieldNames.SetEquals(fields.Select(field => field.FieldName)))
        {
            throw new BusinessCatalogException("image_metadata_fields_incomplete", "The metadata inventory does not contain every required field.");
        }

        return normalized
            .OrderBy(field => field.FieldName, StringComparer.Ordinal)
            .ThenBy(field => field.SourceKind, StringComparer.Ordinal)
            .ToArray();
    }

    private static string DetermineMetadataState(IReadOnlyList<ImageMetadataCatalogField> fields)
    {
        if (fields.Any(field => field.FieldState == "conflict"))
        {
            return "conflict";
        }

        if (fields.Any(field => field.FieldState == "abnormal"))
        {
            return "abnormal";
        }

        var required = new[]
        {
            "camera.manufacturer", "camera.model", "position.latitude_deg", "position.longitude_deg"
        };
        if (required.Any(name => !fields.Any(field =>
                field.FieldName == name && field.FieldState == "present")))
        {
            return "missing_required";
        }

        return "parsed";
    }

    private static string? ReadUnambiguousText(
        IReadOnlyList<ImageMetadataCatalogField> fields,
        string fieldName)
    {
        var present = fields
            .Where(field => field.FieldName == fieldName && field.FieldState == "present" && field.FieldValueJson is not null)
            .Select(field => field.FieldValueJson!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (present.Length != 1)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(present[0]);
            return document.RootElement.ValueKind == JsonValueKind.String
                ? document.RootElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ImageMetadataRunSnapshot ReadRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string metadataRunId) =>
        TryReadRun(connection, transaction, metadataRunId, byImage: false)
        ?? throw new BusinessCatalogException("image_metadata_run_not_found", "The image metadata run does not exist.");

    private static ImageMetadataRunSnapshot? TryReadRun(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string identity,
        bool byImage)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT r.metadata_run_id, r.image_id, r.normalized_file_object_id, r.status,
                   r.normalized_content_hash_snapshot, r.normalized_byte_length_snapshot,
                   f.object_key, r.field_inventory_sha256, r.field_count, r.failure_code
            FROM image_metadata_runs r
            JOIN file_objects f ON f.file_object_id = r.normalized_file_object_id
            WHERE r.{(byImage ? "image_id" : "metadata_run_id")} = $identity;
            """;
        Add(command, "$identity", identity);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ImageMetadataRunSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static string UtcNowText() => DateTimeOffset.UtcNow.ToString("O");

    private static void ValidateReasonCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128 ||
            code.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new BusinessCatalogException("image_metadata_reason_invalid", "The image metadata reason code is invalid.");
        }
    }
}
