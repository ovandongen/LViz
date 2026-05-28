using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// Locks the resolution kernel that <c>MainWindowViewModel</c> previously
/// inlined (and duplicated) at every render and edit-dialog open.
/// </summary>
public class LoadedKeymapTests
{
    // Layer 0 pushes layer 1 via &mo 1, so layer 1's &trans keys fall through
    // to layer 0. Index 2 falls through onto &none (a non-&trans stop).
    private const string Source = @"
        / {
            keymap {
                compatible = ""zmk,keymap"";
                default_layer {
                    display-name = ""Base"";
                    bindings = <&kp A &mo 1 &none>;
                };
                lower {
                    display-name = ""Lower"";
                    bindings = <&trans &kp Z &trans>;
                };
            };
        };";

    private static LoadedKeymap Load() =>
        new(ZmkKeymapLoader.LoadFromText(Source, "test"));

    [Fact]
    public void Resolve_LayerSwitchBinding_ReportsTargetLayerAndName()
    {
        var km = Load();
        var r = km.Resolve(0, 1); // &mo 1
        Assert.Equal("&mo", r.Binding.Behavior);
        Assert.Equal(1, r.TargetLayer);
        Assert.Equal("Lower", r.TargetLayerName);
        Assert.Null(r.HoldTap);
    }

    [Fact]
    public void Resolve_TransparentKey_FallsThroughToPredecessorLayer()
    {
        var km = Load();
        var r = km.Resolve(1, 0); // &trans → layer 0's &kp A
        Assert.Equal("&kp", r.Binding.Behavior);
        Assert.Equal("A", r.Binding.Params[0]);
    }

    [Fact]
    public void Resolve_TransparentKey_StopsOnNonTransparentFallThrough()
    {
        var km = Load();
        var r = km.Resolve(1, 2); // &trans → layer 0's &none
        Assert.Equal("&none", r.Binding.Behavior);
    }

    [Fact]
    public void Resolve_NonTransparentKey_ReturnsBindingDirectly()
    {
        var km = Load();
        var r = km.Resolve(1, 1); // &kp Z
        Assert.Equal("&kp", r.Binding.Behavior);
        Assert.Equal("Z", r.Binding.Params[0]);
        Assert.Null(r.TargetLayer);
    }

    [Fact]
    public void Resolve_OutOfRangeKey_YieldsTransparent()
    {
        var km = Load();
        var r = km.Resolve(0, 99);
        Assert.Equal("&trans", r.Binding.Behavior);
    }

    [Fact]
    public void ClassifyBinding_DistinguishesTransEmptyAndReal()
    {
        var km = Load();
        Assert.Equal(TransparentBindingKind.Transparent, km.ClassifyBinding(1, 0)); // literal &trans
        Assert.Equal(TransparentBindingKind.Empty, km.ClassifyBinding(0, 2));       // &none
        Assert.Null(km.ClassifyBinding(0, 0));                                       // &kp A
    }

    [Fact]
    public void ClassifyBinding_OutOfRange_ReturnsNull()
    {
        var km = Load();
        Assert.Null(km.ClassifyBinding(0, 99));
        Assert.Null(km.ClassifyBinding(99, 0));
    }
}
