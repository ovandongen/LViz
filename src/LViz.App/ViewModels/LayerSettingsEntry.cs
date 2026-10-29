using CommunityToolkit.Mvvm.ComponentModel;

namespace LViz.App.ViewModels;

/// <summary>
/// One row in the Settings window's per-layer table — layer name + color
/// picker + shown/hidden toggle. Wraps <see cref="LayerColorEntry"/> so the
/// Settings UI binds to a uniform shape. Flipping <see cref="IsShown"/>
/// writes straight through to the main VM's setter so persistence and the
/// main-form tab bar stay in sync.
/// </summary>
public sealed partial class LayerSettingsEntry : ObservableObject
{
    private readonly Action<int, bool> _setShown;

    [ObservableProperty] private bool _isShown;

    public int Index { get; }
    public string DisplayName { get; }
    public LayerColorEntry Color { get; }

    /// <summary>Layer 0 (base) is always shown and cannot be hidden.</summary>
    public bool CanHide => Index != 0;

    public LayerSettingsEntry(LayerColorEntry color, bool isShown, Action<int, bool> setShown)
    {
        Index = color.Index;
        DisplayName = color.DisplayName;
        Color = color;
        _isShown = isShown;
        _setShown = setShown;
    }

    partial void OnIsShownChanged(bool value) => _setShown(Index, value);
}
