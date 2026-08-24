using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace QiongTu.Control;

public sealed record ObjectStageReceipt(
    string StageId,
    string Sha256,
    long ByteLength,
    DateTimeOffset CreatedAtUtc);

public sealed record PublishedObject(
    string Sha256,
    long ByteLength,
    string ObjectKey,
    bool Deduplicated);

public sealed record QuarantinedObject(string QuarantineId, string Code, string? StageId);

public sealed record StagingRecoveryResult(
    IReadOnlyList<ObjectStageReceipt> Recoverable,
    IReadOnlyList<QuarantinedObject> Quarantined);

public sealed class ObjectStoreException : IOException
{
    public ObjectStoreException(
        string code,
        string message,
        string? stageId = null,
        string? quarantineId = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        StageId = stageId;
        QuarantineId = quarantineId;
    }

    public string Code { get; }

    public string? StageId { get; }

    public string? QuarantineId { get; }
}

public sealed class ContentAddressedObjectStore
{
    private const string ManifestSchema = "qiongtu.object-stage.v1";
    private const string QuarantineSchema = "qiongtu.object-quarantine.v1";
    private const int BufferSize = 128 * 1024;
    private const int MaximumManifestBytes = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public ContentAddressedObjectStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        StagingDirectory = Path.Combine(RootDirectory, "staging");
        QuarantineDirectory = Path.Combine(RootDirectory, "quarantine");
        PublishedDirectory = Path.Combine(RootDirectory, "published");

