using LViz.App.Services.Flashing;
using LViz.Core.GitHub;
using Xunit;

namespace LViz.Tests.Flashing;

/// <summary>
/// <see cref="FirmwareBuildWatcher"/> — the loop that follows the build of the
/// commit LViz just pushed, for the status bar. Runs against a fake
/// <see cref="IFirmwareBuildService"/> whose <c>FindBuildAsync</c> answers come
/// from a responder function (a queue that repeats its last answer, so a
/// watcher bug loops on the same answer instead of dying on an empty queue),
/// and a delay that records the requested interval and completes after 1 ms.
/// Tests await loop progress by spinning on <c>Current</c>/<c>Changed</c> with
/// a 2 s timeout; nothing real is waited for.
/// </summary>
public sealed class FirmwareBuildWatcherTests : IDisposable
{
    private const string ConfigKey = "github:ovandongen/zmk-config-cosmos82:main:keymap/corne.keymap";
    private const string OtherConfigKey = "github:ovandongen/other-config:main:keymap/corne.keymap";
    private const string Owner = "ovandongen";
    private const string Repo = "zmk-config-cosmos82";
    private const string Branch = "main";
    private const string Sha = "aa11aa22aa33aa44aa55aa66aa77aa88aa99aa00";
    private const string OtherSha = "bb11bb22bb33bb44bb55bb66bb77bb88bb99bb00";
    private const string RunUrl = "https://github.com/ovandongen/zmk-config-cosmos82/actions/runs/101";
    private const string OtherRunUrl = "https://github.com/ovandongen/zmk-config-cosmos82/actions/runs/202";

    private readonly FakeBuilds _builds = new();
    private readonly List<TimeSpan> _delays = new();
    private readonly List<FirmwareWatchStatus?> _log = new();
    private readonly FirmwareBuildWatcher _watcher;

    public FirmwareBuildWatcherTests()
    {
        _watcher = new FirmwareBuildWatcher(_builds, RecordDelayAsync);
        _watcher.Changed += () =>
        {
            lock (_log)
            {
                _log.Add(_watcher.Current);
            }
        };
    }

    public void Dispose() => _watcher.Dispose();

    // ---- the happy paths ----

    [Fact]
    public async Task Watch_NoRunThenRunningThenReady_ClimbsToReadyAndStopsPolling()
    {
        _builds.Responder = Answers(NoRun(), Running(), Ready());

        _watcher.Watch(ConfigKey, Owner, Repo, Branch, Sha);

        // No run yet: Waiting, on the target Watch was called for.
        Assert.Equal(FirmwareWatchState.Waiting, _watcher.Current!.State);
        Assert.Equal(Sha, _watcher.Current.Sha);

        await WaitUntilAsync(
            () => _watcher.Current?.State == FirmwareWatchState.Ready, "Ready");

        Assert.Equal(RunUrl, _watcher.Current!.RunUrl);
        Assert.Equal(ConfigKey, _watcher.Current.ConfigKey);
        Assert.Equal(Owner, _watcher.Current.Owner);
        Assert.Equal(Repo, _watcher.Current.Repo);
        Assert.Equal(Branch, _watcher.Current.Branch);
        Assert.Equal(
            new[]
            {
                FirmwareWatchState.Waiting,
                FirmwareWatchState.Running,
                FirmwareWatchState.Ready,
            },
            States(Snapshot()));

        // Three polls, two waits of the poll interval between them.
        Assert.Equal(3, _builds.FindCalls);
        Assert.Equal(2, DelayCount);
        Assert.All(Delays(), d => Assert.Equal(FirmwareBuildWatcher.PollInterval, d));

        // A finished build is final: no further polls, no further Changed.
        await SettleAsync();
        Assert.Equal(3, _builds.FindCalls);
        Assert.Equal(3, ChangeCount);
    }

    [Fact]
    public async Task Watch_RunningThenFailed_EndsFailedWithRunUrl()
    {
        _builds.Responder = Answers(Running(), Failed(OtherRunUrl));

        _watcher.Watch(ConfigKey, Owner, Repo, Branch, Sha);
        await WaitUntilAsync(
            () => _watcher.Current?.State == FirmwareWatchState.Failed, "Failed");

        Assert.Equal(OtherRunUrl, _watcher.Current!.RunUrl);
        Assert.Equal(
            new[] { FirmwareWatchState.Waiting, FirmwareWatchState.Running, FirmwareWatchState.Failed },
            States(Snapshot()));
        Assert.Equal(2, _builds.FindCalls);
    }

