using LViz.Core.ZmkConfig;
using Xunit;

namespace LViz.Tests.ZmkConfig;

/// <summary>
/// <see cref="ZmkBuildMatrixWriter.AddShield"/> run against the real
/// zmk-config fixtures and a few inline matrices: the writer may only append
/// the shield to the addressed entry's value — or insert one shield line
/// below its board line — and must leave every other byte, comment, quote and
/// line ending as it was.
/// </summary>
public class ZmkBuildMatrixWriterTests
{
    private const string Shield = "raw_hid_adapter";

    private static string Build(string name) => ZmkConfigFixtures.Read(name, "build.yaml");

    private static string[] Lines(string text) => text.Split('\n');

    [Fact]
    public void Cosmos82_CentralEntry_AppendsShieldToItsValue_ChangingNothingElse()
    {
        var text = Build("cosmos82");

        var result = ZmkBuildMatrixWriter.AddShield(text, 1, Shield);

        var before = Lines(text);
        var after = Lines(result);
        Assert.Equal(before.Length, after.Length); // comment lines and blank lines keep their place
        var line = IndexOfLine(
            before, "    shield: cosmos82_right     # central (USB + Stage-2 cursor trackball)");
        Assert.Equal(
            "    shield: cosmos82_right raw_hid_adapter     # central (USB + Stage-2 cursor trackball)",
            after[line]);
        // Every other line — the comment blocks, the peripheral, the settings_reset entry — is byte-identical.
        for (var i = 0; i < before.Length; i++)
        {
            if (i != line)
                Assert.Equal(before[i], after[i]);
        }
    }

    [Fact]
    public void Slabwing_UnibodyEntry_AppendsShieldAfterItsOwnShields()
    {
        var text = Build("slabwing");

        var result = ZmkBuildMatrixWriter.AddShield(text, 0, Shield);

        var before = Lines(text);
        var after = Lines(result);
        Assert.Equal(before.Length, after.Length);
        var line = IndexOfLine(before, "    shield: slabwing nice_view_adapter nice_view");
        Assert.Equal("    shield: slabwing nice_view_adapter nice_view raw_hid_adapter", after[line]);
    }

    [Fact]
    public void Glove80_EntryThatAlreadyHasTheShield_ReturnsTheTextUnchanged()
    {
        var text = Build("glove80");

        Assert.Equal(text, ZmkBuildMatrixWriter.AddShield(text, 0, Shield));
    }

    [Fact]
    public void Glove80_EntryWithoutShieldLine_InsertsItRightBelowTheBoardLine()
    {
        var text = Build("glove80");

        var result = ZmkBuildMatrixWriter.AddShield(text, 1, Shield);

        var lines = Lines(result);
        var board = IndexOfLine(lines, "  - board: glove80_rh");
        Assert.Equal("    shield: raw_hid_adapter", lines[board + 1]);
        Assert.Equal(Lines(text).Length + 1, lines.Length);
        Assert.EndsWith("\n", result); // the file still ends with its newline
    }

    [Fact]
    public void Go60_BoardIsTheLastLineWithoutNewline_ResultStillHasNoTrailingNewline()
    {
        var text = Build("go60");
        Assert.False(text.EndsWith("\n"));

        var result = ZmkBuildMatrixWriter.AddShield(text, 1, Shield);

        Assert.EndsWith("  - board: go60_rh\n    shield: raw_hid_adapter", result);
        Assert.False(result.EndsWith("\n"));
        Assert.Equal(Lines(text).Length + 1, Lines(result).Length);
    }

    [Theory]
    [InlineData("    shield: \"a b\"", "    shield: \"a b raw_hid_adapter\"")]
    [InlineData("    shield: 'a b'", "    shield: 'a b raw_hid_adapter'")]
    [InlineData("    shield:", "    shield: raw_hid_adapter")]
    [InlineData("    shield: \"a b\"  # note", "    shield: \"a b raw_hid_adapter\"  # note")]
    [InlineData("    shield: cos  # note", "    shield: cos raw_hid_adapter  # note")]
    public void ValueShapes_KeepsQuotesCommentsAndTrailingSpacesInPlace(string shieldLine, string expected)
    {
        var result = ZmkBuildMatrixWriter.AddShield("include:\n  - board: x\n" + shieldLine + "\n", 0, Shield);

        var lines = Lines(result);
        Assert.Equal(4, lines.Length);
        Assert.Equal(expected, lines[2]);
    }

    [Fact]
    public void ShieldOnTheDashLine_AppendsToThatSameLine()
    {
        var result = ZmkBuildMatrixWriter.AddShield(
            "include:\n" +
            "  - shield: corne_left\n" +
            "    board: nice_nano_v2\n",
            0, Shield);

        var lines = Lines(result);
        Assert.Equal(4, lines.Length); // in-place edit: nothing inserted
        Assert.Equal("  - shield: corne_left raw_hid_adapter", lines[1]);
    }

    [Fact]
    public void BoardNotOnTheDashLine_NewLineUsesTheBoardLineIndent()
    {
        var result = ZmkBuildMatrixWriter.AddShield(
            "include:\n" +
            "  - name: x\n" +
            "    board: y\n",
            0, Shield);

        var lines = Lines(result);
        Assert.Equal(5, lines.Length);
        Assert.Equal("    shield: raw_hid_adapter", lines[3]);
    }

    [Fact]
    public void KeysBelowBoard_NewLineStillSitsDirectlyBelowTheBoardLine()
    {
        var result = ZmkBuildMatrixWriter.AddShield(
            "include:\n" +
            "  - board: x\n" +
            "    arch: arm64\n",
            0, Shield);

        var lines = Lines(result);
        Assert.Equal("    shield: raw_hid_adapter", lines[2]);
        Assert.Equal("    arch: arm64", lines[3]);
    }

    [Fact]
    public void CrlfFile_AppendedShieldKeepsTheInlineCommentAndItsCarriageReturn()
    {
        var crlf = Build("cosmos82").Replace("\n", "\r\n");

        var result = ZmkBuildMatrixWriter.AddShield(crlf, 1, Shield);

        var lines = Lines(result);
        Assert.Equal(Lines(crlf).Length, lines.Length);
        Assert.All(lines[..^1], line => Assert.EndsWith("\r", line)); // the split's trailing "" is the terminator
        var modified = lines.First(l => l.Contains("cosmos82_right", StringComparison.Ordinal));
        Assert.Contains("cosmos82_right raw_hid_adapter     #", modified);
        Assert.EndsWith("     # central (USB + Stage-2 cursor trackball)\r", modified);
    }

    [Fact]
    public void CrlfFileLastLineWithoutReturn_InsertionGivesItBack()
    {
        var crlf = Build("go60").Replace("\n", "\r\n"); // ends without a newline: the last line lacks its \r

        var result = ZmkBuildMatrixWriter.AddShield(crlf, 1, Shield);

        Assert.Contains("  - board: go60_rh\r\n    shield: raw_hid_adapter", result);
        Assert.False(result.EndsWith("\n"));
        Assert.All(Lines(result), line => Assert.EndsWith("\r", line));
    }

    [Fact]
    public void UnknownEntryIndex_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ZmkBuildMatrixWriter.AddShield(Build("cosmos82"), 5, Shield));
    }

    private static int IndexOfLine(string[] lines, string line)
    {
        var index = Array.IndexOf(lines, line);
        Assert.True(index >= 0, $"line not found: {line}");
        return index;
    }
}
