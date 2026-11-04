using System.Text;
using LViz.Core.Keymap.Writer;
using LViz.Core.Models;

namespace LViz.Core.Keymap.Parser;

/// <summary>
/// Parses one binding typed as text (<c>&amp;bt BT_SEL 0</c>,
/// <c>&amp;kp LS(N1)</c>) into the same flat <see cref="KeyBinding"/> the
/// keymap parser produces, or <c>null</c> when the text is not exactly one
/// valid binding. Used by the key dialog's "Custom binding" field and for
/// parameters of the <c>Raw</c> kind.
/// <para>
/// Strategy: wrap the text in a minimal single-layer keymap document and run
/// the real loader over it, so defines, modifier re-nesting and behavior
/// slicing behave exactly as they do in a file. The loader alone is too
/// lenient — it silently drops stray tokens and missing params — so the
/// token counts of the input and the round-tripped result are compared, and
/// known arities are checked against the caller's <see cref="KeyboardConfig"/>.
/// </para>
/// </summary>
public static class ZmkBindingText
{
    /// <summary>
    /// Characters that could break out of the wrapper document built around
    /// the typed text; a binding never legitimately contains any of them.
    /// Line breaks are refused separately (they would split the binding).
    /// </summary>
    private const string ForbiddenCharacters = ";<>{}\"#/\\,=";

    /// <summary>
    /// Parses <paramref name="text"/> as exactly one binding, or returns
    /// <c>null</c> when it is not one. Never throws for bad text.
    /// </summary>
    /// <param name="text">The typed binding text, e.g. <c>&amp;mo LAYER_Nav</c>.</param>
    /// <param name="defines">
    /// The user's file defines (see
    /// <see cref="Preprocessor.CollectDefines"/>) written into the wrapper so
    /// <c>&amp;mo LAYER_Nav</c> resolves like it does in the file.
    /// </param>
    /// <param name="config">
    /// The parsed keymap, used for the arities of user-defined behaviors
    /// (hold-taps take 2 params, a macro takes its declared param count).
    /// <c>null</c> means no user arities.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="defines"/> is <c>null</c>.</exception>
    public static KeyBinding? TryParse(
        string? text,
        IReadOnlyList<KeyValuePair<string, string>> defines,
        KeyboardConfig? config)
    {
        ArgumentNullException.ThrowIfNull(defines);

        // 1. Trim; a binding is non-empty and starts with a behavior ref.
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed[0] != '&') return null;

        // 2. No characters that could escape the wrapper document.
        foreach (var c in trimmed)
        {
            if (c is '\n' or '\r' or '\u2028' or '\u2029') return null;
            if (ForbiddenCharacters.Contains(c)) return null;
        }

        // 3. Top-level tokens: whitespace splits only at parenthesis depth 0.
        var tokens = SplitTopLevel(trimmed);
        if (tokens is null) return null; // unbalanced parentheses
        var logicalCount = tokens.Count - 1;

        // 4. Run the real loader over a single-layer wrapper.
        KeyboardConfig parsed;
        try
        {
            parsed = ZmkKeymapLoader.LoadFromText(BuildWrapperSource(trimmed, defines), "binding-text");
        }
        catch (ZmkKeymapParseException)
        {
            return null;
        }

        // 5. Exactly one layer with exactly one binding.
        if (parsed.Layers.Count != 1 || parsed.Layers[0].Bindings.Count != 1) return null;
        var binding = parsed.Layers[0].Bindings[0];

        // 6. Stray or missing tokens: the round-trip must have the same
        //    number of logical params (&kp A B slices as one binding, "B"
        //    silently dropped — the formatter then sees one param, not two).
        var formatted = ZmkBindingFormatter.Format(binding, Array.Empty<KeyValuePair<string, string>>());
        var formattedCount = formatted.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length - 1;
        if (formattedCount != logicalCount) return null;

        // 7. Known arity (from the table plus the file's hold-taps/macros).
        if (binding.Behavior == "&bt")
        {
            // &bt is special: the command in its first param decides —
            // BT_SEL/BT_DISC need a profile number, the others stand alone.
            // A bare &bt (zero params) is refused too: BtArity(null) is 1.
            var btArity = BehaviorArityTable.BtArity(
                binding.Params.Count > 0 ? binding.Params[0] : null);
            if (logicalCount != btArity) return null;
        }
        else
        {
            var arity = BehaviorArityTable.Arity(binding.Behavior, UserArities(config));
            if (arity >= 0 && logicalCount != arity) return null;
        }

        // 8. Flat params, defines already resolved by the preprocessor.
        return binding;
    }

    /// <summary>
    /// Splits on whitespace at parenthesis depth 0. Returns <c>null</c> when
    /// the parentheses are unbalanced (a stray <c>)</c> or an unclosed <c>(</c>).
    /// </summary>
    private static List<string>? SplitTopLevel(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        int depth = 0;

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        foreach (var c in text)
        {
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth < 0) return null;
            }

            if (depth == 0 && char.IsWhiteSpace(c)) Flush();
            else current.Append(c);
        }

        if (depth != 0) return null;
        Flush();
        return tokens;
    }

    /// <summary>
    /// The file's defines, then a keymap whose only layer holds the typed
    /// text as its whole <c>bindings</c> array.
    /// </summary>
    private static string BuildWrapperSource(string text, IReadOnlyList<KeyValuePair<string, string>> defines)
    {
        var sb = new StringBuilder();
        foreach (var define in defines)
            sb.Append("#define ").Append(define.Key).Append(' ').Append(define.Value).Append('\n');
        sb.Append("/ { keymap { compatible = \"zmk,keymap\"; l { bindings = <")
          .Append(text)
          .Append(">; }; }; };\n");
        return sb.ToString();
    }

    /// <summary>
    /// Arities the static table can't know: hold-taps always take 2 params,
    /// a macro takes its declared param count. Names already carry the
    /// leading <c>&amp;</c>.
    /// </summary>
    private static Dictionary<string, int>? UserArities(KeyboardConfig? config)
    {
        if (config is null) return null;
        var arities = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var holdTap in config.HoldTaps)
            arities[holdTap.Name] = 2;
        foreach (var macro in config.Macros)
            arities[macro.Name] = macro.Params.Count;
        return arities;
    }
}
