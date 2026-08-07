using System.Text.Json;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public static class CodexRateLimitResetCreditParser
{
    public static ResetCreditSummary? Parse(JsonElement result)
    {
        if (!result.TryGetProperty("rateLimitResetCredits", out var summary) ||
            summary.ValueKind != JsonValueKind.Object ||
            !summary.TryGetProperty("availableCount", out var countElement) ||
            countElement.ValueKind != JsonValueKind.Number ||
            !countElement.TryGetInt64(out var availableCount))
        {
            return null;
        }

        DateTimeOffset? earliestExpiresAt = null;
        if (summary.TryGetProperty("credits", out var credits) &&
            credits.ValueKind == JsonValueKind.Array)
        {
            foreach (var credit in credits.EnumerateArray())
            {
                if (credit.ValueKind != JsonValueKind.Object ||
                    (credit.TryGetProperty("status", out var status) &&
                     status.ValueKind == JsonValueKind.String &&
                     status.GetString() is not "available") ||
                    !credit.TryGetProperty("expiresAt", out var expiry) ||
                    expiry.ValueKind != JsonValueKind.Number ||
                    !expiry.TryGetInt64(out var unixSeconds))
                {
                    continue;
                }

                DateTimeOffset expiresAt;
                try
                {
                    expiresAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }

                if (earliestExpiresAt is null || expiresAt < earliestExpiresAt)
                {
                    earliestExpiresAt = expiresAt;
                }
            }
        }

        return new ResetCreditSummary(
            Math.Max(0, availableCount),
            earliestExpiresAt);
    }
}
