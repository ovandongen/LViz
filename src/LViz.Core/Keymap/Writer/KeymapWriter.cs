using System.Text;
using System.Text.RegularExpressions;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;

namespace LViz.Core.Keymap.Writer;

/// <summary>Change one binding: the cell at
/// (<paramref name="LayerIndex"/>, <paramref name="KeyIndex"/>) becomes
/// <paramref name="Binding"/>.</summary>
public sealed record BindingEdit(int LayerIndex, int KeyIndex, KeyBinding Binding);

/// <summary>Rename layer <paramref name="LayerIndex"/> to
/// <paramref name="Name"/> (written as its <c>display-name</c>).</summary>
public sealed record LayerRename(int LayerIndex, string Name);

/// <summary>
/// Applies binding edits and layer renames to the <b>original</b>
/// <c>.keymap</c> text in place: every character outside the changed
/// bindings and <c>display-name</c> values — comments, blank lines, define
/// names, column alignment — survives verbatim. Unchanged bindings keep
/// their original text; a changed binding keeps the following columns in
/// place where the surrounding whitespace allows it. Anything that cannot
/// be done safely (unmappable layers via <see cref="KeymapSourceMap"/>,
/// invalid indices/names, a result that does not re-parse to exactly the
/// expected bindings and names) throws and returns nothing.
/// Pure function: no I/O, <paramref name="source"/> is never modified.
/// </summary>
public static class KeymapWriter
{
    /// <exception cref="ZmkKeymapParseException">
    /// <paramref name="source"/> does not parse, or
    /// <see cref="KeymapSourceMap.Build"/> refuses it.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An edit or rename targets a layer index outside the parsed layers,
    /// or an edit targets a key index outside that layer's bindings.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Two edits target the same (layer, key), two renames target the same
    /// layer, or a rename name is blank or contains a control character.
    /// (The inner <see cref="ZmkBindingFormatter.Format"/> also throws this
    /// for a malformed behavior.)
    /// </exception>
    public static string Apply(
        string source,
        IReadOnlyList<BindingEdit> bindingEdits,
        IReadOnlyList<LayerRename> renames)
    {
        var config = ZmkKeymapLoader.LoadFromText(source, "writer");

        // ---- input checks ----------------------------------------------------

        foreach (var edit in bindingEdits)
        {
            if (edit.LayerIndex < 0 || edit.LayerIndex >= config.Layers.Count)
                throw new ArgumentOutOfRangeException(
                    nameof(bindingEdits),
                    $"binding edit targets layer {edit.LayerIndex}, the keymap has {config.Layers.Count} layer(s).");
            if (edit.KeyIndex < 0 || edit.KeyIndex >= config.Layers[edit.LayerIndex].Bindings.Count)
                throw new ArgumentOutOfRangeException(
                    nameof(bindingEdits),
                    $"binding edit targets key {edit.KeyIndex} of layer {edit.LayerIndex}, "
                    + $"which has {config.Layers[edit.LayerIndex].Bindings.Count} binding(s).");
        }

        var dupEdit = bindingEdits
            .GroupBy(e => (e.LayerIndex, e.KeyIndex))
            .FirstOrDefault(g => g.Count() > 1);
        if (dupEdit is not null)
            throw new ArgumentException(
                $"two binding edits target layer {dupEdit.Key.LayerIndex}, key {dupEdit.Key.KeyIndex}.",
                nameof(bindingEdits));

        foreach (var rename in renames)
        {
            if (rename.LayerIndex < 0 || rename.LayerIndex >= config.Layers.Count)
                throw new ArgumentOutOfRangeException(
                    nameof(renames),
                    $"layer rename targets layer {rename.LayerIndex}, the keymap has {config.Layers.Count} layer(s).");
        }

        var dupRename = renames
            .GroupBy(r => r.LayerIndex)
            .FirstOrDefault(g => g.Count() > 1);
        if (dupRename is not null)
            throw new ArgumentException(
                $"two layer renames target layer {dupRename.Key}.",
                nameof(renames));

        foreach (var rename in renames)
        {
            if (string.IsNullOrWhiteSpace(rename.Name))
                throw new ArgumentException(
                    $"layer rename for layer {rename.LayerIndex} has an empty name.", nameof(renames));
            if (rename.Name.Any(char.IsControl))
                throw new ArgumentException(
                    $"layer rename for layer {rename.LayerIndex} contains a control character.",
                    nameof(renames));
        }

        // ---- skip no-ops; bail out early when nothing would change -----------

        var expectedBindings = config.Layers
            .Select(l => l.Bindings.ToArray())
            .ToArray();
        var expectedNames = config.Layers.Select(l => l.Name).ToArray();

        var realEdits = new List<BindingEdit>();
        foreach (var edit in bindingEdits)
        {
            var current = config.Layers[edit.LayerIndex].Bindings[edit.KeyIndex];
            if (IsSameBinding(current, edit.Binding)) continue;
            expectedBindings[edit.LayerIndex][edit.KeyIndex] = edit.Binding;
            realEdits.Add(edit);
        }

        var realRenames = new List<LayerRename>();
        foreach (var rename in renames)
        {
            if (string.Equals(rename.Name, config.Layers[rename.LayerIndex].Name, StringComparison.Ordinal))
                continue;
            expectedNames[rename.LayerIndex] = rename.Name;
            realRenames.Add(rename);
        }

        if (realEdits.Count == 0 && realRenames.Count == 0) return source;

        // ---- plan the text changes --------------------------------------------

        var maps = KeymapSourceMap.Build(source, config);
        var defines = LayerReferenceDefines(source);

        var changes = new List<(int Offset, string Replacement, int ReplacedLength)>();

        foreach (var edit in realEdits)
        {
            var span = maps[edit.LayerIndex].Bindings[edit.KeyIndex];
            var plan = PlanBindingReplacement(source, span, ZmkBindingFormatter.Format(edit.Binding, defines));
            changes.Add((span.Start, plan.Replacement, plan.Length));
        }

        foreach (var rename in realRenames)
        {
            var map = maps[rename.LayerIndex];
            var escaped = EscapeDisplayName(rename.Name);
            if (map.DisplayNameValue is { } value)
                changes.Add((value.Start, escaped, value.Length));
            else
            {
                var (insertAt, inserted) = InsertDisplayName(source, map, escaped);
                changes.Add((insertAt, inserted, 0));
            }
        }

        // ---- apply the disjoint changes (see ApplyChanges for the ordering) ----

        var result = ApplyChanges(source, changes);

        // ---- bug guard: the result must re-parse to exactly the expectation ---

        Validate(result, expectedBindings, expectedNames);
        return result;
    }

