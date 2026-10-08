using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using DockXI.Contracts;
using WinImaging = Windows.Graphics.Imaging;

namespace DockXI.UI;

public sealed class PinnedItemViewModel : INotifyPropertyChanged
{
    private BitmapSource? _iconSource;
    private bool _isRunning;
    private bool _isBroken;

    public PinnedItemViewModel(PinnedItem model)
    {
        Model = model;
    }

    public PinnedItem   Model       { get; }
    public Guid         Id          => Model.Id;
    public string       DisplayName => Model.DisplayName;
    public string       TargetPath  => Model.TargetPath;
    public bool         IsSeparator => Model.Kind == PinnedItemKind.Separator;
    public Visibility   IconVisibility      => IsSeparator ? Visibility.Collapsed : Visibility.Visible;
    public Visibility   SeparatorVisibility => IsSeparator ? Visibility.Visible : Visibility.Collapsed;

    public BitmapSource? IconSource
    {
        get => _iconSource;
        private set { _iconSource = value; Notify(nameof(IconSource)); }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value) { return; }
            _isRunning = value;
            Notify(nameof(IsRunning));
        }
    }

    public bool IsBroken
    {
        get => _isBroken;
        set
        {
            if (_isBroken == value) { return; }
            _isBroken = value;
            Notify(nameof(IsBroken));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadIconAsync(IIconExtractor extractor, int dpi, CancellationToken ct = default)
    {
        if (IsSeparator) { return; }   // separators render their own visual; no icon to load

        WinImaging.SoftwareBitmap? sb = null;
        try
        {
            sb = Model.Kind == PinnedItemKind.Url
                ? await extractor.GetFaviconAsync(new Uri(Model.TargetPath), 32, ct)
                : await extractor.GetIconAsync(Model.TargetPath, dpi, 32, ct);
        }
        catch { /* fall through to fallback */ }

        BitmapSource? result = sb is not null
            ? await SoftwareBitmapToWpfAsync(sb)
            : FallbackShellIcon(Model.TargetPath);

        var broken = Model.Kind != PinnedItemKind.Url && !PathExists(Model.TargetPath);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IconSource = result;
            IsBroken   = broken;
        });
    }

    private static bool PathExists(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path); }
        catch { return false; }
    }

    private static async Task<BitmapSource?> SoftwareBitmapToWpfAsync(WinImaging.SoftwareBitmap sb)
    {
        try
        {
            using var bgra = WinImaging.SoftwareBitmap.Convert(
                sb, WinImaging.BitmapPixelFormat.Bgra8, WinImaging.BitmapAlphaMode.Premultiplied);

            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var enc = await WinImaging.BitmapEncoder.CreateAsync(WinImaging.BitmapEncoder.PngEncoderId, ras);
            enc.SetSoftwareBitmap(bgra);
            await enc.FlushAsync();

            // Read PNG bytes via DataReader (avoids AsStreamForRead extension dependency).
            ras.Seek(0);
            var reader = new Windows.Storage.Streams.DataReader(ras);
            await reader.LoadAsync((uint)ras.Size);
            var pngBytes = new byte[ras.Size];
            reader.ReadBytes(pngBytes);

            using var ms = new MemoryStream(pngBytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption  = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return CenterContent(bmp);
        }
        catch { return null; }
    }

    private static BitmapSource? FallbackShellIcon(string path)
    {
        try
        {
            if (!File.Exists(path)) { return null; }
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) { return null; }
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return CenterContent(src);
        }
        catch { return null; }
    }

    // Re-render the icon onto a transparent canvas with the OPAQUE bounding
    // box positioned at the geometric centre. Windows shell icons (the
    // generic file/document fallback in particular) carry asymmetric
    // transparent padding inside their source bitmap, so a regular
    // HorizontalAlignment=Center on the WPF Image element still leaves the
    // visible glyph drifting to one side. This snapshots the visible content
    // and re-centres it in a fresh BGRA buffer of the same dimensions, so
    // every icon in the dock occupies its tile slot symmetrically.
    private static BitmapSource CenterContent(BitmapSource src)
    {
        try
        {
            if (src.Format != System.Windows.Media.PixelFormats.Bgra32 &&
                src.Format != System.Windows.Media.PixelFormats.Pbgra32)
            {
                var conv = new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                conv.Freeze();
                src = conv;
            }

            int w = src.PixelWidth;
            int h = src.PixelHeight;
            if (w <= 0 || h <= 0) { return src; }

            int stride = w * 4;
            var pixels = new byte[h * stride];
            src.CopyPixels(pixels, stride, 0);

            // Find bounding box of pixels with alpha > 8 (ignore near-transparent
            // edge noise). For BGRA byte order, alpha is at offset 3.
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int rowBase = y * stride;
                for (int x = 0; x < w; x++)
                {
                    if (pixels[rowBase + x * 4 + 3] > 8)
                    {
                        if (x < minX) { minX = x; }
                        if (x > maxX) { maxX = x; }
                        if (y < minY) { minY = y; }
                        if (y > maxY) { maxY = y; }
                    }
                }
            }
            if (maxX < 0) { return src; }   // fully transparent — nothing to centre

            int contentW = maxX - minX + 1;
            int contentH = maxY - minY + 1;

            // Where the content currently sits vs. where it SHOULD sit to be
            // geometrically centred. If both deltas are zero the source is
            // already symmetric — skip the copy.
            int targetX = (w - contentW) / 2;
            int targetY = (h - contentH) / 2;
            int shiftX  = targetX - minX;
            int shiftY  = targetY - minY;
            if (shiftX == 0 && shiftY == 0) { return src; }

            var dst = new byte[h * stride];
            for (int y = 0; y < contentH; y++)
            {
                int srcRow = (minY + y) * stride + minX * 4;
                int dstRow = (targetY + y) * stride + targetX * 4;
                Buffer.BlockCopy(pixels, srcRow, dst, dstRow, contentW * 4);
            }

            var centred = BitmapSource.Create(
                w, h, src.DpiX, src.DpiY,
                System.Windows.Media.PixelFormats.Bgra32, null, dst, stride);
            centred.Freeze();
            return centred;
        }
        catch { return src; }   // fall back to original on any failure
    }

    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
