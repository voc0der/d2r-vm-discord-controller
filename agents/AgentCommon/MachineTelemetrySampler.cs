using System.Runtime.InteropServices;

namespace AgentCommon;

public sealed class MachineTelemetrySampler
{
    private readonly object _sync = new();
    private CpuTimes? _lastCpuTimes;

    public MachineTelemetrySnapshot Sample()
    {
        var memory = TryReadPhysicalMemory();
        double? cpuPercent = null;

        if (TryReadCpuTimes(out var currentCpuTimes))
        {
            lock (_sync)
            {
                if (_lastCpuTimes is { } previousCpuTimes)
                {
                    cpuPercent = CalculateCpuPercent(previousCpuTimes, currentCpuTimes);
                }

                _lastCpuTimes = currentCpuTimes;
            }
        }

        var usedBytes = memory.TotalBytes is { } total && memory.AvailableBytes is { } available
            ? (long?)Math.Max(total - available, 0)
            : null;

        return new MachineTelemetrySnapshot(
            MemoryTotalBytes: memory.TotalBytes,
            MemoryAvailableBytes: memory.AvailableBytes,
            MemoryUsedBytes: usedBytes,
            CpuPercent: cpuPercent);
    }

    private static double? CalculateCpuPercent(CpuTimes previous, CpuTimes current)
    {
        var totalDelta = current.Total - previous.Total;
        var idleDelta = current.Idle - previous.Idle;
        if (totalDelta <= 0 || idleDelta < 0)
        {
            return null;
        }

        var busyDelta = Math.Max(totalDelta - idleDelta, 0);
        return Math.Clamp(busyDelta * 100.0 / totalDelta, 0, 100);
    }

    private static MemoryTelemetry TryReadPhysicalMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            return TryReadWindowsMemory();
        }

        if (OperatingSystem.IsLinux())
        {
            return TryReadLinuxMemory();
        }

        return new MemoryTelemetry(null, null);
    }

    private static MemoryTelemetry TryReadWindowsMemory()
    {
        var status = new MemoryStatusEx();
        status.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        if (!GlobalMemoryStatusEx(ref status))
        {
            return new MemoryTelemetry(null, null);
        }

        return new MemoryTelemetry(
            checked((long)Math.Min(status.TotalPhys, long.MaxValue)),
            checked((long)Math.Min(status.AvailPhys, long.MaxValue)));
    }

    private static MemoryTelemetry TryReadLinuxMemory()
    {
        try
        {
            long? total = null;
            long? available = null;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                {
                    total = TryReadMeminfoBytes(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.OrdinalIgnoreCase))
                {
                    available = TryReadMeminfoBytes(line);
                }

                if (total is not null && available is not null)
                {
                    break;
                }
            }

            return new MemoryTelemetry(total, available);
        }
        catch
        {
            return new MemoryTelemetry(null, null);
        }
    }

    private static long? TryReadMeminfoBytes(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var kib)
            ? kib * 1024
            : null;
    }

    private static bool TryReadCpuTimes(out CpuTimes times)
    {
        if (OperatingSystem.IsWindows())
        {
            return TryReadWindowsCpuTimes(out times);
        }

        if (OperatingSystem.IsLinux())
        {
            return TryReadLinuxCpuTimes(out times);
        }

        times = default;
        return false;
    }

    private static bool TryReadWindowsCpuTimes(out CpuTimes times)
    {
        times = default;
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return false;
        }

        var idleTicks = idle.ToUInt64();
        var kernelTicks = kernel.ToUInt64();
        var userTicks = user.ToUInt64();
        times = new CpuTimes(
            Total: checked((long)Math.Min(kernelTicks + userTicks, long.MaxValue)),
            Idle: checked((long)Math.Min(idleTicks, long.MaxValue)));
        return true;
    }

    private static bool TryReadLinuxCpuTimes(out CpuTimes times)
    {
        times = default;
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                return false;
            }

            var values = line
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(part => long.TryParse(part, out var value) ? value : 0)
                .ToArray();
            if (values.Length < 4)
            {
                return false;
            }

            var idle = values[3] + (values.Length > 4 ? values[4] : 0);
            var total = values.Sum();
            times = new CpuTimes(total, idle);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _lowDateTime;
        private readonly uint _highDateTime;

        public ulong ToUInt64() => ((ulong)_highDateTime << 32) | _lowDateTime;
    }

    private readonly record struct CpuTimes(long Total, long Idle);

    private readonly record struct MemoryTelemetry(long? TotalBytes, long? AvailableBytes);
}

public sealed record MachineTelemetrySnapshot(
    long? MemoryTotalBytes,
    long? MemoryAvailableBytes,
    long? MemoryUsedBytes,
    double? CpuPercent);
