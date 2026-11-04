using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Localization;
using LViz.Core.Keymap.Editing;

namespace LViz.App.ViewModels;

/// <summary>
/// View model for the review-before-saving dialog: what saving
/// <see cref="KeymapSavePlan"/> amounts to, as one translated line per change —
/// structure edits first, then renames, then keys — plus the commit message a
/// GitHub save will carry (null for a local file), which the user may edit.
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
    /// order structure edits, renames, keys.</summary>
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
            plan.StructureEdits.Count + plan.Renames.Count + plan.BindingChanges.Count);

        foreach (var edit in plan.StructureEdits)
            lines.Add(DescribeStructure(edit));

        foreach (var rename in plan.Renames)
            lines.Add(Loc.Instance.Format(
                "Edit_ReviewRenameFormat", rename.LayerIndex, rename.OldName, rename.NewName));

        foreach (var change in plan.BindingChanges)
            lines.Add(Loc.Instance.Format(
                "Edit_ReviewKeyFormat",
                change.LayerName, change.KeyIndex, change.Old.Display, change.New.Display));

        return lines;
    }

    private static string DescribeStructure(LayerStructureEdit edit) => edit switch
    {
        LayerStructureEdit.Add add => Loc.Instance.Format("Edit_ReviewAddFormat", add.Name, add.AtIndex),
        LayerStructureEdit.Delete delete => Loc.Instance.Format("Edit_ReviewDeleteFormat", delete.Index),
        LayerStructureEdit.Move move => Loc.Instance.Format("Edit_ReviewMoveFormat", move.From, move.To),
        _ => throw new NotSupportedException($"Unsupported layer structure edit '{edit.GetType().Name}'."),
    };
}
