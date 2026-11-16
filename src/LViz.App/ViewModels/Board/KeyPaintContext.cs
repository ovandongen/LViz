using LViz.Core.Keymap;
using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// What a key paints with besides its binding: the board's profile and the
/// settings as they stand, for the user's layer colours, key labels and
/// modifier glyphs.
/// </summary>
public sealed record KeyPaintContext(string ProfileId, UserSettings Settings)
{
    public string LayerColor(int layerIndex) => LayerColorPalette.GetColor(Settings, ProfileId, layerIndex);

    public KeyLabelOverride? KeyLabel(int layerIndex, int keyIndex)
        => KeyLabelOverrides.Get(Settings, ProfileId, layerIndex, keyIndex);

    public ModifierStyle ModifierStyle => ModifierStyles.FromSetting(Settings.ModifierStyle);
}
