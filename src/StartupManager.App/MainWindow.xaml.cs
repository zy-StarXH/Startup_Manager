using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using StartupManager.App.Host;
using StartupManager.App.Interop;
using StartupManager.Core.Models;

namespace StartupManager.App;

public partial class MainWindow : Window
{
    // The interim web shell (web/prototype-shell.html) carries its own preview
    // titlebar. The production web UI must not, but while the shell is in use the
    // host removes only that legacy bar (detected by the shell's document title),
    // never page chrome from the final UI.
    private const string HideHtmlTitleBarScript = """
        (() => {
          const css = 'html body .app > header.titlebar{display:none !important}';
          const isLegacyShell = () => document.title.indexOf('UI 原型') >= 0;
          const apply = () => {
            if (!isLegacyShell() || !document.head) return;
            let style = document.getElementById('sm-host-chrome');
            if (!style) {
              style = document.createElement('style');
              style.id = 'sm-host-chrome';
              document.head.appendChild(style);
            }
            if (style.textContent !== css) style.textContent = css;
          };
          apply();
          document.addEventListener('DOMContentLoaded', apply);
          if (document.documentElement) {
            new MutationObserver(apply).observe(document.documentElement, { childList: true, subtree: true });
          }
        })();
        """;

    private const string EnsureHideHtmlTitleBarScript = """
        (() => {
          const css = 'html body .app > header.titlebar{display:none !important}';
          const isLegacyShell = () => document.title.indexOf('UI 原型') >= 0;
          const apply = () => {
            if (!isLegacyShell() || !document.head) return;
            let style = document.getElementById('sm-host-chrome');
            if (!style) {
              style = document.createElement('style');
              style.id = 'sm-host-chrome';
              document.head.appendChild(style);
            }
            if (style.textContent !== css) style.textContent = css;
          };
          apply();
          if (!window.__smHostChromeTimer) {
            window.__smHostChromeTimer = window.setInterval(apply, 250);
          }
        })();
        """;

    private readonly HostBackend _backend = new();
    private BridgeHost? _bridge;
    private bool _webViewInitialized;
    private bool _webViewInitializing;
    private bool _coreEventsHooked;
    private bool _loadedOnce;

    public MainWindow()
    {
        InitializeComponent();
        ApplySavedPlacement();
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
    }

    internal BridgeHost? Bridge => _bridge;

    internal bool MinimizeToTray => _bridge?.CurrentSettings.MinimizeToTray ?? true;

