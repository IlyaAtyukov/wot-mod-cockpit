using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты СТИЛЯ МЕНЮ из <c>Styles/DarkTheme.xaml</c> — того, что общее у всех контекстных
/// меню приложения.
///
/// <para><b>Зачем отдельно от тестов экранов.</b> Читаемость текста проверяет
/// <c>ModsViewTests</c> на настоящем меню «Моды». Но у меню «Проблемы» есть две вещи,
/// которых в «Модах» нет: подсказка жеста справа от пункта («двойной щелчок») и
/// разделитель. Обе легко потерять, написав свой шаблон пункта меню: подсказку —
/// забыв колонку под неё, разделитель — не зная, что меню ищет его стиль по особому
/// ключу, а не по типу. Ни то, ни другое не даёт ошибки: подсказка просто пропадает,
/// а разделитель остаётся светлой системной чертой в тёмном меню.</para>
///
/// <para>Меню здесь собирается в коде, без экрана и его модели: проверяется тема, а
/// не разметка конкретного экрана.</para>
/// </summary>
[Collection("WpfBindingTrace")]
public sealed class ThemeMenuTests
{
    /// <summary>
    /// Подсказка жеста видна, а у пункта без подсказки пустая колонка не занимает места.
    /// </summary>
    [Fact]
    public void MenuItem_ShowsInputGestureText_OnlyWhenThereIsOne()
    {
        (bool shownWithGesture, bool hiddenWithout) = WpfHost.Invoke(() =>
        {
            var withGesture = new MenuItem { Header = "Открыть в VS Code", InputGestureText = "двойной щелчок" };
            var without = new MenuItem { Header = "Показать в браузере API" };
            using var host = OpenMenu(withGesture, without);

            TextBlock? gesture = Descendants<TextBlock>(withGesture)
                .FirstOrDefault(t => t.Text == "двойной щелчок");
            TextBlock? empty = Descendants<TextBlock>(without)
                .FirstOrDefault(t => t.Name == "Gesture");

            return (gesture is { IsVisible: true },
                    empty is null || empty.Visibility == Visibility.Collapsed);
        });

        Assert.True(shownWithGesture, "подсказка жеста «двойной щелчок» пропала из пункта меню");
        Assert.True(hiddenWithout, "у пункта без подсказки пустая колонка жеста занимает место");
    }

    /// <summary>
    /// Разделитель внутри меню — тёмная линия темы, а не светлая системная черта.
    /// <para>
    /// Меню назначает разделителю стиль по ключу <c>MenuItem.SeparatorStyleKey</c>, а не
    /// по типу: общий стиль <c>Separator</c> из темы сюда не доходит.
    /// </para>
    /// </summary>
    [Fact]
    public void Separator_InsideMenu_IsADarkThemeLine()
    {
        double luminance = WpfHost.Invoke(() =>
        {
            var separator = new Separator();
            using var host = OpenMenu(new MenuItem { Header = "Раз" }, separator, new MenuItem { Header = "Два" });

            Border? line = Descendants<Border>(separator)
                .FirstOrDefault(b => b.Background is SolidColorBrush);
            Assert.NotNull(line);
            return Luminance(((SolidColorBrush)line!.Background).Color);
        });

        Assert.True(luminance < 0.15,
            $"разделитель в меню светлый (яркость {luminance:0.00}): в тёмном меню осталась системная черта");
    }

    /// <summary>Открывает меню из переданных пунктов над элементом во внеэкранном окне.</summary>
    private static MenuHost OpenMenu(params object[] items)
    {
        var target = new Border { Width = 200, Height = 100 };
        var window = new Window
        {
            Content = target,
            Width = 300,
            Height = 200,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();

        var menu = new ContextMenu();
        foreach (object item in items)
            menu.Items.Add(item);

        target.ContextMenu = menu;
        menu.PlacementTarget = target;
        menu.IsOpen = true;

        for (int i = 0; i < 8; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
        menu.UpdateLayout();

        return new MenuHost(window, menu);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte value)
        {
            double s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (T deep in Descendants<T>(child))
                yield return deep;
        }
    }

    /// <summary>Открытое меню и окно под ним; закрывает оба.</summary>
    private sealed class MenuHost(Window window, ContextMenu menu) : IDisposable
    {
        public void Dispose()
        {
            menu.IsOpen = false;
            window.Close();
        }
    }
}
