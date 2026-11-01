using System.Net.Http.Headers;
using System.Text.Json;

namespace LViz.Core.GitHub;

/// <summary>
/// The only class in LViz that talks HTTP to GitHub. This part is the sign-in:
/// the GitHub App's OAuth device flow, refreshing the resulting tokens, and
/// reading the logged-in user's handle. Repository reads — installed repos,
/// branches, a branch's head commit and the tarball download — live in the
/// same class on top of the shared helpers here
/// (<see cref="ApiRequest"/>, <see cref="SendAsync"/>,
/// <see cref="SendForJsonAsync"/>, <see cref="GetString"/>,
/// <see cref="GetInt64"/>).
///
/// <para>
/// The device flow needs only the App's public client id — no client secret,
/// no private key — and tokens from it refresh without a secret. Every answer
/// that is not what we expected becomes a <see cref="GitHubApiException"/>;
/// no message, and nothing here, ever contains a token.
/// </para>
/// </summary>
public sealed class GitHubClient
{
    private const string ApiHost = "https://api.github.com";
    private const string LoginHost = "https://github.com";
    private const string ApiVersion = "2022-11-28";
    private const string ApiAccept = "application/vnd.github+json";
    private const string LoginAccept = "application/json";

    private const string DeviceCodePath = "/login/device/code";
    private const string AccessTokenPath = "/login/oauth/access_token";

    private const string DeviceFlowGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private const string RefreshTokenGrant = "refresh_token";

    private const string AuthorizationPending = "authorization_pending";
    private const string SlowDown = "slow_down";
    private const string ExpiredToken = "expired_token";
    private const string AccessDeniedError = "access_denied";
    private const string BadRefreshToken = "bad_refresh_token";

    private const int DefaultPollSeconds = 5;
    private const int SlowDownStepSeconds = 5;

    /// <summary>Response body is the bare commit SHA instead of JSON.</summary>
    private const string ShaAccept = "application/vnd.github.sha";

    private const int PageSize = 100;
    private const int TarballChunkBytes = 81920;
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

    /// <summary>
    /// Opens the device flow: <c>POST /login/device/code</c> with the client id.
    /// A JSON <c>error</c> (e.g. <c>device_flow_disabled</c>) or an incomplete
    /// answer becomes <see cref="GitHubErrorKind.Unexpected"/>; the error code
    /// is in the message.
    /// </summary>
    public async Task<GitHubDeviceCode> StartDeviceFlowAsync(CancellationToken ct = default)
    {
        using var request = LoginRequest(DeviceCodePath, new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
        });

        var json = await SendForJsonAsync(request, ct);

        var error = GetString(json, "error");
        if (error is not null)
        {
            throw UnexpectedError($"GitHub POST {DeviceCodePath} returned {error}");
        }

        var deviceCode = GetString(json, "device_code");
        var userCode = GetString(json, "user_code");
        var verificationUri = GetString(json, "verification_uri");
        if (deviceCode is null || userCode is null || verificationUri is null)
        {
            throw UnexpectedError($"GitHub POST {DeviceCodePath} returned an incomplete device code");
        }

