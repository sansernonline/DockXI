namespace DockXI.Contracts;

public interface IShortcutResolver
{
    /// <summary>
    /// Resolves a .lnk shortcut to its target file path.
    /// Returns null if the file is not a valid shortcut, the target cannot be read,
    /// or the resolved target does not exist.
    /// </summary>
    string? ResolveTargetPath(string lnkPath);
}
