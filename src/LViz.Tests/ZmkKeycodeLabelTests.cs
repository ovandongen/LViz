using LViz.Core.Keymap;
using Xunit;

namespace LViz.Tests;

public class ZmkKeycodeLabelTests
{
    [Theory]
    [InlineData("FSLH", "/")]
    [InlineData("BSLH", "\\")]
    [InlineData("SEMI", ";")]
    [InlineData("SQT", "'")]
    [InlineData("COMMA", ",")]
    [InlineData("DOT", ".")]
    [InlineData("SPACE", "␣")]
    [InlineData("UP", "↑")]
    // Long-form aliases emitted by the Moergo editor.
    [InlineData("SINGLE_QUOTE", "'")]
    [InlineData("APOSTROPHE", "'")]
    [InlineData("DOUBLE_QUOTES", "\"")]
    [InlineData("LEFT_BRACKET", "[")]
    [InlineData("RIGHT_PARENTHESIS", ")")]
    [InlineData("SEMICOLON", ";")]
    [InlineData("BACKSLASH", "\\")]
    [InlineData("RETURN", "⏎")]
    // Modifier keys as bare keycodes — rendered as icons.
    [InlineData("LCTRL", "⌃")]
    [InlineData("RCTRL", "⌃")]
    [InlineData("LCTL", "⌃")]
    [InlineData("LSHFT", "⇪")]
    [InlineData("LSHIFT", "⇪")]
    [InlineData("RSHIFT", "⇪")]
    [InlineData("LEFT_SHIFT", "⇪")]
    [InlineData("RGUI", "⌘")]
    [InlineData("LCMD", "⌘")]
    [InlineData("LMETA", "⌘")]
    [InlineData("LWIN", "⌘")]
    [InlineData("LALT", "⌥")]
    [InlineData("RALT", "⌥")]
    // Numpad: KP_ prefix strips and routes through the main map.
    [InlineData("KP_N1", "1")]
    [InlineData("KP_N9", "9")]
    [InlineData("KP_NUMBER_3", "3")]
    [InlineData("KP_PLUS", "+")]
    [InlineData("KP_MINUS", "-")]
    [InlineData("KP_DIVIDE", "/")]
    [InlineData("KP_MULTIPLY", "*")]
    [InlineData("KP_SUBTRACT", "-")]
    [InlineData("KP_SLASH", "/")]
    [InlineData("KP_ASTERISK", "*")]
    [InlineData("KP_DOT", ".")]
    [InlineData("KP_COMMA", ",")]
    [InlineData("KP_ENTER", "⏎")]
    [InlineData("KP_EQUAL", "=")]
    [InlineData("KP_NUMLOCK", "Num")]
    [InlineData("NUM_LOCK", "Num")]
    public void Display_MapsPunctuationAndGlyphs(string input, string expected)
    {
        Assert.Equal(expected, ZmkKeycodeLabel.Display(input, ModifierStyle.Mac));
    }

    [Theory]
    [InlineData("N0", "0")]
    [InlineData("N7", "7")]
    [InlineData("NUMBER_3", "3")]
    [InlineData("NUMBER_9", "9")]
    public void Display_NumberAliasesMapToDigit(string input, string expected)
    {
        Assert.Equal(expected, ZmkKeycodeLabel.Display(input, ModifierStyle.Mac));
    }

    [Theory]
    [InlineData("ESC", "ESC")]
    [InlineData("F14", "F14")]
    [InlineData("A", "A")]
    // Underscored fall-through keycodes get spaces so long labels can wrap.
    [InlineData("UNKNOWN_KC", "UNKNOWN KC")]
    [InlineData("PG_UP", "PG UP")]
    [InlineData("PG_DN", "PG DN")]
    public void Display_UnknownKeycodesFallThroughWithUnderscoresAsSpaces(string input, string expected)
    {
        Assert.Equal(expected, ZmkKeycodeLabel.Display(input, ModifierStyle.Mac));
    }

    [Fact]
    public void Display_TabRendersAsGlyph()
    {
        Assert.Equal("⇥", ZmkKeycodeLabel.Display("TAB", ModifierStyle.Mac));
    }

