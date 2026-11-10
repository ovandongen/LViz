using LViz.Core.ZmkConfig;
using Xunit;

namespace LViz.Tests.ZmkConfig;

/// <summary>
/// The batch 024 text readers, run against the real zmk-config fixtures:
/// west.yml, build.yaml and .conf are parsed as text, keeping comments and
/// layout for the writers that come in later cards.
/// </summary>
public class ZmkConfigTextTests
{
    private static ZmkWestManifest West(string name) =>
        ZmkWestManifestText.Parse(ZmkConfigFixtures.Read(name, "config/west.yml"));

    private static ZmkBuildMatrix Matrix(string name) =>
        ZmkBuildMatrixText.Parse(ZmkConfigFixtures.Read(name, "build.yaml"));

    [Fact]
    public void Cosmos82West_ReadsDefaultsAndRemotes()
    {
        var west = West("cosmos82");

        Assert.Equal("v0.3", west.DefaultRevision);
        Assert.Null(west.DefaultRemote); // cosmos82's defaults give only a revision
        Assert.Equal(["zmkfirmware", "badjeff"], west.Remotes.Select(r => r.Values["name"]));
        Assert.Equal(3, west.RemotesKeyLine);
        Assert.Equal(10, west.ProjectsKeyLine);

        // The comment lines after the badjeff remote are not part of the item.
        var badjeff = west.Remotes[1];
        Assert.Equal(6, badjeff.StartLine);
        Assert.Equal(7, badjeff.EndLine);
        Assert.Equal(4, badjeff.DashIndent);
        Assert.Equal(6, badjeff.KeyIndent);
        Assert.Equal("https://github.com/badjeff", badjeff.Values["url-base"]);
        Assert.Equal(7, badjeff.KeyLines["url-base"]);
    }

    [Fact]
    public void Cosmos82West_ReadsProjectsAndOwner()
    {
        var west = West("cosmos82");

        Assert.Equal(["zmk", "zmk-pmw3610-driver"], west.Projects.Select(p => p.Values["name"]));
        var pmw = west.Project("zmk-pmw3610-driver")!;
        Assert.Equal("zmk-0.3", pmw.Values["revision"]);
        Assert.Equal(19, pmw.EndLine);
        Assert.Equal("zmkfirmware", west.OwnerOf(west.Project("zmk")!));
        Assert.Equal("badjeff", west.OwnerOf(pmw));
        Assert.Null(west.Project("zmk-raw-hid"));
    }

    [Fact]
    public void Glove80West_HasNoDefaultsAndReadsModuleOwners()
    {
        var west = West("glove80");

        Assert.Null(west.DefaultRevision);
        Assert.Null(west.DefaultRemote);
        Assert.Equal("moergo-sc", west.OwnerOf(west.Project("zmk")!));
        Assert.Equal("ovandongen", west.OwnerOf(west.Project("zmk-raw-hid")!));
        Assert.Equal("ovandongen", west.OwnerOf(west.Project("zmk-hid-viz")!));
    }

    [Fact]
    public void ZzenegWest_ModuleOwnerIsZzeneg()
    {
        var west = West("zzeneg");

        Assert.Equal("zzeneg", west.OwnerOf(west.Project("zmk-raw-hid")!));
    }

    [Fact]
    public void Go60West_WithoutTrailingNewline_ParsesProjects()
    {
        var text = ZmkConfigFixtures.Read("go60", "config/west.yml");
        Assert.False(text.EndsWith('\n'));

        var west = ZmkWestManifestText.Parse(text);

        Assert.Equal(["zmk", "zmk-raw-hid", "zmk-hid-viz"], west.Projects.Select(p => p.Values["name"]));
    }

    [Fact]
    public void WestProject_WithDirectUrl_OwnerComesFromUrl()
    {
        var west = ZmkWestManifestText.Parse(
            "manifest:\n  projects:\n    - name: zmk-raw-hid\n      url: https://github.com/someone/zmk-raw-hid\n");

        Assert.Equal("someone", west.OwnerOf(west.Project("zmk-raw-hid")!));
    }

    [Fact]
    public void WestQuotedValues_AreUnquoted()
    {
        var west = ZmkWestManifestText.Parse(
            "manifest:\n  defaults:\n    revision: 'v0.3'\n  remotes:\n    - name: \"someone\"\n      url-base: 'https://github.com/someone'\n");

        Assert.Equal("v0.3", west.DefaultRevision);
        Assert.Equal("someone", west.Remotes[0].Values["name"]);
        Assert.Equal("https://github.com/someone", west.Remotes[0].Values["url-base"]);
    }

    [Fact]
    public void WestProject_WithoutRemote_FallsBackToTheDefaultRemote()
    {
        var west = ZmkWestManifestText.Parse(
            "manifest:\n  defaults:\n    remote: someone\n  remotes:\n    - name: someone\n      url-base: https://github.com/someone\n  projects:\n    - name: zmk-raw-hid\n");

        Assert.Equal("someone", west.OwnerOf(west.Project("zmk-raw-hid")!));
    }

    [Fact]
    public void WestFlowStyle_ThrowsOnItsLine()
    {
        var ex = Assert.Throws<ZmkConfigFormatException>(
            () => ZmkWestManifestText.Parse("manifest:\n  projects: [ {name: zmk} ]\n"));

        Assert.Equal("config/west.yml", ex.File);
        Assert.Equal(2, ex.Line);
        Assert.Contains("flow style is not supported", ex.Message);
    }

