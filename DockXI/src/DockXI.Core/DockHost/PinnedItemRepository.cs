using System.Collections.ObjectModel;
using DockXI.Contracts;
using Microsoft.Extensions.Logging;

namespace DockXI.DockHost;

internal sealed class PinnedItemRepository : IPinnedItemRepository
{
    public const int MaxItems = 50;

    private readonly ObservableCollection<PinnedItem> _items = [];
    private readonly ReadOnlyObservableCollection<PinnedItem> _readonlyItems;
    private readonly IConfigStore _configStore;
    private readonly ILogger<PinnedItemRepository> _logger;

    public PinnedItemRepository(IConfigStore configStore, ILogger<PinnedItemRepository> logger)
    {
        _configStore = configStore;
        _logger = logger;
        _readonlyItems = new ReadOnlyObservableCollection<PinnedItem>(_items);
    }

    public ReadOnlyObservableCollection<PinnedItem> Items => _readonlyItems;

    public int Count => _items.Count;

    public event EventHandler<PinnedItemEventArgs>? ItemAdded;
    public event EventHandler<PinnedItemEventArgs>? ItemRemoved;

    public void Initialize(IEnumerable<PinnedItem> initial)
    {
        _items.Clear();
        var ordered = initial.OrderBy(i => i.SortOrder).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            _items.Add(ordered[i] with { SortOrder = i });
        }
    }

    public PinnedItem? FindById(Guid id) =>
        _items.FirstOrDefault(item => item.Id == id);

    public PinnedItem? FindByTargetPath(string targetPath)
    {
        var normalized = NormalizePath(targetPath);
        return _items.FirstOrDefault(
            item => string.Equals(NormalizePath(item.TargetPath), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public void Add(PinnedItem item, int insertionIndex)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_items.Count >= MaxItems)
        {
            throw new InvalidOperationException("Max item limit reached");
        }

        var clamped = Math.Clamp(insertionIndex, 0, _items.Count);
        var inserted = item with { SortOrder = clamped };
        _items.Insert(clamped, inserted);
        RenumberSortOrders();

        ItemAdded?.Invoke(this, new PinnedItemEventArgs(inserted));
        _configStore.ScheduleSave();
    }

    public void Remove(Guid id)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            _logger.LogDebug("Remove called for unknown id {Id}; no-op.", id);
            return;
        }

        var removed = _items[index];
        _items.RemoveAt(index);
        RenumberSortOrders();

        ItemRemoved?.Invoke(this, new PinnedItemEventArgs(removed));
        _configStore.ScheduleSave();
    }

    public void Reorder(IReadOnlyList<Guid> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        if (orderedIds.Count != _items.Count ||
            orderedIds.Distinct().Count() != orderedIds.Count ||
            orderedIds.Any(id => IndexOf(id) < 0))
        {
            throw new ArgumentException(
                "orderedIds must contain exactly the same ids as Items.",
                nameof(orderedIds));
        }

        var snapshot = _items.ToDictionary(i => i.Id);
        _items.Clear();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            _items.Add(snapshot[orderedIds[i]] with { SortOrder = i });
        }

        _configStore.ScheduleSave();
    }

    public void Update(PinnedItem updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        var index = IndexOf(updated.Id);
        if (index < 0)
        {
            _logger.LogWarning("Update called for unknown id {Id}; no-op.", updated.Id);
            return;
        }

        var existing = _items[index];
        var merged = existing with
        {
            DisplayName = updated.DisplayName,
            ArgumentString = updated.ArgumentString,
            WorkingDirectory = updated.WorkingDirectory,
            IconCacheKey = updated.IconCacheKey,
            CustomIconPath = updated.CustomIconPath,
        };
        _items[index] = merged;

        _configStore.ScheduleSave();
    }

    public IReadOnlyList<PinnedItem> Snapshot() => _items.ToArray();

    private int IndexOf(Guid id)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Id == id)
            {
                return i;
            }
        }
        return -1;
    }

    private void RenumberSortOrders()
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].SortOrder != i)
            {
                _items[i] = _items[i] with { SortOrder = i };
            }
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
