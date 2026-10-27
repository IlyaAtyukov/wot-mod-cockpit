using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WotModCockpit.App.ViewModels;
using WotModCockpit.App.Views;
using WotModCockpit.Core.Mods;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты РАЗМЕТКИ экрана «Моды»: меню правой кнопки над списком модов.
///
/// <para><b>Зачем отдельно от тестов модели представления.</b> Команда
/// <c>OpenProjectCommand</c> существовала и работала задолго до меню — её дёргала кнопка
/// «Открыть проект» справа. Сломаться здесь может не команда, а ЖЕСТ: в WPF правый щелчок
/// выделение НЕ меняет, а меню списка работает через <c>Selected</c>. Пункт «Открыть в VS Code»
/// в таком меню открывал бы проект того мода, который был выбран ДО щелчка, — меню под
/// курсором одно, действие относится к другому. Поймать это тестами модели невозможно:
/// там нет ни мыши, ни контейнеров строк.</para>
///
/// <para>Поэтому здесь поднимается настоящий <see cref="ModsView"/>, в списке настоящие
/// контейнеры строк, и по строке бьют настоящим событием правой кнопки.</para>
/// </summary>
[Collection("WpfBindingTrace")]
public sealed class ModsViewTests
{
    /// <summary>Меню правой кнопки есть, и в нём тот самый пункт.</summary>
    [Fact]
    public void ModList_HasContextMenuWithOpenInVsCode()
    {
        (string[] headers, bool commandsBound) = WpfHost.Invoke(() =>
        {
            var view = new ModsView { DataContext = new FakeMods() };
            using var host = OffscreenHost.Show(view, 1200, 600);

            ListBox list = Descendants<ListBox>(view).First(item => item.Name == "ModList");
            ContextMenu? menu = list.ContextMenu;
            if (menu is null)
            {
                return (Array.Empty<string>(), false);
            }

            // Меню берёт свой DataContext из PlacementTarget, а тот появляется только
            // когда меню ОТКРЫТО. Пока оно закрыто, команды не привязаны ни у одного
            // пункта — и проверять надо именно открытое меню, как им пользуется игрок.
            menu.PlacementTarget = list;
            menu.IsOpen = true;
            host.Pump();

            MenuItem[] items = [.. menu.Items.OfType<MenuItem>()];
            (string[] headers, bool bound) result =
                (items.Select(item => (string)item.Header).ToArray(),
                 items.All(item => item.Command is not null));
            menu.IsOpen = false;
            return result;
        });

        Assert.Contains("Открыть в VS Code", headers);
        Assert.True(commandsBound, "у пунктов меню не привязаны команды");
    }

    /// <summary>
    /// В меню списка есть «Переименовать…» и «Изменить описание…» — правка имени и описания
    /// мода прямо из обозревателя, без поиска кнопки в карточке.
    /// </summary>
    [Fact]
    public void ModList_ContextMenu_HasRenameAndEditDescription()
    {
        string[] headers = WpfHost.Invoke(() =>
        {
            var view = new ModsView { DataContext = new FakeMods() };
            using var host = OffscreenHost.Show(view, 1200, 600);

            ListBox list = Descendants<ListBox>(view).First(item => item.Name == "ModList");
            ContextMenu menu = list.ContextMenu!;
            menu.PlacementTarget = list;
            menu.IsOpen = true;
            host.Pump();

            string[] result = [.. menu.Items.OfType<MenuItem>().Select(item => (string)item.Header)];
            menu.IsOpen = false;
            return result;
        });

        Assert.Contains("Переименовать…", headers);
        Assert.Contains("Изменить описание…", headers);
    }

