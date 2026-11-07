using LViz.App.ViewModels;
using LViz.Core.Models;

namespace LViz.App.Services;

/// <summary>
/// The dialogs of a <see cref="MainWindowViewModel"/> built without a dialog
/// host (view-model tests that never edit a keymap). Every call fails loudly:
/// an edit that opens a dialog without a host is a wiring bug, never a silent
/// "nothing happens".
/// </summary>
internal sealed class UnavailableKeymapEditDialogs : IKeymapEditDialogs
{
    private static InvalidOperationException Unavailable() =>
        new("No IKeymapEditDialogs was supplied to the MainWindowViewModel.");

    public Task<KeyBinding?> EditKeyAsync(KeyDialogRequest request) => throw Unavailable();

    public Task<ZmkCombo?> EditComboAsync(ComboDialogRequest request) => throw Unavailable();

    public Task<string?> RenameLayerAsync(RenameLayerRequest request) => throw Unavailable();

    public Task ShowLayerDeleteBlockedAsync(LayerDeleteBlockedRequest request) => throw Unavailable();

    public Task<KeymapReviewAnswer?> ReviewChangesAsync(KeymapReviewRequest request) => throw Unavailable();

    public Task<KeymapSaveConflictChoice> AskSaveConflictAsync(KeymapConflictRequest request) =>
        throw Unavailable();

    public Task ShowLayerSettingsNoticesAsync(IReadOnlyList<string> lines) => throw Unavailable();
}
