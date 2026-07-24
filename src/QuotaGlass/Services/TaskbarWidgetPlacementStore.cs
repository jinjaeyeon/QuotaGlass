using System.Globalization;
using System.IO;

namespace QuotaGlass.Services;

internal static class TaskbarWidgetPlacementStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "QuotaGlass",
        "taskbar-widget-position.txt");

    public static double? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            var text = File.ReadAllText(SettingsPath);
            return double.TryParse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value)
                ? Math.Clamp(value, 0, 1)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(double position)
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
                Math.Clamp(position, 0, 1).ToString(
                    "R",
                    CultureInfo.InvariantCulture));
        }
        catch
        {
            // A read-only profile must not prevent the widget from working.
        }
    }

    public static void Reset()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                File.Delete(SettingsPath);
            }
        }
        catch
        {
            // Position can still be reset for the current process.
        }
    }
}