    /// <summary>
    /// В меню списка есть «Собрать и установить», и это ПОСЛЕДНИЙ пункт: первый пункт берёт
    /// тест прозрачности недоступного пункта, а пишущее в игру действие не должно попадаться
    /// под руку первым.
    /// </summary>
    [Fact]
    public void ModList_ContextMenu_EndsWithBuildAndInstall()
    {
        (string[] headers, bool bound) = WpfHost.Invoke(() =>
        {
            var view = new ModsView { DataContext = new FakeMods() };
            using var host = OffscreenHost.Show(view, 1200, 600);

            ListBox list = Descendants<ListBox>(view).First(item => item.Name == "ModList");
            ContextMenu menu = list.ContextMenu!;
            menu.PlacementTarget = list;
            menu.IsOpen = true;
            host.Pump();

            MenuItem[] items = [.. menu.Items.OfType<MenuItem>()];
            (string[] headers, bool bound) result =
                (items.Select(item => (string)item.Header).ToArray(),
                 items.Last().Command is not null);
            menu.IsOpen = false;
            return result;
        });

        Assert.Equal("Собрать и установить", headers[^1]);
        Assert.True(bound, "у пункта «Собрать и установить» не привязана команда");
    }

    /// <summary>
    /// Предупреждение «в проекте уже другое имя» в карточке видно, когда оно есть, и не
    /// занимает места, когда его нет. Модель это знает — проверяется, что разметка его
    /// показывает.
    /// </summary>
    [Theory]
    [InlineData("Alpha Path 2", true)]
    [InlineData("Alpha Path", false)]
    public void Card_ProjectMetaNotice_IsShownOnlyWhenThereIsOne(string projectName, bool expected)
    {
        (bool visible, string text) = WpfHost.Invoke(() =>
        {
            var data = new FakeMods();
            data.Selected = FakeMods.RowWithProject("Alpha Path", projectName);
            var view = new ModsView { DataContext = data };
            using var host = OffscreenHost.Show(view, 1200, 900);

            Border box = Descendants<Border>(view).First(b => b.Name == "ProjectMetaNoticeBox");
            return (box.IsVisible, ((TextBlock)box.Child).Text);
        });

        Assert.Equal(expected, visible);
        if (expected)
        {
            Assert.Contains("«Alpha Path 2»", text);
        }
    }

    /// <summary>
    /// Проект, которого в игре нет (в том числе только что удалённый мод), в списке читается с
    /// первого взгляда: «НЕ УСТАНОВЛЕН» вместо пустого места справа. А в карточке нет раздела
    /// файла и состава архива — там были бы пустой путь и «meta.xml: неизвестно — архив не
    /// прочитан», будто архив где-то лежит.
    /// </summary>
    [Fact]
    public void NotInstalledRow_IsMarkedInTheList_AndTheCardHasNoArchiveSection()
    {
        (string rowState, bool archiveVisible, bool installedArchiveVisible) = WpfHost.Invoke(() =>
        {
            var data = new FakeMods(withNotInstalled: true);
            InstalledModViewModel ghost = data.Mods.Single(r => !r.IsInstalled);
            data.Selected = ghost;
            var view = new ModsView { DataContext = data };
            using var host = OffscreenHost.Show(view, 1200, 900);

            ListBox list = Descendants<ListBox>(view).First(item => item.Name == "ModList");
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(ghost);
            string state = Descendants<TextBlock>(container).First(t => t.Name == "State").Text;
            bool archive = Descendants<StackPanel>(view).First(p => p.Name == "ArchiveDetails").IsVisible;

            // Та же карточка у установленного мода: раздел на месте.
            var installedData = new FakeMods();
            installedData.Selected = installedData.Mods[0];
            var installedView = new ModsView { DataContext = installedData };
            using var installedHost = OffscreenHost.Show(installedView, 1200, 900);
            bool installedArchive = Descendants<StackPanel>(installedView).First(p => p.Name == "ArchiveDetails").IsVisible;

            return (state, archive, installedArchive);
        });

        Assert.Equal("НЕ УСТАНОВЛЕН", rowState);
        Assert.False(archiveVisible, "у не установленного проекта видна карточка архива");
        Assert.True(installedArchiveVisible, "у установленного мода пропал раздел файла");
    }

