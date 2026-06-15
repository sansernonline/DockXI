using DockXI.Contracts;
using Microsoft.Extensions.Logging;

namespace DockXI.Settings;

internal sealed class DockConfigStore : IDockConfigStore
{
    private readonly IConfigStore _configStore;
    private readonly ILogger<DockConfigStore> _logger;
    private DockConfig _current = new();

    public DockConfigStore(IConfigStore configStore, ILogger<DockConfigStore> logger)
    {
        _configStore = configStore;
        _logger = logger;
    }

    public DockConfig Current => _current;

    public event EventHandler? Changed;

    public void Initialize(DockConfig initial)
    {
        _current = initial;
    }

    public void UpdatePosition(DockEdge edge)
    {
        if (_current.Position == edge)
        {
            return;
        }

        _current = _current with { Position = edge };
        _logger.LogInformation("DockConfig.Position → {Edge}.", edge);
        Raise();
    }

    public void UpdateAutoHide(bool enabled)
    {
        if (_current.AutoHide == enabled)
        {
            return;
        }

        _current = _current with { AutoHide = enabled };
        _logger.LogInformation("DockConfig.AutoHide → {Enabled}.", enabled);
        Raise();
    }

    public void UpdateIconSize(int iconSizeDp)
    {
        var clamped = Math.Clamp(iconSizeDp, 32, 96);
        if (_current.IconSizeDp == clamped)
        {
            return;
        }

        _current = _current with { IconSizeDp = clamped };
        _logger.LogInformation("DockConfig.IconSizeDp → {Size}.", clamped);
        Raise();
    }

    public void UpdateMagnificationLevel(MagnificationLevel level)
    {
        if (_current.MagnificationLevel == level)
        {
            return;
        }

        _current = _current with { MagnificationLevel = level };
        _logger.LogInformation("DockConfig.MagnificationLevel → {Level}.", level);
        Raise();
    }

    public void UpdateTargetMonitor(ulong displayId)
    {
        if (_current.TargetMonitorDisplayId == displayId) { return; }
        _current = _current with { TargetMonitorDisplayId = displayId };
        _logger.LogInformation("DockConfig.TargetMonitorDisplayId → {Id}.", displayId);
        Raise();
    }

    public void UpdateIsMigratedToPrimary(bool isMigrated)
    {
        if (_current.IsMigratedToPrimary == isMigrated) { return; }
        _current = _current with { IsMigratedToPrimary = isMigrated };
        Raise();
    }

    private void Raise()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        _configStore.ScheduleSave();
    }
}
