using LViz.App.Localization;
using LViz.App.ViewModels;
using LViz.Core.GitHub;
using LViz.Core.Keymap.Parser;
using LViz.Core.Layout;
using LViz.Tests.GitHub;
using LViz.Tests.Keymap;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The "Open from GitHub" window logic: step 0 sign-in (device flow through
/// the fake's hooks), step 1 repo list and branch choice, and step 2 —
/// fetching a repo into the cache, scanning its keymaps and picking one —
/// including error mapping. Expected texts come from <see cref="Loc"/> so a
/// Dutch box still passes, paired with "[Key"-checks proving the resx
/// entries exist.
/// </summary>
public sealed class GitHubOpenViewModelTests : IDisposable
{
    private const string Code = "ABCD-1234";
    private const string VerifyUrl = "https://github.com/login/device";

    /// <summary>Temp "cache folders" handed to the fetch hook, deleted on dispose.</summary>
    private readonly List<string> _cacheFolders = [];

    public void Dispose()
    {
        foreach (var folder in _cacheFolders)
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
    }

    /// <summary>Builds a temp folder holding the given <c>/</c>-separated files.</summary>
    private string CacheFolder(params (string Relative, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "lviz-github-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _cacheFolders.Add(root);
        foreach (var (relative, content) in files)
        {
            var path = Path.Combine([root, .. relative.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return root;
    }

    private static GitHubDeviceCode DeviceCode() =>
        new("device-secret", Code, VerifyUrl, TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5));

    private static List<GitHubRepo> TwoRepos() =>
        [new("octocat", "zmk-config", "main", false), new("octocat", "dotfiles", "master", true)];

    /// <summary>Signed-in fake listing <see cref="TwoRepos"/>, each with only a "main" branch.</summary>
    private static FakeGitHubKeymapService SignedInFake() => new()
    {
        IsSignedIn = true,
        Login = "octocat",
        OnListRepos = () => Task.FromResult<IReadOnlyList<GitHubRepo>>(TwoRepos()),
        OnListBranches = (_, _) => Task.FromResult<IReadOnlyList<string>>(["main"]),
    };

    /// <summary>
    /// The detail a readable fixture must show, computed the way the picker
    /// does: board for the first layer's key count via <see cref="KeyboardProfileRegistry"/>
    /// or the "no layout yet" text — never a hard-coded board name.
    /// </summary>
    private static string ExpectedDetail(string fixture)
    {
        var config = ZmkKeymapLoader.LoadFromText(KeymapFixtures.Read(fixture), "test");
        var count = config.Layers[0].Bindings.Count;
        return KeyboardProfileRegistry.FindByKeyCount(count) is IKeyboardProfile profile
            ? Loc.Instance.Format("GitHub_KeymapBoardFormat", count, profile.DisplayName)
            : Loc.Instance.Format("GitHub_KeymapNoLayoutFormat", count);
    }

    private static string T(string key) => Loc.Instance[key];

    /// <summary>VM with both hooks recording their arguments into the given lists.</summary>
    private static GitHubOpenViewModel Wire(FakeGitHubKeymapService fake, List<string> copied, List<string> opened)
    {
        var vm = new GitHubOpenViewModel(fake);
        vm.CopyToClipboard = c =>
        {
            copied.Add(c);
            return Task.CompletedTask;
        };
        vm.OpenUrl = opened.Add;
        return vm;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    // --- InitializeAsync / repo step ---

    [Fact]
    public async Task InitializeAsync_SignedOut_StaysOnSignInStep()
    {
        var fake = new FakeGitHubKeymapService();
        var vm = Wire(fake, [], []);

        await vm.InitializeAsync();

        Assert.Equal(0, vm.CurrentStep);
        Assert.True(vm.IsSignInStep);
        Assert.Contains(nameof(FakeGitHubKeymapService.EnsureRestoredAsync), fake.Calls);
        Assert.DoesNotContain(nameof(FakeGitHubKeymapService.ListReposAsync), fake.Calls);
    }

    [Fact]
    public async Task InitializeAsync_SignedIn_ListsRepos()
    {
        var fake = new FakeGitHubKeymapService { IsSignedIn = true, Login = "octocat" };
        fake.OnListRepos = () => Task.FromResult<IReadOnlyList<GitHubRepo>>(TwoRepos());
        var vm = Wire(fake, [], []);

        await vm.InitializeAsync();

        Assert.Equal(1, vm.CurrentStep);
        Assert.Equal(2, vm.Repos.Count);
        Assert.Equal("octocat", vm.Login);
        Assert.False(vm.HasNoRepos);
        Assert.False(vm.IsBusy);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task InitializeAsync_SignedInWithoutRepos_SetsHasNoRepos()
    {
        var fake = new FakeGitHubKeymapService { IsSignedIn = true, Login = "octocat" };
        fake.OnListRepos = () => Task.FromResult<IReadOnlyList<GitHubRepo>>([]);
        var vm = Wire(fake, [], []);

        await vm.InitializeAsync();

        Assert.True(vm.HasNoRepos);
        Assert.Empty(vm.Repos);
    }

    [Fact]
    public async Task ShowRepos_Unauthorized_ReturnsToSignInWithSignInAgainText()
    {
        var fake = new FakeGitHubKeymapService { IsSignedIn = true, Login = "octocat" };
        fake.OnListRepos = () => throw new GitHubApiException(GitHubErrorKind.Unauthorized, "bad token");
        var vm = Wire(fake, [], []);

        await vm.InitializeAsync();

        Assert.Equal(0, vm.CurrentStep);
        Assert.Equal(T("GitHub_SignInAgain"), vm.ErrorText);
        Assert.DoesNotContain("[GitHub_", vm.ErrorText);
        Assert.True(vm.HasError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ShowRepos_Network_StaysOnRepoListWithError()
    {
        var fake = new FakeGitHubKeymapService { IsSignedIn = true, Login = "octocat" };
        fake.OnListRepos = () =>
            Task.FromException<IReadOnlyList<GitHubRepo>>(
                new GitHubApiException(GitHubErrorKind.Network, "no route to host"));
        var vm = Wire(fake, [], []);

        await vm.InitializeAsync();

        Assert.Equal(1, vm.CurrentStep);
        Assert.Equal(T("GitHub_ErrorNetwork"), vm.ErrorText);
        Assert.DoesNotContain("[GitHub_", vm.ErrorText);
        Assert.False(vm.HasNoRepos);
        Assert.False(vm.IsBusy);
    }

    // --- sign-in step ---

    [Fact]
    public async Task SignInCommand_CopiesCodeOpensPageAndLoadsReposAfterApproval()
    {
        var fake = new FakeGitHubKeymapService();
        fake.OnStartSignIn = () => Task.FromResult(DeviceCode());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OnCompleteSignIn = _ => gate.Task;
        fake.OnListRepos = () => Task.FromResult<IReadOnlyList<GitHubRepo>>(TwoRepos());

        var copied = new List<string>();
        var opened = new List<string>();
        var vm = Wire(fake, copied, opened);

        var running = vm.SignInCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.IsWaitingForSignIn);

        Assert.Equal([Code], copied);
        Assert.Equal([VerifyUrl], opened);
        Assert.Equal(Code, vm.UserCode);
        Assert.Equal(VerifyUrl, vm.VerificationUri);

        fake.IsSignedIn = true;
        fake.Login = "octocat";
        gate.SetResult();
        await running;

        Assert.False(vm.IsWaitingForSignIn);
        Assert.Null(vm.UserCode);
        Assert.Equal(1, vm.CurrentStep);
        Assert.Equal(2, vm.Repos.Count);
        Assert.Equal("octocat", vm.Login);
        Assert.Null(vm.LoginNotice); // tokens were persisted
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task SignInCommand_WithoutKeychain_ShowsNotSavedNotice()
    {
        var fake = new FakeGitHubKeymapService { IsLoginPersisted = false };
        fake.OnStartSignIn = () => Task.FromResult(DeviceCode());
        fake.OnCompleteSignIn = _ => Task.CompletedTask;
        fake.OnListRepos = () => Task.FromResult<IReadOnlyList<GitHubRepo>>(TwoRepos());
        var vm = Wire(fake, [], []);

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Equal(T("GitHub_LoginNotSaved"), vm.LoginNotice);
        Assert.DoesNotContain("[GitHub_", vm.LoginNotice);
    }

    [Fact]
    public async Task SignInCommand_CodeExpired_ShowsExpiredTextOnSignInStep()
    {
        var fake = new FakeGitHubKeymapService();
        fake.OnStartSignIn = () => Task.FromResult(DeviceCode());
        fake.OnCompleteSignIn = _ =>
            Task.FromException(new GitHubApiException(GitHubErrorKind.DeviceCodeExpired, "expired"));
        var vm = Wire(fake, [], []);

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.CurrentStep);
        Assert.False(vm.IsWaitingForSignIn);
        Assert.Null(vm.UserCode);
        Assert.Equal(T("GitHub_ErrorCodeExpired"), vm.ErrorText);
        Assert.DoesNotContain("[GitHub_", vm.ErrorText);
    }

    [Fact]
    public async Task CancelCommand_DuringWait_CancelsServiceAndCloses()
    {
        var fake = new FakeGitHubKeymapService();
        fake.OnStartSignIn = () => Task.FromResult(DeviceCode());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OnCompleteSignIn = _ => gate.Task;

        var vm = Wire(fake, [], []);
        var closed = false;
        GitHubOpenSelection? closeArg = null;
        vm.CloseRequested += selection =>
        {
            closed = true;
            closeArg = selection;
        };

        var running = vm.SignInCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.IsWaitingForSignIn);

        // The real service stops polling the moment its token fires; the fake
        // hands the token back unwatched, so mirror that behaviour here.
        var token = fake.LastSignInToken!.Value;
        token.Register(() => gate.TrySetCanceled(token));

        vm.CancelCommand.Execute(null);

        Assert.True(closed);
        Assert.Null(closeArg);
        Assert.True(token.IsCancellationRequested);

        await running;
        Assert.False(vm.IsWaitingForSignIn);
        Assert.DoesNotContain(nameof(FakeGitHubKeymapService.ListReposAsync), fake.Calls);
    }

    [Fact]
    public async Task CopyCodeCommand_CopiesAgainOnlyWithCode()
    {
        var copied = new List<string>();
        var vm = Wire(new FakeGitHubKeymapService(), copied, []);

        await vm.CopyCodeCommand.ExecuteAsync(null);
        Assert.Empty(copied);

        vm.UserCode = Code;
        await vm.CopyCodeCommand.ExecuteAsync(null);
        Assert.Equal([Code], copied);
    }

    // --- repo step commands ---

    [Fact]
    public async Task SignOutCommand_ClearsEverything()
    {
        var fake = new FakeGitHubKeymapService { IsSignedIn = true, Login = "octocat" };
        fake.OnListRepos = () => Task.FromResult<IReadOnlyList<GitHubRepo>>(TwoRepos());
        var vm = Wire(fake, [], []);
        await vm.InitializeAsync();
        Assert.Equal(1, vm.CurrentStep);

        await vm.SignOutCommand.ExecuteAsync(null);

        Assert.Contains(nameof(FakeGitHubKeymapService.SignOutAsync), fake.Calls);
        Assert.Equal(0, vm.CurrentStep);
        Assert.Empty(vm.Repos);
        Assert.Null(vm.Login);
        Assert.Null(vm.LoginNotice);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public void InstallOnReposCommand_OpensInstallPage()
    {
        var opened = new List<string>();
        var vm = Wire(new FakeGitHubKeymapService(), [], opened);

        vm.InstallOnReposCommand.Execute(null);

        Assert.Equal(["https://github.com/apps/lviz-for-zmk/installations/new"], opened);
    }

    // --- error text mapping ---

    [Fact]
    public void MessageFor_EveryKind_GetsDistinctResolvedText()
    {
        // Conflict (HTTP 409) cannot reach the UI yet — no call returns it
        // until keymap saving lands — and its translated text arrives with
        // the save UI (saving chain, later card), which must drop this
        // exclusion when it adds the mapping and the Loc keys.
        var kinds = Enum.GetValues<GitHubErrorKind>()
            .Where(kind => kind != GitHubErrorKind.Conflict)
            .ToArray();
        var texts = kinds
            .Select(kind => GitHubOpenViewModel.MessageFor(new GitHubApiException(kind, "detail")))
            .ToList();

        foreach (var text in texts)
        {
            Assert.False(string.IsNullOrWhiteSpace(text));
            // Loc answers "[Key]" for missing entries — nothing may start that way.
            Assert.False(text.StartsWith('['));
        }

        Assert.Equal(kinds.Length, texts.Distinct().Count());
    }

    // --- branch selection ---

    [Fact]
    public async Task InitializeAsync_ReposLoaded_PrepselectsFirstRepoAndDefaultBranch()
    {
        var fake = SignedInFake();
        fake.OnListBranches = (_, _) => Task.FromResult<IReadOnlyList<string>>(["dev", "main"]);
        var vm = Wire(fake, [], []);

        await vm.InitializeAsync();

        Assert.Same(vm.Repos[0], vm.SelectedRepo);
        Assert.Equal("zmk-config", vm.SelectedRepo!.Name);
        await WaitUntil(() => vm.SelectedBranch is not null);
        Assert.Equal("main", vm.SelectedBranch);
        Assert.Equal(["dev", "main"], vm.Branches);
    }

    [Fact]
    public async Task Branches_DefaultBranchMissing_PicksFirstBranch()
    {
        var fake = SignedInFake();
        fake.OnListBranches = (_, _) => Task.FromResult<IReadOnlyList<string>>(["dev"]);
        var vm = Wire(fake, [], []);

        await vm.InitializeAsync();
        await WaitUntil(() => vm.SelectedBranch is not null);

        Assert.Equal("dev", vm.SelectedBranch);
        Assert.Equal(["dev"], vm.Branches);
    }

    [Fact]
    public async Task SelectedRepo_SwitchedWhileBranchesPending_LateResultIsDropped()
    {
        var fake = SignedInFake();
        var gate = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OnListBranches = (_, repo) => repo == "zmk-config"
            ? gate.Task
            : Task.FromResult<IReadOnlyList<string>>(["beta"]);
        var vm = Wire(fake, [], []);

        await vm.InitializeAsync();
        Assert.Equal("zmk-config", vm.SelectedRepo!.Name);
        Assert.Empty(vm.Branches); // A's branches are still in flight

        vm.SelectedRepo = vm.Repos[1];
        await WaitUntil(() => vm.SelectedBranch == "beta");

        gate.SetResult(["dev", "main"]);
        await Task.Delay(50); // give the dropped continuation its chance to misfire

        Assert.Equal(["beta"], vm.Branches);
        Assert.Equal("beta", vm.SelectedBranch);
    }

    // --- open / keymap step ---

    /// <summary>
    /// VM signed in on repo "zmk-config" branch "main" whose fetch hands back
    /// a cache folder with three keymaps: broken, corne (42 keys) and
    /// glove80-editor; Open has run. Selections the VM closed with are collected.
    /// </summary>
    private async Task<(GitHubOpenViewModel Vm, List<GitHubOpenSelection?> Selections)>
        StepTwoWithThreeKeymapsAsync()
    {
        var folder = CacheFolder(
            ("config/corne-broken.keymap", KeymapFixtures.Read("corne-broken.keymap")),
            ("config/corne.keymap", KeymapFixtures.Read("corne.keymap")),
            ("config/glove80-editor.keymap", KeymapFixtures.Read("glove80-editor.keymap")));
        var fake = SignedInFake();
        fake.OnFetchRepo = (_, _, _) => Task.FromResult(new GitHubCacheResult(folder, "deadbeef", true));
        var vm = Wire(fake, [], []);
        var selections = new List<GitHubOpenSelection?>();
        vm.CloseRequested += selections.Add;

        await vm.InitializeAsync();
        await WaitUntil(() => vm.SelectedBranch == "main");
        await vm.OpenCommand.ExecuteAsync(null);
        return (vm, selections);
    }

    [Fact]
    public async Task Open_SingleKeymap_ClosesWithSelectionImmediately()
    {
        var folder = CacheFolder(("config/corne.keymap", KeymapFixtures.Read("corne.keymap")));
        var fake = SignedInFake();
        fake.OnFetchRepo = (_, _, _) => Task.FromResult(new GitHubCacheResult(folder, "deadbeef", true));
        var vm = Wire(fake, [], []);
        GitHubOpenSelection? selection = null;
        vm.CloseRequested += s => selection = s;

        await vm.InitializeAsync();
        await WaitUntil(() => vm.SelectedBranch == "main");
        await vm.OpenCommand.ExecuteAsync(null);

        Assert.NotNull(selection);
        Assert.Contains(nameof(FakeGitHubKeymapService.FetchRepoAsync), fake.Calls);
        Assert.Equal("github:octocat/zmk-config:main:config/corne.keymap", selection!.Keymap.ToString());
        Assert.Equal(Path.Combine(folder, "config", "corne.keymap"), selection.LocalPath);
        Assert.True(File.Exists(selection.LocalPath));
        Assert.Equal(1, vm.CurrentStep); // never visited the keymap step
        Assert.False(vm.IsFetching);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Open_ThreeKeymaps_LandsOnStepTwoSortedWithDetails()
    {
        var (vm, selections) = await StepTwoWithThreeKeymapsAsync();

        Assert.Equal(2, vm.CurrentStep);
        Assert.True(vm.IsKeymapStep);
        Assert.Empty(selections);
        Assert.Equal(
            ["config/corne-broken.keymap", "config/corne.keymap", "config/glove80-editor.keymap"],
            vm.Keymaps.Select(k => k.RelativePath));

        var broken = vm.Keymaps[0];
        Assert.Equal(T("GitHub_KeymapUnreadable"), broken.Detail);
        Assert.DoesNotContain("[GitHub_", broken.Detail);

        var corne = vm.Keymaps[1];
        Assert.Contains("42", corne.Detail);
        Assert.Equal(ExpectedDetail("corne.keymap"), corne.Detail);

        Assert.Equal(ExpectedDetail("glove80-editor.keymap"), vm.Keymaps[2].Detail);
    }

    [Fact]
    public async Task PickKeymapCommand_SecondItem_ClosesWithIt()
    {
        var (vm, selections) = await StepTwoWithThreeKeymapsAsync();

        var item = vm.Keymaps[1];
        vm.PickKeymapCommand.Execute(item);

        Assert.Single(selections);
        Assert.Equal("config/corne.keymap", selections[0]!.Keymap.Path);
        Assert.Equal(item.RelativePath, selections[0]!.Keymap.Path);
        Assert.Equal(item.LocalPath, selections[0]!.LocalPath);

        vm.PickKeymapCommand.Execute(null); // no-op
        Assert.Single(selections);
    }

    [Fact]
    public async Task BackCommand_FromStepTwo_ReturnsToRepoStepWithEmptyList()
    {
        var (vm, selections) = await StepTwoWithThreeKeymapsAsync();

        vm.BackCommand.Execute(null);

        Assert.Equal(1, vm.CurrentStep);
        Assert.False(vm.IsKeymapStep);
        Assert.Empty(vm.Keymaps);
        Assert.Empty(selections);
    }

    [Fact]
    public async Task Open_NoKeymapInFolder_ErrorsOnRepoStep()
    {
        var folder = CacheFolder(("README.md", "no keymaps here"));
        var fake = SignedInFake();
        fake.OnFetchRepo = (_, _, _) => Task.FromResult(new GitHubCacheResult(folder, "deadbeef", true));
        var vm = Wire(fake, [], []);

        await vm.InitializeAsync();
        await WaitUntil(() => vm.SelectedBranch == "main");
        await vm.OpenCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.CurrentStep);
        Assert.Equal(Loc.Instance.Format("GitHub_NoKeymapFormat", "octocat/zmk-config", "main"), vm.ErrorText);
        Assert.DoesNotContain("[GitHub_", vm.ErrorText);
        Assert.Contains("octocat/zmk-config", vm.ErrorText);
        Assert.Contains("main", vm.ErrorText);
        Assert.Empty(vm.Keymaps);
        Assert.False(vm.IsFetching);
    }

    [Fact]
    public async Task Open_RepoTooLarge_ShowsTooLargeTextAndStopsFetching()
    {
        var fake = SignedInFake();
        fake.OnFetchRepo = (_, _, _) =>
            Task.FromException<GitHubCacheResult>(new GitHubApiException(GitHubErrorKind.TooLarge, "over 50 MB"));
        var vm = Wire(fake, [], []);

        await vm.InitializeAsync();
        await WaitUntil(() => vm.SelectedBranch == "main");
        await vm.OpenCommand.ExecuteAsync(null);

        Assert.Equal(T("GitHub_ErrorTooLarge"), vm.ErrorText);
        Assert.DoesNotContain("[GitHub_", vm.ErrorText);
        Assert.False(vm.IsFetching);
        Assert.Equal(1, vm.CurrentStep);
    }

    [Fact]
    public async Task OpenCommand_CanExecute_TracksBranchAndFetching()
    {
        var fake = SignedInFake();
        fake.OnListBranches = (_, _) => Task.FromResult<IReadOnlyList<string>>([]); // no default selection
        var vm = Wire(fake, [], []);

        await vm.InitializeAsync();
        Assert.NotNull(vm.SelectedRepo);
        Assert.Null(vm.SelectedBranch);
        Assert.False(vm.OpenCommand.CanExecute(null)); // no branch yet

        vm.SelectedBranch = "main";
        Assert.True(vm.OpenCommand.CanExecute(null));

        vm.IsFetching = true;
        Assert.False(vm.OpenCommand.CanExecute(null));
    }
}
