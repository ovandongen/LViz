using System.Text;
using System.Text.RegularExpressions;

namespace LViz.Core.Layout.Dtsi;

/// <summary>
/// Shared text-level helpers for the dtsi-based importers: comment masking,
/// node lookup by <c>compatible</c>, and 1-based line reporting.
/// Masking replaces comment characters with spaces and keeps every newline,
/// so a masked string has the same length, offsets and line numbers as the
/// input — later importers (matrix transform) rely on that invariant.
/// </summary>
internal static class DtsiText
{
    /// <summary>
    /// A devicetree node found inside masked text. Body offsets index the
    /// same string that <see cref="FindNodesByCompatible"/> was given.
    /// </summary>
    /// <param name="Name">Node name with any <c>label:</c> prefix stripped
    /// (<c>physical_layout0: physical_layout_0 {</c> → <c>physical_layout_0</c>).</param>
    /// <param name="BodyStart">Offset just after the node's <c>{</c>.</param>
    /// <param name="BodyEnd">Offset of the node's matching <c>}</c>
    /// (exclusive end of <c>text[BodyStart..BodyEnd]</c>), or the end of the
    /// text if the brace is never closed.</param>
    public readonly record struct DtsiNode(string Name, int BodyStart, int BodyEnd);

    /// <summary>
    /// A direct child of a node body found by <see cref="FindChildNodes"/>.
    /// All offsets index the masked text that was scanned.
    /// </summary>
    /// <param name="Name">Node name, any <c>label:</c> prefix left out.</param>
    /// <param name="NameStart">Offset of the first character of <paramref name="Name"/>.</param>
    /// <param name="NameEnd">Offset just past <paramref name="Name"/>.</param>
    /// <param name="Labelled">True when a <c>label:</c> sits before the name on its header line.</param>
    /// <param name="HeaderLineStart">Start of the line holding the <c>{</c>.</param>
    /// <param name="Indent">Leading whitespace of that line.</param>
    /// <param name="OpenBrace">Offset of the node's <c>{</c>.</param>
    /// <param name="CloseBrace">Offset of the matching <c>}</c>.</param>
    public readonly record struct DtsiChildNode(
        string Name, int NameStart, int NameEnd, bool Labelled,
        int HeaderLineStart, string Indent, int OpenBrace, int CloseBrace);

