using System.Text.RegularExpressions;

namespace LViz.Core.ZmkConfig;

/// <summary>
/// Answers what LViz needs to know about a zmk-config: its ZMK source,
/// whether it is split and which build entry is the central, whether the
/// zmk-raw-hid + zmk-hid-viz modules are set up, and what blocks setup.
/// Pure over a <see cref="ZmkConfigFileSet"/>: content never throws — the
/// problems surface as <see cref="ZmkConfigFacts.Blockers"/>.
/// </summary>
public static class ZmkConfigInspector
{
    private const string RawHidAdapterShield = "raw_hid_adapter";
    private const string SettingsResetShield = "settings_reset";
    private const string CentralConfigLine = "config ZMK_SPLIT_ROLE_CENTRAL";

    /// <summary>An <c>if SHIELD_&lt;X&gt;</c> line naming exactly one symbol.</summary>
    private static readonly Regex ShieldIf =
        new(@"^if\s+SHIELD_([A-Za-z0-9_]+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Reads the root's files, then inspects them.</summary>
    public static ZmkConfigFacts Inspect(string root) => Inspect(ZmkConfigFileSet.Read(root));

    public static ZmkConfigFacts Inspect(ZmkConfigFileSet files)
    {
        string? unreadableDetail = null;

        var hasWest = false;
        ZmkWestManifest? west = null;
        if (files.Files.TryGetValue("config/west.yml", out var westText))
        {
            hasWest = true;
            try
            {
                west = ZmkWestManifestText.Parse(westText);
            }
            catch (ZmkConfigFormatException e)
            {
                unreadableDetail ??= e.Message;
            }
        }

        var matrixPath = files.Files.ContainsKey("build.yaml") ? "build.yaml"
            : files.Files.ContainsKey("build.yml") ? "build.yml"
            : null;
        ZmkBuildMatrix? matrix = null;
        if (matrixPath is not null)
        {
            try
            {
                matrix = ZmkBuildMatrixText.Parse(files.Files[matrixPath], matrixPath);
            }
            catch (ZmkConfigFormatException e)
            {
                unreadableDetail ??= e.Message;
            }
        }

        // The bond-wiping entry is tooling, not a face of the board.
        var entries = matrix?.Entries
            .Where(e => !e.Shields.Contains(SettingsResetShield))
            .ToList() ?? [];

        // Later files (ordinal path order) win over earlier ones, and a later
        // assignment within a file wins over its earlier ones.
        var confPaths = files.Files.Keys
            .Where(k => k.EndsWith(".conf", StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in confPaths)
        {
            try
            {
                foreach (var setting in ZmkConfText.Parse(files.Files[path]))
                    settings[setting.Key] = setting.Value;
            }
            catch (ZmkConfigFormatException e)
            {
                unreadableDetail ??= e.Message;
            }
        }

        var zmkProject = west?.Project("zmk");
        var rawHidProject = west?.Project("zmk-raw-hid");
        var hidVizProject = west?.Project("zmk-hid-viz");
        var central = FindCentral(entries, files, out var centralReason);

        var underglow = IsYes(settings, "CONFIG_ZMK_RGB_UNDERGLOW")
                        || IsYes(settings, "CONFIG_HID_VIZ_RGB")
                        || entries.Any(e =>
                            e.Board.StartsWith("glove80_", StringComparison.Ordinal)
                            || e.Board.StartsWith("go60_", StringComparison.Ordinal));

        return new ZmkConfigFacts(
            files.Root,
            matrixPath,
            confPaths,
            zmkProject is null ? null : west!.OwnerOf(zmkProject),
            zmkProject?.Values.GetValueOrDefault("revision") ?? west?.DefaultRevision,
            entries,
            entries.Count == 2,
            central,
            centralReason,
            underglow,
            settings.TryGetValue("CONFIG_HID_VIZ_CONFIG_ID", out var configId)
                ? ZmkYamlLines.Unquote(configId)
                : null,
            rawHidProject is null ? null : west!.OwnerOf(rawHidProject),
            StatusOf(west, rawHidProject, hidVizProject, settings, entries),
            BuildBlockers(hasWest, matrixPath is not null, matrix, entries, west, unreadableDetail),
            unreadableDetail);
    }

    /// <summary>
    /// A rule only speaks when its own input was readable: no manifest means
    /// no <see cref="ZmkConfigBlocker.NoZmkProject"/>, and an unreadable or
    /// absent matrix means no entry blockers.
    /// </summary>
    private static List<ZmkConfigBlocker> BuildBlockers(
        bool hasWest,
        bool hasMatrix,
        ZmkBuildMatrix? matrix,
        List<ZmkBuildEntry> entries,
        ZmkWestManifest? west,
        string? unreadableDetail)
    {
        var blockers = new List<ZmkConfigBlocker>();
        if (!hasWest)
            blockers.Add(ZmkConfigBlocker.NoWestManifest);
        if (!hasMatrix)
            blockers.Add(ZmkConfigBlocker.NoBuildMatrix);
        if (unreadableDetail is not null)
            blockers.Add(ZmkConfigBlocker.Unreadable);
        if (west is not null && west.Project("zmk") is null)
            blockers.Add(ZmkConfigBlocker.NoZmkProject);
        if (matrix is not null && entries.Count == 0)
            blockers.Add(ZmkConfigBlocker.NoBuildEntries);
        if (entries.Count > 2 || entries.Any(IsDongle))
            blockers.Add(ZmkConfigBlocker.Dongle);
        return blockers;
    }

    private static bool IsDongle(ZmkBuildEntry entry) =>
        entry.Board.Contains("dongle", StringComparison.OrdinalIgnoreCase)
        || entry.Shields.Any(s => s.Contains("dongle", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The central: the lone entry of a unibody; else (for a 2-entry matrix)
    /// the entry whose shield a repo <c>Kconfig.defconfig</c> marks central,
    /// else the one entry named <c>*_left</c> / <c>*_lh</c>; else none.
    /// </summary>
    private static int? FindCentral(List<ZmkBuildEntry> entries, ZmkConfigFileSet files, out ZmkCentralReason reason)
    {
        if (entries.Count == 1)
        {
            reason = ZmkCentralReason.Unibody;
            return entries[0].Index;
        }

        reason = ZmkCentralReason.Unknown;
        if (entries.Count != 2)
            return null;

        var centralShields = CentralShieldNames(files);
        var hits = entries.Where(e => e.Shields.Any(centralShields.Contains)).ToList();
        if (hits.Count == 1)
        {
            reason = ZmkCentralReason.RepoKconfig;
            return hits[0].Index;
        }

        hits = entries.Where(HasLeftName).ToList();
        if (hits.Count == 1)
        {
            reason = ZmkCentralReason.LeftNaming;
            return hits[0].Index;
        }
        return null;
    }

    private static bool HasLeftName(ZmkBuildEntry entry) =>
        EndsLeft(entry.Board) || entry.Shields.Any(EndsLeft);

    private static bool EndsLeft(string name) =>
        name.EndsWith("_left", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("_lh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The lower-cased shield names whose <c>if SHIELD_&lt;X&gt;</c> block
    /// sets <c>config ZMK_SPLIT_ROLE_CENTRAL</c> directly, scanned from every
    /// <c>Kconfig.defconfig</c> in the set. <c>if …</c> lines nest (a block
    /// ends at its matching <c>endif</c>); a <c>ZMK_SPLIT_ROLE_CENTRAL</c>
    /// inside a nested other block does not count.
    /// </summary>
    private static HashSet<string> CentralShieldNames(ZmkConfigFileSet files)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files.Files.Keys
                     .Where(k => k.EndsWith("/Kconfig.defconfig", StringComparison.Ordinal))
                     .OrderBy(k => k, StringComparer.Ordinal))
        {
            // A SHIELD_<X> name for a shield block, null for any other 'if' block.
            var openBlocks = new Stack<string?>();
            foreach (var line in ZmkYamlLines.Split(files.Files[path]).Select(l => l.Trim()))
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;

                var shield = ShieldIf.Match(line);
                if (shield.Success)
                    openBlocks.Push(shield.Groups[1].Value);
                else if (line == "endif")
                {
                    if (openBlocks.Count > 0)
                        openBlocks.Pop();
                }
                else if (line.StartsWith(CentralConfigLine, StringComparison.Ordinal)
                         && openBlocks.Count > 0 && openBlocks.Peek() is { } shieldName)
                    names.Add(shieldName.ToLowerInvariant());
                else if (line.StartsWith("if ", StringComparison.Ordinal))
                    openBlocks.Push(null);
            }
        }

        return names;
    }

    private static ZmkModuleStatus StatusOf(
        ZmkWestManifest? west,
        ZmkYamlItem? rawHidProject,
        ZmkYamlItem? hidVizProject,
        Dictionary<string, string> settings,
        List<ZmkBuildEntry> entries)
    {
        if (rawHidProject is null || hidVizProject is null)
            return ZmkModuleStatus.NotSetUp;
        if (!IsYes(settings, "CONFIG_RAW_HID") || !IsYes(settings, "CONFIG_HID_VIZ"))
            return ZmkModuleStatus.NotSetUp;

        // Any entry carrying the shield counts, not only the detected central:
        // the wizard lets the user pick the other half (a guessed or Kconfig
        // central can be wrong), and that choice must read back as set up.
        if (!entries.Any(e => e.Shields.Contains(RawHidAdapterShield)))
            return ZmkModuleStatus.NotSetUp;

        return string.Equals(west!.OwnerOf(rawHidProject), "ovandongen", StringComparison.Ordinal)
            ? ZmkModuleStatus.SetUp
            : ZmkModuleStatus.NeedsFork;
    }

    private static bool IsYes(Dictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) && string.Equals(value, "y", StringComparison.OrdinalIgnoreCase);
}