    [Theory]
    [InlineData(ModifierStyle.Mac, "⌘")]
    [InlineData(ModifierStyle.Windows, "⊞")]
    public void Display_GuiKeycode_RendersPerStyle(ModifierStyle style, string expected)
    {
        Assert.Equal(expected, ZmkKeycodeLabel.Display("LGUI", style));
    }

    [Theory]
    [InlineData("LSHFT", "⇪")]
    [InlineData("RSHFT", "⇪")]
    [InlineData("LCTRL", "⌃")]
    [InlineData("RCTRL", "⌃")]
    [InlineData("LALT", "⌥")]
    [InlineData("RALT", "⌥")]
    [InlineData("LGUI", "⌘")]
    [InlineData("RGUI", "⌘")]
    public void ModifierSubscript_MapsModifierKeycodes(string input, string expected)
    {
        Assert.Equal(expected, ZmkKeycodeLabel.ModifierSubscript(input, ModifierStyle.Mac));
    }

    [Theory]
    [InlineData("TAB")]
    [InlineData("A")]
    [InlineData("SHIFT")]
    [InlineData("")]
    public void ModifierSubscript_NonModifierReturnsNull(string input)
    {
        Assert.Null(ZmkKeycodeLabel.ModifierSubscript(input, ModifierStyle.Mac));
    }

    [Theory]
    [InlineData("LBKT", "{")]
    [InlineData("RBKT", "}")]
    [InlineData("BSLH", "|")]
    [InlineData("SEMI", ":")]
    [InlineData("SQT", "\"")]
    [InlineData("COMMA", "<")]
    [InlineData("DOT", ">")]
    [InlineData("FSLH", "?")]
    [InlineData("GRAVE", "~")]
    [InlineData("N1", "!")]
    [InlineData("N0", ")")]
    [InlineData("NUMBER_7", "&")]
    [InlineData("MINUS", "_")]
    [InlineData("EQUAL", "+")]
    public void Shifted_MapsShiftableKeycodes(string input, string expected)
    {
        Assert.Equal(expected, ZmkKeycodeLabel.Shifted(input));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("TAB")]
    [InlineData("F1")]
    public void Shifted_UnshiftableReturnsNull(string input)
    {
        Assert.Null(ZmkKeycodeLabel.Shifted(input));
    }

    [Fact]
    public void FormatKpParams_PlainKeycode()
    {
        var (label, sub) = ZmkKeycodeLabel.FormatKpParams(new[] { "FSLH" }, ModifierStyle.Mac);
        Assert.Equal("/", label);
        Assert.Equal("", sub);
    }

    [Fact]
    public void FormatKpParams_ShiftedSymbolCollapses()
    {
        var (label, sub) = ZmkKeycodeLabel.FormatKpParams(new[] { "LS", "LBKT" }, ModifierStyle.Mac);
        Assert.Equal("{", label);
        Assert.Equal("", sub);
    }

    [Fact]
    public void FormatKpParams_RightShiftedSymbolCollapses()
    {
        var (label, sub) = ZmkKeycodeLabel.FormatKpParams(new[] { "RS", "N1" }, ModifierStyle.Mac);
        Assert.Equal("!", label);
        Assert.Equal("", sub);
    }

    [Fact]
    public void FormatKpParams_CtrlCombo_RendersAsSubscript()
    {
        var (label, sub) = ZmkKeycodeLabel.FormatKpParams(new[] { "LC", "V" }, ModifierStyle.Mac);
        Assert.Equal("V", label);
        Assert.Equal("⌃", sub);
    }

    [Fact]
    public void FormatKpParams_ChainedModifiers_RenderAllInSubscript()
    {
        var (label, sub) = ZmkKeycodeLabel.FormatKpParams(new[] { "LS", "LC", "V" }, ModifierStyle.Mac);
        Assert.Equal("V", label);
        Assert.Equal("⇪⌃", sub);
    }

    [Fact]
    public void FormatKpParams_ShiftOnNonShiftable_FallsBackToSubscript()
    {
        // LS(A) — "A" has no shifted glyph, so render as A with ⇧ subscript.
        var (label, sub) = ZmkKeycodeLabel.FormatKpParams(new[] { "LS", "A" }, ModifierStyle.Mac);
        Assert.Equal("A", label);
        Assert.Equal("⇪", sub);
    }

