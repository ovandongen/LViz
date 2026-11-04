using LViz.Core.Keymap.Editing;

namespace LViz.App.ViewModels;

/// <summary>
/// What the "review before saving" dialog is shown (part 8): the frozen plan
/// whose change lists it renders, the target the save writes to (a file name or
/// <c>owner/repo (branch)</c>), and the commit message — only for a GitHub
/// save, where the commit is visible to others.
/// </summary>
/// <param name="Plan">The edit the save would write.</param>
/// <param name="TargetName">Where it would be written.</param>
/// <param name="CommitMessage">The message a GitHub commit would carry, else
/// <see langword="null"/>.</param>
public sealed record KeymapReviewRequest(KeymapSavePlan Plan, string TargetName, string? CommitMessage);

/// <summary>
/// What the save-conflict dialog is shown (part 9): the target that changed
/// underneath the edit, so the prompt can name it and word itself for a file
/// versus a repository.
/// </summary>
/// <param name="IsGitHub">True when the target is a GitHub file.</param>
/// <param name="TargetName">Where the save was writing.</param>
public sealed record KeymapConflictRequest(bool IsGitHub, string TargetName);

/// <summary>
/// What the user answered a save conflict: give up, drop the local edit and
/// show what is on the target, or write over it.
/// </summary>
public enum KeymapSaveConflictChoice
{
    /// <summary>Keep editing; nothing was written.</summary>
    Cancel,

    /// <summary>Discard the edit and reload the target as it stands.</summary>
    Reload,

    /// <summary>Write the edit over the changed target.</summary>
    Overwrite,
}
