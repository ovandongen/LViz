using System.Net;
using LViz.Core.GitHub;
using Xunit;

namespace LViz.Tests.GitHub;

/// <summary>
/// The compare read of <see cref="GitHubClient"/> — the changed-file list
/// between two commits — through <see cref="FakeHttpHandler"/>, so nothing
/// reaches the network.
///
/// <para>
/// Same routing pattern as <see cref="GitHubClientActionsTests"/>: answers
/// are chosen by <c>RequestUri.PathAndQuery</c> and anything unrouted answers
/// 404, so a wrong path fails loudly instead of silently.
/// </para>
/// </summary>
public class GitHubClientCompareTests
{
    private const string ClientId = "Iv-maintest";
    private const string UserAgent = "LViz-tests/1.0";
    private const string AccessToken = "ghu_testtoken";

    private const string Owner = "ovandongen";
    private const string Repo = "zmk-config";
    private const string RepoRoot = "/repos/ovandongen/zmk-config";

    private const string BaseUpper = "CC11CC22CC33CC44CC55CC66CC77CC88CC99CC00";
    private const string BaseLower = "cc11cc22cc33cc44cc55cc66cc77cc88cc99cc00";
    private const string HeadUpper = "DD11DD22DD33DD44DD55DD66DD77DD88DD99DD00";
    private const string HeadLower = "dd11dd22dd33dd44dd55dd66dd77dd88dd99dd00";

    private const string ComparePath = RepoRoot + "/compare/" + BaseLower + "..." + HeadLower;

    private readonly FakeHttpHandler _handler = new();
    private readonly GitHubClient _client;
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public GitHubClientCompareTests()
    {
        _client = new GitHubClient(new HttpClient(_handler), ClientId, UserAgent);

        _handler.Respond = request =>
            _routes.TryGetValue(request.RequestUri!.PathAndQuery, out var respond)
                ? respond()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CompareCommits_UpperShas_PathIsRepoCompareWithLowerShas_AndSendsBearerToken()
    {
        Route(ComparePath, () => FakeHttpHandler.Json($$"""
            {"status":"ahead","files":[
              {"filename":"config/cosmos82.keymap","status":"modified"}
            ]}
            """));

        var changes = await _client.CompareCommitsAsync(AccessToken, Owner, Repo, BaseUpper, HeadUpper);

        Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, _handler.Requests[0].Request.Method);
        Assert.Equal(ComparePath, SentPaths()[0]);
        Assert.Equal(new[] { "config/cosmos82.keymap" }, changes.Files);
        Assert.False(changes.Truncated);

        var auth = _handler.Requests.Single().Request.Headers.Authorization;
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal(AccessToken, auth.Parameter);
    }

    [Fact]
    public async Task CompareCommits_RenamedFile_AddsPreviousFilenameToTheThreeNames()
    {
        Route(ComparePath, () => FakeHttpHandler.Json("""
            {"status":"ahead","files":[
              {"filename":"config/cosmos82.keymap","status":"modified"},
              {"filename":"README.md","status":"modified"},
              {"filename":"config/cosmos82.conf","status":"renamed","previous_filename":"config/old.keymap"}
            ]}
            """));

        var changes = await _client.CompareCommitsAsync(AccessToken, Owner, Repo, BaseLower, HeadLower);

        Assert.Equal(
            new[] { "config/cosmos82.keymap", "README.md", "config/cosmos82.conf", "config/old.keymap" },
            changes.Files);
        Assert.False(changes.Truncated);
    }

    [Fact]
    public async Task CompareCommits_DuplicateFilenames_ListedOnce()
    {
        Route(ComparePath, () => FakeHttpHandler.Json("""
            {"status":"diverged","files":[
              {"filename":"config/cosmos82.keymap","status":"modified"},
              {"filename":"README.md","status":"added"},
              {"filename":"README.md","status":"modified"},
              {"filename":"config/cosmos82.keymap","status":"modified"}
            ]}
            """));

        var changes = await _client.CompareCommitsAsync(AccessToken, Owner, Repo, BaseLower, HeadLower);

        Assert.Equal(new[] { "config/cosmos82.keymap", "README.md" }, changes.Files);
        Assert.False(changes.Truncated);
    }

    [Fact]
    public async Task CompareCommits_ThreeHundredEntries_IsTruncated()
    {
        Route(ComparePath, () => FakeHttpHandler.Json(FilesAnswer(300)));

        var changes = await _client.CompareCommitsAsync(AccessToken, Owner, Repo, BaseLower, HeadLower);

        Assert.True(changes.Truncated);
        Assert.Equal(300, changes.Files.Count);
    }

    [Fact]
    public async Task CompareCommits_TwoHundredNinetyNineEntries_IsNotTruncated()
    {
        Route(ComparePath, () => FakeHttpHandler.Json(FilesAnswer(299)));

        var changes = await _client.CompareCommitsAsync(AccessToken, Owner, Repo, BaseLower, HeadLower);

        Assert.False(changes.Truncated);
        Assert.Equal(299, changes.Files.Count);
    }

    [Fact]
    public async Task CompareCommits_IdenticalWithoutFilesKey_IsEmptyAndNotTruncated()
    {
        Route(ComparePath, () => FakeHttpHandler.Json(
            """{"status":"identical","ahead_by":0,"behind_by":0,"total_commits":0}"""));

        var changes = await _client.CompareCommitsAsync(AccessToken, Owner, Repo, BaseLower, HeadLower);

        Assert.Empty(changes.Files);
        Assert.False(changes.Truncated);
    }

    [Fact]
    public async Task CompareCommits_UnknownSha404_IsNotFoundWithoutTokenInMessage()
    {
        // Unrouted: the fake answers 404, like GitHub does for a sha a
        // force-push dropped.
        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.CompareCommitsAsync(AccessToken, Owner, Repo, BaseLower, HeadLower));

        Assert.Equal(GitHubErrorKind.NotFound, ex.Kind);
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal($"GitHub GET {ComparePath} returned 404", ex.Message);
        Assert.DoesNotContain(AccessToken, ex.Message);
    }

    [Theory]
    [InlineData(null, HeadLower, "baseSha")]
    [InlineData("", HeadLower, "baseSha")]
    [InlineData("   ", HeadLower, "baseSha")]
    [InlineData(BaseLower, null, "headSha")]
    [InlineData(BaseLower, "", "headSha")]
    [InlineData(BaseLower, "\t", "headSha")]
    public async Task CompareCommits_BlankSha_IsArgumentExceptionWithoutRequest(
        string? baseSha, string? headSha, string expectedParam)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _client.CompareCommitsAsync(AccessToken, Owner, Repo, baseSha!, headSha!));

        Assert.Equal(expectedParam, ex.ParamName);
        Assert.Empty(_handler.Requests);
    }

    // ---- helpers ----

    /// <summary>A compare answer with <paramref name="count"/> distinct modified files.</summary>
    private static string FilesAnswer(int count) =>
        """{"status":"ahead","files":["""
        + string.Join(',', Enumerable.Range(0, count).Select(
            i => $$"""{"filename":"dir/file{{i}}.f","status":"modified"}"""))
        + "]}";

    /// <summary>Answers one path with a canned response.</summary>
    private void Route(string pathAndQuery, Func<HttpResponseMessage> respond) => _routes[pathAndQuery] = respond;

    /// <summary>The paths every request went to, in order.</summary>
    private string[] SentPaths() =>
        _handler.Requests.Select(entry => entry.Request.RequestUri!.PathAndQuery).ToArray();
}
