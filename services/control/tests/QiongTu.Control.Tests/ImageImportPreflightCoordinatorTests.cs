using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ImageImportPreflightCoordinatorTests
{
    [TestMethod]
    public async Task DjiEvidenceCompletesPreflightAndAutomaticallyCopiesTheApprovedSource()
    {
        await using var scope = await CoordinatorScope.CreateAsync(
            (kind, _, _) => kind == "image_candidate"
                ? Result(kind, "supports_dji", ["dji_exif_manufacturer"], [])
                : Result(kind, "unconfirmed", [], ["generic_positioning_evidence_only"]));
        await scope.WriteSourceAsync("DJI_0001.JPG", "synthetic-jpeg-bytes");
        await scope.StartImportAsync("dataset-version-dji", "session-dji");

        var queued = await scope.PreflightCoordinator.StartAsync(
            "preflight-request-dji",
            new ImageImportPreflightStartParameters("session-dji"));
        await scope.PreflightCoordinator.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await scope.ImportCoordinator.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var completed = scope.PreflightCatalog.Get(
            new ImageImportPreflightGetParameters(queued.PreflightRunId));
        Assert.AreEqual("completed", completed.Status);
        Assert.AreEqual("dji_supported", completed.Decision);
        Assert.AreEqual("completed", scope.ImportCatalog.Get(
            new ImageImportGetParameters("session-dji")).Status);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_objects;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM images;"));
    }

    [TestMethod]
    public async Task OtherManufacturerDecisionLeavesCasAndBusinessObjectsEmpty()
    {
        await using var scope = await CoordinatorScope.CreateAsync(
            (kind, _, _) => Result(kind, "out_of_scope", ["other_exif_manufacturer"], ["other_manufacturer"]));
        await scope.WriteSourceAsync("OTHER_0001.JPG", "synthetic-other-camera");
        await scope.StartImportAsync("dataset-version-other", "session-other");

        var queued = await scope.PreflightCoordinator.StartAsync(
            "preflight-request-other",
            new ImageImportPreflightStartParameters("session-other"));
        await scope.PreflightCoordinator.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var completed = scope.PreflightCatalog.Get(
            new ImageImportPreflightGetParameters(queued.PreflightRunId));
        Assert.AreEqual("out_of_scope", completed.Decision);
        Assert.AreEqual("awaiting_source_preflight", scope.ImportCatalog.Get(
            new ImageImportGetParameters("session-other")).Status);
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_objects;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM images;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM processing_jobs;"));
        Assert.IsFalse(Directory.EnumerateFiles(
            scope.ObjectStore.PublishedDirectory,
            "*",
            SearchOption.AllDirectories).Any());
        var persistedText = scope.PreflightPersistenceText();
        Assert.DoesNotContain(scope.SourceRoot, persistedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic-other-camera", persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain(":\\", persistedText, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SingleMrkWithExactPrivateGroupCoverageCanSupportMetadataMissingImages()
    {
        var observedAssociationCounts = new List<int?>();
        await using var scope = await CoordinatorScope.CreateAsync((kind, hint, associationCount) =>
        {
            if (hint == "mrk")
            {
                observedAssociationCounts.Add(associationCount);
                return Result(kind, "supports_dji", ["dji_mrk_13_field_layout", "dji_mrk_batch_coverage"], []);
            }

            return Result(kind, "unconfirmed", [], ["dji_evidence_missing"]);
        });
        await scope.WriteSourceAsync("flight/DJI_0001.JPG", "synthetic-jpeg-bytes");
        await scope.WriteSourceAsync("flight/mission.MRK", "synthetic-mrk-bytes");
        await scope.StartImportAsync("dataset-version-mrk", "session-mrk");

        var queued = await scope.PreflightCoordinator.StartAsync(
            "preflight-request-mrk",
            new ImageImportPreflightStartParameters("session-mrk"));
        await scope.PreflightCoordinator.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await scope.ImportCoordinator.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(new int?[] { 1 }, observedAssociationCounts);
        var completed = scope.PreflightCatalog.Get(
            new ImageImportPreflightGetParameters(queued.PreflightRunId));
        Assert.AreEqual("dji_supported", completed.Decision);
        var items = scope.PreflightCatalog.ListItems(
            new ImageImportPreflightItemListParameters(queued.PreflightRunId, 50, null));
        var image = items.Items.Single(item => item.CandidateKind == "image_candidate");
        Assert.AreEqual("supports_dji", image.EvidenceState);
        CollectionAssert.Contains(image.EvidenceKinds.ToList(), "dji_mrk_batch_coverage");
    }

    [TestMethod]
    public async Task RecoveryResetsRunningItemAndFinishesWithoutDuplicateRun()
    {
        await using var scope = await CoordinatorScope.CreateAsync(
            (kind, _, _) => Result(kind, "supports_dji", ["dji_exif_manufacturer"], []));
        await scope.WriteSourceAsync("DJI_0001.JPG", "synthetic-jpeg-bytes");
        await scope.StartImportAsync("dataset-version-recovery", "session-recovery");
        var run = scope.PreflightCatalog.Start(
            "preflight-request-recovery",
            new ImageImportPreflightStartParameters("session-recovery"));
        scope.PreflightCatalog.MarkRunning(run.PreflightRunId);
        var item = scope.PreflightCatalog.ListWorkItems(run.PreflightRunId).Single();
        scope.PreflightCatalog.MarkItemRunning(item.ItemId);

        await scope.PreflightCoordinator.RecoverAsync();
        await scope.PreflightCoordinator.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await scope.ImportCoordinator.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var completed = scope.PreflightCatalog.Get(
            new ImageImportPreflightGetParameters(run.PreflightRunId));
        Assert.AreEqual("completed", completed.Status);
        Assert.AreEqual("dji_supported", completed.Decision);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM source_preflight_runs;"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_objects;"));
    }

    [TestMethod]
    public async Task ImportRecoveryCompensatesForCrashAfterDecisionCommitBeforeQueueWrite()
    {
        await using var scope = await CoordinatorScope.CreateAsync(
            (kind, _, _) => Result(kind, "supports_dji", ["dji_exif_manufacturer"], []));
        await scope.WriteSourceAsync("DJI_0001.JPG", "decision-commit-crash-window");
        await scope.StartImportAsync("dataset-version-commit-recovery", "session-commit-recovery");
        var run = scope.PreflightCatalog.Start(
            "preflight-request-commit-recovery",
            new ImageImportPreflightStartParameters("session-commit-recovery"));
        scope.PreflightCatalog.MarkRunning(run.PreflightRunId);
        var item = scope.PreflightCatalog.ListWorkItems(run.PreflightRunId).Single();
        scope.PreflightCatalog.MarkItemRunning(item.ItemId);
        scope.PreflightCatalog.CompleteItem(item.ItemId, Result(
            "image_candidate",
            "supports_dji",
            ["dji_exif_manufacturer"],
            []));
        var committed = scope.PreflightCatalog.CommitDecision(run.PreflightRunId);
        Assert.AreEqual("dji_supported", committed.Decision);
        Assert.AreEqual("ready", scope.ImportCatalog.Get(
            new ImageImportGetParameters("session-commit-recovery")).Status);
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_objects;"));

        await scope.ImportCoordinator.RecoverAsync();
        await scope.ImportCoordinator.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual("completed", scope.ImportCatalog.Get(
            new ImageImportGetParameters("session-commit-recovery")).Status);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_objects;"));
    }

    [TestMethod]
    public async Task ChangedSourceBecomesUnconfirmedAndIsNeverCopied()
    {
        await using var scope = await CoordinatorScope.CreateAsync(
            (kind, _, _) => Result(kind, "supports_dji", ["dji_exif_manufacturer"], []));
        var path = await scope.WriteSourceAsync("DJI_0001.JPG", "original-synthetic-bytes");
        await scope.StartImportAsync("dataset-version-changed", "session-changed");
        await File.WriteAllTextAsync(path, "changed-source-with-different-length");

        var queued = await scope.PreflightCoordinator.StartAsync(
            "preflight-request-changed",
            new ImageImportPreflightStartParameters("session-changed"));
        await scope.PreflightCoordinator.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var completed = scope.PreflightCatalog.Get(
            new ImageImportPreflightGetParameters(queued.PreflightRunId));
        Assert.AreEqual("unconfirmed", completed.Decision);
        Assert.AreEqual("source_evidence_read_failed", completed.DecisionReasonCode);
        Assert.AreEqual(1, completed.FailedItemCount);
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_objects;"));
    }

    [TestMethod]
    public async Task InterruptedRunCanAdoptAReselectedRootOnlyAfterImageIdentityVerification()
    {
        await using var scope = await CoordinatorScope.CreateAsync(
            (kind, _, _) => Result(kind, "supports_dji", ["dji_exif_manufacturer"], []));
        await scope.WriteSourceAsync("DJI_0001.JPG", "stable-reselected-source");
        await scope.StartImportAsync("dataset-version-reselected", "session-reselected");
        var run = scope.PreflightCatalog.Start(
            "preflight-request-before-reselection",
            new ImageImportPreflightStartParameters("session-reselected"));
        scope.PreflightCatalog.MarkRunning(run.PreflightRunId);
        scope.PreflightCatalog.InterruptRun(run.PreflightRunId, "source_device_unavailable");

        var reselectedRoot = Path.Combine(Path.GetDirectoryName(scope.SourceRoot)!, "source-reselected");
        Directory.Move(scope.SourceRoot, reselectedRoot);
        var resumed = await scope.ImportCoordinator.ResumeAsync(
            "image-import-reselect-source",
            "session-reselected",
            reselectedRoot,
            scope.Paths);
        Assert.AreEqual("awaiting_source_preflight", resumed.Status);

        await scope.PreflightCoordinator.StartAsync(
            "preflight-request-after-reselection",
            new ImageImportPreflightStartParameters("session-reselected"));
        await scope.PreflightCoordinator.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await scope.ImportCoordinator.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var completed = scope.PreflightCatalog.Get(
            new ImageImportPreflightGetParameters(run.PreflightRunId));
        Assert.AreEqual("dji_supported", completed.Decision);
        Assert.AreEqual("completed", scope.ImportCatalog.Get(
            new ImageImportGetParameters("session-reselected")).Status);
    }

    private static ImageProbeSourcePreflightResult Result(
        string candidateKind,
        string evidenceState,
        IReadOnlyList<string> evidenceKinds,
        IReadOnlyList<string> reasons) => new(
        ImageProbeProtocol.SourcePreflightV1,
        ImageProbeProtocol.SourcePreflightProfile,
        "completed",
        candidateKind,
        candidateKind == "image_candidate" ? "jpeg_hint" : "not_image",
        evidenceState,
        evidenceKinds,
        reasons,
        new ImageProbeParserIdentity("qiongtu.source-preflight", "1.0.0", "2.9.3"),
        new ImageProbePrivacy(false, false, false, false, false, false, false, false));

    private sealed class CoordinatorScope : IAsyncDisposable
    {
        private readonly string _root;

        private CoordinatorScope(
            string root,
            ControlDataPaths paths,
            BusinessDatabase database,
            ImageImportCatalog importCatalog,
            ImageImportPreflightCatalog preflightCatalog,
            ImageImportSourceSecurity sourceSecurity,
            ImageImportSourceDiscovery sourceDiscovery,
            ContentAddressedObjectStore objectStore,
            ImageImportCoordinator importCoordinator,
            ImageImportPreflightCoordinator preflightCoordinator)
        {
            _root = root;
            Paths = paths;
            Database = database;
            ImportCatalog = importCatalog;
            PreflightCatalog = preflightCatalog;
            SourceSecurity = sourceSecurity;
            SourceDiscovery = sourceDiscovery;
            ObjectStore = objectStore;
            ImportCoordinator = importCoordinator;
            PreflightCoordinator = preflightCoordinator;
            SourceRoot = Path.Combine(root, "source");
            Directory.CreateDirectory(SourceRoot);
        }

        public ControlDataPaths Paths { get; }

        public BusinessDatabase Database { get; }

        public ImageImportCatalog ImportCatalog { get; }

        public ImageImportPreflightCatalog PreflightCatalog { get; }

        public ImageImportSourceSecurity SourceSecurity { get; }

        public ImageImportSourceDiscovery SourceDiscovery { get; }

        public ContentAddressedObjectStore ObjectStore { get; }

        public ImageImportCoordinator ImportCoordinator { get; }

        public ImageImportPreflightCoordinator PreflightCoordinator { get; }

        public string SourceRoot { get; }

        public static Task<CoordinatorScope> CreateAsync(
            Func<string, string?, int?, ImageProbeSourcePreflightResult> resultFactory)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"qiongtu-preflight-coordinator-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = ControlDataPaths.Create(Path.Combine(root, "control"));
            var database = new BusinessDatabase(paths.BusinessDatabase);
            database.Initialize();
            var importCatalog = new ImageImportCatalog(database);
            var preflightCatalog = new ImageImportPreflightCatalog(database);
            var sourceSecurity = new ImageImportSourceSecurity(
                Path.Combine(paths.StateDirectory, "image-import-locators"),
                new PassthroughProtector());
            var sourceDiscovery = new ImageImportSourceDiscovery(sourceSecurity);
            var objectStore = new ContentAddressedObjectStore(paths.ObjectDirectory);
            var importCoordinator = new ImageImportCoordinator(
                importCatalog,
                sourceSecurity,
                sourceDiscovery,
                objectStore);
            var probe = new ImageSourcePreflightProbe(
                sourceDiscovery,
                new FakeProbeClient(resultFactory));
            var preflightCoordinator = new ImageImportPreflightCoordinator(
                preflightCatalog,
                sourceSecurity,
                sourceDiscovery,
                probe,
                importCoordinator,
                paths);
            return Task.FromResult(new CoordinatorScope(
                root,
                paths,
                database,
                importCatalog,
                preflightCatalog,
                sourceSecurity,
                sourceDiscovery,
                objectStore,
                importCoordinator,
                preflightCoordinator));
        }

        public async Task<string> WriteSourceAsync(string relativePath, string content)
        {
            var path = Path.Combine(SourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
            return path;
        }

        public async Task StartImportAsync(string datasetVersionId, string sessionId)
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

            var session = await ImportCoordinator.StartAsync(
                "image-import-request-" + sessionId,
                sessionId,
                datasetVersionId,
                SourceRoot,
                Paths);
            Assert.AreEqual("awaiting_source_preflight", session.Status);
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

        public string PreflightPersistenceText()
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT group_concat(value, '|') FROM (
                    SELECT source_locator_manifest_id_snapshot || '|' || parser_profile || '|' ||
                           parser_version || '|' || policy_version || '|' ||
                           ifnull(decision_reason_code,'') || '|' || ifnull(evidence_summary_json,'') || '|' ||
                           ifnull(failure_code,'') AS value
                    FROM source_preflight_runs
                    UNION ALL
                    SELECT display_name || '|' || candidate_kind || '|' || ifnull(format_hint,'') || '|' ||
                           ifnull(container_hint,'') || '|' || ifnull(evidence_json,'') || '|' ||
                           ifnull(failure_code,'')
                    FROM source_preflight_items
                    UNION ALL
                    SELECT response_json FROM catalog_mutations
                    WHERE method='image-import-preflight.start'
                );
                """;
            return command.ExecuteScalar() as string ?? string.Empty;
        }

        public async ValueTask DisposeAsync()
        {
            await PreflightCoordinator.DisposeAsync();
            await ImportCoordinator.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeProbeClient(
        Func<string, string?, int?, ImageProbeSourcePreflightResult> resultFactory)
        : IImageSourcePreflightProbeClient
    {
        public async Task<ImageProbeSourcePreflightResult> AnalyzeAsync(
            Stream source,
            string candidateKind,
            string? formatHint,
            int? associationItemCount,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[64];
            _ = await source.ReadAsync(buffer, cancellationToken);
            return resultFactory(candidateKind, formatHint, associationItemCount);
        }
    }

    private sealed class PassthroughProtector : IImageImportSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] protectedData) => protectedData.ToArray();
    }
}
