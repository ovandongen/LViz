using System.Text.RegularExpressions;
using LViz.Core.Layout.Dtsi;

namespace LViz.Core.Layout.Import;

/// <summary>
/// Importer 3 of 3: rough layout from the <c>zmk,matrix-transform</c> node,
/// which every ZMK board has. Used when a board ships neither a
/// <c>zmk,physical-layout</c> nor a keymap-editor/QMK <c>info.json</c>.
/// <para>
/// The <c>RC(row,col)</c> numbers describe the matrix wiring, not the
/// position (Cosmos lists its left half <c>RC(0,6) … RC(0,0)</c>), so
/// positions come from how the author laid the map out as text — real
/// configs indent and gap the lines like the board. When every token sits
/// on one line there is no text layout to read and the wiring grid is used
/// instead. Either way the result is rough and carries a warning telling
/// the user to adjust key positions.
/// </para>
/// </summary>
public static class MatrixTransformImporter
{
    private const string Compatible = "zmk,matrix-transform";

    private static readonly Regex TokenPattern =
        new(@"RC\(\s*\d+\s*,\s*\d+\s*\)", RegexOptions.Compiled);
    private static readonly Regex TokenNumbersPattern =
        new(@"RC\(\s*(\d+)\s*,\s*(\d+)\s*\)", RegexOptions.Compiled);
    private static readonly Regex MapPattern =
        new(@"(?<![A-Za-z0-9_])map\s*=\s*<", RegexOptions.Compiled);

    /// <summary>
    /// One <see cref="ImportedLayout"/> per <c>zmk,matrix-transform</c> node
    /// in file order; a file without such a node imports as an empty list.
    /// Malformed maps (no <c>map</c> property, junk inside the map, empty
    /// map) throw <see cref="LayoutImportException"/> with node name and
    /// 1-based line. Pure function, no I/O.
    /// </summary>
    public static IReadOnlyList<ImportedLayout> Import(string dtsiText)
    {
        var masked = DtsiText.MaskComments(dtsiText);
        var nodes = DtsiText.FindNodesByCompatible(masked, Compatible);
        var layouts = new List<ImportedLayout>(nodes.Count);
        foreach (var node in nodes)
            layouts.Add(ImportNode(masked, node));
        return layouts;
    }

    private static ImportedLayout ImportNode(string masked, DtsiText.DtsiNode node)
    {
        var body = masked[node.BodyStart..node.BodyEnd];
        var map = MapPattern.Match(body);
        if (!map.Success)
        {
            throw new LayoutImportException(
                $"Matrix transform node '{node.Name}' (line "
                + $"{DtsiText.LineOf(masked, node.BodyStart)}) has no 'map' property.");
        }

        int contentStart = node.BodyStart + map.Index + map.Length;
        int gt = masked.IndexOf('>', contentStart);
        int contentEnd = gt >= 0 && gt <= node.BodyEnd ? gt : node.BodyEnd;
        var content = masked[contentStart..contentEnd];

        var tokens = TokenPattern.Matches(content);
        RejectUnknownText(masked, node, content, contentStart, tokens);
        if (tokens.Count == 0)
        {
            throw new LayoutImportException(
                $"Matrix transform node '{node.Name}' has an empty map (line "
                + $"{DtsiText.LineOf(masked, contentStart)}).");
        }

        // One placement per token, in token order. Each token knows its
        // physical line and its tab-expanded column on that line.
        var placements = new List<(int LineStart, int Column, Match Token)>(tokens.Count);
        foreach (Match token in tokens)
        {
            int offset = contentStart + token.Index;
            int lineStart = masked.LastIndexOf('\n', Math.Max(offset - 1, 0)) + 1;
            placements.Add((lineStart, ColumnOnLine(masked, lineStart, offset), token));
        }

        var rowStarts = placements.Select(p => p.LineStart).Distinct().ToList();
        var keys = rowStarts.Count >= 2
            ? VisualKeys(placements, rowStarts)
            : GridKeys(placements);
        var warning = rowStarts.Count >= 2
            ? new LayoutImportWarning(LayoutWarningCode.MatrixTransformRough)
            : new LayoutImportWarning(LayoutWarningCode.MatrixTransformGrid);

        return new ImportedLayout(node.Name, LayoutSource.MatrixTransform, keys, [warning]);
    }

