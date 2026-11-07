using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LViz.Core.GitHub;

/// <summary>
/// The only class in LViz that talks HTTP to GitHub, split by API area:
/// this file holds the shared request plumbing (<see cref="ApiRequest"/>,
/// <see cref="SendAsync"/>, <see cref="SendForJsonAsync"/>,
/// <see cref="GetString"/>, <see cref="GetInt64"/>); <c>.Auth</c> the OAuth
/// device flow, refresh and login lookup; <c>.Repos</c> installed repos,
/// branches and a branch's head commit; <c>.Contents</c> the tarball download,
/// committing one file and reading a file's blob SHA.
///
/// <para>
/// The device flow needs only the App's public client id — no client secret,
/// no private key — and tokens from it refresh without a secret. Every answer
/// that is not what we expected becomes a <see cref="GitHubApiException"/>;
/// no message, and nothing here, ever contains a token.
/// </para>
/// </summary>
public sealed partial class GitHubClient
{
    private const string ApiHost = "https://api.github.com";
    private const string LoginHost = "https://github.com";
    private const string ApiVersion = "2022-11-28";
    private const string ApiAccept = "application/vnd.github+json";
    private const string LoginAccept = "application/json";

    /// <summary>Response body is the bare commit SHA instead of JSON.</summary>
    private const string ShaAccept = "application/vnd.github.sha";

    private const int PageSize = 100;
    private const int ShaLength = 40;

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _userAgent;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="http">Injected client — tests hand in one built on a fake handler.</param>
    /// <param name="clientId">GitHub App client id (public by design).</param>
    /// <param name="userAgent">Value of the <c>User-Agent</c> header GitHub requires.</param>
    /// <param name="now">Clock for token expiry maths; defaults to UTC now.</param>
    public GitHubClient(HttpClient http, string clientId, string userAgent, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _clientId = clientId;
        _userAgent = userAgent;
        _now = now is not null ? now : static () => DateTimeOffset.UtcNow;
    }

    // ---- shared request plumbing (every call builds on these) ----

