using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WotModCockpit.Core.Models;

namespace WotModCockpit.App.Converters;

/// <summary>
/// <see cref="LogSeverity"/> → кисть строки лога. Кисти берутся из ресурсов приложения
/// (<c>Log.Error</c>, <c>Log.Warning</c>, <c>Log.Info</c>, <c>Log.Debug</c>, <c>Log.Unknown</c>),
/// чтобы палитра жила в одном месте — в <c>Styles/DarkTheme.xaml</c>.
/// </summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    private static readonly Brush Fallback = new SolidColorBrush(Color.FromRgb(0xC6, 0xC6, 0xC6));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            LogSeverity.Error => "Log.Error",
            LogSeverity.Warning => "Log.Warning",
            LogSeverity.Info => "Log.Info",
            LogSeverity.Debug => "Log.Debug",
            _ => "Log.Unknown"
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// <see cref="FrameOrigin"/> → короткая русская метка для значка рядом с кадром traceback.
/// </summary>
public sealed class FrameOriginToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            FrameOrigin.ModProject => "мод",
            FrameOrigin.ClientSource => "клиент",
            FrameOrigin.ResMods => "res_mods",
            _ => "нет файла"
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// <see cref="FrameOrigin"/> → фон значка. «Мой» кадр зелёный, клиентский синеватый,
/// россыпь — жёлтая (правки в ней затрёт следующая Dev-раскладка), нерешённый — серый.
/// </summary>
public sealed class FrameOriginToBrushConverter : IValueConverter
{
    private static readonly Brush Mod = Freeze(0x1D, 0x3A, 0x32);
    private static readonly Brush Client = Freeze(0x1E, 0x2E, 0x3E);
    private static readonly Brush ResMods = Freeze(0x3A, 0x33, 0x1A);
    private static readonly Brush None = Freeze(0x2E, 0x2E, 0x33);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            FrameOrigin.ModProject => Mod,
            FrameOrigin.ClientSource => Client,
            FrameOrigin.ResMods => ResMods,
            _ => None
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
