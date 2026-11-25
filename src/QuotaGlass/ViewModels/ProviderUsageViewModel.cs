using QuotaGlass.Models;
using QuotaGlass.Services;

namespace QuotaGlass.ViewModels;

public sealed class ProviderUsageViewModel
{
    public ProviderUsageViewModel(
        UsageSnapshot snapshot,
        DateTimeOffset now,
        Func<ProviderUsageViewModel, Task>? redeemHandler = null)
    {
        Snapshot = snapshot;
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
        if (snapshot.ResetCredits?.Redeem is { } redeem &&
            redeemHandler is not null)
        {
            RedeemOption = redeem;
            RedeemToolTip = BuildRedeemToolTip(redeem, now);
            RedeemCommand = new AsyncRelayCommand(
                () => redeemHandler(this),
                () => redeem.UsableNow);
        }

        var meters = snapshot.Meters
            .Select(meter => new MeterUsageViewModel(meter, now))
            .ToArray();

        Meters = meters
            .OrderByDescending(meter => meter.ResetPeriod)
            .ToArray();
        PrimaryMeter = Meters.FirstOrDefault();
        SecondaryMeters = Meters.Skip(1).ToArray();
        CompactMeters = meters
            .OrderBy(meter => meter.ResetPeriod)
            .Take(2)
            .ToArray();
        CompactToolTip = BuildCompactToolTip();
    }

    public string Provider { get; }
    internal UsageSnapshot Snapshot { get; }
    public string DisplayName { get; }
    public string IconText { get; }
    public System.Windows.Media.Geometry? IconGeometry { get; }
    public bool HasVectorIcon => IconGeometry is not null;
    public string AccountLabel { get; }
    public string Source { get; }
    public string? StatusMessage { get; }
    public string? ResetCreditSummaryText { get; }
    public bool HasResetCreditSummary => ResetCreditSummaryText is not null;
    public ResetCreditRedeemOption? RedeemOption { get; }
    public bool HasRedeemOption => RedeemOption is not null;
    public string? RedeemToolTip { get; }
    public AsyncRelayCommand? RedeemCommand { get; }
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

        var meters = Meters
            .OrderBy(meter => meter.ResetPeriod)
            .Select(meter =>
                $"{meter.Label} {meter.RemainingWithResetText}" +
                (meter.IsWarning ? " ⚠" : string.Empty));
        var resetCredits = HasResetCreditSummary
            ? $"\n{ResetCreditSummaryText}"
            : string.Empty;
        return $"{DisplayName}\n{string.Join("\n", meters)}" +
               $"{resetCredits}\n클릭해서 전체 보기";
    }

    internal static string DescribeKind(ResetCreditRedeemOption option) =>
        option.Kind switch
        {
            "session" => "5시간 한도 리셋",
            "weekly" => "주간 한도 리셋",
            "full" => "전체 한도 리셋",
            _ => string.IsNullOrWhiteSpace(option.Label)
                ? "리셋 티켓"
                : option.Label
        };

    internal static string FormatExpiry(
        ResetCreditRedeemOption option) =>
        option.ExpiresAt is { } expiresAt
            ? $"{expiresAt.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "기한 없음";

    private static string BuildRedeemToolTip(
        ResetCreditRedeemOption option,
        DateTimeOffset now)
    {
        var summary =
            $"{DescribeKind(option)} · 만료 {FormatExpiry(option)}" +
            "\n가장 빨리 만료되는 티켓부터 사용합니다.";
        return option.UsableNow
            ? summary
            : $"{summary}\n{ClaudeResetMessages.DescribeUnavailable(option.UnavailableReason)}";
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
