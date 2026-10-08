using System;
using System.Windows;
using DockXI.Contracts;
using Windows.Graphics;

namespace DockXI.UI;

internal sealed class WpfRevealZoneHost : IRevealZoneHost, IDisposable
{
    private RevealZoneWindow? _window;
    private bool _disposed;

    public event EventHandler? PointerEntered;

    public void Show(RectInt32 physicalRect)
    {
        if (_disposed) { return; }
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_window is { IsVisible: true }) { return; }
            _window ??= new RevealZoneWindow(physicalRect);
            _window.PointerEntered += (_, _) => PointerEntered?.Invoke(this, EventArgs.Empty);
            _window.Show();
        });
    }

    public void Hide()
    {
        if (_disposed) { return; }
        Application.Current.Dispatcher.BeginInvoke(() => _window?.Hide());
    }

    public void Dispose()
    {
        _disposed = true;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _window?.Close();
            _window = null;
        });
    }
}

// 1-px sentinel window at the screen edge used for auto-hide reveal.
internal sealed class RevealZoneWindow : Window
{
    public event EventHandler? PointerEntered;

    private DateTime _lastDragPing = DateTime.MinValue;

    public RevealZoneWindow(RectInt32 physicalRect)
    {
        WindowStyle        = WindowStyle.None;
        ResizeMode         = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background         = System.Windows.Media.Brushes.Transparent;
        Topmost            = true;
        ShowInTaskbar      = false;
        // Accept OLE drags so dragging a file to the screen edge also
        // reveals the auto-hidden dock. Mouse events don't fire during a
        // drag, so MouseEnter alone can never trigger reveal-for-drop.
        AllowDrop          = true;

        var dpiScale = GetSystemDpiScale();
        Left   = physicalRect.X      / dpiScale;
        Top    = physicalRect.Y      / dpiScale;
        Width  = physicalRect.Width  / dpiScale;
        Height = Math.Max(1, physicalRect.Height / dpiScale);

        MouseEnter += (_, _) => PointerEntered?.Invoke(this, EventArgs.Empty);
        // Drag hover raises the SAME PointerEntered signal — the main
        // window's handler decides what a reveal means; no interface
        // change needed. DragOver re-pings (throttled) because the first
        // DragEnter can land inside the main window's reveal cooldown and
        // be ignored; hovering a moment longer must still open the dock.
        DragEnter += (_, _) => PointerEntered?.Invoke(this, EventArgs.Empty);
        DragOver  += (_, _) =>
        {
            if ((DateTime.Now - _lastDragPing).TotalMilliseconds < 200) { return; }
            _lastDragPing = DateTime.Now;
            PointerEntered?.Invoke(this, EventArgs.Empty);
        };
    }

    private static double GetSystemDpiScale()
    {
        using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96.0;
    }
}
