using LViz.Core.GitHub;
using LViz.Core.Persistence;
using Xunit;

namespace LViz.Tests.GitHub;

/// <summary>
/// <see cref="GitHubSession"/>: restoring the stored login, handing out a
/// valid access token (refreshing near expiry, single-flight), and signing
/// out. Runs on <see cref="InMemorySecretStore"/> and a real
/// <see cref="GitHubClient"/> over <see cref="FakeHttpHandler"/>, with a
/// mutable clock so expiry maths is exact and nothing touches the network.
/// </summary>
public class GitHubSessionTests
{
    private const string ClientId = "Iv-test";
    private const string UserAgent = "LViz-tests/1.0";
    private const string Login = "ovandongen";
    private const string AccessToken = "ghu_old";
    private const string RefreshToken = "ghr_old";
    private const string NewAccessToken = "ghu_new";
    private const string NewRefreshToken = "ghr_new";

    private static readonly DateTimeOffset Base = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private DateTimeOffset _now = Base;

    private readonly FakeHttpHandler _handler = new();
    private readonly InMemorySecretStore _store = new();
    private readonly GitHubSession _session;

    public GitHubSessionTests()
    {
        var client = new GitHubClient(new HttpClient(_handler), ClientId, UserAgent, () => _now);
        _session = new GitHubSession(client, _store, () => _now);
    }

    // ---- RestoreAsync ----

