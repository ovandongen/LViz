using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;
using static LViz.Core.Keymap.Editing.ComboEdit;
using Xunit;

namespace LViz.Tests.Keymap;

/// <summary>
/// The public face of editing a combo in the <c>.keymap</c> text:
/// <see cref="KeymapComboWriter"/> rewrites only the properties the edit
/// changes, removes a property set back to its default, adds a missing one as
/// its own line, appends a new node after the last combo (creating the
/// <c>combos</c> node when the file has none) and cuts a deleted node out with
/// its blank line — while every other byte, comments included, stays exactly
/// where it was. What matters here is what it may <b>not</b> do: touch a line
/// the edit does not change (a reordered position list is not a change at all),
/// write a layer name the file never uses, leave a double blank line behind, or
/// hand back a text that re-parses to a different combo list or keymap.
/// </summary>
public class KeymapComboWriterTests
{
    private const string Closing = "        };";

    private const string SafetyNet = "        // safety net: always reachable, even if a layer is broken";

    private static string Slabwing => KeymapFixtures.Read("slabwing.keymap");

    private static string Glove80 => KeymapFixtures.Read("glove80.keymap");

    private static string Go60 => KeymapFixtures.Read("go60.keymap");

    private static string Cosmos => KeymapFixtures.Read("cosmos82.keymap");

    private static KeyboardConfig Parse(string source) =>
        ZmkKeymapLoader.LoadFromText(source, "combo-writer-test");

    private static ZmkCombo Combo(string source, string nodeName) =>
        Parse(source).Combos.Single(c => string.Equals(c.NodeName, nodeName, StringComparison.Ordinal));

    private static KeyBinding Keypress(string param) => new("&kp", new[] { param });

    /// <summary>A combo as the dialog hands one over to be added: its node name,
    /// its keys, the binding it fires and the optional properties. The editor
    /// name never reaches the file — a new node is named by its node name — and a
    /// null <paramref name="layers"/> means "all layers", which stays unwritten.</summary>
    private static ZmkCombo NewCombo(
        string nodeName,
        IReadOnlyList<int> positions,
        KeyBinding binding,
        IReadOnlyList<int>? layers = null,
        int? timeoutMs = null,
        int? requirePriorIdleMs = null,
        bool slowRelease = false) =>
        new("Editor name", "", positions, layers ?? new[] { -1 }, binding)
        {
            NodeName = nodeName,
            TimeoutMs = timeoutMs,
            RequirePriorIdleMs = requirePriorIdleMs,
            SlowRelease = slowRelease,
        };

    // ---- reading a written node back -------------------------------------------

    /// <summary>The span of a combo node's block, header line through its
    /// closing <c>};</c>. The fixtures indent combo nodes with eight spaces.</summary>
    private static (int Start, int End) NodeSpan(string text, string node)
    {
        int header = text.IndexOf("        " + node + " {", StringComparison.Ordinal);
        Assert.True(header >= 0, $"no combo node '{node}' in this text");

        int close = text.IndexOf(Closing, header, StringComparison.Ordinal);
        Assert.True(close > header, $"combo node '{node}' is never closed");

        return (header, close + Closing.Length);
    }

    /// <summary>The node's block, verbatim.</summary>
    private static string NodeBlock(string text, string node)
    {
        var (start, end) = NodeSpan(text, node);
        return text[start..end];
    }

    /// <summary>The node's block, one entry per line, without carriage returns.</summary>
    private static List<string> NodeLines(string text, string node) =>
        NodeBlock(text, node).Split('\n').Select(line => line.TrimEnd('\r')).ToList();

    /// <summary>Everything outside the node's block is byte-identical.</summary>
    private static void AssertNothingOutsideNodeChanged(string before, string after, string node)
    {
        var was = NodeSpan(before, node);
        var now = NodeSpan(after, node);

        Assert.Equal(before[..was.Start], after[..now.Start]);
        Assert.Equal(before[was.End..], after[now.End..]);
    }

    /// <summary>Same line count, and exactly one line differs: it reads
    /// <paramref name="expected"/> where the source held something else.</summary>
    private static void AssertExactlyOneLineChanged(string before, string after, string expected)
    {
        var was = before.Split('\n');
        var now = after.Split('\n');

        Assert.Equal(was.Length, now.Length);

        var differing = Enumerable.Range(0, was.Length).Where(i => was[i] != now[i]).ToList();
        Assert.Single(differing);
        Assert.Equal(expected, now[differing[0]].TrimEnd('\r'));
    }

    /// <summary>The <paramref name="count"/> lines that follow a node's closing
    /// <c>};</c> line — what a test appends after it, blank lines included.</summary>
    private static List<string> LinesAfterNode(string text, string node, int count)
    {
        var (_, end) = NodeSpan(text, node);
        int newline = text.IndexOf('\n', end);
        Assert.True(newline >= 0, $"the closing line of '{node}' never ends");

        return text[(newline + 1)..].Split('\n').Select(line => line.TrimEnd('\r')).Take(count).ToList();
    }

