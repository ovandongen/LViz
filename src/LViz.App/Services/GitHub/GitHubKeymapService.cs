using LViz.Core.Diagnostics;
using LViz.Core.GitHub;

namespace LViz.App.Services.GitHub;

/// <summary>
/// The <see cref="IGitHubKeymapService"/> wiring. It adds no knowledge of its
/// own: <see cref="GitHubSession"/> owns the login, <see cref="GitHubClient"/>
/// owns HTTP, <see cref="GitHubRepoCache"/> owns the local copies — this class
/// only ties them together and enforces the house rules: restore at most once,
/// fetch the access token right before every refresh (never hold it), wipe the
/// repo cache on sign-out, and fall back to the cached copy of a keymap when
/// GitHub is unreachable.
///
/// <para>
/// The access-token lifetime is why <see cref="EnsureRestoredAsync"/> caches
/// its <see cref="Task"/> rather than a result: concurrent first calls must
/// not read the secret store twice, but a failed attempt must not poison the
/// service for good.
/// </para>
/// </summary>
/// <param name="client">The GitHub HTTP client.</param>
/// <param name="session">The stored login.</param>
/// <param name="cache">The local repo copies.</param>
/// <param name="delay">
/// How the device-flow polling waits; defaults to <see cref="Task.Delay"/>.
/// Tests pass one that returns instantly.
/// </param>
public sealed class GitHubKeymapService(
    GitHubClient client,
    GitHubSession session,
    GitHubRepoCache cache,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IGitHubKeymapService
{
    private readonly Lock _restoreLock = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private Task<bool>? _restoreTask;

    /// <inheritdoc/>
    public string? Login => session.Login;

    /// <inheritdoc/>
    public bool IsSignedIn => session.IsSignedIn;

    /// <inheritdoc/>
    public bool IsLoginPersisted => session.IsPersisted;

    /// <inheritdoc/>
    public Task EnsureRestoredAsync(CancellationToken ct = default)
    {
        lock (_restoreLock)
        {
            // In flight and succeeded are shared as-is; a faulted or cancelled
            // attempt is replaced so the next call retries — a store read that
            // lost the startup race must not stay dead.
            if (_restoreTask is { IsFaulted: false, IsCanceled: false } shared)
            {
                return shared;
            }

            _restoreTask = shared = session.RestoreAsync(ct);
            return shared;
        }
    }

    /// <inheritdoc/>
    public Task<GitHubDeviceCode> StartSignInAsync(CancellationToken ct = default) =>
        client.StartDeviceFlowAsync(ct);

    /// <inheritdoc/>
    public async Task CompleteSignInAsync(GitHubDeviceCode code, CancellationToken ct = default)
    {
        var tokens = await client.WaitForDeviceAuthorizationAsync(code, _delay, ct);
        await session.SignInAsync(tokens, ct);
    }

    /// <inheritdoc/>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await session.SignOutAsync(ct);

        // Private repo contents must not outlive the login.
        cache.DeleteAll();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GitHubRepo>> ListReposAsync(CancellationToken ct = default)
    {
        await EnsureRestoredAsync(ct);
        return await client.ListInstalledReposAsync(await session.GetAccessTokenAsync(ct), ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListBranchesAsync(
        string owner, string repo, CancellationToken ct = default)
    {
        await EnsureRestoredAsync(ct);
        return await client.ListBranchesAsync(await session.GetAccessTokenAsync(ct), owner, repo, ct);
    }

    /// <inheritdoc/>
    public async Task<GitHubCacheResult> FetchRepoAsync(
        string owner, string repo, string branch, CancellationToken ct = default)
    {
        await EnsureRestoredAsync(ct);

        // The token is fetched right here (it may refresh) and never held.
        var accessToken = await session.GetAccessTokenAsync(ct);
        return await cache.RefreshAsync(client, accessToken, owner, repo, branch, ct);
    }

    /// <inheritdoc/>
    public async Task<GitHubOpenResult> OpenAsync(GitHubKeymapRef keymap, CancellationToken ct = default)
    {
        try
        {
            await EnsureRestoredAsync(ct);
            if (!IsSignedIn)
            {
                return new GitHubOpenResult.Failed(GitHubErrorKind.Unauthorized, "Not signed in");
            }

            try
            {
                await FetchRepoAsync(keymap.Owner, keymap.Repo, keymap.Branch, ct);
            }
            catch (GitHubApiException ex) when (ex.Kind == GitHubErrorKind.Network)
            {
                // GitHub is unreachable, but a copy we already have beats an error.
                var offline = cache.LocalPathFor(keymap);
                return offline is not null
                    ? new GitHubOpenResult.Loaded(offline, FromCacheOffline: true)
                    : new GitHubOpenResult.Failed(GitHubErrorKind.Network, ex.Message);
            }

            var path = cache.LocalPathFor(keymap);
            return path is not null
                ? new GitHubOpenResult.Loaded(path, FromCacheOffline: false)
                : new GitHubOpenResult.Failed(
                    GitHubErrorKind.NotFound,
                    $"{keymap.Path} is not in {keymap.RepoFullName} ({keymap.Branch})");
        }
        catch (GitHubApiException ex)
        {
            return new GitHubOpenResult.Failed(ex.Kind, ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("GitHub", $"Open {keymap}: {ex.Message}");
            return new GitHubOpenResult.Failed(GitHubErrorKind.Unexpected, ex.Message);
        }
    }

    /// <inheritdoc/>
    public string? LocalPathFor(GitHubKeymapRef keymap) => cache.LocalPathFor(keymap);

    /// <inheritdoc/>
    public string CacheFolderFor(string owner, string repo, string branch) =>
        cache.FolderFor(owner, repo, branch);
}
