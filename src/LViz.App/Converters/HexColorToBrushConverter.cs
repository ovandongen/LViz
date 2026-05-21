using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LViz.App.Converters;

/// <summary>
/// Converts a hex color string (e.g. "#FF0000") to an Avalonia SolidColorBrush.
/// Returns a dark gray fallback for invalid/empty values.
/// </summary>
public class HexColorToBrushConverter : IValueConverter
{
    public static readonly HexColorToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                if (!hex.StartsWith('#'))
                    hex = "#" + hex;
                // Moergo emits CSS-style #RRGGBBAA; Avalonia expects #AARRGGBB.
                // Reorder so the alpha moves to the front.
                if (hex.Length == 9)
                    hex = "#" + hex.Substring(7, 2) + hex.Substring(1, 6);
                return new SolidColorBrush(Color.Parse(hex));
            }
            catch
            {
                // Fall through to default
            }
        }

        return new SolidColorBrush(Color.Parse("#333333"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
