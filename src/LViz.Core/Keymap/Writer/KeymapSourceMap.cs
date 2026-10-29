using LViz.Core.Keymap.Parser;
using LViz.Core.Models;

namespace LViz.Core.Keymap.Writer;

/// <summary>A character range inside the original source text: chars
/// <c>[Start, Start + Length)</c>.</summary>
public readonly record struct SourceSpan(int Start, int Length);

/// <summary>
/// Where one layer lives in the original <c>.keymap</c> text: the line and
/// offset of its <c>bindings</c> keyword, the value span of its
/// <c>display-name</c> (when present), and one <see cref="SourceSpan"/> per
/// parsed binding, running from the first character of the binding's first
/// word to the last character of its last word.
/// </summary>
public sealed record LayerSourceMap(
    int LayerIndex,
    int BindingsLine,                 // 1-based line of the `bindings` keyword
    int BindingsKeywordOffset,        // offset of the `b` of `bindings`
    SourceSpan? DisplayNameValue,     // chars between the quotes; null when the layer has no display-name
    IReadOnlyList<SourceSpan> Bindings);

/// <summary>
/// Locates the exact character span of every layer binding and of each
/// layer's <c>display-name</c> value in the <b>original</b> <c>.keymap</c>
/// text, so a writer can patch individual bindings without touching
/// anything else. Spans are only produced for layers that map one-to-one
/// onto the parsed <see cref="KeyboardConfig"/>; anything ambiguous (extra
/// arrays, function-like macro calls, directives inside the array, a slot
/// count or behavior that disagrees with the parser) is refused with a
/// <see cref="ZmkKeymapParseException"/> naming the layer and a 1-based line.
/// Pure: no I/O, input text is never modified.
/// </summary>
public static class KeymapSourceMap
{
    /// <summary>
    /// Builds the per-layer source map for <paramref name="source"/> against
    /// the <paramref name="config"/> parsed from it (both must describe the
    /// same file). The keymap/layer nodes are located exactly as
    /// <see cref="ZmkKeymapInterpreter"/> locates them.
    /// </summary>
    /// <exception cref="ZmkKeymapParseException">
    /// The source has no <c>zmk,keymap</c> node, its layer nodes do not
    /// match <paramref name="config"/>.Layers one-to-one, or any layer's
    /// bindings array cannot be mapped exactly.
    /// </exception>
    public static IReadOnlyList<LayerSourceMap> Build(string source, KeyboardConfig config)
    {
        var preprocessed = Preprocessor.Process(source);
        var tokens = DeviceTreeLexer.Tokenize(preprocessed);
        var ast = DeviceTreeParser.Parse(tokens);

        var keymapNode = FlattenNodes(ast)
            .FirstOrDefault(n => GetCompatible(n) == "zmk,keymap");
        if (keymapNode is null)
            throw new ZmkKeymapParseException("No 'zmk,keymap' node found in source.");

        var layerNodes = keymapNode.Children;
        if (layerNodes.Count != config.Layers.Count)
            throw new ZmkKeymapParseException(
                $"keymap (line {keymapNode.Line}): found {layerNodes.Count} layer nodes "
                + $"in the source, the parser found {config.Layers.Count} layers.");

        var text = new SourceText(source);

        var defines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var define in Preprocessor.CollectDefines(source))
            defines[define.Key] = define.Value;

