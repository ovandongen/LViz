using System.Globalization;
using System.Text.RegularExpressions;
using LViz.Core.Keymap.Parser;
using LViz.Core.Layout.Dtsi;
using LViz.Core.Models;

namespace LViz.Core.Keymap.Writer;

/// <summary>One in-place replacement in the original <c>.keymap</c> text:
/// the <paramref name="Length"/> characters at <paramref name="Offset"/>
/// become <paramref name="Replacement"/>.</summary>
internal readonly record struct TextChange(int Offset, int Length, string Replacement);

/// <summary>
/// Where one layer node sits in the original <c>.keymap</c> text:
/// <see cref="Start"/> is the first character of the line holding the node
/// header (its indentation included), <see cref="End"/> just past the newline
/// that ends the line with the node's closing <c>};</c> — the text length when
/// the file has none — so the block is exactly the lines a move or a delete
/// would cut out.
/// </summary>
internal readonly record struct LayerBlock(int Start, int End, string NodeName, string Indent);

/// <summary>
/// The <b>first</b> <c>#define NAME value</c> line of a name that names a
/// layer: the span of the value plus the span of the whole line
/// (<see cref="LineEnd"/> is just past its newline). A later fallback of the
/// same name (<c>#ifndef LAYER_Lower</c> / <c>#define LAYER_Lower 0</c> /
/// <c>#endif</c>) is never reported, so it can never be rewritten.
/// </summary>
internal sealed record LayerDefine(
    string Name,
    int LayerIndex,
    int ValueOffset,
    int ValueLength,
    int LineStart,
    int LineEnd);

/// <summary>What encloses a numeric layer reference.</summary>
internal enum ReferenceSite { LayerBlock, Combo, ConditionalLayer, Other }

/// <summary>One integer token used as a layer number; <see cref="Line"/> is 1-based.</summary>
internal readonly record struct LayerReference(int Offset, int Length, int Layer, ReferenceSite Site, int Line);

/// <summary>
/// The text side of layer add/delete/move: locates every layer node's block,
/// every <c>#define</c> that names a layer, and every numeric layer reference
/// in the <b>original</b> <c>.keymap</c> text, and turns an
/// <c>old-index → new-index</c> map into the replacements that renumber the
/// file. Like <see cref="KeymapWriter"/> it patches instead of regenerating:
/// comments, blank lines, alignment and anything it does not understand stay
/// byte-for-byte. Offsets index <see cref="Source"/> (they are found on
/// <see cref="Masked"/>, which has the same length and line structure).
/// Pure: no I/O, the input text is never modified.
/// </summary>
internal sealed class KeymapLayerText
{
    private const string KeymapCompatible = "zmk,keymap";
    private const string CombosCompatible = "zmk,combos";
    private const string ConditionalLayersCompatible = "zmk,conditional-layers";

    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    /// <summary>Longest first so <c>&amp;tog</c> is never read as <c>&amp;to</c>.</summary>
    private static readonly string LayerBehaviorAlternation = string.Join(
        "|", ZmkBindingFormatter.LayerBehaviors.Select(b => b[1..]).OrderByDescending(s => s.Length));

    /// <summary>
    /// The layer behaviors with their first parameter — a number
    /// (<c>&amp;mo 3</c>, <c>&amp;lt 1 ENTER</c>) or the name of a define
    /// holding that number (<c>&amp;to LAYER_Lower</c>). Only the first
    /// parameter is taken; the trailing ones belong to the binding.
    /// </summary>
    private static readonly Regex BehaviorParameterRegex = new(
        @"&(?:" + LayerBehaviorAlternation + @")(?![A-Za-z0-9_])[ \t\r\n]+(\d+|[A-Za-z_][A-Za-z0-9_]*)(?![A-Za-z0-9_])",
        Options);

