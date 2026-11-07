using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

public class ZmkBindingFormatterTests
{
    private static IReadOnlyList<KeyValuePair<string, string>> NoDefines =>
        Array.Empty<KeyValuePair<string, string>>();

    /// <summary>A layer define for 3 and one for 1, file order.</summary>
    private static readonly List<KeyValuePair<string, string>> LayerDefines =
    [
        new("LAYER_Magic", "3"),
        new("LAYER_Sym", "1"),
    ];

    /// <summary><c>&amp;magic</c> holds a layer and taps a 0-cell macro;
    /// <c>&amp;tl</c> taps a layer, so its layer param is the second cell.</summary>
    private static readonly List<HoldTap> HoldTaps =
    [
        new("&magic", "&mo", "&m0", 1, 0),
        new("&tl", "&kp", "&mo", 1, 1),
    ];

    private static string Format(string behavior, params string[] parameters) =>
        ZmkBindingFormatter.Format(new KeyBinding(behavior, parameters), NoDefines);

    private static string FormatWithHoldTaps(string behavior, params string[] parameters) =>
        ZmkBindingFormatter.Format(new KeyBinding(behavior, parameters), LayerDefines, HoldTaps);

    [Theory]
    [InlineData("&kp", new[] { "LS", "N7" }, "&kp LS(N7)")]
    [InlineData("&kp", new[] { "LC", "LS", "N7" }, "&kp LC(LS(N7))")]
    [InlineData("&mt", new[] { "LSHFT", "A" }, "&mt LSHFT A")]
    [InlineData("&mt", new[] { "LCTRL", "LS", "A" }, "&mt LCTRL LS(A)")]
    [InlineData("&bt", new[] { "BT_SEL", "0" }, "&bt BT_SEL 0")]
    [InlineData("&trans", new string[0], "&trans")]
    [InlineData("&kp", new[] { "LS" }, "&kp LS")]
    [InlineData("&kp", new[] { "(LC(LSFT))" }, "&kp (LC(LSFT))")]
    public void Format_ReNestsModifierFunctionsAndKeepsOtherParams(
        string behavior, string[] parameters, string expected)
    {
        Assert.Equal(expected, Format(behavior, parameters));
    }

    [Theory]
    [InlineData("&mo")]
    [InlineData("&to")]
    [InlineData("&tog")]
    [InlineData("&sl")]
    public void Format_ReplacesLayerIndexWithFirstMatchingDefineName(string behavior)
    {
        var defines = new List<KeyValuePair<string, string>>
        {
            new("LOWER", "1"),
            new("BACK", "1"),
        };
        var formatted = ZmkBindingFormatter.Format(
            new KeyBinding(behavior, new[] { "1" }), defines);
        Assert.Equal(behavior + " LOWER", formatted);
    }

    [Fact]
    public void Format_ReplacesOnlyFirstParamOfLayerBinding()
    {
        var defines = new List<KeyValuePair<string, string>> { new("LOWER", "1") };
        var formatted = ZmkBindingFormatter.Format(
            new KeyBinding("&lt", new[] { "1", "SPACE" }), defines);
        Assert.Equal("&lt LOWER SPACE", formatted);
    }

    [Fact]
    public void Format_KeepsLayerIndexWhenNoDefineMatches()
    {
        Assert.Equal("&mo 1", Format("&mo", "1"));
    }

    [Fact]
    public void Format_DoesNotReplaceIndexesOfNonLayerBehaviors()
    {
        var defines = new List<KeyValuePair<string, string>> { new("LOWER", "1") };
        var formatted = ZmkBindingFormatter.Format(
            new KeyBinding("&kp", new[] { "1" }), defines);
        Assert.Equal("&kp 1", formatted);
    }

    // ---- hold-tap layer params --------------------------------------------------

    [Fact]
    public void Format_ReplacesHoldTapHoldSideLayerParamWithDefineName()
    {
        // &magic holds &mo (arity 1) and taps a 0-cell macro: only the first
        // param is a layer reference, the trailing 0 stays.
        Assert.Equal("&magic LAYER_Magic 0", FormatWithHoldTaps("&magic", "3", "0"));
    }

