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
        ResetCreditRedeemOption? redeem = null;
        if (summary.TryGetProperty("credits", out var credits) &&
            credits.ValueKind == JsonValueKind.Array)
        {
            foreach (var credit in credits.EnumerateArray())
            {
                if (credit.ValueKind != JsonValueKind.Object ||
                    (credit.TryGetProperty("status", out var status) &&
                     status.ValueKind == JsonValueKind.String &&
                     status.GetString() is not "available"))
                {
                    continue;
                }

                // expiresAt이 null이면 만료되지 않는 티켓이다.
                DateTimeOffset? expiresAt = null;
                if (credit.TryGetProperty("expiresAt", out var expiry) &&
                    expiry.ValueKind == JsonValueKind.Number &&
                    expiry.TryGetInt64(out var unixSeconds))
                {
                    try
                    {
                        expiresAt = DateTimeOffset.FromUnixTimeSeconds(
                            unixSeconds);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        continue;
                    }
                }

                if (expiresAt is { } value &&
                    (earliestExpiresAt is null || value < earliestExpiresAt))
                {
                    earliestExpiresAt = value;
                }

                // 가장 빨리 만료되는 티켓부터 사용한다. 만료가 없는 티켓은 마지막.
                if (ReadString(credit, "id") is { } id &&
                    (redeem is null ||
                     (expiresAt ?? DateTimeOffset.MaxValue) <
                     (redeem.ExpiresAt ?? DateTimeOffset.MaxValue)))
                {
                    redeem = new ResetCreditRedeemOption(
                        id,
                        ReadString(credit, "title") ?? "리셋 티켓",
                        ReadString(credit, "resetType"),
                        1,
                        expiresAt,
                        true,
                        null);
                }
            }
        }

        return new ResetCreditSummary(
            Math.Max(0, availableCount),
            earliestExpiresAt,
            redeem);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
