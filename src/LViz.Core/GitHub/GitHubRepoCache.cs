using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using LViz.Core.Diagnostics;
using LViz.Core.Persistence;

namespace LViz.Core.GitHub;

/// <summary>
/// What one <see cref="GitHubRepoCache.RefreshAsync"/> call produced: the
/// on-disk folder of the repo copy, the branch head it matches, and whether a
/// tarball was actually downloaded (<c>false</c> when the cache was already
/// current).
/// </summary>
public sealed record GitHubCacheResult(string Folder, string Sha, bool Downloaded);

/// <summary>
/// Keeps a local copy of each opened GitHub repo + branch under
/// <c>ApplicationData/LViz/github/{owner}/{repo}/{branch}/</c> (the branch is
/// url-escaped so <c>feature/x</c> stays one folder), so the existing
/// <c>ZmkConfigScanner</c> and keymap loader can read it like any local
/// zmk-config folder.
///
/// <para>
/// Freshness is the branch's head commit: <see cref="RefreshAsync"/> asks
/// GitHub for it first and only downloads when it moved. A new copy is
/// unpacked into a sibling temp folder and swapped in at the end, so a failed
/// or oversized download never leaves a half-written cache folder. Archives
/// over <see cref="MaxDownloadBytes"/> are refused by the client, and tar
/// entries that are not regular files or that would land outside the folder
/// are skipped by <see cref="ExtractTarball"/>.
/// </para>
/// </summary>
public sealed class GitHubRepoCache
{
    /// <summary>Archives announced or read larger than this are refused.</summary>
    public const long MaxDownloadBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Small info file inside each cached folder holding
    /// <c>{"sha":"…","downloadedAt":"…"}</c>. Hidden so board scanners that
    /// walk the folder overlook it.
    /// </summary>
    public const string InfoFileName = ".lviz-cache.json";

    /// <summary>Where the app keeps repo copies: <c>ApplicationData/LViz/github</c>.</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LViz", "github");

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private static readonly JsonSerializerOptions InfoJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <inheritdoc cref="GitHubRepoCache(string)"/>
    /// <param name="root">Cache root; each repo+branch gets folders below it.</param>
    public GitHubRepoCache(string root) => Root = root;

    /// <summary>The cache root this instance reads and writes.</summary>
    public string Root { get; }

    /// <summary>
    /// The folder holding <c>owner/repo</c> at <paramref name="branch"/>:
    /// <c>{Root}/{owner}/{repo}/{escaped-branch}</c>. Exists only after a
    /// successful refresh.
    /// </summary>
    public string FolderFor(string owner, string repo, string branch) =>
        Path.Combine(Root, owner, repo, Uri.EscapeDataString(branch));

