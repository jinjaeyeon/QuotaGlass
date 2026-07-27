using System.Windows;
using System.Windows.Threading;
using QuotaGlass.Services;
using QuotaGlass.ViewModels;

namespace QuotaGlass;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName =
        @"Local\QuotaGlass.SingleInstance.v1";

    private MainWindow? _mainWindow;
    private TaskbarWidgetWindow? _taskbarWidget;
    private MainViewModel? _viewModel;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private int _exitStarted;

    public bool EnforceSingleInstance { get; set; } = true;
    public bool ForceProcessExitOnShutdown { get; set; } = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        System.Windows.Media.RenderOptions.ProcessRenderMode =
            System.Windows.Interop.RenderMode.SoftwareOnly;

        if (EnforceSingleInstance && !TryAcquireSingleInstance())
        {
            Shutdown(0);
            return;
        }

        try
        {
            ClaudeStatusLineInstaller.EnsureInstalled();
        }
        catch
        {
            // Claude integration failure must not prevent the overlay from opening.
        }

        if (e.Args.Contains(
                "--install-claude-bridge",
                StringComparer.Ordinal))
        {
            Shutdown();
            return;
        }

        _viewModel = new MainViewModel();
        _taskbarWidget = new TaskbarWidgetWindow(
            _viewModel,
            ShowFullWindow,
            RequestExit);
        MainWindow = _taskbarWidget;
        _taskbarWidget.Show();
        _ = _viewModel.RefreshAsync();
    }

    public void RequestExit()
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            () =>
            {
                if (ForceProcessExitOnShutdown)
                {
                    StartForcedExitFallback();
                }

                try
                {
                    _mainWindow?.CloseForExit();
                    _mainWindow = null;
                    _taskbarWidget?.CloseForExit();
                    _taskbarWidget = null;
                    _viewModel?.Dispose();
                    _viewModel = null;
                }
                finally
                {
                    Shutdown(0);
                }
            });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _viewModel?.Dispose();
        _viewModel = null;
        base.OnExit(e);
    }

    private void ShowFullWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowFullWindow);
            return;
        }

        if (_mainWindow is null)
        {
            if (_viewModel is null || _taskbarWidget is null)
            {
                return;
            }

            _mainWindow = new MainWindow(
                _viewModel,
                DismissFullWindow,
                _taskbarWidget.RestoreAboveTaskbar);
            MainWindow = _mainWindow;
        }

        _mainWindow.ShowFullWindow();
    }

    private void DismissFullWindow()
    {
        var window = _mainWindow;
        if (window is null)
        {
            return;
        }

        _mainWindow = null;
        window.CloseForDismiss();
        _taskbarWidget?.RestoreAboveTaskbar();
        if (_taskbarWidget is not null)
        {
            MainWindow = _taskbarWidget;
        }
    }

    private bool TryAcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(
            true,
            SingleInstanceMutexName,
            out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        return createdNew;
    }

    private static void StartForcedExitFallback()
    {
        _ = Task.Run(
            async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                Environment.Exit(0);
            });
    }
}