    // ---- layer names -------------------------------------------------------------

    private static readonly Regex LayerReferenceRegex = new(
        @"&(?:" + string.Join("|", ZmkBindingFormatter.LayerBehaviors.Select(b => b[1..]))
        + @")\s+([A-Za-z_]\w*)",
        RegexOptions.Compiled);

    /// <summary>
    /// The file's defines that it already uses as a layer reference
    /// (<c>&amp;mo NAME</c>, <c>&amp;lt NAME …</c>, anywhere in the file).
    /// Only these may name a layer number: MoErgo exports define e.g.
    /// <c>KB_TYPE_GLOVE_80 1</c> ahead of <c>LAYER_Symbol 1</c>, and the
    /// first-match rule would otherwise write <c>&amp;mo KB_TYPE_GLOVE_80</c>.
    /// </summary>
    private static List<KeyValuePair<string, string>> LayerReferenceDefines(string source)
    {
        var used = LayerReferenceRegex.Matches(source)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        return Preprocessor.CollectDefines(source)
            .Where(d => used.Contains(d.Key))
            .ToList();
    }

    // ---- binding replacement ---------------------------------------------------

    private readonly record struct BindingPlan(string Replacement, int Length);

    /// <summary>
    /// The text replacing <c>source[span.Start .. span.Start + length]</c>
    /// (the old binding plus any whitespace eaten to absorb a longer
    /// replacement). Unchanged columns: a shorter replacement is padded to
    /// the old width; a longer one eats whitespace after the binding but
    /// always keeps at least one separator character; at the end of a line
    /// or array nothing is padded.
    /// </summary>
    private static BindingPlan PlanBindingReplacement(string source, SourceSpan old, string newText)
    {
        int end = old.Start + old.Length;
        int wsEnd = end;
        while (wsEnd < source.Length && (source[wsEnd] == ' ' || source[wsEnd] == '\t')) wsEnd++;
        int wsLength = wsEnd - end;
        char? next = wsEnd < source.Length ? source[wsEnd] : null;

        if (next is null or '\r' or '\n' or '>') return new BindingPlan(newText, old.Length);

        int delta = newText.Length - old.Length;
        if (delta < 0) return new BindingPlan(newText + new string(' ', -delta), old.Length);

        // A longer replacement eats whitespace after the binding but always
        // keeps at least one separator character between the bindings.
        int eat = Math.Max(0, Math.Min(delta, wsLength - 1));
        return new BindingPlan(newText, old.Length + eat);
    }

