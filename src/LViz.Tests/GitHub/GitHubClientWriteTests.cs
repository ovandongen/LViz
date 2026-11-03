using System.Net;
using System.Text.Json;
using LViz.Core.GitHub;
using Xunit;

namespace LViz.Tests.GitHub;

/// <summary>
/// The Contents writes of <see cref="GitHubClient"/> — committing one file
/// (including the 409 conflict path) and reading a file's blob SHA — through
/// <see cref="FakeHttpHandler"/>, so nothing reaches the network.
///
/// <para>
/// Same routing pattern as <see cref="GitHubClientReposTests"/>: answers are
/// chosen by <c>RequestUri.PathAndQuery</c> and anything unrouted answers
/// 404, so a wrong path or query fails loudly instead of silently.
/// </para>
/// </summary>
public class GitHubClientWriteTests
{
    private const string ClientId = "Iv-test";
    private const string UserAgent = "LViz-tests/1.0";
    private const string AccessToken = "ghu_testtoken";

    private const string KeymapPath = "config/glove80.keymap";
    private const string ContentsPath = "/repos/ovandongen/zmk-config/contents/" + KeymapPath;

    private const string BaseBlobSha = "aa11aa22aa33aa44aa55aa66aa77aa88aa99aa00";

    private const string CommitShaUpper = "AA11BB22CC33DD44EE55FF6677889900AABBCCDD";
    private const string BlobShaUpper = "DEADBEEF00112233445566778899AABBCCDDEEFF";
    private const string ParentShaUpper = "0A0B0C0D0E0F101112131415161718191A1B1C1D";

    private static readonly byte[] Payload = "layers { default }\n"u8.ToArray();

    private readonly FakeHttpHandler _handler = new();
    private readonly GitHubClient _client;
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public GitHubClientWriteTests()
    {
        _client = new GitHubClient(new HttpClient(_handler), ClientId, UserAgent);

        _handler.Respond = request =>
            _routes.TryGetValue(request.RequestUri!.PathAndQuery, out var respond)
                ? respond()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    // ---- PutFileAsync: the request ----

    [Fact]
    public async Task PutFile_SendsPutWithJsonBody()
    {
        Route(ContentsPath, () => FakeHttpHandler.Json(CommitJson(CommitShaUpper, BlobShaUpper, ParentShaUpper)));

        await _client.PutFileAsync(
            AccessToken, "ovandongen", "zmk-config", "main", KeymapPath,
            Payload, BaseBlobSha, "LViz: save glove80 keymap");

        var (request, body) = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal(ContentsPath, request.RequestUri!.PathAndQuery);
        Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);

        var auth = request.Headers.Authorization;
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal(AccessToken, auth.Parameter);

        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal("LViz: save glove80 keymap", root.GetProperty("message").GetString());
        Assert.Equal("main", root.GetProperty("branch").GetString());
        Assert.Equal(BaseBlobSha, root.GetProperty("sha").GetString());
        Assert.Equal(Payload, Convert.FromBase64String(root.GetProperty("content").GetString()!));
    }

    [Fact]
    public async Task PutFile_PathSegments_AreEscaped()
    {
        const string Path = "/repos/ovandongen/zmk-config/contents/config/my%20board.keymap";
        Route(Path, () => FakeHttpHandler.Json(CommitJson(CommitShaUpper, BlobShaUpper, ParentShaUpper)));

        await _client.PutFileAsync(
            AccessToken, "ovandongen", "zmk-config", "main", "config/my board.keymap",
            Payload, BaseBlobSha, "LViz: save");

        Assert.Equal(Path, SentPaths().Single());
    }

    // ---- PutFileAsync: the answer ----

    [Fact]
    public async Task PutFile_Ok_ReturnsLowercaseCommitBlobAndParentShas()
    {
        Route(ContentsPath, () => FakeHttpHandler.Json(CommitJson(CommitShaUpper, BlobShaUpper, ParentShaUpper)));

        var result = await _client.PutFileAsync(
            AccessToken, "ovandongen", "zmk-config", "main", KeymapPath,
            Payload, BaseBlobSha, "LViz: save glove80 keymap");

        Assert.Equal(
            new GitHubCommitResult(
                CommitShaUpper.ToLowerInvariant(),
                BlobShaUpper.ToLowerInvariant(),
                ParentShaUpper.ToLowerInvariant()),
            result);
    }

    [Fact]
    public async Task PutFile_CommitWithoutParents_HasNullParentSha()
    {
        Route(ContentsPath, () => FakeHttpHandler.Json(CommitJson(CommitShaUpper, BlobShaUpper, parentSha: null)));

        var result = await _client.PutFileAsync(
            AccessToken, "ovandongen", "zmk-config", "main", KeymapPath,
            Payload, BaseBlobSha, "LViz: first commit");

        Assert.Null(result.ParentSha);
    }

