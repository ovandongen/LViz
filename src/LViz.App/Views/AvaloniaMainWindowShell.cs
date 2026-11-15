using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
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
/// Settings, module-setup and flash view-models arrive as factories and are
/// built only when their window opens.
/// </summary>
internal sealed class AvaloniaMainWindowShell : IMainWindowShell
{
    private readonly MainWindowLifecycle _lifecycle;
    private readonly IGitHubKeymapService _gitHub;
    private readonly IHostActionExecutor _hostActions;
    private readonly Func<SettingsViewModel> _createSettings;
    private readonly Func<ModuleSetupContext, ModuleSetupViewModel> _createModuleSetup;
    private readonly Func<FlashContext, FlashViewModel> _createFlash;

    // Single non-modal Settings window. Re-clicking the toolbar button brings
    // the existing window to front rather than spawning a new one.
    private SettingsWindow? _settingsWindow;

    public AvaloniaMainWindowShell(
        MainWindowLifecycle lifecycle,
        IGitHubKeymapService gitHub,
        IHostActionExecutor hostActions,
        Func<SettingsViewModel> createSettings,
        Func<ModuleSetupContext, ModuleSetupViewModel> createModuleSetup,
        Func<FlashContext, FlashViewModel> createFlash)
    {
        _lifecycle = lifecycle;
        _gitHub = gitHub;
        _hostActions = hostActions;
        _createSettings = createSettings;
        _createModuleSetup = createModuleSetup;
        _createFlash = createFlash;
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
        var settingsVm = _createSettings();
        var preferences = settingsVm.MainViewModel.Preferences;
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
                window.Topmost = preferences.IsAlwaysOnTop;
        }
        preferences.PropertyChanged += SyncTopmost;
        window.Closed += (_, _) =>
        {
            preferences.PropertyChanged -= SyncTopmost;
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
    public async Task<bool> RunModuleSetupAsync(ModuleSetupContext context)
    {
        var owner = Owner;
        var dlgVm = _createModuleSetup(context);
        // The Done step opens the config's GitHub Actions page.
        dlgVm.OpenUrl = _hostActions.Launch;
        // The check step reads (and for GitHub fetches) the config straight away.
        _ = dlgVm.StartAsync();
        var dlg = new ModuleSetupWindow
        {
            DataContext = dlgVm,
            Topmost = owner.Topmost,
        };
        // Closing with the window's ✕ yields the default bool — false.
        return await dlg.ShowDialog<bool>(owner);
    }

    /// <inheritdoc />
    public async Task<bool> RunFlashAsync(FlashContext context)
    {
        var owner = Owner;
        var dlgVm = _createFlash(context);
        dlgVm.OpenUrl = _hostActions.Launch;
        dlgVm.CopyToClipboard = async text => await CopyTextAsync(text);
        dlgVm.PickUf2File = PickUf2FileAsync;
        // The source step reads the config and looks the build up straight away.
        _ = dlgVm.StartAsync();
        var dlg = new FlashWindow
        {
            DataContext = dlgVm,
            Topmost = owner.Topmost,
        };
        // Closing with the window's ✕ yields the default bool — false.
        return await dlg.ShowDialog<bool>(owner);
    }

    /// <inheritdoc />
    public async Task<string?> PickUf2FileAsync()
    {
        var file = await Owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Instance["Flash_PickFileTitle"],
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.Instance["Flash_FileTypeUf2"])
                {
                    // .uf2 has no registered UTI on macOS, so a bare
                    // extension filter greys the file out. The image is
                    // plain binary data — tell the OS to treat it as such.
                    Patterns = ["*.uf2"],
                    MimeTypes = ["application/octet-stream"],
                    AppleUniformTypeIdentifiers = ["public.data"],
                },
            ],
        });
        return file.Count > 0 ? file[0].TryGetLocalPath() : null;
    }

    /// <inheritdoc />
    public async Task<GitHubOpenSelection?> PickGitHubKeymapAsync(Func<IReadOnlyCollection<string>> attachedHidNames)
    {
        var owner = Owner;
        var ghVm = new GitHubOpenViewModel(_gitHub)
        {
            OpenUrl = _hostActions.Launch,
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
