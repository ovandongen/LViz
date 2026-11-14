namespace LViz.Core.ZmkConfig;

/// <summary>
/// Patches a zmk-config <c>config/west.yml</c> so it pulls in LViz's two
/// modules — <c>zmk-raw-hid</c> and <c>zmk-hid-viz</c> — from Olaf's GitHub:
/// LViz is tested with the ovandongen fork of raw-hid, so a
/// <c>zmk-raw-hid</c> from another owner is switched over to it. The writer only ever adds lines,
/// rewrites the ones that serve those two projects and their remote, and drops
/// a switched-over project's redundant <c>url:</c> line — same-line comments,
/// layout and every other item stay byte-identical — and re-parses its own
/// result as a bug guard.
/// </summary>
public static class ZmkWestManifestWriter
{
    /// <summary>Where both modules come from.</summary>
    public const string OwnerUrlBase = "https://github.com/ovandongen";

    private const string OwnerName = "ovandongen";
    private const string FallbackRemoteName = "ovandongen-lviz";
    private const string RawHidName = "zmk-raw-hid";
    private const string HidVizName = "zmk-hid-viz";
    private const string WestFile = "config/west.yml";

    /// <summary>
    /// Ensures the manifest has a remote for <see cref="OwnerUrlBase"/> (reused
    /// when one exists), a <c>zmk-raw-hid</c> project from that remote at
    /// <paramref name="rawHidRevision"/>, and a <c>zmk-hid-viz</c> project at
    /// <paramref name="hidVizRevision"/> (an existing one is never touched).
    /// Returns <paramref name="text"/> unchanged when nothing is missing.
    /// </summary>
    /// <exception cref="ZmkConfigFormatException">
    /// the manifest does not parse, or has no <c>projects:</c> section to anchor at.
    /// </exception>
    /// <exception cref="InvalidOperationException">the patched text lost a module (writer bug).</exception>
    public static string AddModules(string text, string rawHidRevision, string hidVizRevision)
    {
        var manifest = ZmkWestManifestText.Parse(text);
        if (manifest.ProjectsKeyLine < 0)
            throw new ZmkConfigFormatException(WestFile, 1, "no projects");

        var patch = new Patch(manifest, ZmkYamlLines.Newline(text) == "\r\n" ? "\r" : "");
        var remoteName = patch.ResolveRemote();
        patch.ApplyRawHid(remoteName, rawHidRevision);
        patch.ApplyHidViz(remoteName, hidVizRevision);
        if (patch.IsEmpty)
            return text;

        var result = string.Join("\n", patch.Apply());
        Guard(result);
        return result;
    }

