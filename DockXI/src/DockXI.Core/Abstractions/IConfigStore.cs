namespace DockXI.Contracts;

public interface IConfigStore
{
    Task<DockConfigDocument> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(DockConfigDocument snapshot, CancellationToken ct = default);

    void ScheduleSave();
}
