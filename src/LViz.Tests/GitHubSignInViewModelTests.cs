using LViz.App.Services.GitHub;
using LViz.App.ViewModels;
using LViz.Core.GitHub;
using Xunit;

namespace LViz.Tests;

/// <summary>
/// The shared device-flow view-model on its own: while waiting the code and
/// URL are on show and reached the desktop fake, an approval clears them and
/// runs the host's next step exactly once, failures hand
/// <see cref="GitHubErrorText"/> text to the host's error line, and a Cancel
/// only ever stops the wait it was meant for. The host hooks are recorded as
/// appended error strings and a sign-in counter.
/// </summary>
public sealed class GitHubSignInViewModelTests
{
    private const string Code = "ABCD-1234";
    private const string VerifyUrl = "https://github.com/login/device";

    private readonly List<string?> _errors = [];
    private int _signedInCalls;

    private GitHubSignInViewModel Wire(FakeGitHubLogin login, FakeDesktopInteraction desktop) =>
        new(login, desktop, error => _errors.Add(error), SignedIn);

    private Task SignedIn()
    {
        _signedInCalls++;
        return Task.CompletedTask;
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

    [Fact]
    public async Task Start_ShowsTheCodeCopiesItAndOpensThePage()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = new FakeGitHubLogin { OnComplete = _ => gate.Task };
        var desktop = new FakeDesktopInteraction();
        var vm = Wire(login, desktop);

        var running = vm.StartCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.IsWaitingForSignIn);

        Assert.Equal(Code, vm.UserCode);
        Assert.Equal(VerifyUrl, vm.VerificationUri);
        Assert.Equal([Code], desktop.CopiedTexts);
        Assert.Equal([VerifyUrl], desktop.OpenedUrls);
        Assert.Equal([null], _errors); // so far only the old-error clear