    /// <summary>True when the owner is spelled like Olaf's account (case is not significant on GitHub).</summary>
    private static bool OwnerIsOvandongen(string? owner) =>
        string.Equals(owner, OwnerName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Compares a remote's <c>url-base</c> with ours: trailing '/' ignored, case-insensitive.</summary>
    private static bool IsOwnerUrlBase(string? urlBase) =>
        urlBase is not null &&
        string.Equals(urlBase.TrimEnd('/'), OwnerUrlBase, StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-parses the patched text and refuses it when a module did not land (writer bug).</summary>
    private static void Guard(string result)
    {
        var parsed = ZmkWestManifestText.Parse(result);
        var rawHid = parsed.Project(RawHidName);
        if (rawHid is null || !OwnerIsOvandongen(parsed.OwnerOf(rawHid)) || parsed.Project(HidVizName) is null)
            throw new InvalidOperationException(
                $"the patched west.yml does not pull both modules from {OwnerUrlBase}");
    }

    /// <summary>
    /// The pending edits of one <see cref="AddModules"/> call, addressed by the
    /// 0-based line numbers of the parsed manifest. Insertions sharing an
    /// anchor keep their call order; <see cref="Apply"/> handles anchors
    /// bottom-up so earlier line numbers stay valid. A deleted line drops out;
    /// insertions anchored on it take its place.
    /// </summary>
    private sealed class Patch(ZmkWestManifest Manifest, string Cr)
    {
        private readonly Dictionary<int, string> _replacements = [];
        private readonly Dictionary<int, List<string>> _insertions = [];
        private readonly HashSet<int> _deletions = [];

        public bool IsEmpty => _replacements.Count == 0 && _insertions.Count == 0 && _deletions.Count == 0;

        /// <summary>
        /// The remote feeding <see cref="OwnerUrlBase"/>: reused when some
        /// remote's <c>url-base</c> is ours, else named <c>ovandongen</c>
        /// (<c>ovandongen-lviz</c> when that name is taken) and inserted
        /// directly after the last remote item — comment lines below that item
        /// stay below it — or, without a <c>remotes:</c> section, as a fresh
        /// section right before <c>projects:</c>.
        /// </summary>
        public string ResolveRemote()
        {
            var reuse = Manifest.Remotes.FirstOrDefault(r => IsOwnerUrlBase(r.Values.GetValueOrDefault("url-base")));
            var reuseName = reuse?.Values.GetValueOrDefault("name");
            if (!string.IsNullOrEmpty(reuseName))
                return reuseName;

            var name = Manifest.Remotes.Any(r => r.Values.GetValueOrDefault("name") == OwnerName)
                ? FallbackRemoteName
                : OwnerName;

            if (Manifest.Remotes.Count > 0)
            {
                var last = Manifest.Remotes[^1];
                InsertAfter(last.EndLine,
                    Spaces(last.DashIndent) + $"- name: {name}",
                    Spaces(last.KeyIndent) + $"url-base: {OwnerUrlBase}");
            }
            else if (Manifest.RemotesKeyLine >= 0)
            {
                var indent = ZmkYamlLines.Indent(Manifest.Lines[Manifest.RemotesKeyLine]);
                InsertAfter(Manifest.RemotesKeyLine,
                    Spaces(indent + 2) + $"- name: {name}",
                    Spaces(indent + 4) + $"url-base: {OwnerUrlBase}");
            }
            else
            {
                var keyLineIndent = ZmkYamlLines.Indent(Manifest.Lines[Manifest.ProjectsKeyLine]);
                var (_, dash, key) = ProjectAnchor();
                InsertAfter(Manifest.ProjectsKeyLine - 1,
                    Spaces(keyLineIndent) + "remotes:",
                    Spaces(dash) + $"- name: {name}",
                    Spaces(key) + $"url-base: {OwnerUrlBase}");
            }
            return name;
        }

        /// <summary>
        /// Appends <c>zmk-raw-hid</c> after the last project when absent;
        /// switches its <c>remote:</c> to <paramref name="remoteName"/> (a
        /// second <c>url:</c> line goes with it, a lone <c>url:</c> becomes
        /// the remote reference) and its <c>revision:</c> to
        /// <paramref name="revision"/> when it comes from someone else;
        /// leaves an Olaf-sourced item exactly as it is.
        /// </summary>
        public void ApplyRawHid(string remoteName, string revision)
        {
            var rawHid = Manifest.Project(RawHidName);
            if (rawHid is null)
            {
                var (anchor, dash, key) = ProjectAnchor();
                InsertAfter(anchor,
                    Spaces(dash) + $"- name: {RawHidName}",
                    Spaces(key) + $"remote: {remoteName}",
                    Spaces(key) + $"revision: {revision}");
                return;
            }

            if (OwnerIsOvandongen(Manifest.OwnerOf(rawHid)))
                return;

            var hasRemote = rawHid.KeyLines.TryGetValue("remote", out var remoteLine);
            var hasUrl = rawHid.KeyLines.TryGetValue("url", out var urlLine);
            if (hasRemote && hasUrl)
            {
                ReplaceKey(remoteLine, "remote", "remote", remoteName);
                Delete(urlLine); // two remote: lines would be untidy; the url one goes
            }
            else if (hasRemote)
                ReplaceKey(remoteLine, "remote", "remote", remoteName);
            else if (hasUrl)
                ReplaceKey(urlLine, "url", "remote", remoteName); // the url line becomes the remote reference
            else
                // The owner came from the default remote: pin this item to ours.
                InsertAfter(rawHid.StartLine, Spaces(rawHid.KeyIndent) + $"remote: {remoteName}");

            if (rawHid.KeyLines.TryGetValue("revision", out var revisionLine))
                ReplaceKey(revisionLine, "revision", "revision", revision);
            else
                InsertAfter(rawHid.EndLine, Spaces(rawHid.KeyIndent) + $"revision: {revision}");
        }

        /// <summary>Appends <c>zmk-hid-viz</c> when absent; an existing one is never touched.</summary>
        public void ApplyHidViz(string remoteName, string revision)
        {
            if (Manifest.Project(HidVizName) is not null)
                return;

            var (anchor, dash, key) = ProjectAnchor();
            InsertAfter(anchor,
                Spaces(dash) + $"- name: {HidVizName}",
                Spaces(key) + $"remote: {remoteName}",
                Spaces(key) + $"revision: {revision}");
        }

        /// <summary>The patched line array.</summary>
        public string[] Apply()
        {
            var lines = new List<string>(Manifest.Lines);
            // Replacements first: the pass below may strip the line ending of
            // a line above a deleted last line, and a replacement applied to
            // that line afterwards would bring the ending back.
            foreach (var (line, replacement) in _replacements)
                lines[line] = replacement;
            for (var i = Manifest.Lines.Length - 1; i >= 0; i--)
            {
                var deleted = _deletions.Contains(i);
                if (_insertions.TryGetValue(i, out var added))
                {
                    if (!deleted && i == Manifest.Lines.Length - 1 && Cr.Length > 0
                        && ZmkYamlLines.IsContent(lines[i]) && !lines[i].EndsWith('\r'))
                        lines[i] += "\r"; // this last line got a successor, so it needs its line ending back
                    lines.InsertRange(i + 1, added);
                }
                if (!deleted)
                    continue;
                lines.RemoveAt(i);
                if (i == Manifest.Lines.Length - 1 && Cr.Length > 0 && lines.Count > 0)
                {
                    var last = lines[^1];
                    if (ZmkYamlLines.IsContent(last) && last.EndsWith('\r'))
                        lines[^1] = last[..^1]; // this line is the last one now, so its ending goes away
                }
            }
            return [.. lines];
        }

        /// <summary>
        /// Where and how to append a project item: after the last item with its
        /// own indents, or right below the <c>projects:</c> key (one level
        /// deeper, +2 per level) when the list holds no items yet.
        /// </summary>
        private (int Anchor, int Dash, int Key) ProjectAnchor()
        {
            if (Manifest.Projects.Count > 0)
            {
                var last = Manifest.Projects[^1];
                return (last.EndLine, last.DashIndent, last.KeyIndent);
            }
            var indent = ZmkYamlLines.Indent(Manifest.Lines[Manifest.ProjectsKeyLine]);
            return (Manifest.ProjectsKeyLine, indent + 2, indent + 4);
        }

        private void InsertAfter(int line, params string[] inserted)
        {
            if (!_insertions.TryGetValue(line, out var list))
                _insertions[line] = list = [];
            foreach (var text in inserted)
                list.Add(text + Cr);
        }

        private void Delete(int line) => _deletions.Add(line);

        /// <summary>
        /// Rewrites the key line so it reads <c>newKey: value</c>, keeping the
        /// line's indent and everything before the old key (its dash, a tab),
        /// its same-line comment and its line ending.
        /// </summary>
        private void ReplaceKey(int line, string oldKey, string newKey, string value)
        {
            var original = Manifest.Lines[line];
            var at = original.IndexOf(oldKey + ":", StringComparison.Ordinal);
            var prefix = at >= 0 ? original[..at] : original[..ZmkYamlLines.Indent(original)];
            var body = original.EndsWith('\r') ? original[..^1] : original;
            var tail = body[ZmkYamlLines.StripComment(body).Length..]; // the whitespace + '#…' part, or empty
            _replacements[line] = prefix + newKey + ": " + value + tail + (original.EndsWith('\r') ? "\r" : "");
        }

        private static string Spaces(int indent) => new(' ', indent);
    }
}
