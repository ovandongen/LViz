using System.IO.Compression;
using System.Text.RegularExpressions;

namespace LViz.Core.Firmware;

/// <summary>
/// The local firmware cache: a downloaded <c>firmware</c> artifact zip is
/// unpacked once into app data and read from there whenever a half is
/// flashed again. One folder per commit —
/// <c>{root}/{owner}/{repo}/{sha}/</c> — holding the zip's <c>.uf2</c> files
/// flattened to the folder root, plus an empty <c>.complete</c> marker: a
/// folder without it is a half-finished store and never reads back.
/// After every store the repo folder is pruned to the newest
/// <see cref="KeepPerRepo"/> commits, so the cache cannot grow without
/// bound. <c>flash-history.json</c> (see <see cref="FirmwareFlashHistory"/>)
/// lives at the top of the same root.
/// </summary>
public sealed class FirmwareCache(string root)
{
    /// <summary>Where the app keeps unpacked firmware: <c>ApplicationData/LViz/firmware</c>.</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LViz", "firmware");

    /// <summary>How many commit folders one repo may keep before the oldest go.</summary>
    public const int KeepPerRepo = 5;

    /// <summary>Empty file inside a commit folder: everything was extracted.</summary>
    private const string MarkerFileName = ".complete";

    private const string Uf2Suffix = ".uf2";
    private const long MaxEntryBytes = 16 * 1024 * 1024;

    private static readonly Regex RepoPartPattern = new("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex ShaPattern = new("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The cache folder for one commit: <c>{root}/{owner}/{repo}/{sha}</c>
    /// with the SHA lower-cased. Folder names are exactly what a GitHub URL
    /// may hold, so nothing here can climb out of the root.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The owner or repo is not <c>[A-Za-z0-9._-]+</c> (and not <c>.</c> or
    /// <c>..</c>), or the SHA is not 40 hex characters.
    /// </exception>
    public string FolderFor(string owner, string repo, string sha)
    {
        if (!IsFolderPart(owner))
            throw new ArgumentException($"Owner is not a usable folder name: '{owner}'.", nameof(owner));
        if (!IsFolderPart(repo))
            throw new ArgumentException($"Repo is not a usable folder name: '{repo}'.", nameof(repo));
        if (sha is null || !ShaPattern.IsMatch(sha))
            throw new ArgumentException($"Commit SHA must be 40 hex characters: '{sha}'.", nameof(sha));

        return Path.Combine(root, owner!, repo!, sha.ToLowerInvariant());
    }

    /// <summary>
    /// The cached <c>.uf2</c> files of a commit, as full paths sorted
    /// ordinally by file name, or null when the commit is not cached (no
    /// folder, or one still missing its <c>.complete</c> marker).
    /// </summary>
    public IReadOnlyList<string>? TryGet(string owner, string repo, string sha)
    {
        var folder = FolderFor(owner, repo, sha);
        return File.Exists(Path.Combine(folder, MarkerFileName)) ? ListUf2(folder) : null;
    }

    /// <summary>
    /// Unpacks a <c>firmware</c> artifact zip into the commit's cache
    /// folder: every <c>.uf2</c> entry (other files are ignored), flattened
    /// to the folder root and replacing whatever was cached for this commit
    /// before. Extraction lands in a <c>.partial</c> sibling that only
    /// becomes the cache folder once complete, so a failed store never
    /// leaves a readable folder behind (a stray <c>.partial</c> the next
    /// store clears aside); then the repo is pruned to the newest
    /// <see cref="KeepPerRepo"/> commits.
    /// </summary>
    /// <returns>The stored files, exactly what <see cref="TryGet"/> returns.</returns>
    /// <exception cref="InvalidDataException">
    /// Two entries share a file name, an entry is over 16 MiB (declared or
    /// counted while copying), or the zip holds no <c>.uf2</c> at all.
    /// </exception>
    public IReadOnlyList<string> Store(string owner, string repo, string sha, Stream zip)
    {
        var folder = FolderFor(owner, repo, sha);
        var partial = folder + ".partial";

        // Entries can only be opened while the archive is open; the list is
        // still fully validated before anything is created on disk, so a
        // refusal leaves neither the folder nor a .partial sibling.
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true))
        {
            var entries = new List<(ZipArchiveEntry Entry, string FileName)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(Uf2Suffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var fileName = Path.GetFileName(entry.FullName);
                if (!seen.Add(fileName))
                    throw new InvalidDataException($"the artifact has two .uf2 files named '{fileName}'");
                if (entry.Length > MaxEntryBytes)
                    throw new InvalidDataException($"'{fileName}' is larger than 16 MiB");
                entries.Add((entry, fileName));
            }

            if (entries.Count == 0)
                throw new InvalidDataException("the artifact has no .uf2 files");

            // A failed store can leave this .partial behind; the next
            // store of this commit clears it before writing again.
            if (Directory.Exists(partial))
                Directory.Delete(partial, recursive: true);

            Directory.CreateDirectory(partial);
            foreach (var (entry, fileName) in entries)
                ExtractBounded(entry, Path.Combine(partial, fileName));
            File.WriteAllBytes(Path.Combine(partial, MarkerFileName), []);
        }

        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.Move(partial, folder);

