using System.Runtime.InteropServices;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public sealed class SystemResourceMonitor
{
    private SystemTimes? _previousSystemTimes;

    public SystemResourceSnapshot Sample(DateTimeOffset? observedAt = null)
    {
        double? cpuUsagePercent = null;
        if (GetSystemTimes(
                out var idleTime,
                out var kernelTime,
                out var userTime))
        {
            var currentSystemTimes = new SystemTimes(
                ToUInt64(idleTime),
                ToUInt64(kernelTime),
                ToUInt64(userTime));
            if (_previousSystemTimes is { } previous)
            {
                cpuUsagePercent = CalculateCpuUsage(
                    previous.Idle,
                    previous.Kernel,
                    previous.User,
                    currentSystemTimes.Idle,
                    currentSystemTimes.Kernel,
                    currentSystemTimes.User);
            }

            _previousSystemTimes = currentSystemTimes;
        }

        var memoryStatus = new NativeMemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<NativeMemoryStatusEx>()
        };
        double? memoryUsagePercent = null;
        ulong usedMemoryBytes = 0;
        ulong totalMemoryBytes = 0;
        if (GlobalMemoryStatusEx(ref memoryStatus) &&
            memoryStatus.TotalPhysicalMemory > 0)
        {
            totalMemoryBytes = memoryStatus.TotalPhysicalMemory;
            var availableMemoryBytes = Math.Min(
                memoryStatus.AvailablePhysicalMemory,
                totalMemoryBytes);
            usedMemoryBytes = totalMemoryBytes - availableMemoryBytes;
            memoryUsagePercent = usedMemoryBytes * 100d /
                                 totalMemoryBytes;
        }

        return new SystemResourceSnapshot(
            observedAt ?? DateTimeOffset.Now,
            cpuUsagePercent,
            memoryUsagePercent,
            usedMemoryBytes,
            totalMemoryBytes);
    }

    internal static double CalculateCpuUsage(
        ulong previousIdle,
        ulong previousKernel,
        ulong previousUser,
        ulong currentIdle,
        ulong currentKernel,
        ulong currentUser)
    {
        var idleDelta = Difference(currentIdle, previousIdle);
        var kernelDelta = Difference(currentKernel, previousKernel);
        var userDelta = Difference(currentUser, previousUser);
        var totalDelta = (double)kernelDelta + userDelta;
        if (totalDelta <= 0)
        {
            return 0;
        }

        var busyDelta = Math.Max(0, totalDelta - idleDelta);
        return Math.Clamp(busyDelta * 100d / totalDelta, 0, 100);
    }

    private static ulong Difference(ulong current, ulong previous) =>
        current >= previous ? current - previous : 0;

    private static ulong ToUInt64(NativeFileTime time) =>
        ((ulong)time.HighDateTime << 32) | time.LowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out NativeFileTime idleTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(
        ref NativeMemoryStatusEx buffer);

    private readonly record struct SystemTimes(
        ulong Idle,
        ulong Kernel,
        ulong User);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
