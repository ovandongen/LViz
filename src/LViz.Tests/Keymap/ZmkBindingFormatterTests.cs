using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

public class ZmkBindingFormatterTests
{
    private static IReadOnlyList<KeyValuePair<string, string>> NoDefines =>
        Array.Empty<KeyValuePair<string, string>>();

    private static string Format(string behavior, params string[] parameters) =>
        ZmkBindingFormatter.Format(new KeyBinding(behavior, parameters), NoDefines);

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
