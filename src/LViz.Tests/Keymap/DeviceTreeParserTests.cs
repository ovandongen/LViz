using LViz.Core.Keymap.Parser;
using Xunit;

namespace LViz.Tests.Keymap;

public class DeviceTreeParserTests
{
    private static DtRoot Parse(string source)
        => DeviceTreeParser.Parse(DeviceTreeLexer.Tokenize(source));

    [Fact]
    public void ParsesEmptyRoot()
    {
        var root = Parse("/ { };");
        Assert.Single(root.Children);
        Assert.Equal("/", root.Children[0].Name);
    }

    [Fact]
    public void ParsesNestedNodes()
    {
        var root = Parse("/ { foo { bar { }; }; };");
        var foo = root.Children[0].Children[0];
        Assert.Equal("foo", foo.Name);
        Assert.Single(foo.Children);
        Assert.Equal("bar", foo.Children[0].Name);
    }

    [Fact]
    public void ParsesLabelledNode()
    {
        var root = Parse("/ { my_label: my_name { }; };");
        var node = root.Children[0].Children[0];
        Assert.Equal("my_label", node.Label);
        Assert.Equal("my_name", node.Name);
    }

    [Fact]
    public void ParsesOverlayByReference()
    {
        var root = Parse("&phandle { extra; };");
        Assert.Single(root.Children);
        Assert.True(root.Children[0].IsReference);
        Assert.Equal("phandle", root.Children[0].Name);
    }

    [Fact]
    public void ParsesBooleanProperty()
    {
        var root = Parse("/ { flag; };");
        var prop = root.Children[0].Properties.Single();
        Assert.Equal("flag", prop.Name);
        Assert.IsType<DtBool>(prop.Value);
    }

    [Fact]
    public void ParsesStringListProperty()
    {
        var root = Parse("/ { compatible = \"a\", \"b\"; };");
        var sl = Assert.IsType<DtStringList>(root.Children[0].Properties[0].Value);
        Assert.Equal(new[] { "a", "b" }, sl.Values);
    }

    [Fact]
    public void ParsesCellArrayWithMixedCells()
    {
        var root = Parse("/ { bindings = <&kp A 1 0xFF (-1)>; };");
        var ca = Assert.IsType<DtCellArray>(root.Children[0].Properties[0].Value);
        Assert.Equal(5, ca.Cells.Count);
        Assert.IsType<DtCellRef>(ca.Cells[0]);
        Assert.Equal("kp", ((DtCellRef)ca.Cells[0]).PhandleName);
        Assert.IsType<DtCellIdent>(ca.Cells[1]);
        Assert.Equal("A", ((DtCellIdent)ca.Cells[1]).Text);
        Assert.IsType<DtCellNumber>(ca.Cells[2]);
        Assert.Equal(1, ((DtCellNumber)ca.Cells[2]).Value);
        Assert.IsType<DtCellNumber>(ca.Cells[3]);
        Assert.Equal(255, ((DtCellNumber)ca.Cells[3]).Value);
        Assert.IsType<DtCellParenExpr>(ca.Cells[4]);
        Assert.Contains("-1", ((DtCellParenExpr)ca.Cells[4]).RawText);
    }

    [Fact]
    public void ConcatenatesMultipleCellSegments()
    {
        var root = Parse("/ { bindings = <&kp A>, <&kp B>; };");
        var ca = Assert.IsType<DtCellArray>(root.Children[0].Properties[0].Value);
        // Two segments → 4 cells (&kp A &kp B).
        Assert.Equal(4, ca.Cells.Count);
    }

    [Fact]
    public void KeepsModifierWrapperVerbatim()
    {
        var root = Parse("/ { bindings = <&kp (LC(LSFT))>; };");
        var ca = (DtCellArray)root.Children[0].Properties[0].Value;
        var paren = Assert.IsType<DtCellParenExpr>(ca.Cells[1]);
        // Should preserve both layers of parens and the modifier names.
        Assert.Contains("LC", paren.RawText);
        Assert.Contains("LSFT", paren.RawText);
    }

    [Fact]
    public void SkipsDeleteNodeWithoutCrashing()
    {
        var root = Parse("/ { /delete-node/ foo; bar { }; };");
        var children = root.Children[0].Children;
        Assert.Single(children);
        Assert.Equal("bar", children[0].Name);
    }
}
