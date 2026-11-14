using LViz.Core.Diagnostics;
using LViz.Core.Firmware;
using LViz.Core.GitHub;

namespace LViz.App.Services.Flashing;

/// <summary>
/// What <see cref="IFirmwareBuildService.FindBuildAsync"/> found for a commit
/// or a branch. The states mirror what a user can do about it:
/// <see cref="Ready"/> offers a flash, <see cref="Running"/> asks to wait,
/// <see cref="Failed"/> and <see cref="NoFirmware"/> point at the run,
/// <see cref="NotInstalled"/> points at the builds App installation page, and
/// <see cref="NoRun"/> means the workflow never ran.
/// </summary>
public enum FirmwareBuildState
{
    /// <summary>No workflow run for that commit or branch at all.</summary>
    NoRun,

    /// <summary>The newest run for it is still going.</summary>
    Running,

    /// <summary>The newest run finished without succeeding.</summary>
    Failed,

    /// <summary>A succeeded run has a downloadable <c>firmware</c> artifact.</summary>
    Ready,

    /// <summary>Runs succeeded but none has a fresh <c>firmware</c> artifact.</summary>
    NoFirmware,

    /// <summary>The builds App cannot see this repository.</summary>
    NotInstalled,
}

/// <summary>
/// The answer to "which build is there for this commit or branch": the state,
/// the run it was decided by (null for <see cref="FirmwareBuildState.NoRun"/>
/// and <see cref="FirmwareBuildState.NotInstalled"/>), the id of the
/// <c>firmware</c> artifact when it is <see cref="FirmwareBuildState.Ready"/>,
/// and — branch lookups only — whether a run newer than the
/// <see cref="FirmwareBuildState.Ready"/> one is still going.
/// </summary>
public sealed record FirmwareBuild(
    FirmwareBuildState State,
    GitHubWorkflowRun? Run,
    long? ArtifactId,
    bool NewerRunPending);

/// <summary>
/// The outcome of <see cref="IFirmwareBuildService.DownloadAsync"/>: the
/// unpacked <c>.uf2</c> files in the cache, the builds App not installed, the
/// artifact deleted on GitHub, or the message of what went wrong.
/// </summary>
public abstract record FirmwareDownloadResult
{
    /// <summary>The firmware is in the cache at these <c>.uf2</c> paths.</summary>
    public sealed record Done(IReadOnlyList<string> Files) : FirmwareDownloadResult;

    /// <summary>The builds App cannot see this repository.</summary>
    public sealed record NotInstalled : FirmwareDownloadResult;

    /// <summary>GitHub has already deleted the artifact (HTTP 410).</summary>
    public sealed record Expired : FirmwareDownloadResult;

    /// <summary>Downloading or unpacking failed.</summary>
    public sealed record Failed(string Message) : FirmwareDownloadResult;
}

/// <summary>
/// Whether the peripheral half needs its flash, as advice: a compare says
/// <see cref="Needed"/> or <see cref="NotNeeded"/>, and
/// <see cref="Unknown"/> covers "there is no record of a past flash" as well
/// as every failure to decide — the caller leaves the box unticked with a
/// hint either way.
/// </summary>
public enum PeripheralAdvice
{
    /// <summary>The changes since the last flash concern the central only.</summary>
    NotNeeded,

    /// <summary>Something the peripheral builds from changed.</summary>
    Needed,

    /// <summary>No flash record, no main login, or GitHub could not be asked.</summary>
    Unknown,
}

/// <summary>
/// Everything the flash window and the status bar need from GitHub to flash:
/// the second (builds) login, finding the workflow build of a commit or
/// branch, downloading its <c>firmware</c> artifact into the local cache, and
/// advising whether the peripheral half needs flashing too.
///
/// <para>
/// Failure contract: <see cref="FindBuildAsync"/> reports
/// <see cref="FirmwareBuildState.NotInstalled"/> for the answers the builds
/// App gives when it is not installed on the repo and otherwise propagates
/// <see cref="GitHubApiException"/>; <see cref="DownloadAsync"/> reports
/// through <see cref="FirmwareDownloadResult"/>;
/// <see cref="AdvisePeripheralAsync"/> degrades to
/// <see cref="PeripheralAdvice.Unknown"/>. Cancellation always propagates.
/// </para>
/// </summary>
public interface IFirmwareBuildService
{
    /// <summary>The builds login's handle, or null when signed out.</summary>
    string? Login { get; }

