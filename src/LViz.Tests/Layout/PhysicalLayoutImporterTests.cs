using LViz.Core.Layout;
using LViz.Core.Layout.Dtsi;
using LViz.Core.Layout.Import;
using Xunit;

namespace LViz.Tests.Layout;

/// <summary>
/// Tests for the shared import contract (layout wizard importers):
/// <see cref="DtsiText"/> comment masking and node lookup, the per-node
/// <see cref="DtsiPhysicalLayoutParser.ParseAll"/> behaviour, and the
/// <see cref="PhysicalLayoutImporter"/> mapping. Real-world fixture:
/// cosmos82.dtsi; regression guard: all seven bundled boards.
/// </summary>
public class PhysicalLayoutImporterTests
{
    // ── real fixture ────────────────────────────────────────────────────

    [Fact]
    public void Cosmos82_ImportsOneLayoutNamedDefaultWith82Keys()
    {
        var raw = LayoutFixtures.Read("cosmos82.dtsi");

        // The keys array really does contain comment lines; they must not
        // add keys — the layout keeps exactly the 82 real entries.
        Assert.Contains("// row 0", raw);

        var imported = PhysicalLayoutImporter.Import(raw);
        var layout = Assert.Single(imported);
        Assert.Equal("Default", layout.Name);
        Assert.Equal(LayoutSource.PhysicalLayout, layout.Source);
        Assert.Empty(layout.Warnings);
        Assert.Equal(82, layout.Keys.Count);
        Assert.Equal(new DtsiKeyPhysical(79, 79, 0, 83, 0, 0, 0), layout.Keys[0]);
    }

    // ── bundled boards must behave exactly as before ────────────────────

    public static IEnumerable<object[]> BundledBoards =>
        KeyboardProfileRegistry.All.Select(p =>
            new object[] { p, $"LViz.Core.Resources.{p.Id.ToLowerInvariant()}.dtsi" });

    [Theory]
    [MemberData(nameof(BundledBoards))]
    public void BundledResource_ParsesToExactlyOneLayoutWithProfileKeyCount(
        IKeyboardProfile profile, string resourceName)
    {
        // Load goes through Parse(), which itself insists on exactly one
        // physical-layout node and throws otherwise.
        var layout = DtsiEmbeddedResource.Load(resourceName);
        Assert.Equal(profile.KeyCount, layout.Keys.Count);
        Assert.False(string.IsNullOrEmpty(layout.NodeName));
    }

    // ── comment handling ────────────────────────────────────────────────

    [Fact]
    public void CommentedOutEntries_AreIgnored()
    {
        var text = """
            / {
                layout0: physical_layout_0 {
                    compatible = "zmk,physical-layout";
                    display-name = "Main";
                    keys = <&key_physical_attrs 100 100 0 0 0 0 0>
                        // <&key_physical_attrs 100 100 0 0 0 0 0>
                        /* <&key_physical_attrs 200 200 0 0 0 0 0> */
                        , <&key_physical_attrs 100 100 100 0 0 0 0>;
                };
            };
            """;

        var layout = Assert.Single(DtsiPhysicalLayoutParser.ParseAll(text));
        Assert.Equal(2, layout.Keys.Count);
    }

    // ── multiple layout nodes ───────────────────────────────────────────

    private const string TwoNodeText = """
        / {
            first_layout: layout_a {
                compatible = "zmk,physical-layout";
                display-name = "Alpha";
                keys = <&key_physical_attrs 100 100 0 0 0 0 0>,
                       <&key_physical_attrs 100 100 100 0 0 0 0>;
            };

            second_layout: layout_b {
                compatible = "zmk,physical-layout";
                keys = <&key_physical_attrs 100 100 0 0 0 0 0>;
            };
        };
        """;

    [Fact]
    public void TwoNodes_ProduceTwoLayoutsInFileOrder()
    {
        var layouts = DtsiPhysicalLayoutParser.ParseAll(TwoNodeText);

        Assert.Equal(2, layouts.Count);
        Assert.Equal("layout_a", layouts[0].NodeName);
        Assert.Equal("Alpha", layouts[0].DisplayName);
        Assert.Equal(2, layouts[0].Keys.Count);

        // Without display-name the node name (label prefix stripped) names it.
        Assert.Null(layouts[1].DisplayName);
        Assert.Equal("layout_b", layouts[1].NodeName);
        Assert.Single(layouts[1].Keys);

        var imported = PhysicalLayoutImporter.Import(TwoNodeText);
        Assert.Equal("Alpha", imported[0].Name);
        Assert.Equal("layout_b", imported[1].Name);
    }

