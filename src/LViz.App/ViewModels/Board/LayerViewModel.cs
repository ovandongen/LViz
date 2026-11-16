using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LViz.App.ViewModels;

/// <summary>
/// The per-layer edit actions a tab gets while a keymap edit is open. The
/// actions take the tab's layer index (<see cref="Move"/> also a delta of
/// −1 / +1); <see cref="LayerCount"/> is the edit's layer count as of
/// construction, which is what bounds the two moves. Built by
/// <see cref="LayerTabsViewModel.Rebuild"/>; null outside edit mode.
/// </summary>
public sealed record LayerTabActions(
    Action<int> Rename,
    Action<int> Duplicate,
    Action<int, int> Move,
    Action<int> Delete,
    int LayerCount);

/// <summary>
/// One tab in the layer-selector bar. The parent <see cref="LayerTabsViewModel"/>
/// passes a <c>selectAction</c> so the VM doesn't need to know its owner, and —
/// while a keymap edit is open — a <see cref="LayerTabActions"/> bundle that
/// makes the tab editable: rename, duplicate, move and delete commands for its
/// own layer. <see cref="TabColor"/> is observable so per-layer color overrides
/// can repaint tabs in place without rebuilding the list (which would steal
/// focus).
/// </summary>
public partial class LayerViewModel : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _tabColor;

    /// <summary>False hides this tab button on the main form only — the
    /// layer itself stays selectable, listed in Settings, and rendered on
    /// keyboard reports.</summary>
    [ObservableProperty] private bool _isShown = true;

    private readonly LayerTabActions? _actions;

    public int Index { get; }
    public string DisplayName { get; }
    public IRelayCommand SelectCommand { get; }

    /// <summary>True when the tab carries the layer actions — the edit
    /// commands are inert without them.</summary>
    public bool IsEditable => _actions is not null;

    // Move bounds are fixed at construction — the tab strip is rebuilt
    // whenever the layer list changes, so they never go stale.
    public bool CanMoveLeft => IsEditable && Index > 0;
    public bool CanMoveRight => IsEditable && Index < _actions!.LayerCount - 1;

    public IRelayCommand RenameCommand { get; }
    public IRelayCommand DuplicateCommand { get; }
    public IRelayCommand MoveLeftCommand { get; }
    public IRelayCommand MoveRightCommand { get; }
    public IRelayCommand DeleteCommand { get; }

    public LayerViewModel(
        int index,
        string displayName,
        string tabColor,
        Action<int> selectAction,
        LayerTabActions? actions = null)
    {
        Index = index;
        DisplayName = displayName;
        _tabColor = tabColor;
        _actions = actions;
        SelectCommand = new RelayCommand(() => selectAction(index));
        RenameCommand = new RelayCommand(() => _actions!.Rename(index), () => IsEditable);
        DuplicateCommand = new RelayCommand(() => _actions!.Duplicate(index), () => IsEditable);
        MoveLeftCommand = new RelayCommand(() => _actions!.Move(index, -1), () => CanMoveLeft);
        MoveRightCommand = new RelayCommand(() => _actions!.Move(index, +1), () => CanMoveRight);
        DeleteCommand = new RelayCommand(() => _actions!.Delete(index), () => IsEditable);
    }
}
