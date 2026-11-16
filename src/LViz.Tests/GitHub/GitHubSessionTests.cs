using System.Net;
using LViz.Core.GitHub;
using LViz.Core.Persistence;
using Xunit;

namespace LViz.Tests.GitHub;

/// <summary>
/// <see cref="GitHubSession"/>: restoring the stored login at most once,
/// completing the device flow, handing out a
/// valid access token (refreshing near expiry, single-flight), signing out,
/// and keeping two sessions under different secret keys apart. Runs on
/// <see cref="InMemorySecretStore"/> and a real
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

    // ---- EnsureRestoredAsync ----

    [Fact]
    public async Task EnsureRestored_ConcurrentAndLaterCalls_ReadStoreOnce()
    {
        // Empty store on purpose: an unsigned-in session re-reads on every
        // RestoreAsync call, so the single read pins THIS session's task
        // cache, not its signed-in short-circuit.
        var counting = new CountingSecretStore(_store);
        var session = SessionOver(counting);

        await Task.WhenAll(session.EnsureRestoredAsync(), session.EnsureRestoredAsync());
        await session.EnsureRestoredAsync();

        Assert.Equal(1, counting.Reads);
        Assert.False(session.IsSignedIn);
    }

    [Fact]
    public async Task EnsureRestored_AfterFaultedRestore_RetriesOnNextCall()
    {
        // A first read that throws must not stay dead: the faulted attempt is
        // dropped and the next call restores the login the store holds.
        await Store(Tokens(Base.AddHours(8)));
        var session = SessionOver(new ThrowingOnceSecretStore(_store));

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EnsureRestoredAsync());

        Assert.False(session.IsSignedIn);
        await session.EnsureRestoredAsync();

        Assert.True(session.IsSignedIn);
        Assert.Equal(Login, session.Login);
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

    // ---- Device flow ----

    [Fact]
    public async Task CompleteSignIn_AdoptsTheLoginAndStoresIt()
    {
        // pending once, then the grant; the session's delay returns instantly.
        var polls = new Queue<Func<HttpResponseMessage>>([
            () => FakeHttpHandler.Json("{\"error\":\"authorization_pending\"}"),
            () => FakeHttpHandler.Json(GrantJson()),
        ]);
        _handler.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/login/device/code" => FakeHttpHandler.Json(DeviceCodeJson()),
            "/login/oauth/access_token" => polls.Dequeue()(),
            "/user" => FakeHttpHandler.Json($$"""{"login":"{{Login}}"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var session = new GitHubSession(
            new GitHubClient(new HttpClient(_handler), ClientId, UserAgent, () => _now),
            _store,
            () => _now,
            delay: static (_, _) => Task.CompletedTask);

        var code = await session.StartSignInAsync();
        await session.CompleteSignInAsync(code);

        Assert.True(session.IsSignedIn);
        Assert.Equal(Login, session.Login);

        var stored = GitHubTokens.TryFromJson(await _store.ReadAsync(GitHubAppInfo.SecretKey));
        Assert.NotNull(stored);
        Assert.Equal("ghu_fresh", stored.AccessToken);
        Assert.Equal(Login, stored.Login);
    }

    // ---- A second App under its own secret key ----

    [Fact]
    public async Task CustomSecretKey_SignInWritesUnderThatKey()
    {
        var builds = BuildsSession();

        await builds.SignInAsync(Tokens(Base.AddHours(8)));

        Assert.NotNull(await _store.ReadAsync(GitHubBuildsAppInfo.SecretKey));
        Assert.Null(await _store.ReadAsync(GitHubAppInfo.SecretKey));
    }

    [Fact]
    public async Task TwoSessions_DoNotTouchEachOthersLogin()
    {
        var builds = BuildsSession();
        await _session.SignInAsync(Tokens(Base.AddHours(8)));
        await builds.SignInAsync(Tokens(Base.AddHours(8)));

        await builds.SignOutAsync();

        Assert.True(_session.IsSignedIn);
        Assert.NotNull(await _store.ReadAsync(GitHubAppInfo.SecretKey));
        Assert.Null(await _store.ReadAsync(GitHubBuildsAppInfo.SecretKey));

        // ... and the other way round.
        await builds.SignInAsync(Tokens(Base.AddHours(8)));
        await _session.SignOutAsync();

        Assert.True(builds.IsSignedIn);
        Assert.NotNull(await _store.ReadAsync(GitHubBuildsAppInfo.SecretKey));
        Assert.Null(await _store.ReadAsync(GitHubAppInfo.SecretKey));
    }

    [Fact]
    public async Task CustomSecretKey_RestoreReadsThatKey()
    {
        await _store.WriteAsync(GitHubBuildsAppInfo.SecretKey, Tokens(Base.AddHours(8)).ToJson());

        var builds = BuildsSession();
        Assert.True(await builds.RestoreAsync());
        Assert.Equal(Login, builds.Login);

        // The default session on the same store sees nothing of its own.
        Assert.False(await _session.RestoreAsync());
        Assert.False(_session.IsSignedIn);
    }

    [Fact]
    public void GitHubBuildsAppInfo_InstallUrl_IsItsInstallationPage()
    {
        Assert.Equal(
            "https://github.com/apps/lviz-for-zmk-builds/installations/new",
            GitHubBuildsAppInfo.InstallUrl);
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

    /// <summary>A session for the second App: its client id and its secret key on the shared store.</summary>
    private GitHubSession BuildsSession() =>
        new(
            new GitHubClient(new HttpClient(_handler), GitHubBuildsAppInfo.ClientId, UserAgent, () => _now),
            _store,
            () => _now,
            GitHubBuildsAppInfo.SecretKey);

    /// <summary>A session on this test's client and clock, reading through <paramref name="store"/>.</summary>
    private GitHubSession SessionOver(ISecretStore store) =>
        new(new GitHubClient(new HttpClient(_handler), ClientId, UserAgent, () => _now), store, () => _now);

    private static string DeviceCodeJson() =>
        "{\"device_code\":\"dc-1\",\"user_code\":\"ABCD-1234\","
        + "\"verification_uri\":\"https://github.com/login/device\",\"expires_in\":900,\"interval\":5}";

    private static string GrantJson() =>
        "{\"access_token\":\"ghu_fresh\",\"expires_in\":28800,"
        + "\"refresh_token\":\"ghr_fresh\",\"refresh_token_expires_in\":15897600}";

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

    /// <summary>Wraps a store and counts reads, to prove the restore ran once.</summary>
    private sealed class CountingSecretStore(ISecretStore inner) : ISecretStore
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public Task<string?> ReadAsync(string key, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _reads);
            return inner.ReadAsync(key, ct);
        }

        public Task<bool> WriteAsync(string key, string value, CancellationToken ct = default) =>
            inner.WriteAsync(key, value, ct);

        public Task DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
    }

    /// <summary>A store whose first read throws — a keychain that loses the startup race.</summary>
    private sealed class ThrowingOnceSecretStore(ISecretStore inner) : ISecretStore
    {
        private int _reads;

        public Task<string?> ReadAsync(string key, CancellationToken ct = default) =>
            Interlocked.Increment(ref _reads) == 1
                ? Task.FromException<string?>(new InvalidOperationException("keychain unavailable"))
                : inner.ReadAsync(key, ct);

        public Task<bool> WriteAsync(string key, string value, CancellationToken ct = default) =>
            inner.WriteAsync(key, value, ct);

        public Task DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
    }
}