        EnsureSafeDirectory(RootDirectory);
        EnsureSafeDirectory(StagingDirectory);
        EnsureSafeDirectory(QuarantineDirectory);
        EnsureSafeDirectory(PublishedDirectory);
    }

    public string RootDirectory { get; }

    public string StagingDirectory { get; }

    public string QuarantineDirectory { get; }

    public string PublishedDirectory { get; }

    public async Task<ObjectStageReceipt> StageFileAsync(
        string sourcePath,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        await using var source = new FileStream(
            Path.GetFullPath(sourcePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await StageAsync(source, expectedSha256, cancellationToken);
    }

    public async Task<ObjectStageReceipt> StageAsync(
        Stream source,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        var expected = expectedSha256 is null ? null : NormalizeSha256(expectedSha256);
        var stageId = Guid.NewGuid().ToString("N");
        var stageDirectory = GetStageDirectory(stageId);
        Directory.CreateDirectory(stageDirectory);
        EnsurePathHasNoReparsePoint(StagingDirectory, stageDirectory);
        var payloadPath = Path.Combine(stageDirectory, "payload");
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long byteLength = 0;
            await using (var destination = new FileStream(
                             payloadPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    byteLength = checked(byteLength + read);
                }

                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }

            var actualSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var createdAtUtc = DateTimeOffset.UtcNow;
            var manifest = new StageManifest(ManifestSchema, stageId, actualSha256, byteLength, createdAtUtc);
            await WriteJsonAtomicallyAsync(Path.Combine(stageDirectory, "stage.json"), manifest, cancellationToken);
            var receipt = ToReceipt(manifest);

            if (expected is not null && !string.Equals(expected, actualSha256, StringComparison.Ordinal))
            {
                var quarantineId = await QuarantineStageDirectoryAsync(stageId, "checksum_mismatch", CancellationToken.None);
                throw new ObjectStoreException(
                    "object_checksum_mismatch",
                    "The staged object did not match the expected SHA-256 and was quarantined.",
                    stageId,
                    quarantineId);
            }

            return receipt;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteStageDirectory(stageDirectory);
            throw;
        }
        catch (ObjectStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            string? quarantineId = null;
            if (Directory.Exists(stageDirectory))
            {
                quarantineId = await TryQuarantineStageDirectoryAsync(stageId, "write_failed");
            }

            throw new ObjectStoreException(
                "object_stage_failed",
                "The object could not be staged and no formal object was created.",
                stageId,
                quarantineId,
                exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<PublishedObject> PublishAsync(
        ObjectStageReceipt stage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        StageManifest manifest;
        try
        {
            manifest = await ValidateStageAsync(stage, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectStoreException exception) when (exception.Code != "object_stage_missing")
        {
            var quarantineId = await TryQuarantineStageDirectoryAsync(stage.StageId, "stage_integrity_failed");
            throw new ObjectStoreException(
                exception.Code,
                "The staged object failed validation and was quarantined.",
                stage.StageId,
                quarantineId,
                exception);
        }

        var objectKey = CreateObjectKey(manifest.Sha256);
        var targetPath = ResolvePublishedPath(objectKey);
        var targetDirectory = Path.GetDirectoryName(targetPath)!;
        EnsureSafeDirectoryWithin(PublishedDirectory, targetDirectory);

        if (File.Exists(targetPath))
        {
            await RequireMatchingPublishedFileAsync(stage.StageId, targetPath, manifest, cancellationToken);

            TryDeleteStageDirectory(GetStageDirectory(stage.StageId));
            return new PublishedObject(manifest.Sha256, manifest.ByteLength, objectKey, Deduplicated: true);
        }

        var payloadPath = Path.Combine(GetStageDirectory(stage.StageId), "payload");
        try
        {
            File.Move(payloadPath, targetPath, overwrite: false);
        }
        catch (IOException exception) when (File.Exists(targetPath))
        {
            await RequireMatchingPublishedFileAsync(stage.StageId, targetPath, manifest, cancellationToken, exception);

            TryDeleteStageDirectory(GetStageDirectory(stage.StageId));
            return new PublishedObject(manifest.Sha256, manifest.ByteLength, objectKey, Deduplicated: true);
        }

        TryDeleteStageDirectory(GetStageDirectory(stage.StageId));
        return new PublishedObject(manifest.Sha256, manifest.ByteLength, objectKey, Deduplicated: false);
    }

    public async Task<StagingRecoveryResult> RecoverStagedAsync(CancellationToken cancellationToken = default)
    {
        var recoverable = new List<ObjectStageReceipt>();
        var quarantined = new List<QuarantinedObject>();
        foreach (var stageDirectory in Directory.EnumerateDirectories(StagingDirectory).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(stageDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ObjectStoreException(
                    "object_store_reparse_detected",
                    "A reparse point was detected in the object staging namespace.");
            }

            var stageId = Path.GetFileName(stageDirectory);
            if (!IsStageId(stageId))
            {
                var quarantineId = await QuarantineArbitraryStageDirectoryAsync(stageDirectory, "invalid_stage_id", cancellationToken);
                quarantined.Add(new QuarantinedObject(quarantineId, "invalid_stage_id", null));
                continue;
            }

            try
            {
                var manifest = await ValidateStageByIdAsync(stageId, cancellationToken);
                recoverable.Add(ToReceipt(manifest));
            }
            catch (ObjectStoreException)
            {
                var quarantineId = await QuarantineStageDirectoryAsync(stageId, "recovery_integrity_failed", CancellationToken.None);
                quarantined.Add(new QuarantinedObject(quarantineId, "recovery_integrity_failed", stageId));
            }
        }

        return new StagingRecoveryResult(recoverable, quarantined);
    }

    public async Task<QuarantinedObject> AbandonAsync(
        ObjectStageReceipt stage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ValidateStageId(stage.StageId);
        var quarantineId = await QuarantineStageDirectoryAsync(stage.StageId, "abandoned", cancellationToken);
        return new QuarantinedObject(quarantineId, "abandoned", stage.StageId);
    }

    private async Task<StageManifest> ValidateStageAsync(
        ObjectStageReceipt receipt,
        CancellationToken cancellationToken)
    {
        ValidateStageId(receipt.StageId);
        var manifest = await ValidateStageByIdAsync(receipt.StageId, cancellationToken);
        if (!string.Equals(receipt.Sha256, manifest.Sha256, StringComparison.Ordinal) ||
            receipt.ByteLength != manifest.ByteLength ||
            receipt.CreatedAtUtc != manifest.CreatedAtUtc)
        {
            throw new ObjectStoreException(
                "object_stage_receipt_mismatch",
                "The stage receipt does not match its persisted manifest.",
                receipt.StageId);
        }

        return manifest;
    }

    private async Task<StageManifest> ValidateStageByIdAsync(string stageId, CancellationToken cancellationToken)
    {
        ValidateStageId(stageId);
        var stageDirectory = GetStageDirectory(stageId);
        if (!Directory.Exists(stageDirectory))
        {
            throw new ObjectStoreException("object_stage_missing", "The staged object no longer exists.", stageId);
        }

        EnsurePathHasNoReparsePoint(StagingDirectory, stageDirectory);
        var manifestPath = Path.Combine(stageDirectory, "stage.json");
        var payloadPath = Path.Combine(stageDirectory, "payload");
        if (!File.Exists(manifestPath) || !File.Exists(payloadPath))
        {
            throw new ObjectStoreException(
                "object_stage_incomplete",
                "The staged object is missing its payload or recovery manifest.",
                stageId);
        }

        EnsurePathHasNoReparsePoint(StagingDirectory, manifestPath);
        EnsurePathHasNoReparsePoint(StagingDirectory, payloadPath);
        var manifestLength = new FileInfo(manifestPath).Length;
        if (manifestLength <= 0 || manifestLength > MaximumManifestBytes)
        {
            throw new ObjectStoreException(
                "object_stage_manifest_invalid",
                "The staged object manifest has an invalid size.",
                stageId);
        }

        StageManifest? manifest;
        try
        {
            await using var manifestStream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                MaximumManifestBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            manifest = await JsonSerializer.DeserializeAsync<StageManifest>(manifestStream, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ObjectStoreException(
                "object_stage_manifest_invalid",
                "The staged object manifest is not valid JSON.",
                stageId,
                innerException: exception);
        }

        if (manifest is null || manifest.SchemaVersion != ManifestSchema || manifest.StageId != stageId ||
            !IsSha256(manifest.Sha256) || manifest.ByteLength < 0)
        {
            throw new ObjectStoreException(
                "object_stage_manifest_invalid",
                "The staged object manifest contains invalid fields.",
                stageId);
        }

        var actual = await HashFileAsync(payloadPath, cancellationToken);
        if (actual.ByteLength != manifest.ByteLength ||
            !string.Equals(actual.Sha256, manifest.Sha256, StringComparison.Ordinal))
        {
            throw new ObjectStoreException(
                "object_stage_integrity_failed",
                "The staged object payload no longer matches its manifest.",
                stageId);
        }

        return manifest;
    }

    private async Task<bool> PublishedFileMatchesAsync(
        string targetPath,
        StageManifest manifest,
        CancellationToken cancellationToken)
    {
        EnsurePathHasNoReparsePoint(PublishedDirectory, targetPath);
        var target = await HashFileAsync(targetPath, cancellationToken);
        return target.ByteLength == manifest.ByteLength &&
               string.Equals(target.Sha256, manifest.Sha256, StringComparison.Ordinal);
    }

    private async Task RequireMatchingPublishedFileAsync(
        string stageId,
        string targetPath,
        StageManifest manifest,
        CancellationToken cancellationToken,
        Exception? concurrentMoveException = null)
    {
        try
        {
            if (await PublishedFileMatchesAsync(targetPath, manifest, cancellationToken))
            {
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            concurrentMoveException = exception;
        }

        var quarantineId = await TryQuarantineStageDirectoryAsync(stageId, "formal_conflict");
        throw new ObjectStoreException(
            "object_formal_conflict",
            "The formal content address could not be verified as identical; the staged object was quarantined without overwriting it.",
            stageId,
            quarantineId,
            concurrentMoveException);
    }

    private static async Task<FileIdentity> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long byteLength = 0;
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                byteLength = checked(byteLength + read);
            }

            return new FileIdentity(
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                byteLength);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<string> QuarantineStageDirectoryAsync(
        string stageId,
        string code,
        CancellationToken cancellationToken)
    {
        ValidateStageId(stageId);
        return await QuarantineArbitraryStageDirectoryAsync(GetStageDirectory(stageId), code, cancellationToken, stageId);
    }

    private async Task<string?> TryQuarantineStageDirectoryAsync(string stageId, string code)
    {
        try
        {
            return await QuarantineStageDirectoryAsync(stageId, code, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectStoreException)
        {
            return null;
        }
    }

    private async Task<string> QuarantineArbitraryStageDirectoryAsync(
        string stageDirectory,
        string code,
        CancellationToken cancellationToken,
        string? stageId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePathHasNoReparsePoint(StagingDirectory, stageDirectory);
        var quarantineId = Guid.NewGuid().ToString("N");
        var quarantineDirectory = Path.Combine(QuarantineDirectory, quarantineId);
        Directory.Move(stageDirectory, quarantineDirectory);
        var manifest = new QuarantineManifest(
            QuarantineSchema,
            quarantineId,
            code,
            stageId,
            DateTimeOffset.UtcNow);
        await WriteJsonAtomicallyAsync(
            Path.Combine(quarantineDirectory, "failure.json"),
            manifest,
            cancellationToken);
        return quarantineId;
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string destinationPath,
        T value,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".{Path.GetFileName(destinationPath)}-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string ResolvePublishedPath(string objectKey)
    {
        var candidate = Path.GetFullPath(Path.Combine(PublishedDirectory, objectKey.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = PublishedDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ObjectStoreException("object_key_invalid", "The computed object key left the published namespace.");
        }

        return candidate;
    }

    private string GetStageDirectory(string stageId)
    {
        ValidateStageId(stageId);
        return Path.Combine(StagingDirectory, stageId);
    }

    private static string CreateObjectKey(string sha256) => $"sha256/{sha256[..2]}/{sha256}";

    private static ObjectStageReceipt ToReceipt(StageManifest manifest) =>
        new(manifest.StageId, manifest.Sha256, manifest.ByteLength, manifest.CreatedAtUtc);

    private static string NormalizeSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("SHA-256 values must contain exactly 64 hexadecimal characters.", nameof(value));
        }

        return value.ToLowerInvariant();
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateStageId(string stageId)
    {
        if (!IsStageId(stageId))
        {
            throw new ObjectStoreException("object_stage_id_invalid", "Stage identifiers must be 32 lowercase hexadecimal characters.");
        }
    }

    private static bool IsStageId(string? stageId) =>
        stageId is { Length: 32 } && stageId.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void EnsureSafeDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ObjectStoreException(
                "object_store_reparse_detected",
                "Object store directories cannot be reparse points.");
        }
    }

    private static void EnsureSafeDirectoryWithin(string root, string targetDirectory)
    {
        EnsureSafeDirectory(root);
        var relative = Path.GetRelativePath(root, targetDirectory);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new ObjectStoreException("object_store_path_escape", "An object store directory left its namespace.");
        }

        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            Directory.CreateDirectory(current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ObjectStoreException(
                    "object_store_reparse_detected",
                    "Object store directories cannot be reparse points.");
            }
        }
    }

    private static void EnsurePathHasNoReparsePoint(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new ObjectStoreException("object_store_path_escape", "An object store path left its namespace.");
        }

        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ObjectStoreException("object_store_reparse_detected", "Object store paths cannot contain reparse points.");
        }

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ObjectStoreException("object_store_reparse_detected", "Object store paths cannot contain reparse points.");
            }
        }
    }

    private static void TryDeleteStageDirectory(string stageDirectory)
    {
        try
        {
            if (Directory.Exists(stageDirectory))
            {
                var stagingRoot = Directory.GetParent(stageDirectory)?.FullName;
                if (stagingRoot is null)
                {
                    return;
                }

                EnsurePathHasNoReparsePoint(stagingRoot, stageDirectory);
                Directory.Delete(stageDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectStoreException)
        {
        }
    }

    private sealed record StageManifest(
        string SchemaVersion,
        string StageId,
        string Sha256,
        long ByteLength,
        DateTimeOffset CreatedAtUtc);

    private sealed record QuarantineManifest(
        string SchemaVersion,
        string QuarantineId,
        string Code,
        string? StageId,
        DateTimeOffset CreatedAtUtc);

    private sealed record FileIdentity(string Sha256, long ByteLength);
}
