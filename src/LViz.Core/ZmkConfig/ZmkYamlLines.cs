namespace LViz.Core.ZmkConfig;

/// <summary>
/// Shared line helpers for the zmk-config text readers. The readers keep the
/// exact line array they were built from, so later writers can patch lines in
/// place; that is also why <see cref="Split"/> splits on '\n' only and lets a
/// line keep its trailing '\r'.
/// </summary>
internal static class ZmkYamlLines
{
    /// <summary>Splits on '\n' only; in a CRLF file every line keeps its trailing '\r'.</summary>
    public static string[] Split(string text) => text.Split('\n');

    /// <summary>Count of leading spaces of a line.</summary>
    public static int Indent(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] == ' ')
            i++;
        return i;
    }

    /// <summary>True for a line that has text and is not a comment.</summary>
    public static bool IsContent(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0 && !trimmed.StartsWith('#');
    }

    /// <summary>
    /// Cuts the line at the first '#' that is preceded by whitespace and trims
    /// the end (which also drops a '\r'). A '#' that starts the line or sits
    /// inside a value (a URL fragment) is kept.
    /// </summary>
    public static string StripComment(string line)
    {
        for (var i = 1; i < line.Length; i++)
        {
            if (line[i] == '#' && char.IsWhiteSpace(line[i - 1]))
                return line[..i].TrimEnd();
        }
        return line.TrimEnd();
    }

    /// <summary>Trims, then removes one pair of matching surrounding quotes.</summary>
    public static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[^1] == value[0])
            return value[1..^1];
        return value;
    }

    /// <summary>"\r\n" when the text contains it, else "\n".</summary>
    public static string Newline(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}

/// <summary>
/// One block-style YAML list item, shared by the west manifest and the build
/// matrix readers. All line numbers are 0-based indexes into the split text.
/// <see cref="EndLine"/> is the item's last content line: comment or blank
/// lines after it belong to neither the item nor the file's structure.
/// </summary>
public sealed record ZmkYamlItem(
    int StartLine,
    int EndLine,
    int DashIndent,
    int KeyIndent,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, int> KeyLines);

internal sealed class PendingItem(int startLine, int dashIndent)
{
    public int StartLine { get; } = startLine;

    public int DashIndent { get; } = dashIndent;

    public int EndLine { get; set; } = startLine;

    /// <summary>Key lines in order: key, value, 0-based line, key indent.</summary>
    public List<(string Key, string Value, int Line, int Indent)> Keys { get; } = [];

    public ZmkYamlItem ToItem()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var keyLines = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, value, line, _) in Keys)
        {
            values[key] = value;
            keyLines[key] = line;
        }
        var keyIndent = Keys.Count > 1 ? Keys[1].Indent : DashIndent + 2;
        return new ZmkYamlItem(StartLine, EndLine, DashIndent, keyIndent, values, keyLines);
    }
}

/// <summary>
/// The list parser both YAML readers share. Only block style is supported;
/// flow style and lines that are neither a dash nor <c>key: value</c> are
/// refused with their line.
/// </summary>
internal static class ZmkYamlList
{
    /// <summary>
    /// Parses the <c>keyIndent</c>-indented list that starts below
    /// <paramref name="keyLine"/>. The first <c>- </c> content line sets the
    /// item indent; content deeper than the dash belongs to the current item;
    /// a content line at or left of <paramref name="keyIndent"/> ends the list.
    /// </summary>
    public static List<ZmkYamlItem> ParseItems(string[] lines, int keyLine, int keyIndent, string file)
    {
        var items = new List<ZmkYamlItem>();
        PendingItem? item = null;
        var itemIndent = -1;

        for (var i = keyLine + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!ZmkYamlLines.IsContent(line))
                continue;
            var indent = ZmkYamlLines.Indent(line);
            if (indent <= keyIndent)
                break;

            var content = ZmkYamlLines.StripComment(line).Trim();
            var isDash = content == "-" || content.StartsWith("- ", StringComparison.Ordinal);
            if (isDash && (itemIndent < 0 || indent == itemIndent))
            {
                if (item is not null)
                    items.Add(item.ToItem());
                item = new PendingItem(i, indent);
                itemIndent = indent;
                AddKey(item, content == "-" ? "" : content[2..], file, i, indent + 2);
                continue;
            }

            if (item is null)
                throw new ZmkConfigFormatException(file, i + 1, "expected a list item");

            AddKey(item, content, file, i, indent);
        }

        if (item is not null)
            items.Add(item.ToItem());
        return items;
    }

    /// <summary>Splits a stripped line into key and unquoted value.</summary>
    public static (string Key, string Value) ParsePair(string content, string file, int zeroBasedLine)
    {
        ThrowIfFlow(content, file, zeroBasedLine);
        var colon = content.IndexOf(':');
        if (colon < 0)
            throw new ZmkConfigFormatException(file, zeroBasedLine + 1, "expected 'key: value'");
        var value = content[(colon + 1)..].Trim();
        ThrowIfFlow(value, file, zeroBasedLine);
        return (content[..colon].Trim(), ZmkYamlLines.Unquote(value));
    }

    /// <summary>Refuses a flow-style value (`[...]` / `{...}`) on its line.</summary>
    public static void ThrowIfFlow(string text, string file, int zeroBasedLine)
    {
        if (text.Length > 0 && (text[0] == '[' || text[0] == '{'))
            throw new ZmkConfigFormatException(file, zeroBasedLine + 1, "flow style is not supported");
    }

    private static void AddKey(PendingItem item, string content, string file, int zeroBasedLine, int keyIndent)
    {
        var (key, value) = ParsePair(content, file, zeroBasedLine);
        item.Keys.Add((key, value, zeroBasedLine, keyIndent));
        item.EndLine = zeroBasedLine;
    }
}
