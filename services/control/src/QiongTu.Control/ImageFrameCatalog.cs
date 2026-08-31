using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control;

internal sealed record ImageInspectionRunSnapshot(
    string InspectionRunId,
    string ImportEntryId,
    string DatasetVersionId,
    string SourceEntryKey,
    int SortIndex,
    string SourceFileObjectId,
    string SourceSha256,
    long SourceByteLength,
    string SourceObjectKey,
    string Status,
    string? ContentContainer,
    int? PrimaryFrameIndex,
    int? FrameCount,
    string? FrameInventoryJson,
    string? FrameInventorySha256,
    string? NormalizationAction,
    string? NormalizedStageId,
    string? NormalizedStageSha256,
    long? NormalizedStageByteLength,
    DateTimeOffset? NormalizedStageCreatedAtUtc,
    string? NormalizedContentSha256,
    long? NormalizedContentByteLength,
    string? NormalizedObjectKey,
    string? ImageId,
    string? FailureCode);

internal sealed record ImageInspectionCompletion(
    string Status,
    string? ImageId,
    string? FailureCode,
    bool ReusedExisting);

internal sealed class ImageFrameCatalog
{
    internal const string ParserSchema = ImageProbeProtocol.CasImageV1;
    internal const string ParserProfile = "cas-image.v1";
    internal const string ProductParser = "qiongtu.cas-image";
    internal const string ProductParserVersion = "1.0.0";
    internal const string NativeDecoder = "magick.net-q16-x64";
    internal const string NativeDecoderVersion = "14.16.0";
    internal const string MainFramePolicy = "photogrammetry-main-frame.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly BusinessDatabase _database;

    public ImageFrameCatalog(BusinessDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public ImageInspectionRunSnapshot EnsureRun(string importEntryId)
    {
        importEntryId = NormalizeIdentifier(importEntryId, nameof(importEntryId), 128);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var source = ReadCanonicalSource(connection, transaction, importEntryId);
        using (var insert = Command(
            connection,
            transaction,
            """
            INSERT INTO image_inspection_runs(
                inspection_run_id, import_entry_id, dataset_version_id, source_file_object_id,
                status, parser_schema, parser_profile, product_parser, product_parser_version,
                native_decoder, native_decoder_version, main_frame_policy_version,
                created_at_utc, updated_at_utc)
            VALUES(
                $inspection_run_id, $import_entry_id, $dataset_version_id, $source_file_object_id,
                'pending', $parser_schema, $parser_profile, $product_parser, $product_parser_version,
                $native_decoder, $native_decoder_version, $main_frame_policy_version,
                $created_at_utc, $updated_at_utc)
            ON CONFLICT(import_entry_id) DO NOTHING;
            """))
        {
            var now = UtcNowText();
            Add(insert, "$inspection_run_id", NewId("image-inspection"));
            Add(insert, "$import_entry_id", source.ImportEntryId);
            Add(insert, "$dataset_version_id", source.DatasetVersionId);
            Add(insert, "$source_file_object_id", source.FileObjectId);
            Add(insert, "$parser_schema", ParserSchema);
            Add(insert, "$parser_profile", ParserProfile);
            Add(insert, "$product_parser", ProductParser);
            Add(insert, "$product_parser_version", ProductParserVersion);
            Add(insert, "$native_decoder", NativeDecoder);
            Add(insert, "$native_decoder_version", NativeDecoderVersion);
            Add(insert, "$main_frame_policy_version", MainFramePolicy);
            Add(insert, "$created_at_utc", now);
            Add(insert, "$updated_at_utc", now);
            insert.ExecuteNonQuery();
        }

        var result = ReadRun(connection, transaction, source.ImportEntryId, byImportEntry: true);
        EnsureRunIdentity(result, source);
        transaction.Commit();
        return result;
    }

    public IReadOnlyList<string> ListRecoverableImportEntryIds()
    {
        using var connection = _database.OpenConnection();
        using var command = Command(
            connection,
            null,
            """
            SELECT e.import_entry_id
            FROM image_import_entries e
            LEFT JOIN image_inspection_runs r ON r.import_entry_id = e.import_entry_id
            WHERE e.status = 'available'
              AND e.canonical_entry_id IS NULL
              AND (r.inspection_run_id IS NULL OR r.status NOT IN ('completed', 'blocked'))
            ORDER BY e.updated_at_utc ASC, e.import_entry_id ASC;
            """);
        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public ImageInspectionRunSnapshot GetRun(string inspectionRunId)
    {
        inspectionRunId = NormalizeIdentifier(inspectionRunId, nameof(inspectionRunId), 128);
        using var connection = _database.OpenConnection();
        return ReadRun(connection, null, inspectionRunId, byImportEntry: false);
    }

    public ImageInspectionRunSnapshot BeginProbe(string inspectionRunId)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadRun(connection, transaction, inspectionRunId, byImportEntry: false);
        if (current.Status is "completed" or "blocked")
        {
            transaction.Commit();
            return current;
        }

        if (current.Status is not ("pending" or "interrupted"))
        {
            throw new BusinessCatalogException("image_inspection_state_conflict", "The image inspection is not ready to probe.");
        }

        UpdateStatus(connection, transaction, inspectionRunId, "probing");
        var result = ReadRun(connection, transaction, inspectionRunId, byImportEntry: false);
        transaction.Commit();
        return result;
    }

    public ImageInspectionRunSnapshot RecordReusableProbe(
        string inspectionRunId,
        ImageProbeCasImageResult result,
        ImageProbeCasImageFrame primaryFrame,
        string inventoryJson,
        string inventorySha256,
        string normalizationAction)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadRun(connection, transaction, inspectionRunId, byImportEntry: false);
        RequireProbeState(current);
        ValidateProbeIdentity(result);
        using var update = Command(
            connection,
            transaction,
            """
            UPDATE image_inspection_runs
            SET status = 'recording', content_container = $content_container,
                primary_frame_index = $primary_frame_index, frame_count = $frame_count,
                frame_inventory_json = $frame_inventory_json, frame_inventory_sha256 = $frame_inventory_sha256,
                normalization_action = $normalization_action,
                normalized_content_sha256 = $normalized_content_sha256,
                normalized_content_byte_length = $normalized_content_byte_length,
                normalized_object_key = $normalized_object_key,
                updated_at_utc = $updated_at_utc
            WHERE inspection_run_id = $inspection_run_id;
            """);
        AddProbeFields(update, result, primaryFrame, inventoryJson, inventorySha256, normalizationAction);
        Add(update, "$normalized_content_sha256", current.SourceSha256);
        Add(update, "$normalized_content_byte_length", current.SourceByteLength);
        Add(update, "$normalized_object_key", current.SourceObjectKey);
        Add(update, "$updated_at_utc", UtcNowText());
        Add(update, "$inspection_run_id", inspectionRunId);
        update.ExecuteNonQuery();
        var snapshot = ReadRun(connection, transaction, inspectionRunId, byImportEntry: false);
        transaction.Commit();
        return snapshot;
    }