    /// <summary>
    /// The cached copy of <paramref name="keymap"/>'s file, or null when it is
    /// not on disk. Pure lookup — no network.
    /// </summary>
    public string? LocalPathFor(GitHubKeymapRef keymap)
    {
        var path = CachedPathFor(keymap);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Where <paramref name="keymap"/>'s file lives inside its branch folder,
    /// whether or not anything is there. The repo-root-relative path is split
    /// so <c>/</c> becomes this OS's separator.
    /// </summary>
    private string CachedPathFor(GitHubKeymapRef keymap) =>
        Path.Combine(
            FolderFor(keymap.Owner, keymap.Repo, keymap.Branch),
            Path.Combine(keymap.Path.Split('/')));

    /// <summary>
    /// The commit SHA the cached folder was downloaded at, or null when
    /// nothing is cached or the info file is unreadable. Never throws.
    /// </summary>
    public string? CachedSha(string owner, string repo, string branch)
    {
        var path = Path.Combine(FolderFor(owner, repo, branch), InfoFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            // TryGetProperty throws on a non-object root (AGENTS.md rule).
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("sha", out var sha)
                && sha.ValueKind == JsonValueKind.String
                ? sha.GetString()
                : null;
        }
        catch (Exception ex)
        {
            // "missing" was already filtered above; anything else means the
            // file is corrupt or locked — treat it as "nothing cached".
            DiagnosticLog.Warn("GitHub", $"{path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Makes the cached copy of <c>owner/repo</c> at <paramref name="branch"/>
    /// match GitHub: reads the branch head, and downloads and unpacks the
    /// commit's tarball only when the head differs from
    /// <see cref="CachedSha"/> or the folder is gone.
    ///
    /// <para>
    /// The archive lands in a temp file inside <see cref="Root"/> and the copy
    /// is unpacked into a sibling temp folder that is moved over the old one
    /// at the very end, so any failure (including
    /// <see cref="GitHubErrorKind.TooLarge"/>) leaves the previous cache
    /// untouched and cleans up its own temp files.
    /// </para>
    /// </summary>
    /// <exception cref="GitHubApiException">Any GitHub failure, mapped by the client.</exception>
    public async Task<GitHubCacheResult> RefreshAsync(
        GitHubClient client,
        string accessToken,
        string owner,
        string repo,
        string branch,
        CancellationToken ct = default)
    {
        var sha = await client.GetBranchHeadAsync(accessToken, owner, repo, branch, ct);
        var folder = FolderFor(owner, repo, branch);

        if (Directory.Exists(folder) && CachedSha(owner, repo, branch) == sha)
        {
            return new GitHubCacheResult(folder, sha, Downloaded: false);
        }

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);

        var tempFile = Path.Combine(Root, $".download-{Guid.NewGuid():N}.tar.gz");
        var tempFolder = folder + ".new-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            using (var download = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await client.DownloadTarballAsync(accessToken, owner, repo, sha, download, MaxDownloadBytes, ct);
            }

            // Off the caller's thread: the awaits above resume on the UI
            // context, and unpacking is synchronous disk work.
            await Task.Run(() =>
            {
                using var archive = File.OpenRead(tempFile);
                ExtractTarball(archive, tempFolder);
            }, ct);

            await AtomicFile.WriteAllTextAsync(
                Path.Combine(tempFolder, InfoFileName),
                InfoJson(sha),
                ct);

            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
            Directory.Move(tempFolder, folder);

            return new GitHubCacheResult(folder, sha, Downloaded: true);
        }
        catch
        {
            TryDeleteFolder(tempFolder);
            throw;
        }
        finally
        {
            TryDeleteFile(tempFile);
        }
    }

    /// <summary>
    /// Brings the cached copy of a branch in step with a file LViz just
    /// committed to it, so the editor reads back what it saved without a
    /// re-download.
    ///
    /// <para>
    /// A branch that was never downloaded is left untouched — there is nothing
    /// to update, and no folder is created. Otherwise
    /// <paramref name="content"/> replaces the keymap's file inside the folder
    /// (subfolders created as needed). When the commit's parent is the commit
    /// the folder was downloaded at, nothing else moved in between, so the new
    /// commit becomes the cached SHA and the next
    /// <see cref="RefreshAsync"/> sees the folder as current. With any other
    /// parent someone else committed other files: the file is still written,
    /// but the recorded SHA is left alone so the next background check sees the
    /// moved head and re-downloads the branch.
    /// </para>
    ///
    /// <para>
    /// The commit already landed on GitHub, so nothing here may fail the save:
    /// IO trouble is logged under the <c>GitHub</c> tag, and a stale cache
    /// costs a re-download.
    /// </para>
    /// </summary>
    public void ApplyCommittedFile(GitHubKeymapRef keymap, byte[] content, GitHubCommitResult commit)
    {
        var folder = FolderFor(keymap.Owner, keymap.Repo, keymap.Branch);
        if (!Directory.Exists(folder))
        {
            return;
        }

        try
        {
            var target = CachedPathFor(keymap);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            AtomicFile.WriteAllBytes(target, content);

            // Only a commit on top of exactly what we hold proves the rest of
            // the folder is still current.
            if (commit.ParentSha is not null
                && commit.ParentSha == CachedSha(keymap.Owner, keymap.Repo, keymap.Branch))
            {
                AtomicFile.WriteAllText(Path.Combine(folder, InfoFileName), InfoJson(commit.CommitSha));
            }
        }
        catch (Exception ex)
        {
            // Broad on purpose: the save itself succeeded, so a cache we
            // cannot update must never surface as an error.
            DiagnosticLog.Warn("GitHub", $"apply committed file {keymap}: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes the whole cache. Never throws: IO failures are logged to the
    /// diagnostics log under the <c>GitHub</c> tag instead.
    /// </summary>
    public void DeleteAll()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn("GitHub", $"delete cache {Root}: {ex.Message}");
        }
    }

    /// <summary>
    /// Unpacks a gzip'd tar (a GitHub archive: every entry under one top
    /// folder) into <paramref name="targetFolder"/>, which is created. The
    /// first path segment is stripped; only regular-file entries are written.
    ///
    /// <para>
    /// Everything else is skipped: directories (parents are created on demand
    /// instead), symlinks and hard links, header entries, entries whose name
    /// holds nothing after the top folder, and any segment that is empty,
    /// <c>.</c>, <c>..</c>, contains <c>:</c> or a character this OS forbids in
    /// file names — plus a final check that the
    /// resolved path stays inside the target folder.
    /// </para>
    /// </summary>
    internal static void ExtractTarball(Stream gzipTar, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);
        var targetFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetFolder));

