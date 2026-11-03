using System.IO;
using System.Text.Json;

namespace LViz.Core.Keymap.Catalog;

/// <summary>
/// Category a ZMK keycode belongs to. The <c>category</c> value in
/// <c>zmk-keycodes.json</c> is the enum name lower-cased.
/// </summary>
public enum ZmkKeycodeCategory
{
    Letters,
    Digits,
    Punctuation,
    Symbols,
    Modifiers,
    Navigation,
    Editing,
    Function,
    Keypad,
    Locks,
    International,
    Consumer,
    System,
    Other,
}

/// <summary>
/// One pickable ZMK keycode from the bundled catalog. <see cref="Name"/> is
/// the canonical ZMK name; <see cref="Aliases"/> and
/// <see cref="DeprecatedNames"/> are the other names it answers to.
/// <see cref="ShiftOf"/> is the canonical name of the key that produces this
/// one with Shift (for example <c>EXCLAMATION</c> → <c>NUMBER_1</c>).
/// </summary>
public sealed record ZmkKeycode(
    string Name,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> DeprecatedNames,
    string Description,
    ZmkKeycodeCategory Category,
    string? ShiftOf);

/// <summary>
/// The bundled ZMK keycode list (<c>Resources/zmk-keycodes.json</c>,
/// generated from ZMK's MIT-licensed <c>keys.h</c>) with lookup by any
/// name, alias or deprecated name, per-category listing and ranked search.
/// Feeds the keycode picker of the keymap editor.
/// </summary>
public static class ZmkKeycodeCatalog
{
    private const string ResourceName = "LViz.Core.Resources.zmk-keycodes.json";

    private static readonly Dictionary<string, ZmkKeycodeCategory> CategoryByName =
        Enum.GetValues<ZmkKeycodeCategory>()
            .ToDictionary(c => c.ToString().ToLowerInvariant(), StringComparer.Ordinal);

    private static readonly Lazy<CatalogData> Data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>All keycodes, in file (ZMK header) order. Loaded once, lazily.</summary>
    public static IReadOnlyList<ZmkKeycode> All => Data.Value.Keycodes;

    /// <summary>
    /// Exact (Ordinal) match on the canonical name, an alias or a deprecated
    /// name — ZMK names are case-sensitive. Null when unknown.
    /// </summary>
    public static ZmkKeycode? Find(string name) =>
        Data.Value.ByName.TryGetValue(name, out var keycode) ? keycode : null;

    /// <summary>The keycodes of one category, in file order.</summary>
    public static IReadOnlyList<ZmkKeycode> InCategory(ZmkKeycodeCategory category) =>
        Data.Value.ByCategory.TryGetValue(category, out var keycodes)
            ? keycodes
            : Array.Empty<ZmkKeycode>();

    /// <summary>
    /// Keycodes matching <paramref name="query"/>, best rank first, ties in
    /// file order; each keycode at most once. Ranks: 1 a name or alias equals
    /// the query, 2 a name or alias starts with it, 3 a name or alias contains
    /// it, 4 a deprecated name equals or starts with it, 5 the description
    /// contains it. Name comparisons also try the query with spaces replaced
    /// by underscores (<c>"page up"</c> finds <c>PAGE_UP</c>); description
    /// matching uses the query as typed. All comparisons are
    /// OrdinalIgnoreCase. A null/whitespace query yields everything (in
    /// category order when <paramref name="category"/> is given).
    /// </summary>
    public static IReadOnlyList<ZmkKeycode> Search(string? query, ZmkKeycodeCategory? category = null)
    {
        var pool = category is null ? All : InCategory(category.Value);
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return pool;
        }

        // "page up" must find PAGE_UP; the description still sees "page up".
        var underscored = trimmed.Replace(' ', '_');

        var scored = new List<(int Rank, int Order, ZmkKeycode Code)>();
        for (var i = 0; i < pool.Count; i++)
        {
            var rank = Rank(pool[i], trimmed, underscored);
            if (rank is not null)
            {
                scored.Add((rank.Value, i, pool[i]));
            }
        }

