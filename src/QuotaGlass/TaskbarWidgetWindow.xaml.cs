using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QuotaGlass.Services;
using QuotaGlass.ViewModels;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace QuotaGlass;

public partial class TaskbarWidgetWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WhMouseLowLevel = 14;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint GwHwndNext = 2;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly string[] DefaultProviderIds =
    [
        "claude-code",
        "codex"
    ];

    private readonly Action _openFullWindow;
    private readonly Action _exitApplication;
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _positionTimer;
    private readonly HashSet<string> _selectedProviderIds;
    private readonly LowLevelMouseProc _mouseHookCallback;
    private double? _positionRatio;
    private nint _mouseHook;
    private NativePoint _dragStartCursor;
    private NativeRect _dragStartWindow;
    private NativeRect _dragTaskbar;
    private nint _lastTaskbar;
    private bool _isPointerDown;
    private bool _isDragging;
    private bool _isClosed;

    public ObservableCollection<ProviderUsageViewModel> WidgetProviders
    {
        get;
    } = [];

    public TaskbarWidgetWindow(
        MainViewModel viewModel,
        Action openFullWindow,
        Action exitApplication)
    {
        InitializeComponent();

        DataContext = viewModel;
        _viewModel = viewModel;
        _openFullWindow = openFullWindow;
        _exitApplication = exitApplication;
        _mouseHookCallback = OnLowLevelMouse;
        _selectedProviderIds = TaskbarWidgetProviderStore.Load()
            ?? new HashSet<string>(
                DefaultProviderIds,
                StringComparer.Ordinal);
        _positionRatio = TaskbarWidgetPlacementStore.Load();
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _positionTimer.Tick += OnPositionTimerTick;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        _viewModel.Providers.CollectionChanged += OnProvidersChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        UpdateWidgetProviders();
    }

    public void CloseForExit()
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        Close();
    }

    public void ResetPlacement()
    {
        _positionRatio = null;
        TaskbarWidgetPlacementStore.Reset();
        PositionOnTaskbar();
    }

    public void RestoreAboveTaskbar()
    {
        if (_isClosed || !IsLoaded)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            () =>
            {
                if (_isClosed || !IsVisible)
                {
                    return;
                }

                PromoteToTopmost();
            });
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(
            handle,
            GwlExStyle,
            new nint(extendedStyle | WsExToolWindow | WsExNoActivate));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionOnTaskbar();
        _positionTimer.Start();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        PositionOnTaskbar();

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        PositionOnTaskbar();
        EnsureAboveTaskbar();
    }

    private void EnsureAboveTaskbar()
    {
        if (_isClosed ||
            !IsVisible ||
            WidgetChrome.ContextMenu?.IsOpen == true)
        {
            return;
        }

        var widget = new WindowInteropHelper(this).Handle;
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (widget == nint.Zero ||
            taskbar == nint.Zero ||
            !IsWindowAbove(taskbar, widget))
        {
            return;
        }

        PromoteToTopmost();
    }

    private void PromoteToTopmost()
    {
        SetWindowPos(
            new WindowInteropHelper(this).Handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoSize |
            SwpNoMove |
            SwpNoActivate |
            SwpNoOwnerZOrder);
    }

    private static bool IsWindowAbove(nint candidate, nint reference)
    {
        for (var window = GetTopWindow(nint.Zero);
             window != nint.Zero;
             window = GetWindow(window, GwHwndNext))
        {
            if (window == reference)
            {
                return false;
            }

            if (window == candidate)
            {
                return true;
            }
        }

        return true;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(PositionOnTaskbar);

    private void WidgetChrome_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!GetCursorPos(out _dragStartCursor))
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (!GetWindowRect(handle, out _dragStartWindow) ||
            taskbar == nint.Zero ||
            !GetWindowRect(taskbar, out _dragTaskbar))
        {
            return;
        }

        _isPointerDown = true;
        _isDragging = false;
        WidgetChrome.CaptureMouse();
        e.Handled = true;
    }

    private void OpenFullWindowMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        _openFullWindow();

    private void RefreshMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        _viewModel.RefreshCommand.Execute(null);

    private void WidgetContextMenu_Opened(
        object sender,
        RoutedEventArgs e)
    {
        StartOutsideClickMonitor();
        UpdateWindowsStartupMenuItem();
        UpdateThemeMenuItems();

        for (var index = WidgetContextMenu.Items.Count - 1; index >= 0; index--)
        {
            if (WidgetContextMenu.Items[index] is WpfMenuItem
                {
                    Tag: ProviderMenuTag
                })
            {
                WidgetContextMenu.Items.RemoveAt(index);
            }
        }

        var insertionIndex =
            WidgetContextMenu.Items.IndexOf(ProviderVisibilityHeading) + 1;
        foreach (var provider in _viewModel.Providers
                     .GroupBy(item => item.Provider, StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            var icon = new WpfTextBlock
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Text = _selectedProviderIds.Contains(provider.Provider)
                    ? "✓"
                    : string.Empty
            };
            var item = new WpfMenuItem
            {
                Header = provider.DisplayName,
                Icon = icon,
                IsCheckable = true,
                IsChecked = _selectedProviderIds.Contains(provider.Provider),
                StaysOpenOnClick = true,
                Tag = new ProviderMenuTag(provider.Provider)
            };
            item.Click += (_, _) =>
            {
                if (item.IsChecked)
                {
                    _selectedProviderIds.Add(provider.Provider);
                }
                else
                {
                    _selectedProviderIds.Remove(provider.Provider);
                }

                icon.Text = item.IsChecked ? "✓" : string.Empty;
                TaskbarWidgetProviderStore.Save(_selectedProviderIds);
                UpdateWidgetProviders();
            };
            WidgetContextMenu.Items.Insert(insertionIndex++, item);
        }
    }

    private void WidgetContextMenu_Closed(
        object sender,
        RoutedEventArgs e) =>
        StopOutsideClickMonitor();

    private void StartOutsideClickMonitor()
    {
        if (_mouseHook != nint.Zero)
        {
            return;
        }

        _mouseHook = SetWindowsHookEx(
            WhMouseLowLevel,
            _mouseHookCallback,
            GetModuleHandle(null),
            0);
    }

    private void StopOutsideClickMonitor()
    {
        if (_mouseHook == nint.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = nint.Zero;
    }

    private nint OnLowLevelMouse(
        int code,
        nint message,
        nint data)
    {
        if (code >= 0 &&
            IsMouseButtonDown(message) &&
            WidgetContextMenu.IsOpen)
        {
            var mouseData = Marshal.PtrToStructure<LowLevelMouseHookData>(
                data);
            if (!IsInsideContextMenu(mouseData.Point))
            {
                WidgetContextMenu.IsOpen = false;
            }
        }

        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private bool IsInsideContextMenu(NativePoint point)
    {
        if (PresentationSource.FromVisual(WidgetContextMenu)
                is not HwndSource source ||
            !GetWindowRect(source.Handle, out var bounds))
        {
            return false;
        }

        return point.X >= bounds.Left &&
               point.X < bounds.Right &&
               point.Y >= bounds.Top &&
               point.Y < bounds.Bottom;
    }

    private static bool IsMouseButtonDown(nint message) =>
        message == WmLeftButtonDown ||
        message == WmRightButtonDown ||
        message == WmMiddleButtonDown ||
        message == WmXButtonDown;

    private void ResetPlacementMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        ResetPlacement();

    private void StartWithWindowsMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        WindowsStartupService.TrySetEnabled(
            StartWithWindowsMenuItem.IsChecked);
        UpdateWindowsStartupMenuItem();
    }

    private void LightThemeMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        SetTheme(AppThemeMode.Light);

    private void DarkThemeMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        SetTheme(AppThemeMode.Dark);

    private void SystemThemeMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        SetTheme(AppThemeMode.System);

    private void ExitMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        _exitApplication();

    private void WidgetChrome_MouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPointerDown ||
            e.LeftButton != MouseButtonState.Pressed ||
            !GetCursorPos(out var cursor))
        {
            return;
        }

        var deltaX = cursor.X - _dragStartCursor.X;
        var deltaY = cursor.Y - _dragStartCursor.Y;
        if (!_isDragging &&
            Math.Abs(deltaX) < 5 &&
            Math.Abs(deltaY) < 5)
        {
            return;
        }

        _isDragging = true;
        var width = _dragStartWindow.Right - _dragStartWindow.Left;
        var height = _dragStartWindow.Bottom - _dragStartWindow.Top;
        var innerLeft = _dragTaskbar.Left + 8;
        var innerRight = Math.Max(
            innerLeft,
            _dragTaskbar.Right - width - 8);
        var x = Math.Clamp(
            _dragStartWindow.Left + deltaX,
            innerLeft,
            innerRight);
        var y = _dragTaskbar.Top +
                Math.Max(
                    1,
                    ((_dragTaskbar.Bottom - _dragTaskbar.Top) - height) / 2);

        SetWindowPos(
            new WindowInteropHelper(this).Handle,
            HwndTopmost,
            x,
            y,
            width,
            height,
            SwpNoActivate);
        e.Handled = true;
    }

    private void WidgetChrome_MouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_isPointerDown)
        {
            return;
        }

        _isPointerDown = false;
        WidgetChrome.ReleaseMouseCapture();

        if (_isDragging)
        {
            SaveCurrentPosition();
            _isDragging = false;
        }
        else
        {
            _openFullWindow();
        }

        e.Handled = true;
    }

    private void PositionOnTaskbar()
    {
        if (!IsLoaded || _isDragging)
        {
            return;
        }

        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == nint.Zero ||
            !GetWindowRect(taskbar, out var taskbarRect))
        {
            return;
        }

        var source = PresentationSource.FromVisual(this);
        var toDevice = source?.CompositionTarget?.TransformToDevice ??
                       Matrix.Identity;
        var size = toDevice.Transform(
            new System.Windows.Point(ActualWidth, ActualHeight));
        var width = Math.Max(1, (int)Math.Ceiling(size.X));
        var height = Math.Max(1, (int)Math.Ceiling(size.Y));

        var taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        var taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        var isHorizontal = taskbarWidth >= taskbarHeight;
        int x;
        int y;

        if (isHorizontal)
        {
            var tray = FindWindowEx(taskbar, nint.Zero, "TrayNotifyWnd", null);
            NativeRect trayRect = default;
            var hasTrayRect = tray != nint.Zero &&
                              GetWindowRect(tray, out trayRect);
            var rightEdge = hasTrayRect
                ? trayRect.Left
                : taskbarRect.Right - 150;

            var innerLeft = taskbarRect.Left + 8;
            var innerRight = Math.Max(
                innerLeft,
                taskbarRect.Right - width - 8);
            x = _positionRatio is { } position
                ? innerLeft +
                  (int)Math.Round((innerRight - innerLeft) * position)
                : Math.Clamp(
                    rightEdge - width - 8,
                    innerLeft,
                    innerRight);
            y = taskbarRect.Top +
                Math.Max(1, (taskbarHeight - height) / 2);
        }
        else
        {
            x = taskbarRect.Left +
                Math.Max(1, (taskbarWidth - width) / 2);
            y = Math.Max(
                taskbarRect.Top + 8,
                taskbarRect.Bottom - height - 150);
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (_lastTaskbar == taskbar &&
            GetWindowRect(handle, out var currentRect) &&
            Math.Abs(currentRect.Left - x) <= 1 &&
            Math.Abs(currentRect.Top - y) <= 1 &&
            Math.Abs((currentRect.Right - currentRect.Left) - width) <= 1 &&
            Math.Abs((currentRect.Bottom - currentRect.Top) - height) <= 1)
        {
            return;
        }

        SetWindowPos(
            handle,
            HwndTopmost,
            x,
            y,
            width,
            height,
            SwpNoActivate);
        _lastTaskbar = taskbar;
    }

    private void SaveCurrentPosition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var windowRect))
        {
            return;
        }

        var width = windowRect.Right - windowRect.Left;
        var innerLeft = _dragTaskbar.Left + 8;
        var innerRight = Math.Max(
            innerLeft,
            _dragTaskbar.Right - width - 8);
        var range = innerRight - innerLeft;
        _positionRatio = range <= 0
            ? 0
            : Math.Clamp(
                (windowRect.Left - innerLeft) / (double)range,
                0,
                1);
        TaskbarWidgetPlacementStore.Save(_positionRatio.Value);
    }

    private void OnProvidersChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e) =>
        UpdateWidgetProviders();

    private void UpdateWindowsStartupMenuItem()
    {
        var isEnabled = WindowsStartupService.IsEnabled();
        StartWithWindowsMenuItem.IsChecked = isEnabled;
        StartWithWindowsCheckGlyph.Text = isEnabled ? "✓" : string.Empty;
    }

    private void SetTheme(AppThemeMode mode)
    {
        if (System.Windows.Application.Current is not App application)
        {
            return;
        }

        application.Theme.SetMode(mode);
        UpdateThemeMenuItems();
    }

    private void UpdateThemeMenuItems()
    {
        if (System.Windows.Application.Current is not App application)
        {
            return;
        }

        var mode = application.Theme.CurrentMode;
        LightThemeMenuItem.IsChecked = mode == AppThemeMode.Light;
        DarkThemeMenuItem.IsChecked = mode == AppThemeMode.Dark;
        SystemThemeMenuItem.IsChecked = mode == AppThemeMode.System;
        LightThemeCheckGlyph.Text =
            LightThemeMenuItem.IsChecked ? "✓" : string.Empty;
        DarkThemeCheckGlyph.Text =
            DarkThemeMenuItem.IsChecked ? "✓" : string.Empty;
        SystemThemeCheckGlyph.Text =
            SystemThemeMenuItem.IsChecked ? "✓" : string.Empty;
    }

    private void UpdateWidgetProviders()
    {
        var expected = _viewModel.Providers
            .Where(provider =>
                _selectedProviderIds.Contains(provider.Provider))
            .ToArray();

        for (var index = WidgetProviders.Count - 1; index >= 0; index--)
        {
            if (expected.All(provider =>
                    provider.Provider != WidgetProviders[index].Provider))
            {
                WidgetProviders.RemoveAt(index);
            }
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (index < WidgetProviders.Count &&
                WidgetProviders[index].Provider == expected[index].Provider)
            {
                WidgetProviders[index] = expected[index];
                continue;
            }

            var existingIndex = -1;
            for (var candidate = index + 1;
                 candidate < WidgetProviders.Count;
                 candidate++)
            {
                if (WidgetProviders[candidate].Provider ==
                    expected[index].Provider)
                {
                    existingIndex = candidate;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                WidgetProviders.Move(existingIndex, index);
                WidgetProviders[index] = expected[index];
            }
            else
            {
                WidgetProviders.Insert(index, expected[index]);
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        StopOutsideClickMonitor();
        _positionTimer.Stop();
        _positionTimer.Tick -= OnPositionTimerTick;
        _viewModel.Providers.CollectionChanged -= OnProvidersChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        base.OnClosed(e);
    }

    private sealed record ProviderMenuTag(string ProviderId);

    private delegate nint LowLevelMouseProc(
        int code,
        nint message,
        nint data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hook,
        LowLevelMouseProc callback,
        nint module,
        uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(
        nint hook,
        int code,
        nint message,
        nint data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(
        string? className,
        string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(
        nint parent,
        nint childAfter,
        string? className,
        string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        nint window,
        out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(
        nint window,
        int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern nint GetWindowLong32(
        nint window,
        int index);

    private static nint GetWindowLongPtr(nint window, int index) =>
        nint.Size == 8
            ? GetWindowLongPtr64(window, index)
            : GetWindowLong32(window, index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(
        nint window,
        int index,
        nint newLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern nint SetWindowLong32(
        nint window,
        int index,
        nint newLong);

    private static nint SetWindowLongPtr(
        nint window,
        int index,
        nint newLong) =>
        nint.Size == 8
            ? SetWindowLongPtr64(window, index, newLong)
            : SetWindowLong32(window, index, newLong);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseHookData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
}