    [Fact]
    public async Task Watch_TwoRunningAnswersInRow_RaisesChangedOnceForRunning()
    {
        _builds.Responder = Answers(Running(), Running(), Ready());

        _watcher.Watch(ConfigKey, Owner, Repo, Branch, Sha);
        await WaitUntilAsync(
            () => _watcher.Current?.State == FirmwareWatchState.Ready, "Ready");

        // The second Running poll changed nothing, so it raised no Changed.
        Assert.Equal(
            new[]
            {
                FirmwareWatchState.Waiting,
                FirmwareWatchState.Running,
                FirmwareWatchState.Ready,
            },
            States(Snapshot()));
        Assert.Equal(3, _builds.FindCalls);
    }

    // ---- the sign-in gate ----

    [Fact]
    public async Task Watch_NotSignedIn_EndsSignInNeededWithoutAnyFind()
    {
        _builds.SignedIn = false;
        _builds.Responder = Answers(Ready());

        _watcher.Watch(ConfigKey, Owner, Repo, Branch, Sha);
        await WaitUntilAsync(
            () => _watcher.Current?.State == FirmwareWatchState.SignInNeeded, "SignInNeeded");

        // The watcher only ever restores the login; it never asks, and it
        // never looks for a build it cannot authorise.
        Assert.Equal(1, _builds.RestoreCalls);
        Assert.Equal(0, _builds.FindCalls);
        Assert.Null(_watcher.Current!.RunUrl);
    }

    [Fact]
    public async Task Unauthorized_EndsSignInNeeded()
    {
        _builds.Responder = Answers(ApiError(GitHubErrorKind.Unauthorized));

        _watcher.Watch(ConfigKey, Owner, Repo, Branch, Sha);
        await WaitUntilAsync(
            () => _watcher.Current?.State == FirmwareWatchState.SignInNeeded, "SignInNeeded");

        Assert.Equal(1, _builds.FindCalls);
        Assert.Equal(0, DelayCount);
    }

    // ---- the give-up paths ----

    [Fact]
    public async Task Watch_NoRunForever_HiddenAfterTheGiveUpBudget()
    {
        _builds.Responder = Answers(NoRun());

        _watcher.Watch(ConfigKey, Owner, Repo, Branch, Sha);
        await WaitUntilAsync(
            () => _watcher.Current?.State == FirmwareWatchState.Hidden, "Hidden");

        // NoRunGiveUp / PollInterval waits, then the poll after them gives up.
        Assert.Equal(8, FirmwareBuildWatcher.NoRunGiveUp.Ticks / FirmwareBuildWatcher.PollInterval.Ticks);
        Assert.Equal(8, DelayCount);
        Assert.Equal(9, _builds.FindCalls);
        Assert.Equal(
            new[] { FirmwareWatchState.Waiting, FirmwareWatchState.Hidden },
            States(Snapshot()));
    }

    [Fact]
    public async Task Watch_NotInstalled_IsHiddenAtOnce()
    {
        _builds.Responder = Answers(new FirmwareBuild(FirmwareBuildState.NotInstalled, null, null, false));

        _watcher.Watch(ConfigKey, Owner, Repo, Branch, Sha);
        await WaitUntilAsync(
            () => _watcher.Current?.State == FirmwareWatchState.Hidden, "Hidden");

        Assert.Equal(1, _builds.FindCalls);
        Assert.Equal(0, DelayCount);
    }

    [Fact]
    public async Task Watch_NoFirmware_IsHiddenAtOnce()
    {
        _builds.Responder = Answers(
            new FirmwareBuild(FirmwareBuildState.NoFirmware, Run(101, RunUrl), null, false));

        _watcher.Watch(ConfigKey, Owner, Repo, Branch, Sha);
        await WaitUntilAsync(
            () => _watcher.Current?.State == FirmwareWatchState.Hidden, "Hidden");

        Assert.Equal(1, _builds.FindCalls);
    }

    [Fact]
    public async Task NetworkTwiceThenReady_EndsReady()
    {
        _builds.Responder = Answers(
            ApiError(GitHubErrorKind.Network), ApiError(GitHubErrorKind.Network), Ready());

        _watcher.Watch(ConfigKey, Owner, Repo, Branch, Sha);
        await WaitUntilAsync(
            () => _watcher.Current?.State == FirmwareWatchState.Ready, "Ready");

        // Two failed polls each retried at the next interval; the state never
        // changed, so only Waiting and Ready were raised.
        Assert.Equal(3, _builds.FindCalls);
        Assert.Equal(2, DelayCount);
        Assert.Equal(
            new[] { FirmwareWatchState.Waiting, FirmwareWatchState.Ready },
            States(Snapshot()));
    }

