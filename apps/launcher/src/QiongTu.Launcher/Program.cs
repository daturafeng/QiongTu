using System.Text.Json;

namespace QiongTu.Launcher;

public static class Program
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.SequenceEqual(["--self-test"], StringComparer.Ordinal))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "ok",
                launcherApiVersion = QiongTu.Contracts.ContractVersions.LauncherApiV1,
                diagnosticSchema = LaunchDiagnosticSchema.V1,
                boundary = "current-user-named-pipe",
                elevationRequired = false,
                modifiesSystemDevices = false,
                ownsControlOrWorkers = false
            }, SerializerOptions));
            return 0;
        }

        var coordinator = new LauncherCoordinator();
        if (args.SequenceEqual(["--probe-only"], StringComparer.Ordinal))
        {
            Console.WriteLine(JsonSerializer.Serialize(coordinator.CreateProbeOnlyReport(), SerializerOptions));
            return 0;
        }

        if (args.Length != 0)
        {
            return 2;
        }

        ApplicationConfiguration.Initialize();
        var layout = InstalledLayout.Resolve(AppContext.BaseDirectory);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            LaunchAttemptResult result;
            try
            {
                result = coordinator.RunAttemptAsync(layout, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                MessageBox.Show(
                    "穹图启动诊断无法安全完成。未修改任何系统设备或驱动。",
                    "穹图启动诊断",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }

            if (result.Report.Outcome == "desktop-session-complete")
            {
                return 0;
            }

            var message = BuildUserMessage(result);
            var canRetry = LauncherRetryPolicy.CanRetry(attempt, result.RetryAllowed);
            var choice = MessageBox.Show(
                message,
                "穹图启动诊断",
                canRetry ? MessageBoxButtons.RetryCancel : MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            if (!canRetry || choice != DialogResult.Retry)
            {
                return 1;
            }
        }

        return 1;
    }

    public static class LauncherRetryPolicy
    {
        public const int MaximumAttempts = 2;

        public static bool CanRetry(int attempt, bool attemptAllowsRetry) =>
            attemptAllowsRetry && attempt > 0 && attempt < MaximumAttempts;
    }

    private static string BuildUserMessage(LaunchAttemptResult result)
    {
        var recommendation = result.Report.RecommendationCode == "virtual-display-compatibility"
            ? "检测到虚拟显示适配器，Electron 在窗口/图形阶段未就绪。请先保存当前远程工作，查看报告中的兼容建议；穹图不会自动停用设备或修改驱动。"
            : "Electron 桌面未正常就绪。穹图没有修改设备、驱动或后台任务。";
        return $"{recommendation}{Environment.NewLine}{Environment.NewLine}诊断报告：{result.ReportPath}";
    }
}
