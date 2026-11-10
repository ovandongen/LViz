namespace LViz.Core.ZmkConfig;

/// <summary>What happened to one line of a <see cref="TextLineDiff"/>.</summary>
public enum TextDiffKind
{
    /// <summary>The line is in both texts.</summary>
    Same,

    /// <summary>The line is only in the new text.</summary>
    Added,

    /// <summary>The line is only in the old text.</summary>
    Removed,
}

/// <summary>One line of a line diff, with what happened to it.</summary>
public sealed record TextDiffLine(TextDiffKind Kind, string Text);

/// <summary>
/// A longest-common-subsequence line diff for the setup wizard's review step.
/// Lines are compared ordinal; within one change hunk the removed lines come
/// before the added ones.
/// </summary>
public static class TextLineDiff
{
    /// <summary>Beyond this many old×new line pairs the quadratic table is abandoned.</summary>
    private const long MaxCells = 4_000_000;

    /// <summary>
    /// Diffs the texts (null old text: everything is added). Lines split on
    /// <c>'\n'</c> with a trailing <c>'\r'</c> dropped, and the empty element
    /// a trailing newline ends the text with is not a line. Huge inputs fall
    /// back to "everything removed, then everything added".
    /// </summary>
    public static IReadOnlyList<TextDiffLine> Compute(string? oldText, string newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        if (oldLines.Count == 0)
            return [.. newLines.Select(l => new TextDiffLine(TextDiffKind.Added, l))];
        if (newLines.Count == 0)
            return [.. oldLines.Select(l => new TextDiffLine(TextDiffKind.Removed, l))];

        var n = oldLines.Count;
        var m = newLines.Count;
        if ((long)n * m > MaxCells)
            return
            [
                .. oldLines.Select(l => new TextDiffLine(TextDiffKind.Removed, l)),
                .. newLines.Select(l => new TextDiffLine(TextDiffKind.Added, l)),
            ];

        // lcs[i, j]: length of the longest common suffix-prefix of old[i..] and new[j..].
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var result = new List<TextDiffLine>(n + m);
        var removed = new List<string>();
        var added = new List<string>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(oldLines[x], newLines[y], StringComparison.Ordinal))
            {
                Flush(result, removed, added);
                result.Add(new TextDiffLine(TextDiffKind.Same, oldLines[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                removed.Add(oldLines[x]);
                x++;
            }
            else
            {
                added.Add(newLines[y]);
                y++;
            }
        }
        while (x < n)
            removed.Add(oldLines[x++]);
        while (y < m)
            added.Add(newLines[y++]);
        Flush(result, removed, added);
        return result;
    }

    /// <summary>
    /// The diff trimmed for display: every changed line plus up to
    /// <paramref name="context"/> same lines around it, with a single null
    /// marking each skipped run. A diff without changes yields nothing.
    /// </summary>
    public static IReadOnlyList<TextDiffLine?> WithContext(IReadOnlyList<TextDiffLine> diff, int context)
    {
        var radius = Math.Max(0, context);
        var keep = new bool[diff.Count];

        var any = false;
        var lastChange = int.MinValue / 2;
        for (var i = 0; i < diff.Count; i++)
        {
            if (diff[i].Kind != TextDiffKind.Same)
            {
                lastChange = i;
                any = true;
            }
            if (i - lastChange <= radius)
                keep[i] = true;
        }
        if (!any)
            return [];

        lastChange = int.MaxValue / 2;
        for (var i = diff.Count - 1; i >= 0; i--)
        {
            if (diff[i].Kind != TextDiffKind.Same)
                lastChange = i;
            if (lastChange - i <= radius)
                keep[i] = true;
        }

        var result = new List<TextDiffLine?>();
        var inGap = false;
        for (var i = 0; i < diff.Count; i++)
        {
            if (keep[i])
            {
                result.Add(diff[i]);
                inGap = false;
            }
            else if (!inGap)
            {
                result.Add(null);
                inGap = true;
            }
        }
        return result;
    }

    /// <summary>The pending hunk: first the removed lines, then the added ones.</summary>
    private static void Flush(List<TextDiffLine> result, List<string> removed, List<string> added)
    {
        foreach (var line in removed)
            result.Add(new TextDiffLine(TextDiffKind.Removed, line));
        foreach (var line in added)
            result.Add(new TextDiffLine(TextDiffKind.Added, line));
        removed.Clear();
        added.Clear();
    }

    /// <summary>
    /// The text's lines: split on <c>'\n'</c>, a trailing <c>'\r'</c> dropped,
    /// and the empty element a trailing newline ends the text with dropped.
    /// Null or empty text has no lines.
    /// </summary>
    private static List<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var parts = text.Split('\n');
        var count = text[^1] == '\n' ? parts.Length - 1 : parts.Length;
        var lines = new List<string>(count);
        for (var i = 0; i < count; i++)
            lines.Add(parts[i].EndsWith('\r') ? parts[i][..^1] : parts[i]);
        return lines;
    }
}
