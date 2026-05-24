using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Localization;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

public sealed partial class EditKeyLabelDialogViewModel : ObservableObject
{
    public int LayerIndex { get; }
    public int KeyIndex { get; }
    public string DefaultLabelHint { get; }
    public bool HasExistingOverride { get; }

    public string LayerKeyHeader => Loc.Instance.Format("EditKey_LayerKeyFormat", LayerIndex, KeyIndex);
    public string DefaultHintLine => Loc.Instance.Format("EditKey_DefaultFormat", DefaultLabelHint);

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

    public KeyLabelOverride ToOverride() => new()
    {
        MainLabel = MainLabel,
        Subscript = Subscript,
        TopLeftBadge = TopLeftBadge,
        Icon = Icon,
        FontSize = FontSize,
        Bold = Bold,
    };

    public void ResetFontSize() => FontSize = null;
    public void ClearIcon() => Icon = "";
}
