using System.Globalization;

namespace LViz.Core.Keymap.Writer;

/// <summary>Small text helpers shared by the keymap writers.</summary>
internal static class WriterText
{
    /// <summary>Escapes a layer name for inside a devicetree string literal:
    /// backslash first, then the quote.</summary>
    public static string EscapeDisplayName(string name) =>
        name.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>A number the way the writers spell it: invariant culture, "-" for null.</summary>
    public static string Num(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "-";
}
