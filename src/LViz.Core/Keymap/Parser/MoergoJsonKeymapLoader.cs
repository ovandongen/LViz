using System.Text.Json;
using System.Text.Json.Serialization;
using LViz.Core.Layout;
using LViz.Core.Models;

namespace LViz.Core.Keymap.Parser;

/// <summary>
/// Parses a Moergo Layout Editor <c>.json</c> export (glove80.com / go60) into
/// the same <see cref="KeyboardConfig"/> the <c>.keymap</c> devicetree path
/// produces, so every downstream consumer is unchanged. Mirrors
/// <see cref="ZmkKeymapLoader"/>'s surface so the two loaders are
/// interchangeable at the call site.
/// <para>
/// Scope is Moergo-only: the export's <c>keyboard</c> field must name a Moergo
/// board (<see cref="MoergoBoards.IsMoergoKeyboardField"/>) or the parse is
/// rejected.
/// </para>
/// </summary>
public static class MoergoJsonKeymapLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Reads <paramref name="path"/> and returns the parsed config. Throws
    /// <see cref="ZmkKeymapParseException"/> when the file can't be read,
    /// isn't a Moergo export, or yields no layers.
    /// </summary>
    public static KeyboardConfig Load(string path, string keyboardId)
    {
        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new ZmkKeymapParseException(
                $"Could not read layout file: {ex.Message}", path, ex);
        }

        return LoadFromText(source, keyboardId, path);
    }

    /// <summary>
    /// Same pipeline as <see cref="Load"/> but operates on in-memory JSON text.
    /// Useful for tests and non-file streams.
    /// </summary>
    public static KeyboardConfig LoadFromText(string json, string keyboardId, string? originPath = null)
    {
        MoergoLayout? layout;
        try
        {
            layout = JsonSerializer.Deserialize<MoergoLayout>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ZmkKeymapParseException(
                $"Failed to parse JSON layout: {ex.Message}", originPath, ex);
        }

        if (layout is null)
            throw new ZmkKeymapParseException("Empty JSON layout.", originPath);

        if (!MoergoBoards.IsMoergoKeyboardField(layout.Keyboard))
            throw new ZmkKeymapParseException(
                $"Not a Moergo layout: keyboard='{layout.Keyboard ?? "<none>"}'. " +
                "JSON layouts are only supported for Moergo boards (glove80 / go60).",
                originPath);

        var layers = MapLayers(layout);
        var macros = (layout.Macros ?? [])
            .Select(m => new ZmkMacro(
                m.Name ?? string.Empty,
                m.Params ?? [],
                (m.Bindings ?? []).Select(MapBinding).ToList()))
            .ToList();
        var holdTaps = (layout.HoldTaps ?? []).Select(MapHoldTap).ToList();
        var combos = (layout.Combos ?? []).Select(MapCombo).ToList();

        var config = new KeyboardConfig(keyboardId, layers, macros, holdTaps, combos);
        KeyboardConfigValidator.Validate(config, originPath);
        return config;
    }

    private static List<Layer> MapLayers(MoergoLayout layout)
    {
        var layers = new List<Layer>();
        var rows = layout.Layers ?? [];
        for (var i = 0; i < rows.Count; i++)
        {
            var name = layout.LayerNames is { } names
                       && i < names.Count
                       && !string.IsNullOrWhiteSpace(names[i])
                ? names[i]
                : $"Layer {i}";
            var bindings = (rows[i] ?? []).Select(MapBinding).ToList();
            layers.Add(new Layer(i, name, bindings));
        }
        return layers;
    }

    private static HoldTap MapHoldTap(MoergoHoldTap h)
    {
        // The Moergo editor stores a hold-tap's two inner behaviors as bare
        // names (e.g. ["&kp", "&kp"]); their cell counts come from the same
        // arity table the devicetree interpreter uses.
        var hold = h.Bindings is { Count: > 0 } b ? b[0] : string.Empty;
        var tap = h.Bindings is { Count: > 1 } b2 ? b2[1] : hold;
        var holdArity = Math.Max(0, BehaviorArityTable.Arity(hold));
        var tapArity = Math.Max(0, BehaviorArityTable.Arity(tap));
        return new HoldTap(h.Name ?? string.Empty, hold, tap, holdArity, tapArity);
    }

    private static ZmkCombo MapCombo(MoergoCombo c) => new(
        c.Name ?? string.Empty,
        c.Description ?? string.Empty,
        c.KeyPositions ?? [],
        c.Layers ?? [],
        c.Binding is { } b ? MapBinding(b) : KeyBinding.None);

    private static KeyBinding MapBinding(MoergoBinding b)
    {
        var binding = new KeyBinding(b.Value ?? "&none", FlattenParams(b.Params));
        if (b.Decoration is { } d)
        {
            binding = binding with
            {
                DecorationLabel = NullIfBlank(d.Label),
                DecorationBackground = NullIfBlank(d.Background),
                DecorationIcon = NullIfBlank(d.Icon),
            };
        }
        return binding;
    }

    /// <summary>
    /// Flattens the recursive Moergo param tree into the flat string shape
    /// <see cref="KeyBinding.Params"/> expects, matching
    /// <c>ZmkKeymapInterpreter.FlattenParenExpr</c>'s depth-first ordering:
    /// a modifier wrap <c>LA(LC(N5))</c> → <c>["LA", "LC", "N5"]</c>.
    /// </summary>
    private static List<string> FlattenParams(IReadOnlyList<MoergoParam>? prms)
    {
        var output = new List<string>();
        if (prms is null) return output;
        foreach (var p in prms) FlattenParam(p, output);
        return output;
    }

    private static void FlattenParam(MoergoParam p, List<string> output)
    {
        output.Add(ValueToString(p.Value));
        if (p.Params is { Count: > 0 })
            foreach (var inner in p.Params) FlattenParam(inner, output);
    }

    // Param values are usually keycode strings ("F1", "LS") but the editor also
    // emits bare integers for layer/positional params ({"value": 2}).
    private static string ValueToString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
        _ => el.GetRawText(),
    };

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

    // ---- JSON DTOs (Moergo Layout Editor export schema) ----

    private sealed record MoergoLayout
    {
        [JsonPropertyName("keyboard")] public string? Keyboard { get; init; }
        [JsonPropertyName("layer_names")] public List<string>? LayerNames { get; init; }
        [JsonPropertyName("layers")] public List<List<MoergoBinding>>? Layers { get; init; }
        [JsonPropertyName("macros")] public List<MoergoMacro>? Macros { get; init; }
        [JsonPropertyName("holdTaps")] public List<MoergoHoldTap>? HoldTaps { get; init; }
        [JsonPropertyName("combos")] public List<MoergoCombo>? Combos { get; init; }
    }

    private sealed record MoergoBinding
    {
        [JsonPropertyName("value")] public string? Value { get; init; }
        [JsonPropertyName("params")] public List<MoergoParam>? Params { get; init; }
        [JsonPropertyName("decoration")] public MoergoDecoration? Decoration { get; init; }
    }

    private sealed record MoergoParam
    {
        // string ("F1", "LS") or number ({"value": 2}) — kept as JsonElement.
        [JsonPropertyName("value")] public JsonElement Value { get; init; }
        [JsonPropertyName("params")] public List<MoergoParam>? Params { get; init; }
    }

    private sealed record MoergoDecoration
    {
        [JsonPropertyName("label")] public string? Label { get; init; }
        [JsonPropertyName("background")] public string? Background { get; init; }
        [JsonPropertyName("icon")] public string? Icon { get; init; }
        // "color" (text foreground) has no model field today — intentionally unmapped.
    }

    private sealed record MoergoMacro
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("params")] public List<string>? Params { get; init; }
        [JsonPropertyName("bindings")] public List<MoergoBinding>? Bindings { get; init; }
    }

    private sealed record MoergoHoldTap
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("bindings")] public List<string>? Bindings { get; init; }
    }

    private sealed record MoergoCombo
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("binding")] public MoergoBinding? Binding { get; init; }
        [JsonPropertyName("keyPositions")] public List<int>? KeyPositions { get; init; }
        [JsonPropertyName("layers")] public List<int>? Layers { get; init; }
    }
}
