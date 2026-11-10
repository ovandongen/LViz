using LViz.Core.ZmkConfig;
using Xunit;

namespace LViz.Tests.ZmkConfig;

/// <summary>
/// The batch 024 <c>.conf</c> writer: options with an active line are rewritten
/// in place, missing ones are appended in one LViz block, and everything else —
/// comments, blank lines, line endings, a missing trailing newline — survives.
/// </summary>
public class ZmkConfWriterTests
{
    private static IReadOnlyList<KeyValuePair<string, string>> Opts(
        params (string Key, string Value)[] settings) =>
        settings.Select(s => new KeyValuePair<string, string>(s.Key, s.Value)).ToList();

    private static string Conf(string fixture) =>
        ZmkConfigFixtures.Read(fixture, $"config/{fixture}.conf");

    [Fact]
    public void Slabwing_OptionsJoinOneLvizBlockBelowTheOriginalText()
    {
        var text = Conf("slabwing");
        var result = ZmkConfWriter.Apply(text, Opts(
            ("CONFIG_RAW_HID", "y"),
            ("CONFIG_HID_VIZ", "y"),
            ("CONFIG_HID_VIZ_SIGNAL", "y"),
            ("CONFIG_HID_VIZ_CONFIG_ID", "\"abc\"")));

        Assert.StartsWith(text, result);
        Assert.EndsWith(
            "\n# LViz live features\nCONFIG_RAW_HID=y\nCONFIG_HID_VIZ=y\n"
            + "CONFIG_HID_VIZ_SIGNAL=y\nCONFIG_HID_VIZ_CONFIG_ID=\"abc\"\n",
            result);

        // Exactly one empty line sits above the header.
        var header = result.LastIndexOf(ZmkConfWriter.BlockHeader, StringComparison.Ordinal);
        Assert.Equal('\n', result[header - 1]); // the empty line's own newline
        Assert.Equal('\n', result[header - 2]); // the last original line's newline
        Assert.NotEqual('\n', result[header - 3]); // ...and no second empty line
    }

    [Fact]
    public void Go60_OptionsAlreadyY_LeaveTheFileAsItIs()
    {
        var text = Conf("go60");
        var result = ZmkConfWriter.Apply(text, Opts(
            ("CONFIG_RAW_HID", "y"),
            ("CONFIG_HID_VIZ_SIGNAL", "y")));

        Assert.Equal(text, result);
    }

    [Fact]
    public void Go60_TurningRgbOff_RewritesOnlyThatLine_AndKeepsTheMissingTrailingNewline()
    {
        var text = Conf("go60");
        var result = ZmkConfWriter.Apply(text, Opts(("CONFIG_HID_VIZ_RGB", "n")));

        Assert.Equal(text.Replace("CONFIG_HID_VIZ_RGB=y", "CONFIG_HID_VIZ_RGB=n"), result);
        Assert.False(result.EndsWith("\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Cosmos82_AppendsAfterOneEmptyLineAndKeepsCommentedOptions()
    {
        var text = Conf("cosmos82");
        var result = ZmkConfWriter.Apply(text, Opts(("CONFIG_RAW_HID", "y")));

        Assert.Equal(text + "\n# LViz live features\nCONFIG_RAW_HID=y\n", result);
        Assert.Contains("# CONFIG_ZMK_USB_LOGGING=y\n", result);
    }

    [Fact]
    public void ExistingHeader_NewOptionJoinsTheBlockWithoutASecondHeader()
    {
        var result = ZmkConfWriter.Apply(
            "# LViz live features\nCONFIG_RAW_HID=y\n", Opts(("CONFIG_HID_VIZ", "y")));

        Assert.Equal("# LViz live features\nCONFIG_RAW_HID=y\nCONFIG_HID_VIZ=y\n", result);
    }

    [Fact]
    public void TextAlreadyEndingInAnEmptyLine_DoesNotGetASecondOne()
    {
        var result = ZmkConfWriter.Apply("CONFIG_A=y\n\n", Opts(("CONFIG_B", "y")));

        Assert.Equal("CONFIG_A=y\n\n# LViz live features\nCONFIG_B=y\n", result);
    }

    [Fact]
    public void NullText_BuildsTheBlockWithItsHeader()
    {
        var result = ZmkConfWriter.Apply(null, Opts(
            ("CONFIG_RAW_HID", "y"),
            ("CONFIG_HID_VIZ", "y")));

        Assert.Equal("# LViz live features\nCONFIG_RAW_HID=y\nCONFIG_HID_VIZ=y\n", result);
    }

    [Fact]
    public void NullTextWithoutOptions_BuildsNothing()
    {
        Assert.Equal("", ZmkConfWriter.Apply(null, Opts()));
    }

    [Fact]
    public void CrlfFile_AppendsWithCrlfAndOneEmptyLine()
    {
        var result = ZmkConfWriter.Apply("CONFIG_A=y\r\n", Opts(("CONFIG_B", "y")));

        Assert.Equal("CONFIG_A=y\r\n\r\n# LViz live features\r\nCONFIG_B=y\r\n", result);
    }

    [Fact]
    public void CrlfFile_RewritingAValue_KeepsTheCarriageReturn()
    {
        var result = ZmkConfWriter.Apply("CONFIG_A=y\r\nCONFIG_C=w\r\n", Opts(("CONFIG_A", "n")));

        Assert.Equal("CONFIG_A=n\r\nCONFIG_C=w\r\n", result);
    }

    [Fact]
    public void CrlfFileWithoutTrailingNewline_AppendingGivesTheLastLineItsNewlineBack()
    {
        var result = ZmkConfWriter.Apply(
            "CONFIG_A=y\r\nCONFIG_B=z", Opts(("CONFIG_B", "n"), ("CONFIG_C", "y")));

        Assert.Equal("CONFIG_A=y\r\nCONFIG_B=n\r\n\r\n# LViz live features\r\nCONFIG_C=y\r\n", result);
    }

    [Fact]
    public void AlreadyTheSameValue_DoesNotEvenRewriteTheSpacingOfItsLine()
    {
        var result = ZmkConfWriter.Apply("CONFIG_A = y\n", Opts(("CONFIG_A", "y")));

        Assert.Equal("CONFIG_A = y\n", result);
    }

    [Fact]
    public void DuplicateKey_TheLastActiveLineDecides()
    {
        var result = ZmkConfWriter.Apply("CONFIG_X=n\nCONFIG_X=y\n", Opts(("CONFIG_X", "n")));

        Assert.Equal("CONFIG_X=n\nCONFIG_X=n\n", result);
    }

    [Fact]
    public void CommentedOption_IsNotPresentAndStaysAsItIs()
    {
        var result = ZmkConfWriter.Apply("# CONFIG_X=y\n", Opts(("CONFIG_X", "n")));

        Assert.Equal("# CONFIG_X=y\n\n# LViz live features\nCONFIG_X=n\n", result);
    }

    [Fact]
    public void AKeyThatIsNotAConfigOption_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => ZmkConfWriter.Apply("CONFIG_A=y\n", Opts(("RAW_HID", "y"))));
    }
}
