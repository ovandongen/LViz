using LViz.Core.Keymap.Editing;
using LViz.Core.Keymap.Writer;
using LViz.Core.Layout;
using LViz.Core.Models;

namespace LViz.App.ViewModels;

/// <summary>
/// What the "review before saving" dialog is shown: the frozen plan
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
/// What the save-conflict dialog is shown: the target that changed
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
    int KeyIndex)
{
    /// <summary>The defines a layer number may be written as, for display;
    /// null = use <see cref="Defines"/>. Formatting with every define names
    /// a layer after the first define with that value, which in files like
    /// a go60 export is a key-position name rather than a layer name.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? LayerDefines { get; init; }

    /// <summary>The binding to edit instead of the layer key's (e.g. a combo's);
    /// LayerIndex stays the layer context and KeyIndex is then ignored.</summary>
    public KeyBinding? Binding { get; init; }

    /// <summary>Caption for dialog title and ring centre instead of "Key N on layer X".</summary>
    public string? Caption { get; init; }
}

/// <summary>
/// What the rename-layer dialog is shown: which layer is being
/// renamed and the name it has right now, so the dialog can pre-fill it.
/// </summary>
/// <param name="LayerIndex">Which layer is being renamed.</param>
/// <param name="CurrentName">The name the layer has right now.</param>
public sealed record RenameLayerRequest(int LayerIndex, string CurrentName);

/// <summary>
/// What the blocked-delete dialog is shown: the layer that cannot be
/// deleted and every reason why, so the dialog can name the layer and list
/// what stands in the way.
/// </summary>
/// <param name="LayerName">The <c>display-name</c> of the layer the user tried
/// to delete.</param>
/// <param name="Blockers">Every reason the delete is refused, in the order
/// <see cref="LayerDeleteCheck.Blockers"/> reports them.</param>
public sealed record LayerDeleteBlockedRequest(
    string LayerName,
    IReadOnlyList<LayerDeleteBlocker> Blockers)
{
    /// <summary>True while the open edit still holds unsaved combo changes: a
    /// combo blocker may then already be fixed in the editor, but the fix only
    /// reaches the file when the edit is saved — the dialog then adds a line
    /// saying so.</summary>
    public bool ComboEditsPending { get; init; }
}

/// <summary>What the combo dialog edits: the board it draws, the edited
/// keymap, the file's defines (parsing) and layer defines (display), which
/// combo (null = a new one) and the layer the board shows, for the dialog's
/// key labels.</summary>
/// <param name="Profile">The board the dialog draws its key picker on.</param>
/// <param name="Config">The edited keymap the dialog reads its combos from.</param>
/// <param name="Defines">The file's #defines, for binding formatting and raw
/// text.</param>
/// <param name="LayerDefines">The defines usable as layer references, for
/// display.</param>
/// <param name="ComboIndex">Which combo to edit (index into
/// <see cref="KeyboardConfig.Combos"/>), or <see langword="null"/> when the
/// dialog opens for a new combo.</param>
/// <param name="ShownLayerIndex">The layer the main window shows — the dialog
/// labels the combo's keys with that layer's bindings.</param>
public sealed record ComboDialogRequest(
    IKeyboardProfile Profile,
    KeyboardConfig Config,
    IReadOnlyList<KeyValuePair<string, string>> Defines,
    IReadOnlyList<KeyValuePair<string, string>> LayerDefines,
    int? ComboIndex,
    int ShownLayerIndex);

/// <summary>
/// What an edit that has finished its work asks the main window to do next;
/// raised through <see cref="KeymapEditViewModel.Completed"/>.
/// </summary>
public abstract record KeymapEditCompletion
{
    private KeymapEditCompletion() { }

    /// <summary>The edit was written; carry the per-layer settings along, close
    /// the edit and reload what was written.</summary>
    /// <param name="Plan">The plan that was written.</param>
    /// <param name="ReloadPath">The file to load afterwards.</param>
    public sealed record Saved(KeymapSavePlan Plan, string ReloadPath) : KeymapEditCompletion;

    /// <summary>The user answered a save conflict with "reload": drop the edit
    /// and show the target as it stands.</summary>
    public sealed record ReloadRequested : KeymapEditCompletion;
}

/// <summary>What kind of line an edit reports to the main window.</summary>
public enum KeymapEditReportKind
{
    /// <summary>A status-bar line.</summary>
    Status,

    /// <summary>A toast; something did not go through.</summary>
    Error,
}

/// <summary>One translated line the edit reports to the main window.</summary>
public sealed record KeymapEditReport(KeymapEditReportKind Kind, string Message);