    /// <summary>The <c>combos</c> node's own block, header line through its
    /// closing <c>};</c>, one entry per line. The fixtures indent it with four
    /// spaces and its children with eight.</summary>
    private static List<string> CombosLines(string text)
    {
        int header = text.IndexOf("\n    combos {", StringComparison.Ordinal);
        Assert.True(header >= 0, "no combos node in this text");

        const string closer = "\n    };";
        int close = text.IndexOf(closer, header, StringComparison.Ordinal);
        Assert.True(close > header, "the combos node is never closed");

        return text[(header + 1)..(close + closer.Length)]
            .Split('\n').Select(line => line.TrimEnd('\r')).ToList();
    }

    /// <summary>No two blank lines sit next to each other in the combos node.</summary>
    private static void AssertNoDoubleBlankLine(List<string> combosLines)
    {
        for (int i = 1; i < combosLines.Count; i++)
            Assert.False(
                combosLines[i - 1].Length == 0 && combosLines[i].Length == 0,
                $"blank lines {i - 1} and {i} of the combos node are both empty");
    }

    // ---- a changed value is rewritten where it stands ----------------------------

    [Fact]
    public void Slabwing_F12_Binding_Rewrites_Exactly_One_Line()
    {
        var source = Slabwing;
        var f12 = Combo(source, "f12");

        var result = KeymapComboWriter.Apply(source, [new Change("f12", f12 with { Binding = Keypress("F11") })]);

        AssertExactlyOneLineChanged(source, result, "            bindings = <&kp F11>;");
        AssertNothingOutsideNodeChanged(source, result, "f12");

        var written = Combo(result, "f12");
        Assert.Equal("&kp", written.Binding.Behavior);
        Assert.Equal(new[] { "F11" }, written.Binding.Params);
        Assert.Equal(new[] { 0, 1 }, written.KeyPositions);
    }

    [Fact]
    public void Slabwing_F12_Same_Binding_Changes_Nothing()
    {
        var source = Slabwing;
        var f12 = Combo(source, "f12");

        var result = KeymapComboWriter.Apply(source, [new Change("f12", f12 with { Binding = Keypress("F12") })]);

        Assert.Equal(source, result);
    }

    [Fact]
    public void Slabwing_Syslayer_Positions_Reordered_Change_Nothing()
    {
        var source = Slabwing;
        var syslayer = Combo(source, "syslayer");
        Assert.Equal(new[] { 53, 52 }, syslayer.KeyPositions);

        var result = KeymapComboWriter.Apply(source, [new Change("syslayer", syslayer with { KeyPositions = new[] { 52, 53 } })]);

        Assert.Equal(source, result);
    }

    [Fact]
    public void Slabwing_Syslayer_Extra_Position_Is_Written_In_The_Combo_s_Own_Order()
    {
        var source = Slabwing;
        var syslayer = Combo(source, "syslayer");

        var result = KeymapComboWriter.Apply(
            source, [new Change("syslayer", syslayer with { KeyPositions = new[] { 53, 52, 40 } })]);

        Assert.Equal(
            new[]
            {
                "        syslayer {",
                "            bindings = <&mo 2>;",
                "            key-positions = <53 52 40>;",
                Closing,
            },
            NodeLines(result, "syslayer"));
        AssertNothingOutsideNodeChanged(source, result, "syslayer");
        Assert.Equal(new[] { 53, 52, 40 }, Combo(result, "syslayer").KeyPositions);
    }

    [Fact]
    public void Glove80_Positions_Use_The_Defines_Its_Combos_Already_Use()
    {
        var source = Glove80;
        var snipe = Combo(source, "combo_snipe3");
        Assert.Equal(new[] { 66, 68 }, snipe.KeyPositions);

        var result = KeymapComboWriter.Apply(
            source, [new Change("combo_snipe3", snipe with { KeyPositions = new[] { 66, 69 } })]);

        Assert.Equal(
            new[]
            {
                "        combo_snipe3 {",
                "            key-positions = <POS_LH_C4R6 POS_LH_T4>;",
                "            bindings = <&snipe_mo>;",
                Closing,
            },
            NodeLines(result, "combo_snipe3"));
        AssertExactlyOneLineChanged(source, result, "            key-positions = <POS_LH_C4R6 POS_LH_T4>;");

        var written = Combo(result, "combo_snipe3");
        Assert.Equal(new[] { 66, 69 }, written.KeyPositions);
        Assert.Equal("&snipe_mo", written.Binding.Behavior);
    }

    // ---- a property that is not there yet is added as its own line ---------------

