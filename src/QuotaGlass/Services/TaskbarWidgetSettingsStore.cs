using System.IO;

namespace QuotaGlass.Services;

internal static class TaskbarWidgetSettingsStore
{
    private static readonly string FreeMovementPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "QuotaGlass",
        "taskbar-widget-free-movement.txt");
    private static readonly string VerticalLayoutPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "QuotaGlass",
        "taskbar-widget-vertical-layout.txt");

    public static bool LoadFreeMovementEnabled()
    {
        try
        {
            if (!File.Exists(FreeMovementPath))
            {
                return false;
            }

            return bool.TryParse(
                       File.ReadAllText(FreeMovementPath),
                       out var enabled) &&
                   enabled;
        }
        catch
        {
            return false;
        }
    }

    public static void SaveFreeMovementEnabled(bool enabled)
    {
        try
        {
            var directory = Path.GetDirectoryName(FreeMovementPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FreeMovementPath, enabled.ToString());
        }
        catch
        {
            // A read-only profile must not prevent the widget from working.
        }
    }

    public static bool LoadVerticalLayoutEnabled()
    {
        try
        {
            if (!File.Exists(VerticalLayoutPath))
            {
                return false;
            }

            return bool.TryParse(
                       File.ReadAllText(VerticalLayoutPath),
                       out var enabled) &&
                   enabled;
        }
        catch
        {
            return false;
        }
    }

    public static void SaveVerticalLayoutEnabled(bool enabled)
    {
        try
        {
            var directory = Path.GetDirectoryName(VerticalLayoutPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(VerticalLayoutPath, enabled.ToString());
        }
        catch
        {
            // A read-only profile must not prevent the widget from working.
        }
    }
}