    [Fact]
    public void WestWithoutManifest_Throws()
    {
        var ex = Assert.Throws<ZmkConfigFormatException>(
            () => ZmkWestManifestText.Parse("# what is this\nremotes: []\n"));

        Assert.Contains("no manifest", ex.Message);
    }

    [Fact]
    public void Cosmos82BuildMatrix_ReadsEntriesAndShields()
    {
        var matrix = Matrix("cosmos82");

        Assert.Equal([0, 1, 2], matrix.Entries.Select(e => e.Index));
        Assert.Equal(["nice_nano_v2", "nice_nano_v2", "nice_nano_v2"], matrix.Entries.Select(e => e.Board));
        Assert.Equal(["cosmos82_left"], matrix.Entries[0].Shields);
        Assert.Equal(["cosmos82_right"], matrix.Entries[1].Shields); // inline comment stripped
        Assert.Equal(["settings_reset"], matrix.Entries[2].Shields);

        Assert.Equal(6, matrix.Entries[0].Item.StartLine);
        Assert.Equal(7, matrix.Entries[0].Item.EndLine);
        Assert.Equal("cosmos82_right", matrix.Entries[1].Item.Values["shield"]);
    }

    [Fact]
    public void SlabwingBuildMatrix_SplitsTheShieldLineOnWhitespace()
    {
        var matrix = Matrix("slabwing");

        Assert.Single(matrix.Entries);
        Assert.Equal(["slabwing", "nice_view_adapter", "nice_view"], matrix.Entries[0].Shields);
    }

    [Fact]
    public void Glove80BuildMatrix_EntryWithoutShieldHasNoShields()
    {
        var matrix = Matrix("glove80");

        Assert.Equal(["glove80_lh", "glove80_rh"], matrix.Entries.Select(e => e.Board));
        Assert.Equal(["raw_hid_adapter"], matrix.Entries[0].Shields);
        Assert.Equal("glove80_rh", matrix.Entries[1].Board);
        Assert.Empty(matrix.Entries[1].Shields);
    }

    [Fact]
    public void BuildMatrixWithoutInclude_Throws()
    {
        var ex = Assert.Throws<ZmkConfigFormatException>(
            () => ZmkBuildMatrixText.Parse("# nothing here\n"));

        Assert.Contains("no include list", ex.Message);
    }

    [Fact]
    public void BuildMatrixEntryWithoutBoard_ThrowsOnItsLine()
    {
        var ex = Assert.Throws<ZmkConfigFormatException>(
            () => ZmkBuildMatrixText.Parse("---\ninclude:\n  - shield: cosmos82_left\n"));

        Assert.Equal("build.yaml", ex.File);
        Assert.Equal(3, ex.Line);
        Assert.Contains("board", ex.Message);
    }

    [Fact]
    public void Cosmos82Conf_ReadsAssignmentsAndIgnoresComments()
    {
        var settings = ZmkConfText.Parse(ZmkConfigFixtures.Read("cosmos82", "config/cosmos82.conf"));

        Assert.Equal("y", ZmkConfText.Get(settings, "CONFIG_ZMK_POINTING"));
        Assert.Equal("900000", ZmkConfText.Get(settings, "CONFIG_ZMK_IDLE_SLEEP_TIMEOUT"));
        Assert.DoesNotContain("CONFIG_ZMK_USB_LOGGING", settings.Select(s => s.Key));
    }

    [Fact]
    public void Glove80Conf_KeepsQuotesOnValues()
    {
        var settings = ZmkConfText.Parse(ZmkConfigFixtures.Read("glove80", "config/glove80.conf"));

        Assert.Equal(
            "\"123e4567-e89b-12d3-a456-426614174000\"",
            ZmkConfText.Get(settings, "CONFIG_HID_VIZ_CONFIG_ID"));
    }

    [Fact]
    public void Conf_GetReturnsTheLastValueAndKeepsDuplicates()
    {
        var settings = ZmkConfText.Parse("A=1\n# A=commented\nA=2\n");

        Assert.Equal(["1", "2"], settings.Select(s => s.Value));
        Assert.Equal("2", ZmkConfText.Get(settings, "A"));
        Assert.Null(ZmkConfText.Get(settings, "B"));
    }

    [Fact]
    public void CrlfWest_ParsesLikeTheLfText()
    {
        var lf = West("cosmos82");
        var crlf = ZmkWestManifestText.Parse(
            ZmkConfigFixtures.Read("cosmos82", "config/west.yml").Replace("\n", "\r\n"));

        Assert.Equal(lf.DefaultRevision, crlf.DefaultRevision);
        Assert.Equal(
            lf.Remotes.Select(r => r.Values["name"]),
            crlf.Remotes.Select(r => r.Values["name"]));
        Assert.Equal(
            lf.Projects.Select(p => (p.Values["name"], p.Values.GetValueOrDefault("revision"))),
            crlf.Projects.Select(p => (p.Values["name"], p.Values.GetValueOrDefault("revision"))));
        Assert.Equal(
            lf.Remotes.Concat(lf.Projects).Select(i => i.EndLine),
            crlf.Remotes.Concat(crlf.Projects).Select(i => i.EndLine));
    }
}