    [Fact]
    public async Task NetworkThreeTimesInRow_EndsHidden()
    {
        _builds.Responder = Answers(ApiError(GitHubErrorKind.Network));

        _watcher.Watch(ConfigKey, Owner, Repo, Branch, Sha);
        await WaitUntilAsync(
            () => _watcher.Current?.State == FirmwareWatchState.Hidden, "Hidden");

        Assert.Equal(3, _builds.FindCalls);
        Assert.Equal(2, DelayCount);
    }

    // ---- replacing a running watch ----

    [Fact]
    public async Task Watch_ASecondWatch_StopsTheFirstLoopAndWatchesTheNewSha()
    {
        // The first commit's build grinds forever; the second one is instantly ready.
        _builds.Responder = sha => sha == Sha ? Running() : Ready(OtherRunUrl);

        _watcher.Watch(ConfigKey, Owner, Repo, Branch, Sha);
        await WaitUntilAsync(
            () => _watcher.Current?.State == FirmwareWatchState.Running, "first Running");

        _watcher.Watch(ConfigKey, Owner, Repo, Branch, OtherSha);

        await WaitUntilAsync(
            () => _watcher.Current is { State: FirmwareWatchState.Ready, Sha: OtherSha },
            "Ready for the second sha");
        Assert.Equal(OtherRunUrl, _watcher.Current!.RunUrl);
        Assert.Contains(Snapshot(), e => e is { State: FirmwareWatchState.Waiting, Sha: OtherSha });

        // The first loop stopped: its poll count no longer grows.
        await SettleAsync();
        var firstShaCalls = _builds.CallsFor(Sha);
        await SettleAsync();
        Assert.Equal(firstShaCalls, _builds.CallsFor(Sha));
    }

    // ---- Clear / ClearUnless ----

    [Fact]
    public async Task Clear_SetsCurrentNullAndRaisesChangedOnce()
    {
        _builds.Responder = Answers(Running());

        _watcher.Watch(ConfigKey, Owner, Repo, Branch, Sha);
        await WaitUntilAsync(
            () => _watcher.Current?.State == FirmwareWatchState.Running, "Running");
        Assert.Equal(2, ChangeCount);

        _watcher.Clear();

        Assert.Null(_watcher.Current);
        Assert.Equal(3, ChangeCount);
        Assert.Null(Snapshot()[2]);

        // Clearing again changes nothing and stays quiet.
        _watcher.Clear();
        await SettleAsync();
        Assert.Equal(3, ChangeCount);
    }

    [Fact]
    public async Task ClearUnless_SameConfigKey_KeepsWatching()
    {
        _builds.Responder = Answers(Running());

        _watcher.Watch(ConfigKey, Owner, Repo, Branch, Sha);
        await WaitUntilAsync(
            () => _watcher.Current?.State == FirmwareWatchState.Running, "Running");

        _watcher.ClearUnless(ConfigKey);

        Assert.NotNull(_watcher.Current);
        var delaysBefore = DelayCount;
        await SettleAsync();
        // Not cleared: the loop keeps polling, and no Changed was raised.
        Assert.True(DelayCount > delaysBefore, "the loop should still be polling");
        Assert.Equal(2, ChangeCount);
    }

    [Fact]
    public async Task ClearUnless_OtherConfigKey_Clears()
    {
        _builds.Responder = Answers(Running());

        _watcher.Watch(ConfigKey, Owner, Repo, Branch, Sha);
        await WaitUntilAsync(
            () => _watcher.Current?.State == FirmwareWatchState.Running, "Running");

        _watcher.ClearUnless(OtherConfigKey);

        Assert.Null(_watcher.Current);
        Assert.Equal(3, ChangeCount);
    }

    // ---- helpers ----

    private Task RecordDelayAsync(TimeSpan interval, CancellationToken ct)
    {
        lock (_delays)
        {
            _delays.Add(interval);
        }

        // 1 ms, not the real interval; it still throws on cancellation so a
        // replaced or cleared loop stops at its next wait like Task.Delay does.
        return Task.Delay(1, ct);
    }

