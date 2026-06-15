using DockXI.Contracts;
using Windows.Graphics;

namespace DockXI.DockHost;

internal sealed class DockPositionService : IDockPositionService
{
    // Values pulled from appsettings.defaults.json (with hardcoded fallbacks
    // if the file is missing or malformed — see Defaults.cs).
    private static int IconSpacingDp => Defaults.Dock.IconSpacingDp;
    // Padding per side: DockRoot Grid padding (8) + DockPlate H-padding.
    // Top/Bottom uses plate H-pad 18 → 26 dp from window edge to first/last icon.
    // Left/Right uses plate H-pad 12 → 20 dp.
    private const int PaddingTopBottomDp = 24;
    private const int PaddingLeftRightDp = 20;
    private static int PaddingForEdge(DockEdge edge) => edge switch
    {
        DockEdge.Top or DockEdge.Bottom => PaddingTopBottomDp,
        _ => PaddingLeftRightDp,
    };
    // Extra width per tile from Button padding (4 dp on each side)
    private const int ButtonPaddingDp = 4;
    private static int EdgeMarginDp => Defaults.Dock.EdgeMarginDp;
    private const int MinDimensionDp = 200;
    // Thickness is orientation-aware: Top/Bottom (icons horizontal) vs Left/Right (icons vertical).
    private static int ThicknessForEdge(DockEdge edge) => edge switch
    {
        DockEdge.Top or DockEdge.Bottom => Defaults.Dock.ThicknessTopBottomDp,
        DockEdge.Left or DockEdge.Right => Defaults.Dock.ThicknessLeftRightDp,
        _ => Defaults.Dock.ThicknessTopBottomDp,
    };
    private const int RevealZoneThicknessPx = 1;

    public RectInt32 ComputeDockRect(
        DockEdge edge,
        int iconCount,
        int iconSizeDp,
        int dpi,
        RectInt32 monitorWorkArea)
    {
        var scale = dpi / 96.0;
        var thickness = (int)(ThicknessForEdge(edge) * scale);
        var marginPx = (int)(EdgeMarginDp * scale);
        // Tile width on screen = icon size + button padding on both sides.
        // Spacing between tiles (not before first or after last) = (N-1) gaps.
        // Total outer padding = DockRoot + DockPlate padding, both sides.
        var tileSizeDp = iconSizeDp + ButtonPaddingDp * 2;
        var paddingDp = PaddingForEdge(edge);
        var contentLengthDp = Math.Max(
            MinDimensionDp,
            iconCount * tileSizeDp + Math.Max(0, iconCount - 1) * IconSpacingDp + paddingDp * 2);
        var contentLengthPx = (int)(contentLengthDp * scale);

        return edge switch
        {
            DockEdge.Bottom => new RectInt32(
                monitorWorkArea.X + (monitorWorkArea.Width - contentLengthPx) / 2,
                monitorWorkArea.Y + monitorWorkArea.Height - thickness - marginPx,
                contentLengthPx,
                thickness),
            DockEdge.Top => new RectInt32(
                monitorWorkArea.X + (monitorWorkArea.Width - contentLengthPx) / 2,
                monitorWorkArea.Y + marginPx,
                contentLengthPx,
                thickness),
            DockEdge.Left => new RectInt32(
                monitorWorkArea.X + marginPx,
                monitorWorkArea.Y + (monitorWorkArea.Height - contentLengthPx) / 2,
                thickness,
                contentLengthPx),
            DockEdge.Right => new RectInt32(
                monitorWorkArea.X + monitorWorkArea.Width - thickness - marginPx,
                monitorWorkArea.Y + (monitorWorkArea.Height - contentLengthPx) / 2,
                thickness,
                contentLengthPx),
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, null),
        };
    }

    public RectInt32 ComputeRevealZoneRect(
        DockEdge edge,
        int dpi,
        RectInt32 monitorOuterBounds,
        int taskbarThicknessPx)
    {
        var offset = taskbarThicknessPx;
        return edge switch
        {
            DockEdge.Bottom => new RectInt32(
                monitorOuterBounds.X,
                monitorOuterBounds.Y + monitorOuterBounds.Height - offset - RevealZoneThicknessPx,
                monitorOuterBounds.Width,
                RevealZoneThicknessPx),
            DockEdge.Top => new RectInt32(
                monitorOuterBounds.X,
                monitorOuterBounds.Y + offset,
                monitorOuterBounds.Width,
                RevealZoneThicknessPx),
            DockEdge.Left => new RectInt32(
                monitorOuterBounds.X + offset,
                monitorOuterBounds.Y,
                RevealZoneThicknessPx,
                monitorOuterBounds.Height),
            DockEdge.Right => new RectInt32(
                monitorOuterBounds.X + monitorOuterBounds.Width - offset - RevealZoneThicknessPx,
                monitorOuterBounds.Y,
                RevealZoneThicknessPx,
                monitorOuterBounds.Height),
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, null),
        };
    }

    public RECT ComputeReservedWorkArea(
        DockEdge edge,
        int dockThicknessPx,
        RectInt32 monitorWorkArea,
        bool autoHide)
    {
        if (autoHide)
        {
            return new RECT
            {
                Left = monitorWorkArea.X,
                Top = monitorWorkArea.Y,
                Right = monitorWorkArea.X + monitorWorkArea.Width,
                Bottom = monitorWorkArea.Y + monitorWorkArea.Height,
            };
        }

        return edge switch
        {
            DockEdge.Bottom => new RECT
            {
                Left = monitorWorkArea.X,
                Top = monitorWorkArea.Y,
                Right = monitorWorkArea.X + monitorWorkArea.Width,
                Bottom = monitorWorkArea.Y + monitorWorkArea.Height - dockThicknessPx,
            },
            DockEdge.Top => new RECT
            {
                Left = monitorWorkArea.X,
                Top = monitorWorkArea.Y + dockThicknessPx,
                Right = monitorWorkArea.X + monitorWorkArea.Width,
                Bottom = monitorWorkArea.Y + monitorWorkArea.Height,
            },
            DockEdge.Left => new RECT
            {
                Left = monitorWorkArea.X + dockThicknessPx,
                Top = monitorWorkArea.Y,
                Right = monitorWorkArea.X + monitorWorkArea.Width,
                Bottom = monitorWorkArea.Y + monitorWorkArea.Height,
            },
            DockEdge.Right => new RECT
            {
                Left = monitorWorkArea.X,
                Top = monitorWorkArea.Y,
                Right = monitorWorkArea.X + monitorWorkArea.Width - dockThicknessPx,
                Bottom = monitorWorkArea.Y + monitorWorkArea.Height,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, null),
        };
    }
}
