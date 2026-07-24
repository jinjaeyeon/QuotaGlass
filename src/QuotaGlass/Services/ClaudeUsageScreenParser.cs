using System.Globalization;
using System.Text.RegularExpressions;
using QuotaGlass.Models;

namespace QuotaGlass.Services;

public static partial class ClaudeUsageScreenParser
{
    public static IReadOnlyList<UsageMeter> Parse(
        string terminalOutput,
        DateTimeOffset observedAt)
    {
        var text = TerminalText.StripControlSequences(terminalOutput);
        var meters = new List<UsageMeter>();

        AddLastMatch(
            meters,
            SessionRegex(),
            text,
            "claude-five-hour",
            "5시간",
            TimeSpan.FromHours(5),
            observedAt);
        AddLastMatch(
            meters,
            WeeklyRegex(),
            text,
            "claude-seven-day",
            "주간",
            TimeSpan.FromDays(7),
            observedAt);

        return meters;
    }

    private static void AddLastMatch(
        ICollection<UsageMeter> meters,
        Regex regex,
        string text,
        string id,
        string label,
        TimeSpan duration,
        DateTimeOffset observedAt)
    {
        var matches = regex.Matches(text);
        if (matches.Count == 0)
        {
            return;
        }

        var match = matches[^1];
        if (!double.TryParse(
                match.Groups["used"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var used) ||
            !TryParseReset(
                match.Groups["reset"].Value,
                observedAt,
                duration,
                out var resetAt))
        {
            return;
        }

        meters.Add(
            new UsageMeter(
                id,
                label,
                100 - Math.Clamp(used, 0, 100),
                100,
                "percent",
                resetAt - duration,
                resetAt));
    }

    private static bool TryParseReset(
        string value,
        DateTimeOffset observedAt,
        TimeSpan windowDuration,
        out DateTimeOffset result)
    {
        var timezoneStart = value.IndexOf(" (", StringComparison.Ordinal);
        var raw = (timezoneStart >= 0 ? value[..timezoneStart] : value).Trim();
        var formats = new[]
        {
            "h:mmtt",
            "htt",
            "MMM d, h:mmtt",
            "MMM d, htt"
        };

        if (!DateTime.TryParseExact(
                raw,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            result = default;
            return false;
        }

        var hasDate = raw.Contains(',', StringComparison.Ordinal);
        var local = hasDate
            ? new DateTime(
                observedAt.Year,
                parsed.Month,
                parsed.Day,
                parsed.Hour,
                parsed.Minute,
                0)
            : new DateTime(
                observedAt.Year,
                observedAt.Month,
                observedAt.Day,
                parsed.Hour,
                parsed.Minute,
                0);
        result = new DateTimeOffset(local, observedAt.Offset);

        if (!hasDate && result <= observedAt)
        {
            var nextDay = result.AddDays(1);
            if (nextDay - observedAt <=
                windowDuration + TimeSpan.FromMinutes(5))
            {
                result = nextDay;
            }
        }
        else if (hasDate && result < observedAt.AddDays(-1))
        {
            result = result.AddYears(1);
        }

        return true;
    }

    [GeneratedRegex(
        @"Current session\s+(?:\d+(?:\.\d+)?%\s+)*(?<used>\d+(?:\.\d+)?)%\s+used\s+Resets\s+(?<reset>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex SessionRegex();

    [GeneratedRegex(
        @"Current week \(all models\)\s+(?:\d+(?:\.\d+)?%\s+)*(?<used>\d+(?:\.\d+)?)%\s+used\s+Resets\s+(?<reset>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex WeeklyRegex();
}
