using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;

namespace DockXI.WpfShell.Services;

/// <summary>
/// Owns the system-tray icon. Lets the user toggle dock visibility, jump
/// back to it after a "hide" without restarting the process, and quit
/// cleanly from outside the dock.
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly MainDockWindow _dock;
    private TaskbarIcon?           _trayIcon;

    public TrayIconManager(MainDockWindow dock)
    {
        _dock = dock;
        _trayIcon = new TaskbarIcon
        {
            IconSource     = LoadIcon(),
            ToolTipText    = "DockXI",
            ContextMenu    = BuildMenu(),
            NoLeftClickDelay = true,
        };
        _trayIcon.TrayMouseDoubleClick += (_, _) => ToggleDock();
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    // -- internals ---------------------------------------------------------

    private static BitmapImage? LoadIcon()
    {
        try
        {
            return new BitmapImage(new Uri(
                "pack://application:,,,/DockXI.WpfShell;component/Assets/icon.ico",
                UriKind.Absolute));
        }
        catch
        {
            return null;     // fall back to default Windows tray icon
        }
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var show = new MenuItem { Header = "Show dock" };
        show.Click += (_, _) => _dock.Show();

        var hide = new MenuItem { Header = "Hide dock" };
        hide.Click += (_, _) => _dock.Hide();

        var quit = new MenuItem { Header = "Quit DockXI" };
        quit.Click += (_, _) => Application.Current.Shutdown();

        menu.Items.Add(show);
        menu.Items.Add(hide);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        return menu;
    }

    private void ToggleDock()
    {
        if (_dock.IsVisible) { _dock.Hide(); }
        else                 { _dock.Show(); _dock.Activate(); }
    }
}