        return new GitHubDeviceCode(
            deviceCode,
            userCode,
            verificationUri,
            TimeSpan.FromSeconds(GetInt64(json, "expires_in") ?? 0),
            TimeSpan.FromSeconds(GetInt64(json, "interval") ?? DefaultPollSeconds));
    }

    /// <summary>
    /// Polls <c>POST /login/oauth/access_token</c> until the user authorizes
    /// the code. GitHub answers HTTP 200 with an <c>error</c> field while it
    /// waits:
    /// <c>authorization_pending</c> keeps polling, <c>slow_down</c> widens the
    /// interval (the response's <c>interval</c> when present, else current +
    /// 5 s), <c>expired_token</c> and <c>access_denied</c> end it.
    ///
    /// <para>
    /// The expiry budget is the sum of the intervals handed to
    /// <paramref name="delay"/> — not wall clock — so a caller (and a test)
    /// that returns instantly still gets a bounded loop. On success the
    /// tokens carry the login from <see cref="GetLoginAsync"/>.
    /// </para>
    /// </summary>
    /// <param name="code">What <see cref="StartDeviceFlowAsync"/> returned.</param>
    /// <param name="delay">How to wait: called with the interval and the token.</param>
    public async Task<GitHubTokens> WaitForDeviceAuthorizationAsync(
        GitHubDeviceCode code,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct = default)
    {
        var interval = code.Interval;
        var waited = TimeSpan.Zero;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            await delay(interval, ct);
            waited += interval;

            using var request = LoginRequest(AccessTokenPath, new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["device_code"] = code.DeviceCode,
                ["grant_type"] = DeviceFlowGrant,
            });

            var json = await SendForJsonAsync(request, ct);

            var error = GetString(json, "error");
            if (error is null)
            {
                var accessToken = GetString(json, "access_token");
                if (accessToken is null)
                {
                    throw UnexpectedError($"GitHub POST {AccessTokenPath} returned no access token");
                }

                return BuildTokens(json, accessToken, await GetLoginAsync(accessToken, ct));
            }

            switch (error)
            {
                case AuthorizationPending:
                    break;
                case SlowDown:
                    interval = TimeSpan.FromSeconds(
                        GetInt64(json, "interval") ?? (interval.TotalSeconds + SlowDownStepSeconds));
                    break;
                case ExpiredToken:
                    throw new GitHubApiException(GitHubErrorKind.DeviceCodeExpired,
                        "GitHub device code expired");
                case AccessDeniedError:
                    throw new GitHubApiException(GitHubErrorKind.AccessDenied,
                        "GitHub sign-in was denied");
                default:
                    throw UnexpectedError($"GitHub POST {AccessTokenPath} returned {error}");
            }

            if (waited >= code.ExpiresIn)
            {
                throw new GitHubApiException(GitHubErrorKind.DeviceCodeExpired,
                    "GitHub device code expired");
            }
        }
    }

    /// <summary>
    /// Trades <paramref name="current"/>'s refresh token for a new access
    /// token at <c>POST /login/oauth/access_token</c> — no client secret is
    /// sent, which GitHub allows for device-flow tokens. The login is kept, so
    /// no extra <c>/user</c> call. A missing refresh token and
    /// <c>bad_refresh_token</c> are <see cref="GitHubErrorKind.Unauthorized"/>.
    /// </summary>
    public async Task<GitHubTokens> RefreshAsync(GitHubTokens current, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(current.RefreshToken))
        {
            throw new GitHubApiException(GitHubErrorKind.Unauthorized,
                "GitHub session has no refresh token");
        }

        using var request = LoginRequest(AccessTokenPath, new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["grant_type"] = RefreshTokenGrant,
            ["refresh_token"] = current.RefreshToken,
        });

        var json = await SendForJsonAsync(request, ct);

        var error = GetString(json, "error");
        if (error is not null)
        {
            if (error == BadRefreshToken)
            {
                throw new GitHubApiException(GitHubErrorKind.Unauthorized,
                    $"GitHub POST {AccessTokenPath} returned {BadRefreshToken}");
            }

            throw UnexpectedError($"GitHub POST {AccessTokenPath} returned {error}");
        }

        var accessToken = GetString(json, "access_token");
        if (accessToken is null)
        {
            throw UnexpectedError($"GitHub POST {AccessTokenPath} returned no access token");
        }

        return BuildTokens(json, accessToken, current.Login);
    }

    /// <summary>
    /// The handle behind an access token: <c>GET /user</c>. A missing
    /// <c>login</c> is <see cref="GitHubErrorKind.Unexpected"/>.
    /// </summary>
    public async Task<string> GetLoginAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = ApiRequest("/user", accessToken);
        var json = await SendForJsonAsync(request, ct);

        return GetString(json, "login")
            ?? throw UnexpectedError("GitHub GET /user returned no login");
    }

    /// <summary>
    /// Every repository behind the user's GitHub App installations:
    /// <c>GET /user/installations</c> first (LViz only sees repos whose
    /// installation it belongs to), then
    /// <c>GET /user/installations/{id}/repositories</c> per installation. Both
    /// are paged 100 at a time until the collected entries reach the
    /// response's <c>total_count</c> or a page comes back empty.
    ///
    /// <para>
    /// Entries without a <c>name</c> or an <c>owner.login</c> are skipped, a
    /// missing <c>default_branch</c> becomes <c>main</c> and a missing
    /// <c>private</c> becomes false. Repos that appear under several
    /// installations are listed once; the result is sorted by owner, then
    /// name, case-insensitively.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<GitHubRepo>> ListInstalledReposAsync(
        string accessToken,
        CancellationToken ct = default)
    {
        var installations = await GetPagedAsync(
            "/user/installations", accessToken, "installations", ct);

        var byFullName = new Dictionary<string, GitHubRepo>(StringComparer.OrdinalIgnoreCase);

        foreach (var installation in installations)
        {
            var id = GetInt64(installation, "id");
            if (id is null)
            {
                continue;
            }

            var entries = await GetPagedAsync(
                $"/user/installations/{id.Value}/repositories", accessToken, "repositories", ct);

            foreach (var entry in entries)
            {
                var name = GetString(entry, "name");
                var owner = GetString(GetObject(entry, "owner"), "login");
                if (name is null || owner is null)
                {
                    continue;
                }

                var repo = new GitHubRepo(
                    owner,
                    name,
                    GetString(entry, "default_branch") ?? "main",
                    GetBool(entry, "private"));

                // The same repo can sit behind several installations; the
                // first sighting wins.
                byFullName.TryAdd(repo.FullName, repo);
            }
        }

        return byFullName.Values
            .OrderBy(repo => repo.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(repo => repo.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Branch names of a repository, in API order:
    /// <c>GET /repos/{owner}/{repo}/branches</c>, 100 per page, paging while a
    /// page is full. Owner and repo are url-escaped.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListBranchesAsync(
        string accessToken,
        string owner,
        string repo,
        CancellationToken ct = default)
    {
        var path = RepoPath(owner, repo) + "/branches";
        var names = new List<string>();

        for (var page = 1; ; page++)
        {
            using var request = ApiRequest($"{path}?per_page={PageSize}&page={page}", accessToken);
            var json = await SendForJsonAsync(request, ct);

            if (json.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var received = 0;
            foreach (var entry in json.EnumerateArray())
            {
                received++;
                var name = GetString(entry, "name");
                if (name is not null)
                {
                    names.Add(name);
                }
            }

            if (received < PageSize)
            {
                break;
            }
        }

        return names;
    }

    /// <summary>
    /// The commit a branch points at: <c>GET /repos/{owner}/{repo}/commits/{branch}</c>
    /// answered as the bare SHA text (accept <c>application/vnd.github.sha</c>).
    /// The branch is escaped segment by segment, so <c>feature/x y</c> goes out
    /// as <c>feature/x%20y</c>. The trimmed answer must be exactly 40 hex
    /// characters — anything else is <see cref="GitHubErrorKind.Unexpected"/>.
    /// Returned lower-case, which is how GitHub writes SHAs.
    /// </summary>
    public async Task<string> GetBranchHeadAsync(
        string accessToken,
        string owner,
        string repo,
        string branch,
        CancellationToken ct = default)
    {
        var path = $"{RepoPath(owner, repo)}/commits/{EscapeBranch(branch)}";
        using var request = ApiRequest(path, accessToken, ShaAccept);

        using var response = await SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        var sha = text.Trim().ToLowerInvariant();
        if (sha.Length != ShaLength || !sha.All(Uri.IsHexDigit))
        {
            throw UnexpectedError($"GitHub {Describe(request)} returned an unexpected commit sha");
        }

        return sha;
    }

    /// <summary>
    /// Streams <c>GET /repos/{owner}/{repo}/tarball/{sha}</c> into
    /// <paramref name="destination"/> in 80 KiB chunks. GitHub answers with a
    /// redirect to codeload, which the injected <see cref="HttpClient"/>
    /// follows. The body is never buffered: as soon as more than
    /// <paramref name="maxBytes"/> has been read — or the response announces
    /// that much in <c>Content-Length</c> — the copy stops and
    /// <see cref="GitHubErrorKind.TooLarge"/> is thrown, so an oversized repo
    /// cannot fill the disk.
    /// </summary>
    public async Task DownloadTarballAsync(
        string accessToken,
        string owner,
        string repo,
        string sha,
        Stream destination,
        long maxBytes,
        CancellationToken ct = default)
    {
        using var request = ApiRequest($"{RepoPath(owner, repo)}/tarball/{sha}", accessToken);

        var response = await SendAsync(request, ct, HttpCompletionOption.ResponseHeadersRead);
        try
        {
            if (response.Content.Headers.ContentLength is long announced && announced > maxBytes)
            {
                throw TooLargeError(maxBytes);
            }

            var buffer = new byte[TarballChunkBytes];
            var total = 0L;

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer, ct);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > maxBytes)
                    {
                        throw TooLargeError(maxBytes);
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    // ---- shared request plumbing (repository calls build on these) ----

    /// <summary>
    /// A GET to <c>https://api.github.com</c> plus <paramref name="pathAndQuery"/>
    /// with the API headers: User-Agent, <c>Accept</c>,
    /// <c>X-GitHub-Api-Version</c> and the bearer token. The caller owns (and
    /// must dispose) the message.
    /// </summary>
    private HttpRequestMessage ApiRequest(
        string pathAndQuery,
        string accessToken,
        string accept = ApiAccept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ApiHost + pathAndQuery);
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
    /// <see cref="GitHubErrorKind.Network"/>, 401/404 map to their kinds, 403
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
    /// Tokens from a grant response. Both expiries are the injected clock plus
    /// the response's <c>expires_in</c> / <c>refresh_token_expires_in</c>
    /// seconds, and null when the field is absent (the App can be configured
    /// not to expire tokens).
    /// </summary>
    private GitHubTokens BuildTokens(JsonElement json, string accessToken, string login)
    {
        return new GitHubTokens(
            accessToken,
            ExpiryFrom(GetInt64(json, "expires_in")),
            GetString(json, "refresh_token"),
            ExpiryFrom(GetInt64(json, "refresh_token_expires_in")),
            login);
    }

    private DateTimeOffset? ExpiryFrom(long? seconds) =>
        seconds is null ? null : _now().AddSeconds(seconds.Value);

    /// <summary>
    /// <c>/repos/{owner}/{repo}</c> with both segments url-escaped, so a space
    /// becomes <c>%20</c> instead of breaking the path.
    /// </summary>
    private static string RepoPath(string owner, string repo) =>
        $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";

    /// <summary>
    /// A branch name escaped for a URL path: GitHub allows <c>/</c> in a branch,
    /// so each <c>/</c>-separated segment is escaped and the separators stay
    /// literal (<c>feature/x y</c> becomes <c>feature/x%20y</c>).
    /// </summary>
    private static string EscapeBranch(string branch) =>
        string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));

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
