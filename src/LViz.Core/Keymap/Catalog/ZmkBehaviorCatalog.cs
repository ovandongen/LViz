using LViz.Core.Models;

namespace LViz.Core.Keymap.Catalog;

/// <summary>
/// What kind of value a behavior parameter takes. The key dialog shows one
/// editor per parameter (keycode picker, modifier picker, layer list, free
/// text) chosen from this kind.
/// </summary>
public enum ZmkBehaviorParamKind
{
    Keycode,
    Modifier,
    Layer,
    Raw,

    /// <summary>A named command from <see cref="ZmkCommandCatalog"/>,
    /// optionally followed by a profile number. One logical parameter that can
    /// produce 1 or 2 flat binding params (<c>&amp;bt BT_SEL 2</c>).</summary>
    Command,
}

/// <summary>
/// Picker grouping for a behavior. <see cref="Keymap"/> marks behaviors
/// defined in the user's own keymap (hold-taps and macros).
/// </summary>
public enum ZmkBehaviorGroup
{
    Key,
    HoldTap,
    Layer,
    Bluetooth,
    Output,
    Lighting,
    Mouse,
    System,
    Clear,
    Keymap,
}

/// <summary>
/// One pickable behavior for the key dialog. <see cref="Code"/> includes the
/// leading '&amp;'; <see cref="Params"/> lists one entry per binding-cell
/// parameter, in order. Friendly names are UI text (resx), not modelled here.
/// </summary>
public sealed record ZmkBehavior(
    string Code,
    ZmkBehaviorGroup Group,
    IReadOnlyList<ZmkBehaviorParamKind> Params);

/// <summary>
/// The behaviors the key dialog offers: the fixed built-in set plus the
/// hold-taps and macros of the currently loaded keymap. Only codes and
/// parameter kinds — anything else is entered as a raw binding by the UI.
/// </summary>
public static class ZmkBehaviorCatalog
{
    /// <summary>Behaviors whose binding parameter is a layer reference.</summary>
    private static readonly HashSet<string> LayerBindings =
        new(StringComparer.Ordinal) { "&mo", "&to", "&tog", "&sl" };

    /// <summary>
    /// The built-in behaviors, in picker order: the ten v1 behaviors, then the
    /// six command behaviors (their parameter is a named command from
    /// <see cref="ZmkCommandCatalog"/>), then the parameter-less key and
    /// system behaviors.
    /// </summary>
    public static IReadOnlyList<ZmkBehavior> BuiltIn { get; } = new[]
    {
        new ZmkBehavior("&kp", ZmkBehaviorGroup.Key, new[] { ZmkBehaviorParamKind.Keycode }),
        new ZmkBehavior("&mt", ZmkBehaviorGroup.HoldTap, new[] { ZmkBehaviorParamKind.Modifier, ZmkBehaviorParamKind.Keycode }),
        new ZmkBehavior("&sk", ZmkBehaviorGroup.Key, new[] { ZmkBehaviorParamKind.Keycode }),
        new ZmkBehavior("&lt", ZmkBehaviorGroup.HoldTap, new[] { ZmkBehaviorParamKind.Layer, ZmkBehaviorParamKind.Keycode }),
        new ZmkBehavior("&mo", ZmkBehaviorGroup.Layer, new[] { ZmkBehaviorParamKind.Layer }),
        new ZmkBehavior("&to", ZmkBehaviorGroup.Layer, new[] { ZmkBehaviorParamKind.Layer }),
        new ZmkBehavior("&tog", ZmkBehaviorGroup.Layer, new[] { ZmkBehaviorParamKind.Layer }),
        new ZmkBehavior("&sl", ZmkBehaviorGroup.Layer, new[] { ZmkBehaviorParamKind.Layer }),
        new ZmkBehavior("&trans", ZmkBehaviorGroup.Clear, Array.Empty<ZmkBehaviorParamKind>()),
        new ZmkBehavior("&none", ZmkBehaviorGroup.Clear, Array.Empty<ZmkBehaviorParamKind>()),
        new ZmkBehavior("&bt", ZmkBehaviorGroup.Bluetooth, new[] { ZmkBehaviorParamKind.Command }),
        new ZmkBehavior("&out", ZmkBehaviorGroup.Output, new[] { ZmkBehaviorParamKind.Command }),
        new ZmkBehavior("&rgb_ug", ZmkBehaviorGroup.Lighting, new[] { ZmkBehaviorParamKind.Command }),
        new ZmkBehavior("&mkp", ZmkBehaviorGroup.Mouse, new[] { ZmkBehaviorParamKind.Command }),
        new ZmkBehavior("&msc", ZmkBehaviorGroup.Mouse, new[] { ZmkBehaviorParamKind.Command }),
        new ZmkBehavior("&mmv", ZmkBehaviorGroup.Mouse, new[] { ZmkBehaviorParamKind.Command }),
        new ZmkBehavior("&caps_word", ZmkBehaviorGroup.Key, Array.Empty<ZmkBehaviorParamKind>()),
        new ZmkBehavior("&key_repeat", ZmkBehaviorGroup.Key, Array.Empty<ZmkBehaviorParamKind>()),
        new ZmkBehavior("&bootloader", ZmkBehaviorGroup.System, Array.Empty<ZmkBehaviorParamKind>()),
        new ZmkBehavior("&sys_reset", ZmkBehaviorGroup.System, Array.Empty<ZmkBehaviorParamKind>()),
        new ZmkBehavior("&soft_off", ZmkBehaviorGroup.System, Array.Empty<ZmkBehaviorParamKind>()),
        new ZmkBehavior("&studio_unlock", ZmkBehaviorGroup.System, Array.Empty<ZmkBehaviorParamKind>()),
    };

