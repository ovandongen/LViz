using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services.GitHub;
using LViz.Core.Diagnostics;
using LViz.Core.GitHub;
using LViz.Core.Persistence;
using LViz.Core.ZmkConfig;

namespace LViz.App.ViewModels;

/// <summary>
/// One file of the setup plan as the review step shows it: its path in the
/// config and the change's diff as prefixed display lines.
/// </summary>
public sealed record ModuleSetupFileView(string Path, bool IsNew, IReadOnlyList<ModuleSetupDiffLine> Lines);

/// <summary>
/// One review diff line: its text prefixed with <c>"+ "</c>, <c>"- "</c> or
/// <c>"  "</c> (a skipped run is <c>"…"</c>), plus what it is so the view can
/// colour it.
/// </summary>
public sealed record ModuleSetupDiffLine(string Text, bool IsAdded, bool IsRemoved, bool IsGap);

/// <summary>
/// The module setup wizard's Review, Save and Done steps: Next on Choose
/// builds the plan (<see cref="GoToReview"/>), Review shows it as per-file
/// diffs, Save commits it to GitHub in one commit or writes it locally, and
/// Done reports whether the board has started talking. A conflict never
/// overwrites — it only offers <see cref="ReloadCommand"/>, which runs
/// <see cref="StartAsync"/> again from Check.
/// </summary>
public sealed partial class ModuleSetupViewModel
{
    /// <summary>The commit message of the GitHub save — English, like the keymap commits.</summary>
    public const string CommitMessage = "LViz: add live-feature modules (zmk-raw-hid, zmk-hid-viz)";

    /// <summary>The commit message when the modules were already set up and only features are added.</summary>
    public const string CommitMessageAddFeatures = "LViz: enable more live features";

    /// <summary>What Review shows and Save writes; built by <see cref="GoToReview"/>.</summary>
    private ZmkModuleSetupPlan? _plan;

    // --- review step ---

    [ObservableProperty]
    private IReadOnlyList<ModuleSetupFileView> _files = [];

    /// <summary>The "not tested with this ZMK yet" notice for an unknown revision, else null.</summary>
    [ObservableProperty]
    private string? _revisionWarning;

    /// <summary>The "switches zzeneg's zmk-raw-hid to ovandongen" notice, else null.</summary>
    [ObservableProperty]
    private string? _forkNotice;

    /// <summary>True after a save conflict; reveals the reload button — there is no overwrite.</summary>
    [ObservableProperty]
    private bool _showReload;

    /// <summary>
    /// Builds the plan from the checked files and facts plus the current
    /// choices and fills Review. A refusal lands in
    /// <see cref="ModuleSetupViewModel.ErrorMessage"/> and stays on Choose.
    /// </summary>
    private void GoToReview()
    {
        if (_configFiles is not { } files || _facts is not { } facts || SelectedCentral is not { } central)
            return; // Check never finished — the wizard cannot reach Next here.

        ErrorMessage = null;
        ZmkModuleSetupPlan plan;
        try
        {
            plan = ZmkModuleSetupPlan.Build(
                files,
                facts,
                new ZmkModuleChoices(
                    central.Index,
                    Rgb && ShowRgb && !RgbLocked,
                    Signal && !SignalLocked,
                    Emit && !EmitLocked,
                    System: System && !SystemLocked),
                _newGuid);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            ErrorMessage = Loc.Instance.Format("ModuleSetup_ErrorPlan", e.Message);
            return;
        }

        _plan = plan;
        Files = [.. plan.Changes.Select(DiffOf)];
        RevisionWarning = plan.Revision.Known
            ? null
            : Loc.Instance.Format(
                "ModuleSetup_RevisionUnknown",
                facts.ZmkOwner ?? Loc.Instance["ModuleSetup_Unknown"],
                facts.ZmkRevision ?? Loc.Instance["ModuleSetup_Unknown"]);
        ForkNotice = plan.SwitchesToFork ? Loc.Instance["ModuleSetup_ForkNotice"] : null;
        Step = ModuleSetupStep.Review;
    }

    /// <summary>The change as a review entry: its diff trimmed to two lines of context per hunk.</summary>
    private static ModuleSetupFileView DiffOf(ZmkConfigFileChange change)
    {
        var diff = TextLineDiff.WithContext(TextLineDiff.Compute(change.OldText, change.NewText), 2);
        var lines = new List<ModuleSetupDiffLine>(diff.Count);
        foreach (var line in diff)
        {
            if (line is null)
            {
                lines.Add(new ModuleSetupDiffLine("\u2026", false, false, true));
                continue;
            }

            var (prefix, added, removed) = line.Kind switch
            {
                TextDiffKind.Added => ("+ ", true, false),
                TextDiffKind.Removed => ("- ", false, true),
                _ => ("  ", false, false),
            };
            lines.Add(new ModuleSetupDiffLine(prefix + line.Text, added, removed, false));
        }

        return new ModuleSetupFileView(change.RelativePath, change.IsNew, lines);
    }

