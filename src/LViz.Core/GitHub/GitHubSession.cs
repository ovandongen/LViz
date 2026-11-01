using LViz.Core.Diagnostics;
using LViz.Core.Persistence;

namespace LViz.Core.GitHub;

/// <summary>
/// The current GitHub login: restores it from the <see cref="ISecretStore"/>,
/// hands out an access token that is valid <i>now</i> (refreshing it through
/// <see cref="GitHubClient"/> when it is about to expire), and signs out.
///
/// <para>
/// The login lives as <see cref="GitHubTokens.ToJson"/> under
/// <see cref="GitHubAppInfo.SecretKey"/>. A store that cannot persist it (a
/// Linux box without a keychain, backed by
/// <see cref="Persistence.InMemorySecretStore"/>) still keeps the login for
/// this process — that is what <see cref="IsPersisted"/> reports, so the UI
/// can say so once.
/// </para>
///
/// <para>
/// An access token that has less than five minutes left is refreshed before
/// being handed out. A refresh GitHub rejects (
/// <see cref="GitHubErrorKind.Unauthorized"/>, e.g. <c>bad_refresh_token</c>)
/// signs the session out so the UI asks the user to sign in again; any other
/// failure (<see cref="GitHubErrorKind.Network"/> etc.) keeps the login.
/// Only one refresh runs at a time — concurrent callers share its result.
/// Nothing here ever logs a token; diagnostics carry the login only.
/// </para>
/// </summary>
public sealed class GitHubSession(GitHubClient client, ISecretStore store, Func<DateTimeOffset>? now = null)
{
    /// <summary>An access token with less than this left is refreshed instead of returned.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Func<DateTimeOffset> _now = now ?? UtcNow;
    private GitHubTokens? _tokens;

    /// <summary>True while a login is held (persisted or session-only).</summary>
    public bool IsSignedIn => _tokens is not null;

    /// <summary>The logged-in user's handle, or null when signed out.</summary>
    public string? Login => _tokens?.Login;

    /// <summary>
    /// True once the login survived a store write or came out of the store;
    /// false after a failed write (the login lives only in this process) and
    /// after signing out.
    /// </summary>
    public bool IsPersisted { get; private set; }

    /// <summary>Raised after a sign-in, a sign-out, and a restore that signed in.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Loads the stored login. Already signed in, this returns true without
    /// reading the store. Garbage in the store is false. A login whose access
    /// token is expired <i>and</i> whose refresh token is missing or expired
    /// is deleted from the store and not restored — that one is dead.
    /// Nothing here talks to the network; a refresh happens at the first
    /// <see cref="GetAccessTokenAsync"/>.
    /// </summary>
    public async Task<bool> RestoreAsync(CancellationToken ct = default)
    {
        if (IsSignedIn)
        {
            return true;
        }

        var stored = GitHubTokens.TryFromJson(await store.ReadAsync(GitHubAppInfo.SecretKey, ct));
        if (stored is null)
        {
            return false;
        }

        if (Expired(stored.AccessTokenExpiresAt) && !RefreshUsable(stored))
        {
            DiagnosticLog.Info("GitHub", $"Stored session for {stored.Login} can no longer refresh, discarding it");
            await store.DeleteAsync(GitHubAppInfo.SecretKey, ct);
            return false;
        }

        _tokens = stored;
        IsPersisted = true;
        DiagnosticLog.Info("GitHub", $"Restored session for {stored.Login}");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Adopts a fresh login and stores it. When the store refuses the write,
    /// the login is still active for this process and <see cref="IsPersisted"/>
    /// says false.
    /// </summary>
    public async Task SignInAsync(GitHubTokens tokens, CancellationToken ct = default)
    {
        _tokens = tokens;
        IsPersisted = await store.WriteAsync(GitHubAppInfo.SecretKey, tokens.ToJson(), ct);

        DiagnosticLog.Info("GitHub", $"Signed in as {tokens.Login} (persisted: {IsPersisted})");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// An access token that is valid right now. Null expiry never expires;
    /// otherwise anything with at least five minutes left is returned as is.
    /// Closer than that, the tokens are refreshed once (concurrent callers
    /// wait on the same refresh and share its result) and the new tokens are
    /// written back to the store. A rejected refresh signs the session out and
    /// rethrows; other failures rethrow with the login intact.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_tokens is null)
        {
            throw new GitHubApiException(GitHubErrorKind.Unauthorized, "Not signed in to GitHub");
        }

        await _refreshGate.WaitAsync(ct);
        try
        {
            // Re-read inside the gate: a caller that queued behind a refresh
            // must see (and reuse) the tokens the first caller just got.
            var tokens = _tokens
                ?? throw new GitHubApiException(GitHubErrorKind.Unauthorized, "Not signed in to GitHub");

            if (tokens.AccessTokenExpiresAt is null || tokens.AccessTokenExpiresAt > _now() + RefreshMargin)
            {
                return tokens.AccessToken;
            }

            GitHubTokens refreshed;
            try
            {
                refreshed = await client.RefreshAsync(tokens, ct);
            }
            catch (GitHubApiException ex) when (ex.Kind == GitHubErrorKind.Unauthorized)
            {
                DiagnosticLog.Info("GitHub", $"GitHub rejected the refresh for {tokens.Login}, signing out");
                await SignOutCoreAsync(ct);
                throw;
            }

            _tokens = refreshed;
            IsPersisted = await store.WriteAsync(GitHubAppInfo.SecretKey, refreshed.ToJson(), ct);

            DiagnosticLog.Info("GitHub", $"Refreshed the access token for {refreshed.Login} (persisted: {IsPersisted})");
            return refreshed.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Drops the login and removes it from the store. Signing out while signed
    /// out still cleans the store but raises nothing; the repo cache is NOT
    /// touched here.
    /// </summary>
    public Task SignOutAsync(CancellationToken ct = default) => SignOutCoreAsync(ct);

    private async Task SignOutCoreAsync(CancellationToken ct)
    {
        var wasSignedIn = IsSignedIn;
        var login = Login;

        _tokens = null;
        IsPersisted = false;
        await store.DeleteAsync(GitHubAppInfo.SecretKey, ct);

        if (wasSignedIn)
        {
            DiagnosticLog.Info("GitHub", $"Signed out of GitHub ({login})");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool Expired(DateTimeOffset? expiry) => expiry is not null && expiry <= _now();

    private static DateTimeOffset UtcNow() => DateTimeOffset.UtcNow;

    private bool RefreshUsable(GitHubTokens tokens) =>
        !string.IsNullOrEmpty(tokens.RefreshToken) && !Expired(tokens.RefreshTokenExpiresAt);
}
