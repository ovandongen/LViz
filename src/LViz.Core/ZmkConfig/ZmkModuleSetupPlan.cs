namespace LViz.Core.ZmkConfig;

/// <summary>
/// What the user picked in the module setup wizard: which build entry is the
/// board's central, and which live features to switch on.
/// </summary>
/// <param name="CentralIndex">A <see cref="ZmkBuildEntry.Index"/> the modules serve.</param>
/// <param name="Rgb">Switch on <c>CONFIG_HID_VIZ_RGB</c> (only honoured when the config has underglow).</param>
/// <param name="Signal">Switch on <c>CONFIG_HID_VIZ_SIGNAL</c>.</param>
/// <param name="Emit">Switch on <c>CONFIG_HID_VIZ_EMIT</c>.</param>
public sealed record ZmkModuleChoices(int CentralIndex, bool Rgb, bool Signal, bool Emit);

/// <summary>
/// One file the setup writes: its '/'-separated path in the config, the text
/// it holds now (null: the file does not exist yet) and the text to save.
/// </summary>
public sealed record ZmkConfigFileChange(string RelativePath, string? OldText, string NewText)
{
    /// <summary>True when this change creates the file.</summary>
    public bool IsNew => OldText is null;
}

/// <summary>
/// The exact file changes the module setup would save, plus what it pins.
/// Built by <see cref="Build"/>.
/// </summary>
/// <param name="Changes">Only the files that actually change, in the order west, build matrix, conf.</param>
/// <param name="Revision">The module revisions the west entries get.</param>
/// <param name="SwitchesToFork">True when the setup replaces a raw-hid fork with Olaf's.</param>
/// <param name="NewConfigId">The generated <c>CONFIG_HID_VIZ_CONFIG_ID</c> value without quotes, or null.</param>
public sealed record ZmkModuleSetupPlan(
    IReadOnlyList<ZmkConfigFileChange> Changes,
    ZmkModuleRevision Revision,
    bool SwitchesToFork,
    string? NewConfigId)
{
    private const string WestPath = "config/west.yml";
    private const string RawHidAdapterShield = "raw_hid_adapter";

    /// <summary>Side suffixes a conf name loses: <c>cosmos82_right</c> → <c>config/cosmos82.conf</c>.</summary>
    private static readonly string[] SideSuffixes = ["_left", "_right", "_lh", "_rh"];

    /// <summary>
    /// Turns the inspected files, the facts and the user's choices into the
    /// files to save. Refuses a blocked or already-set-up config and an
    /// unknown central. The result is guarded: applying every change and
    /// inspecting the outcome must leave the config set up and unblocked.
    /// </summary>
    /// <exception cref="ArgumentException">the config is blocked, already set up, or the central is not a build entry.</exception>
    /// <exception cref="InvalidOperationException">the patched config is not set up (plan bug or hopeless config).</exception>
    public static ZmkModuleSetupPlan Build(
        ZmkConfigFileSet files,
        ZmkConfigFacts facts,
        ZmkModuleChoices choices,
        Func<Guid> newGuid)
    {
        if (facts.Blockers.Count > 0)
            throw new ArgumentException(
                $"the zmk-config is blocked: {string.Join(", ", facts.Blockers)}", nameof(facts));
        if (facts.Status == ZmkModuleStatus.SetUp)
            throw new ArgumentException("the modules are already set up", nameof(facts));
        var central = facts.Entries.FirstOrDefault(e => e.Index == choices.CentralIndex);
        if (central is null)
            throw new ArgumentException(
                $"{choices.CentralIndex} is not a build entry of this config", nameof(choices));

        var revision = ZmkModuleRevisions.For(facts.ZmkOwner, facts.ZmkRevision);
        var changes = new List<ZmkConfigFileChange>();

        var westOld = files.Files[WestPath];
        AddIfChanged(changes, WestPath, westOld,
            ZmkWestManifestWriter.AddModules(westOld, revision.RawHid, revision.HidViz));

        var buildPath = facts.BuildMatrixPath!; // no blockers: the matrix is there
        var buildOld = files.Files[buildPath];
        AddIfChanged(changes, buildPath, buildOld,
            ZmkBuildMatrixWriter.AddShield(buildOld, choices.CentralIndex, RawHidAdapterShield));

        var confPath = ConfPath(facts, central);
        files.Files.TryGetValue(confPath, out var confOld);
        var newConfigId = facts.ConfigId is null ? newGuid().ToString("D") : null;
        var confNew = ZmkConfWriter.Apply(confOld, Settings(facts, choices, newConfigId));
        AddIfChanged(changes, confPath, confOld, confNew);

        Guard(files, changes);

        return new ZmkModuleSetupPlan(changes, revision, facts.Status == ZmkModuleStatus.NeedsFork, newConfigId);
    }

    /// <summary>
    /// The .conf the options go into: the config's only one, else
    /// <c>config/&lt;base&gt;.conf</c> from the central's first non-adapter
    /// shield (else its board) minus one side suffix.
    /// </summary>
    private static string ConfPath(ZmkConfigFacts facts, ZmkBuildEntry central)
    {
        if (facts.ConfPaths.Count == 1)
            return facts.ConfPaths[0];

        var baseName = central.Shields
            .FirstOrDefault(s => !string.Equals(s, RawHidAdapterShield, StringComparison.Ordinal))
            ?? central.Board;
        foreach (var suffix in SideSuffixes)
            if (baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return $"config/{baseName[..^suffix.Length]}.conf";
        return $"config/{baseName}.conf";
    }

    /// <summary>
    /// The Kconfig options in the wizard's fixed order; an unchosen feature
    /// is not passed at all, so its existing line (if any) stays as it is.
    /// </summary>
    private static List<KeyValuePair<string, string>> Settings(
        ZmkConfigFacts facts, ZmkModuleChoices choices, string? newConfigId)
    {
        var settings = new List<KeyValuePair<string, string>>
        {
            new("CONFIG_RAW_HID", "y"),
            new("CONFIG_HID_VIZ", "y"),
        };
        if (choices.Rgb && facts.HasUnderglow)
            settings.Add(new KeyValuePair<string, string>("CONFIG_HID_VIZ_RGB", "y"));
        if (choices.Signal)
            settings.Add(new KeyValuePair<string, string>("CONFIG_HID_VIZ_SIGNAL", "y"));
        if (choices.Emit)
            settings.Add(new KeyValuePair<string, string>("CONFIG_HID_VIZ_EMIT", "y"));
        if (newConfigId is not null)
            settings.Add(new KeyValuePair<string, string>("CONFIG_HID_VIZ_CONFIG_ID", $"\"{newConfigId}\""));
        return settings;
    }

    private static void AddIfChanged(
        List<ZmkConfigFileChange> changes, string path, string? oldText, string newText)
    {
        if (oldText is null || !string.Equals(oldText, newText, StringComparison.Ordinal))
            changes.Add(new ZmkConfigFileChange(path, oldText, newText));
    }

    /// <summary>
    /// Re-inspects the config as it would be after saving every change and
    /// refuses anything but a clean <see cref="ZmkModuleStatus.SetUp"/>.
    /// </summary>
    private static void Guard(ZmkConfigFileSet files, IReadOnlyList<ZmkConfigFileChange> changes)
    {
        var patched = files;
        foreach (var change in changes)
            patched = patched.With(change.RelativePath, change.NewText);

        var after = ZmkConfigInspector.Inspect(patched);
        if (after.Status == ZmkModuleStatus.SetUp && after.Blockers.Count == 0)
            return;
        throw new InvalidOperationException(
            after.Blockers.Count > 0
                ? $"the patched zmk-config is {after.Status} with blockers {string.Join(", ", after.Blockers)}"
                : $"the patched zmk-config is {after.Status}");
    }
}
