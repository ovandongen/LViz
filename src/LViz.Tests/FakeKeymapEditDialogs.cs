using LViz.App.Services;
using LViz.App.ViewModels;
using LViz.Core.Models;

namespace LViz.Tests;

/// <summary>
/// A recording <see cref="IKeymapEditDialogs"/> for view-model tests. Each
/// dialog has a settable handler; the defaults are the quiet answers (review:
/// go ahead with the offered commit message; conflict: keep editing; the two
/// notices: acknowledged), while the dialogs a test must opt into — key
/// editor, combo, rename — throw if opened unexpectedly. Every request is
/// recorded.
/// </summary>
internal sealed class FakeKeymapEditDialogs : IKeymapEditDialogs
{
    public Func<KeyDialogRequest, Task<KeyBinding?>> KeyEditor { get; set; } =
        _ => throw new InvalidOperationException("The key editor was not expected to open.");

    public Func<ComboDialogRequest, Task<ZmkCombo?>> Combo { get; set; } =
        _ => throw new InvalidOperationException("The combo dialog was not expected to open.");

    public Func<RenameLayerRequest, Task<string?>> Rename { get; set; } =
        _ => throw new InvalidOperationException("The rename dialog was not expected to open.");

    public Func<LayerDeleteBlockedRequest, Task> DeleteBlocked { get; set; } = _ => Task.CompletedTask;

    public Func<KeymapReviewRequest, Task<KeymapReviewAnswer?>> Review { get; set; } =
        request => Task.FromResult<KeymapReviewAnswer?>(new KeymapReviewAnswer(request.CommitMessage));

    public Func<KeymapConflictRequest, Task<KeymapSaveConflictChoice>> Conflict { get; set; } =
        _ => Task.FromResult(KeymapSaveConflictChoice.Cancel);

    public Func<IReadOnlyList<string>, Task> Notices { get; set; } = _ => Task.CompletedTask;

    public List<KeyDialogRequest> KeyEditorRequests { get; } = [];
    public List<ComboDialogRequest> ComboRequests { get; } = [];
    public List<RenameLayerRequest> RenameRequests { get; } = [];
    public List<LayerDeleteBlockedRequest> DeleteBlockedRequests { get; } = [];
    public List<KeymapReviewRequest> ReviewRequests { get; } = [];
    public List<KeymapConflictRequest> ConflictRequests { get; } = [];
    public List<IReadOnlyList<string>> NoticeLines { get; } = [];

    public Task<KeyBinding?> EditKeyAsync(KeyDialogRequest request)
    {
        KeyEditorRequests.Add(request);
        return KeyEditor(request);
    }

    public Task<ZmkCombo?> EditComboAsync(ComboDialogRequest request)
    {
        ComboRequests.Add(request);
        return Combo(request);
    }

    public Task<string?> RenameLayerAsync(RenameLayerRequest request)
    {
        RenameRequests.Add(request);
        return Rename(request);
    }

    public Task ShowLayerDeleteBlockedAsync(LayerDeleteBlockedRequest request)
    {
        DeleteBlockedRequests.Add(request);
        return DeleteBlocked(request);
    }

    public Task<KeymapReviewAnswer?> ReviewChangesAsync(KeymapReviewRequest request)
    {
        ReviewRequests.Add(request);
        return Review(request);
    }

    public Task<KeymapSaveConflictChoice> AskSaveConflictAsync(KeymapConflictRequest request)
    {
        ConflictRequests.Add(request);
        return Conflict(request);
    }

    public Task ShowLayerSettingsNoticesAsync(IReadOnlyList<string> lines)
    {
        NoticeLines.Add(lines);
        return Notices(lines);
    }
}
