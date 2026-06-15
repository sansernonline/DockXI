using System.Collections.ObjectModel;

namespace DockXI.Contracts;

public interface IPinnedItemRepository
{
    ReadOnlyObservableCollection<PinnedItem> Items { get; }

    int Count { get; }

    PinnedItem? FindById(Guid id);

    PinnedItem? FindByTargetPath(string targetPath);

    void Add(PinnedItem item, int insertionIndex);

    void Remove(Guid id);

    void Reorder(IReadOnlyList<Guid> orderedIds);

    void Update(PinnedItem updated);

    event EventHandler<PinnedItemEventArgs> ItemAdded;

    event EventHandler<PinnedItemEventArgs> ItemRemoved;
}

public record PinnedItemEventArgs(PinnedItem Item);
