using System.Text.Json;

namespace DockXI.Contracts;

public static class Defaults
{
    private static readonly Lazy<DefaultsDocument> s_doc = new(Load);

    public static DockSection Dock => s_doc.Value.Dock;
    public static HoverZoomSection HoverZoom => s_doc.Value.HoverZoom;

    private static DefaultsDocument Load()
    {
        try
        {
            foreach (var path in CandidatePaths())
            {
                if (File.Exists(path))
                {
                    using var s = File.OpenRead(path);
                    var doc = JsonSerializer.Deserialize<DefaultsDocument>(s, SerializerOptions);
                    if (doc is not null) { return doc; }
                }
            }
        }
        catch
        {
        }
        return new DefaultsDocument();
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "appsettings.defaults.json");
#if DEBUG
        var src = WalkUpForSourceRoot();
        if (src is not null)
        {
            yield return Path.Combine(src, "appsettings.defaults.json");
        }
#endif
    }

    private static string? WalkUpForSourceRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string callerPath = "")
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(callerPath) ?? string.Empty);
            while (dir is not null && !dir.GetFiles("*.sln").Any())
            {
                dir = dir.Parent;
            }
            return dir?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public sealed class DefaultsDocument
    {
        public DockSection Dock { get; set; } = new();
        public HoverZoomSection HoverZoom { get; set; } = new();
    }

    public sealed class DockSection
    {
        // Window thickness when docked at Top/Bottom (height) vs Left/Right (width).
        public int ThicknessTopBottomDp { get; set; } = 74;
        public int ThicknessLeftRightDp { get; set; } = 88;
        public int IconSizeDp { get; set; } = 36;
        public int IconSpacingDp { get; set; } = 6;
        public int EdgeMarginDp { get; set; } = 24;
        public int TooltipGapTopBottomDp { get; set; } = 10;
        public int TooltipGapLeftRightDp { get; set; } = 10;
    }

    public sealed class HoverZoomSection
    {
        public float MagnificationLow { get; set; } = 1.2f;
        public float MagnificationMedium { get; set; } = 1.4f;
        public float MagnificationHigh { get; set; } = 1.7f;
        public float RadiusPx { get; set; } = 180f;
    }
}
