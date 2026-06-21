using LViz.Core.Keymap;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// ZMK pointing behaviors (<c>&amp;mmv</c>, <c>&amp;msc</c>, <c>&amp;mkp</c>)
/// and the zero-arity word behaviors render dedicated labels and badges
/// instead of falling back to raw parameter text.
/// </summary>
public class KeyLabelFormatterPointingTests
{
    [Theory]
    [InlineData("MOVE_UP", "↑")]
    [InlineData("MOVE_DOWN", "↓")]
    [InlineData("MOVE_LEFT", "←")]
    [InlineData("MOVE_RIGHT", "→")]
    public void MouseMove_RendersArrowWithMouseBadge(string param, string expected)
    {
        var (label, _, top) = KeyLabelFormatter.FormatBinding(
            new KeyBinding("&mmv", new[] { param }), targetLayerName: null);
        Assert.Equal(expected, label);
        Assert.Equal("Mouse", top);
    }

    [Theory]
    [InlineData("SCRL_UP", "↑")]
    [InlineData("SCRL_DOWN", "↓")]
    [InlineData("SCRL_LEFT", "←")]
    [InlineData("SCRL_RIGHT", "→")]
    public void MouseScroll_RendersArrowWithScrollBadge(string param, string expected)
    {
        var (label, _, top) = KeyLabelFormatter.FormatBinding(
            new KeyBinding("&msc", new[] { param }), targetLayerName: null);
        Assert.Equal(expected, label);
        Assert.Equal("Scroll", top);
    }

    [Theory]
    [InlineData("LCLK", "L Clk")]
    [InlineData("RCLK", "R Clk")]
    [InlineData("MCLK", "M Clk")]
    [InlineData("MB4", "M4")]
    [InlineData("MB5", "M5")]
    public void MouseButton_RendersClickLabel(string param, string expected)
    {
        var (label, _, top) = KeyLabelFormatter.FormatBinding(
            new KeyBinding("&mkp", new[] { param }), targetLayerName: null);
        Assert.Equal(expected, label);
        Assert.Equal("Click", top);
    }

    [Fact]
    public void CapsWord_RendersTitleCaseLabel()
    {
        var (label, _, _) = KeyLabelFormatter.FormatBinding(
            new KeyBinding("&caps_word", Array.Empty<string>()), targetLayerName: null);
        Assert.Equal("Caps Word", label);
    }

    [Fact]
    public void KeyRepeat_RendersRepeatLabel()
    {
        var (label, _, _) = KeyLabelFormatter.FormatBinding(
            new KeyBinding("&key_repeat", Array.Empty<string>()), targetLayerName: null);
        Assert.Equal("Repeat", label);
    }
}
