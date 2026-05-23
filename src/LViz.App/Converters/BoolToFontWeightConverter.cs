using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LViz.App.Converters;

/// <summary>
/// Maps a bool to an Avalonia <see cref="FontWeight"/>: <c>true</c> →
/// <see cref="FontWeight.Bold"/>, <c>false</c> → <see cref="FontWeight.SemiBold"/>.
/// The false case matches the original literal <c>FontWeight="SemiBold"</c> on
/// the key-label TextBlock in <c>BoardView.axaml</c>, so keys without a
/// user-authored bold flag render identically to before.
/// </summary>
public class BoolToFontWeightConverter : IValueConverter
{
    public static readonly BoolToFontWeightConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontWeight.Bold : FontWeight.SemiBold;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is FontWeight fw && fw >= FontWeight.Bold;
}