    public ImageInspectionRunSnapshot RecordStagedProbe(
        string inspectionRunId,
        ImageProbeCasImageResult result,
        ImageProbeCasImageFrame primaryFrame,
        string inventoryJson,
        string inventorySha256,
        ObjectStageReceipt stage)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadRun(connection, transaction, inspectionRunId, byImportEntry: false);
        RequireProbeState(current);
        ValidateProbeIdentity(result);
        using var update = Command(
            connection,
            transaction,
            """
            UPDATE image_inspection_runs
            SET status = 'staged', content_container = $content_container,
                primary_frame_index = $primary_frame_index, frame_count = $frame_count,
                frame_inventory_json = $frame_inventory_json, frame_inventory_sha256 = $frame_inventory_sha256,
                normalization_action = 'byte_exact_mpo_extract',
                normalized_stage_id = $normalized_stage_id,
                normalized_stage_sha256 = $normalized_stage_sha256,
                normalized_stage_byte_length = $normalized_stage_byte_length,
                normalized_stage_created_at_utc = $normalized_stage_created_at_utc,
                normalized_content_sha256 = $normalized_content_sha256,
                normalized_content_byte_length = $normalized_content_byte_length,
                normalized_object_key = $normalized_object_key,
                updated_at_utc = $updated_at_utc
            WHERE inspection_run_id = $inspection_run_id;
            """);
        AddProbeFields(update, result, primaryFrame, inventoryJson, inventorySha256, "byte_exact_mpo_extract");
        Add(update, "$normalized_stage_id", stage.StageId);
        Add(update, "$normalized_stage_sha256", stage.Sha256);
        Add(update, "$normalized_stage_byte_length", stage.ByteLength);
        Add(update, "$normalized_stage_created_at_utc", stage.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        Add(update, "$normalized_content_sha256", stage.Sha256);
        Add(update, "$normalized_content_byte_length", stage.ByteLength);
        Add(update, "$normalized_object_key", ObjectKey(stage.Sha256));
        Add(update, "$updated_at_utc", UtcNowText());
        Add(update, "$inspection_run_id", inspectionRunId);
        update.ExecuteNonQuery();
        var snapshot = ReadRun(connection, transaction, inspectionRunId, byImportEntry: false);
        transaction.Commit();
        return snapshot;
    }

