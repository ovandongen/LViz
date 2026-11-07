using System.Text.RegularExpressions;
using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Layout.Dtsi;
using LViz.Core.Models;

namespace LViz.Core.Keymap.Writer;

/// <summary>
/// Applies a <see cref="ComboEdit"/> to the <b>original</b> <c>.keymap</c> text
/// in place, the way <see cref="KeymapWriter"/> does for bindings and
/// <see cref="KeymapLayerWriter"/> for layer blocks: only the properties the
/// edit actually changes are touched, and every other character — comments,
/// blank lines, indentation, the other combos, the whole keymap — survives
/// byte-for-byte. A property set back to its default (<c>layers = &lt;-1&gt;</c>,
/// no <c>timeout-ms</c>, no <c>require-prior-idle-ms</c>, no
/// <c>slow-release</c>) is taken out of the node instead of being written; a
/// property the node does not have yet is added as its own line at the end of
/// the node's property list. Key positions come back as the file's own
/// <c>POS_*</c>-style defines when its combos already use defines, and layer
/// numbers as the defines the file already uses as layer references — numbers
/// otherwise. The result is re-parsed and compared with the combo list the edit
/// should have produced; any difference throws and returns nothing.
/// A new combo is appended after the last combo of the <c>combos</c> node, one
/// blank line before it, with its properties in the order the ZMK docs list
/// them and in the indentation the file already uses; a keymap without a
/// <c>combos</c> node gets one just before its <c>zmk,keymap</c> node. A deleted
/// combo loses its own node lines and nothing else — the comments around it and
/// the <c>combos</c> node itself stay, even when it ends up empty.
/// Pure function: no I/O, the source text is never modified.
/// </summary>
public static class KeymapComboWriter
{
    /// <summary>
    /// Apply <paramref name="edits"/> one after another, each against the
    /// result of the previous one: parse, write, verify. An empty list returns
    /// <paramref name="source"/> itself.
    /// </summary>
    /// <exception cref="ZmkKeymapParseException">
    /// <paramref name="source"/> does not parse, has more than one
    /// <c>zmk,combos</c> node, holds a different number of combo nodes than the
    /// parsed combo list, has no <c>zmk,keymap</c> node for a new combos node to
    /// sit next to, or the written text does not re-parse to the combo list and
    /// the layers it should have produced.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An edit addresses no combo of the keymap, an <see cref="ComboEdit.Add"/>
    /// or a renamed <see cref="ComboEdit.Change"/> carries a node name that is
    /// not a legal devicetree name or already taken, or a combo is left with
    /// fewer than two distinct key positions.
    /// </exception>
    public static string Apply(string source, IReadOnlyList<ComboEdit> edits)
    {
        foreach (var edit in edits) source = ApplyOne(source, edit);
        return source;
    }

    private static string ApplyOne(string source, ComboEdit edit) => edit switch
    {
        ComboEdit.Add add => ApplyAdd(source, add),
        ComboEdit.Change change => ApplyChange(source, change),
        ComboEdit.Delete delete => ApplyDelete(source, delete),
        _ => throw new ArgumentException($"unknown combo edit '{edit.GetType().Name}'.", nameof(edit)),
    };

    // ---- the add -------------------------------------------------------------------

    private static string ApplyAdd(string source, ComboEdit.Add add)
    {
        var config = ZmkKeymapLoader.LoadFromText(source, "writer");
        var next = add.Combo;

        if (new HashSet<int>(next.KeyPositions).Count < 2)
            throw new ArgumentException(
                $"combo '{next.NodeName}' needs two or more distinct key positions.",
                nameof(add));

        // A new node writes its name into its header, so it is not optional here.
        var newNodeName = next.NodeName ?? string.Empty;
        if (!ComboRules.IsValidNodeName(newNodeName))
            throw new ArgumentException(
                $"'{newNodeName}' is not a legal combo node name: 1-"
                + ComboRules.MaxNodeNameLength
                + " characters from A-Z a-z 0-9 , . _ + -, starting with a letter.",
                nameof(add));

        if (IndexOfNode(config.Combos, newNodeName) >= 0)
            throw new ArgumentException(
                $"the keymap already has a combo node named '{newNodeName}'.",
                nameof(add));

        var text = ParseText(source, config);
        var statements = text.Statements(next);

        var (offset, replaced, insertion) = text.Combos is { } combos
            ? text.NewChild(combos, newNodeName, statements)
            : CreateCombosNode(text, newNodeName, statements);

        // No label is written, so the file's name for the new combo is its node name.
        var expected = next with { Name = newNodeName, NodeName = newNodeName };

        var result = KeymapWriter.ApplyChanges(source, [(offset, insertion, replaced)]);
        Validate(result, config, WithAppended(config.Combos, expected));
        return result;
    }

