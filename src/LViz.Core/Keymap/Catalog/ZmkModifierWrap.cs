namespace LViz.Core.Keymap.Catalog;

/// <summary>
/// ZMK modifier bits (from <c>modifiers.h</c>), usable as a flag set.
/// </summary>
[Flags]
public enum ZmkModifiers
{
    None = 0,
    LeftCtrl = 0x01,
    LeftShift = 0x02,
    LeftAlt = 0x04,
    LeftGui = 0x08,
    RightCtrl = 0x10,
    RightShift = 0x20,
    RightAlt = 0x40,
    RightGui = 0x80,
}

/// <summary>
/// A keycode split out of a modifier-wrapped binding: the modifier set plus
/// the bare keycode underneath (<c>LS(LC(A))</c> → LeftCtrl | LeftShift, "A").
/// </summary>
public sealed record ZmkWrappedKeycode(ZmkModifiers Modifiers, string Keycode);

/// <summary>
/// Builds and reads the flat param form of modifier-wrapped keycodes:
/// <c>&amp;kp LS(LC(A))</c> is stored parsed as <c>["LC","LS","A"]</c>
/// (outermost first) and re-nested by <c>ZmkBindingFormatter</c> on write.
/// The key dialog's "keycode + modifier checkboxes" editor sits on top of
/// these helpers.
/// </summary>
public static class ZmkModifierWrap
{
    /// <summary>
    /// Single-flag values in the fixed wrap order LC LS LA LG RC RS RA RG —
    /// ascending bit value, which is that order by construction of the enum.
    /// </summary>
    private static readonly (ZmkModifiers Flag, string Function)[] SingleFlags =
    {
        (ZmkModifiers.LeftCtrl, "LC"),
        (ZmkModifiers.LeftShift, "LS"),
        (ZmkModifiers.LeftAlt, "LA"),
        (ZmkModifiers.LeftGui, "LG"),
        (ZmkModifiers.RightCtrl, "RC"),
        (ZmkModifiers.RightShift, "RS"),
        (ZmkModifiers.RightAlt, "RA"),
        (ZmkModifiers.RightGui, "RG"),
    };

    private static readonly Dictionary<string, ZmkModifiers> FlagByFunction =
        SingleFlags.ToDictionary(f => f.Function, f => f.Flag, StringComparer.Ordinal);

    /// <summary>
    /// Flat params for <paramref name="keycode"/> wrapped in the set
    /// modifiers, outermost first in the fixed order LC LS LA LG RC RS RA RG,
    /// then the keycode — <c>("A", LeftShift | LeftCtrl)</c> →
    /// <c>["LC","LS","A"]</c>. No flags → just the keycode.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="keycode"/> is blank.</exception>
    public static IReadOnlyList<string> Wrap(string keycode, ZmkModifiers modifiers)
    {
        if (string.IsNullOrWhiteSpace(keycode))
            throw new ArgumentException("Keycode must not be blank.", nameof(keycode));

        var result = new List<string>();
        foreach (var (flag, function) in SingleFlags)
        {
            if (modifiers.HasFlag(flag)) result.Add(function);
        }
        result.Add(keycode);
        return result;
    }

    /// <summary>
    /// Inverse of <see cref="Wrap"/> on the flat form: leading
    /// <c>LC LS LA LG RC RS RA RG</c> tokens become flags (a repeat just sets
    /// the flag again), and exactly one non-function token must remain as the
    /// keycode. Anything else — empty, no keycode, two bare tokens — is null.
    /// </summary>
    public static ZmkWrappedKeycode? Unwrap(IReadOnlyList<string> flatParams)
    {
        var modifiers = ZmkModifiers.None;
        int i = 0;
        while (i < flatParams.Count
               && FlagByFunction.TryGetValue(flatParams[i], out var flag))
        {
            modifiers |= flag;
            i++;
        }

        // Greedy consumption guarantees the one remaining token, if any, is
        // not a modifier function; zero or several remaining tokens fail.
        if (flatParams.Count - i != 1) return null;

        return new ZmkWrappedKeycode(modifiers, flatParams[i]);
    }

    /// <summary>
    /// Groups flat params into logical params with exactly the rule
    /// <c>ZmkBindingFormatter.ReNestModifiers</c> re-nests by: walking
    /// right-to-left, a modifier function token that has a token after it
    /// joins that token's group; any other token — and a trailing modifier
    /// function — ends its own group.
    /// <c>["LS","LC","A"]</c> → <c>[["LS","LC","A"]]</c>;
    /// <c>["LSHFT","LS","A"]</c> → <c>[["LSHFT"],["LS","A"]]</c>;
    /// <c>["A","LS"]</c> → <c>[["A"],["LS"]]</c>. Empty input → empty list.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="flatParams"/> is null.
    /// </exception>
    public static IReadOnlyList<IReadOnlyList<string>> SplitLogical(IReadOnlyList<string> flatParams)
    {
        ArgumentNullException.ThrowIfNull(flatParams);

        // Built right-to-left: the last entry is the leftmost group once
        // reversed, mirroring the formatter's stack walk.
        var groups = new List<List<string>>();
        for (int i = flatParams.Count - 1; i >= 0; i--)
        {
            if (groups.Count > 0 && FlagByFunction.ContainsKey(flatParams[i]))
                groups[^1].Insert(0, flatParams[i]);
            else
                groups.Add(new List<string> { flatParams[i] });
        }
        groups.Reverse();
        return groups;
    }

    /// <summary>
    /// The wrapper function name for one modifier flag: LeftCtrl → "LC",
    /// …, RightGui → "RG".
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="single"/> is not exactly one flag.
    /// </exception>
    public static string FunctionFor(ZmkModifiers single)
    {
        foreach (var (flag, function) in SingleFlags)
        {
            if (single == flag) return function;
        }
        throw new ArgumentException(
            $"'{single}' is not a single modifier flag.", nameof(single));
    }
}
