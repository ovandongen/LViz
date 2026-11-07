using System.Text;
using LViz.Core.Models;

namespace LViz.Core.Keymap.Editing;

/// <summary>
/// The kinds of problems <see cref="ComboRules.Check"/> can find in a combo.
/// The App turns these into text; Core only reports codes and values.
/// </summary>
public enum ComboProblemKind
{
    /// <summary>error</summary>
    TooFewKeys,
    /// <summary>error</summary>
    InvalidName,
    /// <summary>error; Other = the other combo's name</summary>
    DuplicateName,
    /// <summary>warning; Other = the other combo's name</summary>
    SameKeysAsOther,
    /// <summary>warning; Count = key count</summary>
    TooManyKeys,
    /// <summary>warning; KeyIndex = the key, Count = combos using it (this one included)</summary>
    KeyInManyCombos,
}

/// <summary>One problem found by <see cref="ComboRules.Check"/>.</summary>
public sealed record ComboProblem(
    ComboProblemKind Kind,
    string? Other = null,
    int? KeyIndex = null,
    int? Count = null)
{
    /// <summary>True when this problem blocks saving (OK disabled in the dialog).</summary>
    public bool IsError => Kind is ComboProblemKind.TooFewKeys
        or ComboProblemKind.InvalidName
        or ComboProblemKind.DuplicateName;
}

/// <summary>
/// The pure rules the combo writer and the combo dialog share: what a valid
/// devicetree node name is, how a new combo is auto-named, and which problems
/// a combo has. Decided with Olaf: fewer than two keys, an invalid node name
/// and a name clash are errors; the MoErgo limits (more than four keys, a key
/// in more than five combos) and a duplicate key set on a shared layer are
/// warnings.
/// </summary>
public static class ComboRules
{
    /// <summary>Longest allowed node name (devicetree convention).</summary>
    public const int MaxNodeNameLength = 31;

    /// <summary>Keys per combo MoErgo (and older ZMK) allowed; ZMK main has no limit.</summary>
    public const int MoergoMaxKeysPerCombo = 4;

    /// <summary>Combos per key MoErgo (and older ZMK) allowed; ZMK main has no limit.</summary>
    public const int MoergoMaxCombosPerKey = 5;

