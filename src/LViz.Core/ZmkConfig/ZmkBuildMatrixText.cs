namespace LViz.Core.ZmkConfig;

/// <summary>
/// Reads a zmk-config <c>build.yaml</c> as text. The parse keeps the exact
/// line array so a later writer can patch the file in place, keeping comments
/// and layout. Only block-style YAML is supported.
/// </summary>
public static class ZmkBuildMatrixText
{
    public static ZmkBuildMatrix Parse(string text, string file = "build.yaml")
    {
        var lines = ZmkYamlLines.Split(text);

        // The list hangs off the 'include:' key at indent 0; a leading '---'
        // and comments before it are fine.
        var keyLine = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!ZmkYamlLines.IsContent(lines[i]) || ZmkYamlLines.Indent(lines[i]) != 0)
                continue;
            var content = ZmkYamlLines.StripComment(lines[i]).Trim();
            if (content == "---")
                continue;
            if (content == "include:")
            {
                keyLine = i;
                break;
            }
            if (content.StartsWith("include:", StringComparison.Ordinal))
                ZmkYamlList.ThrowIfFlow(content["include:".Length..].Trim(), file, i);
        }

        if (keyLine < 0)
            throw new ZmkConfigFormatException(file, 1, "no include list");

        var entries = new List<ZmkBuildEntry>();
        foreach (var item in ZmkYamlList.ParseItems(lines, keyLine, 0, file))
        {
            var board = item.Values.GetValueOrDefault("board");
            if (string.IsNullOrEmpty(board))
                throw new ZmkConfigFormatException(file, item.StartLine + 1, "build matrix entry has no board");

            IReadOnlyList<string> shields = item.Values.TryGetValue("shield", out var shield)
                ? shield.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                : [];
            entries.Add(new ZmkBuildEntry(entries.Count, board, shields, item));
        }

        return new ZmkBuildMatrix(lines, entries);
    }
}

/// <summary>
/// A parsed <c>build.yaml</c>. Line numbers are 0-based indexes into
/// <see cref="Lines"/>.
/// </summary>
public sealed record ZmkBuildMatrix(string[] Lines, IReadOnlyList<ZmkBuildEntry> Entries);

/// <summary>
/// One <c>include:</c> row: its position in the matrix (0-based), its
/// <c>board:</c>, its <c>shield:</c> split on whitespace (empty when the
/// entry has no shield), and the raw item for the writer.
/// </summary>
public sealed record ZmkBuildEntry(int Index, string Board, IReadOnlyList<string> Shields, ZmkYamlItem Item);
