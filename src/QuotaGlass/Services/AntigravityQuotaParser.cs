using System.Text.Json;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public static class AntigravityQuotaParser
{
    public static IReadOnlyList<UsageMeter> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var response = document.RootElement.TryGetProperty(
            "response",
            out var responseElement)
            ? responseElement
            : document.RootElement;
        if (!response.TryGetProperty("groups", out var groups) ||
            groups.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var meters = new List<UsageMeter>();
        foreach (var group in groups.EnumerateArray())
        {
            var groupName = ReadString(group, "displayName") ?? "모델";
            if (!group.TryGetProperty("buckets", out var buckets) ||
                buckets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var bucket in buckets.EnumerateArray())
            {
                if (!bucket.TryGetProperty(
                        "remainingFraction",
                        out var remainingElement) ||
                    remainingElement.ValueKind != JsonValueKind.Number ||
                    !TryReadReset(bucket, out var resetAt))
                {
                    continue;
                }

                var id = ReadString(bucket, "bucketId")
                    ?? $"antigravity-{meters.Count}";
                var window = ReadString(bucket, "window");
                var duration = string.Equals(
                    window,
                    "weekly",
                    StringComparison.OrdinalIgnoreCase)
                    ? TimeSpan.FromDays(7)
                    : TimeSpan.FromHours(5);
                var windowLabel = string.Equals(
                    window,
                    "weekly",
                    StringComparison.OrdinalIgnoreCase)
                    ? "주간"
                    : "5시간";

                meters.Add(
                    new UsageMeter(
                        id,
                        $"{groupName} · {windowLabel}",
                        Math.Clamp(remainingElement.GetDouble(), 0, 1) * 100,
                        100,
                        "percent",
                        resetAt - duration,
                        resetAt));
            }
        }

        return meters;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryReadReset(
        JsonElement bucket,
        out DateTimeOffset resetAt)
    {
        resetAt = default;
        if (!bucket.TryGetProperty("resetTime", out var reset))
        {
            return false;
        }

        return reset.ValueKind switch
        {
            JsonValueKind.String => DateTimeOffset.TryParse(
                reset.GetString(),
                out resetAt),
            JsonValueKind.Number when reset.TryGetInt64(out var unix) =>
                TryFromUnix(unix, out resetAt),
            _ => false
        };
    }

    private static bool TryFromUnix(long value, out DateTimeOffset result)
    {
        try
        {
            result = value > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            result = default;
            return false;
        }
    }
}
