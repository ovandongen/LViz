using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace LViz.App.Converters;

/// <summary>
/// Returns true when all bound values are equal by <c>object.Equals</c>.
/// </summary>
public class EqualityToBoolMultiConverter : IMultiValueConverter
{
    public static readonly EqualityToBoolMultiConverter Instance = new();

    public object Convert(IList<object?> values, System.Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return false;
        var first = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            if (!Equals(first, values[i])) return false;
        }
        return first is not null;
    }
}
