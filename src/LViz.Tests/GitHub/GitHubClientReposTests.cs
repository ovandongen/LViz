using System.Net;
using System.Text;
using LViz.Core.GitHub;
using Xunit;

namespace LViz.Tests.GitHub;

/// <summary>
/// The repository reads of <see cref="GitHubClient"/> — installed repos,
/// branches, a branch's head commit and the tarball download — through
/// <see cref="FakeHttpHandler"/>, so nothing reaches the network.
///
/// <para>
/// Responses are routed by <c>RequestUri.PathAndQuery</c>, which makes the
/// paging assertions exact: <see cref="SentPaths"/> is the request log the
/// client produced, and anything unrouted answers 404 rather than hanging.
/// </para>
/// </summary>
public class GitHubClientReposTests
{
    private const string ClientId = "Iv-test";
    private const string UserAgent = "LViz-tests/1.0";
    private const string AccessToken = "ghu_testtoken";

    private const string RepoPath = "/repos/ovandongen/zmk-config";

    private readonly FakeHttpHandler _handler = new();
    private readonly GitHubClient _client;
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public GitHubClientReposTests()
    {
        _client = new GitHubClient(new HttpClient(_handler), ClientId, UserAgent);

        _handler.Respond = request =>
            _routes.TryGetValue(request.RequestUri!.PathAndQuery, out var respond)
                ? respond()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    // ---- ListInstalledReposAsync ----

    [Fact]
    public async Task ListInstalledRepos_TwoInstallations_PagesDeduplicatesAndSorts()
    {
        RouteJson("/user/installations?per_page=100&page=1",
            Page("installations", 2, new[] { """{"id":10}""", """{"id":20}""" }));

        RouteJson("/user/installations/10/repositories?per_page=100&page=1",
            Page("repositories", 2, new[]
            {
                Repo("ovandongen", "zmk-config", "dev", false),
                Repo("alice", "alpha", "trunk", true),
            }));

        // A full second page: 100 entries, so the client must ask for page 2.
        RouteJson("/user/installations/20/repositories?per_page=100&page=1",
            Page("repositories", 101, Enumerable.Range(0, 100).Select(i => Repo("bob", $"repo{i:D3}"))));

        // Page 3 of the whole set, holding a differently-cased copy of a repo
        // installation 10 already listed.
        RouteJson("/user/installations/20/repositories?per_page=100&page=2",
            Page("repositories", 101, new[] { Repo("OvAndOngen", "ZMK-Config") }));

        var repos = await _client.ListInstalledReposAsync(AccessToken);

        var expected = new[] { "alice/alpha" }
            .Concat(Enumerable.Range(0, 100).Select(i => $"bob/repo{i:D3}"))
            .Append("ovandongen/zmk-config");
        Assert.Equal(expected, repos.Select(repo => repo.FullName));

        var alice = repos.First(repo => repo.Owner == "alice");
        Assert.Equal("trunk", alice.DefaultBranch);
        Assert.True(alice.IsPrivate);

        // The first sighting of a duplicated repo wins: "dev" stays, not "main".
        var mine = repos.First(repo => repo.Owner == "ovandongen");
        Assert.Equal("zmk-config", mine.Name);
        Assert.Equal("dev", mine.DefaultBranch);
        Assert.False(mine.IsPrivate);

        Assert.Equal(new[]
        {
            "/user/installations?per_page=100&page=1",
            "/user/installations/10/repositories?per_page=100&page=1",
            "/user/installations/20/repositories?per_page=100&page=1",
            "/user/installations/20/repositories?per_page=100&page=2",
        }, SentPaths());
        AssertAllBearer();
    }

    [Fact]
    public async Task ListInstalledRepos_MissingDefaultBranchBecomesMain_EntryWithoutOwnerSkipped()
    {
        RouteJson("/user/installations?per_page=100&page=1",
            Page("installations", 1, new[] { """{"id":7}""" }));

        RouteJson("/user/installations/7/repositories?per_page=100&page=1",
            Page("repositories", 2, new[]
            {
                """{"name":"novc","owner":{"login":"carol"}}""",
                """{"name":"orphan","default_branch":"main","private":true}""",
            }));

        var repos = await _client.ListInstalledReposAsync(AccessToken);

        var repo = Assert.Single(repos);
        Assert.Equal("carol", repo.Owner);
        Assert.Equal("novc", repo.Name);
        Assert.Equal("carol/novc", repo.FullName);
        Assert.Equal("main", repo.DefaultBranch);
        Assert.False(repo.IsPrivate);
    }

    [Fact]
    public async Task ListInstalledRepos_NoInstallations_SingleRequest()
    {
        RouteJson("/user/installations?per_page=100&page=1", """{"total_count":0,"installations":[]}""");

        var repos = await _client.ListInstalledReposAsync(AccessToken);

        Assert.Empty(repos);
        Assert.Equal(new[] { "/user/installations?per_page=100&page=1" }, SentPaths());
    }

    // ---- ListBranchesAsync ----

    [Fact]
    public async Task ListBranches_FullPage_RequestsNextPageUntilShort()
    {
        var first = Enumerable.Range(0, 100).Select(i => $"b{i:D3}");
        var second = new[] { "x", "y", "z" };

        RouteJson($"{RepoPath}/branches?per_page=100&page=1", Branches(first));
        RouteJson($"{RepoPath}/branches?per_page=100&page=2", Branches(second));

        var names = await _client.ListBranchesAsync(AccessToken, "ovandongen", "zmk-config");

        Assert.Equal(first.Concat(second), names);
        Assert.Equal(new[]
        {
            $"{RepoPath}/branches?per_page=100&page=1",
            $"{RepoPath}/branches?per_page=100&page=2",
        }, SentPaths());
        AssertAllBearer();
    }

    [Fact]
    public async Task ListBranches_ShortFirstPage_SingleRequest()
    {
        RouteJson($"{RepoPath}/branches?per_page=100&page=1",
            Branches(new[] { "main", "feature/x y" }));

        var names = await _client.ListBranchesAsync(AccessToken, "ovandongen", "zmk-config");

        Assert.Equal(new[] { "main", "feature/x y" }, names);
        Assert.Equal(new[] { $"{RepoPath}/branches?per_page=100&page=1" }, SentPaths());
    }

    [Fact]
    public async Task ListBranches_OwnerAndRepoWithSpace_AreEscaped()
    {
        RouteJson("/repos/My%20Org/my%20repo/branches?per_page=100&page=1", Branches(new[] { "main" }));

        var names = await _client.ListBranchesAsync(AccessToken, "My Org", "my repo");

        Assert.Equal(new[] { "main" }, names);
        Assert.Equal("/repos/My%20Org/my%20repo/branches?per_page=100&page=1", SentPaths().Single());
    }

    // ---- GetBranchHeadAsync ----

    [Fact]
    public async Task GetBranchHead_EscapesSlashBranchAndReadsShaText()
    {
        const string Path = RepoPath + "/commits/feature/x%20y";
        RouteText(Path, "ABCDEF0123456789abcdef0123456789abcdef01\n");

        var sha = await _client.GetBranchHeadAsync(AccessToken, "ovandongen", "zmk-config", "feature/x y");

        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", sha);

        var (request, _) = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(Path, request.RequestUri!.PathAndQuery);
        Assert.Equal("application/vnd.github.sha", request.Headers.Accept.Single().MediaType);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(AccessToken, request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task GetBranchHead_BodyIsNotFortyHex_IsUnexpected()
    {
        RouteText($"{RepoPath}/commits/main", "nope");

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.GetBranchHeadAsync(AccessToken, "ovandongen", "zmk-config", "main"));

        Assert.Equal(GitHubErrorKind.Unexpected, ex.Kind);
        Assert.DoesNotContain(AccessToken, ex.Message);
    }

    [Fact]
    public async Task GetBranchHead_NotFound_IsNotFound()
    {
        Route("/repos/ovandongen/gone/commits/main",
            () => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.GetBranchHeadAsync(AccessToken, "ovandongen", "gone", "main"));

        Assert.Equal(GitHubErrorKind.NotFound, ex.Kind);
        Assert.Equal(404, ex.StatusCode);
        Assert.DoesNotContain(AccessToken, ex.Message);
    }

    // ---- DownloadTarballAsync ----

    [Fact]
    public async Task DownloadTarball_BelowLimit_CopiesWholeBody()
    {
        var payload = Enumerable.Range(0, 1_000).Select(i => (byte)(i % 251)).ToArray();
        Route("/repos/o/r/tarball/cafe01", () => Raw(payload, payload.Length));

        using var destination = new MemoryStream();
        await _client.DownloadTarballAsync(AccessToken, "o", "r", "cafe01", destination, 2_000);

        Assert.Equal(payload, destination.ToArray());
        Assert.Equal("/repos/o/r/tarball/cafe01", SentPaths().Single());
        AssertAllBearer();
    }

    [Fact]
    public async Task DownloadTarball_BodyOverLimitWithoutContentLength_IsTooLarge()
    {
        Route("/repos/o/r/tarball/cafe01", () => Raw(new byte[3_000], contentLength: null));

        using var destination = new MemoryStream();
        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.DownloadTarballAsync(AccessToken, "o", "r", "cafe01", destination, 2_000));

        Assert.Equal(GitHubErrorKind.TooLarge, ex.Kind);
        Assert.DoesNotContain(AccessToken, ex.Message);

        // The chunk that crossed the limit is never written.
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public async Task DownloadTarball_ContentLengthOverLimit_IsTooLargeBeforeReading()
    {
        // The body throws when read, so a passing test proves the announced
        // length was checked first.
        Route("/repos/o/r/tarball/cafe01", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnreadableContent(5_000),
        });

        using var destination = new MemoryStream();
        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.DownloadTarballAsync(AccessToken, "o", "r", "cafe01", destination, 2_000));

        Assert.Equal(GitHubErrorKind.TooLarge, ex.Kind);
        Assert.Equal("Repository archive is larger than 0 MB", ex.Message);
        Assert.Empty(destination.ToArray());
    }

