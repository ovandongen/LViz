using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.App.Services.GitHub;
using LViz.Core.Diagnostics;
using LViz.Core.GitHub;
using LViz.Core.Keymap;
using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Parser;
using LViz.Core.Keymap.Writer;
using LViz.Core.Persistence;

namespace LViz.App.ViewModels;

// Saving the edit: build the plan, review it, write it, handle a conflict.
public sealed partial class KeymapEditViewModel
{
    /// <summary>True while a save is in flight; gates Save and every other
    /// action of the edit so none can run against an edit that is being
    /// written.</summary>
    [ObservableProperty]
    private bool _isSaving;

    partial void OnIsSavingChanged(bool value) => NotifyCommandState();

    /// <summary>Only an edit with something to write can be saved, and only
    /// while no save is already running.</summary>
    private bool CanSaveEdit => IsOpen && HasChanges && !IsSaving;

    /// <summary>
    /// Write the edit where it came from: freeze it into a
    /// <see cref="KeymapSavePlan"/> (a writer refusal reports an error and
    /// keeps the user in edit mode), pass it by the review dialog, then save it
    /// through <see cref="SaveAsync"/>.
    /// <list type="bullet">
    /// <item><description>saved — <see cref="Completed"/> with
    /// <see cref="KeymapEditCompletion.Saved"/>; the main window closes the edit,
    /// reloads what was written and reports it in the status bar (toasts are for
    /// errors only);</description></item>
    /// <item><description>conflict — ask: reload loses the changes
    /// (<see cref="KeymapEditCompletion.ReloadRequested"/>), overwrite writes
    /// anyway, cancel keeps editing;</description></item>
    /// <item><description>failed — report the translated reason and keep editing
    /// with the changes intact.</description></item>
    /// </list>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveEdit))]
    private async Task SaveEditAsync()
    {
        if (!IsOpen) return;

        KeymapSavePlan plan;
        try
        {
            plan = BuildSavePlan();
        }
        catch (Exception ex) when (ex is ZmkKeymapParseException or ArgumentException)
        {
            // The text was never written; the edit and its undo history are
            // untouched, so the user stays put and can keep working.
            Report(KeymapEditReportKind.Error, Loc.Instance.Format("Edit_SaveRefusedFormat", ex.Message));
            DiagnosticLog.Warn("Edit", $"Building the keymap save plan failed: {ex}");
            return;
        }

        var answer = await _dialogs.ReviewChangesAsync(new KeymapReviewRequest(
            plan,
            Source.TargetName,
            Source.IsGitHub ? CommitMessageFor(plan) : null));
        if (answer is null) return;
        var commitMessage = answer.CommitMessage;

        IsSaving = true;
        try
        {
            var overwrite = false;

            while (true)
            {
                var outcome = await SaveAsync(plan, overwrite, commitMessage);

                if (outcome is KeymapEditSaveOutcome.Saved saved)
                {
                    await RaiseCompletedAsync(new KeymapEditCompletion.Saved(plan, saved.ReloadPath));
                    return;
                }

                if (outcome is KeymapEditSaveOutcome.Failed failed)
                {
                    Report(KeymapEditReportKind.Error, failed.Message);
                    return;
                }

                // Conflict: the target moved since the edit opened.
                var choice = await _dialogs.AskSaveConflictAsync(
                    new KeymapConflictRequest(Source.IsGitHub, Source.TargetName));

                if (choice == KeymapSaveConflictChoice.Cancel) return;

                if (choice == KeymapSaveConflictChoice.Reload)
                {
                    await RaiseCompletedAsync(new KeymapEditCompletion.ReloadRequested());
                    return;
                }

                overwrite = true;
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    private Task RaiseCompletedAsync(KeymapEditCompletion completion) =>
        Completed?.Invoke(completion) ?? Task.CompletedTask;

    /// <summary>
    /// Freeze the current edit into the text and the change lists a save writes,
    /// against the baseline text this editor opened.
    /// </summary>
    /// <remarks>
    /// <see cref="ZmkKeymapParseException"/> (a writer refused the text) and
    /// <see cref="ArgumentException"/> (the session drifted from the baseline)
    /// propagate — the caller shows them and keeps the user in edit mode.
    /// </remarks>
    public KeymapSavePlan BuildSavePlan() => KeymapSavePlan.Build(Source.BaselineText, Session);

    /// <summary>The English commit message a GitHub save of <paramref name="plan"/> uses.</summary>
    public string CommitMessageFor(KeymapSavePlan plan) => KeymapCommitMessage.Build(plan);

    /// <summary>
    /// Write <paramref name="plan"/> to where this edit came from: the local
    /// file through <see cref="KeymapFileSaver"/> (the content on disk is
    /// backed up first), or GitHub as a commit on the keymap's branch against
    /// <see cref="KeymapEditSource.BaseBlobSha"/> with
    /// <paramref name="commitMessage"/> (the message the user edited in the
    /// review dialog), or <see cref="CommitMessageFor"/> when it is null. A
    /// local save ignores the message. A target that changed since the edit
    /// started reports <see cref="KeymapEditSaveOutcome.Conflict"/>;
    /// <paramref name="overwrite"/> replaces it anyway.
    /// </summary>
    /// <remarks>
    /// The only exceptions caught are the local <see cref="IOException"/> and
    /// <see cref="UnauthorizedAccessException"/>, which become a translated
    /// <see cref="KeymapEditSaveOutcome.Failed"/>; GitHub failures map to
    /// Failed through <see cref="GitHubOpenViewModel.MessageFor"/>, and a
    /// missing service to <c>Edit_SaveGitHubUnavailable</c>. A cancelled
    /// <paramref name="ct"/> propagates. Saving never touches the session —
    /// after any outcome the edit and its undo history are intact.
    /// </remarks>
    public async Task<KeymapEditSaveOutcome> SaveAsync(
        KeymapSavePlan plan,
        bool overwrite,
        string? commitMessage = null,
        CancellationToken ct = default)
    {
        if (!Source.IsGitHub)
        {
            try
            {
                var local = await KeymapFileSaver.SaveAsync(
                    Source.LocalPath, Source.BaselineText, plan.NewSource, overwrite, _backups, ct);

                return local == KeymapFileSaveResult.Saved
                    ? new KeymapEditSaveOutcome.Saved(Source.LocalPath)
                    : new KeymapEditSaveOutcome.Conflict();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn("Editor", $"Local save of {Source.LocalPath} failed: {ex}");
                return new KeymapEditSaveOutcome.Failed(
                    Loc.Instance.Format("Edit_SaveFailedFormat", ex.Message));
            }
        }

        if (_gitHub is null)
            return new KeymapEditSaveOutcome.Failed(Loc.Instance["Edit_SaveGitHubUnavailable"]);

        var save = await _gitHub.SaveAsync(
            Source.GitHub!,
            new UTF8Encoding(false).GetBytes(plan.NewSource),
            Source.BaseBlobSha,
            commitMessage ?? CommitMessageFor(plan),
            overwrite,
            ct);

        switch (save)
        {
            case GitHubSaveResult.Saved committed:
                DiagnosticLog.Info("Editor", $"Committed {Source.Entry} to GitHub as {committed.CommitSha}.");
                return new KeymapEditSaveOutcome.Saved(
                    _gitHub.LocalPathFor(Source.GitHub!) ?? Source.LocalPath);

            case GitHubSaveResult.Failed failure:
                return new KeymapEditSaveOutcome.Failed(GitHubOpenViewModel.MessageFor(
                    new GitHubApiException(failure.Kind, failure.Message)));

            case GitHubSaveResult.Conflict:
                return new KeymapEditSaveOutcome.Conflict();

            default:
                throw new InvalidOperationException(
                    $"unsupported GitHub save result {save.GetType().Name}.");
        }
    }
}
