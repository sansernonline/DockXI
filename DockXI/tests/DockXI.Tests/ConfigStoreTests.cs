using DockXI.Contracts;
using DockXI.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DockXI.Tests.Unit;

public sealed class ConfigStoreTests
{
    [Fact]
    public async Task LoadAsync_NoFile_ReturnsDefault()
    {
        using var locations = new TestStorageLocations();
        var sut = new ConfigStore(locations, NullLogger<ConfigStore>.Instance);

        var loaded = await sut.LoadAsync();

        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Empty(loaded.PinnedItems);
        Assert.False(loaded.AppSettings.HasCompletedFirstRun);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Roundtrips()
    {
        using var locations = new TestStorageLocations();
        var sut = new ConfigStore(locations, NullLogger<ConfigStore>.Instance);

        var original = new DockConfigDocument
        {
            AppSettings = new AppSettings { HasCompletedFirstRun = true },
            DockConfig = new DockConfig { IconSizeDp = 64, MagnificationLevel = MagnificationLevel.High },
            PinnedItems =
            [
                new PinnedItem { Kind = PinnedItemKind.Application, TargetPath = @"C:\Windows\notepad.exe", DisplayName = "Notepad", SortOrder = 0 },
                new PinnedItem { Kind = PinnedItemKind.Folder,      TargetPath = @"C:\Users",              DisplayName = "Users",   SortOrder = 1 },
            ],
        };

        await sut.SaveAsync(original);
        var roundtripped = await sut.LoadAsync();

        Assert.Equal(2, roundtripped.PinnedItems.Count);
        Assert.Equal("Notepad",          roundtripped.PinnedItems[0].DisplayName);
        Assert.Equal(PinnedItemKind.Folder, roundtripped.PinnedItems[1].Kind);
        Assert.Equal(64,                 roundtripped.DockConfig.IconSizeDp);
        Assert.Equal(MagnificationLevel.High, roundtripped.DockConfig.MagnificationLevel);
        Assert.True(roundtripped.AppSettings.HasCompletedFirstRun);
    }

    [Fact]
    public async Task SaveAsync_SecondWrite_CreatesBackup()
    {
        using var locations = new TestStorageLocations();
        var sut = new ConfigStore(locations, NullLogger<ConfigStore>.Instance);

        await sut.SaveAsync(DockConfigDocument.Default);
        Assert.False(File.Exists(locations.ConfigBackupPath));

        await sut.SaveAsync(DockConfigDocument.Default with { SchemaVersion = 2 });
        Assert.True(File.Exists(locations.ConfigBackupPath));
    }

    [Fact]
    public async Task LoadAsync_CorruptJson_ArchivesAndReturnsDefault()
    {
        using var locations = new TestStorageLocations();
        await File.WriteAllTextAsync(locations.ConfigFilePath, "{ this is not valid json");

        var sut = new ConfigStore(locations, NullLogger<ConfigStore>.Instance);

        var loaded = await sut.LoadAsync();

        Assert.Equal(DockConfigDocument.Default.SchemaVersion, loaded.SchemaVersion);
        Assert.False(File.Exists(locations.ConfigFilePath));
        var corruptFiles = Directory.GetFiles(locations.LocalStateFolder, "config.json.corrupt-*");
        Assert.Single(corruptFiles);
    }

    [Fact]
    public async Task SaveAsync_PersistsEnumsAsStrings()
    {
        using var locations = new TestStorageLocations();
        var sut = new ConfigStore(locations, NullLogger<ConfigStore>.Instance);

        var doc = DockConfigDocument.Default with
        {
            DockConfig = new DockConfig { Position = DockEdge.Right, MagnificationLevel = MagnificationLevel.Low },
            PinnedItems = [new PinnedItem { Kind = PinnedItemKind.Url, TargetPath = "https://example.com", DisplayName = "Example" }],
        };
        await sut.SaveAsync(doc);

        var json = await File.ReadAllTextAsync(locations.ConfigFilePath);
        Assert.Contains("\"position\": \"Right\"", json);
        Assert.Contains("\"magnificationLevel\": \"Low\"", json);
        Assert.Contains("\"kind\": \"Url\"", json);
    }

    [Fact]
    public async Task ScheduleSave_BeforeSnapshotSourceSet_LogsAndSkips()
    {
        using var locations = new TestStorageLocations();
        var sut = new ConfigStore(locations, NullLogger<ConfigStore>.Instance);

        sut.ScheduleSave();
        await Task.Delay(400);

        Assert.False(File.Exists(locations.ConfigFilePath));
    }

    [Fact]
    public async Task ScheduleSave_DebouncesMultipleCallsToSingleWrite()
    {
        using var locations = new TestStorageLocations();
        var sut = new ConfigStore(locations, NullLogger<ConfigStore>.Instance);

        var snapshotCalls = 0;
        sut.SetSnapshotSource(() =>
        {
            Interlocked.Increment(ref snapshotCalls);
            return DockConfigDocument.Default;
        });

        sut.ScheduleSave();
        sut.ScheduleSave();
        sut.ScheduleSave();
        sut.ScheduleSave();

        await Task.Delay(500);

        Assert.Equal(1, snapshotCalls);
        Assert.True(File.Exists(locations.ConfigFilePath));
    }

    [Fact]
    public async Task ScheduleSave_FiresAfterDebounceWindowEvenWhenIdle()
    {
        using var locations = new TestStorageLocations();
        var sut = new ConfigStore(locations, NullLogger<ConfigStore>.Instance);

        sut.SetSnapshotSource(() => DockConfigDocument.Default with { SchemaVersion = 42 });

        sut.ScheduleSave();
        await Task.Delay(400);

        var loaded = await sut.LoadAsync();
        Assert.Equal(42, loaded.SchemaVersion);
    }
}