    /// <summary>True while a builds login is held (persisted or session-only).</summary>
    bool IsSignedIn { get; }

    /// <summary>True when the builds login would survive a restart.</summary>
    bool IsLoginPersisted { get; }

    /// <summary>Raised after a builds sign-in, sign-out, or a restore that signed in.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Restores the stored builds login at most once — later and concurrent
    /// calls await the same call. A failed attempt is dropped, so the next
    /// call retries.
    /// </summary>
    Task EnsureRestoredAsync(CancellationToken ct = default);

    /// <summary>Opens the device flow for the builds App; show the code to the user.</summary>
    Task<GitHubDeviceCode> StartSignInAsync(CancellationToken ct = default);

    /// <summary>Polls until the user authorizes the code, then adopts the login.</summary>
    Task CompleteSignInAsync(GitHubDeviceCode code, CancellationToken ct = default);

    /// <summary>
    /// Signs out of the builds App and wipes the firmware cache so private
    /// builds do not linger.
    /// </summary>
    Task SignOutAsync(CancellationToken ct = default);

    /// <summary>
    /// Which build exists for <c>owner/repo</c>: pass exactly one of
    /// <paramref name="headSha"/> (the status-bar case: the build of that
    /// commit) or <paramref name="branch"/> (the "Flash keyboard…" case: the
    /// newest green build on the branch, noting a newer run still going).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Neither or both of <paramref name="headSha"/> and
    /// <paramref name="branch"/> are set.
    /// </exception>
    Task<FirmwareBuild> FindBuildAsync(
        string owner, string repo, string? headSha, string? branch, CancellationToken ct = default);

    /// <summary>
    /// The artifact zip for <paramref name="sha"/>, unpacked into the cache:
    /// a cache hit answers without any request, otherwise the zip is
    /// downloaded and stored before it answers.
    /// </summary>
    Task<FirmwareDownloadResult> DownloadAsync(
        string owner, string repo, string sha, long artifactId, CancellationToken ct = default);

    /// <summary>
    /// Whether the peripheral needs flashing: unknown without a
    /// <paramref name="lastFlashedSha"/>, not needed when it already equals
    /// <paramref name="buildSha"/>, else what the compare between the two
    /// says — read with the <i>main</i> login, which has Contents.
    /// </summary>
    Task<PeripheralAdvice> AdvisePeripheralAsync(
        string owner,
        string repo,
        string? lastFlashedSha,
        string buildSha,
        CancellationToken ct = default);
}

