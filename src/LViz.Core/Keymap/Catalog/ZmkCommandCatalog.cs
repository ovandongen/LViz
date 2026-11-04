using System.Text.RegularExpressions;
using LViz.Core.Keymap.Parser;
using LViz.Core.Models;

namespace LViz.Core.Keymap.Catalog;

/// <summary>
/// One named command a behavior takes as its parameter. <see cref="TakesNumber"/>:
/// a 0-based profile number follows it (<c>&amp;bt BT_SEL 2</c>).
/// <see cref="FromKeymap"/>: not in the bundled list, found in the open keymap.
/// </summary>
public sealed record ZmkCommand(string Name, bool TakesNumber, bool FromKeymap = false);

/// <summary>
/// The named commands a behavior takes as its parameter (<c>&amp;bt BT_SEL</c>,
/// <c>&amp;out OUT_USB</c>, <c>&amp;rgb_ug RGB_TOG</c>, <c>&amp;mkp LCLK</c>, …),
/// for the key dialog's command picker. Bundled names come from ZMK's
/// dt-bindings headers (bt.h, outputs.h, rgb.h, pointing.h, MIT) in picker
/// order; a command the open keymap uses that the bundled list doesn't know
/// (MoErgo's <c>RGB_STATUS</c>) surfaces as a <see cref="ZmkCommand.FromKeymap"/>
/// extra. Codes only — friendly names and explanations are UI text (resx).
/// </summary>
public static class ZmkCommandCatalog
{
    private static readonly ZmkCommand[] NoCommands = Array.Empty<ZmkCommand>();

    /// <summary>
    /// The behaviors whose parameter is a named command, in picker order —
    /// exactly the keys of <see cref="BundledByBehavior"/>.
    /// </summary>
    public static IReadOnlyList<string> Behaviors { get; } =
        new[] { "&bt", "&out", "&rgb_ug", "&mkp", "&msc", "&mmv" };

    /// <summary>The profile numbers the picker offers: ZMK's default 5 profiles.</summary>
    public static IReadOnlyList<int> ProfileNumbers { get; } = new[] { 0, 1, 2, 3, 4 };

    /// <summary>A command-name-shaped parameter token. Also excludes numbers,
    /// nested <c>X(…)</c> remnants and empty strings.</summary>
    private static readonly Regex CommandNamePattern =
        new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private static readonly Dictionary<string, ZmkCommand[]> BundledByBehavior =
        new(StringComparer.Ordinal)
        {
            // &bt's TakesNumber reads BehaviorArityTable.BtCommandsWithProfile —
            // the one place the "command eats a profile number" rule lives.
            ["&bt"] = WithProfileNumbers(
                "BT_SEL", "BT_NXT", "BT_PRV", "BT_CLR", "BT_CLR_ALL", "BT_DISC"),
            ["&out"] = PlainCommands("OUT_USB", "OUT_BLE", "OUT_TOG"),
            ["&rgb_ug"] = PlainCommands(
                "RGB_TOG", "RGB_ON", "RGB_OFF", "RGB_BRI", "RGB_BRD",
                "RGB_HUI", "RGB_HUD", "RGB_SAI", "RGB_SAD",
                "RGB_SPI", "RGB_SPD", "RGB_EFF", "RGB_EFR"),
            ["&mkp"] = PlainCommands("LCLK", "RCLK", "MCLK", "MB4", "MB5"),
            ["&msc"] = PlainCommands("SCRL_UP", "SCRL_DOWN", "SCRL_LEFT", "SCRL_RIGHT"),
            ["&mmv"] = PlainCommands("MOVE_UP", "MOVE_DOWN", "MOVE_LEFT", "MOVE_RIGHT"),
        };

    /// <summary>True for the codes in <see cref="Behaviors"/> (Ordinal);
    /// false for null and anything else.</summary>
    public static bool IsCommandBehavior(string? code) =>
        code is not null && BundledByBehavior.ContainsKey(code);

    /// <summary>Bundled commands of one behavior in picker order, as a fresh
    /// list; empty when not a command behavior.</summary>
    public static IReadOnlyList<ZmkCommand> Bundled(string code) =>
        TryGetBundled(code, out var commands) ? commands.ToArray() : NoCommands;

    /// <summary>
    /// <see cref="Bundled"/> for this behavior, then one entry per command the
    /// keymap uses that the bundled list doesn't know. The walk covers every
    /// layer's bindings (layer order, key order), then every macro's bindings
    /// (<c>config.Macros</c> order), then every combo's binding
    /// (<c>config.Combos</c> order); only a binding of exactly one flat param
    /// with a command-shaped name counts (<c>RGB_COLOR_HSB(0,100,50)</c>
    /// flattens to several params and is skipped). Extras keep first-seen
    /// order. Null config → <see cref="Bundled"/>; a code that isn't a command
    /// behavior → empty.
    /// </summary>
    public static IReadOnlyList<ZmkCommand> For(string code, KeyboardConfig? config)
    {
        if (!TryGetBundled(code, out var bundled)) return NoCommands;
        if (config is null) return bundled.ToArray();

        var result = new List<ZmkCommand>(bundled);
        var seen = new HashSet<string>(bundled.Select(c => c.Name), StringComparer.Ordinal);

        foreach (var binding in config.Layers.SelectMany(l => l.Bindings)
                     .Concat(config.Macros.SelectMany(m => m.Bindings))
                     .Concat(config.Combos.Select(c => c.Binding)))
        {
            if (!string.Equals(binding.Behavior, code, StringComparison.Ordinal)) continue;
            if (binding.Params.Count != 1) continue;
            var name = binding.Params[0];
            if (!CommandNamePattern.IsMatch(name)) continue;
            if (!seen.Add(name)) continue;
            result.Add(new ZmkCommand(name, TakesNumber: false, FromKeymap: true));
        }

        return result;
    }

    /// <summary>Exact (Ordinal) lookup of a command name in
    /// <see cref="For"/>. Null when absent.</summary>
    public static ZmkCommand? Find(string code, string name, KeyboardConfig? config)
    {
        foreach (var command in For(code, config))
        {
            if (string.Equals(command.Name, name, StringComparison.Ordinal))
                return command;
        }
        return null;
    }

    private static bool TryGetBundled(string? code, out ZmkCommand[] commands)
    {
        if (code is not null && BundledByBehavior.TryGetValue(code, out var found))
        {
            commands = found;
            return true;
        }
        commands = NoCommands;
        return false;
    }

    private static ZmkCommand[] WithProfileNumbers(params string[] names) =>
        names
            .Select(n => new ZmkCommand(
                n, BehaviorArityTable.BtCommandsWithProfile.Contains(n)))
            .ToArray();

    private static ZmkCommand[] PlainCommands(params string[] names) =>
        names.Select(n => new ZmkCommand(n, TakesNumber: false)).ToArray();
}
