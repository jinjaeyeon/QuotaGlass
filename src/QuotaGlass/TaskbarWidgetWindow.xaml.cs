using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QuotaGlass.Services;
using QuotaGlass.ViewModels;
using Brushes = System.Windows.Media.Brushes;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfPopup = System.Windows.Controls.Primitives.Popup;
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
    private const uint MonitorDefaultToNearest = 2;
    private const double DefaultWidgetHeight = 44;
    private const double CompactResourceMetricsWidth = 86;
    private const double VerticalProviderGraphWidth = 120;
    private const int MaxWidgetTransparencyPercent = 75;
    private static readonly nint HwndTopmost = new(-1);
    private readonly Action _openFullWindow;
    private readonly Action _exitApplication;
    private readonly AppUpdateService _updateService;
    private readonly Action<PreparedAppUpdate> _restartWithUpdate;
    private readonly MainViewModel _viewModel;
    private readonly SystemResourceMonitor _resourceMonitor = new();
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _resourceTimer;
    private readonly HashSet<string> _selectedProviderIds;
    private readonly LowLevelMouseProc _mouseHookCallback;
    private double? _positionRatio;
    private nint _mouseHook;
    private NativePoint _dragStartCursor;
    private NativeRect _dragStartWindow;
    private NativeRect _dragBounds;
    private nint _dragTaskbar;
    private nint _lastTaskbar;
    private bool _freeMovementEnabled;
    private bool _verticalLayoutEnabled;
    private int _widgetTransparencyPercent;
    private bool _widgetBackgroundEnabled;
    private bool _resourceMetricsEnabled;
    private bool _antiAliasingEnabled;
    private TaskbarWidgetPlacementStore.ScreenPosition? _screenPosition;
    private TaskbarWidgetPlacementStore.MonitorPosition?
        _taskbarMonitorPosition;
    private bool _isPointerDown;
    private bool _isDragging;
    private bool _isHiddenAutomatically;
    private bool _isClosed;
    private string? _updateStatusText;

    public ObservableCollection<ProviderUsageViewModel> WidgetProviders
    {
        get;
    } = [];

    public SystemResourceUsageViewModel SystemResources { get; } = new();

    public static readonly DependencyProperty IsVerticalLayoutProperty =
        DependencyProperty.Register(
            nameof(IsVerticalLayout),
            typeof(bool),
            typeof(TaskbarWidgetWindow),
            new PropertyMetadata(false));

    public bool IsVerticalLayout
    {
        get => (bool)GetValue(IsVerticalLayoutProperty);
        private set => SetValue(IsVerticalLayoutProperty, value);
    }

    public TaskbarWidgetWindow(
        MainViewModel viewModel,
        Action openFullWindow,
        Action exitApplication,
        AppUpdateService updateService,
        Action<PreparedAppUpdate> restartWithUpdate)
    {
        InitializeComponent();

        DataContext = viewModel;
        _viewModel = viewModel;
        _openFullWindow = openFullWindow;
        _exitApplication = exitApplication;
        _updateService = updateService;
        _restartWithUpdate = restartWithUpdate;
        AppVersionMenuItem.Header =
            $"QuotaGlass v{_updateService.CurrentVersion.ToString(3)}";
        _mouseHookCallback = OnLowLevelMouse;
        _selectedProviderIds = TaskbarWidgetProviderStore.LoadOrDefault();
        _positionRatio = TaskbarWidgetPlacementStore.Load();
        _screenPosition = TaskbarWidgetPlacementStore.LoadScreenPosition();
        _taskbarMonitorPosition =
            TaskbarWidgetPlacementStore.LoadTaskbarMonitorPosition();
        _freeMovementEnabled =
            TaskbarWidgetSettingsStore.LoadFreeMovementEnabled();
        _verticalLayoutEnabled =
            TaskbarWidgetSettingsStore.LoadVerticalLayoutEnabled();
        _widgetTransparencyPercent =
            Math.Clamp(
                TaskbarWidgetSettingsStore.LoadTransparencyPercent(),
                0,
                MaxWidgetTransparencyPercent);
        _widgetBackgroundEnabled =
            TaskbarWidgetSettingsStore.LoadBackgroundEnabled();
        _resourceMetricsEnabled =
            TaskbarWidgetSettingsStore.LoadResourceMetricsEnabled();
        _antiAliasingEnabled =
            TaskbarWidgetSettingsStore.LoadAntiAliasingEnabled();
        ApplyRenderingSettings();
        ApplyWidgetTransparency();
        ApplyWidgetBackground();
        ApplyResourceMetricsVisibility();
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _positionTimer.Tick += OnPositionTimerTick;
        _resourceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _resourceTimer.Tick += OnResourceTimerTick;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        _viewModel.Providers.CollectionChanged += OnProvidersChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _updateService.StateChanged += OnUpdateStateChanged;
        UpdateWidgetProviders();
        UpdateCheckForUpdatesMenuItem();
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
        _screenPosition = null;
        _taskbarMonitorPosition = null;
        TaskbarWidgetPlacementStore.Reset();
        PositionWidget();
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
        PositionWidget();
        _positionTimer.Start();
        SampleSystemResources();
        _resourceTimer.Start();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        PositionWidget();

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        if (UpdateAutomaticVisibility())
        {
            return;
        }

        PositionWidget();
        if (_freeMovementEnabled)
        {
            PromoteToTopmost();
        }
        else
        {
            EnsureAboveTaskbar();
        }
    }

    private bool UpdateAutomaticVisibility()
    {
        if (_isClosed || !IsLoaded)
        {
            return false;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var taskbar = GetTaskbarForCurrentWidget();
        var isTaskbarHidden = taskbar != nint.Zero &&
                              !TaskbarVisibilityDetector.IsShown(taskbar);
        var shouldHide = !_freeMovementEnabled &&
                         (FullscreenWindowDetector.IsForegroundFullscreenOn(
                               handle) ||
                          isTaskbarHidden &&
                          !WidgetContextMenu.IsOpen);
        if (shouldHide == _isHiddenAutomatically)
        {
            return shouldHide;
        }

        _isHiddenAutomatically = shouldHide;
        if (shouldHide)
        {
            WidgetContextMenu.IsOpen = false;
            Hide();
            return true;
        }

        Show();
        PositionWidget();
        PromoteToTopmost();
        return false;
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
        var taskbar = GetTaskbarForCurrentWidget();
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
        Dispatcher.BeginInvoke(PositionWidget);

    private void WidgetChrome_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!GetCursorPos(out _dragStartCursor))
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out _dragStartWindow))
        {
            return;
        }

        if (_freeMovementEnabled)
        {
            if (!GetMonitorBoundsForPoint(
                    _dragStartCursor,
                    out _dragBounds))
            {
                return;
            }
        }
        else
        {
            if (!TryGetTaskbarForPoint(
                    _dragStartCursor,
                    out var taskbar) ||
                !GetWindowRect(taskbar.Handle, out _dragBounds))
            {
                return;
            }

            _dragTaskbar = taskbar.Handle;
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

    private async void CheckForUpdatesMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_updateService.CanSelfUpdate ||
            _updateService.IsChecking ||
            _updateService.IsPreparingUpdate)
        {
            return;
        }

        CheckForUpdatesMenuItem.IsEnabled = false;
        CheckForUpdatesMenuItem.Header = "업데이트 확인 중…";
        try
        {
            await _updateService.CheckForUpdateAsync(
                CancellationToken.None,
                throwOnError: true);

            if (_updateService.AvailableUpdate is null)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "현재 최신 버전입니다.",
                    "QuotaGlass 업데이트",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                this,
                $"업데이트 확인에 실패했습니다.\n{exception.Message}",
                "QuotaGlass 업데이트",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            UpdateCheckForUpdatesMenuItem();
            UpdateAppUpdateMenuItem();
        }
    }

    private void AntiAliasingMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        SetAntiAliasingEnabled(!_antiAliasingEnabled);

    private void SetAntiAliasingEnabled(bool enabled)
    {
        _antiAliasingEnabled = enabled;
        TaskbarWidgetSettingsStore.SaveAntiAliasingEnabled(enabled);
        UpdateAntiAliasingMenuItem();
        ApplyRenderingSettings();
    }

    private async void UpdateMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_updateService.AvailableUpdate is null ||
            _updateService.IsPreparingUpdate)
        {
            return;
        }

        UpdateAppUpdateMenuItem();
        try
        {
            SetUpdateStatus("업데이트 다운로드 중…");
            var update = await _updateService.PrepareUpdateAsync(
                CancellationToken.None);
            SetUpdateStatus("재시작 중…");
            _restartWithUpdate(update);
        }
        catch (Exception exception)
        {
            SetUpdateStatus(null);
            UpdateAppUpdateMenuItem();
            System.Windows.MessageBox.Show(
                this,
                $"업데이트에 실패했습니다.\n{exception.Message}",
                "QuotaGlass 업데이트",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void WidgetContextMenu_Opened(
        object sender,
        RoutedEventArgs e)
    {
        StartOutsideClickMonitor();
        RenderingSettings.ApplyToVisualTree(
            WidgetContextMenu,
            _antiAliasingEnabled);
        UpdateWindowsStartupMenuItem();
        UpdateFreeMovementMenuItem();
        UpdateVerticalLayoutMenuItem();
        UpdateWidgetTransparencyMenuItems();
        UpdateWidgetBackgroundMenuItem();
        UpdateResourceMetricsMenuItem();
        UpdateAntiAliasingMenuItem();
        UpdateThemeMenuItems();
        UpdateUpdateMenuItems();

        for (var index = ProviderVisibilityMenuItem.Items.Count - 1;
             index >= 0;
             index--)
        {
            if (ProviderVisibilityMenuItem.Items[index] is WpfMenuItem
                {
                    Tag: ProviderMenuTag
                })
            {
                ProviderVisibilityMenuItem.Items.RemoveAt(index);
            }
        }

        var insertionIndex = ProviderVisibilityMenuItem.Items.Count;
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
                _viewModel.SetTaskbarWidgetProviderVisibility(
                    provider.Provider,
                    item.IsChecked);
                UpdateWidgetProviders();
            };
            ProviderVisibilityMenuItem.Items.Insert(insertionIndex++, item);
        }
    }

    private void WidgetContextMenu_Closed(
        object sender,
        RoutedEventArgs e) =>
        StopOutsideClickMonitor();

    private void ApplyRenderingSettings()
    {
        RenderingSettings.ApplyToVisualTree(this, _antiAliasingEnabled);
        RenderingSettings.ApplyToVisualTree(
            WidgetContextMenu,
            _antiAliasingEnabled);
    }

    private void OnUpdateStateChanged(object? sender, EventArgs e)
    {
        if (_isClosed)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateUpdateMenuItems);
            return;
        }

        UpdateUpdateMenuItems();
    }

    private void UpdateUpdateMenuItems()
    {
        UpdateCheckForUpdatesMenuItem();
        UpdateAppUpdateMenuItem();
    }

    private void UpdateCheckForUpdatesMenuItem()
    {
        CheckForUpdatesMenuItem.IsEnabled =
            _updateService.CanSelfUpdate &&
            !_updateService.IsChecking &&
            !_updateService.IsPreparingUpdate;
        CheckForUpdatesMenuItem.Header = _updateService.IsChecking
            ? "업데이트 확인 중…"
            : "업데이트 확인";
    }

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

        if (IsInsideBounds(point, bounds))
        {
            return true;
        }

        foreach (var menuItem in EnumerateMenuItems(WidgetContextMenu))
        {
            if (menuItem.Template.FindName("SubMenuPopup", menuItem)
                    is not WpfPopup
                    {
                        IsOpen: true,
                        Child: Visual child
                    } ||
                PresentationSource.FromVisual(child)
                    is not HwndSource popupSource ||
                !GetWindowRect(popupSource.Handle, out var popupBounds))
            {
                continue;
            }

            if (IsInsideBounds(point, popupBounds))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<WpfMenuItem> EnumerateMenuItems(
        System.Windows.Controls.ItemsControl itemsControl)
    {
        foreach (var item in itemsControl.Items)
        {
            if (item is not WpfMenuItem menuItem)
            {
                continue;
            }

            yield return menuItem;
            foreach (var child in EnumerateMenuItems(menuItem))
            {
                yield return child;
            }
        }
    }

    private void OnResourceTimerTick(object? sender, EventArgs e) =>
        SampleSystemResources();

    private void SampleSystemResources()
    {
        if (_isClosed)
        {
            return;
        }

        try
        {
            SystemResources.Apply(_resourceMonitor.Sample());
        }
        catch
        {
            // Resource monitoring must not prevent the widget from opening.
        }
    }

    private static bool IsInsideBounds(
        NativePoint point,
        NativeRect bounds) =>
        point.X >= bounds.Left &&
        point.X < bounds.Right &&
        point.Y >= bounds.Top &&
        point.Y < bounds.Bottom;

    private static bool IsMouseButtonDown(nint message) =>
        message == WmLeftButtonDown ||
        message == WmRightButtonDown ||
        message == WmMiddleButtonDown ||
        message == WmXButtonDown;

    private void ResetPlacementMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        ResetPlacement();

    private void FreeMovementMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        SetFreeMovementEnabled(FreeMovementMenuItem.IsChecked);

    private void VerticalLayoutMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        SetVerticalLayoutEnabled(VerticalLayoutMenuItem.IsChecked);

    private void WidgetTransparencyMenuItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem menuItem ||
            !int.TryParse(menuItem.Tag?.ToString(), out var transparencyPercent))
        {
            return;
        }

        SetWidgetTransparency(transparencyPercent);
    }

    private void WidgetBackgroundMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        SetWidgetBackgroundEnabled(WidgetBackgroundMenuItem.IsChecked);

    private void SystemResourceMetricsMenuItem_Click(
        object sender,
        RoutedEventArgs e) =>
        SetResourceMetricsEnabled(SystemResourceMetricsMenuItem.IsChecked);

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
        int x;
        int y;
        if (_freeMovementEnabled)
        {
            if (!GetMonitorBoundsForPoint(cursor, out var monitorBounds))
            {
                monitorBounds = _dragBounds;
            }

            var innerLeft = monitorBounds.Left + 8;
            var innerRight = Math.Max(
                innerLeft,
                monitorBounds.Right - width - 8);
            var innerTop = monitorBounds.Top + 8;
            var innerBottom = Math.Max(
                innerTop,
                monitorBounds.Bottom - height - 8);
            x = Math.Clamp(
                _dragStartWindow.Left + deltaX,
                innerLeft,
                innerRight);
            y = Math.Clamp(
                _dragStartWindow.Top + deltaY,
                innerTop,
                innerBottom);
        }
        else
        {
            if (!TryGetTaskbarForPoint(cursor, out var taskbar))
            {
                return;
            }

            _dragTaskbar = taskbar.Handle;
            _dragBounds = taskbar.Bounds;
            var taskbarWidth = _dragBounds.Right - _dragBounds.Left;
            var taskbarHeight = _dragBounds.Bottom - _dragBounds.Top;
            if (taskbarWidth >= taskbarHeight)
            {
                var innerLeft = _dragBounds.Left + 8;
                var innerRight = Math.Max(
                    innerLeft,
                    _dragBounds.Right - width - 8);
                x = Math.Clamp(
                    _dragStartWindow.Left + deltaX,
                    innerLeft,
                    innerRight);
                y = _dragBounds.Top +
                    Math.Max(
                        1,
                        (taskbarHeight - height) / 2);
            }
            else
            {
                var innerTop = _dragBounds.Top + 8;
                var innerBottom = Math.Max(
                    innerTop,
                    _dragBounds.Bottom - height - 8);
                x = _dragBounds.Left +
                    Math.Max(1, (taskbarWidth - width) / 2);
                y = Math.Clamp(
                    _dragStartWindow.Top + deltaY,
                    innerTop,
                    innerBottom);
            }
        }

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

    private void PositionWidget()
    {
        if (_freeMovementEnabled)
        {
            PositionOnScreen();
        }
        else
        {
            PositionOnTaskbar();
        }
    }

    private void PositionOnScreen()
    {
        if (!IsLoaded || _isDragging)
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

        var savedPosition = _screenPosition;
        NativePoint referencePoint;
        if (savedPosition is { } saved)
        {
            referencePoint = new NativePoint
            {
                X = saved.X + width / 2,
                Y = saved.Y + height / 2
            };
        }
        else if (!GetCursorPos(out referencePoint))
        {
            return;
        }

        if (!GetMonitorBoundsForPoint(referencePoint, out var monitorBounds))
        {
            return;
        }

        var innerLeft = monitorBounds.Left + 8;
        var innerTop = monitorBounds.Top + 8;
        var innerRight = Math.Max(
            innerLeft,
            monitorBounds.Right - width - 8);
        var innerBottom = Math.Max(
            innerTop,
            monitorBounds.Bottom - height - 8);
        var x = savedPosition is { } savedX
            ? Math.Clamp(savedX.X, innerLeft, innerRight)
            : innerRight;
        var y = savedPosition is { } savedY
            ? Math.Clamp(savedY.Y, innerTop, innerBottom)
            : innerBottom;
        _screenPosition = new TaskbarWidgetPlacementStore.ScreenPosition(
            x,
            y);

        var handle = new WindowInteropHelper(this).Handle;
        if (GetWindowRect(handle, out var currentRect) &&
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
    }

    private void PositionOnTaskbar()
    {
        if (!IsLoaded || _isDragging || WidgetContextMenu.IsOpen)
        {
            return;
        }

        if (!TryGetTaskbarForSavedMonitor(out var taskbar) ||
            !GetWindowRect(taskbar.Handle, out var taskbarRect))
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
            var tray = FindWindowEx(
                taskbar.Handle,
                nint.Zero,
                "TrayNotifyWnd",
                null);
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
            var innerTop = taskbarRect.Top + 8;
            var innerBottom = Math.Max(
                innerTop,
                taskbarRect.Bottom - height - 8);
            y = _positionRatio is { } position
                ? innerTop +
                  (int)Math.Round((innerBottom - innerTop) * position)
                : Math.Max(
                    taskbarRect.Top + 8,
                    taskbarRect.Bottom - height - 150);
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (_lastTaskbar == taskbar.Handle &&
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
        _lastTaskbar = taskbar.Handle;
    }

    private void SaveCurrentPosition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var windowRect))
        {
            return;
        }

        if (_freeMovementEnabled)
        {
            _screenPosition = new TaskbarWidgetPlacementStore.ScreenPosition(
                windowRect.Left,
                windowRect.Top);
            TaskbarWidgetPlacementStore.SaveScreenPosition(
                windowRect.Left,
                windowRect.Top);
            return;
        }

        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;
        var taskbar = TryGetTaskbar(_dragTaskbar, out var draggedTaskbar)
            ? draggedTaskbar
            : new TaskbarInfo(_dragTaskbar, _dragBounds, default);
        var taskbarWidth = _dragBounds.Right - _dragBounds.Left;
        var taskbarHeight = _dragBounds.Bottom - _dragBounds.Top;
        if (taskbarWidth >= taskbarHeight)
        {
            var innerLeft = _dragBounds.Left + 8;
            var innerRight = Math.Max(
                innerLeft,
                _dragBounds.Right - width - 8);
            var range = innerRight - innerLeft;
            _positionRatio = range <= 0
                ? 0
                : Math.Clamp(
                    (windowRect.Left - innerLeft) / (double)range,
                    0,
                    1);
        }
        else
        {
            var innerTop = _dragBounds.Top + 8;
            var innerBottom = Math.Max(
                innerTop,
                _dragBounds.Bottom - height - 8);
            var range = innerBottom - innerTop;
            _positionRatio = range <= 0
                ? 0
                : Math.Clamp(
                    (windowRect.Top - innerTop) / (double)range,
                    0,
                    1);
        }

        if (taskbar.Monitor.Left != 0 ||
            taskbar.Monitor.Top != 0 ||
            taskbar.Monitor.Right != 0 ||
            taskbar.Monitor.Bottom != 0)
        {
            _taskbarMonitorPosition =
                new TaskbarWidgetPlacementStore.MonitorPosition(
                    taskbar.Monitor.Left,
                    taskbar.Monitor.Top);
            TaskbarWidgetPlacementStore.SaveTaskbarMonitorPosition(
                taskbar.Monitor.Left,
                taskbar.Monitor.Top);
        }

        TaskbarWidgetPlacementStore.Save(_positionRatio.Value);
    }

    private nint GetTaskbarForCurrentWidget()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (GetWindowRect(handle, out var windowRect))
        {
            var point = new NativePoint
            {
                X = windowRect.Left +
                    (windowRect.Right - windowRect.Left) / 2,
                Y = windowRect.Top +
                    (windowRect.Bottom - windowRect.Top) / 2
            };
            if (TryGetTaskbarForPoint(point, out var taskbar))
            {
                return taskbar.Handle;
            }
        }

        return FindWindow("Shell_TrayWnd", null);
    }

    private bool TryGetTaskbarForSavedMonitor(out TaskbarInfo taskbar)
    {
        var savedMonitor = _taskbarMonitorPosition;
        if (savedMonitor is { } saved)
        {
            foreach (var candidate in EnumerateTaskbars())
            {
                if (candidate.Monitor.Left == saved.X &&
                    candidate.Monitor.Top == saved.Y)
                {
                    taskbar = candidate;
                    return true;
                }
            }
        }

        var mainTaskbar = FindWindow("Shell_TrayWnd", null);
        if (TryGetTaskbar(mainTaskbar, out taskbar))
        {
            if (savedMonitor is not null &&
                (taskbar.Monitor.Left != savedMonitor.Value.X ||
                 taskbar.Monitor.Top != savedMonitor.Value.Y))
            {
                RememberTaskbarMonitor(taskbar);
            }

            return true;
        }

        foreach (var candidate in EnumerateTaskbars())
        {
            taskbar = candidate;
            return true;
        }

        taskbar = default;
        return false;
    }

    private static bool TryGetTaskbarForPoint(
        NativePoint point,
        out TaskbarInfo taskbar)
    {
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        if (monitor != nint.Zero)
        {
            foreach (var candidate in EnumerateTaskbars())
            {
                if (MonitorFromWindow(
                        candidate.Handle,
                        MonitorDefaultToNearest) == monitor)
                {
                    taskbar = candidate;
                    return true;
                }
            }
        }

        var mainTaskbar = FindWindow("Shell_TrayWnd", null);
        if (TryGetTaskbar(mainTaskbar, out taskbar))
        {
            return true;
        }

        taskbar = default;
        return false;
    }

    private static IEnumerable<TaskbarInfo> EnumerateTaskbars()
    {
        var mainTaskbar = FindWindow("Shell_TrayWnd", null);
        if (TryGetTaskbar(mainTaskbar, out var main))
        {
            yield return main;
        }

        if (!AreSecondaryTaskbarsEnabled())
        {
            yield break;
        }

        var secondaryHandles = new List<nint>();
        EnumWindows(
            (window, _) =>
            {
                var className = new StringBuilder(64);
                if (GetClassName(window, className, className.Capacity) > 0 &&
                    string.Equals(
                        className.ToString(),
                        "Shell_SecondaryTrayWnd",
                        StringComparison.Ordinal))
                {
                    secondaryHandles.Add(window);
                }

                return true;
            },
            nint.Zero);

        foreach (var handle in secondaryHandles)
        {
            if (handle != mainTaskbar && TryGetTaskbar(handle, out var secondary))
            {
                yield return secondary;
            }
        }
    }

    private static bool AreSecondaryTaskbarsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            var value = key?.GetValue("MMTaskbarEnabled");
            if (value is not null)
            {
                return value switch
                {
                    int integer => integer != 0,
                    long longValue => longValue != 0,
                    _ => true
                };
            }
        }
        catch
        {
            // The taskbar window presence below is the fallback source of truth.
        }

        return HasSecondaryTaskbarWindow();
    }

    private static bool HasSecondaryTaskbarWindow()
    {
        var found = false;
        EnumWindows(
            (window, _) =>
            {
                var className = new StringBuilder(64);
                if (GetClassName(window, className, className.Capacity) > 0 &&
                    string.Equals(
                        className.ToString(),
                        "Shell_SecondaryTrayWnd",
                        StringComparison.Ordinal))
                {
                    found = true;
                    return false;
                }

                return true;
            },
            nint.Zero);
        return found;
    }

    private static bool TryGetTaskbar(
        nint handle,
        out TaskbarInfo taskbar)
    {
        if (handle == nint.Zero ||
            !GetWindowRect(handle, out var bounds))
        {
            taskbar = default;
            return false;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == nint.Zero)
        {
            taskbar = default;
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            taskbar = default;
            return false;
        }

        taskbar = new TaskbarInfo(handle, bounds, monitorInfo.Monitor);
        return true;
    }

    private void RememberTaskbarMonitor(TaskbarInfo taskbar)
    {
        _taskbarMonitorPosition =
            new TaskbarWidgetPlacementStore.MonitorPosition(
                taskbar.Monitor.Left,
                taskbar.Monitor.Top);
        TaskbarWidgetPlacementStore.SaveTaskbarMonitorPosition(
            taskbar.Monitor.Left,
            taskbar.Monitor.Top);
    }

    private static bool GetMonitorBoundsForPoint(
        NativePoint point,
        out NativeRect bounds)
    {
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        if (monitor == nint.Zero)
        {
            bounds = default;
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            bounds = default;
            return false;
        }

        bounds = monitorInfo.Monitor;
        return true;
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

    private void SetFreeMovementEnabled(bool enabled)
    {
        if (_freeMovementEnabled == enabled)
        {
            UpdateFreeMovementMenuItem();
            return;
        }

        if (enabled &&
            GetWindowRect(
                new WindowInteropHelper(this).Handle,
                out var currentPosition))
        {
            _screenPosition = new TaskbarWidgetPlacementStore.ScreenPosition(
                currentPosition.Left,
                currentPosition.Top);
            TaskbarWidgetPlacementStore.SaveScreenPosition(
                currentPosition.Left,
                currentPosition.Top);
        }

        _freeMovementEnabled = enabled;
        TaskbarWidgetSettingsStore.SaveFreeMovementEnabled(enabled);
        ApplyWidgetLayout();
        PositionWidget();
        if (enabled)
        {
            PromoteToTopmost();
        }

        UpdateFreeMovementMenuItem();
    }

    private void UpdateFreeMovementMenuItem()
    {
        FreeMovementMenuItem.IsChecked = _freeMovementEnabled;
        FreeMovementCheckGlyph.Text = _freeMovementEnabled
            ? "✓"
            : string.Empty;
        UpdateVerticalLayoutMenuItem();
    }

    private void SetVerticalLayoutEnabled(bool enabled)
    {
        if (_verticalLayoutEnabled == enabled)
        {
            UpdateVerticalLayoutMenuItem();
            return;
        }

        _verticalLayoutEnabled = enabled;
        TaskbarWidgetSettingsStore.SaveVerticalLayoutEnabled(enabled);
        ApplyWidgetLayout();
        PositionWidget();
        UpdateVerticalLayoutMenuItem();
    }

    private void UpdateVerticalLayoutMenuItem()
    {
        VerticalLayoutMenuItem.IsChecked = _verticalLayoutEnabled;
        VerticalLayoutMenuItem.IsEnabled = _freeMovementEnabled;
        VerticalLayoutCheckGlyph.Text = _verticalLayoutEnabled
            ? "✓"
            : string.Empty;
    }

    private void UpdateAppUpdateMenuItem()
    {
        if (_updateStatusText is not null)
        {
            UpdateMenuSeparator.Visibility = Visibility.Visible;
            UpdateMenuItem.Visibility = Visibility.Visible;
            UpdateMenuItem.IsEnabled = false;
            UpdateMenuItem.Header = _updateStatusText;
            return;
        }

        if (_updateService.IsPreparingUpdate)
        {
            UpdateMenuSeparator.Visibility = Visibility.Visible;
            UpdateMenuItem.Visibility = Visibility.Visible;
            UpdateMenuItem.IsEnabled = false;
            UpdateMenuItem.Header = "업데이트 다운로드 중…";
            return;
        }

        if (_updateService.AvailableUpdate is not { } update)
        {
            UpdateMenuSeparator.Visibility = Visibility.Collapsed;
            UpdateMenuItem.Visibility = Visibility.Collapsed;
            UpdateMenuItem.IsEnabled = false;
            return;
        }

        UpdateMenuSeparator.Visibility = Visibility.Visible;
        UpdateMenuItem.Visibility = Visibility.Visible;
        UpdateMenuItem.IsEnabled = true;
        UpdateMenuItem.Header =
            $"QuotaGlass 업데이트 (v{update.DisplayVersion})";
    }

    private void SetUpdateStatus(string? status)
    {
        _updateStatusText = status;
        UpdateStatusTextBlock.Text = status ?? string.Empty;
        UpdateStatusBanner.Visibility = status is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateAppUpdateMenuItem();
    }

    private void SetWidgetTransparency(int transparencyPercent)
    {
        _widgetTransparencyPercent = Math.Clamp(
            transparencyPercent,
            0,
            MaxWidgetTransparencyPercent);
        ApplyWidgetTransparency();
        TaskbarWidgetSettingsStore.SaveTransparencyPercent(
            _widgetTransparencyPercent);
        UpdateWidgetTransparencyMenuItems();
    }

    private void ApplyWidgetTransparency() =>
        Opacity = 1 - _widgetTransparencyPercent / 100d;

    private void SetWidgetBackgroundEnabled(bool enabled)
    {
        _widgetBackgroundEnabled = enabled;
        ApplyWidgetBackground();
        TaskbarWidgetSettingsStore.SaveBackgroundEnabled(
            _widgetBackgroundEnabled);
        UpdateWidgetBackgroundMenuItem();
    }

    private void ApplyWidgetBackground()
    {
        if (_widgetBackgroundEnabled)
        {
            WidgetChrome.SetResourceReference(
                System.Windows.Controls.Border.BackgroundProperty,
                "WidgetBrush");
            WidgetChrome.SetResourceReference(
                System.Windows.Controls.Border.BorderBrushProperty,
                "BorderBrush");
        }
        else
        {
            WidgetChrome.Background =
                System.Windows.Media.Brushes.Transparent;
            WidgetChrome.BorderBrush =
                System.Windows.Media.Brushes.Transparent;
        }
    }

    private void UpdateWidgetBackgroundMenuItem()
    {
        WidgetBackgroundMenuItem.IsChecked = _widgetBackgroundEnabled;
        WidgetBackgroundCheckGlyph.Text = _widgetBackgroundEnabled
            ? "✓"
            : string.Empty;
    }

    private void SetResourceMetricsEnabled(bool enabled)
    {
        if (_resourceMetricsEnabled == enabled)
        {
            UpdateResourceMetricsMenuItem();
            return;
        }

        _resourceMetricsEnabled = enabled;
        TaskbarWidgetSettingsStore.SaveResourceMetricsEnabled(enabled);
        ApplyResourceMetricsVisibility();
        ApplyWidgetLayout();
        PositionWidget();
        UpdateResourceMetricsMenuItem();
    }

    private void ApplyResourceMetricsVisibility() =>
        ResourceMetricsPanel.Visibility = _resourceMetricsEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void UpdateResourceMetricsMenuItem()
    {
        SystemResourceMetricsMenuItem.IsChecked = _resourceMetricsEnabled;
        SystemResourceMetricsCheckGlyph.Text = _resourceMetricsEnabled
            ? "✓"
            : string.Empty;
    }

    private void UpdateAntiAliasingMenuItem()
    {
        AntiAliasingMenuItem.IsChecked = _antiAliasingEnabled;
        AntiAliasingCheckGlyph.Text = _antiAliasingEnabled
            ? "✓"
            : string.Empty;
    }

    private void UpdateWidgetTransparencyMenuItems()
    {
        UpdateWidgetTransparencyMenuItem(
            WidgetTransparency0MenuItem,
            WidgetTransparency0CheckGlyph,
            0);
        UpdateWidgetTransparencyMenuItem(
            WidgetTransparency25MenuItem,
            WidgetTransparency25CheckGlyph,
            25);
        UpdateWidgetTransparencyMenuItem(
            WidgetTransparency50MenuItem,
            WidgetTransparency50CheckGlyph,
            50);
        UpdateWidgetTransparencyMenuItem(
            WidgetTransparency75MenuItem,
            WidgetTransparency75CheckGlyph,
            75);
    }

    private void UpdateWidgetTransparencyMenuItem(
        WpfMenuItem menuItem,
        WpfTextBlock checkGlyph,
        int transparencyPercent)
    {
        var isChecked = _widgetTransparencyPercent == transparencyPercent;
        menuItem.IsChecked = isChecked;
        checkGlyph.Text = isChecked ? "✓" : string.Empty;
    }

    private void ApplyWidgetLayout()
    {
        var useVerticalLayout =
            _freeMovementEnabled &&
            _verticalLayoutEnabled;
        WidgetProvidersControl.ItemsPanel =
            (System.Windows.Controls.ItemsPanelTemplate)FindResource(
                useVerticalLayout
                    ? "VerticalWidgetProvidersPanel"
                    : "HorizontalWidgetProvidersPanel");
        WidgetContentPanel.Orientation = useVerticalLayout
            ? System.Windows.Controls.Orientation.Vertical
            : System.Windows.Controls.Orientation.Horizontal;
        ResourceMetricsPanel.Orientation = useVerticalLayout
            ? System.Windows.Controls.Orientation.Vertical
            : System.Windows.Controls.Orientation.Horizontal;
        ResourceMetricsBorder.Width = useVerticalLayout
            ? VerticalProviderGraphWidth
            : CompactResourceMetricsWidth;
        ResourceMetricsBorder.HorizontalAlignment =
            System.Windows.HorizontalAlignment.Left;
        IsVerticalLayout = useVerticalLayout;
        WidgetChrome.Height = useVerticalLayout
            ? double.NaN
            : DefaultWidgetHeight;
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

        ApplyWidgetLayout();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        StopOutsideClickMonitor();
        _positionTimer.Stop();
        _positionTimer.Tick -= OnPositionTimerTick;
        _resourceTimer.Stop();
        _resourceTimer.Tick -= OnResourceTimerTick;
        _viewModel.Providers.CollectionChanged -= OnProvidersChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _updateService.StateChanged -= OnUpdateStateChanged;
        base.OnClosed(e);
    }

    private sealed record ProviderMenuTag(string ProviderId);

    private readonly record struct TaskbarInfo(
        nint Handle,
        NativeRect Bounds,
        NativeRect Monitor);

    private delegate nint LowLevelMouseProc(
        int code,
        nint message,
        nint data);

    private delegate bool EnumWindowsProc(
        nint window,
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

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(
        NativePoint point,
        uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(
        nint window,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsProc callback,
        nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        nint window,
        StringBuilder className,
        int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        nint monitor,
        ref MonitorInfo info);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
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
