using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services.GitHub;
using LViz.Core.GitHub;
using LViz.Core.Keymap.Parser;
using LViz.Core.Layout;
using LViz.Core.Layout.Import;

namespace LViz.App.ViewModels;

/// <summary>
/// What the GitHub window hands back when the user opens a keymap: which
/// reference they picked and where its file sits on disk.
/// </summary>
public sealed record GitHubOpenSelection(GitHubKeymapRef Keymap, string LocalPath);

/// <summary>
/// One <c>.keymap</c> file found in a fetched repo: its repo-relative
/// <c>/</c>-separated path, a translated description (key count and matching
/// board, "no layout yet", or "can't be read"), and the absolute path of the
/// copy sitting in the local cache.
/// </summary>
public sealed record GitHubKeymapItem(string RelativePath, string Detail, string LocalPath);

/// <summary>
/// View model behind the "Open from GitHub…" window. Step 0 signs in through
/// the OAuth device flow — LViz shows the short code, copies it to the
/// clipboard, opens the verification page and notices the approval on its
/// own; step 1 lists the repositories the "LViz for ZMK" app can see, where
/// an empty list offers the install link instead, and picks a branch; step 2
/// shows the keymaps of the fetched repo when it holds more than one — a
/// single keymap opens straight away. The window only wires the
/// clipboard/browser hooks and acts on <see cref="CloseRequested"/>; this
/// class knows no Avalonia windows.
/// </summary>
public partial class GitHubOpenViewModel : ObservableObject
{
    private readonly IGitHubKeymapService _service;

    // Replaced wholesale per sign-in attempt, so a Cancel can only ever hit
    // the wait it was meant for and a stale source is never reused.
    private CancellationTokenSource? _signInCts;

    public GitHubOpenViewModel(IGitHubKeymapService service)
    {
        _service = service;
    }

    // --- hooks the window plugs in ---

    /// <summary>Puts the device code on the clipboard; set by the window.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Opens a URL in the user's browser; set by the window.</summary>
    public Action<string>? OpenUrl { get; set; }

    /// <summary>Raised with the chosen keymap, or null when the user cancels.</summary>
    public event Action<GitHubOpenSelection?>? CloseRequested;

    // --- steps: 0 sign-in, 1 repo list, 2 keymap choice (card 13) ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignInStep))]
    [NotifyPropertyChangedFor(nameof(IsRepoStep))]
    [NotifyPropertyChangedFor(nameof(IsKeymapStep))]
    private int _currentStep;

    public bool IsSignInStep => CurrentStep == 0;
    public bool IsRepoStep => CurrentStep == 1;
    public bool IsKeymapStep => CurrentStep == 2;

    // --- sign-in step ---

    /// <summary>The short code the user types on github.com; null while no flow runs.</summary>
    [ObservableProperty]
    private string? _userCode;

    /// <summary>The github.com page where the code is entered.</summary>
    [ObservableProperty]
    private string? _verificationUri;

    /// <summary>True while waiting for the user to approve the code on GitHub.</summary>
    [ObservableProperty]
    private bool _isWaitingForSignIn;

    // --- shared ---

    /// <summary>Translated reason for the last failure; null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    public bool HasError => ErrorText is not null;

    /// <summary>The logged-in user's handle; null when signed out.</summary>
    [ObservableProperty]
    private string? _login;

    /// <summary>Shown once after a sign-in the keychain could not keep.</summary>
    [ObservableProperty]
    private string? _loginNotice;

    /// <summary>True while a GitHub call is in flight.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private bool _isBusy;

    // --- repo step ---

    public ObservableCollection<GitHubRepo> Repos { get; } = [];

    /// <summary>The repo list came back empty — usually the app is not installed yet.</summary>
    [ObservableProperty]
    private bool _hasNoRepos;

    /// <summary>The repo whose branches and keymaps the rest of the flow works on.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private GitHubRepo? _selectedRepo;

