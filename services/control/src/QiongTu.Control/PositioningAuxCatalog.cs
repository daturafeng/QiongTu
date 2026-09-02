using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control;

internal sealed record PositioningAuxImportWorkItem(
    string ItemId,
    string RunId,
    string ImportSessionId,
    string DatasetVersionId,
    string SourcePreflightItemId,
    string SourceEntryKey,
    string DisplayName,
    int SortIndex,
    string AuxiliaryType,
    long ByteLengthSnapshot,
    DateTimeOffset? SourceLastWriteTimeUtc,
    string SourceIdentityKey,
    int AssociationItemCount,
    string Status,
    string? FailureCode,
    ObjectStageReceipt? StageReceipt,
    string? ExpectedContentHash,
    long? ExpectedByteLength,
    string? ExpectedObjectKey,
    string? FileObjectId,
    string? PositioningAuxFileId);

internal sealed record PositioningAuxStageReceipt(
    string ItemId,
    string StageId,
    string Sha256,
    long ByteLength,
    DateTimeOffset CreatedAtUtc);

internal sealed record PositioningAuxImportCompletion(
    string Status,
    string? PositioningAuxFileId,
    string? FailureCode,
    bool ReusedExisting);

public sealed class PositioningAuxCatalog
{
    public const int DefaultPageSize = BusinessCatalog.DefaultPageSize;
    public const int MaximumPageSize = BusinessCatalog.MaximumPageSize;
    public const int MaximumCatalogPayloadBytes = BusinessCatalog.MaximumCatalogPayloadBytes;

    internal const string AssociationProfile = "positioning-aux-import.v1";
    internal const string AssociationPolicyVersion = "positioning-aux-association.v1";
    internal const string ParserSchema = ImageProbeProtocol.CasPositioningAuxV1;
    internal const string ParserProfile = ImageProbeProtocol.CasPositioningAuxProfile;
    internal const string ParserName = "qiongtu.cas-positioning-aux";
    internal const string ParserVersion = "1.0.0";
    internal const string DjiMrkParserName = "dji-mrk";
    internal const string RinexCandidateParserName = "rinex-candidate";
    internal const string RtcmCandidateParserName = "rtcm3-candidate";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly PositioningAuxPrivacy ResponsePrivacy = new(
        PathsIncluded: false,
        LocatorsIncluded: false,
        SourceKeysIncluded: false,
        HashesIncluded: false,
        ObjectKeysIncluded: false,
        StageReceiptsIncluded: false,
        RawRecordsIncluded: false,
        CoordinatesIncluded: false,
        TimestampsIncluded: false,
        OwnerSampleStatisticsIncluded: false);

    private readonly BusinessDatabase _database;
    private readonly int _maximumResponseBytes;

    public PositioningAuxCatalog(
        BusinessDatabase database,
        int maximumResponseBytes = MaximumCatalogPayloadBytes)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        if (maximumResponseBytes is <= 0 or > NamedPipeControlServer.MaximumResponseBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        }

