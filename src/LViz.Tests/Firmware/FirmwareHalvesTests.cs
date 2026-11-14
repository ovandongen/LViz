using LViz.Core.Firmware;
using LViz.Core.ZmkConfig;
using LViz.Tests.ZmkConfig;
using Xunit;

namespace LViz.Tests.Firmware;

/// <summary>
/// The halves a flashed config offers, over the real zmk-config fixtures and
/// inline build matrices: which entry is the central (the adapter shield
/// wins over the inspector's guess), each half's side, and the <c>.uf2</c>
/// name the stock ZMK workflow produces for it.
/// </summary>
public class FirmwareHalvesTests
{
    /// <summary>A west.yml that passes the inspector's <c>zmk</c> project rule.</summary>
    private const string MinimalWest =
        "manifest:\n" +
        "  projects:\n" +
        "    - name: zmk\n" +
        "      url: https://github.com/zmkfirmware/zmk\n";

    private static ZmkConfigFacts InspectFixture(string name) =>
        ZmkConfigInspector.Inspect(ZmkConfigFixtures.Root(name));

    private static ZmkConfigFacts InspectInline(string buildMatrix) =>
        ZmkConfigInspector.Inspect(new ZmkConfigFileSet("cfg", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["config/west.yml"] = MinimalWest,
            ["build.yaml"] = buildMatrix,
        }));

    private static ZmkBuildEntry Entry(string includeBody) =>
        ZmkBuildMatrixText.Parse("include:\n" + includeBody).Entries[0];

    /// <summary>A half flattened for comparison (the record's Shields list has reference equality).</summary>
    private static (FirmwareHalfRole Role, int EntryIndex, string Board, string Shields, FirmwareSide Side, string FileName)
        Shape(FirmwareHalf half) =>
        (half.Role, half.EntryIndex, half.Board, string.Join(" ", half.Shields), half.Side, half.FileName);

    [Fact]
    public void Cosmos82_NotSetUp_CentralIsTheKconfigRightHalf()
    {
        var facts = InspectFixture("cosmos82");
        Assert.Equal(1, facts.CentralIndex); // Kconfig.defconfig, and no adapter shield anywhere

        Assert.Equal(
        [
            (FirmwareHalfRole.Central, 1, "nice_nano_v2", "cosmos82_right", FirmwareSide.Right, "cosmos82_right-nice_nano_v2-zmk.uf2"),
            (FirmwareHalfRole.Peripheral, 0, "nice_nano_v2", "cosmos82_left", FirmwareSide.Left, "cosmos82_left-nice_nano_v2-zmk.uf2"),
        ],
        FirmwareHalves.For(facts).Select(Shape).ToList());
    }

    [Fact]
    public void Glove80_AdapterShieldNamesTheLeftCentral()
    {
        Assert.Equal(
        [
            (FirmwareHalfRole.Central, 0, "glove80_lh", "raw_hid_adapter", FirmwareSide.Left, "raw_hid_adapter-glove80_lh-zmk.uf2"),
            (FirmwareHalfRole.Peripheral, 1, "glove80_rh", "", FirmwareSide.Right, "glove80_rh-zmk.uf2"),
        ],
        FirmwareHalves.For(InspectFixture("glove80")).Select(Shape).ToList());
    }

    [Fact]
    public void Go60_AdapterShieldNamesTheLeftCentral()
    {
        Assert.Equal(
        [
            (FirmwareHalfRole.Central, 0, "go60_lh", "raw_hid_adapter", FirmwareSide.Left, "raw_hid_adapter-go60_lh-zmk.uf2"),
            (FirmwareHalfRole.Peripheral, 1, "go60_rh", "", FirmwareSide.Right, "go60_rh-zmk.uf2"),
        ],
        FirmwareHalves.For(InspectFixture("go60")).Select(Shape).ToList());
    }

    [Fact]
    public void Zzeneg_AdapterSharesTheShieldLine_SpacesSurviveInTheFileName()
    {
        Assert.Equal(
        [
            (FirmwareHalfRole.Central, 0, "nice_nano_v2", "corne_left raw_hid_adapter", FirmwareSide.Left, "corne_left raw_hid_adapter-nice_nano_v2-zmk.uf2"),
            (FirmwareHalfRole.Peripheral, 1, "nice_nano_v2", "corne_right", FirmwareSide.Right, "corne_right-nice_nano_v2-zmk.uf2"),
        ],
        FirmwareHalves.For(InspectFixture("zzeneg")).Select(Shape).ToList());
    }

    [Fact]
    public void Slabwing_Unibody_OffersOnlyTheCentral()
    {
        Assert.Equal(
        [
            (FirmwareHalfRole.Central, 0, "nice_nano_v2", "slabwing nice_view_adapter nice_view", FirmwareSide.Unknown, "slabwing nice_view_adapter nice_view-nice_nano_v2-zmk.uf2"),
        ],
        FirmwareHalves.For(InspectFixture("slabwing")).Select(Shape).ToList());
    }

    [Fact]
    public void Dongle_Blocked_OffersNothing()
    {
        var facts = InspectFixture("dongle");
        Assert.Contains(ZmkConfigBlocker.Dongle, facts.Blockers);

        Assert.Empty(FirmwareHalves.For(facts));
    }

    [Fact]
    public void ExpectedFileName_Hwmv2BoardWithShield_SlashesBecomeUnderscores() =>
        Assert.Equal("corne_left-nice_nano__zmk-zmk.uf2",
            FirmwareHalves.ExpectedFileName(Entry("  - board: nice_nano//zmk\n    shield: corne_left\n")));

    [Fact]
    public void ExpectedFileName_Hwmv2BoardWithoutShield_IsBoardAndSuffix() =>
        Assert.Equal("xiao_ble__zmk-zmk.uf2",
            FirmwareHalves.ExpectedFileName(Entry("  - board: xiao_ble//zmk\n")));

    [Fact]
    public void ExpectedFileName_ArtifactName_IsThatNameAlone() =>
        Assert.Equal("my-left.uf2",
            FirmwareHalves.ExpectedFileName(
                Entry("  - board: nice_nano_v2\n    shield: corne_left\n    artifact-name: my-left\n")));

    [Fact]
    public void ExpectedFileName_BlankArtifactName_FallsBackToBoardAndShield() =>
        Assert.Equal("corne_left-nice_nano_v2-zmk.uf2",
            FirmwareHalves.ExpectedFileName(
                Entry("  - board: nice_nano_v2\n    shield: corne_left\n    artifact-name: \"   \"\n")));

    [Fact]
    public void ExpectedFileName_PaddedArtifactName_IsTrimmed() =>
        Assert.Equal("my-left.uf2",
            FirmwareHalves.ExpectedFileName(
                Entry("  - board: nice_nano_v2\n    artifact-name: \"  my-left  \"\n")));

    [Fact]
    public void For_UnreadableManifest_EmptyEvenThoughTheHalvesAreNamed()
    {
        var facts = ZmkConfigInspector.Inspect(new ZmkConfigFileSet("cfg",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["config/west.yml"] = "this is not a manifest\n",
                ["build.yaml"] =
                    "include:\n" +
                    "  - board: nice_nano_v2\n    shield: corne_left\n" +
                    "  - board: nice_nano_v2\n    shield: corne_right\n",
            }));
        Assert.Contains(ZmkConfigBlocker.Unreadable, facts.Blockers);
        Assert.NotNull(facts.CentralIndex); // the matrix alone would look flashable

        Assert.Empty(FirmwareHalves.For(facts));
    }

    [Fact]
    public void For_CentralIndexNotInEntries_Empty()
    {
        var entries = ZmkBuildMatrixText.Parse(
            "include:\n" +
            "  - board: nice_nano_v2\n    shield: corne_left\n" +
            "  - board: nice_nano_v2\n    shield: corne_right\n").Entries;
        var facts = new ZmkConfigFacts(
            "cfg", "build.yaml", [], "zmkfirmware", "v0.3", entries, true, 5,
            ZmkCentralReason.LeftNaming, false, null, null, ZmkModuleStatus.NotSetUp, [], null,
            new ZmkLiveFeatures(false, false, false, false));

        Assert.Empty(FirmwareHalves.For(facts));
    }

    [Fact]
    public void For_RightHalfCarriesTheAdapter_WinsOverLeftNaming()
    {
        var facts = InspectInline(
            "include:\n" +
            "  - board: nice_nano_v2\n    shield: corne_left\n" +
            "  - board: nice_nano_v2\n    shield: corne_right raw_hid_adapter\n");
        Assert.Equal(0, facts.CentralIndex); // the inspector guesses the left-named half
        Assert.Empty(facts.Blockers);

        Assert.Equal(
        [
            (FirmwareHalfRole.Central, 1, "nice_nano_v2", "corne_right raw_hid_adapter", FirmwareSide.Right, "corne_right raw_hid_adapter-nice_nano_v2-zmk.uf2"),
            (FirmwareHalfRole.Peripheral, 0, "nice_nano_v2", "corne_left", FirmwareSide.Left, "corne_left-nice_nano_v2-zmk.uf2"),
        ],
        FirmwareHalves.For(facts).Select(Shape).ToList());
    }

    [Fact]
    public void For_BothHalvesCarryTheAdapter_FallsBackToCentralIndex()
    {
        var facts = InspectInline(
            "include:\n" +
            "  - board: nice_nano_v2\n    shield: raw_hid_adapter alpha\n" +
            "  - board: nice_nano_v2\n    shield: corne_left raw_hid_adapter\n");
        // Two adapter hits are not decisive: the left-named half (entry 1)
        // wins even though the first hit is entry 0.
        Assert.Equal(1, facts.CentralIndex);

        Assert.Equal(
        [
            (FirmwareHalfRole.Central, 1, "nice_nano_v2", "corne_left raw_hid_adapter", FirmwareSide.Left, "corne_left raw_hid_adapter-nice_nano_v2-zmk.uf2"),
            (FirmwareHalfRole.Peripheral, 0, "nice_nano_v2", "raw_hid_adapter alpha", FirmwareSide.Unknown, "raw_hid_adapter alpha-nice_nano_v2-zmk.uf2"),
        ],
        FirmwareHalves.For(facts).Select(Shape).ToList());
    }

    [Fact]
    public void For_SettingsResetEntryFirst_MatrixIndexesSurviveTheFilter()
    {
        var facts = InspectInline(
            "include:\n" +
            "  - board: nice_nano_v2\n    shield: settings_reset\n" +
            "  - board: nice_nano_v2\n    shield: corne_left\n" +
            "  - board: nice_nano_v2\n    shield: corne_right raw_hid_adapter\n");
        Assert.Equal([1, 2], facts.Entries.Select(e => e.Index));
        Assert.Empty(facts.Blockers);

        Assert.Equal(
        [
            (FirmwareHalfRole.Central, 2, "nice_nano_v2", "corne_right raw_hid_adapter", FirmwareSide.Right, "corne_right raw_hid_adapter-nice_nano_v2-zmk.uf2"),
            (FirmwareHalfRole.Peripheral, 1, "nice_nano_v2", "corne_left", FirmwareSide.Left, "corne_left-nice_nano_v2-zmk.uf2"),
        ],
        FirmwareHalves.For(facts).Select(Shape).ToList());
    }

    [Fact]
    public void For_NothingNamesTheCentral_Empty()
    {
        var facts = InspectInline(
            "include:\n" +
            "  - board: nice_nano_v2\n    shield: alpha\n" +
            "  - board: nice_nano_v2\n    shield: beta\n");
        Assert.Null(facts.CentralIndex);
        Assert.Empty(facts.Blockers);

        Assert.Empty(FirmwareHalves.For(facts));
    }

    [Fact]
    public void For_NoEntries_Empty()
    {
        var facts = new ZmkConfigFacts(
            "cfg", "build.yaml", [], "zmkfirmware", "v0.3", [], false, null,
            ZmkCentralReason.Unknown, false, null, null, ZmkModuleStatus.NotSetUp, [], null,
            new ZmkLiveFeatures(false, false, false, false));

        Assert.Empty(FirmwareHalves.For(facts));
    }

    [Theory]
    [InlineData("nice_nano_v2", "corne_left", FirmwareSide.Left)]
    [InlineData("nice_nano_v2", "corne_LH", FirmwareSide.Left)]
    [InlineData("nice_nano_v2", "corne_right", FirmwareSide.Right)]
    [InlineData("nice_nano_v2", "corne_RH", FirmwareSide.Right)]
    [InlineData("glove80_lh", "raw_hid_adapter", FirmwareSide.Left)]
    [InlineData("glove80_rh", "raw_hid_adapter", FirmwareSide.Right)]
    [InlineData("nice_nano_v2", "nice_view nice_view_adapter", FirmwareSide.Unknown)]
    [InlineData("nice_nano_v2", "base_lh top_rh", FirmwareSide.Unknown)]
    public void SideOf_BoardAndShieldNames(string board, string shield, FirmwareSide expected) =>
        Assert.Equal(expected, FirmwareHalves.SideOf(Entry($"  - board: {board}\n    shield: {shield}\n")));
}
