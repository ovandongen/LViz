using System.Globalization;
using System.Text;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Layout.Dtsi;
using LViz.Core.Models;

namespace LViz.Core.Keymap.Writer;

/// <summary>
/// Applies a structural <see cref="LayerStructureEdit"/> to the <b>original</b>
/// <c>.keymap</c> text in place, the way <see cref="KeymapWriter"/> does for
/// bindings and names: every character outside the moved block, the renumbered
/// layer numbers and the layer <c>#define</c> values — comments, blank lines,
/// column alignment, everything else — survives verbatim, and a reference
/// written as a define name (<c>&amp;lt LAYER_Symbol RET</c>) is re-pointed by
/// rewriting that define's value instead of the reference. A layer that is
/// added is written in the style the file already uses: node name, indentation
/// and rows per line are copied from the existing blocks, and a new
/// <c>#define</c> only appears in a MoErgo export, where every layer has one.
/// A layer that is deleted takes its block, its own <c>#define</c> line and the
/// layer references pointing at it out of the file — and refuses, before
/// touching anything, a layer something outside the layer blocks still uses
/// (see <see cref="CheckDelete"/>).
/// The result is re-parsed and compared with <see cref="LayerReferenceRemapper.Apply"/>
/// on the original config; any difference throws and returns nothing, so a
/// keymap is never written out pointing at the wrong layer.
/// Pure function: no I/O, <paramref name="source"/> is never modified.
/// </summary>
public static class KeymapLayerWriter
{
    /// <exception cref="ZmkKeymapParseException">
    /// <paramref name="source"/> does not parse, or its layer nodes do not
    /// match the parsed layer list one-for-one.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The edit targets a layer index outside the parsed layers.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An <see cref="LayerStructureEdit.Add"/> names its layer blank, with a
    /// control character, or with a binding count that is not one per key.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A <see cref="LayerStructureEdit.Delete"/> is refused by
    /// <see cref="CheckDelete"/> — the blockers are named in the message — or
    /// the written text does not re-parse to the expected model.
    /// </exception>
    public static string Apply(string source, LayerStructureEdit edit)
    {
        var config = ZmkKeymapLoader.LoadFromText(source, "writer");

        if (edit is LayerStructureEdit.Move move)
        {
            // A layer moved onto its own slot changes nothing — not a number,
            // not a define, not a byte: hand back the very same string.
            if (move.From == move.To) return source;

            var expected = LayerReferenceRemapper.Apply(config, move);
            var result = ApplyMove(source, config, move);

            Validate(result, expected);
            return result;
        }

        if (edit is LayerStructureEdit.Add add)
        {
            // The remapper owns what a valid Add is (index range, one binding
            // per key); a layer name is this writer's problem alone, because it
            // has to end up inside a devicetree string literal.
            var expected = LayerReferenceRemapper.Apply(config, add);
            RequireUsableName(add.Name);

            var result = ApplyAdd(source, config, expected, add);

            Validate(result, expected);
            return result;
        }

        if (edit is LayerStructureEdit.Delete delete)
        {
            // Refuse before writing anything, and refuse for the same reasons the
            // caller sees in CheckDelete: a caller that skipped the pre-check gets
            // the blockers in the exception instead of a silently loosened keymap.
            var check = CheckDelete(config, source, delete.Index);
            if (!check.CanDelete)
                throw new InvalidOperationException(
                    LayerDeleteRules.DescribeBlockers(config, delete.Index, check.Blockers));

            var expected = LayerReferenceRemapper.Apply(config, delete);
            var result = ApplyDelete(source, config, expected, delete);

            Validate(result, expected);
            return result;
        }

        throw new ArgumentException($"Unknown layer edit '{edit.GetType().Name}'.", nameof(edit));
    }

    /// <summary>Apply <paramref name="edits"/> one after another, each against
    /// the result of the previous one. An empty list returns
    /// <paramref name="source"/> itself.</summary>
    public static string ApplyAll(string source, IReadOnlyList<LayerStructureEdit> edits)
    {
        foreach (var edit in edits) source = Apply(source, edit);
        return source;
    }