    public ImageInspectionRunSnapshot MarkPublishing(string inspectionRunId)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadRun(connection, transaction, inspectionRunId, byImportEntry: false);
        if (current.Status is not ("staged" or "interrupted"))
        {
            throw new BusinessCatalogException("image_inspection_state_conflict", "The image inspection is not ready to publish.");
        }

        RequireStage(current);
        UpdateStatus(connection, transaction, inspectionRunId, "publishing");
        var result = ReadRun(connection, transaction, inspectionRunId, byImportEntry: false);
        transaction.Commit();
        return result;
    }

    public ImageInspectionRunSnapshot MarkRecording(string inspectionRunId, PublishedObject published)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadRun(connection, transaction, inspectionRunId, byImportEntry: false);
        if (current.Status is not ("publishing" or "interrupted"))
        {
            throw new BusinessCatalogException("image_inspection_state_conflict", "The image inspection is not ready to record.");
        }

        if (!string.Equals(current.NormalizedContentSha256, published.Sha256, StringComparison.Ordinal) ||
            current.NormalizedContentByteLength != published.ByteLength ||
            !string.Equals(current.NormalizedObjectKey, published.ObjectKey, StringComparison.Ordinal))
        {
            throw new BusinessCatalogException("image_normalized_identity_conflict", "The published normalized object does not match the inspection ledger.");
        }

        UpdateStatus(connection, transaction, inspectionRunId, "recording");
        var result = ReadRun(connection, transaction, inspectionRunId, byImportEntry: false);
        transaction.Commit();
        return result;
    }

    public ImageInspectionCompletion CompleteManifest(string inspectionRunId)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var run = ReadRun(connection, transaction, inspectionRunId, byImportEntry: false);
        if (run.Status == "completed")
        {
            transaction.Commit();
            return new ImageInspectionCompletion("completed", run.ImageId, null, ReusedExisting: true);
        }

        if (run.Status != "recording" || run.FrameInventoryJson is null ||
            run.FrameInventorySha256 is null || run.NormalizedContentSha256 is null ||
            run.NormalizedContentByteLength is null || run.NormalizedObjectKey is null ||
            run.NormalizationAction is null || run.PrimaryFrameIndex is null)
        {
            throw new BusinessCatalogException("image_inspection_state_conflict", "The image inspection is not ready to record its manifest.");
        }

        var probeResult = DeserializeInventory(run.FrameInventoryJson);
        ValidateProbeIdentity(probeResult);
        var primaryFrame = probeResult.Frames.SingleOrDefault(frame => frame.FrameIndex == run.PrimaryFrameIndex.Value)
            ?? throw new BusinessCatalogException("image_manifest_conflict", "The persisted primary frame is missing from the frame inventory.");
        var normalizedFileObjectId = InsertOrReuseNormalizedObject(
            connection,
            transaction,
            run.NormalizedContentSha256,
            run.NormalizedContentByteLength.Value,
            run.NormalizedObjectKey);

        var existingImageId = ReadImageId(connection, transaction, run);
        if (existingImageId is not null)
        {
            EnsureExistingManifest(connection, transaction, run, existingImageId, probeResult);
            CompleteRun(connection, transaction, run.InspectionRunId, existingImageId);
            transaction.Commit();
            return new ImageInspectionCompletion("completed", existingImageId, null, ReusedExisting: true);
        }

        var now = UtcNowText();
        var imageId = NewId("image");
        using (var insert = Command(
            connection,
            transaction,
            """
            INSERT INTO images(
                image_id, dataset_version_id, source_file_object_id, normalized_file_object_id,
                import_source_key, sort_index, content_container, primary_frame_index,
                width, height, image_state, metadata_state, raw_metadata_json, created_at_utc,
                import_entry_id, inspection_run_id, parser_schema, parser_profile,
                product_parser, product_parser_version, native_decoder, native_decoder_version,
                main_frame_policy_version, frame_inventory_sha256)
            VALUES(
                $image_id, $dataset_version_id, $source_file_object_id, $normalized_file_object_id,
                $import_source_key, $sort_index, $content_container, $primary_frame_index,
                $width, $height, 'processing_input', 'not_parsed', NULL, $created_at_utc,
                $import_entry_id, $inspection_run_id, $parser_schema, $parser_profile,
                $product_parser, $product_parser_version, $native_decoder, $native_decoder_version,
                $main_frame_policy_version, $frame_inventory_sha256);
            """))
        {
            Add(insert, "$image_id", imageId);
            Add(insert, "$dataset_version_id", run.DatasetVersionId);
            Add(insert, "$source_file_object_id", run.SourceFileObjectId);
            Add(insert, "$normalized_file_object_id", normalizedFileObjectId);
            Add(insert, "$import_source_key", run.SourceEntryKey);
            Add(insert, "$sort_index", run.SortIndex);
            Add(insert, "$content_container", probeResult.Container);
            Add(insert, "$primary_frame_index", primaryFrame.FrameIndex);
            Add(insert, "$width", EffectiveWidth(primaryFrame));
            Add(insert, "$height", EffectiveHeight(primaryFrame));
            Add(insert, "$created_at_utc", now);
            Add(insert, "$import_entry_id", run.ImportEntryId);
            Add(insert, "$inspection_run_id", run.InspectionRunId);
            AddParserFields(insert, probeResult);
            Add(insert, "$main_frame_policy_version", MainFramePolicy);
            Add(insert, "$frame_inventory_sha256", run.FrameInventorySha256);
            insert.ExecuteNonQuery();
        }

        foreach (var frame in probeResult.Frames.OrderBy(frame => frame.FrameIndex))
        {
            var isPrimary = frame.FrameIndex == primaryFrame.FrameIndex;
            var frameId = NewId("image-frame");
            var action = isPrimary ? run.NormalizationAction : "not_selected";
            using (var insertFrame = Command(
                connection,
                transaction,
                """
                INSERT INTO image_frames(
                    image_frame_id, image_id, frame_index, frame_role, width, height,
                    decode_state, normalized_file_object_id, metadata_json,
                    frame_kind, byte_offset, byte_length, bits_per_channel, orientation,
                    effective_width, effective_height, normalization_action)
                VALUES(
                    $image_frame_id, $image_id, $frame_index, $frame_role, $width, $height,
                    'decoded', $normalized_file_object_id, NULL,
                    $frame_kind, $byte_offset, $byte_length, $bits_per_channel, $orientation,
                    $effective_width, $effective_height, $normalization_action);
                """))
            {
                Add(insertFrame, "$image_frame_id", frameId);
                Add(insertFrame, "$image_id", imageId);
                Add(insertFrame, "$frame_index", frame.FrameIndex);
                Add(insertFrame, "$frame_role", isPrimary ? "primary_photogrammetry" : "auxiliary");
                Add(insertFrame, "$width", frame.Width);
                Add(insertFrame, "$height", frame.Height);
                Add(insertFrame, "$normalized_file_object_id", isPrimary ? normalizedFileObjectId : null);
                Add(insertFrame, "$frame_kind", frame.FrameKind);
                Add(insertFrame, "$byte_offset", frame.ByteOffset);
                Add(insertFrame, "$byte_length", frame.ByteLength);
                Add(insertFrame, "$bits_per_channel", frame.BitsPerChannel);
                Add(insertFrame, "$orientation", frame.Orientation ?? 1);
                Add(insertFrame, "$effective_width", EffectiveWidth(frame));
                Add(insertFrame, "$effective_height", EffectiveHeight(frame));
                Add(insertFrame, "$normalization_action", action);
                insertFrame.ExecuteNonQuery();
            }

            InsertLineage(
                connection,
                transaction,
                frameId,
                run,
                probeResult,
                frame,
                isPrimary ? normalizedFileObjectId : null,
                action,
                now);
        }

        CompleteRun(connection, transaction, run.InspectionRunId, imageId);
        transaction.Commit();
        return new ImageInspectionCompletion("completed", imageId, null, ReusedExisting: false);
    }

    public ImageInspectionCompletion Block(string inspectionRunId, string failureCode)
    {
        failureCode = NormalizeIdentifier(failureCode, nameof(failureCode), 128);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadRun(connection, transaction, inspectionRunId, byImportEntry: false);
        if (current.Status == "blocked")
        {
            transaction.Commit();
            return new ImageInspectionCompletion("blocked", null, current.FailureCode, ReusedExisting: true);
        }

        if (current.Status == "completed")
        {
            throw new BusinessCatalogException("image_manifest_conflict", "A completed image inspection cannot become blocked.");
        }

        using var update = Command(
            connection,
            transaction,
            """
            UPDATE image_inspection_runs
            SET status = 'blocked', failure_code = $failure_code,
                updated_at_utc = $updated_at_utc, completed_at_utc = $completed_at_utc
            WHERE inspection_run_id = $inspection_run_id;
            """);
        var now = UtcNowText();
        Add(update, "$failure_code", failureCode);
        Add(update, "$updated_at_utc", now);
        Add(update, "$completed_at_utc", now);
        Add(update, "$inspection_run_id", inspectionRunId);
        update.ExecuteNonQuery();
        transaction.Commit();
        return new ImageInspectionCompletion("blocked", null, failureCode, ReusedExisting: false);
    }

    public void MarkInterrupted(string inspectionRunId)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadRun(connection, transaction, inspectionRunId, byImportEntry: false);
        if (current.Status is not ("completed" or "blocked" or "interrupted"))
        {
            UpdateStatus(connection, transaction, inspectionRunId, "interrupted");
        }

        transaction.Commit();
    }

    internal static ImageProbeCasImageFrame? SelectPrimaryFrame(ImageProbeCasImageResult result)
    {
        var candidates = result.Frames
            .Where(frame => frame.DecodeState == "decoded" && frame.Width > 0 && frame.Height > 0 &&
                            frame.BitsPerChannel is > 0 and <= 64 && frame.Orientation is >= 1 and <= 8)
            .Select(frame => (Frame: frame, Pixels: checked((long)frame.Width * frame.Height)))
            .Where(item => item.Pixels <= ImageProbeProtocol.MaximumCasPixelsPerFrame)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        return result.Container switch
        {
            "jpeg" => candidates.SingleOrDefault(item => item.Frame.FrameIndex == 0).Frame,
            "mpo" => candidates.OrderByDescending(item => item.Pixels)
                .ThenByDescending(item => item.Frame.FrameKind == "mp_primary_image")
                .ThenBy(item => item.Frame.FrameIndex).First().Frame,
            "tiff" => candidates.OrderByDescending(item => item.Pixels)
                .ThenBy(item => item.Frame.FrameIndex).First().Frame,
            _ => null
        };
    }

    internal static string SerializeInventory(ImageProbeCasImageResult result) =>
        JsonSerializer.Serialize(result, JsonOptions);

    internal static string InventorySha256(string inventoryJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inventoryJson))).ToLowerInvariant();

    private static ImageProbeCasImageResult DeserializeInventory(string inventoryJson)
    {
        try
        {
            return JsonSerializer.Deserialize<ImageProbeCasImageResult>(inventoryJson, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new BusinessCatalogException("image_manifest_conflict", "The persisted frame inventory is invalid.", exception);
        }
    }

    private SourceRow ReadCanonicalSource(SqliteConnection connection, SqliteTransaction transaction, string importEntryId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT e.import_entry_id, e.dataset_version_id, e.source_entry_key, e.sort_index,
                   e.file_object_id, f.content_hash, f.byte_length, f.object_key
            FROM image_import_entries e
            JOIN file_objects f ON f.file_object_id = e.file_object_id
            JOIN file_object_roles r ON r.file_object_id = f.file_object_id AND r.object_role = 'source_image'
            WHERE e.import_entry_id = $import_entry_id
              AND e.status = 'available' AND e.canonical_entry_id IS NULL
              AND f.storage_state = 'available';
            """);
        Add(command, "$import_entry_id", importEntryId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException("image_import_entry_not_available", "Image inspection requires a canonical available source image entry.");
        }

        return new SourceRow(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetString(7));
    }

    private static ImageInspectionRunSnapshot ReadRun(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string identity,
        bool byImportEntry)
    {
        using var command = Command(
            connection,
            transaction,
            $"""
            SELECT r.inspection_run_id, r.import_entry_id, r.dataset_version_id,
                   e.source_entry_key, e.sort_index, r.source_file_object_id,
                   f.content_hash, f.byte_length, f.object_key, r.status,
                   r.content_container, r.primary_frame_index, r.frame_count,
                   r.frame_inventory_json, r.frame_inventory_sha256, r.normalization_action,
                   r.normalized_stage_id, r.normalized_stage_sha256, r.normalized_stage_byte_length,
                   r.normalized_stage_created_at_utc, r.normalized_content_sha256,
                   r.normalized_content_byte_length, r.normalized_object_key,
                   r.image_id, r.failure_code
            FROM image_inspection_runs r
            JOIN image_import_entries e ON e.import_entry_id = r.import_entry_id
            JOIN file_objects f ON f.file_object_id = r.source_file_object_id
            WHERE {(byImportEntry ? "r.import_entry_id" : "r.inspection_run_id")} = $identity;
            """);
        Add(command, "$identity", identity);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException("image_inspection_not_found", "The image inspection was not found.");
        }

        return new ImageInspectionRunSnapshot(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
            reader.GetString(5), reader.GetString(6), reader.GetInt64(7), reader.GetString(8), reader.GetString(9),
            StringOrNull(reader, 10), IntOrNull(reader, 11), IntOrNull(reader, 12), StringOrNull(reader, 13),
            StringOrNull(reader, 14), StringOrNull(reader, 15), StringOrNull(reader, 16), StringOrNull(reader, 17),
            LongOrNull(reader, 18), DateTimeOffsetOrNull(reader, 19), StringOrNull(reader, 20), LongOrNull(reader, 21),
            StringOrNull(reader, 22), StringOrNull(reader, 23), StringOrNull(reader, 24));
    }

    private static void EnsureRunIdentity(ImageInspectionRunSnapshot run, SourceRow source)
    {
        if (run.ImportEntryId != source.ImportEntryId || run.DatasetVersionId != source.DatasetVersionId ||
            run.SourceFileObjectId != source.FileObjectId || run.SourceSha256 != source.Sha256 ||
            run.SourceByteLength != source.ByteLength || run.SourceObjectKey != source.ObjectKey)
        {
            throw new BusinessCatalogException("image_manifest_conflict", "The image inspection source identity no longer matches its canonical import entry.");
        }
    }

    private static void RequireProbeState(ImageInspectionRunSnapshot run)
    {
        if (run.Status != "probing")
        {
            throw new BusinessCatalogException("image_inspection_state_conflict", "The image inspection is not probing.");
        }
    }

    private static void RequireStage(ImageInspectionRunSnapshot run)
    {
        if (run.NormalizedStageId is null || run.NormalizedStageSha256 is null ||
            run.NormalizedStageByteLength is null || run.NormalizedStageCreatedAtUtc is null)
        {
            throw new BusinessCatalogException("image_stage_receipt_missing", "The image inspection stage receipt is incomplete.");
        }
    }

    private static void ValidateProbeIdentity(ImageProbeCasImageResult result)
    {
        if (result.SchemaVersion != ParserSchema || result.Profile != ParserProfile ||
            result.Parser.ProductParser != ProductParser || result.Parser.ProductParserVersion != ProductParserVersion ||
            result.Parser.NativeDecoder != NativeDecoder || result.Parser.NativeDecoderVersion != NativeDecoderVersion)
        {
            throw new BusinessCatalogException("image_probe_identity_conflict", "The image probe identity does not match the fixed inspection profile.");
        }
    }

    private static void AddProbeFields(
        SqliteCommand command,
        ImageProbeCasImageResult result,
        ImageProbeCasImageFrame primaryFrame,
        string inventoryJson,
        string inventorySha256,
        string normalizationAction)
    {
        Add(command, "$content_container", result.Container);
        Add(command, "$primary_frame_index", primaryFrame.FrameIndex);
        Add(command, "$frame_count", result.Frames.Count);
        Add(command, "$frame_inventory_json", inventoryJson);
        Add(command, "$frame_inventory_sha256", inventorySha256);
        Add(command, "$normalization_action", normalizationAction);
    }

    private static void AddParserFields(SqliteCommand command, ImageProbeCasImageResult result)
    {
        Add(command, "$parser_schema", result.SchemaVersion);
        Add(command, "$parser_profile", result.Profile);
        Add(command, "$product_parser", result.Parser.ProductParser);
        Add(command, "$product_parser_version", result.Parser.ProductParserVersion);
        Add(command, "$native_decoder", result.Parser.NativeDecoder);
        Add(command, "$native_decoder_version", result.Parser.NativeDecoderVersion);
    }

    private static string InsertOrReuseNormalizedObject(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sha256,
        long byteLength,
        string objectKey)
    {
        string fileObjectId;
        using (var select = Command(
            connection,
            transaction,
            """
            SELECT file_object_id, storage_state, object_key
            FROM file_objects
            WHERE hash_algorithm = 'sha256' AND content_hash = $content_hash AND byte_length = $byte_length;
            """))
        {
            Add(select, "$content_hash", sha256);
            Add(select, "$byte_length", byteLength);
            using var reader = select.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(1) != "available" || reader.GetString(2) != objectKey)
                {
                    throw new BusinessCatalogException("file_object_identity_conflict", "The normalized content identity conflicts with an unavailable or differently keyed object.");
                }

                fileObjectId = reader.GetString(0);
            }
            else
            {
                fileObjectId = NewId("file-object");
            }
        }

        if (!FileObjectExists(connection, transaction, fileObjectId))
        {
            using var insert = Command(
                connection,
                transaction,
                """
                INSERT INTO file_objects(
                    file_object_id, object_kind, hash_algorithm, content_hash, byte_length,
                    media_type, object_key, storage_state, created_at_utc, available_at_utc)
                VALUES(
                    $file_object_id, 'normalized_image_frame', 'sha256', $content_hash, $byte_length,
                    'image/jpeg', $object_key, 'available', $created_at_utc, $available_at_utc);
                """);
            var now = UtcNowText();
            Add(insert, "$file_object_id", fileObjectId);
            Add(insert, "$content_hash", sha256);
            Add(insert, "$byte_length", byteLength);
            Add(insert, "$object_key", objectKey);
            Add(insert, "$created_at_utc", now);
            Add(insert, "$available_at_utc", now);
            insert.ExecuteNonQuery();
        }

        InsertRole(connection, transaction, fileObjectId, "normalized_image_frame");
        return fileObjectId;
    }

    private static void InsertRole(SqliteConnection connection, SqliteTransaction transaction, string fileObjectId, string role)
    {
        using var insert = Command(
            connection,
            transaction,
            """
            INSERT INTO file_object_roles(file_object_id, object_role, created_at_utc)
            VALUES($file_object_id, $object_role, $created_at_utc)
            ON CONFLICT(file_object_id, object_role) DO NOTHING;
            """);
        Add(insert, "$file_object_id", fileObjectId);
        Add(insert, "$object_role", role);
        Add(insert, "$created_at_utc", UtcNowText());
        insert.ExecuteNonQuery();
    }

    private static bool FileObjectExists(SqliteConnection connection, SqliteTransaction transaction, string fileObjectId)
    {
        using var command = Command(connection, transaction, "SELECT 1 FROM file_objects WHERE file_object_id = $file_object_id;");
        Add(command, "$file_object_id", fileObjectId);
        return command.ExecuteScalar() is not null;
    }

    private static void InsertLineage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string frameId,
        ImageInspectionRunSnapshot run,
        ImageProbeCasImageResult probeResult,
        ImageProbeCasImageFrame frame,
        string? normalizedFileObjectId,
        string action,
        string now)
    {
        var normalizedHash = normalizedFileObjectId is null ? null : run.NormalizedContentSha256;
        var normalizedLength = normalizedFileObjectId is null ? null : run.NormalizedContentByteLength;
        var identity = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sourceFileObjectId"] = run.SourceFileObjectId,
            ["parserSchema"] = probeResult.SchemaVersion,
            ["parserProfile"] = probeResult.Profile,
            ["productParser"] = probeResult.Parser.ProductParser,
            ["productParserVersion"] = probeResult.Parser.ProductParserVersion,
            ["nativeDecoder"] = probeResult.Parser.NativeDecoder,
            ["nativeDecoderVersion"] = probeResult.Parser.NativeDecoderVersion,
            ["frameIndex"] = frame.FrameIndex,
            ["byteOffset"] = frame.ByteOffset,
            ["byteLength"] = frame.ByteLength,
            ["mainFramePolicy"] = MainFramePolicy,
            ["normalizationAction"] = action
        };
        var lineageSha256 = Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions))).ToLowerInvariant();
        using var insert = Command(
            connection,
            transaction,
            """
            INSERT INTO image_frame_lineage(
                image_frame_lineage_id, image_frame_id, source_file_object_id, normalized_file_object_id,
                source_frame_index, normalization_action, parser_schema, parser_profile,
                product_parser, product_parser_version, native_decoder, native_decoder_version,
                main_frame_policy_version, byte_offset, byte_length,
                source_content_hash_snapshot, source_byte_length_snapshot,
                normalized_content_hash_snapshot, normalized_byte_length_snapshot,
                lineage_sha256, created_at_utc)
            VALUES(
                $lineage_id, $image_frame_id, $source_file_object_id, $normalized_file_object_id,
                $source_frame_index, $normalization_action, $parser_schema, $parser_profile,
                $product_parser, $product_parser_version, $native_decoder, $native_decoder_version,
                $main_frame_policy_version, $byte_offset, $byte_length,
                $source_content_hash_snapshot, $source_byte_length_snapshot,
                $normalized_content_hash_snapshot, $normalized_byte_length_snapshot,
                $lineage_sha256, $created_at_utc);
            """);
        Add(insert, "$lineage_id", NewId("image-lineage"));
        Add(insert, "$image_frame_id", frameId);
        Add(insert, "$source_file_object_id", run.SourceFileObjectId);
        Add(insert, "$normalized_file_object_id", normalizedFileObjectId);
        Add(insert, "$source_frame_index", frame.FrameIndex);
        Add(insert, "$normalization_action", action);
        AddParserFields(insert, probeResult);
        Add(insert, "$main_frame_policy_version", MainFramePolicy);
        Add(insert, "$byte_offset", frame.ByteOffset);
        Add(insert, "$byte_length", frame.ByteLength);
        Add(insert, "$source_content_hash_snapshot", run.SourceSha256);
        Add(insert, "$source_byte_length_snapshot", run.SourceByteLength);
        Add(insert, "$normalized_content_hash_snapshot", normalizedHash);
        Add(insert, "$normalized_byte_length_snapshot", normalizedLength);
        Add(insert, "$lineage_sha256", lineageSha256);
        Add(insert, "$created_at_utc", now);
        insert.ExecuteNonQuery();
    }

    private static void CompleteRun(SqliteConnection connection, SqliteTransaction transaction, string inspectionRunId, string imageId)
    {
        using var update = Command(
            connection,
            transaction,
            """
            UPDATE image_inspection_runs
            SET status = 'completed', image_id = $image_id,
                updated_at_utc = $updated_at_utc, completed_at_utc = $completed_at_utc
            WHERE inspection_run_id = $inspection_run_id;
            """);
        var now = UtcNowText();
        Add(update, "$image_id", imageId);
        Add(update, "$updated_at_utc", now);
        Add(update, "$completed_at_utc", now);
        Add(update, "$inspection_run_id", inspectionRunId);
        update.ExecuteNonQuery();
    }

    private static string? ReadImageId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImageInspectionRunSnapshot run)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT image_id
            FROM images
            WHERE inspection_run_id = $inspection_run_id
               OR (dataset_version_id = $dataset_version_id AND source_file_object_id = $source_file_object_id)
            ORDER BY CASE WHEN inspection_run_id = $inspection_run_id THEN 0 ELSE 1 END
            LIMIT 1;
            """);
        Add(command, "$inspection_run_id", run.InspectionRunId);
        Add(command, "$dataset_version_id", run.DatasetVersionId);
        Add(command, "$source_file_object_id", run.SourceFileObjectId);
        return command.ExecuteScalar() as string;
    }

    private static void EnsureExistingManifest(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImageInspectionRunSnapshot run,
        string imageId,
        ImageProbeCasImageResult result)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT count(*) FROM images i
            WHERE i.image_id = $image_id AND i.dataset_version_id = $dataset_version_id
              AND i.source_file_object_id = $source_file_object_id
              AND i.content_container = $content_container
              AND i.primary_frame_index = $primary_frame_index
              AND i.frame_inventory_sha256 = $frame_inventory_sha256
              AND (SELECT count(*) FROM image_frames f WHERE f.image_id = i.image_id) = $frame_count;
            """);
        Add(command, "$image_id", imageId);
        Add(command, "$dataset_version_id", run.DatasetVersionId);
        Add(command, "$source_file_object_id", run.SourceFileObjectId);
        Add(command, "$content_container", result.Container);
        Add(command, "$primary_frame_index", run.PrimaryFrameIndex);
        Add(command, "$frame_inventory_sha256", run.FrameInventorySha256);
        Add(command, "$frame_count", result.Frames.Count);
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
        {
            throw new BusinessCatalogException("image_manifest_conflict", "The existing image manifest does not match the inspection ledger.");
        }
    }

    private static void UpdateStatus(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string inspectionRunId,
        string status)
    {
        using var update = Command(
            connection,
            transaction,
            "UPDATE image_inspection_runs SET status = $status, updated_at_utc = $updated_at_utc WHERE inspection_run_id = $inspection_run_id;");
        Add(update, "$status", status);
        Add(update, "$updated_at_utc", UtcNowText());
        Add(update, "$inspection_run_id", inspectionRunId);
        update.ExecuteNonQuery();
    }

    private static int EffectiveWidth(ImageProbeCasImageFrame frame) =>
        frame.Orientation is 5 or 6 or 7 or 8 ? frame.Height : frame.Width;

    private static int EffectiveHeight(ImageProbeCasImageFrame frame) =>
        frame.Orientation is 5 or 6 or 7 or 8 ? frame.Width : frame.Height;

    private static string ObjectKey(string sha256) => $"sha256/{sha256[..2]}/{sha256}";
    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
    private static string UtcNowText() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? StringOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static int? IntOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static long? LongOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static DateTimeOffset? DateTimeOffsetOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string NormalizeIdentifier(string value, string fieldName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new BusinessCatalogException("invalid_parameters", $"{fieldName} is required.");
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
        {
            throw new BusinessCatalogException("invalid_parameters", $"{fieldName} contains unsupported identifier characters.");
        }

        return normalized;
    }

    private sealed record SourceRow(
        string ImportEntryId,
        string DatasetVersionId,
        string SourceEntryKey,
        int SortIndex,
        string FileObjectId,
        string Sha256,
        long ByteLength,
        string ObjectKey);
}
