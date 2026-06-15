namespace DockXI.Contracts;

/// <summary>
/// In-memory current DockConfig snapshot + targeted mutators. Mirrors
/// IAppSettingsStore: load once at startup from IConfigStore, then mutate
/// through the focused methods so each call schedules at most one debounced
/// save. The mutators are intentionally typed (not a generic `Update(DockConfig)`)
/// so callers can't silently drop fields they didn't mean to touch.
/// </summary>
public interface IDockConfigStore
{
    DockConfig Current { get; }

    void UpdatePosition(DockEdge edge);

    void UpdateAutoHide(bool enabled);

    void UpdateIconSize(int iconSizeDp);

    void UpdateMagnificationLevel(MagnificationLevel level);

    void UpdateTargetMonitor(ulong displayId);

    void UpdateIsMigratedToPrimary(bool isMigrated);

    event EventHandler? Changed;
}
