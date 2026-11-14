using System.Net;
using LViz.Core.GitHub;
using Xunit;

namespace LViz.Tests.GitHub;

/// <summary>
/// The Actions reads of <see cref="GitHubClient"/> — workflow runs, run
/// artifacts and the artifact zip download — through
/// <see cref="FakeHttpHandler"/>, so nothing reaches the network.
///
/// <para>
/// Same routing pattern as <see cref="GitHubClientGitDataTests"/>: answers
/// are chosen by <c>RequestUri.PathAndQuery</c> and anything unrouted answers
/// 404, so a wrong path or query fails loudly instead of silently.
/// </para>
/// </summary>
public class GitHubClientActionsTests
{
    private const string ClientId = "Iv-buildstest";
    private const string UserAgent = "LViz-tests/1.0";
    private const string AccessToken = "ghu_testtoken";

    private const string Owner = "ovandongen";
    private const string Repo = "zmk-config-cosmos82";
    private const string RepoRoot = "/repos/ovandongen/zmk-config-cosmos82";

    private const string ShaUpper = "AA11AA22AA33AA44AA55AA66AA77AA88AA99AA00";
    private const string ShaLower = "aa11aa22aa33aa44aa55aa66aa77aa88aa99aa00";
    private const string OtherSha = "bb11bb22bb33bb44bb55bb66bb77bb88bb99bb00";

    private const string RunsByShaPath = RepoRoot + "/actions/runs?head_sha=" + ShaLower + "&per_page=20";
    private const string RunsByBranchPath = RepoRoot + "/actions/runs?branch=feature%2Fx&per_page=20";
    private const string ArtifactsPath = RepoRoot + "/actions/runs/42/artifacts";
    private const string ZipPath = RepoRoot + "/actions/artifacts/7/zip";

    private static readonly byte[] ZipBytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    /// <summary>A completed successful run and a running one, as the API writes them.</summary>
    private const string TwoRunsJson = """
        {"total_count":2,"workflow_runs":[
          {"id":101,"name":"Build firmware","head_branch":"main","head_sha":"AA11AA22AA33AA44AA55AA66AA77AA88AA99AA00",
           "status":"completed","conclusion":"success",
           "html_url":"https://github.com/ovandongen/zmk-config-cosmos82/actions/runs/101",
           "created_at":"2026-11-09T10:15:30Z","head_commit":{"message":"feat: add symbol layer"}},
          {"id":102,"name":"Build firmware","head_branch":"feature/x","head_sha":"bb11bb22bb33bb44bb55bb66bb77bb88bb99bb00",
           "status":"in_progress","conclusion":null,
           "html_url":"https://github.com/ovandongen/zmk-config-cosmos82/actions/runs/102",
           "created_at":"2026-11-09T11:00:00Z","head_commit":{"message":"wip: flash docs"}}
        ]}
        """;

