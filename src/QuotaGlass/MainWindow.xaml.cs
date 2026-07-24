using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QuotaGlass.ViewModels;
using Forms = System.Windows.Forms;

namespace QuotaGlass;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TaskbarWidgetWindow _taskbarWidget;
    private readonly DispatcherTimer _dismissTimer;
    private bool _allowClose;
    private bool _isPositioning;
    private DateTimeOffset _dismissAllowedAt;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        _taskbarWidget = new TaskbarWidgetWindow(
            _viewModel,
            ShowFullWindow,
            ExitApplication);
        _dismissTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _dismissTimer.Tick += OnDismissTimerTick;

        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
        SizeChanged += OnSizeChanged;
        Closing += OnClosing;
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionAtBottomRight();
        _taskbarWidget.Show();
        _dismissTimer.Start();
        await _viewModel.RefreshAsync();
    }

    private void PositionAtBottomRight()
    {
        if (_isPositioning || !IsLoaded)
        {
            return;
        }

        _isPositioning = true;
        try
        {
            var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
            var transform = PresentationSource.FromVisual(this)?
                .CompositionTarget?
                .TransformFromDevice ?? Matrix.Identity;
            var bottomRight = transform.Transform(
                new System.Windows.Point(
                    screen.WorkingArea.Right,
                    screen.WorkingArea.Bottom));

            Left = bottomRight.X - ActualWidth - 16;
            Top = bottomRight.Y - ActualHeight - 16;
        }
        finally
        {
            _isPositioning = false;
        }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) =>
        HideToTray();

    private void HideToTray()
    {
        Hide();
        _taskbarWidget.RestoreAboveTaskbar();
    }

    private void ShowFullWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowFullWindow);
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        _dismissAllowedAt = DateTimeOffset.UtcNow.AddMilliseconds(700);
        UpdateLayout();
        PositionAtBottomRight();
        Activate();
        SetForegroundWindow(new WindowInteropHelper(this).Handle);

        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            () =>
            {
                if (!IsVisible)
                {
                    return;
                }

                if (!IsActive)
                {
                    Activate();
                    SetForegroundWindow(
                        new WindowInteropHelper(this).Handle);
                }

                _taskbarWidget.RestoreAboveTaskbar();
            });
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            HideIfFocusLeftQuotaGlass);
    }

    private void OnDismissTimerTick(object? sender, EventArgs e) =>
        HideIfFocusLeftQuotaGlass();

    private void HideIfFocusLeftQuotaGlass()
    {
        if (!IsVisible ||
            DateTimeOffset.UtcNow < _dismissAllowedAt)
        {
            return;
        }

        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            return;
        }

        _ = GetWindowThreadProcessId(foreground, out var processId);
        if (processId == Environment.ProcessId)
        {
            return;
        }

        HideToTray();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        PositionAtBottomRight();

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(PositionAtBottomRight);
    }

    private void ExitApplication()
    {
        if (System.Windows.Application.Current is App application)
        {
            application.RequestExit();
        }
    }

    internal void CloseForExit()
    {
        if (_allowClose)
        {
            return;
        }

        _allowClose = true;
        _taskbarWidget.CloseForExit();
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Deactivated -= OnDeactivated;
        SizeChanged -= OnSizeChanged;
        _dismissTimer.Stop();
        _dismissTimer.Tick -= OnDismissTimerTick;
        _viewModel.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out int processId);
}
