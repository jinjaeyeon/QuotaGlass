using System.Globalization;
using System.Text.Json;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public static class ClaudeRateLimitParser
{
    public static IReadOnlyList<UsageMeter> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("rate_limits", out var rateLimits) &&
            rateLimits.ValueKind == JsonValueKind.Object)
        {
            return ParseWindows(
                rateLimits,
                "used_percentage");
        }

        return ParseWindows(root, "utilization");
    }

    private static IReadOnlyList<UsageMeter> ParseWindows(
        JsonElement windows,
        string utilizationPropertyName)
    {
        var meters = new List<UsageMeter>();
        AddWindow(
            meters,
            windows,
            "five_hour",
            "5시간",
            TimeSpan.FromHours(5),
            utilizationPropertyName);
        AddWindow(
            meters,
            windows,
            "seven_day",
            "주간",
            TimeSpan.FromDays(7),
            utilizationPropertyName);

        return meters;
    }

    private static void AddWindow(
        ICollection<UsageMeter> meters,
        JsonElement windows,
        string propertyName,
        string label,
        TimeSpan duration,
        string utilizationPropertyName)
    {
        if (!windows.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty(
                utilizationPropertyName,
                out var utilizationElement) ||
            !TryReadDouble(utilizationElement, out var utilization) ||
            !window.TryGetProperty("resets_at", out var resetElement) ||
            !TryReadResetAt(resetElement, out var resetsAt))
        {
            return;
        }

        var used = Math.Clamp(utilization, 0, 100);

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

    private static bool TryReadDouble(
        JsonElement element,
        out double value)
    {
        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(
                element.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryReadResetAt(
        JsonElement element,
        out DateTimeOffset resetsAt)
    {
        if (element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                element.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out resetsAt))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt64(out var timestamp))
        {
            try
            {
                resetsAt = timestamp >= 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                    : DateTimeOffset.FromUnixTimeSeconds(timestamp);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                // Ignore malformed reset timestamps.
            }
        }

        resetsAt = default;
        return false;
    }
}
