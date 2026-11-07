using LViz.App.ViewModels;
using LViz.Core.Layout;
using LViz.Core.Settings;

namespace LViz.App.Services;

/// <summary>
/// The shell of a <see cref="MainWindowViewModel"/> built without a window
/// host (view-model tests that never reach a window). Every call fails loudly:
/// reaching a window without a host is a wiring bug, never a silent "nothing
/// happens".
/// </summary>
internal sealed class UnavailableMainWindowShell : IMainWindowShell
{
    private static InvalidOperationException Unavailable() =>
        new("No IMainWindowShell was supplied to the MainWindowViewModel.");

    public void Quit() => throw Unavailable();

    public void ShowWindow() => throw Unavailable();

    public Task<string?> PickKeymapFileAsync() => throw Unavailable();

    public Task<bool> CopyTextAsync(string text) => throw Unavailable();

    public void OpenSettings() => throw Unavailable();

    public Task ShowShortcutsAsync() => throw Unavailable();

    public Task<LayoutWizardResult?> RunLayoutWizardAsync(LayoutWizardContext context) => throw Unavailable();

    public Task<GitHubOpenSelection?> PickGitHubKeymapAsync(Func<IReadOnlyCollection<string>> attachedHidNames) =>
        throw Unavailable();

    public Task<UserLayoutFile?> EditLayoutAsync(LayoutEditContext context) => throw Unavailable();

    public Task<LabelEditAnswer<KeyLabelOverride>?> EditKeyLabelAsync(
        int layerIndex, int keyIndex, string defaultHint, KeyLabelOverride? existing) => throw Unavailable();

    public Task<LabelEditAnswer<ComboLabelOverride>?> EditComboLabelAsync(
        int number, string keyPositionsKey, string defaultLabel, string participatingKeys,
        ComboLabelOverride? existing) => throw Unavailable();
}
