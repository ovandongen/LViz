namespace LViz.App.Services;

/// <summary>
/// What a view-model asks of the desktop: the browser, the clipboard and the
/// <c>.uf2</c> file dialog. The shell implements it; tests fake it.
/// </summary>
public interface IDesktopInteraction
{
    /// <summary>Opens a URL in the user's browser.</summary>
    void OpenUrl(string url);

    /// <summary>Put text on the clipboard; false when no clipboard is available.</summary>
    Task<bool> CopyTextAsync(string text);

    /// <summary>File picker for a .uf2 firmware file; null when cancelled.</summary>
    Task<string?> PickUf2FileAsync();
}
