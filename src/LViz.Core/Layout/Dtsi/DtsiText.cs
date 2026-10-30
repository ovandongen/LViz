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

    // The identifier directly before the '{' at braceIndex. Walking backwards
    // stops at whitespace or ':', so a leading "label:" is never part of the
    // returned name. Empty string if the '{' has no name (root node "/ {").
    private static string NameBeforeBrace(string text, int braceIndex)
    {
        int i = braceIndex - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        int end = i + 1;
        while (i >= 0 && IsNameChar(text[i])) i--;
        return text[(i + 1)..end];
    }

    private static bool IsNameChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '@' or ',' or '+' or '-' or '.';
}
