using QuotaGlass.Models;

namespace QuotaGlass.ViewModels;

public sealed class ProviderUsageViewModel
{
    public ProviderUsageViewModel(UsageSnapshot snapshot, DateTimeOffset now)
    {
        Provider = snapshot.Provider;
        DisplayName = snapshot.DisplayName;
        IconText = snapshot.IconText;
        IconGeometry = AgentIconCatalog.Find(snapshot.Provider);
        AccountLabel = snapshot.AccountLabel;
        Source = snapshot.Source;
        StatusMessage = snapshot.StatusMessage;

        Meters = snapshot.Meters
            .Select(meter => new MeterUsageViewModel(meter, now))
            .OrderBy(meter => meter.PaceDelta)
            .ToArray();

        PrimaryMeter = Meters.FirstOrDefault();
        SecondaryMeters = Meters.Skip(1).ToArray();
        CompactMeters = Meters.Take(2).ToArray();
        CompactTitle = FormatCompactTitle(DisplayName);
        CompactToolTip = BuildCompactToolTip();
    }

    public string Provider { get; }
    public string DisplayName { get; }
    public string IconText { get; }
    public System.Windows.Media.Geometry? IconGeometry { get; }
    public bool HasVectorIcon => IconGeometry is not null;
    public string AccountLabel { get; }
    public string Source { get; }
    public string? StatusMessage { get; }
    public IReadOnlyList<MeterUsageViewModel> Meters { get; }
    public MeterUsageViewModel? PrimaryMeter { get; }
    public IReadOnlyList<MeterUsageViewModel> SecondaryMeters { get; }
    public IReadOnlyList<MeterUsageViewModel> CompactMeters { get; }
    public string CompactTitle { get; }
    public string CompactToolTip { get; }
    public bool HasUsage => PrimaryMeter is not null;

    private string BuildCompactToolTip()
    {
        if (!HasUsage)
        {
            return $"{DisplayName}: {StatusMessage}";
        }

        var meters = Meters.Select(meter =>
            $"{meter.Label} {meter.RemainingText}" +
            (meter.IsWarning ? " ⚠" : string.Empty));
        return $"{DisplayName}\n{string.Join("\n", meters)}\n클릭해서 전체 보기";
    }

    private static string FormatCompactTitle(string displayName) =>
        displayName
            .Replace("Claude Code", "Claude", StringComparison.Ordinal)
            .Replace("JetBrains AI", "JetBrains", StringComparison.Ordinal);
}
