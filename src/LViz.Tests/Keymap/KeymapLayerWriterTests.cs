using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;
using static LViz.Core.Keymap.Editing.LayerStructureEdit;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// The public face of layer add/delete/move: <see cref="KeymapLayerWriter"/>
/// turns a <see cref="LayerStructureEdit"/> into text. What matters here is
/// what the writer may <b>not</b> do: for a move, nothing outside the moved
/// block, its separator and the renumbered layer numbers may change; an added
/// layer is written in the style the file already uses and touches nothing of
/// its own; a delete that something outside the layer blocks still uses is
/// refused by <see cref="KeymapLayerWriter.CheckDelete"/> before a character is
/// written; and a result that re-parses to anything but what
/// <see cref="LayerReferenceRemapper"/> expected is refused instead of written.
/// </summary>
public class KeymapLayerWriterTests
{
    private static string Load(string fixture) => KeymapFixtures.Read(fixture);

    private static KeyboardConfig Parse(string source) =>
        ZmkKeymapLoader.LoadFromText(source, "writer-test");

    /// <summary>The layer blocks of a written result, in text order.</summary>
    private static List<LayerBlock> BlocksOf(string text) =>
        KeymapLayerText.Parse(text, Parse(text)).Layers.ToList();

    private static List<string> NodeNamesOf(string text) =>
        BlocksOf(text).Select(b => b.NodeName).ToList();

    private static List<string> NamesOf(string text) =>
        Parse(text).Layers.Select(l => l.Name).ToList();

    /// <summary>The block <paramref name="index"/> holds, verbatim.</summary>
    private static string BlockAt(string text, int index) =>
        text[BlocksOf(text)[index].Start..BlocksOf(text)[index].End];

