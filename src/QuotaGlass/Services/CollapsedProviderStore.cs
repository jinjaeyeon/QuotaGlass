using System.IO;
using System.Text.Json;

namespace QuotaGlass.Services;

internal static class CollapsedProviderStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "QuotaGlass",
        "collapsed-providers.json");

    public static HashSet<string> Load() => Load(SettingsPath);

    internal static HashSet<string> Load(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            var providerIds = JsonSerializer.Deserialize<string[]>(
                File.ReadAllText(settingsPath));
            return new HashSet<string>(
                providerIds?.Where(id => !string.IsNullOrWhiteSpace(id)) ?? [],
                StringComparer.Ordinal);
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public static void Save(IEnumerable<string> providerIds) =>
        Save(SettingsPath, providerIds);

    internal static void Save(
        string settingsPath,
        IEnumerable<string> providerIds)
    {
        try
        {
            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                settingsPath,
                JsonSerializer.Serialize(
                    providerIds
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray()));
        }
        catch
        {
            // A read-only profile must not prevent the window from opening.
        }
    }
}
