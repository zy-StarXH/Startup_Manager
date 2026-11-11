using System.Drawing;
using Forms = System.Windows.Forms;
using DrawingIcon = System.Drawing.Icon;

namespace StartupManager.App.Host;

/// <summary>WinForms NotifyIcon hosted by the WPF message loop.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private bool _disposed;
    private bool _hintShown;

    public TrayIcon()
    {
        _menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false
        };

        var openItem = new Forms.ToolStripMenuItem("打开启动项管理器");
        openItem.Click += (_, _) => OpenRequested?.Invoke();

        var rescanItem = new Forms.ToolStripMenuItem("重新扫描");
        rescanItem.Click += (_, _) => RescanRequested?.Invoke();

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _menu.Items.Add(openItem);
        _menu.Items.Add(rescanItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "启动项管理器",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public event Action? OpenRequested;

    public event Action? RescanRequested;

    public event Action? ExitRequested;

    public void ShowMinimizedHint()
    {
        if (_hintShown || _disposed)
        {
            return;
        }

        _hintShown = true;
        try
        {
            _notifyIcon.ShowBalloonTip(
                3500,
                "启动项管理器",
                "已最小化到系统托盘。双击托盘图标可重新打开。",
                Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not show tray balloon.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        catch
        {
            // best effort
        }

        _menu.Dispose();
    }

    private static DrawingIcon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("Assets/StartupManager.ico", UriKind.Relative));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                using var icon = new DrawingIcon(stream, Forms.SystemInformation.SmallIconSize);
                return (DrawingIcon)icon.Clone();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not load the tray icon resource, using the executable icon.", ex);
        }

        var executable = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(executable))
        {
            var extracted = DrawingIcon.ExtractAssociatedIcon(executable);
            if (extracted is not null)
            {
                return extracted;
            }
        }

        return SystemIcons.Application;
    }
}
