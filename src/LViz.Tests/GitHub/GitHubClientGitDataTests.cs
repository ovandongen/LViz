using System.Net;
using System.Text.Json;
using LViz.Core.GitHub;
using Xunit;

namespace LViz.Tests.GitHub;

/// <summary>
/// The Git Data writes of <see cref="GitHubClient"/> — committing several
/// files as one commit (base commit, tree, commit, ref) through
/// <see cref="FakeHttpHandler"/>, so nothing reaches the network.
///
/// <para>
/// Same routing pattern as <see cref="GitHubClientWriteTests"/>: answers are
/// chosen by <c>RequestUri.PathAndQuery</c> and anything unrouted answers
/// 404, so a wrong path fails loudly instead of silently.
/// </para>
/// </summary>
public class GitHubClientGitDataTests
{
    private const string ClientId = "Iv-test";
    private const string UserAgent = "LViz-tests/1.0";
    private const string AccessToken = "ghu_testtoken";

    private const string Owner = "ovandongen";
    private const string Repo = "zmk-config";
    private const string Message = "LViz: set up modules";

    private const string BaseCommitSha = "aa11aa22aa33aa44aa55aa66aa77aa88aa99aa00";
    private const string BaseTreeSha = "bb11bb22bb33bb44bb55bb66bb77bb88bb99bb00";
    private const string NewTreeSha = "cc11cc22cc33cc44cc55cc66cc77cc88cc99cc00";
    private const string NewCommitShaUpper = "DD11DD22DD33DD44DD55DD66DD77DD88DD99DD00";

    private const string RepoRoot = "/repos/ovandongen/zmk-config";
    private const string BaseCommitPath = RepoRoot + "/git/commits/" + BaseCommitSha;
    private const string TreesPath = RepoRoot + "/git/trees";
    private const string NewCommitPath = RepoRoot + "/git/commits";
    private const string RefPath = RepoRoot + "/git/refs/heads/main";
    private const string SlashRefPath = RepoRoot + "/git/refs/heads/feature/x";

    private const string WestPath = "config/west.yml";
    private const string BuildPath = "build.yaml";
    private const string WestText = "west:\n  manifest:\n    remotes: []\n";
    private const string BuildText = "build:\n  - board: corne_left\n";

    private static readonly GitHubFileWrite[] Files =
    [
        new(WestPath, WestText),
        new(BuildPath, BuildText),
    ];

    private readonly FakeHttpHandler _handler = new();
    private readonly GitHubClient _client;
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public GitHubClientGitDataTests()
    {
        _client = new GitHubClient(new HttpClient(_handler), ClientId, UserAgent);

        _handler.Respond = request =>
            _routes.TryGetValue(request.RequestUri!.PathAndQuery, out var respond)
                ? respond()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    // ---- the four requests, in order ----

    [Fact]
    public async Task CommitFiles_HappyPath_SendsFourRequestsInOrder()
    {
        RouteHappyPath();

        var result = await Commit(Files);

        Assert.Equal(
            new[] { BaseCommitPath, TreesPath, NewCommitPath, RefPath },
            SentPaths());
        Assert.Equal(
            new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Post, HttpMethod.Patch },
            _handler.Requests.Select(entry => entry.Request.Method).ToArray());
        Assert.Equal(
            new GitHubTreeCommitResult(NewCommitShaUpper.ToLowerInvariant(), BaseCommitSha),
            result);
    }

    // ---- the request bodies ----

