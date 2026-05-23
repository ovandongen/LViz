namespace LViz.Core.Keymap.Parser;

/// <summary>
/// Thrown when <see cref="ZmkKeymapLoader"/> cannot produce a usable
/// <see cref="LViz.Core.Models.KeyboardConfig"/> from the source file
/// (e.g. file missing, no <c>zmk,keymap</c> node, malformed beyond recovery).
/// </summary>
public sealed class ZmkKeymapParseException : Exception
{
    public string? FilePath { get; }

    public ZmkKeymapParseException(string message, string? filePath = null, Exception? inner = null)
        : base(message, inner)
    {
        FilePath = filePath;
    }
}