/// <summary>
/// The <see cref="IFirmwareBuildService"/> wiring, modelled on
/// <see cref="GitHub.GitHubKeymapService"/>: <see cref="GitHubSession"/> owns
/// the builds login, <see cref="GitHubClient"/> owns HTTP,
/// <see cref="FirmwareCache"/> owns the local copies — this class ties them
/// together, enforces the house rules (restore at most once, fetch the token
/// right before every call and never hold it, wipe the cache on sign-out),
/// and turns raw runs and artifacts into a
/// <see cref="FirmwareBuildState"/>.
///
/// <para>
/// Two logins are in play and they are not interchangeable: workflow runs,
/// artifacts and downloads go through the builds client and session; the
/// compare reads file names, which only the main App was granted (Contents),
/// so it goes through the main client and session. Without the builds App
/// installed, GitHub answers 404 for a private repo and a plain 403 for no
/// Actions access — both mean
/// <see cref="FirmwareBuildState.NotInstalled"/>, everything else propagates.
/// </para>
/// </summary>
/// <param name="buildsClient">GitHub HTTP client of the builds App.</param>
/// <param name="buildsSession">The stored builds login.</param>
/// <param name="mainClient">GitHub HTTP client of the main App.</param>
/// <param name="mainSession">The stored main login (for the compare).</param>
/// <param name="cache">The local firmware copies.</param>
/// <param name="delay">
/// How the device-flow polling waits; defaults to <see cref="Task.Delay"/>.
/// Tests pass one that returns instantly.
/// </param>
public sealed class FirmwareBuildService(
    GitHubClient buildsClient,
    GitHubSession buildsSession,
    GitHubClient mainClient,
    GitHubSession mainSession,
    FirmwareCache cache,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IFirmwareBuildService
{
    /// <summary>
    /// The name of the merged artifact the stock ZMK workflow uploads; the
    /// only artifact this service ever looks at.
    /// </summary>
    private const string ArtifactName = "firmware";

    /// <summary>Refuse an artifact zip of more than 64 MiB.</summary>
    private const long MaxZipBytes = 64 * 1024 * 1024;

    /// <summary>
    /// How many of a branch's newest succeeded runs get their artifacts
    /// checked — a build older than these is not worth flashing.
    /// </summary>
    private const int BranchArtifactScan = 5;

    private readonly Lock _restoreLock = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private Task<bool>? _restoreTask;

    /// <inheritdoc/>
    public string? Login => buildsSession.Login;

    /// <inheritdoc/>
    public bool IsSignedIn => buildsSession.IsSignedIn;

    /// <inheritdoc/>
    public bool IsLoginPersisted => buildsSession.IsPersisted;

    /// <summary>
    /// The builds session's <see cref="GitHubSession.Changed"/> as-is:
    /// subscribers attach to the session itself, so a sign-in, sign-out or a
    /// restore that signed in lands here with the session as sender.
    /// </summary>
    public event EventHandler? Changed
    {
        add => buildsSession.Changed += value;
        remove => buildsSession.Changed -= value;
    }

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

            _restoreTask = shared = buildsSession.RestoreAsync(ct);
            return shared;
        }
    }

    /// <inheritdoc/>
    public Task<GitHubDeviceCode> StartSignInAsync(CancellationToken ct = default) =>
        buildsClient.StartDeviceFlowAsync(ct);

    /// <inheritdoc/>
    public async Task CompleteSignInAsync(GitHubDeviceCode code, CancellationToken ct = default)
    {
        var tokens = await buildsClient.WaitForDeviceAuthorizationAsync(code, _delay, ct);
        await buildsSession.SignInAsync(tokens, ct);
    }

    /// <inheritdoc/>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await buildsSession.SignOutAsync(ct);

        // Private firmware must not outlive the builds login.
        cache.DeleteBuilds();
    }

    /// <inheritdoc/>
    public async Task<FirmwareBuild> FindBuildAsync(
        string owner,
        string repo,
        string? headSha,
        string? branch,
        CancellationToken ct = default)
    {
        var hasSha = !string.IsNullOrWhiteSpace(headSha);
        var hasBranch = !string.IsNullOrWhiteSpace(branch);
        if (hasSha == hasBranch)
        {
            throw new ArgumentException(
                "Exactly one of head sha or branch is required", nameof(headSha));
        }

        await EnsureRestoredAsync(ct);

        // The token is fetched right here (it may refresh) and never held.
        var accessToken = await buildsSession.GetAccessTokenAsync(ct);

        try
        {
            var runs = await buildsClient.ListWorkflowRunsAsync(
                accessToken, owner, repo, headSha, branch, ct);

            // The rules below are stated as "newest"; never trust the API's
            // order for it. OrderByDescending is stable, so runs created at
            // the same second keep the order GitHub reported.
            var newestFirst = runs.OrderByDescending(run => run.CreatedAt).ToList();
            var succeeded = newestFirst.Where(run => run.Succeeded).ToList();

            // A by-sha lookup answers for exactly this commit, so every
            // succeeded run counts; a branch may sit on an older green build,
            // and the scan stops after the newest few.
            var candidates = hasSha ? succeeded : succeeded.Take(BranchArtifactScan);
            foreach (var run in candidates)
            {
                var artifacts = await buildsClient.ListRunArtifactsAsync(
                    accessToken, owner, repo, run.Id, ct);
                var firmware = artifacts.FirstOrDefault(artifact =>
                    string.Equals(artifact.Name, ArtifactName, StringComparison.Ordinal)
                    && !artifact.Expired);
                if (firmware is not null)
                {
                    // Only the branch flow promises "the newest green build",
                    // so only it notes that something newer is still going.
                    var pending = hasBranch
                        && newestFirst.Any(
                            newer => !newer.IsCompleted && newer.CreatedAt > run.CreatedAt);
                    return new FirmwareBuild(FirmwareBuildState.Ready, run, firmware.Id, pending);
                }
            }

            var running = newestFirst.FirstOrDefault(run => !run.IsCompleted);
            if (running is not null)
            {
                return new FirmwareBuild(FirmwareBuildState.Running, running, null, false);
            }

            var failed = newestFirst.FirstOrDefault(run => run.IsCompleted && !run.Succeeded);
            if (failed is not null)
            {
                return new FirmwareBuild(FirmwareBuildState.Failed, failed, null, false);
            }

            if (succeeded.Count > 0)
            {
                return new FirmwareBuild(FirmwareBuildState.NoFirmware, succeeded[0], null, false);
            }

            return new FirmwareBuild(FirmwareBuildState.NoRun, null, null, false);
        }
        catch (GitHubApiException ex)
            when (ex.Kind == GitHubErrorKind.NotFound || ex.StatusCode == 403)
        {
            // Without the builds App installed GitHub answers 404 (a private
            // repo is invisible to it) or a plain 403 (no Actions access).
            return new FirmwareBuild(FirmwareBuildState.NotInstalled, null, null, false);
        }
    }

    /// <inheritdoc/>
    public async Task<FirmwareDownloadResult> DownloadAsync(
        string owner,
        string repo,
        string sha,
        long artifactId,
        CancellationToken ct = default)
    {
        // A warm cache needs neither a login nor a request.
        var cached = cache.TryGet(owner, repo, sha);
        if (cached is not null)
        {
            return new FirmwareDownloadResult.Done(cached);
        }

        try
        {
            await EnsureRestoredAsync(ct);

            // Fetched right here (it may refresh) and never held.
            var accessToken = await buildsSession.GetAccessTokenAsync(ct);

            await using var zip = new MemoryStream();
            await buildsClient.DownloadArtifactZipAsync(
                accessToken, owner, repo, artifactId, zip, MaxZipBytes, ct);

            zip.Position = 0;
            return new FirmwareDownloadResult.Done(cache.Store(owner, repo, sha, zip));
        }
        catch (GitHubApiException ex)
            when (ex.Kind == GitHubErrorKind.NotFound || ex.StatusCode == 403)
        {
            return new FirmwareDownloadResult.NotInstalled();
        }
        catch (GitHubApiException ex) when (ex.StatusCode == 410)
        {
            // GitHub already deleted the artifact — rebuild instead.
            return new FirmwareDownloadResult.Expired();
        }
        catch (GitHubApiException ex)
        {
            return new FirmwareDownloadResult.Failed(ex.Message);
        }
        catch (InvalidDataException ex)
        {
            // The zip was not a firmware artifact; its message says why.
            return new FirmwareDownloadResult.Failed(ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task<PeripheralAdvice> AdvisePeripheralAsync(
        string owner,
        string repo,
        string? lastFlashedSha,
        string buildSha,
        CancellationToken ct = default)
    {
        // "No record of a flash" has nothing to compare against; the caller
        // leaves the box unticked with a hint.
        if (string.IsNullOrEmpty(lastFlashedSha))
        {
            return PeripheralAdvice.Unknown;
        }

        if (string.Equals(lastFlashedSha, buildSha, StringComparison.OrdinalIgnoreCase))
        {
            return PeripheralAdvice.NotNeeded;
        }

        try
        {
            // The compare lists file names, which only the main App was
            // granted (Contents); RestoreAsync is free while already signed
            // in. A main login that is absent cannot decide anything.
            if (!await mainSession.RestoreAsync(ct))
            {
                return PeripheralAdvice.Unknown;
            }

            var changes = await mainClient.CompareCommitsAsync(
                await mainSession.GetAccessTokenAsync(ct), owner, repo, lastFlashedSha, buildSha, ct);

            return FirmwareChangeRule.PeripheralNeedsFlash(changes)
                ? PeripheralAdvice.Needed
                : PeripheralAdvice.NotNeeded;
        }
        catch (GitHubApiException ex)
        {
            // Advice must never block a flash; whatever made the compare
            // fail, the user decides without it.
            DiagnosticLog.Info("Flash", $"Peripheral advice for {owner}/{repo} failed: {ex.Message}");
            return PeripheralAdvice.Unknown;
        }
    }
}