    /// <summary>
    /// True when <paramref name="name"/> is a legal devicetree node name:
    /// 1–<see cref="MaxNodeNameLength"/> characters from <c>A–Z a–z 0–9 , . _ + -</c>,
    /// starting with a letter (i.e. matches <c>^[A-Za-z][A-Za-z0-9,._+-]*$</c>).
    /// </summary>
    public static bool IsValidNodeName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name!.Length > MaxNodeNameLength) return false;
        if (!IsStartChar(name[0])) return false;
        for (var i = 1; i < name.Length; i++)
            if (!IsRestChar(name[i])) return false;
        return true;
    }

    /// <summary>
    /// The name a new combo gets: <c>combo_</c> plus the binding (for
    /// <c>&amp;kp</c> the params joined with <c>_</c>, otherwise the behavior
    /// without <c>&amp;</c> followed by its params), sanitized to
    /// <c>A–Z a–z 0–9 _</c>, cut to <see cref="MaxNodeNameLength"/> —
    /// <c>combo</c> when the binding is null or contributes nothing. Made
    /// unique against <paramref name="takenNames"/> (Ordinal) by appending
    /// <c>_2</c>, <c>_3</c>, …, cutting the base so the whole stays within
    /// <see cref="MaxNodeNameLength"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="takenNames"/> is null.</exception>
    public static string AutoName(KeyBinding? binding, IEnumerable<string> takenNames)
    {
        ArgumentNullException.ThrowIfNull(takenNames);

        var name = Cut(BaseName(binding), MaxNodeNameLength);

        var taken = takenNames as ISet<string> ?? new HashSet<string>(takenNames, StringComparer.Ordinal);
        if (!taken.Contains(name)) return name;

        for (var n = 2; ; n++)
        {
            var suffix = "_" + n;
            var candidate = Cut(name, MaxNodeNameLength - suffix.Length) + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Every problem <paramref name="combo"/> has, checked against
    /// <paramref name="others"/> — every <em>other</em> combo in the keymap
    /// (the caller leaves the edited one out). Order: too few keys, invalid
    /// name, duplicate name, same keys as another combo on a shared layer,
    /// too many keys, then per key (ascending) the number of combos using it.
    /// Empty when the combo is fine.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="combo"/> or <paramref name="others"/> is null.</exception>
    public static IReadOnlyList<ComboProblem> Check(ZmkCombo combo, IReadOnlyList<ZmkCombo> others)
    {
        ArgumentNullException.ThrowIfNull(combo);
        ArgumentNullException.ThrowIfNull(others);

        var problems = new List<ComboProblem>();
        var keys = new HashSet<int>(combo.KeyPositions);

        if (keys.Count < 2)
            problems.Add(new ComboProblem(ComboProblemKind.TooFewKeys));

        if (!IsValidNodeName(combo.NodeName))
            problems.Add(new ComboProblem(ComboProblemKind.InvalidName));

        var nameTwin = others.FirstOrDefault(o =>
            string.Equals(Identity(o), combo.NodeName, StringComparison.Ordinal));
        if (nameTwin is not null)
            problems.Add(new ComboProblem(ComboProblemKind.DuplicateName, Other: Identity(nameTwin)));

        var keyTwin = others.FirstOrDefault(o =>
            new HashSet<int>(o.KeyPositions).SetEquals(keys) && ScopesOverlap(combo.Layers, o.Layers));
        if (keyTwin is not null)
            problems.Add(new ComboProblem(ComboProblemKind.SameKeysAsOther, Other: Identity(keyTwin)));

        if (keys.Count > MoergoMaxKeysPerCombo)
            problems.Add(new ComboProblem(ComboProblemKind.TooManyKeys, Count: keys.Count));

        foreach (var key in keys.Order())
        {
            var count = 1 + others.Count(o => o.KeyPositions.Contains(key));
            if (count > MoergoMaxCombosPerKey)
                problems.Add(new ComboProblem(ComboProblemKind.KeyInManyCombos, KeyIndex: key, Count: count));
        }

        return problems;
    }

    /// <summary>How the file names a combo: its node name, or the editor name
    /// when it never came from a .keymap node.</summary>
    private static string Identity(ZmkCombo combo) => combo.NodeName ?? combo.Name;

    private static string BaseName(KeyBinding? binding)
    {
        if (binding is null) return "combo";

        string raw;
        if (string.Equals(binding.Behavior, "&kp", StringComparison.Ordinal))
        {
            raw = string.Join('_', binding.Params);
        }
        else
        {
            var behavior = binding.Behavior.StartsWith('&') ? binding.Behavior[1..] : binding.Behavior;
            var parts = new List<string>(binding.Params.Count + 1) { behavior };
            parts.AddRange(binding.Params);
            raw = string.Join('_', parts);
        }

        var part = Sanitize(raw);
        return part.Length == 0 ? "combo" : "combo_" + part;
    }

    /// <summary>Everything outside <c>A–Z a–z 0–9 _</c> becomes <c>_</c>, runs
    /// of <c>_</c> collapse to one, leading and trailing <c>_</c> go.</summary>
    private static string Sanitize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            sb.Append(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_');

        var s = sb.ToString();
        while (s.Contains("__", StringComparison.Ordinal))
            s = s.Replace("__", "_", StringComparison.Ordinal);
        return s.Trim('_');
    }

    private static string Cut(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>Two layer scopes share a layer when either is all-layers
    /// (an empty list or a list holding -1) or the two layer sets intersect.</summary>
    private static bool ScopesOverlap(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        bool allA = ZmkCombo.IsAllLayers(a);
        bool allB = ZmkCombo.IsAllLayers(b);
        return allA || allB || a.Intersect(b).Any();
    }

    private static bool IsStartChar(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsRestChar(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
            or ',' or '.' or '_' or '+' or '-';
}
