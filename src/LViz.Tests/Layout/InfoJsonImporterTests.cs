using LViz.Core.Layout.Dtsi;
using LViz.Core.Layout.Import;
using Xunit;

namespace LViz.Tests.Layout;

/// <summary>
/// Tests for <see cref="InfoJsonImporter"/> (part 2 of the layout wizard
/// importers): keymap-editor / QMK <c>info.json</c> files. Real-world
/// fixtures: slabwing, cosmos82 (cross-checked against its dtsi), go60;
/// naming, pivot, rounding and refusal rules on inline JSON.
/// </summary>
public class InfoJsonImporterTests
{
    // ── real fixtures ───────────────────────────────────────────────────

    [Fact]
    public void Slabwing_ImportsOneLayoutNamedFromTopLevelName()
    {
        var imported = InfoJsonImporter.Import(LayoutFixtures.Read("slabwing.info.json"));

        var layout = Assert.Single(imported);
        Assert.Equal("Slabwing", layout.Name);
        Assert.Equal(LayoutSource.InfoJson, layout.Source);
        Assert.Equal(56, layout.Keys.Count);
        Assert.Empty(layout.Warnings);

        // First key: x 1.85, y 0.27, r 17.7, rx 2.35, ry 0.77, w/h default 1.
        Assert.Equal(new DtsiKeyPhysical(100, 100, 185, 27, 1770, 235, 77), layout.Keys[0]);
    }

    [Fact]
    public void Slabwing_NegativeZeroY_BecomesZero()
    {
        var raw = LayoutFixtures.Read("slabwing.info.json");
        Assert.Contains("-0.0", raw);

        var layout = Assert.Single(InfoJsonImporter.Import(raw));
        Assert.Equal(0, layout.Keys[1].Y);
    }

    [Fact]
    public void Cosmos82_ImportsNameFromKeyboardNameAnd79CentiKeys()
    {
        var layout = Assert.Single(InfoJsonImporter.Import(LayoutFixtures.Read("cosmos82.info.json")));

        Assert.Equal("Cosmos 82", layout.Name);
        Assert.Equal(82, layout.Keys.Count);
        Assert.All(layout.Keys, key =>
        {
            Assert.Equal(79, key.Width);
            Assert.Equal(79, key.Height);
        });
    }

    [Fact]
    public void Go60_WithoutNameField_FallsBackToLayoutName()
    {
        var layout = Assert.Single(InfoJsonImporter.Import(LayoutFixtures.Read("go60.info.json")));

        Assert.Equal("LAYOUT", layout.Name);
        Assert.Equal(60, layout.Keys.Count);
    }

    [Fact]
    public void Cosmos82_InfoJson_MatchesPhysicalLayoutOfSameBoard()
    {
        // The fact the wizard bets on: the two file formats describe the
        // same board, key for key, in the same binding order.
        var fromInfoJson = InfoJsonImporter.Import(LayoutFixtures.Read("cosmos82.info.json"))[0];
        var fromDtsi = PhysicalLayoutImporter.Import(LayoutFixtures.Read("cosmos82.dtsi"))[0];

        Assert.Equal(fromDtsi.Keys, fromInfoJson.Keys);
    }

    // ── naming ──────────────────────────────────────────────────────────

    [Fact]
    public void TwoLayoutsWithTopLevelName_GetParenthesisedSuffixesInDocumentOrder()
    {
        var json = """
            {
              "name": "Board",
              "layouts": {
                "A": { "layout": [ { "x": 0, "y": 0 } ] },
                "B": { "layout": [ { "x": 1, "y": 1 } ] }
              }
            }
            """;

        var imported = InfoJsonImporter.Import(json);

        Assert.Equal(2, imported.Count);
        Assert.Equal("Board (A)", imported[0].Name);
        Assert.Equal("Board (B)", imported[1].Name);
    }

    [Fact]
    public void EmptyLayoutsObject_ImportsEmptyList()
    {
        Assert.Empty(InfoJsonImporter.Import("""{ "name": "X", "layouts": {} }"""));
    }

    // ── rotation pivot without rx/ry ────────────────────────────────────

    [Fact]
    public void RotatedKeyWithoutRxRy_PivotsAtKeyCentreWithOneWarning()
    {
        var json = """
            {
              "layouts": {
                "LAYOUT": { "layout": [
                  { "x": 0, "y": 0 },
                  { "x": 1, "y": 2, "w": 1.5, "r": 30 }
                ] }
              }
            }
            """;

        var layout = Assert.Single(InfoJsonImporter.Import(json));

        // Key 0 does not rotate: origin stays 0. Key 1: centre of
        // (1, 2, 1.5x1) is (1.75, 2.5) -> (175, 250).
        Assert.Equal(new DtsiKeyPhysical(100, 100, 0, 0, 0, 0, 0), layout.Keys[0]);
        Assert.Equal(new DtsiKeyPhysical(150, 100, 100, 200, 3000, 175, 250), layout.Keys[1]);

        var warning = Assert.Single(layout.Warnings);
        Assert.Equal("Key 1: rotation without rx/ry, rotating around the key centre", warning);
    }