    private int DelayCount
    {
        get
        {
            lock (_delays)
            {
                return _delays.Count;
            }
        }
    }

    private TimeSpan[] Delays()
    {
        lock (_delays)
        {
            return _delays.ToArray();
        }
    }

    private int ChangeCount
    {
        get
        {
            lock (_log)
            {
                return _log.Count;
            }
        }
    }

    private FirmwareWatchStatus?[] Snapshot()
    {
        lock (_log)
        {
            return _log.ToArray();
        }
    }

    private static FirmwareWatchState[] States(FirmwareWatchStatus?[] entries) =>
        [.. entries.Select(e => e!.State)];

    /// <summary>
    /// A responder that serves <paramref name="answers"/> in order and keeps
    /// serving its last answer afterwards — so "NoRun forever" and
    /// "Network throws forever" need no padding, and a watcher that polls more
    /// than the states allow is caught by the explicit call-count asserts.
    /// An <see cref="GitHubApiException"/> entry is thrown instead of returned.
    /// </summary>
    private static Func<string, FirmwareBuild> Answers(params object[] answers)
    {
        var queue = new Queue<object>(answers);
        var last = answers[^1];
        return _ =>
        {
            var answer = queue.Count > 0 ? queue.Dequeue() : last;
            if (answer is GitHubApiException ex)
            {
                throw ex;
            }

            return (FirmwareBuild)answer;
        };
    }

    private static GitHubApiException ApiError(GitHubErrorKind kind) =>
        new(kind, $"GitHub GET /repos failed: {kind}");

    private static FirmwareBuild NoRun() => new(FirmwareBuildState.NoRun, null, null, false);

    private static FirmwareBuild Running(string url = RunUrl) =>
        new(FirmwareBuildState.Running, Run(101, url), null, false);

    private static FirmwareBuild Ready(string url = RunUrl) =>
        new(FirmwareBuildState.Ready, Run(101, url), 4001, false);

    private static FirmwareBuild Failed(string url = RunUrl) =>
        new(FirmwareBuildState.Failed, Run(101, url), null, false);

    private static GitHubWorkflowRun Run(long id, string url) =>
        new(
            id,
            "Build firmware",
            Branch,
            Sha,
            "completed",
            "success",
            url,
            new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero),
            null);

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.True(condition(), $"timed out waiting for {because}");
    }

    private static Task SettleAsync() => Task.Delay(50);

    /// <summary>
    /// The build lookups the watcher must make, answered from a settable
    /// responder by sha; sign-in state is settable and every other service
    /// member is out of the watcher's world and refuses. Every
    /// <c>FindBuildAsync</c> call pins the lookup contract: by the saved sha,
    /// never by branch.
    /// </summary>
    private sealed class FakeBuilds : IFirmwareBuildService
    {
        private readonly List<string> _shas = new();
        private int _restoreCalls;
        private int _findCalls;

        public Func<string, FirmwareBuild> Responder { get; set; } = _ => NoRun();

        public bool SignedIn { get; set; } = true;

        public int RestoreCalls => _restoreCalls;

        public int FindCalls => _findCalls;

        public int CallsFor(string sha)
        {
            lock (_shas)
            {
                return _shas.Count(s => string.Equals(s, sha, StringComparison.Ordinal));
            }
        }

        public string? Login => SignedIn ? "octocat" : null;

        public bool IsSignedIn => SignedIn;

        public bool IsLoginPersisted => false;

        // The watcher never subscribes to the service's login changes; the
        // fake has nothing to raise this for.
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067

        public Task EnsureRestoredAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _restoreCalls);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<FirmwareBuild> FindBuildAsync(
            string owner,
            string repo,
            string? headSha,
            string? branch,
            CancellationToken ct = default)
        {
            // The watcher follows exactly the commit it saved.
            Assert.Null(branch);
            Assert.False(string.IsNullOrEmpty(headSha));

            ct.ThrowIfCancellationRequested();
            lock (_shas)
            {
                _shas.Add(headSha!);
            }

            Interlocked.Increment(ref _findCalls);
            return Task.FromResult(Responder(headSha!));
        }

        public Task<GitHubDeviceCode> StartSignInAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CompleteSignInAsync(GitHubDeviceCode code, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SignOutAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<FirmwareDownloadResult> DownloadAsync(
            string owner,
            string repo,
            string sha,
            long artifactId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PeripheralAdvice> AdvisePeripheralAsync(
            string owner,
            string repo,
            string? lastFlashedSha,
            string buildSha,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
