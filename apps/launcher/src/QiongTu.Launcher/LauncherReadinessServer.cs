using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.Launcher;

public sealed record LauncherReadinessSession(string PipeName, string Nonce)
{
    public static LauncherReadinessSession Create()
    {
        var userScope = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)).AsSpan(0, 6))
            .ToLowerInvariant();
        var instance = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return new LauncherReadinessSession($"qiongtu-launch-v1-{userScope}-{instance}", nonce);
    }
}

public sealed record LauncherReadinessResult(
    string Outcome,
    string FailureCode,
    string LastStage,
    IReadOnlyList<SanitizedLaunchEvent> Events);

public sealed class LauncherReadinessServer
{
    public const int MaximumMessageCharacters = 64 * 1024;
    public const int MaximumEvents = 32;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly LauncherReadinessSession _session;

    public LauncherReadinessServer(LauncherReadinessSession session)
    {
        _session = session;
    }

    public async Task<LauncherReadinessResult> WaitForReadinessAsync(
        Task<int> expectedProcessIdTask,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedProcessIdTask);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var events = new List<SanitizedLaunchEvent>();
        var lastSequence = 0;
        var lastStage = "not-started";
        try
        {
            await using var pipe = new NamedPipeServerStream(
                _session.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var expectedProcessId = await expectedProcessIdTask.WaitAsync(timeoutCancellation.Token);
            if (expectedProcessId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(expectedProcessIdTask));
            }
            await pipe.WaitForConnectionAsync(timeoutCancellation.Token);
            using var reader = new StreamReader(
                pipe,
                new UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            while (events.Count < MaximumEvents)
            {
                var line = await ReadBoundedLineAsync(
                    reader,
                    MaximumMessageCharacters,
                    timeoutCancellation.Token);
                if (line is null)
                {
                    return Failure("connection-closed", lastStage, events);
                }

                LaunchReadinessEvent? launchEvent;
                try
                {
                    launchEvent = JsonSerializer.Deserialize<LaunchReadinessEvent>(line, SerializerOptions);
                }
                catch (JsonException)
                {
                    return Failure("invalid-json", lastStage, events);
                }

                if (launchEvent is null
                    || launchEvent.ApiVersion != ContractVersions.LauncherApiV1
                    || launchEvent.ProcessId != expectedProcessId
                    || launchEvent.Sequence != lastSequence + 1
                    || !LaunchReadinessStages.IsKnown(launchEvent.Stage)
                    || !FixedTimeNonceEquals(_session.Nonce, launchEvent.Nonce))
                {
                    return Failure("invalid-event", lastStage, events);
                }

                lastSequence = launchEvent.Sequence;
                lastStage = launchEvent.Stage;
                events.Add(new SanitizedLaunchEvent(
                    launchEvent.Sequence,
                    launchEvent.Stage,
                    launchEvent.TimestampUtc));
                if (launchEvent.Stage is LaunchReadinessStages.ReadyToShow
                    or LaunchReadinessStages.ExistingInstance)
                {
                    return new LauncherReadinessResult("ready", "none", lastStage, events);
                }

                if (launchEvent.Stage is LaunchReadinessStages.RendererFailed
                    or LaunchReadinessStages.GpuProcessFailed)
                {
                    return Failure("electron-reported-failure", lastStage, events);
                }
            }

            return Failure("too-many-events", lastStage, events);
        }
        catch (LauncherProtocolException exception)
        {
            return Failure(exception.Code, lastStage, events);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            return Failure(cancellationToken.IsCancellationRequested ? "cancelled" : "readiness-timeout", lastStage, events);
        }
        catch (IOException)
        {
            return Failure("pipe-io-failure", lastStage, events);
        }
    }

    internal static async Task<string?> ReadBoundedLineAsync(
        TextReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 1024));
        var buffer = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            var character = buffer[0];
            if (character == '\n')
            {
                return builder.ToString().TrimEnd('\r');
            }

            if (builder.Length >= maximumCharacters)
            {
                throw new LauncherProtocolException("message-too-large");
            }

            builder.Append(character);
        }
    }

    private static bool FixedTimeNonceEquals(string expected, string actual)
    {
        if (expected.Length != actual.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(actual));
    }

    private static LauncherReadinessResult Failure(
        string code,
        string lastStage,
        IReadOnlyList<SanitizedLaunchEvent> events) =>
        new("failed", code, lastStage, events.ToArray());

    private sealed class LauncherProtocolException(string code) : Exception
    {
        public string Code { get; } = code;
    }
}
