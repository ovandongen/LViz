using CommunityToolkit.Mvvm.ComponentModel;

namespace LViz.App.ViewModels;

/// <summary>
/// The list panel beside a ring that would need more sectors than it fits
/// (and the keycode list, which always shows). The rows come from a function
/// of the search text, so the panel owns its own search box while the ring
/// view-model owns what the rows hold; <see cref="Refresh"/> re-reads them
/// after anything the source depends on changed.
/// </summary>
public sealed partial class KeyRingPanelViewModel : ObservableObject
{
    private readonly Func<string, IReadOnlyList<KeyRingPanelRow>> _rows;

    /// <summary>The rows as the source produced them, without the highlight.</summary>
    private IReadOnlyList<KeyRingPanelRow> _source = [];

    public KeyRingPanelViewModel(string title, Func<string, IReadOnlyList<KeyRingPanelRow>> rows)
    {
        Title = title;
        _rows = rows;
        Refresh();
    }

    /// <summary>What the panel is listing.</summary>
    public string Title { get; }

    /// <summary>The rows with <see cref="KeyRingPanelRow.IsHighlighted"/> set
    /// on the highlighted row only.</summary>
    public IReadOnlyList<KeyRingPanelRow> Rows { get; private set; } = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _highlightIndex = -1;

    /// <summary>Target of the highlighted row, or null while no row is.</summary>
    public KeyRingTarget? HighlightedTarget =>
        HighlightIndex >= 0 && HighlightIndex < Rows.Count ? Rows[HighlightIndex].Target : null;

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnHighlightIndexChanged(int value) => ApplyHighlight();

    /// <summary>Moves the highlight by <paramref name="delta"/> rows, clamped
    /// to the list; with no rows there is nothing to highlight.</summary>
    public void MoveHighlight(int delta)
    {
        if (_source.Count == 0)
        {
            HighlightIndex = -1;
            return;
        }

        HighlightIndex = Math.Clamp(HighlightIndex + delta, 0, _source.Count - 1);
    }

    /// <summary>Re-reads the rows and puts the highlight on the selected row,
    /// else on the first row, else nowhere.</summary>
    public void Refresh()
    {
        _source = _rows(SearchText);

        var selected = -1;
        for (int i = 0; i < _source.Count; i++)
        {
            if (_source[i].IsSelected)
            {
                selected = i;
                break;
            }
        }

        HighlightIndex = selected >= 0 ? selected : _source.Count > 0 ? 0 : -1;
        ApplyHighlight();
    }

    private void ApplyHighlight()
    {
        Rows = [.. _source.Select((row, index) => row with { IsHighlighted = index == HighlightIndex })];
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(HighlightedTarget));
    }
}
