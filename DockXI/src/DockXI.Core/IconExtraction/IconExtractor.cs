using System.Collections.Concurrent;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using DockXI.Contracts;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;
using static DockXI.IconExtraction.ShellImageFactoryInterop;

namespace DockXI.IconExtraction;

internal sealed class IconExtractor : IIconExtractor
{
    private readonly IShortcutResolver _shortcutResolver;
    private readonly ILogger<IconExtractor> _logger;
    private readonly ConcurrentDictionary<CacheKey, SoftwareBitmap> _cache = new();

    public IconExtractor(IShortcutResolver shortcutResolver, ILogger<IconExtractor> logger)
    {
        _shortcutResolver = shortcutResolver;
        _logger = logger;
    }

    public async Task<SoftwareBitmap?> GetIconAsync(
        string targetPath,
        int dpiBucket,
        int requestedPixelSize,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        var resolved = ResolveShortcutIfNeeded(targetPath);
        var key = BuildCacheKey(resolved, dpiBucket, requestedPixelSize);

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var bitmap = await Task.Run(() => ExtractInner(resolved, requestedPixelSize), ct).ConfigureAwait(false);
        if (bitmap is not null)
        {
            _cache[key] = bitmap;
        }
        return bitmap;
    }

    public async Task<SoftwareBitmap?> GetFaviconAsync(Uri uri, int requestedPixelSize, CancellationToken ct = default)
    {
        if (uri is null || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var cacheKey = new CacheKey($"favicon:{uri.Host.ToLowerInvariant()}", 100, requestedPixelSize, MtimeTicks: 0);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            using var httpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            httpCts.CancelAfter(TimeSpan.FromSeconds(5));

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DockXI/1.0");
            var faviconUri = new Uri($"{uri.Scheme}://{uri.Host}/favicon.ico");
            _logger.LogInformation("Fetching favicon: {Url}", faviconUri);
            var bytes = await http.GetByteArrayAsync(faviconUri, httpCts.Token).ConfigureAwait(false);
            _logger.LogInformation("Favicon bytes for {Host}: {Count}", uri.Host, bytes.Length);
            var bitmap = await DecodeBitmapAsync(bytes).ConfigureAwait(false);
            _logger.LogInformation("Favicon decoded for {Host}: {Ok}", uri.Host, bitmap is not null);
            if (bitmap is not null)
            {
                _cache[cacheKey] = bitmap;
            }
            return bitmap;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogInformation("Favicon fetch failed for {Host}: {Message}", uri.Host, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected favicon error for {Host}.", uri.Host);
            return null;
        }
    }

    private static async Task<SoftwareBitmap?> DecodeBitmapAsync(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }
        try
        {
            using var ms = new MemoryStream(bytes);
            var ras = ms.AsRandomAccessStream();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
            return await decoder.GetSoftwareBitmapAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        }
        catch
        {
            return null;
        }
    }

    public async Task<SoftwareBitmap?> GetGenericUrlIconAsync(int requestedPixelSize, CancellationToken ct = default)
    {
        var key = new CacheKey("generic:url", 100, requestedPixelSize, MtimeTicks: 0);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var bitmap = await Task.Run(ExtractGenericUrlIcon, ct).ConfigureAwait(false);
        if (bitmap is not null)
        {
            _cache[key] = bitmap;
        }
        return bitmap;
    }

    private SoftwareBitmap? ExtractGenericUrlIcon()
    {
        // SHGetFileInfo with SHGFI_USEFILEATTRIBUTES + a synthetic ".url" path
        // returns the system's default URL shortcut icon — i.e. whatever the
        // user's default browser supplies for web shortcuts. The file does not
        // need to exist.
        var info = default(SHFILEINFOW);
        const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        var ok = SHGetFileInfoW("placeholder.url", FILE_ATTRIBUTE_NORMAL, ref info,
            (uint)Marshal.SizeOf<SHFILEINFOW>(),
            SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);

        if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            _logger.LogInformation("Generic URL icon: SHGetFileInfo returned no icon.");
            return null;
        }

        try
        {
            return IconBitmapConverter.FromHIcon(info.hIcon);
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    public void InvalidateCache(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        var resolved = ResolveShortcutIfNeeded(targetPath);
        var prefix = NormalizePath(resolved);
        foreach (var key in _cache.Keys.Where(k => k.NormalizedPath == prefix).ToArray())
        {
            _cache.TryRemove(key, out _);
        }
    }

    private string ResolveShortcutIfNeeded(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        return _shortcutResolver.ResolveTargetPath(path) ?? path;
    }

    private SoftwareBitmap? ExtractInner(string targetPath, int pixelSize)
    {
        try
        {
            var primary = TryShellItemImageFactory(targetPath, pixelSize);
            if (primary is not null)
            {
                return primary;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IShellItemImageFactory failed for {Path}.", targetPath);
        }

        try
        {
            return TrySHGetFileInfo(targetPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SHGetFileInfo fallback failed for {Path}.", targetPath);
            return null;
        }
    }

    private static SoftwareBitmap? TryShellItemImageFactory(string path, int pixelSize)
    {
        SHCreateItemFromParsingName(path, IntPtr.Zero, in IID_IShellItemImageFactory, out var raw);
        if (raw is not IShellItemImageFactory factory)
        {
            return null;
        }

        var size = new SIZE { cx = pixelSize, cy = pixelSize };
        var hr = factory.GetImage(size, SIIGBF.ResizeToFit | SIIGBF.BiggerSizeOk, out var hBitmap);
        if (hr != 0 || hBitmap == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return IconBitmapConverter.FromHBitmap(hBitmap, pixelSize, pixelSize);
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    private static SoftwareBitmap? TrySHGetFileInfo(string path)
    {
        var info = default(SHFILEINFOW);
        var attrs = Directory.Exists(path) ? 0x10u : 0x80u;
        var ok = SHGetFileInfoW(path, attrs, ref info,
            (uint)Marshal.SizeOf<SHFILEINFOW>(),
            SHGFI_ICON | SHGFI_LARGEICON | (File.Exists(path) ? 0u : SHGFI_USEFILEATTRIBUTES));

        if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return IconBitmapConverter.FromHIcon(info.hIcon);
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static CacheKey BuildCacheKey(string path, int dpiBucket, int pixelSize)
    {
        var normalized = NormalizePath(path);
        var mtimeTicks = SafeMtimeTicks(normalized);
        return new CacheKey(normalized, dpiBucket, pixelSize, mtimeTicks);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).ToLowerInvariant();
        }
        catch
        {
            return path.ToLowerInvariant();
        }
    }

    private static long SafeMtimeTicks(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return File.GetLastWriteTimeUtc(path).Ticks;
            }
            if (Directory.Exists(path))
            {
                return Directory.GetLastWriteTimeUtc(path).Ticks;
            }
        }
        catch
        {
            // Unreadable paths share a single bucket (0) — they'll all miss the cache equally.
        }
        return 0;
    }

    internal readonly record struct CacheKey(string NormalizedPath, int DpiBucket, int PixelSize, long MtimeTicks);

    internal int CacheCount => _cache.Count;
}
