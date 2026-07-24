using System.Text.RegularExpressions;

namespace QuotaGlass.Services;

public static partial class TerminalText
{
    public static string StripControlSequences(string value) =>
        ControlSequenceRegex().Replace(value, string.Empty)
            .Replace("\a", string.Empty, StringComparison.Ordinal);

    [GeneratedRegex(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))",
        RegexOptions.Compiled)]
    private static partial Regex ControlSequenceRegex();
}
