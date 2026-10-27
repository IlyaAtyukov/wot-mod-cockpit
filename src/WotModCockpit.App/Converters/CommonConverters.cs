using System.Collections;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace WotModCockpit.App.Converters;

/// <summary>
/// <c>bool</c> → <see cref="Visibility"/>. Параметр <c>invert</c> переворачивает условие,
/// параметр <c>hidden</c> отдаёт <see cref="Visibility.Hidden"/> вместо <see cref="Visibility.Collapsed"/>.
/// Параметры можно комбинировать: <c>invert,hidden</c>.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value switch
        {
            bool b => b,
            null => false,
            _ => true
        };

        string p = parameter as string ?? string.Empty;
        if (p.Contains("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        Visibility off = p.Contains("hidden", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Hidden
            : Visibility.Collapsed;

        return flag ? Visibility.Visible : off;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>Логическое отрицание. Нужно для «кнопка активна, пока не идёт сборка».</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not bool b || !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not bool b || !b;
}

/// <summary>
/// Непустая строка → <see cref="Visibility.Visible"/>. Параметр <c>invert</c> переворачивает.
/// Именно так гасятся пустые состояния шапки («проект не выбран»).
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool has = !string.IsNullOrWhiteSpace(value as string);
        if ((parameter as string)?.Contains("invert", StringComparison.OrdinalIgnoreCase) == true)
        {
            has = !has;
        }

        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Непустая коллекция (или число &gt; 0) → <see cref="Visibility.Visible"/>.
/// Параметр <c>invert</c> переворачивает: так рисуются пустые состояния списков.
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool any = value switch
        {
            null => false,
            int i => i > 0,
            long l => l > 0,
            ICollection c => c.Count > 0,
            IEnumerable e => e.Cast<object?>().Any(),
            _ => false
        };

        if ((parameter as string)?.Contains("invert", StringComparison.OrdinalIgnoreCase) == true)
        {
            any = !any;
        }

        return any ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Не-<c>null</c> → <see cref="Visibility.Visible"/>. Параметр <c>invert</c> переворачивает.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool has = value is not null;
        if ((parameter as string)?.Contains("invert", StringComparison.OrdinalIgnoreCase) == true)
        {
            has = !has;
        }

        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Полный путь → имя файла. Полный путь при этом остаётся в подсказке —
/// в списке .mtmod'ов важно имя, а не «&lt;папка игры&gt;\mods\1.44.0.0\...».
/// </summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || s.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            string name = Path.GetFileName(s);
            return string.IsNullOrEmpty(name) ? s : name;
        }
        catch (ArgumentException)
        {
            return s;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
