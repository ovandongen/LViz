using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using LViz.Core.GitHub;
using Xunit;

namespace LViz.Tests.GitHub;

/// <summary>
/// The local repo cache — folder layout, tar unpacking, head-commit freshness,
/// atomic swap, absorbing a file LViz committed, and cleanup — over a temp
/// root per test. HTTP goes through
/// <see cref="FakeHttpHandler"/> routed by <c>PathAndQuery</c>, and tarballs
/// are built in-test with <see cref="TarWriter"/> over a <see cref="GZipStream"/>,
/// so nothing touches the network.
/// </summary>
public sealed class GitHubRepoCacheTests : IDisposable
{
    private const string ClientId = "Iv-test";
    private const string UserAgent = "LViz-tests/1.0";
    private const string AccessToken = "ghu_testtoken";

    private const string Sha1 = "1111111111111111111111111111111111111111";
    private const string Sha2 = "2222222222222222222222222222222222222222";
    private const string Sha3 = "3333333333333333333333333333333333333333";

    private static readonly GitHubKeymapRef Keymap =
        new("o", "r", "main", "config/cosmos82.keymap");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lviz-repo-cache-tests-{Guid.NewGuid():N}");
    private readonly FakeHttpHandler _handler = new();
    private readonly GitHubClient _client;
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public GitHubRepoCacheTests()
    {
        _client = new GitHubClient(new HttpClient(_handler), ClientId, UserAgent);
        _handler.Respond = request =>
            _routes.TryGetValue(request.RequestUri!.PathAndQuery, out var respond)
                ? respond()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort: a leaked temp folder is not a test failure.
        }
    }

    // ---- ExtractTarball ----

    [Fact]
    public void Extract_StripsTopFolder_AndWritesRegularFiles()
    {
        var target = Path.Combine(_root, "target");
        var bytes = TarGz(
            DirEntry("owner-repo-abc1234/"),
            DirEntry("owner-repo-abc1234/config/"),
            FileEntry("owner-repo-abc1234/config/cosmos82.keymap", "keymap body"),
            FileEntry("owner-repo-abc1234/build.yaml", "shield: zmk"));

        GitHubRepoCache.ExtractTarball(new MemoryStream(bytes), target);

        Assert.Equal(
            new[] { "build.yaml", Path.Combine("config", "cosmos82.keymap") },
            ListedFiles(target));
        Assert.Equal(
            "keymap body",
            File.ReadAllText(Path.Combine(target, "config", "cosmos82.keymap")));
        Assert.Equal("shield: zmk", File.ReadAllText(Path.Combine(target, "build.yaml")));
    }

    [Fact]
    public void Extract_SkipsSymlinks_Traversal_DotSegments_AndTopLevelOnly()
    {
        var target = Path.Combine(_root, "target");
        var bytes = TarGz(
            FileEntry("owner-repo-abc/keep.txt", "keep"),
            SymlinkEntry("owner-repo-abc/link", "config/cosmos82.keymap"),
            FileEntry("owner-repo-abc/../evil.txt", "evil"),
            FileEntry("owner-repo-abc/a/./b.txt", "dot"),
            FileEntry("owner-repo-abc", "top-level only, nothing left after stripping"));

        GitHubRepoCache.ExtractTarball(new MemoryStream(bytes), target);

        Assert.Equal(new[] { "keep.txt" }, ListedFiles(target));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "target", "link")));
    }

    // ---- paths without any network ----

    [Fact]
    public void FolderFor_EscapesBranch_AndCachedLookupsAreNullBeforeDownload()
    {
        var cache = new GitHubRepoCache(_root);

        var folder = cache.FolderFor("o", "r", "feature/x");
        Assert.EndsWith("feature%2Fx", folder);
        Assert.Equal(Path.Combine(_root, "o", "r", "feature%2Fx"), folder);
        Assert.Equal(_root, cache.Root);

        var keymap = new GitHubKeymapRef("o", "r", "main", "config/cosmos82.keymap");
        Assert.Null(cache.LocalPathFor(keymap));
        Assert.Null(cache.CachedSha("o", "r", "main"));
    }

    // ---- RefreshAsync ----

    [Fact]
    public async Task Refresh_FirstTime_DownloadsUnpacksAndRecordsSha()
    {
        var cache = new GitHubRepoCache(_root);
        RouteHead(Sha1);
        RouteTarball(Sha1, TarGz(
            FileEntry("o-r-1111/config/cosmos82.keymap", "keymap body"),
            FileEntry("o-r-1111/build.yaml", "shield: zmk")));

        var result = await cache.RefreshAsync(_client, AccessToken, "o", "r", "main");

        Assert.True(result.Downloaded);
        Assert.Equal(Sha1, result.Sha);
        Assert.Equal(Path.Combine(_root, "o", "r", "main"), result.Folder);
        Assert.Equal(Sha1, cache.CachedSha("o", "r", "main"));

        var keymap = new GitHubKeymapRef("o", "r", "main", "config/cosmos82.keymap");
        var local = cache.LocalPathFor(keymap);
        Assert.Equal(
            Path.Combine(_root, "o", "r", "main", "config", "cosmos82.keymap"), local);
        Assert.NotNull(local);
        Assert.Equal("keymap body", File.ReadAllText(local!));

        Assert.Equal(
            new[]
            {
                GitHubRepoCache.InfoFileName,
                "build.yaml",
                Path.Combine("config", "cosmos82.keymap"),
            },
            ListedFiles(result.Folder));
        Assert.Equal(new[] { "/repos/o/r/commits/main", "/repos/o/r/tarball/" + Sha1 }, SentPaths());
        AssertNoLeftovers();
    }

    [Fact]
    public async Task Refresh_SameHead_SecondCallSendsOnlyTheHeadRequest()
    {
        var cache = new GitHubRepoCache(_root);
        RouteHead(Sha1);
        RouteTarball(Sha1, TarGz(FileEntry("o-r-1111/build.yaml", "shield: zmk")));
        await cache.RefreshAsync(_client, AccessToken, "o", "r", "main");

        _handler.Requests.Clear();
        var again = await cache.RefreshAsync(_client, AccessToken, "o", "r", "main");

        Assert.False(again.Downloaded);
        Assert.Equal(Sha1, again.Sha);
        Assert.Equal(new[] { "/repos/o/r/commits/main" }, SentPaths());
        AssertNoLeftovers();
    }

    [Fact]
    public async Task Refresh_HeadMoved_ReplacesFolderInsteadOfMerging()
    {
        var cache = new GitHubRepoCache(_root);
        RouteHead(Sha1);
        RouteTarball(Sha1, TarGz(
            FileEntry("o-r-1111/config/cosmos82.keymap", "old"),
            FileEntry("o-r-1111/build.yaml", "doomed")));
        await cache.RefreshAsync(_client, AccessToken, "o", "r", "main");

        RouteHead(Sha2);
        RouteTarball(Sha2, TarGz(FileEntry("o-r-2222/config/cosmos82.keymap", "new")));
        var result = await cache.RefreshAsync(_client, AccessToken, "o", "r", "main");

        Assert.True(result.Downloaded);
        Assert.Equal(Sha2, result.Sha);
        Assert.Equal(Sha2, cache.CachedSha("o", "r", "main"));

        // build.yaml is gone: the old copy was replaced, not merged.
        Assert.False(File.Exists(Path.Combine(result.Folder, "build.yaml")));
        Assert.Equal(
            new[] { GitHubRepoCache.InfoFileName, Path.Combine("config", "cosmos82.keymap") },
            ListedFiles(result.Folder));
        Assert.Equal(
            "new",
            File.ReadAllText(Path.Combine(result.Folder, "config", "cosmos82.keymap")));
        AssertNoLeftovers();
    }

    [Fact]
    public async Task Refresh_DownloadTooLarge_KeepsPreviousCopyAndThrows()
    {
        var cache = new GitHubRepoCache(_root);
        RouteHead(Sha1);
        RouteTarball(Sha1, TarGz(
            FileEntry("o-r-1111/config/cosmos82.keymap", "old"),
            FileEntry("o-r-1111/build.yaml", "kept")));
        var folder = (await cache.RefreshAsync(_client, AccessToken, "o", "r", "main")).Folder;

        RouteHead(Sha2);
        // Announces more than the limit; the body must never be read.
        Route("/repos/o/r/tarball/" + Sha2, () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[16]),
            };
            response.Content.Headers.ContentLength = GitHubRepoCache.MaxDownloadBytes + 1;
            return response;
        });

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => cache.RefreshAsync(_client, AccessToken, "o", "r", "main"));

        Assert.Equal(GitHubErrorKind.TooLarge, ex.Kind);
        Assert.Equal(Sha1, cache.CachedSha("o", "r", "main"));
        Assert.Equal(
            new[]
            {
                GitHubRepoCache.InfoFileName,
                "build.yaml",
                Path.Combine("config", "cosmos82.keymap"),
            },
            ListedFiles(folder));
        AssertNoLeftovers();
    }

    // ---- ApplyCommittedFile ----

    [Fact]
    public async Task ApplyCommittedFile_ParentMatchesCached_ReplacesFileAndRecordsCommit()
    {
        var cache = await CachedAtSha1Async();
        var folder = cache.FolderFor("o", "r", "main");
        var infoBefore = InfoText(folder);

        cache.ApplyCommittedFile(Keymap, Content("saved body"), new GitHubCommitResult(Sha2, Sha3, ParentSha: Sha1));

        Assert.Equal("saved body", File.ReadAllText(Path.Combine(folder, "config", "cosmos82.keymap")));
        Assert.Equal(Sha2, cache.CachedSha("o", "r", "main"));

        // Rewritten, and in the shape RefreshAsync writes: camelCase sha plus
        // a fresh round-trip timestamp.
        Assert.NotEqual(infoBefore, InfoText(folder));
        using var document = JsonDocument.Parse(InfoText(folder));
        var info = document.RootElement;
        Assert.Equal(Sha2, info.GetProperty("sha").GetString());
        Assert.True(DateTimeOffset.TryParse(
            info.GetProperty("downloadedAt").GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out _));

        // Only the keymap's file and the info file were touched.
        Assert.Equal(
            new[] { GitHubRepoCache.InfoFileName, "build.yaml", Path.Combine("config", "cosmos82.keymap") },
            ListedFiles(folder));
    }

    [Fact]
    public async Task ApplyCommittedFile_ParentIsAnotherCommit_ReplacesFileAndKeepsCachedSha()
    {
        var cache = await CachedAtSha1Async();
        var folder = cache.FolderFor("o", "r", "main");
        var infoBefore = InfoText(folder);

        cache.ApplyCommittedFile(Keymap, Content("saved body"), new GitHubCommitResult(Sha2, Sha3, ParentSha: Sha3));

        // Someone else committed in between: the editor sees its own save, and
        // the untouched SHA makes the next refresh re-download the branch.
        Assert.Equal("saved body", File.ReadAllText(Path.Combine(folder, "config", "cosmos82.keymap")));
        Assert.Equal(Sha1, cache.CachedSha("o", "r", "main"));
        Assert.Equal(infoBefore, InfoText(folder));
    }

    [Fact]
    public void ApplyCommittedFile_BranchNeverDownloaded_CreatesNothing()
    {
        var cache = new GitHubRepoCache(_root);

        cache.ApplyCommittedFile(Keymap, Content("saved body"), new GitHubCommitResult(Sha2, Sha3, ParentSha: Sha1));

        Assert.False(Directory.Exists(_root));
        Assert.False(Directory.Exists(cache.FolderFor("o", "r", "main")));
        Assert.Null(cache.LocalPathFor(Keymap));
    }

    [Fact]
    public void ApplyCommittedFile_FolderWithoutInfoFile_WritesFileWithoutRecording()
    {
        var cache = new GitHubRepoCache(_root);
        var folder = cache.FolderFor("o", "r", "main");
        Directory.CreateDirectory(folder);

        // A commit with no parent (a branch's first commit) must not be read as
        // "matches a folder that never recorded a commit".
        cache.ApplyCommittedFile(Keymap, Content("saved body"), new GitHubCommitResult(Sha2, Sha3, ParentSha: null));

        Assert.Equal("saved body", File.ReadAllText(Path.Combine(folder, "config", "cosmos82.keymap")));
        Assert.False(File.Exists(Path.Combine(folder, GitHubRepoCache.InfoFileName)));
        Assert.Null(cache.CachedSha("o", "r", "main"));
    }

    [Fact]
    public async Task ApplyCommittedFile_UnwritableTarget_KeepsCacheAndDoesNotThrow()
    {
        var cache = await CachedAtSha1Async();
        var target = Path.Combine(cache.FolderFor("o", "r", "main"), "config", "cosmos82.keymap");
        File.Delete(target);
        // A directory can never take the file's place, so the write must fail.
        Directory.CreateDirectory(target);

        cache.ApplyCommittedFile(Keymap, Content("saved body"), new GitHubCommitResult(Sha2, Sha3, ParentSha: Sha1));

        Assert.True(Directory.Exists(target));
        Assert.Equal(Sha1, cache.CachedSha("o", "r", "main"));
    }

    [Fact]
    public async Task ApplyCommittedFile_PathInNewSubFolder_CreatesTheFolders()
    {
        var cache = await CachedAtSha1Async();
        var keymap = new GitHubKeymapRef("o", "r", "main", "config/new/x.keymap");

        cache.ApplyCommittedFile(keymap, Content("fresh"), new GitHubCommitResult(Sha2, Sha3, ParentSha: Sha1));

        var local = cache.LocalPathFor(keymap);
        Assert.NotNull(local);
        Assert.Equal(Path.Combine(_root, "o", "r", "main", "config", "new", "x.keymap"), local);
        Assert.Equal("fresh", File.ReadAllText(local!));
    }

    // ---- DeleteAll ----

    [Fact]
    public void DeleteAll_RemovesRoot_AndSecondCallIsSilent()
    {
        var cache = new GitHubRepoCache(_root);
        Directory.CreateDirectory(Path.Combine(_root, "o", "r", "main"));
        File.WriteAllText(Path.Combine(_root, "o", "r", "main", "build.yaml"), "x");

        cache.DeleteAll();
        Assert.False(Directory.Exists(_root));

        cache.DeleteAll();
    }

    // ---- helpers ----

    /// <summary>Answers one path with a canned response.</summary>
    private void Route(string pathAndQuery, Func<HttpResponseMessage> respond) => _routes[pathAndQuery] = respond;

    private void RouteHead(string sha) =>
        Route("/repos/o/r/commits/main", () => FakeHttpHandler.Text(sha));

    private void RouteTarball(string sha, byte[] payload) =>
        Route(
            "/repos/o/r/tarball/" + sha,
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });

    /// <summary>
    /// A cache holding <c>o/r</c> at <c>main</c> downloaded at commit
    /// <see cref="Sha1"/>: one keymap plus a second file, so a replaced folder
    /// is visible.
    /// </summary>
    private async Task<GitHubRepoCache> CachedAtSha1Async()
    {
        var cache = new GitHubRepoCache(_root);
        RouteHead(Sha1);
        RouteTarball(Sha1, TarGz(
            FileEntry("o-r-1111/config/cosmos82.keymap", "downloaded body"),
            FileEntry("o-r-1111/build.yaml", "shield: zmk")));
        await cache.RefreshAsync(_client, AccessToken, "o", "r", "main");
        return cache;
    }

    /// <summary>The raw body of a folder's info file.</summary>
    private static string InfoText(string folder) =>
        File.ReadAllText(Path.Combine(folder, GitHubRepoCache.InfoFileName));

    private static byte[] Content(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The paths every request went to, in order.</summary>
    private string[] SentPaths() =>
        _handler.Requests.Select(entry => entry.Request.RequestUri!.PathAndQuery).ToArray();

    /// <summary>Every file under a folder, relative, sorted — the exact contents.</summary>
    private static string[] ListedFiles(string folder) =>
        Directory
            .GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(folder, file))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();

    /// <summary>No half-finished downloads or unpacks left in the root.</summary>
    private void AssertNoLeftovers()
    {
        var leftovers = Directory
            .GetFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Where(entry =>
            {
                var name = Path.GetFileName(entry);
                return name.StartsWith(".download-", StringComparison.Ordinal)
                    || name.Contains(".new-", StringComparison.Ordinal);
            })
            .ToArray();
        Assert.Empty(leftovers);
    }

    // ---- tarball building (GitHub archives are gzip'd tars) ----

    private static byte[] TarGz(params GnuTarEntry[] entries)
    {
        using var outer = new MemoryStream();
        using (var gzip = new GZipStream(outer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Gnu, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                tar.WriteEntry(entry);
            }
        }
        return outer.ToArray();
    }

    private static GnuTarEntry FileEntry(string name, string content) =>
        new(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
        };

    private static GnuTarEntry DirEntry(string name) => new(TarEntryType.Directory, name);

    private static GnuTarEntry SymlinkEntry(string name, string target) =>
        new(TarEntryType.SymbolicLink, name) { LinkName = target };
}
