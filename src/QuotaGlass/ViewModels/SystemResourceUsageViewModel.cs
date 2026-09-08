using QuotaGlass.Models;

namespace QuotaGlass.ViewModels;

public sealed class SystemResourceUsageViewModel : ObservableObject
{
    internal const int MaxHistorySamples = 24;

    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _memoryHistory = new();
    private double? _cpuUsagePercent;
    private double? _memoryUsagePercent;
    private ulong _usedMemoryBytes;
    private ulong _totalMemoryBytes;

    public double? CpuUsagePercent
    {
        get => _cpuUsagePercent;
        private set => SetProperty(ref _cpuUsagePercent, value);
    }

    public double? MemoryUsagePercent
    {
        get => _memoryUsagePercent;
        private set => SetProperty(ref _memoryUsagePercent, value);
    }

    public IReadOnlyList<double> CpuHistory { get; private set; } = [];

    public IReadOnlyList<double> MemoryHistory { get; private set; } = [];

    public string CpuUsageText => FormatPercent(CpuUsagePercent);

    public string MemoryUsageText => FormatPercent(MemoryUsagePercent);

    public string CpuToolTip =>
        $"CPU 사용량 {CpuUsageText} · 5초 간격";

    public string ResourceToolTip =>
        $"CPU {CpuUsageText} · 메모리 {MemoryUsageText} · 5초 간격";

    public string MemoryToolTip
    {
        get
        {
            if (MemoryUsagePercent is not { } usage ||
                _totalMemoryBytes == 0)
            {
                return "메모리 사용량 확인 중… · 5초 간격";
            }

            return $"메모리 사용량 {usage:0}% · " +
                   $"{FormatBytes(_usedMemoryBytes)} / " +
                   $"{FormatBytes(_totalMemoryBytes)} · 5초 간격";
        }
    }

    public void Apply(SystemResourceSnapshot snapshot)
    {
        CpuUsagePercent = snapshot.CpuUsagePercent;
        MemoryUsagePercent = snapshot.MemoryUsagePercent;
        _usedMemoryBytes = snapshot.UsedMemoryBytes;
        _totalMemoryBytes = snapshot.TotalMemoryBytes;

        if (snapshot.CpuUsagePercent is { } cpuUsage)
        {
            AddSample(_cpuHistory, cpuUsage);
        }

        if (snapshot.MemoryUsagePercent is { } memoryUsage)
        {
            AddSample(_memoryHistory, memoryUsage);
        }

        CpuHistory = _cpuHistory.ToArray();
        MemoryHistory = _memoryHistory.ToArray();
        RaisePropertyChanged(nameof(CpuHistory));
        RaisePropertyChanged(nameof(MemoryHistory));
        RaisePropertyChanged(nameof(CpuUsageText));
        RaisePropertyChanged(nameof(MemoryUsageText));
        RaisePropertyChanged(nameof(CpuToolTip));
        RaisePropertyChanged(nameof(ResourceToolTip));
        RaisePropertyChanged(nameof(MemoryToolTip));
    }

    private static void AddSample(Queue<double> history, double value)
    {
        history.Enqueue(Math.Clamp(value, 0, 100));
        while (history.Count > MaxHistorySamples)
        {
            history.Dequeue();
        }
    }

    private static string FormatPercent(double? value) =>
        value is { } percent ? $"{percent:0}%" : "—";

    private static string FormatBytes(ulong bytes)
    {
        const double KibiByte = 1024;
        const double MebiByte = KibiByte * 1024;
        const double GibiByte = MebiByte * 1024;

        return bytes >= GibiByte
            ? $"{bytes / GibiByte:0.0} GB"
            : bytes >= MebiByte
                ? $"{bytes / MebiByte:0} MB"
                : $"{bytes / KibiByte:0} KB";
    }
}
