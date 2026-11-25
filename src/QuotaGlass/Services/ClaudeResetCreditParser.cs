using System.Globalization;
using System.Text.Json;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

internal sealed record ClaudeResetGrant(
    string Id,
    string Label,
    string? Kind,
    long ResetsLeft,
    DateTimeOffset? EndsAt,
    bool Paused,
    bool UsableNow,
    string? UnavailableReason);

internal sealed record ClaudeResetStatus(
    IReadOnlyList<ClaudeResetGrant> Grants,
    string? NextGrantId)
{
    public bool IsUsable(ClaudeResetGrant grant, DateTimeOffset now) =>
        grant.ResetsLeft > 0 &&
        !grant.Paused &&
        (grant.EndsAt is null || grant.EndsAt > now);

    public ResetCreditSummary? ToSummary(DateTimeOffset now)
    {
        var held = Grants.Where(grant => IsUsable(grant, now)).ToArray();
        if (held.Length == 0)
        {
            return null;
        }

        var earliest = held
            .Where(grant => grant.EndsAt is not null)
            .Select(grant => grant.EndsAt)
            .Min();
        return new ResetCreditSummary(
            held.Sum(grant => grant.ResetsLeft),
            earliest);
    }

}

internal static class ClaudeResetCreditParser
{
    /// <summary>
    /// /api/oauth/usage?cedar_ember=1 응답의 cedar_ember 블록을 읽는다.
    /// 계정이 대상이 아니거나 블록이 없으면 null.
    /// </summary>
    public static ClaudeResetStatus? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty(
                "cedar_ember",
                out var block) ||
            block.ValueKind != JsonValueKind.Object ||
            !block.TryGetProperty("eligible", out var eligible) ||
            eligible.ValueKind != JsonValueKind.True)
        {
            return null;
        }

        var grants = new List<ClaudeResetGrant>();
        if (block.TryGetProperty("grants", out var grantsElement) &&
            grantsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var grant in grantsElement.EnumerateArray())
            {
                if (TryReadGrant(grant) is { } parsed)
                {
                    grants.Add(parsed);
                }
            }
        }

        return new ClaudeResetStatus(
            grants,
            ReadString(block, "next_grant_id"));
    }

    private static ClaudeResetGrant? TryReadGrant(JsonElement grant)
    {
        if (grant.ValueKind != JsonValueKind.Object ||
            ReadString(grant, "id") is not { } id ||
            !grant.TryGetProperty("resets_left", out var left) ||
            left.ValueKind != JsonValueKind.Number ||
            !left.TryGetInt64(out var resetsLeft))
        {
            return null;
        }

        return new ClaudeResetGrant(
            id,
            ReadString(grant, "label") ?? string.Empty,
            ReadString(grant, "type"),
            Math.Max(0, resetsLeft),
            ReadTimestamp(grant, "ends_at"),
            ReadBool(grant, "paused", false),
            ReadBool(grant, "usable_now", false),
            ReadString(grant, "unavailable_reason"));
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static bool ReadBool(
        JsonElement element,
        string name,
        bool fallback) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static DateTimeOffset? ReadTimestamp(
        JsonElement element,
        string name) =>
        ReadString(element, name) is { } text &&
        DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
}
