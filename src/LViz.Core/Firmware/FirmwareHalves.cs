using LViz.Core.ZmkConfig;

namespace LViz.Core.Firmware;

/// <summary>Which face of a split board a build entry is flashed as.</summary>
public enum FirmwareHalfRole
{
    /// <summary>The half that talks to the PC: it carries the <c>raw_hid_adapter</c> shield, or the USB port.</summary>
    Central,

    /// <summary>The other half of a split board; it pairs with the central over BLE.</summary>
    Peripheral,
}

/// <summary>Which side of a split board a build entry is.</summary>
public enum FirmwareSide
{
    /// <summary>The board or a shield ends in <c>_left</c> or <c>_lh</c>.</summary>
    Left,

    /// <summary>The board or a shield ends in <c>_right</c> or <c>_rh</c>.</summary>
    Right,

    /// <summary>Nothing named a side, or both a left and a right name occur.</summary>
    Unknown,
}

/// <summary>
/// One face of a zmk-config as the flash window needs it: its role, the
/// build matrix entry it comes from, and the name the stock ZMK workflow
/// gives its <c>.uf2</c> inside the build's <c>firmware</c> artifact.
/// </summary>
/// <param name="Role">Central or peripheral.</param>
/// <param name="EntryIndex">The <see cref="ZmkBuildEntry.Index"/> this half was built from.</param>
/// <param name="Board">The entry's <c>board:</c>.</param>
/// <param name="Shields">The entry's shields, split on whitespace.</param>
/// <param name="Side">Which side of the board this half is.</param>
/// <param name="FileName">The <c>.uf2</c> name inside the <c>firmware</c> artifact.</param>
public sealed record FirmwareHalf(
    FirmwareHalfRole Role,
    int EntryIndex,
    string Board,
    IReadOnlyList<string> Shields,
    FirmwareSide Side,
    string FileName);

/// <summary>
/// Splits an inspected zmk-config into the halves the flash window offers.
/// The central comes first: the one entry carrying the
/// <c>raw_hid_adapter</c> shield when there is exactly one, else the
/// inspector's <see cref="ZmkConfigFacts.CentralIndex"/>; on a two-entry
/// config the other entry follows as the peripheral. Blocked configs (a
/// dongle among them) and configs without an identifiable central offer
/// nothing, and <c>settings_reset</c> is never offered — the inspector
/// already dropped it. Pure over <see cref="ZmkConfigFacts"/>: odd content
/// yields an empty list, never an exception.
/// </summary>
public static class FirmwareHalves
{
    private const string RawHidAdapterShield = "raw_hid_adapter";

    private static readonly string[] LeftSuffixes = ["_left", "_lh"];
    private static readonly string[] RightSuffixes = ["_right", "_rh"];

    /// <summary>
    /// The halves to offer for flashing, central first. Empty when the
    /// config has blockers, has no entries, or has no identifiable central.
    /// </summary>
    public static IReadOnlyList<FirmwareHalf> For(ZmkConfigFacts facts)
    {
        if (facts.Blockers.Count > 0 || facts.Entries.Count == 0)
            return [];

        ZmkBuildEntry? central = null;
        var adapterHits = facts.Entries
            .Where(e => e.Shields.Contains(RawHidAdapterShield, StringComparer.Ordinal))
            .ToList();
        var centralIndex = adapterHits.Count == 1 ? adapterHits[0].Index : facts.CentralIndex;
        if (centralIndex is not null)
            central = facts.Entries.FirstOrDefault(e => e.Index == centralIndex.Value);
        if (central is null)
            return [];

        var halves = new List<FirmwareHalf> { Half(FirmwareHalfRole.Central, central) };
        if (facts.Entries.Count == 2)
            halves.Add(Half(FirmwareHalfRole.Peripheral, facts.Entries.First(e => e.Index != central.Index)));
        return halves;
    }

    /// <summary>
    /// The <c>.uf2</c> the stock ZMK workflow builds for this entry:
    /// <c>artifact-name</c> when set, else
    /// <c>${shield:+$shield-}${board}-zmk</c> with <c>/</c> in the board
    /// name replaced by <c>_</c> (HWMv2 boards like <c>nice_nano//zmk</c>).
    /// The shield value keeps its spaces.
    /// </summary>
    public static string ExpectedFileName(ZmkBuildEntry entry)
    {
        var artifact = entry.Item.Values.GetValueOrDefault("artifact-name");
        if (!string.IsNullOrWhiteSpace(artifact))
            return artifact.Trim() + ".uf2";

        var shield = entry.Item.Values.GetValueOrDefault("shield")?.Trim() ?? string.Empty;
        var board = entry.Board.Replace('/', '_');
        return (shield.Length > 0 ? shield + "-" : string.Empty) + board + "-zmk.uf2";
    }

    /// <summary>
    /// The side this entry is, from its board and shields: left for a name
    /// ending in <c>_left</c> / <c>_lh</c>, right for <c>_right</c> /
    /// <c>_rh</c>, unknown when nothing matches or both occur. Comparisons
    /// are case-insensitive.
    /// </summary>
    public static FirmwareSide SideOf(ZmkBuildEntry entry)
    {
        var left = false;
        var right = false;
        foreach (var name in new[] { entry.Board }.Concat(entry.Shields))
        {
            left |= EndsWithAny(name, LeftSuffixes);
            right |= EndsWithAny(name, RightSuffixes);
        }

        return left == right ? FirmwareSide.Unknown : left ? FirmwareSide.Left : FirmwareSide.Right;
    }

    private static bool EndsWithAny(string name, string[] suffixes) =>
        suffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    private static FirmwareHalf Half(FirmwareHalfRole role, ZmkBuildEntry entry) =>
        new(role, entry.Index, entry.Board, entry.Shields, SideOf(entry), ExpectedFileName(entry));
}
