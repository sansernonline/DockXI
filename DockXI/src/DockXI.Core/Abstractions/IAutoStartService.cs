namespace DockXI.Contracts;

/// <summary>
/// Toggles the Windows "start at login" behaviour by writing into the
/// per-user Run key (HKCU\Software\Microsoft\Windows\CurrentVersion\Run).
/// </summary>
public interface IAutoStartService
{
    bool IsEnabled { get; }
    void Enable();
    void Disable();
}
