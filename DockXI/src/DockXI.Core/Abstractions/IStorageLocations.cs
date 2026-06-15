namespace DockXI.Contracts;

public interface IStorageLocations
{
    string LocalStateFolder { get; }

    string ConfigFilePath { get; }

    string ConfigTempPath { get; }

    string ConfigBackupPath { get; }

    string GetConfigCorruptPath(DateTimeOffset timestampUtc);

    string IconCacheFolder { get; }

    string LogsFolder { get; }
}
