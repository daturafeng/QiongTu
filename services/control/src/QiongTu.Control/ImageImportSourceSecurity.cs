using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QiongTu.Control;

public interface IImageImportSecretProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedData);
}

public sealed class ImageImportSourceSecurityException : IOException
{
    public ImageImportSourceSecurityException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record ImageImportSourceRecoveryManifest(
    string SessionId,
    string SourceRootKey,
    string AbsoluteSourceRoot,
    IReadOnlyDictionary<string, string> RelativePathBySourceItemKey,
    IReadOnlyDictionary<string, ImageImportSourceSnapshot>? SnapshotBySourceItemKey = null);

public sealed class ImageImportSourceSecurity
{
    private const string KeySchema = "qiongtu.image-import-hmac-key.v1";
    private const string RecoveryEnvelopeSchema = "qiongtu.image-import-source-locator-envelope.v1";
    private const string RecoveryPayloadSchema = "qiongtu.image-import-source-locator-payload.v1";
    private const int HmacKeyBytes = 32;
    private const int IoBufferSize = 16 * 1024;
    private const int MaximumProtectedManifestBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IImageImportSecretProtector _protector;
    private readonly Func<byte[]> _keyFactory;
    private readonly SemaphoreSlim _keyGate = new(1, 1);
    private byte[]? _hmacKey;

    public ImageImportSourceSecurity(
        string storageDirectory,
        IImageImportSecretProtector? protector = null,
        Func<byte[]>? keyFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        StorageDirectory = Path.GetFullPath(storageDirectory);
        Directory.CreateDirectory(StorageDirectory);
        if ((File.GetAttributes(StorageDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_storage_reparse_point",
                "The import locator storage directory cannot be a reparse point.");
        }

        _protector = protector ?? new WindowsCurrentUserDpapiProtector();
        _keyFactory = keyFactory ?? CreateRandomHmacKey;
        KeyFilePath = Path.Combine(StorageDirectory, "image-import-hmac-key.v1.json");
    }

    public string StorageDirectory { get; }

    public string KeyFilePath { get; }

    public async Task<string> CreateSourceRootKeyAsync(
        string absoluteSourceRoot,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeAbsolutePathForKey(absoluteSourceRoot);
        return await ComputeKeyAsync("root", normalized, cancellationToken);
    }

    public async Task<string> CreateSourceItemKeyAsync(
        string absoluteSourceRoot,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        _ = NormalizeAbsolutePathForKey(absoluteSourceRoot);
        var normalizedRelativePath = NormalizeRelativePathForKey(relativePath);
        return await ComputeKeyAsync("item", normalizedRelativePath, cancellationToken);
    }

    public async Task<string> CreateSourceIdentityKeyAsync(
        string sourceItemKey,
        string identityMaterial,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceItemKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityMaterial);
        return await ComputeKeyAsync("identity", sourceItemKey + "\0" + identityMaterial, cancellationToken);
    }

    public Task SaveRecoveryManifestAsync(
        ImageImportSourceRecoveryManifest manifest,
        CancellationToken cancellationToken = default) =>
        SaveRecoveryManifestAtPathAsync(
            manifest,
            GetRecoveryManifestPath(manifest.SessionId),
            cancellationToken);

    internal Task SavePreparedRecoveryManifestAsync(
        ImageImportSourceRecoveryManifest manifest,
        string preparationId,
        CancellationToken cancellationToken = default) =>
        SaveRecoveryManifestAtPathAsync(
            manifest,
            GetPreparedRecoveryManifestPath(preparationId),
            cancellationToken);

    internal void CommitPreparedRecoveryManifest(string preparationId, string sessionId)
    {
        ValidateSessionId(sessionId);
        var preparedPath = GetPreparedRecoveryManifestPath(preparationId);
        if (!File.Exists(preparedPath))
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_preparation_missing",
                "The prepared import source locator manifest is missing.");
        }