    // ---- helpers ----

    /// <summary>Answers one path with a canned response.</summary>
    private void Route(string pathAndQuery, Func<HttpResponseMessage> respond) => _routes[pathAndQuery] = respond;

    private void RouteJson(string pathAndQuery, string json) =>
        Route(pathAndQuery, () => FakeHttpHandler.Json(json));

    private void RouteText(string pathAndQuery, string text) =>
        Route(pathAndQuery, () => FakeHttpHandler.Text(text));

    /// <summary>The paths every request went to, in order.</summary>
    private string[] SentPaths() =>
        _handler.Requests.Select(entry => entry.Request.RequestUri!.PathAndQuery).ToArray();

    private void AssertAllBearer()
    {
        Assert.NotEmpty(_handler.Requests);
        Assert.All(_handler.Requests, entry =>
        {
            var auth = entry.Request.Headers.Authorization;
            Assert.NotNull(auth);
            Assert.Equal("Bearer", auth!.Scheme);
            Assert.Equal(AccessToken, auth.Parameter);
        });
    }

    /// <summary>A paged answer: <c>{ "total_count": n, "prop": [ ... ] }</c>.</summary>
    private static string Page(string property, long totalCount, IEnumerable<string> entries) =>
        $$"""{"total_count":{{totalCount}},"{{property}}":[{{string.Join(',', entries)}}]}""";

