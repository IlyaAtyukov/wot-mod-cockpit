using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WotModCockpit.App.Stage3;

namespace WotModCockpit.App.Converters;

/// <summary>
/// Серьёзность находки → цвет значка. Палитра та же, что у уровней python.log:
/// красный «ошибка» в панели Problems и красный ERROR в ленте обязаны означать
/// одно и то же, иначе цвет перестаёт что-либо значить.
/// </summary>
public sealed class ProblemSeverityToBrushConverter : IValueConverter
{
    private static readonly Brush ErrorBrush = Frozen(0xF0, 0x70, 0x70);
    private static readonly Brush WarningBrush = Frozen(0xE3, 0xC0, 0x7B);
    private static readonly Brush InfoBrush = Frozen(0x8B, 0x8B, 0x93);

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            ProblemSeverity.Error => ErrorBrush,
            ProblemSeverity.Warning => WarningBrush,
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

/// <summary>
/// Что мастер делает с файлом → короткое слово для колонки предпросмотра.
/// </summary>
public sealed class ScaffoldActionToTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            ScaffoldActionKind.Copy => "копия",
            ScaffoldActionKind.Rename => "переимен.",
            ScaffoldActionKind.Rewrite => "подстановка",
            ScaffoldActionKind.Directory => "каталог",
            ScaffoldActionKind.Create => "новый",
            _ => string.Empty
        };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
