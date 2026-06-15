using System;
using System.Windows;
using DockXI.Contracts;
using Windows.Graphics;

namespace DockXI.WpfShell;

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

    public RevealZoneWindow(RectInt32 physicalRect)
    {
        WindowStyle        = WindowStyle.None;
        ResizeMode         = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background         = System.Windows.Media.Brushes.Transparent;
        Topmost            = true;
        ShowInTaskbar      = false;

        var dpiScale = GetSystemDpiScale();
        Left   = physicalRect.X      / dpiScale;
        Top    = physicalRect.Y      / dpiScale;
        Width  = physicalRect.Width  / dpiScale;
        Height = Math.Max(1, physicalRect.Height / dpiScale);

        MouseEnter += (_, _) => PointerEntered?.Invoke(this, EventArgs.Empty);
    }

    private static double GetSystemDpiScale()
    {
        using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96.0;
    }
}
