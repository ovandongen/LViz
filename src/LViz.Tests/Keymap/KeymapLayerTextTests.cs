using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;
using static LViz.Core.Keymap.Editing.LayerStructureEdit;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// The text half of layer add/delete/move: finding layer node blocks, layer
/// <c>#define</c>s and numeric layer references in the original
/// <c>.keymap</c> text, and renumbering them. Everything outside the located
/// spans must survive byte-for-byte, so the assertions work on offsets and
/// lines of the untouched source.
/// </summary>
public class KeymapLayerTextTests
{
    private static (KeymapLayerText Text, KeyboardConfig Config) Load(string fixture)
    {
        var source = KeymapFixtures.Read(fixture);
        var config = ZmkKeymapLoader.LoadFromText(source, fixture);
        return (KeymapLayerText.Parse(source, config), config);
    }

    private static KeymapLayerText Parse(string source) =>
        KeymapLayerText.Parse(source, ZmkKeymapLoader.LoadFromText(source, "test"));

    /// <summary>Applies planned changes exactly as the layer writers will.</summary>
    private static string Apply(string source, IReadOnlyList<TextChange> changes) =>
        KeymapWriter.ApplyChanges(
            source,
            changes.Select(c => (c.Offset, c.Replacement, c.Length)).ToList());

    // ---- offsets/lines in the fixture text --------------------------------------

    /// <summary>Offset of the first character of a 1-based line.</summary>
    private static int LineStart(string text, int line)
    {
        int offset = 0;
        for (int current = 1; current < line; current++)
        {
            offset = text.IndexOf('\n', offset) + 1;
            Assert.NotEqual(0, offset); // line number past the end of the file
        }
        return offset;
    }