    /// <summary>Where a keymap that has no <c>combos</c> node yet gets one: the
    /// start of the <c>zmk,keymap</c> node's header line, so the new node lands
    /// just before the keymap with a blank line between the two.</summary>
    private static (int Offset, int ReplacedLength, string Text) CreateCombosNode(
        KeymapComboText text, string nodeName, IReadOnlyList<string> statements)
    {
        if (text.KeymapHeaderStart < 0)
            throw new ZmkKeymapParseException(
                $"the keymap holds no '{KeymapComboText.KeymapCompatible}' node to place a new combos node next to.");

        var ind = DtsiText.LeadingWhitespace(text.Masked, text.KeymapHeaderStart);
        var child = ind + "    ";

        var insertion =
            ind + "combos {" + text.NewLine
            + child + "compatible = \"" + KeymapComboText.CombosCompatible + "\";" + text.NewLine
            + text.NewLine
            + KeymapComboText.NewNode(child, child + "    ", nodeName, statements, text.NewLine)
            + ind + "};" + text.NewLine
            + text.NewLine;

        return (text.KeymapHeaderStart, 0, insertion);
    }

    // ---- the delete ----------------------------------------------------------------

    private static string ApplyDelete(string source, ComboEdit.Delete delete)
    {
        var config = ZmkKeymapLoader.LoadFromText(source, "writer");

        var index = IndexOfNode(config.Combos, delete.NodeName);
        if (index < 0)
            throw new ArgumentException(
                config.Combos.Count == 0
                    ? $"'{delete.NodeName}': the keymap has no combos to delete."
                    : $"'{delete.NodeName}' is not the node name of a combo in the keymap.",
                nameof(delete));

        var text = ParseText(source, config);
        var result = KeymapWriter.ApplyChanges(source, [text.NodeRemoval(NodeOf(text, delete.NodeName))]);

        Validate(result, config, WithoutAt(config.Combos, index));
        return result;
    }

    // ---- the change ----------------------------------------------------------------