    [Fact]
    public void Format_ReplacesHoldTapTapSideLayerParamWithDefineName()
    {
        // &tl taps &mo, so its layer number stands at index HoldArity (1).
        Assert.Equal("&tl A LAYER_Sym", FormatWithHoldTaps("&tl", "A", "1"));
    }

    [Fact]
    public void Format_KeepsHoldTapLayerIndexWhenNoDefineMatches()
    {
        Assert.Equal("&magic 5 0", FormatWithHoldTaps("&magic", "5", "0"));
    }

    [Fact]
    public void Format_WithoutHoldTaps_KeepsHoldTapLayerIndex()
    {
        // The overload without hold-taps knows only the built-in layer
        // behaviors, so a hold-tap keeps its number.
        Assert.Equal(
            "&magic 3 0",
            ZmkBindingFormatter.Format(new KeyBinding("&magic", new[] { "3", "0" }), LayerDefines));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Format_ReplacesLayerBehaviorParamWithEitherOverload(bool withHoldTaps)
    {
        var binding = new KeyBinding("&mo", new[] { "1" });
        var formatted = withHoldTaps
            ? ZmkBindingFormatter.Format(binding, LayerDefines, HoldTaps)
            : ZmkBindingFormatter.Format(binding, LayerDefines);
        Assert.Equal("&mo LAYER_Sym", formatted);
    }

    [Fact]
    public void Format_WithHoldTaps_StillReNestsModifiers()
    {
        Assert.Equal("&kp LS(A)", FormatWithHoldTaps("&kp", "LS", "A"));
    }

    [Fact]
    public void Format_BuiltInLayerBehaviorWinsOverSameNamedHoldTap()
    {
        // A hold-tap named like a built-in never takes over the cell: the
        // built-in's rule (param 0 only) applies.
        var holdTaps = new List<HoldTap> { new("&lt", "&mo", "&mo", 1, 1) };
        var formatted = ZmkBindingFormatter.Format(
            new KeyBinding("&lt", new[] { "1", "SPACE" }), LayerDefines, holdTaps);
        Assert.Equal("&lt LAYER_Sym SPACE", formatted);
    }

    [Fact]
    public void Format_HoldTapIgnoresLayerParamIndexPastTheCells()
    {
        // &tl's tap side would be param 1; this use has one cell, so nothing
        // is replaced and nothing throws.
        Assert.Equal("&tl A", FormatWithHoldTaps("&tl", "A"));
    }

    [Theory]
    [InlineData("kp")]
    [InlineData("& kp")]
    public void Format_RejectsInvalidBehavior(string behavior)
    {
        Assert.Throws<ArgumentException>(() =>
            ZmkBindingFormatter.Format(new KeyBinding(behavior, Array.Empty<string>()), NoDefines));
    }

    [Theory]
    [InlineData("minimal.keymap")]
    [InlineData("corne.keymap")]
    [InlineData("with_combos_macros.keymap")]
    [InlineData("glove80-editor.keymap")]
    public void Format_RoundTripsEveryFixtureBinding(string fixture)
    {
        var original = ZmkKeymapLoader.LoadFromText(KeymapFixtures.Read(fixture), "test");
        var expected = original.Layers.SelectMany(layer => layer.Bindings).ToList();

        var formatted = expected.Select(b => ZmkBindingFormatter.Format(b, NoDefines));
        var text = "/ { keymap { compatible = \"zmk,keymap\"; l { bindings = < "
                   + string.Join(" ", formatted) + " >; }; }; };";

        var reparsed = ZmkKeymapLoader.LoadFromText(text, "test");
        var actual = reparsed.Layers.SelectMany(layer => layer.Bindings).ToList();

        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Behavior, actual[i].Behavior);
            // KeyBinding record equality does not compare list contents —
            // compare the param sequences explicitly.
            Assert.True(
                expected[i].Params.SequenceEqual(actual[i].Params),
                $"Binding {i} ({expected[i].Behavior}): [{string.Join(", ", expected[i].Params)}] "
                + $"/ [{string.Join(", ", actual[i].Params)}]");
        }
    }
}