    [Fact]
    public void Slabwing_F12_Gets_Layers_Before_Its_Closing_Brace()
    {
        var source = Slabwing;
        var f12 = Combo(source, "f12");

        var result = KeymapComboWriter.Apply(source, [new Change("f12", f12 with { Layers = new[] { 1 } })]);

        Assert.Equal(
            new[]
            {
                "        f12 {",
                "            bindings = <&kp F12>;",
                "            key-positions = <0 1>;",
                "            layers = <1>;",
                Closing,
            },
            NodeLines(result, "f12"));
        AssertNothingOutsideNodeChanged(source, result, "f12");

        // the layer defines are unused in this file, so the number stays a number
        Assert.Equal(new[] { 1 }, Combo(result, "f12").Layers);
    }

    [Fact]
    public void Slabwing_F12_Gets_SlowRelease()
    {
        var source = Slabwing;
        var f12 = Combo(source, "f12");

        var result = KeymapComboWriter.Apply(source, [new Change("f12", f12 with { SlowRelease = true })]);

        Assert.Equal(
            new[]
            {
                "        f12 {",
                "            bindings = <&kp F12>;",
                "            key-positions = <0 1>;",
                "            slow-release;",
                Closing,
            },
            NodeLines(result, "f12"));
        AssertNothingOutsideNodeChanged(source, result, "f12");
        Assert.True(Combo(result, "f12").SlowRelease);
    }

    [Fact]
    public void Slabwing_F12_Gets_RequirePriorIdleMs()
    {
        var source = Slabwing;
        var f12 = Combo(source, "f12");

        var result = KeymapComboWriter.Apply(source, [new Change("f12", f12 with { RequirePriorIdleMs = 150 })]);

        Assert.Equal(
            new[]
            {
                "        f12 {",
                "            bindings = <&kp F12>;",
                "            key-positions = <0 1>;",
                "            require-prior-idle-ms = <150>;",
                Closing,
            },
            NodeLines(result, "f12"));
        AssertNothingOutsideNodeChanged(source, result, "f12");
        Assert.Equal(150, Combo(result, "f12").RequirePriorIdleMs);
    }

    // ---- a property set back to its default leaves the file ----------------------

    [Fact]
    public void Slabwing_Bootloader_Losing_Its_Timeout_Loses_Only_That_Line()
    {
        var source = Slabwing;
        var bootloader = Combo(source, "combo_bootloader");
        Assert.Equal(80, bootloader.TimeoutMs);

        var result = KeymapComboWriter.Apply(source, [new Change("combo_bootloader", bootloader with { TimeoutMs = null })]);

        Assert.Equal(
            new[]
            {
                "        combo_bootloader {",
                "            key-positions = <1 2 12>;",
                "            bindings = <&bootloader>;",
                Closing,
            },
            NodeLines(result, "combo_bootloader"));
        AssertNothingOutsideNodeChanged(source, result, "combo_bootloader");

        var written = Combo(result, "combo_bootloader");
        Assert.Null(written.TimeoutMs);
        Assert.Equal(new[] { 1, 2, 12 }, written.KeyPositions);
        Assert.Equal("&bootloader", written.Binding.Behavior);
    }

    [Fact]
    public void Slabwing_Bootloader_Timeout_Keeps_Its_Place()
    {
        var source = Slabwing;
        var bootloader = Combo(source, "combo_bootloader");

        var result = KeymapComboWriter.Apply(source, [new Change("combo_bootloader", bootloader with { TimeoutMs = 60 })]);

        Assert.Equal(
            new[]
            {
                "        combo_bootloader {",
                "            timeout-ms = <60>;",
                "            key-positions = <1 2 12>;",
                "            bindings = <&bootloader>;",
                Closing,
            },
            NodeLines(result, "combo_bootloader"));
        AssertExactlyOneLineChanged(source, result, "            timeout-ms = <60>;");
        Assert.Equal(60, Combo(result, "combo_bootloader").TimeoutMs);
    }

    [Fact]
    public void Slabwing_F12_Dropping_All_Layer_Scope_Keeps_The_Node_As_It_Was()
    {
        var source = Slabwing;
        var f12 = Combo(source, "f12");

        var withOneLayer = KeymapComboWriter.Apply(source, [new Change("f12", f12 with { Layers = new[] { 1 } })]);

        // back to all layers: the added line goes away and nothing else moves
        var result = KeymapComboWriter.Apply(
            withOneLayer, [new Change("f12", Combo(withOneLayer, "f12") with { Layers = new[] { -1 } })]);

        Assert.Equal(NodeLines(source, "f12"), NodeLines(result, "f12"));
        Assert.Equal(Slabwing, result);
        Assert.Equal(new[] { -1 }, Combo(result, "f12").Layers);
    }

