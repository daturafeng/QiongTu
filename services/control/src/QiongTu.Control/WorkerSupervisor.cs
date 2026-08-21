using System.Collections.Concurrent;
using System.Diagnostics;
using QiongTu.Contracts;

namespace QiongTu.Control;

public sealed class WorkerSupervisor : IDisposable
{
    public const string StartingState = "starting";
    public const string RunningState = "running";
    public const string CancellingState = "cancelling";
    public const string CancelledState = "cancelled";
    public const string SucceededState = "succeeded";
    public const string FailedState = "failed";
    public const string LostState = "lost";

    private readonly WorkerRegistry _registry;
    private readonly WorkerRuntimeStore _store;
    private readonly string _logDirectory;
    private readonly ConcurrentDictionary<string, Process> _processes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _cancelRequested = new(StringComparer.Ordinal);
    private bool _disposed;

    public WorkerSupervisor(WorkerRegistry registry, WorkerRuntimeStore store, string logDirectory)
    {
        _registry = registry;
        _store = store;
        _logDirectory = logDirectory;
        Directory.CreateDirectory(logDirectory);
    }

    public int ActiveCount => List().Count(worker => worker.State is StartingState or RunningState or CancellingState);

    public IReadOnlyList<WorkerSnapshot> List() => _store.List();

    public void ReconcilePersistedWorkers()
    {
        foreach (var persisted in _store.ListPersisted()
                     .Where(item => item.Snapshot.State is StartingState or RunningState or CancellingState))
        {
            var worker = persisted.Snapshot;
            if (!_registry.TryGet(worker.WorkerType, out var definition)
                || worker.ProcessId is null
                || !TryAttachToExpectedProcess(persisted, definition, out var process))
            {
                _store.Upsert(worker with
                {
                    State = LostState,
                    EndedAtUtc = DateTimeOffset.UtcNow
                });
                continue;
            }

            _processes[worker.WorkerId] = process;
            Monitor(worker, process, captureOutput: false);
        }
    }