    /// <summary>1-based line holding <paramref name="offset"/>.</summary>
    private static int LineOf(string text, int offset)
    {
        int line = 1;
        for (int i = 0; i < offset; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    private static string LineText(string text, int line)
    {
        int start = LineStart(text, line);
        int end = text.IndexOf('\n', start);
        return end < 0 ? text[start..] : text[start..end];
    }

    // ---- layer blocks ------------------------------------------------------------

    [Fact]
    public void Parse_Glove80_FindsEveryLayerNodeBlock()
    {
        var (text, _) = Load("glove80-editor.keymap");
        var source = text.Source;

        Assert.Equal(
            [
                "layer_Base", "layer_Symbol", "layer_Lower", "layer_Magic",
                "layer_Mouse", "layer_MouseSlow", "layer_MouseFast", "layer_MouseWarp",
            ],
            text.Layers.Select(l => l.NodeName));

        foreach (var block in text.Layers)
        {
            // Start is the first character of the header line, so the block
            // begins with the indentation and the node name.
            Assert.Equal("        " + block.NodeName + " {", LineText(source, LineOf(source, block.Start)));
            Assert.StartsWith("        " + block.NodeName + " {", source[block.Start..]);
            Assert.Equal("        ", block.Indent);

            // End is just past the newline closing the line with `};`.
            Assert.EndsWith("        };" + text.NewLine, source[..block.End], StringComparison.Ordinal);
            Assert.True(block.End < source.Length);
            Assert.Equal('\n', source[block.End - 1]);
        }

        // One block per layer: no neighbour header inside it.
        var symbol = source[text.Layers[1].Start..text.Layers[1].End];
        Assert.Contains("layer_Symbol {", symbol, StringComparison.Ordinal);
        Assert.DoesNotContain("layer_Base {", symbol, StringComparison.Ordinal);
        Assert.DoesNotContain("layer_Lower {", symbol, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Slabwing_BlocksStartAtTheHeaderLinesOwnIndentation()
    {
        var (text, _) = Load("slabwing.keymap");
        var source = text.Source;

        var first = text.Layers[0];
        Assert.Equal("base_layer", first.NodeName);
        Assert.Equal("        ", first.Indent);
        Assert.Equal("        base_layer {", LineText(source, LineOf(source, first.Start)));
        Assert.EndsWith("        };" + text.NewLine, source[..first.End], StringComparison.Ordinal);

        // The block holds the display-name and the whole bindings array, and
        // stops before the blank line that follows it.
        var block = source[first.Start..first.End];
        Assert.Contains("display-name = \"Base\";", block, StringComparison.Ordinal);
        Assert.EndsWith("        };" + text.NewLine, block, StringComparison.Ordinal);
        Assert.DoesNotContain("symbols {", block, StringComparison.Ordinal);
    }

    // ---- layer defines -----------------------------------------------------------

    [Fact]
    public void Parse_Glove80_FindsTheGeneratedLayerDefinesOnly()
    {
        var (text, _) = Load("glove80-editor.keymap");

        Assert.Equal(
            [
                "LAYER_Base", "LAYER_Symbol", "LAYER_Lower", "LAYER_Magic",
                "LAYER_Mouse", "LAYER_MouseSlow", "LAYER_MouseFast", "LAYER_MouseWarp",
            ],
            text.LayerDefines.Select(d => d.Name));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], text.LayerDefines.Select(d => d.LayerIndex));

        // The real definition on line 69, never the #ifndef fallback on 78.
        var lower = text.LayerDefines.Single(d => d.Name == "LAYER_Lower");
        Assert.Equal(69, LineOf(text.Source, lower.LineStart));
        Assert.Equal(2, lower.LayerIndex);
        Assert.Equal("#define LAYER_Lower 2", LineText(text.Source, 69));
        Assert.Equal("2", text.Source.Substring(lower.ValueOffset, lower.ValueLength));
        // [LineStart, LineEnd) is the whole line, newline included.
        Assert.Equal("#define LAYER_Lower 2\n", text.Source[lower.LineStart..lower.LineEnd]);

        // KB_TYPE_GLOVE_80 is 1 too, but nothing uses it as a layer reference.
        // POS_* hold small numbers as well, and ZMK_MB_M1 … ZMK_MT_RAISE 0-7 are
        // in the layer range but used nowhere and named differently.
        Assert.DoesNotContain(text.LayerDefines, d => d.Name.StartsWith("KB_TYPE", StringComparison.Ordinal));
        Assert.DoesNotContain(text.LayerDefines, d => d.Name.StartsWith("POS_", StringComparison.Ordinal));
        Assert.DoesNotContain(text.LayerDefines, d => d.Name.StartsWith("ZMK_", StringComparison.Ordinal));
        Assert.DoesNotContain(text.LayerDefines, d => d.Name == "EDITOR_LAYOUT_OS");
    }

    [Fact]
    public void Parse_Slabwing_IgnoresUnusedDefines()
    {
        var (text, _) = Load("slabwing.keymap");

        // #define BASE 0 … ADJ 3 — nothing in the file uses them, and they are
        // not LAYER_<name>, so the editor leaves them alone.
        Assert.Empty(text.LayerDefines);
        Assert.Contains("#define BASE 0", text.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AcceptsBothWaysOfNamingALayer()
    {
        var source = """
            #define LAYER_B 1
            #define POS 1

            / {
                keymap {
                    compatible = "zmk,keymap";

                    base {
                        bindings = <&kp A &mo LAYER_B>;
                    };

                    other {
                        bindings = <&kp B &to 0>;
                    };
                };
            };
            """;

        var text = Parse(source);

        // LAYER_B is used where a layer number may stand; POS holds a small
        // number that nothing references.
        var define = Assert.Single(text.LayerDefines);
        Assert.Equal("LAYER_B", define.Name);
        Assert.Equal(1, define.LayerIndex);
        Assert.Equal("#define LAYER_B 1", LineText(source, LineOf(source, define.LineStart)));

        var reference = Assert.Single(text.References);
        Assert.Equal(0, reference.Layer);
        Assert.Equal(ReferenceSite.LayerBlock, reference.Site);
        Assert.Equal("0", source.Substring(reference.Offset, reference.Length));
    }

    // ---- references --------------------------------------------------------------

    [Fact]
    public void Parse_Glove80_NumericReferencesAreTheTwoToZeroBindings()
    {
        var (text, _) = Load("glove80-editor.keymap");
        var source = text.Source;

        // Everything else names its layer (&to LAYER_Lower, &lt LAYER_Symbol,
        // &mo LAYER_MouseSlow, …) or is not a layer number at all
        // (#define POS_* 12, tapping-term-ms = <200>, #binding-cells = <0>).
        Assert.Equal([0, 0], text.References.Select(r => r.Layer));
        Assert.Equal([411, 423], text.References.Select(r => r.Line));
        Assert.All(text.References, r => Assert.Equal(ReferenceSite.LayerBlock, r.Site));
        Assert.All(text.References, r => Assert.Equal("0", source.Substring(r.Offset, r.Length)));

        // &magic LAYER_Magic 0 — the trailing 0 is the tap parameter: the
        // pattern takes the first parameter only, and that one is a name.
        int at = 0;
        int magicHits = 0;
        while ((at = source.IndexOf("&magic LAYER_Magic 0", at, StringComparison.Ordinal)) >= 0)
        {
            magicHits++;
            int zero = at + "&magic LAYER_Magic ".Length;
            Assert.DoesNotContain(text.References, r => r.Offset == zero);
            at += 1;
        }
        Assert.Equal(2, magicHits);
    }

    [Fact]
    public void Parse_Slabwing_ClassifiesEveryReferenceSite()
    {
        var (text, _) = Load("slabwing.keymap");
        var source = text.Source;

        // 5 layer-block &lt, 1 combo &mo, 3 conditional layers.
        Assert.Equal(9, text.References.Count);

        var conditional = text.References.Where(r => r.Site == ReferenceSite.ConditionalLayer).ToList();
        Assert.Equal([2, 2, 2], conditional.Select(r => r.Layer));
        Assert.Equal([33, 33, 34], conditional.Select(r => r.Line)); // if-layers = <2 2>; then-layer = <2>;

        var combo = Assert.Single(text.References, r => r.Site == ReferenceSite.Combo);
        Assert.Equal(2, combo.Layer);
        Assert.Equal(60, combo.Line); // bindings = <&mo 2>;
        Assert.Equal("&mo 2", source.Substring(source.LastIndexOf("&mo", combo.Offset, StringComparison.Ordinal), 5));

        var inLayers = text.References.Where(r => r.Site == ReferenceSite.LayerBlock).ToList();
        Assert.Equal([6, 3, 4, 5, 1], inLayers.Select(r => r.Layer)); // &lt 6 F, &lt 3 J, &lt 4 K, &lt 5 L, &lt 1 ENTER
    }

    [Fact]
    public void Parse_Cosmos82_SevenBlocksAndOnlyLayerBlockReferences()
    {
        var (text, _) = Load("cosmos82.keymap");

        Assert.Equal(7, text.Layers.Count);
        Assert.Empty(text.LayerDefines);
        Assert.NotEmpty(text.References);
        Assert.All(text.References, r => Assert.Equal(ReferenceSite.LayerBlock, r.Site));
    }

    [Fact]
    public void Parse_HoldTapOverALayerBehaviorCountsItsFirstParam()
    {
        var source = """
            #include <behaviors.dtsi>

            / {
                behaviors {
                    magic: magic {
                        compatible = "zmk,behavior-hold-tap";
                        #binding-cells = <2>;
                        bindings = <&mo>, <&kp A>;
                    };
                };

                keymap {
                    compatible = "zmk,keymap";

                    base {
                        bindings = <&magic 1 0>;
                    };

                    other {
                        bindings = <&kp B>;
                    };
                };
            };
            """;

        var text = Parse(source);
        var config = ZmkKeymapLoader.LoadFromText(source, "test");
        Assert.Equal("&mo", Assert.Single(config.HoldTaps).HoldBinding);

        // Only the hold parameter is a layer reference; the 0 after it is the
        // tap parameter, and #binding-cells = <2> is not a layer either.
        var reference = Assert.Single(text.References);
        Assert.Equal(1, reference.Layer);
        Assert.Equal(ReferenceSite.LayerBlock, reference.Site);
        Assert.Equal("1", source.Substring(reference.Offset, reference.Length));
    }

    // ---- Renumber ---------------------------------------------------------------

    [Fact]
    public void Renumber_Glove80_MoveMouseTo1_RewritesOnlyTheLayerDefines()
    {
        var (text, config) = Load("glove80-editor.keymap");
        var map = LayerReferenceRemapper.IndexMap(config.Layers.Count, new Move(4, 1));

        var changes = text.Renumber(map);
        var result = Apply(text.Source, changes);

        Assert.Equal(4, changes.Count); // Symbol, Lower, Magic, Mouse move
        Assert.Equal("#define LAYER_Base 0", LineText(result, 67));
        Assert.Equal("#define LAYER_Symbol 2", LineText(result, 68));
        Assert.Equal("#define LAYER_Lower 3", LineText(result, 69));
        Assert.Equal("#define LAYER_Magic 4", LineText(result, 70));
        Assert.Equal("#define LAYER_Mouse 1", LineText(result, 71));
        Assert.Equal("#define LAYER_MouseSlow 5", LineText(result, 72));

        // The #ifndef fallback was never a layer define, so it keeps its value.
        Assert.Equal("#ifndef LAYER_Lower", LineText(result, 77));
        Assert.Equal("#define LAYER_Lower 0", LineText(result, 78));
        Assert.Equal("#endif", LineText(result, 79));

        // Single digits in, single digits out: nothing else moved.
        Assert.Equal(text.Source.Length, result.Length);
        Assert.Equal(config.Layers.Count, ZmkKeymapLoader.LoadFromText(result, "glove80").Layers.Count);
    }

    [Fact]
    public void Renumber_Slabwing_Move2To0_RenumbersComboAndConditionalLayers()
    {
        var (text, config) = Load("slabwing.keymap");
        var map = LayerReferenceRemapper.IndexMap(config.Layers.Count, new Move(2, 0));

        var result = Apply(text.Source, text.Renumber(map));

        Assert.Contains("bindings = <&mo 0>;", result, StringComparison.Ordinal);
        Assert.Contains("if-layers = <0 0>;", result, StringComparison.Ordinal);
        Assert.Contains("then-layer = <0>;", result, StringComparison.Ordinal);
        Assert.Contains("&lt 2 ENTER", result, StringComparison.Ordinal); // layer 1 became layer 2
        Assert.Contains("key-positions = <0 1>;", result, StringComparison.Ordinal); // not a layer ref

        var rewritten = ZmkKeymapLoader.LoadFromText(result, "slabwing");
        Assert.Equal(config.Layers.Count, rewritten.Layers.Count);
        Assert.All(rewritten.Layers, l => Assert.Equal(56, l.Bindings.Count));
        Assert.Equal(56, rewritten.Layers[0].Bindings.Count); // layer 0 keeps its 56 bindings

        // The base layer's layer-toggle targets followed the index map.
        Assert.Equal(
            ["6", "3", "4", "5", "2"],
            rewritten.Layers[0].Bindings.Where(b => b.Behavior == "&lt").Select(b => b.Params[0]));
    }

    [Fact]
    public void Renumber_LeavesDeletedAndUnmovedLayersAlone()
    {
        var (text, config) = Load("slabwing.keymap");

        // Delete layer 2: numbers naming it get no change here (cards 4/5
        // clear those bindings); numbers above it shift down by one.
        var delete = text.Renumber(LayerReferenceRemapper.IndexMap(config.Layers.Count, new Delete(2)));
        var above = text.References.Where(r => r.Layer > 2).Select(r => r.Offset).ToHashSet();
        Assert.Equal(4, above.Count); // &lt 6 F, &lt 3 J, &lt 4 K, &lt 5 L
        Assert.Equal(above.Count, delete.Count);
        Assert.All(delete, c => Assert.Contains(c.Offset, above));
        Assert.Contains("if-layers = <2 2>;", Apply(text.Source, delete), StringComparison.Ordinal);

        // Move 0→6: everything shifts down by one.
        var move = text.Renumber(LayerReferenceRemapper.IndexMap(config.Layers.Count, new Move(0, 6)));
        Assert.NotEmpty(move);
        var result = Apply(text.Source, move);
        Assert.Contains("if-layers = <1 1>;", result, StringComparison.Ordinal);
        Assert.Contains("then-layer = <1>;", result, StringComparison.Ordinal);
        Assert.Contains("bindings = <&mo 1>;", result, StringComparison.Ordinal);

        // A reference outside the layer count is ignored.
        var synthetic = Parse("""
            / {
                keymap {
                    compatible = "zmk,keymap";

                    base {
                        bindings = <&mo 9 &to 0>;
                    };
                };
            };
            """);
        var oneLayer = new int?[] { 0 };
        Assert.Contains(synthetic.References, r => r.Layer == 9);
        Assert.Empty(synthetic.Renumber(oneLayer));
    }

    [Fact]
    public void Renumber_ChangesAreOrderedAndDisjoint()
    {
        // KeymapWriter.ApplyChanges refuses overlapping changes, so a move that
        // renumbers a define and a reference on the same line must not collide.
        var (text, config) = Load("glove80-editor.keymap");
        var changes = text.Renumber(LayerReferenceRemapper.IndexMap(config.Layers.Count, new Move(7, 0)));

        Assert.NotEmpty(changes);
        Assert.Equal(
            changes.Select(c => c.Offset).OrderBy(o => o).ToList(),
            changes.Select(c => c.Offset).ToList());

        int past = -1;
        foreach (var change in changes)
        {
            Assert.True(change.Offset >= past, $"change at offset {change.Offset} overlaps the previous one");
            past = change.Offset + change.Length;
            Assert.True(int.TryParse(change.Replacement, out _), $"'{change.Replacement}' is not a number");
        }
    }

    // ---- FindNameUsages ----------------------------------------------------------

    [Fact]
    public void FindNameUsages_Glove80_SkipsDirectivesAndLayerBlocks()
    {
        var (text, _) = Load("glove80-editor.keymap");

        // #define LAYER_Lower 2 (69), #ifndef LAYER_Lower (77) and
        // #define LAYER_Lower 0 (78) are directive subjects; the &to on 389 is
        // inside a layer block. Only the tap-dance helper call is left.
        var lower = text.FindNameUsages("LAYER_Lower");
        Assert.Equal([103], lower.Select(o => LineOf(text.Source, o)));
        Assert.Equal(
            "        ZMK_TD_LAYER(lower, LAYER_Lower)",
            LineText(text.Source, LineOf(text.Source, lower[0])));

        // The input-listener node name and its `layers` scope; the
        // &mo LAYER_MouseSlow binding inside layer_Mouse is a layer block.
        var slow = text.FindNameUsages("LAYER_MouseSlow");
        Assert.Equal([86, 87], slow.Select(o => LineOf(text.Source, o)));
        Assert.Equal("    LAYER_MouseSlow {", LineText(text.Source, 86));
        Assert.Equal("        layers = <LAYER_MouseSlow>;", LineText(text.Source, 87));
    }

    [Fact]
    public void FindNameUsages_IsWholeWord()
    {
        var (text, _) = Load("glove80-editor.keymap");

        // LAYER_Mouse's own occurrences are its define (directive subject) and
        // the longer LAYER_Mouse{Slow,Fast,Warp} names; nothing uses it alone.
        Assert.Empty(text.FindNameUsages("LAYER_Mouse"));
        Assert.Empty(text.FindNameUsages(""));
    }

    [Fact]
    public void Parse_ComboLayerScopeNumbersAreComboReferencesAndMinusOneIsNotOne()
    {
        var source = """
            / {
                combos {
                    compatible = "zmk,combos";

                    c1 {
                        bindings = <&kp A>;
                        key-positions = <0 1>;
                        layers = <2 0 -1>;
                    };
                };

                keymap {
                    compatible = "zmk,keymap";

                    base {
                        bindings = <&kp A>;
                    };
                };
            };
            """;

        var text = Parse(source);

        // -1 means "all layers", not layer 1, and key positions are not layers.
        Assert.Equal([2, 0], text.References.Select(r => r.Layer));
        Assert.All(text.References, r => Assert.Equal(ReferenceSite.Combo, r.Site));
    }

    [Fact]
    public void FindNameUsages_FindsUseTheWritersCannotRewrite()
    {
        // Card 5 refuses to delete a layer whose define is used by something
        // outside its own block — here a macro body.
        var source = """
            #define MY_LAYER 1

            / {
                macros {
                    m1: m1 {
                        compatible = "zmk,behavior-macro";
                        #binding-cells = <0>;
                        bindings = <&mo MY_LAYER>;
                    };
                };

                keymap {
                    compatible = "zmk,keymap";

                    base {
                        bindings = <&kp A>;
                    };

                    other {
                        bindings = <&kp B>;
                    };
                };
            };
            """;

        var text = Parse(source);

        Assert.Equal([8], text.FindNameUsages("MY_LAYER").Select(o => LineOf(source, o)));
        Assert.Contains("bindings = <&mo MY_LAYER>;", LineText(source, 8), StringComparison.Ordinal);

        // Used where a layer number may stand, so it is a layer define too.
        var define = Assert.Single(text.LayerDefines);
        Assert.Equal("MY_LAYER", define.Name);
        Assert.Equal(1, define.LayerIndex);
    }

    // ---- line endings and structure errors ---------------------------------------

    [Fact]
    public void Parse_CarriageReturnLineEndings_AreDetectedAndKept()
    {
        var source = string.Join("\r\n", [
            "#define LAYER_other 1",
            "",
            "/ {",
            "\tkeymap {",
            "\t\tcompatible = \"zmk,keymap\";",
            "",
            "\t\tbase {",
            "\t\t\tbindings = <&kp A &to 0>;",
            "\t\t};",
            "",
            "\t\tother {",
            "\t\t\tbindings = <&kp B &mo LAYER_other>;",
            "\t\t};",
            "\t};",
            "};",
        ]);

        var text = Parse(source);

        Assert.Equal("\r\n", text.NewLine);
        Assert.Equal(2, text.Layers.Count);
        Assert.Equal("\t\t", text.Layers[0].Indent);
        Assert.Equal("base", text.Layers[0].NodeName);

        foreach (var block in text.Layers)
        {
            // End lands after the whole CRLF, not between CR and LF.
            Assert.Equal('\r', source[block.End - 2]);
            Assert.Equal('\n', source[block.End - 1]);
            Assert.EndsWith("\t\t};" + text.NewLine, source[..block.End], StringComparison.Ordinal);
        }

        // LAYER_other matches LAYER_ + the layer name, and the reference to it
        // is named rather than numeric.
        var define = Assert.Single(text.LayerDefines);
        Assert.Equal("LAYER_other", define.Name);
        Assert.Equal(1, define.LayerIndex);
        Assert.DoesNotContain(text.References, r => r.Layer == 1);
        var reference = Assert.Single(text.References);
        Assert.Equal(0, reference.Layer);
    }

    [Fact]
    public void Parse_KeymapNodeWithANonLayerChild_TheParserAlreadyRefusesIt()
    {
        // ZmkKeymapInterpreter counts every child of the keymap node as a
        // layer, so the extra child is a 2-layer config with mismatched
        // binding counts: ZmkKeymapLoader throws before the text scanner runs.
        var source = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    base {
                        bindings = <&kp A &kp B>;
                    };

                    helper {
                        compatible = "zmk,behavior-macro";
                        #binding-cells = <0>;
                        bindings = <&kp C>;
                    };
                };
            };
            """;

        var ex = Assert.Throws<ZmkKeymapParseException>(
            () => ZmkKeymapLoader.LoadFromText(source, "test"));
        Assert.Contains("inconsistent binding counts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_LayerNodesNotMatchingTheParsedLayers_ThrowsOnTheKeymapLine()
    {
        // The parser drops the inactive #ifdef branch, the text scanner sees
        // both, and a writer that guessed either would corrupt the file.
        var source = """
            / {
                keymap {
                    compatible = "zmk,keymap";

                    base {
                        bindings = <&kp A>;
                    };
            #ifdef EXTRA
                    hidden {
                        bindings = <&kp B>;
                    };
            #endif
                };
            };
            """;

        var config = ZmkKeymapLoader.LoadFromText(source, "test");
        Assert.Single(config.Layers);

        var ex = Assert.Throws<ZmkKeymapParseException>(() => KeymapLayerText.Parse(source, config));
        Assert.Contains("keymap (line 2)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("found 2 layer nodes in the source, the parser found 1 layers", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_WithoutAKeymapNode_Throws()
    {
        var source = """
            / {
                combos {
                    compatible = "zmk,combos";
                };
            };
            """;

        var ex = Assert.Throws<ZmkKeymapParseException>(
            () => KeymapLayerText.Parse(source, SingleLayerConfig()));
        Assert.Contains("No 'zmk,keymap' node", ex.Message, StringComparison.Ordinal);
    }

    private static KeyboardConfig SingleLayerConfig() => new(
        "test",
        [new Layer(0, "Base", [new KeyBinding("&kp", ["A"])])],
        [],
        [],
        []);
}
