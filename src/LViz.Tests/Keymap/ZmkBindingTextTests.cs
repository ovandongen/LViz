using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

public class ZmkBindingTextTests
{
    private static IReadOnlyList<KeyValuePair<string, string>> NoDefines =>
        Array.Empty<KeyValuePair<string, string>>();

    private static void AssertBinding(
        string text, string behavior, params string[] parameters)
        => AssertBindingWithConfig(null, text, behavior, parameters);

    private static void AssertBindingWithConfig(
        KeyboardConfig? config, string text, string behavior, params string[] parameters)
    {
        var binding = ZmkBindingText.TryParse(text, NoDefines, config);
        Assert.NotNull(binding);
        Assert.Equal(behavior, binding!.Behavior);
        Assert.Equal(parameters, binding.Params);
    }

    private static KeyboardConfig ConfigWith(HoldTap? holdTap = null, ZmkMacro? macro = null) =>
        new("test",
            [new Layer(0, "default", [new KeyBinding("&trans", Array.Empty<string>())])],
            macro is null ? [] : [macro],
            holdTap is null ? [] : [holdTap],
            []);

    [Theory]
    [InlineData("&kp A")]
    [InlineData("  &kp   A  ")]
    public void TryParse_ParsesSimpleKeycode(string text)
        => AssertBinding(text, "&kp", "A");

    [Theory]
    [InlineData("&kp LS(N1)", new[] { "LS", "N1" })]
    [InlineData("&kp LC(LS( N7 ))", new[] { "LC", "LS", "N7" })]
    public void TryParse_FlattensModifierWraps(string text, string[] parameters)
        => AssertBinding(text, "&kp", parameters);

    [Theory]
    [InlineData("&mt F1 N1", new[] { "F1", "N1" })]
    [InlineData("&mt LSHFT LS(A)", new[] { "LSHFT", "LS", "A" })]
    public void TryParse_ModTapKeepsLogicalParams(string text, string[] parameters)
        => AssertBinding(text, "&mt", parameters);

    [Fact]
    public void TryParse_BtSelTakesTwoParams()
        => AssertBinding("&bt BT_SEL 0", "&bt", "BT_SEL", "0");

    [Fact]
    public void TryParse_BtWithoutBtSelTakesOneParam()
        => AssertBinding("&bt BT_CLR", "&bt", "BT_CLR");

    [Theory]
    [InlineData("&out OUT_USB", "&out", "OUT_USB")]
    [InlineData("&rgb_ug RGB_TOG", "&rgb_ug", "RGB_TOG")]
    [InlineData("&mkp LCLK", "&mkp", "LCLK")]
    public void TryParse_SingleParamBehaviors(string text, string behavior, string parameter)
        => AssertBinding(text, behavior, parameter);

    [Theory]
    [InlineData("&caps_word")]
    [InlineData("&bootloader")]
    [InlineData("&trans")]
    public void TryParse_ZeroParamBehaviors(string text)
    {
        var binding = ZmkBindingText.TryParse(text, NoDefines, null);
        Assert.NotNull(binding);
        Assert.Equal(text, binding!.Behavior);
        Assert.Empty(binding.Params);
    }

    [Fact]
    public void TryParse_ResolvesTheFilesOwnDefines()
    {
        var defines = new List<KeyValuePair<string, string>> { new("LAYER_Nav", "2") };
        var binding = ZmkBindingText.TryParse("&mo LAYER_Nav", defines, null);
        Assert.NotNull(binding);
        Assert.Equal("&mo", binding!.Behavior);
        Assert.Equal(new[] { "2" }, binding.Params);
    }

    [Fact]
    public void TryParse_UnknownBehaviorAcceptsAnyParamCount()
        => AssertBinding("&foo 1 2", "&foo", "1", "2");

    [Fact]
    public void TryParse_HoldTapFromConfigTakesTwoParams()
    {
        var config = ConfigWith(holdTap: new HoldTap("&hm", "&kp", "&kp", 1, 1));
        AssertBindingWithConfig(config, "&hm LSHFT A", "&hm", "LSHFT", "A");
    }

    [Fact]
    public void TryParse_HoldTapWithTooFewParams_IsNull()
    {
        var config = ConfigWith(holdTap: new HoldTap("&hm", "&kp", "&kp", 1, 1));
        Assert.Null(ZmkBindingText.TryParse("&hm A", NoDefines, config));
    }

    [Fact]
    public void TryParse_MacroFromConfigTakesItsParamCount()
    {
        var config = ConfigWith(macro: new ZmkMacro("&m1", ["p1"], Array.Empty<KeyBinding>()));
        AssertBindingWithConfig(config, "&m1 X", "&m1", "X");
    }

    [Fact]
    public void TryParse_MacroWithMissingParam_IsNull()
    {
        var config = ConfigWith(macro: new ZmkMacro("&m1", ["p1"], Array.Empty<KeyBinding>()));
        Assert.Null(ZmkBindingText.TryParse("&m1", NoDefines, config));
    }

    [Fact]
    public void TryParse_BtAloneWithNoParams_IsNull()
    {
        // &bt is special-cased to 1 or 2 params (rule 7).
        Assert.Null(ZmkBindingText.TryParse("&bt", NoDefines, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kp A")]
    [InlineData("&kp A &kp B")]
    [InlineData("&kp A B")]
    [InlineData("&kp")]
    [InlineData("&lt 1")]
    [InlineData("&kp LS(A")]
    [InlineData("&kp A)")]
    [InlineData("&kp A; &kp B")]
    [InlineData("&kp A>")]
    [InlineData("&kp A /* x */")]
    [InlineData("&kp A\n&kp B")]
    [InlineData("&kp \"A\"")]
    public void TryParse_NotExactlyOneValidBinding_IsNull(string? text)
        => Assert.Null(ZmkBindingText.TryParse(text, NoDefines, null));

    [Fact]
    public void TryParse_NullDefines_Throws()
        => Assert.Throws<ArgumentNullException>(() => ZmkBindingText.TryParse("&kp A", null!, null));
}
