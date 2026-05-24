using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Localization;
using LViz.App.Services;

namespace LViz.App.ViewModels;

public sealed partial class IconPickerViewModel : ObservableObject
{
    // Fits the fixed 640x560 dialog: inner 608px / (72 button + 4 margin) = 8.
    private const int IconsPerRow = 8;

    public string InitialIcon { get; }
    public ObservableCollection<IReadOnlyList<FontAwesomeCatalog.Entry>> Rows { get; } = new();
    public int TotalCount { get; }

    [ObservableProperty] private string _query = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedText))]
    private FontAwesomeCatalog.Entry? _selected;

    [ObservableProperty] private string _countText = "";

    public string SelectedText =>
        Loc.Instance.Format("IconPicker_SelectedFormat",
            Selected?.Name ?? Loc.Instance["IconPicker_None"]);

    public IconPickerViewModel(string initialIcon)
    {
        InitialIcon = initialIcon;
        TotalCount = FontAwesomeCatalog.All.Count;
        ApplyFilter("");
        if (!string.IsNullOrEmpty(InitialIcon))
            Selected = FontAwesomeCatalog.All.FirstOrDefault(e => e.Name == InitialIcon);
    }

    partial void OnQueryChanged(string value) => ApplyFilter(value);

    private void ApplyFilter(string query)
    {
        Rows.Clear();
        int count = 0;
        var row = new List<FontAwesomeCatalog.Entry>(IconsPerRow);
        foreach (var e in FontAwesomeCatalog.Filter(query))
        {
            row.Add(e);
            count++;
            if (row.Count == IconsPerRow)
            {
                Rows.Add(row);
                row = new List<FontAwesomeCatalog.Entry>(IconsPerRow);
            }
        }
        if (row.Count > 0) Rows.Add(row);
        CountText = Loc.Instance.Format("IconPicker_CountFormat", count, TotalCount);
    }
}