    /// <summary>
    /// <see cref="BuiltIn"/>, then one entry per hold-tap in
    /// <c>config.HoldTaps</c> order, then one per macro in <c>config.Macros</c>
    /// order. A user-defined code that collides with a built-in or with an
    /// earlier user entry is skipped (built-in / earlier wins). Null config →
    /// <see cref="BuiltIn"/>.
    /// </summary>
    public static IReadOnlyList<ZmkBehavior> For(KeyboardConfig? config)
    {
        if (config is null) return BuiltIn;

        var result = new List<ZmkBehavior>(BuiltIn);
        var seen = new HashSet<string>(
            BuiltIn.Select(b => b.Code), StringComparer.Ordinal);

        foreach (var holdTap in config.HoldTaps)
        {
            if (!seen.Add(holdTap.Name)) continue;
            result.Add(new ZmkBehavior(
                holdTap.Name,
                ZmkBehaviorGroup.Keymap,
                new[] { KindOf(holdTap.HoldBinding), KindOf(holdTap.TapBinding) }));
        }

        foreach (var macro in config.Macros)
        {
            if (!seen.Add(macro.Name)) continue;
            result.Add(new ZmkBehavior(
                macro.Name,
                ZmkBehaviorGroup.Keymap,
                Enumerable.Repeat(ZmkBehaviorParamKind.Raw, macro.Params.Count)
                    .ToArray()));
        }

        return result;
    }

    /// <summary>
    /// Exact (Ordinal) lookup of a behavior code in <see cref="For"/>.
    /// Null when unknown.
    /// </summary>
    public static ZmkBehavior? Find(KeyboardConfig? config, string code)
    {
        foreach (var behavior in For(config))
        {
            if (string.Equals(behavior.Code, code, StringComparison.Ordinal))
                return behavior;
        }
        return null;
    }

    /// <summary>
    /// Parameter kind for a hold-tap's hold/tap binding: layer behaviors are
    /// <see cref="ZmkBehaviorParamKind.Layer"/>, <c>&amp;kp</c> is
    /// <see cref="ZmkBehaviorParamKind.Keycode"/>, everything else
    /// (including the user's own behaviors) is
    /// <see cref="ZmkBehaviorParamKind.Raw"/>.
    /// </summary>
    private static ZmkBehaviorParamKind KindOf(string binding) =>
        LayerBindings.Contains(binding) ? ZmkBehaviorParamKind.Layer
        : binding == "&kp" ? ZmkBehaviorParamKind.Keycode
        : ZmkBehaviorParamKind.Raw;
}