        return scored
            .OrderBy(s => s.Rank)
            .ThenBy(s => s.Order)
            .Select(s => s.Code)
            .ToList();
    }

    /// <summary>Best rank (1–5) for this keycode, or null when it does not match.</summary>
    private static int? Rank(ZmkKeycode keycode, string query, string underscoredQuery)
    {
        var best = int.MaxValue;

        foreach (var name in NameAndAliases(keycode))
        {
            if (EqualsAny(name, query, underscoredQuery, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (StartsWithAny(name, query, underscoredQuery))
            {
                best = Math.Min(best, 2);
            }
            else if (name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     name.Contains(underscoredQuery, StringComparison.OrdinalIgnoreCase))
            {
                best = Math.Min(best, 3);
            }
        }

        if (keycode.DeprecatedNames.Any(deprecated =>
                EqualsAny(deprecated, query, underscoredQuery, StringComparison.OrdinalIgnoreCase) ||
                StartsWithAny(deprecated, query, underscoredQuery)))
        {
            best = Math.Min(best, 4);
        }

        if (keycode.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            best = Math.Min(best, 5);
        }

        return best == int.MaxValue ? null : best;
    }

    private static IEnumerable<string> NameAndAliases(ZmkKeycode keycode)
    {
        yield return keycode.Name;
        foreach (var alias in keycode.Aliases)
        {
            yield return alias;
        }
    }

    private static bool EqualsAny(string candidate, string query, string underscoredQuery, StringComparison comparison) =>
        candidate.Equals(query, comparison) || candidate.Equals(underscoredQuery, comparison);

    private static bool StartsWithAny(string candidate, string query, string underscoredQuery) =>
        candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(underscoredQuery, StringComparison.OrdinalIgnoreCase);

    private static CatalogData Load()
    {
        var assembly = typeof(ZmkKeycodeCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException(
                $"Embedded keycode catalog '{ResourceName}' not found. Manifest contains: "
                + string.Join(", ", assembly.GetManifestResourceNames()));

        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("keycodes", out var keycodesElement) ||
            keycodesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"{ResourceName}: expected an object with a 'keycodes' array.");
        }

        var keycodes = new List<ZmkKeycode>();
        var index = 0;
        foreach (var entry in keycodesElement.EnumerateArray())
        {
            keycodes.Add(ParseEntry(entry, index));
            index++;
        }

        var byName = new Dictionary<string, ZmkKeycode>(StringComparer.Ordinal);
        foreach (var keycode in keycodes)
        {
            foreach (var name in keycode.Aliases.Concat(keycode.DeprecatedNames).Prepend(keycode.Name))
            {
                if (!byName.TryAdd(name, keycode))
                {
                    throw new InvalidDataException(
                        $"{ResourceName}: name '{name}' is used by both {byName[name].Name} and {keycode.Name}.");
                }
            }
        }

        var byCategory = keycodes
            .GroupBy(k => k.Category)
            .ToDictionary(g => g.Key, g => g.ToArray());

        return new CatalogData(keycodes.ToArray(), byName, byCategory);
    }

    private static ZmkKeycode ParseEntry(JsonElement entry, int index)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{ResourceName}: keycodes[{index}] is not an object.");
        }

        var names = ReadStringArray(entry, "names", index, required: true)!;
        if (names.Length == 0 || names[0].Length == 0)
        {
            throw new InvalidDataException($"{ResourceName}: keycodes[{index}] has no canonical name.");
        }

        var description = ReadOptionalString(entry, "description", index);
        if (string.IsNullOrEmpty(description))
        {
            throw new InvalidDataException($"{ResourceName}: keycodes[{index}] ({names[0]}) has no description.");
        }

        var categoryValue = ReadOptionalString(entry, "category", index);
        if (categoryValue is null)
        {
            throw new InvalidDataException($"{ResourceName}: keycodes[{index}] ({names[0]}) has no category.");
        }

        if (!CategoryByName.TryGetValue(categoryValue, out var category))
        {
            throw new InvalidDataException(
                $"{ResourceName}: keycodes[{index}] ({names[0]}) has unknown category '{categoryValue}'.");
        }

        var deprecated = ReadStringArray(entry, "deprecated", index, required: false) ?? Array.Empty<string>();
        var shiftOf = ReadOptionalString(entry, "shiftOf", index);

        return new ZmkKeycode(
            names[0],
            names.Length > 1 ? names[1..] : Array.Empty<string>(),
            deprecated,
            description,
            category,
            shiftOf);
    }

    private static string[]? ReadStringArray(JsonElement entry, string property, int index, bool required)
    {
        if (!entry.TryGetProperty(property, out var element))
        {
            if (required)
            {
                throw new InvalidDataException($"{ResourceName}: keycodes[{index}] has no '{property}'.");
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Array ||
            element.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            throw new InvalidDataException($"{ResourceName}: keycodes[{index}].{property} is not an array of strings.");
        }

        return element.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static string? ReadOptionalString(JsonElement entry, string property, int index)
    {
        if (!entry.TryGetProperty(property, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{ResourceName}: keycodes[{index}].{property} is not a string.");
        }

        var value = element.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private sealed record CatalogData(
        ZmkKeycode[] Keycodes,
        Dictionary<string, ZmkKeycode> ByName,
        Dictionary<ZmkKeycodeCategory, ZmkKeycode[]> ByCategory);
}
