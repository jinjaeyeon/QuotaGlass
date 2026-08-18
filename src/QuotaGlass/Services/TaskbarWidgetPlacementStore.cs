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
    private static readonly string ScreenSettingsPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "QuotaGlass",
        "taskbar-widget-screen-position.txt");
    private static readonly string TaskbarMonitorSettingsPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "QuotaGlass",
        "taskbar-widget-monitor-position.txt");

    internal readonly record struct ScreenPosition(int X, int Y);
    internal readonly record struct MonitorPosition(int X, int Y);

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

    public static ScreenPosition? LoadScreenPosition()
    {
        try
        {
            if (!File.Exists(ScreenSettingsPath))
            {
                return null;
            }

            var values = File.ReadAllLines(ScreenSettingsPath);
            return values.Length >= 2 &&
                   int.TryParse(
                       values[0],
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out var x) &&
                   int.TryParse(
                       values[1],
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out var y)
                ? new ScreenPosition(x, y)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveScreenPosition(int x, int y)
    {
        try
        {
            var directory = Path.GetDirectoryName(ScreenSettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(
                ScreenSettingsPath,
                [
                    x.ToString(CultureInfo.InvariantCulture),
                    y.ToString(CultureInfo.InvariantCulture)
                ]);
        }
        catch
        {
            // A read-only profile must not prevent the widget from working.
        }
    }

    public static MonitorPosition? LoadTaskbarMonitorPosition() =>
        LoadTaskbarMonitorPosition(TaskbarMonitorSettingsPath);

    internal static MonitorPosition? LoadTaskbarMonitorPosition(
        string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return null;
            }

            var values = File.ReadAllLines(settingsPath);
            return values.Length >= 2 &&
                   int.TryParse(
                       values[0],
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out var x) &&
                   int.TryParse(
                       values[1],
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out var y)
                ? new MonitorPosition(x, y)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveTaskbarMonitorPosition(int x, int y) =>
        SaveTaskbarMonitorPosition(TaskbarMonitorSettingsPath, x, y);

    internal static void SaveTaskbarMonitorPosition(
        string settingsPath,
        int x,
        int y)
    {
        try
        {
            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(
                settingsPath,
                [
                    x.ToString(CultureInfo.InvariantCulture),
                    y.ToString(CultureInfo.InvariantCulture)
                ]);
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

            if (File.Exists(ScreenSettingsPath))
            {
                File.Delete(ScreenSettingsPath);
            }

            if (File.Exists(TaskbarMonitorSettingsPath))
            {
                File.Delete(TaskbarMonitorSettingsPath);
            }
        }
        catch
        {
            // Position can still be reset for the current process.
        }
    }
}
