using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.Flashing;
using LViz.App.Services.GitHub;
using LViz.Core.Firmware;
using LViz.Core.GitHub;
using LViz.Core.ZmkConfig;

namespace LViz.App.ViewModels;

/// <summary>The GitHub zmk-config whose build gets flashed.</summary>
public sealed record FlashGitHubTarget(string Owner, string Repo, string Branch);

/// <summary>
/// What to flash: the history key of this config, the folder to inspect (the
/// local root, or the GitHub branch cache folder), the GitHub target — null
/// for a local config, which then opens straight in file mode — and the
/// commit whose build to flash (the status-bar case; null means "the newest
/// green build on the branch").
/// </summary>
public sealed record FlashContext(
    string ConfigKey,
    string ConfigRoot,
    FlashGitHubTarget? GitHub,
    string? Sha);

/// <summary>The flash window's steps.</summary>
public enum FlashStep
{
    Source,
    Halves,
    Flash,
    Done,
}

/// <summary>
/// One half on the Halves step: its tick state, the label beside the
/// checkbox and an optional hint under it explaining the pre-selected state.
/// </summary>
public sealed partial class FlashHalfRow : ObservableObject
{
    public FlashHalfRow(FirmwareHalf half, string label, bool isChecked, string? hint)
    {
        Half = half;
        _isChecked = isChecked;
        Label = label;
        Hint = hint;
    }

    /// <summary>The half this row stands for.</summary>
    public FirmwareHalf Half { get; }

    /// <summary>The label beside the checkbox, from <see cref="FlashViewModel.HalfLabel"/>.</summary>
    public string Label { get; }

    /// <summary>Why the tick is pre-set as it is; null when nothing to explain.</summary>
    public string? Hint { get; }

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>
/// View model behind the flash window. The source step shows the GitHub
/// build to flash — signing in to the builds App through the device flow
/// when needed — and the halves step pre-selects which halves go: the
/// central always, the peripheral only when the compare since its last flash
/// says so. The shell calls <see cref="StartAsync"/> and closes the window
/// on <see cref="CloseRequested"/>.
///
/// <para>
/// File mode: FlashViewModel.File.cs; Flash and Done steps:
/// FlashViewModel.Run.cs — those partials hold the picked file, the run
/// progress and <see cref="Flashed"/>.
/// </para>
/// </summary>
public sealed partial class FlashViewModel : ObservableObject
{
    private readonly FlashContext _context;
    private readonly IFirmwareBuildService _builds;
    private readonly FirmwareFlashHistory _history;
    private readonly IKeyboardFlashAccess _keyboard;
    private readonly FlashRun _run;
    private readonly IDesktopInteraction _desktop;

    // What the Flash and Done steps (FlashViewModel.Run.cs) work with: the
    // inspected config, its halves, and the ticked plan of bytes to flash.
    private readonly List<(FirmwareHalf Half, byte[] Bytes)> _plan = [];
    private string? _planSha;
    private IReadOnlyList<FirmwareHalf> _halves = [];
    private bool _isUnibody;
    private ZmkConfigFacts? _facts;

    // The GitHub build the source step settled on, and the downloaded file
    // matching each half's expected name.
    private FirmwareBuild? _build;
    private readonly Dictionary<FirmwareHalf, string> _downloadedFiles = [];

    /// <param name="context">What to flash: local or GitHub config, optional commit.</param>
    /// <param name="builds">The builds login, build lookup, download and advice.</param>
    /// <param name="history">Last-flash records backing the peripheral advice.</param>
    /// <param name="keyboard">The connected board; the Flash step drives it.</param>
    /// <param name="run">Flashes one half; the Flash step calls it.</param>
    /// <param name="desktop">Browser, clipboard and <c>.uf2</c> file dialog.</param>
    public FlashViewModel(
        FlashContext context,
        IFirmwareBuildService builds,
        FirmwareFlashHistory history,
        IKeyboardFlashAccess keyboard,
        FlashRun run,
        IDesktopInteraction desktop)
    {
        _context = context;
        _builds = builds;
        _history = history;
        _keyboard = keyboard;
        _run = run;
        _desktop = desktop;
        SignIn = new GitHubSignInViewModel(builds, desktop, text => ErrorMessage = text, OnSignedInAsync);
    }

