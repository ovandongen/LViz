using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using LViz.App;
using LViz.App.Services.GitHub;
using LViz.Core.GitHub;
using LViz.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LViz.Tests.GitHub;

/// <summary>
/// <see cref="GitHubKeymapService"/> — tying client, session and cache
/// together: restore-once, sign-in completion, sign-out wiping the cache,
/// the open flow with its offline fallback, and the save flow (commit,
/// conflict, overwrite, keeping the cache copy in step). Runs on a real
/// <see cref="GitHubClient"/> over <see cref="FakeHttpHandler"/>, an
/// <see cref="InMemorySecretStore"/>, a temp cache root and tarballs built
/// in-test with <see cref="TarWriter"/>, so nothing touches the network.
/// "Signed in" means valid tokens written into the store before the first
/// service call.
/// </summary>
public sealed class GitHubKeymapServiceTests : IDisposable
{
    private const string ClientId = "Iv-test";
    private const string UserAgent = "LViz-tests/1.0";
    private const string Login = "octocat";
    private const string AccessToken = "ghu_testtoken";
    private const string RefreshToken = "ghr_testrefresh";

    private const string Sha1 = "1111111111111111111111111111111111111111";

    /// <summary>Where the service commits <see cref="Keymap"/> to / reads its
    /// current blob SHA from.</summary>
    private const string ContentsPath = "/repos/o/r/contents/config/cosmos82.keymap";

    /// <summary>What <see cref="NewContent"/> was loaded as, per the editor.</summary>
    private const string LoadedBlobSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>The blob SHA currently on GitHub — what "overwrite" commits with.</summary>
    private const string RemoteBlobSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string CommitSha = "cccccccccccccccccccccccccccccccccccccccc";

    private static readonly byte[] NewContent = "layers { edited }\n"u8.ToArray();

