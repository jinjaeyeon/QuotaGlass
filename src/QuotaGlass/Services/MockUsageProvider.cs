using QuotaGlass.Models;

namespace QuotaGlass.Services;

public sealed class MockUsageProvider(
    string provider,
    string displayName,
    string iconText,
    string accountLabel,
    IReadOnlyList<MockMeter> meters) : IUsageProvider
{
    public string ProviderId => provider;
    public string DisplayName => displayName;
    public string IconText => iconText;
    public string AccountLabel => accountLabel;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(70, cancellationToken);

        var now = DateTimeOffset.Now;
        var snapshots = meters.Select(meter =>
        {
            var resetsAt = now + TimeSpan.FromTicks(
                (long)(meter.Window.Ticks * meter.RemainingTimeRatio));
            var windowStart = resetsAt - meter.Window;

            return new UsageMeter(
                meter.Id,
                meter.Label,
                meter.Remaining,
                meter.Limit,
                meter.Unit,
                windowStart,
                resetsAt);
        }).ToArray();

        return new UsageSnapshot(
            provider,
            displayName,
            iconText,
            accountLabel,
            snapshots,
            now,
            "예시 데이터");
    }

    public static IReadOnlyList<IUsageProvider> CreateDefaults() =>
    [
        new MockUsageProvider(
            "claude-code",
            "Claude Code",
            "C",
            "개인 · 5시간/주간",
            [
                new("five-hour", "5시간", 41, 100, "percent", TimeSpan.FromHours(5), 0.35),
                new("weekly", "주간", 68, 100, "percent", TimeSpan.FromDays(7), 0.60)
            ]),
        new MockUsageProvider(
            "codex",
            "Codex",
            "<>",
            "팀 · 월간",
            [
                new("monthly", "월간", 43, 100, "percent", TimeSpan.FromDays(30), 0.62)
            ]),
        new MockUsageProvider(
            "github-copilot",
            "GitHub Copilot",
            "GH",
            "개인 · 월간",
            [
                new("chat", "Chat", 156, 200, "requests", TimeSpan.FromDays(31), 0.58),
                new("completions", "코드 완성", 1740, 2000, "requests", TimeSpan.FromDays(31), 0.58)
            ]),
        new MockUsageProvider(
            "antigravity",
            "Antigravity",
            "✦",
            "개인",
            [
                new("model-quota", "모델 quota", 88, 100, "percent", TimeSpan.FromHours(5), 0.74)
            ]),
        new MockUsageProvider(
            "cursor",
            "Cursor",
            "↖",
            "팀",
            [
                new("monthly", "월간 포함량", 63, 100, "percent", TimeSpan.FromDays(30), 0.40)
            ]),
        new MockUsageProvider(
            "jetbrains",
            "JetBrains AI",
            "JB",
            "개인",
            [
                new("credits", "AI Credits", 6.2, 20, "credits", TimeSpan.FromDays(30), 0.52)
            ])
    ];
}

public sealed record MockMeter(
    string Id,
    string Label,
    double Remaining,
    double Limit,
    string Unit,
    TimeSpan Window,
    double RemainingTimeRatio);
