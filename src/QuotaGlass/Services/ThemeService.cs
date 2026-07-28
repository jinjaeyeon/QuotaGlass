using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace QuotaGlass.Services;

internal enum AppThemeMode
{
    System,
    Light,
    Dark
}

internal sealed class ThemeService : IDisposable
{
    private const string PersonalizeRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "QuotaGlass",
        "theme.txt");

    private readonly System.Windows.Application _application;
    private bool _isDisposed;

    public ThemeService(System.Windows.Application application)
    {
        _application = application;
        CurrentMode = Load();
        Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public AppThemeMode CurrentMode { get; private set; }

    public event EventHandler? ThemeChanged;

    public void SetMode(AppThemeMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            return;
        }

        CurrentMode = mode;
        Save(mode);
        Apply();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(
        object sender,
        UserPreferenceChangedEventArgs e)
    {
        if (CurrentMode != AppThemeMode.System || _isDisposed)
        {
            return;
        }

        _application.Dispatcher.BeginInvoke(
            () =>
            {
                if (_isDisposed || CurrentMode != AppThemeMode.System)
                {
                    return;
                }

                Apply();
                ThemeChanged?.Invoke(this, EventArgs.Empty);
            });
    }

    private void Apply()
    {
        var useLightTheme = CurrentMode == AppThemeMode.Light ||
                            (CurrentMode == AppThemeMode.System &&
                             IsSystemLightTheme());
        var colors = useLightTheme
            ? LightColors
            : DarkColors;

        foreach (var (resourceKey, color) in colors)
        {
            _application.Resources[resourceKey] = new SolidColorBrush(
                (MediaColor)MediaColorConverter.ConvertFromString(color));
        }
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                PersonalizeRegistryPath);
            return key?.GetValue(AppsUseLightThemeValue) is int value
                ? value != 0
                : true;
        }
        catch
        {
            return true;
        }
    }

    private static AppThemeMode Load()
    {
        try
        {
            if (File.Exists(SettingsPath) &&
                Enum.TryParse<AppThemeMode>(
                    File.ReadAllText(SettingsPath).Trim(),
                    ignoreCase: true,
                    out var mode) &&
                Enum.IsDefined(mode))
            {
                return mode;
            }
        }
        catch
        {
            // A damaged or read-only profile falls back to the OS theme.
        }

        return AppThemeMode.System;
    }

    private static void Save(AppThemeMode mode)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(SettingsPath, mode.ToString());
        }
        catch
        {
            // Theme changes still apply for the current process.
        }
    }

    private static readonly IReadOnlyDictionary<string, string> DarkColors =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBrush"] = "#F2161920",
            ["SurfaceBrush"] = "#18FFFFFF",
            ["SurfaceHoverBrush"] = "#24FFFFFF",
            ["BorderBrush"] = "#2EFFFFFF",
            ["TextBrush"] = "#FFF7F8FA",
            ["MutedTextBrush"] = "#A8F7F8FA",
            ["SafeBrush"] = "#FF78D6A3",
            ["WarningBrush"] = "#FFFFC56D",
            ["DangerBrush"] = "#FFFF7185",
            ["TrackBrush"] = "#26FFFFFF",
            ["ContextMenuBrush"] = "#FF1C2028",
            ["WidgetBrush"] = "#FA161920",
            ["IconSurfaceBrush"] = "#20FFFFFF",
            ["BadgeBrush"] = "#16FFFFFF",
            ["MenuHoverBrush"] = "#26FFFFFF",
            ["SeparatorBrush"] = "#28FFFFFF",
            ["ScrollBarTrackBrush"] = "#0FFFFFFF",
            ["ScrollBarThumbBrush"] = "#4DFFFFFF",
            ["ScrollBarThumbHoverBrush"] = "#80FFFFFF",
            ["ScrollBarThumbPressedBrush"] = "#B378D6A3"
        };

    private static readonly IReadOnlyDictionary<string, string> LightColors =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBrush"] = "#F7F7F8FA",
            ["SurfaceBrush"] = "#FFFFFFFF",
            ["SurfaceHoverBrush"] = "#FFE9EDF2",
            ["BorderBrush"] = "#FFD0D5DD",
            ["TextBrush"] = "#FF171A21",
            ["MutedTextBrush"] = "#A85A6472",
            ["SafeBrush"] = "#FF238454",
            ["WarningBrush"] = "#FFB66B00",
            ["DangerBrush"] = "#FFD8324C",
            ["TrackBrush"] = "#22171A21",
            ["ContextMenuBrush"] = "#FFF7F8FA",
            ["WidgetBrush"] = "#FAF7F8FA",
            ["IconSurfaceBrush"] = "#14171A21",
            ["BadgeBrush"] = "#0F171A21",
            ["MenuHoverBrush"] = "#14171A21",
            ["SeparatorBrush"] = "#1F171A21",
            ["ScrollBarTrackBrush"] = "#12171A21",
            ["ScrollBarThumbBrush"] = "#66171A21",
            ["ScrollBarThumbHoverBrush"] = "#99171A21",
            ["ScrollBarThumbPressedBrush"] = "#CC238454"
        };
}
