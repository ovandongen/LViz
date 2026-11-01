using LViz.Core.GitHub;

namespace LViz.App.Services.GitHub;

/// <summary>
/// The single thing the main window and the GitHub window talk to for
/// GitHub-hosted keymaps: signing in through the OAuth device flow, browsing
/// the repositories installed on the LViz App and their branches, refreshing
/// the local cache copy of a repository, and opening a keymap from it.
///
/// <para>
/// Failure contract: everything throws <see cref="GitHubApiException"/> for a
/// GitHub failure and propagates a cancelled token, except
/// <see cref="OpenAsync"/> (which reports through <see cref="GitHubOpenResult"/>
/// so offline fallback can ride along) and <see cref="LocalPathFor"/> /
/// <see cref="CacheFolderFor"/> (no I/O beyond a file check).
/// </para>
/// </summary>
public interface IGitHubKeymapService
{
    /// <summary>The logged-in user's handle, or null when signed out.</summary>
    string? Login { get; }

    /// <summary>True while a login is held (persisted or session-only).</summary>
    bool IsSignedIn { get; }

    /// <summary>True when the login would survive a restart; false when it only lives in this process.</summary>
    bool IsLoginPersisted { get; }

    /// <summary>
    /// Restores the stored login at most once — later and concurrent calls
    /// await the same call. A failed attempt is dropped, so the next call
    /// retries.
    /// </summary>
    Task EnsureRestoredAsync(CancellationToken ct = default);

    /// <summary>Opens the device flow; show the returned code and URL to the user.</summary>
    Task<GitHubDeviceCode> StartSignInAsync(CancellationToken ct = default);

    /// <summary>Polls until the user authorizes the code, then adopts the login.</summary>
    Task CompleteSignInAsync(GitHubDeviceCode code, CancellationToken ct = default);

    /// <summary>
    /// Signs out, forgetting the tokens and wiping the repo cache so private
    /// repo contents do not linger.
    /// </summary>
    Task SignOutAsync(CancellationToken ct = default);

    /// <summary>The repositories the current login's App installation can see.</summary>
    Task<IReadOnlyList<GitHubRepo>> ListReposAsync(CancellationToken ct = default);

    /// <summary>Branch names of <c>owner/repo</c>, in API order.</summary>
    Task<IReadOnlyList<string>> ListBranchesAsync(string owner, string repo, CancellationToken ct = default);

    /// <summary>
    /// Makes the local copy of <c>owner/repo</c> at <paramref name="branch"/>
    /// match GitHub, downloading only when the branch head moved.
    /// </summary>
    Task<GitHubCacheResult> FetchRepoAsync(string owner, string repo, string branch, CancellationToken ct = default);

    /// <summary>
    /// Refreshes the keymap's repo + branch and returns the local path of its
    /// file. When GitHub cannot be reached, falls back to the cached copy of
    /// that file and says so through <see cref="GitHubOpenResult.Loaded"/>.
    /// </summary>
    Task<GitHubOpenResult> OpenAsync(GitHubKeymapRef keymap, CancellationToken ct = default);

    /// <summary>The locally cached copy of this keymap, or null when it is not on disk.</summary>
    string? LocalPathFor(GitHubKeymapRef keymap);

    /// <summary>
    /// The cache folder of <c>owner/repo</c> at <paramref name="branch"/>,
    /// whether or not it exists on disk.
    /// </summary>
    string CacheFolderFor(string owner, string repo, string branch);
}

/// <summary>
/// The outcome of <see cref="IGitHubKeymapService.OpenAsync"/>: a local file
/// to open (possibly the offline copy of a cached repo), or the kind and
/// message of what went wrong.
/// </summary>
public abstract record GitHubOpenResult
{
    /// <summary>
    /// The keymap is on disk at <see cref="LocalPath"/>;
    /// <see cref="FromCacheOffline"/> is true when GitHub was unreachable and
    /// this copy came from the cache.
    /// </summary>
    public sealed record Loaded(string LocalPath, bool FromCacheOffline) : GitHubOpenResult;

    /// <summary>The keymap could not be opened.</summary>
    public sealed record Failed(GitHubErrorKind Kind, string Message) : GitHubOpenResult;
}
