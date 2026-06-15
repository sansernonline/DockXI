using DockXI.Contracts;
using DockXI.DockHost;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DockXI.Tests.Unit;

public sealed class PinnedItemRepositoryTests
{
    private static PinnedItemRepository CreateRepo(out Mock<IConfigStore> store)
    {
        store = new Mock<IConfigStore>();
        return new PinnedItemRepository(store.Object, NullLogger<PinnedItemRepository>.Instance);
    }

    private static PinnedItem MakeItem(string path = @"C:\app.exe", string name = "App") =>
        new() { Kind = PinnedItemKind.Application, TargetPath = path, DisplayName = name };

    [Fact]
    public void Add_FirstItem_AppearsAndFiresEventAndSchedulesSave()
    {
        var repo = CreateRepo(out var store);
        PinnedItem? added = null;
        repo.ItemAdded += (_, e) => added = e.Item;

        var item = MakeItem();
        repo.Add(item, 0);

        Assert.Equal(1, repo.Count);
        Assert.Equal(item.Id, repo.Items[0].Id);
        Assert.NotNull(added);
        store.Verify(s => s.ScheduleSave(), Times.Once);
    }

    [Fact]
    public void Add_AtMiddleIndex_ShiftsExistingItemsAndRenumbersSortOrder()
    {
        var repo = CreateRepo(out _);
        var a = MakeItem(@"C:\a.exe", "A");
        var b = MakeItem(@"C:\b.exe", "B");
        var c = MakeItem(@"C:\c.exe", "C");

        repo.Add(a, 0);
        repo.Add(b, 1);
        repo.Add(c, 1);

        Assert.Equal(new[] { "A", "C", "B" }, repo.Items.Select(i => i.DisplayName));
        Assert.Equal(new[] { 0, 1, 2 },        repo.Items.Select(i => i.SortOrder));
    }

    [Fact]
    public void Add_AtCapacity_ThrowsWithExpectedMessage()
    {
        var repo = CreateRepo(out _);
        for (var i = 0; i < PinnedItemRepository.MaxItems; i++)
        {
            repo.Add(MakeItem($@"C:\app{i}.exe"), i);
        }

        var ex = Assert.Throws<InvalidOperationException>(
            () => repo.Add(MakeItem(@"C:\overflow.exe"), 0));
        Assert.Equal("Max item limit reached", ex.Message);
    }

    [Fact]
    public void Remove_ExistingId_RemovesAndFiresEventAndSchedulesSave()
    {
        var repo = CreateRepo(out var store);
        var item = MakeItem();
        repo.Add(item, 0);
        store.Invocations.Clear();

        PinnedItem? removed = null;
        repo.ItemRemoved += (_, e) => removed = e.Item;

        repo.Remove(item.Id);

        Assert.Equal(0, repo.Count);
        Assert.Equal(item.Id, removed?.Id);
        store.Verify(s => s.ScheduleSave(), Times.Once);
    }

    [Fact]
    public void Remove_UnknownId_NoOpAndDoesNotScheduleSave()
    {
        var repo = CreateRepo(out var store);
        repo.Add(MakeItem(), 0);
        store.Invocations.Clear();

        repo.Remove(Guid.NewGuid());

        Assert.Equal(1, repo.Count);
        store.Verify(s => s.ScheduleSave(), Times.Never);
    }

    [Fact]
    public void Reorder_ValidIds_PermutesItemsAndRenumbers()
    {
        var repo = CreateRepo(out _);
        var a = MakeItem(@"C:\a.exe", "A");
        var b = MakeItem(@"C:\b.exe", "B");
        var c = MakeItem(@"C:\c.exe", "C");
        repo.Add(a, 0);
        repo.Add(b, 1);
        repo.Add(c, 2);

        repo.Reorder([c.Id, a.Id, b.Id]);

        Assert.Equal(new[] { "C", "A", "B" }, repo.Items.Select(i => i.DisplayName));
        Assert.Equal(new[] { 0, 1, 2 },        repo.Items.Select(i => i.SortOrder));
    }

    [Fact]
    public void Reorder_WrongIdSet_Throws()
    {
        var repo = CreateRepo(out _);
        repo.Add(MakeItem(@"C:\a.exe", "A"), 0);
        repo.Add(MakeItem(@"C:\b.exe", "B"), 1);

        Assert.Throws<ArgumentException>(
            () => repo.Reorder([Guid.NewGuid(), Guid.NewGuid()]));
    }

    [Fact]
    public void Update_MergesMutableFieldsButPreservesIdentityFields()
    {
        var repo = CreateRepo(out var store);
        var original = MakeItem(@"C:\app.exe", "Original") with { SortOrder = 7 };
        repo.Add(original, 0);
        store.Invocations.Clear();

        var update = original with
        {
            Id = Guid.NewGuid(),
            Kind = PinnedItemKind.Folder,
            TargetPath = @"D:\other.exe",
            SortOrder = 99,
            DisplayName = "Renamed",
            ArgumentString = "--foo",
            WorkingDirectory = @"C:\work",
            IconCacheKey = "abc",
        };
        repo.Update(update with { Id = original.Id });

        var stored = repo.FindById(original.Id);
        Assert.NotNull(stored);
        Assert.Equal("Renamed",      stored!.DisplayName);
        Assert.Equal("--foo",        stored.ArgumentString);
        Assert.Equal(@"C:\work",     stored.WorkingDirectory);
        Assert.Equal("abc",          stored.IconCacheKey);
        Assert.Equal(PinnedItemKind.Application, stored.Kind);
        Assert.Equal(@"C:\app.exe",  stored.TargetPath);
        Assert.Equal(0,              stored.SortOrder);
        store.Verify(s => s.ScheduleSave(), Times.Once);
    }

    [Fact]
    public void FindByTargetPath_IsCaseInsensitive_AndTolerantOfTrailingSeparator()
    {
        var repo = CreateRepo(out _);
        repo.Add(MakeItem(@"C:\Windows\Notepad.exe"), 0);

        Assert.NotNull(repo.FindByTargetPath(@"c:\windows\notepad.exe"));
        Assert.NotNull(repo.FindByTargetPath(@"C:\WINDOWS\NOTEPAD.EXE"));
    }

    [Fact]
    public void Initialize_OrdersBySortOrder_AndRenumbersContiguously()
    {
        var repo = CreateRepo(out var store);
        var items = new[]
        {
            MakeItem(@"C:\c.exe", "C") with { SortOrder = 9 },
            MakeItem(@"C:\a.exe", "A") with { SortOrder = 1 },
            MakeItem(@"C:\b.exe", "B") with { SortOrder = 5 },
        };

        repo.Initialize(items);

        Assert.Equal(new[] { "A", "B", "C" }, repo.Items.Select(i => i.DisplayName));
        Assert.Equal(new[] { 0, 1, 2 },        repo.Items.Select(i => i.SortOrder));
        store.Verify(s => s.ScheduleSave(), Times.Never);
    }
}