    private readonly FakeHttpHandler _handler = new();
    private readonly GitHubClient _client;
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public GitHubClientActionsTests()
    {
        _client = new GitHubClient(new HttpClient(_handler), ClientId, UserAgent);

        _handler.Respond = request =>
            _routes.TryGetValue(request.RequestUri!.PathAndQuery, out var respond)
                ? respond()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    // ---- workflow runs by sha ----

    [Fact]
    public async Task ListWorkflowRuns_BySha_QueryIsLowerShaWithPerPage_TwoRunsParseEveryField()
    {
        Route(RunsByShaPath, () => FakeHttpHandler.Json(TwoRunsJson));

        var runs = await _client.ListWorkflowRunsAsync(AccessToken, Owner, Repo, ShaUpper, null);

        Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, _handler.Requests[0].Request.Method);
        Assert.Equal(RunsByShaPath, SentPaths()[0]);

        Assert.Equal(2, runs.Count);

        var done = runs[0];
        Assert.Equal(101, done.Id);
        Assert.Equal("Build firmware", done.Name);
        Assert.Equal("main", done.HeadBranch);
        Assert.Equal(ShaLower, done.HeadSha);
        Assert.Equal("completed", done.Status);
        Assert.Equal("success", done.Conclusion);
        Assert.Equal(
            "https://github.com/ovandongen/zmk-config-cosmos82/actions/runs/101", done.HtmlUrl);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 15, 30, TimeSpan.Zero), done.CreatedAt);
        Assert.Equal("feat: add symbol layer", done.CommitMessage);
        Assert.True(done.IsCompleted);
        Assert.True(done.Succeeded);

        var running = runs[1];
        Assert.Equal(102, running.Id);
        Assert.Equal(OtherSha, running.HeadSha);
        Assert.Equal("in_progress", running.Status);
        Assert.Null(running.Conclusion);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 11, 0, 0, TimeSpan.Zero), running.CreatedAt);
        Assert.Equal("wip: flash docs", running.CommitMessage);
        Assert.False(running.IsCompleted);
        Assert.False(running.Succeeded);
    }

    [Fact]
    public async Task ListWorkflowRuns_ByBranch_QueryEscapesSlash()
    {
        Route(RunsByBranchPath, () => FakeHttpHandler.Json(TwoRunsJson));

        var runs = await _client.ListWorkflowRunsAsync(AccessToken, Owner, Repo, null, "feature/x");

        Assert.Equal(RunsByBranchPath, SentPaths()[0]);
        Assert.Equal(2, runs.Count);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", "  ")]
    [InlineData(ShaUpper, "main")]
    [InlineData(ShaUpper, OtherSha)]
    public async Task ListWorkflowRuns_FilterNotExactlyOne_IsArgumentExceptionWithoutRequest(
        string? headSha, string? branch)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client.ListWorkflowRunsAsync(AccessToken, Owner, Repo, headSha, branch));

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task ListWorkflowRuns_RunWithoutIdShaOrStatus_IsSkipped()
    {
        Route(RunsByShaPath, () => FakeHttpHandler.Json($$"""
            {"total_count":4,"workflow_runs":[
              {"head_branch":"main","status":"completed","head_sha":"{{ShaLower}}"},
              {"id":2,"head_branch":"main","status":"completed"},
              {"id":3,"head_branch":"main","head_sha":"{{OtherSha}}"},
              {"id":4,"name":"Build firmware","head_branch":"main","head_sha":"{{OtherSha}}","status":"completed"}
            ]}
            """));

        var runs = await _client.ListWorkflowRunsAsync(AccessToken, Owner, Repo, ShaUpper, null);

        Assert.Single(runs);
        Assert.Equal(4, runs[0].Id);
    }

    [Fact]
    public async Task ListWorkflowRuns_MissingCreatedAtOrHeadCommit_FallsBackQuietly()
    {
        Route(RunsByShaPath, () => FakeHttpHandler.Json($$"""
            {"workflow_runs":[
              {"id":1,"head_sha":"{{ShaLower}}","status":"queued","created_at":"yesterday"},
              {"id":2,"head_sha":"{{OtherSha}}","status":"queued"}
            ]}
            """));

        var runs = await _client.ListWorkflowRunsAsync(AccessToken, Owner, Repo, ShaUpper, null);

        Assert.Equal(2, runs.Count);
        Assert.Equal(DateTimeOffset.MinValue, runs[0].CreatedAt);
        Assert.Equal(DateTimeOffset.MinValue, runs[1].CreatedAt);
        Assert.Null(runs[0].CommitMessage);
        Assert.Equal(string.Empty, runs[0].Name);
        Assert.Equal(string.Empty, runs[0].HeadBranch);
        Assert.Equal(string.Empty, runs[0].HtmlUrl);
    }

    [Fact]
    public async Task ListWorkflowRuns_AnswerWithoutWorkflowRuns_IsUnexpectedWithoutQueryOrToken()
    {
        Route(RunsByShaPath, () => FakeHttpHandler.Json("""{"total_count":0}"""));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.ListWorkflowRunsAsync(AccessToken, Owner, Repo, ShaUpper, null));

        Assert.Equal(GitHubErrorKind.Unexpected, ex.Kind);
        Assert.Equal(
            $"GitHub GET {RepoRoot}/actions/runs returned no workflow_runs", ex.Message);
        Assert.DoesNotContain(AccessToken, ex.Message);
        Assert.DoesNotContain("head_sha", ex.Message);
        Assert.DoesNotContain("per_page", ex.Message);
    }

    [Fact]
    public async Task ListWorkflowRuns_TransportError_IsNetwork()
    {
        _handler.Throw = new HttpRequestException();

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.ListWorkflowRunsAsync(AccessToken, Owner, Repo, ShaUpper, null));

        Assert.Equal(GitHubErrorKind.Network, ex.Kind);
        Assert.Null(ex.StatusCode);
        Assert.DoesNotContain(AccessToken, ex.Message);
        Assert.DoesNotContain("head_sha", ex.Message);
    }

    // ---- run artifacts ----

    [Fact]
    public async Task ListRunArtifacts_ListsBothWithSizesAndExpiry_EntryWithoutNameSkipped()
    {
        Route(ArtifactsPath, () => FakeHttpHandler.Json("""
            {"total_count":3,"artifacts":[
              {"id":7,"name":"firmware","size_in_bytes":4321,"expired":false},
              {"id":8,"name":"other","size_in_bytes":99,"expired":true},
              {"id":9,"size_in_bytes":1,"expired":false}
            ]}
            """));

        var artifacts = await _client.ListRunArtifactsAsync(AccessToken, Owner, Repo, 42);

        Assert.Equal(HttpMethod.Get, _handler.Requests[0].Request.Method);
        Assert.Equal(ArtifactsPath, SentPaths()[0]);
        Assert.Equal(2, artifacts.Count);
        Assert.Equal(new GitHubArtifact(7, "firmware", 4321, false), artifacts[0]);
        Assert.Equal(new GitHubArtifact(8, "other", 99, true), artifacts[1]);
    }

    [Fact]
    public async Task ListRunArtifacts_AnswerWithoutArtifacts_IsUnexpected()
    {
        Route(ArtifactsPath, () => FakeHttpHandler.Json("""{"total_count":0}"""));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.ListRunArtifactsAsync(AccessToken, Owner, Repo, 42));

        Assert.Equal(GitHubErrorKind.Unexpected, ex.Kind);
        Assert.Contains("no artifacts", ex.Message);
        Assert.DoesNotContain(AccessToken, ex.Message);
    }

    // ---- artifact zip download ----

    [Fact]
    public async Task DownloadArtifactZip_StreamsBodyAndSendsBearerToken()
    {
        Route(ZipPath, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(ZipBytes),
        });

        using var destination = new MemoryStream();
        await _client.DownloadArtifactZipAsync(
            AccessToken, Owner, Repo, 7, destination, 1024 * 1024);

        Assert.Equal(ZipPath, SentPaths()[0]);
        Assert.Equal(ZipBytes, destination.ToArray());

        var auth = _handler.Requests.Single().Request.Headers.Authorization;
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal(AccessToken, auth.Parameter);
    }

    [Fact]
    public async Task DownloadArtifactZip_AnnouncedSizeOverMax_IsTooLargeAndWritesNothing()
    {
        Route(ZipPath, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(ZipBytes),
        });

        using var destination = new MemoryStream();
        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.DownloadArtifactZipAsync(AccessToken, Owner, Repo, 7, destination, 5));

        Assert.Equal(GitHubErrorKind.TooLarge, ex.Kind);
        Assert.Contains("Firmware artifact", ex.Message);
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public async Task DownloadArtifactZip_ExpiredArtifact410_StaysUnexpectedWithStatusCode()
    {
        Route(ZipPath, () => FakeHttpHandler.Json(
            """{"message":"Artifact has expired"}""", (HttpStatusCode)410));

        using var destination = new MemoryStream();
        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.DownloadArtifactZipAsync(
                AccessToken, Owner, Repo, 7, destination, 1024 * 1024));

        Assert.Equal(GitHubErrorKind.Unexpected, ex.Kind);
        Assert.Equal(410, ex.StatusCode);
        Assert.DoesNotContain(AccessToken, ex.Message);
        Assert.Empty(destination.ToArray());
    }

    // ---- helpers ----

    /// <summary>Answers one path with a canned response.</summary>
    private void Route(string pathAndQuery, Func<HttpResponseMessage> respond) => _routes[pathAndQuery] = respond;

    /// <summary>The paths every request went to, in order.</summary>
    private string[] SentPaths() =>
        _handler.Requests.Select(entry => entry.Request.RequestUri!.PathAndQuery).ToArray();
}