    [Fact]
    public void Parse_OnTextWithTwoNodes_ThrowsInvalidOperation()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DtsiPhysicalLayoutParser.Parse(TwoNodeText));
        Assert.Contains("2", ex.Message);
    }

    // ── value handling ──────────────────────────────────────────────────

    [Fact]
    public void NegativeRotation_Survives()
    {
        var text = """
            / {
                layout0: physical_layout_0 {
                    compatible = "zmk,physical-layout";
                    keys = <&key_physical_attrs 79 79 446 586 (-1200) 496 636>;
                };
            };
            """;

        var layout = Assert.Single(DtsiPhysicalLayoutParser.ParseAll(text));
        Assert.Equal(new DtsiKeyPhysical(79, 79, 446, 586, -1200, 496, 636), layout.Keys[0]);
    }

    // ── "nothing found" vs malformed ────────────────────────────────────

    [Fact]
    public void StrayEntries_WithoutPhysicalLayoutNode_YieldEmptyList()
    {
        var text = """
            / {
                something_else {
                    compatible = "zmk,other-thing";
                    keys = <&key_physical_attrs 100 100 0 0 0 0 0>;
                };
            };
            """;

        Assert.Empty(DtsiPhysicalLayoutParser.ParseAll(text));
    }

    [Fact]
    public void SixFieldEntry_IsRefusedWithNodeNameAndLineNumber()
    {
        var text = """
            / {
                layout0: physical_layout_0 {
                    compatible = "zmk,physical-layout";
                    display-name = "Broken";
                    keys = <&key_physical_attrs 100 100 0 0 0 0>,
                           <&key_physical_attrs 100 100 100 0 0 0 0>;
                };
            };
            """;

        // Count the line of the 6-field entry independently of the parser.
        int markerIndex = text.IndexOf("&key_physical_attrs 100 100 0 0 0 0>", StringComparison.Ordinal);
        Assert.True(markerIndex >= 0);
        int expectedLine = 1 + text[..markerIndex].Count(c => c == '\n');
        Assert.Equal(5, expectedLine);

        var ex = Assert.Throws<LayoutImportException>(
            () => DtsiPhysicalLayoutParser.ParseAll(text));
        Assert.Contains("physical_layout_0", ex.Message);
        Assert.Contains($"line {expectedLine}", ex.Message);
    }

    [Fact]
    public void PhysicalLayoutNodeWithoutEntries_IsRefused()
    {
        var text = """
            / {
                empty_layout: layout_a {
                    compatible = "zmk,physical-layout";
                    status = "okay";
                };
            };
            """;

        var ex = Assert.Throws<LayoutImportException>(
            () => DtsiPhysicalLayoutParser.ParseAll(text));
        Assert.Contains("layout_a", ex.Message);
        Assert.Contains("line 2", ex.Message);
    }

    // ── DtsiText helpers ────────────────────────────────────────────────

    [Fact]
    public void MaskComments_PreservesLengthAndNewlinePositions()
    {
        var text = LayoutFixtures.Read("cosmos82.dtsi");
        var masked = DtsiText.MaskComments(text);

        Assert.Equal(text.Length, masked.Length);
        Assert.Equal(NewlineIndices(text), NewlineIndices(masked));

        // Comment bodies are gone, quoted strings are not.
        Assert.DoesNotContain("Copyright", masked);
        Assert.DoesNotContain("// row 0", masked);
        Assert.Contains("\"Default\"", masked);
    }

    [Fact]
    public void MaskComments_KeepsSlashPairsInsideStrings()
    {
        var masked = DtsiText.MaskComments("""status = "a // b";""");
        Assert.Equal("""status = "a // b";""", masked);
    }

    [Fact]
    public void FindNodes_FindsMatrixTransformNodeInCosmos82()
    {
        var masked = DtsiText.MaskComments(LayoutFixtures.Read("cosmos82.dtsi"));

        var node = Assert.Single(DtsiText.FindNodesByCompatible(masked, "zmk,matrix-transform"));
        Assert.Equal("keymap_transform_0", node.Name);
        // Body spans exactly between the node's '{' and its matching '}'.
        Assert.Equal('{', masked[node.BodyStart - 1]);
        Assert.Equal('}', masked[node.BodyEnd]);
    }

    private static IEnumerable<int> NewlineIndices(string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                yield return i;
    }
}
