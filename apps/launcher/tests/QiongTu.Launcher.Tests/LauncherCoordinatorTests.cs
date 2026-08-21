using System.Diagnostics;

namespace QiongTu.Launcher.Tests;

[TestClass]
public sealed class LauncherCoordinatorTests
{
    [TestMethod]
    public async Task AbnormalElectronExitIsBoundedAndDoesNotTerminateUnrelatedBackgroundProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), $"qiongtu-launch-coordinator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var sentinel = StartCommand("ping -n 30 127.0.0.1");
        try
        {
            var coordinator = new LauncherCoordinator(
                new WindowsEnvironmentProbe(new EmptyDisplayAdapterReader()),
                new ExitingProcessRunner(),
                new LaunchDiagnosticWriter(root));
            var result = await coordinator.RunAttemptAsync(
                new InstalledDesktopLayout(true, "fixed-by-test-runner", "none"),
                CancellationToken.None);

            Assert.AreEqual("electron-exited-before-ready", result.Report.Outcome);
            Assert.IsTrue(result.RetryAllowed);
            Assert.IsFalse(sentinel.HasExited, "Launcher must never apply process-tree cleanup to unrelated Control/Worker processes.");
            Assert.IsTrue(File.Exists(result.ReportPath));
        }
        finally
        {
            if (!sentinel.HasExited)
            {
                sentinel.Kill(entireProcessTree: true);
                await sentinel.WaitForExitAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RetryPolicyAllowsOnlyOneUserInitiatedRetry()
    {
        Assert.IsTrue(Program.LauncherRetryPolicy.CanRetry(1, attemptAllowsRetry: true));
        Assert.IsFalse(Program.LauncherRetryPolicy.CanRetry(2, attemptAllowsRetry: true));
        Assert.IsFalse(Program.LauncherRetryPolicy.CanRetry(1, attemptAllowsRetry: false));
    }

    private static Process StartCommand(string command)
    {
        var commandInterpreter = Environment.GetEnvironmentVariable("COMSPEC")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = commandInterpreter,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(command);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The bounded test process did not start.");
    }

    private sealed class ExitingProcessRunner : IElectronProcessRunner
    {
        public Process Start(InstalledDesktopLayout layout, LauncherReadinessSession session) =>
            StartCommand("exit 7");
    }

    private sealed class EmptyDisplayAdapterReader : IDisplayAdapterReader
    {
        public IReadOnlyList<DisplayAdapterSnapshot> Read() => [];
    }
}
