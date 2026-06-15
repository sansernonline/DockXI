using DockXI.Contracts;
using Windows.Graphics;

namespace DockXI.DockHost;

internal sealed class DockPositionServiceStub : IDockPositionService
{
    public RectInt32 ComputeDockRect(
        DockEdge edge,
        int iconCount,
        int iconSizeDp,
        int dpi,
        RectInt32 monitorWorkArea)
    {
        throw new NotImplementedException("DockPositionServiceStub — implemented in M5.");
    }

    public RectInt32 ComputeRevealZoneRect(
        DockEdge edge,
        int dpi,
        RectInt32 monitorOuterBounds,
        int taskbarThicknessPx)
    {
        throw new NotImplementedException("DockPositionServiceStub — implemented in M5.");
    }

    public RECT ComputeReservedWorkArea(
        DockEdge edge,
        int dockThicknessPx,
        RectInt32 monitorWorkArea,
        bool autoHide)
    {
        throw new NotImplementedException("DockPositionServiceStub — implemented in M5.");
    }
}
