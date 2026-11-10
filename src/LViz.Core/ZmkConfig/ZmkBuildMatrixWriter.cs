namespace LViz.Core.ZmkConfig;

/// <summary>
/// Adds one shield to one <c>build.yaml</c> entry — zmk-raw-hid's
/// <c>raw_hid_adapter</c> goes on the central half's entry, and the setup plan
/// passes that entry's index. An entry whose <c>shield:</c> line exists gets
/// the shield appended to its value directly after the last value character,
/// so trailing spaces and an inline comment keep their place; an entry without
/// one gets a fresh <c>shield:</c> line below its <c>board:</c> line. Every
/// other byte survives, and the result is re-parsed as a bug guard.
/// </summary>
public static class ZmkBuildMatrixWriter
{
    private const string ShieldKey = "shield";

    /// <summary>
    /// Adds <paramref name="shield"/> to the entry with
    /// <paramref name="entryIndex"/>. Returns <paramref name="text"/> unchanged
    /// when that entry already lists the shield.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">no entry has that index.</exception>
    /// <exception cref="ZmkConfigFormatException">the text does not parse as a build matrix.</exception>
    /// <exception cref="InvalidOperationException">the patched text lost the shield or an entry (writer bug).</exception>
    public static string AddShield(string text, int entryIndex, string shield)
    {
        var matrix = ZmkBuildMatrixText.Parse(text);
        var entry = matrix.Entries.FirstOrDefault(e => e.Index == entryIndex);
        if (entry is null)
            throw new ArgumentOutOfRangeException(
                nameof(entryIndex), entryIndex, $"the build matrix has no entry {entryIndex}");
        if (entry.Shields.Contains(shield))
            return text;

        var lines = new List<string>(matrix.Lines);
        var cr = ZmkYamlLines.Newline(text) == "\r\n" ? "\r" : "";

        if (entry.Item.KeyLines.TryGetValue(ShieldKey, out var shieldLine))
        {
            lines[shieldLine] = AppendToValue(lines[shieldLine], shield);
        }
        else
        {
            var boardLine = entry.Item.KeyLines["board"];
            var indent = IsOnDashLine(lines[boardLine]) ? entry.Item.KeyIndent : ZmkYamlLines.Indent(lines[boardLine]);
            if (cr.Length > 0 && boardLine == lines.Count - 1 && !lines[boardLine].EndsWith('\r'))
                lines[boardLine] += cr; // this last line got a successor, so it needs its line ending back
            lines.Insert(boardLine + 1, new string(' ', indent) + ShieldKey + ": " + shield + cr);
        }

        var result = string.Join("\n", lines);
        Guard(result, entryIndex, shield, matrix.Entries.Count);
        return result;
    }

    /// <summary>
    /// Inserts <c>" " + shield</c> after the value's last character — the end
    /// of the line after <see cref="ZmkYamlLines.StripComment"/> — so trailing
    /// spaces, a same-line comment and a trailing '\r' keep their place. A
    /// quoted value takes the shield inside its closing quote; an empty value
    /// becomes <c>shield: &lt;shield&gt;</c> because the insert follows the colon.
    /// </summary>
    private static string AppendToValue(string line, string shield)
    {
        var stripped = ZmkYamlLines.StripComment(line);
        var at = stripped.Length;
        var colon = stripped.IndexOf(':'); // a shield key line has one
        if (colon >= 0)
        {
            var value = stripped[(colon + 1)..].Trim();
            if (ZmkYamlLines.Unquote(value).Length != value.Length)
                at--; // quoted: insert inside the closing quote
        }
        return line[..at] + " " + shield + line[at..];
    }

    /// <summary>True when the key sits behind the item's dash on the item's first line.</summary>
    private static bool IsOnDashLine(string line) =>
        ZmkYamlLines.StripComment(line).Trim().StartsWith("- ", StringComparison.Ordinal);

    /// <summary>
    /// Re-parses the patched text and refuses it when the shield did not land
    /// on the entry or an entry disappeared (writer bug).
    /// </summary>
    private static void Guard(string result, int entryIndex, string shield, int entryCount)
    {
        var parsed = ZmkBuildMatrixText.Parse(result);
        var entry = parsed.Entries.FirstOrDefault(e => e.Index == entryIndex);
        if (parsed.Entries.Count != entryCount || entry is null || !entry.Shields.Contains(shield))
            throw new InvalidOperationException(
                $"the patched build.yaml does not add '{shield}' to entry {entryIndex}");
    }
}
