using CommunityToolkit.Mvvm.ComponentModel;
using LViz.App.Services;
using LViz.Core.Colors;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// Press-highlight colors for the board render surface. The rim stroke is a
/// darkened version of the fill (or a caller-supplied override). Composed by
/// <see cref="MainWindowViewModel"/> (user-customizable, persisted) and
/// <see cref="ExitKeyPickerViewModel"/> (the fixed picker palette), and exposed
/// through <see cref="IBoardSurface"/> so <c>BoardView</c> binds one style
/// source for both hosts.
/// </summary>
public partial class BoardStyleViewModel : ObservableObject
{
    // Null for hosts that don't persist (e.g. the exit-key picker).
    private readonly ISettingsService? _settings;
    // Non-null for hosts that want a fixed rim instead of the derived darken.
    private readonly string? _strokeOverride;

    public BoardStyleViewModel(
        string pressHighlightColor,
        ISettingsService? settings = null,
        string? strokeOverride = null)
    {
        _pressHighlightColor = pressHighlightColor;
        _settings = settings;
        _strokeOverride = strokeOverride;
    }

    /// <summary>Color used for the press-highlight dot pulsed on each pressed key ("#RRGGBB").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PressHighlightStrokeColor))]
    private string _pressHighlightColor;

    partial void OnPressHighlightColorChanged(string value) =>
        _settings?.Update(s => s with { PressHighlightColor = value }, "BoardStyle");

    /// <summary>
    /// Rim color for the press dot — the fixed override when one was supplied,
    /// otherwise a darkened version of <see cref="PressHighlightColor"/> (each
    /// channel × 0.55) so the dot reads against light layer fills.
    /// </summary>
    public string PressHighlightStrokeColor
    {
        get
        {
            if (_strokeOverride is not null) return _strokeOverride;
            if (HexRgb.TryParse(PressHighlightColor, out var r, out var g, out var b))
                return $"#{(int)(r * 0.55):X2}{(int)(g * 0.55):X2}{(int)(b * 0.55):X2}";
            return AppTheme.PressHighlightStrokeFallbackHex;
        }
    }
}
