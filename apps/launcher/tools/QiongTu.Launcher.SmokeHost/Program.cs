using System.Diagnostics;
using System.Text.Json;
using QiongTu.Launcher;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: QiongTu.Launcher.SmokeHost <electron.exe> <desktop-directory>");
    return 2;
}

var electronExecutable = Path.GetFullPath(args[0]);
var desktopDirectory = Path.GetFullPath(args[1]);
if (!File.Exists(electronExecutable)
    || !string.Equals(Path.GetFileName(electronExecutable), "electron.exe", StringComparison.OrdinalIgnoreCase)
    || !File.Exists(Path.Combine(desktopDirectory, "package.json")))
{
    Console.Error.WriteLine("The development Electron layout is invalid.");
    return 2;
}

var runDirectory = Path.Combine(
    Path.GetTempPath(),
    $"qiongtu-launcher-readiness-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(runDirectory);
var session = LauncherReadinessSession.Create();
var server = new LauncherReadinessServer(session);
var expectedProcessId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
var readinessTask = server.WaitForReadinessAsync(
    expectedProcessId.Task,
    TimeSpan.FromSeconds(20),
    timeout.Token);
Process? electron = null;
try
{
    var startInfo = new ProcessStartInfo
    {
        FileName = electronExecutable,
        WorkingDirectory = desktopDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("--noerrdialogs");
    startInfo.ArgumentList.Add("--disable-breakpad");
    startInfo.ArgumentList.Add($"--user-data-dir={Path.Combine(runDirectory, "user-data")}");
    startInfo.ArgumentList.Add(desktopDirectory);
    startInfo.ArgumentList.Add("--launcher-readiness-smoke");
    startInfo.Environment[ElectronProcessRunner.PipeEnvironmentKey] = session.PipeName;
    startInfo.Environment[ElectronProcessRunner.NonceEnvironmentKey] = session.Nonce;
    electron = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Electron smoke process did not start.");
    expectedProcessId.SetResult(electron.Id);
    var stdoutTask = electron.StandardOutput.ReadToEndAsync(timeout.Token);
    var stderrTask = electron.StandardError.ReadToEndAsync(timeout.Token);
    var readiness = await readinessTask;
    if (readiness.Outcome == "ready")
    {
        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await electron.WaitForExitAsync(exitTimeout.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    if (!electron.HasExited)
    {
        electron.Kill(entireProcessTree: true);
        await electron.WaitForExitAsync(CancellationToken.None);
    }

    var stdout = await stdoutTask;
    var stderr = await stderrTask;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        readiness.Outcome,
        readiness.FailureCode,
        readiness.LastStage,
        stages = readiness.Events.Select(item => item.Stage).ToArray(),
        electronExitCode = electron.ExitCode,
        standardOutputEmpty = string.IsNullOrWhiteSpace(stdout),
        standardErrorPresent = !string.IsNullOrWhiteSpace(stderr)
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    return readiness.Outcome == "ready" ? 0 : 10;
}
finally
{
    if (electron is not null)
    {
        if (!electron.HasExited)
        {
            electron.Kill(entireProcessTree: true);
            electron.WaitForExit();
        }
        electron.Dispose();
    }

    var expectedPrefix = Path.Combine(Path.GetTempPath(), "qiongtu-launcher-readiness-smoke-");
    var resolvedRunDirectory = Path.GetFullPath(runDirectory);
    if (resolvedRunDirectory.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
        && Directory.Exists(resolvedRunDirectory))
    {
        Directory.Delete(resolvedRunDirectory, recursive: true);
    }
}
