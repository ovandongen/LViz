using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LViz.App.ViewModels;

/// <summary>
/// One tab in the layer-selector bar. The parent <see cref="MainWindowViewModel"/>
/// passes a <c>selectAction</c> so the VM doesn't need to know its owner.
/// <see cref="TabColor"/> is observable so per-layer color overrides can
/// repaint tabs in place without rebuilding the list (which would steal focus).
/// </summary>
public partial class LayerViewModel : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _tabColor;

    public int Index { get; }
    public string DisplayName { get; }
    public IRelayCommand SelectCommand { get; }

    public LayerViewModel(int index, string displayName, string tabColor, Action<int> selectAction)
    {
        Index = index;
        DisplayName = displayName;
        _tabColor = tabColor;
        SelectCommand = new RelayCommand(() => selectAction(index));
    }
}
