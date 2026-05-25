using LViz.Core.Keymap.Parser;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// Moergo's keymap exporter names layer nodes <c>layer_&lt;Name&gt;</c>
/// without a <c>display-name</c>. The interpreter strips the redundant
/// <c>layer_</c> prefix when falling back to the node name; an explicit
/// <c>display-name</c> still wins.
/// </summary>
public class LayerNamePrefixStripTests
{
    [Fact]
    public void StripsLayerPrefix_WhenNoDisplayNameSet()
    {
        var src = @"
            / {
                keymap {
                    compatible = ""zmk,keymap"";
                    layer_Symbol { bindings = <&trans>; };
                    layer_Mouse  { bindings = <&trans>; };
                };
            };";
        var config = ZmkKeymapLoader.LoadFromText(src, "test");
        Assert.Equal("Symbol", config.Layers[0].Name);
        Assert.Equal("Mouse", config.Layers[1].Name);
    }

    [Fact]
    public void ExplicitDisplayNameWins_OverNodeNamePrefix()
    {
        var src = @"
            / {
                keymap {
                    compatible = ""zmk,keymap"";
                    layer_Symbol {
                        display-name = ""Symbols!"";
                        bindings = <&trans>;
                    };
                };
            };";
        var config = ZmkKeymapLoader.LoadFromText(src, "test");
        Assert.Equal("Symbols!", config.Layers[0].Name);
    }

    [Fact]
    public void NonLayerPrefixedNodeNames_PassThrough()
    {
        var src = @"
            / {
                keymap {
                    compatible = ""zmk,keymap"";
                    default_layer { bindings = <&trans>; };
                    lower         { bindings = <&trans>; };
                };
            };";
        var config = ZmkKeymapLoader.LoadFromText(src, "test");
        Assert.Equal("default_layer", config.Layers[0].Name);
        Assert.Equal("lower", config.Layers[1].Name);
    }
}
