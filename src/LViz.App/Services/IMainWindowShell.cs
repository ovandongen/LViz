using LViz.App.ViewModels;
using LViz.Core.Layout;
using LViz.Core.Settings;

namespace LViz.App.Services;

/// <summary>
/// What the main window's view-model asks of the window host: quit, show the
/// window, open the other windows and dialogs, copy to the clipboard. The
/// view-model awaits these and applies the answer; which window owns a dialog
/// is the implementation's business. A <see langword="null"/> answer always
/// means the user backed out.
/// </summary>
public interface IMainWindowShell
{
    /// <summary>Quit the application (after the unsaved-edits prompt).</summary>
    void Quit();

    /// <summary>Show, activate and un-minimize the main window.</summary>
    void ShowWindow();

    /// <summary>Let the user pick a keymap file; its local path, or null.</summary>
    Task<string?> PickKeymapFileAsync();

    /// <summary>Put text on the clipboard; false when no clipboard is available.</summary>
    Task<bool> CopyTextAsync(string text);

    /// <summary>Open the single Settings window, or bring the open one to the front.</summary>
    void OpenSettings();

    /// <summary>Show the read-only keyboard shortcuts list.</summary>
    Task ShowShortcutsAsync();

    /// <summary>Run the layout wizard; the saved layout, or null on cancel.</summary>
    Task<LayoutWizardResult?> RunLayoutWizardAsync(LayoutWizardContext context);

    /// <summary>Run the GitHub sign-in / repo / keymap picker; the chosen keymap, or null.</summary>
    Task<GitHubOpenSelection?> PickGitHubKeymapAsync(Func<IReadOnlyCollection<string>> attachedHidNames);

    /// <summary>Run the "Edit layout…" window; the saved layout, or null on cancel.</summary>
    Task<UserLayoutFile?> EditLayoutAsync(LayoutEditContext context);

    /// <summary>Edit one key's label override; null on cancel, else the answer
    /// (a null <see cref="LabelEditAnswer{T}.Value"/> clears the override).</summary>
    Task<LabelEditAnswer<KeyLabelOverride>?> EditKeyLabelAsync(
        int layerIndex, int keyIndex, string defaultHint, KeyLabelOverride? existing);

    /// <summary>Edit one combo's label override; null on cancel, else the answer.</summary>
    Task<LabelEditAnswer<ComboLabelOverride>?> EditComboLabelAsync(
        int number, string keyPositionsKey, string defaultLabel, string participatingKeys,
        ComboLabelOverride? existing);
}

/// <summary>A saved label dialog: the new override, or null to clear it.</summary>
public sealed record LabelEditAnswer<T>(T? Value) where T : class;
