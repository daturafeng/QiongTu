using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.Control;

public sealed class NamedPipeControlServer : IAsyncDisposable
{
    public const int MaximumMessageCharacters = 64 * 1024;
    public const int MaximumResponseBytes = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName;
    private readonly ControlRequestDispatcher _dispatcher;
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;
    private int _nextClientId;

    public NamedPipeControlServer(string pipeName, ControlRequestDispatcher dispatcher)
    {
        _pipeName = pipeName;
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        if (_acceptLoop is not null)
        {
            throw new InvalidOperationException("The named-pipe server has already started.");
        }

        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await Task.WhenAll(_clients.Values);
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }

            var clientId = Interlocked.Increment(ref _nextClientId);
            var task = HandleClientAsync(pipe, cancellationToken);
            _clients[clientId] = task;
            _ = task.ContinueWith(
                completedTask => _clients.TryRemove(clientId, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            await using (pipe)
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true))
            await using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
            {
                while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    string? line;
                    try
                    {
                        line = await ReadBoundedLineAsync(reader, MaximumMessageCharacters, cancellationToken);
                    }
                    catch (ControlProtocolException exception)
                    {
                        await writer.WriteLineAsync(SerializeBoundedResponse(
                            new ControlResponse(
                                ContractVersions.ControlApiV1,
                                string.Empty,
                                false,
                                null,
                                new ControlError(exception.Code, exception.Message))));
                        break;
                    }

                    if (line is null)
                    {
                        break;
                    }

                    ControlResponse response;
                    try
                    {
                        var request = JsonSerializer.Deserialize<ControlRequest>(line, SerializerOptions)
                            ?? throw new JsonException();
                        response = await _dispatcher.DispatchAsync(request, cancellationToken);
                    }
                    catch (JsonException)
                    {
                        response = new ControlResponse(
                            ContractVersions.ControlApiV1,
                            string.Empty,
                            false,
                            null,
                            new ControlError("invalid_json", "The request is not valid control JSON."));
                    }

                    await writer.WriteLineAsync(SerializeBoundedResponse(response));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // A desktop client may disconnect immediately after receiving a response. The connection ends,
            // but the control service and other current-user clients must remain available.
        }
    }

    internal static string SerializeBoundedResponse(ControlResponse response)
    {
        var json = JsonSerializer.Serialize(response, SerializerOptions);
        if (Encoding.UTF8.GetByteCount(json) <= MaximumResponseBytes)
        {
            return json;
        }

        return JsonSerializer.Serialize(
            new ControlResponse(
                ContractVersions.ControlApiV1,
                response.RequestId,
                false,
                null,
                new ControlError(
                    "response_too_large",
                    "The control response exceeds the bounded message size; request a smaller page.")),
            SerializerOptions);
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
                throw new ControlProtocolException("message_too_large", "The control message exceeds the size limit.");
            }

            builder.Append(character);
        }
    }
}
