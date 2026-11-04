using LViz.App.Localization;
using LViz.Core.Keymap.Writer;

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
/// The <see cref="Conflict"/>, <see cref="QuitWithUnsavedEdits"/>,
/// <see cref="LayerDeleteBlocked"/> and <see cref="LayerSettingsNotice"/>
/// factories cover the prompts the app shows today.
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
    /// The blocked-delete prompt naming the layer and listing one bulleted
    /// line per reason the delete was refused. The user only acknowledges it
    /// — there is nothing to fix from here, the blockers live in the file.
    /// </summary>
    public static ChoiceDialogViewModel LayerDeleteBlocked(LayerDeleteBlockedRequest request) => new(
        Loc.Instance["Edit_DeleteBlockedTitle"],
        Bulleted(
            Loc.Instance.Format("Edit_DeleteBlockedFormat", request.LayerName),
            request.Blockers.Select(BlockerText)),
        [new ChoiceDialogOption("ok", Loc.Instance["Edit_Ok"], true)]);

    /// <summary>
    /// The after-save notice about layer settings the structural save moved:
    /// one bulleted line per setting that was dropped or needs a look. The
    /// lines arrive already translated — the remap owns their content, this
    /// prompt only frames them.
    /// </summary>
    public static ChoiceDialogViewModel LayerSettingsNotice(IReadOnlyList<string> lines) => new(
        Loc.Instance["Edit_LayerSettingsNoticeTitle"],
        Bulleted(Loc.Instance["Edit_LayerSettingsNoticeIntro"], lines),
        [new ChoiceDialogOption("ok", Loc.Instance["Edit_Ok"], true)]);

    /// <summary>The intro line followed by every other line as a bullet.</summary>
    private static string Bulleted(string intro, IEnumerable<string> lines) =>
        string.Join("\n", [intro, .. lines.Select(line => "• " + line)]);

    /// <summary>
    /// The sentence for one delete blocker. Named blockers name themselves;
    /// an unnamed <see cref="LayerDeleteBlockerKind.Other"/> blocker falls
    /// back to a phrasing that only cites the line.
    /// </summary>
    private static string BlockerText(LayerDeleteBlocker blocker) => blocker.Kind switch
    {
        LayerDeleteBlockerKind.LastLayer => Loc.Instance["Edit_BlockerLastLayer"],
        LayerDeleteBlockerKind.Combo => Loc.Instance.Format(
            "Edit_BlockerComboFormat", blocker.Name),
        LayerDeleteBlockerKind.ConditionalLayer => Loc.Instance.Format(
            "Edit_BlockerConditionalFormat", blocker.Name, blocker.Line),
        LayerDeleteBlockerKind.DefineUsage => Loc.Instance.Format(
            "Edit_BlockerDefineFormat", blocker.Name, blocker.Line),
        _ => blocker.Name.Length > 0
            ? Loc.Instance.Format("Edit_BlockerOtherFormat", blocker.Name, blocker.Line)
            : Loc.Instance.Format("Edit_BlockerOtherUnnamedFormat", blocker.Line),
    };

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