    [Theory]
    [InlineData("LSHFT", "⇧")]
    [InlineData("RSHFT", "⇧")]
    [InlineData("LCTRL", "Ctrl")]
    [InlineData("RCTRL", "Ctrl")]
    [InlineData("LALT", "Alt")]
    [InlineData("RALT", "Alt")]
    [InlineData("LGUI", "⊞")]
    [InlineData("RGUI", "⊞")]
    [InlineData("LEFT_COMMAND", "⊞")]
    [InlineData("LWIN", "⊞")]
    public void Display_WindowsStyle_UsesWindowsGlyphs(string input, string expected)
    {
        Assert.Equal(expected, ZmkKeycodeLabel.Display(input, ModifierStyle.Windows));
    }

    [Theory]
    [InlineData("LSHFT", "⇧")]
    [InlineData("LCTRL", "Ctrl")]
    [InlineData("LALT", "Alt")]
    [InlineData("LGUI", "⊞")]
    public void ModifierSubscript_WindowsStyle_UsesWindowsGlyphs(string input, string expected)
    {
        Assert.Equal(expected, ZmkKeycodeLabel.ModifierSubscript(input, ModifierStyle.Windows));
    }

    [Theory]
    [InlineData("LS", "⇧")]
    [InlineData("RS", "⇧")]
    [InlineData("LC", "Ctrl")]
    [InlineData("LA", "Alt")]
    [InlineData("LG", "⊞")]
    [InlineData("RG", "⊞")]
    public void ShortModifierGlyph_WindowsStyle(string input, string expected)
    {
        Assert.Equal(expected, ZmkKeycodeLabel.ShortModifierGlyph(input, ModifierStyle.Windows));
    }

    [Theory]
    [InlineData("LS", "⇪")]
    [InlineData("LC", "⌃")]
    [InlineData("LA", "⌥")]
    [InlineData("LG", "⌘")]
    public void ShortModifierGlyph_MacStyle(string input, string expected)
    {
        Assert.Equal(expected, ZmkKeycodeLabel.ShortModifierGlyph(input, ModifierStyle.Mac));
    }

    [Fact]
    public void FormatKpParams_WindowsStyle_CtrlComboRendersAsCtrlSubscript()
    {
        var (label, sub) = ZmkKeycodeLabel.FormatKpParams(new[] { "LC", "V" }, ModifierStyle.Windows);
        Assert.Equal("V", label);
        Assert.Equal("Ctrl", sub);
    }

    [Fact]
    public void FormatKpParams_WindowsStyle_GuiComboRendersAsWinSubscript()
    {
        var (label, sub) = ZmkKeycodeLabel.FormatKpParams(new[] { "LG", "L" }, ModifierStyle.Windows);
        Assert.Equal("L", label);
        Assert.Equal("⊞", sub);
    }

    [Fact]
    public void FormatKpParams_WindowsStyle_ShiftedSymbolStillCollapses()
    {
        // The "single shift over a shiftable key" branch is style-independent —
        // LS(LBKT) renders the literal '{' on both Mac and Windows.
        var (label, sub) = ZmkKeycodeLabel.FormatKpParams(new[] { "LS", "LBKT" }, ModifierStyle.Windows);
        Assert.Equal("{", label);
        Assert.Equal("", sub);
    }

    [Fact]
    public void FormatKpParams_WindowsStyle_ChainedModifiers()
    {
        var (label, sub) = ZmkKeycodeLabel.FormatKpParams(new[] { "LC", "LS", "T" }, ModifierStyle.Windows);
        Assert.Equal("T", label);
        Assert.Equal("Ctrl⇧", sub);
    }

    [Theory]
    [InlineData("Windows", ModifierStyle.Windows)]
    [InlineData("windows", ModifierStyle.Windows)]
    [InlineData("Mac", ModifierStyle.Mac)]
    [InlineData(null, ModifierStyle.Mac)]
    [InlineData("", ModifierStyle.Mac)]
    [InlineData("Linux", ModifierStyle.Mac)]
    public void ModifierStyles_FromSetting(string? value, ModifierStyle expected)
    {
        Assert.Equal(expected, ModifierStyles.FromSetting(value));
    }
}