    // ---- a rename ----------------------------------------------------------------

    [Fact]
    public void Slabwing_Rename_Writes_The_Header_Line()
    {
        var source = Slabwing;
        var f12 = Combo(source, "f12");

        var result = KeymapComboWriter.Apply(source, [new Change("f12", f12 with { NodeName = "combo_f12" })]);

        AssertExactlyOneLineChanged(source, result, "        combo_f12 {");
        Assert.Equal(
            new[]
            {
                "        combo_f12 {",
                "            bindings = <&kp F12>;",
                "            key-positions = <0 1>;",
                Closing,
            },
            NodeLines(result, "combo_f12"));

        var written = Combo(result, "combo_f12");
        Assert.Equal("combo_f12", written.NodeName);
        Assert.Equal("&kp", written.Binding.Behavior);
    }

    [Fact]
    public void Slabwing_Rename_Keeps_A_Label_Prefix_And_The_Combo_s_Name()
    {
        var source = Slabwing.Replace("        f12 {", "        touch: f12 {", StringComparison.Ordinal);
        Assert.Equal("touch", Combo(source, "f12").Name);

        var result = KeymapComboWriter.Apply(source, [new Change("f12", Combo(source, "f12") with { NodeName = "combo_f12" })]);

        AssertExactlyOneLineChanged(source, result, "        touch: combo_f12 {");

        var written = Combo(result, "combo_f12");
        Assert.Equal("touch", written.Name);
        Assert.Equal(new[] { 0, 1 }, written.KeyPositions);
    }

    [Fact]
    public void Comments_Inside_The_Node_Are_Left_Alone()
    {
        var source = Slabwing.Replace(
            "        f12 {\n            bindings = <&kp F12>;\n",
            "        f12 {\n            // fires on the touch bar\n            bindings = <&kp F12>;\n",
            StringComparison.Ordinal);

        var result = KeymapComboWriter.Apply(source,
            [new Change("f12", Combo(source, "f12") with { Binding = Keypress("F11"), SlowRelease = true })]);

        Assert.Equal(
            new[]
            {
                "        f12 {",
                "            // fires on the touch bar",
                "            bindings = <&kp F11>;",
                "            key-positions = <0 1>;",
                "            slow-release;",
                Closing,
            },
            NodeLines(result, "f12"));
        Assert.True(Combo(result, "f12").SlowRelease);
    }

    [Fact]
    public void An_Inserted_Line_Uses_The_Source_s_Own_Newline()
    {
        var source = Slabwing.Replace("\n", "\r\n", StringComparison.Ordinal);

        var result = KeymapComboWriter.Apply(source, [new Change("f12", Combo(source, "f12") with { Layers = new[] { 1 } })]);

        // every newline in the result is still a CRLF pair
        Assert.Equal(result.Count(c => c == '\n'), result.Count(c => c == '\r'));
        Assert.EndsWith("            layers = <1>;\r\n        };", NodeBlock(result, "f12"), StringComparison.Ordinal);
        Assert.Equal(new[] { 1 }, Combo(result, "f12").Layers);
    }

    // ---- a new node is appended after the last combo ------------------------------

    [Fact]
    public void Slabwing_Add_Appends_The_Node_After_The_Last_Combo()
    {
        var source = Slabwing;
        var before = Parse(source);

        var result = KeymapComboWriter.Apply(
            source, [new Add(NewCombo("combo_esc", new[] { 10, 11 }, Keypress("ESC"), timeoutMs: 30))]);

        Assert.Equal(
            new[]
            {
                "",
                "        combo_esc {",
                "            timeout-ms = <30>;",
                "            key-positions = <10 11>;",
                "            bindings = <&kp ESC>;",
                "        };",
                "    };",
            },
            LinesAfterNode(result, "syslayer", 7));

        // everything up to the insertion point is untouched
        Assert.Equal(source[..NodeSpan(source, "syslayer").End], result[..NodeSpan(result, "syslayer").End]);

        var after = Parse(result);
        Assert.Equal(before.Combos.Select(c => c.NodeName).Append("combo_esc"), after.Combos.Select(c => c.NodeName));
        foreach (var combo in before.Combos)
            Assert.True(combo.IsSameAs(after.Combos.Single(c => c.NodeName == combo.NodeName)));

        var written = Combo(result, "combo_esc");
        Assert.Equal("combo_esc", written.Name);
        Assert.Equal(new[] { 10, 11 }, written.KeyPositions);
        Assert.Equal(new[] { -1 }, written.Layers);
        Assert.Equal(30, written.TimeoutMs);
        Assert.Equal("&kp", written.Binding.Behavior);
        Assert.Equal(new[] { "ESC" }, written.Binding.Params);
    }

