using System.Net;
using LViz.Core.GitHub;
using Xunit;

namespace LViz.Tests.GitHub;

/// <summary>
/// <see cref="GitHubClient"/> sign-in: the device flow, token refresh and
/// <c>/user</c>, all through <see cref="FakeHttpHandler"/> so nothing reaches
/// the network. The clock is fixed and the poll delay only records what it was
/// asked to wait, so the polling tests run instantly and assert on the
/// intervals themselves.
///
/// <para>
/// Every failure assertion also checks that the message holds no token — the
/// App writes these messages to the diagnostics log.
/// </para>
/// </summary>
public class GitHubClientAuthTests
{
    private const string ClientId = "Iv-test";
    private const string UserAgent = "LViz-tests/1.0";
    private const string DeviceCodeValue = "DC-1";
    private const string AccessToken = "ghu_5A7ccEsS4DzQe2Hc622i06fB";
    private const string RefreshToken = "ghu_rEfr3sh7HsTf3kA90m2x5yZc";
    private const string Login = "ovandongen";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeHttpHandler _handler = new();
    private readonly GitHubClient _client;
    private readonly List<TimeSpan> _delays = new();

    public GitHubClientAuthTests()
    {
        _client = new GitHubClient(new HttpClient(_handler), ClientId, UserAgent, () => Now);
    }

    // ---- StartDeviceFlowAsync ----

    [Fact]
    public async Task StartDeviceFlow_PostsClientId_MapsResponse()
    {
        _handler.Respond = _ => FakeHttpHandler.Json(
            "{\"device_code\":\"DC-1\",\"user_code\":\"ABCD-1234\","
            + "\"verification_uri\":\"https://github.com/login/device\","
            + "\"expires_in\":900,\"interval\":5}");

        var code = await _client.StartDeviceFlowAsync();

        Assert.Equal(DeviceCodeValue, code.DeviceCode);
        Assert.Equal("ABCD-1234", code.UserCode);
        Assert.Equal("https://github.com/login/device", code.VerificationUri);
        Assert.Equal(TimeSpan.FromSeconds(900), code.ExpiresIn);
        Assert.Equal(TimeSpan.FromSeconds(5), code.Interval);

        var (request, body) = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://github.com/login/device/code", request.RequestUri!.ToString());
        Assert.Equal(ClientId, FormValue(body, "client_id"));
        Assert.Equal(UserAgent, SingleHeader(request, "User-Agent"));
        Assert.Equal("application/json", request.Headers.Accept.Single().MediaType);
    }

    [Fact]
    public async Task StartDeviceFlow_MissingInterval_DefaultsToFiveSeconds()
    {
        _handler.Respond = _ => FakeHttpHandler.Json(
            "{\"device_code\":\"DC-1\",\"user_code\":\"ABCD-1234\","
            + "\"verification_uri\":\"https://github.com/login/device\",\"expires_in\":900}");

        var code = await _client.StartDeviceFlowAsync();

        Assert.Equal(TimeSpan.FromSeconds(5), code.Interval);
    }

    [Fact]
    public async Task StartDeviceFlow_ErrorField_IsUnexpectedWithErrorCode()
    {
        _handler.Respond = _ => FakeHttpHandler.Json("{\"error\":\"device_flow_disabled\"}");

        var ex = await ExpectError(GitHubErrorKind.Unexpected, () => _client.StartDeviceFlowAsync());

        Assert.Contains("device_flow_disabled", ex.Message);
    }

    [Theory]
    [InlineData("{\"user_code\":\"ABCD-1234\",\"verification_uri\":\"https://github.com/login/device\"}")]
    [InlineData("{\"device_code\":\"DC-1\",\"verification_uri\":\"https://github.com/login/device\"}")]
    [InlineData("{\"device_code\":\"DC-1\",\"user_code\":\"ABCD-1234\"}")]
    public async Task StartDeviceFlow_MissingRequiredField_IsUnexpected(string json)
    {
        _handler.Respond = _ => FakeHttpHandler.Json(json);

        await ExpectError(GitHubErrorKind.Unexpected, () => _client.StartDeviceFlowAsync());
    }

    // ---- WaitForDeviceAuthorizationAsync ----

