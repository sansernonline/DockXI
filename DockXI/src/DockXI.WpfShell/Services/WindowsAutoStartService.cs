using System;
using System.Diagnostics;
using DockXI.Contracts;
using Microsoft.Win32;

namespace DockXI.WpfShell.Services;

/// <summary>
/// HKCU Run-key implementation of <see cref="IAutoStartService"/>. No admin
/// rights needed — writes are scoped to the current user.
/// </summary>
internal sealed class WindowsAutoStartService : IAutoStartService
{
    private const string RunKey    = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DockXI";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
    }

    public void Enable()
    {
        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath)) { return; }
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        key?.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
