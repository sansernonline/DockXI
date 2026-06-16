using System.Text.Json.Serialization;

namespace DockXI.Contracts;

public sealed record DockConfigDocument
{
    public static DockConfigDocument Default => new();

    public int SchemaVersion { get; set; } = 1;

    public AppSettings AppSettings { get; set; } = new();

    public DockConfig DockConfig { get; set; } = new();

    public IReadOnlyList<PinnedItem> PinnedItems { get; set; } = [];

    public IReadOnlyList<MonitorBinding> MonitorBindings { get; set; } = [];
}

public sealed record AppSettings
{
    public bool AutoStartEnabled { get; set; }

    public ThemeOverride ThemeOverride { get; set; } = ThemeOverride.System;

    public bool TelemetryOptIn { get; set; }

    public bool HasCompletedFirstRun { get; set; }
}

public sealed record DockConfig
{
    public DockEdge Position { get; set; } = DockEdge.Bottom;

    public bool AutoHide { get; set; }

    /// <summary>
    /// When true, all drag operations (drag-in from Explorer, drag-out to
    /// unpin, internal reorder) are ignored. Clicking tiles to launch and
    /// menu actions still work. Used as an "anti-accidental" guard.
    /// </summary>
    public bool IsLocked { get; set; }

    public int IconSizeDp { get; set; } = Defaults.Dock.IconSizeDp;

    public MagnificationLevel MagnificationLevel { get; set; } = MagnificationLevel.Medium;

    public ulong TargetMonitorDisplayId { get; set; }

    public bool IsMigratedToPrimary { get; set; }
}

public sealed record PinnedItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public PinnedItemKind Kind { get; set; } = PinnedItemKind.Application;

    public string TargetPath { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? ArgumentString { get; set; }

    public string? WorkingDirectory { get; set; }

    public int SortOrder { get; set; }

    public string? IconCacheKey { get; set; }

    public string? CustomIconPath { get; set; }
}

public sealed record MonitorBinding
{
    public ulong DisplayId { get; set; }

    public string FriendlyName { get; set; } = string.Empty;

    public DockEdge LastEdge { get; set; } = DockEdge.Bottom;

    public int LastIconSizeDp { get; set; } = Defaults.Dock.IconSizeDp;

    public DateTimeOffset LastSeenAt { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ThemeOverride>))]
public enum ThemeOverride { System, Light, Dark }

[JsonConverter(typeof(JsonStringEnumConverter<DockEdge>))]
public enum DockEdge { Top, Bottom, Left, Right }

[JsonConverter(typeof(JsonStringEnumConverter<MagnificationLevel>))]
public enum MagnificationLevel
{
    Low = 1,
    Medium = 2,
    High = 3
}

[JsonConverter(typeof(JsonStringEnumConverter<PinnedItemKind>))]
public enum PinnedItemKind { Application, Folder, Url, File }
