namespace DockXI.Contracts;

/// <summary>
/// Holds the current in-memory AppSettings snapshot. Mutators schedule a
/// debounced save via the injected IConfigStore. Used both by the dock's
/// first-run TeachingTip (HasCompletedFirstRun) and the Settings panel in M6.
/// </summary>
public interface IAppSettingsStore
{
    AppSettings Current { get; }

    /// <summary>
    /// Mark the first-run onboarding complete. Idempotent — calling more
    /// than once after the flag is set is a no-op (no extra ScheduleSave).
    /// </summary>
    void MarkFirstRunComplete();

    void UpdateAutoStart(bool enabled);

    event EventHandler? Changed;
}
