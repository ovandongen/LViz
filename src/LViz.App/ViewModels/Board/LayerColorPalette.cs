using LViz.Core.Settings;

namespace LViz.App.ViewModels;

/// <summary>
/// Default layer tab colors — indexed, wraps after 10. Matches the Svalboard
/// palette for visual consistency with the sister app. Per-keyboard, per-layer
/// overrides the user set via the Settings window live in
/// <see cref="UserSettings.LayerColors"/>; <see cref="GetColor"/> looks them
/// up there and missing entries fall through to the built-in palette.
/// Stateless: the settings travel with every call, so there is no cache to
/// keep in sync.
/// </summary>
public static class LayerColorPalette
{
    private static readonly string[] DefaultColors =
    [
        "#89B4FA", // layer 0 — blue
        "#F38BA8", // layer 1 — pink
        "#A6E3A1", // layer 2 — green
        "#FAB387", // layer 3 — orange
        "#CBA6F7", // layer 4 — purple
        "#F9E2AF", // layer 5 — yellow
        "#94E2D5", // layer 6 — teal
        "#F5C2E7", // layer 7 — magenta
        "#B4BEFE", // layer 8 — lavender
        "#EBA0AC", // layer 9 — maroon
    ];

    /// <summary>Built-in palette color for a layer (no override lookup).</summary>
    public static string GetDefaultColor(int index)
        => DefaultColors[((index % DefaultColors.Length) + DefaultColors.Length) % DefaultColors.Length];

    /// <summary>
    /// Effective color for a layer on the given keyboard profile — a non-blank
    /// user override in <paramref name="settings"/> wins, otherwise falls back
    /// to <see cref="GetDefaultColor"/>.
    /// </summary>
    public static string GetColor(UserSettings settings, string profileId, int index)
        => settings.LayerColors.TryGetValue(profileId, out var perLayer)
            && perLayer.TryGetValue(index, out var hex)
            && !string.IsNullOrWhiteSpace(hex)
            ? hex
            : GetDefaultColor(index);
}
