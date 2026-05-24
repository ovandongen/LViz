using CommunityToolkit.Mvvm.ComponentModel;

namespace LViz.App.ViewModels;

/// <summary>
/// One numbered pill rendered on a key when the combo overlay is on. A
/// key in N combos gets N badges (capped at 3 — higher combo numbers
/// don't fit the bottom-left strip). Each badge maps 1:1 to a combo, so
/// hovering it highlights only that combo's legend tile.
/// </summary>
public partial class KeyComboBadgeViewModel : ObservableObject
{
    /// <summary>
    /// Pills cap per key — combo numbers past this don't fit the
    /// bottom-left strip and are dropped from the rendered set (still
    /// listed in the per-key tooltip).
    /// </summary>
    public const int MaxBadgesPerKey = 3;

    public int Number { get; }

    [ObservableProperty] private bool _isHighlighted;

    public KeyComboBadgeViewModel(int number) => Number = number;
}
