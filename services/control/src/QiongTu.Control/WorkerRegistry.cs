namespace QiongTu.Control;

public sealed record WorkerDefinition(
    string WorkerType,
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    WorkerResourceRequirements? ResourceRequirements = null);

public sealed record WorkerResourceRequirements(
    string Profile,
    int MinimumLogicalProcessors,
    long MinimumAvailableMemoryBytes,
    long MinimumAvailableDiskBytes,
    bool RequiresNvidia,
    int? MinimumCudaDriverApiVersion,
    long? MinimumTotalGpuMemoryBytes,
    long? MinimumFreeGpuMemoryBytes);

public sealed class WorkerRegistry
{
    private readonly Dictionary<string, WorkerDefinition> _definitions = new(StringComparer.Ordinal);

    public void Register(WorkerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.WorkerType) ||
            definition.WorkerType.Length > 128 ||
            definition.WorkerType.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException("A bounded worker type is required.", nameof(definition));
        }

        if (definition.Arguments.Count > 64 || definition.Arguments.Any(argument => argument.Length > 2_048))
        {
            throw new ArgumentException("The registered worker argument list exceeds its bound.", nameof(definition));
        }

        if (_definitions.Count >= 64)
        {
            throw new InvalidOperationException("The bounded worker registry is full.");
        }

        ValidateRequirements(definition.ResourceRequirements);

        _definitions.Add(definition.WorkerType, definition);
    }

    public bool TryGet(string workerType, out WorkerDefinition definition) =>
        _definitions.TryGetValue(workerType, out definition!);

    public IReadOnlyList<WorkerDefinition> List() =>
        _definitions.Values.OrderBy(item => item.WorkerType, StringComparer.Ordinal).ToArray();

    private static void ValidateRequirements(WorkerResourceRequirements? requirements)
    {
        if (requirements is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(requirements.Profile) || requirements.Profile.Length > 64 ||
            requirements.Profile.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')) ||
            requirements.MinimumLogicalProcessors < 0 ||
            requirements.MinimumAvailableMemoryBytes < 0 ||
            requirements.MinimumAvailableDiskBytes < 0 ||
            requirements.MinimumCudaDriverApiVersion is < 0 ||
            requirements.MinimumTotalGpuMemoryBytes is < 0 ||
            requirements.MinimumFreeGpuMemoryBytes is < 0 ||
            (!requirements.RequiresNvidia &&
             (requirements.MinimumCudaDriverApiVersion is not null ||
              requirements.MinimumTotalGpuMemoryBytes is not null ||
              requirements.MinimumFreeGpuMemoryBytes is not null)))
        {
            throw new ArgumentException("The worker resource requirements are invalid.", nameof(requirements));
        }
    }
}
