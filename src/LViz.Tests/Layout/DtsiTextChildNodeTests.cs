using LViz.Core.Layout.Dtsi;
using Xunit;

namespace LViz.Tests.Layout;

/// <summary><see cref="DtsiText.FindChildNodes"/>: the one depth-0 child scanner the keymap writers project from.</summary>
public class DtsiTextChildNodeTests
{
    private static List<DtsiText.DtsiChildNode> Scan(string text) =>
        DtsiText.FindChildNodes(text, 0, text.Length);

    [Fact]
    public void LabelledNode_ReportsNameWithoutLabelAndHeaderDetails()
    {
        const string text = "a {\n};\n    lbl: combo_esc {\n        x;\n    };\n";
        var node = Assert.Single(Scan(text), n => n.Name == "combo_esc");

        Assert.True(node.Labelled);
        Assert.Equal("combo_esc", text[node.NameStart..node.NameEnd]);
        Assert.Equal("    ", node.Indent);
        Assert.Equal(text.IndexOf("    lbl", StringComparison.Ordinal), node.HeaderLineStart);
        Assert.Equal('{', text[node.OpenBrace]);
        Assert.Equal('}', text[node.CloseBrace]);
        Assert.Equal(text.LastIndexOf('}'), node.CloseBrace);
    }

    [Fact]
    public void UnlabelledNode_IsNotLabelled()
    {
        var node = Assert.Single(Scan("layer_base {\n};\n"));
        Assert.False(node.Labelled);
        Assert.Equal("layer_base", node.Name);
    }

    [Fact]
    public void NestedBraces_AreOneChild()
    {
        const string text = "outer {\n    inner {\n        p = <1>;\n    };\n};\nsecond {\n};\n";
        var nodes = Scan(text);

        Assert.Equal(["outer", "second"], nodes.Select(n => n.Name));
        Assert.Equal(text.IndexOf("};\nsecond", StringComparison.Ordinal), nodes[0].CloseBrace);
    }

    [Fact]
    public void UnclosedChild_IsDropped()
    {
        var nodes = Scan("done {\n};\nopen {\n    x;\n");
        Assert.Equal(["done"], nodes.Select(n => n.Name));
    }

    [Fact]
    public void EndOfClosingLine_SkipsTrailingSpacesToPastTheNewline()
    {
        const string text = "n {\n};  \nnext";
        Assert.Equal(text.IndexOf("next", StringComparison.Ordinal), DtsiText.EndOfClosingLine(text, text.IndexOf('}')));
        Assert.Equal(4, DtsiText.EndOfClosingLine("n {}", 3));
    }
}
