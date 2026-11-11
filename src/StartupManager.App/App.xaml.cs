using System.Windows;
using System.Windows.Threading;
using StartupManager.App.Host;
using StartupManager.App.Interop;

namespace StartupManager.App;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = @"Local\StartupManager.Instance";
    private const string ActivateEventName = @"Local\StartupManager.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private Thread? _activationThread;
    private volatile bool _isShuttingDown;
    private bool _fatalDialogShown;
    private TrayIcon? _tray;
    private MainWindow? _mainWindow;

    public bool IsExiting { get; private set; }

    public bool TrayAvailable => _tray is not null;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppLog.Info($"StartupManager starting (debug={BuildInfo.IsDebug}, elevated={ProcessElevation.IsElevated}).");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        SessionEnding += (_, _) => IsExiting = true;

        try
        {
            if (!AcquireSingleInstance())
            {
                AppLog.Info("Another instance is running; activation requested; exiting.");
                Shutdown();
                return;
            }

            _mainWindow = new MainWindow();
            MainWindow = _mainWindow;

            try
            {
                _tray = new TrayIcon();
                _tray.OpenRequested += ShowMainWindow;
                _tray.RescanRequested += OnTrayRescanRequested;
                _tray.ExitRequested += ExitApplication;
            }
            catch (Exception ex)
            {
                // The window still works without a tray icon; closing then exits
                // instead of hiding with no way to restore.
                _tray = null;
                AppLog.Error("The tray icon is unavailable; continuing without it.", ex);
            }

            StartActivationListener();
            _mainWindow.Show();
        }
        catch (Exception ex)
        {
            AppLog.Error("Fatal startup failure.", ex);
            ShowFatalMessage(ex);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isShuttingDown = true;
        try
        {
            _activateEvent?.Set();
        }
        catch
        {
            // best effort
        }

        _tray?.Dispose();
        _instanceMutex?.Dispose();
        AppLog.Info($"StartupManager exiting with code {e.ApplicationExitCode}.");
        base.OnExit(e);
    }

    public void ShowMinimizedToTrayHint() => _tray?.ShowMinimizedHint();

    public void ExitApplication()
    {
        if (IsExiting)
        {
            return;
        }

        AppLog.Info("Exit requested from the tray.");
        IsExiting = true;
        try
        {
            _mainWindow?.Close();
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not close the main window cleanly.", ex);
        }

        Shutdown();
    }

    private bool AcquireSingleInstance()
    {
        // Create the activation event first: a second instance can signal it even
        // before the first instance starts its listener thread.
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not create the activation event.", ex);
        }

        try
        {
            _instanceMutex = new Mutex(initiallyOwned: false, InstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                try
                {
                    NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                }
                catch
                {
                    // best effort
                }

                _activateEvent?.Set();
                return false;
            }
        }
        catch (Exception ex)
        {
            // Fail open: a broken mutex must not prevent the app from starting.
            AppLog.Error("Single-instance check failed; continuing without it.", ex);
        }

        return true;
    }

    private void StartActivationListener()
    {
        _activationThread = new Thread(ActivationLoop)
        {
            IsBackground = true,
            Name = "StartupManager.ActivationListener"
        };
        _activationThread.Start();
    }

    private void ActivationLoop()
    {
        while (!_isShuttingDown)
        {
            try
            {
                if (_activateEvent is null || !_activateEvent.WaitOne(500))
                {
                    continue;
                }

                Dispatcher.BeginInvoke(new Action(ShowMainWindow));
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Error("Activation listener failed.", ex);
                return;
            }
        }
    }

    private void ShowMainWindow()
    {
        try
        {
            _mainWindow?.BringToForeground();
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not activate the main window.", ex);
        }
    }

    private async void OnTrayRescanRequested()
    {
        try
        {
            ShowMainWindow();
            if (_mainWindow is not null)
            {
                await _mainWindow.RequestRescanAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Tray rescan failed.", ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;
        ShowFatalMessage(e.Exception);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled application-domain exception.", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }

    private void ShowFatalMessage(Exception exception)
    {
        if (_fatalDialogShown)
        {
            return;
        }

        _fatalDialogShown = true;
        try
        {
            System.Windows.MessageBox.Show(
                "程序遇到意外错误，操作可能未完成。\n\n"
                + exception.Message
                + "\n\n详细信息已写入日志：\n"
                + AppLog.DirectoryPath,
                "启动项管理器",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Never let the error dialog cascade.
        }
    }
}