        using var reader = new TarReader(new GZipStream(gzipTar, CompressionMode.Decompress));

        while (reader.GetNextEntry() is TarEntry entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }

            var destination = ResolveTargetPath(entry.Name, targetFull);
            if (destination is null)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var output = File.Create(destination);
            entry.DataStream?.CopyTo(output);
        }
    }

    /// <summary>
    /// Where a tar entry may be written: strip the archive's top folder, reject
    /// traversal-ish segments, and require the resolved path to sit inside
    /// <paramref name="targetFull"/> (already an absolute path). Null means
    /// "skip this entry".
    /// </summary>
    private static string? ResolveTargetPath(string entryName, string targetFull)
    {
        var normalized = entryName.Replace('\\', '/');

        var slash = normalized.IndexOf('/');
        if (slash < 0)
        {
            return null; // the archive's own top folder — nothing left to place
        }

        var rest = normalized[(slash + 1)..];
        if (rest.Length == 0)
        {
            return null;
        }

        var segments = rest.Split('/');
        foreach (var segment in segments)
        {
            // Invalid file-name characters differ per OS (Windows refuses
            // <>|"?* that a Linux-made repo may carry); skipping one such
            // file beats failing the whole download.
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.Contains(':')
                || segment.IndexOfAny(InvalidNameChars) >= 0)
            {
                return null;
            }
        }

        var full = Path.GetFullPath(Path.Combine(targetFull, Path.Combine(segments)));
        return full.StartsWith(targetFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full
            : null;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("GitHub", $"delete temp file {path}: {ex.Message}");
        }
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("GitHub", $"delete temp folder {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// The body of <see cref="InfoFileName"/> recording <paramref name="sha"/>
    /// as the commit the folder matches, stamped now. Written with
    /// <see cref="AtomicFile"/> by both the download path and
    /// <see cref="ApplyCommittedFile"/>.
    /// </summary>
    private static string InfoJson(string sha) =>
        JsonSerializer.Serialize(
            new StoredInfo(sha, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            InfoJsonOptions);

    /// <summary>Wire shape of <see cref="InfoFileName"/>; declaration order is the on-disk order.</summary>
    private sealed record StoredInfo(string Sha, string DownloadedAt);
}
