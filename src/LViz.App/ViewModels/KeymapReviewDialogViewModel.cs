using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Localization;
using LViz.Core.Keymap.Editing;
using LViz.Core.Models;

namespace LViz.App.ViewModels;

/// <summary>
/// View model for the review-before-saving dialog: what saving
/// <see cref="KeymapSavePlan"/> amounts to, as one translated line per change —
/// structure edits first, then renames, then combos, then keys — plus the
/// commit message a GitHub save will carry (null for a local file), which the
/// user may edit.
/// <para>
/// The change list is a frozen plan, built once in the constructor; only the
/// commit message changes, and with it whether Save is possible.
/// </para>
/// </summary>
public sealed partial class KeymapReviewDialogViewModel : ObservableObject
{
    private readonly KeymapReviewRequest _request;

    public KeymapReviewDialogViewModel(KeymapReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        Lines = BuildLines(request.Plan);
        _commitMessage = request.CommitMessage;
    }

    /// <summary>"These changes will be saved to &lt;target&gt;:".</summary>
    public string HeaderText => Loc.Instance.Format("Edit_ReviewHeaderFormat", _request.TargetName);

    /// <summary>Every change the save writes, one translated line each, in the
    /// order structure edits, renames, combos, keys.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>The message the GitHub commit will carry, starting at the
    /// generated one; <see langword="null"/> for a local file. Not translated
    /// — it is what goes to GitHub.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string? _commitMessage;

    /// <summary>False for a local file, where there is nothing to commit.</summary>
    public bool HasCommitMessage => _request.CommitMessage is not null;

    /// <summary>False while a GitHub save's commit message is blank: GitHub
    /// refuses a commit without one.</summary>
    public bool CanSave => !HasCommitMessage || !string.IsNullOrWhiteSpace(CommitMessage);

    /// <summary>The go-ahead with the commit message trimmed, or
    /// <see langword="null"/> while <see cref="CanSave"/> is false.</summary>
    public KeymapReviewAnswer? Answer() =>
        !CanSave ? null : new KeymapReviewAnswer(HasCommitMessage ? CommitMessage!.Trim() : null);

    private static IReadOnlyList<string> BuildLines(KeymapSavePlan plan)
    {
        var lines = new List<string>(
            plan.StructureEdits.Count + plan.Renames.Count
            + plan.ComboChanges.Count + plan.BindingChanges.Count);

        foreach (var edit in plan.StructureEdits)
            lines.Add(DescribeStructure(edit));

        foreach (var rename in plan.Renames)
            lines.Add(Loc.Instance.Format(
                "Edit_ReviewRenameFormat", rename.LayerIndex, rename.OldName, rename.NewName));

        foreach (var change in plan.ComboChanges)
            lines.Add(DescribeCombo(change));

        foreach (var change in plan.BindingChanges)
            lines.Add(Loc.Instance.Format(
                "Edit_ReviewKeyFormat",
                change.LayerName, change.KeyIndex, change.Old.Display, change.New.Display));

        return lines;
    }

    /// <summary>
    /// One combo line: an Add names its node, keys and binding; a Change names
    /// the node the file knows (the new values render after it); a Delete names
    /// the node and the keys it had. A hand-built plan whose Delete carries no
    /// Old is a broken plan and throws.
    /// </summary>
    private static string DescribeCombo(KeymapComboChange change)
    {
        switch (change.Edit)
        {
            case ComboEdit.Add add:
                return Loc.Instance.Format(
                    "Edit_ReviewComboAddFormat",
                    add.Combo.NodeName ?? string.Empty,
                    ComboKeys(add.Combo), add.Combo.Binding.Display);

            case ComboEdit.Change edit:
                return Loc.Instance.Format(
                    "Edit_ReviewComboEditFormat",
                    edit.NodeName, ComboKeys(edit.Combo), edit.Combo.Binding.Display);

            case ComboEdit.Delete delete:
                if (change.Old is null)
                    throw new ArgumentException(
                        "combo delete without the deleted combo.", nameof(change));
                return Loc.Instance.Format(
                    "Edit_ReviewComboDeleteFormat", delete.NodeName, ComboKeys(change.Old));

            default:
                throw new NotSupportedException(
                    $"Unsupported combo edit '{change.Edit.GetType().Name}'.");
        }
    }

    /// <summary>The combo's key positions in their own order, joined with
    /// <c>+</c> — <c>46+47</c>.</summary>
    private static string ComboKeys(ZmkCombo combo) => string.Join("+", combo.KeyPositions);

    private static string DescribeStructure(LayerStructureEdit edit) => edit switch
    {
        LayerStructureEdit.Add add => Loc.Instance.Format("Edit_ReviewAddFormat", add.Name, add.AtIndex),
        LayerStructureEdit.Delete delete => Loc.Instance.Format("Edit_ReviewDeleteFormat", delete.Index),
        LayerStructureEdit.Move move => Loc.Instance.Format("Edit_ReviewMoveFormat", move.From, move.To),
        _ => throw new NotSupportedException($"Unsupported layer structure edit '{edit.GetType().Name}'."),
    };
}
