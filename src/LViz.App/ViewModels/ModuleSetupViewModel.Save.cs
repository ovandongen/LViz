using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services.GitHub;
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
                new ZmkModuleChoices(central.Index, Rgb && ShowRgb, Signal, Emit),
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
                    target.Owner, target.Repo, target.Branch, _baseSha!, writes, CommitMessage);
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
                        ErrorMessage = GitHubOpenViewModel.MessageFor(
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
        Step = ModuleSetupStep.Done;
        RefreshBoardStatus();
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

    /// <summary>True while the keyboard reports in; the window ticks <see cref="RefreshBoardStatus"/>.</summary>
    [ObservableProperty]
    private bool _isBoardConnected;

    /// <summary>Re-reads whether the board is plugged in; the window calls this on a timer.</summary>
    public void RefreshBoardStatus() => IsBoardConnected = _context.IsBoardConnected();

    /// <summary>Opens a URL in the browser; the shell wires it, tests collect the call.</summary>
    public Action<string>? OpenUrl { get; set; }

    [RelayCommand]
    private void OpenActions()
    {
        if (ActionsUrl is { } url)
            OpenUrl?.Invoke(url);
    }
}
