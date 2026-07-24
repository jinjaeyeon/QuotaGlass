using Microsoft.Win32;

namespace QuotaGlass.Services;

internal static class WindowsStartupService
{
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuotaGlass";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var configuredCommand = key?.GetValue(ValueName) as string;
            return string.Equals(
                configuredCommand,
                BuildCommand(),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                key.SetValue(
                    ValueName,
                    BuildCommand(),
                    RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }

            return IsEnabled() == enabled;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildCommand()
    {
        var executablePath = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(executablePath)
            ? string.Empty
            : $"\"{executablePath}\"";
    }
}
