using LViz.App.Services;
using LViz.App.ViewModels;
using LViz.Core.Layout;
using LViz.Core.Settings;

namespace LViz.Tests;

/// <summary>
/// A recording <see cref="IMainWindowShell"/> for view-model tests. Every call
/// is counted or recorded; the dialogs answer "the user backed out" (null) until
/// a test sets an answer.
/// </summary>
internal sealed class FakeMainWindowShell : IMainWindowShell
{
    public int QuitCount { get; private set; }
    public int ShowWindowCount { get; private set; }
    public int OpenSettingsCount { get; private set; }
    public int ShortcutsCount { get; private set; }
    public int GitHubPickerCount { get; private set; }

    public string? PickedKeymapPath { get; set; }
    public bool ClipboardAvailable { get; set; } = true;
    public Exception? ClipboardFailure { get; set; }
    public LayoutWizardResult? WizardAnswer { get; set; }
    public GitHubOpenSelection? GitHubAnswer { get; set; }
    public UserLayoutFile? EditLayoutAnswer { get; set; }
    public LabelEditAnswer<KeyLabelOverride>? KeyLabelAnswer { get; set; }
    public LabelEditAnswer<ComboLabelOverride>? ComboLabelAnswer { get; set; }

    public List<string> CopiedTexts { get; } = [];
    public List<LayoutWizardContext> WizardContexts { get; } = [];
    public List<LayoutEditContext> EditLayoutContexts { get; } = [];
    public List<(int Layer, int Key, string Hint, KeyLabelOverride? Existing)> KeyLabelRequests { get; } = [];
    public List<(string Key, string DefaultLabel, ComboLabelOverride? Existing)> ComboLabelRequests { get; } = [];

    public void Quit() => QuitCount++;

    public void ShowWindow() => ShowWindowCount++;

    public Task<string?> PickKeymapFileAsync() => Task.FromResult(PickedKeymapPath);

    public Task<bool> CopyTextAsync(string text)
    {
        if (ClipboardFailure is not null) throw ClipboardFailure;
        if (!ClipboardAvailable) return Task.FromResult(false);
        CopiedTexts.Add(text);
        return Task.FromResult(true);
    }

    public void OpenSettings() => OpenSettingsCount++;

    public Task ShowShortcutsAsync()
    {
        ShortcutsCount++;
        return Task.CompletedTask;
    }

    public Task<LayoutWizardResult?> RunLayoutWizardAsync(LayoutWizardContext context)
    {
        WizardContexts.Add(context);
        return Task.FromResult(WizardAnswer);
    }

    public Task<GitHubOpenSelection?> PickGitHubKeymapAsync(Func<IReadOnlyCollection<string>> attachedHidNames)
    {
        GitHubPickerCount++;
        return Task.FromResult(GitHubAnswer);
    }

    public Task<UserLayoutFile?> EditLayoutAsync(LayoutEditContext context)
    {
        EditLayoutContexts.Add(context);
        return Task.FromResult(EditLayoutAnswer);
    }

    public Task<LabelEditAnswer<KeyLabelOverride>?> EditKeyLabelAsync(
        int layerIndex, int keyIndex, string defaultHint, KeyLabelOverride? existing)
    {
        KeyLabelRequests.Add((layerIndex, keyIndex, defaultHint, existing));
        return Task.FromResult(KeyLabelAnswer);
    }

    public Task<LabelEditAnswer<ComboLabelOverride>?> EditComboLabelAsync(
        int number, string keyPositionsKey, string defaultLabel, string participatingKeys,
        ComboLabelOverride? existing)
    {
        ComboLabelRequests.Add((keyPositionsKey, defaultLabel, existing));
        return Task.FromResult(ComboLabelAnswer);
    }
}
