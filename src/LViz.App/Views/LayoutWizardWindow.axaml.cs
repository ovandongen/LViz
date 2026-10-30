using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LViz.App.Localization;
using LViz.App.ViewModels;

namespace LViz.App.Views;

/// <summary>
/// Modal three-step wizard window (source → preview → save) for adding a
/// board that ships no bundled layout. Its <c>DataContext</c> must be a
/// <see cref="LayoutWizardViewModel"/>; the VM closes the dialog by raising
/// <see cref="LayoutWizardViewModel.CloseRequested"/> (null = cancel, a
/// <see cref="LayoutWizardResult"/> = saved), which this code-behind turns
/// into <c>Close(result)</c> — so <c>ShowDialog</c> returns exactly that.
/// The window also supplies the real folder/file pickers the VM's
/// <c>PickFolder</c>/<c>PickFile</c> hooks call.
/// </summary>
public partial class LayoutWizardWindow : Window
{
    private LayoutWizardViewModel? _boundVm;

    public LayoutWizardWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel();
    }

    private void BindViewModel()
    {
        if (_boundVm is { } old)
        {
            old.CloseRequested -= OnCloseRequested;
            _boundVm = null;
        }

        if (DataContext is not LayoutWizardViewModel vm)
            return;

        vm.PickFolder = PickFolderAsync;
        vm.PickFile = PickFileAsync;
        vm.CloseRequested += OnCloseRequested;
        _boundVm = vm;
    }

    private void OnCloseRequested(LayoutWizardResult? result) => Close(result);

    private async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.Instance["Wizard_BrowseFolder"],
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    private async Task<string?> PickFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Instance["Wizard_BrowseFile"],
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.Instance["Wizard_FileType"])
                {
                    // .dtsi/.overlay have no registered UTI on macOS, so a
                    // bare extension filter greys the files out — tell the OS
                    // to treat them as plain text/JSON, which they are.
                    Patterns = ["*.dtsi", "*.overlay", "*.json"],
                    MimeTypes = ["text/plain", "application/json"],
                    AppleUniformTypeIdentifiers = ["public.plain-text", "public.json"],
                },
                FilePickerFileTypes.All,
            ],
        });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_boundVm is { } vm)
            vm.CloseRequested -= OnCloseRequested;
        _boundVm = null;
        base.OnClosed(e);
    }
}
