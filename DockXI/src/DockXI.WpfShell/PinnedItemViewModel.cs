using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using DockXI.Contracts;
using WinImaging = Windows.Graphics.Imaging;

namespace DockXI.WpfShell;

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
            return bmp;
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
            return src;
        }
        catch { return null; }
    }

    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
