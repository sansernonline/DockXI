using System.Runtime.InteropServices;
using DockXI.Contracts;

namespace DockXI.DockHost;

internal static class NativeMethods
{
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;

    internal const uint SPI_SETWORKAREA = 0x002F;
    internal const uint SPIF_SENDCHANGE = 0x0002;

    internal const int DWMWA_CLOAK = 13;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    internal static void ApplyExtendedStyles(IntPtr hWnd)
    {
        var current = GetWindowLong(hWnd, GWL_EXSTYLE);
        var updated = current | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(updated));
        }
        else
        {
            SetWindowLong(hWnd, GWL_EXSTYLE, updated);
        }
    }
}
