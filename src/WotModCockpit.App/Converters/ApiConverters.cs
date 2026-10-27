using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WotModCockpit.App.ApiBrowser;
using WotModCockpit.Core.ApiIndex;

namespace WotModCockpit.App.Converters;

/// <summary>
/// Перечисление → <c>bool</c> для тумблеров-переключателей режима.
/// Параметр — имя элемента перечисления. Обратное преобразование отдаёт элемент только при
/// установке в true: сброс тумблера НЕ должен менять модель, иначе снятая галочка обнуляла бы
/// режим и левая колонка оставалась бы пустой.
/// </summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is string name
           && string.Equals(value.ToString(), name, StringComparison.Ordinal);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is not string name)
            return Binding.DoNothing;

        Type enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return enumType.IsEnum && Enum.TryParse(enumType, name, out object? parsed)
            ? parsed!
            : Binding.DoNothing;
    }
}

/// <summary>
/// Перечисление → <see cref="System.Windows.Visibility"/>. Тем же параметром-именем, что и
/// <see cref="EnumToBooleanConverter"/>: левая колонка переключает три разных списка, и цепочка
/// «enum → bool → Visibility» в XAML не выражается без своего преобразователя.
/// </summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is not string spec)
            return System.Windows.Visibility.Collapsed;

        string current = value.ToString() ?? string.Empty;

        // Через запятую можно перечислить несколько значений: три каталога делят одну панель.
        bool match = false;
        foreach (string name in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(current, name, StringComparison.Ordinal))
            {
                match = true;
                break;
            }
        }

        return match ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Вид символа → цвет значка. Цвет здесь несёт смысл: класс и событие — самые частые цели
/// поиска, вложенная функция целью не бывает почти никогда и приглушена.
/// </summary>
public sealed class SymbolKindToBrushConverter : IValueConverter
{
    private static readonly Brush ClassBrush = Frozen(0x4E, 0xC9, 0xB0);
    private static readonly Brush FunctionBrush = Frozen(0xDC, 0xDC, 0xAA);
    private static readonly Brush MethodBrush = Frozen(0x6F, 0xB3, 0xE0);
    private static readonly Brush EventBrush = Frozen(0x63, 0xC7, 0x9E);
    private static readonly Brush ModuleBrush = Frozen(0x9E, 0x86, 0xB8);
    private static readonly Brush ConstantBrush = Frozen(0xE8, 0xC2, 0x75);
    private static readonly Brush MutedBrush = Frozen(0x8B, 0x8B, 0x93);

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SymbolKind.Class or SymbolKind.Service => ClassBrush,
        SymbolKind.Function => FunctionBrush,
        SymbolKind.Method => MethodBrush,
        SymbolKind.Event => EventBrush,
        SymbolKind.Module => ModuleBrush,
        SymbolKind.Constant => ConstantBrush,
        _ => MutedBrush
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
/// Категория попадания xref → цвет. Объявление и подписка выделены: именно их ищут,
/// когда спрашивают «где это вызывается»; строковый литерал приглушён как вероятный мусор.
/// </summary>
public sealed class XrefKindToBrushConverter : IValueConverter
{
    private static readonly Brush Declaration = Frozen(0x4E, 0xC9, 0xB0);
    private static readonly Brush Subscription = Frozen(0x63, 0xC7, 0x9E);
    private static readonly Brush Call = Frozen(0x6F, 0xB3, 0xE0);
    private static readonly Brush Noise = Frozen(0x7C, 0x7C, 0x85);
    private static readonly Brush Normal = Frozen(0xB0, 0xB0, 0xB8);

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ApiXrefKind.Declaration => Declaration,
        ApiXrefKind.EventSubscription => Subscription,
        ApiXrefKind.Call => Call,
        ApiXrefKind.StringLiteral or ApiXrefKind.Comment => Noise,
        _ => Normal
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
/// Слой корпуса → прозрачность строки. Сгенерированные ресурсы и вендорный код, если их
/// всё-таки включили галочкой, обязаны отличаться от игрового кода с одного взгляда:
/// иначе выдача из <c>strings.py</c> неотличима от настоящего клиента.
/// </summary>
public sealed class TierToOpacityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SourceTier.Generated => 0.65,
        SourceTier.Vendor => 0.5,
        _ => 1.0
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
