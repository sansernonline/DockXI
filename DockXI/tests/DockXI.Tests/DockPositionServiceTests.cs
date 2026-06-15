using DockXI.Contracts;
using DockXI.DockHost;
using Windows.Graphics;
using Xunit;

namespace DockXI.Tests.Unit;

public sealed class DockPositionServiceTests
{
    // 1920×1080 monitor, workArea clips the taskbar (40px) from the bottom.
    private static readonly RectInt32 WorkArea   = new(0, 0, 1920, 1040);
    private static readonly RectInt32 OuterBounds = new(0, 0, 1920, 1080);

    private readonly DockPositionService _sut = new();

    // ----- ComputeDockRect -----------------------------------------------

    // iconCount=3, iconSizeDp=48, dpi=96 (scale=1.0)
    // tileSizeDp = 48 + 4*2 = 56
    // Bottom/Top:  padding=24 → contentLen = max(200, 3*56+2*6+2*24) = 228; thickness=74
    // Left/Right:  padding=20 → contentLen = max(200, 3*56+2*6+2*20) = 220; thickness=88
    // margin = EdgeMarginDp = 24 px

    [Fact]
    public void ComputeDockRect_Bottom_CenteredAboveBottomMargin()
    {
        var rect = _sut.ComputeDockRect(DockEdge.Bottom, iconCount: 3, iconSizeDp: 48, dpi: 96, WorkArea);

        Assert.Equal((1920 - 228) / 2, rect.X);
        Assert.Equal(1040 - 74 - 24,   rect.Y);
        Assert.Equal(228,               rect.Width);
        Assert.Equal(74,                rect.Height);
    }

    [Fact]
    public void ComputeDockRect_Top_CenteredBelowTopMargin()
    {
        var rect = _sut.ComputeDockRect(DockEdge.Top, iconCount: 3, iconSizeDp: 48, dpi: 96, WorkArea);

        Assert.Equal((1920 - 228) / 2, rect.X);
        Assert.Equal(24,               rect.Y);
        Assert.Equal(228,              rect.Width);
        Assert.Equal(74,               rect.Height);
    }

    [Fact]
    public void ComputeDockRect_Left_CenteredRightOfLeftMargin()
    {
        var rect = _sut.ComputeDockRect(DockEdge.Left, iconCount: 3, iconSizeDp: 48, dpi: 96, WorkArea);

        Assert.Equal(24,               rect.X);
        Assert.Equal((1040 - 220) / 2, rect.Y);
        Assert.Equal(88,               rect.Width);
        Assert.Equal(220,              rect.Height);
    }

    [Fact]
    public void ComputeDockRect_Right_CenteredLeftOfRightMargin()
    {
        var rect = _sut.ComputeDockRect(DockEdge.Right, iconCount: 3, iconSizeDp: 48, dpi: 96, WorkArea);

        Assert.Equal(1920 - 88 - 24,   rect.X);
        Assert.Equal((1040 - 220) / 2, rect.Y);
        Assert.Equal(88,               rect.Width);
        Assert.Equal(220,              rect.Height);
    }

    [Fact]
    public void ComputeDockRect_ZeroIcons_UsesMinDimension()
    {
        // With 0 items: contentLengthDp = max(200, 0+0+40) = 200 px
        var rect = _sut.ComputeDockRect(DockEdge.Bottom, iconCount: 0, iconSizeDp: 48, dpi: 96, WorkArea);

        Assert.Equal(200, rect.Width);
    }

    [Fact]
    public void ComputeDockRect_HighDpi_ScalesCorrectly()
    {
        // dpi=192 → scale=2.0; ThicknessTopBottomDp=74 → px=148, margin=48
        // iconCount=0 → contentLengthDp=200 → contentLengthPx=400
        var rect = _sut.ComputeDockRect(DockEdge.Bottom, iconCount: 0, iconSizeDp: 48, dpi: 192, WorkArea);

        Assert.Equal(148, rect.Height);  // ThicknessTopBottomDp(74) * scale(2)
        Assert.Equal(400, rect.Width);   // contentLengthPx
        Assert.Equal(1040 - 148 - 48, rect.Y);
    }

