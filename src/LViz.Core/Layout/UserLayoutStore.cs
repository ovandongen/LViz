using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using LViz.Core.Diagnostics;
using LViz.Core.Persistence;

namespace LViz.Core.Layout;

/// <summary>
/// Loads and saves <see cref="UserLayoutFile"/>s — JSON board layouts sitting in
/// a folder next to <c>settings.json</c> — as opposed to the compiled profiles in
/// <see cref="KeyboardProfileRegistry"/>.
/// <para>
/// Every malformed file is skipped with a <see cref="DiagnosticLog"/> warning
/// instead of throwing: one broken layout must never stop the app from starting
/// with the layouts that do parse.
/// </para>
/// </summary>
public static class UserLayoutStore
{
    /// <summary>
    /// Only files whose name ends in this (with a non-empty stem) are read.
    /// Checked by hand rather than with a <c>*.layout.json</c> glob because
    /// Windows file matching also consults 8.3 short names, which lets a
    /// <c>x.layout.json.bak</c> slip into a glob it should not match.
    /// </summary>
    private const string FileSuffix = ".layout.json";

    private const string LogTag = "Layouts";

    /// <summary>Where the app looks for layouts: <c>ApplicationData/LViz/layouts</c>.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LViz", "layouts");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9_-]*$", RegexOptions.NonBacktracking);

    /// <summary>
    /// Every layout in <paramref name="directory"/>, ordered by ordinal file name.
    /// A missing directory yields an empty list; a file that fails any check is
    /// skipped and logged, and the rest still load. A file loads only when its
    /// name is <c>&lt;id&gt;.layout.json</c>; on a case-sensitive file system two
    /// case variants of that name can still share an id, and the first by file
    /// name wins.
    /// </summary>
    public static IReadOnlyList<UserLayoutFile> LoadAll(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        string[] entries;
        try
        {
            entries = Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(LogTag, $"{directory}: {ex.Message}");
            return [];
        }

        var fileNames = new List<string>();
        foreach (var path in entries)
        {
            var name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(name) && name.Length > FileSuffix.Length &&
                name.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                fileNames.Add(name);
            }
        }
        fileNames.Sort(StringComparer.Ordinal);

