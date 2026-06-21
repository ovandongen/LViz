using LViz.Core.Keymap;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// User-authored <c>zmk,behavior-macro</c> nodes (e.g. "BracketC" that types
/// <c>[ ] ←</c>) should render the concatenated keycodes the macro produces,
/// not the user's arbitrary macro name. Tested via the formatter with an
/// explicit macro map (matching what <c>MainWindowViewModel</c> threads
/// through per active-layer apply).
/// </summary>
public class UserMacroLabelTests
{
    [Fact]
    public void BracketC_RendersAsConcatenatedKeycodes()
    {
        var src = @"
            / {
                macros {
                    BracketC: BracketC {
                        compatible = ""zmk,behavior-macro"";
                        #binding-cells = <0>;
                        bindings = <&kp LEFT_BRACKET &kp RIGHT_BRACKET &kp LEFT>;
                        label = ""BRACKETC"";
                    };
                };
                keymap {
                    compatible = ""zmk,keymap"";
                    default_layer { bindings = <&BracketC>; };
                };
            };";
        var config = ZmkKeymapLoader.LoadFromText(src, "test");
        var macros = config.Macros.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var binding = config.Layers[0].Bindings[0];

        var (label, sub, top) = KeyLabelFormatter.FormatBinding(binding, null, holdTap: null, macros: macros);

        Assert.Equal("[]←", label);
        Assert.Equal("", sub);
        Assert.Equal("Macro", top);
    }

    [Fact]
    public void CurlyC_HandlesShiftedKeycodesInMacroBody()
    {
        var src = @"
            / {
                macros {
                    CurlyC: CurlyC {
                        compatible = ""zmk,behavior-macro"";
                        #binding-cells = <0>;
                        bindings = <&kp LS(LEFT_BRACKET) &kp LS(RIGHT_BRACKET) &kp LEFT>;
                    };
                };
                keymap {
                    compatible = ""zmk,keymap"";
                    default_layer { bindings = <&CurlyC>; };
                };
            };";
        var config = ZmkKeymapLoader.LoadFromText(src, "test");
        var macros = config.Macros.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var binding = config.Layers[0].Bindings[0];

        var (label, _, top) = KeyLabelFormatter.FormatBinding(binding, null, holdTap: null, macros: macros);

        Assert.Equal("{}←", label);
        Assert.Equal("Macro", top);
    }

    [Fact]
    public void NonKpMacroBody_FallsBackToMacroName()
    {
        // A pairing macro whose body is &bt + &out — no &kp at all — should
        // not produce an empty label. Fall back to the macro name.
        var src = @"
            / {
                macros {
                    pair_phone: pair_phone {
                        compatible = ""zmk,behavior-macro"";
                        #binding-cells = <0>;
                        bindings = <&bt BT_SEL 0 &out OUT_BLE>;
                    };
                };
                keymap {
                    compatible = ""zmk,keymap"";
                    default_layer { bindings = <&pair_phone>; };
                };
            };";
        var config = ZmkKeymapLoader.LoadFromText(src, "test");
        var macros = config.Macros.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var binding = config.Layers[0].Bindings[0];

        var (label, _, top) = KeyLabelFormatter.FormatBinding(binding, null, holdTap: null, macros: macros);

        Assert.Equal("pair phone", label);  // fallback formatter splits underscores
        Assert.Equal("", top);
    }

    [Fact]
    public void SingleBindingNonKpMacro_RendersInnerBindingLabel()
    {
        // Moergo's rgb_ug_status_macro pattern: the body is one non-&kp
        // binding. The key should show the inner binding's label ("Status"),
        // not the macro's long node name.
        var src = @"
            / {
                macros {
                    rgb_ug_status_macro: rgb_ug_status_macro {
                        compatible = ""zmk,behavior-macro"";
                        #binding-cells = <0>;
                        bindings = <&rgb_ug RGB_STATUS>;
                    };
                };
                keymap {
                    compatible = ""zmk,keymap"";
                    default_layer { bindings = <&rgb_ug_status_macro>; };
                };
            };";
        var config = ZmkKeymapLoader.LoadFromText(src, "test");
        var macros = config.Macros.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var binding = config.Layers[0].Bindings[0];

        var (label, _, top) = KeyLabelFormatter.FormatBinding(binding, null, holdTap: null, macros: macros);

        Assert.Equal("Status", label);
        Assert.Equal("Macro", top);
    }

    [Fact]
    public void NoMacrosArgument_DoesNotChangeFallbackBehavior()
    {
        var binding = new KeyBinding("&BracketC", Array.Empty<string>());
        var (label, _, top) = KeyLabelFormatter.FormatBinding(binding, null);
        Assert.Equal("BracketC", label);
        Assert.Equal("", top);
    }
}
