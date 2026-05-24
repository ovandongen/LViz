namespace LViz.Core.Keymap.Parser;

/// <summary>
/// Known ZMK behavior names → number of parameter cells they consume from a
/// <c>bindings = &lt;…&gt;;</c> array. Unknown behaviors fall back to greedy
/// consumption — eat tokens until the next <c>&amp;</c> phandle or end of
/// the cell array.
/// </summary>
public static class BehaviorArityTable
{
    public const int UnknownArity = -1;

    private static readonly Dictionary<string, int> _known = new(StringComparer.Ordinal)
    {
        ["&kp"] = 1,
        ["&trans"] = 0,
        ["&none"] = 0,

        ["&mo"] = 1,
        ["&to"] = 1,
        ["&tog"] = 1,
        ["&sl"] = 1,
        ["&lt"] = 2,
        ["&mt"] = 2,

        ["&out"] = 1,
        ["&sys_reset"] = 0,
        ["&bootloader"] = 0,
        ["&reset"] = 0,
        ["&caps_word"] = 0,
        ["&key_repeat"] = 0,
        ["&studio_unlock"] = 0,
        ["&soft_off"] = 0,

        ["&macro_tap"] = 0,
        ["&macro_press"] = 0,
        ["&macro_release"] = 0,
        ["&macro_pause_for_release"] = 0,
        ["&macro_wait_time"] = 1,
        ["&macro_tap_time"] = 1,
        ["&macro_param_1to1"] = 0,
        ["&macro_param_1to2"] = 0,
        ["&macro_param_2to1"] = 0,
        ["&macro_param_2to2"] = 0,
    };

    public static int Arity(string behavior, IReadOnlyDictionary<string, int>? userDefined = null)
    {
        if (_known.TryGetValue(behavior, out var a)) return a;
        if (userDefined is not null && userDefined.TryGetValue(behavior, out var u)) return u;
        return UnknownArity;
    }
}
