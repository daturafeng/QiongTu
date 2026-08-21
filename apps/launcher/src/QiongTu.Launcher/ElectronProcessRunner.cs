using System.Diagnostics;

namespace QiongTu.Launcher;

public interface IElectronProcessRunner
{
    Process Start(InstalledDesktopLayout layout, LauncherReadinessSession session);
}

public sealed class ElectronProcessRunner : IElectronProcessRunner
{
    public const string PipeEnvironmentKey = "QIONGTU_LAUNCH_PIPE_NAME";
    public const string NonceEnvironmentKey = "QIONGTU_LAUNCH_NONCE";

    public Process Start(InstalledDesktopLayout layout, LauncherReadinessSession session)
    {
        if (!layout.IsValid || string.IsNullOrWhiteSpace(layout.ElectronExecutable))
        {
            throw new InvalidOperationException("The installed desktop layout is not valid.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = layout.ElectronExecutable,
            WorkingDirectory = Path.GetDirectoryName(layout.ElectronExecutable)
                ?? throw new InvalidOperationException("The desktop executable directory is unavailable."),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment[PipeEnvironmentKey] = session.PipeName;
        startInfo.Environment[NonceEnvironmentKey] = session.Nonce;
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Electron desktop process did not start.");
    }
}
