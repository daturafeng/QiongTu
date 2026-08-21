namespace QiongTu.Contracts;

public sealed record LaunchReadinessEvent(
    string ApiVersion,
    string Nonce,
    int ProcessId,
    int Sequence,
    string Stage,
    DateTimeOffset TimestampUtc);

public static class LaunchReadinessStages
{
    public const string MainStarted = "main.started";
    public const string AppReady = "app.ready";
    public const string ControlConnecting = "control.connecting";
    public const string ControlConnected = "control.connected";
    public const string ControlUnavailable = "control.unavailable";
    public const string BrowserWindowCreating = "browser-window.creating";
    public const string RendererLoaded = "renderer.loaded";
    public const string ReadyToShow = "ready-to-show";
    public const string RendererFailed = "renderer.failed";
    public const string GpuProcessFailed = "gpu-process.failed";
    public const string ExistingInstance = "existing-instance";

    public static bool IsKnown(string stage) => stage is
        MainStarted or
        AppReady or
        ControlConnecting or
        ControlConnected or
        ControlUnavailable or
        BrowserWindowCreating or
        RendererLoaded or
        ReadyToShow or
        RendererFailed or
        GpuProcessFailed or
        ExistingInstance;
}