        var maps = new List<LayerSourceMap>(layerNodes.Count);
        for (int i = 0; i < layerNodes.Count; i++)
            maps.Add(MapLayer(text, defines, layerNodes[i], config.Layers[i], i));
        return maps;
    }

    // ---- per-layer mapping -------------------------------------------------

    private static LayerSourceMap MapLayer(
        SourceText text,
        Dictionary<string, string> defines,
        DtNode layerNode,
        Layer layer,
        int layerIndex)
    {
        var name = layer.Name;

        SourceSpan? displayName = null;
        var displayNameProp = FirstProperty(layerNode, "display-name");
        if (displayNameProp is not null)
            displayName = MapDisplayNameValue(text, name, displayNameProp.Line);

        var bindingsProp = FirstProperty(layerNode, "bindings");
        if (bindingsProp is null)
            throw Fail(name, layerNode.Line, "has no 'bindings' property");

        int keywordOffset = FindKeywordOnLine(text, name, bindingsProp.Line, "bindings");
        int bindingsLine = text.OffsetToLine(keywordOffset);

        int pos = text.SkipWhitespace(keywordOffset + "bindings".Length);
        pos = Expect(text, name, pos, '=', "expected '=' after the 'bindings' keyword");
        pos = text.SkipWhitespace(pos + 1);
        pos = Expect(text, name, pos, '<', "expected '<' to open the bindings array");
        int open = pos;

        int close = FindArrayEnd(text, name, open);

        EnsureNoDirectiveLines(text, name, open, close);

        int after = text.SkipWhitespace(close + 1);
        if (after >= text.Length || text.Mask[after] != ';')
            throw Fail(name, text.OffsetToLine(Math.Min(after, text.Length - 1)),
                "the bindings array must end with a single '>' followed by ';' "
                + "(multiple '<…>, <…>' groups are not supported)");

        var words = SplitWords(text, open + 1, close);
        var slots = GroupSlots(text, defines, name, words);

        if (slots.Count != layer.Bindings.Count)
            throw Fail(name, bindingsLine,
                $"found {slots.Count} bindings in the source, the parser found {layer.Bindings.Count}");

        var bindings = new List<SourceSpan>(slots.Count);
        for (int k = 0; k < slots.Count; k++)
        {
            var slot = slots[k];
            var want = layer.Bindings[k].Behavior;
            if (!string.Equals(slot.Behavior, want, StringComparison.Ordinal))
                throw Fail(name, text.OffsetToLine(slot.Start),
                    $"binding {k + 1} is '{slot.Behavior}' in the source, the parser found '{want}'");
            bindings.Add(new SourceSpan(slot.Start, slot.End - slot.Start));
        }

        return new LayerSourceMap(layerIndex, bindingsLine, keywordOffset, displayName, bindings);
    }

    private static SourceSpan MapDisplayNameValue(SourceText text, string layerName, int line)
    {
        const string word = "display-name";
        int keyword = FindKeywordOnLine(text, layerName, line, word);
        int pos = text.SkipWhitespace(keyword + word.Length);
        pos = Expect(text, layerName, pos, '=', "expected '=' after the 'display-name' keyword");
        pos = text.SkipWhitespace(pos + 1);
        pos = Expect(text, layerName, pos, '"', "expected a quoted string as the 'display-name' value");

        // Scan to the closing quote, honouring backslash escapes (including \").
        int start = pos + 1;
        int j = start;
        while (j < text.Length)
        {
            char c = text.Source[j];
            if (c == '\\' && j + 1 < text.Length) { j += 2; continue; }
            if (c == '"')
                return new SourceSpan(start, j - start);
            j++;
        }
        throw Fail(layerName, line, "the 'display-name' string is not terminated");
    }

    // ---- structural scanning (all on the comment-stripped mask) ------------

    /// <summary>First word-bounded occurrence of <paramref name="word"/> on
    /// the 1-based <paramref name="line"/>, skipping string literals.</summary>
    private static int FindKeywordOnLine(SourceText text, string layerName, int line, string word)
    {
        if (line < 1 || line > text.LineCount)
            throw Fail(layerName, Math.Max(1, line),
                $"line {line} is outside the file");
        int from = text.LineStart(line);
        int to = text.LineEnd(line);

        int idx = from;
        while (true)
        {
            idx = text.Mask.IndexOf(word, idx, StringComparison.Ordinal);
            if (idx < 0 || idx + word.Length > to) break;
            bool inString = text.InString[idx] || text.InString[idx + word.Length - 1];
            bool boundaryBefore = idx == 0 || !IsWordChar(text.Mask[idx - 1]);
            bool boundaryAfter = idx + word.Length >= text.Length || !IsWordChar(text.Mask[idx + word.Length]);
            if (!inString && boundaryBefore && boundaryAfter)
                return idx;
            idx++;
        }
        throw Fail(layerName, line, $"could not locate the '{word}' keyword on line {line}");
    }

    private static int FindArrayEnd(SourceText text, string layerName, int open)
    {
        for (int j = open + 1; j < text.Length; j++)
        {
            if (text.InString[j]) { j = text.StringEnd[j] - 1; continue; }
            if (text.Mask[j] == '>') return j;
        }
        throw Fail(layerName, text.OffsetToLine(open), "the bindings array '<…>' is not closed");
    }

    /// <summary>
    /// Refuses any line inside the array whose first non-whitespace mask
    /// character is <c>#</c> — a preprocessor directive there means the
    /// parser saw a different binding sequence than the raw text holds.
    /// </summary>
    private static void EnsureNoDirectiveLines(SourceText text, string layerName, int open, int close)
    {
        int first = text.OffsetToLine(open);
        int last = text.OffsetToLine(close);
        for (int line = first; line <= last; line++)
        {
            int start = text.LineStart(line);
            int end = text.LineEnd(line);
            int p = start;
            while (p < end && char.IsWhiteSpace(text.Mask[p])) p++;
            if (p > open && p < close && p < end && text.Mask[p] == '#')
                throw Fail(layerName, line,
                    "the bindings array contains a preprocessor line (first character '#') — "
                    + "the text and the parsed bindings can no longer be matched one-to-one");
        }
    }

    private readonly record struct Word(int Start, int End);

    private sealed class Slot
    {
        public int Start;
        public int End;
        public required string Behavior;
    }

    /// <summary>
    /// Splits the array content into words at mask whitespace, keeping
    /// <c>(…)</c> groups (e.g. <c>LS( N7 )</c>) inside one word.
    /// </summary>
    private static List<Word> SplitWords(SourceText text, int from, int toExclusive)
    {
        var words = new List<Word>();
        int pos = from;
        while (pos < toExclusive)
        {
            pos = text.SkipWhitespace(pos);
            if (pos >= toExclusive) break;
            int start = pos;
            int depth = 0;
            while (pos < toExclusive)
            {
                if (text.InString[pos]) { pos = text.StringEnd[pos]; continue; }
                char c = text.Mask[pos];
                if (c == '(') depth++;
                else if (c == ')') { if (depth > 0) depth--; }
                else if (char.IsWhiteSpace(c) && depth == 0) break;
                pos++;
            }
            words.Add(new Word(start, pos));
        }
        return words;
    }

    /// <summary>
    /// Groups words into binding slots. A slot starts at a word beginning
    /// with <c>&amp;</c> or at an object-like define whose expanded value
    /// starts with <c>&amp;</c>; every other word extends the current slot.
    /// </summary>
    private static List<Slot> GroupSlots(
        SourceText text, Dictionary<string, string> defines, string layerName, List<Word> words)
    {
        var slots = new List<Slot>();
        foreach (var word in words)
        {
            var wordText = text.Source[word.Start..word.End];
            var behavior = SlotBehavior(wordText, defines);
            if (behavior is not null)
            {
                slots.Add(new Slot { Start = word.Start, End = word.End, Behavior = behavior });
            }
            else if (slots.Count == 0)
            {
                throw Fail(layerName, text.OffsetToLine(word.Start),
                    $"'{wordText}' appears before the first binding of the array");
            }
            else
            {
                slots[^1].End = word.End;
            }
        }
        return slots;
    }

    /// <summary>
    /// The behavior a word opens: the <c>&amp;word</c> up to a
    /// <c>(</c>, or the first token of the expanded object-like define
    /// (define-to-define chains followed up to 8 levels) when that starts
    /// with <c>&amp;</c>. <c>null</c> means the word is a parameter of the
    /// current slot, not a slot start.
    /// </summary>
    private static string? SlotBehavior(string word, Dictionary<string, string> defines)
    {
        if (word.Length == 0) return null;
        if (word[0] == '&') return CutAtParen(word);
        if (!defines.TryGetValue(word, out var value)) return null;

        for (int hop = 0; hop < 8; hop++)
        {
            var token = FirstToken(value);
            if (token.Length == 0 || token[0] == '&') break;
            if (!defines.TryGetValue(token, out var next)) break;
            value = next;
        }
        var first = FirstToken(value);
        return first.Length > 0 && first[0] == '&' ? first : null;
    }

    private static string CutAtParen(string s)
    {
        int idx = s.IndexOf('(');
        return idx < 0 ? s : s[..idx];
    }

    private static string FirstToken(string value)
    {
        value = value.TrimStart();
        int end = 0;
        while (end < value.Length && !char.IsWhiteSpace(value[end]) && value[end] != '(') end++;
        return value[..end];
    }

    // ---- shared helpers -----------------------------------------------------

    private static Exception Fail(string layerName, int line, string what) =>
        new ZmkKeymapParseException($"layer '{layerName}' (line {line}): {what}");

    private static int Expect(SourceText text, string layerName, int pos, char c, string what)
    {
        if (pos >= text.Length || text.Mask[pos] != c)
            throw Fail(layerName, text.OffsetToLine(Math.Min(pos, text.Length - 1)), what);
        return pos;
    }

    private static DtProperty? FirstProperty(DtNode node, string name)
        => node.Properties.FirstOrDefault(p => p.Name == name);

    private static string? GetCompatible(DtNode node)
        => FirstProperty(node, "compatible")?.Value is DtStringList sl && sl.Values.Count > 0
            ? sl.Values[0]
            : null;

    private static IEnumerable<DtNode> FlattenNodes(DtRoot root)
    {
        foreach (var child in root.Children)
            foreach (var n in FlattenNode(child))
                yield return n;
    }

    private static IEnumerable<DtNode> FlattenNode(DtNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var n in FlattenNode(child))
                yield return n;
    }

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.';

    /// <summary>
    /// The original source plus a same-length mask with comment characters
    /// replaced by spaces (newlines kept) and string literals copied
    /// verbatim. <see cref="InString"/> marks every character belonging to a
    /// string literal (quotes included); <see cref="StringEnd"/> gives the
    /// offset just past its closing quote. Comment detection mirrors
    /// <c>Preprocessor.StripComments</c> position-for-position.
    /// </summary>
    private sealed class SourceText
    {
        public string Source { get; }
        public string Mask { get; }
        public bool[] InString { get; }
        public int[] StringEnd { get; }
        public int Length => Source.Length;

        private readonly int[] _lineStarts;
        public int LineCount => _lineStarts.Length;

        public SourceText(string source)
        {
            Source = source;
            int n = source.Length;
            var mask = new char[n];
            InString = new bool[n];
            StringEnd = new int[n];
            Array.Fill(mask, ' ');

            int i = 0;
            while (i < n)
            {
                char c = source[i];

                if (c == '"')
                {
                    int start = i;
                    int j = i + 1;
                    while (j < n)
                    {
                        char s = source[j];
                        if (s == '\\' && j + 1 < n) { j += 2; continue; }
                        j++;
                        if (s == '"') break;
                    }
                    int last = Math.Min(j - 1, n - 1); // after loop j is past the quote (or n)
                    for (int k = start; k <= last; k++)
                    {
                        mask[k] = source[k];
                        InString[k] = true;
                        StringEnd[k] = last + 1;
                    }
                    i = last + 1;
                    continue;
                }

                if (c == '/' && i + 1 < n && source[i + 1] == '/')
                {
                    int j = i + 2;
                    while (j < n && source[j] != '\n') j++;
                    for (int k = i; k < j; k++) mask[k] = ' ';
                    i = j;
                    continue;
                }

                if (c == '/' && i + 1 < n && source[i + 1] == '*')
                {
                    int j = i + 2;
                    while (j + 1 < n && !(source[j] == '*' && source[j + 1] == '/')) j++;
                    int end = j + 1 < n ? j + 2 : n;
                    for (int k = i; k < end; k++) mask[k] = source[k] == '\n' ? '\n' : ' ';
                    i = end;
                    continue;
                }

                mask[i] = c;
                i++;
            }

            Mask = new string(mask);

            var lineStarts = new List<int> { 0 };
            for (int p = 0; p < n; p++)
                if (source[p] == '\n') lineStarts.Add(p + 1); // '\r' before it stays in the previous line
            _lineStarts = lineStarts.ToArray();
        }

        public int LineStart(int line) => _lineStarts[line - 1];

        /// <summary>Exclusive end offset of a 1-based line (the index of its
        /// newline, or EOF for the last line).</summary>
        public int LineEnd(int line) => line < LineCount ? _lineStarts[line] - 1 : Length;

        public int OffsetToLine(int offset)
        {
            int lo = 0, hi = LineCount - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_lineStarts[mid] <= offset) lo = mid;
                else hi = mid - 1;
            }
            return lo + 1;
        }

        public int SkipWhitespace(int i)
        {
            while (i < Length && char.IsWhiteSpace(Mask[i])) i++;
            return i;
        }
    }
}