    /// <summary>
    /// Replaces <c>// …</c> end-of-line comments and <c>/* … */</c> block
    /// comments with spaces. Newlines are kept, so the result has the same
    /// length, the same newline positions, and therefore the same line
    /// numbers as the input. Text inside <c>"…"</c> strings is copied
    /// verbatim — a <c>//</c> in a string is data, not a comment.
    /// (Devicetree strings have no escape syntax to honour here.)
    /// </summary>
    public static string MaskComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '"')
            {
                sb.Append(c);
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    sb.Append(text[i]);
                    i++;
                }
                if (i < text.Length) sb.Append('"');
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    sb.Append(' ');
                    i++;
                }
                i--; // land on the newline so the outer loop copies it
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                while (i < text.Length &&
                       !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < text.Length) // consume the closing "*/" as two spaces
                {
                    sb.Append("  ");
                    i += 2;
                }
                i--;
                continue;
            }

            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Every node whose properties contain <c>compatible = "&lt;compatible&gt;"</c>
    /// (whitespace around <c>=</c> tolerated), as the enclosing node: the
    /// nearest <c>{</c> that is still open at that point. Nodes in file order;
    /// a match outside any node is skipped, as is a repeat inside one node.
    /// Takes masked text so commented-out properties are invisible.
    /// </summary>
    public static IReadOnlyList<DtsiNode> FindNodesByCompatible(string masked, string compatible)
    {
        var regex = new Regex(
            @"(?<![A-Za-z0-9_])compatible\s*=\s*""" + Regex.Escape(compatible) + @"""");
        var matches = regex.Matches(masked);
        if (matches.Count == 0)
            return [];

        var openBraces = new Stack<int>();
        var order = new List<int>();          // node-open offsets, file order
        var seen = new HashSet<int>();
        var closeOf = new Dictionary<int, int>();

        int next = 0;
        for (int i = 0; i < masked.Length; i++)
        {
            if (next < matches.Count && i == matches[next].Index)
            {
                if (openBraces.Count > 0 && seen.Add(openBraces.Peek()))
                    order.Add(openBraces.Peek());
                next++;
            }

            char c = masked[i];
            if (c == '{')
            {
                openBraces.Push(i);
            }
            else if (c == '}' && openBraces.Count > 0)
            {
                closeOf[openBraces.Pop()] = i;
            }
        }

        var nodes = new List<DtsiNode>(order.Count);
        foreach (int open in order)
        {
            int close = closeOf.TryGetValue(open, out int end) ? end : masked.Length;
            nodes.Add(new DtsiNode(NameBeforeBrace(masked, open), open + 1, close));
        }
        return nodes;
    }

    /// <summary>1-based line number of <paramref name="offset"/>.
    /// Offsets outside the text clamp to the first/last line.</summary>
    public static int LineOf(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int line = 1;
        for (int i = 0; i < offset; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    /// <summary>
    /// The direct child nodes of the body <c>[bodyStart, bodyEnd)</c> of masked
    /// text, in order: every <c>[label:] name {</c> at depth 0, brace-matched to
    /// its <c>}</c>. An unclosed child is dropped, which makes a caller's
    /// count check fail loudly.
    /// </summary>
    public static List<DtsiChildNode> FindChildNodes(string masked, int bodyStart, int bodyEnd)
    {
        var nodes = new List<DtsiChildNode>();
        int depth = 0;
        int open = 0;
        int nameStart = 0;
        int nameEnd = 0;
        string name = string.Empty;
        bool labelled = false;
        int headerStart = 0;
        string indent = string.Empty;

        for (int i = bodyStart; i < bodyEnd; i++)
        {
            char c = masked[i];

            if (c == '{')
            {
                if (depth == 0)
                {
                    open = i;
                    nameEnd = i;
                    while (nameEnd > 0 && char.IsWhiteSpace(masked[nameEnd - 1])) nameEnd--;
                    nameStart = nameEnd;
                    while (nameStart > 0 && IsNodeNameChar(masked[nameStart - 1])) nameStart--;
                    name = masked[nameStart..nameEnd];
                    labelled = HasLabelPrefix(masked, nameStart);
                    headerStart = LineStartOf(masked, i);
                    indent = LeadingWhitespace(masked, headerStart);
                }

                depth++;
            }
            else if (c == '}' && depth > 0)
            {
                depth--;
                if (depth == 0)
                    nodes.Add(new DtsiChildNode(
                        name, nameStart, nameEnd, labelled, headerStart, indent, open, i));
            }
        }

        return nodes;
    }

    // Does a "label:" sit before the node name? Only spaces and tabs separate
    // the two, so the walk never leaves the header line.
    private static bool HasLabelPrefix(string masked, int nameStart)
    {
        int p = nameStart - 1;
        while (p >= 0 && (masked[p] == ' ' || masked[p] == '\t')) p--;
        return p >= 0 && masked[p] == ':';
    }

    /// <summary>Offset of the first character of the line holding
    /// <paramref name="offset"/>.</summary>
    public static int LineStartOf(string text, int offset)
    {
        int newline = offset <= 0 ? -1 : text.LastIndexOf('\n', offset - 1);
        return newline + 1;
    }

    /// <summary>Offset just past the newline that ends the line holding
    /// <paramref name="offset"/>, or the text length when there is none.</summary>
    public static int EndOfLine(string text, int offset)
    {
        int newline = text.IndexOf('\n', Math.Min(offset, text.Length));
        return newline < 0 ? text.Length : newline + 1;
    }

    /// <summary>The spaces and tabs at the start of the line beginning at
    /// <paramref name="lineStart"/>.</summary>
    public static string LeadingWhitespace(string text, int lineStart)
    {
        int p = lineStart;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
        return text[lineStart..p];
    }

    /// <summary>Offset just past the newline ending the line that holds the
    /// closing <c>}</c> (and its <c>;</c>), or the text length when the file
    /// has no newline there.</summary>
    public static int EndOfClosingLine(string text, int closeBrace)
    {
        int p = closeBrace + 1;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
        int newline = text.IndexOf('\n', p);
        return newline < 0 ? text.Length : newline + 1;
    }

    /// <summary>The node name directly before the <c>{</c> at
    /// <paramref name="braceIndex"/>, with any <c>label:</c> prefix left out.
    /// Walking backwards stops at whitespace or ':'. Empty string if the '{'
    /// has no name (root node <c>/ {</c>).</summary>
    public static string NameBeforeBrace(string text, int braceIndex)
    {
        int i = braceIndex - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        int end = i + 1;
        while (i >= 0 && IsNodeNameChar(text[i])) i--;
        return text[(i + 1)..end];
    }

    public static bool IsNodeNameChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '@' or ',' or '+' or '-' or '.';
}
