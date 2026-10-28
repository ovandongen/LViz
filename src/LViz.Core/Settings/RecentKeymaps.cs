namespace LViz.Core.Settings;

/// <summary>
/// Pure bookkeeping for the per-profile "recent keymaps" list persisted in
/// <see cref="UserSettings.RecentKeymapPaths"/>. Both helpers are
/// copy-on-write: they return a new list and never mutate their input, so
/// callers can hand them live settings lists directly.
/// </summary>
public static class RecentKeymaps
{
    /// <summary>Maximum number of paths kept per profile; the oldest fall off the end.</summary>
    public const int MaxEntries = 8;

    /// <summary>
    /// Returns a new list with <paramref name="path"/> at index 0. Earlier
    /// copies of <paramref name="path"/> are dropped (move-to-front, ordinal
    /// match) so the list stays duplicate-free, and the result is trimmed to
    /// <see cref="MaxEntries"/>. A null, empty, or whitespace path yields an
    /// unchanged copy of <paramref name="current"/>.
    /// </summary>
    public static List<string> Push(IReadOnlyList<string> current, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new List<string>(current);

        var result = new List<string>(current.Count + 1) { path };
        foreach (var entry in current)
            if (!string.Equals(entry, path, StringComparison.Ordinal))
                result.Add(entry);

        if (result.Count > MaxEntries)
            result.RemoveRange(MaxEntries, result.Count - MaxEntries);
        return result;
    }

    /// <summary>
    /// Returns a new list without <paramref name="path"/> (ordinal match). A
    /// path that isn't present yields an unchanged copy of
    /// <paramref name="current"/>.
    /// </summary>
    public static List<string> Remove(IReadOnlyList<string> current, string path)
    {
        var result = new List<string>(current.Count);
        foreach (var entry in current)
            if (!string.Equals(entry, path, StringComparison.Ordinal))
                result.Add(entry);
        return result;
    }
}
