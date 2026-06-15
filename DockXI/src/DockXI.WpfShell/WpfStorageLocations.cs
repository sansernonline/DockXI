using System.IO;
using DockXI.Contracts;

namespace DockXI.WpfShell;

internal sealed class WpfStorageLocations : IStorageLocations
{
    public WpfStorageLocations()
    {
        LocalStateFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DockXI");
        Directory.CreateDirectory(LocalStateFolder);
        Directory.CreateDirectory(IconCacheFolder);
        Directory.CreateDirectory(LogsFolder);
    }

    public string LocalStateFolder { get; }

    public string ConfigFilePath   => Path.Combine(LocalStateFolder, "config.json");
    public string ConfigTempPath   => Path.Combine(LocalStateFolder, "config.json.tmp");
    public string ConfigBackupPath => Path.Combine(LocalStateFolder, "config.json.bak");

    public string GetConfigCorruptPath(DateTimeOffset timestampUtc) =>
        Path.Combine(LocalStateFolder, $"config.json.corrupt-{timestampUtc.UtcDateTime:yyyyMMddHHmmss}");

    public string IconCacheFolder => Path.Combine(LocalStateFolder, "IconCache");
    public string LogsFolder      => Path.Combine(LocalStateFolder, "logs");
}
