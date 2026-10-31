using System.Text.RegularExpressions;
using LViz.Core.Layout.Dtsi;

namespace LViz.Core.Layout.Import;

/// <summary>One imported layout and the absolute path of the board file it came from.</summary>
public sealed record LayoutCandidate(ImportedLayout Layout, string FilePath);

/// <summary>
/// Result of scanning a zmk-config folder (or one file inside it): the layout
/// candidates the importers produced, the keymap files found, the keyboard and
/// shield names when discoverable, and one entry per file that could not be
/// read or imported. All paths are absolute; <see cref="Problems"/> entries are
/// <c>"&lt;path relative to Root&gt;: &lt;message&gt;"</c>.
/// </summary>
public sealed record ZmkConfigScan(
    string Root,
    IReadOnlyList<LayoutCandidate> Candidates,
    IReadOnlyList<string> KeymapPaths,
    string? KeyboardName,
    string? ShieldName,
    IReadOnlyList<string> Problems);

/// <summary>
/// Runs the three board-file importers over a whole zmk-config folder (or a
/// single file) and collects what the layout wizard needs to offer a board:
/// layout candidates, keymap files, keyboard name, shield name. A broken file
/// never stops the scan — it is reported in <see cref="ZmkConfigScan.Problems"/>
/// and the scan continues.
/// </summary>
public static class ZmkConfigScanner
{
    /// <summary>Directory levels below the root the walk descends into.</summary>
    private const int MaxDepth = 8;

    /// <summary>Untracked or vendor folders a zmk-config repo accumulates.</summary>
    private static readonly string[] SkippedDirectoryNames =
        ["build", "zephyr", "zmk", "modules", "node_modules"];

    private static readonly Regex ConfNamePattern = new(
        @"CONFIG_ZMK_KEYBOARD_NAME\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    private static readonly Regex KconfigDefaultPattern = new(
        @"^default\s+""([^""]*)""", RegexOptions.Compiled);

    /// <summary>Suffix of a <c>&lt;id&gt;.layout.json</c> the wizard saved earlier.</summary>
    private const string UserLayoutSuffix = ".layout.json";

    private enum BoardFileKind
    {
        UserLayout,
        DtsiLike,
        InfoJson,
        Other,
    }

    /// <summary>
    /// Scan <paramref name="root"/>: walk it (max depth 8, skipping dot-directories
    /// and build/zephyr/zmk/modules/node_modules), import every
    /// <c>.dtsi</c>/<c>.overlay</c> (physical layouts first, then matrix transforms),
    /// every exact-name <c>info.json</c>, and every <c>&lt;id&gt;.layout.json</c>
    /// a previous wizard run saved here, then collect keymaps, the keyboard
    /// name and the shield name.
    /// </summary>
    public static ZmkConfigScan ScanFolder(string root)
    {
        root = Path.GetFullPath(root);
        var problems = new List<string>();
        var files = Walk(root);

        var candidates = new List<LayoutCandidate>();
        foreach (var file in files)
            candidates.AddRange(ImportFile(file, root, problems));

        var (keymaps, keyboardName, shieldName) = ReadContext(root, files, problems);
        return new ZmkConfigScan(
            root,
            OrderCandidates(candidates),
            keymaps,
            keyboardName,
            shieldName,
            problems);
    }

    /// <summary>
    /// Scan a single board file. When it sits under a <c>boards</c> directory
    /// the enclosing zmk-config root (that directory's parent) supplies the
    /// keymaps, keyboard name and shield name; otherwise <c>Root</c> is the
    /// file's folder and those three are empty/null.
    /// </summary>
    public static ZmkConfigScan ScanFile(string path)
    {
        path = Path.GetFullPath(path);
        var problems = new List<string>();
        string? boardsRoot = FindBoardsRoot(path);
        string root = boardsRoot ?? Path.GetDirectoryName(path)!;
        var kind = Classify(path);

        List<LayoutCandidate> candidates;
        if (kind == BoardFileKind.Other)
        {
            candidates = [];
            problems.Add(Problem(path, root, "unsupported file type"));
        }
        else
        {
            candidates = ImportFile(path, root, problems, kind);
        }

        IReadOnlyList<string> keymaps = [];
        string? keyboardName = null;
        string? shieldName = null;
        if (boardsRoot is not null)
        {
            var (km, kn, sn) = ReadContext(boardsRoot, Walk(boardsRoot), problems);
            keymaps = km;
            keyboardName = kn;
            shieldName = sn;
        }

        return new ZmkConfigScan(
            root, OrderCandidates(candidates), keymaps, keyboardName, shieldName, problems);
    }

