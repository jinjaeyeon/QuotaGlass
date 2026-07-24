using System.Windows;
using System.Windows.Threading;
using QuotaGlass.Services;

namespace QuotaGlass;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName =
        @"Local\QuotaGlass.SingleInstance.v1";

    private MainWindow? _mainWindow;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private int _exitStarted;

    public bool EnforceSingleInstance { get; set; } = true;
    public bool ForceProcessExitOnShutdown { get; set; } = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.StartHidden();
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
        base.OnExit(e);
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
