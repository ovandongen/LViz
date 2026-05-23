using CommunityToolkit.Mvvm.ComponentModel;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// Bindable state for <see cref="Views.EditKeyLabelDialog"/>. Initialised
/// from the current <see cref="KeyLabelOverride"/> (if any) plus the
/// formatter-computed default label as a read-only hint, and produces an
/// updated <see cref="KeyLabelOverride"/> on save. Empty save collapses to
/// "no override" via <see cref="KeyLabelOverride.IsEmpty"/>.
/// </summary>
public sealed partial class EditKeyLabelDialogViewModel : ObservableObject
{
    public int LayerIndex { get; }
    public int KeyIndex { get; }

    /// <summary>Read-only hint of what the parser-computed default looks like.</summary>
    public string DefaultLabelHint { get; }

    /// <summary>Whether there's currently an override saved for this slot — drives the "Clear override" button.</summary>
    public bool HasExistingOverride { get; }

    [ObservableProperty] private string _mainLabel = "";
    [ObservableProperty] private string _subscript = "";
    [ObservableProperty] private string _topLeftBadge = "";
    [ObservableProperty] private string _icon = "";
    [ObservableProperty] private double? _fontSize;
    [ObservableProperty] private bool _bold;

    public EditKeyLabelDialogViewModel(int layerIndex, int keyIndex, string defaultLabelHint, KeyLabelOverride? existing)
    {
        LayerIndex = layerIndex;
        KeyIndex = keyIndex;
        DefaultLabelHint = defaultLabelHint;
        HasExistingOverride = existing is not null;

        if (existing is not null)
        {
            _mainLabel = existing.MainLabel;
            _subscript = existing.Subscript;
            _topLeftBadge = existing.TopLeftBadge;
            _icon = existing.Icon;
            _fontSize = existing.FontSize;
            _bold = existing.Bold;
        }
    }

    /// <summary>Snapshot of the current edits as a <see cref="KeyLabelOverride"/>.</summary>
    public KeyLabelOverride ToOverride() => new()
    {
        MainLabel = MainLabel ?? "",
        Subscript = Subscript ?? "",
        TopLeftBadge = TopLeftBadge ?? "",
        Icon = Icon ?? "",
        FontSize = FontSize,
        Bold = Bold,
    };

    public void ResetFontSize() => FontSize = null;
    public void ClearIcon() => Icon = "";
}
