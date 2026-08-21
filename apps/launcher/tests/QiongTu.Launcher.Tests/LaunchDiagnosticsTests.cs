using System.Text;

namespace QiongTu.Launcher.Tests;

[TestClass]
public sealed class LaunchDiagnosticsTests
{
    [TestMethod]
    public async Task WritesBomFreeReportWithoutIdentityPathsOrCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), $"qiongtu-launch-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var report = new LaunchDiagnosticReportV1(
                LaunchDiagnosticSchema.V1,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                "0.1.0",
                new LaunchEnvironmentSnapshot(
                    "available",
                    "Windows",
                    "10.0.19045",
                    "x64",
                    "console",
                    [new DisplayAdapterSnapshot("OrayIddDriver Device", "Oray", "17.50", "2025-06-11", "virtual-display")]),
                "electron-not-ready",
                "electron-reported-failure",
                "gpu-process.failed",
                unchecked((int)0xc0000135),
                "virtual-display-compatibility",
                [new SanitizedLaunchEvent(1, "main.started", DateTimeOffset.UtcNow)],
                new LaunchPrivacyDeclaration(false, false, false, false, false));
            var path = await new LaunchDiagnosticWriter(root).WriteAtomicallyAsync(report, CancellationToken.None);
            var bytes = await File.ReadAllBytesAsync(path);
            var json = Encoding.UTF8.GetString(bytes);

            Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf);
            Assert.IsFalse(json.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(json.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(json.Contains("qiongtu-launch-v1-", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(json.Contains("nonce", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(json.Contains("bearer", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(json.Contains(root, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void EnvironmentProbeDegradesWithoutElevationOrFailure()
    {
        var probe = new WindowsEnvironmentProbe(new ThrowingDisplayAdapterReader());
        var result = probe.Capture();

        Assert.AreEqual("unavailable", result.ProbeStatus);
        Assert.AreEqual("unknown", result.SessionKind);
        Assert.IsEmpty(result.DisplayAdapters);
    }

    [TestMethod]
    public void InstalledLayoutAcceptsOnlyTheFixedDesktopExecutableName()
    {
        var root = Path.Combine(Path.GetTempPath(), $"qiongtu-layout-{Guid.NewGuid():N}");
        var desktop = Path.Combine(root, "desktop");
        Directory.CreateDirectory(desktop);
        try
        {
            File.WriteAllBytes(Path.Combine(desktop, "QiongTu.exe"), []);
            var valid = InstalledLayout.Resolve(root, new FixedTrustVerifier(isTrusted: true));
            Assert.IsTrue(valid.IsValid);

            var untrusted = InstalledLayout.Resolve(root, new FixedTrustVerifier(isTrusted: false));
            Assert.IsFalse(untrusted.IsValid);
            Assert.AreEqual("desktop-signature-invalid", untrusted.FailureCode);

            File.Delete(Path.Combine(desktop, "QiongTu.exe"));
            var missing = InstalledLayout.Resolve(root, new FixedTrustVerifier(isTrusted: true));
            Assert.IsFalse(missing.IsValid);
            Assert.AreEqual("desktop-executable-missing", missing.FailureCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void WinTrustAcceptsSignedWindowsExecutableAndRejectsUnsignedFile()
    {
        var verifier = new WinTrustExecutableVerifier();
        var signedExecutable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet",
            "dotnet.exe");
        var trustStatus = verifier.GetTrustStatus(signedExecutable);
        Assert.AreEqual(0, trustStatus, $"WinVerifyTrust returned 0x{trustStatus:X8} for Authenticode-signed dotnet.exe.");

        var unsignedFile = Path.Combine(Path.GetTempPath(), $"qiongtu-unsigned-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(unsignedFile, [0x4d, 0x5a]);
            Assert.IsFalse(verifier.IsTrusted(unsignedFile));
        }
        finally
        {
            File.Delete(unsignedFile);
        }
    }

    private sealed class ThrowingDisplayAdapterReader : IDisplayAdapterReader
    {
        public IReadOnlyList<DisplayAdapterSnapshot> Read() =>
            throw new UnauthorizedAccessException("simulated");
    }

    private sealed class FixedTrustVerifier(bool isTrusted) : IExecutableTrustVerifier
    {
        public bool IsTrusted(string executablePath) => isTrusted;
    }
}
