namespace QuotaGlass.Models;

public sealed record SystemResourceSnapshot(
    DateTimeOffset ObservedAt,
    double? CpuUsagePercent,
    double? MemoryUsagePercent,
    ulong UsedMemoryBytes,
    ulong TotalMemoryBytes);
