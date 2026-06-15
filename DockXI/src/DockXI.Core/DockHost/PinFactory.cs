using DockXI.Contracts;

namespace DockXI.DockHost;

internal static class PinFactory
{
    public static PinnedItem? FromPath(string rawPath, IShortcutResolver shortcutResolver)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var targetPath = rawPath;
        if (rawPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            targetPath = shortcutResolver.ResolveTargetPath(rawPath) ?? rawPath;
        }

        var kind = DetectKind(targetPath);
        var displayName = BuildDisplayName(targetPath, kind);

        return new PinnedItem
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            TargetPath = targetPath,
            DisplayName = displayName,
            SortOrder = 0,
        };
    }

    internal static PinnedItemKind DetectKind(string path)
    {
        if (Directory.Exists(path))
        {
            return PinnedItemKind.Folder;
        }

        var ext = Path.GetExtension(path);
        if (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return PinnedItemKind.Application;
        }

        return PinnedItemKind.File;
    }

    internal static string BuildDisplayName(string path, PinnedItemKind kind)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        if (kind == PinnedItemKind.Folder)
        {
            var trimmed = Path.TrimEndingDirectorySeparator(path);
            var folderName = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(folderName) ? trimmed : folderName;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
