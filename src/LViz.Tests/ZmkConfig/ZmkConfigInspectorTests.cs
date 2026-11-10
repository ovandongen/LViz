using LViz.Core.ZmkConfig;
using Xunit;

namespace LViz.Tests.ZmkConfig;

/// <summary>
/// The batch 024 inspector, run against the real zmk-config fixtures: where
/// ZMK comes from, whether the config is split and which build entry is the
/// central, whether the raw-hid modules are set up, and what blocks setup.
/// </summary>
public class ZmkConfigInspectorTests
{
    private static ZmkConfigFacts Inspect(string name) =>
        ZmkConfigInspector.Inspect(ZmkConfigFixtures.Root(name));

    [Fact]
    public void Cosmos82_StockSplit_HasItsCentralInItsOwnKconfig()
    {
        var facts = Inspect("cosmos82");

        Assert.Equal("build.yaml", facts.BuildMatrixPath);
        Assert.Equal("zmkfirmware", facts.ZmkOwner);
        Assert.Equal("v0.3", facts.ZmkRevision); // the zmk project has none, the manifest default answers

        Assert.Equal([0, 1], facts.Entries.Select(e => e.Index)); // settings_reset is not a face of the board
        Assert.True(facts.IsSplit);
        // The right half is the central: its Kconfig.defconfig sets ZMK_SPLIT_ROLE_CENTRAL.
        Assert.Equal(1, facts.CentralIndex);
        Assert.Equal(ZmkCentralReason.RepoKconfig, facts.CentralReason);
        Assert.Contains("cosmos82_right", facts.Entries[1].Shields);

        Assert.False(facts.HasUnderglow);
        Assert.Null(facts.ConfigId);
        Assert.Null(facts.RawHidOwner);
        Assert.Equal(ZmkModuleStatus.NotSetUp, facts.Status);

        Assert.Equal(["config/cosmos82.conf"], facts.ConfPaths);
        Assert.Empty(facts.Blockers);
        Assert.Null(facts.UnreadableDetail);
    }

    [Fact]
    public void Slabwing_Unibody_ItsOnlyEntryIsTheCentral()
    {
        var facts = Inspect("slabwing");

        Assert.Equal("zmkfirmware", facts.ZmkOwner);
        Assert.Equal("v0.3", facts.ZmkRevision);
        Assert.Single(facts.Entries);
        Assert.False(facts.IsSplit);
        Assert.Equal(0, facts.CentralIndex);
        Assert.Equal(ZmkCentralReason.Unibody, facts.CentralReason);
        Assert.Equal(ZmkModuleStatus.NotSetUp, facts.Status);
        Assert.Equal(["config/slabwing.conf"], facts.ConfPaths);
    }

    [Fact]
    public void Glove80_SetUpWithTheStableFork_LeftNamingNamesItsCentral()
    {
        var facts = Inspect("glove80");

        Assert.Equal("moergo-sc", facts.ZmkOwner);
        Assert.Equal("main", facts.ZmkRevision);
        Assert.Equal(0, facts.CentralIndex); // glove80_lh; the repo has no shield Kconfig
        Assert.Equal(ZmkCentralReason.LeftNaming, facts.CentralReason);
        Assert.True(facts.HasUnderglow);
        Assert.Equal("123e4567-e89b-12d3-a456-426614174000", facts.ConfigId);
        Assert.Equal("ovandongen", facts.RawHidOwner);
        Assert.Equal(ZmkModuleStatus.SetUp, facts.Status);
    }

    [Fact]
    public void Go60_SetUp_CarriesItsConfigId()
    {
        var facts = Inspect("go60");

        Assert.Equal(ZmkModuleStatus.SetUp, facts.Status);
        Assert.Equal("999e4567-e89b-12d3-a456-426614174000", facts.ConfigId);
    }

    [Fact]
    public void Zzeneg_SetUpWithTheOtherFork_NeedsFork()
    {
        var facts = Inspect("zzeneg");

        Assert.Equal(0, facts.CentralIndex); // corne_left
        Assert.Equal(ZmkCentralReason.LeftNaming, facts.CentralReason);
        Assert.Equal("zzeneg", facts.RawHidOwner);
        Assert.Equal(ZmkModuleStatus.NeedsFork, facts.Status);
    }

    [Fact]
    public void Dongle_IsBlocked_AndKeepsItsThreeRealEntries()
    {
        var facts = Inspect("dongle");

        Assert.Equal([ZmkConfigBlocker.Dongle], facts.Blockers);
        Assert.Equal([0, 1, 2], facts.Entries.Select(e => e.Index)); // settings_reset excluded
        Assert.All(facts.Entries, e => Assert.DoesNotContain("settings_reset", e.Shields));
        Assert.Null(facts.CentralIndex); // 3 entries fit neither the unibody nor the split rule
        Assert.Equal(ZmkCentralReason.Unknown, facts.CentralReason);
    }

    [Fact]
    public void With_BrokenWest_IsUnreadableAndNamesTheFile()
    {
        var files = ZmkConfigFileSet.Read(ZmkConfigFixtures.Root("cosmos82"))
            .With("config/west.yml", "remotes:\n  - name: zmkfirmware\n");

        var facts = ZmkConfigInspector.Inspect(files);

        Assert.Equal([ZmkConfigBlocker.Unreadable], facts.Blockers);
        Assert.NotNull(facts.UnreadableDetail);
        Assert.Contains("config/west.yml", facts.UnreadableDetail);
    }

    [Fact]
    public void MissingBuildMatrix_IsTheOnlyBlocker_WhileTheWestFactsStillRead()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lviz-zmkconfig-matrix-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(
                Path.Combine(root, "config", "west.yml"),
                ZmkConfigFixtures.Read("cosmos82", "config/west.yml"));

            var facts = ZmkConfigInspector.Inspect(root);

            Assert.Equal([ZmkConfigBlocker.NoBuildMatrix], facts.Blockers); // not NoBuildEntries: nothing was parsed
            Assert.Null(facts.BuildMatrixPath);
            Assert.Empty(facts.Entries);
            Assert.Empty(facts.ConfPaths);
            Assert.Equal("zmkfirmware", facts.ZmkOwner);
            Assert.Equal("v0.3", facts.ZmkRevision);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Locator_FindsRootFromAKeymapPath()
    {
        var copy = ZmkConfigFixtures.CopyToTemp("cosmos82");
        try
        {
            // config/cosmos82.keymap is not in the fixture: the file itself need not exist.
            Assert.Equal(copy, ZmkConfigLocator.FindRoot(Path.Combine(copy, "config", "cosmos82.keymap")));
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    [Fact]
    public void Locator_GivesUpWithoutAWestManifestAbove()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lviz-zmkconfig-noroot-{Guid.NewGuid():N}");
        var keymaps = Path.Combine(root, "keymaps");
        Directory.CreateDirectory(keymaps);
        try
        {
            Assert.Null(ZmkConfigLocator.FindRoot(Path.Combine(keymaps, "anything.keymap")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
