using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control;

public sealed class ImageImportPreflightCatalog
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 50;
    public const int MaximumCatalogPayloadBytes = BusinessCatalog.MaximumCatalogPayloadBytes;
    public const string ParserProfile = "source-preflight.v1";
    public const string ParserVersion = "1.0.0";
    public const string PolicyVersion = "dji-source-policy.v1";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly ImageImportPreflightPrivacy ResponsePrivacy = new(
        PathsIncluded: false,
        LocatorsIncluded: false,
        SourceKeysIncluded: false,
        HashesIncluded: false,
        ObjectKeysIncluded: false,
        StageReceiptsIncluded: false,
        QuarantineIncluded: false,
        RawMetadataIncluded: false,
        SerialNumbersIncluded: false,
        CoordinatesIncluded: false,
        OwnerSampleStatisticsIncluded: false);

    private readonly BusinessDatabase _database;
    private readonly int _maximumResponseBytes;

    public ImageImportPreflightCatalog(
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

    public ImageImportPreflightRun Start(
        string requestId,
        ImageImportPreflightStartParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var normalized = parameters with
        {
            ImportSessionId = NormalizeId(parameters.ImportSessionId, nameof(parameters.ImportSessionId))
        };
        return ExecuteIdempotent(
            requestId,
            ControlMethods.ImageImportPreflightStart,
            normalized,
            (connection, transaction) =>
            {
                var existing = TryReadRunForSession(connection, transaction, normalized.ImportSessionId);
                if (existing is not null)
                {
                    return existing;
                }

                var binding = ReadWaitingSessionBinding(connection, transaction, normalized.ImportSessionId);
                var runId = NewId("source-preflight-run");
                var now = UtcNowText();
                using (var insertRun = Command(
                    connection,
                    transaction,
                    """
                    INSERT INTO source_preflight_runs(
                        source_preflight_run_id, import_session_id, dataset_version_id,
                        source_root_key_snapshot, source_locator_manifest_id_snapshot,
                        parser_profile, parser_version, policy_version, status,
                        created_at_utc, updated_at_utc)
                    VALUES(
                        $run_id, $import_session_id, $dataset_version_id,
                        $source_root_key, $manifest_id,
                        $parser_profile, $parser_version, $policy_version, 'queued',
                        $created_at_utc, $updated_at_utc);
                    """))
                {
                    Add(insertRun, "$run_id", runId);
                    Add(insertRun, "$import_session_id", binding.ImportSessionId);
                    Add(insertRun, "$dataset_version_id", binding.DatasetVersionId);
                    Add(insertRun, "$source_root_key", binding.SourceRootKey);
                    Add(insertRun, "$manifest_id", binding.SourceLocatorManifestId);
                    Add(insertRun, "$parser_profile", ParserProfile);
                    Add(insertRun, "$parser_version", ParserVersion);
                    Add(insertRun, "$policy_version", PolicyVersion);
                    Add(insertRun, "$created_at_utc", now);
                    Add(insertRun, "$updated_at_utc", now);
                    insertRun.ExecuteNonQuery();
                }

                using (var selectEntries = Command(
                    connection,
                    transaction,
                    """
                    SELECT import_entry_id, source_entry_key, display_name, sort_index,
                           byte_length_snapshot, source_last_write_time_utc, source_identity_key
                    FROM image_import_entries
                    WHERE import_session_id = $import_session_id
                    ORDER BY sort_index, import_entry_id;
                    """))
                {
                    Add(selectEntries, "$import_session_id", binding.ImportSessionId);
                    using var reader = selectEntries.ExecuteReader();
                    while (reader.Read())
                    {
                        InsertItem(
                            connection,
                            transaction,
                            new SourcePreflightDiscoveredItem(
                                NewId("source-preflight-item"),
                                runId,
                                binding.ImportSessionId,
                                binding.DatasetVersionId,
                                reader.GetString(0),
                                reader.GetString(1),
                                reader.GetString(2),
                                reader.GetInt32(3),
                                "image_candidate",
                                FormatHint(reader.GetString(2)),
                                LongOrNull(reader, 4),
                                StringOrNull(reader, 5),
                                StringOrNull(reader, 6),
                                now));
                    }
                }

                RefreshCounts(connection, transaction, runId, now);
                return ReadRun(connection, transaction, runId);
            });
    }

    public ImageImportPreflightRun Get(ImageImportPreflightGetParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var runId = NormalizeId(parameters.PreflightRunId, nameof(parameters.PreflightRunId));
        using var connection = _database.OpenConnection();
        var run = ReadRun(connection, null, runId);
        EnsureResponseWithinLimit(run);
        return run;
    }

    public PageResult<ImageImportPreflightItem> ListItems(
        ImageImportPreflightItemListParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var runId = NormalizeId(parameters.PreflightRunId, nameof(parameters.PreflightRunId));
        var page = NormalizePage(parameters.PageSize, parameters.Cursor);
        var cursor = DecodeCursor(page.Cursor, ControlMethods.ImageImportPreflightItemList, runId);
        using var connection = _database.OpenConnection();
        EnsureRunExists(connection, runId);
        using var command = Command(
            connection,
            null,
            """
            SELECT source_preflight_item_id, source_preflight_run_id, import_session_id,
                   dataset_version_id, sort_index, display_name, candidate_kind, format_hint,
                   status, container_hint, evidence_state, evidence_json, failure_code,
                   created_at_utc, updated_at_utc, completed_at_utc
            FROM source_preflight_items
            WHERE source_preflight_run_id = $run_id
              AND ($has_cursor = 0 OR sort_index > $sort_index OR
                   (sort_index = $sort_index AND source_preflight_item_id > $cursor_id))
            ORDER BY sort_index, source_preflight_item_id
            LIMIT $limit;
            """);
        Add(command, "$run_id", runId);
        Add(command, "$has_cursor", cursor is null ? 0 : 1);
        Add(command, "$sort_index", cursor?.SortIndex ?? -1);
        Add(command, "$cursor_id", cursor?.Id ?? string.Empty);
        Add(command, "$limit", page.PageSize + 1);
        using var reader = command.ExecuteReader();
        var rows = new List<(ImageImportPreflightItem Item, int SortIndex, string Id)>();
        while (reader.Read())
        {
            var item = ReadItem(reader);
            rows.Add((item, item.SortIndex, item.PreflightItemId));
        }

        string? nextCursor = null;
        if (rows.Count > page.PageSize)
        {
            var last = rows[page.PageSize - 1];
            nextCursor = EncodeCursor(
                last.SortIndex,
                last.Id,
                ControlMethods.ImageImportPreflightItemList,
                runId);
            rows.RemoveRange(page.PageSize, rows.Count - page.PageSize);
        }

        var result = new PageResult<ImageImportPreflightItem>(
            rows.Select(row => row.Item).ToArray(),
            nextCursor);
        EnsureResponseWithinLimit(result);
        return result;
    }

    internal ImageImportPreflightRun? TryGetForSession(string importSessionId)
    {
        importSessionId = NormalizeId(importSessionId, nameof(importSessionId));
        using var connection = _database.OpenConnection();
        return TryReadRunForSession(connection, null, importSessionId);
    }

    internal SourcePreflightRunBinding GetRunBinding(string runId)
    {
        runId = NormalizeId(runId, nameof(runId));
        using var connection = _database.OpenConnection();
        using var command = Command(
            connection,
            null,
            """
            SELECT source_preflight_run_id, import_session_id, dataset_version_id,
                   source_root_key_snapshot, source_locator_manifest_id_snapshot, status
            FROM source_preflight_runs
            WHERE source_preflight_run_id = $run_id;
            """);
        Add(command, "$run_id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException(
                "image_import_preflight_not_found",
                "The image import source preflight run was not found.");
        }

        return new SourcePreflightRunBinding(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
    }

    internal ImageImportPreflightRun AddSidecarItems(
        string runId,
        IEnumerable<SourcePreflightSidecarCandidate> candidates)
    {
        runId = NormalizeId(runId, nameof(runId));
        ArgumentNullException.ThrowIfNull(candidates);
        var normalized = candidates
            .Select(candidate => NormalizeSidecarCandidate(candidate))
            .OrderBy(candidate => candidate.SourceEntryKey, StringComparer.Ordinal)
            .ToArray();
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var binding = ReadRunPrivate(connection, transaction, runId);
        if (binding.Status is not ("queued" or "running" or "interrupted"))
        {
            transaction.Commit();
            return ReadRun(connection, null, runId);
        }

        var existingKeys = new HashSet<string>(StringComparer.Ordinal);
        var nextSortIndex = 0;
        using (var existing = Command(
            connection,
            transaction,
            "SELECT source_entry_key, sort_index FROM source_preflight_items WHERE source_preflight_run_id = $run_id;"))
        {
            Add(existing, "$run_id", runId);
            using var reader = existing.ExecuteReader();
            while (reader.Read())
            {
                existingKeys.Add(reader.GetString(0));
                nextSortIndex = Math.Max(nextSortIndex, checked(reader.GetInt32(1) + 1));
            }
        }

        var now = UtcNowText();
        foreach (var candidate in normalized)
        {
            if (!existingKeys.Add(candidate.SourceEntryKey))
            {
                continue;
            }

            InsertItem(
                connection,
                transaction,
                new SourcePreflightDiscoveredItem(
                    NewId("source-preflight-item"),
                    runId,
                    binding.ImportSessionId,
                    binding.DatasetVersionId,
                    null,
                    candidate.SourceEntryKey,
                    candidate.DisplayName,
                    nextSortIndex++,
                    "positioning_aux_candidate",
                    candidate.FormatHint,
                    candidate.Snapshot.Length,
                    candidate.Snapshot.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                    candidate.Snapshot.Identity,
                    now));
        }

        RefreshCounts(connection, transaction, runId, now);
        transaction.Commit();
        return Get(new ImageImportPreflightGetParameters(runId));
    }

    internal ImageImportPreflightRun MarkRunning(string runId)
    {
        runId = NormalizeId(runId, nameof(runId));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadRunPrivate(connection, transaction, runId);
        if (current.Status == "running")
        {
            transaction.Commit();
            return ReadRun(connection, null, runId);
        }

        if (current.Status is not ("queued" or "interrupted"))
        {
            throw new BusinessCatalogException(
                "image_import_preflight_not_runnable",
                "The image import source preflight run cannot be started from its current state.");
        }

        var now = UtcNowText();
        using var update = Command(
            connection,
            transaction,
            """
            UPDATE source_preflight_runs
            SET status = 'running', started_at_utc = COALESCE(started_at_utc, $now),
                updated_at_utc = $now, completed_at_utc = NULL, failure_code = NULL
            WHERE source_preflight_run_id = $run_id;
            """);
        Add(update, "$now", now);
        Add(update, "$run_id", runId);
        update.ExecuteNonQuery();
        transaction.Commit();
        return Get(new ImageImportPreflightGetParameters(runId));
    }

    internal IReadOnlyList<SourcePreflightWorkItem> ListWorkItems(
        string runId,
        bool includeCompleted = false)
    {
        runId = NormalizeId(runId, nameof(runId));
        using var connection = _database.OpenConnection();
        EnsureRunExists(connection, runId);
        using var command = Command(
            connection,
            null,
            """
            SELECT source_preflight_item_id, source_entry_key, display_name, sort_index,
                   candidate_kind, format_hint, byte_length_snapshot,
                   source_last_write_time_utc, source_identity_key, status,
                   evidence_state, evidence_json
            FROM source_preflight_items
            WHERE source_preflight_run_id = $run_id
              AND ($include_completed = 1 OR status = 'queued')
            ORDER BY CASE candidate_kind WHEN 'positioning_aux_candidate' THEN 0 ELSE 1 END,
                     sort_index, source_preflight_item_id;
            """);
        Add(command, "$run_id", runId);
        Add(command, "$include_completed", includeCompleted ? 1 : 0);
        using var reader = command.ExecuteReader();
        var items = new List<SourcePreflightWorkItem>();
        while (reader.Read())
        {
            var evidence = ReadEvidence(StringOrNull(reader, 11));
            items.Add(new SourcePreflightWorkItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                StringOrNull(reader, 5),
                LongOrNull(reader, 6),
                ParseOptionalTime(StringOrNull(reader, 7)),
                StringOrNull(reader, 8),
                reader.GetString(9),
                StringOrNull(reader, 10),
                evidence.EvidenceKinds,
                evidence.ReasonCodes));
        }

        return items;
    }

    internal void MarkItemRunning(string itemId)
    {
        itemId = NormalizeId(itemId, nameof(itemId));
        using var connection = _database.OpenConnection();
        using var command = Command(
            connection,
            null,
            """
            UPDATE source_preflight_items
            SET status = 'running', updated_at_utc = $now
            WHERE source_preflight_item_id = $item_id AND status = 'queued';
            """);
        Add(command, "$now", UtcNowText());
        Add(command, "$item_id", itemId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new BusinessCatalogException(
                "image_import_preflight_item_not_runnable",
                "The source preflight item cannot be started from its current state.");
        }
    }

    internal void CompleteItem(
        string itemId,
        ImageProbeSourcePreflightResult result)
    {
        itemId = NormalizeId(itemId, nameof(itemId));
        ArgumentNullException.ThrowIfNull(result);
        ValidateProbeResult(result);
        var evidenceJson = SerializeEvidence(result.EvidenceKinds, result.ReasonCodes);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var runId = ReadItemRunId(connection, transaction, itemId);
        var now = UtcNowText();
        using var update = Command(
            connection,
            transaction,
            """
            UPDATE source_preflight_items
            SET status = 'completed', container_hint = $container_hint,
                evidence_state = $evidence_state, evidence_json = $evidence_json,
                parser_profile = $parser_profile, parser_version = $parser_version,
                failure_code = NULL, updated_at_utc = $now, completed_at_utc = $now
            WHERE source_preflight_item_id = $item_id AND status = 'running';
            """);
        Add(update, "$container_hint", result.ContainerHint);
        Add(update, "$evidence_state", result.EvidenceState);
        Add(update, "$evidence_json", evidenceJson);
        Add(update, "$parser_profile", result.Profile);
        Add(update, "$parser_version", result.Parser.ProductParserVersion);
        Add(update, "$now", now);
        Add(update, "$item_id", itemId);
        if (update.ExecuteNonQuery() != 1)
        {
            throw new BusinessCatalogException(
                "image_import_preflight_item_not_running",
                "The source preflight item is not running.");
        }

        RefreshCounts(connection, transaction, runId, now);
        transaction.Commit();
    }

    internal void CompleteItemReadFailure(string itemId, string failureCode)
    {
        itemId = NormalizeId(itemId, nameof(itemId));
        failureCode = NormalizeCode(failureCode, nameof(failureCode));
        var evidenceJson = SerializeEvidence([], [failureCode]);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var runId = ReadItemRunId(connection, transaction, itemId);
        var now = UtcNowText();
        using var update = Command(
            connection,
            transaction,
            """
            UPDATE source_preflight_items
            SET status = 'completed', container_hint = 'unknown',
                evidence_state = 'read_failed', evidence_json = $evidence_json,
                parser_profile = $parser_profile, parser_version = $parser_version,
                failure_code = $failure_code, updated_at_utc = $now, completed_at_utc = $now
            WHERE source_preflight_item_id = $item_id AND status = 'running';
            """);
        Add(update, "$evidence_json", evidenceJson);
        Add(update, "$parser_profile", ParserProfile);
        Add(update, "$parser_version", ParserVersion);
        Add(update, "$failure_code", failureCode);
        Add(update, "$now", now);
        Add(update, "$item_id", itemId);
        if (update.ExecuteNonQuery() != 1)
        {
            throw new BusinessCatalogException(
                "image_import_preflight_item_not_running",
                "The source preflight item is not running.");
        }

        RefreshCounts(connection, transaction, runId, now);
        transaction.Commit();
    }

    internal ImageImportPreflightRun CommitDecision(
        string runId,
        string decision,
        string decisionReasonCode)
    {
        runId = NormalizeId(runId, nameof(runId));
        decision = NormalizeDecision(decision);
        decisionReasonCode = NormalizeCode(decisionReasonCode, nameof(decisionReasonCode));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadRunPrivate(connection, transaction, runId);
        if (current.Status == "completed")
        {
            transaction.Commit();
            return ReadRun(connection, null, runId);
        }

        if (current.Status != "running")
        {
            throw new BusinessCatalogException(
                "image_import_preflight_not_running",
                "The image import source preflight run is not running.");
        }

        var incomplete = ScalarLong(
            connection,
            transaction,
            "SELECT count(*) FROM source_preflight_items WHERE source_preflight_run_id = $run_id AND status IN ('queued', 'running');",
            ("$run_id", runId));
        if (incomplete != 0)
        {
            throw new BusinessCatalogException(
                "image_import_preflight_incomplete",
                "The image import source preflight run still has incomplete items.");
        }

        var now = UtcNowText();
        RefreshCounts(connection, transaction, runId, now);
        var counts = ReadDecisionCounts(connection, transaction, runId);
        ValidateDecision(decision, counts);
        var summaryJson = JsonSerializer.Serialize(
            new SourcePreflightDecisionSummary(
                "qiongtu.source-evidence.v1",
                runId,
                decision,
                decisionReasonCode,
                PolicyVersion,
                PathsIncluded: false,
                RawMetadataIncluded: false,
                CoordinatesIncluded: false,
                OwnerSampleStatisticsIncluded: false),
            SerializerOptions);

        using (var updateRun = Command(
            connection,
            transaction,
            """
            UPDATE source_preflight_runs
            SET status = 'completed', decision = $decision,
                decision_reason_code = $decision_reason_code,
                evidence_summary_json = $evidence_summary_json,
                failure_code = NULL, updated_at_utc = $now, completed_at_utc = $now
            WHERE source_preflight_run_id = $run_id AND status = 'running';
            """))
        {
            Add(updateRun, "$decision", decision);
            Add(updateRun, "$decision_reason_code", decisionReasonCode);
            Add(updateRun, "$evidence_summary_json", summaryJson);
            Add(updateRun, "$now", now);
            Add(updateRun, "$run_id", runId);
            updateRun.ExecuteNonQuery();
        }

        using (var updateDataset = Command(
            connection,
            transaction,
            """
            UPDATE dataset_versions
            SET source_eligibility_state = $decision,
                source_evidence_json = $evidence_summary_json,
                source_eligibility_run_id = $run_id,
                source_eligibility_decided_at_utc = $now
            WHERE dataset_version_id = $dataset_version_id AND lifecycle_state = 'draft';
            """))
        {
            Add(updateDataset, "$decision", decision);
            Add(updateDataset, "$evidence_summary_json", summaryJson);
            Add(updateDataset, "$run_id", runId);
            Add(updateDataset, "$now", now);
            Add(updateDataset, "$dataset_version_id", current.DatasetVersionId);
            if (updateDataset.ExecuteNonQuery() != 1)
            {
                throw new BusinessCatalogException(
                    "dataset_version_not_draft",
                    "Source preflight requires a draft dataset version.");
            }
        }

        if (decision == "dji_supported")
        {
            using (var releaseEntries = Command(
                connection,
                transaction,
                """
                UPDATE image_import_entries
                SET status = 'discovered', failure_code = NULL, updated_at_utc = $now
                WHERE import_session_id = $import_session_id
                  AND status = 'awaiting_source_preflight';
                """))
            {
                Add(releaseEntries, "$now", now);
                Add(releaseEntries, "$import_session_id", current.ImportSessionId);
                releaseEntries.ExecuteNonQuery();
            }

            using var releaseSession = Command(
                connection,
                transaction,
                """
                UPDATE image_import_sessions
                SET status = 'ready', last_error_code = NULL, updated_at_utc = $now
                WHERE import_session_id = $import_session_id
                  AND status = 'awaiting_source_preflight';
                """);
            Add(releaseSession, "$now", now);
            Add(releaseSession, "$import_session_id", current.ImportSessionId);
            if (releaseSession.ExecuteNonQuery() != 1)
            {
                throw new BusinessCatalogException(
                    "image_import_source_not_ready",
                    "The waiting image import session could not be released.");
            }
        }
        else
        {
            using var blockSession = Command(
                connection,
                transaction,
                """
                UPDATE image_import_sessions
                SET last_error_code = $reason_code, updated_at_utc = $now
                WHERE import_session_id = $import_session_id
                  AND status = 'awaiting_source_preflight';
                """);
            Add(blockSession, "$reason_code", decisionReasonCode);
            Add(blockSession, "$now", now);
            Add(blockSession, "$import_session_id", current.ImportSessionId);
            if (blockSession.ExecuteNonQuery() != 1)
            {
                throw new BusinessCatalogException(
                    "image_import_source_not_ready",
                    "The waiting image import session could not be updated.");
            }
        }

        transaction.Commit();
        return Get(new ImageImportPreflightGetParameters(runId));
    }

    internal IReadOnlyList<string> InterruptRunningRuns(string failureCode = "control_restarted")
    {
        failureCode = NormalizeCode(failureCode, nameof(failureCode));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var runIds = new List<string>();
        using (var select = Command(
            connection,
            transaction,
            "SELECT source_preflight_run_id FROM source_preflight_runs WHERE status = 'running' ORDER BY source_preflight_run_id;"))
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
            using (var resetItems = Command(
                connection,
                transaction,
                "UPDATE source_preflight_items SET status = 'queued', updated_at_utc = $now WHERE source_preflight_run_id = $run_id AND status = 'running';"))
            {
                Add(resetItems, "$now", now);
                Add(resetItems, "$run_id", runId);
                resetItems.ExecuteNonQuery();
            }

            using var interrupt = Command(
                connection,
                transaction,
                """
                UPDATE source_preflight_runs
                SET status = 'interrupted', failure_code = $failure_code,
                    updated_at_utc = $now, completed_at_utc = $now
                WHERE source_preflight_run_id = $run_id AND status = 'running';
                """);
            Add(interrupt, "$failure_code", failureCode);
            Add(interrupt, "$now", now);
            Add(interrupt, "$run_id", runId);
            interrupt.ExecuteNonQuery();
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
            "SELECT source_preflight_run_id FROM source_preflight_runs WHERE status IN ('queued', 'interrupted') ORDER BY created_at_utc, source_preflight_run_id;");
        using var reader = command.ExecuteReader();
        var runIds = new List<string>();
        while (reader.Read())
        {
            runIds.Add(reader.GetString(0));
        }

        return runIds;
    }

    private static void InsertItem(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourcePreflightDiscoveredItem item)
    {
        using var command = Command(
            connection,
            transaction,
            """
            INSERT INTO source_preflight_items(
                source_preflight_item_id, source_preflight_run_id, import_session_id,
                dataset_version_id, import_entry_id, source_entry_key, display_name,
                sort_index, candidate_kind, format_hint, byte_length_snapshot,
                source_last_write_time_utc, source_identity_key, status,
                created_at_utc, updated_at_utc)
            VALUES(
                $item_id, $run_id, $import_session_id,
                $dataset_version_id, $import_entry_id, $source_entry_key, $display_name,
                $sort_index, $candidate_kind, $format_hint, $byte_length_snapshot,
                $source_last_write_time_utc, $source_identity_key, 'queued',
                $created_at_utc, $updated_at_utc);
            """);
        Add(command, "$item_id", item.ItemId);
        Add(command, "$run_id", item.RunId);
        Add(command, "$import_session_id", item.ImportSessionId);
        Add(command, "$dataset_version_id", item.DatasetVersionId);
        Add(command, "$import_entry_id", item.ImportEntryId);
        Add(command, "$source_entry_key", item.SourceEntryKey);
        Add(command, "$display_name", item.DisplayName);
        Add(command, "$sort_index", item.SortIndex);
        Add(command, "$candidate_kind", item.CandidateKind);
        Add(command, "$format_hint", item.FormatHint);
        Add(command, "$byte_length_snapshot", item.ByteLengthSnapshot);
        Add(command, "$source_last_write_time_utc", item.SourceLastWriteTimeUtc);
        Add(command, "$source_identity_key", item.SourceIdentityKey);
        Add(command, "$created_at_utc", item.CreatedAtUtc);
        Add(command, "$updated_at_utc", item.CreatedAtUtc);
        command.ExecuteNonQuery();
    }

    private static void RefreshCounts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string now)
    {
        using var command = Command(
            connection,
            transaction,
            """
            UPDATE source_preflight_runs
            SET total_item_count = (SELECT count(*) FROM source_preflight_items i WHERE i.source_preflight_run_id = $run_id),
                image_candidate_count = (SELECT count(*) FROM source_preflight_items i WHERE i.source_preflight_run_id = $run_id AND i.candidate_kind = 'image_candidate'),
                sidecar_candidate_count = (SELECT count(*) FROM source_preflight_items i WHERE i.source_preflight_run_id = $run_id AND i.candidate_kind = 'positioning_aux_candidate'),
                completed_item_count = (SELECT count(*) FROM source_preflight_items i WHERE i.source_preflight_run_id = $run_id AND i.status IN ('completed', 'failed')),
                supports_dji_item_count = (SELECT count(*) FROM source_preflight_items i WHERE i.source_preflight_run_id = $run_id AND i.status = 'completed' AND i.evidence_state = 'supports_dji'),
                out_of_scope_item_count = (SELECT count(*) FROM source_preflight_items i WHERE i.source_preflight_run_id = $run_id AND i.status = 'completed' AND i.evidence_state = 'out_of_scope'),
                unconfirmed_item_count = (SELECT count(*) FROM source_preflight_items i WHERE i.source_preflight_run_id = $run_id AND i.status = 'completed' AND i.evidence_state = 'unconfirmed'),
                conflict_item_count = (SELECT count(*) FROM source_preflight_items i WHERE i.source_preflight_run_id = $run_id AND i.status = 'completed' AND i.evidence_state = 'conflict'),
                failed_item_count = (SELECT count(*) FROM source_preflight_items i WHERE i.source_preflight_run_id = $run_id AND (i.status = 'failed' OR i.evidence_state = 'read_failed')),
                blocking_image_count = (SELECT count(*) FROM source_preflight_items i WHERE i.source_preflight_run_id = $run_id AND i.candidate_kind = 'image_candidate' AND (i.status = 'failed' OR i.evidence_state IS NULL OR i.evidence_state <> 'supports_dji')),
                updated_at_utc = $now
            WHERE source_preflight_run_id = $run_id;
            """);
        Add(command, "$run_id", runId);
        Add(command, "$now", now);
        command.ExecuteNonQuery();
    }

    private static SourcePreflightDecisionCounts ReadDecisionCounts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT image_candidate_count, sidecar_candidate_count,
                   supports_dji_item_count, out_of_scope_item_count,
                   unconfirmed_item_count, conflict_item_count,
                   failed_item_count, blocking_image_count
            FROM source_preflight_runs
            WHERE source_preflight_run_id = $run_id;
            """);
        Add(command, "$run_id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException(
                "image_import_preflight_not_found",
                "The image import source preflight run was not found.");
        }

        return new SourcePreflightDecisionCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7));
    }

    private static void ValidateDecision(string decision, SourcePreflightDecisionCounts counts)
    {
        if (decision == "dji_supported" &&
            (counts.ImageCandidateCount == 0 || counts.SupportsDjiItemCount == 0 || counts.BlockingImageCount != 0))
        {
            throw new BusinessCatalogException(
                "image_import_preflight_decision_invalid",
                "The source preflight evidence cannot support a DJI eligibility decision.");
        }
    }

    private static ImageImportPreflightRun ReadRun(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT r.source_preflight_run_id, r.import_session_id, r.dataset_version_id,
                   dv.source_eligibility_state, r.status, r.decision, r.decision_reason_code,
                   r.parser_profile, r.parser_version, r.policy_version,
                   r.total_item_count, r.image_candidate_count, r.sidecar_candidate_count,
                   r.completed_item_count, r.supports_dji_item_count,
                   r.out_of_scope_item_count, r.unconfirmed_item_count,
                   r.conflict_item_count, r.failed_item_count, r.blocking_image_count,
                   r.failure_code, r.created_at_utc, r.started_at_utc,
                   r.updated_at_utc, r.completed_at_utc
            FROM source_preflight_runs r
            JOIN dataset_versions dv ON dv.dataset_version_id = r.dataset_version_id
            WHERE r.source_preflight_run_id = $run_id;
            """);
        Add(command, "$run_id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException(
                "image_import_preflight_not_found",
                "The image import source preflight run was not found.");
        }

        return new ImageImportPreflightRun(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            StringOrNull(reader, 5),
            StringOrNull(reader, 6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetInt32(15),
            reader.GetInt32(16),
            reader.GetInt32(17),
            reader.GetInt32(18),
            reader.GetInt32(19),
            StringOrNull(reader, 20),
            ParseTime(reader.GetString(21)),
            ParseOptionalTime(StringOrNull(reader, 22)),
            ParseTime(reader.GetString(23)),
            ParseOptionalTime(StringOrNull(reader, 24)),
            ResponsePrivacy);
    }

    private static ImageImportPreflightRun? TryReadRunForSession(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string importSessionId)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT source_preflight_run_id FROM source_preflight_runs WHERE import_session_id = $import_session_id;");
        Add(command, "$import_session_id", importSessionId);
        var runId = command.ExecuteScalar() as string;
        return runId is null ? null : ReadRun(connection, transaction, runId);
    }

    private static ImageImportPreflightItem ReadItem(SqliteDataReader reader)
    {
        var evidence = ReadEvidence(StringOrNull(reader, 11));
        return new ImageImportPreflightItem(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetString(6),
            StringOrNull(reader, 7),
            reader.GetString(8),
            StringOrNull(reader, 9),
            StringOrNull(reader, 10),
            evidence.EvidenceKinds,
            evidence.ReasonCodes,
            StringOrNull(reader, 12),
            ParseTime(reader.GetString(13)),
            ParseTime(reader.GetString(14)),
            ParseOptionalTime(StringOrNull(reader, 15)),
            ResponsePrivacy);
    }

    private static SourcePreflightRunPrivate ReadRunPrivate(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT source_preflight_run_id, import_session_id, dataset_version_id, status
            FROM source_preflight_runs
            WHERE source_preflight_run_id = $run_id;
            """);
        Add(command, "$run_id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException(
                "image_import_preflight_not_found",
                "The image import source preflight run was not found.");
        }

        return new SourcePreflightRunPrivate(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
    }

    private static SourcePreflightSessionBinding ReadWaitingSessionBinding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string importSessionId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT s.import_session_id, s.dataset_version_id, s.source_root_key,
                   s.source_locator_manifest_id, s.status,
                   dv.lifecycle_state, dv.source_eligibility_state
            FROM image_import_sessions s
            JOIN dataset_versions dv ON dv.dataset_version_id = s.dataset_version_id
            WHERE s.import_session_id = $import_session_id;
            """);
        Add(command, "$import_session_id", importSessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException(
                "image_import_session_not_found",
                "The image import session was not found.");
        }

        if (reader.GetString(4) != "awaiting_source_preflight" ||
            reader.GetString(5) != "draft" ||
            reader.GetString(6) is not ("pending" or "unconfirmed"))
        {
            throw new BusinessCatalogException(
                "image_import_source_not_ready",
                "The image import session is not waiting for a source preflight decision.");
        }

        return new SourcePreflightSessionBinding(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
    }

    private static string ReadItemRunId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT source_preflight_run_id FROM source_preflight_items WHERE source_preflight_item_id = $item_id;");
        Add(command, "$item_id", itemId);
        return command.ExecuteScalar() as string
            ?? throw new BusinessCatalogException(
                "image_import_preflight_item_not_found",
                "The source preflight item was not found.");
    }

    private static void EnsureRunExists(SqliteConnection connection, string runId)
    {
        using var command = Command(
            connection,
            null,
            "SELECT 1 FROM source_preflight_runs WHERE source_preflight_run_id = $run_id;");
        Add(command, "$run_id", runId);
        if (command.ExecuteScalar() is null)
        {
            throw new BusinessCatalogException(
                "image_import_preflight_not_found",
                "The image import source preflight run was not found.");
        }
    }

    private T ExecuteIdempotent<T>(
        string requestId,
        string method,
        object normalizedParameters,
        Func<SqliteConnection, SqliteTransaction, T> operation)
    {
        requestId = NormalizeIdentifier(requestId, "requestId", 128);
        var parameterHash = Sha256Hex(
            JsonSerializer.SerializeToUtf8Bytes(normalizedParameters, SerializerOptions));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var committed = false;
        try
        {
            var existing = ReadMutation(connection, transaction, requestId);
            if (existing is not null)
            {
                if (existing.Method != method ||
                    !string.Equals(existing.ParametersSha256, parameterHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new BusinessCatalogException(
                        "idempotency_conflict",
                        "The requestId was already used with different parameters.");
                }

                var replay = JsonSerializer.Deserialize<T>(existing.ResponseJson, SerializerOptions)
                    ?? throw new BusinessCatalogException(
                        "idempotency_replay_failed",
                        "The saved source preflight response could not be read.");
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
        var json = JsonSerializer.Serialize(response, SerializerOptions);
        if (Encoding.UTF8.GetByteCount(json) > _maximumResponseBytes)
        {
            throw new BusinessCatalogException(
                "response_too_large",
                "The source preflight response exceeds the control protocol size limit.");
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

    private static SourcePreflightSidecarCandidate NormalizeSidecarCandidate(
        SourcePreflightSidecarCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var formatHint = NormalizeRequiredText(candidate.FormatHint, "formatHint", 16).ToLowerInvariant();
        if (formatHint is not ("mrk" or "nav" or "obs" or "rtk"))
        {
            throw new BusinessCatalogException(
                "invalid_parameters",
                "The source preflight sidecar format hint is unsupported.");
        }

        return candidate with
        {
            SourceEntryKey = NormalizeSha256(candidate.SourceEntryKey, nameof(candidate.SourceEntryKey)),
            DisplayName = NormalizeDisplayName(candidate.DisplayName),
            FormatHint = formatHint
        };
    }

    private static void ValidateProbeResult(ImageProbeSourcePreflightResult result)
    {
        if (result.SchemaVersion != ImageProbeProtocol.SourcePreflightV1 ||
            result.Profile != ImageProbeProtocol.SourcePreflightProfile ||
            result.Status != "completed" ||
            result.CandidateKind is not ("image_candidate" or "positioning_aux_candidate") ||
            result.ContainerHint is not ("jpeg_hint" or "mpo_hint" or "tiff" or "bigtiff" or "not_image" or "unknown") ||
            result.EvidenceState is not ("supports_dji" or "out_of_scope" or "unconfirmed" or "conflict") ||
            result.EvidenceKinds.Count > ImageProbeProtocol.MaximumEvidenceKinds ||
            result.ReasonCodes.Count > ImageProbeProtocol.MaximumReasonCodes ||
            result.Privacy.PathsIncluded || result.Privacy.LocatorsIncluded ||
            result.Privacy.ContentHashesIncluded || result.Privacy.ObjectKeysIncluded ||
            result.Privacy.RawMetadataIncluded || result.Privacy.SerialNumbersIncluded ||
            result.Privacy.CoordinatesIncluded || result.Privacy.OwnerSampleStatisticsIncluded)
        {
            throw new BusinessCatalogException(
                "image_probe_response_invalid",
                "The source preflight probe response is invalid.");
        }
    }

    private static string SerializeEvidence(
        IEnumerable<string> evidenceKinds,
        IEnumerable<string> reasonCodes)
    {
        var evidence = evidenceKinds
            .Select(value => NormalizeCode(value, "evidenceKind"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(ImageProbeProtocol.MaximumEvidenceKinds)
            .ToArray();
        var reasons = reasonCodes
            .Select(value => NormalizeCode(value, "reasonCode"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(ImageProbeProtocol.MaximumReasonCodes)
            .ToArray();
        return JsonSerializer.Serialize(
            new SourcePreflightEvidence(evidence, reasons),
            SerializerOptions);
    }

    private static SourcePreflightEvidence ReadEvidence(string? json)
    {
        if (json is null)
        {
            return new SourcePreflightEvidence([], []);
        }

        try
        {
            return JsonSerializer.Deserialize<SourcePreflightEvidence>(json, SerializerOptions)
                ?? new SourcePreflightEvidence([], []);
        }
        catch (JsonException exception)
        {
            throw new BusinessCatalogException(
                "image_import_preflight_evidence_invalid",
                "The source preflight evidence record is invalid.",
                exception);
        }
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

        return new BusinessPage(
            pageSize,
            NormalizeOptionalText(requestedCursor, "cursor", 512));
    }

    private static string EncodeCursor(int sortIndex, string id, string method, string scope)
    {
        var json = JsonSerializer.Serialize(
            new SourcePreflightCursor(1, method, scope, sortIndex, id),
            SerializerOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static SourcePreflightCursor? DecodeCursor(string? cursor, string method, string scope)
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
            var decoded = JsonSerializer.Deserialize<SourcePreflightCursor>(json, SerializerOptions)
                ?? throw new JsonException();
            if (decoded.Version != 1 || decoded.SortIndex < 0 ||
                decoded.Method != method || decoded.Scope != scope)
            {
                throw new BusinessCatalogException(
                    "invalid_cursor",
                    "The page cursor does not belong to this source preflight item list.");
            }

            return decoded with { Id = NormalizeId(decoded.Id, "cursor.id") };
        }
        catch (Exception exception) when (exception is FormatException
            or JsonException
            or BusinessCatalogException)
        {
            throw new BusinessCatalogException(
                "invalid_cursor",
                "The source preflight item cursor is invalid.",
                exception);
        }
    }

    private static string FormatHint(string displayName)
    {
        var extension = Path.GetExtension(displayName).TrimStart('.').ToLowerInvariant();
        return extension is "jpg" or "jpeg" or "mpo" or "tif" or "tiff"
            ? extension
            : throw new BusinessCatalogException(
                "invalid_parameters",
                "The image import candidate has an unsupported format hint.");
    }

    private static string NormalizeDecision(string value)
    {
        var normalized = NormalizeRequiredText(value, nameof(value), 32);
        return normalized is "dji_supported" or "out_of_scope" or "unconfirmed"
            ? normalized
            : throw new BusinessCatalogException(
                "invalid_parameters",
                "The source preflight decision is invalid.");
    }

    private static string NormalizeCode(string value, string fieldName)
    {
        var normalized = NormalizeIdentifier(value, fieldName, 128);
        return normalized;
    }

    private static string NormalizeId(string value, string fieldName) =>
        NormalizeIdentifier(value, fieldName, 128);

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

    private static string NormalizeDisplayName(string value)
    {
        var normalized = NormalizeRequiredText(value, "displayName", 255);
        if (normalized.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            throw new BusinessCatalogException(
                "invalid_parameters",
                "displayName must be a leaf name, not a path.");
        }

        return normalized;
    }

    private static string NormalizeSha256(string value, string fieldName)
    {
        var normalized = NormalizeRequiredText(value, fieldName, 64).ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new BusinessCatalogException(
                "invalid_parameters",
                $"{fieldName} must be a SHA-256 hex string.");
        }

        return normalized;
    }

    private static string NormalizeRequiredText(string value, string fieldName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new BusinessCatalogException("invalid_parameters", $"{fieldName} is required.");
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new BusinessCatalogException(
                "invalid_parameters",
                $"{fieldName} exceeds the maximum length.");
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
            throw new BusinessCatalogException(
                "invalid_parameters",
                $"{fieldName} exceeds the maximum length.");
        }

        return normalized;
    }

    private static long ScalarLong(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql);
        foreach (var parameter in parameters)
        {
            Add(command, parameter.Name, parameter.Value);
        }

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static string UtcNowText() =>
        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record SourcePreflightDiscoveredItem(
        string ItemId,
        string RunId,
        string ImportSessionId,
        string DatasetVersionId,
        string? ImportEntryId,
        string SourceEntryKey,
        string DisplayName,
        int SortIndex,
        string CandidateKind,
        string? FormatHint,
        long? ByteLengthSnapshot,
        string? SourceLastWriteTimeUtc,
        string? SourceIdentityKey,
        string CreatedAtUtc);

    private sealed record SourcePreflightSessionBinding(
        string ImportSessionId,
        string DatasetVersionId,
        string SourceRootKey,
        string SourceLocatorManifestId);

    private sealed record SourcePreflightRunPrivate(
        string RunId,
        string ImportSessionId,
        string DatasetVersionId,
        string Status);

    private sealed record SourcePreflightEvidence(
        IReadOnlyList<string> EvidenceKinds,
        IReadOnlyList<string> ReasonCodes);

    private sealed record SourcePreflightCursor(
        int Version,
        string Method,
        string Scope,
        int SortIndex,
        string Id);

    private sealed record SourcePreflightDecisionSummary(
        string SchemaVersion,
        string PreflightRunId,
        string Decision,
        string ReasonCode,
        string PolicyVersion,
        bool PathsIncluded,
        bool RawMetadataIncluded,
        bool CoordinatesIncluded,
        bool OwnerSampleStatisticsIncluded);

    private sealed record SourcePreflightDecisionCounts(
        int ImageCandidateCount,
        int SidecarCandidateCount,
        int SupportsDjiItemCount,
        int OutOfScopeItemCount,
        int UnconfirmedItemCount,
        int ConflictItemCount,
        int FailedItemCount,
        int BlockingImageCount);

    private sealed record CatalogMutation(
        string Method,
        string ParametersSha256,
        string ResponseJson);
}

internal sealed record SourcePreflightRunBinding(
    string RunId,
    string ImportSessionId,
    string DatasetVersionId,
    string SourceRootKey,
    string SourceLocatorManifestId,
    string Status);

internal sealed record SourcePreflightSidecarCandidate(
    string SourceEntryKey,
    string DisplayName,
    string FormatHint,
    ImageImportSourceSnapshot Snapshot);

internal sealed record SourcePreflightWorkItem(
    string ItemId,
    string SourceEntryKey,
    string DisplayName,
    int SortIndex,
    string CandidateKind,
    string? FormatHint,
    long? ByteLengthSnapshot,
    DateTimeOffset? SourceLastWriteTimeUtc,
    string? SourceIdentityKey,
    string Status,
    string? EvidenceState,
    IReadOnlyList<string> EvidenceKinds,
    IReadOnlyList<string> ReasonCodes);
