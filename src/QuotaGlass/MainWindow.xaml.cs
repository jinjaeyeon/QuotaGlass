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
    private readonly Action _dismissWindow;
    private readonly Action _restoreWidget;
    private readonly DispatcherTimer _dismissTimer;
    private bool _allowClose;
    private bool _isPositioning;
    private DateTimeOffset _dismissAllowedAt;

    public MainWindow(
        MainViewModel viewModel,
        Action dismissWindow,
        Action restoreWidget)
    {
        InitializeComponent();

        _dismissWindow = dismissWindow;
        _restoreWidget = restoreWidget;
        DataContext = viewModel;

        _dismissTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _dismissTimer.Tick += OnDismissTimerTick;

        Deactivated += OnDeactivated;
        SizeChanged += OnSizeChanged;
        Closing += OnClosing;
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
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
        _dismissWindow();

    private void CollapseProviderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: ProviderUsageViewModel provider
            } && DataContext is MainViewModel viewModel)
        {
            viewModel.CollapseProvider(provider.Provider);
        }
    }

    private void ExpandProviderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: ProviderUsageViewModel provider
            } && DataContext is MainViewModel viewModel)
        {
            viewModel.ExpandProvider(provider.Provider);
        }
    }

    internal void ShowFullWindow()
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

        _dismissTimer.Start();
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

                _restoreWidget();
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

        _dismissWindow();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        PositionAtBottomRight();

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(PositionAtBottomRight);
    }

    internal void CloseForExit()
    {
        if (_allowClose)
        {
            return;
        }

        _allowClose = true;
        Close();
    }

    internal void CloseForDismiss()
    {
        if (_allowClose)
        {
            return;
        }

        _allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Dispatcher.BeginInvoke(_dismissWindow);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Deactivated -= OnDeactivated;
        SizeChanged -= OnSizeChanged;
        _dismissTimer.Stop();
        _dismissTimer.Tick -= OnDismissTimerTick;
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
