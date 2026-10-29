using System.Collections.Generic;
using System.Linq;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;
using Xunit;

namespace LViz.Tests.Keymap;

public class KeymapWriterTests
{
    private static string Read(string fixture) => KeymapFixtures.Read(fixture);

    private static string Apply(
        string source,
        IReadOnlyList<BindingEdit>? edits = null,
        IReadOnlyList<LayerRename>? renames = null) =>
        KeymapWriter.Apply(source, edits ?? Array.Empty<BindingEdit>(), renames ?? Array.Empty<LayerRename>());

    private static KeyBinding Bind(string behavior, params string[] parameters) =>
        new(behavior, parameters);

    // ---- no-op handling ---------------------------------------------------------

    [Theory]
    [InlineData("minimal.keymap")]
    [InlineData("corne.keymap")]
    [InlineData("with_combos_macros.keymap")]
    [InlineData("glove80-editor.keymap")]
    public void Apply_NoEditsNoRenames_ReturnsInputVerbatim(string fixture)
    {
        var source = Read(fixture);
        Assert.Same(source, Apply(source));
    }

    [Fact]
    public void Apply_NoOpEditOnUnmappableSource_ReturnsInputWithoutMapping()
    {
        // Comma-joined arrays are refused by KeymapSourceMap, but an edit
        // that equals the current binding must still be a pure no-op —
        // &mo LOWER parses to exactly ("&mo", ["1"]) via the LOWER define.
        var source = """
            #define LOWER 1

            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <&mo LOWER>, <&kp A>;
                    };
                };
            };
            """;
        Assert.Equal(source, Apply(source, [new BindingEdit(0, 0, Bind("&mo", "1"))]));
    }

    [Fact]
    public void Apply_EditEqualToCurrentBinding_KeepsOriginalText()
    {
        var source = Read("with_combos_macros.keymap");
        // layer 0, key 4 is `&mo LOWER`; ("&mo", ["1"]) is the same binding.
        Assert.Equal(source, Apply(source, [new BindingEdit(0, 4, Bind("&mo", "1"))]));
    }

    // ---- binding edits on the minimal fixture ------------------------------------

    [Fact]
    public void Apply_MinimalFixture_EditChangesOnlyThatSubstring()
    {
        var source = Read("minimal.keymap");
        var result = Apply(source, [new BindingEdit(0, 0, Bind("&kp", "Z"))]);

        Assert.Equal(
            source.Replace("&kp A     &kp B", "&kp Z     &kp B", StringComparison.Ordinal),
            result);
    }

    // ---- padding rules (exact expected text) --------------------------------------