    private static string ApplyChange(string source, ComboEdit.Change change)
    {
        var config = ZmkKeymapLoader.LoadFromText(source, "writer");

        var index = IndexOfNode(config.Combos, change.NodeName);
        if (index < 0)
            throw new ArgumentException(
                config.Combos.Count == 0
                    ? $"'{change.NodeName}': the keymap has no combos to change."
                    : $"'{change.NodeName}' is not the node name of a combo in the keymap.",
                nameof(change));

        var current = config.Combos[index];
        var next = change.Combo;

        if (new HashSet<int>(next.KeyPositions).Count < 2)
            throw new ArgumentException(
                $"combo '{change.NodeName}' needs two or more distinct key positions.",
                nameof(change));

        // A null NodeName means "the same node" — only a spelled-out other name renames.
        var newNodeName = next.NodeName ?? change.NodeName;
        var renamed = !string.Equals(newNodeName, current.NodeName, StringComparison.Ordinal);
        if (renamed)
        {
            if (!ComboRules.IsValidNodeName(newNodeName))
                throw new ArgumentException(
                    $"'{newNodeName}' is not a legal combo node name: 1-"
                    + ComboRules.MaxNodeNameLength
                    + " characters from A-Z a-z 0-9 , . _ + -, starting with a letter.",
                    nameof(change));

            var twin = config.Combos.FirstOrDefault(c =>
                !string.Equals(c.NodeName, change.NodeName, StringComparison.Ordinal)
                && string.Equals(c.NodeName, newNodeName, StringComparison.Ordinal));
            if (twin is not null)
                throw new ArgumentException(
                    $"the keymap already has a combo node named '{newNodeName}'.",
                    nameof(change));
        }

        var text = ParseText(source, config);
        var node = NodeOf(text, change.NodeName);

        var expected = current with
        {
            // The parsed Name is the node's label when it has one, and its node
            // name otherwise — so a rename only moves it when there is no label.
            Name = renamed && !node.HasLabel ? newNodeName : current.Name,
            NodeName = newNodeName,
            KeyPositions = next.KeyPositions,
            Layers = next.Layers,
            Binding = next.Binding,
            TimeoutMs = next.TimeoutMs,
            RequirePriorIdleMs = next.RequirePriorIdleMs,
            SlowRelease = next.SlowRelease,
        };

        var changes = new List<(int Offset, string Replacement, int ReplacedLength)>();
        if (renamed)
            changes.Add((node.NameStart, newNodeName, node.NameEnd - node.NameStart));

        foreach (var property in KeymapComboText.ComboProperties)
        {
            if (!Differs(property, current, next)) continue;

            var statement = text.FindStatement(node, property);
            var formatted = text.FormatStatement(property, next);

            if (statement is not null)
            {
                // Set back to its default: the statement leaves the file. Otherwise
                // it is rewritten where it stands, indent and the ';' included.
                changes.Add(formatted is null
                    ? text.Removal(statement.Value)
                    : (statement.Value.NameStart, formatted + ";", statement.Value.SpanEnd - statement.Value.NameStart));
            }
            else if (formatted is not null)
            {
                changes.Add(text.Insertion(node, formatted));
            }
        }

        if (changes.Count == 0) return source;

        var result = KeymapWriter.ApplyChanges(source, changes);
        Validate(result, config, WithReplaced(config.Combos, index, expected));
        return result;
    }

    /// <summary>The combos text of a parsed keymap, refusing a text whose combo
    /// nodes and parsed combos are not the same list.</summary>
    private static KeymapComboText ParseText(string source, KeyboardConfig config)
    {
        var text = KeymapComboText.Parse(source, config);
        if (text.Nodes.Count != config.Combos.Count)
            throw new ZmkKeymapParseException(
                $"combos (line {text.CombosLine}): the source holds {text.Nodes.Count} combo nodes, "
                + $"the parser found {config.Combos.Count} combos.");
        return text;
    }

    private static ComboNode NodeOf(KeymapComboText text, string nodeName)
    {
        int index = text.Nodes.FindIndex(n => string.Equals(n.Name, nodeName, StringComparison.Ordinal));
        if (index < 0)
            throw new ZmkKeymapParseException(
                $"the source holds no combo node named '{nodeName}' (combos are on line {text.CombosLine}).");
        return text.Nodes[index];
    }

    private static int IndexOfNode(IReadOnlyList<ZmkCombo> combos, string nodeName)
    {
        for (int i = 0; i < combos.Count; i++)
            if (string.Equals(combos[i].NodeName, nodeName, StringComparison.Ordinal)) return i;
        return -1;
    }

    private static IReadOnlyList<ZmkCombo> WithReplaced(
        IReadOnlyList<ZmkCombo> combos, int index, ZmkCombo replacement)
    {
        var list = new List<ZmkCombo>(combos);
        list[index] = replacement;
        return list;
    }

    private static IReadOnlyList<ZmkCombo> WithoutAt(IReadOnlyList<ZmkCombo> combos, int index)
    {
        var list = new List<ZmkCombo>(combos);
        list.RemoveAt(index);
        return list;
    }

    private static IReadOnlyList<ZmkCombo> WithAppended(IReadOnlyList<ZmkCombo> combos, ZmkCombo appended)
    {
        var list = new List<ZmkCombo>(combos);
        list.Add(appended);
        return list;
    }

