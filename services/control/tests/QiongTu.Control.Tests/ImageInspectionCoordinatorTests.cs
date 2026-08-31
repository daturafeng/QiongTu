using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ImageInspectionCoordinatorTests
{
    [TestMethod]
    public async Task JpegManifestPreservesOrientationReusesSourceAndIsIdempotent()
    {
        await using var scope = await InspectionScope.CreateAsync([1, 2, 3, 4]);
        var result = CompletedResult(
            "jpeg",
            [new ImageProbeCasImageFrame(0, "jpeg", 0, 4, 4, 3, 8, 6, "decoded")]);
        await using var coordinator = scope.CreateCoordinator(new FixedProbe(result));

        await coordinator.EnqueueImportEntryAsync(InspectionScope.ImportEntryId);
        await WaitForIdleAsync(coordinator);
        await coordinator.EnqueueImportEntryAsync(InspectionScope.ImportEntryId);
        await WaitForIdleAsync(coordinator);

        Assert.AreEqual(
            "completed",
            scope.Scalar<string>("SELECT status FROM image_inspection_runs;"),
            scope.Scalar<string>("SELECT COALESCE(failure_code, 'none') FROM image_inspection_runs;"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM images;"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM image_frames;"));
        Assert.AreEqual(3L, scope.Scalar<long>("SELECT width FROM images;"));
        Assert.AreEqual(4L, scope.Scalar<long>("SELECT height FROM images;"));
        Assert.AreEqual(2L, scope.Scalar<long>("SELECT count(*) FROM file_object_roles WHERE file_object_id='source-object';"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_objects;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM images WHERE raw_metadata_json IS NOT NULL;"));
    }

    [TestMethod]
    public async Task BlockedProbeDoesNotCreateManifestOrNormalizedRole()
    {
        await using var scope = await InspectionScope.CreateAsync([1, 2, 3, 4]);
        var blocked = CompletedResult("jpeg", []) with
        {
            Status = "blocked",
            Container = "unknown",
            StructureState = "rejected",
            DecodeState = "not_decoded",
            ReasonCodes = ["unsupported_image_container"]
        };
        await using var coordinator = scope.CreateCoordinator(new FixedProbe(blocked));

        await coordinator.EnqueueImportEntryAsync(InspectionScope.ImportEntryId);
        await WaitForIdleAsync(coordinator);

        Assert.AreEqual("blocked", scope.Scalar<string>("SELECT status FROM image_inspection_runs;"));
        Assert.AreEqual("unsupported_image_container", scope.Scalar<string>("SELECT failure_code FROM image_inspection_runs;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM images;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM image_frames;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_object_roles WHERE object_role='normalized_image_frame';"));
        Assert.AreEqual("available", scope.Scalar<string>("SELECT status FROM image_import_entries;"));
    }

    [TestMethod]
    public async Task MultiPageTiffSelectsLargestPageAndPreservesDepthAndPageIdentity()
    {
        await using var scope = await InspectionScope.CreateAsync(Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        var result = CompletedResult(
            "tiff",
            [
                new ImageProbeCasImageFrame(0, "tiff_page", 8, 0, 2, 2, 16, 1, "decoded"),
                new ImageProbeCasImageFrame(1, "tiff_page", 32, 0, 4, 3, 16, 6, "decoded")
            ]);
        await using var coordinator = scope.CreateCoordinator(new FixedProbe(result));

        await coordinator.EnqueueImportEntryAsync(InspectionScope.ImportEntryId);
        await WaitForIdleAsync(coordinator);

        Assert.AreEqual("completed", scope.Scalar<string>("SELECT status FROM image_inspection_runs;"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT primary_frame_index FROM images;"));
        Assert.AreEqual(3L, scope.Scalar<long>("SELECT width FROM images;"));
        Assert.AreEqual(4L, scope.Scalar<long>("SELECT height FROM images;"));
        Assert.AreEqual(2L, scope.Scalar<long>("SELECT count(*) FROM image_frames WHERE bits_per_channel=16 AND byte_length=0;"));
        Assert.AreEqual(32L, scope.Scalar<long>("SELECT byte_offset FROM image_frames WHERE frame_index=1;"));
        Assert.AreEqual("reuse_source_tiff_page", scope.Scalar<string>("SELECT normalization_action FROM image_frames WHERE frame_index=1;"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_objects;"));
    }

    [TestMethod]
    public async Task SameContentImportedAgainInSameDatasetReusesAuthoritativeManifest()
    {
        await using var scope = await InspectionScope.CreateAsync([1, 2, 3, 4]);
        var result = CompletedResult(
            "jpeg",
            [new ImageProbeCasImageFrame(0, "jpeg", 0, 4, 4, 3, 8, 1, "decoded")]);
        await using var coordinator = scope.CreateCoordinator(new FixedProbe(result));
        await coordinator.EnqueueImportEntryAsync(InspectionScope.ImportEntryId);
        await WaitForIdleAsync(coordinator);
        var secondEntryId = scope.AddCanonicalImportEntry("second");

        await coordinator.EnqueueImportEntryAsync(secondEntryId);
        await WaitForIdleAsync(coordinator);

        Assert.AreEqual(2L, scope.Scalar<long>("SELECT count(*) FROM image_inspection_runs WHERE status='completed';"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM images;"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM image_frames;"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(DISTINCT image_id) FROM image_inspection_runs;"));
    }

    [TestMethod]
    public async Task MpoPrimaryFrameIsPublishedByteExactAndRecoveredAfterPublishBeforeDatabaseCommit()
    {
        var source = Enumerable.Range(0, 20).Select(value => checked((byte)value)).ToArray();
        await using var scope = await InspectionScope.CreateAsync(source);
        var result = CompletedResult(
            "mpo",
            [
                new ImageProbeCasImageFrame(0, "mp_primary_image", 0, 8, 2, 2, 8, 1, "decoded"),
                new ImageProbeCasImageFrame(1, "mp_auxiliary_image", 8, 12, 4, 3, 8, 1, "decoded")
            ]);
        var catalog = new ImageFrameCatalog(scope.Database);
        var run = catalog.EnsureRun(InspectionScope.ImportEntryId);
        catalog.BeginProbe(run.InspectionRunId);
        var primary = ImageFrameCatalog.SelectPrimaryFrame(result)!;
        var inventory = ImageFrameCatalog.SerializeInventory(result);
        var stage = await scope.Store.StagePublishedRangeAsync(scope.PublishedSource, primary.ByteOffset, primary.ByteLength);
        catalog.RecordStagedProbe(run.InspectionRunId, result, primary, inventory, ImageFrameCatalog.InventorySha256(inventory), stage);
        catalog.MarkPublishing(run.InspectionRunId);
        var published = await scope.Store.PublishAsync(stage);

        await using var recovered = scope.CreateCoordinator(new ThrowingProbe());
        await recovered.RecoverAsync();
        await WaitForIdleAsync(recovered);

        Assert.AreEqual(
            "completed",
            scope.Scalar<string>("SELECT status FROM image_inspection_runs;"),
            scope.Scalar<string>("SELECT COALESCE(failure_code, 'none') FROM image_inspection_runs;"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM images;"));
        Assert.AreEqual(2L, scope.Scalar<long>("SELECT count(*) FROM image_frames;"));
        Assert.AreEqual(2L, scope.Scalar<long>("SELECT count(*) FROM file_objects;"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM file_object_roles WHERE object_role='normalized_image_frame';"));
        Assert.AreEqual(published.Sha256, scope.Scalar<string>("SELECT normalized_content_sha256 FROM image_inspection_runs;"));
        await using var stream = await scope.Store.OpenPublishedReadAsync(published);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        CollectionAssert.AreEqual(source[8..20], memory.ToArray());
    }

    [TestMethod]
    public async Task MetadataCatalogPersistsCompleteImmutableInventoryAndIdempotentSummary()
    {
        await using var scope = await CreateCompletedJpegScopeAsync();
        var imageId = scope.Scalar<string>("SELECT image_id FROM images;");
        var catalog = new ImageMetadataCatalog(scope.Database);
        var run = catalog.EnsureRun(imageId);
        catalog.BeginParsing(run.MetadataRunId);
        var fields = CompleteMetadataFields();

        var completed = catalog.Complete(run.MetadataRunId, fields);
        var replay = catalog.Complete(run.MetadataRunId, fields);

        Assert.AreEqual("completed", completed.Status);
        Assert.IsFalse(completed.ReusedExisting);
        Assert.IsTrue(replay.ReusedExisting);
        Assert.AreEqual(20L, scope.Scalar<long>("SELECT count(*) FROM image_metadata_fields;"));
        Assert.AreEqual("DJI", scope.Scalar<string>("SELECT manufacturer FROM images;"));
        Assert.AreEqual("FC-Test", scope.Scalar<string>("SELECT camera_model FROM images;"));
        Assert.AreEqual("2026-08-31T01:02:03Z", scope.Scalar<string>("SELECT capture_time_utc FROM images;"));
        Assert.AreEqual("parsed", scope.Scalar<string>("SELECT metadata_state FROM images;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM images WHERE raw_metadata_json IS NOT NULL;"));
        Assert.AreEqual(
            "gps_exif",
            scope.Scalar<string>("SELECT source_kind FROM image_metadata_fields WHERE field_name='position.latitude_deg';"));

        using var connection = scope.Database.OpenConnection();
        Assert.Throws<SqliteException>(() => ExecuteSql(
            connection,
            "UPDATE image_metadata_fields SET source_detail='changed' WHERE field_name='camera.manufacturer';"));
        Assert.Throws<SqliteException>(() => ExecuteSql(
            connection,
            "UPDATE images SET manufacturer='changed';"));

        var changed = fields
            .Select(field => field.FieldName == "camera.model"
                ? field with { FieldValueJson = JsonSerializer.Serialize("FC-Changed") }
                : field)
            .ToArray();
        var conflict = Assert.Throws<BusinessCatalogException>(() => catalog.Complete(run.MetadataRunId, changed));
        Assert.AreEqual("image_metadata_inventory_conflict", conflict.Code);
    }

    [TestMethod]
    public async Task MetadataCatalogPreservesConflictingCoordinateSourcesWithoutSelectingSummary()
    {
        await using var scope = await CreateCompletedJpegScopeAsync();
        var imageId = scope.Scalar<string>("SELECT image_id FROM images;");
        var catalog = new ImageMetadataCatalog(scope.Database);
        var run = catalog.EnsureRun(imageId);
        catalog.BeginParsing(run.MetadataRunId);
        var fields = CompleteMetadataFields()
            .Where(field => field.FieldName is not ("position.latitude_deg" or "position.longitude_deg"))
            .Concat(
            [
                ValueField("position.latitude_deg", 29.0, "gps_exif", "conflict", "GPS.Latitude"),
                ValueField("position.latitude_deg", 29.1, "dji_xmp", "conflict", "drone-dji:GpsLatitude"),
                ValueField("position.longitude_deg", 106.0, "gps_exif", "conflict", "GPS.Longitude"),
                ValueField("position.longitude_deg", 106.1, "dji_xmp", "conflict", "drone-dji:GpsLongitude")
            ])
            .ToArray();

        catalog.Complete(run.MetadataRunId, fields);

        Assert.AreEqual("conflict", scope.Scalar<string>("SELECT metadata_state FROM images;"));
        Assert.AreEqual(4L, scope.Scalar<long>(
            "SELECT count(*) FROM image_metadata_fields WHERE field_name IN ('position.latitude_deg','position.longitude_deg') AND field_state='conflict';"));
    }

    [TestMethod]
    public async Task BlockedMetadataRunLeavesFrameManifestAndFieldsUntouched()
    {
        await using var scope = await CreateCompletedJpegScopeAsync();
        var imageId = scope.Scalar<string>("SELECT image_id FROM images;");
        var catalog = new ImageMetadataCatalog(scope.Database);
        var run = catalog.EnsureRun(imageId);
        catalog.BeginParsing(run.MetadataRunId);

        catalog.Block(run.MetadataRunId, "image_metadata_probe_timeout");

        Assert.AreEqual("blocked", scope.Scalar<string>("SELECT status FROM image_metadata_runs;"));
        Assert.AreEqual("abnormal", scope.Scalar<string>("SELECT metadata_state FROM images;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM image_metadata_fields;"));
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM image_frames;"));
        Assert.AreEqual("completed", scope.Scalar<string>("SELECT status FROM image_inspection_runs;"));
    }

    [TestMethod]
    public async Task CompletedFrameManifestWakesIndependentMetadataCoordinator()
    {
        await using var scope = await InspectionScope.CreateAsync([1, 2, 3, 4]);
        var metadataProbe = new FixedMetadataProbe(CompletedMetadataResult(CompleteMetadataFields()));
        await using var metadataCoordinator = new ImageMetadataCoordinator(
            new ImageMetadataCatalog(scope.Database),
            scope.Store,
            metadataProbe);
        var imageResult = CompletedResult(
            "jpeg",
            [new ImageProbeCasImageFrame(0, "jpeg", 0, 4, 4, 3, 8, 1, "decoded")]);
        await using var imageCoordinator = new ImageInspectionCoordinator(
            new ImageFrameCatalog(scope.Database),
            scope.Store,
            new FixedProbe(imageResult),
            metadataCoordinator.EnqueueImageAsync);

        await imageCoordinator.EnqueueImportEntryAsync(InspectionScope.ImportEntryId);
        await WaitForIdleAsync(imageCoordinator);
        await WaitForIdleAsync(metadataCoordinator);

        Assert.AreEqual("completed", scope.Scalar<string>("SELECT status FROM image_metadata_runs;"));
        Assert.AreEqual(20L, scope.Scalar<long>("SELECT count(*) FROM image_metadata_fields;"));
        Assert.AreEqual(1, metadataProbe.CallCount);
    }

    [TestMethod]
    public async Task MetadataCoordinatorRecoveryReparsesInterruptedRunOnce()
    {
        await using var scope = await CreateCompletedJpegScopeAsync();
        var catalog = new ImageMetadataCatalog(scope.Database);
        var run = catalog.EnsureRun(scope.Scalar<string>("SELECT image_id FROM images;"));
        catalog.BeginParsing(run.MetadataRunId);
        var probe = new FixedMetadataProbe(CompletedMetadataResult(CompleteMetadataFields()));
        await using var coordinator = new ImageMetadataCoordinator(catalog, scope.Store, probe);

        await coordinator.RecoverAsync();
        await WaitForIdleAsync(coordinator);
        await coordinator.EnqueueImageAsync(run.ImageId);
        await WaitForIdleAsync(coordinator);

        Assert.AreEqual("completed", scope.Scalar<string>("SELECT status FROM image_metadata_runs;"));
        Assert.AreEqual(1, probe.CallCount);
    }

    private static ImageProbeCasImageResult CompletedResult(
        string container,
        IReadOnlyList<ImageProbeCasImageFrame> frames) =>
        new(
            ImageProbeProtocol.CasImageV1,
            ImageProbeProtocol.CasImageProfile,
            "completed",
            "source_image",
            container,
            "validated",
            "decoded",
            frames,
            [],
            new ImageProbeCasImageParserIdentity(
                "qiongtu.cas-image",
                "1.0.0",
                "magick.net-q16-x64",
                "14.16.0"),
            new ImageProbePrivacy(false, false, false, false, false, false, false, false));

    private static async Task<InspectionScope> CreateCompletedJpegScopeAsync()
    {
        var scope = await InspectionScope.CreateAsync([1, 2, 3, 4]);
        var result = CompletedResult(
            "jpeg",
            [new ImageProbeCasImageFrame(0, "jpeg", 0, 4, 4, 3, 8, 1, "decoded")]);
        await using var coordinator = scope.CreateCoordinator(new FixedProbe(result));
        await coordinator.EnqueueImportEntryAsync(InspectionScope.ImportEntryId);
        await WaitForIdleAsync(coordinator);
        return scope;
    }

    private static IReadOnlyList<ImageMetadataCatalogField> CompleteMetadataFields()
    {
        var values = new Dictionary<string, ImageMetadataCatalogField>(StringComparer.Ordinal)
        {
            ["capture.time_local"] = ValueField("capture.time_local", "2026-08-31T09:02:03", "exif", "present", "ExifSubIFD.DateTimeOriginal"),
            ["capture.time_utc"] = ValueField("capture.time_utc", "2026-08-31T01:02:03Z", "exif", "present", "ExifSubIFD.DateTimeOriginal+OffsetTimeOriginal"),
            ["camera.manufacturer"] = ValueField("camera.manufacturer", "DJI", "exif", "present", "ExifIFD0.Make"),
            ["camera.model"] = ValueField("camera.model", "FC-Test", "exif", "present", "ExifIFD0.Model"),
            ["camera.lens_model"] = ValueField("camera.lens_model", "Lens-Test", "exif", "present", "ExifSubIFD.LensModel"),
            ["camera.focal_length_mm"] = ValueField("camera.focal_length_mm", 24.0, "exif", "present", "ExifSubIFD.FocalLength"),
            ["position.latitude_deg"] = ValueField("position.latitude_deg", 29.0, "gps_exif", "present", "GPS.Latitude"),
            ["position.longitude_deg"] = ValueField("position.longitude_deg", 106.0, "gps_exif", "present", "GPS.Longitude")
        };
        return ImageMetadataCatalog.RequiredFieldNames
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => values.TryGetValue(name, out var value)
                ? value
                : new ImageMetadataCatalogField(name, null, "derived", "missing", "image-metadata.v1:missing"))
            .ToArray();
    }

    private static ImageProbeImageMetadataResult CompletedMetadataResult(
        IReadOnlyList<ImageMetadataCatalogField> fields)
    {
        var probeFields = fields.Select(field =>
        {
            if (field.FieldValueJson is null)
            {
                return new ImageProbeImageMetadataField(
                    field.FieldName,
                    field.SourceKind,
                    field.SourceDetail,
                    field.FieldState,
                    "none",
                    null,
                    null,
                    null,
                    null);
            }

            using var document = JsonDocument.Parse(field.FieldValueJson);
            var unit = field.FieldName == "camera.focal_length_mm"
                ? "mm"
                : field.FieldName.EndsWith("_deg", StringComparison.Ordinal)
                    ? "deg"
                    : field.FieldName.EndsWith("_m", StringComparison.Ordinal)
                        ? "m"
                        : null;
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.String => new ImageProbeImageMetadataField(
                    field.FieldName,
                    field.SourceKind,
                    field.SourceDetail,
                    field.FieldState,
                    "text",
                    document.RootElement.GetString(),
                    null,
                    null,
                    null),
                JsonValueKind.Number => new ImageProbeImageMetadataField(
                    field.FieldName,
                    field.SourceKind,
                    field.SourceDetail,
                    field.FieldState,
                    "number",
                    null,
                    document.RootElement.GetDouble(),
                    null,
                    unit),
                JsonValueKind.True or JsonValueKind.False => new ImageProbeImageMetadataField(
                    field.FieldName,
                    field.SourceKind,
                    field.SourceDetail,
                    field.FieldState,
                    "boolean",
                    null,
                    null,
                    document.RootElement.GetBoolean(),
                    null),
                _ => throw new AssertFailedException("Unsupported synthetic metadata value kind.")
            };
        }).ToArray();
        return new ImageProbeImageMetadataResult(
            ImageProbeProtocol.ImageMetadataV1,
            ImageProbeProtocol.ImageMetadataProfile,
            "completed",
            "normalized_image_frame",
            probeFields,
            [],
            new ImageProbeImageMetadataParserIdentity(
                ImageMetadataCatalog.ProductParser,
                ImageMetadataCatalog.ProductParserVersion,
                ImageMetadataCatalog.MetadataExtractorVersion,
                ImageMetadataCatalog.FieldMappingVersion,
                ImageMetadataCatalog.ConflictPolicyVersion),
            new ImageProbePrivacy(
                false,
                false,
                false,
                false,
                false,
                false,
                probeFields.Any(field =>
                    field.FieldName.StartsWith("position.", StringComparison.Ordinal) &&
                    field.FieldState is "present" or "conflict"),
                false));
    }

    private static ImageMetadataCatalogField ValueField(
        string fieldName,
        object value,
        string sourceKind,
        string fieldState,
        string sourceDetail) =>
        new(fieldName, JsonSerializer.Serialize(value), sourceKind, fieldState, sourceDetail);

    private static void ExecuteSql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task WaitForIdleAsync(ImageInspectionCoordinator coordinator)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!coordinator.IsIdle && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(coordinator.IsIdle, "The image inspection coordinator did not become idle.");
    }

    private static async Task WaitForIdleAsync(ImageMetadataCoordinator coordinator)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!coordinator.IsIdle && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(coordinator.IsIdle, "The image metadata coordinator did not become idle.");
    }

    private sealed class FixedProbe(ImageProbeCasImageResult result) : IImageCasProbeClient
    {
        public Task<ImageProbeCasImageResult> AnalyzeAsync(
            ContentAddressedObjectStore objectStore,
            PublishedObject sourceObject,
            string objectKind,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class ThrowingProbe : IImageCasProbeClient
    {
        public Task<ImageProbeCasImageResult> AnalyzeAsync(
            ContentAddressedObjectStore objectStore,
            PublishedObject sourceObject,
            string objectKind,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("Recovery after formal publication must not probe or extract the source again.");
    }

    private sealed class FixedMetadataProbe(ImageProbeImageMetadataResult result) : IImageMetadataProbeClient
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ImageProbeImageMetadataResult> AnalyzeAsync(
            ContentAddressedObjectStore objectStore,
            PublishedObject normalizedObject,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(result);
        }
    }

    private sealed class InspectionScope : IAsyncDisposable
    {
        private readonly string _root;

        private InspectionScope(
            string root,
            BusinessDatabase database,
            ContentAddressedObjectStore store,
            PublishedObject publishedSource)
        {
            _root = root;
            Database = database;
            Store = store;
            PublishedSource = publishedSource;
        }

        public const string ImportEntryId = "import-entry-inspection";
        public BusinessDatabase Database { get; }
        public ContentAddressedObjectStore Store { get; }
        public PublishedObject PublishedSource { get; }

        public static async Task<InspectionScope> CreateAsync(byte[] sourceBytes)
        {
            var root = Path.Combine(Path.GetTempPath(), $"qiongtu-image-inspection-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var database = new BusinessDatabase(Path.Combine(root, "qiongtu.db"));
            database.Initialize();
            var store = new ContentAddressedObjectStore(Path.Combine(root, "objects"));
            await using var input = new MemoryStream(sourceBytes, writable: false);
            var stage = await store.StageAsync(input);
            var published = await store.PublishAsync(stage);
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO projects(project_id,name,spatial_configuration_state,lifecycle_state,created_at_utc,updated_at_utc)
                VALUES('project-inspection','Project','pending','active','2026-08-31T00:00:00Z','2026-08-31T00:00:00Z');
                INSERT INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
                VALUES('dataset-inspection','project-inspection','Dataset','active','2026-08-31T00:00:00Z','2026-08-31T00:00:00Z');
                INSERT INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc)
                VALUES('dataset-version-inspection','dataset-inspection',1,'draft','dji_supported','not_run','2026-08-31T00:00:00Z');
                INSERT INTO file_objects(
                    file_object_id,object_kind,hash_algorithm,content_hash,byte_length,media_type,
                    object_key,storage_state,created_at_utc,available_at_utc)
                VALUES(
                    'source-object','source_image','sha256',$sha256,$byte_length,'image/jpeg',
                    $object_key,'available','2026-08-31T00:00:00Z','2026-08-31T00:00:00Z');
                INSERT INTO file_object_roles(file_object_id,object_role,created_at_utc)
                VALUES('source-object','source_image','2026-08-31T00:00:00Z');
                INSERT INTO image_import_sessions(
                    import_session_id,dataset_version_id,source_root_key,source_locator_manifest_id,status,
                    total_entry_count,available_entry_count,created_at_utc,updated_at_utc,completed_at_utc)
                VALUES(
                    'session-inspection','dataset-version-inspection',$root_key,'manifest-inspection','completed',
                    1,1,'2026-08-31T00:00:00Z','2026-08-31T00:00:00Z','2026-08-31T00:00:00Z');
                INSERT INTO image_import_entries(
                    import_entry_id,import_session_id,dataset_version_id,source_entry_key,display_name,sort_index,
                    byte_length_snapshot,status,stage_receipt_id,stage_receipt_sha256,stage_receipt_byte_length,
                    stage_receipt_created_at_utc,expected_content_hash,expected_byte_length,expected_object_key,
                    file_object_id,created_at_utc,updated_at_utc,terminal_at_utc)
                VALUES(
                    'import-entry-inspection','session-inspection','dataset-version-inspection',$entry_key,'DJI_TEST.JPG',0,
                    $byte_length,'available','source-stage',$sha256,$byte_length,
                    '2026-08-31T00:00:00Z',$sha256,$byte_length,$object_key,
                    'source-object','2026-08-31T00:00:00Z','2026-08-31T00:00:00Z','2026-08-31T00:00:00Z');
                """;
            command.Parameters.AddWithValue("$sha256", published.Sha256);
            command.Parameters.AddWithValue("$byte_length", published.ByteLength);
            command.Parameters.AddWithValue("$object_key", published.ObjectKey);
            command.Parameters.AddWithValue("$root_key", new string('a', 64));
            command.Parameters.AddWithValue("$entry_key", new string('b', 64));
            command.ExecuteNonQuery();
            return new InspectionScope(root, database, store, published);
        }

        public ImageInspectionCoordinator CreateCoordinator(IImageCasProbeClient probe) =>
            new(new ImageFrameCatalog(Database), Store, probe);

        public string AddCanonicalImportEntry(string suffix)
        {
            var sessionId = $"session-inspection-{suffix}";
            var entryId = $"import-entry-inspection-{suffix}";
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO image_import_sessions(
                    import_session_id,dataset_version_id,source_root_key,source_locator_manifest_id,status,
                    total_entry_count,available_entry_count,created_at_utc,updated_at_utc,completed_at_utc)
                VALUES(
                    $session_id,'dataset-version-inspection',$root_key,$manifest_id,'completed',
                    1,1,'2026-08-31T00:00:01Z','2026-08-31T00:00:01Z','2026-08-31T00:00:01Z');
                INSERT INTO image_import_entries(
                    import_entry_id,import_session_id,dataset_version_id,source_entry_key,display_name,sort_index,
                    byte_length_snapshot,status,stage_receipt_id,stage_receipt_sha256,stage_receipt_byte_length,
                    stage_receipt_created_at_utc,expected_content_hash,expected_byte_length,expected_object_key,
                    file_object_id,created_at_utc,updated_at_utc,terminal_at_utc)
                VALUES(
                    $entry_id,$session_id,'dataset-version-inspection',$entry_key,'DJI_SECOND.JPG',0,
                    $byte_length,'available',$stage_id,$sha256,$byte_length,
                    '2026-08-31T00:00:01Z',$sha256,$byte_length,$object_key,
                    'source-object','2026-08-31T00:00:01Z','2026-08-31T00:00:01Z','2026-08-31T00:00:01Z');
                """;
            command.Parameters.AddWithValue("$session_id", sessionId);
            command.Parameters.AddWithValue("$entry_id", entryId);
            command.Parameters.AddWithValue("$root_key", Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sessionId))).ToLowerInvariant());
            command.Parameters.AddWithValue("$entry_key", Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entryId))).ToLowerInvariant());
            command.Parameters.AddWithValue("$manifest_id", $"manifest-{suffix}");
            command.Parameters.AddWithValue("$stage_id", $"stage-{suffix}");
            command.Parameters.AddWithValue("$sha256", PublishedSource.Sha256);
            command.Parameters.AddWithValue("$byte_length", PublishedSource.ByteLength);
            command.Parameters.AddWithValue("$object_key", PublishedSource.ObjectKey);
            command.ExecuteNonQuery();
            return entryId;
        }

        public T Scalar<T>(string sql)
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
