using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control;

public sealed class ImageImportCatalog
{
    public const int DefaultPageSize = BusinessCatalog.DefaultPageSize;
    public const int MaximumPageSize = BusinessCatalog.MaximumPageSize;
    public const int MaximumCatalogPayloadBytes = BusinessCatalog.MaximumCatalogPayloadBytes;

    private const string StatusAwaitingSourcePreflight = "awaiting_source_preflight";
    private const string StatusAwaitingSource = "awaiting_source";
    private const string StatusReady = "ready";
    private const string StatusStaging = "staging";
    private const string StatusPublishing = "publishing";
    private const string StatusCompleted = "completed";
    private const string StatusCancelled = "cancelled";
    private const string StatusFailed = "failed";
    private const string EntryDiscovered = "discovered";
    private const string EntryStaging = "staging";
    private const string EntryStaged = "staged";
    private const string EntryPublishing = "publishing";
    private const string EntryAvailable = "available";
    private const string EntryDuplicate = "duplicate";
    private const string EntryCancelled = "cancelled";
    private const string EntrySourceLocked = "source_locked";
    private const string EntrySourceMissing = "source_missing";
    private const string EntrySourceUnavailable = "source_unavailable";
    private const string EntrySourceChanged = "source_changed";
    private const string EntryIntegrityFailed = "integrity_failed";
    private const string EntryStorageFull = "storage_full";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly ImageImportPrivacy ResponsePrivacy = new(false, false, false, false, false, false);

    private readonly BusinessDatabase _database;
    private readonly int _maximumResponseBytes;

    public ImageImportCatalog(BusinessDatabase database, int maximumResponseBytes = MaximumCatalogPayloadBytes)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        if (maximumResponseBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        }

