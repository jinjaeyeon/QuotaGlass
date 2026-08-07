using QuotaGlass.Models;

namespace QuotaGlass.ViewModels;

public sealed class ProviderUsageViewModel
{
    public ProviderUsageViewModel(UsageSnapshot snapshot, DateTimeOffset now)
    {
        Provider = snapshot.Provider;
        DisplayName = snapshot.DisplayName;
        IconText = snapshot.IconText;
        IconGeometry = AgentIconCatalog.Find(snapshot.Provider);
        AccountLabel = snapshot.AccountLabel;
        Source = snapshot.Source;
        StatusMessage = snapshot.StatusMessage;
        ResetCreditSummaryText = FormatResetCreditSummary(
            snapshot.ResetCredits,
            now);

        Meters = snapshot.Meters
            .Select(meter => new MeterUsageViewModel(meter, now))
            .OrderBy(meter => meter.PaceDelta)
            .ToArray();

        PrimaryMeter = Meters.FirstOrDefault();
        SecondaryMeters = Meters.Skip(1).ToArray();
        CompactMeters = Meters.Take(2).ToArray();
        CompactToolTip = BuildCompactToolTip();
    }

    public string Provider { get; }
    public string DisplayName { get; }
    public string IconText { get; }
    public System.Windows.Media.Geometry? IconGeometry { get; }
    public bool HasVectorIcon => IconGeometry is not null;
    public string AccountLabel { get; }
    public string Source { get; }
    public string? StatusMessage { get; }
    public string? ResetCreditSummaryText { get; }
    public bool HasResetCreditSummary => ResetCreditSummaryText is not null;
    public IReadOnlyList<MeterUsageViewModel> Meters { get; }
    public MeterUsageViewModel? PrimaryMeter { get; }
    public IReadOnlyList<MeterUsageViewModel> SecondaryMeters { get; }
    public IReadOnlyList<MeterUsageViewModel> CompactMeters { get; }
    public string CompactToolTip { get; }
    public bool HasUsage => PrimaryMeter is not null;

    private string BuildCompactToolTip()
    {
        if (!HasUsage)
        {
            return $"{DisplayName}: {StatusMessage}";
        }

        var meters = Meters.Select(meter =>
            $"{meter.Label} {meter.RemainingWithResetText}" +
            (meter.IsWarning ? " ⚠" : string.Empty));
        var resetCredits = HasResetCreditSummary
            ? $"\n{ResetCreditSummaryText}"
            : string.Empty;
        return $"{DisplayName}\n{string.Join("\n", meters)}" +
               $"{resetCredits}\n클릭해서 전체 보기";
    }

    private static string? FormatResetCreditSummary(
        ResetCreditSummary? summary,
        DateTimeOffset now)
    {
        if (summary is null)
        {
            return null;
        }

        var countText = $"리셋 티켓 {summary.AvailableCount}장";
        if (summary.EarliestExpiresAt is not { } expiresAt)
        {
            return countText;
        }

        var localExpiry = expiresAt.ToLocalTime();
        var remaining = expiresAt - now;
        var deadlineText = remaining <= TimeSpan.Zero
            ? "기한 지남"
            : remaining.TotalDays >= 1
                ? $"{(int)remaining.TotalDays}일 {remaining.Hours}시간 남음"
                : remaining.TotalHours >= 1
                    ? $"{(int)remaining.TotalHours}시간 {remaining.Minutes}분 남음"
                    : $"{Math.Max(1, remaining.Minutes)}분 남음";

        return $"{countText} · 가장 이른 기한 " +
               $"{localExpiry:M월 d일 HH:mm} ({deadlineText})";
    }
}