        var layouts = new List<UserLayoutFile>(fileNames.Count);
        var loadedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fileName in fileNames)
        {
            if (!TryRead(Path.Combine(directory, fileName), out var layout, out var reason))
            {
                DiagnosticLog.Warn(LogTag, $"{fileName}: {reason}");
                continue;
            }

            if (!loadedIds.Add(layout.Id))
            {
                DiagnosticLog.Warn(LogTag,
                    $"{fileName}: duplicate id '{layout.Id}'; keeping the earlier file");
                continue;
            }

            layouts.Add(layout);
        }

        return layouts;
    }

    /// <summary>
    /// Writes <c>&lt;id&gt;.layout.json</c> into <paramref name="directory"/>,
    /// creating the directory when needed and overwriting an existing file for
    /// that id.
    /// </summary>
    /// <exception cref="ArgumentException">The id could never be loaded back —
    /// it does not match <c>[a-z0-9][a-z0-9_-]*</c>, so it would also be an
    /// unsafe file-name stem.</exception>
    public static void Save(UserLayoutFile layout, string directory)
    {
        if (!IdPattern.IsMatch(layout.Id))
            throw new ArgumentException(
                $"Layout id '{layout.Id}' does not match [a-z0-9][a-z0-9_-]* and could never be loaded back.",
                nameof(layout));

        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(StoredLayout.From(layout), JsonOptions);
        AtomicFile.WriteAllText(Path.Combine(directory, layout.Id + FileSuffix), json);
    }

    /// <summary>
    /// Reads one layout file. False plus a one-line reason when the file is
    /// unparseable or fails validation — never an exception.
    /// </summary>
    private static bool TryRead(
        string path,
        [NotNullWhen(true)] out UserLayoutFile? layout,
        out string? reason)
    {
        layout = null;

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = ex.Message;
            return false;
        }

        StoredLayout? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredLayout>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            reason = $"invalid JSON: {ex.Message}";
            return false;
        }

        if (stored is null)
        {
            reason = "invalid JSON: not a layout object";
            return false;
        }

        if (stored.SchemaVersion != UserLayoutFile.CurrentSchemaVersion)
        {
            reason = $"unsupported schemaVersion {stored.SchemaVersion} " +
                     $"(this build reads {UserLayoutFile.CurrentSchemaVersion})";
            return false;
        }

        if (stored.Id is null || !IdPattern.IsMatch(stored.Id))
        {
            reason = $"invalid id '{stored.Id}' (expected [a-z0-9][a-z0-9_-]*)";
            return false;
        }

        // The wizard saves and deletes by <id>.layout.json, so a renamed copy
        // still carrying the original's id would shadow (or be shadowed by) the
        // real file. Case-insensitive like the suffix check.
        var stem = Path.GetFileName(path)[..^FileSuffix.Length];
        if (!stem.Equals(stored.Id, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"file name does not match id '{stored.Id}' (expected {stored.Id}{FileSuffix})";
            return false;
        }

        if (string.IsNullOrWhiteSpace(stored.Name))
        {
            reason = "name is blank";
            return false;
        }

        if (stored.Keys is null || stored.Keys.Count == 0)
        {
            reason = "no keys";
            return false;
        }

        var keys = new List<UserLayoutKey>(stored.Keys.Count);
        for (var i = 0; i < stored.Keys.Count; i++)
        {
            var storedKey = stored.Keys[i];
            if (storedKey is null)
            {
                reason = $"key {i} is not an object";
                return false;
            }

            if (storedKey.W <= 0 || storedKey.H <= 0)
            {
                reason = $"key {i} has w={storedKey.W} h={storedKey.H}; both must be positive";
                return false;
            }

            if (!TryHand(storedKey.Hand, out var hand))
            {
                reason = $"key {i} has hand {FormatHand(storedKey.Hand)}; expected \"left\" or \"right\"";
                return false;
            }

            keys.Add(new UserLayoutKey(
                storedKey.X, storedKey.Y, storedKey.W, storedKey.H,
                storedKey.R, storedKey.Rx, storedKey.Ry, hand));
        }

        layout = new UserLayoutFile(
            stored.SchemaVersion, stored.Id, stored.Name, stored.HidName, keys);
        reason = null;
        return true;
    }

    /// <summary>
    /// A user layout names its hand in lower case on disk. Read tolerantly —
    /// <c>"Left"</c> is still the left hand — but written canonically.
    /// </summary>
    private static bool TryHand(string? value, out Hand hand)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "left":
                hand = Hand.Left;
                return true;
            case "right":
                hand = Hand.Right;
                return true;
            default:
                hand = default;
                return false;
        }
    }

    private static string FormatHand(string? value) =>
        value is null ? "null" : $"\"{value}\"";

    /// <summary>
    /// Wire shape of a layout file. Kept separate from <see cref="UserLayoutFile"/>
    /// so a missing or unrecognised value stays observable: the domain record's
    /// non-nullable members would silently default an absent <c>hand</c> to
    /// <c>Hand.Left</c>, and the store has to reject that instead.
    /// Property order is the on-disk order.
    /// </summary>
    private sealed class StoredLayout
    {
        public int SchemaVersion { get; init; }
        public string? Id { get; init; }
        public string? Name { get; init; }
        public string? HidName { get; init; }
        public List<StoredKey?>? Keys { get; init; }

        public static StoredLayout From(UserLayoutFile layout) => new()
        {
            SchemaVersion = layout.SchemaVersion,
            Id = layout.Id,
            Name = layout.Name,
            HidName = layout.HidName,
            Keys =
            [
                .. layout.Keys.Select(key => (StoredKey?)new StoredKey
                {
                    X = key.X,
                    Y = key.Y,
                    W = key.W,
                    H = key.H,
                    R = key.R,
                    Rx = key.Rx,
                    Ry = key.Ry,
                    Hand = key.Hand == Hand.Left ? "left" : "right",
                }),
            ],
        };
    }

    private sealed class StoredKey
    {
        public int X { get; init; }
        public int Y { get; init; }
        public int W { get; init; }
        public int H { get; init; }
        public int R { get; init; }
        public int Rx { get; init; }
        public int Ry { get; init; }
        public string? Hand { get; init; }
    }
}
