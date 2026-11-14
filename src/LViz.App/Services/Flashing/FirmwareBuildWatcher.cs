using LViz.Core.Diagnostics;
using LViz.Core.GitHub;

namespace LViz.App.Services.Flashing;

/// <summary>
/// What the status bar should say about the build of the commit LViz just
/// saved. <see cref="Waiting"/> means GitHub has not created the workflow run
/// yet; <see cref="Hidden"/> means there is nothing to show (the run never
/// turned up, there is no firmware to flash, the builds App is not installed
/// on the repo, or the watch was cleared).
/// </summary>
public enum FirmwareWatchState
{
    /// <summary>Watched, but GitHub has not created a run for the commit yet.</summary>
    Waiting,

    /// <summary>A run exists and is still going.</summary>
    Running,

    /// <summary>The build succeeded and its firmware is downloadable.</summary>
    Ready,

    /// <summary>The build failed.</summary>
    Failed,

    /// <summary>The builds login is missing; the watcher never starts a sign-in.</summary>
    SignInNeeded,

    /// <summary>Nothing to show.</summary>
    Hidden,
}

/// <summary>
/// One snapshot of the watched build: which config/commit it belongs to (so
/// the status bar can tie it to the loaded config), its current
/// <see cref="FirmwareWatchState"/>, and the run's GitHub page when there is
/// one to point at.
/// </summary>
public sealed record FirmwareWatchStatus(
    string ConfigKey,
    string Owner,
    string Repo,
    string Branch,
    string Sha,
    FirmwareWatchState State,
    string? RunUrl);

