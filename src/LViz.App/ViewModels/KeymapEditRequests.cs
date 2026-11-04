using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;

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
/// The review dialog's go-ahead: the save continues. A <see langword="null"/>
/// answer instead of this record means the user kept editing.
/// </summary>
/// <param name="CommitMessage">The commit message as the user left it, for a
/// GitHub save; <see langword="null"/> for a local file.</param>
public sealed record KeymapReviewAnswer(string? CommitMessage);

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

/// <summary>
/// What the key dialog edits: the config it reads (layers, hold-taps,
/// macros), the file's #defines (for formatting and raw text), and the key;
/// the binding is Config.Layers[LayerIndex].Bindings[KeyIndex].
/// </summary>
/// <param name="Config">The loaded keymap the dialog reads from.</param>
/// <param name="Defines">The file's #defines, for formatting and raw text.</param>
/// <param name="LayerIndex">Which layer holds the key.</param>
/// <param name="KeyIndex">Which key on that layer.</param>
public sealed record KeyDialogRequest(
    KeyboardConfig Config,
    IReadOnlyList<KeyValuePair<string, string>> Defines,
    int LayerIndex,
    int KeyIndex);

/// <summary>
/// What the rename-layer dialog is shown (part 7): which layer is being
/// renamed and the name it has right now, so the dialog can pre-fill it.
/// </summary>
/// <param name="LayerIndex">Which layer is being renamed.</param>
/// <param name="CurrentName">The name the layer has right now.</param>
public sealed record RenameLayerRequest(int LayerIndex, string CurrentName);

/// <summary>
/// What the blocked-delete dialog is shown (part 7): the layer that cannot be
/// deleted and every reason why, so the dialog can name the layer and list
/// what stands in the way.
/// </summary>
/// <param name="LayerName">The <c>display-name</c> of the layer the user tried
/// to delete.</param>
/// <param name="Blockers">Every reason the delete is refused, in the order
/// <see cref="LayerDeleteCheck.Blockers"/> reports them.</param>
public sealed record LayerDeleteBlockedRequest(
    string LayerName,
    IReadOnlyList<LayerDeleteBlocker> Blockers);