        _maximumResponseBytes = maximumResponseBytes;
    }

    internal void ValidateDatasetVersionForImport(string datasetVersionId)
    {
        datasetVersionId = NormalizeId(datasetVersionId, nameof(datasetVersionId));
        using var connection = _database.OpenConnection();
        _ = ReadDatasetVersionGate(connection, null, datasetVersionId);
    }

    internal ImageImportSession StartPrepared(
        string requestId,
        string importSessionId,
        string datasetVersionId,
        string sourceRootKey,
        string sourceLocatorManifestId,
        IEnumerable<ImageImportDiscoveredEntry>? discoveredEntries = null)
    {
        importSessionId = NormalizeId(importSessionId, nameof(importSessionId));
        var prepared = new ImageImportPreparedStartParameters(
            NormalizeId(datasetVersionId, nameof(datasetVersionId)),
            NormalizeSha256(sourceRootKey, nameof(sourceRootKey)),
            NormalizeIdentifier(sourceLocatorManifestId, nameof(sourceLocatorManifestId), 128));
        var normalizedEntries = NormalizeDiscoveredEntries(discoveredEntries ?? []);
        if (normalizedEntries.Any(entry => !string.Equals(entry.ImportSessionId, importSessionId, StringComparison.Ordinal)))
        {
            throw new BusinessCatalogException(
                "invalid_parameters",
                "Discovered import entries must belong to the prepared session.");
        }

        return ExecutePreparedStart(
            requestId,
            importSessionId,
            prepared,
            new IdempotentPreparedImageImportStartParameters(importSessionId, prepared),
            normalizedEntries);
    }

    internal ImageImportSession? TryReplayPreparedStart(
        string requestId,
        string importSessionId,
        string datasetVersionId,
        string sourceRootKey,
        string sourceLocatorManifestId)
    {
        var prepared = new ImageImportPreparedStartParameters(
            NormalizeId(datasetVersionId, nameof(datasetVersionId)),
            NormalizeSha256(sourceRootKey, nameof(sourceRootKey)),
            NormalizeIdentifier(sourceLocatorManifestId, nameof(sourceLocatorManifestId), 128));
        var idempotent = new IdempotentPreparedImageImportStartParameters(
            NormalizeId(importSessionId, nameof(importSessionId)),
            prepared);
        return TryReplayIdempotent<ImageImportSession>(
            requestId,
            ControlMethods.ImageImportStart,
            idempotent);
    }

    private ImageImportSession ExecutePreparedStart(
        string requestId,
        string importSessionId,
        ImageImportPreparedStartParameters prepared,
        object idempotentParameters,
        IReadOnlyList<ImageImportDiscoveredEntry> discoveredEntries)
    {
        return ExecuteIdempotent(requestId, ControlMethods.ImageImportStart, idempotentParameters, (connection, transaction) =>
        {
            var dataset = ReadDatasetVersionGate(connection, transaction, prepared.DatasetVersionId);
            var status = string.Equals(dataset.SourceEligibilityState, "dji_supported", StringComparison.Ordinal)
                ? StatusReady
                : StatusAwaitingSourcePreflight;
            var now = UtcNowText();
            using var command = Command(
                connection,
                transaction,
                """
                INSERT INTO image_import_sessions(
                    import_session_id, dataset_version_id, source_root_key, source_locator_manifest_id,
                    status, created_at_utc, updated_at_utc)
                VALUES(
                    $import_session_id, $dataset_version_id, $source_root_key, $source_locator_manifest_id,
                    $status, $created_at_utc, $updated_at_utc);
                """);
            Add(command, "$import_session_id", importSessionId);
            Add(command, "$dataset_version_id", prepared.DatasetVersionId);
            Add(command, "$source_root_key", prepared.SourceRootKey);
            Add(command, "$source_locator_manifest_id", prepared.SourceLocatorManifestId);
            Add(command, "$status", status);
            Add(command, "$created_at_utc", now);
            Add(command, "$updated_at_utc", now);
            command.ExecuteNonQuery();
            if (discoveredEntries.Count > 0)
            {
                var session = ReadSession(connection, transaction, importSessionId);
                InsertDiscoveredEntries(connection, transaction, session, discoveredEntries);
                RefreshSessionCounts(connection, transaction, importSessionId);
            }

            return ReadSession(connection, transaction, importSessionId);
        });
    }

    internal ImageImportSession ResumePrepared(
        string requestId,
        string importSessionId,
        string? sourceRootKey = null)
    {
        importSessionId = NormalizeId(importSessionId, nameof(importSessionId));
        sourceRootKey = sourceRootKey is null
            ? null
            : NormalizeSha256(sourceRootKey, nameof(sourceRootKey));
        var normalized = new IdempotentPreparedImageImportResumeParameters(importSessionId, sourceRootKey);
        return ExecuteIdempotent(requestId, ControlMethods.ImageImportResume, normalized, (connection, transaction) =>
        {
            var session = ReadSession(connection, transaction, normalized.ImportSessionId);
            if (IsTerminalSession(session.Status))
            {
                return session;
            }

            var dataset = ReadDatasetVersionGate(connection, transaction, session.DatasetVersionId);
            var sourceEligible = string.Equals(dataset.SourceEligibilityState, "dji_supported", StringComparison.Ordinal);
            var nextStatus = sourceEligible
                ? StatusReady
                : StatusAwaitingSourcePreflight;
            using var update = Command(
                connection,
                transaction,
                "UPDATE image_import_sessions SET status = $status, source_root_key = COALESCE($source_root_key, source_root_key), updated_at_utc = $updated_at_utc, last_error_code = NULL WHERE import_session_id = $import_session_id;");
            Add(update, "$status", nextStatus);
            Add(update, "$source_root_key", normalized.SourceRootKey);
            Add(update, "$updated_at_utc", UtcNowText());
            Add(update, "$import_session_id", normalized.ImportSessionId);
            update.ExecuteNonQuery();
            if (sourceEligible)
            {
                using var releaseEntries = Command(
                    connection,
                    transaction,
                    "UPDATE image_import_entries SET status = 'discovered', failure_code = NULL, updated_at_utc = $updated_at_utc WHERE import_session_id = $import_session_id AND status = 'awaiting_source_preflight';");
                Add(releaseEntries, "$updated_at_utc", UtcNowText());
                Add(releaseEntries, "$import_session_id", normalized.ImportSessionId);
                releaseEntries.ExecuteNonQuery();
            }

            RefreshSessionCounts(connection, transaction, normalized.ImportSessionId);
            return ReadSession(connection, transaction, normalized.ImportSessionId);
        });
    }

    internal ImageImportSession? TryReplayPreparedResume(
        string requestId,
        string importSessionId,
        string? sourceRootKey)
    {
        importSessionId = NormalizeId(importSessionId, nameof(importSessionId));
        sourceRootKey = sourceRootKey is null
            ? null
            : NormalizeSha256(sourceRootKey, nameof(sourceRootKey));
        return TryReplayIdempotent<ImageImportSession>(
            requestId,
            ControlMethods.ImageImportResume,
            new IdempotentPreparedImageImportResumeParameters(importSessionId, sourceRootKey));
    }

    public ImageImportSession Cancel(string requestId, ImageImportCancelParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var normalized = parameters with { ImportSessionId = NormalizeId(parameters.ImportSessionId, nameof(parameters.ImportSessionId)) };
        return ExecuteIdempotent(requestId, ControlMethods.ImageImportCancel, normalized, (connection, transaction) =>
        {
            var session = ReadSession(connection, transaction, normalized.ImportSessionId);
            if (IsTerminalSession(session.Status))
            {
                return session;
            }

            var now = UtcNowText();
            using (var cancelEntries = Command(
                connection,
                transaction,
                """
                UPDATE image_import_entries
                SET status = 'cancelled', failure_code = 'cancelled_by_user',
                    updated_at_utc = $updated_at_utc, terminal_at_utc = $terminal_at_utc
                WHERE import_session_id = $import_session_id
                  AND status NOT IN ('available', 'duplicate', 'cancelled', 'integrity_failed', 'storage_full');
                """))
            {
                Add(cancelEntries, "$updated_at_utc", now);
                Add(cancelEntries, "$terminal_at_utc", now);
                Add(cancelEntries, "$import_session_id", normalized.ImportSessionId);
                cancelEntries.ExecuteNonQuery();
            }

            using (var cancelSession = Command(
                connection,
                transaction,
                """
                UPDATE image_import_sessions
                SET status = 'cancelled', updated_at_utc = $updated_at_utc, cancelled_at_utc = $cancelled_at_utc
                WHERE import_session_id = $import_session_id;
                """))
            {
                Add(cancelSession, "$updated_at_utc", now);
                Add(cancelSession, "$cancelled_at_utc", now);
                Add(cancelSession, "$import_session_id", normalized.ImportSessionId);
                cancelSession.ExecuteNonQuery();
            }

            RefreshSessionCounts(connection, transaction, normalized.ImportSessionId, allowTerminalUpdate: true);
            return ReadSession(connection, transaction, normalized.ImportSessionId);
        });
    }

    public ImageImportSession Get(ImageImportGetParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        using var connection = _database.OpenConnection();
        var result = ReadSession(connection, null, NormalizeId(parameters.ImportSessionId, nameof(parameters.ImportSessionId)));
        EnsureResponseWithinLimit(result);
        return result;
    }

    public PageResult<ImageImportSession> List(ImageImportListParameters? parameters = null)
    {
        var datasetVersionId = NormalizeOptionalId(parameters?.DatasetVersionId, nameof(ImageImportListParameters.DatasetVersionId));
        var page = NormalizePage(parameters?.PageSize, parameters?.Cursor);
        var scope = datasetVersionId ?? string.Empty;
        var cursor = DecodeCursor(page.Cursor, ControlMethods.ImageImportList, scope);
        using var connection = _database.OpenConnection();
        using var command = Command(
            connection,
            null,
            """
            SELECT import_session_id, created_at_utc
            FROM image_import_sessions
            WHERE ($dataset_version_id IS NULL OR dataset_version_id = $dataset_version_id)
              AND ($cursor_position IS NULL OR
                   created_at_utc < $cursor_position OR
                   (created_at_utc = $cursor_position AND import_session_id < $cursor_id))
            ORDER BY created_at_utc DESC, import_session_id DESC
            LIMIT $limit;
            """);
        Add(command, "$dataset_version_id", datasetVersionId);
        Add(command, "$cursor_position", cursor?.Position);
        Add(command, "$cursor_id", cursor?.Id);
        Add(command, "$limit", page.PageSize + 1);
        var identities = new List<(string Id, string Position)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                identities.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var rows = identities
            .Select(identity => (Item: ReadSession(connection, null, identity.Id), Position: identity.Position, Id: identity.Id))
            .ToList();
        var result = ToPage(rows, page.PageSize, ControlMethods.ImageImportList, scope);
        EnsureResponseWithinLimit(result);
        return result;
    }

    public PageResult<ImageImportEntry> ListEntries(ImageImportEntryListParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var sessionId = NormalizeId(parameters.ImportSessionId, nameof(parameters.ImportSessionId));
        var page = NormalizePage(parameters.PageSize, parameters.Cursor);
        var cursor = DecodeCursor(page.Cursor, ControlMethods.ImageImportEntryList, sessionId);
        int? cursorSortIndex = null;
        if (cursor is not null)
        {
            if (!int.TryParse(cursor.Position, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPosition) ||
                parsedPosition < 0)
            {
                throw new BusinessCatalogException("invalid_cursor", "The page cursor is invalid.");
            }

            cursorSortIndex = parsedPosition;
        }

        using var connection = _database.OpenConnection();
        EnsureSessionExists(connection, sessionId);
        using var command = Command(
            connection,
            null,
            """
            SELECT import_entry_id, sort_index
            FROM image_import_entries
            WHERE import_session_id = $import_session_id
              AND ($cursor_position IS NULL OR
                   sort_index > $cursor_sort_index OR
                   (sort_index = $cursor_sort_index AND import_entry_id > $cursor_id))
            ORDER BY sort_index ASC, import_entry_id ASC
            LIMIT $limit;
            """);
        Add(command, "$import_session_id", sessionId);
        Add(command, "$cursor_position", cursor?.Position);
        Add(command, "$cursor_sort_index", cursorSortIndex);
        Add(command, "$cursor_id", cursor?.Id);
        Add(command, "$limit", page.PageSize + 1);
        var identities = new List<(string Id, string Position)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                identities.Add((reader.GetString(0), reader.GetInt32(1).ToString(CultureInfo.InvariantCulture)));
            }
        }

        var rows = identities
            .Select(identity => (Item: ReadEntry(connection, null, identity.Id), Position: identity.Position, Id: identity.Id))
            .ToList();
        var result = ToPage(rows, page.PageSize, ControlMethods.ImageImportEntryList, sessionId);
        EnsureResponseWithinLimit(result);
        return result;
    }

    internal ImageImportEntry RegisterDiscoveredEntry(ImageImportDiscoveredEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var normalized = entry with
        {
            ImportSessionId = NormalizeId(entry.ImportSessionId, nameof(entry.ImportSessionId)),
            SourceEntryKey = NormalizeSha256(entry.SourceEntryKey, nameof(entry.SourceEntryKey)),
            DisplayName = NormalizeDisplayName(entry.DisplayName),
            SourceIdentityKey = NormalizeOptionalSha256(entry.SourceIdentityKey, nameof(entry.SourceIdentityKey))
        };

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var session = ReadSession(connection, transaction, normalized.ImportSessionId);
        var status = string.Equals(session.SourceEligibilityState, "dji_supported", StringComparison.Ordinal)
            ? EntryDiscovered
            : StatusAwaitingSourcePreflight;
        var entryId = NewId("image-import-entry");
        var now = UtcNowText();
        using var command = Command(
            connection,
            transaction,
            """
            INSERT INTO image_import_entries(
                import_entry_id, import_session_id, dataset_version_id, source_entry_key,
                display_name, sort_index, byte_length_snapshot, source_last_write_time_utc,
                source_identity_key, status, created_at_utc, updated_at_utc)
            VALUES(
                $import_entry_id, $import_session_id, $dataset_version_id, $source_entry_key,
                $display_name, $sort_index, $byte_length_snapshot, $source_last_write_time_utc,
                $source_identity_key, $status, $created_at_utc, $updated_at_utc);
            """);
        Add(command, "$import_entry_id", entryId);
        Add(command, "$import_session_id", normalized.ImportSessionId);
        Add(command, "$dataset_version_id", session.DatasetVersionId);
        Add(command, "$source_entry_key", normalized.SourceEntryKey);
        Add(command, "$display_name", normalized.DisplayName);
        Add(command, "$sort_index", normalized.SortIndex);
        Add(command, "$byte_length_snapshot", normalized.ByteLengthSnapshot);
        Add(command, "$source_last_write_time_utc", normalized.SourceLastWriteTimeUtc?.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "$source_identity_key", normalized.SourceIdentityKey);
        Add(command, "$status", status);
        Add(command, "$created_at_utc", now);
        Add(command, "$updated_at_utc", now);
        command.ExecuteNonQuery();
        RefreshSessionCounts(connection, transaction, normalized.ImportSessionId);
        var result = ReadEntry(connection, transaction, entryId);
        transaction.Commit();
        EnsureResponseWithinLimit(result);
        return result;
    }

    internal IReadOnlyList<ImageImportEntry> RegisterDiscoveredEntries(IEnumerable<ImageImportDiscoveredEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalizedEntries = NormalizeDiscoveredEntries(entries);

        if (normalizedEntries.Length == 0)
        {
            return Array.Empty<ImageImportEntry>();
        }

        var sessionId = normalizedEntries[0].ImportSessionId;
        if (normalizedEntries.Any(entry => !string.Equals(entry.ImportSessionId, sessionId, StringComparison.Ordinal)))
        {
            throw new BusinessCatalogException("invalid_parameters", "Discovered import entries must belong to one session.");
        }

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var session = ReadSession(connection, transaction, sessionId);
        InsertDiscoveredEntries(connection, transaction, session, normalizedEntries);

        RefreshSessionCounts(connection, transaction, sessionId);
        var result = ReadEntriesForSession(connection, transaction, sessionId);
        transaction.Commit();
        return result;
    }

    private static ImageImportDiscoveredEntry[] NormalizeDiscoveredEntries(
        IEnumerable<ImageImportDiscoveredEntry> entries) =>
        entries.Select(entry =>
        {
            ArgumentNullException.ThrowIfNull(entry);
            return entry with
            {
                ImportSessionId = NormalizeId(entry.ImportSessionId, nameof(entry.ImportSessionId)),
                SourceEntryKey = NormalizeSha256(entry.SourceEntryKey, nameof(entry.SourceEntryKey)),
                DisplayName = NormalizeDisplayName(entry.DisplayName),
                SourceIdentityKey = NormalizeOptionalSha256(entry.SourceIdentityKey, nameof(entry.SourceIdentityKey))
            };
        }).ToArray();

    private static void InsertDiscoveredEntries(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImageImportSession session,
        IReadOnlyList<ImageImportDiscoveredEntry> entries)
    {
        var status = string.Equals(session.SourceEligibilityState, "dji_supported", StringComparison.Ordinal)
            ? EntryDiscovered
            : StatusAwaitingSourcePreflight;
        var now = UtcNowText();
        foreach (var entry in entries)
        {
            using var command = Command(
                connection,
                transaction,
                """
                INSERT INTO image_import_entries(
                    import_entry_id, import_session_id, dataset_version_id, source_entry_key,
                    display_name, sort_index, byte_length_snapshot, source_last_write_time_utc,
                    source_identity_key, status, created_at_utc, updated_at_utc)
                VALUES(
                    $import_entry_id, $import_session_id, $dataset_version_id, $source_entry_key,
                    $display_name, $sort_index, $byte_length_snapshot, $source_last_write_time_utc,
                    $source_identity_key, $status, $created_at_utc, $updated_at_utc)
                ON CONFLICT(import_session_id, source_entry_key) DO NOTHING;
                """);
            Add(command, "$import_entry_id", NewId("image-import-entry"));
            Add(command, "$import_session_id", entry.ImportSessionId);
            Add(command, "$dataset_version_id", session.DatasetVersionId);
            Add(command, "$source_entry_key", entry.SourceEntryKey);
            Add(command, "$display_name", entry.DisplayName);
            Add(command, "$sort_index", entry.SortIndex);
            Add(command, "$byte_length_snapshot", entry.ByteLengthSnapshot);
            Add(command, "$source_last_write_time_utc", entry.SourceLastWriteTimeUtc?.ToString("O", CultureInfo.InvariantCulture));
            Add(command, "$source_identity_key", entry.SourceIdentityKey);
            Add(command, "$status", status);
            Add(command, "$created_at_utc", now);
            Add(command, "$updated_at_utc", now);
            command.ExecuteNonQuery();
        }
    }

    internal ImageImportEntry MarkStaging(string importEntryId)
    {
        importEntryId = NormalizeId(importEntryId, nameof(importEntryId));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = UtcNowText();
        using var command = Command(
            connection,
            transaction,
            """
            UPDATE image_import_entries
            SET status = 'staging', failure_code = NULL, updated_at_utc = $updated_at_utc
            WHERE import_entry_id = $import_entry_id
              AND status IN ('discovered', 'source_locked', 'source_missing', 'source_unavailable', 'source_changed');
            """);
        Add(command, "$updated_at_utc", now);
        Add(command, "$import_entry_id", importEntryId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new BusinessCatalogException("image_import_entry_not_mutable", "The image import entry cannot be staged.");
        }

        var row = ReadEntryInternal(connection, transaction, importEntryId);
        UpdateSessionStatus(connection, transaction, row.ImportSessionId, StatusStaging, null);
        var result = ReadEntry(connection, transaction, importEntryId);
        transaction.Commit();
        EnsureResponseWithinLimit(result);
        return result;
    }

    internal ImageImportSession MarkAwaitingSource(string importSessionId, string errorCode)
    {
        importSessionId = NormalizeId(importSessionId, nameof(importSessionId));
        errorCode = NormalizeIdentifier(errorCode, nameof(errorCode), 128);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        UpdateSessionStatus(connection, transaction, importSessionId, StatusAwaitingSource, errorCode);
        var result = ReadSession(connection, transaction, importSessionId);
        transaction.Commit();
        EnsureResponseWithinLimit(result);
        return result;
    }

    internal ImageImportEntry MarkEntryError(string importEntryId, string errorCode)
    {
        importEntryId = NormalizeId(importEntryId, nameof(importEntryId));
        var status = MapEntryErrorStatus(errorCode);
        var normalizedErrorCode = NormalizeIdentifier(errorCode, nameof(errorCode), 128);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = UtcNowText();
        using var command = Command(
            connection,
            transaction,
            $"""
            UPDATE image_import_entries
            SET status = $status,
                failure_code = $failure_code,
                updated_at_utc = $updated_at_utc,
                terminal_at_utc = $terminal_at_utc
            WHERE import_entry_id = $import_entry_id
              AND status NOT IN ('available', 'duplicate', 'cancelled', 'integrity_failed', 'storage_full');
            """);
        Add(command, "$status", status);
        Add(command, "$failure_code", normalizedErrorCode);
        Add(command, "$updated_at_utc", now);
        Add(command, "$terminal_at_utc", IsTerminalEntryStatus(status) ? now : null);
        Add(command, "$import_entry_id", importEntryId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new BusinessCatalogException("image_import_entry_not_mutable", "The image import entry cannot be marked with this error.");
        }

        var row = ReadEntryInternal(connection, transaction, importEntryId);
        if (status is EntrySourceMissing or EntrySourceUnavailable)
        {
            UpdateSessionStatus(connection, transaction, row.ImportSessionId, StatusAwaitingSource, normalizedErrorCode);
        }

        RefreshSessionCounts(connection, transaction, row.ImportSessionId);
        var result = ReadEntry(connection, transaction, importEntryId);
        transaction.Commit();
        EnsureResponseWithinLimit(result);
        return result;
    }

    internal ImageImportEntry ResetEntryForSourceRetry(string importEntryId, string errorCode)
    {
        importEntryId = NormalizeId(importEntryId, nameof(importEntryId));
        errorCode = NormalizeIdentifier(errorCode, nameof(errorCode), 128);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = Command(
            connection,
            transaction,
            """
            UPDATE image_import_entries
            SET status = 'source_unavailable', failure_code = $failure_code,
                stage_receipt_id = NULL, stage_receipt_sha256 = NULL,
                stage_receipt_byte_length = NULL, stage_receipt_created_at_utc = NULL,
                expected_content_hash = NULL, expected_byte_length = NULL, expected_object_key = NULL,
                updated_at_utc = $updated_at_utc, terminal_at_utc = NULL
            WHERE import_entry_id = $import_entry_id
              AND status IN ('staged', 'publishing');
            """);
        Add(command, "$failure_code", errorCode);
        Add(command, "$updated_at_utc", UtcNowText());
        Add(command, "$import_entry_id", importEntryId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new BusinessCatalogException(
                "image_import_entry_not_retryable",
                "The image import entry cannot return to a source retry state.");
        }

        var row = ReadEntryInternal(connection, transaction, importEntryId);
        UpdateSessionStatus(connection, transaction, row.ImportSessionId, StatusAwaitingSource, errorCode);
        RefreshSessionCounts(connection, transaction, row.ImportSessionId);
        var result = ReadEntry(connection, transaction, importEntryId);
        transaction.Commit();
        EnsureResponseWithinLimit(result);
        return result;
    }

    internal ImageImportEntry RecordStageReceipt(ImageImportStageReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var normalized = receipt with
        {
            ImportEntryId = NormalizeId(receipt.ImportEntryId, nameof(receipt.ImportEntryId)),
            StageReceiptId = NormalizeIdentifier(receipt.StageReceiptId, nameof(receipt.StageReceiptId), 128),
            Sha256 = NormalizeSha256(receipt.Sha256, nameof(receipt.Sha256))
        };
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = UtcNowText();
        using var command = Command(
            connection,
            transaction,
            """
            UPDATE image_import_entries
            SET status = 'staged',
                stage_receipt_id = $stage_receipt_id,
                stage_receipt_sha256 = $stage_receipt_sha256,
                stage_receipt_byte_length = $stage_receipt_byte_length,
                stage_receipt_created_at_utc = $stage_receipt_created_at_utc,
                updated_at_utc = $updated_at_utc
            WHERE import_entry_id = $import_entry_id
              AND status NOT IN ('available', 'duplicate', 'cancelled', 'integrity_failed', 'storage_full');
            """);
        Add(command, "$stage_receipt_id", normalized.StageReceiptId);
        Add(command, "$stage_receipt_sha256", normalized.Sha256);
        Add(command, "$stage_receipt_byte_length", normalized.ByteLength);
        Add(command, "$stage_receipt_created_at_utc", (normalized.CreatedAtUtc ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture));
        Add(command, "$updated_at_utc", now);
        Add(command, "$import_entry_id", normalized.ImportEntryId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new BusinessCatalogException("image_import_entry_not_mutable", "The image import entry cannot accept a stage receipt.");
        }

        var row = ReadEntryInternal(connection, transaction, normalized.ImportEntryId);
        UpdateSessionStatus(connection, transaction, row.ImportSessionId, StatusStaging, null);
        var result = ReadEntry(connection, transaction, normalized.ImportEntryId);
        transaction.Commit();
        EnsureResponseWithinLimit(result);
        return result;
    }

    internal IReadOnlyList<ImageImportWorkItem> ListIncompleteWorkItems(string? importSessionId = null)
    {
        var normalizedSessionId = NormalizeOptionalId(importSessionId, nameof(importSessionId));
        using var connection = _database.OpenConnection();
        using var command = Command(
            connection,
            null,
            """
            SELECT import_entry_id, import_session_id, dataset_version_id, source_entry_key,
                   display_name, sort_index, byte_length_snapshot, source_last_write_time_utc,
                   source_identity_key, status, failure_code, stage_receipt_id,
                   stage_receipt_sha256, stage_receipt_byte_length, stage_receipt_created_at_utc,
                   expected_content_hash, expected_byte_length, expected_object_key
            FROM image_import_entries
            WHERE ($import_session_id IS NULL OR import_session_id = $import_session_id)
              AND status NOT IN ('available', 'duplicate', 'cancelled', 'integrity_failed', 'storage_full')
            ORDER BY import_session_id ASC, sort_index ASC, import_entry_id ASC;
            """);
        Add(command, "$import_session_id", normalizedSessionId);
        var result = new List<ImageImportWorkItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ImageImportWorkItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                LongOrNull(reader, 6),
                ParseOptionalTime(StringOrNull(reader, 7)),
                StringOrNull(reader, 8),
                reader.GetString(9),
                StringOrNull(reader, 10),
                ReadOptionalStageReceipt(reader),
                StringOrNull(reader, 15),
                LongOrNull(reader, 16),
                StringOrNull(reader, 17)));
        }

        return result;
    }

    internal IReadOnlyList<ImageImportSourceBinding> ListSourceBindings(string importSessionId)
    {
        importSessionId = NormalizeId(importSessionId, nameof(importSessionId));
        using var connection = _database.OpenConnection();
        _ = ReadSession(connection, null, importSessionId);
        using var command = Command(
            connection,
            null,
            "SELECT source_entry_key, byte_length_snapshot, source_last_write_time_utc, source_identity_key FROM image_import_entries WHERE import_session_id = $import_session_id ORDER BY sort_index ASC, import_entry_id ASC;");
        Add(command, "$import_session_id", importSessionId);
        var result = new List<ImageImportSourceBinding>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ImageImportSourceBinding(
                reader.GetString(0),
                LongOrNull(reader, 1),
                ParseOptionalTime(StringOrNull(reader, 2)),
                StringOrNull(reader, 3)));
        }

        return result;
    }

    internal ImageImportSession? TryGetSession(string importSessionId)
    {
        importSessionId = NormalizeId(importSessionId, nameof(importSessionId));
        using var connection = _database.OpenConnection();
        try
        {
            return ReadSession(connection, null, importSessionId);
        }
        catch (BusinessCatalogException exception) when (exception.Code == "image_import_session_not_found")
        {
            return null;
        }
    }

    internal ImageImportEntry MarkPublishing(string importEntryId, string expectedSha256, long expectedByteLength)
    {
        importEntryId = NormalizeId(importEntryId, nameof(importEntryId));
        expectedSha256 = NormalizeSha256(expectedSha256, nameof(expectedSha256));
        if (expectedByteLength < 0)
        {
            throw new BusinessCatalogException("invalid_parameters", "expectedByteLength must be non-negative.");
        }

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = UtcNowText();
        using var command = Command(
            connection,
            transaction,
            """
            UPDATE image_import_entries
            SET status = 'publishing',
                expected_content_hash = $expected_content_hash,
                expected_byte_length = $expected_byte_length,
                expected_object_key = $expected_object_key,
                updated_at_utc = $updated_at_utc
            WHERE import_entry_id = $import_entry_id
              AND status = 'staged';
            """);
        Add(command, "$expected_content_hash", expectedSha256);
        Add(command, "$expected_byte_length", expectedByteLength);
        Add(command, "$expected_object_key", ObjectKey(expectedSha256));
        Add(command, "$updated_at_utc", now);
        Add(command, "$import_entry_id", importEntryId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new BusinessCatalogException("image_import_entry_not_staged", "The image import entry must be staged before publishing.");
        }

        var row = ReadEntryInternal(connection, transaction, importEntryId);
        UpdateSessionStatus(connection, transaction, row.ImportSessionId, StatusPublishing, null);
        var result = ReadEntry(connection, transaction, importEntryId);
        transaction.Commit();
        EnsureResponseWithinLimit(result);
        return result;
    }

    internal ImageImportEntry CompletePublishedEntry(string importEntryId, string sha256, long byteLength, string? mediaType = null)
    {
        importEntryId = NormalizeId(importEntryId, nameof(importEntryId));
        sha256 = NormalizeSha256(sha256, nameof(sha256));
        if (byteLength < 0)
        {
            throw new BusinessCatalogException("invalid_parameters", "byteLength must be non-negative.");
        }

        mediaType = NormalizeOptionalText(mediaType, nameof(mediaType), 128);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var entry = ReadEntryInternal(connection, transaction, importEntryId);
        if (!string.Equals(entry.Status, EntryPublishing, StringComparison.Ordinal))
        {
            throw new BusinessCatalogException("image_import_entry_not_publishing", "The image import entry must be publishing before it can be completed.");
        }

        var fileObjectId = InsertOrReuseSourceImageObject(connection, transaction, sha256, byteLength, mediaType);
        var canonicalEntryId = FindCanonicalEntry(connection, transaction, entry.ImportSessionId, fileObjectId);
        var now = UtcNowText();
        using var update = Command(
            connection,
            transaction,
            """
            UPDATE image_import_entries
            SET status = $status,
                expected_content_hash = $expected_content_hash,
                expected_byte_length = $expected_byte_length,
                expected_object_key = $expected_object_key,
                file_object_id = $file_object_id,
                canonical_entry_id = $canonical_entry_id,
                updated_at_utc = $updated_at_utc,
                terminal_at_utc = $terminal_at_utc
            WHERE import_entry_id = $import_entry_id;
            """);
        Add(update, "$status", canonicalEntryId is null ? EntryAvailable : EntryDuplicate);
        Add(update, "$expected_content_hash", sha256);
        Add(update, "$expected_byte_length", byteLength);
        Add(update, "$expected_object_key", ObjectKey(sha256));
        Add(update, "$file_object_id", fileObjectId);
        Add(update, "$canonical_entry_id", canonicalEntryId);
        Add(update, "$updated_at_utc", now);
        Add(update, "$terminal_at_utc", now);
        Add(update, "$import_entry_id", importEntryId);
        update.ExecuteNonQuery();
        RefreshSessionCounts(connection, transaction, entry.ImportSessionId);
        var result = ReadEntry(connection, transaction, importEntryId);
        transaction.Commit();
        EnsureResponseWithinLimit(result);
        return result;
    }

    private static IReadOnlyList<ImageImportEntry> ReadEntriesForSession(SqliteConnection connection, SqliteTransaction transaction, string importSessionId)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT import_entry_id FROM image_import_entries WHERE import_session_id = $import_session_id ORDER BY sort_index ASC, import_entry_id ASC;");
        Add(command, "$import_session_id", importSessionId);
        var ids = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }

        return ids.Select(id => ReadEntry(connection, transaction, id)).ToArray();
    }

    private static void UpdateSessionStatus(SqliteConnection connection, SqliteTransaction transaction, string importSessionId, string status, string? errorCode)
    {
        var now = UtcNowText();
        using var command = Command(
            connection,
            transaction,
            """
            UPDATE image_import_sessions
            SET status = $status,
                last_error_code = $last_error_code,
                updated_at_utc = $updated_at_utc
            WHERE import_session_id = $import_session_id
              AND status NOT IN ('completed', 'cancelled', 'failed');
            """);
        Add(command, "$status", status);
        Add(command, "$last_error_code", errorCode);
        Add(command, "$updated_at_utc", now);
        Add(command, "$import_session_id", importSessionId);
        command.ExecuteNonQuery();
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

    private T? TryReplayIdempotent<T>(
        string requestId,
        string method,
        object normalizedParameters)
        where T : class
    {
        requestId = NormalizeIdentifier(requestId, "requestId", 128);
        var parameterHash = Sha256Hex(JsonSerializer.SerializeToUtf8Bytes(normalizedParameters, SerializerOptions));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = ReadMutation(connection, transaction, requestId);
        if (existing is null)
        {
            transaction.Commit();
            return null;
        }

        if (!string.Equals(existing.Method, method, StringComparison.Ordinal) ||
            !string.Equals(existing.ParametersSha256, parameterHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessCatalogException(
                "idempotency_conflict",
                "The requestId was already used with a different method or parameters.");
        }

        var replay = JsonSerializer.Deserialize<T>(existing.ResponseJson, SerializerOptions)
            ?? throw new BusinessCatalogException(
                "idempotency_replay_failed",
                "The saved idempotent response could not be read.");
        transaction.Commit();
        EnsureResponseWithinLimit(replay);
        return replay;
    }

    private string SerializeResponse<T>(T response)
    {
        var json = JsonSerializer.Serialize(response, SerializerOptions);
        if (Encoding.UTF8.GetByteCount(json) > _maximumResponseBytes)
        {
            throw new BusinessCatalogException("response_too_large", "The image import response exceeds the control protocol size limit.");
        }

        return json;
    }

    private void EnsureResponseWithinLimit<T>(T response) => _ = SerializeResponse(response);

    private static DatasetVersionGate ReadDatasetVersionGate(SqliteConnection connection, SqliteTransaction? transaction, string datasetVersionId)
    {
        using var command = Command(connection, transaction, "SELECT lifecycle_state, source_eligibility_state FROM dataset_versions WHERE dataset_version_id = $dataset_version_id;");
        Add(command, "$dataset_version_id", datasetVersionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException("dataset_version_not_found", "The dataset version was not found.");
        }

        if (!string.Equals(reader.GetString(0), "draft", StringComparison.Ordinal))
        {
            throw new BusinessCatalogException("dataset_version_not_draft", "Image import requires a draft dataset version.");
        }

        return new DatasetVersionGate(reader.GetString(0), reader.GetString(1));
    }

    private static ImageImportSession ReadSession(SqliteConnection connection, SqliteTransaction? transaction, string importSessionId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT s.import_session_id, s.dataset_version_id, dv.source_eligibility_state,
                   s.status, s.total_entry_count, s.available_entry_count,
                   s.duplicate_entry_count, s.failed_entry_count, s.cancelled_entry_count,
                   s.last_error_code, s.created_at_utc, s.updated_at_utc,
                   s.completed_at_utc, s.cancelled_at_utc
            FROM image_import_sessions s
            JOIN dataset_versions dv ON dv.dataset_version_id = s.dataset_version_id
            WHERE s.import_session_id = $import_session_id;
            """);
        Add(command, "$import_session_id", importSessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException("image_import_session_not_found", "The image import session was not found.");
        }

        return new ImageImportSession(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            StringOrNull(reader, 9),
            ParseTime(reader.GetString(10)),
            ParseTime(reader.GetString(11)),
            ParseOptionalTime(StringOrNull(reader, 12)),
            ParseOptionalTime(StringOrNull(reader, 13)),
            ResponsePrivacy);
    }

    private static void EnsureSessionExists(SqliteConnection connection, string importSessionId)
    {
        using var command = Command(connection, null, "SELECT 1 FROM image_import_sessions WHERE import_session_id = $import_session_id;");
        Add(command, "$import_session_id", importSessionId);
        if (command.ExecuteScalar() is null)
        {
            throw new BusinessCatalogException("image_import_session_not_found", "The image import session was not found.");
        }
    }

    private static ImageImportEntry ReadEntry(SqliteConnection connection, SqliteTransaction? transaction, string importEntryId)
    {
        var row = ReadEntryInternal(connection, transaction, importEntryId);
        return new ImageImportEntry(
            row.ImportEntryId,
            row.ImportSessionId,
            row.DatasetVersionId,
            row.SortIndex,
            row.DisplayName,
            row.ByteLengthSnapshot,
            row.SourceLastWriteTimeUtc,
            row.Status,
            row.FailureCode,
            row.CanonicalEntryId,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.TerminalAtUtc,
            ResponsePrivacy);
    }

    private static ImageImportEntryRow ReadEntryInternal(SqliteConnection connection, SqliteTransaction? transaction, string importEntryId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT import_entry_id, import_session_id, dataset_version_id, sort_index,
                   display_name, byte_length_snapshot, source_last_write_time_utc,
                   status, failure_code, canonical_entry_id, created_at_utc,
                   updated_at_utc, terminal_at_utc
            FROM image_import_entries
            WHERE import_entry_id = $import_entry_id;
            """);
        Add(command, "$import_entry_id", importEntryId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new BusinessCatalogException("image_import_entry_not_found", "The image import entry was not found.");
        }

        return new ImageImportEntryRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            LongOrNull(reader, 5),
            ParseOptionalTime(StringOrNull(reader, 6)),
            reader.GetString(7),
            StringOrNull(reader, 8),
            StringOrNull(reader, 9),
            ParseTime(reader.GetString(10)),
            ParseTime(reader.GetString(11)),
            ParseOptionalTime(StringOrNull(reader, 12)));
    }

    private static void RefreshSessionCounts(SqliteConnection connection, SqliteTransaction transaction, string importSessionId, bool allowTerminalUpdate = false)
    {
        var counts = ReadCounts(connection, transaction, importSessionId);
        var now = UtcNowText();
        using var update = Command(
            connection,
            transaction,
            $"""
            UPDATE image_import_sessions
            SET total_entry_count = $total_entry_count,
                available_entry_count = $available_entry_count,
                duplicate_entry_count = $duplicate_entry_count,
                failed_entry_count = $failed_entry_count,
                cancelled_entry_count = $cancelled_entry_count,
                updated_at_utc = $updated_at_utc
            WHERE import_session_id = $import_session_id{(allowTerminalUpdate ? string.Empty : " AND status NOT IN ('completed', 'cancelled', 'failed')")};
            """);
        Add(update, "$total_entry_count", counts.Total);
        Add(update, "$available_entry_count", counts.Available);
        Add(update, "$duplicate_entry_count", counts.Duplicate);
        Add(update, "$failed_entry_count", counts.Failed);
        Add(update, "$cancelled_entry_count", counts.Cancelled);
        Add(update, "$updated_at_utc", now);
        Add(update, "$import_session_id", importSessionId);
        update.ExecuteNonQuery();

        if (!allowTerminalUpdate && counts.Total > 0 && counts.Total == counts.Available + counts.Duplicate + counts.Failed + counts.Cancelled)
        {
            using var complete = Command(
                connection,
                transaction,
                """
                UPDATE image_import_sessions
                SET status = 'completed', updated_at_utc = $updated_at_utc, completed_at_utc = $completed_at_utc
                WHERE import_session_id = $import_session_id
                  AND status NOT IN ('completed', 'cancelled', 'failed');
                """);
            Add(complete, "$updated_at_utc", now);
            Add(complete, "$completed_at_utc", now);
            Add(complete, "$import_session_id", importSessionId);
            complete.ExecuteNonQuery();
        }
    }

    private static ImportCounts ReadCounts(SqliteConnection connection, SqliteTransaction transaction, string importSessionId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT
                count(*),
                COALESCE(SUM(CASE WHEN status = 'available' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'duplicate' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status IN ('integrity_failed', 'storage_full') THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN status = 'cancelled' THEN 1 ELSE 0 END), 0)
            FROM image_import_entries
            WHERE import_session_id = $import_session_id;
            """);
        Add(command, "$import_session_id", importSessionId);
        using var reader = command.ExecuteReader();
        reader.Read();
        return new ImportCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
    }

    private static string InsertOrReuseSourceImageObject(SqliteConnection connection, SqliteTransaction transaction, string sha256, long byteLength, string? mediaType)
    {
        using (var select = Command(connection, transaction, "SELECT file_object_id, storage_state, object_key FROM file_objects WHERE hash_algorithm = 'sha256' AND content_hash = $content_hash AND byte_length = $byte_length;"))
        {
            Add(select, "$content_hash", sha256);
            Add(select, "$byte_length", byteLength);
            using var reader = select.ExecuteReader();
            if (reader.Read())
            {
                if (!string.Equals(reader.GetString(1), "available", StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(2), ObjectKey(sha256), StringComparison.Ordinal))
                {
                    throw new BusinessCatalogException("file_object_identity_conflict", "An existing content object with the same hash and length is not available at the expected content key.");
                }

                var existingId = reader.GetString(0);
                reader.Close();
                InsertFileObjectRole(connection, transaction, existingId, "source_image");
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
                file_object_id, object_kind, hash_algorithm, content_hash, byte_length,
                media_type, object_key, storage_state, created_at_utc, available_at_utc)
            VALUES(
                $file_object_id, 'source_image', 'sha256', $content_hash, $byte_length,
                $media_type, $object_key, 'available', $created_at_utc, $available_at_utc);
            """);
        Add(insert, "$file_object_id", fileObjectId);
        Add(insert, "$content_hash", sha256);
        Add(insert, "$byte_length", byteLength);
        Add(insert, "$media_type", mediaType);
        Add(insert, "$object_key", ObjectKey(sha256));
        Add(insert, "$created_at_utc", now);
        Add(insert, "$available_at_utc", now);
        insert.ExecuteNonQuery();
        InsertFileObjectRole(connection, transaction, fileObjectId, "source_image");
        return fileObjectId;
    }

    private static void InsertFileObjectRole(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fileObjectId,
        string objectRole)
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
        Add(insert, "$object_role", objectRole);
        Add(insert, "$created_at_utc", UtcNowText());
        insert.ExecuteNonQuery();
    }

    private static string? FindCanonicalEntry(SqliteConnection connection, SqliteTransaction transaction, string importSessionId, string fileObjectId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT import_entry_id
            FROM image_import_entries
            WHERE import_session_id = $import_session_id
              AND file_object_id = $file_object_id
              AND status = 'available'
            ORDER BY sort_index ASC, import_entry_id ASC
            LIMIT 1;
            """);
        Add(command, "$import_session_id", importSessionId);
        Add(command, "$file_object_id", fileObjectId);
        return command.ExecuteScalar() as string;
    }

    private static bool IsTerminalSession(string status) => status is StatusCompleted or StatusCancelled or StatusFailed;

    private static bool IsTerminalEntryStatus(string status) =>
        status is EntryAvailable or EntryDuplicate or EntryCancelled or EntryIntegrityFailed or EntryStorageFull;

    private static string MapEntryErrorStatus(string errorCode) => errorCode switch
    {
        "source_locked" => EntrySourceLocked,
        "source_missing" => EntrySourceMissing,
        "source_root_missing" => EntrySourceUnavailable,
        "source_device_unavailable" => EntrySourceUnavailable,
        "source_unavailable" => EntrySourceUnavailable,
        "source_changed" => EntrySourceChanged,
        "source_copy_destination_disk_full" => EntryStorageFull,
        "object_store_disk_full" => EntryStorageFull,
        "object_source_read_failed" => EntrySourceUnavailable,
        "object_stage_failed" => EntryIntegrityFailed,
        "object_formal_conflict" => EntryIntegrityFailed,
        "object_formal_integrity_failed" => EntryIntegrityFailed,
        "object_stage_receipt_mismatch" => EntryIntegrityFailed,
        "object_stage_integrity_failed" => EntryIntegrityFailed,
        "object_stage_manifest_invalid" => EntryIntegrityFailed,
        "object_stage_incomplete" => EntryIntegrityFailed,
        _ => EntryIntegrityFailed
    };

    private static ObjectStageReceipt? ReadOptionalStageReceipt(SqliteDataReader reader)
    {
        var stageReceiptId = StringOrNull(reader, 11);
        if (stageReceiptId is null)
        {
            return null;
        }

        return new ObjectStageReceipt(
            stageReceiptId,
            reader.GetString(12),
            reader.GetInt64(13),
            ParseTime(reader.GetString(14)));
    }

    private static PageResult<T> ToPage<T>(List<(T Item, string Position, string Id)> rows, int pageSize, string method, string scope)
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
        var json = JsonSerializer.Serialize(new ImageImportCursor(1, method, scope, position, id), SerializerOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static ImageImportCursor? DecodeCursor(string? cursor, string method, string scope)
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
            var decoded = JsonSerializer.Deserialize<ImageImportCursor>(json, SerializerOptions) ?? throw new JsonException();
            if (decoded.Version != 1 || !string.Equals(decoded.Method, method, StringComparison.Ordinal) ||
                !string.Equals(decoded.Scope, scope, StringComparison.Ordinal))
            {
                throw new BusinessCatalogException("invalid_cursor", "The page cursor does not belong to this image import list and filter.");
            }

            return decoded with
            {
                Position = NormalizeRequiredText(decoded.Position, "cursor.position", 64),
                Id = NormalizeId(decoded.Id, "cursor.id")
            };
        }
        catch (Exception ex) when (ex is FormatException or JsonException or BusinessCatalogException)
        {
            throw new BusinessCatalogException("invalid_cursor", "The page cursor is invalid.", ex);
        }
    }

    private static CatalogMutation? ReadMutation(SqliteConnection connection, SqliteTransaction transaction, string requestId)
    {
        using var command = Command(connection, transaction, "SELECT method, parameters_sha256, response_json FROM catalog_mutations WHERE request_id = $request_id;");
        Add(command, "$request_id", requestId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new CatalogMutation(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
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

    private static string NormalizeId(string value, string fieldName) => NormalizeIdentifier(value, fieldName, 128);

    private static string? NormalizeOptionalId(string? value, string fieldName) => value is null ? null : NormalizeIdentifier(value, fieldName, 128);

    private static string NormalizeIdentifier(string value, string fieldName, int maximumLength)
    {
        var normalized = NormalizeRequiredText(value, fieldName, maximumLength);
        if (normalized.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
        {
            throw new BusinessCatalogException("invalid_parameters", $"{fieldName} contains unsupported identifier characters.");
        }

        return normalized;
    }

    private static string NormalizeDisplayName(string value)
    {
        var normalized = NormalizeRequiredText(value, "displayName", 255);
        if (normalized.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            throw new BusinessCatalogException("invalid_parameters", "displayName must be a leaf name, not a path.");
        }

        return normalized;
    }

    private static string NormalizeSha256(string value, string fieldName)
    {
        var normalized = NormalizeRequiredText(value, fieldName, 64).ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new BusinessCatalogException("invalid_parameters", $"{fieldName} must be a SHA-256 hex string.");
        }

        return normalized;
    }

    private static string? NormalizeOptionalSha256(string? value, string fieldName) => value is null ? null : NormalizeSha256(value, fieldName);

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

    private static string? StringOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? LongOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ParseOptionalTime(string? value) => value is null ? null : ParseTime(value);

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static string UtcNowText() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ObjectKey(string sha256) => $"sha256/{sha256[..2]}/{sha256}";
}

internal sealed record ImageImportDiscoveredEntry(
    string ImportSessionId,
    string SourceEntryKey,
    string DisplayName,
    int SortIndex,
    long? ByteLengthSnapshot,
    DateTimeOffset? SourceLastWriteTimeUtc,
    string? SourceIdentityKey);

internal sealed record ImageImportStageReceipt(
    string ImportEntryId,
    string StageReceiptId,
    string Sha256,
    long ByteLength,
    DateTimeOffset? CreatedAtUtc = null);

internal sealed record ImageImportWorkItem(
    string ImportEntryId,
    string ImportSessionId,
    string DatasetVersionId,
    string SourceEntryKey,
    string DisplayName,
    int SortIndex,
    long? ByteLengthSnapshot,
    DateTimeOffset? SourceLastWriteTimeUtc,
    string? SourceIdentityKey,
    string Status,
    string? FailureCode,
    ObjectStageReceipt? StageReceipt,
    string? ExpectedContentHash,
    long? ExpectedByteLength,
    string? ExpectedObjectKey);

internal sealed record ImageImportSourceBinding(
    string SourceEntryKey,
    long? ByteLengthSnapshot,
    DateTimeOffset? SourceLastWriteTimeUtc,
    string? SourceIdentityKey);

internal sealed record ImageImportPreparedStartParameters(
    string DatasetVersionId,
    string SourceRootKey,
    string SourceLocatorManifestId);

internal sealed record IdempotentPreparedImageImportStartParameters(
    string ImportSessionId,
    ImageImportPreparedStartParameters Parameters);

internal sealed record IdempotentPreparedImageImportResumeParameters(
    string ImportSessionId,
    string? SourceRootKey);

internal sealed record DatasetVersionGate(string LifecycleState, string SourceEligibilityState);

internal sealed record ImageImportCursor(int Version, string Method, string Scope, string Position, string Id);

internal sealed record ImageImportEntryRow(
    string ImportEntryId,
    string ImportSessionId,
    string DatasetVersionId,
    int SortIndex,
    string DisplayName,
    long? ByteLengthSnapshot,
    DateTimeOffset? SourceLastWriteTimeUtc,
    string Status,
    string? FailureCode,
    string? CanonicalEntryId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? TerminalAtUtc);

internal sealed record ImportCounts(int Total, int Available, int Duplicate, int Failed, int Cancelled);