    /// <summary>Raised when the user closes the window; carries <see cref="Flashed"/>.</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>True once a flash succeeded; the close event carries it. Set by the Run partial.</summary>
    public bool Flashed { get; private set; }

    // --- steps ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceStep))]
    [NotifyPropertyChangedFor(nameof(IsHalvesStep))]
    [NotifyPropertyChangedFor(nameof(IsFlashStep))]
    [NotifyPropertyChangedFor(nameof(IsDoneStep))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    private FlashStep _step;

    public bool IsSourceStep => Step == FlashStep.Source;
    public bool IsHalvesStep => Step == FlashStep.Halves;
    public bool IsFlashStep => Step == FlashStep.Flash;
    public bool IsDoneStep => Step == FlashStep.Done;

    partial void OnStepChanged(FlashStep value)
    {
        if (value == FlashStep.Flash)
        {
            StartFlashRun(); // FlashViewModel.Run.cs: queue the halves, show the first
        }
    }

    /// <summary>True while a restore, lookup, download or plan read is in flight.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _isBusy;

    /// <summary>
    /// What the busy work is, under the source step: the build lookup (which
    /// includes restoring the login) or the download; null when idle.
    /// </summary>
    [ObservableProperty]
    private string? _busyText;

    /// <summary>What went wrong; red text in the window's bottom bar.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>File mode — a local <c>.uf2</c> instead of a GitHub build.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _isFileMode;

    /// <summary>A local config has no build to fall back to, so "Use the GitHub build" stays off.</summary>
    public bool CanUseBuild => _context.GitHub is not null;

    // --- source step ---

    /// <summary>True while the builds sign-in is needed before a build can be looked up.</summary>
    [ObservableProperty]
    private bool _isSignInNeeded;

    /// <summary>"Builds: signed in as &lt;login&gt;"; null when signed out.</summary>
    [ObservableProperty]
    private string? _loginText;

    /// <summary>One line saying which build will be flashed, or why there is none.</summary>
    [ObservableProperty]
    private string? _buildText;

    /// <summary>The "a newer build is still running" note beside a ready build; else null.</summary>
    [ObservableProperty]
    private string? _newerRunNote;

    /// <summary>Visibility of the "Check again" button (a run in flight).</summary>
    [ObservableProperty]
    private bool _showRefresh;

    /// <summary>Visibility of the "Open the build on GitHub" button (a run to look at).</summary>
    [ObservableProperty]
    private bool _showOpenRun;

    /// <summary>Visibility of the "Install on GitHub" button (the builds App can't see the repo).</summary>
    [ObservableProperty]
    private bool _showInstall;

    // --- halves step ---

    /// <summary>The halves to pick, central first, with their pre-ticked state.</summary>
    public ObservableCollection<FlashHalfRow> HalfRows { get; } = [];

    /// <summary>
    /// Reads the config and fills the source step: a config with no
    /// flashable halves stops with an error; a local config opens in file
    /// mode; a GitHub config restores the builds login and looks up the
    /// build, or asks for the builds sign-in first.
    /// </summary>
    public async Task StartAsync()
    {
        ErrorMessage = null;
        IsBusy = true;
        BusyText = Loc.Instance["Flash_LookingUp"];
        try
        {
            ZmkConfigFacts facts;
            try
            {
                facts = ZmkConfigInspector.Inspect(_context.ConfigRoot);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ErrorMessage = Loc.Instance.Format("Flash_ErrorRead", e.Message);
                return;
            }

            _facts = facts;
            _halves = FirmwareHalves.For(facts);
            _isUnibody = _halves.Count == 1;
            if (_halves.Count == 0)
            {
                ErrorMessage = Loc.Instance["Flash_ErrorNoHalves"];
                return;
            }

            IsFileMode = _context.GitHub is null;
            if (IsFileMode)
            {
                return; // the file-mode step is FlashViewModel.File.cs
            }

            await _builds.EnsureRestoredAsync();
            UpdateLoginText();
            if (_builds.IsSignedIn)
            {
                await LoadBuildAsync();
            }
            else
            {
                IsSignInNeeded = true;
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    private void UpdateLoginText() =>
        LoginText = _builds.IsSignedIn && _builds.Login is { } login
            ? Loc.Instance.Format("Flash_SignedInAs", login)
            : null;

    // --- builds sign-in (the shared GitHubSignInViewModel, through the builds App) ---

    /// <summary>
    /// The device flow of the builds App: code, clipboard, browser and wait;
    /// its error text lands in <see cref="ErrorMessage"/> and a finished
    /// sign-in continues here in <see cref="OnSignedInAsync"/>.
    /// </summary>
    public GitHubSignInViewModel SignIn { get; }

    /// <summary>
    /// What a finished sign-in does: leave the sign-in panel, refresh the
    /// login line and look up the build.
    /// </summary>
    private async Task OnSignedInAsync()
    {
        IsSignInNeeded = false;
        UpdateLoginText();
        await LoadBuildAsync();
    }

    /// <summary>Signs out (the service also wipes the firmware cache) and asks to sign in again.</summary>
    [RelayCommand]
    private async Task SignOut()
    {
        await _builds.SignOutAsync();
        LoginText = null;
        IsSignInNeeded = true;
        ClearBuild();
    }

    private void ClearBuild()
    {
        _build = null;
        BuildText = null;
        NewerRunNote = null;
        ShowRefresh = false;
        ShowOpenRun = false;
        ShowInstall = false;
    }

    // --- the build ---

    /// <summary>
    /// Ask GitHub which build there is: the commit's own when the context
    /// carries a SHA, else the branch's newest green build. A GitHub answer
    /// that fails lands in <see cref="ErrorMessage"/> as translated text.
    /// </summary>
    private async Task LoadBuildAsync()
    {
        if (_context.GitHub is not { } target)
        {
            return; // a local config has no build
        }

        ErrorMessage = null;
        IsBusy = true;
        BusyText = Loc.Instance["Flash_LookingUp"];
        try
        {
            _build = await _builds.FindBuildAsync(
                target.Owner,
                target.Repo,
                _context.Sha,
                _context.Sha is null ? target.Branch : null);
            ApplyBuild();
        }
        catch (GitHubApiException e) when (e.Kind == GitHubErrorKind.Unauthorized)
        {
            // GitHub rejected the builds token (revoked, or the App was
            // uninstalled): the way out is signing in again, so show the
            // sign-in panel with the reason above it.
            LoginText = null;
            ClearBuild();
            IsSignInNeeded = true;
            ErrorMessage = GitHubErrorText.For(e);
        }
        catch (GitHubApiException e)
        {
            ErrorMessage = GitHubErrorText.For(e);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            NextCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Turns the looked-up build into the one build line and the buttons that fit its state.</summary>
    private void ApplyBuild()
    {
        var build = _build!;
        NewerRunNote = build.NewerRunPending ? Loc.Instance["Flash_NewerRunPending"] : null;
        BuildText = build.State switch
        {
            FirmwareBuildState.Ready => Loc.Instance.Format(
                "Flash_BuildReady",
                build.Run!.HeadSha[..7],
                FirstLine(build.Run.CommitMessage),
                build.Run.CreatedAt.ToLocalTime().ToString("g")),
            FirmwareBuildState.Running => Loc.Instance["Flash_BuildRunning"],
            FirmwareBuildState.Failed => Loc.Instance["Flash_BuildFailed"],
            FirmwareBuildState.NoFirmware => Loc.Instance["Flash_BuildNoFirmware"],
            FirmwareBuildState.NoRun => Loc.Instance["Flash_BuildNoRun"],
            _ => Loc.Instance["Flash_BuildNotInstalled"],
        };
        ShowRefresh = build.State == FirmwareBuildState.Running;
        ShowOpenRun = build.State is FirmwareBuildState.Running or FirmwareBuildState.Failed;
        ShowInstall = build.State == FirmwareBuildState.NotInstalled;
    }

    private static string FirstLine(string? message) =>
        (message ?? string.Empty).Split('\n')[0].TrimEnd('\r');

    /// <summary>Re-runs the lookup — the "Check again" button behind a running build.</summary>
    [RelayCommand]
    private Task RefreshBuild() => LoadBuildAsync();

    /// <summary>Re-runs the lookup after installing the builds App or fixing what failed.</summary>
    [RelayCommand]
    private Task Retry() => LoadBuildAsync();

    /// <summary>Opens the workflow run the build line is about.</summary>
    [RelayCommand]
    private void OpenRun()
    {
        if (_build?.Run is { HtmlUrl: { Length: > 0 } url })
        {
            _desktop.OpenUrl(url);
        }
    }

    /// <summary>Opens the builds App's installation page — the fix for "can't see this repository".</summary>
    [RelayCommand]
    private void Install() => _desktop.OpenUrl(GitHubBuildsAppInfo.InstallUrl);

    // --- mode switch ---

    /// <summary>Switch to flashing a local <c>.uf2</c> (file mode itself is FlashViewModel.File.cs).</summary>
    [RelayCommand]
    private void UseFile() => IsFileMode = true;

    /// <summary>Switch back to the GitHub build; a local config never got here.</summary>
    [RelayCommand(CanExecute = nameof(CanUseBuild))]
    private void UseBuild() => IsFileMode = false;

    partial void OnIsFileModeChanged(bool value)
    {
        if (value)
        {
            PopulateFileHalves(); // FlashViewModel.File.cs
        }

        NextCommand.NotifyCanExecuteChanged();
    }

    // --- next / back / close ---

    private bool CanNext() => !IsBusy && (IsFileMode
        // File mode (FlashViewModel.File.cs): only on the source step, with
        // a file and half picked and the central's warning ticked if shown.
        ? Step == FlashStep.Source && CanNextFile()
        : Step switch
        {
            FlashStep.Source => _build?.State == FirmwareBuildState.Ready,
            FlashStep.Halves => HalfRows.Any(r => r.IsChecked),
            _ => false,
        });

    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task Next()
    {
        if (IsFileMode)
        {
            NextFile(); // FlashViewModel.File.cs
            return;
        }

        switch (Step)
        {
            case FlashStep.Source:
                await PrepareHalvesAsync();
                break;
            case FlashStep.Halves:
                BuildPlan();
                break;
        }
    }

    private bool CanBack() => Step == FlashStep.Halves;

    /// <summary>Back to the source step; the looked-up build stays, so Next redoes only the download.</summary>
    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back()
    {
        if (Step == FlashStep.Halves)
        {
            Step = FlashStep.Source;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(Flashed);

    /// <summary>
    /// The window is closing: stop a running flash and a pending sign-in
    /// wait. A flash left running would keep polling for a bootloader drive
    /// with no window open and copy its file onto the next one that appears.
    /// </summary>
    public void CancelWork()
    {
        _runCts?.Cancel();
        SignIn.Cancel();
    }

    /// <summary>
    /// Download the ready build, match every half's expected file name
    /// against what arrived, and build the ticked-half rows; every failure
    /// lands in <see cref="ErrorMessage"/> and stays on the source step.
    /// </summary>
    private async Task PrepareHalvesAsync()
    {
        var target = _context.GitHub!;
        if (_build is not { State: FirmwareBuildState.Ready, Run: { } run, ArtifactId: { } artifactId })
        {
            return;
        }

        ErrorMessage = null;
        IsBusy = true;
        BusyText = Loc.Instance["Flash_Downloading"];
        try
        {
            var download = await _builds.DownloadAsync(target.Owner, target.Repo, run.HeadSha, artifactId);
            IReadOnlyList<string> files;
            switch (download)
            {
                case FirmwareDownloadResult.Done(var done):
                    files = done;
                    break;
                case FirmwareDownloadResult.NotInstalled:
                    ShowInstall = true;
                    ErrorMessage = Loc.Instance["Flash_BuildNotInstalled"];
                    return;
                case FirmwareDownloadResult.Expired:
                    ErrorMessage = Loc.Instance["Flash_ErrorExpired"];
                    return;
                case FirmwareDownloadResult.Failed(var message):
                    ErrorMessage = Loc.Instance.Format("Flash_ErrorDownload", message);
                    return;
                default:
                    return;
            }

            _downloadedFiles.Clear();
            foreach (var half in _halves)
            {
                var found =
                    files.FirstOrDefault(f =>
                        string.Equals(Path.GetFileName(f), half.FileName, StringComparison.Ordinal))
                    ?? files.FirstOrDefault(f =>
                        string.Equals(Path.GetFileName(f), half.FileName, StringComparison.OrdinalIgnoreCase));
                if (found is null)
                {
                    ErrorMessage = Loc.Instance.Format(
                        "Flash_ErrorMissingFile",
                        half.FileName,
                        string.Join(", ", files.Select(Path.GetFileName)));
                    return;
                }

                _downloadedFiles[half] = found;
            }

            HalfRows.Clear();
            foreach (var half in _halves)
            {
                var ticked = true;
                string? hint = null;
                if (half.Role == FirmwareHalfRole.Peripheral)
                {
                    var lastSha = _history.Get(_context.ConfigKey, FirmwareHalfRole.Peripheral)?.CommitSha;
                    var advice = await _builds.AdvisePeripheralAsync(
                        target.Owner, target.Repo, lastSha, run.HeadSha);
                    (ticked, hint) = advice switch
                    {
                        PeripheralAdvice.Needed => (true, Loc.Instance["Flash_HintPeripheralNeeded"]),
                        PeripheralAdvice.NotNeeded => (false, Loc.Instance["Flash_HintPeripheralNotNeeded"]),
                        _ => (false, Loc.Instance["Flash_HintPeripheralUnknown"]),
                    };
                }

                var row = new FlashHalfRow(half, HalfLabel(half, _isUnibody), ticked, hint);
                row.PropertyChanged += OnHalfRowChanged;
                HalfRows.Add(row);
            }

            Step = FlashStep.Halves;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            NextCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnHalfRowChanged(object? sender, PropertyChangedEventArgs e) =>
        NextCommand.NotifyCanExecuteChanged();

    /// <summary>Read the ticked halves' bytes into the flash plan and move on; a failed read stays put.</summary>
    private void BuildPlan()
    {
        var next = new List<(FirmwareHalf Half, byte[] Bytes)>();
        foreach (var row in HalfRows.Where(r => r.IsChecked))
        {
            if (!_downloadedFiles.TryGetValue(row.Half, out var path))
            {
                continue;
            }

            try
            {
                next.Add((row.Half, File.ReadAllBytes(path)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ErrorMessage = Loc.Instance.Format("Flash_ErrorDownload", e.Message);
                return;
            }
        }

        if (next.Count == 0)
        {
            return;
        }

        _plan.Clear();
        _plan.AddRange(next);
        _planSha = _build?.Run?.HeadSha;
        Step = FlashStep.Flash;
    }

    /// <summary>
    /// The label for one half: "Keyboard" on a unibody; else the side name —
    /// a central with a known side is labelled as such ("Right half (central)").
    /// </summary>
    public static string HalfLabel(FirmwareHalf half, bool unibody)
    {
        if (unibody)
        {
            return Loc.Instance["Flash_HalfKeyboard"];
        }

        var side = half.Side switch
        {
            FirmwareSide.Left => Loc.Instance["Flash_HalfLeft"],
            FirmwareSide.Right => Loc.Instance["Flash_HalfRight"],
            _ => half.Role == FirmwareHalfRole.Central
                ? Loc.Instance["Flash_HalfCentral"]
                : Loc.Instance["Flash_HalfOther"],
        };

        return half.Role == FirmwareHalfRole.Central && half.Side != FirmwareSide.Unknown
            ? Loc.Instance.Format("Flash_HalfWithCentral", side)
            : side;
    }
}
