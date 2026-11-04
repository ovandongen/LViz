using LViz.Core.Models;

namespace LViz.Core.Keymap.Catalog;

/// <summary>
/// One keycode that appears in the open keymap, with how often it is used.
/// </summary>
public sealed record ZmkKeycodeUse(ZmkKeycode Keycode, int Count);

/// <summary>
/// How often each keycode appears in a loaded keymap, and the spelling to
/// write back for it. Feeds the keycode picker's "Used in this keymap"
/// category (see <see cref="Ranked"/>) and the key dialog's name choice
/// (see <see cref="NameFor"/>).
/// </summary>
public sealed class ZmkKeycodeUsage
{
    private readonly Dictionary<ZmkKeycode, int> _counts;

    /// <summary>Per keycode, how often each exact spelling was written.</summary>
    private readonly Dictionary<ZmkKeycode, Dictionary<string, int>> _spellings;

    private ZmkKeycodeUsage(
        Dictionary<ZmkKeycode, int> counts,
        Dictionary<ZmkKeycode, Dictionary<string, int>> spellings)
    {
        _counts = counts;
        _spellings = spellings;
        Ranked = counts.Count == 0
            ? Array.Empty<ZmkKeycodeUse>()
            : BuildRanked(counts);
    }

    /// <summary>A usage with no uses: for a dialog without a config and for tests.</summary>
    public static ZmkKeycodeUsage Empty { get; } =
        new(new(), new());

    /// <summary>
    /// Every used keycode once, count descending, ties in
    /// <see cref="ZmkKeycodeCatalog.All"/> order.
    /// </summary>
    public IReadOnlyList<ZmkKeycodeUse> Ranked { get; }

    /// <summary>Occurrences of <paramref name="keycode"/>; 0 when unused.</summary>
    public int CountOf(ZmkKeycode keycode) =>
        _counts.TryGetValue(keycode, out var count) ? count : 0;

    /// <summary>
    /// The name to write for <paramref name="keycode"/>. Used keycodes get
    /// the spelling the file already uses most for them (ties: shorter
    /// spelling, then Ordinal); unused ones the shortest of the canonical
    /// name plus aliases, earliest on a length tie (deprecated names are
    /// never offered).
    /// </summary>
    public string NameFor(ZmkKeycode keycode)
    {
        if (_spellings.TryGetValue(keycode, out var spellings))
        {
            return spellings
                .OrderByDescending(sp => sp.Value)
                .ThenBy(sp => sp.Key.Length)
                .ThenBy(sp => sp.Key, StringComparer.Ordinal)
                .First()
                .Key;
        }

        var best = keycode.Name;
        foreach (var alias in keycode.Aliases)
        {
            if (alias.Length < best.Length) best = alias;
        }
        return best;
    }

    /// <summary>
    /// Counts every keycode in <paramref name="config"/>: for each binding
    /// of each layer, the behavior is looked up in
    /// <see cref="ZmkBehaviorCatalog"/> (unknown behaviors are skipped), its
    /// flat params are grouped by <see cref="ZmkModifierWrap.SplitLogical"/>
    /// (a group count that disagrees with the behavior's param kinds skips
    /// the binding), and each <see cref="ZmkBehaviorParamKind.Keycode"/> /
    /// <see cref="ZmkBehaviorParamKind.Modifier"/> group that unwraps to a
    /// catalog-known keycode counts once for the keycode and once for that
    /// exact spelling.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="config"/> is null.
    /// </exception>
    public static ZmkKeycodeUsage Count(KeyboardConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var counts = new Dictionary<ZmkKeycode, int>();
        var spellings = new Dictionary<ZmkKeycode, Dictionary<string, int>>();

        foreach (var layer in config.Layers)
        {
            foreach (var binding in layer.Bindings)
            {
                var behavior = ZmkBehaviorCatalog.Find(config, binding.Behavior);
                if (behavior is null) continue;

                var groups = ZmkModifierWrap.SplitLogical(binding.Params);
                if (groups.Count != behavior.Params.Count) continue;

                for (int i = 0; i < groups.Count; i++)
                {
                    if (behavior.Params[i]
                        is not (ZmkBehaviorParamKind.Keycode or ZmkBehaviorParamKind.Modifier))
                    {
                        continue;
                    }

                    var unwrapped = ZmkModifierWrap.Unwrap(groups[i]);
                    if (unwrapped is null) continue;

                    var keycode = ZmkKeycodeCatalog.Find(unwrapped.Keycode);
                    if (keycode is null) continue;

                    counts[keycode] = counts.TryGetValue(keycode, out var seen) ? seen + 1 : 1;
                    if (!spellings.TryGetValue(keycode, out var bySpelling))
                    {
                        bySpelling = new Dictionary<string, int>(StringComparer.Ordinal);
                        spellings[keycode] = bySpelling;
                    }
                    bySpelling[unwrapped.Keycode] =
                        bySpelling.TryGetValue(unwrapped.Keycode, out var spellingCount)
                            ? spellingCount + 1
                            : 1;
                }
            }
        }

        return new ZmkKeycodeUsage(counts, spellings);
    }

    private static IReadOnlyList<ZmkKeycodeUse> BuildRanked(Dictionary<ZmkKeycode, int> counts)
    {
        // Every counted keycode came out of the catalog, so it has a file index.
        var all = ZmkKeycodeCatalog.All;
        var fileOrder = new Dictionary<ZmkKeycode, int>();
        for (int i = 0; i < all.Count; i++)
        {
            fileOrder[all[i]] = i;
        }

        return counts
            .Select(kv => (Use: new ZmkKeycodeUse(kv.Key, kv.Value), Order: fileOrder[kv.Key]))
            .OrderByDescending(x => x.Use.Count)
            .ThenBy(x => x.Order)
            .Select(x => x.Use)
            .ToList();
    }
}
