using System.Globalization;
using System.Text;
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
    private const string CombosCompatible = "zmk,combos";

    /// <summary>The node a newly created <c>combos</c> node is placed before.</summary>
    private const string KeymapCompatible = "zmk,keymap";

    /// <summary>Every property a combo node may hold, in the order a new node
    /// writes them (the order the ZMK docs list them in). <c>key-positions</c>
    /// and <c>bindings</c> always carry a value; the other four disappear when
    /// set back to their default.</summary>
    private static readonly IReadOnlyList<string> ComboProperties =
        ["timeout-ms", "key-positions", "layers", "bindings", "require-prior-idle-ms", "slow-release"];

    /// <summary>One combo property's <c>&lt;…&gt;</c> array, for the define-prefix scan.</summary>
    private static readonly Regex KeyPositionsArrayRegex = new(
        @"(?<![-A-Za-z0-9_])key-positions[ \t\r\n]*=[ \t\r\n]*<([^>]*)>",
        RegexOptions.CultureInvariant);

    /// <summary>A define-shaped cell of a <c>&lt;…&gt;</c> array.</summary>
    private static readonly Regex IdentifierTokenRegex = new(
        @"[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, Regex> PropertyRegexes =
        ComboProperties.ToDictionary(
            p => p,
            p => new Regex(
                @"(?<![-A-Za-z0-9_])" + Regex.Escape(p) + @"(?![A-Za-z0-9_-])",
                RegexOptions.Compiled | RegexOptions.CultureInvariant),
            StringComparer.Ordinal);

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

    /// <summary>The text of a new combo node — header line, one line per
    /// non-default property, closing <c>};</c> — every line ending in
    /// <paramref name="newLine"/>, so the returned text ends with one too.</summary>
    private static string NewNode(
        string childIndent, string propIndent, string nodeName, IReadOnlyList<string> statements, string newLine)
    {
        var sb = new StringBuilder();
        sb.Append(childIndent).Append(nodeName).Append(" {").Append(newLine);
        foreach (var statement in statements)
            sb.Append(propIndent).Append(statement).Append(';').Append(newLine);
        sb.Append(childIndent).Append("};").Append(newLine);
        return sb.ToString();
    }

    /// <summary>Where a keymap that has no <c>combos</c> node yet gets one: the
    /// start of the <c>zmk,keymap</c> node's header line, so the new node lands
    /// just before the keymap with a blank line between the two.</summary>
    private static (int Offset, int ReplacedLength, string Text) CreateCombosNode(
        ComboText text, string nodeName, IReadOnlyList<string> statements)
    {
        if (text.KeymapHeaderStart < 0)
            throw new ZmkKeymapParseException(
                $"the keymap holds no '{KeymapCompatible}' node to place a new combos node next to.");

        var ind = text.LeadingWhitespace(text.KeymapHeaderStart);
        var child = ind + "    ";

        var insertion =
            ind + "combos {" + text.NewLine
            + child + "compatible = \"" + CombosCompatible + "\";" + text.NewLine
            + text.NewLine
            + NewNode(child, child + "    ", nodeName, statements, text.NewLine)
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

        foreach (var property in ComboProperties)
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
    private static ComboText ParseText(string source, KeyboardConfig config)
    {
        var text = ComboText.Parse(source, config);
        if (text.Nodes.Count != config.Combos.Count)
            throw new ZmkKeymapParseException(
                $"combos (line {text.CombosLine}): the source holds {text.Nodes.Count} combo nodes, "
                + $"the parser found {config.Combos.Count} combos.");
        return text;
    }

    private static ComboNode NodeOf(ComboText text, string nodeName)
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
        "bindings" => !KeymapWriter.IsSameBinding(current.Binding, next.Binding),
        "timeout-ms" => current.TimeoutMs != next.TimeoutMs,
        "require-prior-idle-ms" => current.RequirePriorIdleMs != next.RequirePriorIdleMs,
        "slow-release" => current.SlowRelease != next.SlowRelease,
        _ => throw new ArgumentException($"'{property}' is not a combo property.", nameof(property)),
    };

    /// <summary>Layer scope as the file means it: an empty list or a list holding
    /// -1 is "all layers", which no <c>layers</c> property spells.</summary>
    private static bool SameLayerScope(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        bool allA = IsAllLayers(a);
        bool allB = IsAllLayers(b);
        return allA || allB ? allA == allB : new HashSet<int>(a).SetEquals(b);
    }

    private static bool IsAllLayers(IReadOnlyList<int> layers) =>
        layers.Count == 0 || layers.Contains(-1);

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
                if (!KeymapWriter.IsSameBinding(actual.Bindings[key], want.Bindings[key]))
                    throw new ZmkKeymapParseException(
                        $"after writing combos, layer {layer} ('{actual.Name}') key {key} is "
                        + $"'{actual.Bindings[key].Display}', expected '{want.Bindings[key].Display}'.");
            }
        }
    }

    private static string Describe(ZmkCombo combo) =>
        $"node '{combo.NodeName}' name '{combo.Name}' keys [{Join(combo.KeyPositions)}] layers [{Join(combo.Layers)}] "
        + $"firing '{combo.Binding.Display}' timeout-ms {Num(combo.TimeoutMs)} "
        + $"require-prior-idle-ms {Num(combo.RequirePriorIdleMs)} slow-release {combo.SlowRelease}";

    private static string Join(IReadOnlyList<int> values) => string.Join(' ', values);

    private static string Num(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    // ---- where things sit in the text ----------------------------------------------

    /// <summary>One combo node: header line, the span of its name (a
    /// <c>label:</c> prefix sits outside it), its body and its closing brace.</summary>
    private readonly record struct ComboNode(
        string Name,
        int NameStart,
        int NameEnd,
        int BodyStart,
        int CloseBrace,
        bool HasLabel,
        string Indent);

    /// <summary>One property statement of a combo node: the offset of its name
    /// and of the <c>;</c> that ends it. <see cref="OwnLines"/> is true when
    /// nothing else shares its line(s), which decides a whole-line removal.</summary>
    private readonly record struct Statement(int NameStart, int SpanEnd, bool OwnLines);

    /// <summary>The combos node, its combo children and the define tables their
    /// values are spelled with, read once per edit.</summary>
    private sealed class ComboText
    {
        private readonly KeyboardConfig _config;
        private readonly HashSet<string> _positionPrefixes;

        private ComboText(
            string source,
            string masked,
            string newLine,
            DtsiText.DtsiNode? combos,
            List<ComboNode> nodes,
            int keymapHeaderStart,
            KeyboardConfig config)
        {
            Masked = masked;
            Source = source;
            NewLine = newLine;
            Combos = combos;
            Nodes = nodes;
            KeymapHeaderStart = keymapHeaderStart;
            _config = config;
            _positionPrefixes = CollectPositionPrefixes(masked, combos);
            Defines = Preprocessor.CollectDefines(source);
            LayerDefines = KeymapWriter.LayerReferenceDefines(source, config);
        }

        /// <summary>The source text with comment characters replaced by spaces
        /// (length, offsets and line numbers preserved), so every offset here
        /// indexes both texts.</summary>
        public string Masked { get; }

        /// <summary>The file as it stands. The add and delete rules ask whether
        /// a line is <em>blank</em>, and a comment line is not blank, so those
        /// two read this text while everything structural reads <see cref="Masked"/>.</summary>
        public string Source { get; }

        /// <summary><c>"\r\n"</c> when the source contains CRLF line endings, else <c>"\n"</c>.</summary>
        public string NewLine { get; }

        public DtsiText.DtsiNode? Combos { get; }

        public List<ComboNode> Nodes { get; }

        /// <summary>Every object-like define in file order with its trimmed value.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Defines { get; }

        /// <summary>The defines the file already uses where a layer number may stand.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> LayerDefines { get; }

        /// <summary>1-based line of the combos node, 0 when the file has none.</summary>
        public int CombosLine =>
            Combos is { } node ? DtsiText.LineOf(Masked, node.BodyStart - 1) : 0;

        /// <summary>Start of the line holding the <c>zmk,keymap</c> node's
        /// header, or -1 when the file has no such node — where a new combos
        /// node goes.</summary>
        public int KeymapHeaderStart { get; }

        /// <summary>The indent of the combos node's header line, empty when the
        /// file has no combos node.</summary>
        public string CombosIndent =>
            Combos is { } node ? LeadingWhitespace(LineStartOf(Masked, node.BodyStart - 1)) : string.Empty;

        /// <summary>The indent a new child node gets: the first existing child's,
        /// or the combos node's own indent plus four spaces.</summary>
        public string NewChildIndent => Nodes.Count > 0 ? Nodes[0].Indent : CombosIndent + "    ";

        /// <summary>The indent a property line of a new child gets: the first
        /// existing child's first property indent, or <see cref="NewChildIndent"/>
        /// plus four spaces.</summary>
        public string NewChildPropertyIndent =>
            Nodes.Count > 0 ? PropertyIndent(Nodes[0]) : NewChildIndent + "    ";

        public static ComboText Parse(string source, KeyboardConfig config)
        {
            var masked = DtsiText.MaskComments(source);
            var found = DtsiText.FindNodesByCompatible(masked, CombosCompatible);

            if (found.Count > 1)
                throw new ZmkKeymapParseException(
                    $"the keymap has {found.Count} '{CombosCompatible}' nodes (lines "
                    + string.Join(", ", found.Select(n => DtsiText.LineOf(masked, n.BodyStart - 1)))
                    + "); the combo writer edits one combos node.");

            var combos = found.Count > 0 ? found[0] : (DtsiText.DtsiNode?)null;
            var nodes = combos is { } node
                ? FindChildNodes(masked, node.BodyStart, node.BodyEnd)
                : new List<ComboNode>();
            var keymap = DtsiText.FindNodesByCompatible(masked, KeymapCompatible);

            return new ComboText(
                source,
                masked,
                source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
                combos,
                nodes,
                keymap.Count > 0 ? LineStartOf(masked, keymap[0].BodyStart - 1) : -1,
                config);
        }

        // ---- statements ---------------------------------------------------------

        /// <summary>The node's statement of <paramref name="property"/>, or null
        /// when the node does not hold one. Only a property sitting directly in
        /// the node counts: braces are counted across the node body.</summary>
        public Statement? FindStatement(ComboNode node, string property)
        {
            var body = Masked[node.BodyStart..node.CloseBrace];

            foreach (Match match in PropertyRegexes[property].Matches(body))
            {
                if (NetDepth(body, 0, match.Index) != 0) continue;

                int nameStart = node.BodyStart + match.Index;
                int semicolon = StatementEnd(nameStart + match.Length);
                if (semicolon < 0 || semicolon >= node.CloseBrace)
                    throw new ZmkKeymapParseException(
                        $"combo '{node.Name}' (line {DtsiText.LineOf(Masked, nameStart)}): "
                        + $"'{property}' is not closed by a ';'.");

                return new Statement(nameStart, semicolon + 1, OwnLines(nameStart, semicolon));
            }

            return null;
        }

        /// <summary>The removal of a statement no longer wanted: its whole
        /// line(s) with the newline, or — when something else shares the line —
        /// just the statement and the spaces behind it.</summary>
        public (int Offset, string Replacement, int ReplacedLength) Removal(Statement statement)
        {
            if (statement.OwnLines)
            {
                int start = LineStartOf(Masked, statement.NameStart);
                int end = EndOfLine(Masked, statement.SpanEnd - 1);
                return (start, string.Empty, end - start);
            }

            int from = statement.SpanEnd;
            while (from < Masked.Length && (Masked[from] == ' ' || Masked[from] == '\t')) from++;
            return (statement.NameStart, string.Empty, from - statement.NameStart);
        }

        /// <summary>The insertion of a property the node does not have yet: a new
        /// line just before the node's closing <c>};</c>, indented like its
        /// first property. An inline node (<c>f12 { bindings = &lt;&amp;kp F12&gt;; }</c>)
        /// gets the statement before its closing brace instead, so the one-liner
        /// stays one line.</summary>
        public (int Offset, string Replacement, int ReplacedLength) Insertion(ComboNode node, string statement)
        {
            int closingLineStart = LineStartOf(Masked, node.CloseBrace);
            if (AllWhitespace(closingLineStart, node.CloseBrace))
                return (closingLineStart, PropertyIndent(node) + statement + ";" + NewLine, 0);

            return (node.CloseBrace, statement + "; ", 0);
        }

        /// <summary>The indent of the node's first property line — the rest of
        /// the header line included, so an inline node reports its own indent.
        /// An empty node indents like itself plus four spaces.</summary>
        public string PropertyIndent(ComboNode node)
        {
            int i = node.BodyStart;
            while (i < node.CloseBrace)
            {
                int newline = Masked.IndexOf('\n', i);
                int lineEnd = newline < 0 ? Masked.Length : newline;
                if (Masked[i..lineEnd].Trim().Length > 0) return LeadingWhitespace(LineStartOf(Masked, i));
                i = newline < 0 ? Masked.Length : newline + 1;
            }

            return node.Indent + "    ";
        }

        // ---- values -------------------------------------------------------------

        /// <summary>Every non-default statement of <paramref name="combo"/>, in
        /// <see cref="ComboProperties"/> order — the body of a node being added.</summary>
        public IReadOnlyList<string> Statements(ZmkCombo combo)
        {
            var statements = new List<string>();
            foreach (var property in ComboProperties)
                if (FormatStatement(property, combo) is { } statement)
                    statements.Add(statement);
            return statements;
        }

        /// <summary>The statement text (no trailing <c>;</c>) for
        /// <paramref name="property"/>, or null when it holds its default.</summary>
        public string? FormatStatement(string property, ZmkCombo combo) => property switch
        {
            "key-positions" => "key-positions = <" + string.Join(' ', combo.KeyPositions.Select(FormatPosition)) + ">",
            "layers" => IsAllLayers(combo.Layers)
                ? null
                : "layers = <" + string.Join(' ', combo.Layers.Select(FormatLayer)) + ">",
            "bindings" => "bindings = <" + ZmkBindingFormatter.Format(combo.Binding, LayerDefines, _config.HoldTaps) + ">",
            "timeout-ms" => combo.TimeoutMs is int timeout
                ? "timeout-ms = <" + Num(timeout) + ">"
                : null,
            "require-prior-idle-ms" => combo.RequirePriorIdleMs is int idle
                ? "require-prior-idle-ms = <" + Num(idle) + ">"
                : null,
            "slow-release" => combo.SlowRelease ? "slow-release" : null,
            _ => throw new ArgumentException($"'{property}' is not a combo property.", nameof(property)),
        };

        /// <summary>A key position, spelled with a define when the file's combos
        /// already spell positions that way: the first define (file order) whose
        /// name carries one of the prefixes those positions use (POS_) and whose
        /// value is this number. Numbers otherwise.</summary>
        private string FormatPosition(int position)
        {
            if (_positionPrefixes.Count == 0) return Num(position);

            string value = Num(position);
            foreach (var define in Defines)
            {
                if (!string.Equals(define.Value, value, StringComparison.Ordinal)) continue;
                foreach (var prefix in _positionPrefixes)
                    if (define.Key.StartsWith(prefix, StringComparison.Ordinal)) return define.Key;
            }

            return value;
        }

        /// <summary>A layer number, spelled with the first define the file uses
        /// as a layer reference and whose value is this number.</summary>
        private string FormatLayer(int layer)
        {
            string value = Num(layer);
            foreach (var define in LayerDefines)
                if (string.Equals(define.Value, value, StringComparison.Ordinal)) return define.Key;
            return value;
        }

        /// <summary>The prefixes (up to and including the first <c>_</c>) of the
        /// defines the source already uses inside <c>key-positions</c> arrays,
        /// across every combo of the combos node.</summary>
        private static HashSet<string> CollectPositionPrefixes(string masked, DtsiText.DtsiNode? combos)
        {
            var prefixes = new HashSet<string>(StringComparer.Ordinal);
            if (combos is not { } node) return prefixes;

            var body = masked[node.BodyStart..node.BodyEnd];
            foreach (Match array in KeyPositionsArrayRegex.Matches(body))
            {
                foreach (Match token in IdentifierTokenRegex.Matches(array.Groups[1].Value))
                {
                    int cut = token.Value.IndexOf('_');
                    if (cut >= 0) prefixes.Add(token.Value[..(cut + 1)]);
                }
            }

            return prefixes;
        }

        // ---- combo nodes --------------------------------------------------------

        /// <summary>Where a new child node goes and what is written there. The
        /// child joins just past the last child's <c>};</c> line, or — when the
        /// node has no children yet — just past the last line of its body that
        /// holds text, comments included, so a note left in an empty node stays
        /// above what is added.</summary>
        /// <remarks>An inline node (<c>combos { compatible = …; };</c>) has no
        /// line to join: there the spaces in front of the closing brace are
        /// replaced, which gives the brace its own indented line back instead of
        /// leaving <c>};</c> dangling behind the new node.</remarks>
        public (int Offset, int ReplacedLength, string Text) NewChild(
            DtsiText.DtsiNode combos, string nodeName, IReadOnlyList<string> statements)
        {
            var node = NewNode(NewChildIndent, NewChildPropertyIndent, nodeName, statements, NewLine);

            if (!StartsItsLine(combos.BodyEnd))
            {
                int spaces = combos.BodyEnd;
                while (spaces > combos.BodyStart && IsSpaceOrTab(Masked[spaces - 1])) spaces--;
                return (spaces, combos.BodyEnd - spaces, NewLine + node + CombosIndent);
            }

            if (Nodes.Count > 0) return (EndOfLine(Masked, Nodes[^1].CloseBrace), 0, NewLine + node);

            int slot = combos.BodyStart;
            for (int i = combos.BodyStart; i < combos.BodyEnd;)
            {
                int lineEnd = EndOfLine(Source, i);
                if (!IsBlank(i, Math.Min(lineEnd, combos.BodyEnd)))
                    slot = Math.Min(lineEnd, combos.BodyEnd);
                i = lineEnd;
            }

            return (slot, 0, NewLine + node);
        }

        /// <summary>True when only whitespace sits between the start of
        /// <paramref name="offset"/>'s line and that offset.</summary>
        private bool StartsItsLine(int offset)
        {
            int start = LineStartOf(Masked, offset - 1);
            return LeadingWhitespace(Masked, start).Length == offset - start;
        }

        private static bool IsSpaceOrTab(char c) => c is ' ' or '\t';

        /// <summary>The removal of a whole combo node: its header line through
        /// its closing <c>};</c> line. The blank line after it leaves with it
        /// when the node sat between two blank lines; when it was the last child
        /// before the combos node's own <c>};</c>, the blank line before it
        /// leaves instead. Either way the file keeps one blank line between what
        /// remains, and comments around the node are untouched.</summary>
        public (int Offset, string Replacement, int ReplacedLength) NodeRemoval(ComboNode node)
        {
            int start = LineStartOf(Masked, node.NameStart);
            int end = EndOfLine(Masked, node.CloseBrace);
            int before = start > 0 ? LineStartOf(Masked, start - 1) : -1;
            bool blankBefore = before >= 0 && IsBlank(before, start);

            if (end >= Source.Length) return (start, string.Empty, end - start);

            int afterEnd = EndOfLine(Source, end);
            if (blankBefore && IsBlank(end, afterEnd))
                return (start, string.Empty, afterEnd - start);

            if (blankBefore && Combos is { } combos && end <= combos.BodyEnd && combos.BodyEnd < afterEnd)
                return (before, string.Empty, end - before);

            return (start, string.Empty, end - start);
        }

        /// <summary>The direct child nodes of a body, in order: every
        /// <c>[label:] name {</c> at depth 0, brace-matched to its <c>}</c>. An
        /// unclosed child is dropped, which makes the caller's combo-count check
        /// fail loudly.</summary>
        private static List<ComboNode> FindChildNodes(string masked, int bodyStart, int bodyEnd)
        {
            var nodes = new List<ComboNode>();
            int depth = 0;
            int nameStart = 0;
            int nameEnd = 0;
            int open = 0;
            bool labelled = false;
            string name = string.Empty;
            string indent = string.Empty;

            for (int i = bodyStart; i < bodyEnd; i++)
            {
                char c = masked[i];

                if (c == '{')
                {
                    if (depth == 0)
                    {
                        nameEnd = i;
                        while (nameEnd > 0 && char.IsWhiteSpace(masked[nameEnd - 1])) nameEnd--;
                        nameStart = nameEnd;
                        while (nameStart > 0 && IsNodeNameChar(masked[nameStart - 1])) nameStart--;
                        name = masked[nameStart..nameEnd];
                        labelled = HasLabelPrefix(masked, nameStart);
                        open = i + 1;
                        indent = LeadingWhitespace(masked, LineStartOf(masked, i));
                    }

                    depth++;
                }
                else if (c == '}' && depth > 0)
                {
                    depth--;
                    if (depth == 0)
                        nodes.Add(new ComboNode(name, nameStart, nameEnd, open, i, labelled, indent));
                }
            }

            return nodes;
        }

        /// <summary>Does a <c>label:</c> sit before the node name? Only spaces and
        /// tabs separate the two, so the walk never leaves the header line.</summary>
        private static bool HasLabelPrefix(string masked, int nameStart)
        {
            int p = nameStart - 1;
            while (p >= 0 && (masked[p] == ' ' || masked[p] == '\t')) p--;
            return p >= 0 && masked[p] == ':';
        }

        // ---- text helpers -------------------------------------------------------

        /// <summary>Offset of the <c>;</c> ending the statement whose name ends
        /// at <paramref name="from"/>, skipping over <c>"…"</c> strings, or -1
        /// when the statement runs into a brace instead.</summary>
        private int StatementEnd(int from)
        {
            for (int i = from; i < Masked.Length; i++)
            {
                char c = Masked[i];
                if (c == '"')
                {
                    int closing = Masked.IndexOf('"', i + 1);
                    if (closing < 0) return -1;
                    i = closing;
                }
                else if (c == ';') return i;
                else if (c is '{' or '}') return -1;
            }

            return -1;
        }

        /// <summary>True when nothing but whitespace shares the statement's
        /// line(s): everything before its name on that line and everything after
        /// its <c>;</c> on that line is whitespace.</summary>
        private bool OwnLines(int nameStart, int semicolon)
        {
            for (int i = LineStartOf(Masked, nameStart); i < nameStart; i++)
                if (!char.IsWhiteSpace(Masked[i])) return false;

            for (int i = semicolon + 1, end = EndOfLine(Masked, semicolon); i < end; i++)
                if (!char.IsWhiteSpace(Masked[i])) return false;

            return true;
        }

        private static int NetDepth(string text, int from, int to)
        {
            int depth = 0;
            for (int i = from; i < to; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}') depth--;
            }

            return depth;
        }

        private bool AllWhitespace(int from, int to)
        {
            for (int i = from; i < to; i++)
                if (!char.IsWhiteSpace(Masked[i])) return false;

            return true;
        }

        /// <summary>Is the source empty from <paramref name="from"/> to
        /// <paramref name="to"/>? Comments count as text here: the add and delete
        /// rules ask about <em>blank</em> lines, and a comment line is not one.</summary>
        private bool IsBlank(int from, int to)
        {
            for (int i = from; i < to; i++)
                if (!char.IsWhiteSpace(Source[i])) return false;

            return true;
        }

        public string LeadingWhitespace(int lineStart) => LeadingWhitespace(Masked, lineStart);

        private static string LeadingWhitespace(string text, int lineStart)
        {
            int p = lineStart;
            while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
            return text[lineStart..p];
        }

        private static int LineStartOf(string text, int offset)
        {
            int newline = offset <= 0 ? -1 : text.LastIndexOf('\n', offset - 1);
            return newline + 1;
        }

        private static int EndOfLine(string text, int offset)
        {
            int newline = text.IndexOf('\n', Math.Min(offset, text.Length));
            return newline < 0 ? text.Length : newline + 1;
        }

        private static bool IsNodeNameChar(char c) =>
            char.IsAsciiLetterOrDigit(c) || c is '_' or '@' or ',' or '+' or '-' or '.';

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
