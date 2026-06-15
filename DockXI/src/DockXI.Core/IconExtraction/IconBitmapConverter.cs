using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace DockXI.IconExtraction;

/// <summary>
/// Converts native HBITMAP / HICON handles into a managed SoftwareBitmap.
/// </summary>
internal static class IconBitmapConverter
{
    private const int DibRgbColors = 0;

    public static SoftwareBitmap? FromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero)
        {
            return null;
        }

        if (!GetIconInfo(hIcon, out var iconInfo))
        {
            return null;
        }

        try
        {
            // Modern shell icons always carry a 32bpp color bitmap. Monochrome
            // icons (hbmColor == null, hbmMask packs AND+XOR rows) are vanishingly
            // rare on Win 10 22H2+ and not worth supporting on the fallback path.
            if (iconInfo.hbmColor == IntPtr.Zero)
            {
                return null;
            }

            if (!TryGetBitmapSize(iconInfo.hbmColor, out var width, out var height))
            {
                return null;
            }

            return FromHBitmap(iconInfo.hbmColor, width, height);
        }
        finally
        {
            if (iconInfo.hbmColor != IntPtr.Zero)
            {
                ShellImageFactoryInterop.DeleteObject(iconInfo.hbmColor);
            }
            if (iconInfo.hbmMask != IntPtr.Zero)
            {
                ShellImageFactoryInterop.DeleteObject(iconInfo.hbmMask);
            }
        }
    }

    private static bool TryGetBitmapSize(IntPtr hbm, out int width, out int height)
    {
        var bmp = default(BITMAP);
        var size = Marshal.SizeOf<BITMAP>();
        var written = GetObject(hbm, size, ref bmp);
        if (written == 0)
        {
            width = 0;
            height = 0;
            return false;
        }
        width = bmp.bmWidth;
        height = bmp.bmHeight;
        return width > 0 && height > 0;
    }

    public static SoftwareBitmap? FromHBitmap(IntPtr hBitmap, int width, int height)
    {
        if (hBitmap == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return null;
        }

        var stride = width * 4;
        var bytes = new byte[stride * height];

        var bmi = new BITMAPINFO
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        var hdc = GetDC(IntPtr.Zero);
        try
        {
            var copied = GetDIBits(hdc, hBitmap, 0, (uint)height, bytes, ref bmi, DibRgbColors);
            if (copied == 0)
            {
                return null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(bytes.AsBuffer());
        return bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr h, int c, ref BITMAP pv);
}
