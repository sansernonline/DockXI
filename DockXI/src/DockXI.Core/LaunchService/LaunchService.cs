using System.Diagnostics;
using DockXI.Contracts;
using Microsoft.Extensions.Logging;

namespace DockXI.LaunchService;

internal sealed class LaunchService : ILaunchService, IDisposable
{
    private readonly ILogger<LaunchService> _logger;
    private readonly Timer _snapshotTimer;
    private HashSet<string> _runningNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public LaunchService(ILogger<LaunchService> logger)
    {
        _logger = logger;
        // Take the first snapshot at construction time so the dock has an
        // accurate IsRunning the moment it renders, not 1 s later.
        RefreshSnapshot();
        _snapshotTimer = new Timer(_ => RefreshSnapshot(), state: null,
            dueTime: TimeSpan.FromSeconds(1), period: TimeSpan.FromSeconds(1));
    }

    public event EventHandler? ProcessSnapshotUpdated;

    public Task<bool> LaunchAsync(PinnedItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        try
        {
            switch (item.Kind)
            {
                case PinnedItemKind.Application:
                case PinnedItemKind.File:
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = item.TargetPath,
                        UseShellExecute = true,
                    };
                    if (!string.IsNullOrWhiteSpace(item.ArgumentString))
                    {
                        psi.Arguments = item.ArgumentString;
                    }
                    if (!string.IsNullOrWhiteSpace(item.WorkingDirectory))
                    {
                        psi.WorkingDirectory = item.WorkingDirectory;
                    }
                    Process.Start(psi);
                    break;
                }

                case PinnedItemKind.Folder:
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{item.TargetPath}\"",
                        UseShellExecute = false,
                    });
                    break;
                }

                case PinnedItemKind.Url:
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = item.TargetPath,
                        UseShellExecute = true,
                    });
                    break;
                }

                default:
                    _logger.LogWarning("Unknown PinnedItemKind {Kind} for {Path}.", item.Kind, item.TargetPath);
                    return Task.FromResult(false);
            }

            _logger.LogInformation("Launched {Kind} '{Path}'.", item.Kind, item.TargetPath);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launch failed for {Path}.", item.TargetPath);
            return Task.FromResult(false);
        }
    }

    public bool IsTargetValid(PinnedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.Kind switch
        {
            PinnedItemKind.Application or PinnedItemKind.File => File.Exists(item.TargetPath),
            PinnedItemKind.Folder => Directory.Exists(item.TargetPath),
            PinnedItemKind.Url => Uri.TryCreate(item.TargetPath, UriKind.Absolute, out var uri)
                                  && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
            _ => false,
        };
    }

    public bool IsProcessRunning(PinnedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind != PinnedItemKind.Application)
        {
            return false;
        }

        var processName = Path.GetFileNameWithoutExtension(item.TargetPath);
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        // ProcessName comparison is case-insensitive (Windows process names) and
        // intentionally ignores path — two notepad.exe instances in different
        // directories both light up the indicator. That matches user intuition
        // ("is the app I see running this app?") and avoids OS-name-quirks like
        // 32/64-bit redirection masking the executable path.
        return _runningNames.Contains(processName);
    }

    private void RefreshSnapshot()
    {
        try
        {
            var processes = Process.GetProcesses();
            var fresh = new HashSet<string>(processes.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var p in processes)
            {
                try { fresh.Add(p.ProcessName); }
                catch { /* process may have exited between GetProcesses and read */ }
                finally { p.Dispose(); }
            }

            // Only raise the event when membership actually changed — pumps to
            // PinnedItemView.IsRunning on every tick would shake the UI thread
            // unnecessarily and can interfere with the running-dot fade-in.
            if (!fresh.SetEquals(_runningNames))
            {
                _runningNames = fresh;
                ProcessSnapshotUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Process snapshot refresh failed; previous snapshot retained.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _snapshotTimer.Dispose();
    }
}
