using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Services;

namespace LViz.App.ViewModels;

/// <summary>
/// Bindable state for <see cref="Views.IconPickerDialog"/>. Holds the full
/// <see cref="FontAwesomeCatalog"/> filtered live as the user types, and
/// the currently focused selection. Catalog access is lazy — first access
/// to <see cref="Entries"/> triggers the one-time JSON parse inside
/// <c>FontAwesomeCatalog</c>.
/// </summary>
public sealed partial class IconPickerViewModel : ObservableObject
{
    /// <summary>Initial icon to highlight, if any. Empty for "no current selection".</summary>
    public string InitialIcon { get; }

    /// <summary>The visible (filtered) catalog entries, rebuilt on every <see cref="Query"/> change.</summary>
    public ObservableCollection<FontAwesomeCatalog.Entry> Entries { get; } = new();

    /// <summary>Total number of entries in the catalog — useful for the "x of N icons" hint.</summary>
    public int TotalCount { get; }

    /// <summary>Catalog load failure (if any) — surfaced as a status line in the dialog.</summary>
    public string? LoadError { get; }

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private FontAwesomeCatalog.Entry? _selected;

    public IconPickerViewModel(string initialIcon)
    {
        InitialIcon = initialIcon ?? "";
        try
        {
            TotalCount = FontAwesomeCatalog.All.Count;
            ApplyFilter("");
            if (!string.IsNullOrEmpty(InitialIcon))
                Selected = FontAwesomeCatalog.All.FirstOrDefault(e => e.Name == InitialIcon);
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
        }
    }

    partial void OnQueryChanged(string value) => ApplyFilter(value);

    private void ApplyFilter(string query)
    {
        Entries.Clear();
        foreach (var e in FontAwesomeCatalog.Filter(query))
            Entries.Add(e);
    }
}
