using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Services;
using LViz.Core.GitHub;

namespace LViz.App.ViewModels;

/// <summary>
/// The recents flyout of the load menu: the active profile's recent keymaps in
/// flyout-ready shape plus the row/clear/refresh commands. Bound directly from
/// the main window as <c>Recents</c>. Persistence is <see cref="KeymapLibrary"/>;
/// loading a row is the main view-model's job, reached through the callbacks.
/// </summary>
public sealed class RecentKeymapsViewModel : ObservableObject
{
    private readonly KeymapLibrary _library;
    private readonly Func<string> _profileId;
    private readonly Func<string?> _loadedEntry;
    private readonly Action<GitHubKeymapRef> _openGitHub;
    private readonly Action<string> _openLocal;
    private readonly Action<string> _removed;

    internal RecentKeymapsViewModel(
        KeymapLibrary library,
        Func<string> profileId,
        Func<string?> loadedEntry,
        Action<GitHubKeymapRef> openGitHub,
        Action<string> openLocal,
        Action<string> removed)
    {
        _library = library;
        _profileId = profileId;
        _loadedEntry = loadedEntry;
        _openGitHub = openGitHub;
        _openLocal = openLocal;
        _removed = removed;
        RefreshCommand = new RelayCommand(Refresh);
        OpenCommand = new RelayCommand<RecentKeymapItem>(Open);
        ClearCommand = new RelayCommand(Clear);
    }

    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand<RecentKeymapItem> OpenCommand { get; }
    public IRelayCommand ClearCommand { get; }

    /// <summary>
    /// Recent keymap entries for the active profile, most-recent-first. Each
    /// read re-loads settings, so the list is a snapshot, not a live view.
    /// </summary>
    public IReadOnlyList<string> Entries => _library.RecentsFor(_profileId());

    /// <summary>True when the active profile has at least one recent keymap.</summary>
    public bool HasAny => Entries.Count > 0;

    /// <summary>
    /// <see cref="Entries"/> in flyout-ready shape: one item per entry in the
    /// same order. A <see cref="GitHubKeymapRef"/> entry renders as a GitHub row
    /// (repo · branch as the folder line, never greyed out). Local entries are
    /// stamped at read time with whether the file is still on disk. Each row's
    /// IsCurrent compares the entry with the loaded keymap's settings entry (a
    /// GitHub keymap's <em>file</em> lives in the cache, so the loaded path
    /// can't answer that). Snapshot, rebuilt on every read.
    /// </summary>
    public IReadOnlyList<RecentKeymapItem> Items
    {
        get
        {
            var current = _loadedEntry();
            var entries = Entries;
            var items = new List<RecentKeymapItem>(entries.Count);
            foreach (var entry in entries)
            {
                if (GitHubKeymapRef.TryParse(entry, out var keymap))
                {
                    // A GitHub row is never greyed out: "missing" has no
                    // meaning for a repo path, and the cache may be cold.
                    items.Add(new RecentKeymapItem(
                        entry,
                        keymap.FileName,
                        $"{keymap.RepoFullName} · {keymap.Branch}",
                        Exists: true,
                        IsCurrent: entry == current,
                        IsGitHub: true));
                    continue;
                }

                items.Add(new RecentKeymapItem(
                    entry,
                    Path.GetFileName(entry),
                    Path.GetDirectoryName(entry) ?? "",
                    _library.FileExists(entry),
                    string.Equals(entry, current, StringComparison.Ordinal)));
            }
            return items;
        }
    }

    /// <summary>
    /// Raises all three members so an open flyout refreshes in one step. Called
    /// after every load (success or failure), booking and profile switch.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Entries));
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(HasAny));
    }

    /// <summary>
    /// Row click: a GitHub row re-opens through the network (cache fallback
    /// offline) and is never pruned on failure; a local row loads a still-present
    /// keymap through the normal path, or — when its file has gone missing —
    /// prunes it. The disk is re-checked at click time because
    /// <see cref="RecentKeymapItem.Exists"/> is only a snapshot.
    /// </summary>
    private void Open(RecentKeymapItem? item)
    {
        if (item is null) return;

        if (GitHubKeymapRef.TryParse(item.Path, out var keymap))
        {
            _openGitHub(keymap);
            return;
        }

        if (_library.FileExists(item.Path))
        {
            _openLocal(item.Path);
            return;
        }

        _library.RemoveRecent(_profileId(), item.Path);
        Refresh();
        _removed(item.FileName);
    }

    private void Clear()
    {
        if (!HasAny) return;
        _library.ClearRecents(_profileId());
        Refresh();
    }
}
