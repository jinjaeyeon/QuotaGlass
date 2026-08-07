using QuotaGlass.Models;

namespace QuotaGlass.ViewModels;

public sealed class MeterUsageViewModel
{
    private const double WarningTolerance = 0.05;

    public MeterUsageViewModel(UsageMeter meter, DateTimeOffset now)
    {
        Label = meter.Label;
        RemainingRatio = meter.RemainingRatio;
        IsReset = meter.IsReset;
        ResetsAt = meter.ResetsAt;
        SafeRemainingRatio = IsReset
            ? 1
            : meter.RemainingTimeRatio(now);
        PaceDelta = IsReset ? 0 : meter.PaceDelta(now);
        IsWarning = !IsReset && PaceDelta < -WarningTolerance;
        IsWatch = !IsReset && !IsWarning && PaceDelta < 0;
        RemainingText = FormatRemaining(meter);
        CompactLabel = FormatCompactLabel(meter.Label);
        ResetCountdownText = IsReset
            ? "초기화 완료 · 새 사용 가능"
            : meter.ResetsAt <= now
                ? "초기화 시각 지남 · 갱신 필요"
                : $"{FormatDuration(meter.ResetsAt - now)} 후 초기화";
        ResetText = $"{Label} · {ResetCountdownText}";
        RemainingWithResetText =
            $"{RemainingText} · {ResetCountdownText}";
        StatusText = IsReset
            ? "✓ 초기화 완료 · 사용 가능"
            : FormatStatus(PaceDelta, IsWarning, IsWatch);
    }

    public string Label { get; }
    public double RemainingRatio { get; }
    public double SafeRemainingRatio { get; }
    public double PaceDelta { get; }
    public bool IsWarning { get; }
    public bool IsWatch { get; }
    public bool IsReset { get; }
    public DateTimeOffset ResetsAt { get; }
    public string RemainingText { get; }
    public string RemainingWithResetText { get; }
    public string CompactLabel { get; }
    public string ResetCountdownText { get; }
    public string ResetText { get; }
    public string StatusText { get; }

    private static string FormatCompactLabel(string label)
    {
        var normalized = label
            .Replace("시간", "h", StringComparison.Ordinal)
            .Replace("일", "d", StringComparison.Ordinal)
            .Replace("개월", "mo", StringComparison.Ordinal)
            .Replace("월간", "월", StringComparison.Ordinal);

        return normalized.Length <= 4
            ? normalized
            : normalized[..4];
    }

    private static string FormatRemaining(UsageMeter meter) =>
        meter.Unit == "credits"
            ? $"{meter.Remaining:0.#} / {meter.Limit:0.#} credits ({meter.RemainingRatio:P0})"
            : $"{meter.RemainingRatio:P0}";

    private static string FormatStatus(double delta, bool warning, bool watch)
    {
        var points = Math.Abs(delta) * 100;

        if (warning)
        {
            return $"⚠ 경고 · {points:0}％p 빠르게 소진";
        }

        if (watch)
        {
            return $"△ 주의 · {points:0}％p 빠름";
        }

        return $"✓ 안정권 · {points:0}％p 여유";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "곧";
        }

        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}일 {duration.Hours}시간";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}시간 {duration.Minutes}분";
        }

        return $"{Math.Max(1, duration.Minutes)}분";
    }
}
