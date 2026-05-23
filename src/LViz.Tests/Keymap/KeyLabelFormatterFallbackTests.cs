using LViz.Core.Keymap;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// Pins the underscore-to-space conversion in fallback label paths. Long
/// labels like <c>my_macro</c> or <c>FOO_BAR_BAZ</c> must wrap on space
/// breaks inside a 60-pixel key cap.
/// </summary>
public class KeyLabelFormatterFallbackTests
{
    [Fact]
    public void UnknownZeroArityBehavior_UnderscoresBecomeSpaces()
    {
        var (label, _, _) = KeyLabelFormatter.FormatBinding(
            new KeyBinding("&my_macro", Array.Empty<string>()), targetLayerName: null);
        Assert.Equal("my macro", label);
    }

    [Fact]
    public void UnknownBehaviorWithParams_UnderscoresBecomeSpaces()
    {
        var (label, _, _) = KeyLabelFormatter.FormatBinding(
            new KeyBinding("&xxx", new[] { "FOO_BAR" }), targetLayerName: null);
        Assert.Equal("FOO BAR", label);
    }
}
