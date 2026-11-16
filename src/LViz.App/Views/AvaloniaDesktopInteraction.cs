using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LViz.App.Localization;
using LViz.App.Services;

namespace LViz.App.Views;

/// <summary>
/// The real <see cref="IDesktopInteraction"/>: the browser through the host
/// executor, the clipboard and the file dialog over the main window, which is
/// looked up at call time (<see cref="MainWindowOwner"/>).
/// </summary>
internal sealed class AvaloniaDesktopInteraction(IHostActionExecutor hostActions) : IDesktopInteraction
{
    /// <inheritdoc />
    public void OpenUrl(string url) => hostActions.Launch(url);

    /// <inheritdoc />
    public async Task<bool> CopyTextAsync(string text)
    {
        var owner = MainWindowOwner.Current;
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
    public async Task<string?> PickUf2FileAsync()
    {
        var file = await MainWindowOwner.Current.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
}