    /// <summary><c>layers = &lt;…&gt;</c> (combo layer scopes, input listeners).
    /// The lookbehind keeps <c>if-layers</c>, which is a pattern of its own, out.</summary>
    private static readonly Regex LayersArrayRegex = new(
        @"(?<![-A-Za-z0-9_])layers[ \t\r\n]*=[ \t\r\n]*<([^>]*)>",
        Options);

    /// <summary><c>if-layers = &lt;…&gt;</c> and <c>then-layer = &lt;…&gt;</c>.</summary>
    private static readonly Regex ConditionalLayerArrayRegex = new(
        @"(?<![-A-Za-z0-9_])(?:if-layers|then-layer)[ \t\r\n]*=[ \t\r\n]*<([^>]*)>",
        Options);

    /// <summary>A cell of a <c>&lt;…&gt;</c> array: a number or an identifier.</summary>
    private static readonly Regex ArrayTokenRegex = new(@"\d+|[A-Za-z_][A-Za-z0-9_]*", Options);

    /// <summary>A directive keyword and the name it talks about.</summary>
    private static readonly Regex DirectiveRegex = new(
        @"^[ \t]*#[ \t]*(define|undef|ifdef|ifndef)[ \t]+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Multiline | Options);

    /// <summary>The rest of a <c>#define</c> line, when it is one plain
    /// integer and nothing else (so function-like macros and
    /// <c>'W'</c>-style values are not layer numbers).</summary>
    private static readonly Regex IntegerValueRegex = new(@"^[ \t]+(\d+)[ \t\r]*$", Options);

    private static readonly Regex SanitizerRegex = new(@"[^A-Za-z0-9_]", Options);

    private readonly HashSet<int> _directiveNameOffsets;

    private KeymapLayerText(
        string source,
        string masked,
        string newLine,
        IReadOnlyList<LayerBlock> layers,
        IReadOnlyList<LayerDefine> layerDefines,
        IReadOnlyList<LayerReference> references,
        HashSet<string> layerReferenceNames,
        IReadOnlyList<Directive> directives)
    {
        Source = source;
        Masked = masked;
        NewLine = newLine;
        Layers = layers;
        LayerDefines = layerDefines;
        References = references;
        LayerReferenceNames = layerReferenceNames;
        _directiveNameOffsets = directives.Select(d => d.NameOffset).ToHashSet();
    }

    /// <summary>The unmodified source text every offset in this class indexes.</summary>
    public string Source { get; }

    /// <summary><see cref="Source"/> with comment characters replaced by spaces
    /// (length, offsets and line numbers preserved).</summary>
    public string Masked { get; }

    /// <summary><c>"\r\n"</c> when the source contains CRLF line endings, else <c>"\n"</c>.</summary>
    public string NewLine { get; }

    /// <summary>The keymap's layer nodes, in layer order.</summary>
    public IReadOnlyList<LayerBlock> Layers { get; }

    /// <summary>The defines that name a layer, ordered by line.</summary>
    public IReadOnlyList<LayerDefine> LayerDefines { get; }

    /// <summary>Every numeric layer reference, in file order.</summary>
    public IReadOnlyList<LayerReference> References { get; }

    /// <summary>The define names this file uses where a layer number may stand
    /// (<c>&amp;mo NAME</c>, <c>layers = &lt;NAME&gt;</c>, …). Only such a define
    /// may be written back in place of a layer number.</summary>
    public IReadOnlySet<string> LayerReferenceNames { get; }

    /// <summary>
    /// Reads <paramref name="source"/> against the <paramref name="config"/>
    /// parsed from it (both must describe the same file: the config decides how
    /// many layer nodes the keymap must hold and which layer each define names).
    /// </summary>
    /// <exception cref="ZmkKeymapParseException">
    /// The source has no <c>zmk,keymap</c> node, or the layer nodes found in
    /// the text do not match <paramref name="config"/>.Layers one-for-one.
    /// </exception>
    public static KeymapLayerText Parse(string source, KeyboardConfig config)
    {
        var masked = DtsiText.MaskComments(source);

        var keymaps = DtsiText.FindNodesByCompatible(masked, KeymapCompatible);
        if (keymaps.Count == 0)
            throw new ZmkKeymapParseException("No 'zmk,keymap' node found in source.");

        var keymap = keymaps[0];
        var blocks = FindChildNodes(masked, keymap.BodyStart, keymap.BodyEnd);
        if (blocks.Count != config.Layers.Count)
            throw new ZmkKeymapParseException(
                $"keymap (line {DtsiText.LineOf(masked, keymap.BodyStart - 1)}): found {blocks.Count} layer nodes "
                + $"in the source, the parser found {config.Layers.Count} layers.");

        var directives = CollectDirectives(masked);

        var references = new List<LayerReference>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        ScanReferences(masked, config, blocks, references, usedNames);

        var defines = FindLayerDefines(masked, config, directives, usedNames);

        return new KeymapLayerText(
            source,
            masked,
            source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
            blocks,
            defines,
            references,
            usedNames,
            directives);
    }

    /// <summary>
    /// The replacements that renumber the file for
    /// <paramref name="indexMap"/> — one entry per old layer index, its new
    /// index or <c>null</c> when that layer is deleted (see
    /// <c>LayerReferenceRemapper.IndexMap</c>). Deleted layers get no change
    /// here: clearing their bindings is the delete writer's job. References and
    /// defines that already hold their new index, and numbers outside the map,
    /// are skipped. The result is disjoint and ordered by offset, ready for
    /// <see cref="KeymapWriter.ApplyChanges"/>.
    /// </summary>
    public IReadOnlyList<TextChange> Renumber(IReadOnlyList<int?> indexMap)
    {
        var changes = new List<TextChange>();

        foreach (var reference in References)
        {
            if (NewIndexOf(reference.Layer, indexMap) is int next && next != reference.Layer)
                changes.Add(new TextChange(reference.Offset, reference.Length, Describe(next)));
        }

        foreach (var define in LayerDefines)
        {
            if (NewIndexOf(define.LayerIndex, indexMap) is int next && next != define.LayerIndex)
                changes.Add(new TextChange(define.ValueOffset, define.ValueLength, Describe(next)));
        }

        return changes.OrderBy(c => c.Offset).ToList();
    }

    /// <summary>
    /// Offsets of whole-word occurrences of <paramref name="name"/> in
    /// <see cref="Masked"/>, minus the subject of a
    /// <c>#define/#ifdef/#ifndef/#undef</c> directive and minus anything inside
    /// a layer block — what is left is where a layer's define is used by
    /// something the layer writers cannot rewrite.
    /// </summary>
    public IReadOnlyList<int> FindNameUsages(string name)
    {
        var usages = new List<int>();
        if (name.Length == 0) return usages;

        int index = 0;
        while (true)
        {
            index = Masked.IndexOf(name, index, StringComparison.Ordinal);
            if (index < 0) return usages;

            if (IsWholeWord(index, name)
                && !InsideLayerBlock(index)
                && !_directiveNameOffsets.Contains(index))
                usages.Add(index);

            index++;
        }
    }

    /// <summary>
    /// The name of the innermost node holding <paramref name="offset"/> — for the
    /// delete pre-check to say <i>where</i> a reference it cannot rewrite sits
    /// (<c>adjust</c> for a tri-layer, <c>behaviors</c> for a hold-tap).
    /// <c>""</c> when the offset sits in the root <c>/ { }</c> node or outside any
    /// node. Best effort: braces are counted on <see cref="Masked"/>, so a
    /// <c>{</c> inside a string literal would miscount it, which no realistic
    /// <c>.keymap</c> contains.
    /// </summary>
    public string EnclosingNodeName(int offset)
    {
        if (offset <= 0 || offset > Masked.Length) return string.Empty;

        var names = new List<string>();
        for (int i = 0; i < offset; i++)
        {
            switch (Masked[i])
            {
                case '{':
                    names.Add(NameBeforeBrace(Masked, i));
                    break;
                case '}' when names.Count > 0:
                    names.RemoveAt(names.Count - 1);
                    break;
            }
        }

        return names.Count == 0 ? string.Empty : names[^1];
    }

    // ---- layer nodes ------------------------------------------------------------

    /// <summary>
    /// The direct child nodes of a body, in order: every <c>name {</c> at depth
    /// 0, brace-matched to its <c>}</c> and the <c>;</c> after it. An unclosed
    /// child is dropped, which makes the caller's layer-count check fail loudly.
    /// </summary>
    private static List<LayerBlock> FindChildNodes(string masked, int bodyStart, int bodyEnd)
    {
        var blocks = new List<LayerBlock>();
        int depth = 0;
        int headerStart = 0;
        string nodeName = string.Empty;
        string indent = string.Empty;

        for (int i = bodyStart; i < bodyEnd; i++)
        {
            char c = masked[i];

            if (c == '{')
            {
                if (depth == 0)
                {
                    nodeName = NameBeforeBrace(masked, i);
                    headerStart = LineStartOf(masked, i);
                    indent = LeadingWhitespace(masked, headerStart);
                }
                depth++;
            }
            else if (c == '}' && depth > 0)
            {
                depth--;
                if (depth == 0)
                    blocks.Add(new LayerBlock(headerStart, EndOfClosingLine(masked, i), nodeName, indent));
            }
        }

        return blocks;
    }

    /// <summary>Offset just past the newline ending the line that holds the
    /// closing <c>}</c> (and its <c>;</c>), or the text length when the file
    /// has no newline there.</summary>
    private static int EndOfClosingLine(string text, int closeBrace)
    {
        int p = closeBrace + 1;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
        int newline = text.IndexOf('\n', p);
        return newline < 0 ? text.Length : newline + 1;
    }

    // ---- layer defines ----------------------------------------------------------

    private readonly record struct Directive(
        string Keyword, string Name, int NameOffset, int LineStart, int LineEnd);

    private static List<Directive> CollectDirectives(string masked)
    {
        var directives = new List<Directive>();
        foreach (Match match in DirectiveRegex.Matches(masked))
        {
            int lineEnd = EndOfLine(masked, match.Index);
            directives.Add(new Directive(
                match.Groups[1].Value,
                match.Groups[2].Value,
                match.Groups[2].Index,
                match.Index,
                lineEnd));
        }
        return directives;
    }

    /// <summary>
    /// Every <c>#define NAME value</c> whose first definition assigns a value in
    /// the layer range <b>and</b> that the file treats as a layer name: either it
    /// uses NAME where a layer number may stand, or NAME is MoErgo's
    /// <c>LAYER_&lt;sanitised layer name&gt;</c> for that index. Anything else —
    /// Olaf's unused <c>#define BASE 0</c>, MoErgo's <c>KB_TYPE_GLOVE_80 1</c>,
    /// a <c>POS_*</c> that happens to be a small number — is left alone.
    /// </summary>
    private static List<LayerDefine> FindLayerDefines(
        string masked, KeyboardConfig config, IReadOnlyList<Directive> directives, HashSet<string> usedNames)
    {
        var defines = new List<LayerDefine>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var directive in directives)
        {
            if (!directive.Keyword.Equals("define", StringComparison.Ordinal)) continue;
            if (!seen.Add(directive.Name)) continue;

            int valueEnd = directive.LineEnd;
            int valueStart = directive.NameOffset + directive.Name.Length;
            var value = IntegerValueRegex.Match(masked[valueStart..valueEnd]);
            if (!value.Success) continue;

            string digits = value.Groups[1].Value;
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int index)) continue;
            if (index < 0 || index >= config.Layers.Count) continue;