    [Fact]
    public void Slabwing_Add_Writes_Every_New_Property_In_The_Docs_Order()
    {
        var result = KeymapComboWriter.Apply(Slabwing, [new Add(NewCombo(
            "combo_fancy",
            new[] { 10, 11 },
            Keypress("ESC"),
            layers: new[] { 1, 2 },
            timeoutMs: 30,
            requirePriorIdleMs: 150,
            slowRelease: true))]);

        Assert.Equal(
            new[]
            {
                "        combo_fancy {",
                "            timeout-ms = <30>;",
                "            key-positions = <10 11>;",
                "            layers = <1 2>;",
                "            bindings = <&kp ESC>;",
                "            require-prior-idle-ms = <150>;",
                "            slow-release;",
                Closing,
            },
            NodeLines(result, "combo_fancy"));

        var written = Combo(result, "combo_fancy");
        Assert.Equal(new[] { 1, 2 }, written.Layers);
        Assert.Equal(150, written.RequirePriorIdleMs);
        Assert.True(written.SlowRelease);
    }

    [Fact]
    public void Glove80_Add_Spells_Its_Positions_With_The_Defines_Its_Combos_Use()
    {
        var result = KeymapComboWriter.Apply(
            Glove80, [new Add(NewCombo("combo_tab", new[] { 53, 54 }, Keypress("TAB")))]);

        Assert.Equal(
            new[]
            {
                "        combo_tab {",
                "            key-positions = <POS_LH_T2 POS_LH_T3>;",
                "            bindings = <&kp TAB>;",
                Closing,
            },
            NodeLines(result, "combo_tab"));
        Assert.Equal(new[] { "", "        combo_tab {" }, LinesAfterNode(result, "combo_snipe3", 2));

        var written = Combo(result, "combo_tab");
        Assert.Equal(new[] { 53, 54 }, written.KeyPositions);
        Assert.Equal("&kp", written.Binding.Behavior);
    }

    [Fact]
    public void Go60_Add_Spells_Its_Positions_With_The_Defines_Its_Combos_Use()
    {
        var result = KeymapComboWriter.Apply(
            Go60, [new Add(NewCombo("combo_esc", new[] { 0, 1 }, Keypress("ESC")))]);

        Assert.Equal(
            new[]
            {
                "        combo_esc {",
                "            key-positions = <POS_LH_C6R1 POS_LH_C5R1>;",
                "            bindings = <&kp ESC>;",
                Closing,
            },
            NodeLines(result, "combo_esc"));

        var written = Combo(result, "combo_esc");
        Assert.Equal(new[] { 0, 1 }, written.KeyPositions);
        Assert.Equal(new[] { -1 }, written.Layers);
    }

    [Fact]
    public void Cosmos_Add_Creates_The_Combos_Node_Before_The_Keymap()
    {
        var source = Cosmos;
        Assert.Empty(Parse(source).Combos);

        var result = KeymapComboWriter.Apply(
            source, [new Add(NewCombo("combo_ESC", new[] { 0, 1 }, Keypress("ESC")))]);

        // the new node sits right above the keymap, in the file's own 4-space indent
        int keymapLine = result.IndexOf("\n    keymap {", StringComparison.Ordinal) + 1;
        Assert.True(keymapLine > 0, "no keymap node in this text");
        Assert.EndsWith(
            "    combos {\n"
            + "        compatible = \"zmk,combos\";\n"
            + "\n"
            + "        combo_ESC {\n"
            + "            key-positions = <0 1>;\n"
            + "            bindings = <&kp ESC>;\n"
            + "        };\n"
            + "    };\n"
            + "\n",
            result[..keymapLine],
            StringComparison.Ordinal);

        var after = Parse(result);
        Assert.Single(after.Combos);
        Assert.Equal("combo_ESC", after.Combos[0].NodeName);
        Assert.Equal(new[] { 0, 1 }, after.Combos[0].KeyPositions);
        Assert.Equal(7, after.Layers.Count);
    }

    [Fact]
    public void Cosmos_Two_Adds_In_One_Call_Share_The_Node_The_First_Created()
    {
        var result = KeymapComboWriter.Apply(Cosmos,
        [
            new Add(NewCombo("combo_ESC", new[] { 0, 1 }, Keypress("ESC"))),
            new Add(NewCombo("combo_TAB", new[] { 2, 3 }, Keypress("TAB"))),
        ]);

        int keymapLine = result.IndexOf("\n    keymap {", StringComparison.Ordinal) + 1;
        Assert.EndsWith(
            "    combos {\n"
            + "        compatible = \"zmk,combos\";\n"
            + "\n"
            + "        combo_ESC {\n"
            + "            key-positions = <0 1>;\n"
            + "            bindings = <&kp ESC>;\n"
            + "        };\n"
            + "\n"
            + "        combo_TAB {\n"
            + "            key-positions = <2 3>;\n"
            + "            bindings = <&kp TAB>;\n"
            + "        };\n"
            + "    };\n"
            + "\n",
            result[..keymapLine],
            StringComparison.Ordinal);

        var after = Parse(result);
        Assert.Equal(new[] { "combo_ESC", "combo_TAB" }, after.Combos.Select(c => c.NodeName));
        Assert.Equal(7, after.Layers.Count);
    }

