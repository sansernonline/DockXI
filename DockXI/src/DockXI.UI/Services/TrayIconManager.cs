using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DockXI.Contracts;
using H.NotifyIcon;

namespace DockXI.UI.Services;

/// <summary>
/// Owns the system-tray icon. Lets the user toggle dock visibility, jump
/// back to it after a "hide" without restarting the process, toggle
/// start-with-Windows, and quit cleanly from outside the dock.
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly MainDockWindow    _dock;
    private readonly IAutoStartService _autoStart;
    private TaskbarIcon?               _trayIcon;

    public TrayIconManager(MainDockWindow dock, IAutoStartService autoStart)
    {
        _dock      = dock;
        _autoStart = autoStart;
        _trayIcon = new TaskbarIcon
        {
            IconSource     = LoadIcon(),
            ToolTipText    = "DockXI",
            ContextMenu    = BuildMenu(),
            NoLeftClickDelay = true,
        };
        _trayIcon.TrayMouseDoubleClick += (_, _) => ToggleDock();

        // H.NotifyIcon v2 BREAKING CHANGE vs the old Hardcodet library:
        // a TaskbarIcon constructed in CODE never registers the Win32
        // notify icon by itself — without this call the app runs with no
        // tray icon at all (not even in the taskbar overflow flyout).
        // XAML-declared icons get created by their own lifecycle; code-
        // created ones must call ForceCreate. enablesEfficiencyMode=false
        // so the library can't auto-hide the icon while the dock window
        // is hidden — the tray icon is exactly how users get the dock
        // back, so it must always be visible.
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
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
            // Pack URI without assembly prefix → resolves to current assembly,
            // so it survives future AssemblyName renames.
            return new BitmapImage(new Uri(
                "pack://application:,,,/Assets/icon.ico",
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

        // Start-with-Windows toggle. The check state is NOT cached: the HKCU
        // Run key is re-read every time the menu opens (see menu.Opened
        // below), so the tick always reflects reality even when the state
        // was changed elsewhere — from the dock's own context menu, or by
        // the user deleting the Run entry in Task Manager / regedit.
        var autoStartItem = new MenuItem
        {
            Header      = "Start with Windows",
            IsCheckable = true,
            IsChecked   = _autoStart.IsEnabled,
        };
        autoStartItem.Click += (_, _) =>
        {
            if (_autoStart.IsEnabled) { _autoStart.Disable(); }
            else                      { _autoStart.Enable(); }
            autoStartItem.IsChecked = _autoStart.IsEnabled;
        };

        var quit = new MenuItem { Header = "Quit DockXI" };
        quit.Click += (_, _) => Application.Current.Shutdown();

        menu.Items.Add(show);
        menu.Items.Add(hide);
        menu.Items.Add(new Separator());
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);

        // Refresh live state on every open (right-click).
        menu.Opened += (_, _) => autoStartItem.IsChecked = _autoStart.IsEnabled;
        return menu;
    }

    private void ToggleDock()
    {
        if (_dock.IsVisible) { _dock.Hide(); }
        else                 { _dock.Show(); _dock.Activate(); }
    }
}