    /// <summary>
    /// A request to <c>https://api.github.com</c> plus
    /// <paramref name="pathAndQuery"/> with the API headers: User-Agent,
    /// <c>Accept</c>, <c>X-GitHub-Api-Version</c> and the bearer token.
    /// Defaults to GET without a body; a writer passes its method and content
    /// (the request then owns the content and disposes it). The caller owns
    /// (and must dispose) the message.
    /// </summary>
    private HttpRequestMessage ApiRequest(
        string pathAndQuery,
        string accessToken,
        string accept = ApiAccept,
        HttpMethod? method = null,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, ApiHost + pathAndQuery)
        {
            Content = content,
        };
        AddCommonHeaders(request, accept);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    /// <summary>
    /// A POST to <c>https://github.com</c> plus <paramref name="path"/> with an
    /// url-encoded form body. The caller owns (and must dispose) the message.
    /// </summary>
    private HttpRequestMessage LoginRequest(string path, IEnumerable<KeyValuePair<string, string>> form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, LoginHost + path)
        {
            Content = new FormUrlEncodedContent(form),
        };
        AddCommonHeaders(request, LoginAccept);
        return request;
    }

    private void AddCommonHeaders(HttpRequestMessage request, string accept)
    {
        // Without-validation keeps a non-strict User-Agent (e.g. a version
        // suffix) from silently disappearing.
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
    }

    /// <summary>
    /// Sends and returns a successful response; the caller owns it. Everything
    /// else is disposed here and reported as a <see cref="GitHubApiException"/>:
    /// transport errors and timeouts are
    /// <see cref="GitHubErrorKind.Network"/>, 401/404/409 map to their kinds, 403
    /// and 429 that say "rate limited" (an exhausted
    /// <c>x-ratelimit-remaining</c> or a <c>retry-after</c>) map to
    /// <see cref="GitHubErrorKind.RateLimited"/>, anything else is
    /// <see cref="GitHubErrorKind.Unexpected"/>. A cancelled
    /// <paramref name="ct"/> is never swallowed into
    /// <see cref="GitHubErrorKind.Network"/>.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, completion, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            throw new GitHubApiException(GitHubErrorKind.Network,
                $"GitHub {Describe(request)} timed out");
        }
        catch (HttpRequestException)
        {
            throw new GitHubApiException(GitHubErrorKind.Network,
                $"GitHub {Describe(request)} request failed");
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = (int)response.StatusCode;
        var described = Describe(request);
        var limited = (status == 403 || status == 429)
            && (HeaderValue(response, "retry-after") is not null
                || HeaderValue(response, "x-ratelimit-remaining") == "0");
        response.Dispose();

        if (status == 401)
        {
            throw new GitHubApiException(GitHubErrorKind.Unauthorized,
                $"GitHub {described} returned {status}", status);
        }

        if (status == 404)
        {
            throw new GitHubApiException(GitHubErrorKind.NotFound,
                $"GitHub {described} returned {status}", status);
        }

        if (status == 409)
        {
            throw new GitHubApiException(GitHubErrorKind.Conflict,
                $"GitHub {described} returned {status}", status);
        }

        if (limited)
        {
            throw new GitHubApiException(GitHubErrorKind.RateLimited,
                $"GitHub {described} returned {status}", status);
        }

        throw new GitHubApiException(GitHubErrorKind.Unexpected,
            $"GitHub {described} returned {status}", status);
    }

    /// <summary>
    /// <see cref="SendAsync"/> plus a JSON body parse. Invalid (or empty) JSON
    /// is <see cref="GitHubErrorKind.Unexpected"/>. The returned element is a
    /// clone, so it stays valid after the document is released.
    /// </summary>
    private async Task<JsonElement> SendForJsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var described = Describe(request);

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw UnexpectedError($"GitHub {described} returned invalid JSON");
        }
    }

    /// <summary>
    /// Walks <c>GET {path}?per_page=100&amp;page=n</c> from page 1 and returns
    /// every element of the answer's <paramref name="arrayProperty"/>. Stops as
    /// soon as the collected entries reach the answer's <c>total_count</c>, or a
    /// page brings nothing (an absent array counts as nothing). The elements
    /// come from a cloned document, so they stay valid here.
    /// </summary>
    private async Task<List<JsonElement>> GetPagedAsync(
        string path,
        string accessToken,
        string arrayProperty,
        CancellationToken ct)
    {
        var collected = new List<JsonElement>();

        for (var page = 1; ; page++)
        {
            using var request = ApiRequest($"{path}?per_page={PageSize}&page={page}", accessToken);
            var json = await SendForJsonAsync(request, ct);

            var array = GetArray(json, arrayProperty);
            var added = 0;
            foreach (var entry in array)
            {
                collected.Add(entry);
                added++;
            }

            if (added == 0 || collected.Count >= (GetInt64(json, "total_count") ?? collected.Count))
            {
                break;
            }
        }

        return collected;
    }

    /// <summary>
    /// <c>/repos/{owner}/{repo}</c> with both segments url-escaped, so a space
    /// becomes <c>%20</c> instead of breaking the path.
    /// </summary>
    private static string RepoPath(string owner, string repo) =>
        $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";

    /// <summary>
    /// A path escaped for a URL: GitHub allows <c>/</c> in branch names and in
    /// file paths, so each <c>/</c>-separated segment is escaped and the
    /// separators stay literal (<c>config/my board.keymap</c> becomes
    /// <c>config/my%20board.keymap</c>).
    /// </summary>
    private static string EscapePathSegments(string value) =>
        string.Join('/', value.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// Method and URL path only — never the query, which is where a caller
    /// could have put a token.
    /// </summary>
    private static string Describe(HttpRequestMessage request) =>
        $"{request.Method.Method} {request.RequestUri?.AbsolutePath ?? string.Empty}";

    private static GitHubApiException UnexpectedError(string message) =>
        new(GitHubErrorKind.Unexpected, message);

    private static GitHubApiException TooLargeError(long maxBytes) =>
        new(GitHubErrorKind.TooLarge, $"Repository archive is larger than {maxBytes / (1024 * 1024)} MB");

    private static string? HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// String property, or null when <paramref name="obj"/> is not an object,
    /// the property is missing, or it is not a string.
    /// </summary>
    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Integer property, or null when <paramref name="obj"/> is not an object,
    /// the property is missing, or it is not a whole number.
    /// </summary>
    private static long? GetInt64(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Boolean property, or false when <paramref name="obj"/> is not an object,
    /// the property is missing, or it is not a boolean — which is how a repo
    /// entry without <c>private</c> reads as public.
    /// </summary>
    private static bool GetBool(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var value)
            && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            && value.GetBoolean();

    /// <summary>
    /// Nested object property, or an undefined element when
    /// <paramref name="obj"/> is not an object, the property is missing, or it
    /// is not an object. Undefined feeds <see cref="GetString"/> et al. as null.
    /// </summary>
    private static JsonElement GetObject(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    /// <summary>
    /// Array property elements, or nothing when <paramref name="obj"/> is not an
    /// object, the property is missing, or it is not an array.
    /// </summary>
    private static IEnumerable<JsonElement> GetArray(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];
}
