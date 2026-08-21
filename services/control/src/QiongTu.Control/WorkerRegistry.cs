namespace QiongTu.Control;

public sealed record WorkerDefinition(
    string WorkerType,
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

public sealed class WorkerRegistry
{
    private readonly Dictionary<string, WorkerDefinition> _definitions = new(StringComparer.Ordinal);

    public void Register(WorkerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.WorkerType))
        {
            throw new ArgumentException("Worker type is required.", nameof(definition));
        }

        _definitions.Add(definition.WorkerType, definition);
    }

    public bool TryGet(string workerType, out WorkerDefinition definition) =>
        _definitions.TryGetValue(workerType, out definition!);
}
