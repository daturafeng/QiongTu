using System.Diagnostics;
using System.Reflection;

namespace QiongTu.Launcher;

public sealed record LaunchAttemptResult(
    LaunchDiagnosticReportV1 Report,
    string ReportPath,
    bool RetryAllowed);

public sealed class LauncherCoordinator
{
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(20);
    private readonly WindowsEnvironmentProbe _environmentProbe;
    private readonly IElectronProcessRunner _processRunner;
    private readonly LaunchDiagnosticWriter _reportWriter;
    private readonly Func<DateTimeOffset> _now;

    public LauncherCoordinator(
        WindowsEnvironmentProbe? environmentProbe = null,
        IElectronProcessRunner? processRunner = null,
        LaunchDiagnosticWriter? reportWriter = null,
        Func<DateTimeOffset>? now = null)
    {
        _environmentProbe = environmentProbe ?? new WindowsEnvironmentProbe();
        _processRunner = processRunner ?? new ElectronProcessRunner();
        _reportWriter = reportWriter ?? new LaunchDiagnosticWriter();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<LaunchAttemptResult> RunAttemptAsync(
        InstalledDesktopLayout layout,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        var environment = _environmentProbe.Capture();
        if (!layout.IsValid)
        {
            return await WriteResultAsync(
                runId,
                environment,
                "installation-invalid",
                layout.FailureCode,
                "not-started",
                null,
                "repair-installation",
                [],
                retryAllowed: false,
                cancellationToken);
        }

        var session = LauncherReadinessSession.Create();
        var server = new LauncherReadinessServer(session);
        var expectedProcessId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readinessTask = server.WaitForReadinessAsync(
            expectedProcessId.Task,
            ReadinessTimeout,
            attemptCancellation.Token);
        Process process;
        try
        {
            process = _processRunner.Start(layout, session);
            expectedProcessId.SetResult(process.Id);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or System.ComponentModel.Win32Exception)
        {
            expectedProcessId.TrySetCanceled(cancellationToken);
            attemptCancellation.Cancel();
            return await WriteResultAsync(
                runId,
                environment,
                "electron-start-failed",
                "electron-start-failed",
                "not-started",
                null,
                "repair-installation",
                [],
                retryAllowed: true,
                cancellationToken);
        }

        using (process)
        {
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var firstCompleted = await Task.WhenAny(readinessTask, exitTask);
            if (firstCompleted == exitTask && !readinessTask.IsCompleted)
            {
                attemptCancellation.Cancel();
            }

            var readiness = await readinessTask;
            if (readiness.Outcome == "ready")
            {
                await exitTask;
                return await WriteResultAsync(
                    runId,
                    environment,
                    process.ExitCode == 0 ? "desktop-session-complete" : "electron-exited-after-ready",
                    process.ExitCode == 0 ? "none" : "electron-exited-after-ready",
                    readiness.LastStage,
                    process.ExitCode,
                    process.ExitCode == 0 ? "none" : "collect-launch-diagnostics",
                    readiness.Events,
                    retryAllowed: process.ExitCode != 0,
                    cancellationToken);
            }

            int? exitCode = process.HasExited ? process.ExitCode : null;
            var recommendation = SelectRecommendation(environment, readiness.LastStage, readiness.FailureCode);
            return await WriteResultAsync(
                runId,
                environment,
                process.HasExited ? "electron-exited-before-ready" : "electron-not-ready",
                readiness.FailureCode,
                readiness.LastStage,
                exitCode,
                recommendation,
                readiness.Events,
                retryAllowed: process.HasExited,
                cancellationToken);
        }
    }

    public LaunchDiagnosticReportV1 CreateProbeOnlyReport()
    {
        var environment = _environmentProbe.Capture();
        return CreateReport(
            Guid.NewGuid(),
            environment,
            "probe-only",
            environment.ProbeStatus == "available" ? "none" : "environment-probe-unavailable",
            "not-started",
            null,
            environment.ProbeStatus == "available" ? "none" : "environment-probe-unavailable",
            []);
    }

    private async Task<LaunchAttemptResult> WriteResultAsync(
        Guid runId,
        LaunchEnvironmentSnapshot environment,
        string outcome,
        string failureCode,
        string lastStage,
        int? exitCode,
        string recommendation,
        IReadOnlyList<SanitizedLaunchEvent> events,
        bool retryAllowed,
        CancellationToken cancellationToken)
    {
        var report = CreateReport(
            runId,
            environment,
            outcome,
            failureCode,
            lastStage,
            exitCode,
            recommendation,
            events);
        var path = await _reportWriter.WriteAtomicallyAsync(report, cancellationToken);
        return new LaunchAttemptResult(report, path, retryAllowed);
    }

    private LaunchDiagnosticReportV1 CreateReport(
        Guid runId,
        LaunchEnvironmentSnapshot environment,
        string outcome,
        string failureCode,
        string lastStage,
        int? exitCode,
        string recommendation,
        IReadOnlyList<SanitizedLaunchEvent> events) =>
        new(
            LaunchDiagnosticSchema.V1,
            runId,
            _now(),
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            environment,
            outcome,
            failureCode,
            lastStage,
            exitCode,
            recommendation,
            events.ToArray(),
            new LaunchPrivacyDeclaration(false, false, false, false, false));

    private static string SelectRecommendation(
        LaunchEnvironmentSnapshot environment,
        string lastStage,
        string failureCode)
    {
        var hasVirtualDisplay = environment.DisplayAdapters.Any(
            adapter => adapter.AdapterKind == "virtual-display");
        if (hasVirtualDisplay && lastStage is "browser-window.creating" or "gpu-process.failed" or "renderer.failed")
        {
            return "virtual-display-compatibility";
        }

        return failureCode == "readiness-timeout"
            ? "desktop-readiness-timeout"
            : "collect-launch-diagnostics";
    }
}
