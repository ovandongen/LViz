using LViz.Core.Layout.Dtsi;
using LViz.Core.Layout.Import;
using Xunit;

namespace LViz.Tests.Layout;

/// <summary>
/// Tests for <see cref="MatrixTransformImporter"/> (layout
/// wizard importers): rough layout from <c>zmk,matrix-transform</c> nodes.
/// Text layout when the map spans several lines, RC wiring grid when it is
/// on one line. Real-world fixtures: slabwing.overlay and cosmos82.dtsi.
/// </summary>
public class MatrixTransformImporterTests
{
    // ── real fixtures, visual mode ──────────────────────────────────────

    [Fact]
    public void Slabwing_ImportsOneVisualLayout()
    {
        var imported = MatrixTransformImporter.Import(LayoutFixtures.Read("slabwing.overlay"));

        var layout = Assert.Single(imported);
        Assert.Equal("keymap_transform_0", layout.Name);
        Assert.Equal(LayoutSource.MatrixTransform, layout.Source);
        Assert.Equal(56, layout.Keys.Count);
        Assert.Equal([new LayoutImportWarning(LayoutWarningCode.MatrixTransformRough)], layout.Warnings);
    }

    [Fact]
    public void Slabwing_XValuesPerVisualRow()
    {
        var layout = Assert.Single(MatrixTransformImporter.Import(
            LayoutFixtures.Read("slabwing.overlay")));

        AssertXPerRow(layout, new()
        {
            [0] = [100, 200, 300, 400, 500, 625, 725, 825, 925, 1025],
            [100] = [0, 100, 200, 300, 400, 500, 625, 725, 825, 925, 1025, 1150],
            [200] = [0, 100, 200, 300, 400, 500, 625, 725, 825, 925, 1025, 1150],
            [300] = [0, 100, 200, 300, 400, 500, 625, 725, 825, 925, 1025, 1150],
            [400] = [200, 300, 825, 925],
            [500] = [300, 400, 500, 625, 750, 850],
        });
    }

    [Fact]
    public void Cosmos82_ImportsOneVisualLayoutInEightRows()
    {
        var layout = Assert.Single(MatrixTransformImporter.Import(
            LayoutFixtures.Read("cosmos82.dtsi")));

        Assert.Equal("keymap_transform_0", layout.Name);
        Assert.Equal(LayoutSource.MatrixTransform, layout.Source);
        Assert.Equal(82, layout.Keys.Count);
        Assert.Equal([new LayoutImportWarning(LayoutWarningCode.MatrixTransformRough)], layout.Warnings);
        Assert.Equal(Enumerable.Range(0, 8).Select(r => r * 100),
            layout.Keys.Select(k => k.Y).Distinct());
    }

    [Fact]
    public void Cosmos82_XValuesPerVisualRow()
    {
        var layout = Assert.Single(MatrixTransformImporter.Import(
            LayoutFixtures.Read("cosmos82.dtsi")));

        var topRow = new List<int> { 0, 100, 200, 300, 400, 500, 600, 825, 925, 1025, 1125, 1225, 1325, 1425 };
        AssertXPerRow(layout, new()
        {
            [0] = topRow,
            [100] = topRow,
            [200] = topRow,
            [300] = topRow,
            [400] = [100, 200, 300, 400, 500, 600, 825, 925, 1025, 1125, 1225, 1325],
            [500] = [300, 400, 1025, 1125],
            [600] = [200, 300, 400, 925, 1025, 1125],
            [700] = [300, 400, 925, 1025],
        });
    }

    [Fact]
    public void Cosmos82_IgnoresPhysicalLayoutAndKscanNodes()
    {
        var raw = LayoutFixtures.Read("cosmos82.dtsi");
        // The file also carries a physical layout (79-wide caps) and kscan
        // nodes; this importer reads only the matrix transform.
        Assert.Contains("zmk,physical-layout", raw);
        Assert.Contains("zmk,kscan-gpio-matrix", raw);

        var layout = Assert.Single(MatrixTransformImporter.Import(raw));
        Assert.All(layout.Keys, k => Assert.Equal(100, k.Width));
    }

    // ── inline, grid mode ───────────────────────────────────────────────

