using Windows.Graphics.Imaging;

namespace DockXI.Contracts;

public interface IIconExtractor
{
    Task<SoftwareBitmap?> GetIconAsync(
        string targetPath,
        int dpiBucket,
        int requestedPixelSize,
        CancellationToken ct = default);

    Task<SoftwareBitmap?> GetFaviconAsync(
        Uri uri,
        int requestedPixelSize,
        CancellationToken ct = default);

    /// <summary>
    /// Generic browser/link icon used when a favicon fetch fails or while it
    /// is still in flight. Backed by the system's default ".url" shell icon
    /// so it follows whichever browser the user has set as default.
    /// </summary>
    Task<SoftwareBitmap?> GetGenericUrlIconAsync(
        int requestedPixelSize,
        CancellationToken ct = default);

    void InvalidateCache(string targetPath);
}
