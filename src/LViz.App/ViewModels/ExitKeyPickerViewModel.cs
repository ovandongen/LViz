using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LViz.App.Services;
using LViz.Core.Layout;

namespace LViz.App.ViewModels;

/// <summary>
/// View model for the exit-tap key picker popup. Hosts a stripped-down
/// copy of the active keyboard profile (empty labels, no combo earmarks)
/// and lets the user click a single cap to choose the firmware key index
/// that will toggle the auto-switch engine's escape hatch on double-tap.
///
/// <para>Owns a <see cref="BoardSurfaceViewModel"/> so <c>BoardView</c> can
/// render it without forking the control — the same canvas math, palette,
/// and per-cap template apply. The selected cap is tinted green via
/// <see cref="KeyViewModel.KeyFillColor"/>.</para>
/// </summary>
public partial class ExitKeyPickerViewModel : ObservableObject
{
    private const string DefaultFill = AppTheme.KeyDefaultFillHex;
    private const string SelectedFill = AppTheme.AccentGreenHex;

    private readonly IKeyboardProfile _profile;
    private int? _selectedIndex;

    public ExitKeyPickerViewModel(IKeyboardProfile profile, int? initialSelection)
    {
        _profile = profile;
        // Picker never persists and never stacks; it uses a fixed press palette
        // (no settings service passed → layout/style don't write back) and an
        // inert legend with the overlay off (no combo earmarks here). Keys are
        // stripped — empty Label / Subscript / Icon / TopLeftLabel — so the
        // picker shows the physical layout only; KeyFillColor flips between
        // default and SelectedFill as the user toggles a cap.
        var style = new BoardStyleViewModel(
            AppTheme.PressHighlightDefaultHex,
            strokeOverride: AppTheme.PickerPressStrokeHex);
        Board = new BoardSurfaceViewModel(
            profile, new BoardLayoutViewModel(profile), style,
            keys => new ComboLegendViewModel(keys, style, overlayVisible: false, profileId: () => ""),
            pos => new KeyViewModel(pos)
            {
                Label = "",
                Subscript = "",
                IconName = "",
                TopLeftLabel = "",
                Behavior = "",
                KeyFillColor = DefaultFill,
                IsInCombo = false,
                IsPressed = false,
                Tooltip = pos.Description ?? $"#{pos.Index}",
            })
        {
            OnKeyTapped = ToggleKey,
        };

        // Apply the initial selection so reopening the picker shows the
        // current configuration. Silently drops an out-of-range index that
        // wouldn't map to a cap on this profile (e.g. carried over from a
        // different keyboard).
        if (initialSelection is int idx && idx >= 0 && idx < Board.Keys.Count)
        {
            _selectedIndex = idx;
            Board.Keys[idx].KeyFillColor = SelectedFill;
        }

        UpdateChips();
    }

    /// <summary>The board <c>BoardView</c> renders; a tap on a cap toggles it.</summary>
    public BoardSurfaceViewModel Board { get; }

    /// <summary>True when the user has at least one cap selected — drives
    /// the OK button's IsEnabled and the empty-state hint visibility.</summary>
    [ObservableProperty]
    private bool _hasSelection;

    /// <summary>Human-readable summary of the current selection, e.g.
    /// "#42". Empty when nothing's picked.</summary>
    [ObservableProperty]
    private string _selectionSummary = "";

    /// <summary>Snapshot of the currently-selected firmware key index.
    /// Consumed by the SettingsViewModel when the user confirms the
    /// picker. Null when the user picked nothing.</summary>
    public int? SelectedIndex => _selectedIndex;

    private void ToggleKey(int index)
    {
        if (index < 0 || index >= Board.Keys.Count) return;

        // Tap an already-selected key to deselect.
        if (_selectedIndex == index)
        {
            Board.Keys[index].KeyFillColor = DefaultFill;
            _selectedIndex = null;
            UpdateChips();
            return;
        }

        // Replace the previous selection (single-key picker).
        if (_selectedIndex is int prev && prev >= 0 && prev < Board.Keys.Count)
            Board.Keys[prev].KeyFillColor = DefaultFill;

        _selectedIndex = index;
        Board.Keys[index].KeyFillColor = SelectedFill;
        UpdateChips();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        if (_selectedIndex is int idx && idx >= 0 && idx < Board.Keys.Count)
            Board.Keys[idx].KeyFillColor = DefaultFill;
        _selectedIndex = null;
        UpdateChips();
    }

    private void UpdateChips()
    {
        HasSelection = _selectedIndex is not null;
        SelectionSummary = _selectedIndex switch
        {
            int i when i >= 0 && i < _profile.Keys.Count =>
                _profile.Keys[i].Description is { Length: > 0 } d ? $"{d} (#{i})" : $"#{i}",
            _ => "",
        };
    }
}
