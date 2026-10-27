using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WotModCockpit.App.Stage4;

namespace WotModCockpit.App.Converters;

/// <summary>
/// Число → отступ слева. Нужен схематичному превью: ползунок рисуется не контролом
/// WPF, а двумя прямоугольниками, и положение бегунка — это его левый отступ.
/// </summary>
public sealed class LeftMarginConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double left = value switch
        {
            double d => d,
            int i => i,
            _ => 0
        };

        return new Thickness(Math.Max(0, left), 0, 0, 0);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Серьёзность замечания дизайнера → цвет. Палитра та же, что у панели Problems и у
/// уровней python.log: красный «ошибка» обязан означать одно и то же во всём кокпите.
/// </summary>
public sealed class SettingsIssueToBrushConverter : IValueConverter
{
    private static readonly Brush ErrorBrush = Frozen(0xF0, 0x70, 0x70);
    private static readonly Brush WarningBrush = Frozen(0xE3, 0xC0, 0x7B);
    private static readonly Brush InfoBrush = Frozen(0x8B, 0x8B, 0x93);

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            SettingsIssueSeverity.Error => ErrorBrush,
            SettingsIssueSeverity.Warning => WarningBrush,
            _ => InfoBrush
        };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