    [Fact]
    public async Task Restore_EmptyStore_StaysSignedOut()
    {
        var changes = 0;
        _session.Changed += (_, _) => changes++;

        Assert.False(await _session.RestoreAsync());

        Assert.False(_session.IsSignedIn);
        Assert.Null(_session.Login);
        Assert.False(_session.IsPersisted);
        Assert.Equal(0, changes);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Restore_ValidStoredTokens_SignsInWithoutNetwork()
    {
        await Store(Tokens(Base.AddHours(8)));
        var changes = 0;
        _session.Changed += (_, _) => changes++;

        Assert.True(await _session.RestoreAsync());

        Assert.True(_session.IsSignedIn);
        Assert.Equal(Login, _session.Login);
        Assert.True(_session.IsPersisted);
        Assert.Equal(1, changes);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Restore_AlreadySignedIn_ReturnsTrueWithoutReading()
    {
        await _session.SignInAsync(Tokens(Base.AddHours(8)));
        await _store.DeleteAsync(GitHubAppInfo.SecretKey);

        Assert.True(await _session.RestoreAsync());
        Assert.Equal(Login, _session.Login);
    }

    [Fact]
    public async Task Restore_GarbageInStore_StaysSignedOut()
    {
        await _store.WriteAsync(GitHubAppInfo.SecretKey, "not json {{{");

        Assert.False(await _session.RestoreAsync());

        Assert.False(_session.IsSignedIn);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Restore_AccessAndRefreshBothExpired_DeletesStoredLogin()
    {
        await Store(Tokens(accessExpiresAt: Base.AddMinutes(-1), refreshExpiresAt: Base.AddMinutes(-1)));

        Assert.False(await _session.RestoreAsync());

        Assert.False(_session.IsSignedIn);
        Assert.Null(await _store.ReadAsync(GitHubAppInfo.SecretKey));
    }

    [Fact]
    public async Task Restore_AccessExpiredRefreshValid_RestoresWithoutRequest()
    {
        await Store(Tokens(accessExpiresAt: Base.AddMinutes(-1)));

        Assert.True(await _session.RestoreAsync());

        Assert.True(_session.IsSignedIn);
        Assert.True(_session.IsPersisted);
        Assert.Empty(_handler.Requests);
    }

    // ---- GetAccessTokenAsync ----

    [Fact]
    public async Task GetAccessToken_TwoHoursLeft_ReturnsCurrentTokenWithoutRequest()
    {
        await _session.SignInAsync(Tokens(accessExpiresAt: _now.AddHours(2)));

        Assert.Equal(AccessToken, await _session.GetAccessTokenAsync());
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GetAccessToken_FourMinutesLeft_RefreshesAndStoresNewTokens()
    {
        await _session.SignInAsync(Tokens(accessExpiresAt: _now.AddMinutes(4)));
        _handler.Respond = _ => FakeHttpHandler.Json(RefreshedJson());

        Assert.Equal(NewAccessToken, await _session.GetAccessTokenAsync());

        var (request, body) = Assert.Single(_handler.Requests);
        Assert.Equal("https://github.com/login/oauth/access_token", request.RequestUri!.ToString());
        Assert.Contains("refresh_token=" + RefreshToken, body);

        var stored = GitHubTokens.TryFromJson(await _store.ReadAsync(GitHubAppInfo.SecretKey));
        Assert.NotNull(stored);
        Assert.Equal(NewAccessToken, stored.AccessToken);
        Assert.Equal(NewRefreshToken, stored.RefreshToken);
        Assert.Equal(Login, stored.Login);
    }

    [Fact]
    public async Task GetAccessToken_NullExpiry_ReturnsCurrentTokenWithoutRequest()
    {
        await _session.SignInAsync(Tokens(accessExpiresAt: null));

        Assert.Equal(AccessToken, await _session.GetAccessTokenAsync());
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GetAccessToken_BadRefreshToken_SignsOutAndRethrowsUnauthorized()
    {
        await _session.SignInAsync(Tokens(accessExpiresAt: _now.AddMinutes(-1)));
        _handler.Respond = _ => FakeHttpHandler.Json("{\"error\":\"bad_refresh_token\"}");
        var changes = 0;
        _session.Changed += (_, _) => changes++;

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => _session.GetAccessTokenAsync());

        Assert.Equal(GitHubErrorKind.Unauthorized, ex.Kind);
        Assert.False(_session.IsSignedIn);
        Assert.Null(_session.Login);
        Assert.False(_session.IsPersisted);
        Assert.Null(await _store.ReadAsync(GitHubAppInfo.SecretKey));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task GetAccessToken_NetworkFailure_RethrowsAndKeepsSignIn()
    {
        await _session.SignInAsync(Tokens(accessExpiresAt: _now.AddMinutes(-1)));
        _handler.Throw = new HttpRequestException("no network");
        var changes = 0;
        _session.Changed += (_, _) => changes++;

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => _session.GetAccessTokenAsync());

        Assert.Equal(GitHubErrorKind.Network, ex.Kind);
        Assert.True(_session.IsSignedIn);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task GetAccessToken_TwoConcurrentCallersWhenExpired_RefreshOnce()
    {
        // The first refresh answer waits (truly asynchronously) on the gate
        // so both callers are parked — one in HTTP, one on the semaphore —
        // before it arrives; any later request answers at once, which would
        // expose a double refresh as two requests.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GatingHttpHandler(gate, _ => FakeHttpHandler.Json(RefreshedJson()));
        var client = new GitHubClient(new HttpClient(handler), ClientId, UserAgent, () => _now);
        var session = new GitHubSession(client, _store, () => _now);
        await session.SignInAsync(Tokens(accessExpiresAt: _now.AddMinutes(-1)));

        var first = session.GetAccessTokenAsync();
        var second = session.GetAccessTokenAsync();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        gate.SetResult();

        var tokens = await Task.WhenAll(first, second);

        Assert.Equal(NewAccessToken, tokens[0]);
        Assert.Same(tokens[0], tokens[1]);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAccessToken_NotSignedIn_ThrowsUnauthorized()
    {
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => _session.GetAccessTokenAsync());

        Assert.Equal(GitHubErrorKind.Unauthorized, ex.Kind);
        Assert.Equal("Not signed in to GitHub", ex.Message);
        Assert.Empty(_handler.Requests);
    }

    // ---- SignInAsync / SignOutAsync ----

    [Fact]
    public async Task SignIn_StoreRefusesWrite_KeepsLoginForThisProcessOnly()
    {
        var client = new GitHubClient(new HttpClient(_handler), ClientId, UserAgent, () => _now);
        var session = new GitHubSession(client, new FailingSecretStore(), () => _now);
        var changes = 0;
        session.Changed += (_, _) => changes++;

        await session.SignInAsync(Tokens(Base.AddHours(8)));

        Assert.True(session.IsSignedIn);
        Assert.Equal(Login, session.Login);
        Assert.False(session.IsPersisted);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task SignOut_ClearsStateAndStore_AndSecondSignOutRaisesNothing()
    {
        await _session.SignInAsync(Tokens(Base.AddHours(8)));
        var changes = 0;
        _session.Changed += (_, _) => changes++;

        await _session.SignOutAsync();

        Assert.False(_session.IsSignedIn);
        Assert.Null(_session.Login);
        Assert.False(_session.IsPersisted);
        Assert.Null(await _store.ReadAsync(GitHubAppInfo.SecretKey));
        Assert.Equal(1, changes);

        await _session.SignOutAsync();

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task SignOut_WhileSignedOut_CleansStoreWithoutChanged()
    {
        await Store(Tokens(Base.AddHours(8)));
        var changes = 0;
        _session.Changed += (_, _) => changes++;

        await _session.SignOutAsync();

        Assert.Null(await _store.ReadAsync(GitHubAppInfo.SecretKey));
        Assert.Equal(0, changes);
    }

    // ---- helpers ----

    /// <summary>An explicit null access expiry means "never expires"; the refresh token defaults to valid.</summary>
    private static GitHubTokens Tokens(
        DateTimeOffset? accessExpiresAt,
        DateTimeOffset? refreshExpiresAt = null) =>
        new(AccessToken, accessExpiresAt, RefreshToken,
            refreshExpiresAt ?? Base.AddSeconds(15897600), Login);

    private Task Store(GitHubTokens tokens) =>
        _store.WriteAsync(GitHubAppInfo.SecretKey, tokens.ToJson());

    private static string RefreshedJson() =>
        "{\"access_token\":\"" + NewAccessToken + "\",\"expires_in\":28800,"
        + "\"refresh_token\":\"" + NewRefreshToken + "\",\"refresh_token_expires_in\":15897600}";

    /// <summary>
    /// Answers the first request only after <paramref name="gate"/> completes
    /// and every later one immediately — truly asynchronously, because
    /// blocking inside <see cref="FakeHttpHandler.Respond"/> would park the
    /// calling thread instead of the caller's task. Counts every request.
    /// </summary>
    private sealed class GatingHttpHandler(
        TaskCompletionSource gate,
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _count;

        public int RequestCount => Volatile.Read(ref _count);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var waitsForGate = Interlocked.Increment(ref _count) == 1;
            if (request.Content is not null)
            {
                await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (waitsForGate)
            {
                await gate.Task;
            }

            return respond(request);
        }
    }

    /// <summary>A store whose writes always fail — a Linux box without a keychain.</summary>
    private sealed class FailingSecretStore : ISecretStore
    {
        public Task<string?> ReadAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<bool> WriteAsync(string key, string value, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