    /// <summary>Branch names of <see cref="SelectedRepo"/>; empty while loading.</summary>
    public ObservableCollection<string> Branches { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private string? _selectedBranch;

    /// <summary>True while the selected repo+branch is being fetched and scanned.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private bool _isFetching;

    /// <summary>The keymaps step 2 offers; filled only when a repo holds several.</summary>
    public ObservableCollection<GitHubKeymapItem> Keymaps { get; } = [];

    /// <summary>Restores a stored login and lands on the repo list when it worked.</summary>
    public async Task InitializeAsync()
    {
        await _service.EnsureRestoredAsync();
        if (_service.IsSignedIn)
        {
            await ShowReposAsync();
        }
        else
        {
            CurrentStep = 0;
        }
    }

    /// <summary>
    /// Step 1: list the repositories the app installation can see. A login
    /// GitHub no longer accepts sends the user back to step 0 with a
    /// "sign in again" line; everything else stays on the list with an
    /// error. The first repo becomes the selection, which starts its
    /// branch load.
    /// </summary>
    internal async Task ShowReposAsync()
    {
        CurrentStep = 1;
        Login = _service.Login;
        ErrorText = null;
        IsBusy = true;
        Repos.Clear();
        HasNoRepos = false;
        try
        {
            foreach (var repo in await _service.ListReposAsync())
            {
                Repos.Add(repo);
            }

            HasNoRepos = Repos.Count == 0;
            SelectedRepo = Repos.FirstOrDefault();
        }
        catch (GitHubApiException e)
        {
            HandleApiError(e);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Shared <see cref="GitHubApiException"/> handling for the repo-list,
    /// branch and fetch steps: a login GitHub rejects sends the user back to
    /// step 0 with a "sign in again" line; everything else stays put with
    /// its translated text.
    /// </summary>
    private void HandleApiError(GitHubApiException e)
    {
        if (e.Kind == GitHubErrorKind.Unauthorized)
        {
            CurrentStep = 0;
            ErrorText = Loc.Instance["GitHub_SignInAgain"];
        }
        else
        {
            ErrorText = MessageFor(e);
        }
    }

    partial void OnSelectedRepoChanged(GitHubRepo? value) => _ = LoadBranchesAsync(value);

    /// <summary>
    /// List <paramref name="repo"/>'s branches and preselect its default
    /// branch (or the first one when the default is gone). Fire-and-forget
    /// from the selection hook: a result that arrives after the user picked
    /// another repo is dropped so two loads never mix lists.
    /// </summary>
    private async Task LoadBranchesAsync(GitHubRepo? repo)
    {
        Branches.Clear();
        SelectedBranch = null;
        if (repo is null)
        {
            return;
        }

        try
        {
            var branches = await _service.ListBranchesAsync(repo.Owner, repo.Name);
            if (!ReferenceEquals(SelectedRepo, repo))
            {
                return;
            }

            foreach (var branch in branches)
            {
                Branches.Add(branch);
            }

            SelectedBranch = branches.Contains(repo.DefaultBranch)
                ? repo.DefaultBranch
                : branches.FirstOrDefault();
        }
        catch (GitHubApiException e)
        {
            HandleApiError(e);
        }
    }

    /// <summary>
    /// Runs the whole device flow: ask GitHub for a code, hand it to the
    /// clipboard and browser hooks, then wait for the approval and continue
    /// straight to the repo list. Cancel() aborts the wait.
    /// </summary>
    [RelayCommand]
    private async Task SignIn()
    {
        ErrorText = null;

        _signInCts?.Dispose();
        _signInCts = new CancellationTokenSource();

        GitHubDeviceCode code;
        try
        {
            code = await _service.StartSignInAsync();
        }
        catch (GitHubApiException e)
        {
            ErrorText = MessageFor(e);
            return;
        }

        UserCode = code.UserCode;
        VerificationUri = code.VerificationUri;
        IsWaitingForSignIn = true;

        try
        {
            await (CopyToClipboard?.Invoke(code.UserCode) ?? Task.CompletedTask);
            OpenUrl?.Invoke(code.VerificationUri);
            await _service.CompleteSignInAsync(code, _signInCts.Token);
        }
        catch (OperationCanceledException)
        {
            IsWaitingForSignIn = false;
            return;
        }
        catch (GitHubApiException e)
        {
            IsWaitingForSignIn = false;
            UserCode = null;
            ErrorText = MessageFor(e);
            return;
        }

        IsWaitingForSignIn = false;
        UserCode = null;
        LoginNotice = _service.IsLoginPersisted ? null : Loc.Instance["GitHub_LoginNotSaved"];
        await ShowReposAsync();
    }

    /// <summary>Re-copies the code after the first automatic copy failed or got lost.</summary>
    [RelayCommand]
    private async Task CopyCode()
    {
        if (UserCode is string code)
        {
            await (CopyToClipboard?.Invoke(code) ?? Task.CompletedTask);
        }
    }

    /// <summary>Re-opens the github.com page when the user closed the first one.</summary>
    [RelayCommand]
    private void OpenVerificationPage()
    {
        if (VerificationUri is string uri)
        {
            OpenUrl?.Invoke(uri);
        }
    }

    /// <summary>Opens the app's installation page — the fix for a zero-repo list.</summary>
    [RelayCommand]
    private void InstallOnRepos() => OpenUrl?.Invoke(GitHubAppInfo.InstallUrl);

    /// <summary>Re-lists the repos after installing the app on GitHub.</summary>
    [RelayCommand]
    private Task ReloadRepos() => ShowReposAsync();

    /// <summary>
    /// Fetch the selected repo+branch into the cache and open what it holds:
    /// the only keymap straight away, several through step 2, none as an
    /// error on step 1. The scan and keymap parsing run on a background
    /// thread (like the wizard's source load); the results are applied here.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task Open()
    {
        var repo = SelectedRepo!;
        var branch = SelectedBranch!;
        ErrorText = null;
        IsFetching = true;
        Keymaps.Clear();
        try
        {
            var result = await _service.FetchRepoAsync(repo.Owner, repo.Name, branch);
            var items = await Task.Run(() => ScanKeymaps(result.Folder));
            if (items.Count == 0)
            {
                ErrorText = Loc.Instance.Format("GitHub_NoKeymapFormat", repo.FullName, branch);
            }
            else if (items.Count == 1)
            {
                Close(items[0]);
            }
            else
            {
                foreach (var item in items)
                {
                    Keymaps.Add(item);
                }

                CurrentStep = 2;
            }
        }
        catch (GitHubApiException e)
        {
            HandleApiError(e);
        }
        finally
        {
            IsFetching = false;
        }
    }

    private bool CanOpen() =>
        SelectedRepo is not null && SelectedBranch is not null && !IsFetching && !IsBusy;

    /// <summary>
    /// Every <c>.keymap</c> in the cache folder <paramref name="folder"/>,
    /// ordinal-sorted by repo-relative path, each described by the board its
    /// first layer's key count resolves to ("no layout yet" for a count no
    /// board has, "can't be read" for a broken file). Runs off the UI thread.
    /// </summary>
    internal static List<GitHubKeymapItem> ScanKeymaps(string folder)
    {
        var items = new List<GitHubKeymapItem>();
        foreach (var path in ZmkConfigScanner.ScanFolder(folder).KeymapPaths)
        {
            var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
            string detail;
            try
            {
                var config = ZmkKeymapLoader.Load(path, "github-pick");
                var count = config.Layers.Count > 0 ? config.Layers[0].Bindings.Count : 0;
                detail = KeyboardProfileRegistry.FindByKeyCount(count) is IKeyboardProfile profile
                    ? Loc.Instance.Format("GitHub_KeymapBoardFormat", count, profile.DisplayName)
                    : Loc.Instance.Format("GitHub_KeymapNoLayoutFormat", count);
            }
            catch (ZmkKeymapParseException)
            {
                detail = Loc.Instance["GitHub_KeymapUnreadable"];
            }

            items.Add(new GitHubKeymapItem(relative, detail, path));
        }

        return items.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToList();
    }

    /// <summary>Step 2: take the keymap the user clicked.</summary>
    [RelayCommand]
    private void PickKeymap(GitHubKeymapItem? item)
    {
        if (item is not null)
        {
            Close(item);
        }
    }

    /// <summary>Step 2 back to the repo/branch step; the list refetches on the next Open.</summary>
    [RelayCommand]
    private void Back()
    {
        if (CurrentStep == 2)
        {
            Keymaps.Clear();
            CurrentStep = 1;
        }
    }

    private void Close(GitHubKeymapItem item) => CloseRequested?.Invoke(new GitHubOpenSelection(
        new GitHubKeymapRef(
            SelectedRepo!.Owner,
            SelectedRepo.Name,
            SelectedBranch!,
            item.RelativePath),
        item.LocalPath));

    /// <summary>Signs out (the service also wipes the repo cache) and returns to step 0.</summary>
    [RelayCommand]
    private async Task SignOut()
    {
        await _service.SignOutAsync();
        Repos.Clear();
        Login = null;
        LoginNotice = null;
        ErrorText = null;
        CurrentStep = 0;
    }

    [RelayCommand]
    private void Cancel()
    {
        _signInCts?.Cancel();
        CloseRequested?.Invoke(null);
    }

    /// <summary>
    /// Translated text for a failed GitHub call. Exception messages are
    /// English diagnostics-log material; only Unexpected surfaces its
    /// message, where nothing else describes the failure.
    /// </summary>
    internal static string MessageFor(GitHubApiException e) => e.Kind switch
    {
        GitHubErrorKind.Network => Loc.Instance["GitHub_ErrorNetwork"],
        GitHubErrorKind.Unauthorized => Loc.Instance["GitHub_SignInAgain"],
        GitHubErrorKind.RateLimited => Loc.Instance["GitHub_ErrorRateLimited"],
        GitHubErrorKind.DeviceCodeExpired => Loc.Instance["GitHub_ErrorCodeExpired"],
        GitHubErrorKind.AccessDenied => Loc.Instance["GitHub_ErrorAccessDenied"],
        GitHubErrorKind.NotFound => Loc.Instance["GitHub_ErrorNotFound"],
        GitHubErrorKind.TooLarge => Loc.Instance["GitHub_ErrorTooLarge"],
        _ => Loc.Instance.Format("GitHub_ErrorUnexpectedFormat", e.Message),
    };
}
