using LViz.App.Localization;

namespace LViz.App.ViewModels;

/// <summary>
/// One button of a <see cref="ChoiceDialog"/>: the id the dialog closes with,
/// its translated label, and whether it is the primary (accent-styled) choice.
/// </summary>
/// <param name="Id">Returned as the dialog result when the option is clicked.</param>
/// <param name="Label">Button text.</param>
/// <param name="IsAccent">True for the primary option.</param>
public sealed record ChoiceDialogOption(string Id, string Label, bool IsAccent);

/// <summary>
/// View model for the small reusable choice dialog: a title, a wrapped message
/// and a row of options, each closing the window with its id. Read-only by
/// design — every string is resolved through <see cref="Loc"/> at construction,
/// so nothing here raises <see cref="System.ComponentModel.INotifyPropertyChanged"/>.
/// The <see cref="Conflict"/> and <see cref="QuitWithUnsavedEdits"/> factories
/// cover the two prompts the app shows today.
/// </summary>
public sealed class ChoiceDialogViewModel(
    string title,
    string message,
    IReadOnlyList<ChoiceDialogOption> options)
{
    /// <summary>Window title.</summary>
    public string Title { get; } = title;

    /// <summary>What happened and what the options mean.</summary>
    public string Message { get; } = message;

    /// <summary>The options, in button order.</summary>
    public IReadOnlyList<ChoiceDialogOption> Options { get; } = options;

    /// <summary>
    /// The save-conflict prompt naming the changed target. Cancel keeps
    /// editing, Reload drops the edit for the other version, Overwrite (the
    /// primary choice) writes over it.
    /// </summary>
    public static ChoiceDialogViewModel Conflict(KeymapConflictRequest request) => new(
        Loc.Instance["Edit_ConflictTitle"],
        Loc.Instance.Format(
            request.IsGitHub ? "Edit_ConflictGitHubFormat" : "Edit_ConflictLocalFormat",
            request.TargetName),
        [
            new ChoiceDialogOption("cancel", Loc.Instance["Edit_Cancel"], false),
            new ChoiceDialogOption("reload", Loc.Instance["Edit_ConflictReload"], false),
            new ChoiceDialogOption("overwrite", Loc.Instance["Edit_ConflictOverwrite"], true),
        ]);

    /// <summary>
    /// The quit-with-unsaved-edits prompt naming the change count. There is
    /// deliberately no "save and quit" — saving stays an explicit in-editor
    /// step with its own review dialog.
    /// </summary>
    public static ChoiceDialogViewModel QuitWithUnsavedEdits(int count) => new(
        Loc.Instance["Edit_QuitTitle"],
        Loc.Instance.Format("Edit_QuitMessageFormat", count),
        [
            new ChoiceDialogOption("cancel", Loc.Instance["Edit_Cancel"], false),
            new ChoiceDialogOption("quit", Loc.Instance["Edit_QuitConfirm"], true),
        ]);

    /// <summary>
    /// Maps a dialog result back to a save-conflict answer. Anything that is
    /// not an explicit reload or overwrite — including <see langword="null"/>
    /// from Esc, the window's close button or a cancelled callback — is Cancel:
    /// nothing is ever written or discarded implicitly.
    /// </summary>
    public static KeymapSaveConflictChoice ToConflictChoice(string? id) => id switch
    {
        "reload" => KeymapSaveConflictChoice.Reload,
        "overwrite" => KeymapSaveConflictChoice.Overwrite,
        _ => KeymapSaveConflictChoice.Cancel,
    };
}