/// <summary>
/// Follows the GitHub Actions build of one commit — the one LViz just pushed
/// to a GitHub zmk-config — so the status bar can say "Building…",
/// "Firmware ready: Flash" or "Build failed".
///
/// <para>
/// GitHub creates the workflow run a few seconds after the push, so a watch
/// usually starts in <see cref="FirmwareWatchState.Waiting"/>;
/// <see cref="NoRunGiveUp"/> bounds how long a run that never appears is
/// waited for. Every poll goes through
/// <see cref="IFirmwareBuildService.FindBuildAsync"/> by sha only (branch is
/// part of the status for display, never of the lookup). The watcher only ever
/// <see cref="IFirmwareBuildService.EnsureRestoredAsync"/> the builds login —
/// it never starts a sign-in; without a login it settles at
/// <see cref="FirmwareWatchState.SignInNeeded"/> and stops.
/// </para>
/// </summary>
/// <param name="builds">The builds login and the run/artifact lookups.</param>
/// <param name="delay">
/// How the poll interval is waited; defaults to <see cref="Task.Delay"/>.
/// Tests pass one that returns (almost) instantly.
/// </param>
public sealed class FirmwareBuildWatcher(
    IFirmwareBuildService builds,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    /// <summary>How long to wait between two polls while a run is pending.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a commit without any workflow run is waited for before the
    /// watch gives up (GitHub creates the run a few seconds after the push;
    /// past this it is not coming).
    /// </summary>
    public static readonly TimeSpan NoRunGiveUp = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Consecutive failed polls after which the watch ends as
    /// <see cref="FirmwareWatchState.Hidden"/> instead of retrying.
    /// </summary>
    private const int MaxConsecutiveFailures = 3;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private FirmwareWatchStatus? _current;

    /// <summary>The watched build's state, or null when nothing is being watched.</summary>
    public FirmwareWatchStatus? Current => _current;

    /// <summary>
    /// Raised every time <see cref="Current"/> changes value, on whatever
    /// thread the loop runs on (the subscriber marshals). An unchanged poll
    /// does not raise it.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Watches the build of one commit: cancels any previous loop, sets
    /// <see cref="Current"/> to <see cref="FirmwareWatchState.Waiting"/> for
    /// the new target and starts the polling loop in the background.
    /// </summary>
    public void Watch(string configKey, string owner, string repo, string branch, string sha)
    {
        var target = new FirmwareWatchStatus(
            configKey, owner, repo, branch, sha, FirmwareWatchState.Waiting, null);

        CancellationTokenSource cts;
        lock (_gate)
        {
            _cts?.Cancel();
            cts = new CancellationTokenSource();
            _cts = cts;
            _current = target;
        }

        Changed?.Invoke();
        _ = RunAsync(target, cts.Token);
    }

    /// <summary>Stops watching; <see cref="Current"/> becomes null.</summary>
    public void Clear()
    {
        FirmwareWatchStatus? previous;
        lock (_gate)
        {
            _cts?.Cancel();
            previous = _current;
            _current = null;
        }

        if (previous is not null)
        {
            Changed?.Invoke();
        }
    }

    /// <summary><see cref="Clear"/> unless the watched build is for exactly this config.</summary>
    public void ClearUnless(string configKey)
    {
        if (!string.Equals(Current?.ConfigKey, configKey, StringComparison.Ordinal))
        {
            Clear();
        }
    }

    /// <summary>Stops the loop; leaves <see cref="Current"/> as it is.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
        }
    }

    /// <summary>
    /// The polling loop: restore the login, then poll until the build is done,
    /// the answer says there is nothing to show, the no-run budget runs out,
    /// or <paramref name="token"/> is cancelled — cancellation ends it quietly,
    /// without touching <see cref="Current"/>.
    /// </summary>
    private async Task RunAsync(FirmwareWatchStatus target, CancellationToken token)
    {
        var status = target;
        var waited = TimeSpan.Zero;
        var failures = 0;

        // Value-change gate: the loop re-publishes Running or Waiting polls
        // every interval, and only an actual change may raise Changed.
        void Publish(FirmwareWatchState state, string? runUrl)
        {
            var next = status with { State = state, RunUrl = runUrl };
            if (next == status)
            {
                return;
            }

            status = next;
            lock (_gate)
            {
                // Superseded or cleared: this loop no longer owns Current.
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _current = next;
            }

            Changed?.Invoke();
        }

        try
        {
            try
            {
                await builds.EnsureRestoredAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (GitHubApiException ex)
            {
                // Defensive: the restore itself is a store read; a login that
                // cannot be restored has nothing to poll with.
                DiagnosticLog.Info("Flash", $"Build watch restore failed: {ex.Message}");
                Publish(
                    ex.Kind == GitHubErrorKind.Unauthorized
                        ? FirmwareWatchState.SignInNeeded
                        : FirmwareWatchState.Hidden,
                    null);
                return;
            }

            if (!builds.IsSignedIn)
            {
                // The watcher never starts a sign-in; the flash window does.
                Publish(FirmwareWatchState.SignInNeeded, null);
                return;
            }

            while (true)
            {
                token.ThrowIfCancellationRequested();

                FirmwareBuild? build = null;
                try
                {
                    // Always by sha: this watch is about the commit LViz saved,
                    // not about whatever else the branch has been doing.
                    build = await builds.FindBuildAsync(
                        target.Owner, target.Repo, target.Sha, null, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (GitHubApiException ex) when (ex.Kind == GitHubErrorKind.Unauthorized)
                {
                    DiagnosticLog.Info("Flash", $"Build watch of {target.Sha} lost the login: {ex.Message}");
                    Publish(FirmwareWatchState.SignInNeeded, null);
                    return;
                }
                catch (GitHubApiException ex)
                {
                    failures++;
                    DiagnosticLog.Info(
                        "Flash",
                        $"Build watch of {target.Sha} failed ({failures}/{MaxConsecutiveFailures}): {ex.Message}");
                    if (failures >= MaxConsecutiveFailures)
                    {
                        Publish(FirmwareWatchState.Hidden, null);
                        return;
                    }
                    // Keep the current state and try again at the next poll.
                }

                if (build is not null)
                {
                    failures = 0;
                    switch (build.State)
                    {
                        case FirmwareBuildState.Ready:
                            Publish(FirmwareWatchState.Ready, build.Run?.HtmlUrl);
                            return;
                        case FirmwareBuildState.Failed:
                            Publish(FirmwareWatchState.Failed, build.Run?.HtmlUrl);
                            return;
                        case FirmwareBuildState.Running:
                            Publish(FirmwareWatchState.Running, build.Run?.HtmlUrl);
                            break;
                        case FirmwareBuildState.NoRun:
                            // Still waiting for GitHub to create the run — but
                            // not forever: the summed poll waits bound it.
                            if (waited >= NoRunGiveUp)
                            {
                                Publish(FirmwareWatchState.Hidden, null);
                                return;
                            }
                            break;
                        default:
                            // NoFirmware / NotInstalled: nothing the status bar
                            // could offer for this commit.
                            Publish(FirmwareWatchState.Hidden, null);
                            return;
                    }
                }

                try
                {
                    await _delay(PollInterval, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }

                waited += PollInterval;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Replaced, cleared or disposed: end quietly without touching Current.
        }
        catch (Exception ex)
        {
            // Nothing here is expected (the service contract is runs-or-
            // GitHubApiException); a surprise must not hang the bar on
            // "Building…" forever, so it hides it and logs.
            DiagnosticLog.Error("Flash", $"Build watch of {target.Sha} stopped: {ex}");
            Publish(FirmwareWatchState.Hidden, null);
        }
    }
}
