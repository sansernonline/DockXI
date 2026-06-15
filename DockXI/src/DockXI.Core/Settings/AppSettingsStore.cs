using DockXI.Contracts;
using Microsoft.Extensions.Logging;

namespace DockXI.Settings;

internal sealed class AppSettingsStore : IAppSettingsStore
{
    private readonly IConfigStore _configStore;
    private readonly ILogger<AppSettingsStore> _logger;
    private AppSettings _current = new();

    public AppSettingsStore(IConfigStore configStore, ILogger<AppSettingsStore> logger)
    {
        _configStore = configStore;
        _logger = logger;
    }

    public AppSettings Current => _current;

    public event EventHandler? Changed;

    public void Initialize(AppSettings initial)
    {
        _current = initial;
    }

    public void MarkFirstRunComplete()
    {
        if (_current.HasCompletedFirstRun) { return; }
        _current = _current with { HasCompletedFirstRun = true };
        _logger.LogInformation("HasCompletedFirstRun → true.");
        Changed?.Invoke(this, EventArgs.Empty);
        _configStore.ScheduleSave();
    }

    public void UpdateAutoStart(bool enabled)
    {
        if (_current.AutoStartEnabled == enabled) { return; }
        _current = _current with { AutoStartEnabled = enabled };
        _logger.LogInformation("AutoStartEnabled → {Enabled}.", enabled);
        Changed?.Invoke(this, EventArgs.Empty);
        _configStore.ScheduleSave();
    }
}
