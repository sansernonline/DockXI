using DockXI.Contracts;
using DockXI.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DockXI.Tests.Unit;

public sealed class DockConfigStoreTests
{
    private static DockConfigStore CreateStore(out Mock<IConfigStore> configStore)
    {
        configStore = new Mock<IConfigStore>();
        return new DockConfigStore(configStore.Object, NullLogger<DockConfigStore>.Instance);
    }

    [Fact]
    public void Current_DefaultsToFreshDockConfig()
    {
        var sut = CreateStore(out _);
        Assert.Equal(DockEdge.Bottom, sut.Current.Position);
        Assert.False(sut.Current.AutoHide);
        Assert.Equal(Defaults.Dock.IconSizeDp, sut.Current.IconSizeDp);
        Assert.Equal(MagnificationLevel.Medium, sut.Current.MagnificationLevel);
    }

    [Fact]
    public void Initialize_ReplacesCurrent_AndDoesNotScheduleSave()
    {
        var sut = CreateStore(out var store);

        sut.Initialize(new DockConfig { Position = DockEdge.Left, AutoHide = true, IconSizeDp = 64 });

        Assert.Equal(DockEdge.Left, sut.Current.Position);
        Assert.True(sut.Current.AutoHide);
        Assert.Equal(64, sut.Current.IconSizeDp);
        store.Verify(s => s.ScheduleSave(), Times.Never);
    }

    [Fact]
    public void UpdatePosition_Different_FiresChanged_AndSchedulesSave()
    {
        var sut = CreateStore(out var store);
        var fired = 0;
        sut.Changed += (_, _) => fired++;

        sut.UpdatePosition(DockEdge.Right);

        Assert.Equal(DockEdge.Right, sut.Current.Position);
        Assert.Equal(1, fired);
        store.Verify(s => s.ScheduleSave(), Times.Once);
    }

    [Fact]
    public void UpdatePosition_Same_IsNoop()
    {
        var sut = CreateStore(out var store);
        sut.UpdatePosition(DockEdge.Right);
        store.Invocations.Clear();
        var fired = 0;
        sut.Changed += (_, _) => fired++;

        sut.UpdatePosition(DockEdge.Right);

        Assert.Equal(0, fired);
        store.Verify(s => s.ScheduleSave(), Times.Never);
    }

    [Fact]
    public void UpdateAutoHide_TogglesAndSchedules()
    {
        var sut = CreateStore(out var store);

        sut.UpdateAutoHide(true);

        Assert.True(sut.Current.AutoHide);
        store.Verify(s => s.ScheduleSave(), Times.Once);
    }

    [Theory]
    [InlineData(-50, 32)]   // below clamp
    [InlineData(20,  32)]   // below clamp
    [InlineData(64,  64)]   // mid-range
    [InlineData(500, 96)]   // above clamp
    public void UpdateIconSize_ClampsTo32To96(int input, int expected)
    {
        var sut = CreateStore(out _);
        sut.UpdateIconSize(input);
        Assert.Equal(expected, sut.Current.IconSizeDp);
    }

    [Fact]
    public void UpdateMagnificationLevel_Persists()
    {
        var sut = CreateStore(out var store);

        sut.UpdateMagnificationLevel(MagnificationLevel.High);

        Assert.Equal(MagnificationLevel.High, sut.Current.MagnificationLevel);
        store.Verify(s => s.ScheduleSave(), Times.Once);
    }
}
