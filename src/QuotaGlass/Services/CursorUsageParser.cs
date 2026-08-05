using System.Globalization;
using System.Text.Json;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public static class CursorUsageParser
{
    public static IReadOnlyList<UsageMeter> Parse(
        JsonElement result,
        DateTimeOffset now)
    {
        if (!result.TryGetProperty("planUsage", out var planUsage) ||
            planUsage.ValueKind != JsonValueKind.Object ||
            !TryReadDouble(planUsage, "totalPercentUsed", out var usedPercent))
        {
            return [];
        }

        var (windowStart, resetsAt) = ReadBillingCycle(result, now);
        return
        [
            new UsageMeter(
                "included-monthly",
                "월간 포함량",
                100 - Math.Clamp(usedPercent, 0, 100),
                100,
                "percent",
                windowStart,
                resetsAt)
        ];
    }

    public static string? ReadPlanName(JsonElement result)
    {
        if (!result.TryGetProperty("planInfo", out var planInfo) ||
            planInfo.ValueKind != JsonValueKind.Object ||
            !planInfo.TryGetProperty("planName", out var planName) ||
            planName.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return planName.GetString();
    }

    private static (DateTimeOffset Start, DateTimeOffset End) ReadBillingCycle(
        JsonElement result,
        DateTimeOffset now)
    {
        if (TryReadUnixMilliseconds(
                result,
                "billingCycleStart",
                out var windowStart) &&
            TryReadUnixMilliseconds(
                result,
                "billingCycleEnd",
                out var resetsAt) &&
            resetsAt > windowStart)
        {
            return (windowStart, resetsAt);
        }

        var end = now.AddMonths(1);
        return (now, end);
    }

    private static bool TryReadUnixMilliseconds(
        JsonElement parent,
        string propertyName,
        out DateTimeOffset value)
    {
        value = default;
        if (!parent.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        long milliseconds;
        if (element.ValueKind == JsonValueKind.String)
        {
            if (!long.TryParse(
                    element.GetString(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out milliseconds))
            {
                return false;
            }
        }
        else if (element.ValueKind == JsonValueKind.Number &&
                 element.TryGetInt64(out milliseconds))
        {
        }
        else
        {
            return false;
        }

        try
        {
            value = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryReadDouble(
        JsonElement parent,
        string propertyName,
        out double value)
    {
        value = 0;
        return parent.TryGetProperty(propertyName, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetDouble(out value);
    }
}