            bool usedAsLayerReference = usedNames.Contains(directive.Name);
            if (!usedAsLayerReference && !IsNamedLikeLayer(config.Layers[index].Name, directive.Name)) continue;

            defines.Add(new LayerDefine(
                directive.Name,
                index,
                valueStart + value.Groups[1].Index,
                digits.Length,
                directive.LineStart,
                directive.LineEnd));
        }

        return defines;
    }

    /// <summary>Does <paramref name="defineName"/> read as MoErgo's name for
    /// <paramref name="layerName"/> (see <see cref="SanitizeName"/>)?</summary>
    private static bool IsNamedLikeLayer(string layerName, string defineName) =>
        string.Equals(defineName, "LAYER_" + SanitizeName(layerName), StringComparison.Ordinal);

    /// <summary>The name-key rule every layer name in this project follows: every
    /// character outside <c>[A-Za-z0-9_]</c> becomes an underscore (MoErgo's
    /// <c>LAYER_&lt;name&gt;</c> defines and node names alike are built this way).</summary>
    internal static string SanitizeName(string value) => SanitizerRegex.Replace(value, "_");

    // ---- references -------------------------------------------------------------

    private readonly record struct Token(int Offset, int Length, string Text, ReferenceSite Site);

    /// <summary>Maps an offset to the node that encloses it.</summary>
    private sealed class SiteMap
    {
        private readonly IReadOnlyList<LayerBlock> _layers;
        private readonly DtsiText.DtsiNode? _combos;
        private readonly DtsiText.DtsiNode? _conditional;

        public SiteMap(
            IReadOnlyList<LayerBlock> layers,
            DtsiText.DtsiNode? combos,
            DtsiText.DtsiNode? conditional)
        {
            _layers = layers;
            _combos = combos;
            _conditional = conditional;
        }

        public ReferenceSite SiteOf(int offset)
        {
            foreach (var layer in _layers)
                if (offset >= layer.Start && offset < layer.End) return ReferenceSite.LayerBlock;
            if (_combos is { } combos && offset >= combos.BodyStart && offset < combos.BodyEnd)
                return ReferenceSite.Combo;
            if (_conditional is { } conditional && offset >= conditional.BodyStart && offset < conditional.BodyEnd)
                return ReferenceSite.ConditionalLayer;
            return ReferenceSite.Other;
        }
    }

    /// <summary>
    /// Every layer-number-shaped token in the file: the first parameter of a
    /// layer behavior, of a hold-tap holding one, and of the
    /// <c>layers</c>/<c>if-layers</c>/<c>then-layer</c> arrays. Numbers become
    /// <paramref name="references"/>, identifiers become
    /// <paramref name="usedNames"/> — the names the file uses where a layer
    /// number may stand.
    /// </summary>
    private static void ScanReferences(
        string masked,
        KeyboardConfig config,
        IReadOnlyList<LayerBlock> blocks,
        List<LayerReference> references,
        HashSet<string> usedNames)
    {
        var combos = DtsiText.FindNodesByCompatible(masked, CombosCompatible);
        var conditional = DtsiText.FindNodesByCompatible(masked, ConditionalLayersCompatible);
        var sites = new SiteMap(
            blocks,
            combos.Count > 0 ? combos[0] : null,
            conditional.Count > 0 ? conditional[0] : null);

        var tokens = new List<Token>();

        foreach (Match match in BehaviorParameterRegex.Matches(masked))
            AddToken(tokens, masked, match.Groups[1], sites);

        foreach (var name in HoldTapLayerNames(config))
        {
            var regex = new Regex(
                @"&" + Regex.Escape(name) + @"(?![A-Za-z0-9_])[ \t\r\n]+(\d+|[A-Za-z_][A-Za-z0-9_]*)(?![A-Za-z0-9_])",
                Options);
            foreach (Match match in regex.Matches(masked))
                AddToken(tokens, masked, match.Groups[1], sites);
        }

        foreach (var regex in new[] { LayersArrayRegex, ConditionalLayerArrayRegex })
        {
            foreach (Match match in regex.Matches(masked))
            {
                int from = match.Groups[1].Index;
                var cells = masked[from..(from + match.Groups[1].Length)];
                foreach (Match cell in ArrayTokenRegex.Matches(cells))
                    AddToken(tokens, masked, cell.Index + from, cell.Length, cell.Value, sites);
            }
        }

        var taken = new HashSet<int>();
        foreach (var token in tokens.OrderBy(t => t.Offset))
        {
            if (!taken.Add(token.Offset)) continue;

            if (token.Text[0] is >= '0' and <= '9')
            {
                if (int.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int layer))
                    references.Add(new LayerReference(
                        token.Offset, token.Length, layer, token.Site, DtsiText.LineOf(masked, token.Offset)));
            }
            else
            {
                usedNames.Add(token.Text);
            }
        }
    }

    /// <summary>The bare names (no <c>&amp;</c>) of hold-taps whose hold side is
    /// a layer behavior — <c>&amp;magic 3 0</c> holds layer 3. The layer
    /// behaviors themselves are already covered by their own pattern.</summary>
    private static IEnumerable<string> HoldTapLayerNames(KeyboardConfig config)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var holdTap in config.HoldTaps)
        {
            if (!ZmkBindingFormatter.LayerBehaviors.Contains(holdTap.HoldBinding)) continue;
            string name = holdTap.Name.StartsWith('&')
                ? holdTap.Name[1..]
                : holdTap.Name;
            if (name.Length == 0 || ZmkBindingFormatter.LayerBehaviors.Contains("&" + name)) continue;
            if (emitted.Add(name)) yield return name;
        }
    }

    private static void AddToken(List<Token> tokens, string masked, Group token, SiteMap sites) =>
        AddToken(tokens, masked, token.Index, token.Length, token.Value, sites);

    private static void AddToken(
        List<Token> tokens, string masked, int offset, int length, string text, SiteMap sites)
    {
        if (offset > 0)
        {
            char before = masked[offset - 1];
            // '-1' in a combo scope means "all layers", and `foo3` is one word,
            // not a reference to 3. Behavior parameters never hit this: their
            // token is preceded by the whitespace their pattern demands.
            if (before == '-' || IsIdentifierChar(before)) return;
        }
        tokens.Add(new Token(offset, length, text, sites.SiteOf(offset)));
    }

    // ---- helpers ----------------------------------------------------------------

    private static int? NewIndexOf(int layer, IReadOnlyList<int?> indexMap) =>
        layer >= 0 && layer < indexMap.Count ? indexMap[layer] : null;

    private static string Describe(int index) => index.ToString(CultureInfo.InvariantCulture);

    private bool IsWholeWord(int index, string name)
    {
        if (index > 0 && IsIdentifierChar(Masked[index - 1])) return false;
        int after = index + name.Length;
        return after >= Masked.Length || !IsIdentifierChar(Masked[after]);
    }

    private bool InsideLayerBlock(int offset) =>
        Layers.Any(b => offset >= b.Start && offset < b.End);

    /// <summary>Offset of the first character of the line holding
    /// <paramref name="offset"/>.</summary>
    private static int LineStartOf(string text, int offset)
    {
        int newline = offset <= 0 ? -1 : text.LastIndexOf('\n', offset - 1);
        return newline + 1;
    }

    /// <summary>Offset just past the newline that ends the line holding
    /// <paramref name="offset"/>, or the text length when there is none.</summary>
    private static int EndOfLine(string text, int offset)
    {
        int newline = text.IndexOf('\n', Math.Min(offset, text.Length));
        return newline < 0 ? text.Length : newline + 1;
    }

    private static string LeadingWhitespace(string text, int lineStart)
    {
        int p = lineStart;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
        return text[lineStart..p];
    }

    /// <summary>The node name directly before a <c>{</c>, with any
    /// <c>label:</c> prefix left out.</summary>
    private static string NameBeforeBrace(string text, int braceIndex)
    {
        int i = braceIndex - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        int end = i + 1;
        while (i >= 0 && IsNodeNameChar(text[i])) i--;
        return text[(i + 1)..end];
    }

    private static bool IsIdentifierChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c == '_';

    private static bool IsNodeNameChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '@' or ',' or '+' or '-' or '.';
}