    private static readonly GitHubKeymapRef Keymap =
        new("o", "r", "main", "config/cosmos82.keymap");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"lviz-keymap-service-tests-{Guid.NewGuid():N}");
    private readonly FakeHttpHandler _handler = new();
    private readonly InMemorySecretStore _store = new();
    private readonly GitHubClient _client;
    private readonly GitHubKeymapService _service;
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public GitHubKeymapServiceTests()
    {
        _client = new GitHubClient(new HttpClient(_handler), ClientId, UserAgent);
        _handler.Respond = request =>
            _routes.TryGetValue(request.RequestUri!.PathAndQuery, out var respond)
                ? respond()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        _service = CreateService(_store);
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

    // ---- restore ----

    [Fact]
    public async Task EnsureRestored_ConcurrentAndLaterCalls_ReadStoreOnce()
    {
        // Empty store on purpose: an unsigned-in session re-reads on every
        // RestoreAsync call, so the single read pins THIS service's task
        // cache, not the session's own signed-in short-circuit.
        var counting = new CountingSecretStore(_store);
        var service = CreateService(counting);

        await Task.WhenAll(service.EnsureRestoredAsync(), service.EnsureRestoredAsync());
        await service.EnsureRestoredAsync();

        Assert.Equal(1, counting.Reads);
        Assert.False(service.IsSignedIn);
    }

    // ---- OpenAsync ----

    [Fact]
    public async Task Open_SignedOut_FailsUnauthorized_WithoutHttpRequest()
    {
        var result = await _service.OpenAsync(Keymap);

        var failed = Assert.IsType<GitHubOpenResult.Failed>(result);
        Assert.Equal(GitHubErrorKind.Unauthorized, failed.Kind);
        Assert.Equal("Not signed in", failed.Message);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Open_SignedIn_DownloadsAndLoadsFile_NotFromCache()
    {
        await StoreSignedInAsync();
        RouteHead(Sha1);
        RouteTarball(Sha1, TarGz(FileEntry("o-r-1111/config/cosmos82.keymap", "keymap body")));

        var result = await _service.OpenAsync(Keymap);

        var loaded = Assert.IsType<GitHubOpenResult.Loaded>(result);
        Assert.False(loaded.FromCacheOffline);
        Assert.True(File.Exists(loaded.LocalPath));
        Assert.EndsWith("cosmos82.keymap", loaded.LocalPath);
        Assert.Equal("keymap body", File.ReadAllText(loaded.LocalPath));
    }

    [Fact]
    public async Task Open_FileNotInTarball_FailsNotFound()
    {
        await StoreSignedInAsync();
        RouteHead(Sha1);
        RouteTarball(Sha1, TarGz(FileEntry("o-r-1111/build.yaml", "shield: zmk")));

        var result = await _service.OpenAsync(Keymap);

        var failed = Assert.IsType<GitHubOpenResult.Failed>(result);
        Assert.Equal(GitHubErrorKind.NotFound, failed.Kind);
        Assert.Equal("config/cosmos82.keymap is not in o/r (main)", failed.Message);
    }

    [Fact]
    public async Task Open_GitHubUnreachableAfterSuccessfulOpen_LoadsCachedCopy()
    {
        await StoreSignedInAsync();
        RouteHead(Sha1);
        RouteTarball(Sha1, TarGz(FileEntry("o-r-1111/config/cosmos82.keymap", "keymap body")));
        var first = Assert.IsType<GitHubOpenResult.Loaded>(await _service.OpenAsync(Keymap));

        _handler.Throw = new HttpRequestException("no network");
        var second = Assert.IsType<GitHubOpenResult.Loaded>(await _service.OpenAsync(Keymap));

        Assert.True(second.FromCacheOffline);
        Assert.Equal(first.LocalPath, second.LocalPath);
    }

    [Fact]
    public async Task Open_GitHubUnreachableWithoutCache_FailsNetwork()
    {
        await StoreSignedInAsync();
        _handler.Throw = new HttpRequestException("no network");

        var result = await _service.OpenAsync(Keymap);

        var failed = Assert.IsType<GitHubOpenResult.Failed>(result);
        Assert.Equal(GitHubErrorKind.Network, failed.Kind);
    }

    [Fact]
    public async Task Open_HeadReturns404_FailsNotFound()
    {
        await StoreSignedInAsync();
        // No routes at all: every path answers the fake handler default —
        // and here the head commit is what 404s.

        var result = await _service.OpenAsync(Keymap);

        var failed = Assert.IsType<GitHubOpenResult.Failed>(result);
        Assert.Equal(GitHubErrorKind.NotFound, failed.Kind);
    }

    // ---- SaveAsync ----

    [Fact]
    public async Task Save_SignedIn_CommitsWithLoadedBlobSha_AndUpdatesCacheCopy()
    {
        await CachedCopyAsync();
        Route(ContentsPath, () => FakeHttpHandler.Json(CommitJson(CommitSha, RemoteBlobSha, parentSha: Sha1)));

        var result = await _service.SaveAsync(
            Keymap, NewContent, LoadedBlobSha, "LViz: save cosmos82", overwrite: false);

        var saved = Assert.IsType<GitHubSaveResult.Saved>(result);
        Assert.Equal(CommitSha, saved.CommitSha);

        var (request, body) = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal(ContentsPath, request.RequestUri!.PathAndQuery);

        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal(LoadedBlobSha, root.GetProperty("sha").GetString());
        Assert.Equal("LViz: save cosmos82", root.GetProperty("message").GetString());
        Assert.Equal("main", root.GetProperty("branch").GetString());
        Assert.Equal(NewContent, Convert.FromBase64String(root.GetProperty("content").GetString()!));

        // The cached copy now holds what was committed, and counts as current
        // at the commit (its parent was the commit it was downloaded at).
        Assert.Equal(NewContent, File.ReadAllBytes(_service.LocalPathFor(Keymap)!));
        Assert.Equal(CommitSha, CachedSha());
    }

    [Fact]
    public async Task Save_StaleBaseSha_ReportsConflictAndLeavesCacheCopy()
    {
        await CachedCopyAsync();
        Route(ContentsPath, () => FakeHttpHandler.Json(
            """{"message":"A commit has been made but not reflected in the response"}""", (HttpStatusCode)409));

        var result = await _service.SaveAsync(
            Keymap, NewContent, LoadedBlobSha, "LViz: save cosmos82", overwrite: false);

        Assert.IsType<GitHubSaveResult.Conflict>(result);
        Assert.Equal("keymap body", File.ReadAllText(_service.LocalPathFor(Keymap)!));
        Assert.Equal(Sha1, CachedSha());
    }

    [Fact]
    public async Task Save_Overwrite_ReadsCurrentBlobShaThenCommitsWithIt()
    {
        await CachedCopyAsync();
        Route(ContentsPath + "?ref=main", () => FakeHttpHandler.Json($$"""{"type":"file","sha":"{{RemoteBlobSha}}"}"""));
        Route(ContentsPath, () => FakeHttpHandler.Json(CommitJson(CommitSha, RemoteBlobSha, parentSha: Sha1)));

        var result = await _service.SaveAsync(
            Keymap, NewContent, LoadedBlobSha, "LViz: save cosmos82", overwrite: true);

        Assert.IsType<GitHubSaveResult.Saved>(result);
        Assert.Equal(new[] { ContentsPath + "?ref=main", ContentsPath }, SentPaths());
        Assert.Equal(HttpMethod.Get, _handler.Requests[0].Request.Method);

        // The commit replaces GitHub's current version, not the loaded baseline.
        using var document = JsonDocument.Parse(_handler.Requests[1].Body!);
        Assert.Equal(RemoteBlobSha, document.RootElement.GetProperty("sha").GetString());
    }

    [Fact]
    public async Task Save_SignedOut_FailsUnauthorized_WithoutHttpRequest()
    {
        var result = await _service.SaveAsync(
            Keymap, NewContent, LoadedBlobSha, "LViz: save cosmos82", overwrite: false);

        var failed = Assert.IsType<GitHubSaveResult.Failed>(result);
        Assert.Equal(GitHubErrorKind.Unauthorized, failed.Kind);
        Assert.Equal("Not signed in", failed.Message);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Save_PutReturns404_FailsNotFound_AndLeavesCacheCopy()
    {
        await CachedCopyAsync();
        Route(ContentsPath, () => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await _service.SaveAsync(
            Keymap, NewContent, LoadedBlobSha, "LViz: save cosmos82", overwrite: false);

        var failed = Assert.IsType<GitHubSaveResult.Failed>(result);
        Assert.Equal(GitHubErrorKind.NotFound, failed.Kind);
        Assert.Contains(ContentsPath, failed.Message);
        Assert.DoesNotContain(AccessToken, failed.Message);
        Assert.Equal("keymap body", File.ReadAllText(_service.LocalPathFor(Keymap)!));
    }

    [Fact]
    public async Task Save_CancelledToken_PropagatesCancellation_WithoutHttpRequest()
    {
        await CachedCopyAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.SaveAsync(
                Keymap, NewContent, LoadedBlobSha, "LViz: save cosmos82", overwrite: false, cancelled.Token));

        Assert.Empty(_handler.Requests);
        Assert.Equal("keymap body", File.ReadAllText(_service.LocalPathFor(Keymap)!));
    }

    // ---- sign out ----

    [Fact]
    public async Task SignOut_ClearsSessionStoreAndCacheRoot()
    {
        await StoreSignedInAsync();
        RouteHead(Sha1);
        RouteTarball(Sha1, TarGz(FileEntry("o-r-1111/config/cosmos82.keymap", "keymap body")));
        Assert.IsType<GitHubOpenResult.Loaded>(await _service.OpenAsync(Keymap));
        Assert.True(Directory.Exists(_root));

        await _service.SignOutAsync();

        Assert.False(_service.IsSignedIn);
        Assert.Null(_service.Login);
        Assert.Null(await _store.ReadAsync(GitHubAppInfo.SecretKey));
        Assert.False(Directory.Exists(_root));
    }

    // ---- sign in ----

    [Fact]
    public async Task CompleteSignIn_PollsPendingThenSuccess_SignsInAndStoresTokens()
    {
        // pending once, then the grant; the service's delay returns instantly.
        var polls = new Queue<Func<HttpResponseMessage>>([
            () => FakeHttpHandler.Json("{\"error\":\"authorization_pending\"}"),
            () => FakeHttpHandler.Json(GrantJson()),
        ]);
        _handler.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/login/device/code" => FakeHttpHandler.Json(DeviceCodeJson()),
            "/login/oauth/access_token" => polls.Dequeue()(),
            "/user" => FakeHttpHandler.Json("{\"login\":\"octocat\"}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        var code = await _service.StartSignInAsync();
        await _service.CompleteSignInAsync(code);

        Assert.True(_service.IsSignedIn);
        Assert.Equal(Login, _service.Login);

        var stored = GitHubTokens.TryFromJson(await _store.ReadAsync(GitHubAppInfo.SecretKey));
        Assert.NotNull(stored);
        Assert.Equal("ghu_fresh", stored.AccessToken);
        Assert.Equal(Login, stored.Login);
    }

    // ---- DI ----

    [Fact]
    public void AppServices_ResolvesGitHubKeymapService_AsSingleton()
    {
        using var provider = AppServices.BuildProvider();

        var first = provider.GetRequiredService<IGitHubKeymapService>();
        var second = provider.GetRequiredService<IGitHubKeymapService>();

        Assert.Same(first, second);
    }

    // ---- helpers ----

    /// <summary>A service over this test's handler and cache root, with instant polling.</summary>
    private GitHubKeymapService CreateService(ISecretStore store) =>
        new(_client, new GitHubSession(_client, store), new GitHubRepoCache(_root),
            static (_, _) => Task.CompletedTask);

    /// <summary>A live login sitting in the store, as a restored app would find it.</summary>
    private Task StoreSignedInAsync() =>
        _store.WriteAsync(
            GitHubAppInfo.SecretKey,
            new GitHubTokens(
                AccessToken,
                DateTimeOffset.UtcNow.AddHours(8),
                RefreshToken,
                DateTimeOffset.UtcNow.AddDays(183),
                Login).ToJson());

    /// <summary>Answers one path with a canned response.</summary>
    private void Route(string pathAndQuery, Func<HttpResponseMessage> respond) => _routes[pathAndQuery] = respond;

    private void RouteHead(string sha) =>
        Route("/repos/o/r/commits/main", () => FakeHttpHandler.Text(sha));

    private void RouteTarball(string sha, byte[] payload) =>
        Route(
            "/repos/o/r/tarball/" + sha,
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });

    /// <summary>
    /// Signed in with the keymap's branch already downloaded at commit
    /// <see cref="Sha1"/> — the state the editor saves from. The request log is
    /// cleared afterwards, so a save test only sees its own traffic.
    /// </summary>
    private async Task CachedCopyAsync()
    {
        await StoreSignedInAsync();
        RouteHead(Sha1);
        RouteTarball(Sha1, TarGz(FileEntry("o-r-1111/config/cosmos82.keymap", "keymap body")));
        Assert.IsType<GitHubOpenResult.Loaded>(await _service.OpenAsync(Keymap));
        _handler.Requests.Clear();
    }

    /// <summary>The commit the cached copy of <c>o/r</c> at <c>main</c> matches.</summary>
    private string? CachedSha() => new GitHubRepoCache(_root).CachedSha("o", "r", "main");

    /// <summary>The paths every request went to, in order.</summary>
    private string[] SentPaths() =>
        _handler.Requests.Select(entry => entry.Request.RequestUri!.PathAndQuery).ToArray();

    /// <summary>
    /// A successful commit answer; a null parent leaves <c>parents</c> out of
    /// the commit object entirely.
    /// </summary>
    private static string CommitJson(string commitSha, string blobSha, string? parentSha)
    {
        var commit = parentSha is null
            ? $$"""{"sha":"{{commitSha}}"}"""
            : $$"""{"sha":"{{commitSha}}","parents":[{"sha":"{{parentSha}}"}]}""";
        return $$"""{"content":{"sha":"{{blobSha}}"},"commit":{{commit}}}""";
    }

    private static string DeviceCodeJson() =>
        "{\"device_code\":\"dc-1\",\"user_code\":\"ABCD-1234\","
        + "\"verification_uri\":\"https://github.com/login/device\",\"expires_in\":900,\"interval\":5}";

    private static string GrantJson() =>
        "{\"access_token\":\"ghu_fresh\",\"expires_in\":28800,"
        + "\"refresh_token\":\"ghr_fresh\",\"refresh_token_expires_in\":15897600}";

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

    /// <summary>Wraps a store and counts reads, to prove the restore ran once.</summary>
    private sealed class CountingSecretStore(ISecretStore inner) : ISecretStore
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public Task<string?> ReadAsync(string key, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _reads);
            return inner.ReadAsync(key, ct);
        }

        public Task<bool> WriteAsync(string key, string value, CancellationToken ct = default) =>
            inner.WriteAsync(key, value, ct);

        public Task DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
    }
}
