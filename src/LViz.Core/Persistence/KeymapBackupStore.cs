using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LViz.Core.Persistence;

/// <summary>
/// Pre-save snapshots of locally opened keymap files. Every save first copies
/// the file as it sits on disk into <c>{root}/{key}/{utc stamp}.keymap</c> and
/// keeps the newest <see cref="Keep"/> per keymap file, older ones are deleted.
/// </summary>
/// <remarks>
/// The folder name is a hash of the full path rather than the file name, so it stays
/// short, stays valid for any name, and never collides between two boards whose
/// keymaps are both called <c>default.keymap</c>. Each folder also holds
/// <see cref="SourceFileName"/> with the real path, so a later history UI can name the
/// file its backups belong to. Restoring is out of scope here: this stores and lists.
/// </remarks>
public sealed class KeymapBackupStore(string root, Func<DateTimeOffset>? now = null)
{
    /// <summary>How many snapshots to keep per keymap file.</summary>
    public const int Keep = 20;

    /// <summary>Inside a backup folder: the full path of the keymap the snapshots came from.</summary>
    public const string SourceFileName = "source.txt";

    /// <summary>Extension of a stored snapshot.</summary>
    public const string BackupExtension = ".keymap";

    /// <summary>Leading hex chars of the SHA-256 of the full path — the folder name.</summary>
    private const int KeyHexLength = 16;

    private readonly Func<DateTimeOffset> _now = now is not null ? now : static () => DateTimeOffset.UtcNow;

    /// <summary>Where the app keeps keymap snapshots: <c>ApplicationData/LViz/keymap-backups</c>.</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LViz", "keymap-backups");

    /// <summary>Root folder holding one subfolder per keymap file.</summary>
    public string Root { get; } = root;

    /// <summary>
    /// Copies the file's current bytes to a timestamped snapshot and returns its
    /// path. Snapshots taken in the same millisecond get <c>-2</c>, <c>-3</c>, …
    /// appended, and everything past the newest <see cref="Keep"/> is deleted.
    /// </summary>
    /// <exception cref="FileNotFoundException">There is no file on disk to snapshot.</exception>
    public string Backup(string keymapPath)
    {
        var full = Path.GetFullPath(keymapPath);
        if (!File.Exists(full))
            throw new FileNotFoundException($"Nothing to back up at {full}.", full);

        var folder = FolderFor(full);
        Directory.CreateDirectory(folder);

        var source = Path.Combine(folder, SourceFileName);
        if (!File.Exists(source))
            AtomicFile.WriteAllText(source, full);

        var stamp = _now().ToUniversalTime().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var target = Path.Combine(folder, stamp + BackupExtension);
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(folder, $"{stamp}-{n}{BackupExtension}");

        AtomicFile.WriteAllBytes(target, File.ReadAllBytes(full));

        foreach (var stale in BackupsNewestFirst(folder).Skip(Keep))
            File.Delete(stale);

        return target;
    }

    /// <summary>Snapshots of this keymap, newest first; empty while it has no folder.</summary>
    public IReadOnlyList<string> List(string keymapPath)
    {
        var folder = FolderFor(Path.GetFullPath(keymapPath));
        return Directory.Exists(folder) ? BackupsNewestFirst(folder) : [];
    }

    private string FolderFor(string fullPath) => Path.Combine(Root, KeyFor(fullPath));

    private static string KeyFor(string fullPath) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)))[..KeyHexLength];

    /// <summary>
    /// Snapshot paths in one folder, newest first; the time-stamped names sort by time.
    /// The extension is checked by hand because a Win32 `*.keymap` pattern also matches
    /// through 8.3 short names, which would drag an orphaned `….keymap.tmp` in.
    /// </summary>
    private static IReadOnlyList<string> BackupsNewestFirst(string folder)
    {
        var paths = Directory.GetFiles(folder)
            .Where(path => Path.GetFileName(path).EndsWith(BackupExtension, StringComparison.Ordinal))
            .ToList();
        paths.Sort(static (a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
        paths.Reverse();
        return paths;
    }
}
