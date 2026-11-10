using LViz.Core.ZmkConfig;
using Xunit;

namespace LViz.Tests.ZmkConfig;

/// <summary>
/// <see cref="ZmkModuleSetupPlan.Build"/> run against the real zmk-config
/// fixtures: the plan is the exact text the wizard would save (west modules,
/// the central's shield, the .conf options), nothing more, and it only
/// survives its own re-inspect guard when the result is genuinely set up.
/// </summary>
public class ZmkModuleSetupPlanTests
{
    private const string TestGuidString = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private static Guid NewGuid() => Guid.Parse(TestGuidString);

    private static (ZmkConfigFileSet Files, ZmkConfigFacts Facts) Load(string name)
    {
        var files = ZmkConfigFileSet.Read(ZmkConfigFixtures.Root(name));
        return (files, ZmkConfigInspector.Inspect(files));
    }

    private static ZmkConfigFileChange Change(ZmkModuleSetupPlan plan, string path) =>
        plan.Changes.Single(c => c.RelativePath == path);

    [Fact]
    public void Cosmos82_SignalOnly_WritesWestBuildAndConf()
    {
        var (files, facts) = Load("cosmos82");

        var plan = ZmkModuleSetupPlan.Build(files, facts, new ZmkModuleChoices(1, false, true, false), NewGuid);

        Assert.Equal(
            ["config/west.yml", "build.yaml", "config/cosmos82.conf"],
            plan.Changes.Select(c => c.RelativePath));
        Assert.Equal(new ZmkModuleRevision("main", "main", false), plan.Revision);
        Assert.Equal(TestGuidString, plan.NewConfigId);
        Assert.False(plan.SwitchesToFork);

        var conf = Change(plan, "config/cosmos82.conf");
        Assert.False(conf.IsNew);
        Assert.EndsWith(
            "# LViz live features\nCONFIG_RAW_HID=y\nCONFIG_HID_VIZ=y\nCONFIG_HID_VIZ_SIGNAL=y\n" +
            $"CONFIG_HID_VIZ_CONFIG_ID=\"{TestGuidString}\"\n",
            conf.NewText);
        Assert.Contains("shield: cosmos82_right raw_hid_adapter", Change(plan, "build.yaml").NewText);
    }

    [Fact]
    public void Cosmos82_OtherHalfPicked_PutsTheShieldThere_AndPassesTheGuard()
    {
        // The Kconfig names the right half central; the user overrides it.
        var (files, facts) = Load("cosmos82");
        Assert.Equal(1, facts.CentralIndex);

        var plan = ZmkModuleSetupPlan.Build(files, facts, new ZmkModuleChoices(0, false, true, false), NewGuid);

        var build = Change(plan, "build.yaml").NewText;
        Assert.Contains("cosmos82_left raw_hid_adapter", build);
        Assert.DoesNotContain("cosmos82_right raw_hid_adapter", build);
    }

    [Fact]
    public void Cosmos82_RgbWithoutUnderglow_WritesNoRgbLine()
    {
        var (files, facts) = Load("cosmos82");

        var plan = ZmkModuleSetupPlan.Build(files, facts, new ZmkModuleChoices(1, true, false, false), NewGuid);

        Assert.DoesNotContain("CONFIG_HID_VIZ_RGB", Change(plan, "config/cosmos82.conf").NewText);
    }

    [Fact]
    public void Slabwing_FeaturesWithUnderglow_WritesRgbSignalEmitInOrder()
    {
        // With the whole conf replaced, its new text is exactly the LViz block.
        var files = ZmkConfigFileSet.Read(ZmkConfigFixtures.Root("slabwing"))
            .With("config/slabwing.conf", "CONFIG_ZMK_RGB_UNDERGLOW=y\n");
        var facts = ZmkConfigInspector.Inspect(files);
        Assert.True(facts.HasUnderglow);

        var plan = ZmkModuleSetupPlan.Build(files, facts, new ZmkModuleChoices(0, true, true, true), NewGuid);

        Assert.Equal(
            "CONFIG_ZMK_RGB_UNDERGLOW=y\n\n" +
            "# LViz live features\nCONFIG_RAW_HID=y\nCONFIG_HID_VIZ=y\nCONFIG_HID_VIZ_RGB=y\n" +
            "CONFIG_HID_VIZ_SIGNAL=y\nCONFIG_HID_VIZ_EMIT=y\n" +
            $"CONFIG_HID_VIZ_CONFIG_ID=\"{TestGuidString}\"\n",
            Change(plan, "config/slabwing.conf").NewText);
    }

