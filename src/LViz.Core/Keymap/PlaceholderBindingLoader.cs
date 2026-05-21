using LViz.Core.Models;

namespace LViz.Core.Keymap;

/// <summary>
/// Stand-in for the (not-yet-written) ZMK <c>.keymap</c> parser. Produces a
/// single-layer <see cref="KeyboardConfig"/> where every binding is
/// <c>&amp;kp</c> with a positional placeholder ("idx 0", "idx 1", …). Lets
/// the app render real board geometry end-to-end before binding data is
/// available.
/// </summary>
public static class PlaceholderBindingLoader
{
    public static KeyboardConfig BuildForProfile(string keyboardId, int keyCount)
    {
        var bindings = new List<KeyBinding>(keyCount);
        for (int i = 0; i < keyCount; i++)
        {
            bindings.Add(new KeyBinding("&kp", new[] { i.ToString() })
            {
                DecorationLabel = $"idx {i}",
            });
        }
        return new KeyboardConfig(
            KeyboardId: keyboardId,
            Layers: new[] { new Layer(0, "Base", bindings) },
            Macros: Array.Empty<ZmkMacro>(),
            HoldTaps: Array.Empty<HoldTap>(),
            Combos: Array.Empty<ZmkCombo>());
    }
}