    [Fact]
    public void Slabwing_Add_Keeps_Every_Newline_A_CRLF_Pair()
    {
        var source = Slabwing.Replace("\n", "\r\n", StringComparison.Ordinal);

        var result = KeymapComboWriter.Apply(
            source, [new Add(NewCombo("combo_esc", new[] { 10, 11 }, Keypress("ESC")))]);

        Assert.Equal(result.Count(c => c == '\n'), result.Count(c => c == '\r'));
        Assert.Contains(
            "\r\n\r\n        combo_esc {\r\n            key-positions = <10 11>;\r\n"
            + "            bindings = <&kp ESC>;\r\n        };\r\n    };",
            result,
            StringComparison.Ordinal);
        Assert.Equal(new[] { 10, 11 }, Combo(result, "combo_esc").KeyPositions);
    }

    [Fact]
    public void An_Inline_Combos_Node_Keeps_The_Node_It_Gained_Inside_It()
    {
        // no fixture writes a combos node on one line, so build one
        var source = Cosmos.Replace(
            "    keymap {",
            "    combos { compatible = \"zmk,combos\"; };\n\n    keymap {",
            StringComparison.Ordinal);
        Assert.Empty(Parse(source).Combos);

        var result = KeymapComboWriter.Apply(
            source, [new Add(NewCombo("combo_ESC", new[] { 0, 1 }, Keypress("ESC")))]);

        int combos = result.IndexOf("    combos {", StringComparison.Ordinal);
        int keymap = result.IndexOf("\n    keymap {", StringComparison.Ordinal);
        Assert.True(combos >= 0 && keymap > 0, "the anchors are gone");

        // the brace that shared the header line gets its own indented line back
        Assert.Equal(new[]
        {
            "    combos { compatible = \"zmk,combos\";",
            "        combo_ESC {",
            "            key-positions = <0 1>;",
            "            bindings = <&kp ESC>;",
            "        };",
            "    };",
            "",
        }, result[combos..keymap].Split('\n'));

        var after = Parse(result);
        Assert.Single(after.Combos);
        Assert.Equal("combo_ESC", after.Combos[0].NodeName);
    }

    // ---- a deleted node loses its own lines, nothing else -------------------------

    [Fact]
    public void Slabwing_Delete_Middle_Combo_Keeps_The_Comment_And_One_Blank_Line()
    {
        var result = KeymapComboWriter.Apply(Slabwing, [new Delete("mouse-mid")]);

        Assert.DoesNotContain("mouse-mid", result, StringComparison.Ordinal);

        var lines = CombosLines(result);
        AssertNoDoubleBlankLine(lines);

        int comment = lines.IndexOf(SafetyNet);
        Assert.True(comment >= 0, "the comment above the node is gone");
        Assert.Equal(new[] { "", "        combo_bootloader {" }, lines.Skip(comment + 1).Take(2));

        Assert.Equal(
            new[] { "combo_bootloader", "f12", "syslayer" },
            Parse(result).Combos.Select(c => c.NodeName));
    }

    [Fact]
    public void Slabwing_Delete_Last_Combo_Leaves_No_Blank_Above_The_Closer()
    {
        var result = KeymapComboWriter.Apply(Slabwing, [new Delete("syslayer")]);

        var lines = CombosLines(result);
        Assert.Equal("        };", lines[^2]);
        Assert.Equal("    };", lines[^1]);
        Assert.DoesNotContain("syslayer", result, StringComparison.Ordinal);

        Assert.Equal(
            new[] { "combo_bootloader", "mouse-mid", "f12" },
            Parse(result).Combos.Select(c => c.NodeName));
    }

    [Fact]
    public void Slabwing_Delete_First_Combo_Keeps_The_Comment_Above_It()
    {
        var result = KeymapComboWriter.Apply(Slabwing, [new Delete("combo_bootloader")]);

        var lines = CombosLines(result);
        AssertNoDoubleBlankLine(lines);

        int comment = lines.IndexOf(SafetyNet);
        Assert.True(comment >= 0, "the comment above the node is gone");
        Assert.Equal(new[] { "", "        mouse-mid {" }, lines.Skip(comment + 1).Take(2));

        Assert.Equal(
            new[] { "mouse-mid", "f12", "syslayer" },
            Parse(result).Combos.Select(c => c.NodeName));
    }

