using LViz.Core.Models;

namespace LViz.Core.Keymap.Parser;

/// <summary>
/// Post-parse sanity checks shared by every <see cref="KeyboardConfig"/>
/// producer (the <c>.keymap</c> devicetree path and the Moergo JSON path).
/// Surfaces a truncated or malformed layout before the UI silently renders a
/// half-empty board.
/// </summary>
public static class KeyboardConfigValidator
{
    /// <summary>
    /// Throws <see cref="ZmkKeymapParseException"/> when <paramref name="config"/>
    /// has no layers, or when its layers disagree on binding count (one per
    /// physical key position is expected).
    /// </summary>
    public static void Validate(KeyboardConfig config, string? originPath)
    {
        if (config.Layers.Count == 0)
        {
            throw new ZmkKeymapParseException(
                "No layers found in layout.", originPath);
        }

        // Every layer in a single keymap should have the same number of
        // bindings — one per physical key position. A mismatch usually means
        // the file is truncated or a binding got mistyped.
        var counts = config.Layers
            .Select(l => (l.Name, Count: l.Bindings.Count))
            .ToList();
        var expected = counts[0].Count;
        var oddOnes = counts.Where(c => c.Count != expected).ToList();
        if (oddOnes.Count > 0)
        {
            var detail = string.Join(", ",
                counts.Select(c => $"'{c.Name}' = {c.Count}"));
            throw new ZmkKeymapParseException(
                $"Layers have inconsistent binding counts ({detail}). " +
                "The file may be truncated or a layer's 'bindings' is malformed.",
                originPath);
        }
    }
}
