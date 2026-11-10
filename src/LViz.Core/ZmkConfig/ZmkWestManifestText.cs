using System.Text.RegularExpressions;

namespace LViz.Core.ZmkConfig;

/// <summary>
/// Reads a zmk-config <c>config/west.yml</c> as text. The parse keeps the
/// exact line array so a later writer can patch the file in place, keeping
/// comments and layout. Only block-style YAML is supported.
/// </summary>
public static class ZmkWestManifestText
{
    private static readonly Regex SectionName =
        new(@"^[A-Za-z0-9][A-Za-z0-9_-]*:$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ZmkWestManifest Parse(string text, string file = "config/west.yml")
    {
        var lines = ZmkYamlLines.Split(text);

        // The manifest starts at the first root-level content line; a leading
        // '---' and comments before it are fine.
        var manifestLine = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!ZmkYamlLines.IsContent(lines[i]) || ZmkYamlLines.Indent(lines[i]) != 0)
                continue;
            var content = ZmkYamlLines.StripComment(lines[i]).Trim();
            if (content == "---")
                continue;
            if (content == "manifest:")
                manifestLine = i;
            else if (content.StartsWith("manifest:", StringComparison.Ordinal))
                ZmkYamlList.ThrowIfFlow(content["manifest:".Length..].Trim(), file, i);
            break;
        }

        if (manifestLine < 0)
            throw new ZmkConfigFormatException(file, 1, "no manifest");

        // Sections are the content lines at the manifest's child indent that
        // read as 'key:'; each section runs to the next one.
        var childIndent = -1;
        var sections = new List<(string Name, int Line)>();
        for (var i = manifestLine + 1; i < lines.Length; i++)
        {
            if (!ZmkYamlLines.IsContent(lines[i]))
                continue;
            var indent = ZmkYamlLines.Indent(lines[i]);
            if (childIndent < 0)
            {
                childIndent = indent;
                if (childIndent == 0)
                    break; // nothing is indented under the manifest
            }
            if (indent == 0)
                break; // the manifest block ends at the next root-level key
            if (indent != childIndent)
                continue; // content of the section we are in

            var content = ZmkYamlLines.StripComment(lines[i]).Trim();
            var colon = content.IndexOf(':');
            if (colon >= 0)
            {
                var rest = content[(colon + 1)..].Trim();
                if (rest.Length > 0)
                {
                    ZmkYamlList.ThrowIfFlow(rest, file, i); // 'key: [' / 'key: {'
                    throw new ZmkConfigFormatException(file, i + 1, "expected a section");
                }
                if (SectionName.IsMatch(content))
                {
                    sections.Add((content[..^1], i));
                    continue;
                }
            }
            throw new ZmkConfigFormatException(file, i + 1, "expected a section");
        }

        string? defaultRevision = null;
        string? defaultRemote = null;
        var remotes = new List<ZmkYamlItem>();
        var projects = new List<ZmkYamlItem>();
        var remotesKeyLine = -1;
        var projectsKeyLine = -1;

        for (var s = 0; s < sections.Count; s++)
        {
            var (name, line) = sections[s];
            var end = s + 1 < sections.Count ? sections[s + 1].Line : lines.Length;
            switch (name)
            {
                case "defaults":
                    foreach (var (key, value) in KeyedChildren(lines, line, childIndent, end, file))
                    {
                        if (key == "revision")
                            defaultRevision = value;
                        else if (key == "remote")
                            defaultRemote = value;
                    }
                    break;
                case "remotes":
                    remotes = ZmkYamlList.ParseItems(lines, line, childIndent, file);
                    remotesKeyLine = line;
                    break;
                case "projects":
                    projects = ZmkYamlList.ParseItems(lines, line, childIndent, file);
                    projectsKeyLine = line;
                    break;
            }
        }

        return new ZmkWestManifest(
            lines, defaultRevision, defaultRemote, remotes, projects, remotesKeyLine, projectsKeyLine);
    }

    /// <summary>The 'key: value' lines in a section's body (deeper than the section key).</summary>
    private static IEnumerable<(string Key, string Value)> KeyedChildren(
        string[] lines, int sectionLine, int childIndent, int end, string file)
    {
        for (var i = sectionLine + 1; i < end; i++)
        {
            if (!ZmkYamlLines.IsContent(lines[i]) || ZmkYamlLines.Indent(lines[i]) <= childIndent)
                continue;
            yield return ZmkYamlList.ParsePair(ZmkYamlLines.StripComment(lines[i]).Trim(), file, i);
        }
    }
}

/// <summary>
/// A parsed <c>west.yml</c>. Line numbers are 0-based indexes into
/// <see cref="Lines"/>; the key lines are -1 when the section is absent.
/// </summary>
public sealed record ZmkWestManifest(
    string[] Lines,
    string? DefaultRevision,
    string? DefaultRemote,
    IReadOnlyList<ZmkYamlItem> Remotes,
    IReadOnlyList<ZmkYamlItem> Projects,
    int RemotesKeyLine,
    int ProjectsKeyLine)
{
    /// <summary>The project whose <c>name</c> matches, or null.</summary>
    public ZmkYamlItem? Project(string name) => FindByName(Projects, name);

    /// <summary>The <c>url-base</c> of the named remote, or null.</summary>
    public string? RemoteUrlBase(string remoteName) =>
        FindByName(Remotes, remoteName)?.Values.GetValueOrDefault("url-base");

    /// <summary>
    /// The GitHub owner behind a project: the last path segment of its
    /// remote's <c>url-base</c> (remote from the item, else the default
    /// remote), or the second-to-last segment of the project's own
    /// <c>url:</c> when it has one. Trailing '/' is ignored.
    /// </summary>
    public string? OwnerOf(ZmkYamlItem project)
    {
        var remote = project.Values.GetValueOrDefault("remote") ?? DefaultRemote;
        var urlBase = remote is null ? null : RemoteUrlBase(remote);
        if (urlBase is not null)
        {
            var segments = Segments(urlBase);
            if (segments.Count > 0)
                return segments[^1];
        }

        var url = project.Values.GetValueOrDefault("url");
        if (url is null)
            return null;
        var urlSegments = Segments(url);
        return urlSegments.Count >= 2 ? urlSegments[^2] : null;
    }

    private static ZmkYamlItem? FindByName(IReadOnlyList<ZmkYamlItem> items, string name) =>
        items.FirstOrDefault(i => string.Equals(i.Values.GetValueOrDefault("name"), name, StringComparison.Ordinal));

    private static List<string> Segments(string url) =>
        [.. url.Split('/', StringSplitOptions.RemoveEmptyEntries)];
}
