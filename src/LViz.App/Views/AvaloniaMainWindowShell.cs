using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using LViz.App.Localization;
using LViz.App.Services;
using LViz.App.Services.GitHub;
using LViz.App.ViewModels;
using LViz.Core.Layout;
using LViz.Core.Settings;

namespace LViz.App.Views;

/// <summary>
/// The real <see cref="IMainWindowShell"/>: every window and dialog the main
/// view-model opens, modal over (or beside) the application's main window. The
/// owner is looked up at call time (<see cref="MainWindowOwner"/>); the
/// container is only asked for the Settings and GitHub pieces when one opens.
/// </summary>
internal sealed class AvaloniaMainWindowShell : IMainWindowShell
{
    private readonly IServiceProvider _services;
    private readonly MainWindowLifecycle _lifecycle;

    // Single non-modal Settings window. Re-clicking the toolbar button brings
    // the existing window to front rather than spawning a new one.
    private SettingsWindow? _settingsWindow;

    public AvaloniaMainWindowShell(IServiceProvider services, MainWindowLifecycle lifecycle)
    {
        _services = services;
        _lifecycle = lifecycle;
    }

    private static MainWindow Owner => MainWindowOwner.Current;

    /// <inheritdoc />
    public void Quit() => _lifecycle.QuitAfterConfirm(() => Environment.Exit(0));

    /// <inheritdoc />
    public void ShowWindow() => _lifecycle.Show();

    /// <inheritdoc />
    public async Task<string?> PickKeymapFileAsync()
    {
        var file = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Instance["LoadDialog_Title"],
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.Instance["LoadDialog_FileType"])
                {
                    // .keymap has no registered UTI on macOS, so a
                    // bare extension filter greys the file out. Tell
                    // the OS to treat it as plain text — the file is.
                    Patterns = ["*.keymap"],
                    MimeTypes = ["text/plain"],
                    AppleUniformTypeIdentifiers = ["public.plain-text"],
                },
                // Moergo Layout Editor exports — accepted only on the
                // Glove80 / GO60 profiles (gated in KeymapStateService).
                new FilePickerFileType(Loc.Instance["LoadDialog_FileTypeMoergoJson"])
                {
                    Patterns = ["*.json"],
                    MimeTypes = ["application/json"],
                    AppleUniformTypeIdentifiers = ["public.json"],
                },
                FilePickerFileTypes.All,
            ],
        });
        return file.Count > 0 ? file[0].Path.LocalPath : null;
    }

    /// <inheritdoc />
    public async Task<bool> CopyTextAsync(string text)
    {
        var owner = Owner;
        var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard ?? owner.Clipboard;
        if (clipboard is null) return false;
        // SetTextAsync silently no-ops on Avalonia 11.3 macOS
        // when invoked from a borderless transparent window.
        // The newer SetDataAsync + DataTransfer path writes reliably.
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(DataFormat.Text, text));
        await clipboard.SetDataAsync(transfer);
        return true;
    }

    /// <inheritdoc />
    public void OpenSettings()
    {
        if (_settingsWindow is { } existing && existing.IsVisible)
        {
            existing.Activate();
            return;
        }
        var owner = Owner;
        var viewModel = _services.GetRequiredService<MainWindowViewModel>();
        var settingsVm = _services.GetRequiredService<SettingsViewModel>();
        var window = new SettingsWindow
        {
            DataContext = settingsVm,
            Topmost = owner.Topmost,
        };
        _settingsWindow = window;
        // Keep the settings window above the main window when the
        // user has the main window pinned, otherwise it opens behind
        // and looks lost.
        void SyncTopmost(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WindowPreferencesViewModel.IsAlwaysOnTop))
                window.Topmost = viewModel.Preferences.IsAlwaysOnTop;
        }
        viewModel.Preferences.PropertyChanged += SyncTopmost;
        window.Closed += (_, _) =>
        {
            viewModel.Preferences.PropertyChanged -= SyncTopmost;
            settingsVm.Dispose();
            _settingsWindow = null;
        };
        window.Show(owner);
    }

    /// <inheritdoc />
    /// <remarks>Keys are spelled for the host OS.</remarks>
    public async Task ShowShortcutsAsync()
    {
        var dlg = new ShortcutsWindow
        {
            DataContext = new ShortcutsWindowViewModel(OperatingSystem.IsMacOS()),
        };
        await dlg.ShowDialog(Owner);
    }

    /// <inheritdoc />
    public async Task<LayoutWizardResult?> RunLayoutWizardAsync(LayoutWizardContext context)
    {
        var owner = Owner;
        var dlgVm = new LayoutWizardViewModel(context);
        // A GitHub keymap's repo is scanned straight away.
        _ = dlgVm.StartAsync();
        var dlg = new LayoutWizardWindow
        {
            DataContext = dlgVm,
            Topmost = owner.Topmost,
        };
        return await dlg.ShowDialog<LayoutWizardResult?>(owner);
    }

    /// <inheritdoc />
    public async Task<GitHubOpenSelection?> PickGitHubKeymapAsync(Func<IReadOnlyCollection<string>> attachedHidNames)
    {
        var owner = Owner;
        var ghVm = new GitHubOpenViewModel(_services.GetRequiredService<IGitHubKeymapService>())
        {
            OpenUrl = url => _services.GetRequiredService<IHostActionExecutor>().Launch(url),
            AttachedHidNames = attachedHidNames,
        };
        var dlg = new GitHubOpenWindow
        {
            DataContext = ghVm,
            Topmost = owner.Topmost,
        };
        return await dlg.ShowDialog<GitHubOpenSelection?>(owner);
    }

    /// <inheritdoc />
    public async Task<UserLayoutFile?> EditLayoutAsync(LayoutEditContext context)
    {
        var owner = Owner;
        var dlg = new LayoutEditWindow
        {
            DataContext = new LayoutEditWindowViewModel(context),
            Topmost = owner.Topmost,
        };
        return await dlg.ShowDialog<UserLayoutFile?>(owner);
    }

    /// <inheritdoc />
    public async Task<LabelEditAnswer<KeyLabelOverride>?> EditKeyLabelAsync(
        int layerIndex, int keyIndex, string defaultHint, KeyLabelOverride? existing)
    {
        var dlg = new EditKeyLabelDialog
        {
            DataContext = new EditKeyLabelDialogViewModel(layerIndex, keyIndex, defaultHint, existing),
        };
        var result = await dlg.ShowDialog<EditKeyLabelDialog.Result>(Owner);
        return result.Saved ? new LabelEditAnswer<KeyLabelOverride>(result.Value) : null;
    }

    /// <inheritdoc />
    public async Task<LabelEditAnswer<ComboLabelOverride>?> EditComboLabelAsync(
        int number, string keyPositionsKey, string defaultLabel, string participatingKeys,
        ComboLabelOverride? existing)
    {
        var dlg = new EditComboLabelDialog
        {
            DataContext = new EditComboLabelDialogViewModel(
                number, keyPositionsKey, defaultLabel, participatingKeys, existing),
        };
        var result = await dlg.ShowDialog<EditComboLabelDialog.Result>(Owner);
        return result.Saved ? new LabelEditAnswer<ComboLabelOverride>(result.Value) : null;
    }
}