        Prune(Path.GetDirectoryName(folder), fresh: folder);

        return TryGet(owner, repo, sha)
            ?? throw new InvalidOperationException($"the cache folder '{folder}' vanished after the store");
    }

    /// <summary>
    /// Wipes every downloaded build: each owner folder — and everything
    /// below it — is deleted, while files at the top of the root,
    /// <c>flash-history.json</c> included, stay (they are the user's own
    /// records, not private firmware). A missing root is a no-op. Called on
    /// builds sign-out so private firmware doesn't outlive the login.
    /// </summary>
    public void DeleteBuilds()
    {
        if (!Directory.Exists(root))
            return;

        foreach (var ownerFolder in Directory.EnumerateDirectories(root))
            Directory.Delete(ownerFolder, recursive: true);
    }

    /// <summary>
    /// Owner and repo spellings that may become a folder name: GitHub-style
    /// names only, never a traversal — the regex would happily match
    /// <c>.</c> and <c>..</c>.
    /// </summary>
    private static bool IsFolderPart(string? name) =>
        name is not null && name is not "." and not ".." && RepoPartPattern.IsMatch(name);

    private static IReadOnlyList<string> ListUf2(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(path => path.EndsWith(Uf2Suffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Copies an entry, refusing to grow past the limit even when its
    /// declared <see cref="ZipArchiveEntry.Length"/> lies.
    /// </summary>
    private static void ExtractBounded(ZipArchiveEntry entry, string target)
    {
        using var source = entry.Open();
        using var sink = File.Create(target);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            if (written > MaxEntryBytes)
                throw new InvalidDataException($"'{entry.FullName}' expands past 16 MiB");
            sink.Write(buffer, 0, read);
        }
    }

    /// <summary>
    /// Leaves the <see cref="KeepPerRepo"/> newest commit folders of a repo,
    /// judged by the <c>.complete</c> marker's write time — a folder without
    /// one counts as oldest — and never prunes the folder just stored, even
    /// when its marker time somehow puts it last.
    /// </summary>
    private static void Prune(string? repoFolder, string fresh)
    {
        if (string.IsNullOrEmpty(repoFolder) || !Directory.Exists(repoFolder))
            return;

        var folders = Directory.EnumerateDirectories(repoFolder)
            .Select(path => (Path: path, MarkerTime: TimeOfMarker(path)))
            .OrderByDescending(f => f.MarkerTime)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        var keep = folders.Take(KeepPerRepo).Select(f => f.Path).ToList();
        if (!keep.Contains(fresh, StringComparer.Ordinal))
        {
            keep.RemoveAt(keep.Count - 1);
            keep.Add(fresh);
        }

        var keepSet = keep.ToHashSet(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (!keepSet.Contains(folder.Path))
                Directory.Delete(folder.Path, recursive: true);
        }
    }

    private static DateTime TimeOfMarker(string folder)
    {
        var marker = Path.Combine(folder, MarkerFileName);
        return File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : DateTime.MinValue;
    }
}
