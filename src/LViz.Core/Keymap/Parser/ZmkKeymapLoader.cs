using LViz.Core.Models;

namespace LViz.Core.Keymap.Parser;

/// <summary>
/// Top-level entry point for parsing a ZMK <c>.keymap</c> file. Reads the
/// file, runs the preprocess → lex → parse → interpret pipeline, and
/// returns a <see cref="KeyboardConfig"/> ready for
/// <see cref="LViz.Core.Keymap.LayerBindingResolver"/>.
/// </summary>
public static class ZmkKeymapLoader
{
    /// <summary>
    /// Loads <paramref name="keymapPath"/> and returns the parsed config.
    /// Throws <see cref="ZmkKeymapParseException"/> when the file can't be
    /// read or yields no layers.
    /// </summary>
    public static KeyboardConfig Load(string keymapPath, string keyboardId)
    {
        string source;
        try
        {
            source = File.ReadAllText(keymapPath);
        }
        catch (Exception ex)
        {
            throw new ZmkKeymapParseException(
                $"Could not read keymap file: {ex.Message}", keymapPath, ex);
        }

        return LoadFromText(source, keyboardId, keymapPath);
    }

    /// <summary>
    /// Same pipeline as <see cref="Load"/> but operates on in-memory source
    /// text. Useful for tests and for resources loaded from non-file streams.
    /// </summary>
    public static KeyboardConfig LoadFromText(string source, string keyboardId, string? originPath = null)
    {
        try
        {
            var preprocessed = Preprocessor.Process(source);
            var tokens = DeviceTreeLexer.Tokenize(preprocessed);
            var ast = DeviceTreeParser.Parse(tokens);
            var config = ZmkKeymapInterpreter.Interpret(ast, keyboardId);

            if (config.Layers.Count == 0)
            {
                throw new ZmkKeymapParseException(
                    "No 'zmk,keymap' node with layer children found in file.",
                    originPath);
            }

            // Every layer in a single keymap should have the same number of
            // bindings — one per physical key position. A mismatch usually
            // means the file is truncated or a binding got mistyped (e.g.
            // a missing '&' caused the slicer to drop tokens). Surface it
            // before the UI silently renders a half-empty board.
            var counts = config.Layers
                .Select(l => (l.Name, Count: l.Bindings.Count))
                .ToList();
            var expected = counts[0].Count;
            var oddOnes = counts.Where(c => c.Count != expected).ToList();
            if (oddOnes.Count > 0)
            {
                var detail = string.Join(", ",
                    counts.Select(c => $"'{c.Name}' = {c.Count}"));
                throw new ZmkKeymapParseException(
                    $"Layers have inconsistent binding counts ({detail}). " +
                    "The file may be truncated or a layer's 'bindings' is malformed.",
                    originPath);
            }
            return config;
        }
        catch (ZmkKeymapParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ZmkKeymapParseException(
                $"Failed to parse keymap: {ex.Message}", originPath, ex);
        }
    }
}
