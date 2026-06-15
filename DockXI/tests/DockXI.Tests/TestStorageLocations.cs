using DockXI.Contracts;

namespace DockXI.Tests.Unit;

internal sealed class TestStorageLocations : IStorageLocations, IDisposable
{
    public TestStorageLocations()
    {
        LocalStateFolder = Path.Combine(Path.GetTempPath(), "DockXITests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(LocalStateFolder);
        Directory.CreateDirectory(IconCacheFolder);
        Directory.CreateDirectory(LogsFolder);
    }

    public string LocalStateFolder { get; }
    public string ConfigFilePath => Path.Combine(LocalStateFolder, "config.json");
    public string ConfigTempPath => Path.Combine(LocalStateFolder, "config.json.tmp");
    public string ConfigBackupPath => Path.Combine(LocalStateFolder, "config.json.bak");
    public string IconCacheFolder => Path.Combine(LocalStateFolder, "IconCache");
    public string LogsFolder => Path.Combine(LocalStateFolder, "logs");

    public string GetConfigCorruptPath(DateTimeOffset timestampUtc) =>
        Path.Combine(LocalStateFolder, $"config.json.corrupt-{timestampUtc.UtcDateTime:yyyyMMddHHmmss}");

    public void Dispose()
    {
        try { Directory.Delete(LocalStateFolder, recursive: true); } catch { }
    }
}
