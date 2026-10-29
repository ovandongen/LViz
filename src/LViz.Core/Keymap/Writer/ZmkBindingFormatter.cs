using System.Globalization;
using LViz.Core.Models;

namespace LViz.Core.Keymap.Writer;

/// <summary>
/// Renders a parsed <see cref="KeyBinding"/> back into ZMK <c>.keymap</c>
/// cell text (e.g. <c>&amp;kp LS(N7)</c>). The inverse of the flattening the
/// interpreter performs in <c>ConsumeLogicalParam</c>/<c>FlattenParenExpr</c>
/// — never use <see cref="KeyBinding.Display"/>, which emits the flat param
/// list as separate cells and is not valid ZMK for modifier-wrapped keys.
/// </summary>
public static class ZmkBindingFormatter
{
    /// <summary>
    /// The functions the interpreter flattens out of <c>LS(...)</c>-style
    /// wrappers; anything else (e.g. <c>LSHFT</c> as an &amp;mt param) is a
    /// plain identifier token and stays bare.
    /// </summary>
    private static readonly HashSet<string> ModifierFunctions = new(StringComparer.Ordinal)
    {
        "LS", "RS", "LC", "RC", "LA", "RA", "LG", "RG",
    };

    /// <summary>Behaviors whose first param is a layer reference.</summary>
    internal static readonly HashSet<string> LayerBehaviors = new(StringComparer.Ordinal)
    {
        "&mo", "&to", "&tog", "&sl", "&lt",
    };

    /// <summary>
    /// Formats <paramref name="binding"/> as one or more devicetree cells:
    /// the behavior, then the logical params separated by single spaces.
    /// Consecutive modifier functions re-nest around the param that follows
    /// them (<c>["LC","LS","N7"]</c> → <c>LC(LS(N7))</c>); a trailing bare
    /// modifier stays bare; every other param is emitted verbatim. For the
    /// layer behaviors, a non-negative integer first param is replaced by
    /// the first define in <paramref name="defines"/> whose (trimmed) value
    /// equals it — the file's own name for that layer number, when known.
    /// No arity checking: the writer validates by re-parsing the result.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="binding"/>'s behavior does not start with
    /// <c>&amp;</c> or contains whitespace.
    /// </exception>
    public static string Format(KeyBinding binding, IReadOnlyList<KeyValuePair<string, string>> defines)
    {
        var behavior = binding.Behavior;
        if (string.IsNullOrEmpty(behavior)
            || behavior[0] != '&'
            || behavior.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                $"'{behavior}' is not a valid behavior reference: it must start with '&' and contain no whitespace.",
                nameof(binding));
        }

        var logical = ReNestModifiers(binding.Params);

        if (logical.Count > 0 && LayerBehaviors.Contains(behavior))
            logical[0] = ReplaceLayerRef(logical[0], defines);

        return logical.Count == 0 ? behavior : behavior + " " + string.Join(" ", logical);
    }

    /// <summary>
    /// Walks the params right-to-left; each modifier function with a param
    /// after it wraps the next logical param, so <c>["LC","LS","N7"]</c>
    /// becomes <c>["LC(LS(N7))"]</c>. Any other param, including verbatim
    /// paren expressions like <c>"(LC(LSFT))"</c>, passes through untouched.
    /// </summary>
    private static List<string> ReNestModifiers(IReadOnlyList<string> parameters)
    {
        var stack = new Stack<string>();
        for (int i = parameters.Count - 1; i >= 0; i--)
        {
            var p = parameters[i];
            if (ModifierFunctions.Contains(p) && stack.Count > 0)
                stack.Push(p + "(" + stack.Pop() + ")");
            else
                stack.Push(p);
        }
        // Stack enumerates most-recent-first, i.e. left-to-right here.
        return new List<string>(stack);
    }

    private static string ReplaceLayerRef(string first, IReadOnlyList<KeyValuePair<string, string>> defines)
    {
        var text = first.Trim();
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layer)
            || layer < 0)
        {
            return first;
        }
        foreach (var define in defines)
        {
            if (string.Equals(define.Value.Trim(), text, StringComparison.Ordinal))
                return define.Key;
        }
        return first;
    }
}
