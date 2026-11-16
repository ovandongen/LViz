using LViz.App.Services;

namespace LViz.Tests;

/// <summary>
/// A recording <see cref="IDesktopInteraction"/>: every opened URL and copied
/// text is collected, the file picker answers with <see cref="Uf2Path"/>, and
/// <see cref="CopyResult"/> decides whether the copy succeeds.
/// </summary>
internal sealed class FakeDesktopInteraction : IDesktopInteraction
{
    public List<string> OpenedUrls { get; } = [];
    public List<string> CopiedTexts { get; } = [];

    /// <summary>What <see cref="PickUf2FileAsync"/> returns; null = cancelled.</summary>
    public string? Uf2Path { get; set; }

    /// <summary>False stands for "no clipboard available": nothing is recorded.</summary>
    public bool CopyResult { get; set; } = true;

    public void OpenUrl(string url) => OpenedUrls.Add(url);

    public Task<bool> CopyTextAsync(string text)
    {
        if (!CopyResult) return Task.FromResult(false);
        CopiedTexts.Add(text);
        return Task.FromResult(true);
    }

    public Task<string?> PickUf2FileAsync() => Task.FromResult(Uf2Path);
}
