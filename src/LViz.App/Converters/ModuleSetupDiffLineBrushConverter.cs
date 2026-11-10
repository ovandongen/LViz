using System.Globalization;
using Avalonia.Data.Converters;
using LViz.App.Services;
using LViz.App.ViewModels;

namespace LViz.App.Converters;

/// <summary>
/// Colours one review diff line: added green, removed red, a collapsed gap
/// subtle, plain context text regular, with the <see cref="AppTheme"/> brushes
/// the window itself uses.
/// </summary>
internal sealed class ModuleSetupDiffLineBrushConverter : IValueConverter
{
    public static readonly ModuleSetupDiffLineBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ModuleSetupDiffLine { IsAdded: true } => AppTheme.AccentGreenBrush,
            ModuleSetupDiffLine { IsRemoved: true } => AppTheme.AccentRedBrush,
            ModuleSetupDiffLine { IsGap: true } => AppTheme.TextSubtleBrush,
            _ => AppTheme.TextBrush,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
