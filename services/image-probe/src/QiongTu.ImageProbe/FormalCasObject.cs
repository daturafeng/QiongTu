using System.Security.Cryptography;
using QiongTu.Contracts;

namespace QiongTu.ImageProbe;

internal static class FormalCasObject
{
    public static FileStream OpenAndVerify(ImageProbeCasImageRequestHeader header)
    {
        var stream = Open(header.FormalObjectRoot, header.ObjectKey);
        try
        {
            Verify(stream, header.ExpectedSha256, header.ExpectedByteLength);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static FileStream OpenAndVerify(ImageProbeCasPositioningAuxRequestHeader header)
    {
        var stream = Open(header.FormalObjectRoot, header.ObjectKey);
        try
        {
            Verify(stream, header.ExpectedSha256, header.ExpectedByteLength);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static FileStream Open(string formalObjectRoot, string objectKey)
    {
        var formalRoot = Path.GetFullPath(formalObjectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(formalRoot))
        {
            throw new IOException("The formal object namespace is unavailable.");
        }

        EnsureNoReparsePoint(formalRoot, formalRoot);
        var relativeObjectPath = objectKey.Replace('/', Path.DirectorySeparatorChar);
        var objectPath = Path.GetFullPath(Path.Combine(formalRoot, relativeObjectPath));
        var prefix = formalRoot + Path.DirectorySeparatorChar;
        if (!objectPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new CasImageStructureException("formal_object_namespace_invalid");
        }

        EnsureNoReparsePoint(formalRoot, objectPath);
        return new FileStream(
            objectPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.RandomAccess);
    }

    private static void EnsureNoReparsePoint(string root, string target)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = Path.GetFullPath(target);
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(normalizedTarget, normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !normalizedTarget.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new CasImageStructureException("formal_object_namespace_invalid");
        }

        var current = normalizedRoot;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new CasImageStructureException("formal_object_reparse_detected");
        }

        if (string.Equals(normalizedTarget, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var relative = Path.GetRelativePath(normalizedRoot, normalizedTarget);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new CasImageStructureException("formal_object_reparse_detected");
            }
        }
    }

    private static void Verify(FileStream stream, string expectedSha256, long expectedByteLength)
    {
        if (stream.Length != expectedByteLength)
        {
            throw new CasImageStructureException("formal_object_integrity_failed");
        }

        stream.Position = 0;
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actualHash, expectedSha256, StringComparison.Ordinal))
        {
            throw new CasImageStructureException("formal_object_integrity_failed");
        }

        stream.Position = 0;
    }
}
