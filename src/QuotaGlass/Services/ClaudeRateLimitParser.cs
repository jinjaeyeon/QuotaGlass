using System.Globalization;
using System.Text.Json;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public static class ClaudeRateLimitParser
{
    public static IReadOnlyList<UsageMeter> Parse(
        string json,
        DateTimeOffset? observedAt = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("rate_limits", out var rateLimits) &&
            rateLimits.ValueKind == JsonValueKind.Object)
        {
            return ParseWindows(
                rateLimits,
                "used_percentage",
                observedAt ?? DateTimeOffset.Now);
        }

        return ParseWindows(
            root,
            "utilization",
            observedAt ?? DateTimeOffset.Now);
    }

    private static IReadOnlyList<UsageMeter> ParseWindows(
        JsonElement windows,
        string utilizationPropertyName,
        DateTimeOffset observedAt)
    {
        var meters = new List<UsageMeter>();
        AddWindow(
            meters,
            windows,
            "five_hour",
            "5시간",
            TimeSpan.FromHours(5),
            utilizationPropertyName,
            observedAt);
        AddWindow(
            meters,
            windows,
            "seven_day",
            "주간",
            TimeSpan.FromDays(7),
            utilizationPropertyName,
            observedAt);

        return meters;
    }

    private static void AddWindow(
        ICollection<UsageMeter> meters,
        JsonElement windows,
        string propertyName,
        string label,
        TimeSpan duration,
        string utilizationPropertyName,
        DateTimeOffset observedAt)
    {
        if (!windows.TryGetProperty(propertyName, out var window))
        {
            return;
        }

        double used;
        DateTimeOffset resetsAt;
        var isReset = false;
        if (window.ValueKind == JsonValueKind.Null)
        {
            used = 0;
            resetsAt = observedAt;
            isReset = true;
        }
        else
        {
            if (window.ValueKind != JsonValueKind.Object ||
                !window.TryGetProperty(
                    utilizationPropertyName,
                    out var utilizationElement) ||
                !TryReadDouble(utilizationElement, out var utilization))
            {
                return;
            }

            used = Math.Clamp(utilization, 0, 100);
            if (!window.TryGetProperty("resets_at", out var resetElement) ||
                !TryReadResetAt(resetElement, out resetsAt))
            {
                // Claude omits the reset timestamp when a window is
                // completely unused. Keep the full-remaining meter instead
                // of dropping the 5H/weekly graph from both views.
                if (used > 0)
                {
                    return;
                }

                resetsAt = observedAt;
                isReset = true;
            }
        }

        meters.Add(
            new UsageMeter(
                $"claude-{propertyName.Replace('_', '-')}",
                label,
                100 - used,
                100,
                "percent",
                resetsAt - duration,
                resetsAt,
                isReset));
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
