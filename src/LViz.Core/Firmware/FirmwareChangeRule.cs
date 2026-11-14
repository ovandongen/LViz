using LViz.Core.GitHub;

namespace LViz.Core.Firmware;

/// <summary>
/// Decides, from the files a zmk-config changed since the peripheral's last
/// recorded flash, whether the peripheral half needs flashing too.
///
/// <para>
/// ZMK keymaps, combos and behaviors all run on the central half, so changes
/// to a <c>.keymap</c>, docs, CI files, layout jsons, the ignore file or the
/// license only call for flashing the central. The peripheral's firmware
/// only changes with what it builds from: the ZMK/module revisions
/// (<c>west.yml</c>), board and shield files (<c>.overlay</c>, <c>.conf</c>,
/// <c>.dtsi</c>, Kconfig) and the shared conf.
/// </para>
///
/// <para>
/// "There is no record of a flash at all" is the caller's case to treat as
/// unticked; this rule only ever judges a given change list.
/// </para>
/// </summary>
public static class FirmwareChangeRule
{
    /// <summary>
    /// True when a <c>/</c>-separated repo path is a file only the central
    /// half builds from: <c>*.keymap</c>, <c>*.md</c>, anything under
    /// <c>.github/</c>, <c>*.layout.json</c>, a <c>.gitignore</c> anywhere,
    /// or a <c>LICENSE*</c> file. Every comparison is case-insensitive.
    /// </summary>
    public static bool IsCentralOnly(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return path.EndsWith(".keymap", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(".github/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".layout.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a peripheral flash is on the table: the compare was cut off
    /// (so changes we cannot see may touch the peripheral), or some changed
    /// file is not central-only. An empty change list changed nothing and
    /// needs nothing.
    /// </summary>
    public static bool PeripheralNeedsFlash(GitHubCompareResult changes)
    {
        if (changes.Truncated)
        {
            return true;
        }

        foreach (var file in changes.Files)
        {
            if (!IsCentralOnly(file))
            {
                return true;
            }
        }

        return false;
    }
}
