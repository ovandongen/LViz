using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using LViz.App.Services;
using LViz.App.ViewModels;
using LViz.Core.Settings;
// Avalonia.Input also declares a KeyBinding, so the model type is aliased.
using ZmkCombo = LViz.Core.Models.ZmkCombo;
using ZmkKeyBinding = LViz.Core.Models.KeyBinding;

namespace LViz.App.Views;

/// <summary>
/// The real <see cref="IKeymapEditDialogs"/>: every dialog keymap edit mode
/// opens, modal over the application's main window. The owner is looked up at
/// call time from the application lifetime — the container builds this (and the
/// view-model that holds it) before the window exists, and an edit can only be
/// running once the window is up.
/// </summary>
internal sealed class AvaloniaKeymapEditDialogs : IKeymapEditDialogs
{
    private readonly ISettingsService _settings;

    public AvaloniaKeymapEditDialogs(ISettingsService settings) => _settings = settings;

    private static MainWindow Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow as MainWindow
        ?? throw new InvalidOperationException("The main window is not available to own a dialog.");

    private bool UseRing => _settings.Load().RingKeyEditor;

    /// <inheritdoc />
    public Task<ZmkKeyBinding?> EditKeyAsync(KeyDialogRequest request) =>
        Owner.EditKeyAsync(request, UseRing);

    /// <inheritdoc />
    /// <remarks>The dialog's Binding button opens the user's key editor
    /// anchored on the button itself.</remarks>
    public async Task<ZmkCombo?> EditComboAsync(ComboDialogRequest request)
    {
        var owner = Owner;
        var comboVm = new ComboDialogViewModel(request);
        var dlg = new ComboDialog { DataContext = comboVm };
        comboVm.BindingEditorRequested =
            r => KeyEditorHost.EditAsync(dlg, dlg.BindingAnchor, r, UseRing);
        return await dlg.ShowDialog<ZmkCombo?>(owner);
    }

    /// <inheritdoc />
    public async Task<string?> RenameLayerAsync(RenameLayerRequest request)
    {
        var dlg = new RenameLayerDialog { DataContext = new RenameLayerDialogViewModel(request) };
        return await dlg.ShowDialog<string?>(Owner);
    }

    /// <inheritdoc />
    public async Task ShowLayerDeleteBlockedAsync(LayerDeleteBlockedRequest request)
    {
        var dlg = new ChoiceDialog { DataContext = ChoiceDialogViewModel.LayerDeleteBlocked(request) };
        await dlg.ShowDialog<string?>(Owner);
    }

    /// <inheritdoc />
    public async Task<KeymapReviewAnswer?> ReviewChangesAsync(KeymapReviewRequest request)
    {
        var dlg = new KeymapReviewDialog { DataContext = new KeymapReviewDialogViewModel(request) };
        return await dlg.ShowDialog<KeymapReviewAnswer?>(Owner);
    }

    /// <inheritdoc />
    public async Task<KeymapSaveConflictChoice> AskSaveConflictAsync(KeymapConflictRequest request)
    {
        var dlg = new ChoiceDialog { DataContext = ChoiceDialogViewModel.Conflict(request) };
        return ChoiceDialogViewModel.ToConflictChoice(await dlg.ShowDialog<string?>(Owner));
    }

    /// <inheritdoc />
    public async Task ShowLayerSettingsNoticesAsync(IReadOnlyList<string> lines)
    {
        var dlg = new ChoiceDialog { DataContext = ChoiceDialogViewModel.LayerSettingsNotice(lines) };
        await dlg.ShowDialog<string?>(Owner);
    }
}
