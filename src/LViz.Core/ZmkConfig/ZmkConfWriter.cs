using System.Text;
using System.Text.RegularExpressions;

namespace LViz.Core.ZmkConfig;

/// <summary>
/// Sets Kconfig options in a zmk-config <c>.conf</c> file. An option that has
/// an active <c>KEY=VALUE</c> line keeps its place — only the text after the
/// first <c>=</c> of its <em>last</em> line is rewritten — and options without
/// one are appended at the end of the file in one LViz block behind a single
/// <see cref="BlockHeader"/>. Everything else survives byte-identically,
/// including commented-out options, blank lines and every line ending; the
/// result is re-parsed as a bug guard.
/// </summary>
public static class ZmkConfWriter
{
    /// <summary>The comment line that introduces the appended LViz block.</summary>
    public const string BlockHeader = "# LViz live features";

    private static readonly Regex KeyPattern =
        new(@"\ACONFIG_[A-Z0-9_]+\Z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Applies <paramref name="settings"/> to the <c>.conf</c> text (null: the
    /// file does not exist yet). Returns <paramref name="text"/> unchanged when
    /// nothing differs; a null or empty text builds a fresh block, and null
    /// text with no settings yields the empty string.
    /// </summary>
    /// <exception cref="ArgumentException">a key is not <c>CONFIG_[A-Z0-9_]+</c>.</exception>
    /// <exception cref="InvalidOperationException">the patched text lost an option (writer bug).</exception>
    public static string Apply(string? text, IReadOnlyList<KeyValuePair<string, string>> settings)
    {
        foreach (var setting in settings)
            if (!KeyPattern.IsMatch(setting.Key))
                throw new ArgumentException(
                    $"'{setting.Key}' is not a CONFIG_[A-Z0-9_]+ option name", nameof(settings));

        if (string.IsNullOrEmpty(text))
        {
            // Null or empty text: the header and the settings, each line with
            // its own newline, no leading empty line.
            if (settings.Count == 0)
                return text ?? "";
            var sb = new StringBuilder(BlockHeader).Append('\n');
            foreach (var (key, value) in settings)
                sb.Append(key).Append('=').Append(value).Append('\n');
            return sb.ToString();
        }

        var lines = ZmkYamlLines.Split(text);
        var cr = ZmkYamlLines.Newline(text) == "\r\n" ? "\r" : "";
        var parsed = ZmkConfText.Parse(text);

        var replacements = new Dictionary<int, string>();
        var appended = new List<KeyValuePair<string, string>>();
        foreach (var setting in settings)
        {
            var last = LastOf(parsed, setting.Key);
            if (last is null)
            {
                appended.Add(setting);
                continue;
            }
            if (string.Equals(last.Value, setting.Value, StringComparison.Ordinal))
                continue;
            replacements[last.Line] = ReplaceValue(lines[last.Line], setting.Value);
        }

        if (replacements.Count == 0 && appended.Count == 0)
            return text;

        string result;
        if (appended.Count == 0)
        {
            // A pure rewrite cannot change the line count, so the text keeps
            // its own (missing) trailing newline by re-joining the split.
            foreach (var (line, replacement) in replacements)
                lines[line] = replacement;
            result = string.Join("\n", lines);
        }
        else
        {
            // The trailing "" of a split is the final newline's terminator,
            // not a line: it goes back after the appended lines.
            var body = text[^1] == '\n' ? lines[..^1] : lines;
            foreach (var (line, replacement) in replacements)
                body[line] = replacement;
            if (text[^1] != '\n' && cr.Length > 0 && !body[^1].EndsWith('\r'))
                body[^1] += cr; // the appended newline is the file's, so the last line gets its \r back

            var outLines = new List<string>(body);
            if (!body.Any(l => string.Equals(l.Trim(), BlockHeader, StringComparison.Ordinal)))
            {
                if (body.Length == 0 || body[^1].Trim().Length > 0)
                    outLines.Add(cr); // one empty line above a fresh header (a CRLF file gives it its \r)
                outLines.Add(BlockHeader + cr);
            }
            foreach (var (key, value) in appended)
                outLines.Add($"{key}={value}{cr}");
            outLines.Add("");
            result = string.Join("\n", outLines);
        }

        Guard(result, settings);
        return result;
    }

    /// <summary>The last active assignment of <paramref name="key"/>, or null.</summary>
    private static ZmkConfSetting? LastOf(IReadOnlyList<ZmkConfSetting> parsed, string key)
    {
        for (var i = parsed.Count - 1; i >= 0; i--)
            if (string.Equals(parsed[i].Key, key, StringComparison.Ordinal))
                return parsed[i];
        return null;
    }

    /// <summary>
    /// Rewrites the line so only the part after its first '=' changes; a
    /// trailing '\r' belongs to the line ending and stays.
    /// </summary>
    private static string ReplaceValue(string line, string value)
    {
        var cr = line.EndsWith('\r') ? "\r" : "";
        var body = cr.Length > 0 ? line[..^1] : line;
        var equals = body.IndexOf('='); // an active line has one
        return body[..(equals + 1)] + value + cr;
    }

    /// <summary>
    /// Re-parses the patched text and refuses it when an option did not end up
    /// with its requested value (writer bug). The last setting for a key wins,
    /// mirroring what the pass above wrote.
    /// </summary>
    private static void Guard(string result, IReadOnlyList<KeyValuePair<string, string>> settings)
    {
        var parsed = ZmkConfText.Parse(result);
        foreach (var key in settings.Select(s => s.Key).Distinct())
        {
            var expected = settings.Last(s => string.Equals(s.Key, key, StringComparison.Ordinal)).Value;
            if (!string.Equals(ZmkConfText.Get(parsed, key), expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"the patched .conf does not set {key}");
        }
    }
}
