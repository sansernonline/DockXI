namespace DockXI.Contracts;

public interface ILaunchService
{
    Task<bool> LaunchAsync(PinnedItem item, CancellationToken ct = default);

    bool IsTargetValid(PinnedItem item);

    bool IsProcessRunning(PinnedItem item);

    event EventHandler ProcessSnapshotUpdated;
}
