using DockXI.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DockXI.Tests.Unit;

public sealed class InMemoryLogStoreTests
{
    private static LogEntry MakeEntry(string msg = "hello", LogLevel level = LogLevel.Information) =>
        new(DateTimeOffset.UtcNow, level, "TestCategory", msg, null);

    // ----- Add / Count / Snapshot ----------------------------------------

    [Fact]
    public void Add_SingleEntry_CountIsOne()
    {
        var sut = new InMemoryLogStore();
        sut.Add(MakeEntry());
        Assert.Equal(1, sut.Count);
    }

    [Fact]
    public void Snapshot_ReturnsEntriesInInsertionOrder()
    {
        var sut = new InMemoryLogStore();
        sut.Add(MakeEntry("first"));
        sut.Add(MakeEntry("second"));
        sut.Add(MakeEntry("third"));

        var snap = sut.Snapshot();

        Assert.Equal(3, snap.Count);
        Assert.Equal("first",  snap[0].Message);
        Assert.Equal("second", snap[1].Message);
        Assert.Equal("third",  snap[2].Message);
    }

    // ----- Capacity / overflow / DroppedCount ----------------------------

    [Fact]
    public void Add_ExactlyAtCapacity_NothingDropped()
    {
        const int cap = 5;
        var sut = new InMemoryLogStore(cap);
        for (var i = 0; i < cap; i++)
        {
            sut.Add(MakeEntry($"msg{i}"));
        }

        Assert.Equal(cap, sut.Count);
        Assert.Equal(0L, sut.DroppedCount);
    }

    [Fact]
    public void Add_AboveCapacity_OldestDropped()
    {
        const int cap = 3;
        var sut = new InMemoryLogStore(cap);

        for (var i = 0; i < cap + 2; i++)
        {
            sut.Add(MakeEntry($"msg{i}"));
        }

        Assert.Equal(cap, sut.Count);
        Assert.Equal(2L, sut.DroppedCount);
        // Newest entries survive; oldest (msg0, msg1) were evicted.
        var snap = sut.Snapshot();
        Assert.Equal("msg2", snap[0].Message);
        Assert.Equal("msg4", snap[2].Message);
    }

    [Fact]
    public void Add_ZeroOrNegativeCapacity_FallsBackToDefault()
    {
        // Capacity ≤ 0 should not crash and should use default capacity (1000).
        var sut = new InMemoryLogStore(0);
        for (var i = 0; i < 1001; i++)
        {
            sut.Add(MakeEntry($"msg{i}"));
        }
        Assert.Equal(1000, sut.Count);
        Assert.Equal(1L, sut.DroppedCount);
    }

    // ----- Clear ---------------------------------------------------------

    [Fact]
    public void Clear_ResetsCountAndDroppedCount()
    {
        const int cap = 2;
        var sut = new InMemoryLogStore(cap);
        sut.Add(MakeEntry("a"));
        sut.Add(MakeEntry("b"));
        sut.Add(MakeEntry("c")); // triggers a drop

        sut.Clear();

        Assert.Equal(0, sut.Count);
        Assert.Equal(0L, sut.DroppedCount);
    }

    [Fact]
    public void Clear_ThenAdd_WorksNormally()
    {
        var sut = new InMemoryLogStore(10);
        sut.Add(MakeEntry("before"));
        sut.Clear();
        sut.Add(MakeEntry("after"));

        var snap = sut.Snapshot();
        Assert.Single(snap);
        Assert.Equal("after", snap[0].Message);
    }

    // ----- EntryAdded event ----------------------------------------------

    [Fact]
    public void EntryAdded_FiresOncePerAdd()
    {
        var sut = new InMemoryLogStore();
        var fired = 0;
        sut.EntryAdded += (_, _) => fired++;

        sut.Add(MakeEntry("a"));
        sut.Add(MakeEntry("b"));

        Assert.Equal(2, fired);
    }

    [Fact]
    public void EntryAdded_PassesCorrectEntry()
    {
        var sut = new InMemoryLogStore();
        LogEntry? captured = null;
        sut.EntryAdded += (_, e) => captured = e;

        var entry = MakeEntry("special");
        sut.Add(entry);

        Assert.NotNull(captured);
        Assert.Equal("special", captured!.Message);
    }

    [Fact]
    public void EntryAdded_ThrowingHandler_DoesNotPropagateException()
    {
        var sut = new InMemoryLogStore();
        sut.EntryAdded += (_, _) => throw new InvalidOperationException("handler blow-up");

        var ex = Record.Exception(() => sut.Add(MakeEntry("safe")));

        Assert.Null(ex);
        Assert.Equal(1, sut.Count);
    }

    // ----- LogEntry.FormattedLine ----------------------------------------

    [Fact]
    public void FormattedLine_ContainsCategoryAndMessage()
    {
        var entry = new LogEntry(
            new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero),
            LogLevel.Warning,
            "MyApp.Module",
            "Something went wrong",
            null);

        var line = entry.FormattedLine;

        Assert.Contains("MyApp.Module",       line);
        Assert.Contains("Something went wrong", line);
        Assert.Contains("Warning",             line);
    }

    [Fact]
    public void FormattedLine_WithException_IncludesExceptionDetail()
    {
        var entry = new LogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Error,
            "Cat",
            "Oops",
            "System.Exception: boom\r\n  at Foo()");

        var line = entry.FormattedLine;

        Assert.Contains("System.Exception: boom", line);
    }

    [Fact]
    public void FormattedLine_WithoutException_NoExtraNewline()
    {
        var entry = MakeEntry("clean message");
        Assert.DoesNotContain(Environment.NewLine, entry.FormattedLine);
    }
}