    [Fact]
    public void Slabwing_Unibody_EndsConfAndAddsShield()
    {
        var (files, facts) = Load("slabwing");

        var plan = ZmkModuleSetupPlan.Build(files, facts, new ZmkModuleChoices(0, false, false, false), NewGuid);

        Assert.Equal(TestGuidString, Change(plan, "config/slabwing.conf").NewText.Split('"')[1]);
        Assert.EndsWith(
            "# LViz live features\nCONFIG_RAW_HID=y\nCONFIG_HID_VIZ=y\n" +
            $"CONFIG_HID_VIZ_CONFIG_ID=\"{TestGuidString}\"\n",
            Change(plan, "config/slabwing.conf").NewText);
        Assert.Contains("shield: slabwing nice_view_adapter nice_view raw_hid_adapter",
            Change(plan, "build.yaml").NewText);
    }

    [Fact]
    public void Zzeneg_SwitchesFork_AndLeavesBuildMatrixAlone()
    {
        var (files, facts) = Load("zzeneg");

        // The guard passing is implied: Build threw nothing on the re-inspect.
        var plan = ZmkModuleSetupPlan.Build(files, facts, new ZmkModuleChoices(0, false, false, false), NewGuid);

        // The shield is already there, so build.yaml is not a change.
        Assert.Equal(["config/west.yml", "config/corne.conf"], plan.Changes.Select(c => c.RelativePath));
        Assert.True(plan.SwitchesToFork);
    }

    [Fact]
    public void Glove80_AlreadySetUp_Refuses()
    {
        var (files, facts) = Load("glove80");
        Assert.Equal(ZmkModuleStatus.SetUp, facts.Status);

        Assert.Throws<ArgumentException>(
            () => ZmkModuleSetupPlan.Build(files, facts, new ZmkModuleChoices(0, false, false, false), NewGuid));
    }

    [Fact]
    public void Dongle_Blocked_Refuses()
    {
        var (files, facts) = Load("dongle");
        Assert.Contains(ZmkConfigBlocker.Dongle, facts.Blockers);

        Assert.Throws<ArgumentException>(
            () => ZmkModuleSetupPlan.Build(files, facts, new ZmkModuleChoices(0, false, false, false), NewGuid));
    }

    [Fact]
    public void Cosmos82_SettingsResetEntry_Refuses()
    {
        var (files, facts) = Load("cosmos82");

        // Index 2 is the settings_reset entry: filtered out of facts.Entries.
        Assert.Throws<ArgumentException>(
            () => ZmkModuleSetupPlan.Build(files, facts, new ZmkModuleChoices(2, false, false, false), NewGuid));
    }

    [Fact]
    public void TwoConfs_PicksCentralShieldNameMinusSide()
    {
        var files = ZmkConfigFileSet.Read(ZmkConfigFixtures.Root("cosmos82"))
            .With("config/cosmos82_right.conf", "CONFIG_ZMK_SLEEP=y\n");
        var facts = ZmkConfigInspector.Inspect(files);

        var plan = ZmkModuleSetupPlan.Build(files, facts, new ZmkModuleChoices(1, false, false, false), NewGuid);

        // The central's shield cosmos82_right, minus _right, is cosmos82.
        var conf = Change(plan, "config/cosmos82.conf");
        Assert.False(conf.IsNew);
        Assert.NotNull(conf.OldText);
    }

