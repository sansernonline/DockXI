using DockXI.Contracts;
using DockXI.DockHost;
using Moq;
using Xunit;

namespace DockXI.Tests.Unit;

public sealed class PinFactoryTests
{
    private static Mock<IShortcutResolver> NoopResolver()
    {
        var mock = new Mock<IShortcutResolver>();
        mock.Setup(r => r.ResolveTargetPath(It.IsAny<string>())).Returns((string?)null);
        return mock;
    }

    [Fact]
    public void FromPath_ExePath_ReturnsApplicationKind()
    {
        var item = PinFactory.FromPath(@"C:\Windows\System32\notepad.exe", NoopResolver().Object);

        Assert.NotNull(item);
        Assert.Equal(PinnedItemKind.Application, item!.Kind);
        Assert.Equal(@"C:\Windows\System32\notepad.exe", item.TargetPath);
        Assert.Equal("notepad", item.DisplayName);
    }

    [Fact]
    public void FromPath_FolderPath_ReturnsFolderKindWithFolderName()
    {
        var tmpRoot = Path.Combine(Path.GetTempPath(), "DockXIPinFactory_" + Guid.NewGuid().ToString("N"));
        var inner = Path.Combine(tmpRoot, "MyFolder");
        Directory.CreateDirectory(inner);
        try
        {
            var item = PinFactory.FromPath(inner, NoopResolver().Object);

            Assert.NotNull(item);
            Assert.Equal(PinnedItemKind.Folder, item!.Kind);
            Assert.Equal("MyFolder", item.DisplayName);
        }
        finally
        {
            Directory.Delete(tmpRoot, recursive: true);
        }
    }

    [Fact]
    public void FromPath_TxtFile_ReturnsFileKind()
    {
        var item = PinFactory.FromPath(@"C:\Users\readme.txt", NoopResolver().Object);

        Assert.NotNull(item);
        Assert.Equal(PinnedItemKind.File, item!.Kind);
        Assert.Equal("readme", item.DisplayName);
    }

    [Fact]
    public void FromPath_LnkPath_ResolvesViaShortcutResolverAndUsesResolvedTarget()
    {
        var resolver = new Mock<IShortcutResolver>();
        resolver
            .Setup(r => r.ResolveTargetPath(@"C:\Users\Public\Desktop\Notepad.lnk"))
            .Returns(@"C:\Windows\System32\notepad.exe");

        var item = PinFactory.FromPath(@"C:\Users\Public\Desktop\Notepad.lnk", resolver.Object);

        Assert.NotNull(item);
        Assert.Equal(@"C:\Windows\System32\notepad.exe", item!.TargetPath);
        Assert.Equal(PinnedItemKind.Application, item.Kind);
        Assert.Equal("notepad", item.DisplayName);
        resolver.Verify(r => r.ResolveTargetPath(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void FromPath_LnkPath_WhenResolverReturnsNull_FallsBackToLnkAsTarget()
    {
        var item = PinFactory.FromPath(@"C:\some\broken.lnk", NoopResolver().Object);

        Assert.NotNull(item);
        Assert.Equal(@"C:\some\broken.lnk", item!.TargetPath);
        // .lnk that didn't resolve is treated as a generic File (no .exe extension);
        // M3 can refine this once IIconExtractor can handle .lnk specifically.
        Assert.Equal(PinnedItemKind.File, item.Kind);
    }

    [Fact]
    public void FromPath_EmptyOrWhitespace_ReturnsNull()
    {
        Assert.Null(PinFactory.FromPath("",     NoopResolver().Object));
        Assert.Null(PinFactory.FromPath("   ",  NoopResolver().Object));
    }

    [Fact]
    public void DetectKind_ExeIsApplication_DirectoryIsFolder_OtherIsFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "DockXIKind_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            Assert.Equal(PinnedItemKind.Application, PinFactory.DetectKind(@"C:\foo.exe"));
            Assert.Equal(PinnedItemKind.Folder,      PinFactory.DetectKind(tmp));
            Assert.Equal(PinnedItemKind.File,        PinFactory.DetectKind(@"C:\foo.txt"));
            Assert.Equal(PinnedItemKind.File,        PinFactory.DetectKind(@"C:\foo"));
        }
        finally
        {
            Directory.Delete(tmp);
        }
    }
}
