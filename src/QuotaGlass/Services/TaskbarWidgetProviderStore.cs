using System.IO;
using System.Text.Json;

namespace QuotaGlass.Services;

internal static class TaskbarWidgetProviderStore
{
    private static readonly string[] DefaultProviderIds =
    [
        "claude-code",
        "codex"
    ];

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "QuotaGlass",
        "taskbar-widget-providers.json");

    public static HashSet<string>? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            var providerIds = JsonSerializer.Deserialize<string[]>(
                File.ReadAllText(SettingsPath));
            return providerIds is null
                ? null
                : new HashSet<string>(
                    providerIds.Where(id => !string.IsNullOrWhiteSpace(id)),
                    StringComparer.Ordinal);
        }
        catch
        {
            return null;
        }
    }

    public static HashSet<string> LoadOrDefault() =>
        Load() ?? new HashSet<string>(DefaultProviderIds, StringComparer.Ordinal);

    public static void Save(IEnumerable<string> providerIds)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(
                    providerIds
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray()));
        }
        catch
        {
            // A read-only profile must not prevent the widget from working.
        }
    }
}
