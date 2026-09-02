using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace QiongTu.Control;

public sealed class ImageImportSourceDiscoveryException : IOException
{
    public ImageImportSourceDiscoveryException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record ImageImportSourceDiscoveryOptions(
    int MaximumEntries = 1_000_000,
    int MaximumCandidates = 100_000,
    int MaximumDepth = 64,
    long MaximumFileBytes = 16L * 1024 * 1024 * 1024,
    IReadOnlySet<string>? CandidateExtensions = null)
{
    public IReadOnlySet<string> EffectiveCandidateExtensions =>
        CandidateExtensions ?? ImageImportSourceDiscovery.DefaultCandidateExtensions;
}

public sealed record ImageImportSourceSnapshot(
    long? Length,
    DateTimeOffset LastWriteTimeUtc,
    FileAttributes Attributes,
    string? Identity);

public sealed record ImageImportDiscoveredSourceRoot(
    string SourceRootKey,
    string LeafDisplayName,
    ImageImportSourceSnapshot Snapshot);

public sealed record ImageImportDiscoveredItem(
    string SourceRootKey,
    string SourceItemKey,
    string LeafDisplayName,
    ImageImportSourceSnapshot Snapshot);

public sealed record ImageImportSourceDiscoveryResult(
    ImageImportDiscoveredSourceRoot SourceRoot,
    IReadOnlyList<ImageImportDiscoveredItem> Candidates,
    ImageImportSourceRecoveryManifest RecoveryManifest);

public sealed record ImageImportSourceCopyResult(
    string SourceItemKey,
    long BytesCopied,
    ImageImportSourceSnapshot SnapshotBeforeCopy,
    ImageImportSourceSnapshot SnapshotAfterCopy);

public sealed class ImageImportSourceDiscovery
{
    private const int BufferSize = 128 * 1024;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagOverlapped = 0x40000000;

    public static readonly IReadOnlySet<string> DefaultCandidateExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",
            ".jpeg",
            ".mpo",
            ".tif",
            ".tiff"
        };