    /// <summary>
    /// Stable sort: candidates whose key count equals
    /// <paramref name="keymapKeyCount"/> first (no boost when it is null),
    /// then saved layout &gt; physical layout &gt; info.json &gt; matrix
    /// transform, then file path (ordinal). A physical layout in a file
    /// therefore always beats that same file's matrix transform.
    /// </summary>
    public static IReadOnlyList<LayoutCandidate> Rank(
        IEnumerable<LayoutCandidate> candidates, int? keymapKeyCount) =>
        candidates
            .OrderBy(c => keymapKeyCount is int n && c.Layout.Keys.Count == n ? 0 : 1)
            .ThenBy(c => SourceRank(c.Layout.Source))
            .ThenBy(c => c.FilePath, StringComparer.Ordinal)
            .ToList();

    // --- walking ---

    private static List<string> Walk(string root)
    {
        var files = new List<string>();
        CollectFiles(root, depth: 0, files);
        return files;
    }

    private static void CollectFiles(string dir, int depth, List<string> files)
    {
        string[] subFiles, subDirs;
        try
        {
            subFiles = Directory.GetFiles(dir);
            subDirs = Directory.GetDirectories(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // an unreadable folder is not a file problem; skip it
        }

        files.AddRange(subFiles);
        if (depth >= MaxDepth)
            return;

        foreach (var sub in subDirs)
        {
            if (ShouldSkipDirectory(Path.GetFileName(sub)))
                continue;
            CollectFiles(sub, depth + 1, files);
        }
    }

    private static bool ShouldSkipDirectory(string name) =>
        name.Length == 0
        || name[0] == '.'
        || SkippedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The nearest ancestor directory named <c>boards</c>, as its parent — the zmk-config root.</summary>
    private static string? FindBoardsRoot(string filePath)
    {
        for (var dir = Path.GetDirectoryName(filePath);
             dir is not null && Path.GetDirectoryName(dir) is not null;
             dir = Path.GetDirectoryName(dir))
        {
            if (Path.GetFileName(dir).Equals("boards", StringComparison.Ordinal))
                return Path.GetDirectoryName(dir);
        }
        return null;
    }

    // --- importing ---

    private static BoardFileKind Classify(string path)
    {
        string name = Path.GetFileName(path);
        // Checked before the other kinds: a saved layout always wins its
        // file-name shape over the extension rules.
        if (name.Length > UserLayoutSuffix.Length &&
            name.EndsWith(UserLayoutSuffix, StringComparison.OrdinalIgnoreCase))
            return BoardFileKind.UserLayout;
        if (name.Equals("info.json", StringComparison.OrdinalIgnoreCase))
            return BoardFileKind.InfoJson;
        string ext = Path.GetExtension(name);
        return ext.Equals(".dtsi", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".overlay", StringComparison.OrdinalIgnoreCase)
            ? BoardFileKind.DtsiLike
            : BoardFileKind.Other;
    }

    private static List<LayoutCandidate> ImportFile(
        string file, string root, List<string> problems, BoardFileKind? kindHint = null)
    {
        var result = new List<LayoutCandidate>();
        var kind = kindHint ?? Classify(file);
        if (kind == BoardFileKind.Other)
            return result;

        if (kind == BoardFileKind.UserLayout)
        {
            // TryLoadFile reads and validates the file itself — don't pre-read it.
            if (!UserLayoutStore.TryLoadFile(file, out var saved, out var reason))
            {
                problems.Add(Problem(file, root, $"layout file: {reason}"));
                return result;
            }
            var keys = saved.Keys
                .Select(k => new DtsiKeyPhysical(k.W, k.H, k.X, k.Y, k.R, k.Rx, k.Ry))
                .ToList();
            var hands = saved.Keys.Select(k => k.Hand).ToList();
            result.Add(new LayoutCandidate(
                new ImportedLayout(saved.Name, LayoutSource.UserLayout, keys, [], hands, saved.Id), file));
            return result;
        }

        if (ReadAllTextSafe(file, root, problems) is not string text)
            return result;

        if (kind == BoardFileKind.DtsiLike)
        {
            // An LViz export next to its own <id>.layout.json only repeats that
            // layout, minus its hands; the saved layout is the candidate. Without
            // the twin (deleted), the export is all there is, so it stays in.
            if (ZmkPhysicalLayoutWriter.IsGeneratedByLviz(text) && File.Exists(
                    Path.Combine(Path.GetDirectoryName(file) ?? "",
                        Path.GetFileNameWithoutExtension(file) + UserLayoutSuffix)))
                return result;

            // Physical layouts before matrix transforms: the better source wins.
            result.AddRange(Safe(PhysicalLayoutImporter.Import, file, "physical layout", text, root, problems)
                .Select(l => new LayoutCandidate(l, file)));
            result.AddRange(Safe(MatrixTransformImporter.Import, file, "matrix transform", text, root, problems)
                .Select(l => new LayoutCandidate(l, file)));
        }
        else
        {
            result.AddRange(Safe(InfoJsonImporter.Import, file, "info.json", text, root, problems)
                .Select(l => new LayoutCandidate(l, file)));
        }
        return result;
    }

    private static IReadOnlyList<ImportedLayout> Safe(
        Func<string, IReadOnlyList<ImportedLayout>> import,
        string file, string what, string text, string root, List<string> problems)
    {
        try
        {
            return import(text);
        }
        catch (LayoutImportException ex)
        {
            problems.Add(Problem(file, root, $"{what}: {ex.Message}"));
            return [];
        }
    }

    private static string? ReadAllTextSafe(string file, string root, List<string> problems)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add(Problem(file, root, e.Message));
            return null;
        }
    }

    // --- folder context: keymaps, keyboard name, shield ---

    private static (IReadOnlyList<string> Keymaps, string? KeyboardName, string? ShieldName)
        ReadContext(string root, List<string> files, List<string> problems) =>
        (FindKeymaps(root, files), FindKeyboardName(root, files, problems), FindShieldName(root));

    private static IReadOnlyList<string> FindKeymaps(string root, List<string> files)
    {
        var keymaps = files
            .Where(f => Path.GetExtension(f).Equals(".keymap", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var inConfig = keymaps.Where(f => IsDirectChild(f, root, "config")).ToList();
        var picked = inConfig.Count > 0 ? inConfig : keymaps;
        picked.Sort(StringComparer.Ordinal);
        return picked;
    }

    private static string? FindKeyboardName(string root, List<string> files, List<string> problems)
    {
        // .conf first: config/ files before the rest, each group ordinal by path.
        var confs = files
            .Where(f => Path.GetExtension(f).Equals(".conf", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var ordered = confs
            .OrderBy(f => IsDirectChild(f, root, "config") ? 0 : 1)
            .ThenBy(f => f, StringComparer.Ordinal);
        foreach (var conf in ordered)
        {
            if (ReadAllTextSafe(conf, root, problems) is not string text)
                continue;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimStart();
                if (line.StartsWith('#'))
                    continue;
                var match = ConfNamePattern.Match(line);
                if (match.Success && match.Groups[1].Value.Length > 0)
                    return match.Groups[1].Value;
            }
        }

        // Fallback: Kconfig.defconfig `config ZMK_KEYBOARD_NAME` + `default "…"` blocks.
        var kconfigs = files
            .Where(f => Path.GetFileName(f).Equals("Kconfig.defconfig", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);
        foreach (var kconfig in kconfigs)
        {
            if (ReadAllTextSafe(kconfig, root, problems) is not string text)
                continue;
            if (ParseKconfigKeyboardName(text) is string name)
                return name;
        }
        return null;
    }

    private static string? ParseKconfigKeyboardName(string text)
    {
        bool inBlock = false;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (!inBlock)
            {
                inBlock = line == "config ZMK_KEYBOARD_NAME";
                continue;
            }
            if (line.StartsWith("config", StringComparison.Ordinal)
                || line.StartsWith("if", StringComparison.Ordinal)
                || line.StartsWith("endif", StringComparison.Ordinal)
                || line.StartsWith("menu", StringComparison.Ordinal))
            {
                // A `config` line may re-open the search for another stanza.
                inBlock = line == "config ZMK_KEYBOARD_NAME";
                continue;
            }
            var match = KconfigDefaultPattern.Match(line);
            if (match.Success && match.Groups[1].Value.Length > 0)
                return match.Groups[1].Value;
        }
        return null;
    }

    private static string? FindShieldName(string root)
    {
        string shieldsDir = Path.Combine(root, "boards", "shields");
        if (!Directory.Exists(shieldsDir))
            return null;
        try
        {
            return Directory.GetDirectories(shieldsDir)
                .Select(Path.GetFileName)
                .Where(n => n is not null && !ShouldSkipDirectory(n))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsDirectChild(string file, string root, string dirName) =>
        Path.GetDirectoryName(file) == Path.Combine(root, dirName);

    // --- ordering / formatting ---

    private static int SourceRank(LayoutSource source) => source switch
    {
        LayoutSource.UserLayout => 0,
        LayoutSource.PhysicalLayout => 1,
        LayoutSource.InfoJson => 2,
        _ => 3,
    };

    private static List<LayoutCandidate> OrderCandidates(List<LayoutCandidate> candidates) =>
        // OrderBy/ThenBy are stable, so same-file order survives.
        [.. candidates
            .OrderBy(c => SourceRank(c.Layout.Source))
            .ThenBy(c => c.FilePath, StringComparer.Ordinal)];

    private static string Problem(string file, string root, string message) =>
        $"{Path.GetRelativePath(root, file)}: {message}";
}