    [Fact]
    public void TwoConfs_SkipsTheAdapterShieldWhenNamingTheConf()
    {
        // The central carries [raw_hid_adapter, corne_left]: the adapter
        // never names the conf file, the first other shield (corne_left,
        // minus _left) does. The adapter is listed FIRST here so the skip
        // is the only way to reach corne.conf.
        var files = ZmkConfigFileSet.Read(ZmkConfigFixtures.Root("zzeneg"))
            .With("build.yaml",
                "---\ninclude:\n" +
                "  - board: nice_nano_v2\n    shield: raw_hid_adapter corne_left\n" +
                "  - board: nice_nano_v2\n    shield: corne_right\n")
            .With("config/aaa.conf", "CONFIG_ZMK_SLEEP=y\n");
        var facts = ZmkConfigInspector.Inspect(files);
        Assert.Equal(0, facts.CentralIndex); // corne_left names it

        var plan = ZmkModuleSetupPlan.Build(files, facts, new ZmkModuleChoices(0, false, false, false), NewGuid);

        var conf = Change(plan, "config/corne.conf");
        Assert.False(conf.IsNew);
    }

    [Fact]
    public void ExistingConfigId_KeepsIt_AndMintsNothing()
    {
        var files = ZmkConfigFileSet.Read(ZmkConfigFixtures.Root("zzeneg"))
            .With("config/corne.conf",
                "CONFIG_RAW_HID=y\nCONFIG_HID_VIZ=y\n" +
                "CONFIG_HID_VIZ_CONFIG_ID=\"123e4567-e89b-12d3-a456-426614174000\"\n");
        var facts = ZmkConfigInspector.Inspect(files);

        // The GUID source must not be consulted when the config has an id.
        var plan = ZmkModuleSetupPlan.Build(files, facts, new ZmkModuleChoices(0, false, false, false),
            () => throw new InvalidOperationException("no new GUID expected"));

        Assert.Null(plan.NewConfigId);
        // West switches fork; the conf already says everything.
        Assert.Equal(["config/west.yml"], plan.Changes.Select(c => c.RelativePath));
    }

    [Fact]
    public void LaterConfOverridesRawHid_GuardRefusesThePlan()
    {
        // The plan sets CONFIG_RAW_HID=y in cosmos82.conf, but the alphabetically
        // later zzz.conf still says n: the fresh inspect sees NotSetUp and refuses.
        var files = ZmkConfigFileSet.Read(ZmkConfigFixtures.Root("cosmos82"))
            .With("config/zzz.conf", "CONFIG_RAW_HID=n\n");
        var facts = ZmkConfigInspector.Inspect(files);
        Assert.Equal(ZmkModuleStatus.NotSetUp, facts.Status);

        var e = Assert.Throws<InvalidOperationException>(
            () => ZmkModuleSetupPlan.Build(files, facts, new ZmkModuleChoices(1, false, false, false), NewGuid));

        Assert.Contains("NotSetUp", e.Message);
    }

    [Fact]
    public void NoConf_CreatesConfigConf()
    {
        var loaded = ZmkConfigFileSet.Read(ZmkConfigFixtures.Root("cosmos82"));
        var files = new ZmkConfigFileSet(loaded.Root,
            loaded.Files
                .Where(kv => !kv.Key.EndsWith(".conf", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
        var facts = ZmkConfigInspector.Inspect(files);

        var plan = ZmkModuleSetupPlan.Build(files, facts, new ZmkModuleChoices(1, false, false, false), NewGuid);

        var conf = Change(plan, "config/cosmos82.conf");
        Assert.True(conf.IsNew);
        Assert.Equal(
            "# LViz live features\nCONFIG_RAW_HID=y\nCONFIG_HID_VIZ=y\n" +
            $"CONFIG_HID_VIZ_CONFIG_ID=\"{TestGuidString}\"\n",
            conf.NewText);
    }

    [Fact]
    public void For_MoergoMain_IsKnown_EverythingElse_IsNot()
    {
        Assert.True(ZmkModuleRevisions.For("moergo-sc", "main").Known);
        Assert.False(ZmkModuleRevisions.For("zmkfirmware", "v0.3").Known);
    }
}