    /// <summary>
    /// ГЛАВНАЯ ПРОВЕРКА: правый щелчок по строке СНАЧАЛА выделяет её.
    /// <para>
    /// Без этого меню открывалось бы над одним модом, а «Открыть в VS Code» открывал бы
    /// проект другого — того, что был выбран раньше.
    /// </para>
    /// </summary>
    [Fact]
    public void RightClick_SelectsTheRowUnderCursor()
    {
        (string? selectedBefore, string? selectedAfter) = WpfHost.Invoke(() =>
        {
            var data = new FakeMods();
            var view = new ModsView { DataContext = data };
            using var host = OffscreenHost.Show(view, 1200, 600);

            ListBox list = Descendants<ListBox>(view).First(item => item.Name == "ModList");
            list.SelectedItem = data.Mods[0];
            host.Pump();

            // Вторая строка: по ней и щёлкаем правой кнопкой.
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(data.Mods[1]);
            string? before = data.Selected?.Title;

            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            {
                RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent
            });
            host.Pump();

            return (before, data.Selected?.Title);
        });

        Assert.Equal("Alpha Path", selectedBefore);
        Assert.Equal("Amber Study", selectedAfter);
    }

    /// <summary>
    /// Текст меню ЧИТАЕТСЯ: и в покое, и под курсором.
    /// <para>
    /// Меню долго было светлым системным, а текст — светлым из темы: неявный стиль
    /// <c>TextBlock</c> в <c>DarkTheme.xaml</c> красит в #DCDCDC любой текст приложения,
    /// в том числе заголовки пунктов меню, а стиля для самого меню в теме не было вовсе.
    /// Светло-серое на светло-сером — «текст умирает на фоне».
    /// </para>
    /// <para>
    /// Проверяется не цвет, а КОНТРАСТ — отношение яркостей по WCAG, порог 4.5 (уровень
    /// AA для обычного текста). Цвет можно поменять на любой, лишь бы читалось; тест
    /// сторожит именно «читается», а не конкретный оттенок.
    /// </para>
    /// </summary>
    [Fact]
    public void ContextMenu_TextIsReadable_AtRestAndUnderCursor()
    {
        List<(string header, double atRest, double highlighted)> items = WpfHost.Invoke(() =>
        {
            var view = new ModsView { DataContext = new FakeMods() };
            using var host = OffscreenHost.Show(view, 1200, 600);

            ListBox list = Descendants<ListBox>(view).First(item => item.Name == "ModList");
            ContextMenu menu = list.ContextMenu!;
            menu.PlacementTarget = list;
            menu.IsOpen = true;
            host.Pump();

            var result = new List<(string, double, double)>();
            foreach (MenuItem item in menu.Items.OfType<MenuItem>())
            {
                string header = (string)item.Header;
                TextBlock text = HeaderText(item, header);
                Color ink = ((SolidColorBrush)text.Foreground).Color;
                Color paper = BackgroundBehind(text);
                Color lit = HighlightBackground(item, paper);

                result.Add((header, Contrast(ink, paper), Contrast(ink, lit)));
            }

            menu.IsOpen = false;
            return result;
        });

        Assert.NotEmpty(items);
        foreach ((string header, double atRest, double highlighted) in items)
        {
            Assert.True(atRest >= 4.5,
                $"«{header}»: контраст текста с фоном меню {atRest:0.0} — ниже 4.5, текст сливается");
            Assert.True(highlighted >= 4.5,
                $"«{header}»: контраст под курсором {highlighted:0.0} — ниже 4.5, текст сливается с подсветкой");
        }
    }

    /// <summary>
    /// Недоступный пункт ВЫГЛЯДИТ недоступным.
    /// <para>
    /// У чужого мода нет каталога проекта, и «Открыть в VS Code» для него гаснет. Раньше
    /// этого не было видно: системный шаблон меню гасит пункт цветом текста, а неявный
    /// стиль <c>TextBlock</c> из темы этот цвет перебивает — неявный стиль сильнее
    /// наследования. Пункт выглядел рабочим и не делал ничего. Прозрачность от стиля
    /// текста не зависит, поэтому гаснет пункт ею.
    /// </para>
    /// </summary>
    [Fact]
    public void ContextMenu_DisabledItem_LooksDisabled()
    {
        (double enabled, double disabled) = WpfHost.Invoke(() =>
        {
            var view = new ModsView { DataContext = new FakeMods() };
            using var host = OffscreenHost.Show(view, 1200, 600);

            ListBox list = Descendants<ListBox>(view).First(item => item.Name == "ModList");
            ContextMenu menu = list.ContextMenu!;
            menu.PlacementTarget = list;
            menu.IsOpen = true;
            host.Pump();

            MenuItem item = menu.Items.OfType<MenuItem>().First();
            TextBlock text = HeaderText(item, (string)item.Header);
            double before = VisibleOpacity(text, item);

            item.IsEnabled = false;
            host.Pump();
            double after = VisibleOpacity(text, item);

            menu.IsOpen = false;
            return (before, after);
        });

        Assert.True(enabled > 0.95, $"доступный пункт полупрозрачен ({enabled:0.00})");
        Assert.True(disabled <= 0.5,
            $"недоступный пункт выглядит как доступный: видимость {disabled:0.00}");
    }

    /// <summary>Заголовок пункта меню — тот самый TextBlock, что рисует его текст.</summary>
    private static TextBlock HeaderText(MenuItem item, string header) =>
        Descendants<TextBlock>(item).First(text => text.Text.Replace("_", "") == header);

    /// <summary>
    /// Цвет, на котором лежит текст: слои фона от текста вверх по дереву, пока не
    /// встретится непрозрачный. Полупрозрачные слои накладываются на него, как их
    /// и рисует WPF.
    /// </summary>
    private static Color BackgroundBehind(DependencyObject from)
    {
        var layers = new List<Color>();
        for (DependencyObject? node = from; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            Brush? brush = node switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                _ => null,
            };

            if (brush is SolidColorBrush solid && solid.Color.A > 0)
            {
                layers.Add(solid.Color);
                if (solid.Color.A == 255)
                    break;
            }
        }

        // Снизу вверх: первым кладётся самый дальний (непрозрачный) слой.
        Color result = Colors.White;
        for (int i = layers.Count - 1; i >= 0; i--)
            result = Over(layers[i], result);
        return result;
    }

    /// <summary>
    /// Фон пункта ПОД КУРСОРОМ: сеттер триггера <c>IsHighlighted</c> в шаблоне пункта.
    /// <para>
    /// Навести мышь в тесте нельзя, а флаг подсветки у пункта меню снаружи не ставится.
    /// Поэтому проверяется то, что шаблон ОБЕЩАЕТ нарисовать, — ровно тот цвет, который
    /// появится под курсором.
    /// </para>
    /// </summary>
    private static Color HighlightBackground(MenuItem item, Color underneath)
    {
        Setter? setter = item.Template.Triggers
            .OfType<Trigger>()
            .Where(t => t.Property == MenuItem.IsHighlightedProperty && Equals(t.Value, true))
            .SelectMany(t => t.Setters.OfType<Setter>())
            .FirstOrDefault(s => s.Property.Name == "Background");

        Assert.True(setter?.Value is SolidColorBrush,
            "в шаблоне пункта меню нет подсветки под курсором сплошным цветом — проверять нечего");
        return Over(((SolidColorBrush)setter!.Value).Color, underneath);
    }

    /// <summary>Итоговая непрозрачность текста: произведение Opacity от текста до пункта.</summary>
    private static double VisibleOpacity(UIElement from, UIElement upTo)
    {
        double opacity = 1.0;
        for (DependencyObject? node = from; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement element)
                opacity *= element.Opacity;
            if (ReferenceEquals(node, upTo))
                break;
        }

        return opacity;
    }

    /// <summary>Наложение полупрозрачного цвета на непрозрачный.</summary>
    private static Color Over(Color top, Color bottom)
    {
        double a = top.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(top.R * a + bottom.R * (1 - a)),
            (byte)Math.Round(top.G * a + bottom.G * (1 - a)),
            (byte)Math.Round(top.B * a + bottom.B * (1 - a)));
    }

    /// <summary>Контраст по WCAG 2.x: (L1 + 0.05) / (L2 + 0.05), от 1 до 21.</summary>
    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
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
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (T deep in Descendants<T>(child))
                yield return deep;
        }
    }

    /// <summary>
    /// Подставная модель экрана: ровно то, что связывает разметка списка и его меню.
    /// <para>
    /// Настоящая <see cref="ModsViewModel"/> требует каталога игры, службы библиотеки и
    /// поисковика установки — всего того, к чему проверка ЖЕСТА отношения не имеет.
    /// </para>
    /// </summary>
    private sealed class FakeMods
    {
        public FakeMods(bool withNotInstalled = false)
        {
            List<InstalledModViewModel> rows =
            [
                Row("ru.example.alpha", "Alpha Path", @"C:\repos\wot-mod-alpha"),
                Row("ru.example.amber", "Amber Study", @"C:\repos\wot-mod-amber"),
                Row("tv.example.stuff", "Чужой мод", projectDir: null)
            ];

            if (withNotInstalled)
            {
                var project = new ModProjectInfo(
                    @"C:\repos\wot-mod-delta", "ru.example.delta", "1.0.0", "Delta Path", true, null,
                    @"C:\repos\wot-mod-delta\dist\ru.example.delta_1.0.0.mtmod", "1.0.0", 1000, null);
                rows.Add(InstalledModViewModel.NotInstalled(
                    new ProjectModStatus(project, [], null, ModBuildMatch.Unknown)));
            }

            Mods = rows;
        }

        public IReadOnlyList<InstalledModViewModel> Mods { get; }

        public InstalledModViewModel? Selected { get; set; }

        public ICommand OpenProjectCommand { get; } = new Stub();

        public ICommand RevealCommand { get; } = new Stub();

        public ICommand RenameCommand { get; } = new Stub();

        public ICommand EditDescriptionCommand { get; } = new Stub();

        public ICommand BuildAndInstallCommand { get; } = new Stub();

        private static InstalledModViewModel Row(string modId, string title, string? projectDir)
        {
            var mod = new InstalledMod
            {
                FullPath = @"C:\Games\Tanki\mods\1.44.0.0\" + modId + "_1.0.0.mtmod",
                ModId = modId,
                MetaName = title,
                Mine = projectDir is null
                    ? null
                    : new MyModLink(projectDir, modId, true, null, null, ModBuildMatch.Unknown)
            };

            return new InstalledModViewModel(mod);
        }

        /// <summary>
        /// Установленный свой мод, у которого в игре имя <paramref name="gameName"/>, а в
        /// <c>src\meta.xml</c> проекта — <paramref name="projectName"/>.
        /// </summary>
        public static InstalledModViewModel RowWithProject(string gameName, string projectName)
        {
            const string dir = @"C:\repos\wot-mod-alpha";
            const string path = @"C:\Games\Tanki\mods\1.44.0.0\ru.example.alpha_1.0.0.mtmod";
            var mod = new InstalledMod
            {
                FullPath = path,
                ModId = "ru.example.alpha",
                MetaStatus = ModMetaStatus.Present,
                MetaName = gameName,
                Description = "описание",
                Mine = new MyModLink(dir, "ru.example.alpha", true, null, null, ModBuildMatch.Unknown)
            };
            var project = new ModProjectInfo(dir, "ru.example.alpha", "1.0.0", projectName, true, null, null, null, 0, null)
            {
                MetaName = projectName,
                Description = "описание"
            };

            return new InstalledModViewModel(mod, new ProjectModStatus(project, [path], path, ModBuildMatch.Unknown));
        }

        private sealed class Stub : ICommand
        {
            /// <summary>Заглушке нечего пересчитывать: подписку принимаем и забываем.</summary>
            public event EventHandler? CanExecuteChanged
            {
                add { }
                remove { }
            }

            public bool CanExecute(object? parameter) => true;

            public void Execute(object? parameter)
            {
            }
        }
    }

    /// <summary>Окно за пределами экрана — как в других тестах разметки.</summary>
    private sealed class OffscreenHost : IDisposable
    {
        private readonly Window _window;

        private OffscreenHost(Window window) => _window = window;

        public static OffscreenHost Show(FrameworkElement content, double width, double height)
        {
            var window = new Window
            {
                Content = content,
                Width = width,
                Height = height,
                Left = -32000,
                Top = -32000,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false
            };

            window.Show();
            window.UpdateLayout();
            return new OffscreenHost(window);
        }

        public void Pump()
        {
            for (int i = 0; i < 8; i++)
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
            }

            _window.UpdateLayout();
        }

        public void Dispose() => _window.Close();
    }
}
