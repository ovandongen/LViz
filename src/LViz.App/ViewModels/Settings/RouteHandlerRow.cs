using CommunityToolkit.Mvvm.ComponentModel;

namespace LViz.App.ViewModels;

/// <summary>One candidate handler within a route, with an enable/disable toggle.</summary>
public sealed partial class RouteHandlerRow : ObservableObject
{
    private readonly Action _onChanged;

    public RouteHandlerRow(string targetKey, string displayName, bool routed, Action onChanged)
    {
        TargetKey = targetKey;
        DisplayName = displayName;
        _routed = routed;
        _onChanged = onChanged;
    }

    public string TargetKey { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _routed;

    partial void OnRoutedChanged(bool value) => _onChanged();
}
