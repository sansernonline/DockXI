using System.Text.Json;
using DockXI.Contracts;
using Microsoft.Extensions.Logging;

namespace DockXI.Storage;

internal sealed class ConfigStore : IConfigStore, IAsyncDisposable
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(250);

    private readonly IStorageLocations _locations;
    private readonly ILogger<ConfigStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly object _scheduleGate = new();
    private CancellationTokenSource? _pendingCts;
    private Task? _pendingTask;

    private Func<DockConfigDocument>? _snapshotSource;
    private bool _disposed;

    public ConfigStore(IStorageLocations locations, ILogger<ConfigStore> logger)
    {
        _locations = locations;
        _logger = logger;
    }

    public void SetSnapshotSource(Func<DockConfigDocument> source)
    {
        _snapshotSource = source;
    }

    public async Task<DockConfigDocument> LoadAsync(CancellationToken ct = default)
    {
        var path = _locations.ConfigFilePath;
        if (!File.Exists(path))
        {
            _logger.LogInformation("Config file not found at {Path}; returning defaults.", path);
            return DockConfigDocument.Default;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var doc = await JsonSerializer.DeserializeAsync(
                stream,
                DockXIJsonContext.Default.DockConfigDocument,
                ct).ConfigureAwait(false);

            return doc ?? DockConfigDocument.Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            await ArchiveCorruptFileAsync(path, ex).ConfigureAwait(false);
            return DockConfigDocument.Default;
        }
    }

    public async Task SaveAsync(DockConfigDocument snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tmpPath = _locations.ConfigTempPath;
            var finalPath = _locations.ConfigFilePath;
            var backupPath = _locations.ConfigBackupPath;

            await using (var stream = File.Create(tmpPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    snapshot,
                    DockXIJsonContext.Default.DockConfigDocument,
                    ct).ConfigureAwait(false);
            }

            if (File.Exists(finalPath))
            {
                File.Replace(tmpPath, finalPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmpPath, finalPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to persist config to {Path}.", _locations.ConfigFilePath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void ScheduleSave()
    {
        if (_snapshotSource is null)
        {
            _logger.LogWarning("ScheduleSave called before snapshot source set; skipping.");
            return;
        }

        lock (_scheduleGate)
        {
            _pendingCts?.Cancel();
            _pendingCts = new CancellationTokenSource();
            _pendingTask = RunDebouncedSaveAsync(_pendingCts.Token);
        }
    }

    /// <summary>
    /// Cancels any pending debounced save and writes the current snapshot to
    /// disk synchronously (well, awaitably). Call this from a shutdown path to
    /// guarantee state lands on disk before the process exits.
    /// </summary>
    public async Task FlushAsync()
    {
        if (_snapshotSource is null)
        {
            return;
        }

        lock (_scheduleGate)
        {
            _pendingCts?.Cancel();
        }

        DockConfigDocument snapshot;
        try
        {
            snapshot = _snapshotSource();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot source threw during flush.");
            return;
        }

        await SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunDebouncedSaveAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceWindow, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        DockConfigDocument snapshot;
        try
        {
            snapshot = _snapshotSource!();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot source threw during debounced save.");
            return;
        }

        await SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ArchiveCorruptFileAsync(string path, Exception cause)
    {
        var corruptPath = _locations.GetConfigCorruptPath(DateTimeOffset.UtcNow);
        try
        {
            File.Move(path, corruptPath, overwrite: true);
            _logger.LogWarning(
                cause,
                "Corrupt config archived to {CorruptPath}; defaults will be used.",
                corruptPath);
        }
        catch (Exception moveEx)
        {
            _logger.LogError(
                moveEx,
                "Corrupt config at {Path} could not be archived (original cause: {Cause}).",
                path,
                cause.Message);
        }

        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Task? pending;
        lock (_scheduleGate)
        {
            pending = _pendingTask;
        }

        if (pending is not null)
        {
            try { await pending.ConfigureAwait(false); }
            catch { /* logged inside RunDebouncedSaveAsync */ }
        }

        _writeLock.Dispose();
        _pendingCts?.Dispose();
    }
}