    [Fact]
    public async Task WaitForDeviceAuthorization_PollsUntilSuccess_ThenReadsLogin()
    {
        QueueTokenResponses(
            "{\"error\":\"authorization_pending\"}",
            "{\"error\":\"slow_down\"}",
            "{\"access_token\":\"" + AccessToken + "\",\"expires_in\":28800,"
            + "\"refresh_token\":\"" + RefreshToken + "\",\"refresh_token_expires_in\":15897600}");

        var tokens = await _client.WaitForDeviceAuthorizationAsync(Code(), Delay);

        Assert.Equal(
            new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) },
            _delays);

        Assert.Equal(AccessToken, tokens.AccessToken);
        Assert.Equal(RefreshToken, tokens.RefreshToken);
        Assert.Equal(Login, tokens.Login);
        Assert.Equal(Now.AddHours(8), tokens.AccessTokenExpiresAt);
        Assert.Equal(Now.AddSeconds(15897600), tokens.RefreshTokenExpiresAt);

        // Three polls plus the /user lookup, and the polls carry the grant.
        Assert.Equal(4, _handler.Requests.Count);
        var (poll, pollBody) = _handler.Requests[0];
        Assert.Equal("https://github.com/login/oauth/access_token", poll.RequestUri!.ToString());
        Assert.Equal(ClientId, FormValue(pollBody, "client_id"));
        Assert.Equal(DeviceCodeValue, FormValue(pollBody, "device_code"));
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", FormValue(pollBody, "grant_type"));

        var user = _handler.Requests[3].Request;
        Assert.Equal("https://api.github.com/user", user.RequestUri!.ToString());
        Assert.Equal(UserAgent, SingleHeader(user, "User-Agent"));
        Assert.Equal("2022-11-28", SingleHeader(user, "X-GitHub-Api-Version"));
        Assert.Equal("Bearer", user.Headers.Authorization?.Scheme);
        Assert.Equal(AccessToken, user.Headers.Authorization?.Parameter);
        Assert.Equal("application/vnd.github+json", user.Headers.Accept.Single().MediaType);
    }

    [Fact]
    public async Task WaitForDeviceAuthorization_SlowDownWithInterval_UsesThatInterval()
    {
        QueueTokenResponses(
            "{\"error\":\"slow_down\",\"interval\":12}",
            "{\"access_token\":\"" + AccessToken + "\"}");

        await _client.WaitForDeviceAuthorizationAsync(Code(), Delay);

        Assert.Equal(
            new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(12) },
            _delays);
    }

    [Fact]
    public async Task WaitForDeviceAuthorization_WithoutExpiryFields_LeavesExpiriesAndRefreshNull()
    {
        QueueTokenResponses("{\"access_token\":\"" + AccessToken + "\"}");

        var tokens = await _client.WaitForDeviceAuthorizationAsync(Code(), Delay);

        Assert.Equal(AccessToken, tokens.AccessToken);
        Assert.Null(tokens.AccessTokenExpiresAt);
        Assert.Null(tokens.RefreshTokenExpiresAt);
        Assert.Null(tokens.RefreshToken);
    }

    [Theory]
    [InlineData("expired_token", GitHubErrorKind.DeviceCodeExpired)]
    [InlineData("access_denied", GitHubErrorKind.AccessDenied)]
    public async Task WaitForDeviceAuthorization_TerminalError_MapsToKind(string error, GitHubErrorKind kind)
    {
        QueueTokenResponses("{\"error\":\"" + error + "\"}");

        await ExpectError(kind, () => _client.WaitForDeviceAuthorizationAsync(Code(), Delay));

        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task WaitForDeviceAuthorization_UnknownError_IsUnexpectedWithErrorCode()
    {
        QueueTokenResponses("{\"error\":\"unsupported_grant_type\"}");

        var ex = await ExpectError(GitHubErrorKind.Unexpected,
            () => _client.WaitForDeviceAuthorizationAsync(Code(), Delay));

        Assert.Contains("unsupported_grant_type", ex.Message);
    }

    [Fact]
    public async Task WaitForDeviceAuthorization_200WithoutToken_IsUnexpected()
    {
        QueueTokenResponses("{\"scope\":\"repo\"}");

        await ExpectError(GitHubErrorKind.Unexpected,
            () => _client.WaitForDeviceAuthorizationAsync(Code(), Delay));
    }

    [Fact]
    public async Task WaitForDeviceAuthorization_SuccessOnBudgetExhaustingPoll_WinsOverExpiry()
    {
        QueueTokenResponses("{\"access_token\":\"" + AccessToken + "\"}");

        // The first delay already spends the whole 5 s budget; a success on
        // that same poll must still be returned, not an expiry.
        var tokens = await _client.WaitForDeviceAuthorizationAsync(
            Code(expiresIn: TimeSpan.FromSeconds(5)), Delay);

        Assert.Equal(AccessToken, tokens.AccessToken);
        Assert.Single(_delays);
    }

    [Fact]
    public async Task WaitForDeviceAuthorization_AlwaysPending_ExpiresAfterBudgetedDelays()
    {
        _handler.Respond = _ => FakeHttpHandler.Json("{\"error\":\"authorization_pending\"}");

        await ExpectError(GitHubErrorKind.DeviceCodeExpired,
            () => _client.WaitForDeviceAuthorizationAsync(
                Code(expiresIn: TimeSpan.FromSeconds(15)), Delay));

        // 5 s + 5 s + 5 s reaches the 15 s budget: three polls, no fourth.
        Assert.Equal(
            new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5) },
            _delays);
    }

    // ---- RefreshAsync ----

    [Fact]
    public async Task Refresh_SendsRefreshTokenWithoutSecret_KeepsLogin()
    {
        _handler.Respond = _ => FakeHttpHandler.Json(
            "{\"access_token\":\"" + AccessToken + "\",\"expires_in\":28800}");

        var tokens = await _client.RefreshAsync(Tokens());

        var (request, body) = Assert.Single(_handler.Requests);
        Assert.Equal("https://github.com/login/oauth/access_token", request.RequestUri!.ToString());
        Assert.Equal(ClientId, FormValue(body, "client_id"));
        Assert.Equal("refresh_token", FormValue(body, "grant_type"));
        Assert.Equal(RefreshToken, FormValue(body, "refresh_token"));
        Assert.DoesNotContain("client_secret", body!);
        Assert.Equal(UserAgent, SingleHeader(request, "User-Agent"));

        Assert.Equal(AccessToken, tokens.AccessToken);
        Assert.Equal(Login, tokens.Login);
        Assert.Equal(Now.AddHours(8), tokens.AccessTokenExpiresAt);
    }

    [Fact]
    public async Task Refresh_BadRefreshToken_IsUnauthorized()
    {
        _handler.Respond = _ => FakeHttpHandler.Json("{\"error\":\"bad_refresh_token\"}");

        await ExpectError(GitHubErrorKind.Unauthorized, () => _client.RefreshAsync(Tokens()));
    }

    [Fact]
    public async Task Refresh_OtherError_IsUnexpectedWithErrorCode()
    {
        _handler.Respond = _ => FakeHttpHandler.Json("{\"error\":\"bad_verification_code\"}");

        var ex = await ExpectError(GitHubErrorKind.Unexpected, () => _client.RefreshAsync(Tokens()));

        Assert.Contains("bad_verification_code", ex.Message);
    }

    [Fact]
    public async Task Refresh_WithoutRefreshToken_IsUnauthorized_AndSendsNothing()
    {
        var ex = await ExpectError(GitHubErrorKind.Unauthorized,
            () => _client.RefreshAsync(Tokens(refreshToken: null)));

        Assert.Empty(_handler.Requests);
        Assert.Null(ex.StatusCode);
    }

    // ---- GetLoginAsync, which exercises the shared SendAsync mapping ----

    [Fact]
    public async Task GetLogin_ReturnsLogin_AndSendsApiHeaders()
    {
        _handler.Respond = _ => FakeHttpHandler.Json("{\"login\":\"" + Login + "\"}");

        Assert.Equal(Login, await _client.GetLoginAsync(AccessToken));

        var request = Assert.Single(_handler.Requests).Request;
        Assert.Equal("https://api.github.com/user", request.RequestUri!.ToString());
        Assert.Equal(UserAgent, SingleHeader(request, "User-Agent"));
        Assert.Equal("2022-11-28", SingleHeader(request, "X-GitHub-Api-Version"));
        Assert.Equal(AccessToken, request.Headers.Authorization?.Parameter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GitHubErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, GitHubErrorKind.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, GitHubErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, GitHubErrorKind.Unexpected)]
    public async Task GetLogin_ErrorStatus_MapsToKind(HttpStatusCode status, GitHubErrorKind kind)
    {
        _handler.Respond = _ =>
        {
            var response = FakeHttpHandler.Text("no", status);
            if (status == HttpStatusCode.Forbidden)
            {
                response.Headers.Add("x-ratelimit-remaining", "0");
            }

            return response;
        };

        var ex = await ExpectError(kind, () => _client.GetLoginAsync(AccessToken));

        Assert.Equal((int)status, ex.StatusCode);
    }

    [Fact]
    public async Task GetLogin_Non2xx_MessageIsMethodPathAndCode()
    {
        _handler.Respond = _ => FakeHttpHandler.Text("boom", HttpStatusCode.InternalServerError);

        var ex = await ExpectError(GitHubErrorKind.Unexpected, () => _client.GetLoginAsync(AccessToken));

        Assert.Equal("GitHub GET /user returned 500", ex.Message);
    }

    [Fact]
    public async Task GetLogin_ForbiddenWithoutRateLimitHeaders_IsUnexpected()
    {
        _handler.Respond = _ => FakeHttpHandler.Text("forbidden", HttpStatusCode.Forbidden);

        var ex = await ExpectError(GitHubErrorKind.Unexpected, () => _client.GetLoginAsync(AccessToken));

        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public async Task GetLogin_TooManyRequestsWithRetryAfter_IsRateLimited()
    {
        _handler.Respond = _ =>
        {
            var response = FakeHttpHandler.Text("slow down", (HttpStatusCode)429);
            response.Headers.Add("retry-after", "30");
            return response;
        };

        var ex = await ExpectError(GitHubErrorKind.RateLimited, () => _client.GetLoginAsync(AccessToken));

        Assert.Equal(429, ex.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"login\":42}")]
    public async Task GetLogin_UnusableBody_IsUnexpected(string body)
    {
        _handler.Respond = _ => FakeHttpHandler.Text(body);

        await ExpectError(GitHubErrorKind.Unexpected, () => _client.GetLoginAsync(AccessToken));
    }

    [Fact]
    public async Task GetLogin_HttpRequestException_IsNetwork()
    {
        _handler.Throw = new HttpRequestException("connection refused");

        var ex = await ExpectError(GitHubErrorKind.Network, () => _client.GetLoginAsync(AccessToken));

        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public async Task GetLogin_Timeout_IsNetwork()
    {
        _handler.Throw = new TaskCanceledException("the request timed out");

        var ex = await ExpectError(GitHubErrorKind.Network, () => _client.GetLoginAsync(AccessToken));

        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public async Task GetLogin_CancelledToken_StaysOperationCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // ThrowsAny on OperationCanceledException: a GitHubApiException here
        // would fail this, because the two types are unrelated.
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _client.GetLoginAsync(AccessToken, cts.Token));

        Assert.IsNotType<GitHubApiException>(ex);
    }

    // ---- helpers ----

    /// <summary>Answers the token endpoint from a queue; <c>/user</c> always reports <see cref="Login"/>.</summary>
    private void QueueTokenResponses(params string[] tokenBodies)
    {
        var queue = new Queue<string>(tokenBodies);
        _handler.Respond = request => request.RequestUri?.AbsolutePath == "/user"
            ? FakeHttpHandler.Json("{\"login\":\"" + Login + "\"}")
            : FakeHttpHandler.Json(queue.Dequeue());
    }

    private static GitHubDeviceCode Code(
        TimeSpan? expiresIn = null,
        TimeSpan? interval = null) =>
        new(
            DeviceCodeValue,
            "ABCD-1234",
            "https://github.com/login/device",
            expiresIn ?? TimeSpan.FromSeconds(900),
            interval ?? TimeSpan.FromSeconds(5));

    private static GitHubTokens Tokens(string? refreshToken = RefreshToken) =>
        new("ghu_old1234567890", null, refreshToken, null, Login);

    private Task Delay(TimeSpan interval, CancellationToken ct)
    {
        _delays.Add(interval);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asserts the call failed with <paramref name="kind"/>, and that the
    /// message leaked none of the secrets this class uses — those messages go
    /// to the diagnostics log.
    /// </summary>
    private static async Task<GitHubApiException> ExpectError(GitHubErrorKind kind, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<GitHubApiException>(action);

        Assert.Equal(kind, ex.Kind);
        Assert.DoesNotContain(AccessToken, ex.Message);
        Assert.DoesNotContain(RefreshToken, ex.Message);
        Assert.DoesNotContain(DeviceCodeValue, ex.Message);

        return ex;
    }

    private static string SingleHeader(HttpRequestMessage request, string name)
    {
        Assert.True(request.Headers.TryGetValues(name, out var values), $"'{name}' header missing");
        return values!.Single();
    }

    /// <summary>One url-decoded field of an url-encoded request body, or null.</summary>
    private static string? FormValue(string? body, string name)
    {
        if (body is null)
        {
            return null;
        }

        foreach (var pair in body.Split('&'))
        {
            var split = pair.IndexOf('=');
            if (split > 0 && pair[..split] == name)
            {
                return Uri.UnescapeDataString(pair[(split + 1)..]);
            }
        }

        return null;
    }
}