    [Fact]
    public void OneLineMap_UsesTheRcWiringGrid()
    {
        var layout = Assert.Single(MatrixTransformImporter.Import(InlineMap(
            "map = < RC(0,0) RC(0,1) RC(1,0) RC(1,1) >;")));

        Assert.Equal(LayoutSource.MatrixTransform, layout.Source);
        Assert.Equal([new LayoutImportWarning(LayoutWarningCode.MatrixTransformGrid)], layout.Warnings);
        Assert.Equal(
            [
                new DtsiKeyPhysical(100, 100, 0, 0, 0, 0, 0),
                new DtsiKeyPhysical(100, 100, 100, 0, 0, 0, 0),
                new DtsiKeyPhysical(100, 100, 0, 100, 0, 0, 0),
                new DtsiKeyPhysical(100, 100, 100, 100, 0, 0, 0),
            ],
            layout.Keys);
    }

    [Fact]
    public void RcTokens_AreWhitespaceTolerant()
    {
        var layout = Assert.Single(MatrixTransformImporter.Import(InlineMap(
            "map = < RC(0, 0) RC( 0 , 1 ) >;")));

        Assert.Equal([0, 100], layout.Keys.Select(k => k.X));
    }

    // ── inline, tab handling ────────────────────────────────────────────

    [Fact]
    public void TabAndFourSpaces_IndentEqually()
    {
        // Visual mode (two token lines); one tab and four spaces must give
        // both rows the same column for their first key.
        var map = "\tRC(0,0) RC(0,1)\n    RC(1,0) RC(1,1)";

        var layout = Assert.Single(MatrixTransformImporter.Import(InlineMap(
            "map = <\n" + map + "\n>;")));

        Assert.Equal([new LayoutImportWarning(LayoutWarningCode.MatrixTransformRough)], layout.Warnings);
        Assert.Equal([0, 100, 0, 100], layout.Keys.Select(k => k.X));
        Assert.Equal([0, 0, 100, 100], layout.Keys.Select(k => k.Y));
    }

    // ── refusals ────────────────────────────────────────────────────────

    [Fact]
    public void UnknownTextInMap_ThrowsWithLine()
    {
        var ex = Assert.Throws<LayoutImportException>(() => MatrixTransformImporter.Import(
            InlineMap("map = <\n        RC(0,0) FOO(1)\n>;")));

        Assert.Contains("FOO(1)", ex.Message);
        Assert.Contains("keymap_transform_0", ex.Message);
        Assert.Contains("line 5", ex.Message);
    }

    [Fact]
    public void MissingMap_ThrowsWithNodeAndLine()
    {
        var text = """
            / {
                default_transform: keymap_transform_0 {
                    compatible = "zmk,matrix-transform";
                    columns = <2>;
                };
            };
            """;
        var ex = Assert.Throws<LayoutImportException>(
            () => MatrixTransformImporter.Import(text));

        Assert.Contains("keymap_transform_0", ex.Message);
        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public void EmptyMap_ThrowsWithLine()
    {
        var ex = Assert.Throws<LayoutImportException>(
            () => MatrixTransformImporter.Import(InlineMap("map = <>;")));

        Assert.Contains("keymap_transform_0", ex.Message);
        Assert.Contains("line 4", ex.Message);
    }

    // ── no matrix transform ─────────────────────────────────────────────

    [Fact]
    public void FileWithoutMatrixTransform_ImportsEmptyList()
    {
        var text = """
            / {
                kscan0: kscan {
                    compatible = "zmk,kscan-gpio-matrix";
                };
            };
            """;
        Assert.Empty(MatrixTransformImporter.Import(text));
    }

    // ── helpers ─────────────────────────────────────────────────────────

    /// <summary>Wraps a map property in a single matrix-transform node.
    /// The node opens on line 2, the map argument starts on line 4.</summary>
    private static string InlineMap(string mapLine) =>
        "/ {\n"
        + "    default_transform: keymap_transform_0 {\n"
        + "        compatible = \"zmk,matrix-transform\";\n"
        + "        " + mapLine + "\n"
        + "    };\n"
        + "};\n";

    private static void AssertXPerRow(
        ImportedLayout layout, Dictionary<int, List<int>> expectedByY)
    {
        var rows = layout.Keys.GroupBy(k => k.Y).OrderBy(g => g.Key).ToList();
        Assert.Equal(expectedByY.Keys.OrderBy(y => y), rows.Select(g => g.Key));
        foreach (var row in rows)
            Assert.Equal(expectedByY[row.Key], row.Select(k => k.X).ToList());
    }
}
