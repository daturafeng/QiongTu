using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class OwnerPrivateImageCompatibilityTests
{
    private const string OwnerSampleEnvironmentKey = "QIONGTU_OWNER_SAMPLE";
    private const string OwnerManifestEnvironmentKey = "QIONGTU_OWNER_SAMPLE_MANIFEST";
    private const string OwnerAcceptance33DEnvironmentKey = "QIONGTU_OWNER_SAMPLE_ACCEPT_33D";
    private const string OwnerAcceptance33EEnvironmentKey = "QIONGTU_OWNER_SAMPLE_ACCEPT_33E";
    private const int MaximumTraversalEntries = 4096;
    private const int MaximumCandidateAttempts = 64;
    private const int MaximumMpfHeaderScanBytes = 16 * 1024 * 1024;
    private const long MaximumManifestImageryBytes = 8L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> CandidateExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".mpo"
    };

    [TestMethod]
    public async Task OwnerPrivateSample33DCompatibilityRequiresRedactedOptInAndTemporaryStore()
    {
        var binding = RequirePrivateBindingOrInconclusive(
            OwnerAcceptance33DEnvironmentKey,
            "Owner-private 3.3d compatibility is skipped until all redacted opt-in bindings are present.");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"qiongtu-owner-private-33d-{Guid.NewGuid():N}");
        try
        {
            _ = ValidatePrivateManifestGate(binding.ManifestPath);
            if (!Directory.Exists(binding.SourceRoot) || !File.Exists(binding.ManifestPath))
            {
                Assert.Fail("The owner-private compatibility binding is unavailable.");
            }

            var store = new ContentAddressedObjectStore(Path.Combine(tempRoot, "objects"));
            var options = new ImageCasProbeOptions(Timeout: TimeSpan.FromSeconds(60));
            var casProbe = new IsolatedImageCasProbeClient(options, CreateDevelopmentProbeStartInfo);
            var metadataProbe = new IsolatedImageMetadataProbeClient(options, CreateDevelopmentProbeStartInfo);

            var result = await AnalyzeBoundedRepresentativesAsync(
                binding.SourceRoot,
                store,
                casProbe,
                metadataProbe,
                CancellationToken.None);

            if (!result.MpoValidated)
            {
                Assert.Fail(result.MpfMarkerObserved
                    ? result.MpoFailureCode ?? "owner_private_mpo_probe_not_completed"
                    : "owner_private_mpf_marker_not_found");
            }

            if (!result.MetadataValidated)
            {
                Assert.Fail("owner_private_metadata_probe_not_completed");
            }
        }
        finally
        {
            DeleteDirectoryOrFail(tempRoot);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task OwnerPrivateSample33EVendorPayloadDispositionRequiresIndependentOptIn()
    {
        var binding = RequirePrivateBindingOrInconclusive(
            OwnerAcceptance33EEnvironmentKey,
            "Owner-private 3.3e disposition is skipped until all redacted opt-in bindings are present.");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"qiongtu-owner-private-33e-{Guid.NewGuid():N}");
        try
        {
            var manifestIdentity = ValidatePrivateManifestGate(binding.ManifestPath);
            if (!Directory.Exists(binding.SourceRoot) || !File.Exists(binding.ManifestPath))
            {
                Assert.Fail("The owner-private compatibility binding is unavailable.");
            }

            await AssertPrivateSourceMatchesManifestAsync(
                binding.SourceRoot,
                manifestIdentity,
                CancellationToken.None);
            var candidate = await FindBoundedMpfCandidateAsync(binding.SourceRoot, CancellationToken.None);
            if (candidate is null)
            {
                Assert.Fail("owner_private_mpf_marker_not_found");
            }

            var store = new ContentAddressedObjectStore(Path.Combine(tempRoot, "objects"));
            var published = await PublishPrivateCandidateAsync(store, candidate.Path, CancellationToken.None);
            candidate.State.AssertUnchanged(candidate.Path);
            var database = CreatePrivateInspectionDatabase(tempRoot, published);
            var probe = new IsolatedImageCasProbeClient(
                new ImageCasProbeOptions(Timeout: TimeSpan.FromSeconds(60)),
                CreateDevelopmentProbeStartInfo);
            await using var coordinator = new ImageInspectionCoordinator(
                new ImageFrameCatalog(database),
                store,
                probe);

            await coordinator.EnqueueImportEntryAsync("owner-private-entry");
            await WaitForIdleAsync(coordinator);
            candidate.State.AssertUnchanged(candidate.Path);

            using var connection = database.OpenConnection();
            if (Scalar<string>(connection, "SELECT status FROM image_inspection_runs;") != "blocked")
            {
                Assert.Fail("owner_private_vendor_payload_not_blocked");
            }

            if (Scalar<string>(connection, "SELECT failure_code FROM image_inspection_runs;") !=
                "mpf_unreferenced_trailing_data")
            {
                Assert.Fail("owner_private_vendor_payload_reason_mismatch");
            }

            if (Scalar<string>(connection, "SELECT support_disposition FROM image_inspection_runs;") !=
                ImageInspectionSupportPolicy.UnsupportedVendorPayload)
            {
                Assert.Fail("owner_private_vendor_payload_disposition_mismatch");
            }

            if (Scalar<string>(connection, "SELECT support_policy_version FROM image_inspection_runs;") !=
                ImageInspectionSupportPolicy.Version)
            {
                Assert.Fail("owner_private_vendor_payload_policy_mismatch");
            }

            if (Scalar<long>(connection, "SELECT count(*) FROM images;") != 0 ||
                Scalar<long>(connection, "SELECT count(*) FROM image_frames;") != 0 ||
                Scalar<long>(connection, "SELECT count(*) FROM file_object_roles WHERE object_role='normalized_image_frame';") != 0 ||
                Scalar<string>(connection, "SELECT status FROM image_import_entries;") != "available")
            {
                Assert.Fail("owner_private_vendor_payload_manifest_pollution");
            }
        }
        finally
        {
            DeleteDirectoryOrFail(tempRoot);
        }
    }

    private static OwnerPrivateBinding RequirePrivateBindingOrInconclusive(
        string acceptanceEnvironmentKey,
        string skippedMessage)
    {
        var sourceRoot = Environment.GetEnvironmentVariable(OwnerSampleEnvironmentKey);
        var manifestPath = Environment.GetEnvironmentVariable(OwnerManifestEnvironmentKey);
        var acceptance = Environment.GetEnvironmentVariable(acceptanceEnvironmentKey);
        if (string.IsNullOrWhiteSpace(sourceRoot) ||
            string.IsNullOrWhiteSpace(manifestPath) ||
            !string.Equals(acceptance, "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive(skippedMessage);
        }

        try
        {
            return new OwnerPrivateBinding(
                Path.GetFullPath(sourceRoot),
                Path.GetFullPath(manifestPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Assert.Fail("The owner-private compatibility binding is invalid.");
            throw;
        }
    }

    private static PrivateManifestIdentity ValidatePrivateManifestGate(string manifestPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            var root = document.RootElement;
            var imageCount = 0;
            long imageBytes = 0;
            string? fingerprintValue = null;
            if (!root.TryGetProperty("inventory_schema_version", out var schemaVersion) ||
                string.IsNullOrWhiteSpace(schemaVersion.GetString()) ||
                !root.TryGetProperty("source_id", out var sourceId) ||
                string.IsNullOrWhiteSpace(sourceId.GetString()) ||
                !root.TryGetProperty("source_policy", out var policy) ||
                !policy.TryGetProperty("mode", out var mode) ||
                mode.GetString() is not ("read-only" or "read_only") ||
                !IsFalse(policy, "source_paths_emitted") ||
                !IsFalse(policy, "file_names_emitted") ||
                !IsFalse(policy, "absolute_coordinates_emitted") ||
                !IsFalse(policy, "serial_numbers_emitted") ||
                !IsFalse(policy, "capture_timestamps_emitted") ||
                !policy.TryGetProperty("source_unchanged_during_scan", out var unchanged) ||
                unchanged.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("imagery", out var imagery) ||
                !imagery.TryGetProperty("count", out var count) ||
                !count.TryGetInt32(out imageCount) ||
                imageCount <= 0 ||
                imageCount > MaximumTraversalEntries ||
                !imagery.TryGetProperty("total_bytes", out var totalBytes) ||
                !totalBytes.TryGetInt64(out imageBytes) ||
                imageBytes <= 0 ||
                imageBytes > MaximumManifestImageryBytes ||
                !imagery.TryGetProperty("fingerprint", out var fingerprint) ||
                (fingerprintValue = fingerprint.GetString()) is not { Length: 71 } ||
                !fingerprintValue.StartsWith("sha256:", StringComparison.Ordinal) ||
                !fingerprintValue[7..].All(Uri.IsHexDigit))
            {
                Assert.Fail("The owner-private manifest does not satisfy the redacted read-only gate.");
            }

            return new PrivateManifestIdentity(imageCount, imageBytes, fingerprintValue!);
        }
        catch (JsonException)
        {
            Assert.Fail("The owner-private manifest is not valid redacted JSON.");
        }
        catch (IOException)
        {
            Assert.Fail("The owner-private manifest could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Fail("The owner-private manifest could not be read.");
        }

        throw new InvalidOperationException("The owner-private manifest gate did not return an identity.");
    }

    private static bool IsFalse(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.False;

    private static async Task AssertPrivateSourceMatchesManifestAsync(
        string sourceRoot,
        PrivateManifestIdentity expected,
        CancellationToken cancellationToken)
    {
        var candidates = EnumerateCandidatePaths(sourceRoot)
            .Where(path => Path.GetExtension(path) is { } extension &&
                (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)))
            .Select(path => new PrivateManifestCandidate(
                path,
                Path.GetRelativePath(sourceRoot, path).Replace('\\', '/')))
            .OrderBy(candidate => candidate.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length != expected.ImageCount || candidates.Length > MaximumTraversalEntries)
        {
            Assert.Fail("owner_private_manifest_identity_mismatch");
        }

        long totalBytes = 0;
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = SourceFileState.Capture(candidate.Path);
            totalBytes = checked(totalBytes + before.ByteLength);
            if (totalBytes > MaximumManifestImageryBytes)
            {
                Assert.Fail("owner_private_manifest_identity_limit_exceeded");
            }

            using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var stream = new FileStream(
                candidate.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    fileHash.AppendData(buffer, 0, read);
                }
            }

            before.AssertUnchanged(candidate.Path);
            var contentHash = Convert.ToHexString(fileHash.GetHashAndReset()).ToLowerInvariant();
            AppendUtf8(aggregate, candidate.RelativePath);
            AppendUtf8(aggregate, "\0");
            AppendUtf8(aggregate, before.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendUtf8(aggregate, "\0");
            AppendUtf8(aggregate, contentHash);
            AppendUtf8(aggregate, "\n");
        }

        var actualFingerprint = "sha256:" + Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
        if (totalBytes != expected.TotalBytes ||
            !string.Equals(actualFingerprint, expected.Fingerprint, StringComparison.Ordinal))
        {
            Assert.Fail("owner_private_manifest_identity_mismatch");
        }
    }

    private static void AppendUtf8(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static async Task<PrivateCompatibilityResult> AnalyzeBoundedRepresentativesAsync(
        string sourceRoot,
        ContentAddressedObjectStore store,
        IsolatedImageCasProbeClient casProbe,
        IsolatedImageMetadataProbeClient metadataProbe,
        CancellationToken cancellationToken)
    {
        var mpoValidated = false;
        var metadataValidated = false;
        var mpfMarkerObserved = false;
        string? mpoFailureCode = null;
        var candidateAttempts = 0;

        foreach (var candidate in EnumerateCandidatePaths(sourceRoot))
        {
            if (mpoValidated && metadataValidated)
            {
                break;
            }

            var sourceState = SourceFileState.Capture(candidate);
            var hasMpfMarker = await ContainsMpfMarkerAsync(candidate, cancellationToken);
            sourceState.AssertUnchanged(candidate);
            mpfMarkerObserved |= hasMpfMarker;
            if ((metadataValidated && !hasMpfMarker) || candidateAttempts >= MaximumCandidateAttempts)
            {
                continue;
            }

            candidateAttempts++;
            var published = await PublishPrivateCandidateAsync(store, candidate, cancellationToken);
            sourceState.AssertUnchanged(candidate);

            var image = await casProbe.AnalyzeAsync(store, published, "source_image", cancellationToken);
            AssertCasPrivacy(image);
            if (image.Status != "completed")
            {
                if (hasMpfMarker)
                {
                    mpoFailureCode ??= image.ReasonCodes.FirstOrDefault() ?? "owner_private_mpo_probe_blocked";
                }

                continue;
            }

            Assert.IsTrue(image.Container is "jpeg" or "mpo", "The owner-private container scenario returned an unsupported category.");
            if (image.Container == "mpo")
            {
                Assert.IsTrue(image.Frames.Any(frame => frame.FrameKind == "mp_primary_image"));
                Assert.IsTrue(image.Frames.Any(frame => frame.FrameKind == "mp_auxiliary_image"));
                mpoValidated = true;
            }
            else if (hasMpfMarker)
            {
                mpoFailureCode ??= "owner_private_mpf_classification_mismatch";
            }

            if (!metadataValidated)
            {
                var normalized = await PublishNormalizedPrimaryAsync(store, published, image, cancellationToken);
                var metadata = await metadataProbe.AnalyzeAsync(store, normalized, cancellationToken);
                AssertMetadataPrivacy(metadata);
                AssertMetadataCategories(metadata);
                metadataValidated = true;
            }
        }

        return new PrivateCompatibilityResult(
            mpoValidated,
            metadataValidated,
            mpfMarkerObserved,
            mpoFailureCode);
    }

    private static async Task<bool> ContainsMpfMarkerAsync(string candidate, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                candidate,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            var matched = 0;
            var scanned = 0;
            byte[] marker = [(byte)'M', (byte)'P', (byte)'F', 0];
            while (scanned < MaximumMpfHeaderScanBytes)
            {
                var requested = Math.Min(buffer.Length, MaximumMpfHeaderScanBytes - scanned);
                var read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                if (read == 0)
                {
                    return false;
                }

                scanned += read;
                for (var index = 0; index < read; index++)
                {
                    matched = buffer[index] == marker[matched]
                        ? matched + 1
                        : buffer[index] == marker[0] ? 1 : 0;
                    if (matched == marker.Length)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Fail("owner_private_header_scan_failed");
            throw;
        }
    }

    private static async Task<PrivateCandidate?> FindBoundedMpfCandidateAsync(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        var attempts = 0;
        foreach (var path in EnumerateCandidatePaths(sourceRoot))
        {
            if (attempts++ >= MaximumCandidateAttempts)
            {
                break;
            }

            var state = SourceFileState.Capture(path);
            var hasMarker = await ContainsMpfMarkerAsync(path, cancellationToken);
            state.AssertUnchanged(path);
            if (hasMarker)
            {
                return new PrivateCandidate(path, state);
            }
        }

        return null;
    }

    private static BusinessDatabase CreatePrivateInspectionDatabase(
        string tempRoot,
        PublishedObject published)
    {
        var database = new BusinessDatabase(Path.Combine(tempRoot, "qiongtu.db"));
        database.Initialize();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO projects(project_id,name,spatial_configuration_state,lifecycle_state,created_at_utc,updated_at_utc)
            VALUES('owner-private-project','Private Gate','pending','active','2026-08-31T00:00:00Z','2026-08-31T00:00:00Z');
            INSERT INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
            VALUES('owner-private-dataset','owner-private-project','Private Gate','active','2026-08-31T00:00:00Z','2026-08-31T00:00:00Z');
            INSERT INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc)
            VALUES('owner-private-version','owner-private-dataset',1,'draft','dji_supported','not_run','2026-08-31T00:00:00Z');
            INSERT INTO file_objects(
                file_object_id,object_kind,hash_algorithm,content_hash,byte_length,media_type,
                object_key,storage_state,created_at_utc,available_at_utc)
            VALUES(
                'owner-private-source','source_image','sha256',$sha256,$byte_length,'image/jpeg',
                $object_key,'available','2026-08-31T00:00:00Z','2026-08-31T00:00:00Z');
            INSERT INTO file_object_roles(file_object_id,object_role,created_at_utc)
            VALUES('owner-private-source','source_image','2026-08-31T00:00:00Z');
            INSERT INTO image_import_sessions(
                import_session_id,dataset_version_id,source_root_key,source_locator_manifest_id,status,
                total_entry_count,available_entry_count,created_at_utc,updated_at_utc,completed_at_utc)
            VALUES(
                'owner-private-session','owner-private-version',$root_key,'owner-private-manifest','completed',
                1,1,'2026-08-31T00:00:00Z','2026-08-31T00:00:00Z','2026-08-31T00:00:00Z');
            INSERT INTO image_import_entries(
                import_entry_id,import_session_id,dataset_version_id,source_entry_key,display_name,sort_index,
                byte_length_snapshot,status,stage_receipt_id,stage_receipt_sha256,stage_receipt_byte_length,
                stage_receipt_created_at_utc,expected_content_hash,expected_byte_length,expected_object_key,
                file_object_id,created_at_utc,updated_at_utc,terminal_at_utc)
            VALUES(
                'owner-private-entry','owner-private-session','owner-private-version',$entry_key,'OWNER_PRIVATE.JPG',0,
                $byte_length,'available','owner-private-stage',$sha256,$byte_length,
                '2026-08-31T00:00:00Z',$sha256,$byte_length,$object_key,
                'owner-private-source','2026-08-31T00:00:00Z','2026-08-31T00:00:00Z','2026-08-31T00:00:00Z');
            """;
        command.Parameters.AddWithValue("$sha256", published.Sha256);
        command.Parameters.AddWithValue("$byte_length", published.ByteLength);
        command.Parameters.AddWithValue("$object_key", published.ObjectKey);
        command.Parameters.AddWithValue("$root_key", new string('a', 64));
        command.Parameters.AddWithValue("$entry_key", new string('b', 64));
        command.ExecuteNonQuery();
        return database;
    }

    private static async Task WaitForIdleAsync(ImageInspectionCoordinator coordinator)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (!coordinator.IsIdle && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        if (!coordinator.IsIdle)
        {
            Assert.Fail("owner_private_image_inspection_timeout");
        }
    }

    private static T Scalar<T>(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(
            command.ExecuteScalar()!,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> EnumerateCandidatePaths(string sourceRoot)
    {
        var root = new DirectoryInfo(sourceRoot);
        if (!root.Exists)
        {
            yield break;
        }

        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        var visited = 0;
        while (pending.Count > 0 && visited < MaximumTraversalEntries)
        {
            var current = pending.Pop();
            if (IsReparsePoint(current.Attributes))
            {
                continue;
            }

            FileSystemInfo[] children;
            try
            {
                children = current
                    .EnumerateFileSystemInfos()
                    .Take(MaximumTraversalEntries - visited + 1)
                    .ToArray();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                visited++;
                if (visited > MaximumTraversalEntries)
                {
                    yield break;
                }

                if (IsReparsePoint(child.Attributes))
                {
                    continue;
                }

                if (child is DirectoryInfo directory)
                {
                    pending.Push(directory);
                }
                else if (child is FileInfo file && CandidateExtensions.Contains(file.Extension))
                {
                    yield return file.FullName;
                }
            }
        }
    }

    private static bool IsReparsePoint(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0;

    private static async Task<PublishedObject> PublishPrivateCandidateAsync(
        ContentAddressedObjectStore store,
        string candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                candidate,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stage = await store.StageAsync(stream, cancellationToken: cancellationToken);
            return await store.PublishAsync(stage, cancellationToken);
        }
        catch (ObjectStoreException)
        {
            Assert.Fail("The owner-private candidate could not be copied into the temporary compatibility store.");
            throw;
        }
        catch (IOException)
        {
            Assert.Fail("The owner-private candidate could not be copied into the temporary compatibility store.");
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Fail("The owner-private candidate could not be copied into the temporary compatibility store.");
            throw;
        }
    }

    private static async Task<PublishedObject> PublishNormalizedPrimaryAsync(
        ContentAddressedObjectStore store,
        PublishedObject source,
        ImageProbeCasImageResult image,
        CancellationToken cancellationToken)
    {
        if (image.Container == "jpeg")
        {
            return source;
        }

        var primary = ImageFrameCatalog.SelectPrimaryFrame(image);
        if (primary is null || primary.ByteLength <= 0)
        {
            Assert.Fail("The owner-private primary frame scenario did not produce an extractable result.");
        }

        var stage = await store.StagePublishedRangeAsync(
            source,
            primary.ByteOffset,
            primary.ByteLength,
            cancellationToken);
        try
        {
            var normalized = await store.PublishAsync(stage, cancellationToken);
            await AssertRangeByteExactAsync(store, source, normalized, primary, cancellationToken);
            return normalized;
        }
        catch (ObjectStoreException)
        {
            Assert.Fail("The owner-private primary frame scenario could not publish a temporary normalized object.");
            throw;
        }
    }

    private static async Task AssertRangeByteExactAsync(
        ContentAddressedObjectStore store,
        PublishedObject source,
        PublishedObject normalized,
        ImageProbeCasImageFrame primary,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = await store.OpenPublishedReadAsync(source, cancellationToken);
        await using var normalizedStream = await store.OpenPublishedReadAsync(normalized, cancellationToken);
        sourceStream.Seek(primary.ByteOffset, SeekOrigin.Begin);
        var sourceBuffer = new byte[128 * 1024];
        var normalizedBuffer = new byte[128 * 1024];
        long compared = 0;
        while (compared < primary.ByteLength)
        {
            var requested = (int)Math.Min(sourceBuffer.Length, primary.ByteLength - compared);
            var sourceRead = await sourceStream.ReadAsync(sourceBuffer.AsMemory(0, requested), cancellationToken);
            var normalizedRead = await normalizedStream.ReadAsync(normalizedBuffer.AsMemory(0, requested), cancellationToken);
            if (sourceRead != requested || normalizedRead != requested ||
                !sourceBuffer.AsSpan(0, requested).SequenceEqual(normalizedBuffer.AsSpan(0, requested)))
            {
                Assert.Fail("The owner-private normalized frame was not byte-exact.");
            }

            compared += requested;
        }

        if (normalizedStream.ReadByte() != -1)
        {
            Assert.Fail("The owner-private normalized frame length was not byte-exact.");
        }
    }

    private static void AssertCasPrivacy(ImageProbeCasImageResult image)
    {
        Assert.IsFalse(image.Privacy.PathsIncluded);
        Assert.IsFalse(image.Privacy.LocatorsIncluded);
        Assert.IsFalse(image.Privacy.ContentHashesIncluded);
        Assert.IsFalse(image.Privacy.ObjectKeysIncluded);
        Assert.IsFalse(image.Privacy.RawMetadataIncluded);
        Assert.IsFalse(image.Privacy.SerialNumbersIncluded);
        Assert.IsFalse(image.Privacy.CoordinatesIncluded);
        Assert.IsFalse(image.Privacy.OwnerSampleStatisticsIncluded);
    }

    private static void AssertMetadataPrivacy(ImageProbeImageMetadataResult metadata)
    {
        Assert.AreEqual("completed", metadata.Status);
        Assert.IsFalse(metadata.Privacy.PathsIncluded);
        Assert.IsFalse(metadata.Privacy.LocatorsIncluded);
        Assert.IsFalse(metadata.Privacy.ContentHashesIncluded);
        Assert.IsFalse(metadata.Privacy.ObjectKeysIncluded);
        Assert.IsFalse(metadata.Privacy.RawMetadataIncluded);
        Assert.IsFalse(metadata.Privacy.SerialNumbersIncluded);
        Assert.IsFalse(metadata.Privacy.OwnerSampleStatisticsIncluded);
    }

    private static void AssertMetadataCategories(ImageProbeImageMetadataResult metadata)
    {
        Assert.AreEqual(ImageProbeProtocol.ImageMetadataV1, metadata.SchemaVersion);
        Assert.AreEqual(ImageProbeProtocol.ImageMetadataProfile, metadata.Profile);
        Assert.AreEqual(ImageMetadataCatalog.ProductParser, metadata.Parser.ProductParser);
        Assert.AreEqual(ImageMetadataCatalog.ProductParserVersion, metadata.Parser.ProductParserVersion);
        Assert.AreEqual(ImageMetadataCatalog.MetadataExtractorVersion, StripBuildMetadata(metadata.Parser.MetadataExtractorVersion));
        Assert.AreEqual(ImageMetadataCatalog.FieldMappingVersion, metadata.Parser.FieldMappingVersion);
        Assert.AreEqual(ImageMetadataCatalog.ConflictPolicyVersion, metadata.Parser.ConflictPolicyVersion);
        Assert.IsTrue(metadata.Fields.All(field =>
            ImageMetadataCatalog.RequiredFieldNames.Contains(field.FieldName) &&
            field.SourceKind is "exif" or "gps_exif" or "dji_xmp" or "derived" &&
            field.FieldState is "present" or "missing" or "conflict" or "abnormal" or "not_assessable"));
    }

    private static string StripBuildMetadata(string version)
    {
        var index = version.IndexOf('+', StringComparison.Ordinal);
        return index >= 0 ? version[..index] : version;
    }

    private static ProcessStartInfo CreateDevelopmentProbeStartInfo()
    {
        var executablePath = Path.Combine(
            FindRepositoryRoot(),
            "services",
            "image-probe",
            "src",
            "QiongTu.ImageProbe",
            "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net10.0",
            "win-x64",
            "QiongTu.ImageProbe.exe");
        var startInfo = new ProcessStartInfo { FileName = executablePath };
        startInfo.ArgumentList.Add(ImageProbeProtocol.StdioArgument);
        return startInfo;
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        Assert.Fail("The repository root could not be resolved.");
        throw new InvalidOperationException();
    }

    private static void DeleteDirectoryOrFail(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Fail("The owner-private compatibility temporary store could not be cleaned up.");
        }
    }

    private sealed record OwnerPrivateBinding(string SourceRoot, string ManifestPath);

    private sealed record PrivateManifestIdentity(int ImageCount, long TotalBytes, string Fingerprint);

    private sealed record PrivateManifestCandidate(string Path, string RelativePath);

    private sealed record PrivateCandidate(string Path, SourceFileState State);

    private sealed record PrivateCompatibilityResult(
        bool MpoValidated,
        bool MetadataValidated,
        bool MpfMarkerObserved,
        string? MpoFailureCode);

    private sealed record SourceFileState(long ByteLength, DateTime LastWriteTimeUtc, FileAttributes Attributes)
    {
        public static SourceFileState Capture(string path)
        {
            var info = new FileInfo(path);
            return new SourceFileState(info.Length, info.LastWriteTimeUtc, info.Attributes);
        }

        public void AssertUnchanged(string path)
        {
            var current = Capture(path);
            if (ByteLength != current.ByteLength ||
                LastWriteTimeUtc != current.LastWriteTimeUtc ||
                Attributes != current.Attributes)
            {
                Assert.Fail("The owner-private candidate changed during the compatibility check.");
            }
        }
    }
}