    [Fact]
    public void RotatedKeyWithOnlyOneOfRxRy_FillsTheOtherFromCentreOnce()
    {
        var json = """
            {
              "layouts": { "LAYOUT": { "layout": [ { "x": 1, "y": 2, "r": 30, "rx": 9 } ] } }
            }
            """;

        var layout = Assert.Single(InfoJsonImporter.Import(json));

        Assert.Equal(900, layout.Keys[0].Rx);
        Assert.Equal(250, layout.Keys[0].Ry);
        Assert.Equal(
            "Key 0: rotation without rx/ry, rotating around the key centre",
            Assert.Single(layout.Warnings));
    }

    // ── rounding ────────────────────────────────────────────────────────

    [Fact]
    public void Rounding_IsAwayFromZero()
    {
        var json = """
            {
              "layouts": { "LAYOUT": { "layout": [
                { "x": 0.125, "y": 0 },
                { "x": -0.125, "y": 0 }
              ] } }
            }
            """;

        var layout = Assert.Single(InfoJsonImporter.Import(json));

        Assert.Equal(13, layout.Keys[0].X);
        Assert.Equal(-13, layout.Keys[1].X);
    }

    // ── refusals ────────────────────────────────────────────────────────

    [Fact]
    public void InvalidJSON_ReportsParserLineNumber()
    {
        var json = """
            {
              "layouts": {
                "A": oops
              }
            }
            """;

        var ex = Assert.Throws<LayoutImportException>(() => InfoJsonImporter.Import(json));
        Assert.Contains("line 3", ex.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "layouts": 5 }""")]
    [InlineData("""{ "layouts": [ "LAYOUT" ] }""")]
    [InlineData("[1, 2]")]
    public void MissingOrNonObjectLayouts_IsRefused(string json)
    {
        var ex = Assert.Throws<LayoutImportException>(() => InfoJsonImporter.Import(json));
        Assert.Equal("info.json has no 'layouts' object", ex.Message);
    }

    [Theory]
    [InlineData("""{ "layouts": { "LAYOUT": { "layout": [] } } }""")]
    [InlineData("""{ "layouts": { "LAYOUT": { } } }""")]
    [InlineData("""{ "layouts": { "LAYOUT": 5 } }""")]
    public void LayoutWithoutKeys_IsRefusedByName(string json)
    {
        var ex = Assert.Throws<LayoutImportException>(() => InfoJsonImporter.Import(json));
        Assert.Contains("LAYOUT", ex.Message);
    }

    [Fact]
    public void KeyWithoutX_IsRefusedWithLayoutNameAndIndex()
    {
        var json = """
            {
              "layouts": { "LAYOUT": { "layout": [ { "x": 0, "y": 0 }, { "y": 1 } ] } }
            }
            """;

        var ex = Assert.Throws<LayoutImportException>(() => InfoJsonImporter.Import(json));
        Assert.Contains("LAYOUT", ex.Message);
        Assert.Contains("key 1", ex.Message);
        Assert.Contains("'x'", ex.Message);
    }

    [Theory]
    [InlineData("""{ "layouts": { "L": { "layout": [ { "x": 0, "y": 0, "w": 0 } ] } } }""")]
    [InlineData("""{ "layouts": { "L": { "layout": [ { "x": 0, "y": 0, "w": 1, "h": -0.5 } ] } } }""")]
    public void NonPositiveSize_IsRefused(string json)
    {
        var ex = Assert.Throws<LayoutImportException>(() => InfoJsonImporter.Import(json));
        Assert.Contains("non-positive", ex.Message);
        Assert.Contains("key 0", ex.Message);
    }

    [Fact]
    public void UnknownProperties_AreIgnored()
    {
        var json = """
            {
              "comment": "hi",
              "layouts": {
                "LAYOUT": {
                  "layout": [ { "x": 0, "y": 0, "row": 3, "col": 4, "label": "Q", "matrix": [3, 4] } ]
                }
              }
            }
            """;

        var layout = Assert.Single(InfoJsonImporter.Import(json));
        Assert.Equal(new DtsiKeyPhysical(100, 100, 0, 0, 0, 0, 0), layout.Keys[0]);
    }
}
