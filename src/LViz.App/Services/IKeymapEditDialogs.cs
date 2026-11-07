using LViz.App.ViewModels;
using LViz.Core.Models;

namespace LViz.App.Services;

/// <summary>
/// Every dialog keymap edit mode opens, one method each. The edit
/// view-model awaits these and applies the answer; where a dialog lives (which
/// window owns it, ring or dialog for a key) is the implementation's business.
/// A <see langword="null"/> answer always means the user backed out.
/// </summary>
public interface IKeymapEditDialogs
{
    /// <summary>Edit one binding in the user's key editor (the ring or the key
    /// dialog); the new binding, or null on cancel.</summary>
    Task<KeyBinding?> EditKeyAsync(KeyDialogRequest request);

    /// <summary>Add or edit a combo; the new combo, or null on cancel.</summary>
    Task<ZmkCombo?> EditComboAsync(ComboDialogRequest request);

    /// <summary>Ask for a layer's new name; null on cancel.</summary>
    Task<string?> RenameLayerAsync(RenameLayerRequest request);

    /// <summary>Tell the user why a layer cannot be deleted.</summary>
    Task ShowLayerDeleteBlockedAsync(LayerDeleteBlockedRequest request);

    /// <summary>Review the frozen save plan; null abandons the save, else the
    /// go-ahead with the commit message as the user left it.</summary>
    Task<KeymapReviewAnswer?> ReviewChangesAsync(KeymapReviewRequest request);

    /// <summary>The target changed underneath the edit: cancel, reload or
    /// overwrite.</summary>
    Task<KeymapSaveConflictChoice> AskSaveConflictAsync(KeymapConflictRequest request);

    /// <summary>After a structural save: the (already translated) lines about
    /// per-layer settings that moved or were dropped.</summary>
    Task ShowLayerSettingsNoticesAsync(IReadOnlyList<string> lines);
}