    private static List<KeyBinding> Transparent(int count) =>
        Enumerable.Repeat(KeyBinding.Transparent, count).ToList();

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        int at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }

    // ---- cosmos82: a plain move, no defines in the file -------------------------

    [Fact]
    public void Cosmos82_Move_Sys_To_3_Order_The_Model_Promises()
    {
        var source = Load("cosmos82.keymap");
        var edit = new Move(1, 3);

        var result = KeymapLayerWriter.Apply(source, edit);

        // the layer names, in order, are exactly the remapper's
        var expected = LayerReferenceRemapper.Apply(Parse(source), edit);
        Assert.Equal(expected.Layers.Select(l => l.Name), NamesOf(result));

        // …and the blocks themselves travelled with them
        Assert.Equal(
            ["base_layer", "symbol", "nav", "sys_layer", "nav-alt", "nav-alt-shift", "numbers"],
            NodeNamesOf(result));
    }

    [Fact]
    public void Cosmos82_Move_Sys_To_3_Touches_Nothing_Outside_The_Blocks()
    {
        var source = Load("cosmos82.keymap");
        var result = KeymapLayerWriter.Apply(source, new Move(1, 3));

        var was = BlocksOf(source);
        var now = BlocksOf(result);

        // every non-layer line survives: the text before the first block and
        // after the last one is identical, character for character
        Assert.Equal(source[..was[0].Start], result[..now[0].Start]);
        Assert.Equal(source[was[^1].End..], result[now[^1].End..]);

        // only the base layer's own text changed (its references were renumbered);
        // every other block comes back untouched, just in another order
        Assert.Equal(
            was.Where(b => b.NodeName != "base_layer")
                .Select(b => source[b.Start..b.End]).Order(StringComparer.Ordinal),
            now.Where(b => b.NodeName != "base_layer")
                .Select(b => result[b.Start..b.End]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Cosmos82_Moved_Block_Appears_Verbatim()
    {
        var source = Load("cosmos82.keymap");
        var result = KeymapLayerWriter.Apply(source, new Move(1, 3));

        // the Sys block holds no numeric layer reference, so it survives untouched
        var movedWas = BlocksOf(source).First(b => b.NodeName == "sys_layer");
        var movedNow = BlocksOf(result).First(b => b.NodeName == "sys_layer");
        Assert.Equal(source[movedWas.Start..movedWas.End], result[movedNow.Start..movedNow.End]);
    }

    [Fact]
    public void Cosmos82_Move_Renumbers_The_Numeric_References()
    {
        var source = Load("cosmos82.keymap");
        var result = KeymapLayerWriter.Apply(source, new Move(1, 3));

        // New index of each old layer: 0→0, 1→3, 2→1, 3→2, 4→4, 5→5, 6→6 — so
        // the three layers that moved re-point and the rest keep their number.
        Assert.Contains("&mo 3", result, StringComparison.Ordinal);
        Assert.DoesNotContain("&mo 1", result, StringComparison.Ordinal);
        Assert.Contains("&lt 2 J", result, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt 3 J", result, StringComparison.Ordinal);
        Assert.Contains("&lt 1 ENTER", result, StringComparison.Ordinal);
        Assert.Contains("&lt 6 F", result, StringComparison.Ordinal);
        Assert.Contains("&lt 4 K", result, StringComparison.Ordinal);
        Assert.Contains("&lt 5 L", result, StringComparison.Ordinal);
    }

    // ---- glove80: define-named references ---------------------------------------

    [Fact]
    public void Glove80_Move_Mouse_To_1_Rewrites_Defines_Not_References()
    {
        var source = Load("glove80-editor.keymap");
        var result = KeymapLayerWriter.Apply(source, new Move(4, 1));

        // the layer that moved and the layers it jumped over change value
        Assert.Contains("#define LAYER_Mouse 1", result, StringComparison.Ordinal);
        Assert.Contains("#define LAYER_Symbol 2", result, StringComparison.Ordinal);
        Assert.Contains("#define LAYER_Lower 3", result, StringComparison.Ordinal);
        Assert.Contains("#define LAYER_Magic 4", result, StringComparison.Ordinal);
        Assert.DoesNotContain("#define LAYER_Mouse 4", result, StringComparison.Ordinal);
        Assert.DoesNotContain("#define LAYER_Symbol 1", result, StringComparison.Ordinal);

        // the #ifndef fallback is a fallback, never the layer's own number
        Assert.Contains(
            "#ifndef LAYER_Lower\n#define LAYER_Lower 0\n#endif", result, StringComparison.Ordinal);

        // a reference written with a define name needs no change of its own
        Assert.Contains("&lt LAYER_Mouse TAB", result, StringComparison.Ordinal);
        Assert.Contains("&to LAYER_Lower", result, StringComparison.Ordinal);
        // and a number that keeps its index stays as it was
        Assert.Contains("&to 0", result, StringComparison.Ordinal);

        Assert.Equal(
            ["layer_Base", "layer_Mouse", "layer_Symbol", "layer_Lower", "layer_Magic",
             "layer_MouseSlow", "layer_MouseFast", "layer_MouseWarp"],
            NodeNamesOf(result));

        // the block order and the renumbered defines agree: the result re-parses
        // to what the model says (Apply would have thrown instead of returning)
        var expected = LayerReferenceRemapper.Apply(Parse(source), new Move(4, 1));
        Assert.Equal(expected.Layers.Select(l => l.Name), NamesOf(result));
    }

    [Fact]
    public void Glove80_Move_There_And_Back_Is_The_Original_Text_Byte_For_Byte()
    {
        var source = Load("glove80-editor.keymap");

        var result = KeymapLayerWriter.ApplyAll(source, [new Move(4, 1), new Move(1, 4)]);

        Assert.Equal(source, result);
    }

    // ---- slabwing: combos, conditional layers, unused defines --------------------

    [Fact]
    public void Slabwing_Move_Adjust_To_0_Renumbers_Combos_And_Conditional_Layers()
    {
        var source = Load("slabwing.keymap");
        var result = KeymapLayerWriter.Apply(source, new Move(2, 0));

        Assert.Equal(
            ["adjust_layer", "base_layer", "symbols", "nav", "nav-alt", "nav-alt-shift", "numbers"],
            NodeNamesOf(result));

        // the combo that fired the Adjust layer now fires it at its new index
        Assert.Contains("bindings = <&mo 0>;", result, StringComparison.Ordinal);
        Assert.DoesNotContain("&mo 2>", result, StringComparison.Ordinal);

        // so does the tri-layer that reaches it
        Assert.Contains("if-layers = <0 0>;", result, StringComparison.Ordinal);
        Assert.Contains("then-layer = <0>;", result, StringComparison.Ordinal);

        // BASE/NAV/SYM/ADJ are never used where a layer number may stand, so
        // they are not layer defines and keep their numbers
        Assert.Contains("#define SYM  2", result, StringComparison.Ordinal);
        Assert.Contains("#define BASE 0", result, StringComparison.Ordinal);
        Assert.Contains("#define NAV  1", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Slabwing_Move_Base_To_The_End_And_Keeps_The_Tail()
    {
        var source = Load("slabwing.keymap");
        var result = KeymapLayerWriter.Apply(source, new Move(0, 6));

        Assert.Equal("Base", NamesOf(result)[^1]);
        Assert.Equal("base_layer", NodeNamesOf(result)[^1]);

        // appending moved the separator with the block, so the text after the
        // last block is still the original tail of the file
        var was = BlocksOf(source);
        var now = BlocksOf(result);
        Assert.Equal(source[was[^1].End..], result[now[^1].End..]);

        // the vacated first slot keeps its blank line before the new first block
        Assert.Contains("compatible = \"zmk,keymap\";\n\n        symbols {", result,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Slabwing_Move_The_Last_Layer_Takes_Its_Leading_Separator()
    {
        var source = Load("slabwing.keymap");
        var result = KeymapLayerWriter.Apply(source, new Move(6, 0));

        Assert.Equal("Numbers", NamesOf(result)[0]);
        Assert.Equal(
            ["numbers", "base_layer", "symbols", "adjust_layer", "nav", "nav-alt", "nav-alt-shift"],
            NodeNamesOf(result));

        // the last block has no separator behind it, so it borrows the one in
        // front of it — and the text left behind still ends exactly like the
        // original file did after its own last block
        var was = BlocksOf(source);
        var now = BlocksOf(result);
        Assert.Equal(source[was[^1].End..], result[now[^1].End..]);

        // every pair of neighbouring blocks keeps its own single blank line
        for (int i = 0; i + 1 < now.Count; i++)
            Assert.Equal("\n", result[now[i].End..now[i + 1].Start]);
    }

    [Fact]
    public void Slabwing_Move_Last_To_Front_And_Back_Is_The_Original_Text()
    {
        var source = Load("slabwing.keymap");

        var result = KeymapLayerWriter.ApplyAll(source, [new Move(6, 0), new Move(0, 6)]);

        Assert.Equal(source, result);
    }

    // ---- add: a new layer in the file's own style ---------------------------------

    [Fact]
    public void Cosmos82_Add_Gaming_At_The_End()
    {
        var source = Load("cosmos82.keymap");
        int keys = Parse(source).Layers[0].Bindings.Count;
        Assert.Equal(82, keys);

        var result = KeymapLayerWriter.Apply(source, new Add(7, "Gaming", Transparent(keys)));

        var was = BlocksOf(source);
        var now = BlocksOf(result);

        // cosmos names its nodes `base_layer` next to `symbol`, so neither the
        // layer_ prefix nor the _layer suffix applies: the node is just "Gaming"
        Assert.Equal(8, now.Count);
        Assert.Equal("Gaming", now[^1].NodeName);
        Assert.Equal("Gaming", NamesOf(result)[^1]);

        string block = result[now[^1].Start..now[^1].End];
        Assert.Contains("display-name = \"Gaming\";", block, StringComparison.Ordinal);
        Assert.Equal(82, CountOf(block, "&trans"));
        // appended behind the file's own separator, at the blocks' indentation
        Assert.Contains("};\n\n        Gaming {", result, StringComparison.Ordinal);
        Assert.Equal("        };", block.Split('\n')[^2]);

        // nothing in front of the first original block moved
        Assert.Equal(source[..was[0].Start], result[..now[0].Start]);
        Assert.Equal(was[0].Start, now[0].Start);
    }

    [Fact]
    public void Slabwing_Duplicate_Base_At_The_Front_Renumbers_Everything_Else()
    {
        var source = Load("slabwing.keymap");
        var config = Parse(source);

        var result = KeymapLayerWriter.Apply(source, new Add(0, "Base copy", config.Layers[0].Bindings));

        var now = BlocksOf(result);
        Assert.Equal(8, now.Count);
        Assert.Equal("Base_copy", now[0].NodeName);
        Assert.Equal("base_layer", now[1].NodeName);
        Assert.Equal(["Base copy", "Base", "Symbols", "Adjust"], NamesOf(result).Take(4));
        Assert.Contains("compatible = \"zmk,keymap\";\n\n        Base_copy {", result,
            StringComparison.Ordinal);

        // every reference in the file moves up one: the five of the duplicated
        // layer, now held twice (the copy and the original)…
        Assert.Equal(2, CountOf(result, "&lt 7 F"));
        Assert.Equal(2, CountOf(result, "&lt 4 J"));
        Assert.Equal(2, CountOf(result, "&lt 2 ENTER"));
        Assert.DoesNotContain("&lt 6 F", result, StringComparison.Ordinal);
        // …the combo…
        Assert.Contains("bindings = <&mo 3>;", result, StringComparison.Ordinal);
        Assert.DoesNotContain("&mo 2>", result, StringComparison.Ordinal);
        // …and the tri-layer that reaches Adjust
        Assert.Contains("if-layers = <3 3>;", result, StringComparison.Ordinal);
        Assert.Contains("then-layer = <3>;", result, StringComparison.Ordinal);

        // no define: Slabwing's BASE/NAV/SYM/ADJ name no layer, so the MoErgo
        // define table the new layer would have joined does not exist here
        Assert.DoesNotContain("#define LAYER_", result, StringComparison.Ordinal);
        Assert.Contains("#define ADJ  3", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Glove80_Add_Num_Pad_At_2_Adds_Its_Define_And_Uses_The_Define_Names()
    {
        var source = Load("glove80-editor.keymap");
        var config = Parse(source);
        Assert.Equal(8, config.Layers.Count);

        var result = KeymapLayerWriter.Apply(source, new Add(2, "Num Pad", config.Layers[0].Bindings));

        Assert.Equal(9, Parse(result).Layers.Count);
        Assert.Equal(
            ["layer_Base", "layer_Symbol", "layer_Num_Pad", "layer_Lower", "layer_Magic",
             "layer_Mouse", "layer_MouseSlow", "layer_MouseFast", "layer_MouseWarp"],
            NodeNamesOf(result));

        // the export names every layer define LAYER_<name>, so the new layer gets
        // one — on the line after the last of them, which itself moved to 8
        Assert.Contains("#define LAYER_MouseWarp 8\n#define LAYER_Num_Pad 2\n", result,
            StringComparison.Ordinal);
        Assert.Contains("#define LAYER_Lower 3", result, StringComparison.Ordinal);
        // the fallback is a fallback: it never holds the layer's own number
        Assert.Contains("#ifndef LAYER_Lower\n#define LAYER_Lower 0\n#endif", result,
            StringComparison.Ordinal);

        // the copied bindings point where the copied layers went, written the way
        // the file writes them
        string block = BlockAt(result, 2);
        Assert.Contains("display-name = \"Num Pad\";", block, StringComparison.Ordinal);
        Assert.Contains("&lt LAYER_Symbol RET", block, StringComparison.Ordinal);  // 1 → 1
        Assert.Contains("&to LAYER_Lower", block, StringComparison.Ordinal);       // 2 → 3
        Assert.Contains("&lt LAYER_Mouse TAB", block, StringComparison.Ordinal);   // 4 → 5
        // a hold-tap's layer is still a renumbered number: only the layer
        // behaviors have a define-name spelling
        Assert.Contains("&magic 4 0", block, StringComparison.Ordinal);            // 3 → 4
    }

    [Fact]
    public void A_Name_A_Node_Already_Has_Gets_A_Number_Suffix()
    {
        var source = Load("slabwing.keymap");
        int keys = Parse(source).Layers[0].Bindings.Count;

        // base_layer exists; Base does not, so the first clone keeps its name
        var once = KeymapLayerWriter.Apply(source, new Add(0, "Base", Transparent(keys)));
        Assert.Equal("Base", NodeNamesOf(once)[0]);

        // and the second one has to step aside
        var twice = KeymapLayerWriter.Apply(once, new Add(0, "Base", Transparent(keys)));
        Assert.Equal(["Base_2", "Base", "base_layer", "symbols"], NodeNamesOf(twice).Take(4));
    }

    [Fact]
    public void Add_Then_Move_Both_Validate()
    {
        var source = Load("slabwing.keymap");
        var config = Parse(source);
        int keys = config.Layers[0].Bindings.Count;

        var edits = new LayerStructureEdit[] { new Add(1, "X", Transparent(keys)), new Move(1, 3) };
        var result = KeymapLayerWriter.ApplyAll(source, edits);

        // the added block moved with the separator rule a move uses, and the
        // result is still exactly what the model says
        Assert.Equal(
            LayerReferenceRemapper.ApplyAll(config, edits).Layers.Select(l => l.Name),
            NamesOf(result));
        Assert.Equal(
            ["base_layer", "symbols", "adjust_layer", "X", "nav", "nav-alt", "nav-alt-shift", "numbers"],
            NodeNamesOf(result));
    }

    // ---- no-ops and refusals -----------------------------------------------------

    [Fact]
    public void Move_Onto_Its_Own_Slot_Returns_The_Same_Instance()
    {
        var source = Load("slabwing.keymap");

        Assert.Same(source, KeymapLayerWriter.Apply(source, new Move(2, 2)));
        Assert.Same(source, KeymapLayerWriter.ApplyAll(source, []));
    }

    [Fact]
    public void Move_Out_Of_Range_Throws()
    {
        var source = Load("slabwing.keymap");

        // To is a position in the shortened list, so 7 is out of range for 7
        // layers even though it reads as "append"
        Assert.Throws<ArgumentOutOfRangeException>(
            () => KeymapLayerWriter.Apply(source, new Move(0, 7)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => KeymapLayerWriter.Apply(source, new Move(7, 0)));
    }

    [Fact]
    public void Unparsable_Source_Throws_The_Parse_Exception()
    {
        Assert.Throws<ZmkKeymapParseException>(
            () => KeymapLayerWriter.Apply("#include <behaviors.dtsi>\n", new Move(0, 0)));
    }

    [Fact]
    public void A_Layer_Name_Becomes_A_Node_Key_And_A_Quoted_Display_Name()
    {
        var source = Load("slabwing.keymap");
        int keys = Parse(source).Layers[0].Bindings.Count;

        var digits = KeymapLayerWriter.Apply(source, new Add(0, "1337 Pad", Transparent(keys)));
        // a node key may not start with a digit, and nothing outside
        // [A-Za-z0-9_] survives one
        Assert.Equal("l1337_Pad", NodeNamesOf(digits)[0]);
        Assert.Equal("1337 Pad", NamesOf(digits)[0]);

        var quoted = KeymapLayerWriter.Apply(source, new Add(0, "Ali\"s \\ Pad", Transparent(keys)));
        Assert.Equal("Ali_s___Pad", NodeNamesOf(quoted)[0]);
        Assert.Contains("display-name = \"Ali\\\"s \\\\ Pad\";", quoted, StringComparison.Ordinal);
        // and the name the model sees is exactly the one the caller passed
        Assert.Equal("Ali\"s \\ Pad", NamesOf(quoted)[0]);
    }

    [Fact]
    public void A_Single_Layer_File_Appends_Behind_One_New_Line()
    {
        var source = Load("minimal.keymap");
        var config = Parse(source);
        Assert.Single(config.Layers);

        var result = KeymapLayerWriter.Apply(source, new Add(1, "Symbol", Transparent(config.Layers[0].Bindings.Count)));

        // its only node is default_layer, so the file's style is a _layer suffix
        Assert.Equal(["default_layer", "Symbol_layer"], NodeNamesOf(result));
        // with no gap between two blocks to copy, the separator is one newline —
        // the same blank line every other file puts between its layers
        Assert.Contains("        };\n\n        Symbol_layer {", result, StringComparison.Ordinal);
        Assert.Equal(2, Parse(result).Layers.Count);
    }

    [Fact]
    public void An_Added_Block_Keeps_The_File_S_CrLf_Line_Endings()
    {
        var source = Load("minimal.keymap").Replace("\n", "\r\n", StringComparison.Ordinal);
        int keys = Parse(source).Layers[0].Bindings.Count;

        var result = KeymapLayerWriter.Apply(source, new Add(1, "Extra", Transparent(keys)));

        // nothing in the block we wrote may introduce a bare LF into a CRLF file
        for (int i = 1; i < result.Length; i++)
            if (result[i] == '\n' && result[i - 1] != '\r')
                Assert.Fail($"offset {i}: bare LF in a CRLF keymap");

        Assert.Contains("display-name = \"Extra\";", result, StringComparison.Ordinal);
        Assert.Equal(2, Parse(result).Layers.Count);
    }

    [Fact]
    public void Add_Rejects_A_Name_It_Could_Not_Write_And_An_Index_Out_Of_Range()
    {
        var source = Load("slabwing.keymap");
        int keys = Parse(source).Layers[0].Bindings.Count;

        // a control character would end up inside a string literal
        Assert.Throws<ArgumentException>(
            () => KeymapLayerWriter.Apply(source, new Add(1, "Bad", Transparent(keys))));
        Assert.Throws<ArgumentException>(
            () => KeymapLayerWriter.Apply(source, new Add(1, "   ", Transparent(keys))));
        // 7 layers are numbered 0…7 for an insert position
        Assert.Throws<ArgumentOutOfRangeException>(
            () => KeymapLayerWriter.Apply(source, new Add(99, "Fine", Transparent(keys))));
        Assert.Throws<ArgumentException>(
            () => KeymapLayerWriter.Apply(source, new Add(1, "Fine", Transparent(keys - 1))));
    }

    [Fact]
    public void Delete_Out_Of_Range_Throws()
    {
        var source = Load("slabwing.keymap");

        Assert.Throws<ArgumentOutOfRangeException>(() => KeymapLayerWriter.CheckDelete(source, 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeymapLayerWriter.CheckDelete(source, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeymapLayerWriter.Apply(source, new Delete(7)));
    }

    // ---- delete: the pre-check, what it refuses, and what a delete writes --------

    [Fact]
    public void Slabwing_Delete_Adjust_Is_Refused_By_The_Combo_And_The_Tri_Layer()
    {
        var source = Load("slabwing.keymap");

        var check = KeymapLayerWriter.CheckDelete(source, 2);

        Assert.False(check.CanDelete);

        // the combo is reported first and as a unit: its binding and its layer
        // scope sit apart, and it is the combo the user has to edit
        var combo = check.Blockers[0];
        Assert.Equal(LayerDeleteBlockerKind.Combo, combo.Kind);
        Assert.Equal("syslayer", combo.Name);
        Assert.Equal(0, combo.Line);

        // then the tri-layer that reaches Adjust, one blocker per reference
        var tri = check.Blockers.Skip(1).ToList();
        Assert.All(tri, blocker =>
        {
            Assert.Equal(LayerDeleteBlockerKind.ConditionalLayer, blocker.Kind);
            Assert.Equal("adjust", blocker.Name);
        });
        Assert.Equal([33, 33, 34], tri.Select(b => b.Line).ToList());

        // and a caller that skipped the pre-check is refused for all of them
        var error = Assert.Throws<InvalidOperationException>(
            () => KeymapLayerWriter.Apply(source, new Delete(2)));
        Assert.Contains("syslayer", error.Message, StringComparison.Ordinal);
        Assert.Contains("adjust", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Slabwing_Delete_Numbers_Clears_The_Key_That_Pointed_At_It()
    {
        var source = Load("slabwing.keymap");
        var config = Parse(source);

        var check = KeymapLayerWriter.CheckDelete(source, 6);
        Assert.True(check.CanDelete);
        Assert.Empty(check.Blockers);
        // exactly the base layer's `&lt 6 F`, counted the way the dialog does
        Assert.Equal(LayerReferenceRemapper.CountClearedBindings(config, 6), check.ClearedBindings);
        Assert.True(check.ClearedBindings >= 1);

        var result = KeymapLayerWriter.Apply(source, new Delete(6));

        Assert.Equal(6, Parse(result).Layers.Count);
        Assert.Equal(
            LayerReferenceRemapper.Apply(config, new Delete(6)).Layers.Select(l => l.Name),
            NamesOf(result));
        Assert.Equal(
            ["base_layer", "symbols", "adjust_layer", "nav", "nav-alt", "nav-alt-shift"],
            NodeNamesOf(result));

        // the layer-shift key lost its target and kept its tap side
        Assert.DoesNotContain("&lt 6 F", result, StringComparison.Ordinal);
        Assert.Contains("&kp F ", result, StringComparison.Ordinal);

        // Numbers was the last layer, so nothing below it had to move
        Assert.Contains("&lt 3 J", result, StringComparison.Ordinal);
        Assert.Contains("&lt 1 ENTER", result, StringComparison.Ordinal);
        Assert.Contains("bindings = <&mo 2>;", result, StringComparison.Ordinal);
        Assert.Contains("if-layers = <2 2>;", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Deleting a layer in the middle renumbers the numbers this writer cannot
    /// check against the model — a tri-layer's <c>if-layers</c> is not part of it
    /// — so the ones that moved are asserted in the text by hand.
    /// </summary>
    [Fact]
    public void Slabwing_Delete_Nav_Renumbers_The_Conditional_Layers()
    {
        var source = Load("slabwing.keymap");

        var check = KeymapLayerWriter.CheckDelete(source, 1);
        Assert.True(check.CanDelete);
        Assert.Equal(1, check.ClearedBindings); // the base layer's &lt 1 ENTER

        var result = KeymapLayerWriter.Apply(source, new Delete(1));

        // the tri-layer reached Adjust; with Nav gone, Adjust is layer 1
        Assert.Contains("if-layers = <1 1>;", result, StringComparison.Ordinal);
        Assert.Contains("then-layer = <1>;", result, StringComparison.Ordinal);
        Assert.DoesNotContain("if-layers = <2 2>;", result, StringComparison.Ordinal);

        // so did the combo that fires it, and every key above the gap
        Assert.Contains("bindings = <&mo 1>;", result, StringComparison.Ordinal);
        Assert.Contains("&lt 5 F", result, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt 6 F", result, StringComparison.Ordinal);

        // and the key that pointed at Nav is a plain tap now
        Assert.Contains("&kp ENTER", result, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt 1 ENTER", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Cosmos82_Delete_Sys_Renumbers_Everything_Above_It()
    {
        var source = Load("cosmos82.keymap");
        var config = Parse(source);

        var check = KeymapLayerWriter.CheckDelete(source, 1);
        Assert.True(check.CanDelete); // no combos, no defines, no tri-layer here
        Assert.Equal(1, check.ClearedBindings); // the base layer's &mo 1

        var result = KeymapLayerWriter.Apply(source, new Delete(1));

        Assert.Equal(6, Parse(result).Layers.Count);
        Assert.Equal(
            LayerReferenceRemapper.Apply(config, new Delete(1)).Layers.Select(l => l.Name),
            NamesOf(result));
        Assert.DoesNotContain("sys_layer", result, StringComparison.Ordinal);

        // the key that pushed Sys became &none; nothing else in the file used it
        Assert.Equal(0, CountOf(source, "&none"));
        Assert.Equal(1, CountOf(result, "&none"));
        Assert.DoesNotContain("&mo 1", result, StringComparison.Ordinal);

        // every layer above the deleted one slid down one
        Assert.Contains("&lt 1 ENTER", result, StringComparison.Ordinal); // was 2, Symbol
        Assert.Contains("&lt 2 J", result, StringComparison.Ordinal);      // was 3
        Assert.Contains("&lt 3 K", result, StringComparison.Ordinal);      // was 4
        Assert.Contains("&lt 4 L", result, StringComparison.Ordinal);      // was 5
        Assert.Contains("&lt 5 F", result, StringComparison.Ordinal);      // was 6, Numbers
        Assert.DoesNotContain("&lt 2 ENTER", result, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt 3 J", result, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt 6 F", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Glove80_Delete_Lower_Is_Blocked_By_Its_Define_Name()
    {
        var source = Load("glove80-editor.keymap");

        var check = KeymapLayerWriter.CheckDelete(source, 2);

        Assert.False(check.CanDelete);
        Assert.Single(check.Blockers);

        var blocker = check.Blockers[0];
        Assert.Equal(LayerDeleteBlockerKind.DefineUsage, blocker.Kind);
        Assert.Equal("LAYER_Lower", blocker.Name);
        Assert.Equal(103, blocker.Line); // ZMK_TD_LAYER(lower, LAYER_Lower)
    }

    [Fact]
    public void Glove80_Delete_MouseSlow_Is_Blocked_By_The_Input_Listener()
    {
        var source = Load("glove80-editor.keymap");

        var check = KeymapLayerWriter.CheckDelete(source, 5);

        Assert.False(check.CanDelete);
        // its listener's node name on line 86 and its `layers` list on 87
        Assert.Equal([86, 87], check.Blockers.Select(b => b.Line).ToList());
        Assert.All(check.Blockers, blocker =>
        {
            Assert.Equal(LayerDeleteBlockerKind.DefineUsage, blocker.Kind);
            Assert.Equal("LAYER_MouseSlow", blocker.Name);
        });
    }

    [Fact]
    public void Glove80_Delete_Magic_Takes_Its_Define_With_It()
    {
        var source = Load("glove80-editor.keymap");
        var config = Parse(source);

        var check = KeymapLayerWriter.CheckDelete(source, 3);
        Assert.True(check.CanDelete);
        Assert.Equal(2, check.ClearedBindings); // the two `&magic LAYER_Magic 0`

        var result = KeymapLayerWriter.Apply(source, new Delete(3));

        // the define line went with the layer, and no spelling of its name is
        // left: the keys that held it are &none now
        Assert.Equal(0, CountOf(result, "LAYER_Magic"));
        Assert.DoesNotContain("&magic", result, StringComparison.Ordinal);

        // the table below it closed up; the #ifndef fallback is not part of it
        Assert.Contains("#define LAYER_Lower 2", result, StringComparison.Ordinal);
        Assert.Contains("#define LAYER_Mouse 3", result, StringComparison.Ordinal);
        Assert.Contains("#define LAYER_MouseWarp 6", result, StringComparison.Ordinal);
        Assert.Contains("#ifndef LAYER_Lower\n#define LAYER_Lower 0\n#endif", result,
            StringComparison.Ordinal);

        Assert.Equal(7, Parse(result).Layers.Count);
        Assert.Equal(
            LayerReferenceRemapper.Apply(config, new Delete(3)).Layers.Select(l => l.Name),
            NamesOf(result));
    }

    [Fact]
    public void The_Last_Layer_Cannot_Be_Deleted()
    {
        var source = Load("minimal.keymap");

        var check = KeymapLayerWriter.CheckDelete(source, 0);

        Assert.False(check.CanDelete);
        Assert.Single(check.Blockers);
        Assert.Equal(LayerDeleteBlockerKind.LastLayer, check.Blockers[0].Kind);
        Assert.Equal(0, check.Blockers[0].Line);
        Assert.Equal(0, check.ClearedBindings); // nothing to clear but itself

        Assert.Throws<InvalidOperationException>(
            () => KeymapLayerWriter.Apply(source, new Delete(0)));
    }

    [Fact]
    public void Add_Then_Delete_The_Same_Slot_Is_The_Original_Text_Byte_For_Byte()
    {
        var source = Load("cosmos82.keymap");

        var result = KeymapLayerWriter.ApplyAll(
            source, [new Add(1, "Tmp", Transparent(82)), new Delete(1)]);

        // the block Add wrote is exactly the span Delete cuts, and the numbers it
        // pushed up are pushed back: what is left is the file we started with
        Assert.Equal(source, result);
    }

    [Fact]
    public void A_Deleted_Define_Line_Takes_Its_Carriage_Return_With_It()
    {
        var source = Load("glove80-editor.keymap").Replace("\n", "\r\n", StringComparison.Ordinal);

        var result = KeymapLayerWriter.Apply(source, new Delete(3));

        // a whole-line removal must not leave a lone LF, and must not leave half
        // a define line either
        for (int i = 1; i < result.Length; i++)
            if (result[i] == '\n' && result[i - 1] != '\r')
                Assert.Fail($"offset {i}: bare LF in a CRLF keymap");

        Assert.Equal(0, CountOf(result, "LAYER_Magic"));
        Assert.Contains("#define LAYER_Mouse 3", result, StringComparison.Ordinal);
        Assert.Equal(7, Parse(result).Layers.Count);
    }

    // ---- the bug guard -----------------------------------------------------------

    /// <summary>
    /// A define that the file re-defines further down is the one case the text
    /// scanner cannot renumber (only the first definition of a name may name a
    /// layer, and the preprocessor's last value wins). The written text would
    /// then still point at the old layer — the guard is what keeps that file from
    /// being handed back as a result. It drives the real pipeline through the
    /// public entry point, so a seam would only weaken the check.
    /// </summary>
    [Fact]
    public void A_Define_The_Writer_Cannot_Rewrite_Is_Refused_Instead_Of_Written_Wrong()
    {
        var result = Assert.Throws<InvalidOperationException>(
            () => KeymapLayerWriter.Apply(RedefinedLayerDefine, new Move(2, 0)));

        Assert.Contains("layer 1 ('Alpha') key 0", result.Message, StringComparison.Ordinal);
        Assert.Contains("&mo 2", result.Message, StringComparison.Ordinal);
        Assert.Contains("&mo 0", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Macro_s_Layer_Reference_Follows_The_Move()
    {
        var result = KeymapLayerWriter.Apply(MacroKeymap("&mo 2", "#define UNUSED 0"), new Move(2, 0));

        Assert.Contains("bindings = <&mo 0>;", result, StringComparison.Ordinal);
        Assert.Equal("0", Assert.Single(Parse(result).Macros).Bindings[0].Params[0]);
    }

    [Fact]
    public void A_Macro_The_Writer_Gets_Wrong_Is_Refused()
    {
        // Only the macro uses the twice-defined name, so the layers and combos
        // all match and the macro comparison is what catches it.
        var source = MacroKeymap("&mo LAYER_Alpha", "#define LAYER_Alpha 1\n#define LAYER_Alpha 2");

        var ex = Assert.Throws<InvalidOperationException>(
            () => KeymapLayerWriter.Apply(source, new Move(2, 0)));

        Assert.Contains("macro", ex.Message, StringComparison.Ordinal);
        Assert.Contains("&mo 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("&mo 0", ex.Message, StringComparison.Ordinal);
    }

    private static string MacroKeymap(string macroBinding, string defines) => $$"""
        #include <behaviors.dtsi>
        #include <dt-bindings/zmk/keys.h>

        {{defines}}

        / {
            macros {
                go: go {
                    compatible = "zmk,behavior-macro";
                    #binding-cells = <0>;
                    bindings = <{{macroBinding}}>;
                };
            };

            keymap {
                compatible = "zmk,keymap";

                alpha {
                    bindings = <&kp A  &go>;
                };

                beta {
                    bindings = <&kp B  &trans>;
                };

                gamma {
                    bindings = <&kp C  &trans>;
                };
            };
        };
        """;

    /// <summary>
    /// Three layers. <c>LAYER_Alpha</c> is defined twice, so its live value is
    /// <c>2</c> (the preprocessor's last-wins) while the writer sees the first
    /// definition and reads it as layer 1.
    /// </summary>
    private const string RedefinedLayerDefine = """
        #include <behaviors.dtsi>
        #include <dt-bindings/zmk/keys.h>

        #define LAYER_Alpha 1
        #define LAYER_Alpha 2

        / {
            keymap {
                compatible = "zmk,keymap";

                alpha {
                    display-name = "Alpha";
                    bindings = <
                        &mo LAYER_Alpha  &trans
                    >;
                };

                beta {
                    display-name = "Beta";
                    bindings = <
                        &trans  &trans
                    >;
                };

                gamma {
                    display-name = "Gamma";
                    bindings = <
                        &lt 0 B  &trans
                    >;
                };
            };
        };
        """;
}