    public WorkerSnapshot Start(string workerType)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_registry.TryGet(workerType, out var definition))
        {
            throw new ControlProtocolException("worker_not_registered", "The requested worker type is not registered.");
        }

        var workerId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        var startInfo = new ProcessStartInfo
        {
            FileName = definition.FileName,
            WorkingDirectory = definition.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in definition.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var starting = new WorkerSnapshot(
            workerId,
            workerType,
            StartingState,
            null,
            startedAt,
            null,
            null);
        _store.Upsert(starting);

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The registered worker did not start.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            TryStore(starting with { State = FailedState, EndedAtUtc = DateTimeOffset.UtcNow });
            throw new ControlProtocolException("worker_start_failed", "The registered worker did not start.");
        }

        var processStartedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        var executablePath = Path.GetFullPath(process.MainModule?.FileName ?? definition.FileName);

        var snapshot = new WorkerSnapshot(
            workerId,
            workerType,
            RunningState,
            process.Id,
            startedAt,
            null,
            null);
        try
        {
            _store.Upsert(snapshot, executablePath, processStartedAtUtc);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
            }
            finally
            {
                process.Dispose();
            }

            TryStore(starting with { State = FailedState, EndedAtUtc = DateTimeOffset.UtcNow });
            throw;
        }

        _processes[workerId] = process;
        Monitor(snapshot, process, captureOutput: true);
        return snapshot;
    }

    public async Task<WorkerSnapshot> CancelAsync(string workerId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _store.List().SingleOrDefault(worker => worker.WorkerId == workerId)
            ?? throw new ControlProtocolException("worker_not_found", "The requested worker does not exist.");
        if (snapshot.State is not RunningState and not CancellingState)
        {
            return snapshot;
        }

        _cancelRequested[workerId] = 0;
        snapshot = snapshot with { State = CancellingState };
        _store.Upsert(snapshot);

        Process? process = null;
        try
        {
            if (!_processes.TryGetValue(workerId, out process) || HasExitedSafely(process))
            {
                var ended = snapshot with { State = CancelledState, EndedAtUtc = DateTimeOffset.UtcNow };
                _store.Upsert(ended);
                return ended;
            }

            try
            {
                _ = process.CloseMainWindow();
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                try
                {
                    if (!HasExitedSafely(process))
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }

                await process.WaitForExitAsync();
            }

            var cancelled = snapshot with
            {
                State = CancelledState,
                EndedAtUtc = DateTimeOffset.UtcNow
            };
            _store.Upsert(cancelled);
            return cancelled;
        }
        finally
        {
            _cancelRequested.TryRemove(workerId, out _);
            if (process is not null && HasExitedSafely(process))
            {
                process.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var process in _processes.Values)
        {
            process.Dispose();
        }

        _processes.Clear();
    }

    private void Monitor(WorkerSnapshot initial, Process process, bool captureOutput)
    {
        _ = Task.Run(async () =>
        {
            Task? standardOutput = null;
            Task? standardError = null;
            if (captureOutput)
            {
                standardOutput = DrainToBoundedLogAsync(
                    process.StandardOutput,
                    Path.Combine(_logDirectory, $"{initial.WorkerId}.stdout.log"));
                standardError = DrainToBoundedLogAsync(
                    process.StandardError,
                    Path.Combine(_logDirectory, $"{initial.WorkerId}.stderr.log"));
            }

            try
            {
                await process.WaitForExitAsync();
                if (standardOutput is not null && standardError is not null)
                {
                    await Task.WhenAll(standardOutput, standardError);
                }

                var cancelled = _cancelRequested.ContainsKey(initial.WorkerId);
                TryStore(initial with
                {
                    State = cancelled ? CancelledState : process.ExitCode == 0 ? SucceededState : FailedState,
                    EndedAtUtc = DateTimeOffset.UtcNow,
                    ExitCode = process.ExitCode
                });
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                TryStore(initial with { State = LostState, EndedAtUtc = DateTimeOffset.UtcNow });
            }
            finally
            {
                _processes.TryRemove(initial.WorkerId, out _);
                if (!_cancelRequested.ContainsKey(initial.WorkerId))
                {
                    process.Dispose();
                }
            }
        });
    }

    private static bool TryAttachToExpectedProcess(
        PersistedWorker persisted,
        WorkerDefinition definition,
        out Process process)
    {
        process = null!;
        try
        {
            var worker = persisted.Snapshot;
            if (persisted.ExecutablePath is null || persisted.ProcessStartedAtUtc is null)
            {
                return false;
            }

            var candidate = Process.GetProcessById(worker.ProcessId!.Value);
            var processStartedAt = new DateTimeOffset(candidate.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var actualExecutable = Path.GetFullPath(candidate.MainModule?.FileName ?? string.Empty);
            var registeredExecutable = Path.GetFullPath(definition.FileName);
            if (Math.Abs((processStartedAt - persisted.ProcessStartedAtUtc.Value).TotalSeconds) > 1
                || !string.Equals(actualExecutable, persisted.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(actualExecutable, registeredExecutable, StringComparison.OrdinalIgnoreCase))
            {
                candidate.Dispose();
                return false;
            }

            process = candidate;
            return !candidate.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task DrainToBoundedLogAsync(TextReader reader, string logPath)
    {
        const int maximumLogCharacters = 8 * 1024 * 1024;
        var buffer = new char[8192];
        var written = 0;
        StreamWriter? writer = null;
        try
        {
            writer = new StreamWriter(new FileStream(
                logPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 8192,
                useAsync: true));
            while (true)
            {
                var count = await reader.ReadAsync(buffer);
                if (count == 0)
                {
                    break;
                }

                var remaining = maximumLogCharacters - written;
                if (remaining > 0)
                {
                    var writeCount = Math.Min(count, remaining);
                    await writer.WriteAsync(buffer.AsMemory(0, writeCount));
                    written += writeCount;
                }
            }

            await writer.FlushAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            while (await reader.ReadAsync(buffer) != 0)
            {
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync();
            }
        }
    }

    private void TryStore(WorkerSnapshot snapshot)
    {
        try
        {
            _store.Upsert(snapshot);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool HasExitedSafely(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}

public sealed class ControlProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
