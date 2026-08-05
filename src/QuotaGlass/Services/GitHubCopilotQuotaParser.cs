using System.Text.Json;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public static class GitHubCopilotQuotaParser
{
    public static IReadOnlyList<UsageMeter> Parse(
        JsonElement result,
        DateTimeOffset now)
    {
        if (!result.TryGetProperty("quotaSnapshots", out var snapshots) ||
            snapshots.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var premium = TryCreateMeter(
            snapshots,
            "premium_interactions",
            "AI Credits",
            "credits",
            now);
        if (premium is not null)
        {
            return [premium];
        }

        var meters = new List<UsageMeter>();
        AddMeter(meters, snapshots, "chat", "Chat", "requests", now);
        AddMeter(
            meters,
            snapshots,
            "completions",
            "코드 완성",
            "requests",
            now);
        return meters;
    }

    private static void AddMeter(
        ICollection<UsageMeter> meters,
        JsonElement snapshots,
        string id,
        string label,
        string unit,
        DateTimeOffset now)
    {
        var meter = TryCreateMeter(snapshots, id, label, unit, now);
        if (meter is not null)
        {
            meters.Add(meter);
        }
    }

    private static UsageMeter? TryCreateMeter(
        JsonElement snapshots,
        string id,
        string label,
        string unit,
        DateTimeOffset now)
    {
        if (!snapshots.TryGetProperty(id, out var snapshot) ||
            snapshot.ValueKind != JsonValueKind.Object ||
            ReadBoolean(snapshot, "isUnlimitedEntitlement") ||
            !TryReadNumber(snapshot, "entitlementRequests", out var limit) ||
            limit <= 0)
        {
            return null;
        }

        var used = TryReadNumber(snapshot, "usedRequests", out var usedValue)
            ? usedValue
            : limit * (1 - ReadPercentage(snapshot) / 100);
        var remaining = Math.Clamp(limit - used, 0, limit);
        var (windowStart, resetsAt) = ResolveMonthlyWindow(snapshot, now);

        return new UsageMeter(
            id,
            label,
            remaining,
            limit,
            unit,
            windowStart,
            resetsAt);
    }

    private static (DateTimeOffset Start, DateTimeOffset End) ResolveMonthlyWindow(
        JsonElement snapshot,
        DateTimeOffset now)
    {
        var utcNow = now.ToUniversalTime();
        var calendarStart = new DateTimeOffset(
            utcNow.Year,
            utcNow.Month,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        var calendarEnd = calendarStart.AddMonths(1);

        if (snapshot.TryGetProperty("resetDate", out var resetElement) &&
            resetElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(resetElement.GetString(), out var reported) &&
            reported > now.AddHours(1))
        {
            return (reported.AddMonths(-1), reported);
        }

        return (calendarStart, calendarEnd);
    }

    private static bool TryReadNumber(
        JsonElement parent,
        string propertyName,
        out double value)
    {
        value = 0;
        return parent.TryGetProperty(propertyName, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetDouble(out value);
    }

    private static bool ReadBoolean(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var element) &&
        element.ValueKind is JsonValueKind.True;

    private static double ReadPercentage(JsonElement snapshot) =>
        TryReadNumber(snapshot, "remainingPercentage", out var percentage)
            ? Math.Clamp(percentage, 0, 100)
            : 0;
}
