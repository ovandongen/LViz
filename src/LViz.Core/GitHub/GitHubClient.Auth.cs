using System.Text.Json;

namespace LViz.Core.GitHub;

/// <summary>Sign-in: device flow, token refresh, login lookup.</summary>
public sealed partial class GitHubClient
{
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
}