    [Fact]
    public async Task CommitFiles_TreeBody_HasBaseTreeAndBlobEntriesInOrder()
    {
        RouteHappyPath();

        await Commit(Files);

        using var document = JsonDocument.Parse(_handler.Requests[1].Body!);
        var root = document.RootElement;
        Assert.Equal(BaseTreeSha, root.GetProperty("base_tree").GetString());

        var entries = root.GetProperty("tree").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(WestPath, entries[0].GetProperty("path").GetString());
        Assert.Equal(WestText, entries[0].GetProperty("content").GetString());
        Assert.Equal(BuildPath, entries[1].GetProperty("path").GetString());
        Assert.Equal(BuildText, entries[1].GetProperty("content").GetString());
        foreach (var entry in entries)
        {
            Assert.Equal("100644", entry.GetProperty("mode").GetString());
            Assert.Equal("blob", entry.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task CommitFiles_CommitBody_CarriesMessageParentAndNewTree()
    {
        RouteHappyPath();

        await Commit(Files);

        using var document = JsonDocument.Parse(_handler.Requests[2].Body!);
        var root = document.RootElement;
        Assert.Equal(Message, root.GetProperty("message").GetString());
        Assert.Equal(NewTreeSha, root.GetProperty("tree").GetString());

        var parents = root.GetProperty("parents").EnumerateArray().ToArray();
        Assert.Single(parents);
        Assert.Equal(BaseCommitSha, parents[0].GetString());
    }

    [Fact]
    public async Task CommitFiles_RefBody_MovesBranchToNewCommitWithoutForce()
    {
        RouteHappyPath();

        await Commit(Files);

        using var document = JsonDocument.Parse(_handler.Requests[3].Body!);
        var root = document.RootElement;
        Assert.Equal(NewCommitShaUpper, root.GetProperty("sha").GetString());
        Assert.False(root.GetProperty("force").GetBoolean());
    }

    [Fact]
    public async Task CommitFiles_BranchWithSlash_PatchesPathWithLiteralSlash()
    {
        RouteCommitTreeAndCommit();
        Route(SlashRefPath, () => FakeHttpHandler.Json("""{"ref":"refs/heads/feature/x"}"""));

        await Commit(Files, branch: "feature/x");

        Assert.Equal(SlashRefPath, SentPaths()[3]);
    }

    // ---- conflict and unexpected answers ----

    [Fact]
    public async Task CommitFiles_Patch422_IsConflictWithoutToken()
    {
        RouteCommitTreeAndCommit();
        Route(RefPath, () => FakeHttpHandler.Json(
            """{"message":"Reference update failed with 'non-fast-forward'"}""", (HttpStatusCode)422));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => Commit(Files));

        Assert.Equal(GitHubErrorKind.Conflict, ex.Kind);
        Assert.Equal(422, ex.StatusCode);
        Assert.Equal($"GitHub PATCH {RefPath} returned 422", ex.Message);
        Assert.DoesNotContain(AccessToken, ex.Message);
    }

    [Fact]
    public async Task CommitFiles_Patch409_AlreadyArrivesAsConflict()
    {
        RouteCommitTreeAndCommit();
        Route(RefPath, () => FakeHttpHandler.Json(
            """{"message":"Something happened"}""", HttpStatusCode.Conflict));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => Commit(Files));

        Assert.Equal(GitHubErrorKind.Conflict, ex.Kind);
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task CommitFiles_TreePost422_StaysUnexpected()
    {
        RouteCommitTreeAndCommit();
        Route(TreesPath, () => FakeHttpHandler.Json(
            """{"message":"Invalid object"}""", (HttpStatusCode)422));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => Commit(Files));

        Assert.Equal(GitHubErrorKind.Unexpected, ex.Kind);
        Assert.Equal(422, ex.StatusCode);
        Assert.DoesNotContain(AccessToken, ex.Message);
    }

    [Fact]
    public async Task CommitFiles_BaseCommitWithoutTree_IsUnexpected()
    {
        Route(BaseCommitPath, () => FakeHttpHandler.Json($$"""{"sha":"{{BaseCommitSha}}"}"""));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => Commit(Files));

        Assert.Equal(GitHubErrorKind.Unexpected, ex.Kind);
        Assert.DoesNotContain(AccessToken, ex.Message);
    }

    [Fact]
    public async Task CommitFiles_TreeAnswerWithoutSha_IsUnexpected()
    {
        RouteCommitTreeAndCommit();
        Route(TreesPath, () => FakeHttpHandler.Json("{}"));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => Commit(Files));

