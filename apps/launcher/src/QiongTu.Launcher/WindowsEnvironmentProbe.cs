using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QiongTu.Launcher;

public interface IDisplayAdapterReader
{
    IReadOnlyList<DisplayAdapterSnapshot> Read();
}

public sealed class WindowsEnvironmentProbe
{
    private readonly IDisplayAdapterReader _displayAdapters;

    public WindowsEnvironmentProbe(IDisplayAdapterReader? displayAdapters = null)
    {
        _displayAdapters = displayAdapters ?? new RegistryDisplayAdapterReader();
    }

    public LaunchEnvironmentSnapshot Capture()
    {
        try
        {
            var adapters = _displayAdapters.Read();
            return new LaunchEnvironmentSnapshot(
                "available",
                RuntimeInformation.OSDescription,
                Environment.OSVersion.Version.ToString(),
                RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                System.Windows.Forms.SystemInformation.TerminalServerSession ? "remote" : "console",
                adapters);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or System.Security.SecurityException
            or InvalidOperationException)
        {
            return new LaunchEnvironmentSnapshot(
                "unavailable",
                RuntimeInformation.OSDescription,
                Environment.OSVersion.Version.ToString(),
                RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                "unknown",
                []);
        }
    }
}

public sealed class RegistryDisplayAdapterReader : IDisplayAdapterReader
{
    private const string DisplayClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public IReadOnlyList<DisplayAdapterSnapshot> Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var displayClass = localMachine.OpenSubKey(DisplayClassPath, writable: false)
            ?? throw new InvalidOperationException("The Windows display adapter registry class is unavailable.");
        var adapters = new List<DisplayAdapterSnapshot>();
        foreach (var subKeyName in displayClass.GetSubKeyNames().Order(StringComparer.Ordinal))
        {
            try
            {
                using var adapterKey = displayClass.OpenSubKey(subKeyName, writable: false);
                if (adapterKey is null)
                {
                    continue;
                }

                var description = ReadBoundedString(adapterKey, "DriverDesc");
                if (string.IsNullOrWhiteSpace(description))
                {
                    continue;
                }

                var provider = ReadBoundedString(adapterKey, "ProviderName");
                var version = ReadBoundedString(adapterKey, "DriverVersion");
                var date = ReadBoundedString(adapterKey, "DriverDate");
                var infPath = ReadBoundedString(adapterKey, "InfPath");
                adapters.Add(new DisplayAdapterSnapshot(
                    description,
                    provider,
                    version,
                    date,
                    Classify(description, provider, infPath)));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                or IOException
                or System.Security.SecurityException)
            {
                // A vendor or security product can protect an unrelated display-class subkey.
                // Keep the adapters that are readable without elevation.
            }
        }

        return adapters
            .OrderBy(adapter => adapter.Description, StringComparer.OrdinalIgnoreCase)
            .ThenBy(adapter => adapter.Provider, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ReadBoundedString(RegistryKey key, string valueName)
    {
        var value = key.GetValue(valueName, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var text = value switch
        {
            string item => item,
            string[] items => string.Join(";", items),
            _ => string.Empty
        };
        return text.Length <= 256 ? text : text[..256];
    }

    private static string Classify(string description, string provider, string infPath)
    {
        var evidence = $"{description} {provider} {infPath}";
        if (ContainsAny(evidence, "oray", "idd", "indirect", "virtual", "remote"))
        {
            return "virtual-display";
        }

        if (ContainsAny(evidence, "nvidia", "intel", "amd", "advanced micro devices"))
        {
            return "physical-gpu";
        }

        if (ContainsAny(evidence, "microsoft basic display", "basicdisplay"))
        {
            return "software-display";
        }

        return "display-adapter";
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}
