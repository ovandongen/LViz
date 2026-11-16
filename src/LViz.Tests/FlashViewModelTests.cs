using System.Buffers.Binary;
using System.Text;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.Flashing;
using LViz.App.ViewModels;
using LViz.Core.Firmware;
using LViz.Core.GitHub;
using LViz.Core.ZmkConfig;
using LViz.Tests.ZmkConfig;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The flash window's first two steps over the real cosmos82 fixture with a
/// faked build service: the source step's builds sign-in (code, hooks,
/// cancel), how each <see cref="FirmwareBuildState"/> reads, the by-SHA vs
/// by-branch lookup, and the halves step — download matching, the central's
/// tick, and the peripheral advice from history plus compare. File mode's
/// own behaviour is the next card's.
/// </summary>
public sealed class FlashViewModelTests : IDisposable
{
    private const string Owner = "maker";
    private const string Repo = "cosmos-config";
    private const string Branch = "main";
    private const string Sha = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0";
    private const string ConfigKey = "github:maker/cosmos-config:main";

    private const string CentralFile = "cosmos82_right-nice_nano_v2-zmk.uf2";
    private const string PeripheralFile = "cosmos82_left-nice_nano_v2-zmk.uf2";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"lviz-flashvm-{Guid.NewGuid():N}");
    private readonly FakeBuilds _builds = new();
    private readonly FirmwareFlashHistory _history;
    private readonly FakeDesktopInteraction _desktop = new();

    public FlashViewModelTests()
    {
        Directory.CreateDirectory(_tempDir);
        _history = new FirmwareFlashHistory(Path.Combine(_tempDir, "flash-history.json"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
        }
    }

    private static string T(string key) => Loc.Instance[key];

    // --- the sign-in ---

    [Fact]
    public async Task Start_SignedOut_AskstoSignInWithoutLookingUp()
    {
        _builds.SignedIn = false;
        var vm = GitHubVm();

        await vm.StartAsync();

        Assert.True(vm.IsSignInNeeded);
        Assert.Null(vm.BuildText);
        Assert.Empty(_builds.FindCalls);
        Assert.True(_builds.Restores >= 1);
        Assert.False(vm.IsBusy);
        Assert.True(vm.IsSourceStep);
    }

    [Fact]
    public async Task SignIn_StartCommand_CopiesCodeOpensPageThenLoadstheBuild()
    {
        _builds.SignedIn = false;
        _builds.Build = ReadyBuild(pending: false);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _builds.OnCompleteSignIn = _ => gate.Task;
        var vm = GitHubVm();
        await vm.StartAsync();

        var running = vm.SignIn.StartCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.SignIn.IsWaitingForSignIn);

        Assert.Equal("WFDS-MQNM", vm.SignIn.UserCode);
        Assert.Equal(["WFDS-MQNM"], _desktop.CopiedTexts); // auto-copied once
        Assert.Equal(["https://github.com/login/device"], _desktop.OpenedUrls);

        gate.SetResult();
        await running;

        Assert.False(vm.SignIn.IsWaitingForSignIn);
        Assert.Null(vm.SignIn.UserCode);
        Assert.False(vm.IsSignInNeeded);
        Assert.Equal(T("Flash_SignedInAs").Replace("{0}", "build-user"), vm.LoginText);
        Assert.Contains(Sha[..7], vm.BuildText);
        Assert.Equal(1, _builds.CompleteCalls);
    }

    [Fact]
    public async Task SignIn_CancelCommand_StopsTheWaitAndKeepsTheStep()
    {
        _builds.SignedIn = false;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _builds.OnCompleteSignIn = ct =>
        {
            ct.Register(() => gate.TrySetCanceled(ct));
            return gate.Task;
        };
        var vm = GitHubVm();
        await vm.StartAsync();

        var running = vm.SignIn.StartCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.SignIn.IsWaitingForSignIn);

        vm.SignIn.CancelCommand.Execute(null);
        await running;

        Assert.False(vm.SignIn.IsWaitingForSignIn);
        Assert.Null(vm.SignIn.UserCode);
        Assert.True(vm.IsSignInNeeded);
        Assert.Empty(_builds.FindCalls);
    }

    [Fact]
    public async Task CancelWork_StopsAPendingSignIn()
    {
        // The window closing mid-wait: CancelWork aborts the sign-in the way
        // the sign-in's own Cancel button does — no build lookup follows.
        _builds.SignedIn = false;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _builds.OnCompleteSignIn = ct =>
        {
            ct.Register(() => gate.TrySetCanceled(ct));
            return gate.Task;
        };
        var vm = GitHubVm();
        await vm.StartAsync();

        var running = vm.SignIn.StartCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.SignIn.IsWaitingForSignIn);

        vm.CancelWork();
        await running;

        Assert.False(vm.SignIn.IsWaitingForSignIn);
        Assert.Null(vm.SignIn.UserCode);
        Assert.True(vm.IsSignInNeeded);
        Assert.Empty(_builds.FindCalls);
    }

    [Fact]
    public async Task SignOutCommand_ClearstheBuildAndAsksAgain()
    {
        _builds.Build = ReadyBuild(pending: false);
        var vm = GitHubVm();
        await vm.StartAsync();
        Assert.NotNull(vm.BuildText);

        await vm.SignOutCommand.ExecuteAsync(null);

        Assert.Equal(1, _builds.SignOuts);
        Assert.Null(vm.LoginText);
        Assert.True(vm.IsSignInNeeded);
        Assert.Null(vm.BuildText);
        Assert.False(vm.ShowInstall);
    }

    // --- looking the build up ---

    [Fact]
    public async Task Start_ReadyByBranch_AskstheBranchAndShowstheSummary()
    {
        _builds.Build = ReadyBuild(pending: true);
        var vm = GitHubVm();

        await vm.StartAsync();

        Assert.Equal(new List<(string?, string?)> { (null, Branch) }, _builds.FindCalls);
        Assert.Contains(Sha[..7], vm.BuildText);
        Assert.Contains("Tune cosmos82 keymap", vm.BuildText); // first line only
        Assert.DoesNotContain("body", vm.BuildText);
        Assert.Equal(T("Flash_NewerRunPending"), vm.NewerRunNote);
        Assert.True(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task Start_WithSha_AsksthatShaNottheBranch()
    {
        _builds.Build = ReadyBuild(pending: false);
        var vm = GitHubVm(sha: Sha);

        await vm.StartAsync();

        Assert.Equal(new List<(string?, string?)> { (Sha, null) }, _builds.FindCalls);
        Assert.Null(vm.NewerRunNote);
    }

    [Fact]
    public async Task Start_NotInstalled_OffersInstallAndOpensItsPage()
    {
        _builds.Build = new FirmwareBuild(FirmwareBuildState.NotInstalled, null, null, false);
        var vm = GitHubVm();

        await vm.StartAsync();

        Assert.Equal(T("Flash_BuildNotInstalled"), vm.BuildText);
        Assert.True(vm.ShowInstall);
        Assert.False(vm.ShowRefresh);
        Assert.False(vm.NextCommand.CanExecute(null));

        vm.InstallCommand.Execute(null);
        Assert.Equal([GitHubBuildsAppInfo.InstallUrl], _desktop.OpenedUrls);
    }

    [Fact]
    public async Task Start_BuildStates_ReadEachLine()
    {
        var run = Run("completed", "failure");
        var cases = new (FirmwareBuildState State, string Key, bool Refresh, bool OpenRun)[]
        {
            (FirmwareBuildState.Running, "Flash_BuildRunning", true, true),
            (FirmwareBuildState.Failed, "Flash_BuildFailed", false, true),
            (FirmwareBuildState.NoFirmware, "Flash_BuildNoFirmware", false, false),
            (FirmwareBuildState.NoRun, "Flash_BuildNoRun", false, false),
        };

        foreach (var (state, key, refresh, openRun) in cases)
        {
            _builds.Build = new FirmwareBuild(state, run, null, false);
            var vm = GitHubVm();
            await vm.StartAsync();

            Assert.Equal(T(key), vm.BuildText);
            Assert.Equal(refresh, vm.ShowRefresh);
            Assert.Equal(openRun, vm.ShowOpenRun);
            Assert.False(vm.NextCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task Start_FindThrowsApiError_ShowsTranslatedText()
    {
        _builds.FindError = new GitHubApiException(GitHubErrorKind.Network, "no route to host");
        var vm = GitHubVm();

        await vm.StartAsync();

        Assert.Equal(T("GitHub_ErrorNetwork"), vm.ErrorMessage);
        Assert.Null(vm.BuildText);
    }

    [Fact]
    public async Task Start_FindThrowsOtherError_ShowsItInsteadOfAnEmptyStep()
    {
        // StartAsync runs fire-and-forget from the shell: an escape would be lost.
        _builds.FindError = new InvalidOperationException("reply did not parse");
        var vm = GitHubVm();

        await vm.StartAsync();

        Assert.Equal(T("GitHub_ErrorUnexpectedFormat").Replace("{0}", "reply did not parse"), vm.ErrorMessage);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.BuildText);
    }

    [Fact]
    public async Task Start_TokenRejected_GoesToTheSignInPanel()
    {
        // The builds token was valid when restored but GitHub refuses it now
        // (revoked, App uninstalled): the way out is signing in again.
        _builds.FindError = new GitHubApiException(GitHubErrorKind.Unauthorized, "bad credentials");
        var vm = GitHubVm();

        await vm.StartAsync();

        Assert.True(vm.IsSignInNeeded);
        Assert.Null(vm.LoginText);
        Assert.Null(vm.BuildText);
        Assert.Equal(T("GitHub_SignInAgain"), vm.ErrorMessage);
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task BusyText_NamesTheLookupThenTheDownload()
    {
        _builds.Build = ReadyBuild(pending: false);
        _builds.Download = new FirmwareDownloadResult.Done([Uf2(CentralFile), Uf2(PeripheralFile)]);
        var vm = GitHubVm();
        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlashViewModel.BusyText))
            {
                seen.Add(vm.BusyText);
            }
        };

        await vm.StartAsync();
        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal([T("Flash_LookingUp"), null, T("Flash_Downloading"), null], seen);
    }

    // --- source → halves ---

    [Fact]
    public async Task Next_DownloadDone_BuildsRowsWithCentralTicked()
    {
        _builds.Build = ReadyBuild(pending: false);
        _builds.Download = new FirmwareDownloadResult.Done([Uf2(CentralFile), Uf2(PeripheralFile)]);
        var vm = GitHubVm();
        await vm.StartAsync();
        Assert.True(vm.NextCommand.CanExecute(null));

        await vm.NextCommand.ExecuteAsync(null);

        Assert.True(vm.IsHalvesStep);
        Assert.Equal(2, vm.HalfRows.Count);
        var central = vm.HalfRows[0];
        Assert.Equal(FirmwareHalfRole.Central, central.Half.Role);
        Assert.Equal(T("Flash_HalfWithCentral").Replace("{0}", T("Flash_HalfRight")), central.Label);
        Assert.True(central.IsChecked);
        Assert.Null(central.Hint);
        var peripheral = vm.HalfRows[1];
        Assert.Equal(T("Flash_HalfLeft"), peripheral.Label);
        Assert.False(peripheral.IsChecked);
        Assert.Equal(T("Flash_HintPeripheralUnknown"), peripheral.Hint);
        // Without a flash record the advice is asked with no last SHA.
        Assert.Null(_builds.AdviceLastSha);
        Assert.Equal(Sha, _builds.AdviceBuildSha);
        Assert.True(vm.NextCommand.CanExecute(null)); // the central is ticked
        Assert.True(vm.BackCommand.CanExecute(null));
    }

    [Fact]
    public async Task Next_HistoryAndAdviceNeeded_TicksThePeripheral()
    {
        _history.Record(ConfigKey, FirmwareHalfRole.Peripheral, "abc");
        _builds.Build = ReadyBuild(pending: false);
        _builds.Download = new FirmwareDownloadResult.Done([Uf2(CentralFile), Uf2(PeripheralFile)]);
        _builds.Advice = PeripheralAdvice.Needed;
        var vm = GitHubVm();
        await vm.StartAsync();

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal("abc", _builds.AdviceLastSha);
        Assert.True(vm.HalfRows[1].IsChecked);
        Assert.Equal(T("Flash_HintPeripheralNeeded"), vm.HalfRows[1].Hint);
    }

    [Fact]
    public async Task Next_AdviceNotNeeded_LeavesThePeripheralUnticked_WithItsHint()
    {
        _history.Record(ConfigKey, FirmwareHalfRole.Peripheral, Sha);
        _builds.Build = ReadyBuild(pending: false);
        _builds.Download = new FirmwareDownloadResult.Done([Uf2(CentralFile), Uf2(PeripheralFile)]);
        _builds.Advice = PeripheralAdvice.NotNeeded;
        var vm = GitHubVm();
        await vm.StartAsync();

        await vm.NextCommand.ExecuteAsync(null);

        Assert.False(vm.HalfRows[1].IsChecked);
        Assert.Equal(T("Flash_HintPeripheralNotNeeded"), vm.HalfRows[1].Hint);
    }

    [Fact]
    public async Task Next_MissingPeripheralFile_NamesItAndStaysOnSource()
    {
        _builds.Build = ReadyBuild(pending: false);
        _builds.Download = new FirmwareDownloadResult.Done([Uf2(CentralFile), Uf2("extra.uf2")]);
        var vm = GitHubVm();
        await vm.StartAsync();

        await vm.NextCommand.ExecuteAsync(null);

        Assert.True(vm.IsSourceStep);
        Assert.Contains(PeripheralFile, vm.ErrorMessage);
        Assert.Contains(CentralFile, vm.ErrorMessage);
        Assert.Contains("extra.uf2", vm.ErrorMessage);
    }

    [Fact]
    public async Task Next_ExpiredArtifact_ShowstheExpiredError()
    {
        _builds.Build = ReadyBuild(pending: false);
        _builds.Download = new FirmwareDownloadResult.Expired();
        var vm = GitHubVm();
        await vm.StartAsync();

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(T("Flash_ErrorExpired"), vm.ErrorMessage);
        Assert.True(vm.IsSourceStep);
    }

    [Fact]
    public async Task Next_DownloadFailed_ShowsItsMessage()
    {
        _builds.Build = ReadyBuild(pending: false);
        _builds.Download = new FirmwareDownloadResult.Failed("zip was truncated");
        var vm = GitHubVm();
        await vm.StartAsync();

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(T("Flash_ErrorDownload").Replace("{0}", "zip was truncated"), vm.ErrorMessage);
        Assert.True(vm.IsSourceStep);
    }

    // --- the halves step ---

    [Fact]
    public async Task Halves_Next_IsonlyEnabledWithSomethingTicked()
    {
        _builds.Build = ReadyBuild(pending: false);
        _builds.Download = new FirmwareDownloadResult.Done([Uf2(CentralFile), Uf2(PeripheralFile)]);
        var vm = GitHubVm();
        await vm.StartAsync();
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.IsHalvesStep);

        vm.HalfRows[0].IsChecked = false;
        Assert.False(vm.NextCommand.CanExecute(null));

        vm.HalfRows[0].IsChecked = true;
        Assert.True(vm.NextCommand.CanExecute(null));
        vm.NextCommand.Execute(null);
        Assert.True(vm.IsFlashStep);
    }

    [Fact]
    public async Task Halves_Back_ReturnstoSource_WhereNextStillWorks()
    {
        _builds.Build = ReadyBuild(pending: false);
        _builds.Download = new FirmwareDownloadResult.Done([Uf2(CentralFile), Uf2(PeripheralFile)]);
        var vm = GitHubVm();
        await vm.StartAsync();
        await vm.NextCommand.ExecuteAsync(null);

        vm.BackCommand.Execute(null);

        Assert.True(vm.IsSourceStep);
        Assert.Contains(Sha[..7], vm.BuildText);
        Assert.True(vm.NextCommand.CanExecute(null));
    }

    // --- mode switch ---

    [Fact]
    public async Task LocalContext_OpensStraightInFileMode()
    {
        var vm = LocalVm();

        await vm.StartAsync();

        Assert.True(vm.IsFileMode);
        Assert.False(vm.CanUseBuild);
        Assert.False(vm.UseBuildCommand.CanExecute(null));
        Assert.False(vm.NextCommand.CanExecute(null)); // file mode's Next is the next card's
        Assert.Equal(0, _builds.Restores); // nothing to restore without a GitHub target — the fake counts them
    }

    [Fact]
    public async Task UseFileCommand_SwitchesModeOfftheBuild_AndBack()
    {
        _builds.Build = ReadyBuild(pending: false);
        var vm = GitHubVm();
        await vm.StartAsync();

        vm.UseFileCommand.Execute(null);
        Assert.True(vm.IsFileMode);
        Assert.False(vm.NextCommand.CanExecute(null)); // a ready build is not offered in file mode

        vm.UseBuildCommand.Execute(null);
        Assert.False(vm.IsFileMode);
        Assert.True(vm.NextCommand.CanExecute(null));
    }

    // --- labels ---

    [Fact]
    public void HalfLabel_UnibodyIstheKeyboard_AndUnknownSideGetsRoleNames()
    {
        var slabwing = FirmwareHalves.For(ZmkConfigInspector.Inspect(ZmkConfigFixtures.Root("slabwing")));
        Assert.Single(slabwing);
        Assert.Equal(T("Flash_HalfKeyboard"), FlashViewModel.HalfLabel(slabwing[0], unibody: true));

        var centralUnknown = new FirmwareHalf(
            FirmwareHalfRole.Central, 0, "nice_nano_v2", [], FirmwareSide.Unknown, "x-zmk.uf2");
        var peripheralUnknown = new FirmwareHalf(
            FirmwareHalfRole.Peripheral, 1, "nice_nano_v2", [], FirmwareSide.Unknown, "y-zmk.uf2");
        Assert.Equal(T("Flash_HalfCentral"), FlashViewModel.HalfLabel(centralUnknown, unibody: false));
        Assert.Equal(T("Flash_HalfOther"), FlashViewModel.HalfLabel(peripheralUnknown, unibody: false));
    }

    // --- fixtures and fakes ---

    /// <summary>One run as the API reports it; the commit message's first line feeds the build text.</summary>
    private static GitHubWorkflowRun Run(string status = "completed", string? conclusion = "success") =>
        new(42, "Build firmware", Branch, Sha, status, conclusion,
            "https://github.com/maker/cosmos-config/actions/runs/42",
            new DateTimeOffset(2026, 11, 11, 10, 30, 0, TimeSpan.Zero),
            "Tune cosmos82 keymap\n\nbody text that must not show");

    private static FirmwareBuild ReadyBuild(bool pending) =>
        new(FirmwareBuildState.Ready, Run(), 7, pending);

    /// <summary>One valid 512-byte UF2 block carrying the ASCII payload (as in FirmwareBuildServiceTests).</summary>
    private static byte[] Uf2Block(string payload)
    {
        var bytes = Encoding.ASCII.GetBytes(payload);
        var block = new byte[Uf2File.BlockSize];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0), Uf2File.Magic0);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), Uf2File.Magic1);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(24), 1); // block count
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(28), (uint)bytes.Length);
        bytes.CopyTo(block.AsSpan(32));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(508), Uf2File.MagicEnd);
        return block;
    }

    /// <summary>A temp <c>.uf2</c> under a flashable name; its path is what DownloadAsync would hand back.</summary>
    private string Uf2(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, Uf2Block("ZMK"));
        return path;
    }

    private FlashViewModel GitHubVm(string? sha = null) => MakeVm(new FlashGitHubTarget(Owner, Repo, Branch), sha);

    private FlashViewModel LocalVm() => MakeVm(null, null);

    private FlashViewModel MakeVm(FlashGitHubTarget? target, string? sha)
    {
        var keyboard = new FakeKeyboard();
        var context = new FlashContext(
            target is null ? ZmkConfigFixtures.Root("cosmos82") : ConfigKey,
            ZmkConfigFixtures.Root("cosmos82"),
            target,
            sha);
        return new FlashViewModel(
            context, _builds, _history, keyboard,
            new FlashRun(keyboard, new Uf2DriveWatcher(new NoDrives())), _desktop);
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

    private sealed class NoDrives : IUf2DriveSource
    {
        public IReadOnlyList<Uf2Drive> List() => [];
    }

    private sealed class FakeKeyboard : IKeyboardFlashAccess
    {
        public bool IsConnected => false;

        public KeyboardBus Bus => KeyboardBus.None;

        public int? BootloaderPeripheralSlots => null;

        public Task<ControlResult> EnterBootloaderAsync(byte targetHalf, CancellationToken ct = default) =>
            Task.FromResult(new ControlResult(ControlOutcome.Failed, "keyboard not connected"));

        public Task<string?> QueryConfigIdAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>Scripted answers plus a call log; only this card's steps are exercised.</summary>
    private sealed class FakeBuilds : IFirmwareBuildService
    {
        public bool SignedIn = true;

        public string? LoginName = "build-user";

        public FirmwareBuild Build = new(FirmwareBuildState.NoRun, null, null, false);

        public Exception? FindError;

        public FirmwareDownloadResult Download = new FirmwareDownloadResult.Done([]);

        public PeripheralAdvice Advice = PeripheralAdvice.Unknown;

        public Func<CancellationToken, Task> OnCompleteSignIn = _ => Task.CompletedTask;

        public int Restores;

        public int CompleteCalls;

        public int SignOuts;

        public readonly List<(string? Sha, string? Branch)> FindCalls = [];

        public readonly List<string> DownloadCalls = [];

        public int AdviceCalls;

        public string? AdviceLastSha;

        public string? AdviceBuildSha;

        public string? Login => LoginName;

        public bool IsSignedIn => SignedIn;

        public bool IsLoginPersisted => true;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task EnsureRestoredAsync(CancellationToken ct = default)
        {
            Restores++;
            return Task.CompletedTask;
        }

        public Task<GitHubDeviceCode> StartSignInAsync(CancellationToken ct = default) =>
            Task.FromResult(new GitHubDeviceCode(
                "dev-code", "WFDS-MQNM", "https://github.com/login/device",
                TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(1)));

        public Task CompleteSignInAsync(GitHubDeviceCode code, CancellationToken ct = default)
        {
            CompleteCalls++;
            SignedIn = true;
            return OnCompleteSignIn(ct);
        }

        public Task SignOutAsync(CancellationToken ct = default)
        {
            SignOuts++;
            SignedIn = false;
            return Task.CompletedTask;
        }

        public Task<FirmwareBuild> FindBuildAsync(
            string owner, string repo, string? headSha, string? branch, CancellationToken ct = default)
        {
            FindCalls.Add((headSha, branch));
            if (FindError is not null)
            {
                throw FindError;
            }

            return Task.FromResult(Build);
        }

        public Task<FirmwareDownloadResult> DownloadAsync(
            string owner, string repo, string sha, long artifactId, CancellationToken ct = default)
        {
            DownloadCalls.Add($"{owner}/{repo}@{sha.Substring(0, 7)}#{artifactId}");
            return Task.FromResult(Download);
        }

        public Task<PeripheralAdvice> AdvisePeripheralAsync(
            string owner, string repo, string? lastFlashedSha, string buildSha, CancellationToken ct = default)
        {
            AdviceCalls++;
            AdviceLastSha = lastFlashedSha;
            AdviceBuildSha = buildSha;
            return Task.FromResult(Advice);
        }
    }
}
