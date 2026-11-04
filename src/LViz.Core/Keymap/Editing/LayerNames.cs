namespace LViz.Core.Keymap.Editing;

/// <summary>
/// Picking a layer name that is not taken yet: a new layer gets "Layer N", a
/// duplicate gets "&lt;name&gt; copy", and both are made unique by appending a
/// number before they go into the keymap.
/// </summary>
public static class LayerNames
{
    /// <summary>
    /// <paramref name="wanted"/> when no entry of <paramref name="taken"/> equals
    /// it (ordinal, case-sensitive, untrimmed); otherwise the first of
    /// <c>wanted + " 2"</c>, <c>wanted + " 3"</c>, … that no entry equals.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="taken"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="wanted"/> is null or blank.</exception>
    public static string Unique(string wanted, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        if (string.IsNullOrWhiteSpace(wanted))
            throw new ArgumentException("layer name is empty.", nameof(wanted));

        var set = taken as ISet<string> ?? new HashSet<string>(taken, StringComparer.Ordinal);
        if (!set.Contains(wanted)) return wanted;

        for (var n = 2; ; n++)
        {
            var candidate = wanted + " " + n;
            if (!set.Contains(candidate)) return candidate;
        }
    }
}