    /// <summary>
    /// What deleting layer <paramref name="index"/> would do, without writing
    /// anything: how many layer bindings lose their target and get cleared
    /// (<c>&amp;mo X → &amp;none</c>, <c>&amp;lt X K → &amp;kp K</c>), and — when
    /// the delete is impossible — every place that still uses the layer. The
    /// delete dialog asks this first so the user sees the damage, and
    /// <see cref="Apply"/> refuses exactly those deletes.
    /// </summary>
    /// <exception cref="ZmkKeymapParseException">
    /// <paramref name="source"/> does not parse, or its layer nodes do not
    /// match the parsed layer list one-for-one.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is outside the parsed layers.
    /// </exception>
    public static LayerDeleteCheck CheckDelete(string source, int index)
    {
        var config = ZmkKeymapLoader.LoadFromText(source, "writer");
        return CheckDelete(config, source, index);
    }

    /// <summary>The policy is <see cref="LayerDeleteRules"/>; this scans the text
    /// for the uses it cannot see in the model: a layer number outside the layer
    /// blocks and the combos node (a combo's own number is reported as the combo),
    /// and the layer's <c>#define</c> names, which anything can use — a hold-tap
    /// spelled <c>ZMK_TD_LAYER</c>, an input listener's <c>layers</c> list.</summary>
    private static LayerDeleteCheck CheckDelete(KeyboardConfig config, string source, int index)
    {
        var text = KeymapLayerText.Parse(source, config);

        var uses = text.References
            .Where(r => r.Site is not (ReferenceSite.LayerBlock or ReferenceSite.Combo))
            .Select(r => new LayerTextUse(
                r.Layer,
                r.Site == ReferenceSite.ConditionalLayer
                    ? LayerDeleteBlockerKind.ConditionalLayer
                    : LayerDeleteBlockerKind.Other,
                text.EnclosingNodeName(r.Offset),
                r.Line))
            .ToList();

        // A layer can be named by more than one define, so the usages are merged
        // into file order.
        uses.AddRange(text.LayerDefines
            .SelectMany(d => text.FindNameUsages(d.Name).Select(offset => (offset, d)))
            .OrderBy(u => u.offset)
            .Select(u => new LayerTextUse(
                u.d.LayerIndex, LayerDeleteBlockerKind.DefineUsage, u.d.Name, DtsiText.LineOf(text.Masked, u.offset))));

        return LayerDeleteRules.Check(config, uses, index);
    }

    // ---- move --------------------------------------------------------------------

    /// <summary>
    /// A move changes two unrelated things, so it runs in two phases and no
    /// offset ever has to be re-based across the other:
    /// <list type="number">
    /// <item>renumber the whole file (numeric references and layer define
    /// values) on the original text; blocks stay where they are;</item>
    /// <item>re-scan the result and move the block, now indexed in that text, to
    /// its new position.</item>
    /// </list>
    /// </summary>
    private static string ApplyMove(string source, KeyboardConfig config, LayerStructureEdit.Move move)
    {
        var map = LayerReferenceRemapper.IndexMap(config.Layers.Count, move);
        var renumbered = KeymapWriter.ApplyChanges(
            source,
            KeymapLayerText.Parse(source, config)
                .Renumber(map)
                .Select(c => (c.Offset, c.Replacement, c.Length))
                .ToList());

        var renumberedConfig = ZmkKeymapLoader.LoadFromText(renumbered, "writer");
        var blocks = KeymapLayerText.Parse(renumbered, renumberedConfig).Layers;
        return MoveBlock(renumbered, blocks, move.From, move.To);
    }

    /// <summary>
    /// Cut layer block <paramref name="from"/> together with the separator that
    /// travels with it and put the pair back at position <paramref name="to"/> of
    /// the shortened list — <c>block + separator</c> directly before the block
    /// sitting there, or <c>separator + block</c> after the last block when
    /// appending. Because the separator moves with its block, every block keeps a
    /// separator of its own on both sides and moving the same layer there and
    /// back returns the original text byte-for-byte.
    /// </summary>
    private static string MoveBlock(
        string text, IReadOnlyList<LayerBlock> blocks, int from, int to)
    {
        var (cutStart, cutEnd) = CutSpan(blocks, from);
        string block = text[blocks[from].Start..blocks[from].End];
        string separator = SeparatorOf(text, blocks, from);
        string remaining = text.Remove(cutStart, cutEnd - cutStart);

        // The offsets above still index the un-cut text; anything at or past the
        // cut sits that much further left once the cut is made.
        int shift = cutEnd - cutStart;
        var shortened = new List<LayerBlock>(blocks.Count - 1);
        for (int i = 0; i < blocks.Count; i++)
            if (i != from) shortened.Add(blocks[i]);

        var (offset, append) = InsertionPoint(shortened, to);
        int insertAt = offset <= cutStart ? offset : offset - shift;

        return remaining.Insert(insertAt, append ? separator + block : block + separator);
    }