    [Fact]
    public void ComputeDockRect_InvalidEdge_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _sut.ComputeDockRect((DockEdge)99, 1, 48, 96, WorkArea));
    }

    // ----- ComputeRevealZoneRect -----------------------------------------

    // taskbarThicknessPx=40; reveal zone is always 1px wide/tall.

    [Fact]
    public void ComputeRevealZoneRect_Bottom_OnePxAboveTaskbar()
    {
        var r = _sut.ComputeRevealZoneRect(DockEdge.Bottom, dpi: 96, OuterBounds, taskbarThicknessPx: 40);

        Assert.Equal(0,          r.X);
        Assert.Equal(1920,       r.Width);
        Assert.Equal(1,          r.Height);
        // bottom of monitor minus taskbar minus 1 px reveal strip
        Assert.Equal(1080 - 40 - 1, r.Y);
    }

    [Fact]
    public void ComputeRevealZoneRect_Top_OnePxBelowTaskbar()
    {
        var r = _sut.ComputeRevealZoneRect(DockEdge.Top, dpi: 96, OuterBounds, taskbarThicknessPx: 40);

        Assert.Equal(0,    r.X);
        Assert.Equal(40,   r.Y);
        Assert.Equal(1920, r.Width);
        Assert.Equal(1,    r.Height);
    }

    [Fact]
    public void ComputeRevealZoneRect_Left_OnePxRightOfTaskbar()
    {
        var r = _sut.ComputeRevealZoneRect(DockEdge.Left, dpi: 96, OuterBounds, taskbarThicknessPx: 40);

        Assert.Equal(40,   r.X);
        Assert.Equal(0,    r.Y);
        Assert.Equal(1,    r.Width);
        Assert.Equal(1080, r.Height);
    }

    [Fact]
    public void ComputeRevealZoneRect_Right_OnePxLeftOfTaskbar()
    {
        var r = _sut.ComputeRevealZoneRect(DockEdge.Right, dpi: 96, OuterBounds, taskbarThicknessPx: 40);

        Assert.Equal(1920 - 40 - 1, r.X);
        Assert.Equal(0,             r.Y);
        Assert.Equal(1,             r.Width);
        Assert.Equal(1080,          r.Height);
    }

    [Fact]
    public void ComputeRevealZoneRect_InvalidEdge_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _sut.ComputeRevealZoneRect((DockEdge)99, 96, OuterBounds, 40));
    }

    // ----- ComputeReservedWorkArea ----------------------------------------

    [Fact]
    public void ComputeReservedWorkArea_AutoHide_ReturnsFullWorkArea()
    {
        var area = _sut.ComputeReservedWorkArea(DockEdge.Bottom, dockThicknessPx: 80, WorkArea, autoHide: true);

        Assert.Equal(WorkArea.X,                       area.Left);
        Assert.Equal(WorkArea.Y,                       area.Top);
        Assert.Equal(WorkArea.X + WorkArea.Width,      area.Right);
        Assert.Equal(WorkArea.Y + WorkArea.Height,     area.Bottom);
    }

    [Fact]
    public void ComputeReservedWorkArea_Bottom_ShrinksBottom()
    {
        var area = _sut.ComputeReservedWorkArea(DockEdge.Bottom, dockThicknessPx: 80, WorkArea, autoHide: false);

        Assert.Equal(0,          area.Left);
        Assert.Equal(0,          area.Top);
        Assert.Equal(1920,       area.Right);
        Assert.Equal(1040 - 80,  area.Bottom);
    }

    [Fact]
    public void ComputeReservedWorkArea_Top_ShrinksTop()
    {
        var area = _sut.ComputeReservedWorkArea(DockEdge.Top, dockThicknessPx: 80, WorkArea, autoHide: false);

        Assert.Equal(80,   area.Top);
        Assert.Equal(1040, area.Bottom);
    }

    [Fact]
    public void ComputeReservedWorkArea_Left_ShrinksLeft()
    {
        var area = _sut.ComputeReservedWorkArea(DockEdge.Left, dockThicknessPx: 80, WorkArea, autoHide: false);

        Assert.Equal(80,   area.Left);
        Assert.Equal(1920, area.Right);
    }

    [Fact]
    public void ComputeReservedWorkArea_Right_ShrinksRight()
    {
        var area = _sut.ComputeReservedWorkArea(DockEdge.Right, dockThicknessPx: 80, WorkArea, autoHide: false);

        Assert.Equal(0,          area.Left);
        Assert.Equal(1920 - 80,  area.Right);
    }

    [Fact]
    public void ComputeReservedWorkArea_InvalidEdge_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _sut.ComputeReservedWorkArea((DockEdge)99, 80, WorkArea, autoHide: false));
    }
}
