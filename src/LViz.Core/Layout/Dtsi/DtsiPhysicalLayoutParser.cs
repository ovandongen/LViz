using System.Globalization;
using System.Text.RegularExpressions;
using LViz.Core.Layout.Import;

namespace LViz.Core.Layout.Dtsi;

/// <summary>
/// Parses ZMK physical-layouts dtsi text into one <see cref="DtsiPhysicalLayout"/>
/// per <c>zmk,physical-layout</c> node. Comments are masked first, so a
/// commented-out <c>&amp;key_physical_attrs</c> never becomes a key, and each
/// layout node keeps its own name and <c>display-name</c> instead of being
/// merged into one flat list. Negative rotations use devicetree's
/// <c>(-NNNN)</c> wrapper, which is normalized here. Malformed input is
/// refused with a <see cref="LayoutImportException"/> naming the node and the
/// 1-based line; a file without any physical-layout node yields an empty list.
/// </summary>
public static class DtsiPhysicalLayoutParser
{
    private const string PhysicalLayoutCompatible = "zmk,physical-layout";
    private const string EntryMarker = "&key_physical_attrs";

    // Seven integer fields after &key_physical_attrs. Each can be a bare
    // number (signed) or a (-NNNN) wrapper that devicetree requires for
    // negatives inside <...> cell arrays. Whitespace between fields is
    // arbitrary (the upstream files line-align with extra spaces).
    private const string IntField = @"(-?\d+|\(\s*-\d+\s*\))";
    private static readonly Regex EntryRegex = new(
        @"&key_physical_attrs\s+" +
        IntField + @"\s+" + IntField + @"\s+" +
        IntField + @"\s+" + IntField + @"\s+" +
        IntField + @"\s+" + IntField + @"\s+" + IntField,
        RegexOptions.Compiled);

    private static readonly Regex DisplayNameRegex = new(
        @"display-name\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    /// <summary>All <c>zmk,physical-layout</c> nodes in file order.
    /// Empty list when the text has none.</summary>
    /// <exception cref="LayoutImportException">A node has an entry that does
    /// not match the 7-field shape, or has no entries at all; the message
    /// names the node and gives the 1-based line.</exception>
    public static IReadOnlyList<DtsiPhysicalLayout> ParseAll(string text)
    {
        var masked = DtsiText.MaskComments(text);
        var nodes = DtsiText.FindNodesByCompatible(masked, PhysicalLayoutCompatible);

        var layouts = new List<DtsiPhysicalLayout>(nodes.Count);
        foreach (var node in nodes)
        {
            var body = masked.Substring(node.BodyStart, node.BodyEnd - node.BodyStart);

            var displayNameMatch = DisplayNameRegex.Match(body);
            string? displayName = displayNameMatch.Success
                ? displayNameMatch.Groups[1].Value
                : null;

            var matches = EntryRegex.Matches(body);
            if (CountOccurrences(body) != matches.Count)
            {
                int offset = FirstUnmatchedOccurrence(body, matches);
                int line = DtsiText.LineOf(masked, node.BodyStart + offset);
                throw new LayoutImportException(
                    $"Physical layout '{node.Name}' has a malformed {EntryMarker} " +
                    $"entry (expected 7 integer fields) on line {line}.");
            }
            if (matches.Count == 0)
            {
                int line = DtsiText.LineOf(masked, node.BodyStart - 1);
                throw new LayoutImportException(
                    $"Physical layout node '{node.Name}' (line {line}) " +
                    $"has no {EntryMarker} entries.");
            }

            var keys = new List<DtsiKeyPhysical>(matches.Count);
            foreach (Match m in matches)
            {
                keys.Add(new DtsiKeyPhysical(
                    Width: ParseField(m.Groups[1].Value),
                    Height: ParseField(m.Groups[2].Value),
                    X: ParseField(m.Groups[3].Value),
                    Y: ParseField(m.Groups[4].Value),
                    Rotation: ParseField(m.Groups[5].Value),
                    Rx: ParseField(m.Groups[6].Value),
                    Ry: ParseField(m.Groups[7].Value)));
            }
            layouts.Add(new DtsiPhysicalLayout(node.Name, displayName, keys));
        }
        return layouts;
    }

    /// <summary>The single layout of a file holding exactly one
    /// <c>zmk,physical-layout</c> node — the shape of every bundled board dtsi.</summary>
    /// <exception cref="InvalidOperationException">The text does not hold
    /// exactly one physical-layout node.</exception>
    public static DtsiPhysicalLayout Parse(string text)
    {
        var layouts = ParseAll(text);
        if (layouts.Count != 1)
            throw new InvalidOperationException(
                $"Expected exactly one {PhysicalLayoutCompatible} node, found {layouts.Count}.");
        return layouts[0];
    }

    private static int CountOccurrences(string body)
    {
        int count = 0;
        int idx = 0;
        while ((idx = body.IndexOf(EntryMarker, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += EntryMarker.Length;
        }
        return count;
    }

    // Every regex match starts exactly on an occurrence of the marker, so
    // when the counts disagree some occurrence has no match: that is the
    // malformed entry. Body-relative offset in the masked body.
    private static int FirstUnmatchedOccurrence(string body, MatchCollection matches)
    {
        var starts = new HashSet<int>();
        foreach (Match m in matches) starts.Add(m.Index);

        int idx = 0;
        while ((idx = body.IndexOf(EntryMarker, idx, StringComparison.Ordinal)) >= 0)
        {
            if (!starts.Contains(idx)) return idx;
            idx += EntryMarker.Length;
        }
        return 0; // unreachable when the counts actually differ
    }

    private static int ParseField(string raw)
    {
        // Strip the devicetree (-NNNN) wrapper if present, then parse as a
        // signed integer. The regex already guarantees one of these shapes.
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('(') && trimmed.EndsWith(')'))
            trimmed = trimmed[1..^1].Trim();
        return int.Parse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