    /// <summary>Does this property hold something else than the file says it does?
    /// Positions compare as sets (a reordering is not a change), layers with
    /// <see cref="ZmkCombo.IsSameAs"/>'s scope rule, bindings by behavior + params.</summary>
    private static bool Differs(string property, ZmkCombo current, ZmkCombo next) => property switch
    {
        "key-positions" => !new HashSet<int>(current.KeyPositions).SetEquals(next.KeyPositions),
        "layers" => !SameLayerScope(current.Layers, next.Layers),
        "bindings" => !current.Binding.SameAs(next.Binding),
        "timeout-ms" => current.TimeoutMs != next.TimeoutMs,
        "require-prior-idle-ms" => current.RequirePriorIdleMs != next.RequirePriorIdleMs,
        "slow-release" => current.SlowRelease != next.SlowRelease,
        _ => throw new ArgumentException($"'{property}' is not a combo property.", nameof(property)),
    };

    /// <summary>Layer scope as the file means it: an empty list or a list holding
    /// -1 is "all layers", which no <c>layers</c> property spells.</summary>
    private static bool SameLayerScope(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        bool allA = ZmkCombo.IsAllLayers(a);
        bool allB = ZmkCombo.IsAllLayers(b);
        return allA || allB ? allA == allB : new HashSet<int>(a).SetEquals(b);
    }

    // ---- bug guard -------------------------------------------------------------------

    /// <summary>
    /// Re-parse what was written: the combos must be the previous list with the
    /// edited one replaced in place (same count, and per position the same node
    /// name and <see cref="ZmkCombo.IsSameAs"/>), and every layer must still
    /// hold its bindings.
    /// </summary>
    private static void Validate(string result, KeyboardConfig before, IReadOnlyList<ZmkCombo> expectedCombos)
    {
        KeyboardConfig config;
        try
        {
            config = ZmkKeymapLoader.LoadFromText(result, "writer");
        }
        catch (ZmkKeymapParseException ex)
        {
            throw new ZmkKeymapParseException(
                $"after writing combos, the result does not parse any more: {ex.Message}", inner: ex);
        }

        if (config.Combos.Count != expectedCombos.Count)
            throw new ZmkKeymapParseException(
                $"after writing combos, the keymap re-parses to {config.Combos.Count} combos, "
                + $"expected {expectedCombos.Count}.");

        for (int i = 0; i < expectedCombos.Count; i++)
        {
            var actual = config.Combos[i];
            var want = expectedCombos[i];

            if (!string.Equals(actual.NodeName, want.NodeName, StringComparison.Ordinal))
                throw new ZmkKeymapParseException(
                    $"after writing combos, combo {i} is node '{actual.NodeName}', expected '{want.NodeName}'.");

            if (!actual.IsSameAs(want))
                throw new ZmkKeymapParseException(
                    $"after writing combos, combo {i} is {Describe(actual)}, expected {Describe(want)}.");
        }

        if (config.Layers.Count != before.Layers.Count)
            throw new ZmkKeymapParseException(
                $"after writing combos, the keymap re-parses to {config.Layers.Count} layers, "
                + $"expected {before.Layers.Count}.");

        for (int layer = 0; layer < before.Layers.Count; layer++)
        {
            var actual = config.Layers[layer];
            var want = before.Layers[layer];

            if (actual.Bindings.Count != want.Bindings.Count)
                throw new ZmkKeymapParseException(
                    $"after writing combos, layer {layer} ('{actual.Name}') has {actual.Bindings.Count} bindings, "
                    + $"expected {want.Bindings.Count}.");

            for (int key = 0; key < want.Bindings.Count; key++)
            {
                if (!actual.Bindings[key].SameAs(want.Bindings[key]))
                    throw new ZmkKeymapParseException(
                        $"after writing combos, layer {layer} ('{actual.Name}') key {key} is "
                        + $"'{actual.Bindings[key].Display}', expected '{want.Bindings[key].Display}'.");
            }
        }
    }

    private static string Describe(ZmkCombo combo) =>
        $"node '{combo.NodeName}' name '{combo.Name}' keys [{Join(combo.KeyPositions)}] layers [{Join(combo.Layers)}] "
        + $"firing '{combo.Binding.Display}' timeout-ms {WriterText.Num(combo.TimeoutMs)} "
        + $"require-prior-idle-ms {WriterText.Num(combo.RequirePriorIdleMs)} slow-release {combo.SlowRelease}";

    private static string Join(IReadOnlyList<int> values) => string.Join(' ', values);
}
