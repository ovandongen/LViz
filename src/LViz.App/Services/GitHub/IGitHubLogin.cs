using LViz.Core.GitHub;

namespace LViz.App.Services.GitHub;

/// <summary>
/// One GitHub App login, as the sign-in UI needs it: whether it is held,
/// restoring it once, and the OAuth device flow that establishes or replaces
/// it. Implemented per App — the keymap service and the firmware-builds
/// service each surface their own App's login.
/// </summary>
public interface IGitHubLogin
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

    /// <summary>Signs out and wipes what the login fetched.</summary>
    Task SignOutAsync(CancellationToken ct = default);
}
