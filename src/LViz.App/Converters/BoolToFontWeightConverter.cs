using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LViz.App.Converters;

public class BoolToFontWeightConverter : IValueConverter
{
    public static readonly BoolToFontWeightConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontWeight.Bold : FontWeight.SemiBold;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is FontWeight fw && fw >= FontWeight.Bold;
}
