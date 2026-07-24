using System.Text.Json;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public static class ClaudeRateLimitParser
{
    public static IReadOnlyList<UsageMeter> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(
                "rate_limits",
                out var rateLimits) ||
            rateLimits.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var meters = new List<UsageMeter>();
        AddWindow(
            meters,
            rateLimits,
            "five_hour",
            "5시간",
            TimeSpan.FromHours(5));
        AddWindow(
            meters,
            rateLimits,
            "seven_day",
            "주간",
            TimeSpan.FromDays(7));

        return meters;
    }

    private static void AddWindow(
        ICollection<UsageMeter> meters,
        JsonElement rateLimits,
        string propertyName,
        string label,
        TimeSpan duration)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("used_percentage", out var usedElement) ||
            usedElement.ValueKind != JsonValueKind.Number ||
            !window.TryGetProperty("resets_at", out var resetElement) ||
            resetElement.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        var used = Math.Clamp(usedElement.GetDouble(), 0, 100);
        var resetsAt = DateTimeOffset.FromUnixTimeSeconds(
            resetElement.GetInt64());

        meters.Add(
            new UsageMeter(
                $"claude-{propertyName.Replace('_', '-')}",
                label,
                100 - used,
                100,
                "percent",
                resetsAt - duration,
                resetsAt));
    }
}