    /// <summary>
    /// Text layout: visual rows (lines with tokens, numbered from 0), X from
    /// the column relative to the leftmost token in units of a quarter of the
    /// dominant inter-token pitch, Y one row unit per visual row.
    /// </summary>
    private static IReadOnlyList<DtsiKeyPhysical> VisualKeys(
        List<(int LineStart, int Column, Match Token)> placements, List<int> rowStarts)
    {
        var rowIndex = rowStarts
            .Select((start, i) => (start, i))
            .ToDictionary(x => x.start, x => x.i);

        var distances = new List<int>();
        foreach (int start in rowStarts)
        {
            var cols = placements.Where(p => p.LineStart == start).Select(p => p.Column);
            int? prev = null;
            foreach (int col in cols)
            {
                if (prev is int p) distances.Add(col - p);
                prev = col;
            }
        }
        int pitch = 8;
        if (distances.Count > 0)
        {
            var counts = distances.GroupBy(d => d)
                .Select(g => (distance: g.Key, count: g.Count()))
                .ToList();
            int max = counts.Max(c => c.count);
            pitch = counts.Where(c => c.count == max).Min(c => c.distance);
        }
        int minCol = placements.Min(p => p.Column);

        var keys = new List<DtsiKeyPhysical>(placements.Count);
        foreach (var (lineStart, column, _) in placements)
        {
            // Snap to 0.25u: quarter-pitches from the leftmost token, each
            // quarter worth 25 centi-u.
            int x = (int)(Math.Round((column - minCol) / (double)pitch * 4,
                MidpointRounding.AwayFromZero) * 25);
            keys.Add(new DtsiKeyPhysical(100, 100, x, rowIndex[lineStart] * 100, 0, 0, 0));
        }
        return keys;
    }

    /// <summary>Wiring grid fallback: the RC(row,col) numbers as positions.</summary>
    private static IReadOnlyList<DtsiKeyPhysical> GridKeys(
        List<(int LineStart, int Column, Match Token)> placements)
    {
        var keys = new List<DtsiKeyPhysical>(placements.Count);
        foreach (var (_, _, token) in placements)
        {
            var numbers = TokenNumbersPattern.Match(token.Value);
            int row = int.Parse(numbers.Groups[1].Value);
            int col = int.Parse(numbers.Groups[2].Value);
            keys.Add(new DtsiKeyPhysical(100, 100, col * 100, row * 100, 0, 0, 0));
        }
        return keys;
    }

    /// <summary>Anything that is neither whitespace nor part of an RC token.</summary>
    private static void RejectUnknownText(
        string masked, DtsiText.DtsiNode node, string content, int contentStart,
        MatchCollection tokens)
    {
        var covered = new bool[content.Length];
        foreach (Match token in tokens)
            for (int i = token.Index; i < token.Index + token.Length; i++)
                covered[i] = true;

        for (int i = 0; i < content.Length; i++)
        {
            if (char.IsWhiteSpace(content[i]) || covered[i]) continue;
            int end = i;
            while (end < content.Length && !char.IsWhiteSpace(content[end])) end++;
            string offender = content[i..Math.Min(end, i + 16)];
            throw new LayoutImportException(
                $"Unexpected text '{offender}' in the map of matrix transform node "
                + $"'{node.Name}' (line {DtsiText.LineOf(masked, contentStart + i)}).");
        }
    }

    /// <summary>Character offset of <paramref name="offset"/> from
    /// <paramref name="lineStart"/>, tabs expanded to the next multiple of 4.</summary>
    private static int ColumnOnLine(string text, int lineStart, int offset)
    {
        int col = 0;
        for (int i = lineStart; i < offset; i++)
            col = text[i] == '\t' ? (col / 4 + 1) * 4 : col + 1;
        return col;
    }
}
