using DockXI.Contracts;
using DockXI.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DockXI.Tests.Unit;

public sealed class AppSettingsStoreTests
{
    private static AppSettingsStore CreateStore(out Mock<IConfigStore> configStore)
    {
        configStore = new Mock<IConfigStore>();
        return new AppSettingsStore(configStore.Object, NullLogger<AppSettingsStore>.Instance);
    }

    [Fact]
    public void Current_DefaultsToFreshAppSettings()
    {
        var sut = CreateStore(out _);
        Assert.False(sut.Current.HasCompletedFirstRun);
        Assert.False(sut.Current.AutoStartEnabled);
        Assert.Equal(ThemeOverride.System, sut.Current.ThemeOverride);
    }

    [Fact]
    public void Initialize_ReplacesCurrent_AndDoesNotScheduleSave()
    {
        var sut = CreateStore(out var store);

        sut.Initialize(new AppSettings { HasCompletedFirstRun = true, AutoStartEnabled = true });

        Assert.True(sut.Current.HasCompletedFirstRun);
        Assert.True(sut.Current.AutoStartEnabled);
        store.Verify(s => s.ScheduleSave(), Times.Never);
    }

    [Fact]
    public void MarkFirstRunComplete_SetsFlag_FiresChanged_AndSchedulesSave()
    {
        var sut = CreateStore(out var store);
        var fired = 0;
        sut.Changed += (_, _) => fired++;

        sut.MarkFirstRunComplete();

        Assert.True(sut.Current.HasCompletedFirstRun);
        Assert.Equal(1, fired);
        store.Verify(s => s.ScheduleSave(), Times.Once);
    }

    [Fact]
    public void MarkFirstRunComplete_IsIdempotent_NoExtraSaveOrEvent()
    {
        var sut = CreateStore(out var store);
        sut.Initialize(new AppSettings { HasCompletedFirstRun = true });
        var fired = 0;
        sut.Changed += (_, _) => fired++;

        sut.MarkFirstRunComplete();
        sut.MarkFirstRunComplete();

        Assert.Equal(0, fired);
        store.Verify(s => s.ScheduleSave(), Times.Never);
    }
}
