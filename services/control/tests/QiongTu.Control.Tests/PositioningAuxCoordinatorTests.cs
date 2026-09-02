using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PositioningAuxCoordinatorTests
{
    [TestMethod]
    public async Task ApprovedBatchRetainsAllSidecarsAndOnlyFormallyParsesMrk()
    {
        await using var scope = await CoordinatorScope.CreateAsync(autoEnqueue: true);
        await scope.WriteAsync("flight/IMG_0001.JPG", SourcePreflightSyntheticFixture.BareJpeg());
        var mrk = SourcePreflightSyntheticFixture.DjiMrk(1);
        await scope.WriteAsync("flight/mission.MRK", mrk);
        await scope.WriteAsync("flight/mission.NAV", SourcePreflightSyntheticFixture.Rinex());
        await scope.WriteAsync("flight/mission.OBS", SourcePreflightSyntheticFixture.Rinex());
        await scope.WriteAsync("flight/mission.RTK", SourcePreflightSyntheticFixture.Rtcm3());

        var preflight = await scope.StartApprovedAsync("dataset-version-aux", "session-aux");
        await scope.AuxCoordinator.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual("dji_supported", preflight.Decision);
        var run = scope.CurrentRun();
        Assert.AreEqual("completed", run.Status);
        Assert.AreEqual(4, run.TotalFileCount);
        Assert.AreEqual(4, run.CompletedFileCount);
        var files = scope.AuxCatalog.ListFiles(
            new PositioningAuxFileListParameters(run.DatasetVersionId, run.RunId, 20, null)).Items;
        Assert.HasCount(4, files);
        var parsed = files.Single(file => file.AuxType == "mrk");
        Assert.AreEqual("parsed", parsed.ParseState);
        Assert.AreEqual("passed", parsed.QualityState);
        Assert.AreEqual("not_recorded", parsed.UsageState);
        Assert.IsTrue(files.Where(file => file.AuxType != "mrk")
            .All(file => file.ParseState == "unsupported" && file.QualityState == "not_checked"));
        Assert.IsTrue(files.All(file =>
            !file.Privacy.PathsIncluded && !file.Privacy.HashesIncluded &&
            !file.Privacy.ObjectKeysIncluded && !file.Privacy.RawRecordsIncluded &&
            !file.Privacy.CoordinatesIncluded && !file.Privacy.TimestampsIncluded));
        Assert.AreEqual(3L, scope.Scalar<long>(
            "SELECT count(*) FROM file_objects WHERE object_kind='positioning_aux';"));
        Assert.AreEqual(3L, scope.Scalar<long>(
            "SELECT count(*) FROM file_object_roles WHERE object_role='positioning_aux';"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM positioning_aux_usage;"));
        CollectionAssert.AreEqual(mrk, await File.ReadAllBytesAsync(scope.SourcePath("flight/mission.MRK")));
    }

    [TestMethod]
    public async Task SourceChangedAfterPreflightInterruptsOnlyFormalRetention()
    {
        await using var scope = await CoordinatorScope.CreateAsync(autoEnqueue: false);
        await scope.WriteAsync("flight/IMG_0001.JPG", SourcePreflightSyntheticFixture.BareJpeg());
        await scope.WriteAsync("flight/mission.MRK", SourcePreflightSyntheticFixture.DjiMrk(1));
        var preflight = await scope.StartApprovedAsync("dataset-version-change", "session-change");
        await File.AppendAllTextAsync(scope.SourcePath("flight/mission.MRK"), "changed");

        await scope.AuxCoordinator.EnqueueApprovedSessionAsync(preflight.ImportSessionId);
        await scope.AuxCoordinator.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var run = scope.CurrentRun();
        Assert.AreEqual("interrupted", run.Status);
        Assert.AreEqual(0L, scope.Scalar<long>(
            "SELECT count(*) FROM file_objects WHERE object_kind='positioning_aux';"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM positioning_aux_files;"));
        Assert.AreEqual(1L, scope.Scalar<long>(
            "SELECT count(*) FROM positioning_aux_import_items WHERE status='interrupted';"));
    }

    [TestMethod]
    public async Task RecoveryCreatesRunWhenControlStoppedAfterApprovedPreflightCallbackBoundary()
    {
        await using var scope = await CoordinatorScope.CreateAsync(autoEnqueue: false);
        await scope.WriteAsync("flight/IMG_0001.JPG", SourcePreflightSyntheticFixture.BareJpeg());
        await scope.WriteAsync("flight/mission.MRK", SourcePreflightSyntheticFixture.DjiMrk(1));
        _ = await scope.StartApprovedAsync("dataset-version-recover", "session-recover");
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM positioning_aux_import_runs;"));

        await scope.AuxCoordinator.RecoverAsync();
        await scope.AuxCoordinator.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual("completed", scope.CurrentRun().Status);
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM positioning_aux_files;"));
    }

    private sealed class CoordinatorScope : IAsyncDisposable
    {
        private readonly string _root;

        private CoordinatorScope(
            string root,
            string sourceRoot,
            ControlDataPaths paths,
            BusinessDatabase database,
            ImageImportCatalog importCatalog,
            ImageImportCoordinator importCoordinator,
            ImageImportPreflightCatalog preflightCatalog,
            ImageImportPreflightCoordinator preflightCoordinator,
            PositioningAuxCatalog auxCatalog,
            PositioningAuxCoordinator auxCoordinator)
        {
            _root = root;
            SourceRoot = sourceRoot;
            Paths = paths;
            Database = database;
            ImportCatalog = importCatalog;
            ImportCoordinator = importCoordinator;
            PreflightCatalog = preflightCatalog;
            PreflightCoordinator = preflightCoordinator;
            AuxCatalog = auxCatalog;
            AuxCoordinator = auxCoordinator;
        }

        public string SourceRoot { get; }

        public ControlDataPaths Paths { get; }

        public BusinessDatabase Database { get; }

        public ImageImportCatalog ImportCatalog { get; }

        public ImageImportCoordinator ImportCoordinator { get; }

        public ImageImportPreflightCatalog PreflightCatalog { get; }

        public ImageImportPreflightCoordinator PreflightCoordinator { get; }

        public PositioningAuxCatalog AuxCatalog { get; }

        public PositioningAuxCoordinator AuxCoordinator { get; }

        public static Task<CoordinatorScope> CreateAsync(bool autoEnqueue)
        {
            var root = Path.Combine(Path.GetTempPath(), $"qiongtu-aux-coordinator-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var sourceRoot = Path.Combine(root, "source");
            Directory.CreateDirectory(sourceRoot);
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
            var auxCatalog = new PositioningAuxCatalog(database);
            var auxCoordinator = new PositioningAuxCoordinator(
                auxCatalog,
                preflightCatalog,
                sourceSecurity,
                sourceDiscovery,
                objectStore,
                new FakePositioningAuxProbe(),
                paths);
            var sourceProbe = new ImageSourcePreflightProbe(
                sourceDiscovery,
                new FakeSourcePreflightProbe());
            Func<string, CancellationToken, Task> callback = autoEnqueue
                ? auxCoordinator.EnqueueApprovedSessionAsync
                : static (_, _) => Task.CompletedTask;
            var preflightCoordinator = new ImageImportPreflightCoordinator(
                preflightCatalog,
                sourceSecurity,
                sourceDiscovery,
                sourceProbe,
                callback,
                paths);
            return Task.FromResult(new CoordinatorScope(
                root,
                sourceRoot,
                paths,
                database,
                importCatalog,
                importCoordinator,
                preflightCatalog,
                preflightCoordinator,
                auxCatalog,
                auxCoordinator));
        }

        public string SourcePath(string relativePath) =>
            Path.Combine(SourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public async Task WriteAsync(string relativePath, byte[] bytes)
        {
            var path = SourcePath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
        }

        public async Task<ImageImportPreflightRun> StartApprovedAsync(string datasetVersionId, string sessionId)
        {
            SeedDatasetVersion(datasetVersionId);
            var session = await ImportCoordinator.StartAsync(
                "import-request-" + sessionId,
                sessionId,
                datasetVersionId,
                SourceRoot,
                Paths);
            Assert.AreEqual("awaiting_source_preflight", session.Status);
            var started = await PreflightCoordinator.StartAsync(
                "preflight-request-" + sessionId,
                new ImageImportPreflightStartParameters(sessionId));
            await PreflightCoordinator.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return PreflightCatalog.Get(new ImageImportPreflightGetParameters(started.PreflightRunId));
        }

        public PositioningAuxImportRun CurrentRun()
        {
            var runId = Scalar<string>(
                "SELECT positioning_aux_import_run_id FROM positioning_aux_import_runs LIMIT 1;");
            return AuxCatalog.Get(new PositioningAuxImportGetParameters(runId));
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

        private void SeedDatasetVersion(string datasetVersionId)
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR IGNORE INTO projects(
                    project_id,name,spatial_configuration_state,lifecycle_state,created_at_utc,updated_at_utc)
                VALUES('project-aux','Project','pending','active','2026-08-31T00:00:00Z','2026-08-31T00:00:00Z');
                INSERT OR IGNORE INTO datasets(
                    dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
                VALUES('dataset-aux','project-aux','Dataset','active','2026-08-31T00:00:00Z','2026-08-31T00:00:00Z');
                INSERT INTO dataset_versions(
                    dataset_version_id,dataset_id,version_number,lifecycle_state,
                    source_eligibility_state,quality_gate_state,created_at_utc)
                VALUES(
                    $dataset_version_id,'dataset-aux',
                    (SELECT COALESCE(MAX(version_number),0)+1 FROM dataset_versions WHERE dataset_id='dataset-aux'),
                    'draft','pending','not_run','2026-08-31T00:00:00Z');
                """;
            command.Parameters.AddWithValue("$dataset_version_id", datasetVersionId);
            command.ExecuteNonQuery();
        }

        public async ValueTask DisposeAsync()
        {
            await PreflightCoordinator.DisposeAsync();
            await AuxCoordinator.DisposeAsync();
            await ImportCoordinator.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeSourcePreflightProbe : IImageSourcePreflightProbeClient
    {
        public Task<ImageProbeSourcePreflightResult> AnalyzeAsync(
            Stream source,
            string candidateKind,
            string? formatHint,
            int? associationItemCount,
            CancellationToken cancellationToken)
        {
            var result = formatHint == "mrk"
                ? Result(candidateKind, "supports_dji", ["dji_mrk_13_field_layout", "dji_mrk_batch_coverage"], [])
                : candidateKind == "image_candidate"
                    ? Result(candidateKind, "unconfirmed", [], ["dji_evidence_missing"])
                    : Result(candidateKind, "unconfirmed", [], ["generic_positioning_evidence_only"]);
            return Task.FromResult(result);
        }
    }

    private sealed class FakePositioningAuxProbe : IPositioningAuxProbeClient
    {
        public Task<ImageProbeCasPositioningAuxResult> AnalyzeMrkAsync(
            ContentAddressedObjectStore objectStore,
            PublishedObject sourceObject,
            int associationItemCount,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ImageProbeCasPositioningAuxResult(
                ImageProbeProtocol.CasPositioningAuxV1,
                ImageProbeProtocol.CasPositioningAuxProfile,
                "parsed",
                "passed",
                "positioning_aux",
                "mrk",
                "contiguous",
                "complete",
                "non_negative",
                "all_q50",
                new string('a', 64),
                [],
                new ImageProbeCasPositioningAuxParserIdentity(
                    PositioningAuxCatalog.ParserName,
                    PositioningAuxCatalog.ParserVersion,
                    ImageProbeProtocol.DjiMrkParserV1,
                    ImageProbeProtocol.DjiMrkQualityPolicyV1),
                new ImageProbePrivacy(false, false, false, false, false, false, false, false)));
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

    private sealed class PassthroughProtector : IImageImportSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] protectedData) => protectedData.ToArray();
    }
}