        Assert.Equal(GitHubErrorKind.Unexpected, ex.Kind);
    }

    [Fact]
    public async Task CommitFiles_CommitAnswerWithoutSha_IsUnexpected()
    {
        Route(BaseCommitPath, () => FakeHttpHandler.Json(
            $$$"""{"sha":"{{{BaseCommitSha}}}","tree":{"sha":"{{{BaseTreeSha}}}"}}"""));
        Route(TreesPath, () => FakeHttpHandler.Json($$"""{"sha":"{{NewTreeSha}}"}"""));
        Route(NewCommitPath, () => FakeHttpHandler.Json("{}"));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => Commit(Files));

        Assert.Equal(GitHubErrorKind.Unexpected, ex.Kind);
    }

    // ---- argument guards ----

    [Fact]
    public async Task CommitFiles_EmptyFileList_IsArgumentExceptionWithoutRequest()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Commit([]));

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task CommitFiles_DuplicatePath_IsArgumentExceptionWithoutRequest()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Commit(
        [
            new(WestPath, WestText),
            new(WestPath, BuildText),
        ]));

        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("config\\west.yml")]
    [InlineData("../west.yml")]
    [InlineData("./west.yml")]
    [InlineData("config//west.yml")]
    [InlineData("config/../west.yml")]
    public async Task CommitFiles_BadFilePath_IsArgumentExceptionWithoutRequest(string path)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Commit([new GitHubFileWrite(path, "x")]));

        Assert.Equal("files", ex!.ParamName);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task CommitFiles_AbsolutePath_IsArgumentExceptionNamingTheRepoRoot()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Commit([new GitHubFileWrite("/config/west.yml", "x")]));

        // Not just any ArgumentException: the dedicated absolute-path branch
        // speaks up, instead of the empty-segment rule behind it.
        Assert.Contains("relative to the repo root", ex.Message);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task CommitFiles_BlankMessage_IsArgumentExceptionWithoutRequest()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client.CommitFilesAsync(
                AccessToken, Owner, Repo, "main", BaseCommitSha, Files, "  "));

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task CommitFiles_BlankBaseCommitSha_IsArgumentExceptionWithoutRequest()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client.CommitFilesAsync(
                AccessToken, Owner, Repo, "main", " ", Files, Message));

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task CommitFiles_PathsDifferingOnlyInCase_AreNotDuplicates()
    {
        RouteHappyPath();

        var result = await Commit(
        [
            new("Config/west.yml", WestText),
            new(WestPath, BuildText),
        ]);

        Assert.Equal(NewCommitShaUpper.ToLowerInvariant(), result.CommitSha);
        Assert.Equal(4, _handler.Requests.Count);
    }

    // ---- helpers ----

    /// <summary>Runs the call under test with the standard repo and message.</summary>
    private Task<GitHubTreeCommitResult> Commit(
        IReadOnlyList<GitHubFileWrite> files,
        string branch = "main") =>
        _client.CommitFilesAsync(AccessToken, Owner, Repo, branch, BaseCommitSha, files, Message);

    /// <summary>Answers the base commit, tree and commit steps happily; each test routes the ref step itself.</summary>
    private void RouteCommitTreeAndCommit()
    {
        Route(BaseCommitPath, () => FakeHttpHandler.Json(
            $$$"""{"sha":"{{{BaseCommitSha}}}","tree":{"sha":"{{{BaseTreeSha}}}"}}"""));
        Route(TreesPath, () => FakeHttpHandler.Json($$"""{"sha":"{{NewTreeSha}}"}"""));
        Route(NewCommitPath, () => FakeHttpHandler.Json($$"""{"sha":"{{NewCommitShaUpper}}"}"""));
    }

    /// <summary>Answers all four steps happily.</summary>
    private void RouteHappyPath()
    {
        RouteCommitTreeAndCommit();
        Route(RefPath, () => FakeHttpHandler.Json(
            $$$"""{"ref":"refs/heads/main","object":{"sha":"{{{NewCommitShaUpper}}}"}}"""));
    }

    /// <summary>Answers one path with a canned response.</summary>
    private void Route(string pathAndQuery, Func<HttpResponseMessage> respond) => _routes[pathAndQuery] = respond;

    /// <summary>The paths every request went to, in order.</summary>
    private string[] SentPaths() =>
        _handler.Requests.Select(entry => entry.Request.RequestUri!.PathAndQuery).ToArray();
}
