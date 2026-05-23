using LViz.Core.Keymap;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// Pins formatting of ZMK built-in <c>&amp;mt</c> (mod-tap) bindings. The
/// behavior isn't a user-defined hold-tap node so it has to be handled
/// directly in the standard-behavior switch.
/// </summary>
public class KeyLabelFormatterModTapTests
{
    [Fact]
    public void Mt_RaltGrave_ShowsBacktickWithAltSubscript()
    {
        // From docs/corne.keymap line 90: `&mt RALT GRAVE`.
        ZmkKeycodeLabel.CurrentModifierStyle = ModifierStyle.Mac;
        var (label, sub, top) = KeyLabelFormatter.FormatBinding(
            new KeyBinding("&mt", new[] { "RALT", "GRAVE" }), targetLayerName: null);
        Assert.Equal("`", label);
        Assert.Equal("⌥", sub);
        Assert.Equal("Mod-Tap", top);
    }

    [Fact]
    public void Mt_LshiftD_ShowsDWithShiftSubscript()
    {
        ZmkKeycodeLabel.CurrentModifierStyle = ModifierStyle.Mac;
        var (label, sub, top) = KeyLabelFormatter.FormatBinding(
            new KeyBinding("&mt", new[] { "LSHFT", "D" }), targetLayerName: null);
        Assert.Equal("D", label);
        Assert.Equal("⇪", sub);
        Assert.Equal("Mod-Tap", top);
    }

    [Fact]
    public void Mt_LshiftAlias_ResolvesToShiftGlyph()
    {
        // LSHIFT (vs LSHFT) appears in many real-world keymaps including the
        // corne file shipped in docs/. Both spellings must collapse to the
        // shift glyph in the subscript.
        ZmkKeycodeLabel.CurrentModifierStyle = ModifierStyle.Mac;
        var (_, sub, _) = KeyLabelFormatter.FormatBinding(
            new KeyBinding("&mt", new[] { "LSHIFT", "F" }), targetLayerName: null);
        Assert.Equal("⇪", sub);
    }

    [Fact]
    public void Mt_WindowsStyle_ShowsAltText()
    {
        ZmkKeycodeLabel.CurrentModifierStyle = ModifierStyle.Windows;
        try
        {
            var (_, sub, _) = KeyLabelFormatter.FormatBinding(
                new KeyBinding("&mt", new[] { "RALT", "GRAVE" }), targetLayerName: null);
            Assert.Equal("Alt", sub);
        }
        finally
        {
            ZmkKeycodeLabel.CurrentModifierStyle = ModifierStyle.Mac;
        }
    }
}
