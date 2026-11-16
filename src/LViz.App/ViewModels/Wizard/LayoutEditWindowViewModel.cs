using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Localization;
using LViz.Core.Layout;
using LViz.Core.Layout.Dtsi;
using LViz.Core.Persistence;

namespace LViz.App.ViewModels;

/// <summary>
/// Everything the edit window needs that it can't discover itself: the saved
/// layout being edited (name, id and device name stay exactly as they are —
/// only the geometry changes here), the key captions to show on the keys, and
/// the folder user layouts live in.
/// </summary>
public sealed record LayoutEditContext(
    UserLayoutFile Layout,
    IReadOnlyList<string> Labels,
    string LayoutsDirectory);

/// <summary>
/// View model behind the stand-alone "Edit layout…" window: one
/// <see cref="LayoutEditorViewModel"/> over the saved layout's keys (labels
/// fixed, split line from the current hands) and explicit save/cancel.
/// <para>
/// Save normalises the edited keys and writes them back over
/// <see cref="UserLayoutStore"/> under the same id, name and device name;
/// reset (the editor's own) goes back to the last saved version because that
/// is what the editor started from. A <c>.dtsi</c> next to the layout is only
/// <i>refreshed</i> — this window never creates one, since it can't tell
/// whether the layout came from a physical-layout dtsi the user maintains.
/// A save failure shows on the window and keeps it open; success closes with
/// the written file.
/// </para>
/// </summary>
public sealed partial class LayoutEditWindowViewModel : ObservableObject
{
    private readonly LayoutEditContext _context;

    public LayoutEditWindowViewModel(LayoutEditContext context)
    {
        _context = context;
        Editor = new LayoutEditorViewModel(
            context.Layout.Keys,
            context.Labels,
            UserLayoutGeometry.InitialSplitX(context.Layout.Keys));
        Title = Loc.Instance.Format("EditLayout_TitleFormat", context.Layout.Name);
        Editor.PropertyChanged += OnEditorPropertyChanged;
    }

    /// <summary>The shared editor over the layout's keys.</summary>
    public LayoutEditorViewModel Editor { get; }

    /// <summary>Window title naming the layout being edited.</summary>
    public string Title { get; }

    /// <summary>Last save failure; set on an IO error, which keeps the window open.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Raised on cancel (null) and on a successful save (the written file).</summary>
    public event Action<UserLayoutFile?>? CloseRequested;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        // Normalised, not the raw edit: the window's own rule is that what
        // lands on disk starts at the origin, like every wizard save.
        var file = _context.Layout with { Keys = UserLayoutGeometry.Normalize(Editor.Keys) };

        // Both files or neither: a failed .dtsi refresh puts the layout back.
        FileRollback? rollback = null;
        try
        {
            rollback = FileRollback.Capture(
            [
                UserLayoutStore.PathFor(file.Id, _context.LayoutsDirectory),
                ZmkPhysicalLayoutWriter.PathFor(file.Id, _context.LayoutsDirectory),
            ]);
            UserLayoutStore.Save(file, _context.LayoutsDirectory);
            // Refresh-only: createIfMissing stays false (see the class remarks).
            ZmkPhysicalLayoutWriter.WriteNextTo(file, _context.LayoutsDirectory, createIfMissing: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            rollback?.Restore();
            ErrorMessage = Loc.Instance.Format("Wizard_SaveFailedFormat", ex.Message);
            return;
        }

        CloseRequested?.Invoke(file);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    private bool CanSave() => Editor.IsModified;

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LayoutEditorViewModel.IsModified))
            SaveCommand.NotifyCanExecuteChanged();
    }
}