    public void BringToForeground()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(handle);
        }
    }

    internal Task RequestRescanAsync() => _bridge?.RefreshAsync() ?? Task.CompletedTask;

    private void ApplySavedPlacement()
    {
        var placement = WindowPlacementStore.Load();
        if (placement is null)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
        {
            return;
        }

        _loadedOnce = true;
        await InitializeWebViewAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(handle);
        source?.AddHook(WndProc);
        ApplyWindowCorner();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeGlyph();
        ApplyWindowCorner();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var app = System.Windows.Application.Current as App;
        if (app is null)
        {
            _bridge?.Dispose();
            return;
        }

        if (app.IsExiting)
        {
            SavePlacement();
            _bridge?.Dispose();
            return;
        }

        if (MinimizeToTray && app.TrayAvailable)
        {
            e.Cancel = true;
            SavePlacement();
            Hide();
            app.ShowMinimizedToTrayHint();
            return;
        }

        SavePlacement();
        _bridge?.Dispose();
        Dispatcher.BeginInvoke(new Action(app.Shutdown));
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateMaximizeGlyph()
    {
        if (WindowState == WindowState.Maximized)
        {
            MaximizeGlyph.Data = Geometry.Parse("M3.5,1.5 H8.5 V6.5 M1.5,3.5 H6.5 V8.5 H1.5 Z");
            MaximizeButton.ToolTip = "向下还原";
            AutomationProperties.SetName(MaximizeButton, "向下还原");
        }
        else
        {
            MaximizeGlyph.Data = Geometry.Parse("M1,1 H9 V9 H1 Z");
            MaximizeButton.ToolTip = "最大化";
            AutomationProperties.SetName(MaximizeButton, "最大化");
        }
    }

    private void ApplyWindowCorner()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var preference = WindowState == WindowState.Maximized
                ? NativeMethods.DWMWCP_DONOTROUND
                : NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(
                handle,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref preference,
                sizeof(int));
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not apply the DWM corner preference.", ex);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_GETMINMAXINFO)
        {
            ApplyTaskbarSafeMaximizeBounds(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ApplyTaskbarSafeMaximizeBounds(IntPtr hwnd, IntPtr lParam)
    {
        try
        {
            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return;
            }

            var info = new NativeMethods.MONITORINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
            };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return;
            }

            var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
            mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
            mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
            mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
            mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;

            var dpi = NativeMethods.GetDpiForWindow(hwnd);
            if (dpi == 0)
            {
                dpi = 96;
            }

            mmi.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi / 96.0);
            mmi.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi / 96.0);
            Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not adjust maximize bounds.", ex);
        }
    }

    private void SavePlacement()
    {
        try
        {
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth, ActualHeight)
                : RestoreBounds;
            if (bounds.Width < 120 || bounds.Height < 120)
            {
                return;
            }

            WindowPlacementStore.Save(new WindowPlacement
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = WindowState == WindowState.Maximized
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not capture window placement.", ex);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        if (_webViewInitializing)
        {
            return;
        }

        _webViewInitializing = true;
        ShowLoading();
        try
        {
            var paths = AppPaths.CreateDefault();
            paths.EnsureCreated();
            var userDataFolder = Path.Combine(paths.DataDirectory, "WebView2");
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xF3, 0xF3, 0xF3);
            await WebView.EnsureCoreWebView2Async(environment);

            var core = WebView.CoreWebView2
                       ?? throw new InvalidOperationException("WebView2 核心初始化失败。");
            core.Settings.AreDevToolsEnabled = BuildInfo.IsDebug;
            core.Settings.AreDefaultContextMenusEnabled = BuildInfo.IsDebug;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = BuildInfo.IsDebug;

            if (!_coreEventsHooked)
            {
                core.NavigationCompleted += OnNavigationCompleted;
                core.ProcessFailed += OnProcessFailed;
                _coreEventsHooked = true;
            }

            await core.AddScriptToExecuteOnDocumentCreatedAsync(HideHtmlTitleBarScript);

            var webRoot = Path.Combine(AppContext.BaseDirectory, "web");
            if (!File.Exists(Path.Combine(webRoot, "index.html")))
            {
                ShowError(
                    "界面资源缺失",
                    "找不到 web\\index.html，发布目录必须包含 web 文件夹。\n\n" + webRoot,
                    showInstall: false);
                return;
            }

            core.SetVirtualHostNameToFolderMapping(
                "app.local",
                webRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);

            _bridge ??= new BridgeHost(WebView, _backend);
            _bridge.Attach();

            WebView.Source = new Uri("https://app.local/index.html");
            _webViewInitialized = true;
            AppLog.Info("WebView2 navigated to https://app.local/index.html.");
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            AppLog.Error("The WebView2 runtime is missing.", ex);
            ShowError(
                "未安装 WebView2 运行时",
                "启动项管理器使用 Microsoft Edge WebView2 显示界面，但当前系统未安装该运行时。\n\n请点击“安装 WebView2 运行时”，安装完成后点击“重试”。",
                showInstall: true);
        }
        catch (Exception ex) when (IsWebView2Missing(ex))
        {
            AppLog.Error("The WebView2 runtime could not be loaded.", ex);
            ShowError(
                "未安装 WebView2 运行时",
                "启动项管理器使用 Microsoft Edge WebView2 显示界面，但当前系统未安装该运行时。\n\n请点击“安装 WebView2 运行时”，安装完成后点击“重试”。",
                showInstall: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("WebView2 initialization failed.", ex);
            ShowError(
                "界面初始化失败",
                "无法初始化内嵌浏览器。\n\n" + ex.Message,
                showInstall: false);
        }
        finally
        {
            _webViewInitializing = false;
        }
    }

    private static bool IsWebView2Missing(Exception exception)
    {
        Exception? current = exception;
        while (current is not null)
        {
            if (current is WebView2RuntimeNotFoundException)
            {
                return true;
            }

            if (current.Message.Contains("WebView2 Runtime", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("WebView2 运行时", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("WebView2 is not installed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ErrorOverlay.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Visible;
            AppLog.Info("WebView2 navigation completed successfully.");
            if (WebView.CoreWebView2 is not null)
            {
                try
                {
                    await WebView.CoreWebView2.ExecuteScriptAsync(EnsureHideHtmlTitleBarScript);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Could not hide the legacy HTML titlebar.", ex);
                }
            }

            return;
        }

        AppLog.Error($"WebView2 navigation failed: {e.WebErrorStatus}.");
        ShowError(
            "界面加载失败",
            $"无法加载本地界面（{e.WebErrorStatus}）。请确认发布目录中的 web 资源是否完整。",
            showInstall: false);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        AppLog.Error($"WebView2 process failed: {e.ProcessFailedKind}.");
        Dispatcher.BeginInvoke(new Action(() => ShowError(
            "界面进程异常",
            $"WebView2 进程已退出（{e.ProcessFailedKind}）。点击“重试”重新加载界面。",
            showInstall: false)));
    }

    private void ShowLoading()
    {
        // WebView2 is an HWND child and always paints above WPF content, so it must
        // be collapsed while a WPF overlay is visible.
        WebView.Visibility = Visibility.Collapsed;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Visible;
    }

    private void ShowError(string title, string message, bool showInstall)
    {
        WebView.Visibility = Visibility.Collapsed;
        ErrorTitle.Text = title;
        ErrorMessage.Text = message;
        InstallWebViewButton.Visibility = showInstall ? Visibility.Visible : Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Collapsed;
        ErrorOverlay.Visibility = Visibility.Visible;
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_webViewInitialized && WebView.CoreWebView2 is not null)
        {
            ShowLoading();
            WebView.Reload();
            return;
        }

        ErrorOverlay.Visibility = Visibility.Collapsed;
        await InitializeWebViewAsync();
    }

    private void InstallWebViewButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShellLauncher.OpenUrl("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not open the WebView2 download page.", ex);
            System.Windows.MessageBox.Show(
                this,
                "无法打开下载页面，请在浏览器中访问 Microsoft Edge WebView2 下载页。",
                "启动项管理器",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShellLauncher.OpenDirectory(AppLog.DirectoryPath);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not open the log directory.", ex);
        }
    }
}