        _maximumResponseBytes = maximumResponseBytes;
    }

    internal PositioningAuxImportRun EnsureRunForCompletedPreflight(
        string sourcePreflightRunId,
        IEnumerable<PositioningAuxAssociationBinding> associationBindings)
    {
        sourcePreflightRunId = NormalizeId(sourcePreflightRunId, nameof(sourcePreflightRunId));
        ArgumentNullException.ThrowIfNull(associationBindings);
        var bindings = NormalizeAssociationBindings(associationBindings);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var gate = ReadCompletedDjiGate(connection, transaction, sourcePreflightRunId);
        var existing = TryReadRunForGate(connection, transaction, gate);
        if (existing is not null)
        {
            EnsureExistingAssociationBindings(connection, transaction, existing.RunId, bindings);
            transaction.Commit();
            return existing;
        }

        var now = UtcNowText();
        var sidecars = ReadCompletedSidecarCandidates(connection, transaction, gate, bindings);
        var runId = NewId("positioning-aux-run");
        using (var insert = Command(
            connection,
            transaction,
            """
            INSERT INTO positioning_aux_import_runs(
                positioning_aux_import_run_id, import_session_id, dataset_version_id,
                source_preflight_run_id, association_policy_version, parser_profile,
                parser_version, status, total_item_count, completed_item_count,
                failed_item_count, created_at_utc, updated_at_utc, completed_at_utc)
            VALUES(
                $run_id, $import_session_id, $dataset_version_id, $source_preflight_run_id,
                $association_policy_version, $parser_profile, $parser_version,
                $status, $total_item_count, 0, 0, $created_at_utc, $updated_at_utc,
                $completed_at_utc);
            """))
        {
            Add(insert, "$run_id", runId);
            Add(insert, "$import_session_id", gate.ImportSessionId);
            Add(insert, "$dataset_version_id", gate.DatasetVersionId);
            Add(insert, "$source_preflight_run_id", gate.SourcePreflightRunId);
            Add(insert, "$association_policy_version", AssociationPolicyVersion);
            Add(insert, "$parser_profile", ParserProfile);
            Add(insert, "$parser_version", ParserVersion);
            Add(insert, "$status", sidecars.Count == 0 ? "completed" : "pending");
            Add(insert, "$total_item_count", sidecars.Count);
            Add(insert, "$created_at_utc", now);
            Add(insert, "$updated_at_utc", now);
            Add(insert, "$completed_at_utc", sidecars.Count == 0 ? now : null);
            insert.ExecuteNonQuery();
        }

        foreach (var candidate in sidecars)
        {
            using var insert = Command(
                connection,
                transaction,
                """
                INSERT INTO positioning_aux_import_items(
                    positioning_aux_import_item_id, positioning_aux_import_run_id,
                    source_preflight_item_id, source_entry_key, display_name, sort_index,
                    auxiliary_type, byte_length_snapshot, source_last_write_time_utc,
                    source_identity_key, association_item_count, status, created_at_utc,
                    updated_at_utc)
                VALUES(
                    $item_id, $run_id, $source_preflight_item_id, $source_entry_key,
                    $display_name, $sort_index, $auxiliary_type, $byte_length_snapshot,
                    $source_last_write_time_utc, $source_identity_key,
                    $association_item_count, 'pending', $created_at_utc, $updated_at_utc);
                """);
            Add(insert, "$item_id", NewId("positioning-aux-item"));
            Add(insert, "$run_id", runId);
            Add(insert, "$source_preflight_item_id", candidate.SourcePreflightItemId);
            Add(insert, "$source_entry_key", candidate.SourceEntryKey);
            Add(insert, "$display_name", candidate.DisplayName);
            Add(insert, "$sort_index", candidate.SortIndex);
            Add(insert, "$auxiliary_type", candidate.AuxiliaryType);
            Add(insert, "$byte_length_snapshot", candidate.ByteLengthSnapshot);
            Add(insert, "$source_last_write_time_utc", candidate.SourceLastWriteTimeUtcText);
            Add(insert, "$source_identity_key", candidate.SourceIdentityKey);
            Add(insert, "$association_item_count", candidate.AssociationItemCount);
            Add(insert, "$created_at_utc", now);
            Add(insert, "$updated_at_utc", now);
            insert.ExecuteNonQuery();
        }

        RefreshRunCounts(connection, transaction, runId, now);
        var result = ReadRun(connection, transaction, runId);
        transaction.Commit();
        EnsureResponseWithinLimit(result);
        return result;
    }

    public PositioningAuxImportRun Get(PositioningAuxImportGetParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var runId = NormalizeId(parameters.RunId, nameof(parameters.RunId));
        using var connection = _database.OpenConnection();
        var result = ReadRun(connection, null, runId);
        EnsureResponseWithinLimit(result);
        return result;
    }

    public PositioningAuxImportRun Resume(
        string requestId,
        PositioningAuxImportResumeParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var normalized = parameters with
        {
            RunId = NormalizeId(parameters.RunId, nameof(parameters.RunId)),
            SourceRootPath = NormalizeRequiredText(parameters.SourceRootPath, nameof(parameters.SourceRootPath), 4096)
        };
        return ExecuteIdempotent(
            requestId,
            ControlMethods.PositioningAuxImportResume,
            normalized,
            (connection, transaction) =>
            {
                var current = ReadRun(connection, transaction, normalized.RunId);
                if (current.Status is "completed" or "blocked" or "cancelled" or "running")
                {
                    return current;
                }

                if (current.Status is not ("pending" or "interrupted"))
                {
                    throw new BusinessCatalogException(
                        "positioning_aux_import_not_resumable",
                        "The positioning auxiliary import run cannot be resumed.");
                }

                var now = UtcNowText();
                using var update = Command(
                    connection,
                    transaction,
                    """
                    UPDATE positioning_aux_import_runs
                    SET status = 'running', started_at_utc = COALESCE(started_at_utc, $now),
                        last_error_code = NULL, updated_at_utc = $now
                    WHERE positioning_aux_import_run_id = $run_id
                      AND status IN ('pending', 'interrupted');
                    """);
                Add(update, "$now", now);
                Add(update, "$run_id", normalized.RunId);
                if (update.ExecuteNonQuery() != 1)
                {
                    throw new BusinessCatalogException(
                        "positioning_aux_import_not_resumable",
                        "The positioning auxiliary import run could not resume.");
                }

                RefreshRunCounts(connection, transaction, normalized.RunId, now);
                return ReadRun(connection, transaction, normalized.RunId);
            });
    }

    public PositioningAuxImportRun Cancel(
        string requestId,
        PositioningAuxImportCancelParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var normalized = parameters with { RunId = NormalizeId(parameters.RunId, nameof(parameters.RunId)) };
        return ExecuteIdempotent(
            requestId,
            ControlMethods.PositioningAuxImportCancel,
            normalized,
            (connection, transaction) =>
            {
                var current = ReadRun(connection, transaction, normalized.RunId);
                if (current.Status is "completed" or "blocked" or "cancelled")
                {
                    return current;
                }

                var now = UtcNowText();
                using (var blockItems = Command(
                    connection,
                    transaction,
                    """
                    UPDATE positioning_aux_import_items
                    SET status = 'blocked', failure_code = 'cancelled_by_user',
                        updated_at_utc = $now, terminal_at_utc = $now
                    WHERE positioning_aux_import_run_id = $run_id
                      AND status NOT IN ('completed', 'blocked');
                    """))
                {
                    Add(blockItems, "$now", now);
                    Add(blockItems, "$run_id", normalized.RunId);
                    blockItems.ExecuteNonQuery();
                }

                using (var cancel = Command(
                    connection,
                    transaction,
                    """
                    UPDATE positioning_aux_import_runs
                    SET total_item_count = (
                            SELECT count(*) FROM positioning_aux_import_items
                            WHERE positioning_aux_import_run_id = $run_id),
                        completed_item_count = (
                            SELECT count(*) FROM positioning_aux_import_items
                            WHERE positioning_aux_import_run_id = $run_id AND status = 'completed'),
                        failed_item_count = (
                            SELECT count(*) FROM positioning_aux_import_items
                            WHERE positioning_aux_import_run_id = $run_id AND status = 'blocked'),
                        status = 'cancelled',
                        last_error_code = 'cancelled_by_user',
                        updated_at_utc = $now, cancelled_at_utc = $now
                    WHERE positioning_aux_import_run_id = $run_id
                      AND status IN ('pending', 'running', 'interrupted');
                    """))
                {
                    Add(cancel, "$now", now);
                    Add(cancel, "$run_id", normalized.RunId);
                    if (cancel.ExecuteNonQuery() != 1)
                    {
                        throw new BusinessCatalogException(
                            "positioning_aux_import_not_cancellable",
                        "The positioning auxiliary import run could not be cancelled.");
                    }
                }

                return ReadRun(connection, transaction, normalized.RunId);
            });
    }

    public PageResult<PositioningAuxFile> ListFiles(PositioningAuxFileListParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var datasetVersionId = NormalizeOptionalId(parameters.DatasetVersionId, nameof(parameters.DatasetVersionId));
        var runId = NormalizeOptionalId(parameters.RunId, nameof(parameters.RunId));
        if (datasetVersionId is null && runId is null)
        {
            throw new BusinessCatalogException(
                "positioning_aux_filter_required",
                "A dataset version or positioning auxiliary import run filter is required.");
        }

        var page = NormalizePage(parameters.PageSize, parameters.Cursor);
        var scope = $"{datasetVersionId ?? string.Empty}|{runId ?? string.Empty}";
        var cursor = DecodeCursor(page.Cursor, ControlMethods.PositioningAuxFileList, scope);
        using var connection = _database.OpenConnection();
        using var command = Command(
            connection,
            null,
            """
            SELECT p.positioning_aux_file_id, p.created_at_utc
            FROM positioning_aux_files p
            JOIN positioning_aux_import_items item
              ON item.positioning_aux_import_item_id = p.positioning_aux_import_item_id
            WHERE ($dataset_version_id IS NULL OR p.dataset_version_id = $dataset_version_id)
              AND ($run_id IS NULL OR item.positioning_aux_import_run_id = $run_id)
              AND ($has_cursor = 0 OR
                   p.created_at_utc < $cursor_position OR
                   (p.created_at_utc = $cursor_position AND p.positioning_aux_file_id < $cursor_id))
            ORDER BY p.created_at_utc DESC, p.positioning_aux_file_id DESC
            LIMIT $limit;
            """);
        Add(command, "$dataset_version_id", datasetVersionId);
        Add(command, "$run_id", runId);
        Add(command, "$has_cursor", cursor is null ? 0 : 1);
        Add(command, "$cursor_position", cursor?.Position);
        Add(command, "$cursor_id", cursor?.Id);
        Add(command, "$limit", page.PageSize + 1);
        var rows = new List<(PositioningAuxFile Item, string Position, string Id)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                rows.Add((ReadFile(connection, null, id), reader.GetString(1), id));
            }
        }

        var result = ToPage(rows, page.PageSize, ControlMethods.PositioningAuxFileList, scope);
        EnsureResponseWithinLimit(result);
        return result;
    }

    internal PositioningAuxImportRun MarkRunning(string runId)
    {
        runId = NormalizeId(runId, nameof(runId));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadRun(connection, transaction, runId);
        if (current.Status == "running")
        {
            transaction.Commit();
            return current;
        }

        if (current.Status is not ("pending" or "interrupted"))
        {
            throw new BusinessCatalogException(
                "positioning_aux_import_not_runnable",
                "The positioning auxiliary import run cannot be started.");
        }

        var now = UtcNowText();
        using var update = Command(
            connection,
            transaction,
            """
            UPDATE positioning_aux_import_runs
            SET status = 'running', started_at_utc = COALESCE(started_at_utc, $now),
                last_error_code = NULL, updated_at_utc = $now
            WHERE positioning_aux_import_run_id = $run_id;
            """);
        Add(update, "$now", now);
        Add(update, "$run_id", runId);
        update.ExecuteNonQuery();
        var result = ReadRun(connection, transaction, runId);
        transaction.Commit();
        return result;
    }

    internal PositioningAuxImportWorkItem MarkStaging(string itemId)
    {
        itemId = NormalizeId(itemId, nameof(itemId));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var item = ReadWorkItem(connection, transaction, itemId);
        if (item.Status is not ("pending" or "interrupted"))
        {
            throw new BusinessCatalogException(
                "positioning_aux_item_not_stageable",
                "The positioning auxiliary item cannot begin staging.");
        }

        EnsureRunIsRunning(connection, transaction, item.RunId);
        var now = UtcNowText();
        using var update = Command(
            connection,
            transaction,
            """
            UPDATE positioning_aux_import_items
            SET status = 'staging', failure_code = NULL, updated_at_utc = $now
            WHERE positioning_aux_import_item_id = $item_id;
            """);
        Add(update, "$now", now);
        Add(update, "$item_id", itemId);
        update.ExecuteNonQuery();
        var result = ReadWorkItem(connection, transaction, itemId);
        transaction.Commit();
        return result;
    }

    internal PositioningAuxImportWorkItem RecordStageReceipt(PositioningAuxStageReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var normalized = receipt with
        {
            ItemId = NormalizeId(receipt.ItemId, nameof(receipt.ItemId)),
            StageId = NormalizeIdentifier(receipt.StageId, nameof(receipt.StageId), 128),
            Sha256 = NormalizeSha256(receipt.Sha256, nameof(receipt.Sha256))
        };
        if (normalized.ByteLength < 0)
        {
            throw new BusinessCatalogException("invalid_parameters", "The positioning auxiliary stage byte length is invalid.");
        }

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var item = ReadWorkItem(connection, transaction, normalized.ItemId);
        if (item.Status != "staging")
        {
            throw new BusinessCatalogException(
                "positioning_aux_item_not_staging",
                "The positioning auxiliary item is not staging.");
        }

        var now = UtcNowText();
        using var update = Command(
            connection,
            transaction,
            """
            UPDATE positioning_aux_import_items
            SET status = 'staged', stage_id = $stage_id, stage_sha256 = $stage_sha256,
                stage_byte_length = $stage_byte_length,
                stage_created_at_utc = $stage_created_at_utc, updated_at_utc = $now
            WHERE positioning_aux_import_item_id = $item_id;
            """);
        Add(update, "$stage_id", normalized.StageId);
        Add(update, "$stage_sha256", normalized.Sha256);
        Add(update, "$stage_byte_length", normalized.ByteLength);
        Add(update, "$stage_created_at_utc", normalized.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        Add(update, "$now", now);
        Add(update, "$item_id", normalized.ItemId);
        update.ExecuteNonQuery();
        var result = ReadWorkItem(connection, transaction, normalized.ItemId);
        transaction.Commit();
        return result;
    }

    internal PositioningAuxImportWorkItem MarkPublishing(string itemId, string expectedSha256, long expectedByteLength)
    {
        itemId = NormalizeId(itemId, nameof(itemId));
        expectedSha256 = NormalizeSha256(expectedSha256, nameof(expectedSha256));
        if (expectedByteLength < 0)
        {
            throw new BusinessCatalogException("invalid_parameters", "The expected positioning auxiliary byte length is invalid.");
        }

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var item = ReadWorkItem(connection, transaction, itemId);
        if (item.Status is not ("staged" or "interrupted"))
        {
            throw new BusinessCatalogException(
                "positioning_aux_item_not_publishable",
                "The positioning auxiliary item cannot begin publishing.");
        }

        if (item.StageReceipt is null)
        {
            throw new BusinessCatalogException(
                "positioning_aux_stage_missing",
                "The positioning auxiliary item does not have a recoverable stage receipt.");
        }

        var now = UtcNowText();
        using var update = Command(
            connection,
            transaction,
            """
            UPDATE positioning_aux_import_items
            SET status = 'publishing', expected_content_hash = $expected_hash,
                expected_byte_length = $expected_byte_length,
                expected_object_key = $expected_object_key, failure_code = NULL,
                updated_at_utc = $now
            WHERE positioning_aux_import_item_id = $item_id;
            """);
        Add(update, "$expected_hash", expectedSha256);
        Add(update, "$expected_byte_length", expectedByteLength);
        Add(update, "$expected_object_key", ObjectKey(expectedSha256));
        Add(update, "$now", now);
        Add(update, "$item_id", itemId);
        update.ExecuteNonQuery();
        var result = ReadWorkItem(connection, transaction, itemId);
        transaction.Commit();
        return result;
    }

    internal PositioningAuxImportCompletion CompletePublishedRetention(
        string itemId,
        string sha256,
        long byteLength,
        string? mediaType = null)
    {
        itemId = NormalizeId(itemId, nameof(itemId));
        sha256 = NormalizeSha256(sha256, nameof(sha256));
        if (byteLength < 0)
        {
            throw new BusinessCatalogException("invalid_parameters", "The positioning auxiliary byte length is invalid.");
        }

        mediaType = NormalizeOptionalText(mediaType, nameof(mediaType), 128);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var item = ReadWorkItem(connection, transaction, itemId);
        if (item.Status == "completed" && item.PositioningAuxFileId is not null)
        {
            transaction.Commit();
            return new PositioningAuxImportCompletion("completed", item.PositioningAuxFileId, null, ReusedExisting: true);
        }

        if (item.Status == "retained" && item.PositioningAuxFileId is not null)
        {
            transaction.Commit();
            return new PositioningAuxImportCompletion("retained", item.PositioningAuxFileId, null, ReusedExisting: true);
        }

        if (item.Status != "publishing")
        {
            throw new BusinessCatalogException(
                "positioning_aux_item_not_publishing",
                "The positioning auxiliary item must be publishing before it can be retained.");
        }

        if (item.ExpectedContentHash != sha256 ||
            item.ExpectedByteLength != byteLength ||
            item.ExpectedObjectKey != ObjectKey(sha256))
        {
            throw new BusinessCatalogException(
                "positioning_aux_object_identity_conflict",
                "The published positioning auxiliary object does not match the import ledger.");
        }

        var fileObjectId = InsertOrReusePositioningAuxObject(
            connection,
            transaction,
            sha256,
            byteLength,
            mediaType ?? DefaultMediaType(item.AuxiliaryType));
        var now = UtcNowText();
        using (var bindObject = Command(
            connection,
            transaction,
            """
            UPDATE positioning_aux_import_items
            SET file_object_id = $file_object_id, updated_at_utc = $now
            WHERE positioning_aux_import_item_id = $item_id;
            """))
        {
            Add(bindObject, "$file_object_id", fileObjectId);
            Add(bindObject, "$now", now);
            Add(bindObject, "$item_id", item.ItemId);
            bindObject.ExecuteNonQuery();
        }

        var auxFileId = TryReadFileIdForItem(connection, transaction, item.ItemId);
        var reused = auxFileId is not null;
        if (auxFileId is null)
        {
            auxFileId = NewId("positioning-aux-file");
            var parse = InitialParseState(item.AuxiliaryType);
            using var insertFile = Command(
                connection,
                transaction,
                """
                INSERT INTO positioning_aux_files(
                    positioning_aux_file_id, dataset_version_id, import_session_id,
                    positioning_aux_import_item_id, source_preflight_item_id,
                    file_object_id, auxiliary_type, association_policy_version,
                    association_evidence_json, retention_state, parse_state, quality_state,
                    parser_schema, parser_profile, parser_name, parser_version,
                    created_at_utc, updated_at_utc)
                VALUES(
                    $file_id, $dataset_version_id, $import_session_id, $item_id,
                    $source_preflight_item_id, $file_object_id, $auxiliary_type,
                    $association_policy_version, $association_evidence_json, 'retained',
                    $parse_state, $quality_state, $parser_schema, $parser_profile,
                    $parser_name, $parser_version, $created_at_utc, $updated_at_utc);
                """);
            Add(insertFile, "$file_id", auxFileId);
            Add(insertFile, "$dataset_version_id", item.DatasetVersionId);
            Add(insertFile, "$import_session_id", item.ImportSessionId);
            Add(insertFile, "$item_id", item.ItemId);
            Add(insertFile, "$source_preflight_item_id", item.SourcePreflightItemId);
            Add(insertFile, "$file_object_id", fileObjectId);
            Add(insertFile, "$auxiliary_type", item.AuxiliaryType);
            Add(insertFile, "$association_policy_version", AssociationPolicyVersion);
            Add(insertFile, "$association_evidence_json", AssociationEvidenceJson(item));
            Add(insertFile, "$parse_state", parse.ParseState);
            Add(insertFile, "$quality_state", parse.QualityState);
            Add(insertFile, "$parser_schema", parse.ParserSchema);
            Add(insertFile, "$parser_profile", parse.ParserProfile);
            Add(insertFile, "$parser_name", parse.ParserName);
            Add(insertFile, "$parser_version", parse.ParserVersion);
            Add(insertFile, "$created_at_utc", now);
            Add(insertFile, "$updated_at_utc", now);
            insertFile.ExecuteNonQuery();
        }

        SetItemStatus(connection, transaction, item.ItemId, "retained", null, now, auxFileId);
        if (item.AuxiliaryType != "mrk")
        {
            SetItemStatus(connection, transaction, item.ItemId, "completed", null, now, auxFileId);
        }

        RefreshRunCounts(connection, transaction, item.RunId, now);
        transaction.Commit();
        return new PositioningAuxImportCompletion(
            item.AuxiliaryType == "mrk" ? "retained" : "completed",
            auxFileId,
            null,
            reused);
    }

    internal PositioningAuxImportWorkItem BeginParsing(string itemId)
    {
        itemId = NormalizeId(itemId, nameof(itemId));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var item = ReadWorkItem(connection, transaction, itemId);
        if (item.AuxiliaryType != "mrk")
        {
            throw new BusinessCatalogException(
                "positioning_aux_parse_unsupported",
                "Only retained MRK positioning auxiliary files are parsed in the first version.");
        }

        if (item.Status == "completed")
        {
            transaction.Commit();
            return item;
        }

        if (item.Status is not ("retained" or "interrupted"))
        {
            throw new BusinessCatalogException(
                "positioning_aux_item_not_parseable",
                "The positioning auxiliary item cannot begin parsing.");
        }

        var now = UtcNowText();
        SetItemStatus(connection, transaction, item.ItemId, "parsing", null, now, item.PositioningAuxFileId);
        var result = ReadWorkItem(connection, transaction, itemId);
        transaction.Commit();
        return result;
    }

    internal PositioningAuxImportCompletion CompleteParsedMrk(
        string itemId,
        ImageProbeCasPositioningAuxResult result)
    {
        itemId = NormalizeId(itemId, nameof(itemId));
        ArgumentNullException.ThrowIfNull(result);
        ValidateMrkProbeResult(result);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var item = ReadWorkItem(connection, transaction, itemId);
        if (item.PositioningAuxFileId is null)
        {
            throw new BusinessCatalogException(
                "positioning_aux_file_not_retained",
                "The positioning auxiliary file has not been retained.");
        }

        var file = ReadFilePrivate(connection, transaction, item.PositioningAuxFileId);
        if (item.Status == "completed")
        {
            if (file.ParseInventorySha256 != result.CanonicalInventoryHash)
            {
                throw new BusinessCatalogException(
                    "positioning_aux_parse_inventory_conflict",
                    "The repeated MRK parse result conflicts with the retained inventory.");
            }

            transaction.Commit();
            return new PositioningAuxImportCompletion("completed", item.PositioningAuxFileId, null, ReusedExisting: true);
        }

        if (item.Status != "parsing" || file.ParseState != "not_attempted")
        {
            throw new BusinessCatalogException(
                "positioning_aux_item_not_parsing",
                "The positioning auxiliary item is not parsing.");
        }

        var now = UtcNowText();
        using (var updateFile = Command(
            connection,
            transaction,
            """
            UPDATE positioning_aux_files
            SET parse_state = 'parsed', quality_state = $quality_state,
                parser_schema = $parser_schema, parser_profile = $parser_profile,
                parser_name = $parser_name, parser_version = $parser_version,
                parse_inventory_sha256 = $inventory_sha256,
                parsed_summary_json = $summary_json, failure_code = NULL,
                updated_at_utc = $now, parsed_at_utc = $now
            WHERE positioning_aux_file_id = $file_id;
            """))
        {
            Add(updateFile, "$quality_state", result.QualityState);
            Add(updateFile, "$parser_schema", ParserSchema);
            Add(updateFile, "$parser_profile", ParserProfile);
            Add(updateFile, "$parser_name", DjiMrkParserName);
            Add(updateFile, "$parser_version", ParserVersion);
            Add(updateFile, "$inventory_sha256", result.CanonicalInventoryHash);
            Add(updateFile, "$summary_json", ParsedSummaryJson(result));
            Add(updateFile, "$now", now);
            Add(updateFile, "$file_id", item.PositioningAuxFileId);
            updateFile.ExecuteNonQuery();
        }

        SetItemStatus(connection, transaction, item.ItemId, "completed", null, now, item.PositioningAuxFileId);
        RefreshRunCounts(connection, transaction, item.RunId, now);
        transaction.Commit();
        return new PositioningAuxImportCompletion("completed", item.PositioningAuxFileId, null, ReusedExisting: false);
    }

    internal PositioningAuxImportCompletion BlockItem(string itemId, string failureCode)
    {
        itemId = NormalizeId(itemId, nameof(itemId));
        failureCode = NormalizeReasonCode(failureCode, nameof(failureCode));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var item = ReadWorkItem(connection, transaction, itemId);
        if (item.Status == "blocked")
        {
            transaction.Commit();
            return new PositioningAuxImportCompletion("blocked", item.PositioningAuxFileId, item.FailureCode, ReusedExisting: true);
        }

        if (item.Status == "completed")
        {
            throw new BusinessCatalogException(
                "positioning_aux_item_terminal",
                "Completed positioning auxiliary items are immutable.");
        }

        var now = UtcNowText();
        if (item.PositioningAuxFileId is not null)
        {
            var file = ReadFilePrivate(connection, transaction, item.PositioningAuxFileId);
            if (file.ParseState == "not_attempted")
            {
                using var updateFile = Command(
                    connection,
                    transaction,
                    """
                    UPDATE positioning_aux_files
                    SET parse_state = 'failed', quality_state = 'failed',
                        parser_schema = $parser_schema, parser_profile = $parser_profile,
                        parser_name = $parser_name, parser_version = $parser_version,
                        failure_code = $failure_code, updated_at_utc = $now
                    WHERE positioning_aux_file_id = $file_id;
                    """);
                Add(updateFile, "$parser_schema", ParserSchema);
                Add(updateFile, "$parser_profile", ParserProfile);
                Add(updateFile, "$parser_name", ParserName);
                Add(updateFile, "$parser_version", ParserVersion);
                Add(updateFile, "$failure_code", failureCode);
                Add(updateFile, "$now", now);
                Add(updateFile, "$file_id", item.PositioningAuxFileId);
                updateFile.ExecuteNonQuery();
            }
        }

        SetItemStatus(connection, transaction, item.ItemId, "blocked", failureCode, now, item.PositioningAuxFileId);
        RefreshRunCounts(connection, transaction, item.RunId, now);
        transaction.Commit();
        return new PositioningAuxImportCompletion("blocked", item.PositioningAuxFileId, failureCode, ReusedExisting: false);
    }

    internal PositioningAuxImportWorkItem MarkItemInterrupted(string itemId, string failureCode)
    {
        itemId = NormalizeId(itemId, nameof(itemId));
        failureCode = NormalizeReasonCode(failureCode, nameof(failureCode));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var item = ReadWorkItem(connection, transaction, itemId);
        if (item.Status is "completed" or "blocked")
        {
            transaction.Commit();
            return item;
        }

        var now = UtcNowText();
        SetItemStatus(connection, transaction, item.ItemId, "interrupted", failureCode, now, item.PositioningAuxFileId);
        using var interruptRun = Command(
            connection,
            transaction,
            """
            UPDATE positioning_aux_import_runs
            SET status = 'interrupted', last_error_code = $failure_code,
                updated_at_utc = $now
            WHERE positioning_aux_import_run_id = $run_id
              AND status IN ('pending', 'running', 'interrupted');
            """);
        Add(interruptRun, "$failure_code", failureCode);
        Add(interruptRun, "$now", now);
        Add(interruptRun, "$run_id", item.RunId);
        interruptRun.ExecuteNonQuery();
        RefreshRunCounts(connection, transaction, item.RunId, now);
        var result = ReadWorkItem(connection, transaction, itemId);
        transaction.Commit();
        return result;
    }

    internal IReadOnlyList<string> InterruptRunningRuns(string failureCode = "control_restarted")
    {
        failureCode = NormalizeReasonCode(failureCode, nameof(failureCode));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var runIds = new List<string>();
        using (var select = Command(
            connection,
            transaction,
            """
            SELECT positioning_aux_import_run_id
            FROM positioning_aux_import_runs
            WHERE status = 'running'
            ORDER BY positioning_aux_import_run_id;
            """))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                runIds.Add(reader.GetString(0));
            }
        }

        var now = UtcNowText();
        foreach (var runId in runIds)
        {
            using (var interruptItems = Command(
                connection,
                transaction,
                """
                UPDATE positioning_aux_import_items
                SET status = 'interrupted', failure_code = $failure_code,
                    updated_at_utc = $now
                WHERE positioning_aux_import_run_id = $run_id
                  AND status NOT IN ('completed', 'blocked');
                """))
            {
                Add(interruptItems, "$failure_code", failureCode);
                Add(interruptItems, "$now", now);
                Add(interruptItems, "$run_id", runId);
                interruptItems.ExecuteNonQuery();
            }

            using var interruptRun = Command(
                connection,
                transaction,
                """
                UPDATE positioning_aux_import_runs
                SET status = 'interrupted', last_error_code = $failure_code,
                    updated_at_utc = $now
                WHERE positioning_aux_import_run_id = $run_id AND status = 'running';
                """);
            Add(interruptRun, "$failure_code", failureCode);
            Add(interruptRun, "$now", now);
            Add(interruptRun, "$run_id", runId);
            interruptRun.ExecuteNonQuery();
            RefreshRunCounts(connection, transaction, runId, now);
        }

        transaction.Commit();
        return runIds;
    }

    internal IReadOnlyList<string> ListRecoverableRunIds()
    {
        using var connection = _database.OpenConnection();
        using var command = Command(
            connection,
            null,
            """
            SELECT positioning_aux_import_run_id
            FROM positioning_aux_import_runs
            WHERE status IN ('pending', 'running', 'interrupted')
            ORDER BY created_at_utc, positioning_aux_import_run_id;
            """);
        var runIds = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            runIds.Add(reader.GetString(0));
        }

        return runIds;
    }

    internal IReadOnlyList<string> ListApprovedPreflightRunIdsWithoutAuxRun()
    {
        using var connection = _database.OpenConnection();
        using var command = Command(
            connection,
            null,
            """
            SELECT preflight.source_preflight_run_id
            FROM source_preflight_runs preflight
            JOIN dataset_versions version
              ON version.dataset_version_id = preflight.dataset_version_id
            LEFT JOIN positioning_aux_import_runs aux
              ON aux.source_preflight_run_id = preflight.source_preflight_run_id
            WHERE preflight.status = 'completed'
              AND preflight.decision = 'dji_supported'
              AND version.lifecycle_state = 'draft'
              AND version.source_eligibility_state = 'dji_supported'
              AND version.source_eligibility_run_id = preflight.source_preflight_run_id
              AND aux.positioning_aux_import_run_id IS NULL
            ORDER BY preflight.completed_at_utc, preflight.source_preflight_run_id;
            """);
        var runIds = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            runIds.Add(reader.GetString(0));
        }

        return runIds;
    }

    internal IReadOnlyList<PositioningAuxImportWorkItem> ListIncompleteWorkItems(string? runId = null)
    {
        var normalizedRunId = NormalizeOptionalId(runId, nameof(runId));
        using var connection = _database.OpenConnection();
        using var command = Command(
            connection,
            null,
            """
            SELECT positioning_aux_import_item_id
            FROM positioning_aux_import_items
            WHERE ($run_id IS NULL OR positioning_aux_import_run_id = $run_id)
              AND status NOT IN ('completed', 'blocked')
            ORDER BY positioning_aux_import_run_id, sort_index, positioning_aux_import_item_id;
            """);
        Add(command, "$run_id", normalizedRunId);
        var ids = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }

        return ids.Select(id => ReadWorkItem(connection, null, id)).ToArray();
    }

    internal void RecordUsage(
        string positioningAuxFileId,
        string jobExecutionId,
        string usageState,
        string? evidenceJson = null)
    {
        positioningAuxFileId = NormalizeId(positioningAuxFileId, nameof(positioningAuxFileId));
        jobExecutionId = NormalizeId(jobExecutionId, nameof(jobExecutionId));
        usageState = NormalizeRequiredText(usageState, nameof(usageState), 16);
        if (usageState is not ("used" or "rejected"))
        {
            throw new BusinessCatalogException(
                "positioning_aux_usage_state_invalid",
                "The positioning auxiliary usage state is invalid.");
        }

        var evidence = CanonicalEvidenceJson(evidenceJson);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var file = ReadFilePrivate(connection, transaction, positioningAuxFileId);
        try
        {
            using var insert = Command(
                connection,
                transaction,
                """
                INSERT INTO positioning_aux_usage(
                    positioning_aux_usage_id, positioning_aux_file_id, job_execution_id,
                    usage_state, evidence_schema, use_role, content_hash_snapshot,
                    parse_inventory_sha256_snapshot, evidence_json, recorded_at_utc)
                VALUES(
                    $usage_id, $file_id, $execution_id, $usage_state,
                    'positioning-aux-usage.v1', 'positioning_aux', $content_hash,
                    $inventory_sha256, $evidence_json, $recorded_at_utc)
                ON CONFLICT(positioning_aux_file_id, job_execution_id) DO NOTHING;
                """);
            Add(insert, "$usage_id", NewId("positioning-aux-usage"));
            Add(insert, "$file_id", file.PositioningAuxFileId);
            Add(insert, "$execution_id", jobExecutionId);
            Add(insert, "$usage_state", usageState);
            Add(insert, "$content_hash", file.ContentHash);
            Add(insert, "$inventory_sha256", usageState == "used" ? file.ParseInventorySha256 : file.ParseInventorySha256);
            Add(insert, "$evidence_json", evidence);
            Add(insert, "$recorded_at_utc", UtcNowText());
            insert.ExecuteNonQuery();
            transaction.Commit();
        }
        catch (SqliteException exception)
        {
            transaction.Rollback();
            throw new BusinessCatalogException(
                "positioning_aux_usage_invalid",
                "The positioning auxiliary usage evidence is inconsistent with the retained file and job execution.",
                exception);
        }
    }

    private static void ValidateMrkProbeResult(ImageProbeCasPositioningAuxResult result)
    {
        if (result.SchemaVersion != ParserSchema ||
            result.Profile != ParserProfile ||
            result.ParseState != "parsed" ||
            result.QualityState is not ("passed" or "warning") ||
            result.ObjectKind != "positioning_aux" ||
            result.AuxiliaryType != "mrk" ||
            result.SequenceState != "contiguous" ||
            result.CoverageState != "complete" ||
            result.StandardDeviationState != "non_negative" ||
            result.RtkQualityState is not ("all_q50" or "non_q50" or "mixed_q") ||
            !IsSha256(result.CanonicalInventoryHash) ||
            result.ReasonCodes.Count != 0 ||
            result.Parser.ProductParser != ParserName ||
            result.Parser.ProductParserVersion != ParserVersion ||
            result.Parser.AuxiliaryParserVersion != ImageProbeProtocol.DjiMrkParserV1 ||
            result.Parser.QualityPolicyVersion != ImageProbeProtocol.DjiMrkQualityPolicyV1 ||
            result.Privacy.PathsIncluded || result.Privacy.LocatorsIncluded ||
            result.Privacy.ContentHashesIncluded || result.Privacy.ObjectKeysIncluded ||
            result.Privacy.RawMetadataIncluded || result.Privacy.SerialNumbersIncluded ||
            result.Privacy.CoordinatesIncluded || result.Privacy.OwnerSampleStatisticsIncluded)
        {
            throw new BusinessCatalogException(
                "positioning_aux_probe_response_invalid",
                "The positioning auxiliary parse result is invalid.");
        }
    }

    private static string ParsedSummaryJson(ImageProbeCasPositioningAuxResult result)
    {
        var summary = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = "qiongtu.positioning-aux.parsed-summary.v1",
            ["profile"] = result.Profile,
            ["auxiliaryType"] = result.AuxiliaryType,
            ["sequenceState"] = NormalizeReasonCode(result.SequenceState, nameof(result.SequenceState)),
            ["coverageState"] = NormalizeReasonCode(result.CoverageState, nameof(result.CoverageState)),
            ["standardDeviationState"] = NormalizeReasonCode(result.StandardDeviationState, nameof(result.StandardDeviationState)),
            ["rtkQualityState"] = NormalizeReasonCode(result.RtkQualityState, nameof(result.RtkQualityState)),
            ["auxiliaryParserVersion"] = result.Parser.AuxiliaryParserVersion,
            ["qualityPolicyVersion"] = result.Parser.QualityPolicyVersion,
            ["reasonCodes"] = result.ReasonCodes
                .Select(code => NormalizeReasonCode(code, "reasonCode"))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            ["privacy"] = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["pathsIncluded"] = false,
                ["rawRecordsIncluded"] = false,
                ["coordinatesIncluded"] = false,
                ["timestampsIncluded"] = false,
                ["ownerSampleStatisticsIncluded"] = false
            }
        };
        return JsonSerializer.Serialize(summary, JsonOptions);
    }

    private static string AssociationEvidenceJson(PositioningAuxImportWorkItem item)
    {
        var evidence = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = "qiongtu.positioning-aux.association.v1",
            ["associationProfile"] = AssociationProfile,
            ["associationPolicyVersion"] = AssociationPolicyVersion,
            ["sourcePreflightItemId"] = item.SourcePreflightItemId,
            ["associationItemCount"] = item.AssociationItemCount,
            ["pathsIncluded"] = false,
            ["sourceKeysIncluded"] = false,
            ["hashesIncluded"] = false,
            ["coordinatesIncluded"] = false,
            ["timestampsIncluded"] = false,
            ["ownerSampleStatisticsIncluded"] = false
        };
        return JsonSerializer.Serialize(evidence, JsonOptions);
    }

    private static string CanonicalEvidenceJson(string? evidenceJson)
    {
        if (string.IsNullOrWhiteSpace(evidenceJson))
        {
            return """{"schemaVersion":"positioning-aux-usage.v1","pathsIncluded":false,"coordinatesIncluded":false,"timestampsIncluded":false}""";
        }

        if (evidenceJson.Length > 8192)
        {
            throw new BusinessCatalogException(
                "positioning_aux_usage_evidence_invalid",
                "The positioning auxiliary usage evidence is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(evidenceJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new BusinessCatalogException(
                    "positioning_aux_usage_evidence_invalid",
                    "The positioning auxiliary usage evidence must be a JSON object.");
            }

            EnsureUsageEvidenceIsPrivate(document.RootElement);

            return JsonSerializer.Serialize(document.RootElement, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new BusinessCatalogException(
                "positioning_aux_usage_evidence_invalid",
                "The positioning auxiliary usage evidence is invalid JSON.",
                exception);
        }
    }

    private static void EnsureUsageEvidenceIsPrivate(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            var normalizedName = property.Name.Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
            if (normalizedName.Contains("path", StringComparison.Ordinal) ||
                normalizedName.Contains("locator", StringComparison.Ordinal) ||
                normalizedName.Contains("sourcekey", StringComparison.Ordinal) ||
                normalizedName.Contains("hash", StringComparison.Ordinal) ||
                normalizedName.Contains("objectkey", StringComparison.Ordinal) ||
                normalizedName.Contains("stage", StringComparison.Ordinal) ||
                normalizedName.Contains("rawrecord", StringComparison.Ordinal) ||
                normalizedName.Contains("coordinate", StringComparison.Ordinal) ||
                normalizedName is "latitude" or "longitude" or "timestamp" or "time" ||
                normalizedName.Contains("ownersample", StringComparison.Ordinal))
            {
                throw new BusinessCatalogException(
                    "positioning_aux_usage_evidence_invalid",
                    "The positioning auxiliary usage evidence contains a private or unsupported field.");
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                EnsureUsageEvidenceIsPrivate(property.Value);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in property.Value.EnumerateArray())
                {
                    if (child.ValueKind == JsonValueKind.Object)
                    {
                        EnsureUsageEvidenceIsPrivate(child);
                    }
                }
            }
        }
    }

    private static PositioningAuxInitialParse InitialParseState(string auxiliaryType) => auxiliaryType switch
    {
        "mrk" => new PositioningAuxInitialParse("not_attempted", "not_checked", null, null, null, null),
        "nav" or "obs" => new PositioningAuxInitialParse("unsupported", "not_checked", ParserSchema, ParserProfile, RinexCandidateParserName, ParserVersion),
        "rtk" => new PositioningAuxInitialParse("unsupported", "not_checked", ParserSchema, ParserProfile, RtcmCandidateParserName, ParserVersion),
        _ => throw new BusinessCatalogException("positioning_aux_type_invalid", "The positioning auxiliary type is invalid.")
    };

    private static string DefaultMediaType(string? auxiliaryType) => auxiliaryType switch
    {
        "mrk" => "text/plain",
        "nav" or "obs" => "application/rinex",
        "rtk" => "application/octet-stream",
        _ => "application/octet-stream"
    };

    private static string InsertOrReusePositioningAuxObject(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sha256,
        long byteLength,
        string mediaType)
    {
        using (var select = Command(
            connection,
            transaction,
            """
            SELECT file_object_id, storage_state, object_key
            FROM file_objects
            WHERE hash_algorithm = 'sha256'
              AND content_hash = $content_hash
              AND byte_length = $byte_length;
            """))
        {
            Add(select, "$content_hash", sha256);
            Add(select, "$byte_length", byteLength);
            using var reader = select.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(1) != "available" ||
                    reader.GetString(2) != ObjectKey(sha256))
                {
                    throw new BusinessCatalogException(
                        "file_object_identity_conflict",
                        "The positioning auxiliary content identity conflicts with an unavailable or differently keyed object.");
                }

                var existingId = reader.GetString(0);
                reader.Close();
                InsertRole(connection, transaction, existingId, "positioning_aux");
                return existingId;
            }
        }

        var now = UtcNowText();
        var fileObjectId = NewId("file-object");
        using var insert = Command(
            connection,
            transaction,
            """
            INSERT INTO file_objects(
                file_object_id, object_kind, hash_algorithm, content_hash,
                byte_length, media_type, object_key, storage_state,
                created_at_utc, available_at_utc)
            VALUES(
                $file_object_id, 'positioning_aux', 'sha256', $content_hash,
                $byte_length, $media_type, $object_key, 'available',
                $created_at_utc, $available_at_utc);
            """);
        Add(insert, "$file_object_id", fileObjectId);
        Add(insert, "$content_hash", sha256);
        Add(insert, "$byte_length", byteLength);
        Add(insert, "$media_type", mediaType);
        Add(insert, "$object_key", ObjectKey(sha256));
        Add(insert, "$created_at_utc", now);
        Add(insert, "$available_at_utc", now);
        insert.ExecuteNonQuery();
        InsertRole(connection, transaction, fileObjectId, "positioning_aux");
        return fileObjectId;
    }

    private static void InsertRole(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fileObjectId,
        string role)
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

    private static void RefreshRunCounts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string now)
    {
        using (var updateCounts = Command(
            connection,
            transaction,
            """
            UPDATE positioning_aux_import_runs
            SET total_item_count = (
                    SELECT count(*) FROM positioning_aux_import_items
                    WHERE positioning_aux_import_run_id = $run_id),
                completed_item_count = (
                    SELECT count(*) FROM positioning_aux_import_items
                    WHERE positioning_aux_import_run_id = $run_id AND status = 'completed'),
                failed_item_count = (
                    SELECT count(*) FROM positioning_aux_import_items
                    WHERE positioning_aux_import_run_id = $run_id AND status = 'blocked'),
                updated_at_utc = $now
            WHERE positioning_aux_import_run_id = $run_id;
            """))
        {
            Add(updateCounts, "$run_id", runId);
            Add(updateCounts, "$now", now);
            updateCounts.ExecuteNonQuery();
        }

        using var complete = Command(
            connection,
            transaction,
            """
            UPDATE positioning_aux_import_runs
            SET status = CASE WHEN failed_item_count = 0 THEN 'completed' ELSE 'blocked' END,
                last_error_code = CASE WHEN failed_item_count = 0 THEN NULL ELSE 'positioning_aux_item_blocked' END,
                updated_at_utc = $now,
                completed_at_utc = $now
            WHERE positioning_aux_import_run_id = $run_id
              AND status IN ('pending', 'running', 'interrupted')
              AND total_item_count = completed_item_count + failed_item_count;
            """);
        Add(complete, "$run_id", runId);
        Add(complete, "$now", now);
        complete.ExecuteNonQuery();
    }

    private static void SetItemStatus(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        string status,
        string? failureCode,
        string now,
        string? fileId)
    {
        using var update = Command(
            connection,
            transaction,
            """
            UPDATE positioning_aux_import_items
            SET status = $status, failure_code = $failure_code,
                positioning_aux_file_id = COALESCE($file_id, positioning_aux_file_id),
                updated_at_utc = $now,
                terminal_at_utc = CASE WHEN $status IN ('completed', 'blocked') THEN $now ELSE terminal_at_utc END
            WHERE positioning_aux_import_item_id = $item_id;
            """);
        Add(update, "$status", status);
        Add(update, "$failure_code", failureCode);
        Add(update, "$file_id", fileId);
        Add(update, "$now", now);
        Add(update, "$item_id", itemId);
        update.ExecuteNonQuery();
    }

    private static IReadOnlyDictionary<string, PositioningAuxAssociationBinding> NormalizeAssociationBindings(
        IEnumerable<PositioningAuxAssociationBinding> bindings)
    {
        var normalized = new Dictionary<string, PositioningAuxAssociationBinding>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            var sourcePreflightItemId = NormalizeId(binding.SourcePreflightItemId, nameof(binding.SourcePreflightItemId));
            var sourceEntryKey = NormalizeSha256(binding.SourceEntryKey, nameof(binding.SourceEntryKey));
            if (binding.AssociationItemCount <= 0)
            {
                throw new BusinessCatalogException(
                    "positioning_aux_association_count_invalid",
                    "The positioning auxiliary association count must be positive.");
            }

            if (!normalized.TryAdd(
                    sourcePreflightItemId,
                    new PositioningAuxAssociationBinding(
                        sourcePreflightItemId,
                        sourceEntryKey,
                        binding.AssociationItemCount)))
            {
                throw new BusinessCatalogException(
                    "positioning_aux_association_duplicate",
                    "Each positioning auxiliary preflight item can have only one association binding.");
            }
        }

        return normalized;
    }

    private static void EnsureExistingAssociationBindings(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        IReadOnlyDictionary<string, PositioningAuxAssociationBinding> bindings)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT source_preflight_item_id, source_entry_key, association_item_count
            FROM positioning_aux_import_items
            WHERE positioning_aux_import_run_id = $run_id;
            """);
        Add(command, "$run_id", runId);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var itemId = reader.GetString(0);
            if (!bindings.TryGetValue(itemId, out var binding) ||
                binding.SourceEntryKey != reader.GetString(1) ||
                binding.AssociationItemCount != reader.GetInt32(2))
            {
                throw new BusinessCatalogException(
                    "positioning_aux_association_conflict",
                    "The existing positioning auxiliary import run has different association bindings.");
            }

            seen.Add(itemId);
        }

        if (seen.Count != bindings.Count)
        {
            throw new BusinessCatalogException(
                "positioning_aux_association_conflict",
                "The association binding set does not match the existing positioning auxiliary import run.");
        }
    }

    private static PositioningAuxGate ReadCompletedDjiGate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePreflightRunId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT r.source_preflight_run_id, r.import_session_id, r.dataset_version_id,
                   r.image_candidate_count
            FROM source_preflight_runs r
            JOIN dataset_versions dv ON dv.dataset_version_id = r.dataset_version_id
            WHERE r.source_preflight_run_id = $run_id
              AND r.status = 'completed'
              AND r.decision = 'dji_supported'
              AND dv.lifecycle_state = 'draft'
              AND dv.source_eligibility_state = 'dji_supported'
              AND dv.source_eligibility_run_id = r.source_preflight_run_id;
            """);
        Add(command, "$run_id", sourcePreflightRunId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException(
                "positioning_aux_source_gate_not_satisfied",
                "Positioning auxiliary import requires a completed dji_supported source preflight for the same draft dataset.");
        }

        return new PositioningAuxGate(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2));
    }

    private static PositioningAuxImportRun? TryReadRunForGate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PositioningAuxGate gate)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT positioning_aux_import_run_id
            FROM positioning_aux_import_runs
            WHERE import_session_id = $import_session_id
              AND association_policy_version = $association_policy_version
              AND parser_profile = $parser_profile
              AND parser_version = $parser_version;
            """);
        Add(command, "$import_session_id", gate.ImportSessionId);
        Add(command, "$association_policy_version", AssociationPolicyVersion);
        Add(command, "$parser_profile", ParserProfile);
        Add(command, "$parser_version", ParserVersion);
        var runId = command.ExecuteScalar() as string;
        return runId is null ? null : ReadRun(connection, transaction, runId);
    }

    private static IReadOnlyList<PositioningAuxSidecarCandidate> ReadCompletedSidecarCandidates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PositioningAuxGate gate,
        IReadOnlyDictionary<string, PositioningAuxAssociationBinding> bindings)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT source_preflight_item_id, source_entry_key, display_name, sort_index,
                   format_hint, byte_length_snapshot, source_last_write_time_utc,
                   source_identity_key
            FROM source_preflight_items
            WHERE source_preflight_run_id = $run_id
              AND import_session_id = $import_session_id
              AND dataset_version_id = $dataset_version_id
              AND candidate_kind = 'positioning_aux_candidate'
              AND status = 'completed'
              AND format_hint IN ('mrk', 'nav', 'obs', 'rtk')
              AND byte_length_snapshot IS NOT NULL
              AND source_identity_key IS NOT NULL
            ORDER BY sort_index, source_preflight_item_id;
            """);
        Add(command, "$run_id", gate.SourcePreflightRunId);
        Add(command, "$import_session_id", gate.ImportSessionId);
        Add(command, "$dataset_version_id", gate.DatasetVersionId);
        using var reader = command.ExecuteReader();
        var result = new List<PositioningAuxSidecarCandidate>();
        while (reader.Read())
        {
            var sourcePreflightItemId = reader.GetString(0);
            var sourceEntryKey = reader.GetString(1);
            if (!bindings.TryGetValue(sourcePreflightItemId, out var binding) ||
                binding.SourceEntryKey != sourceEntryKey)
            {
                throw new BusinessCatalogException(
                    "positioning_aux_association_missing",
                    "Every completed positioning auxiliary sidecar requires a protected-manifest association binding.");
            }

            result.Add(new PositioningAuxSidecarCandidate(
                sourcePreflightItemId,
                sourceEntryKey,
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetInt64(5),
                StringOrNull(reader, 6),
                reader.GetString(7),
                binding.AssociationItemCount));
        }

        if (result.Count != bindings.Count)
        {
            throw new BusinessCatalogException(
                "positioning_aux_association_unknown_item",
                "Association bindings must match completed positioning auxiliary sidecar items exactly.");
        }

        return result;
    }

    private static PositioningAuxImportRun ReadRun(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT positioning_aux_import_run_id, import_session_id, dataset_version_id,
                   status, total_item_count, completed_item_count, failed_item_count,
                   association_policy_version, parser_profile, parser_version,
                   last_error_code, created_at_utc, started_at_utc, updated_at_utc,
                   completed_at_utc, cancelled_at_utc
            FROM positioning_aux_import_runs
            WHERE positioning_aux_import_run_id = $run_id;
            """);
        Add(command, "$run_id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException(
                "positioning_aux_import_not_found",
                "The positioning auxiliary import run was not found.");
        }

        return new PositioningAuxImportRun(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            AssociationProfile,
            reader.GetString(7),
            reader.GetString(8),
            ParserName,
            reader.GetString(9),
            StringOrNull(reader, 10),
            ParseTime(reader.GetString(11)),
            ParseOptionalTime(StringOrNull(reader, 12)),
            ParseTime(reader.GetString(13)),
            ParseOptionalTime(StringOrNull(reader, 14)),
            ParseOptionalTime(StringOrNull(reader, 15)),
            ResponsePrivacy);
    }

    private static PositioningAuxImportWorkItem ReadWorkItem(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string itemId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT item.positioning_aux_import_item_id, item.positioning_aux_import_run_id,
                   run.import_session_id, run.dataset_version_id, item.source_preflight_item_id,
                   item.source_entry_key, item.display_name, item.sort_index,
                   item.auxiliary_type, item.byte_length_snapshot,
                   item.source_last_write_time_utc, item.source_identity_key,
                   item.association_item_count, item.status, item.failure_code,
                   item.stage_id, item.stage_sha256, item.stage_byte_length,
                   item.stage_created_at_utc, item.expected_content_hash,
                   item.expected_byte_length, item.expected_object_key,
                   item.file_object_id, item.positioning_aux_file_id
            FROM positioning_aux_import_items item
            JOIN positioning_aux_import_runs run
              ON run.positioning_aux_import_run_id = item.positioning_aux_import_run_id
            WHERE item.positioning_aux_import_item_id = $item_id;
            """);
        Add(command, "$item_id", itemId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException(
                "positioning_aux_item_not_found",
                "The positioning auxiliary import item was not found.");
        }

        var stage = StringOrNull(reader, 15) is { } stageId
            ? new ObjectStageReceipt(
                stageId,
                reader.GetString(16),
                reader.GetInt64(17),
                ParseTime(reader.GetString(18)))
            : null;
        return new PositioningAuxImportWorkItem(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.GetString(8),
            reader.GetInt64(9),
            ParseOptionalTime(StringOrNull(reader, 10)),
            reader.GetString(11),
            reader.GetInt32(12),
            reader.GetString(13),
            StringOrNull(reader, 14),
            stage,
            StringOrNull(reader, 19),
            LongOrNull(reader, 20),
            StringOrNull(reader, 21),
            StringOrNull(reader, 22),
            StringOrNull(reader, 23));
    }

    private static PositioningAuxFile ReadFile(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string positioningAuxFileId)
    {
        var file = ReadFilePrivate(connection, transaction, positioningAuxFileId);
        var usage = ReadUsageState(connection, transaction, positioningAuxFileId);
        var parseState = file.ItemStatus == "parsing" && file.ParseState == "not_attempted"
            ? "parsing"
            : file.ParseState;
        return new PositioningAuxFile(
            file.PositioningAuxFileId,
            file.RunId,
            file.DatasetVersionId,
            file.AuxiliaryType,
            file.RetentionState,
            parseState,
            file.QualityState,
            file.ParserProfile ?? ParserProfile,
            file.ParserName ?? ParserName,
            file.ParserVersion ?? ParserVersion,
            usage,
            file.FailureCode ?? file.ItemFailureCode,
            file.CreatedAtUtc,
            file.UpdatedAtUtc,
            file.CreatedAtUtc,
            file.ParsedAtUtc,
            file.QualityState == "not_checked" ? null : file.ParsedAtUtc,
            ResponsePrivacy);
    }

    private static PositioningAuxFilePrivate ReadFilePrivate(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string positioningAuxFileId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT p.positioning_aux_file_id, item.positioning_aux_import_run_id,
                   p.dataset_version_id, p.auxiliary_type, p.retention_state,
                   p.parse_state, p.quality_state, p.parser_profile, p.parser_name,
                   p.parser_version, p.parse_inventory_sha256, p.failure_code,
                   p.created_at_utc, p.updated_at_utc, p.parsed_at_utc,
                   f.content_hash, f.byte_length, item.status, item.failure_code
            FROM positioning_aux_files p
            JOIN file_objects f ON f.file_object_id = p.file_object_id
            JOIN positioning_aux_import_items item
              ON item.positioning_aux_import_item_id = p.positioning_aux_import_item_id
            WHERE p.positioning_aux_file_id = $file_id;
            """);
        Add(command, "$file_id", positioningAuxFileId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException(
                "positioning_aux_file_not_found",
                "The positioning auxiliary file was not found.");
        }

        return new PositioningAuxFilePrivate(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            StringOrNull(reader, 7),
            StringOrNull(reader, 8),
            StringOrNull(reader, 9),
            StringOrNull(reader, 10),
            StringOrNull(reader, 11),
            ParseTime(reader.GetString(12)),
            ParseTime(reader.GetString(13)),
            ParseOptionalTime(StringOrNull(reader, 14)),
            reader.GetString(15),
            reader.GetInt64(16),
            reader.GetString(17),
            StringOrNull(reader, 18));
    }

    private static string ReadUsageState(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string positioningAuxFileId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT
                COALESCE(SUM(CASE WHEN usage_state = 'used' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN usage_state = 'rejected' THEN 1 ELSE 0 END), 0)
            FROM positioning_aux_usage
            WHERE positioning_aux_file_id = $file_id;
            """);
        Add(command, "$file_id", positioningAuxFileId);
        using var reader = command.ExecuteReader();
        reader.Read();
        if (reader.GetInt32(0) > 0)
        {
            return "used";
        }

        return reader.GetInt32(1) > 0 ? "rejected" : "not_recorded";
    }

    private static string? TryReadFileIdForItem(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT positioning_aux_file_id FROM positioning_aux_files WHERE positioning_aux_import_item_id = $item_id;");
        Add(command, "$item_id", itemId);
        return command.ExecuteScalar() as string;
    }

    private static void EnsureRunIsRunning(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT status FROM positioning_aux_import_runs WHERE positioning_aux_import_run_id = $run_id;");
        Add(command, "$run_id", runId);
        var status = command.ExecuteScalar() as string;
        if (status != "running")
        {
            throw new BusinessCatalogException(
                "positioning_aux_import_not_running",
                "The positioning auxiliary import run is not running.");
        }
    }

    private T ExecuteIdempotent<T>(
        string requestId,
        string method,
        object normalizedParameters,
        Func<SqliteConnection, SqliteTransaction, T> operation)
    {
        requestId = NormalizeIdentifier(requestId, "requestId", 128);
        var parameterHash = Sha256Hex(JsonSerializer.SerializeToUtf8Bytes(normalizedParameters, JsonOptions));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var committed = false;
        try
        {
            var existing = ReadMutation(connection, transaction, requestId);
            if (existing is not null)
            {
                if (existing.Method != method ||
                    !string.Equals(existing.ParametersSha256, parameterHash, StringComparison.Ordinal))
                {
                    throw new BusinessCatalogException(
                        "idempotency_conflict",
                        "The requestId was already used with different parameters.");
                }

                var replay = JsonSerializer.Deserialize<T>(existing.ResponseJson, JsonOptions)
                    ?? throw new BusinessCatalogException(
                        "idempotency_replay_failed",
                        "The saved positioning auxiliary response could not be read.");
                transaction.Commit();
                committed = true;
                EnsureResponseWithinLimit(replay);
                return replay;
            }

            var response = operation(connection, transaction);
            var responseJson = SerializeResponse(response);
            using var insert = Command(
                connection,
                transaction,
                """
                INSERT INTO catalog_mutations(
                    request_id, method, parameters_sha256, response_json, completed_at_utc)
                VALUES($request_id, $method, $parameters_sha256, $response_json, $completed_at_utc);
                """);
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
        var json = JsonSerializer.Serialize(response, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > _maximumResponseBytes)
        {
            throw new BusinessCatalogException(
                "response_too_large",
                "The positioning auxiliary response exceeds the control protocol size limit.");
        }

        return json;
    }

    private void EnsureResponseWithinLimit<T>(T response) => _ = SerializeResponse(response);

    private static CatalogMutation? ReadMutation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string requestId)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT method, parameters_sha256, response_json FROM catalog_mutations WHERE request_id = $request_id;");
        Add(command, "$request_id", requestId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new CatalogMutation(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static PageResult<T> ToPage<T>(
        List<(T Item, string Position, string Id)> rows,
        int pageSize,
        string method,
        string scope)
    {
        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var last = rows[pageSize - 1];
            nextCursor = EncodeCursor(last.Position, last.Id, method, scope);
            rows.RemoveRange(pageSize, rows.Count - pageSize);
        }

        return new PageResult<T>(rows.Select(row => row.Item).ToArray(), nextCursor);
    }

    private static string EncodeCursor(string position, string id, string method, string scope)
    {
        var json = JsonSerializer.Serialize(new PositioningAuxCursor(1, method, scope, position, id), JsonOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static PositioningAuxCursor? DecodeCursor(string? cursor, string method, string scope)
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
            var decoded = JsonSerializer.Deserialize<PositioningAuxCursor>(json, JsonOptions)
                ?? throw new JsonException();
            if (decoded.Version != 1 || decoded.Method != method || decoded.Scope != scope)
            {
                throw new BusinessCatalogException(
                    "invalid_cursor",
                    "The page cursor does not belong to this positioning auxiliary list and filter.");
            }

            return decoded with
            {
                Position = NormalizeRequiredText(decoded.Position, "cursor.position", 64),
                Id = NormalizeId(decoded.Id, "cursor.id")
            };
        }
        catch (Exception exception) when (exception is FormatException or JsonException or BusinessCatalogException)
        {
            throw new BusinessCatalogException("invalid_cursor", "The positioning auxiliary page cursor is invalid.", exception);
        }
    }

    private static BusinessPage NormalizePage(int? requestedPageSize, string? requestedCursor)
    {
        var pageSize = requestedPageSize ?? DefaultPageSize;
        if (pageSize is <= 0 or > MaximumPageSize)
        {
            throw new BusinessCatalogException("invalid_page_size", $"Page size must be between 1 and {MaximumPageSize}.");
        }

        return new BusinessPage(pageSize, NormalizeOptionalText(requestedCursor, "cursor", 512));
    }

    private static string NormalizeReasonCode(string value, string fieldName) =>
        NormalizeIdentifier(value, fieldName, 128);

    private static string NormalizeId(string value, string fieldName) =>
        NormalizeIdentifier(value, fieldName, 128);

    private static string? NormalizeOptionalId(string? value, string fieldName) =>
        value is null ? null : NormalizeIdentifier(value, fieldName, 128);

    private static string NormalizeIdentifier(string value, string fieldName, int maximumLength)
    {
        var normalized = NormalizeRequiredText(value, fieldName, maximumLength);
        if (normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
        {
            throw new BusinessCatalogException(
                "invalid_parameters",
                $"{fieldName} contains unsupported identifier characters.");
        }

        return normalized;
    }

    private static string NormalizeSha256(string value, string fieldName)
    {
        var normalized = NormalizeRequiredText(value, fieldName, 64).ToLowerInvariant();
        if (!IsSha256(normalized))
        {
            throw new BusinessCatalogException("invalid_parameters", $"{fieldName} must be a SHA-256 hex string.");
        }

        return normalized;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

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

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? StringOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? LongOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ParseOptionalTime(string? value) =>
        value is null ? null : ParseTime(value);

    private static string UtcNowText() =>
        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static string ObjectKey(string sha256) => $"sha256/{sha256[..2]}/{sha256}";

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal sealed record PositioningAuxGate(
    string SourcePreflightRunId,
    string ImportSessionId,
    string DatasetVersionId);

internal sealed record PositioningAuxAssociationBinding(
    string SourcePreflightItemId,
    string SourceEntryKey,
    int AssociationItemCount);

internal sealed record PositioningAuxSidecarCandidate(
    string SourcePreflightItemId,
    string SourceEntryKey,
    string DisplayName,
    int SortIndex,
    string AuxiliaryType,
    long ByteLengthSnapshot,
    string? SourceLastWriteTimeUtcText,
    string SourceIdentityKey,
    int AssociationItemCount);

internal sealed record PositioningAuxInitialParse(
    string ParseState,
    string QualityState,
    string? ParserSchema,
    string? ParserProfile,
    string? ParserName,
    string? ParserVersion);

internal sealed record PositioningAuxFilePrivate(
    string PositioningAuxFileId,
    string RunId,
    string DatasetVersionId,
    string AuxiliaryType,
    string RetentionState,
    string ParseState,
    string QualityState,
    string? ParserProfile,
    string? ParserName,
    string? ParserVersion,
    string? ParseInventorySha256,
    string? FailureCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ParsedAtUtc,
    string ContentHash,
    long ByteLength,
    string ItemStatus,
    string? ItemFailureCode);

internal sealed record PositioningAuxCursor(
    int Version,
    string Method,
    string Scope,
    string Position,
    string Id);
