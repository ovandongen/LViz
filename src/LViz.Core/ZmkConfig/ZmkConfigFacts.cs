namespace LViz.Core.ZmkConfig;

/// <summary>Whether the zmk-raw-hid and zmk-hid-viz modules are set up.</summary>
public enum ZmkModuleStatus
{
    /// <summary>A project, a <c>.conf</c> switch or the shield is missing.</summary>
    NotSetUp,

    /// <summary>Set up with Olaf's stable fork <c>ovandongen/zmk-raw-hid</c>.</summary>
    SetUp,

    /// <summary>Set up, but with a raw-hid fork other than Olaf's.</summary>
    NeedsFork,
}

/// <summary>How <see cref="ZmkConfigFacts.CentralIndex"/> was picked.</summary>
public enum ZmkCentralReason
{
    /// <summary>One build entry: the board is not split.</summary>
    Unibody,

    /// <summary>A <c>Kconfig.defconfig</c> puts <c>ZMK_SPLIT_ROLE_CENTRAL</c> behind the central's shield.</summary>
    RepoKconfig,

    /// <summary>Exactly one entry has a board or shield ending in <c>_left</c> or <c>_lh</c>.</summary>
    LeftNaming,

    /// <summary>Nothing identified the central.</summary>
    Unknown,
}

/// <summary>Why LViz cannot use a zmk-config as it stands.</summary>
public enum ZmkConfigBlocker
{
    /// <summary>No <c>config/west.yml</c>.</summary>
    NoWestManifest,

    /// <summary>No <c>build.yaml</c> and no <c>build.yml</c>.</summary>
    NoBuildMatrix,

    /// <summary>No project <c>zmk</c> in the west manifest.</summary>
    NoZmkProject,

    /// <summary>A dongle entry, or more than 2 build entries.</summary>
    Dongle,

    /// <summary>The parsed matrix has no usable entry.</summary>
    NoBuildEntries,

    /// <summary>A reader refused a file; see <see cref="ZmkConfigFacts.UnreadableDetail"/>.</summary>
    Unreadable,
}

/// <summary>Which live-feature options the merged <c>.conf</c> settings set to <c>y</c>.</summary>
public sealed record ZmkLiveFeatures(bool Rgb, bool Signal, bool Emit, bool System);

/// <summary>
/// What LViz needs to know about a zmk-config root: its ZMK source, whether
/// it is split and which build entry is the central, whether the modules are
/// set up, and what blocks setup. Built by <see cref="ZmkConfigInspector"/>.
/// </summary>
/// <param name="Root">The config root the files were read from.</param>
/// <param name="BuildMatrixPath"><c>"build.yaml"</c>, <c>"build.yml"</c>, or null.</param>
/// <param name="ConfPaths">The <c>.conf</c> files' relative paths, sorted.</param>
/// <param name="ZmkOwner">Owner of the project <c>zmk</c>, or null.</param>
/// <param name="ZmkRevision">That project's revision, else the manifest default.</param>
/// <param name="Entries">Build entries without <c>settings_reset</c>, in matrix order.</param>
/// <param name="IsSplit">True when exactly 2 entries remain.</param>
/// <param name="CentralIndex">A <see cref="ZmkBuildEntry.Index"/> (the original
/// position in the matrix), never a position in <paramref name="Entries"/>.</param>
/// <param name="CentralReason">How the central was picked.</param>
/// <param name="HasUnderglow">Underglow (or HID-VIZ RGB) is switched on, or the board is a MoErgo one.</param>
/// <param name="ConfigId"><c>CONFIG_HID_VIZ_CONFIG_ID</c> without its quotes, or null.</param>
/// <param name="RawHidOwner">Owner of the project <c>zmk-raw-hid</c>, or null when it is absent.</param>
/// <param name="Status">Whether the modules are set up.</param>
/// <param name="Blockers">Why the config cannot be used as it stands; empty when it can.</param>
/// <param name="UnreadableDetail">The reader's message behind <see cref="ZmkConfigBlocker.Unreadable"/>, else null.</param>
/// <param name="Features">Which live-feature options the merged <c>.conf</c> settings set to <c>y</c>.</param>
public sealed record ZmkConfigFacts(
    string Root,
    string? BuildMatrixPath,
    IReadOnlyList<string> ConfPaths,
    string? ZmkOwner,
    string? ZmkRevision,
    IReadOnlyList<ZmkBuildEntry> Entries,
    bool IsSplit,
    int? CentralIndex,
    ZmkCentralReason CentralReason,
    bool HasUnderglow,
    string? ConfigId,
    string? RawHidOwner,
    ZmkModuleStatus Status,
    IReadOnlyList<ZmkConfigBlocker> Blockers,
    string? UnreadableDetail,
    ZmkLiveFeatures Features);