    [Fact]
    public void Slabwing_Delete_Every_Combo_Keeps_The_Combos_Node_Itself()
    {
        var result = KeymapComboWriter.Apply(Slabwing,
        [
            new Delete("combo_bootloader"),
            new Delete("mouse-mid"),
            new Delete("f12"),
            new Delete("syslayer"),
        ]);

        Assert.Equal(
            new[]
            {
                "    combos {",
                "        compatible = \"zmk,combos\";",
                "",
                SafetyNet,
                "    };",
            },
            CombosLines(result));

        Assert.Empty(Parse(result).Combos);
    }

    [Fact]
    public void Slabwing_Delete_Then_Add_The_Same_Name_Appends_It_Last()
    {
        var source = Slabwing;
        var f12 = Combo(source, "f12");

        var result = KeymapComboWriter.Apply(source, [new Delete("f12"), new Add(f12)]);

        Assert.Equal(
            new[] { "combo_bootloader", "mouse-mid", "syslayer", "f12" },
            Parse(result).Combos.Select(c => c.NodeName));
        Assert.Equal(new[] { "", "        f12 {" }, LinesAfterNode(result, "syslayer", 2));

        // the rebuilt node lists its properties the way the docs do
        Assert.Equal(
            new[]
            {
                "        f12 {",
                "            key-positions = <0 1>;",
                "            bindings = <&kp F12>;",
                Closing,
            },
            NodeLines(result, "f12"));
        Assert.True(f12.IsSameAs(Combo(result, "f12")));
    }

    [Fact]
    public void Slabwing_Delete_Keeps_Every_Newline_A_CRLF_Pair()
    {
        var source = Slabwing.Replace("\n", "\r\n", StringComparison.Ordinal);

        var result = KeymapComboWriter.Apply(source, [new Delete("mouse-mid")]);

        Assert.Equal(result.Count(c => c == '\n'), result.Count(c => c == '\r'));
        Assert.DoesNotContain("mouse-mid", result, StringComparison.Ordinal);
        Assert.Equal(
            new[] { "combo_bootloader", "f12", "syslayer" },
            Parse(result).Combos.Select(c => c.NodeName));
    }

    [Fact]
    public void Slabwing_Add_To_A_Node_Just_Deleted_Empty_Goes_Below_Its_Comment()
    {
        // the combos node left behind has no children and one comment line
        var emptied = KeymapComboWriter.Apply(Slabwing,
        [
            new Delete("combo_bootloader"),
            new Delete("mouse-mid"),
            new Delete("f12"),
            new Delete("syslayer"),
        ]);

        var result = KeymapComboWriter.Apply(
            emptied, [new Add(NewCombo("combo_esc", new[] { 10, 11 }, Keypress("ESC")))]);

        Assert.Equal(
            new[]
            {
                "    combos {",
                "        compatible = \"zmk,combos\";",
                "",
                SafetyNet,
                "",
                "        combo_esc {",
                "            key-positions = <10 11>;",
                "            bindings = <&kp ESC>;",
                "        };",
                "    };",
            },
            CombosLines(result));

        Assert.Equal(new[] { 10, 11 }, Combo(result, "combo_esc").KeyPositions);
    }

    // ---- bad input -------------------------------------------------------------

    [Fact]
    public void Change_Naming_No_Combo_Is_Rejected()
    {
        var source = Slabwing;
        var f12 = Combo(source, "f12");

        Assert.Throws<ArgumentException>(() => KeymapComboWriter.Apply(source, [new Change("nope", f12)]));
    }

    [Fact]
    public void Change_In_A_Keymap_Without_Combos_Is_Rejected()
    {
        var source = KeymapFixtures.Read("cosmos82.keymap");
        Assert.Empty(Parse(source).Combos);

        var combo = new ZmkCombo("F12", "", new[] { 0, 1 }, new[] { -1 }, Keypress("F12"))
        {
            NodeName = "combo_f12",
        };

        Assert.Throws<ArgumentException>(() => KeymapComboWriter.Apply(source, [new Change("combo_f12", combo)]));
    }

    [Fact]
    public void Rename_To_Another_Combo_s_Name_Is_Rejected()
    {
        var source = Slabwing;
        var f12 = Combo(source, "f12");

        Assert.Throws<ArgumentException>(
            () => KeymapComboWriter.Apply(source, [new Change("f12", f12 with { NodeName = "syslayer" })]));
    }

    [Fact]
    public void Rename_To_An_Illegal_Node_Name_Is_Rejected()
    {
        var source = Slabwing;
        var f12 = Combo(source, "f12");

        Assert.Throws<ArgumentException>(
            () => KeymapComboWriter.Apply(source, [new Change("f12", f12 with { NodeName = "1x" })]));
    }