        File.Move(preparedPath, GetRecoveryManifestPath(sessionId), overwrite: true);
    }

    internal bool TryCommitPreparedRecoveryManifest(string preparationId, string sessionId)
    {
        ValidateSessionId(sessionId);
        var preparedPath = GetPreparedRecoveryManifestPath(preparationId);
        if (!File.Exists(preparedPath))
        {
            return false;
        }

        File.Move(preparedPath, GetRecoveryManifestPath(sessionId), overwrite: true);
        return true;
    }

    internal void DeletePreparedRecoveryManifest(string preparationId)
    {
        var preparedPath = GetPreparedRecoveryManifestPath(preparationId);
        try
        {
            if (File.Exists(preparedPath))
            {
                File.Delete(preparedPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A protected preparation is safe to leave for bounded later cleanup. Cleanup
            // must not replace the authoritative catalog/idempotency error seen by the caller.
        }
    }

    private async Task SaveRecoveryManifestAtPathAsync(
        ImageImportSourceRecoveryManifest manifest,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateSessionId(manifest.SessionId);
        if (manifest.RelativePathBySourceItemKey.Count > 100_000)
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_manifest_too_large",
                "The import source locator manifest exceeded the supported item count.");
        }

        var payload = new RecoveryPayload(
            RecoveryPayloadSchema,
            manifest.SessionId,
            manifest.SourceRootKey,
            Path.GetFullPath(manifest.AbsoluteSourceRoot),
            manifest.RelativePathBySourceItemKey
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            (manifest.SnapshotBySourceItemKey ?? new Dictionary<string, ImageImportSourceSnapshot>())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            DateTimeOffset.UtcNow);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        byte[] protectedBytes;
        try
        {
            protectedBytes = _protector.Protect(payloadBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadBytes);
        }

        var envelope = new RecoveryEnvelope(
            RecoveryEnvelopeSchema,
            manifest.SessionId,
            manifest.SourceRootKey,
            Convert.ToBase64String(protectedBytes),
            DateTimeOffset.UtcNow);
        CryptographicOperations.ZeroMemory(protectedBytes);

        await WriteJsonAtomicallyAsync(destinationPath, envelope, cancellationToken);
    }

    public async Task<ImageImportSourceRecoveryManifest> LoadRecoveryManifestAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        var path = GetRecoveryManifestPath(sessionId);
        if (!File.Exists(path))
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_manifest_missing",
                "The protected import source locator manifest is missing.");
        }

        var length = new FileInfo(path).Length;
        if (length <= 0 || length > MaximumProtectedManifestBytes)
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_manifest_invalid",
                "The protected import source locator manifest has an invalid size.");
        }

        RecoveryEnvelope? envelope;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            envelope = await JsonSerializer.DeserializeAsync<RecoveryEnvelope>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_manifest_invalid",
                "The protected import source locator manifest is not valid JSON.",
                exception);
        }

        if (envelope is null ||
            envelope.SchemaVersion != RecoveryEnvelopeSchema ||
            envelope.SessionId != sessionId ||
            string.IsNullOrWhiteSpace(envelope.SourceRootKey) ||
            string.IsNullOrWhiteSpace(envelope.ProtectedPayload))
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_manifest_invalid",
                "The protected import source locator manifest contains invalid fields.");
        }

        byte[] protectedBytes;
        byte[] payloadBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(envelope.ProtectedPayload);
            payloadBytes = _protector.Unprotect(protectedBytes);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ExternalException)
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_manifest_protection_failed",
                "The protected import source locator manifest could not be decrypted.",
                exception);
        }

        try
        {
            RecoveryPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<RecoveryPayload>(payloadBytes, JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new ImageImportSourceSecurityException(
                    "source_locator_manifest_protection_failed",
                    "The protected import source locator payload is invalid.",
                    exception);
            }

            if (payload is null ||
                payload.SchemaVersion != RecoveryPayloadSchema ||
                payload.SessionId != envelope.SessionId ||
                payload.SourceRootKey != envelope.SourceRootKey ||
                string.IsNullOrWhiteSpace(payload.AbsoluteSourceRoot) ||
                payload.RelativePathBySourceItemKey is null ||
                payload.SnapshotBySourceItemKey is null)
            {
                throw new ImageImportSourceSecurityException(
                    "source_locator_manifest_protection_failed",
                    "The protected import source locator payload contains invalid fields.");
            }

            var absoluteSourceRoot = Path.GetFullPath(payload.AbsoluteSourceRoot);
            var expectedRootKey = await CreateSourceRootKeyAsync(absoluteSourceRoot, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(payload.SourceRootKey),
                    Encoding.ASCII.GetBytes(expectedRootKey)))
            {
                throw new ImageImportSourceSecurityException(
                    "source_locator_manifest_protection_failed",
                    "The protected import source root identity is inconsistent.");
            }

            var relativePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            var snapshots = new Dictionary<string, ImageImportSourceSnapshot>(StringComparer.Ordinal);
            foreach (var pair in payload.RelativePathBySourceItemKey)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    throw new ImageImportSourceSecurityException(
                        "source_locator_manifest_protection_failed",
                        "The protected import source locator payload contains invalid locator entries.");
                }

                _ = NormalizeRelativePathForKey(pair.Value);
                var expectedItemKey = await CreateSourceItemKeyAsync(absoluteSourceRoot, pair.Value, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(pair.Key),
                        Encoding.ASCII.GetBytes(expectedItemKey)))
                {
                    throw new ImageImportSourceSecurityException(
                        "source_locator_manifest_protection_failed",
                        "The protected import source item identity is inconsistent.");
                }

                relativePaths.Add(pair.Key, pair.Value);
                if (payload.SnapshotBySourceItemKey.TryGetValue(pair.Key, out var snapshot))
                {
                    snapshots.Add(pair.Key, snapshot);
                }
            }

            if (payload.SnapshotBySourceItemKey.Keys.Any(key => !relativePaths.ContainsKey(key)))
            {
                throw new ImageImportSourceSecurityException(
                    "source_locator_manifest_protection_failed",
                    "The protected import source snapshot map is inconsistent.");
            }

            return new ImageImportSourceRecoveryManifest(
                payload.SessionId,
                payload.SourceRootKey,
                absoluteSourceRoot,
                relativePaths,
                snapshots);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadBytes);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public string GetRecoveryManifestPath(string sessionId)
    {
        ValidateSessionId(sessionId);
        return Path.Combine(StorageDirectory, $"image-import-session-{sessionId}.locator.v1.json");
    }

    private string GetPreparedRecoveryManifestPath(string preparationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preparationId);
        if (preparationId.Length > 128)
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_preparation_invalid",
                "The import source locator preparation identifier is invalid.");
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(preparationId));
        return Path.Combine(
            StorageDirectory,
            $"image-import-prepared-{Convert.ToHexString(digest).ToLowerInvariant()}.locator.v1.json");
    }

    public static string ToLeafDisplayName(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(leaf) ? trimmed : leaf.Normalize(NormalizationForm.FormC);
    }

    internal static string NormalizeRelativePathForKey(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Normalize(NormalizationForm.FormC);
        if (Path.IsPathRooted(normalized) ||
            normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_relative_path_invalid",
                "The import source relative locator path is invalid.");
        }

        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    internal static string NormalizeAbsolutePathForKey(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolutePath))
            .Normalize(NormalizationForm.FormC);
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    private async Task<string> ComputeKeyAsync(
        string domain,
        string normalizedValue,
        CancellationToken cancellationToken)
    {
        var key = await LoadOrCreateHmacKeyAsync(cancellationToken);
        var text = domain + "\0" + normalizedValue;
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            using var hmac = new HMACSHA256(key);
            return Convert.ToHexString(hmac.ComputeHash(bytes)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<byte[]> LoadOrCreateHmacKeyAsync(CancellationToken cancellationToken)
    {
        if (_hmacKey is not null)
        {
            return _hmacKey;
        }

        await _keyGate.WaitAsync(cancellationToken);
        try
        {
            if (_hmacKey is not null)
            {
                return _hmacKey;
            }

            if (File.Exists(KeyFilePath))
            {
                var persisted = await ReadProtectedKeyAsync(cancellationToken);
                _hmacKey = persisted;
                return _hmacKey;
            }

            var key = _keyFactory();
            if (key.Length != HmacKeyBytes)
            {
                throw new ImageImportSourceSecurityException(
                    "source_locator_key_invalid",
                    "The import source locator key factory returned an invalid key length.");
            }

            byte[]? protectedKey = null;
            try
            {
                protectedKey = _protector.Protect(key);
                var persisted = new HmacKeyFile(KeySchema, Convert.ToBase64String(protectedKey), DateTimeOffset.UtcNow);
                try
                {
                    await WriteJsonAtomicallyAsync(
                        KeyFilePath,
                        persisted,
                        cancellationToken,
                        overwrite: false);
                }
                catch (IOException) when (File.Exists(KeyFilePath))
                {
                    var winningKey = await ReadProtectedKeyAsync(cancellationToken);
                    _hmacKey = winningKey;
                    return _hmacKey;
                }

                _hmacKey = key.ToArray();
                return _hmacKey;
            }
            finally
            {
                if (protectedKey is not null)
                {
                    CryptographicOperations.ZeroMemory(protectedKey);
                }

                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch (ImageImportSourceSecurityException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_key_io_failed",
                "The protected import source locator key could not be read or created.");
        }
        finally
        {
            _keyGate.Release();
        }
    }

    private async Task<byte[]> ReadProtectedKeyAsync(CancellationToken cancellationToken)
    {
        HmacKeyFile? file;
        try
        {
            await using var stream = new FileStream(
                KeyFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            file = await JsonSerializer.DeserializeAsync<HmacKeyFile>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_key_invalid",
                "The protected import source locator key file is not valid JSON.",
                exception);
        }

        if (file is null || file.SchemaVersion != KeySchema || string.IsNullOrWhiteSpace(file.ProtectedKey))
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_key_invalid",
                "The protected import source locator key file contains invalid fields.");
        }

        byte[]? protectedKey = null;
        byte[] key;
        try
        {
            protectedKey = Convert.FromBase64String(file.ProtectedKey);
            key = _protector.Unprotect(protectedKey);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ExternalException)
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_key_protection_failed",
                "The protected import source locator key could not be decrypted.",
                exception);
        }
        finally
        {
            if (protectedKey is not null)
            {
                CryptographicOperations.ZeroMemory(protectedKey);
            }
        }

        if (key.Length != HmacKeyBytes)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new ImageImportSourceSecurityException(
                "source_locator_key_invalid",
                "The protected import source locator key has an invalid length.");
        }

        return key;
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string destinationPath,
        T value,
        CancellationToken cancellationToken,
        bool overwrite = true)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(destinationPath)}-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             IoBufferSize,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static byte[] CreateRandomHmacKey() => RandomNumberGenerator.GetBytes(HmacKeyBytes);

    private static void ValidateSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId.Length > 96 ||
            sessionId.Any(character => character is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '-' and not '_' and not '.' and not ':'))
        {
            throw new ImageImportSourceSecurityException(
                "source_locator_session_id_invalid",
                "Import source locator session identifiers contain unsupported characters.");
        }
    }

    private sealed record HmacKeyFile(
        string SchemaVersion,
        string ProtectedKey,
        DateTimeOffset CreatedAtUtc);

    private sealed record RecoveryEnvelope(
        string SchemaVersion,
        string SessionId,
        string SourceRootKey,
        string ProtectedPayload,
        DateTimeOffset UpdatedAtUtc);

    private sealed record RecoveryPayload(
        string SchemaVersion,
        string SessionId,
        string SourceRootKey,
        string AbsoluteSourceRoot,
        IReadOnlyDictionary<string, string> RelativePathBySourceItemKey,
        IReadOnlyDictionary<string, ImageImportSourceSnapshot> SnapshotBySourceItemKey,
        DateTimeOffset CreatedAtUtc);

    public sealed class WindowsCurrentUserDpapiProtector : IImageImportSecretProtector
    {
        private const int CryptProtectUiForbidden = 0x1;

        public byte[] Protect(byte[] plaintext)
        {
            ArgumentNullException.ThrowIfNull(plaintext);
            return CryptProtect(plaintext, protect: true);
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            ArgumentNullException.ThrowIfNull(protectedData);
            return CryptProtect(protectedData, protect: false);
        }

        private static byte[] CryptProtect(byte[] input, bool protect)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Windows CurrentUser DPAPI is required for import source locator protection.");
            }

            using var inputHandle = new PinnedBlob(input);
            var inputBlob = new DataBlob(input.Length, inputHandle.Pointer);
            var outputBlob = new DataBlob();
            var success = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob);
            if (!success)
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            try
            {
                var output = new byte[outputBlob.Count];
                Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                return output;
            }
            finally
            {
                if (outputBlob.Data != IntPtr.Zero)
                {
                    _ = LocalFree(outputBlob.Data);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public DataBlob(int count, IntPtr data)
            {
                Count = count;
                Data = data;
            }

            public int Count;
            public IntPtr Data;
        }

        private sealed class PinnedBlob : IDisposable
        {
            private readonly byte[] _bytes;
            private readonly GCHandle _handle;

            public PinnedBlob(byte[] bytes)
            {
                _bytes = bytes;
                _handle = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
                Pointer = _handle.AddrOfPinnedObject();
            }

            public IntPtr Pointer { get; }

            public void Dispose()
            {
                _handle.Free();
            }
        }

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob dataIn,
            string? dataDescription,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            ref DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob dataIn,
            IntPtr dataDescription,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            ref DataBlob dataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr handle);
    }
}
