using LViz.Core.Keymap.Parser;
using Xunit;

namespace LViz.Tests.Keymap;

public class DeviceTreeLexerTests
{
    [Fact]
    public void LexesStructuralTokens()
    {
        var tokens = DeviceTreeLexer.Tokenize("{ } < > ; , = & : / [ ] ( )");
        var kinds = tokens.Select(t => t.Kind).ToArray();
        Assert.Equal(DtTokenKind.LBrace, kinds[0]);
        Assert.Equal(DtTokenKind.RBrace, kinds[1]);
        Assert.Equal(DtTokenKind.LAngle, kinds[2]);
        Assert.Equal(DtTokenKind.RAngle, kinds[3]);
        Assert.Equal(DtTokenKind.End, kinds[^1]);
    }

    [Fact]
    public void LexesAmpersandRefAsTwoTokens()
    {
        var tokens = DeviceTreeLexer.Tokenize("&kp");
        Assert.Equal(DtTokenKind.Amp, tokens[0].Kind);
        Assert.Equal(DtTokenKind.Ident, tokens[1].Kind);
        Assert.Equal("kp", tokens[1].Text);
    }

    [Fact]
    public void LexesDecimalAndHexNumbers()
    {
        var tokens = DeviceTreeLexer.Tokenize("0 42 0xFF 0x1A");
        Assert.Equal("0", tokens[0].Text);
        Assert.Equal("42", tokens[1].Text);
        Assert.Equal("0xFF", tokens[2].Text);
        Assert.Equal("0x1A", tokens[3].Text);
        Assert.All(tokens.Take(4), t => Assert.Equal(DtTokenKind.Number, t.Kind));
    }

    [Fact]
    public void LexesStringWithEscapes()
    {
        var tokens = DeviceTreeLexer.Tokenize("\"hello\\nworld\"");
        Assert.Equal(DtTokenKind.String, tokens[0].Kind);
        Assert.Equal("hello\nworld", tokens[0].Text);
    }

    [Fact]
    public void LexesHyphenatedAndHashIdentifiers()
    {
        var tokens = DeviceTreeLexer.Tokenize("key-positions #binding-cells display-name");
        Assert.All(tokens.Take(3), t => Assert.Equal(DtTokenKind.Ident, t.Kind));
        Assert.Equal("key-positions", tokens[0].Text);
        Assert.Equal("#binding-cells", tokens[1].Text);
        Assert.Equal("display-name", tokens[2].Text);
    }

    [Fact]
    public void RecognizesDeleteNodeAsSingleToken()
    {
        var tokens = DeviceTreeLexer.Tokenize("/delete-node/ foo;");
        Assert.Equal(DtTokenKind.DirectiveDelete, tokens[0].Kind);
        Assert.Equal("/delete-node/", tokens[0].Text);
    }

    [Fact]
    public void TracksLineNumbers()
    {
        var tokens = DeviceTreeLexer.Tokenize("a\nb\nc");
        Assert.Equal(1, tokens[0].Line);
        Assert.Equal(2, tokens[1].Line);
        Assert.Equal(3, tokens[2].Line);
    }

    [Fact]
    public void ParseNumberLiteralHandlesSignedHex()
    {
        Assert.Equal(255, DeviceTreeLexer.ParseNumberLiteral("0xFF"));
        Assert.Equal(-1, DeviceTreeLexer.ParseNumberLiteral("-1"));
        Assert.Equal(42, DeviceTreeLexer.ParseNumberLiteral("42"));
    }
}
