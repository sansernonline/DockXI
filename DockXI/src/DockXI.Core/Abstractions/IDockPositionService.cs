using Windows.Graphics;

namespace DockXI.Contracts;

public interface IDockPositionService
{
    RectInt32 ComputeDockRect(
        DockEdge edge,
        int iconCount,
        int iconSizeDp,
        int dpi,
        RectInt32 monitorWorkArea);

    RectInt32 ComputeRevealZoneRect(
        DockEdge edge,
        int dpi,
        RectInt32 monitorOuterBounds,
        int taskbarThicknessPx);

    RECT ComputeReservedWorkArea(
        DockEdge edge,
        int dockThicknessPx,
        RectInt32 monitorWorkArea,
        bool autoHide);
}

public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}