    public static readonly IReadOnlySet<string> DefaultPreflightSidecarExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mrk",
            ".nav",
            ".obs",
            ".rtk"
        };

    private readonly ImageImportSourceSecurity _security;
    private readonly ImageImportSourceDiscoveryOptions _options;

    public ImageImportSourceDiscovery(
        ImageImportSourceSecurity security,
        ImageImportSourceDiscoveryOptions? options = null)
    {
        _security = security ?? throw new ArgumentNullException(nameof(security));
        _options = options ?? new ImageImportSourceDiscoveryOptions();
        ValidateOptions(_options);
    }

    public Task<ImageImportSourceDiscoveryResult> DiscoverAsync(
        string sessionId,
        string sourceRoot,
        ControlDataPaths controlDataPaths,
        CancellationToken cancellationToken = default) =>
        DiscoverCoreAsync(sessionId, sourceRoot, controlDataPaths, persistRecoveryManifest: true, cancellationToken);

    internal Task<ImageImportSourceDiscoveryResult> DiscoverPreparedAsync(
        string sessionId,
        string sourceRoot,
        ControlDataPaths controlDataPaths,
        CancellationToken cancellationToken = default) =>
        DiscoverCoreAsync(sessionId, sourceRoot, controlDataPaths, persistRecoveryManifest: false, cancellationToken);

    public async Task<ImageImportSourceDiscoveryResult> DiscoverPreflightSidecarsAsync(
        ImageImportSourceRecoveryManifest existingManifest,
        ControlDataPaths controlDataPaths,
        CancellationToken cancellationToken = default) =>
        await DiscoverPreflightSidecarsCoreAsync(
            existingManifest,
            controlDataPaths,
            persistRecoveryManifest: true,
            cancellationToken);

    internal async Task<ImageImportSourceDiscoveryResult> DiscoverPreflightSidecarsPreparedAsync(
        ImageImportSourceRecoveryManifest existingManifest,
        ControlDataPaths controlDataPaths,
        CancellationToken cancellationToken = default) =>
        await DiscoverPreflightSidecarsCoreAsync(
            existingManifest,
            controlDataPaths,
            persistRecoveryManifest: false,
            cancellationToken);

    private async Task<ImageImportSourceDiscoveryResult> DiscoverPreflightSidecarsCoreAsync(
        ImageImportSourceRecoveryManifest existingManifest,
        ControlDataPaths controlDataPaths,
        bool persistRecoveryManifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(existingManifest);
        ArgumentNullException.ThrowIfNull(controlDataPaths);
        var sidecarOptions = _options with
        {
            CandidateExtensions = DefaultPreflightSidecarExtensions
        };
        var sidecarDiscovery = new ImageImportSourceDiscovery(_security, sidecarOptions);
        var discovered = await sidecarDiscovery.DiscoverPreparedAsync(
            existingManifest.SessionId,
            existingManifest.AbsoluteSourceRoot,
            controlDataPaths,
            cancellationToken);
        if (!string.Equals(
                discovered.SourceRoot.SourceRootKey,
                existingManifest.SourceRootKey,
                StringComparison.Ordinal))
        {
            throw new ImageImportSourceDiscoveryException(
                "source_root_identity_changed",
                "The import source root identity changed before source preflight.");
        }

        var relativePaths = new Dictionary<string, string>(
            existingManifest.RelativePathBySourceItemKey,
            StringComparer.Ordinal);
        var snapshots = new Dictionary<string, ImageImportSourceSnapshot>(
            existingManifest.SnapshotBySourceItemKey ?? new Dictionary<string, ImageImportSourceSnapshot>(),
            StringComparer.Ordinal);
        foreach (var pair in discovered.RecoveryManifest.RelativePathBySourceItemKey)
        {
            if (relativePaths.TryGetValue(pair.Key, out var existingRelativePath) &&
                !string.Equals(existingRelativePath, pair.Value, StringComparison.Ordinal))
            {
                throw new ImageImportSourceDiscoveryException(
                    "source_path_normalization_conflict",
                    "The import source contains paths that collide after normalization.");
            }

            relativePaths[pair.Key] = pair.Value;
        }

        foreach (var pair in discovered.RecoveryManifest.SnapshotBySourceItemKey ??
                     new Dictionary<string, ImageImportSourceSnapshot>())
        {
            snapshots[pair.Key] = pair.Value;
        }

        var mergedManifest = existingManifest with
        {
            RelativePathBySourceItemKey = relativePaths,
            SnapshotBySourceItemKey = snapshots
        };
        if (persistRecoveryManifest)
        {
            await _security.SaveRecoveryManifestAsync(mergedManifest, cancellationToken);
        }

        return discovered with { RecoveryManifest = mergedManifest };
    }

    private async Task<ImageImportSourceDiscoveryResult> DiscoverCoreAsync(
        string sessionId,
        string sourceRoot,
        ControlDataPaths controlDataPaths,
        bool persistRecoveryManifest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(controlDataPaths);

        var normalizedRoot = NormalizeExistingDirectory(sourceRoot);
        EnsureNoControlDirectoryOverlap(normalizedRoot, controlDataPaths);
        EnsurePathHasNoReparsePoint(normalizedRoot, normalizedRoot, rootCode: "source_root_reparse_point");

        var rootKey = await _security.CreateSourceRootKeyAsync(normalizedRoot, cancellationToken);
        var rootSnapshot = CreateDirectorySnapshot(normalizedRoot);
        var root = new ImageImportDiscoveredSourceRoot(
            rootKey,
            ImageImportSourceSecurity.ToLeafDisplayName(normalizedRoot),
            rootSnapshot);

        var candidates = new List<PendingCandidate>();
        var stack = new Stack<DirectoryFrame>();
        stack.Push(new DirectoryFrame(normalizedRoot, string.Empty, 0));
        var entriesSeen = 0;

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = stack.Pop();
            EnsurePathHasNoReparsePoint(normalizedRoot, frame.AbsolutePath);

            IReadOnlyList<(string AbsolutePath, string RelativePath)> orderedEntries;
            try
            {
                orderedEntries = Directory.EnumerateFileSystemEntries(frame.AbsolutePath)
                    .Select(path =>
                    {
                        var absolutePath = Path.GetFullPath(path);
                        return (absolutePath, Path.GetRelativePath(normalizedRoot, absolutePath));
                    })
                    .OrderBy(entry => NormalizeRelativePathForOrdering(entry.Item2), StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                throw new ImageImportSourceDiscoveryException(
                    "source_directory_unavailable",
                    "An import source directory is temporarily unavailable.");
            }

            foreach (var entry in orderedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entriesSeen++;
                if (entriesSeen > _options.MaximumEntries)
                {
                    throw new ImageImportSourceDiscoveryException(
                        "source_scan_entry_limit_exceeded",
                        "The import source scan reached the configured file-system entry limit.");
                }

                var attributes = GetAttributesWithoutPathLeak(entry.AbsolutePath, "source_entry_unavailable");
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // The selected root itself is rejected above. Nested reparse entries are
                    // intentionally skipped so an unrelated link cannot make the remaining
                    // ordinary source files unavailable, and the target is never traversed.
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var childDepth = frame.Depth + 1;
                    if (childDepth > _options.MaximumDepth)
                    {
                        throw new ImageImportSourceDiscoveryException(
                            "source_scan_depth_limit_exceeded",
                            "The import source scan reached the configured recursion depth limit.");
                    }

                    stack.Push(new DirectoryFrame(entry.AbsolutePath, entry.RelativePath, childDepth));
                    continue;
                }

                if (!IsCandidate(entry.AbsolutePath, _options.EffectiveCandidateExtensions))
                {
                    continue;
                }

                var fileInfo = new FileInfo(entry.AbsolutePath);
                if (fileInfo.Length > _options.MaximumFileBytes)
                {
                    throw new ImageImportSourceDiscoveryException(
                        "source_candidate_file_too_large",
                        "An import source candidate exceeds the configured single-file size limit.");
                }

                if (candidates.Count >= _options.MaximumCandidates)
                {
                    throw new ImageImportSourceDiscoveryException(
                        "source_candidate_limit_exceeded",
                        "The import source scan reached the configured candidate limit.");
                }

                EnsurePathHasNoReparsePoint(normalizedRoot, entry.AbsolutePath);
                candidates.Add(new PendingCandidate(entry.AbsolutePath, entry.RelativePath));
            }
        }

        var normalizedRelativePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var normalizedRelativePath = ImageImportSourceSecurity.NormalizeRelativePathForKey(candidate.RelativePath);
            if (!normalizedRelativePaths.Add(normalizedRelativePath))
            {
                throw new ImageImportSourceDiscoveryException(
                    "source_path_normalization_conflict",
                    "The import source contains paths that collide after Unicode and Windows case normalization.");
            }
        }

        var discovered = new List<ImageImportDiscoveredItem>(candidates.Count);
        var manifestEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in candidates
                     .OrderBy(item => NormalizeRelativePathForOrdering(item.RelativePath), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemKey = await _security.CreateSourceItemKeyAsync(normalizedRoot, candidate.RelativePath, cancellationToken);
            var snapshot = CreateFileSnapshot(candidate.AbsolutePath);
            discovered.Add(new ImageImportDiscoveredItem(
                rootKey,
                itemKey,
                ImageImportSourceSecurity.ToLeafDisplayName(candidate.AbsolutePath),
                snapshot));
            manifestEntries.Add(itemKey, candidate.RelativePath);
        }

        var recoveryManifest = new ImageImportSourceRecoveryManifest(
            sessionId,
            rootKey,
            normalizedRoot,
            manifestEntries,
            discovered.ToDictionary(
                item => item.SourceItemKey,
                item => item.Snapshot,
                StringComparer.Ordinal));
        if (persistRecoveryManifest)
        {
            await _security.SaveRecoveryManifestAsync(recoveryManifest, cancellationToken);
        }

        return new ImageImportSourceDiscoveryResult(root, discovered, recoveryManifest);
    }

    public async Task<ImageImportSourceCopyResult> CopySourceItemAsync(
        ImageImportSourceRecoveryManifest manifest,
        string sourceItemKey,
        ImageImportSourceSnapshot expectedSnapshot,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceItemKey);
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        }

        if (!manifest.RelativePathBySourceItemKey.TryGetValue(sourceItemKey, out var relativePath))
        {
            throw new ImageImportSourceDiscoveryException(
                "source_locator_item_missing",
                "The requested import source locator item is not present in the protected manifest.");
        }

        var sourcePath = ResolveManifestItemPath(manifest.AbsoluteSourceRoot, relativePath);
        EnsurePathHasNoReparsePoint(manifest.AbsoluteSourceRoot, sourcePath);

        await using var source = OpenReadOnlySource(sourcePath, manifest.AbsoluteSourceRoot);
        var before = CreateFileSnapshot(sourcePath, source.SafeFileHandle);
        RequireMatchingSnapshot(expectedSnapshot, before);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long copied = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                try
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                catch (IOException exception) when (IsDiskFull(exception))
                {
                    throw new ImageImportSourceDiscoveryException(
                        "source_copy_destination_disk_full",
                        "The import copy destination reported insufficient disk space.");
                }

                copied = checked(copied + read);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ImageImportSourceDiscoveryException)
        {
            throw;
        }
        catch (IOException)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_copy_failed",
                "The import source could not be copied read-only.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var after = CreateFileSnapshot(sourcePath, source.SafeFileHandle);
        RequireMatchingSnapshot(before, after);
        if (copied != before.Length)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_changed",
                "The import source changed while it was being copied.");
        }

        return new ImageImportSourceCopyResult(sourceItemKey, copied, before, after);
    }

    public FileStream OpenSourceReadOnly(
        ImageImportSourceRecoveryManifest manifest,
        string sourceItemKey,
        ImageImportSourceSnapshot expectedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceItemKey);
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        if (!manifest.RelativePathBySourceItemKey.TryGetValue(sourceItemKey, out var relativePath))
        {
            throw new ImageImportSourceDiscoveryException(
                "source_locator_item_missing",
                "The requested import source locator item is not present in the protected manifest.");
        }

        var sourcePath = ResolveManifestItemPath(manifest.AbsoluteSourceRoot, relativePath);
        EnsurePathHasNoReparsePoint(manifest.AbsoluteSourceRoot, sourcePath);
        var stream = OpenReadOnlySource(sourcePath, manifest.AbsoluteSourceRoot);
        try
        {
            RequireMatchingSnapshot(expectedSnapshot, CreateFileSnapshot(sourcePath, stream.SafeFileHandle));
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal async Task<T> ReadSourceItemAsync<T>(
        ImageImportSourceRecoveryManifest manifest,
        string sourceItemKey,
        ImageImportSourceSnapshot expectedSnapshot,
        Func<Stream, CancellationToken, Task<T>> consumeAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumeAsync);
        if (!manifest.RelativePathBySourceItemKey.TryGetValue(sourceItemKey, out var relativePath))
        {
            throw new ImageImportSourceDiscoveryException(
                "source_locator_item_missing",
                "The requested import source locator item is not present in the protected manifest.");
        }

        var sourcePath = ResolveManifestItemPath(manifest.AbsoluteSourceRoot, relativePath);
        EnsurePathHasNoReparsePoint(manifest.AbsoluteSourceRoot, sourcePath);
        await using var source = OpenReadOnlySource(sourcePath, manifest.AbsoluteSourceRoot);
        var before = CreateFileSnapshot(sourcePath, source.SafeFileHandle);
        RequireMatchingSnapshot(expectedSnapshot, before);
        var result = await consumeAsync(source, cancellationToken);
        var after = CreateFileSnapshot(sourcePath, source.SafeFileHandle);
        RequireMatchingSnapshot(before, after);
        return result;
    }

    private static void ValidateOptions(ImageImportSourceDiscoveryOptions options)
    {
        if (options.MaximumEntries <= 0 ||
            options.MaximumCandidates <= 0 ||
            options.MaximumDepth < 0 ||
            options.MaximumFileBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Import source discovery limits must be positive.");
        }

        if (options.EffectiveCandidateExtensions.Count == 0 ||
            options.EffectiveCandidateExtensions.Any(extension => !extension.StartsWith(".", StringComparison.Ordinal) || extension.Contains(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)))
        {
            throw new ArgumentException("Import source candidate extensions are invalid.", nameof(options));
        }
    }

    private static FileStream OpenReadOnlySource(string sourcePath, string sourceRoot)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var handle = CreateFileW(
                    sourcePath,
                    GenericRead,
                    FileShareRead,
                    IntPtr.Zero,
                    OpenExisting,
                    FileAttributeNormal | FileFlagOverlapped | FileFlagSequentialScan | FileFlagOpenReparsePoint,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new Win32Exception(error);
                }

                try
                {
                    if (!GetFileInformationByHandle(handle, out var information))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    if ((((FileAttributes)information.FileAttributes) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new ImageImportSourceDiscoveryException(
                            "source_reparse_point_detected",
                            "The import source contains a reparse point and will not be followed.");
                    }

                    var resolvedPath = GetResolvedPathFromHandle(handle);
                    if (!IsSameOrInside(resolvedPath, sourceRoot))
                    {
                        throw new ImageImportSourceDiscoveryException(
                            "source_path_escape",
                            "The import source handle resolved outside its selected root.");
                    }

                    return new FileStream(handle, FileAccess.Read, BufferSize, isAsync: true);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            return new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_missing",
                "The import source file is missing.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_device_unavailable",
                "The import source device or directory is unavailable.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_access_denied",
                "The import source file could not be opened read-only.");
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            throw new ImageImportSourceDiscoveryException(
                "source_locked",
                "The import source file is locked by another process.");
        }
        catch (IOException)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_unavailable",
                "The import source file is unavailable.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            throw new ImageImportSourceDiscoveryException(
                exception.NativeErrorCode == 2 ? "source_missing" : "source_device_unavailable",
                "The import source file or device is unavailable.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 32 or 33)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_locked",
                "The import source file is locked by another process.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 5)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_access_denied",
                "The import source file could not be opened read-only.");
        }
        catch (Win32Exception)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_unavailable",
                "The import source file is unavailable.");
        }
    }

    private static ImageImportSourceSnapshot CreateDirectorySnapshot(string directoryPath)
    {
        var attributes = GetAttributesWithoutPathLeak(directoryPath, "source_root_unavailable");
        return new ImageImportSourceSnapshot(
            null,
            File.GetLastWriteTimeUtc(directoryPath),
            attributes,
            TryGetPathIdentity(directoryPath, null));
    }

    private static ImageImportSourceSnapshot CreateFileSnapshot(string filePath, SafeFileHandle? handle = null)
    {
        try
        {
            if (OperatingSystem.IsWindows() && handle is not null && !handle.IsInvalid)
            {
                if (!GetFileInformationByHandle(handle, out var handleInformation))
                {
                    throw new ImageImportSourceDiscoveryException(
                        "source_unavailable",
                        "The import source handle could not be inspected.");
                }

                var handleAttributes = (FileAttributes)handleInformation.FileAttributes;
                if ((handleAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ImageImportSourceDiscoveryException(
                        "source_reparse_point_detected",
                        "The import source contains a reparse point and will not be followed.");
                }

                var length = ((long)handleInformation.FileSizeHigh << 32) | handleInformation.FileSizeLow;
                var fileTime = ((long)handleInformation.LastWriteTime.dwHighDateTime << 32) |
                               (uint)handleInformation.LastWriteTime.dwLowDateTime;
                return new ImageImportSourceSnapshot(
                    length,
                    new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero),
                    handleAttributes,
                    FormatIdentity(handleInformation));
            }

            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                throw new ImageImportSourceDiscoveryException(
                    "source_missing",
                    "The import source file is missing.");
            }

            var attributes = info.Attributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ImageImportSourceDiscoveryException(
                    "source_reparse_point_detected",
                    "The import source contains a reparse point and will not be followed.");
            }

            return new ImageImportSourceSnapshot(
                info.Length,
                info.LastWriteTimeUtc,
                attributes,
                TryGetPathIdentity(filePath, handle));
        }
        catch (ImageImportSourceDiscoveryException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_missing",
                "The import source file is missing.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_device_unavailable",
                "The import source device or directory is unavailable.");
        }
        catch (IOException)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_unavailable",
                "The import source file is unavailable.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_access_denied",
                "The import source file could not be inspected.");
        }
    }

    private static string ResolveManifestItemPath(string root, string relativePath)
    {
        var rootFullPath = NormalizeExistingDirectory(root);
        var candidate = Path.GetFullPath(Path.Combine(rootFullPath, relativePath));
        if (!IsSameOrInside(candidate, rootFullPath))
        {
            throw new ImageImportSourceDiscoveryException(
                "source_locator_path_escape",
                "The import source locator escaped its protected source root.");
        }

        return candidate;
    }

    private static string NormalizeExistingDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath))
        {
            throw new ImageImportSourceDiscoveryException(
                "source_root_missing",
                "The import source root is missing.");
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static void EnsureNoControlDirectoryOverlap(string sourceRoot, ControlDataPaths controlDataPaths)
    {
        var controlDirectories = new[]
        {
            controlDataPaths.RuntimeDirectory,
            controlDataPaths.StateDirectory,
            controlDataPaths.ObjectDirectory,
            controlDataPaths.LogDirectory
        };

        foreach (var controlDirectory in controlDirectories.Select(Path.GetFullPath))
        {
            var normalizedControl = Path.TrimEndingDirectorySeparator(controlDirectory);
            if (IsSameOrInside(sourceRoot, normalizedControl) || IsSameOrInside(normalizedControl, sourceRoot))
            {
                throw new ImageImportSourceDiscoveryException(
                    "source_control_path_overlap",
                    "The import source root overlaps a controlled QiongTu data directory.");
            }
        }
    }

    private static void EnsurePathHasNoReparsePoint(
        string root,
        string candidate,
        string rootCode = "source_reparse_point_detected")
    {
        var rootFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidateFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (!IsSameOrInside(candidateFullPath, rootFullPath))
        {
            throw new ImageImportSourceDiscoveryException(
                "source_path_escape",
                "The import source path escaped its selected root.");
        }

        var relative = Path.GetRelativePath(rootFullPath, candidateFullPath);
        var current = rootFullPath;
        var rootAttributes = GetAttributesWithoutPathLeak(current, rootCode);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ImageImportSourceDiscoveryException(
                rootCode,
                "The import source root is a reparse point and will not be followed.");
        }

        if (relative == ".")
        {
            return;
        }

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var attributes = GetAttributesWithoutPathLeak(current, "source_entry_unavailable");
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ImageImportSourceDiscoveryException(
                    "source_reparse_point_detected",
                    "The import source contains a reparse point and will not be followed.");
            }
        }
    }

    private static FileAttributes GetAttributesWithoutPathLeak(string path, string unavailableCode)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ImageImportSourceDiscoveryException(
                unavailableCode,
                "The import source file-system entry is unavailable.");
        }
    }

    private static void RequireMatchingSnapshot(ImageImportSourceSnapshot expected, ImageImportSourceSnapshot actual)
    {
        if (expected.Length != actual.Length ||
            expected.LastWriteTimeUtc != actual.LastWriteTimeUtc ||
            NormalizeAttributes(expected.Attributes) != NormalizeAttributes(actual.Attributes) ||
            (expected.Identity is not null && actual.Identity is not null && expected.Identity != actual.Identity))
        {
            throw new ImageImportSourceDiscoveryException(
                "source_changed",
                "The import source changed while it was being copied.");
        }
    }

    private static FileAttributes NormalizeAttributes(FileAttributes attributes) =>
        attributes & ~(FileAttributes.Archive | FileAttributes.NotContentIndexed);

    private static bool IsCandidate(string path, IReadOnlySet<string> candidateExtensions) =>
        candidateExtensions.Contains(Path.GetExtension(path));

    private static string NormalizeRelativePathForOrdering(string relativePath) =>
        ImageImportSourceSecurity.NormalizeRelativePathForKey(relativePath);

    private static bool IsSameOrInside(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var relative = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        return relative == "." ||
               (!Path.IsPathRooted(relative) &&
                relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xffff) == 32 || (exception.HResult & 0xffff) == 33;

    private static bool IsDiskFull(IOException exception) =>
        (exception.HResult & 0xffff) is 39 or 112;

    private static string? TryGetPathIdentity(string path, SafeFileHandle? handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (handle is not null && !handle.IsInvalid)
        {
            return TryGetIdentityFromHandle(handle);
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return TryGetIdentityFromHandle(stream.SafeFileHandle);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? TryGetIdentityFromHandle(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            return null;
        }

        return FormatIdentity(information);
    }

    private static string FormatIdentity(ByHandleFileInformation information) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{information.VolumeSerialNumber:x8}:{information.FileIndexHigh:x8}{information.FileIndexLow:x8}");

    private static string GetResolvedPathFromHandle(SafeFileHandle handle)
    {
        const int maximumWindowsPathCharacters = 32_768;
        var buffer = new StringBuilder(maximumWindowsPathCharacters);
        var length = GetFinalPathNameByHandleW(
            handle,
            buffer,
            (uint)buffer.Capacity,
            0);
        if (length == 0 || length >= buffer.Capacity)
        {
            throw new ImageImportSourceDiscoveryException(
                "source_handle_path_unavailable",
                "The import source handle path could not be verified.");
        }

        var resolved = buffer.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            resolved = @"\\" + resolved[8..];
        }
        else if (resolved.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            resolved = resolved[4..];
        }

        return Path.GetFullPath(resolved);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private sealed record DirectoryFrame(string AbsolutePath, string RelativePath, int Depth);

    private sealed record PendingCandidate(string AbsolutePath, string RelativePath);
}
