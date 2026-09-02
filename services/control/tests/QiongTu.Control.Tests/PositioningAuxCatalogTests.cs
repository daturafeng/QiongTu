using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class PositioningAuxCatalogTests
{
    [TestMethod]
    public void SourceGateMustBeCompletedDjiSupportedBeforeCreatingAuxRun()
    {
        using var scope = new CatalogScope();
        scope.SeedPreflight("dataset-version-gate", "import-session-gate", "source-preflight-gate", "unconfirmed", includeSidecars: true);

        var blocked = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.EnsureRunForCompletedPreflight(
                "source-preflight-gate",
                scope.AssociationBindings("source-preflight-gate")));

        Assert.AreEqual("positioning_aux_source_gate_not_satisfied", blocked.Code);
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM positioning_aux_import_runs;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM positioning_aux_files;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_objects;"));
    }

    [TestMethod]
    public void CompletedDjiPreflightCreatesIndependentRunAndOnlyPendingSidecarWorkItems()
    {
        using var scope = new CatalogScope();
        scope.SeedPreflight("dataset-version-start", "import-session-start", "source-preflight-start", "dji_supported", includeSidecars: true);

        var first = scope.Catalog.EnsureRunForCompletedPreflight(
            "source-preflight-start",
            scope.AssociationBindings("source-preflight-start"));
        var replay = scope.Catalog.EnsureRunForCompletedPreflight(
            "source-preflight-start",
            scope.AssociationBindings("source-preflight-start"));

        Assert.AreEqual(first.RunId, replay.RunId);
        Assert.AreEqual("pending", first.Status);
        Assert.AreEqual(4, first.TotalFileCount);
        Assert.AreEqual(PositioningAuxCatalog.AssociationProfile, first.AssociationProfile);
        Assert.AreEqual(PositioningAuxCatalog.AssociationPolicyVersion, first.AssociationPolicyVersion);
        Assert.AreEqual(PositioningAuxCatalog.ParserProfile, first.ParserProfile);
        Assert.AreEqual(PositioningAuxCatalog.ParserName, first.ParserName);
        AssertSanitized(first);
        Assert.HasCount(4, scope.Catalog.ListIncompleteWorkItems(first.RunId));
        Assert.IsEmpty(scope.Catalog.ListFiles(new PositioningAuxFileListParameters("dataset-version-start", null, 20, null)).Items);
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM positioning_aux_files;"));
    }

    [TestMethod]
    public void AssociationBindingsMustExactlyMatchCompletedSidecarItems()
    {
        using var scope = new CatalogScope();
        scope.SeedPreflight("dataset-version-association", "import-session-association", "source-preflight-association", "dji_supported", includeSidecars: true);

        var missing = scope.AssociationBindings("source-preflight-association").Where(binding => !binding.SourcePreflightItemId.EndsWith("-mrk", StringComparison.Ordinal)).ToArray();
        var missingError = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.EnsureRunForCompletedPreflight("source-preflight-association", missing));
        Assert.AreEqual("positioning_aux_association_missing", missingError.Code);

        var wrongSourceKey = scope.AssociationBindings("source-preflight-association")
            .Select(binding => binding.SourcePreflightItemId.EndsWith("-mrk", StringComparison.Ordinal)
                ? binding with { SourceEntryKey = Sha('9') }
                : binding)
            .ToArray();
        var wrongKeyError = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.EnsureRunForCompletedPreflight("source-preflight-association", wrongSourceKey));
        Assert.AreEqual("positioning_aux_association_missing", wrongKeyError.Code);

        var zero = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.EnsureRunForCompletedPreflight(
                "source-preflight-association",
                [new PositioningAuxAssociationBinding("source-preflight-association-mrk", Sha('a'), 0)]));
        Assert.AreEqual("positioning_aux_association_count_invalid", zero.Code);
    }

    [TestMethod]
    public void StatusTransitionsRetainCasObjectAndParseMrkWithoutPromotingPreflightEvidence()
    {
        using var scope = new CatalogScope();
        var run = scope.StartDjiRun("dataset-version-mrk", "import-session-mrk", "source-preflight-mrk");
        var item = scope.Catalog.ListIncompleteWorkItems(run.RunId).Single(work => work.AuxiliaryType == "mrk");

        scope.Catalog.MarkRunning(run.RunId);
        var staged = scope.Catalog.MarkStaging(item.ItemId);
        Assert.AreEqual("staging", staged.Status);
        scope.Catalog.RecordStageReceipt(new PositioningAuxStageReceipt(item.ItemId, "stage-mrk-1", Sha('a'), 128, DateTimeOffset.Parse("2026-08-28T00:00:00Z")));
        scope.Catalog.MarkPublishing(item.ItemId, Sha('a'), 128);
        var retained = scope.Catalog.CompletePublishedRetention(item.ItemId, Sha('a'), 128, "text/plain");
        Assert.AreEqual("retained", retained.Status);

        var listedRetained = scope.Catalog.ListFiles(new PositioningAuxFileListParameters("dataset-version-mrk", run.RunId, 20, null)).Items.Single();
        Assert.AreEqual("retained", listedRetained.RetentionState);
        Assert.AreEqual("not_attempted", listedRetained.ParseState);
        Assert.AreEqual("not_checked", listedRetained.QualityState);
        Assert.AreEqual("not_recorded", listedRetained.UsageState);
        AssertSanitized(listedRetained);

        scope.Catalog.BeginParsing(item.ItemId);
        var completed = scope.Catalog.CompleteParsedMrk(item.ItemId, MrkResult("passed", Sha('b')));
        Assert.AreEqual("completed", completed.Status);

        var parsed = scope.Catalog.ListFiles(new PositioningAuxFileListParameters("dataset-version-mrk", run.RunId, 20, null)).Items.Single();
        Assert.AreEqual("parsed", parsed.ParseState);
        Assert.AreEqual("passed", parsed.QualityState);
        Assert.AreEqual("dji-mrk", parsed.ParserName);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_object_roles WHERE object_role='positioning_aux';"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM images;"));
    }

    [TestMethod]
    public void NavObsAndRtkAreRetainedAsUnsupportedAndNotChecked()
    {
        using var scope = new CatalogScope();
        var run = scope.StartDjiRun("dataset-version-sidecars", "import-session-sidecars", "source-preflight-sidecars");
        scope.Catalog.MarkRunning(run.RunId);

        foreach (var item in scope.Catalog.ListIncompleteWorkItems(run.RunId).Where(item => item.AuxiliaryType != "mrk"))
        {
            var hash = item.AuxiliaryType switch
            {
                "nav" => Sha('c'),
                "obs" => Sha('d'),
                _ => Sha('e')
            };
            scope.RetainWithoutParsing(item, hash);
        }

        var files = scope.Catalog.ListFiles(new PositioningAuxFileListParameters("dataset-version-sidecars", run.RunId, 20, null)).Items;
        Assert.HasCount(3, files);
        Assert.IsTrue(files.All(file => file.ParseState == "unsupported"));
        Assert.IsTrue(files.All(file => file.QualityState == "not_checked"));
        Assert.IsTrue(files.All(file => file.UsageState == "not_recorded"));
        Assert.AreEqual("rinex-candidate", files.Single(file => file.AuxType == "nav").ParserName);
        Assert.AreEqual("rtcm3-candidate", files.Single(file => file.AuxType == "rtk").ParserName);
    }

    [TestMethod]
    public void ContentAddressedObjectsDeduplicateWhileDatasetAssociationsRemainIndependent()
    {
        using var scope = new CatalogScope();
        var firstRun = scope.StartDjiRun("dataset-version-dedupe-a", "import-session-dedupe-a", "source-preflight-dedupe-a");
        var secondRun = scope.StartDjiRun("dataset-version-dedupe-b", "import-session-dedupe-b", "source-preflight-dedupe-b");
        var first = scope.Catalog.ListIncompleteWorkItems(firstRun.RunId).Single(item => item.AuxiliaryType == "nav");
        var second = scope.Catalog.ListIncompleteWorkItems(secondRun.RunId).Single(item => item.AuxiliaryType == "nav");

        scope.Catalog.MarkRunning(firstRun.RunId);
        scope.Catalog.MarkRunning(secondRun.RunId);
        var firstRetained = scope.RetainWithoutParsing(first, Sha('f'));
        var secondRetained = scope.RetainWithoutParsing(second, Sha('f'));

        Assert.AreNotEqual(firstRetained.PositioningAuxFileId, secondRetained.PositioningAuxFileId);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE content_hash='" + Sha('f') + "';"));
        Assert.AreEqual(2L, scope.Scalar<long>("SELECT count(*) FROM positioning_aux_files;"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_object_roles WHERE object_role='positioning_aux';"));
    }

    [TestMethod]
    public void PublicResponsesDoNotExposePrivateObjectSourceOrRecordData()
    {
        using var scope = new CatalogScope();
        var run = scope.StartDjiRun("dataset-version-privacy", "import-session-privacy", "source-preflight-privacy");
        scope.Catalog.MarkRunning(run.RunId);
        var item = scope.Catalog.ListIncompleteWorkItems(run.RunId).Single(item => item.AuxiliaryType == "rtk");
        scope.RetainWithoutParsing(item, Sha('1'));

        var response = new
        {
            Run = scope.Catalog.Get(new PositioningAuxImportGetParameters(run.RunId)),
            Files = scope.Catalog.ListFiles(new PositioningAuxFileListParameters("dataset-version-privacy", run.RunId, 20, null))
        };

        AssertSanitized(response);
    }

    [TestMethod]
    public void UsageRequiresExecutionLevelUsedEvidenceAndAggregatesPerFile()
    {
        using var scope = new CatalogScope();
        var run = scope.StartDjiRun("dataset-version-usage", "import-session-usage", "source-preflight-usage");
        scope.Catalog.MarkRunning(run.RunId);
        var mrkItem = scope.Catalog.ListIncompleteWorkItems(run.RunId).Single(item => item.AuxiliaryType == "mrk");
        var navItem = scope.Catalog.ListIncompleteWorkItems(run.RunId).Single(item => item.AuxiliaryType == "nav");

        var mrkFileId = scope.RetainParsedMrk(mrkItem, Sha('2'), Sha('3'));
        var navFileId = scope.RetainWithoutParsing(navItem, Sha('4')).PositioningAuxFileId!;
        scope.SeedJobExecution("dataset-version-usage", "job-usage", "execution-usage");

        var invalid = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.RecordUsage(navFileId, "execution-usage", "used"));
        Assert.AreEqual("positioning_aux_usage_invalid", invalid.Code);

        scope.Catalog.RecordUsage(navFileId, "execution-usage", "rejected");
        Assert.AreEqual("rejected", scope.Catalog.ListFiles(new PositioningAuxFileListParameters("dataset-version-usage", run.RunId, 20, null))
            .Items.Single(file => file.PositioningAuxFileId == navFileId).UsageState);

        scope.Catalog.RecordUsage(mrkFileId, "execution-usage", "used");
        var mrk = scope.Catalog.ListFiles(new PositioningAuxFileListParameters("dataset-version-usage", run.RunId, 20, null))
            .Items.Single(file => file.PositioningAuxFileId == mrkFileId);
        Assert.AreEqual("used", mrk.UsageState);

        var privateEvidence = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.RecordUsage(
                mrkFileId,
                "execution-usage",
                "used",
                """{"sourcePath":"D:\\private\\flight.mrk"}"""));
        Assert.AreEqual("positioning_aux_usage_evidence_invalid", privateEvidence.Code);
    }

    [TestMethod]
    public void ParsedMrkRequiresSuccessfulFixedStructureStates()
    {
        using var scope = new CatalogScope();
        var run = scope.StartDjiRun("dataset-version-invalid-parse", "import-session-invalid-parse", "source-preflight-invalid-parse");
        var item = scope.Catalog.ListIncompleteWorkItems(run.RunId).Single(work => work.AuxiliaryType == "mrk");
        scope.Catalog.MarkRunning(run.RunId);
        scope.RetainWithoutParsing(item, Sha('7'));
        scope.Catalog.BeginParsing(item.ItemId);

        var invalid = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.CompleteParsedMrk(
                item.ItemId,
                MrkResult("failed", Sha('8')) with { ReasonCodes = ["mrk_sequence_gap"] }));

        Assert.AreEqual("positioning_aux_probe_response_invalid", invalid.Code);
    }

    [TestMethod]
    public void IdempotentResumeCancelAndRecoveryBoundariesAreStable()
    {
        using var scope = new CatalogScope();
        var run = scope.StartDjiRun("dataset-version-recovery", "import-session-recovery", "source-preflight-recovery");
        scope.Catalog.MarkRunning(run.RunId);
        var item = scope.Catalog.ListIncompleteWorkItems(run.RunId).First();

        scope.Catalog.MarkItemInterrupted(item.ItemId, "source_device_unavailable");
        CollectionAssert.Contains(scope.Catalog.ListRecoverableRunIds().ToList(), run.RunId);
        Assert.IsTrue(scope.Catalog.ListIncompleteWorkItems(run.RunId).Any(work => work.Status == "interrupted"));

        var resumed = scope.Catalog.Resume("resume-recovery", new PositioningAuxImportResumeParameters(run.RunId, "D:\\safe-synthetic-root"));
        var replay = scope.Catalog.Resume("resume-recovery", new PositioningAuxImportResumeParameters(run.RunId, "D:\\safe-synthetic-root"));
        Assert.AreEqual(resumed.RunId, replay.RunId);
        Assert.AreEqual("running", resumed.Status);

        var conflict = Assert.Throws<BusinessCatalogException>(() =>
            scope.Catalog.Resume("resume-recovery", new PositioningAuxImportResumeParameters(run.RunId, "E:\\different-root")));
        Assert.AreEqual("idempotency_conflict", conflict.Code);

        var cancelled = scope.Catalog.Cancel("cancel-recovery", new PositioningAuxImportCancelParameters(run.RunId));
        Assert.AreEqual("cancelled", cancelled.Status);
        Assert.AreEqual("cancelled_by_user", cancelled.LastErrorCode);
        Assert.IsFalse(scope.Catalog.ListRecoverableRunIds().Contains(run.RunId));
    }

    private static ImageProbeCasPositioningAuxResult MrkResult(string qualityState, string inventoryHash) => new(
        ImageProbeProtocol.CasPositioningAuxV1,
        ImageProbeProtocol.CasPositioningAuxProfile,
        "parsed",
        qualityState,
        "positioning_aux",
        "mrk",
        "contiguous",
        "complete",
        "non_negative",
        qualityState == "passed" ? "all_q50" : "mixed_q",
        inventoryHash,
        [],
        new ImageProbeCasPositioningAuxParserIdentity(
            PositioningAuxCatalog.ParserName,
            PositioningAuxCatalog.ParserVersion,
            ImageProbeProtocol.DjiMrkParserV1,
            ImageProbeProtocol.DjiMrkQualityPolicyV1),
        new ImageProbePrivacy(false, false, false, false, false, false, false, false));

    private static void AssertSanitized(object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var privateName in new[]
                 {
                     "absolutePath", "relativePath", "sourceEntryKey", "sourceRootKey",
                     "sourceIdentityKey", "contentHash", "sha256", "objectKey",
                     "stageReceipt", "stageId", "rawRecord", "rawRecords",
                     "latitude", "longitude", "coordinate", "coordinates",
                     "timestamp", "timestamps", "locator", "ownerSampleStatistics"
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
            $"qiongtu-positioning-aux-tests-{Guid.NewGuid():N}");

        public CatalogScope()
        {
            Directory.CreateDirectory(_root);
            Database = new BusinessDatabase(Path.Combine(_root, "business.db"));
            Database.Initialize();
            Catalog = new PositioningAuxCatalog(Database);
        }

        public BusinessDatabase Database { get; }

        public PositioningAuxCatalog Catalog { get; }

        public PositioningAuxImportRun StartDjiRun(string datasetVersionId, string sessionId, string preflightRunId)
        {
            SeedPreflight(datasetVersionId, sessionId, preflightRunId, "dji_supported", includeSidecars: true);
            return Catalog.EnsureRunForCompletedPreflight(preflightRunId, AssociationBindings(preflightRunId));
        }

        public IReadOnlyList<PositioningAuxAssociationBinding> AssociationBindings(string preflightRunId) =>
        [
            new PositioningAuxAssociationBinding(preflightRunId + "-mrk", Sha('a'), 1),
            new PositioningAuxAssociationBinding(preflightRunId + "-nav", Sha('c'), 1),
            new PositioningAuxAssociationBinding(preflightRunId + "-obs", Sha('e'), 1),
            new PositioningAuxAssociationBinding(preflightRunId + "-rtk", Sha('1'), 1)
        ];

        public PositioningAuxImportCompletion RetainWithoutParsing(PositioningAuxImportWorkItem item, string sha256)
        {
            Catalog.MarkStaging(item.ItemId);
            Catalog.RecordStageReceipt(new PositioningAuxStageReceipt(item.ItemId, "stage-" + item.AuxiliaryType + "-" + Guid.NewGuid().ToString("N")[..8], sha256, item.ByteLengthSnapshot, DateTimeOffset.Parse("2026-08-28T00:00:00Z")));
            Catalog.MarkPublishing(item.ItemId, sha256, item.ByteLengthSnapshot);
            return Catalog.CompletePublishedRetention(item.ItemId, sha256, item.ByteLengthSnapshot, null);
        }

        public string RetainParsedMrk(PositioningAuxImportWorkItem item, string contentHash, string inventoryHash)
        {
            var retained = RetainWithoutParsing(item, contentHash);
            Catalog.BeginParsing(item.ItemId);
            Catalog.CompleteParsedMrk(item.ItemId, MrkResult("warning", inventoryHash));
            return retained.PositioningAuxFileId!;
        }

        public void SeedJobExecution(string datasetVersionId, string jobId, string executionId)
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO processing_jobs(
                    processing_job_id, project_id, dataset_version_id, job_type,
                    requested_outputs_json, parameter_profile, parameter_schema_version,
                    parameters_json, parameter_sha256, lifecycle_state, recovery_state,
                    created_at_utc, submitted_at_utc)
                VALUES(
                    $job_id, 'project-positioning', $dataset_version_id, 'photogrammetry',
                    '["dom"]', 'standard', 'v1', '{}',
                    'abababababababababababababababababababababababababababababababab',
                    'succeeded', 'not_applicable',
                    '2026-08-28T00:00:00Z', '2026-08-28T00:00:00Z');
                INSERT INTO job_executions(
                    job_execution_id, processing_job_id, attempt_number, execution_mode,
                    worker_type, worker_version, engine_name, engine_version,
                    parameter_sha256, lifecycle_state, checkpoint_compatibility_state,
                    started_at_utc, ended_at_utc)
                VALUES(
                    $execution_id, $job_id, 1, 'full', 'photogrammetry',
                    '1.0.0', 'synthetic-engine', '1.0.0',
                    'abababababababababababababababababababababababababababababababab',
                    'succeeded', 'unavailable',
                    '2026-08-28T00:00:00Z', '2026-08-28T00:10:00Z');
                """;
            command.Parameters.AddWithValue("$job_id", jobId);
            command.Parameters.AddWithValue("$execution_id", executionId);
            command.Parameters.AddWithValue("$dataset_version_id", datasetVersionId);
            command.ExecuteNonQuery();
        }

        public void SeedPreflight(
            string datasetVersionId,
            string sessionId,
            string preflightRunId,
            string decision,
            bool includeSidecars)
        {
            var sourceRoot = Sha('0');
            var now = "2026-08-28T00:00:00Z";
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR IGNORE INTO projects(
                    project_id, name, spatial_configuration_state, lifecycle_state,
                    created_at_utc, updated_at_utc)
                VALUES(
                    'project-positioning', 'Project', 'pending', 'active',
                    '2026-08-28T00:00:00Z', '2026-08-28T00:00:00Z');
                INSERT OR IGNORE INTO datasets(
                    dataset_id, project_id, name, lifecycle_state,
                    created_at_utc, updated_at_utc)
                VALUES(
                    'dataset-positioning', 'project-positioning', 'Dataset', 'active',
                    '2026-08-28T00:00:00Z', '2026-08-28T00:00:00Z');
                INSERT INTO dataset_versions(
                    dataset_version_id, dataset_id, version_number, lifecycle_state,
                    source_eligibility_state, quality_gate_state, created_at_utc)
                VALUES(
                    $dataset_version_id, 'dataset-positioning',
                    (SELECT COALESCE(MAX(version_number), 0) + 1 FROM dataset_versions WHERE dataset_id='dataset-positioning'),
                    'draft', 'pending', 'not_run', '2026-08-28T00:00:00Z');
                INSERT INTO image_import_sessions(
                    import_session_id, dataset_version_id, source_root_key,
                    source_locator_manifest_id, status, total_entry_count,
                    created_at_utc, updated_at_utc)
                VALUES(
                    $session_id, $dataset_version_id, $source_root, $session_id,
                    'awaiting_source_preflight', 1,
                    '2026-08-28T00:00:00Z', '2026-08-28T00:00:00Z');
                INSERT INTO image_import_entries(
                    import_entry_id, import_session_id, dataset_version_id,
                    source_entry_key, display_name, sort_index,
                    byte_length_snapshot, source_identity_key, status,
                    created_at_utc, updated_at_utc)
                VALUES(
                    $session_id || '-image', $session_id, $dataset_version_id,
                    $image_source_key, 'DJI_0001.JPG', 1,
                    1024, $image_identity, 'awaiting_source_preflight',
                    '2026-08-28T00:00:00Z', '2026-08-28T00:00:00Z');
                INSERT INTO source_preflight_runs(
                    source_preflight_run_id, import_session_id, dataset_version_id,
                    source_root_key_snapshot, source_locator_manifest_id_snapshot,
                    parser_profile, parser_version, policy_version, status,
                    total_item_count, image_candidate_count, sidecar_candidate_count,
                    completed_item_count, supports_dji_item_count, out_of_scope_item_count,
                    unconfirmed_item_count, conflict_item_count, failed_item_count,
                    blocking_image_count, created_at_utc, started_at_utc, updated_at_utc)
                VALUES(
                    $preflight_run_id, $session_id, $dataset_version_id,
                    $source_root, $session_id, 'source-preflight.v1', '1.0.0',
                    'dji-source-policy.v1', 'running',
                    $total_count, 1, $sidecar_count, $total_count,
                    CASE WHEN $decision='dji_supported' THEN $total_count ELSE 0 END,
                    CASE WHEN $decision='out_of_scope' THEN 1 ELSE 0 END,
                    CASE WHEN $decision='unconfirmed' THEN $total_count ELSE 0 END,
                    0, 0,
                    CASE WHEN $decision='dji_supported' THEN 0 ELSE 1 END,
                    '2026-08-28T00:00:00Z',
                    '2026-08-28T00:00:00Z',
                    '2026-08-28T00:00:00Z');
                INSERT INTO source_preflight_items(
                    source_preflight_item_id, source_preflight_run_id, import_session_id,
                    dataset_version_id, import_entry_id, source_entry_key, display_name,
                    sort_index, candidate_kind, format_hint, byte_length_snapshot,
                    source_identity_key, status, container_hint, evidence_state,
                    evidence_json, parser_profile, parser_version, created_at_utc,
                    updated_at_utc, completed_at_utc)
                VALUES(
                    $preflight_run_id || '-image', $preflight_run_id, $session_id,
                    $dataset_version_id, $session_id || '-image', $image_source_key,
                    'DJI_0001.JPG', 1, 'image_candidate', 'jpg', 1024,
                    $image_identity, 'completed', 'jpeg_hint',
                    CASE WHEN $decision='dji_supported' THEN 'supports_dji' ELSE 'unconfirmed' END,
                    '{"evidenceKinds":["synthetic_dji"],"reasonCodes":[]}',
                    'source-preflight.v1', '1.0.0',
                    '2026-08-28T00:00:00Z',
                    '2026-08-28T00:00:00Z',
                    '2026-08-28T00:00:00Z');
                """;
            command.Parameters.AddWithValue("$dataset_version_id", datasetVersionId);
            command.Parameters.AddWithValue("$session_id", sessionId);
            command.Parameters.AddWithValue("$preflight_run_id", preflightRunId);
            command.Parameters.AddWithValue("$source_root", sourceRoot);
            command.Parameters.AddWithValue("$image_source_key", Sha('7'));
            command.Parameters.AddWithValue("$image_identity", Sha('8'));
            command.Parameters.AddWithValue("$decision", decision);
            command.Parameters.AddWithValue("$sidecar_count", includeSidecars ? 4 : 0);
            command.Parameters.AddWithValue("$total_count", includeSidecars ? 5 : 1);
            command.ExecuteNonQuery();

            if (includeSidecars)
            {
                var sidecars = new[]
                {
                    ("mrk", 2, 128L, Sha('a'), Sha('b')),
                    ("nav", 3, 256L, Sha('c'), Sha('d')),
                    ("obs", 4, 512L, Sha('e'), Sha('f')),
                    ("rtk", 5, 768L, Sha('1'), Sha('2'))
                };
                foreach (var sidecar in sidecars)
                {
                    using var sidecarCommand = connection.CreateCommand();
                    sidecarCommand.CommandText =
                        """
                        INSERT INTO source_preflight_items(
                            source_preflight_item_id, source_preflight_run_id,
                            import_session_id, dataset_version_id, import_entry_id,
                            source_entry_key, display_name, sort_index, candidate_kind,
                            format_hint, byte_length_snapshot, source_last_write_time_utc,
                            source_identity_key, status, container_hint, evidence_state,
                            evidence_json, parser_profile, parser_version, created_at_utc,
                            updated_at_utc, completed_at_utc)
                        VALUES(
                            $item_id, $preflight_run_id, $session_id,
                            $dataset_version_id, NULL, $source_entry_key,
                            $display_name, $sort_index, 'positioning_aux_candidate',
                            $format_hint, $byte_length, $last_write,
                            $source_identity_key, 'completed', 'not_image',
                            'supports_dji',
                            '{"evidenceKinds":["synthetic_sidecar"],"reasonCodes":[]}',
                            'source-preflight.v1', '1.0.0',
                            '2026-08-28T00:00:00Z',
                            '2026-08-28T00:00:00Z',
                            '2026-08-28T00:00:00Z');
                        """;
                    sidecarCommand.Parameters.AddWithValue("$item_id", preflightRunId + "-" + sidecar.Item1);
                    sidecarCommand.Parameters.AddWithValue("$preflight_run_id", preflightRunId);
                    sidecarCommand.Parameters.AddWithValue("$session_id", sessionId);
                    sidecarCommand.Parameters.AddWithValue("$dataset_version_id", datasetVersionId);
                    sidecarCommand.Parameters.AddWithValue("$source_entry_key", sidecar.Item4);
                    sidecarCommand.Parameters.AddWithValue("$display_name", "DJI_0001." + sidecar.Item1.ToUpperInvariant());
                    sidecarCommand.Parameters.AddWithValue("$sort_index", sidecar.Item2);
                    sidecarCommand.Parameters.AddWithValue("$format_hint", sidecar.Item1);
                    sidecarCommand.Parameters.AddWithValue("$byte_length", sidecar.Item3);
                    sidecarCommand.Parameters.AddWithValue("$last_write", now);
                    sidecarCommand.Parameters.AddWithValue("$source_identity_key", sidecar.Item5);
                    sidecarCommand.ExecuteNonQuery();
                }
            }

            using var complete = connection.CreateCommand();
            complete.CommandText =
                """
                UPDATE source_preflight_runs
                SET status='completed', decision=$decision,
                    decision_reason_code=CASE WHEN $decision='dji_supported'
                        THEN 'dji_evidence_confirmed' ELSE 'dji_evidence_incomplete' END,
                    evidence_summary_json='{"decision":"synthetic"}',
                    updated_at_utc='2026-08-28T00:00:01Z',
                    completed_at_utc='2026-08-28T00:00:01Z'
                WHERE source_preflight_run_id=$preflight_run_id;
                UPDATE dataset_versions
                SET source_eligibility_state=$decision,
                    source_evidence_json='{"decision":"synthetic"}',
                    source_eligibility_run_id=$preflight_run_id,
                    source_eligibility_decided_at_utc='2026-08-28T00:00:01Z'
                WHERE dataset_version_id=$dataset_version_id;
                UPDATE image_import_sessions
                SET status=CASE WHEN $decision='dji_supported' THEN 'ready' ELSE 'awaiting_source_preflight' END,
                    updated_at_utc='2026-08-28T00:00:01Z'
                WHERE import_session_id=$session_id;
                """;
            complete.Parameters.AddWithValue("$decision", decision);
            complete.Parameters.AddWithValue("$preflight_run_id", preflightRunId);
            complete.Parameters.AddWithValue("$dataset_version_id", datasetVersionId);
            complete.Parameters.AddWithValue("$session_id", sessionId);
            complete.ExecuteNonQuery();
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