    [Fact]
    public void Apply_ShorterReplacementMidLine_PadsToKeepNextColumn()
    {
        var source = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <
                            &kp SPACE  &kp B
                        >;
                    };
                };
            };
            """;
        var expected = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <
                            &kp A      &kp B
                        >;
                    };
                };
            };
            """;
        Assert.Equal(expected, Apply(source, [new BindingEdit(0, 0, Bind("&kp", "A"))]));
    }

    [Fact]
    public void Apply_LongerReplacementMidLine_EatsSpacesKeepsOne()
    {
        var source = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <
                            &kp A      &kp B
                        >;
                    };
                };
            };
            """;
        var expected = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <
                            &kp LS(N7) &kp B
                        >;
                    };
                };
            };
            """;
        Assert.Equal(expected, Apply(source, [new BindingEdit(0, 0, Bind("&kp", "LS", "N7"))]));
    }

    [Fact]
    public void Apply_LongerReplacementWithTooFewSpaces_KeepsSingleSpace()
    {
        var source = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <
                            &kp A &kp B
                        >;
                    };
                };
            };
            """;
        var expected = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <
                            &kp LS(N7) &kp B
                        >;
                    };
                };
            };
            """;
        Assert.Equal(expected, Apply(source, [new BindingEdit(0, 0, Bind("&kp", "LS", "N7"))]));
    }

    [Fact]
    public void Apply_LastBindingOnLine_AddsNoTrailingSpaces()
    {
        var source = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <
                            &trans    &kp SPACE
                        >;
                    };
                };
            };
            """;
        var expected = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <
                            &trans    &trans
                        >;
                    };
                };
            };
            """;
        Assert.Equal(expected, Apply(source, [new BindingEdit(0, 1, Bind("&trans"))]));
    }

    [Fact]
    public void Apply_MinimalFixture_LastBindingOnLine_ReplacesWithoutPadding()
    {
        var source = Read("minimal.keymap");
        // key 15 = `&bootloader`, last binding of the last line
        var result = Apply(source, [new BindingEdit(0, 15, Bind("&trans"))]);

        Assert.Contains("&sys_reset    &trans\n", result, StringComparison.Ordinal);
        // shorter replacement at end of line: padded away, no trailing space added
        var line = result.Split('\n').First(l => l.Contains("&sys_reset", StringComparison.Ordinal));
        Assert.EndsWith("&trans", line, StringComparison.Ordinal);
    }

    // ---- formatting features through the writer ------------------------------------

    [Fact]
    public void Apply_LayerParamWrittenAsFileDefine()
    {
        var source = Read("with_combos_macros.keymap");
        // LOWER is #define'd to 1, so the layer param "1" is written back
        // with the file's own name.
        var result = Apply(source, [new BindingEdit(0, 2, Bind("&mo", "1"))]);
        Assert.Contains("&mo LOWER", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", "&mo LAYER_Symbol")] // not KB_TYPE_GLOVE_80, defined earlier with value 1
    [InlineData("2", "&mo LAYER_Lower")]  // not KB_TYPE_GO_60
    [InlineData("3", "&mo 3")]            // LAYER_Magic is never used as a layer reference
    public void Apply_LayerParamUsesOnlyDefinesTheFileUsesAsLayerReferences(string layer, string expected)
    {
        var source = Read("glove80-editor.keymap");
        var config = ZmkKeymapLoader.LoadFromText(source, "test");
        var span = KeymapSourceMap.Build(source, config)[0].Bindings[0];

        var result = Apply(source, [new BindingEdit(0, 0, Bind("&mo", layer))]);

        Assert.Equal(expected, result.Substring(span.Start, expected.Length));
    }

    [Fact]
    public void Apply_ModifierParamsReNested()
    {
        var source = Read("with_combos_macros.keymap");
        var result = Apply(source, [new BindingEdit(0, 0, Bind("&kp", "LC", "LS", "N7"))]);
        Assert.Contains("&kp LC(LS(N7))", result, StringComparison.Ordinal);
    }

    // ---- comments -------------------------------------------------------------------

    [Fact]
    public void Apply_EditNextToComment_KeepsCommentVerbatim()
    {
        var source = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <
                            &kp A /* keep me */ &kp B
                        >;
                    };
                };
            };
            """;
        var expected = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <
                            &kp Z /* keep me */ &kp B
                        >;
                    };
                };
            };
            """;
        Assert.Equal(expected, Apply(source, [new BindingEdit(0, 0, Bind("&kp", "Z"))]));
    }

    // ---- renames ----------------------------------------------------------------------

    [Fact]
    public void Apply_RenameWithExistingDisplayName_ReplacesOnlyTheValue()
    {
        var source = Read("minimal.keymap");
        var result = Apply(source, renames: [new LayerRename(0, "Main")]);

        Assert.Contains("display-name = \"Main\";", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Base\"", result, StringComparison.Ordinal);
        Assert.Equal(
            source.Split('\n').Length,
            result.Split('\n').Length);
        Assert.Equal("Main", ZmkKeymapLoader.LoadFromText(result, "writer").Layers[0].Name);
    }

    [Fact]
    public void Apply_RenameWithoutDisplayName_InsertsLineAboveBindings()
    {
        var source = Read("glove80-editor.keymap");
        var before = ZmkKeymapLoader.LoadFromText(source, "writer");
        var result = Apply(source, renames: [new LayerRename(0, "Main")]);

        // the property lives directly above the keymap's own bindings line,
        // with that line's indentation (the fixture also has `bindings`
        // inside behavior-template macros, so anchor on the inserted line)
        var resultLines = result.Split('\n');
        var inserted = Array.FindAll(resultLines,
            l => l == "            display-name = \"Main\";");
        Assert.Single(inserted);
        int at = Array.IndexOf(resultLines, inserted[0]);
        Assert.EndsWith("bindings = <", resultLines[at + 1], StringComparison.Ordinal);
        Assert.StartsWith("            ", resultLines[at + 1], StringComparison.Ordinal);

        // every other line is untouched: drop the inserted line and the text
        // is exactly the input again
        Assert.Equal(source, string.Join("\n", resultLines.Where((_, i) => i != at)));

        var after = ZmkKeymapLoader.LoadFromText(result, "writer");
        Assert.Equal("Main", after.Layers[0].Name);
        // every other layer keeps its position and name
        Assert.Equal(before.Layers.Count, after.Layers.Count);
        for (int i = 1; i < after.Layers.Count; i++)
            Assert.Equal(before.Layers[i].Name, after.Layers[i].Name);
    }

    [Fact]
    public void Apply_RenameWithQuotesRoundTripsThroughParse()
    {
        var source = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        display-name = "Base";
                        bindings = <
                            &kp A
                        >;
                    };
                };
            };
            """;
        var result = Apply(source, renames: [new LayerRename(0, "Say \"hi\"")]);

        Assert.Contains("display-name = \"Say \\\"hi\\\"\";", result, StringComparison.Ordinal);
        Assert.Equal("Say \"hi\"", ZmkKeymapLoader.LoadFromText(result, "writer").Layers[0].Name);
    }

    // ---- refusals -----------------------------------------------------------------------

    [Fact]
    public void Apply_FunctionLikeMacroInLayer_ThrowsParseError()
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
        Assert.Throws<ZmkKeymapParseException>(() =>
            Apply(source, [new BindingEdit(0, 0, Bind("&kp", "Z"))]));
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 5)]
    [InlineData(-1, 0)]
    public void Apply_KeyOrLayerIndexOutOfRange_ThrowsArgumentOutOfRange(int layer, int key)
    {
        var source = Read("minimal.keymap");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Apply(source, [new BindingEdit(layer, key, Bind("&kp", "Z"))]));
    }

    [Fact]
    public void Apply_LayerIndexOutOfRangeForRename_ThrowsArgumentOutOfRange()
    {
        var source = Read("minimal.keymap");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Apply(source, renames: [new LayerRename(1, "Main")]));
    }

    [Fact]
    public void Apply_DuplicateEdits_ThrowArgument()
    {
        var source = Read("minimal.keymap");
        Assert.Throws<ArgumentException>(() =>
            Apply(source,
            [
                new BindingEdit(0, 0, Bind("&kp", "Z")),
                new BindingEdit(0, 0, Bind("&kp", "Y")),
            ]));
    }

    [Fact]
    public void Apply_DuplicateRenames_ThrowArgument()
    {
        var source = Read("minimal.keymap");
        Assert.Throws<ArgumentException>(() =>
            Apply(source, renames:
            [
                new LayerRename(0, "Main"),
                new LayerRename(0, "Other"),
            ]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    public void Apply_InvalidRenameName_ThrowsArgument(string name)
    {
        var source = Read("minimal.keymap");
        Assert.Throws<ArgumentException>(() =>
            Apply(source, renames: [new LayerRename(0, name)]));
    }

    // ---- CRLF -----------------------------------------------------------------------------

    [Fact]
    public void Apply_CrlfSource_KeepsCrlfForEditAndInsertedLine()
    {
        var source = ("""
            / {
                keymap {
                    compatible = "zmk,keymap";

                    l1 {
                        bindings = <
                            &kp A      &kp B
                        >;
                    };
                };
            };
            """).Replace("\n", "\r\n");

        var result = Apply(
            source,
            [new BindingEdit(0, 0, Bind("&kp", "Z"))],
            [new LayerRename(0, "Main")]);

        // every LF is preceded by a CR
        Assert.False(result.Skip(1).Where((c, i) => c == '\n' && result[i] != '\r').Any());
        Assert.Contains("display-name = \"Main\";\r\n            bindings = <", result, StringComparison.Ordinal);
        Assert.Contains("&kp Z      &kp B\r\n", result, StringComparison.Ordinal);
        Assert.Equal("Main", ZmkKeymapLoader.LoadFromText(result, "writer").Layers[0].Name);
    }
}
