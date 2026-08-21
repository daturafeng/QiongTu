using System.Text;
using System.Text.Json;

namespace QiongTu.Launcher;

public static class LaunchDiagnosticSchema
{
    public const string V1 = "qiongtu.launch-diagnostics.v1";
}

public sealed record DisplayAdapterSnapshot(
    string Description,
    string Provider,
    string DriverVersion,
    string DriverDate,
    string AdapterKind);

public sealed record LaunchEnvironmentSnapshot(
    string ProbeStatus,
    string OperatingSystem,
    string OperatingSystemVersion,
    string Architecture,
    string SessionKind,
    IReadOnlyList<DisplayAdapterSnapshot> DisplayAdapters);

public sealed record SanitizedLaunchEvent(
    int Sequence,
    string Stage,
    DateTimeOffset TimestampUtc);

public sealed record LaunchPrivacyDeclaration(
    bool UserNameIncluded,
    bool MachineNameIncluded,
    bool LocalPathsIncluded,
    bool CredentialsIncluded,
    bool RawImagePathsIncluded);

public sealed record LaunchDiagnosticReportV1(
    string SchemaVersion,
    Guid RunId,
    DateTimeOffset CreatedAtUtc,
    string ApplicationVersion,
    LaunchEnvironmentSnapshot Environment,
    string Outcome,
    string FailureCode,
    string LastStage,
    int? ElectronExitCode,
    string RecommendationCode,
    IReadOnlyList<SanitizedLaunchEvent> Events,
    LaunchPrivacyDeclaration Privacy);

public sealed class LaunchDiagnosticWriter
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _diagnosticDirectory;

    public LaunchDiagnosticWriter(string? diagnosticDirectory = null)
    {
        _diagnosticDirectory = diagnosticDirectory ?? GetDefaultDiagnosticDirectory();
    }

    public async Task<string> WriteAtomicallyAsync(
        LaunchDiagnosticReportV1 report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_diagnosticDirectory);
        var destination = Path.Combine(_diagnosticDirectory, $"launch-{report.RunId:N}.json");
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(report, SerializerOptions);
        await File.WriteAllTextAsync(temporary, json, Utf8WithoutBom, cancellationToken);
        File.Move(temporary, destination, overwrite: true);
        return destination;
    }

    private static string GetDefaultDiagnosticDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The current user's local application data directory is unavailable.");
        }

        return Path.Combine(localAppData, "QiongTu", "diagnostics", "launch");
    }
}
