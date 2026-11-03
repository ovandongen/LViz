using LViz.App.Services.GitHub;
using LViz.Core.GitHub;

namespace LViz.Tests.GitHub;

/// <summary>
/// Scriptable <see cref="IGitHubKeymapService"/> for UI tests. Every method
/// forwards to a settable <c>On*</c> delegate (the method's arguments minus
/// the cancellation token, returning its result type — a <see cref="Task"/>
/// for the async ones, so tests can delay or throw) and appends its name to
/// <see cref="Calls"/>. Defaults: restore/sign-out complete, cache lookups
/// report "nothing cached", everything else throws
/// <see cref="NotSupportedException"/> so an un-scripted call fails loudly.
/// Cards 12–13 reuse this fake for the GitHub window tests.
/// </summary>
public sealed class FakeGitHubKeymapService : IGitHubKeymapService
{
    public string? Login { get; set; }
    public bool IsSignedIn { get; set; }
    public bool IsLoginPersisted { get; set; } = true;

    /// <summary>Names of the service methods called, in call order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The token the last <see cref="CompleteSignInAsync"/> call
    /// received (null until it runs), so tests can assert a cancel reached
    /// the service; added for the GitHub window tests.</summary>
    public CancellationToken? LastSignInToken { get; private set; }

    public Func<Task> OnEnsureRestored { get; set; } = () => Task.CompletedTask;
    public Func<Task<GitHubDeviceCode>> OnStartSignIn { get; set; } = NotSupported<GitHubDeviceCode>;
    public Func<GitHubDeviceCode, Task> OnCompleteSignIn { get; set; } =
        _ => throw new NotSupportedException();
    public Func<Task> OnSignOut { get; set; } = () => Task.CompletedTask;
    public Func<Task<IReadOnlyList<GitHubRepo>>> OnListRepos { get; set; } =
        NotSupported<IReadOnlyList<GitHubRepo>>;
    public Func<string, string, Task<IReadOnlyList<string>>> OnListBranches { get; set; } =
        (_, _) => throw new NotSupportedException();
    public Func<string, string, string, Task<GitHubCacheResult>> OnFetchRepo { get; set; } =
        (_, _, _) => throw new NotSupportedException();
    public Func<GitHubKeymapRef, Task<GitHubOpenResult>> OnOpen { get; set; } =
        _ => throw new NotSupportedException();
    public Func<GitHubKeymapRef, byte[], string, string, bool, Task<GitHubSaveResult>> OnSave { get; set; } =
        (_, _, _, _, _) => throw new NotSupportedException();
    public Func<GitHubKeymapRef, string?> OnLocalPathFor { get; set; } = _ => null;
    public Func<string, string, string, string> OnCacheFolderFor { get; set; } =
        (owner, repo, branch) =>
            Path.Combine(Path.GetTempPath(), "lviz-fake-github", owner, repo, branch);

    public Task EnsureRestoredAsync(CancellationToken ct = default)
    {
        Calls.Add(nameof(EnsureRestoredAsync));
        return OnEnsureRestored();
    }

    public Task<GitHubDeviceCode> StartSignInAsync(CancellationToken ct = default)
    {
        Calls.Add(nameof(StartSignInAsync));
        return OnStartSignIn();
    }

    public Task CompleteSignInAsync(GitHubDeviceCode code, CancellationToken ct = default)
    {
        Calls.Add(nameof(CompleteSignInAsync));
        LastSignInToken = ct;
        return OnCompleteSignIn(code);
    }

    public Task SignOutAsync(CancellationToken ct = default)
    {
        Calls.Add(nameof(SignOutAsync));
        return OnSignOut();
    }

    public Task<IReadOnlyList<GitHubRepo>> ListReposAsync(CancellationToken ct = default)
    {
        Calls.Add(nameof(ListReposAsync));
        return OnListRepos();
    }

    public Task<IReadOnlyList<string>> ListBranchesAsync(string owner, string repo, CancellationToken ct = default)
    {
        Calls.Add(nameof(ListBranchesAsync));
        return OnListBranches(owner, repo);
    }

    public Task<GitHubCacheResult> FetchRepoAsync(
        string owner, string repo, string branch, CancellationToken ct = default)
    {
        Calls.Add(nameof(FetchRepoAsync));
        return OnFetchRepo(owner, repo, branch);
    }

    public Task<GitHubOpenResult> OpenAsync(GitHubKeymapRef keymap, CancellationToken ct = default)
    {
        Calls.Add(nameof(OpenAsync));
        return OnOpen(keymap);
    }

    public Task<GitHubSaveResult> SaveAsync(
        GitHubKeymapRef keymap,
        byte[] content,
        string baseBlobSha,
        string message,
        bool overwrite,
        CancellationToken ct = default)
    {
        Calls.Add(nameof(SaveAsync));
        return OnSave(keymap, content, baseBlobSha, message, overwrite);
    }

    public string? LocalPathFor(GitHubKeymapRef keymap)
    {
        Calls.Add(nameof(LocalPathFor));
        return OnLocalPathFor(keymap);
    }

    public string CacheFolderFor(string owner, string repo, string branch)
    {
        Calls.Add(nameof(CacheFolderFor));
        return OnCacheFolderFor(owner, repo, branch);
    }

    private static Task<T> NotSupported<T>() => throw new NotSupportedException();
}
