using DockXI.Contracts;
using DockXI.LaunchService;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DockXI.Tests.Unit;

public sealed class LaunchServiceTests
{
    private readonly DockXI.LaunchService.LaunchService _sut =
        new(NullLogger<DockXI.LaunchService.LaunchService>.Instance);

    [Fact]
    public void IsTargetValid_ApplicationExisting_Returns_True()
    {
        var item = new PinnedItem
        {
            Kind = PinnedItemKind.Application,
            TargetPath = Environment.GetFolderPath(Environment.SpecialFolder.System) + @"\notepad.exe",
        };
        Assert.True(_sut.IsTargetValid(item));
    }

    [Fact]
    public void IsTargetValid_ApplicationMissing_Returns_False()
    {
        var item = new PinnedItem { Kind = PinnedItemKind.Application, TargetPath = @"C:\does-not-exist\nope.exe" };
        Assert.False(_sut.IsTargetValid(item));
    }

    [Fact]
    public void IsTargetValid_FolderExisting_Returns_True()
    {
        var path = Path.Combine(Path.GetTempPath(), "DockXILaunch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var item = new PinnedItem { Kind = PinnedItemKind.Folder, TargetPath = path };
            Assert.True(_sut.IsTargetValid(item));
        }
        finally { Directory.Delete(path); }
    }

    [Fact]
    public void IsTargetValid_FolderMissing_Returns_False()
    {
        var item = new PinnedItem { Kind = PinnedItemKind.Folder, TargetPath = @"C:\definitely-not-a-folder-here" };
        Assert.False(_sut.IsTargetValid(item));
    }

    [Theory]
    [InlineData("https://example.com",      true)]
    [InlineData("http://example.com",       true)]
    [InlineData("ftp://example.com",        false)]
    [InlineData("not a url",                false)]
    [InlineData("",                         false)]
    public void IsTargetValid_Url_AcceptsHttpAndHttpsOnly(string url, bool expected)
    {
        var item = new PinnedItem { Kind = PinnedItemKind.Url, TargetPath = url };
        Assert.Equal(expected, _sut.IsTargetValid(item));
    }

    [Fact]
    public void IsProcessRunning_NonApplicationKind_ReturnsFalse()
    {
        // URL / Folder / File kinds never light up the running indicator —
        // the indicator is a "is this exe in the process list" signal only.
        Assert.False(_sut.IsProcessRunning(new PinnedItem { Kind = PinnedItemKind.Folder, TargetPath = @"C:\Users" }));
        Assert.False(_sut.IsProcessRunning(new PinnedItem { Kind = PinnedItemKind.Url, TargetPath = "https://example.com" }));
        Assert.False(_sut.IsProcessRunning(new PinnedItem { Kind = PinnedItemKind.File, TargetPath = @"C:\readme.txt" }));
    }

    [Fact]
    public void IsProcessRunning_SelfProcess_ReturnsTrue()
    {
        // The current test runner itself is in the running process list, so
        // pinning its own executable should report IsRunning=true.
        var self = System.Diagnostics.Process.GetCurrentProcess();
        var item = new PinnedItem
        {
            Kind = PinnedItemKind.Application,
            TargetPath = self.MainModule?.FileName ?? "testhost.exe",
        };
        Assert.True(_sut.IsProcessRunning(item));
    }

    [Fact]
    public void IsProcessRunning_MissingExe_ReturnsFalse()
    {
        var item = new PinnedItem
        {
            Kind = PinnedItemKind.Application,
            TargetPath = @"C:\definitely-not-running-9bf4c2.exe",
        };
        Assert.False(_sut.IsProcessRunning(item));
    }
}
