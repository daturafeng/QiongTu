using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.Control;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.SequenceEqual([HardwareProbeChildProtocol.Argument], StringComparer.Ordinal))
        {
            Console.WriteLine(JsonSerializer.Serialize(
                NvidiaNativeProbe.Capture(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return 0;
        }

        if (args.Contains("--self-test", StringComparer.Ordinal))
        {
            WriteSelfTest();
            return 0;
        }

        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine(ContractVersions.ControlApiV1);
            return 0;
        }

        try
        {
            var options = ParseOptions(args);
            var paths = ControlDataPaths.Create(options.RuntimeDirectory);
            var registry = CreateWorkerRegistry(options.EnableLifecycleProbe, paths);
            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };

            var application = new ControlApplication(paths, registry);
            await application.RunAsync(shutdown.Token);
            return 0;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"QiongTu.Control could not acquire its runtime boundary: {exception.Message}");
            return 2;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"QiongTu.Control failed: {exception.Message}");
            return 1;
        }
    }

    public static ControlSelfTestResult CreateSelfTestResult()
    {
        var boundary = new ControlBoundary(
            ContractVersions.ControlApiV1,
            "named-pipe",
            "qiongtu-control-v1-<current-user>-<instance>",
            LanBindingAllowed: false);

        return new ControlSelfTestResult(
            ContractVersions.ControlApiV1,
            "ok",
            boundary,
            [
                ".NET 10 control boundary is loadable.",
                "The control API is restricted to current-user named pipes.",
                "The artifact service binds only a token-protected IPv4 loopback endpoint.",
                "The discovery file excludes artifact access tokens.",
                "SQLite owns the durable worker runtime ledger.",
                "Only registered worker types can be launched.",
                "Processing capability and worker admission are available through the current-user control pipe.",
                "NVIDIA and CUDA driver calls run in a bounded isolated child mode without developer tools.",
                "Production worker start checks fixed capability requirements before process creation."
            ]);
    }

    private static ControlOptions ParseOptions(IReadOnlyList<string> args)
    {
        string? runtimeDirectory = null;
        var enableLifecycleProbe = false;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--runtime-dir":
                    if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
                    {
                        throw new ArgumentException("--runtime-dir requires a path.");
                    }

                    runtimeDirectory = args[index];
                    break;
                case "--enable-lifecycle-probe":
                    enableLifecycleProbe = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown QiongTu.Control option: {args[index]}");
            }
        }

        return new ControlOptions(runtimeDirectory, enableLifecycleProbe);
    }

    private static WorkerRegistry CreateWorkerRegistry(bool enableLifecycleProbe, ControlDataPaths paths)
    {
        var registry = new WorkerRegistry();
        if (enableLifecycleProbe)
        {
            registry.Register(new WorkerDefinition(
                "lifecycle-probe",
                Path.Combine(Environment.SystemDirectory, "ping.exe"),
                ["-n", "120", "127.0.0.1"],
                paths.RuntimeDirectory));
        }

        return registry;
    }

    private static void WriteSelfTest()
    {
        var result = CreateSelfTestResult();
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });
        Console.WriteLine(json);
    }

    private sealed record ControlOptions(string? RuntimeDirectory, bool EnableLifecycleProbe);
}
