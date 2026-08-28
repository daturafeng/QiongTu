using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ImageImportPreflightCatalogTests
{
    [TestMethod]
    public void StartIsIdempotentAndListsOnlyBoundedSanitizedItems()
    {
        using var scope = new CatalogScope();
        scope.SeedWaitingImport("dataset-version-a", "session-a", 3);

        var first = scope.Preflights.Start(
            "preflight-start-a",
            new ImageImportPreflightStartParameters("session-a"));
        var replay = scope.Preflights.Start(
            "preflight-start-a",
            new ImageImportPreflightStartParameters("session-a"));

        Assert.AreEqual(first.PreflightRunId, replay.PreflightRunId);
        Assert.AreEqual("queued", first.Status);
        Assert.AreEqual(3, first.ImageCandidateCount);
        AssertSanitized(first);

        var page1 = scope.Preflights.ListItems(new ImageImportPreflightItemListParameters(
            first.PreflightRunId,
            2,
            null));
        Assert.HasCount(2, page1.Items);
        Assert.IsNotNull(page1.NextCursor);
        var page2 = scope.Preflights.ListItems(new ImageImportPreflightItemListParameters(
            first.PreflightRunId,
            2,
            page1.NextCursor));
        Assert.HasCount(1, page2.Items);
        Assert.IsNull(page2.NextCursor);
        foreach (var item in page1.Items.Concat(page2.Items))
        {
            AssertSanitized(item);
        }

        var conflict = Assert.Throws<BusinessCatalogException>(() => scope.Preflights.Start(
            "preflight-start-a",
            new ImageImportPreflightStartParameters("different-session")));
        Assert.AreEqual("idempotency_conflict", conflict.Code);

        var invalidCursor = Assert.Throws<BusinessCatalogException>(() =>
            scope.Preflights.ListItems(new ImageImportPreflightItemListParameters(
                first.PreflightRunId,
                2,
                page1.NextCursor + "!")));
        Assert.AreEqual("invalid_cursor", invalidCursor.Code);
    }

    [TestMethod]
    public void FixedDjiDecisionAtomicallyReleasesTheWaitingImportLedger()
    {
        using var scope = new CatalogScope();
        scope.SeedWaitingImport("dataset-version-dji", "session-dji", 2);
        var run = scope.Preflights.Start(
            "preflight-start-dji",
            new ImageImportPreflightStartParameters("session-dji"));
        scope.Preflights.MarkRunning(run.PreflightRunId);

        foreach (var item in scope.Preflights.ListWorkItems(run.PreflightRunId))
        {
            scope.Preflights.MarkItemRunning(item.ItemId);
            scope.Preflights.CompleteItem(item.ItemId, ProbeResult(
                item.CandidateKind,
                "supports_dji",
                ["dji_exif_manufacturer"],
                []));
        }

        var completed = scope.Preflights.CommitDecision(run.PreflightRunId);
        Assert.AreEqual("completed", completed.Status);
        Assert.AreEqual("dji_supported", completed.Decision);
        Assert.AreEqual("dji_evidence_confirmed", completed.DecisionReasonCode);
        Assert.AreEqual("dji_supported", completed.SourceEligibilityState);

        var import = scope.Imports.Get(new ImageImportGetParameters("session-dji"));
        Assert.AreEqual("ready", import.Status);
        Assert.AreEqual("dji_supported", import.SourceEligibilityState);
        var entries = scope.Imports.ListEntries(new ImageImportEntryListParameters("session-dji", 50, null));
        Assert.IsTrue(entries.Items.All(item => item.Status == "discovered"));
        Assert.AreEqual(run.PreflightRunId, scope.Scalar<string>(
            "SELECT source_eligibility_run_id FROM dataset_versions WHERE dataset_version_id='dataset-version-dji';"));
    }

    [TestMethod]
    [DataRow("out_of_scope", "out_of_scope", "other_manufacturer_detected")]
    [DataRow("conflict", "out_of_scope", "source_evidence_conflict")]
    [DataRow("unconfirmed", "unconfirmed", "dji_evidence_incomplete")]
    public void BlockingEvidenceNeverReleasesCopyOrCreatesFormalRecords(
        string evidenceState,
        string expectedDecision,
        string expectedReason)
    {
        using var scope = new CatalogScope();
        var suffix = evidenceState.Replace('_', '-');
        var datasetVersionId = "dataset-version-" + suffix;
        var sessionId = "session-" + suffix;
        scope.SeedWaitingImport(datasetVersionId, sessionId, 1);
        var run = scope.Preflights.Start(
            "preflight-start-" + suffix,
            new ImageImportPreflightStartParameters(sessionId));
        scope.Preflights.MarkRunning(run.PreflightRunId);
        var item = scope.Preflights.ListWorkItems(run.PreflightRunId).Single();
        scope.Preflights.MarkItemRunning(item.ItemId);
        scope.Preflights.CompleteItem(item.ItemId, ProbeResult(
            item.CandidateKind,
            evidenceState,
            evidenceState == "out_of_scope" ? ["other_exif_manufacturer"] : [],
            [evidenceState == "conflict" ? "manufacturer_xmp_conflict" : "dji_evidence_missing"]));

        var completed = scope.Preflights.CommitDecision(run.PreflightRunId);
        Assert.AreEqual(expectedDecision, completed.Decision);
        Assert.AreEqual(expectedReason, completed.DecisionReasonCode);
        Assert.AreEqual("awaiting_source_preflight", scope.Imports.Get(
            new ImageImportGetParameters(sessionId)).Status);
        Assert.AreEqual("awaiting_source_preflight", scope.Imports.ListEntries(
            new ImageImportEntryListParameters(sessionId, 50, null)).Items.Single().Status);
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_objects;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM images;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM processing_jobs;"));
    }

    [TestMethod]
    public void RestartInterruptionResetsOnlyRunningItemsAndPreservesCompletedEvidence()
    {
        using var scope = new CatalogScope();
        scope.SeedWaitingImport("dataset-version-recovery", "session-recovery", 2);
        var run = scope.Preflights.Start(
            "preflight-start-recovery",
            new ImageImportPreflightStartParameters("session-recovery"));
        scope.Preflights.MarkRunning(run.PreflightRunId);
        var items = scope.Preflights.ListWorkItems(run.PreflightRunId);
        scope.Preflights.MarkItemRunning(items[0].ItemId);
        scope.Preflights.CompleteItem(items[0].ItemId, ProbeResult(
            "image_candidate",
            "supports_dji",
            ["dji_exif_manufacturer"],
            []));
        scope.Preflights.MarkItemRunning(items[1].ItemId);

        var interrupted = scope.Preflights.InterruptRunningRuns();
        CollectionAssert.Contains(interrupted.ToList(), run.PreflightRunId);
        var after = scope.Preflights.Get(new ImageImportPreflightGetParameters(run.PreflightRunId));
        Assert.AreEqual("interrupted", after.Status);
        Assert.AreEqual("pending", after.SourceEligibilityState);
        var recoveredItems = scope.Preflights.ListWorkItems(run.PreflightRunId, includeCompleted: true);
        Assert.AreEqual("completed", recoveredItems.Single(item => item.ItemId == items[0].ItemId).Status);
        Assert.AreEqual("queued", recoveredItems.Single(item => item.ItemId == items[1].ItemId).Status);
        CollectionAssert.Contains(scope.Preflights.ListRecoverableRunIds().ToList(), run.PreflightRunId);
    }

    [TestMethod]
    public void ProvenanceTriggerAndResponseLimitRejectBypassWithoutPartialWrites()
    {
        using var scope = new CatalogScope();
        scope.SeedWaitingImport("dataset-version-guard", "session-guard", 1);
        Assert.Throws<SqliteException>(() => scope.Execute(
            "UPDATE dataset_versions SET source_eligibility_state='dji_supported' WHERE dataset_version_id='dataset-version-guard';"));

        var limited = new ImageImportPreflightCatalog(scope.Database, maximumResponseBytes: 64);
        var oversized = Assert.Throws<BusinessCatalogException>(() => limited.Start(
            "preflight-start-too-large",
            new ImageImportPreflightStartParameters("session-guard")));
        Assert.AreEqual("response_too_large", oversized.Code);
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM source_preflight_runs;"));
        Assert.AreEqual(0L, scope.Scalar<long>(
            "SELECT count(*) FROM catalog_mutations WHERE request_id='preflight-start-too-large';"));
    }

    [TestMethod]
    public void DatabaseRejectsCrossBindingIdentityMutationAndSkippedItemTransitions()
    {
        using var scope = new CatalogScope();
        scope.SeedWaitingImport("dataset-version-constraints", "session-constraints", 1);
        var run = scope.Preflights.Start(
            "preflight-start-constraints",
            new ImageImportPreflightStartParameters("session-constraints"));
        var item = scope.Preflights.ListWorkItems(run.PreflightRunId).Single();

        Assert.Throws<SqliteException>(() => scope.Execute(
            $"UPDATE source_preflight_items SET status='completed' WHERE source_preflight_item_id='{item.ItemId}';"));
        Assert.Throws<SqliteException>(() => scope.Execute(
            $"UPDATE source_preflight_runs SET source_root_key_snapshot='{Sha('9')}' WHERE source_preflight_run_id='{run.PreflightRunId}';"));
        Assert.Throws<SqliteException>(() => scope.Execute(
            $"UPDATE source_preflight_items SET dataset_version_id='different-dataset' WHERE source_preflight_item_id='{item.ItemId}';"));
        Assert.AreEqual("queued", scope.Preflights.Get(
            new ImageImportPreflightGetParameters(run.PreflightRunId)).Status);
        Assert.AreEqual("queued", scope.Preflights.ListItems(
            new ImageImportPreflightItemListParameters(run.PreflightRunId, 20, null)).Items.Single().Status);
    }

    private static ImageProbeSourcePreflightResult ProbeResult(
        string candidateKind,
        string evidenceState,
        IReadOnlyList<string> evidenceKinds,
        IReadOnlyList<string> reasonCodes) => new(
        ImageProbeProtocol.SourcePreflightV1,
        ImageProbeProtocol.SourcePreflightProfile,
        "completed",
        candidateKind,
        candidateKind == "image_candidate" ? "jpeg_hint" : "not_image",
        evidenceState,
        evidenceKinds,
        reasonCodes,
        new ImageProbeParserIdentity("qiongtu.source-preflight", "1.0.0", "2.9.3"),
        new ImageProbePrivacy(false, false, false, false, false, false, false, false));

    private static void AssertSanitized(object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var privateName in new[]
                 {
                     "absolutePath", "relativePath", "sourceEntryKey", "sourceRootKey",
                     "contentHash", "objectKey", "stageReceipt", "rawMetadata",
                     "serialNumber", "latitude", "longitude", "ownerSampleStatistics"
                 })
        {
            Assert.DoesNotContain(
                $"\"{privateName}\":",
                json,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class CatalogScope : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"qiongtu-preflight-catalog-tests-{Guid.NewGuid():N}");

        public CatalogScope()
        {
            Directory.CreateDirectory(_root);
            Database = new BusinessDatabase(Path.Combine(_root, "business.db"));
            Database.Initialize();
            Imports = new ImageImportCatalog(Database);
            Preflights = new ImageImportPreflightCatalog(Database);
        }

        public BusinessDatabase Database { get; }

        public ImageImportCatalog Imports { get; }

        public ImageImportPreflightCatalog Preflights { get; }

        public void SeedWaitingImport(string datasetVersionId, string sessionId, int itemCount)
        {
            using (var connection = Database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT OR IGNORE INTO projects(project_id,name,spatial_configuration_state,lifecycle_state,created_at_utc,updated_at_utc)
                    VALUES('project-preflight','Project','pending','active','2026-08-28T00:00:00Z','2026-08-28T00:00:00Z');
                    INSERT OR IGNORE INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
                    VALUES('dataset-preflight','project-preflight','Dataset','active','2026-08-28T00:00:00Z','2026-08-28T00:00:00Z');
                    INSERT INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc)
                    VALUES($dataset_version_id,'dataset-preflight',
                        (SELECT COALESCE(MAX(version_number),0)+1 FROM dataset_versions WHERE dataset_id='dataset-preflight'),
                        'draft','pending','not_run','2026-08-28T00:00:00Z');
                    """;
                command.Parameters.AddWithValue("$dataset_version_id", datasetVersionId);
                command.ExecuteNonQuery();
            }

            var entries = Enumerable.Range(0, itemCount)
                .Select(index => new ImageImportDiscoveredEntry(
                    sessionId,
                    Sha((char)('a' + index)),
                    $"DJI_{index + 1:0000}.JPG",
                    index,
                    1024 + index,
                    DateTimeOffset.Parse("2026-08-28T00:00:00Z").AddSeconds(index),
                    Sha((char)('f' - index))))
                .ToArray();
            Imports.StartPrepared(
                "image-import-start-" + sessionId,
                sessionId,
                datasetVersionId,
                Sha('0'),
                sessionId,
                entries);
        }

        public void Execute(string sql)
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public T Scalar<T>(string sql)
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(
                command.ExecuteScalar()!,
                typeof(T),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Sha(char value) => new(value, 64);
}
