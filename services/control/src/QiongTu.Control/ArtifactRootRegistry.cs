namespace QiongTu.Control;

public sealed class ArtifactRootRegistry
{
    private readonly Dictionary<string, string> _roots = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void RegisterTrustedRoot(string rootId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (rootId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Artifact root identifiers may only contain ASCII letters, digits, '-' and '_'.", nameof(rootId));
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(fullPath);
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("Artifact roots cannot be reparse points.", nameof(path));
        }

        lock (_gate)
        {
            _roots[rootId] = fullPath;
        }
    }

    public bool TryResolveFile(string rootId, string relativePath, out string filePath)
    {
        filePath = string.Empty;
        string root;
        lock (_gate)
        {
            if (!_roots.TryGetValue(rootId, out root!))
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(candidate)
            || ContainsReparsePoint(root, candidate))
        {
            return false;
        }

        filePath = candidate;
        return true;
    }

    public bool TryOpenRead(string rootId, string relativePath, out FileStream? stream)
    {
        stream = null;
        if (!TryResolveFile(rootId, relativePath, out var filePath))
        {
            return false;
        }

        try
        {
            stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            stream = null;
            return false;
        }
    }

    private static bool ContainsReparsePoint(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