    /// <summary>
    /// What a delete removes and a move cuts: block <paramref name="index"/> plus
    /// the separator next to it. A block that isn't last takes the separator
    /// <i>after</i> it, the last block the one <i>before</i> it, so whatever is
    /// left always ends on a block boundary instead of on a stray blank line.
    /// </summary>
    private static (int Start, int End) CutSpan(IReadOnlyList<LayerBlock> blocks, int index)
    {
        if (index < 0 || index >= blocks.Count)
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"layer {index} is out of range 0…{blocks.Count - 1}.");

        if (index < blocks.Count - 1) return (blocks[index].Start, blocks[index + 1].Start);
        if (index == 0)
            throw new InvalidOperationException(
                "a keymap with a single layer block has no separator to move with it.");

        return (blocks[index - 1].End, blocks[index].End);
    }

    /// <summary>The blank lines and comments between block <paramref name="index"/>
    /// and its neighbour — the separator it takes with it (the previous neighbour
    /// for the last block, matching <see cref="CutSpan"/>).</summary>
    private static string SeparatorOf(string text, IReadOnlyList<LayerBlock> blocks, int index) =>
        index < blocks.Count - 1
            ? text[blocks[index].End..blocks[index + 1].Start]
            : text[blocks[index - 1].End..blocks[index].Start];

    /// <summary>
    /// Where a block landing at position <paramref name="at"/> goes: the
    /// <see cref="LayerBlock.Start"/> of the block currently sitting there
    /// (insert before it, separator after the block), or the
    /// <see cref="LayerBlock.End"/> of the last block when
    /// <paramref name="at"/> appends (separator before the block). Offsets index
    /// the same text as <paramref name="blocks"/>.
    /// </summary>
    private static (int Offset, bool Append) InsertionPoint(
        IReadOnlyList<LayerBlock> blocks, int at)
    {
        if (at < 0)
            throw new ArgumentOutOfRangeException(
                nameof(at), at, "position is before the first layer.");

        if (at < blocks.Count) return (blocks[at].Start, false);

        if (blocks.Count == 0)
            throw new InvalidOperationException("the keymap has no layer block to append after.");

        return (blocks[^1].End, true);
    }

    // ---- add ---------------------------------------------------------------------

    /// <summary>
    /// Insert a new layer, in the same two phases as <see cref="ApplyMove"/> so no
    /// offset ever has to be re-based across the other half of the change:
    /// <list type="number">
    /// <item>renumber the whole file on the original text, and add the new
    /// layer's <c>#define</c> line when the file keeps a define per layer;</item>
    /// <item>re-scan the result and insert the freshly written block at its
    /// position, with a separator copied from the file.</item>
    /// </list>
    /// The block's bindings are read from the <b>renumbered</b> model, so they are
    /// already in the new index space and none of them is ever rewritten again.
    /// </summary>
    private static string ApplyAdd(
        string source, KeyboardConfig config, KeyboardConfig expected, LayerStructureEdit.Add add)
    {
        var text = KeymapLayerText.Parse(source, config);
        var changes = text
            .Renumber(LayerReferenceRemapper.IndexMap(config.Layers.Count, add))
            .Select(c => (c.Offset, c.Replacement, c.Length))
            .ToList();

        // Its own insert change, so it can never overlap the value change sitting
        // on the define line above it.
        if (NewDefineLine(text, config, add) is { } define)
            changes.Add((define.Offset, define.Line, 0));

        var renumbered = KeymapWriter.ApplyChanges(source, changes);

        var renumberedConfig = ZmkKeymapLoader.LoadFromText(renumbered, "writer");
        var renumberedText = KeymapLayerText.Parse(renumbered, renumberedConfig);
        var blocks = renumberedText.Layers;

        string block = NewBlock(renumberedText, renumberedConfig, expected, add);

        // The gap between two existing blocks is the separator a new block needs
        // too; a keymap with a single layer has no gap to copy, so its blank line
        // is spelled out instead.
        string separator = blocks.Count == 1
            ? renumberedText.NewLine
            : SeparatorOf(renumbered, blocks, 0);

        var (offset, append) = InsertionPoint(blocks, add.AtIndex);
        return renumbered.Insert(offset, append ? separator + block : block + separator);
    }

    /// <summary>
    /// Where to insert the new layer's <c>#define</c> line, and what to insert —
    /// only for a MoErgo export, where <b>every</b> layer <c>i</c> already has a
    /// <c>#define LAYER_&lt;sanitised name of layer i&gt; i</c>. Cosmos (no
    /// defines at all) and Slabwing (defines that name nothing) get none: half a
    /// define table would be worse than none, because the binding formatter picks
    /// the first define whose value matches and would start mixing define names
    /// and bare numbers inside one bindings array.
    /// </summary>
    private static (int Offset, string Line)? NewDefineLine(
        KeymapLayerText text, KeyboardConfig config, LayerStructureEdit.Add add)
    {
        var byName = text.LayerDefines.ToDictionary(d => d.Name, StringComparer.Ordinal);
        int lastLineEnd = -1;

        for (int i = 0; i < config.Layers.Count; i++)
        {
            if (!byName.TryGetValue("LAYER_" + KeymapLayerText.SanitizeName(config.Layers[i].Name), out var define)
                || define.LayerIndex != i)
                return null;

            lastLineEnd = Math.Max(lastLineEnd, define.LineEnd);
        }

        string name = "LAYER_" + KeymapLayerText.SanitizeName(add.Name);

        // A name the file already defines is left alone rather than redefined:
        // a second #define would silently win over the first for anything the
        // preprocessor expands, and this writer cannot rewrite those uses. The
        // new layer simply keeps bare numbers, like a file without defines.
        if (Preprocessor.CollectDefines(text.Source).Any(d => d.Key == name)) return null;

        return (lastLineEnd, $"#define {name} {add.AtIndex}{text.NewLine}");
    }

    /// <summary>
    /// The text of the new block, laid out like the blocks it joins: header and
    /// closing <c>};</c> at the first block's indentation, everything between them
    /// at the indentation of that block's <c>bindings</c> line, and as many
    /// bindings per line as layer 0 uses. Cells are separated by two spaces with
    /// no column alignment — the file's own alignment is not ours to reproduce.
    /// </summary>
    private static string NewBlock(
        KeymapLayerText scan,
        KeyboardConfig config,
        KeyboardConfig expected,
        LayerStructureEdit.Add add)
    {
        string text = scan.Source;
        var maps = KeymapSourceMap.Build(text, config);
        string indent = scan.Layers[0].Indent;
        string inner = DtsiText.LeadingWhitespace(text, DtsiText.LineStartOf(text, maps[0].BindingsKeywordOffset));
        var rows = RowLengths(text, maps[0].Bindings);
        var defines = LayerReferenceDefines(scan);

        var cells = expected.Layers[add.AtIndex].Bindings
            .Select(b => ZmkBindingFormatter.Format(b, defines, expected.HoldTaps))
            .ToList();

        string nl = scan.NewLine;
        var sb = new StringBuilder();
        sb.Append(indent).Append(NodeNameFor(scan.Layers, add.Name)).Append(" {").Append(nl);
        sb.Append(inner).Append("display-name = \"").Append(WriterText.EscapeDisplayName(add.Name)).Append("\";").Append(nl);
        sb.Append(inner).Append("bindings = <").Append(nl);

        // The row lengths add up to layer 0's binding count, which the remapper
        // already insisted is the number of cells there are to write.
        int taken = 0;
        foreach (int row in rows)
        {
            sb.Append(inner).Append(string.Join("  ", cells.GetRange(taken, row))).Append(nl);
            taken += row;
        }

        sb.Append(inner).Append(">;").Append(nl);
        sb.Append(indent).Append("};").Append(nl);
        return sb.ToString();
    }

    /// <summary>
    /// The node name of a new layer called <paramref name="name"/>, in the naming
    /// style the file itself uses: <c>layer_X</c> when every layer node starts
    /// with <c>layer_</c>, <c>X_layer</c> when every one ends with it, and a bare
    /// <c>X</c> for a file that mixes the two. A name a layer node already holds
    /// gets <c>_2</c>, <c>_3</c>, … until it is unique.
    /// </summary>
    private static string NodeNameFor(IReadOnlyList<LayerBlock> blocks, string name)
    {
        string key = KeymapLayerText.SanitizeName(name);
        if (char.IsAsciiDigit(key[0])) key = "l" + key;

        string candidate = blocks.All(b => b.NodeName.StartsWith("layer_", StringComparison.Ordinal))
            ? "layer_" + key
            : blocks.All(b => b.NodeName.EndsWith("_layer", StringComparison.Ordinal))
                ? key + "_layer"
                : key;

        var taken = blocks.Select(b => b.NodeName).ToHashSet(StringComparer.Ordinal);
        string unique = candidate;
        for (int suffix = 2; taken.Contains(unique); suffix++)
            unique = candidate + "_" + suffix.ToString(CultureInfo.InvariantCulture);

        return unique;
    }

    /// <summary>How many of layer 0's bindings start on each source line, in line
    /// order — the row layout the new block copies. The spans run in file order,
    /// so a single forward scan over the text numbers them.</summary>
    private static List<int> RowLengths(string text, IReadOnlyList<SourceSpan> spans)
    {
        var rows = new List<int>();
        int offset = 0;
        int line = 1;
        int current = -1;

        foreach (var span in spans)
        {
            for (; offset < span.Start; offset++)
                if (text[offset] == '\n') line++;

            if (line != current)
            {
                rows.Add(0);
                current = line;
            }
            rows[^1]++;
        }

        return rows;
    }

    /// <summary>
    /// The defines allowed to stand in for a layer number in text this writer
    /// writes: the layer defines that the file itself uses where a layer number
    /// may stand — the rule <see cref="KeymapWriter"/> follows, which is what
    /// keeps MoErgo's <c>KB_TYPE_GLOVE_80 1</c> out of an <c>&amp;mo</c> — each
    /// with the value it holds in the scanned text, so scanning the renumbered
    /// text hands back the layers' new numbers.
    /// </summary>
    private static List<KeyValuePair<string, string>> LayerReferenceDefines(KeymapLayerText text) =>
        text.LayerDefines
            .Where(d => text.LayerReferenceNames.Contains(d.Name))
            .Select(d => new KeyValuePair<string, string>(
                d.Name, text.Source[d.ValueOffset..(d.ValueOffset + d.ValueLength)]))
            .ToList();

    /// <summary>What a new layer's name may be: the rule a rename already
    /// follows, since the name ends up inside a devicetree string literal.</summary>
    private static void RequireUsableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A new layer needs a name.", "edit");
        if (name.Any(char.IsControl))
            throw new ArgumentException("A new layer name may not contain a control character.", "edit");
    }

    // ---- delete ------------------------------------------------------------------

    /// <summary>
    /// A delete changes three unrelated things, so it runs in three phases and no
    /// offset ever has to be re-based across another:
    /// <list type="number">
    /// <item>clear the references to the layer inside the <b>remaining</b> layer
    /// bindings, through <see cref="KeymapWriter"/>, which keeps its span
    /// patching, its column alignment and its own bug guard;</item>
    /// <item>on that text, renumber every remaining layer number and drop the
    /// deleted layer's own <c>#define</c> line;</item>
    /// <item>on the re-scanned text, cut the block out with the rule a move uses
    /// (<see cref="CutSpan"/>), separator included.</item>
    /// </list>
    /// A MoErgo <c>#ifndef … #define LAYER_X 0 … #endif</c> fallback of the
    /// deleted layer's name is not a layer define and stays put, exactly like a
    /// move leaving it alone.
    /// </summary>
    private static string ApplyDelete(
        string source, KeyboardConfig config, KeyboardConfig expected, LayerStructureEdit.Delete delete)
    {
        var cleared = KeymapWriter.Apply(source, LayerDeleteRules.ClearedEdits(config, expected, delete.Index), []);

        var clearedConfig = ZmkKeymapLoader.LoadFromText(cleared, "writer");
        var text = KeymapLayerText.Parse(cleared, clearedConfig);

        var changes = text
            .Renumber(LayerReferenceRemapper.IndexMap(config.Layers.Count, delete))
            .Select(c => (c.Offset, c.Replacement, c.Length))
            .ToList();

        // Whole lines, so the define table keeps no hole of its own. They can
        // never overlap a renumbering: a deleted layer's define value maps to
        // null and is left untouched.
        foreach (var define in text.LayerDefines.Where(d => d.LayerIndex == delete.Index))
            changes.Add((define.LineStart, string.Empty, define.LineEnd - define.LineStart));

        var renumbered = KeymapWriter.ApplyChanges(cleared, changes);

        var cutConfig = ZmkKeymapLoader.LoadFromText(renumbered, "writer");
        var (cutStart, cutEnd) = CutSpan(KeymapLayerText.Parse(renumbered, cutConfig).Layers, delete.Index);
        return renumbered.Remove(cutStart, cutEnd - cutStart);
    }

    // ---- bug guard ---------------------------------------------------------------

    /// <summary>
    /// Re-parse what was written and compare it with the model the edit should
    /// have produced: same layers in the same order with the same names and
    /// bindings, same combos with the same bindings and layer scopes, same macros
    /// with the same bodies. Conditional layers are not in the model, so a
    /// renumbered <c>if-layers</c>/<c>then-layer</c> is not checked here.
    /// </summary>
    private static void Validate(string result, KeyboardConfig expected)
    {
        var config = ZmkKeymapLoader.LoadFromText(result, "writer");

        if (config.Layers.Count != expected.Layers.Count)
            throw new InvalidOperationException(
                $"after writing, the keymap re-parses to {config.Layers.Count} layers, "
                + $"expected {expected.Layers.Count}.");

        for (int layer = 0; layer < expected.Layers.Count; layer++)
        {
            var actual = config.Layers[layer];
            var want = expected.Layers[layer];

            if (!string.Equals(actual.Name, want.Name, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"after writing, layer {layer} is named '{actual.Name}', expected '{want.Name}'.");

            if (actual.Bindings.Count != want.Bindings.Count)
                throw new InvalidOperationException(
                    $"after writing, layer {layer} ('{actual.Name}') has {actual.Bindings.Count} bindings, "
                    + $"expected {want.Bindings.Count}.");

            for (int key = 0; key < want.Bindings.Count; key++)
            {
                if (!actual.Bindings[key].SameAs(want.Bindings[key]))
                    throw new InvalidOperationException(
                        $"after writing, layer {layer} ('{actual.Name}') key {key} is "
                        + $"'{actual.Bindings[key].Display}', expected '{want.Bindings[key].Display}'.");
            }
        }

        if (config.Combos.Count != expected.Combos.Count)
            throw new InvalidOperationException(
                $"after writing, the keymap re-parses to {config.Combos.Count} combos, "
                + $"expected {expected.Combos.Count}.");

        for (int combo = 0; combo < expected.Combos.Count; combo++)
        {
            var actual = config.Combos[combo];
            var want = expected.Combos[combo];

            if (!actual.Binding.SameAs(want.Binding))
                throw new InvalidOperationException(
                    $"after writing, combo '{actual.Name}' fires '{actual.Binding.Display}', "
                    + $"expected '{want.Binding.Display}'.");

            if (!actual.Layers.SequenceEqual(want.Layers))
                throw new InvalidOperationException(
                    $"after writing, combo '{actual.Name}' is active on layers "
                    + $"{DescribeScopes(actual.Layers)}, expected {DescribeScopes(want.Layers)}.");
        }

        if (config.Macros.Count != expected.Macros.Count)
            throw new InvalidOperationException(
                $"after writing, the keymap re-parses to {config.Macros.Count} macros, "
                + $"expected {expected.Macros.Count}.");

        for (int macro = 0; macro < expected.Macros.Count; macro++)
        {
            var actual = config.Macros[macro];
            var want = expected.Macros[macro];

            if (actual.Bindings.Count != want.Bindings.Count
                || !actual.Bindings.Zip(want.Bindings).All(p => p.First.SameAs(p.Second)))
                throw new InvalidOperationException(
                    $"after writing, macro '{actual.Name}' runs "
                    + $"<{string.Join(", ", actual.Bindings.Select(b => b.Display))}>, expected "
                    + $"<{string.Join(", ", want.Bindings.Select(b => b.Display))}>.");
        }
    }

    private static string DescribeScopes(IReadOnlyList<int> scopes) =>
        "<" + string.Join(" ", scopes) + ">";
}
