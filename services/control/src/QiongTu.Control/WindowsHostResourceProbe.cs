using System.Runtime.InteropServices;
using QiongTu.Contracts;

namespace QiongTu.Control;

internal interface IHostResourceProbe
{
    CapabilityHost CaptureHost();

    CpuCapability CaptureCpu();

    MemoryCapability CaptureMemory();

    StorageCapability CaptureStorage(string role, string directoryPath);
}

internal sealed partial class WindowsHostResourceProbe : IHostResourceProbe
{
    private const int RemoteSessionMetric = 0x1000;

    public CapabilityHost CaptureHost()
    {
        var isWindows = OperatingSystem.IsWindows();
        return new CapabilityHost(
            isWindows ? "present" : "unknown",
            Bounded(RuntimeInformation.OSDescription, 128),
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            isWindows ? (GetSystemMetrics(RemoteSessionMetric) != 0 ? "remote" : "console") : "unknown");
    }

    public CpuCapability CaptureCpu()
    {
        var processorCount = Environment.ProcessorCount;
        return new CpuCapability(
            processorCount > 0 ? "present" : "unknown",
            processorCount > 0 ? processorCount : null,
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
    }

    public MemoryCapability CaptureMemory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new MemoryCapability("unknown", null, null);
        }

        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return new MemoryCapability("unknown", null, null);
        }

        return new MemoryCapability(
            "present",
            CheckedLong(status.TotalPhysical),
            CheckedLong(status.AvailablePhysical));
    }

    public StorageCapability CaptureStorage(string role, string directoryPath)
    {
        var safeRole = NormalizeRole(role);
        try
        {
            var fullPath = Path.GetFullPath(directoryPath);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return UnknownStorage(safeRole);
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return UnknownStorage(safeRole, DriveTypeName(drive.DriveType));
            }

            return new StorageCapability(
                safeRole,
                drive.TotalSize,
                drive.AvailableFreeSpace,
                DriveTypeName(drive.DriveType),
                "present");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or System.Security.SecurityException)
        {
            return UnknownStorage(safeRole);
        }
    }

    private static StorageCapability UnknownStorage(string role, string driveType = "unknown") =>
        new(role, null, null, driveType, "unknown");

    private static string NormalizeRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role) || role.Length > 64 ||
            role.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException("A bounded storage role is required.", nameof(role));
        }

        return role;
    }

    private static string DriveTypeName(DriveType driveType) => driveType switch
    {
        DriveType.Fixed => "fixed",
        DriveType.Removable => "removable",
        DriveType.Network => "network",
        DriveType.Ram => "ram",
        DriveType.CDRom => "optical",
        DriveType.NoRootDirectory => "no-root",
        _ => "unknown"
    };

    private static string Bounded(string value, int maximumLength)
    {
        var safe = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return safe.Length <= maximumLength ? safe : safe[..maximumLength];
    }

    private static long CheckedLong(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