    // ---- rename ----------------------------------------------------------------

    /// <summary>Escapes a layer name for inside a devicetree string
    /// literal: backslash first, then the quote.</summary>
    private static string EscapeDisplayName(string name) =>
        name.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>
    /// The insertion for a layer without a <c>display-name</c>: as its own
    /// line directly above the <c>bindings</c> line when that line is
    /// indented, otherwise inline before the keyword with a trailing space.
    /// Returns (insertion offset, inserted text) with the old text kept.
    /// </summary>
    private static (int, string) InsertDisplayName(string source, LayerSourceMap map, string escapedName)
    {
        string property = $"display-name = \"{escapedName}\";";

        int newline = source.LastIndexOf('\n', map.BindingsKeywordOffset - 1);
        int lineStart = newline + 1;
        bool indentOnly = true;
        for (int i = lineStart; i < map.BindingsKeywordOffset; i++)
        {
            if (!char.IsWhiteSpace(source[i])) { indentOnly = false; break; }
        }

        if (!indentOnly) return (map.BindingsKeywordOffset, property + " ");

        string indent = source[lineStart..map.BindingsKeywordOffset];
        // the line's own terminator: the newline at `newline`, preceded by a CR
        // when the file uses CRLF
        string lineEnd = newline >= 1 && source[newline - 1] == '\r' ? "\r\n" : "\n";
        return (map.BindingsKeywordOffset, property + lineEnd + indent);
    }

    // ---- apply + validate --------------------------------------------------------

    private static string ApplyChanges(
        string source,
        List<(int Offset, string Replacement, int ReplacedLength)> changes)
    {
        // Spans are disjoint, so patching left-to-right (as the output is
        // built) is equivalent to applying them highest-offset-first while
        // keeping each offset valid: nothing before an offset is touched.
        var sb = new StringBuilder(source.Length);
        int cursor = 0;
        foreach (var (offset, replacement, replacedLength) in changes.OrderBy(c => c.Offset))
        {
            if (offset < cursor)
                throw new InvalidOperationException(
                    $"planned changes overlap: change at offset {offset} starts inside the "
                    + $"previous change (ends at {cursor}).");
            sb.Append(source, cursor, offset - cursor);
            sb.Append(replacement);
            cursor = offset + replacedLength;
        }
        sb.Append(source, cursor, source.Length - cursor);
        return sb.ToString();
    }

    private static void Validate(
        string result,
        KeyBinding[][] expectedBindings,
        string[] expectedNames)
    {
        var config = ZmkKeymapLoader.LoadFromText(result, "writer");

        if (config.Layers.Count != expectedNames.Length)
            throw new InvalidOperationException(
                $"the written keymap re-parses to {config.Layers.Count} layers, "
                + $"expected {expectedNames.Length}.");

        for (int layer = 0; layer < expectedNames.Length; layer++)
        {
            var actual = config.Layers[layer];
            if (!string.Equals(actual.Name, expectedNames[layer], StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"after writing, layer {layer} is named '{actual.Name}', expected '{expectedNames[layer]}'.");

            if (actual.Bindings.Count != expectedBindings[layer].Length)
                throw new InvalidOperationException(
                    $"after writing, layer {layer} ('{actual.Name}') has {actual.Bindings.Count} bindings, "
                    + $"expected {expectedBindings[layer].Length}.");

            for (int key = 0; key < expectedBindings[layer].Length; key++)
            {
                if (!IsSameBinding(actual.Bindings[key], expectedBindings[layer][key]))
                    throw new InvalidOperationException(
                        $"after writing, layer {layer} ('{actual.Name}') key {key} is "
                        + $"'{actual.Bindings[key].Display}', expected "
                        + $"'{expectedBindings[layer][key].Display}'.");
            }
        }
    }

    /// <summary>KeyBinding's record equality ignores the contents of
    /// <c>Params</c> — compare behavior plus params explicitly.</summary>
    private static bool IsSameBinding(KeyBinding a, KeyBinding b) =>
        string.Equals(a.Behavior, b.Behavior, StringComparison.Ordinal)
        && a.Params.SequenceEqual(b.Params, StringComparer.Ordinal);
}