    [Fact]
    public async Task PutFile_StaleBaseSha_IsConflictWithStatusCode()
    {
        Route(ContentsPath, () => FakeHttpHandler.Json(
            """{"message":"A commit has been made but not reflected in the response"}""",
            (HttpStatusCode)409));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.PutFileAsync(
                AccessToken, "ovandongen", "zmk-config", "main", KeymapPath,
                Payload, BaseBlobSha, "LViz: save glove80 keymap"));

        Assert.Equal(GitHubErrorKind.Conflict, ex.Kind);
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains(ContentsPath, ex.Message);
        Assert.DoesNotContain(AccessToken, ex.Message);
    }

    [Fact]
    public async Task PutFile_AnswerWithoutCommit_IsUnexpected()
    {
        Route(ContentsPath, () => FakeHttpHandler.Json(
            "{\"content\":{\"sha\":\"" + BlobShaUpper + "\"}}"));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.PutFileAsync(
                AccessToken, "ovandongen", "zmk-config", "main", KeymapPath,
                Payload, BaseBlobSha, "LViz: save"));

        Assert.Equal(GitHubErrorKind.Unexpected, ex.Kind);
        Assert.DoesNotContain(AccessToken, ex.Message);
    }

    [Fact]
    public async Task PutFile_AnswerWithoutContentSha_IsUnexpected()
    {
        Route(ContentsPath, () => FakeHttpHandler.Json(
            "{\"commit\":{\"sha\":\"" + CommitShaUpper + "\"}}"));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.PutFileAsync(
                AccessToken, "ovandongen", "zmk-config", "main", KeymapPath,
                Payload, BaseBlobSha, "LViz: save"));

        Assert.Equal(GitHubErrorKind.Unexpected, ex.Kind);
    }

    // ---- PutFileAsync: argument guards ----

    [Fact]
    public async Task PutFile_BlankMessage_IsArgumentExceptionWithoutRequest()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client.PutFileAsync(
                AccessToken, "ovandongen", "zmk-config", "main", KeymapPath,
                Payload, BaseBlobSha, "   "));

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task PutFile_BlankPath_IsArgumentExceptionWithoutRequest()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client.PutFileAsync(
                AccessToken, "ovandongen", "zmk-config", "main", " ",
                Payload, BaseBlobSha, "LViz: save"));

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task PutFile_BlankBaseBlobSha_IsArgumentExceptionWithoutRequest()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client.PutFileAsync(
                AccessToken, "ovandongen", "zmk-config", "main", KeymapPath,
                Payload, "", "LViz: save"));

        Assert.Empty(_handler.Requests);
    }

    // ---- GetFileShaAsync ----

    [Fact]
    public async Task GetFileSha_EscapesPathAndBranch_ReturnsLowercaseSha()
    {
        const string Path = "/repos/o/r/contents/config/x.keymap?ref=feature%2Fa";
        Route(Path, () => FakeHttpHandler.Json($$"""{"type":"file","sha":"{{BlobShaUpper}}"}"""));

        var sha = await _client.GetFileShaAsync(AccessToken, "o", "r", "feature/a", "config/x.keymap");

        Assert.Equal(BlobShaUpper.ToLowerInvariant(), sha);
        Assert.Equal(Path, SentPaths().Single());
    }

    [Fact]
    public async Task GetFileSha_DirType_IsUnexpected()
    {
        const string Path = "/repos/o/r/contents/config?ref=main";
        Route(Path, () => FakeHttpHandler.Json($$"""{"type":"dir","sha":"{{BlobShaUpper}}"}"""));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.GetFileShaAsync(AccessToken, "o", "r", "main", "config"));

        Assert.Equal(GitHubErrorKind.Unexpected, ex.Kind);
        Assert.DoesNotContain(AccessToken, ex.Message);
    }

    [Fact]
    public async Task GetFileSha_AnswerWithoutSha_IsUnexpected()
    {
        const string Path = "/repos/o/r/contents/config/x.keymap?ref=main";
        Route(Path, () => FakeHttpHandler.Json("""{"type":"file"}"""));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.GetFileShaAsync(AccessToken, "o", "r", "main", "config/x.keymap"));

        Assert.Equal(GitHubErrorKind.Unexpected, ex.Kind);
    }

    [Fact]
    public async Task GetFileSha_MissingFile_IsNotFound()
    {
        const string Path = "/repos/o/r/contents/gone.keymap?ref=main";
        Route(Path, () => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(
            () => _client.GetFileShaAsync(AccessToken, "o", "r", "main", "gone.keymap"));

        Assert.Equal(GitHubErrorKind.NotFound, ex.Kind);
        Assert.Equal(404, ex.StatusCode);
        Assert.DoesNotContain(AccessToken, ex.Message);
    }

    [Fact]
    public async Task GetFileSha_BlankPath_IsArgumentExceptionWithoutRequest()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _client.GetFileShaAsync(AccessToken, "o", "r", "main", ""));

        Assert.Empty(_handler.Requests);
    }

    // ---- helpers ----

    /// <summary>Answers one path with a canned response.</summary>
    private void Route(string pathAndQuery, Func<HttpResponseMessage> respond) => _routes[pathAndQuery] = respond;

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
}