        gate.SetResult();
        await running;
    }

    [Fact]
    public async Task Start_Approved_ClearsTheCodeAndCallsOnSignedInOnce()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = new FakeGitHubLogin { OnComplete = _ => gate.Task };
        var vm = Wire(login, new FakeDesktopInteraction());

        var running = vm.StartCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.IsWaitingForSignIn);

        gate.SetResult();
        await running;

        Assert.False(vm.IsWaitingForSignIn);
        Assert.Null(vm.UserCode);
        Assert.Equal(1, _signedInCalls);
        Assert.Equal(1, login.CompleteCalls);
    }

    [Fact]
    public async Task Start_StartFails_ShowsTheErrorTextAndNeverWaits()
    {
        var failure = new GitHubApiException(GitHubErrorKind.RateLimited, "slow down");
        var login = new FakeGitHubLogin { OnStart = () => Task.FromException<GitHubDeviceCode>(failure) };
        var desktop = new FakeDesktopInteraction();
        var vm = Wire(login, desktop);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.IsWaitingForSignIn);
        Assert.Null(vm.UserCode);
        Assert.Equal([null, GitHubErrorText.For(failure)], _errors);
        Assert.DoesNotContain("[GitHub_", _errors[1]!); // the resx entry really resolves
        Assert.Empty(desktop.CopiedTexts);
        Assert.Empty(desktop.OpenedUrls);
        Assert.Equal(0, login.CompleteCalls);
        Assert.Equal(0, _signedInCalls);
    }

    [Fact]
    public async Task Start_CompleteFails_StopsWaitingClearsTheCodeAndShowsTheError()
    {
        var failure = new GitHubApiException(GitHubErrorKind.DeviceCodeExpired, "expired");
        var login = new FakeGitHubLogin { OnComplete = _ => Task.FromException(failure) };
        var vm = Wire(login, new FakeDesktopInteraction());

        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.IsWaitingForSignIn);
        Assert.Null(vm.UserCode);
        Assert.Equal([null, GitHubErrorText.For(failure)], _errors);
        Assert.Equal(0, _signedInCalls);
    }

    [Fact]
    public async Task Cancel_WhileTheCodeIsFetched_NeitherCopiesNorOpensNorWaits()
    {
        var codeGate = new TaskCompletionSource<GitHubDeviceCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = new FakeGitHubLogin { OnStart = () => codeGate.Task };
        var desktop = new FakeDesktopInteraction();
        var vm = Wire(login, desktop);

        var running = vm.StartCommand.ExecuteAsync(null);
        await WaitUntil(() => login.StartCalls == 1);
        vm.Cancel();
        codeGate.SetResult(new GitHubDeviceCode(
            "device-secret", Code, VerifyUrl, TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5)));
        await running;

        Assert.False(vm.IsWaitingForSignIn);
        Assert.Null(vm.UserCode);
        Assert.Empty(desktop.CopiedTexts);
        Assert.Empty(desktop.OpenedUrls);
        Assert.Equal(0, login.CompleteCalls);
        Assert.Equal(0, _signedInCalls);
    }

    [Fact]
    public async Task Start_ClearsAnOldErrorFirst()
    {
        var log = new List<string>();
        var login = new FakeGitHubLogin(log)
        {
            OnStart = () => Task.FromException<GitHubDeviceCode>(
                new GitHubApiException(GitHubErrorKind.NotFound, "gone")),
        };
        var vm = new GitHubSignInViewModel(
            login,
            new FakeDesktopInteraction(),
            error => log.Add(error is null ? "cleared" : "error"),
            () =>
            {
                log.Add("signed-in");
                return Task.CompletedTask;
            });

        await vm.StartCommand.ExecuteAsync(null);

        // The error line is cleared before the flow touches anything else.
        Assert.Equal(["cleared", "start", "error"], log);
    }

    [Fact]
    public async Task Cancel_WhileWaiting_StopsTheWaitAndDoesNotCallOnSignedIn()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = new FakeGitHubLogin { OnComplete = _ => gate.Task };
        var vm = Wire(login, new FakeDesktopInteraction());
        var running = vm.StartCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.IsWaitingForSignIn);

        vm.CancelCommand.Execute(null);

        Assert.False(vm.IsWaitingForSignIn);
        Assert.Null(vm.UserCode);
        await running;
        Assert.False(gate.Task.IsCompleted); // nobody approved anything
        Assert.Equal(0, _signedInCalls);
    }

    [Fact]
    public void Cancel_WithNothingRunning_DoesNothing()
    {
        var vm = Wire(new FakeGitHubLogin(), new FakeDesktopInteraction());

        vm.CancelCommand.Execute(null);
        vm.CancelCommand.Execute(null);

        Assert.False(vm.IsWaitingForSignIn);
        Assert.Null(vm.UserCode);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task Start_AfterCancel_RunsAFreshFlow()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var login = new FakeGitHubLogin { OnComplete = _ => gate.Task };
        var desktop = new FakeDesktopInteraction();
        var vm = Wire(login, desktop);

        var first = vm.StartCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.IsWaitingForSignIn);
        vm.CancelCommand.Execute(null);
        await first;

        login.OnComplete = null; // the second attempt is approved at once
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(1, _signedInCalls);
        Assert.False(vm.IsWaitingForSignIn);
        Assert.Null(vm.UserCode);
        Assert.Equal(2, login.StartCalls);
        Assert.Equal(2, desktop.CopiedTexts.Count);
    }

    [Fact]
    public async Task CopyCode_And_OpenVerificationPage_WithNoCode_DoNothing()
    {
        var desktop = new FakeDesktopInteraction();
        var vm = Wire(new FakeGitHubLogin(), desktop);

        await vm.CopyCodeCommand.ExecuteAsync(null);
        vm.OpenVerificationPageCommand.Execute(null);

        Assert.Empty(desktop.CopiedTexts);
        Assert.Empty(desktop.OpenedUrls);
    }

    /// <summary>
    /// A scripted <see cref="IGitHubLogin"/>: <see cref="OnStart"/> answers
    /// the code request (a real-shaped code by default),
    /// <see cref="OnComplete"/> answers the wait and receives its token —
    /// <see cref="CompleteSignInAsync"/> watches that token the way the real
    /// service does, so a Cancel ends the wait. A shared <c>log</c> records
    /// call order for the ordering test.
    /// </summary>
    private sealed class FakeGitHubLogin(List<string>? log = null) : IGitHubLogin
    {
        /// <summary>Answer for StartSignInAsync; null returns the default code.</summary>
        public Func<Task<GitHubDeviceCode>>? OnStart { get; set; }

        /// <summary>Answer for CompleteSignInAsync; null approves immediately.</summary>
        public Func<CancellationToken, Task>? OnComplete { get; set; }

        public int StartCalls { get; private set; }
        public int CompleteCalls { get; private set; }

        public string? Login => null;
        public bool IsSignedIn { get; set; }
        public bool IsLoginPersisted { get; set; } = true;

        public Task EnsureRestoredAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<GitHubDeviceCode> StartSignInAsync(CancellationToken ct = default)
        {
            StartCalls++;
            log?.Add("start");
            return OnStart?.Invoke() ?? Task.FromResult(
                new GitHubDeviceCode(
                    "device-secret", Code, VerifyUrl, TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5)));
        }

        public async Task CompleteSignInAsync(GitHubDeviceCode code, CancellationToken ct = default)
        {
            CompleteCalls++;
            log?.Add("complete");
            if (OnComplete is { } on)
            {
                // WaitAsync mirrors the real service: the wait ends with its token.
                await on(ct).WaitAsync(ct);
            }
        }

        public Task SignOutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
