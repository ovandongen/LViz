namespace LViz.App.ViewModels;

/// <summary>
/// One row of the recents flyout: the stored path split into
/// <see cref="FileName"/> (the row label) and <see cref="Folder"/> (the
/// dimmed second line), plus the two facts the row renders from —
/// <see cref="Exists"/> (a stale row greys out; clicking it prunes the entry
/// instead of loading) and <see cref="IsCurrent"/> (a check mark on the
/// loaded keymap). Rows are snapshots: the view model rebuilds the whole
/// list on every read, so nothing here watches the disk on its own.
/// </summary>
/// <param name="Path">Full keymap path exactly as persisted in settings.</param>
/// <param name="FileName">Final path segment, shown as the row label.</param>
/// <param name="Folder">Directory part of the path ("" when there is none),
/// shown dimmed under the label.</param>
/// <param name="Exists">Whether the file was on disk when the list was built.</param>
/// <param name="IsCurrent">True when <paramref name="Path"/> equals the
/// currently loaded keymap's path (ordinal).</param>
public sealed record RecentKeymapItem(
    string Path,
    string FileName,
    string Folder,
    bool Exists,
    bool IsCurrent);
