namespace QiongTu.Control;

public sealed record ControlDataPaths(
    string RuntimeDirectory,
    string StateDirectory,
    string ObjectDirectory,
    string LogDirectory,
    string DiscoveryFile,
    string LockFile,
    string RuntimeDatabase,
    string BusinessDatabase)
{
    public static ControlDataPaths Create(string? runtimeDirectory = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The current user's local application data directory is unavailable.");
        }

        var applicationRoot = runtimeDirectory is null
            ? Path.Combine(localAppData, "QiongTu")
            : Path.GetFullPath(runtimeDirectory);
        var runtimeRoot = Path.Combine(applicationRoot, "runtime");
        var stateRoot = Path.Combine(applicationRoot, "state");
        var objectRoot = Path.Combine(applicationRoot, "objects");
        var logRoot = Path.Combine(applicationRoot, "logs", "workers");

        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(stateRoot);
        Directory.CreateDirectory(objectRoot);
        Directory.CreateDirectory(logRoot);

        return new ControlDataPaths(
            runtimeRoot,
            stateRoot,
            objectRoot,
            logRoot,
            Path.Combine(runtimeRoot, "control.json"),
            Path.Combine(runtimeRoot, "control.lock"),
            Path.Combine(stateRoot, "control-runtime.db"),
            Path.Combine(stateRoot, "qiongtu.db"));
    }
}
