using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

public class KeymapSourceMapTests
{
    private static string TextOf(string source, SourceSpan span) =>
        source.Substring(span.Start, span.Length);

    /// <summary>1-based line number containing the first occurrence of
    /// <paramref name="needle"/>.</summary>
    private static int LineOf(string source, string needle)
    {
        var lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(needle, StringComparison.Ordinal)) return i + 1;
        throw new InvalidOperationException($"needle not found: {needle}");
    }

    private static int LineOfOffset(string source, int offset) =>
        source[..offset].Count(c => c == '\n') + 1;

    private static (string Source, KeyboardConfig Config, IReadOnlyList<LayerSourceMap> Maps)
        BuildMap(string source)
    {
        var config = ZmkKeymapLoader.LoadFromText(source, "test");
        return (source, config, KeymapSourceMap.Build(source, config));
    }

    // ---- fixtures -----------------------------------------------------------

    [Theory]
    [InlineData("minimal.keymap")]
    [InlineData("corne.keymap")]
    [InlineData("with_combos_macros.keymap")]
    [InlineData("glove80-editor.keymap")]
    public void Build_MapsEveryLayerAndBinding_InAllFixtures(string fixture)
    {
        var source = KeymapFixtures.Read(fixture);
        var config = ZmkKeymapLoader.LoadFromText(source, "test");

        var maps = KeymapSourceMap.Build(source, config);

        Assert.Equal(config.Layers.Count, maps.Count);
        for (int i = 0; i < maps.Count; i++)
        {
            var map = maps[i];
            var layer = config.Layers[i];
            Assert.Equal(i, map.LayerIndex);
            Assert.Equal(layer.Bindings.Count, map.Bindings.Count);
            Assert.Equal("bindings", source.Substring(map.BindingsKeywordOffset, 8));
            Assert.Equal(map.BindingsLine, LineOfOffset(source, map.BindingsKeywordOffset));

            for (int k = 0; k < map.Bindings.Count; k++)
            {
                var span = map.Bindings[k];
                Assert.InRange(span.Start, 0, source.Length - 1);
                Assert.True(span.Start + span.Length <= source.Length);
                Assert.StartsWith(layer.Bindings[k].Behavior, TextOf(source, span),
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Build_Minimal_BindingSpansAndDisplayName()
    {
        var (source, config, maps) = BuildMap(KeymapFixtures.Read("minimal.keymap"));
        var map = maps[0];

        Assert.Equal("&kp A", TextOf(source, map.Bindings[0]));

        int lt = -1;
        for (int k = 0; k < config.Layers[0].Bindings.Count; k++)
            if (config.Layers[0].Bindings[k].Behavior == "&lt") lt = k;
        Assert.True(lt >= 0, "no &lt binding parsed");
        Assert.Equal("&lt 1 SPACE", TextOf(source, map.Bindings[lt]));

        Assert.NotNull(map.DisplayNameValue);
        Assert.Equal("Base", TextOf(source, map.DisplayNameValue!.Value));
    }

    [Fact]
    public void Build_Glove80_NoDisplayNames_NestedParamSpan()
    {
        var (source, config, maps) = BuildMap(KeymapFixtures.Read("glove80-editor.keymap"));

        Assert.All(maps, map => Assert.Null(map.DisplayNameValue));

        int symbol = -1;
        for (int i = 0; i < config.Layers.Count; i++)
            if (config.Layers[i].Name == "Symbol") symbol = i;
        Assert.True(symbol >= 0, "no Symbol layer parsed");

        int k = -1;
        for (int i = 0; i < config.Layers[symbol].Bindings.Count; i++)
        {
            var b = config.Layers[symbol].Bindings[i];
            if (b.Behavior == "&kp" && b.Params.SequenceEqual(new[] { "LS", "N7" })) { k = i; break; }
        }
        Assert.True(k >= 0, "no &kp LS(N7) binding parsed");
        Assert.Equal("&kp LS(N7)", TextOf(source, maps[symbol].Bindings[k]));
    }

    // ---- inline sources -------------------------------------------------------

    [Fact]
    public void Build_IgnoresCommentsInsideArray()
    {
        var source = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <
                            // row 1
                            &kp A  /* mid */ &kp B
                        >;
                    };
                };
            };
            """;
        var (src, config, maps) = BuildMap(source);

        var map = maps[0];
        Assert.Equal(2, config.Layers[0].Bindings.Count);
        Assert.Equal(2, map.Bindings.Count);
        Assert.Equal("&kp A", TextOf(src, map.Bindings[0]));
        Assert.Equal("&kp B", TextOf(src, map.Bindings[1]));
        Assert.All(map.Bindings, span =>
        {
            var text = TextOf(src, span);
            Assert.DoesNotContain("/", text, StringComparison.Ordinal);
            Assert.DoesNotContain("row", text, StringComparison.Ordinal);
            Assert.DoesNotContain("mid", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Build_MapsObjectLikeDefineBinding()
    {
        var source = """
            #define XXX &none

            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <
                            &kp A
                            XXX
                        >;
                    };
                };
            };
            """;
        var (src, config, maps) = BuildMap(source);

        Assert.Equal("&none", config.Layers[0].Bindings[1].Behavior);
        Assert.Equal(2, maps[0].Bindings.Count);
        Assert.Equal("XXX", TextOf(src, maps[0].Bindings[1]));
    }

    [Fact]
    public void Build_DefineParamStaysInSameSlot()
    {
        var source = """
            #define LOWER 1

            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = < &mo LOWER >;
                    };
                };
            };
            """;
        var (src, config, maps) = BuildMap(source);

        Assert.Single(config.Layers[0].Bindings);
        Assert.Single(maps[0].Bindings);
        Assert.Equal("&mo LOWER", TextOf(src, maps[0].Bindings[0]));
    }

    // ---- refusals -------------------------------------------------------------

    [Fact]
    public void Build_RefusesFunctionLikeMacroCall()
    {
        var source = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <
                            HRM(A, B)
                            &kp A
                        >;
                    };
                };
            };
            """;
        var config = ZmkKeymapLoader.LoadFromText(source, "test");

        var ex = Assert.Throws<ZmkKeymapParseException>(() => KeymapSourceMap.Build(source, config));
        Assert.Contains($"line {LineOf(source, "HRM(")}", ex.Message);
    }

    [Fact]
    public void Build_RefusesDirectiveInsideArray()
    {
        var source = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <
                            &kp A
                            #ifdef FOO
                            &kp B
                            #endif
                        >;
                    };
                };
            };
            """;
        var config = ZmkKeymapLoader.LoadFromText(source, "test");

        var ex = Assert.Throws<ZmkKeymapParseException>(() => KeymapSourceMap.Build(source, config));
        Assert.Contains($"line {LineOf(source, "#ifdef")}", ex.Message);
    }

    [Fact]
    public void Build_RefusesCommaSeparatedArrays()
    {
        var source = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <&kp A>, <&mo 1>;
                    };
                };
            };
            """;
        // The parser merges the comma-separated groups into one cell array,
        // so the loader itself accepts this input — the source mapper is the
        // one that must refuse it.
        var config = ZmkKeymapLoader.LoadFromText(source, "test");
        Assert.Equal(2, config.Layers[0].Bindings.Count);

        var ex = Assert.Throws<ZmkKeymapParseException>(() => KeymapSourceMap.Build(source, config));
        Assert.Contains($"line {LineOf(source, "bindings")}", ex.Message);
    }

    [Fact]
    public void Build_KeywordOffsetPointsAtBindings()
    {
        var source = KeymapFixtures.Read("corne.keymap");
        var config = ZmkKeymapLoader.LoadFromText(source, "test");

        var maps = KeymapSourceMap.Build(source, config);

        Assert.All(maps, map =>
            Assert.Equal("bindings", source.Substring(map.BindingsKeywordOffset, 8)));
    }
}
