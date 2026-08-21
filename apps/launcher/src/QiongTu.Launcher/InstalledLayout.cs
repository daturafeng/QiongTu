namespace QiongTu.Launcher;

public sealed record InstalledDesktopLayout(
    bool IsValid,
    string ElectronExecutable,
    string FailureCode);

public static class InstalledLayout
{
    public static InstalledDesktopLayout Resolve(
        string launcherDirectory,
        IExecutableTrustVerifier? trustVerifier = null)
    {
        var root = Path.GetFullPath(launcherDirectory);
        var desktopDirectory = Path.GetFullPath(Path.Combine(root, "desktop"));
        var executable = Path.GetFullPath(Path.Combine(desktopDirectory, "QiongTu.exe"));
        var expectedPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!executable.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(executable), "QiongTu.exe", StringComparison.OrdinalIgnoreCase))
        {
            return new InstalledDesktopLayout(false, string.Empty, "layout-outside-install-root");
        }

        if (!File.Exists(executable))
        {
            return new InstalledDesktopLayout(false, string.Empty, "desktop-executable-missing");
        }

        var verifier = trustVerifier ?? new WinTrustExecutableVerifier();
        return verifier.IsTrusted(executable)
            ? new InstalledDesktopLayout(true, executable, "none")
            : new InstalledDesktopLayout(false, string.Empty, "desktop-signature-invalid");
    }
}
