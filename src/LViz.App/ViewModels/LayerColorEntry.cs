using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LViz.App.ViewModels;

/// <summary>
/// One row in the Settings window's per-layer color list. Bridges the Avalonia
/// <see cref="Color"/> a <c>ColorView</c> picker writes to and the hex string
/// the rest of the pipeline (palette, settings) consumes. Reset reverts the
/// override and snaps the swatch back to the built-in palette default.
/// </summary>
public partial class LayerColorEntry : ObservableObject
{
    private readonly Action<int, string?> _setOverride;
    private readonly Func<int, string> _getDefaultHex;
    private bool _suppressColorWrite;

    public int Index { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorHex))]
    private Color _color;

    /// <summary>Hex form for the swatch preview Border (HexColorToBrushConverter).</summary>
    public string ColorHex => $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}";

    public IRelayCommand ResetCommand { get; }

    public LayerColorEntry(int index, string displayName, Color color, Action<int, string?> setOverride, Func<int, string> getDefaultHex)
    {
        Index = index;
        DisplayName = displayName;
        _color = color;
        _setOverride = setOverride;
        _getDefaultHex = getDefaultHex;
        ResetCommand = new RelayCommand(Reset);
    }

    partial void OnColorChanged(Color value)
    {
        if (_suppressColorWrite) return;
        _setOverride(Index, $"#{value.R:X2}{value.G:X2}{value.B:X2}");
    }

    private void Reset()
    {
        _setOverride(Index, null);
        if (Color.TryParse(_getDefaultHex(Index), out var defaultColor))
        {
            // Update the swatch without re-running OnColorChanged — the override
            // was already cleared above; we don't want to immediately re-set it.
            _suppressColorWrite = true;
            try { Color = defaultColor; }
            finally { _suppressColorWrite = false; }
        }
    }
}
