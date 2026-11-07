using System.Text;
using System.Text.RegularExpressions;
using LViz.Core.Keymap.Parser;
using LViz.Core.Layout.Dtsi;
using LViz.Core.Models;

namespace LViz.Core.Keymap.Writer;

/// <summary>One combo node: header line, the span of its name (a
/// <c>label:</c> prefix sits outside it), its body and its closing brace.</summary>
internal readonly record struct ComboNode(
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
internal readonly record struct Statement(int NameStart, int SpanEnd, bool OwnLines);

/// <summary>The combos node, its combo children and the define tables their
/// values are spelled with, read once per edit.</summary>
internal sealed class KeymapComboText
{
    internal const string CombosCompatible = "zmk,combos";

    /// <summary>The node a newly created <c>combos</c> node is placed before.</summary>
    internal const string KeymapCompatible = "zmk,keymap";

    /// <summary>Every property a combo node may hold, in the order a new node
    /// writes them (the order the ZMK docs list them in). <c>key-positions</c>
    /// and <c>bindings</c> always carry a value; the other four disappear when
    /// set back to their default.</summary>
    internal static readonly IReadOnlyList<string> ComboProperties =
        ["timeout-ms", "key-positions", "layers", "bindings", "require-prior-idle-ms", "slow-release"];

    /// <summary>The text of a new combo node — header line, one line per
    /// non-default property, closing <c>};</c> — every line ending in
    /// <paramref name="newLine"/>, so the returned text ends with one too.</summary>
    internal static string NewNode(
        string childIndent, string propIndent, string nodeName, IReadOnlyList<string> statements, string newLine)
    {
        var sb = new StringBuilder();
        sb.Append(childIndent).Append(nodeName).Append(" {").Append(newLine);
        foreach (var statement in statements)
            sb.Append(propIndent).Append(statement).Append(';').Append(newLine);
        sb.Append(childIndent).Append("};").Append(newLine);
        return sb.ToString();
    }

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

    private readonly KeyboardConfig _config;
    private readonly HashSet<string> _positionPrefixes;

    private KeymapComboText(
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
        Combos is { } node ? DtsiText.LeadingWhitespace(Masked, DtsiText.LineStartOf(Masked, node.BodyStart - 1)) : string.Empty;

    /// <summary>The indent a new child node gets: the first existing child's,
    /// or the combos node's own indent plus four spaces.</summary>
    public string NewChildIndent => Nodes.Count > 0 ? Nodes[0].Indent : CombosIndent + "    ";

    /// <summary>The indent a property line of a new child gets: the first
    /// existing child's first property indent, or <see cref="NewChildIndent"/>
    /// plus four spaces.</summary>
    public string NewChildPropertyIndent =>
        Nodes.Count > 0 ? PropertyIndent(Nodes[0]) : NewChildIndent + "    ";

    public static KeymapComboText Parse(string source, KeyboardConfig config)
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
            ? DtsiText.FindChildNodes(masked, node.BodyStart, node.BodyEnd)
                .Select(n => new ComboNode(
                    n.Name, n.NameStart, n.NameEnd, n.OpenBrace + 1, n.CloseBrace, n.Labelled, n.Indent))
                .ToList()
            : new List<ComboNode>();
        var keymap = DtsiText.FindNodesByCompatible(masked, KeymapCompatible);

        return new KeymapComboText(
            source,
            masked,
            source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
            combos,
            nodes,
            keymap.Count > 0 ? DtsiText.LineStartOf(masked, keymap[0].BodyStart - 1) : -1,
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
            int start = DtsiText.LineStartOf(Masked, statement.NameStart);
            int end = DtsiText.EndOfLine(Masked, statement.SpanEnd - 1);
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
        int closingLineStart = DtsiText.LineStartOf(Masked, node.CloseBrace);
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
            if (Masked[i..lineEnd].Trim().Length > 0) return DtsiText.LeadingWhitespace(Masked, DtsiText.LineStartOf(Masked, i));
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
        "layers" => ZmkCombo.IsAllLayers(combo.Layers)
            ? null
            : "layers = <" + string.Join(' ', combo.Layers.Select(FormatLayer)) + ">",
        "bindings" => "bindings = <" + ZmkBindingFormatter.Format(combo.Binding, LayerDefines, _config.HoldTaps) + ">",
        "timeout-ms" => combo.TimeoutMs is int timeout
            ? "timeout-ms = <" + WriterText.Num(timeout) + ">"
            : null,
        "require-prior-idle-ms" => combo.RequirePriorIdleMs is int idle
            ? "require-prior-idle-ms = <" + WriterText.Num(idle) + ">"
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
        if (_positionPrefixes.Count == 0) return WriterText.Num(position);

        string value = WriterText.Num(position);
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
        string value = WriterText.Num(layer);
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

        if (Nodes.Count > 0) return (DtsiText.EndOfLine(Masked, Nodes[^1].CloseBrace), 0, NewLine + node);

        int slot = combos.BodyStart;
        for (int i = combos.BodyStart; i < combos.BodyEnd;)
        {
            int lineEnd = DtsiText.EndOfLine(Source, i);
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
        int start = DtsiText.LineStartOf(Masked, offset - 1);
        return DtsiText.LeadingWhitespace(Masked, start).Length == offset - start;
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
        int start = DtsiText.LineStartOf(Masked, node.NameStart);
        int end = DtsiText.EndOfLine(Masked, node.CloseBrace);
        int before = start > 0 ? DtsiText.LineStartOf(Masked, start - 1) : -1;
        bool blankBefore = before >= 0 && IsBlank(before, start);

        if (end >= Source.Length) return (start, string.Empty, end - start);

        int afterEnd = DtsiText.EndOfLine(Source, end);
        if (blankBefore && IsBlank(end, afterEnd))
            return (start, string.Empty, afterEnd - start);

        if (blankBefore && Combos is { } combos && end <= combos.BodyEnd && combos.BodyEnd < afterEnd)
            return (before, string.Empty, end - before);

        return (start, string.Empty, end - start);
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
        for (int i = DtsiText.LineStartOf(Masked, nameStart); i < nameStart; i++)
            if (!char.IsWhiteSpace(Masked[i])) return false;

        for (int i = semicolon + 1, end = DtsiText.EndOfLine(Masked, semicolon); i < end; i++)
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
}
