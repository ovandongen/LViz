using LViz.Core.Models;

namespace LViz.Core.Keymap;

/// <summary>
/// Stand-in for the (no-longer-needed) initial-fork loader. Produces a
/// single-layer <see cref="KeyboardConfig"/> where every binding is
/// <c>&amp;kp</c> with a positional placeholder ("idx 0", "idx 1", …).
/// Superseded by <see cref="Parser.ZmkKeymapLoader"/>; kept for one release
/// in case anyone needs to render bare board geometry without a real keymap.
/// </summary>
[Obsolete("Use LViz.Core.Keymap.Parser.ZmkKeymapLoader instead.")]
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