    // --- save ---

    private bool CanSave() => Step == ModuleSetupStep.Review && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_plan is not { } plan || _root is not { } root)
            return; // Review was never reached; there is nothing to save.

        ErrorMessage = null;
        ShowReload = false;
        IsBusy = true;
        try
        {
            if (_context.GitHub is { } target)
            {
                var writes = plan.Changes
                    .Select(c => new GitHubFileWrite(c.RelativePath, c.NewText))
                    .ToList();
                var result = await _gitHub!.SaveFilesAsync(
                    target.Owner, target.Repo, target.Branch, _baseSha!, writes,
                    _facts?.Status == ZmkModuleStatus.SetUp ? CommitMessageAddFeatures : CommitMessage);
                switch (result)
                {
                    case GitHubSaveResult.Saved:
                        FinishSave();
                        break;
                    case GitHubSaveResult.Conflict:
                        ErrorMessage = Loc.Instance["ModuleSetup_ConflictGitHub"];
                        ShowReload = true;
                        break;
                    case GitHubSaveResult.Failed failed:
                        ErrorMessage = GitHubErrorText.For(
                            new GitHubApiException(failed.Kind, failed.Message));
                        break;
                }
            }
            else
            {
                try
                {
                    if (await ConfigFileSaver.SaveAsync(root, plan.Changes, _backups)
                        == KeymapFileSaveResult.Saved)
                        FinishSave();
                    else
                    {
                        ErrorMessage = Loc.Instance["ModuleSetup_ConflictLocal"];
                        ShowReload = true;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    ErrorMessage = Loc.Instance.Format("ModuleSetup_ErrorSave", e.Message);
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The saved outcome: Done, and the board status read fresh.</summary>
    private void FinishSave()
    {
        Saved = true;
        IsAddFeaturesSave = _facts?.Status == ZmkModuleStatus.SetUp;
        Step = ModuleSetupStep.Done;
        _ = RefreshBoardStatusAsync();
    }

    /// <summary>The conflict's only escape: no overwrite — read the config again from Check.</summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        ShowReload = false;
        await StartAsync();
    }

    // --- done step ---

    public bool IsGitHub => _context.GitHub is not null;

    /// <summary>The config's GitHub Actions page, or null for a local config.</summary>
    public string? ActionsUrl =>
        _context.GitHub is { } target
            ? $"https://github.com/{target.Owner}/{target.Repo}/actions"
            : null;

    /// <summary>
    /// True once the board reports the config ID this config carries — the one
    /// the wizard just wrote, else the one the config already had; the window
    /// ticks <see cref="RefreshBoardStatusAsync"/>.
    /// </summary>
    [ObservableProperty]
    private bool _isBoardConnected;

    /// <summary>
    /// The save only added features to a config that was already set up. Its
    /// config ID didn't change, so the board reports it with the old firmware
    /// too: the Done step can't tell when the new firmware is on, skips the
    /// board check and asks to flash it instead.
    /// </summary>
    [ObservableProperty]
    private bool _isAddFeaturesSave;

    // One config-ID query in flight at a time; a tick arriving during one is
    // dropped, not queued behind it.
    private bool _boardQueryRunning;

    /// <summary>
    /// Asks the board for its config ID and lands in <see cref="IsBoardConnected"/>
    /// whether it reports the ID this config carries; the window calls this on
    /// a timer. A query already running returns at once; one that throws counts
    /// as no answer. After an add-features save it asks nothing (see
    /// <see cref="IsAddFeaturesSave"/>).
    /// </summary>
    public async Task RefreshBoardStatusAsync()
    {
        if (_boardQueryRunning || IsAddFeaturesSave)
            return;
        _boardQueryRunning = true;
        try
        {
            string? reported;
            try
            {
                reported = await _context.QueryConfigId(CancellationToken.None);
            }
            catch (Exception e)
            {
                DiagnosticLog.Warn("ModuleSetup", $"Querying the board's config ID failed: {e.Message}");
                reported = null;
            }

            var expected = _plan?.NewConfigId ?? _facts?.ConfigId;
            // The 0xFA reply carries at most 31 characters: a GUID comes back cut.
            IsBoardConnected = ZmkConfigId.Matches(reported, expected);
        }
        finally
        {
            _boardQueryRunning = false;
        }
    }

    [RelayCommand]
    private void OpenActions()
    {
        if (ActionsUrl is { } url)
            _desktop.OpenUrl(url);
    }
}
