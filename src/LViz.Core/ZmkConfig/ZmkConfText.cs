namespace LViz.Core.ZmkConfig;

/// <summary>
/// A <c>KEY=VALUE</c> line of a <c>.conf</c> file. <see cref="Line"/> is a
/// 0-based index into the split text, like the YAML line numbers. The value
/// keeps its surrounding quotes: <c>.conf</c> values are never unquoted.
/// </summary>
public sealed record ZmkConfSetting(string Key, string Value, int Line);

/// <summary>
/// Reads a ZMK <c>.conf</c> fragment as text. Lines that are not a
/// <c>KEY=VALUE</c> assignment (comments, blanks, <c>rsource</c> lines) do
/// not produce settings; the writer later patches the raw text instead.
/// </summary>
public static class ZmkConfText
{
    /// <summary>Every assignment, in file order, duplicates kept.</summary>
    public static IReadOnlyList<ZmkConfSetting> Parse(string text)
    {
        var lines = ZmkYamlLines.Split(text);
        var settings = new List<ZmkConfSetting>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!ZmkYamlLines.IsContent(lines[i]))
                continue;
            var content = lines[i].Trim();
            var equals = content.IndexOf('=');
            if (equals <= 0)
                continue;
            settings.Add(new ZmkConfSetting(content[..equals].Trim(), content[(equals + 1)..].Trim(), i));
        }
        return settings;
    }

    /// <summary>The value of the last assignment of <paramref name="key"/>, or null.</summary>
    public static string? Get(IReadOnlyList<ZmkConfSetting> settings, string key)
    {
        for (var i = settings.Count - 1; i >= 0; i--)
        {
            if (string.Equals(settings[i].Key, key, StringComparison.Ordinal))
                return settings[i].Value;
        }
        return null;
    }
}