    /// <summary>A bare array of <c>{ "name": ... }</c> branch objects.</summary>
    private static string Branches(IEnumerable<string> names) =>
        "[" + string.Join(',', names.Select(name => $$"""{"name":"{{name}}"}""")) + "]";

    /// <summary>One repository entry; pass a null to leave that field out.</summary>
    private static string Repo(string? owner, string name, string? defaultBranch = "main", bool? isPrivate = false)
    {
        var fields = new List<string> { $"\"name\":\"{name}\"" };

        if (owner is not null)
        {
            fields.Add($"\"owner\":{{\"login\":\"{owner}\"}}");
        }

        if (defaultBranch is not null)
        {
            fields.Add($"\"default_branch\":\"{defaultBranch}\"");
        }

        if (isPrivate is not null)
        {
            fields.Add($"\"private\":{(isPrivate.Value ? "true" : "false")}");
        }

        return "{" + string.Join(',', fields) + "}";
    }

    /// <summary>A raw body, with <c>Content-Length</c> set (or cleared) by hand.</summary>
    private static HttpResponseMessage Raw(byte[] payload, long? contentLength)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        response.Content.Headers.ContentLength = contentLength;
        return response;
    }

    /// <summary>A body that fails when read, so a size check must run first.</summary>
    private sealed class UnreadableContent : StreamContent
    {
        public UnreadableContent(long announcedLength) : base(new ThrowingReadStream())
        {
            Headers.ContentLength = announcedLength;
        }
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("body must not be read");

        public override int Read(Span<byte> buffer) => throw new IOException("body must not be read");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("body must not be read");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
    }
}
