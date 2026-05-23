using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// End-to-end slicer tests: a one-layer keymap is parsed and the resulting
/// <see cref="Layer.Bindings"/> are inspected. Exercises the arity table,
/// the greedy fallback for unknown behaviors, and the <c>&amp;bt</c> wart.
/// </summary>
public class BehaviorAritySlicerTests
{
    private static IReadOnlyList<KeyBinding> SliceLayer(string bindingsCellArray)
    {
        var src = $@"
            / {{
                keymap {{
                    compatible = ""zmk,keymap"";
                    default_layer {{
                        bindings = <{bindingsCellArray}>;
                    }};
                }};
            }};";
        var config = ZmkKeymapLoader.LoadFromText(src, "test");
        return config.Layers[0].Bindings;
    }

    [Fact]
    public void SlicesMixedKnownBehaviors()
    {
        var bindings = SliceLayer("&kp A &kp B &mo 1 &lt 2 SPACE &mt LSHFT C");
        Assert.Equal(5, bindings.Count);

        Assert.Equal("&kp", bindings[0].Behavior);
        Assert.Equal(new[] { "A" }, bindings[0].Params);

        Assert.Equal("&kp", bindings[1].Behavior);
        Assert.Equal(new[] { "B" }, bindings[1].Params);

        Assert.Equal("&mo", bindings[2].Behavior);
        Assert.Equal(new[] { "1" }, bindings[2].Params);

        Assert.Equal("&lt", bindings[3].Behavior);
        Assert.Equal(new[] { "2", "SPACE" }, bindings[3].Params);

        Assert.Equal("&mt", bindings[4].Behavior);
        Assert.Equal(new[] { "LSHFT", "C" }, bindings[4].Params);
    }

    [Fact]
    public void TransAndNoneTakeNoParams()
    {
        var bindings = SliceLayer("&trans &none &kp A");
        Assert.Equal(3, bindings.Count);
        Assert.Equal("&trans", bindings[0].Behavior);
        Assert.Empty(bindings[0].Params);
        Assert.Equal("&none", bindings[1].Behavior);
        Assert.Empty(bindings[1].Params);
        Assert.Equal("&kp", bindings[2].Behavior);
    }

    [Fact]
    public void UnknownBehaviorFallsBackToGreedy()
    {
        // &xxx is unknown — consumes all non-ref tokens until the next ref.
        var bindings = SliceLayer("&xxx LSHFT A &kp B");
        Assert.Equal(2, bindings.Count);
        Assert.Equal("&xxx", bindings[0].Behavior);
        Assert.Equal(new[] { "LSHFT", "A" }, bindings[0].Params);
        Assert.Equal("&kp", bindings[1].Behavior);
    }

    [Fact]
    public void BtSelTakesTwoParamsWhileBtClrTakesOne()
    {
        var bindings = SliceLayer("&bt BT_SEL 0 &bt BT_CLR");
        Assert.Equal(2, bindings.Count);
        Assert.Equal(new[] { "BT_SEL", "0" }, bindings[0].Params);
        Assert.Equal(new[] { "BT_CLR" }, bindings[1].Params);
    }

    [Fact]
    public void ModifierWrappedKeycodeSurvivesAsSingleParam()
    {
        // (LC(LSFT)) should be one param, not multiple tokens.
        var bindings = SliceLayer("&kp (LC(LSFT))");
        Assert.Single(bindings);
        var param = Assert.Single(bindings[0].Params);
        Assert.Contains("LC", param);
        Assert.Contains("LSFT", param);
    }
}