    [Fact]
    public void Combo_With_One_Key_Is_Rejected()
    {
        var source = Slabwing;
        var f12 = Combo(source, "f12");

        Assert.Throws<ArgumentException>(
            () => KeymapComboWriter.Apply(source, [new Change("f12", f12 with { KeyPositions = new[] { 4 } })]));
    }

    [Fact]
    public void Add_An_Existing_Node_Name_Is_Rejected()
    {
        var source = Slabwing;

        Assert.Throws<ArgumentException>(() => KeymapComboWriter.Apply(source, [new Add(Combo(source, "f12"))]));
    }

    [Fact]
    public void Add_An_Illegal_Node_Name_Is_Rejected()
    {
        Assert.Throws<ArgumentException>(() => KeymapComboWriter.Apply(Slabwing,
            [new Add(NewCombo("1x", new[] { 10, 11 }, Keypress("ESC")))]));
    }

    [Fact]
    public void Add_Without_A_Node_Name_Is_Rejected()
    {
        var nameless = NewCombo("combo_esc", new[] { 10, 11 }, Keypress("ESC")) with { NodeName = null };

        Assert.Throws<ArgumentException>(() => KeymapComboWriter.Apply(Slabwing, [new Add(nameless)]));
    }

    [Fact]
    public void Add_With_One_Key_Is_Rejected()
    {
        Assert.Throws<ArgumentException>(() => KeymapComboWriter.Apply(Slabwing,
            [new Add(NewCombo("combo_esc", new[] { 10 }, Keypress("ESC")))]));
    }

    [Fact]
    public void Delete_Naming_No_Combo_Is_Rejected()
    {
        Assert.Throws<ArgumentException>(() => KeymapComboWriter.Apply(Slabwing, [new Delete("nope")]));
    }

    [Fact]
    public void Delete_In_A_Keymap_Without_Combos_Is_Rejected()
    {
        var source = Cosmos;
        Assert.Empty(Parse(source).Combos);

        Assert.Throws<ArgumentException>(() => KeymapComboWriter.Apply(source, [new Delete("combo_ESC")]));
    }

    [Fact]
    public void Two_Combos_Nodes_Are_Refused()
    {
        var source = Slabwing + """

            / {
                combos {
                    compatible = "zmk,combos";
                };
            };
            """;

        // it still parses (the parser reads the first combos node), but the writer
        // cannot know which node a name refers to
        Assert.NotEmpty(Parse(source).Combos);

        var f12 = Combo(source, "f12");
        var error = Assert.Throws<ZmkKeymapParseException>(
            () => KeymapComboWriter.Apply(source, [new Change("f12", f12 with { TimeoutMs = 30 })]));

        Assert.Contains("2 'zmk,combos' nodes", error.Message, StringComparison.Ordinal);
    }

    // ---- the shape of the API --------------------------------------------------

    [Fact]
    public void No_Edits_Returns_The_Source()
    {
        var source = Slabwing;

        Assert.Equal(source, KeymapComboWriter.Apply(source, []));
    }

    [Fact]
    public void Edits_Apply_In_Order_Each_Against_The_Previous_Result()
    {
        var source = Slabwing;
        var f12 = Combo(source, "f12");
        var bootloader = Combo(source, "combo_bootloader");

        var result = KeymapComboWriter.Apply(source,
        [
            new Change("f12", f12 with { Binding = Keypress("F11") }),
            new Change("f12", f12 with { Binding = Keypress("F13") }),
            new Change("combo_bootloader", bootloader with { TimeoutMs = null }),
        ]);

        Assert.Equal("F13", Combo(result, "f12").Binding.Params[0]);
        Assert.Null(Combo(result, "combo_bootloader").TimeoutMs);
        Assert.Equal("&bootloader", Combo(result, "combo_bootloader").Binding.Behavior);
    }

    [Fact]
    public void Every_Other_Combo_And_Every_Layer_Survives_The_Write()
    {
        var source = Slabwing;
        var before = Parse(source);

        var result = KeymapComboWriter.Apply(
            source, [new Change("f12", Combo(source, "f12") with { Binding = Keypress("F11"), Layers = new[] { 0, 2 } })]);

        var after = Parse(result);

        Assert.Equal(before.Combos.Select(c => c.NodeName), after.Combos.Select(c => c.NodeName));
        Assert.Equal(before.Layers.Select(l => l.Name), after.Layers.Select(l => l.Name));
        Assert.Equal(
            before.Layers.Select(l => string.Join('|', l.Bindings.Select(b => b.Display))).ToList(),
            after.Layers.Select(l => string.Join('|', l.Bindings.Select(b => b.Display))).ToList());

        foreach (var name in new[] { "combo_bootloader", "mouse-mid", "syslayer" })
            Assert.True(before.Combos.Single(c => c.NodeName == name).IsSameAs(after.Combos.Single(c => c.NodeName == name)));
    }
}
