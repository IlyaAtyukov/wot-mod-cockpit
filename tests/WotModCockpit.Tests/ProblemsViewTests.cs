using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WotModCockpit.App.Stage3;
using WotModCockpit.App.ViewModels;
using WotModCockpit.App.Views;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты САМОЙ РАЗМЕТКИ панели Problems.
///
/// <para><b>Зачем отдельно от тестов модели представления.</b> Связки «находка → VS Code»
/// и «находка → браузер API» работали, но пользователь их не видел. Измерено на живом окне:
/// док задавался жёстким <c>Height="230"</c>, полоса кнопок лежала ПОСЛЕДНЕЙ внутри
/// прокрутки карточки, у которой viewport = 143 px при extent = 381 px, — то есть кнопки
/// стояли на 192 px ниже видимой области, и WPF честно рапортовал <c>IsVisible=False</c>.
/// Ни один тест команд этого поймать не мог: команды-то исправны.</para>
///
/// <para>Поэтому здесь разметка поднимается по-настоящему, в окне высотой ровно с тот самый
/// док, и проверяется ГЕОМЕТРИЯ: кнопки обязаны попадать внутрь панели.</para>
/// </summary>
[Collection("WpfBindingTrace")]
public sealed class ProblemsViewTests
{
    /// <summary>Высота дока Problems по умолчанию — та самая, при которой кнопки исчезали.</summary>
    private const double DockHeight = 230;

    /// <summary>Разметка обязана загружаться: неизвестный ключ ресурса = падение при запуске.</summary>
    [Fact]
    public void View_LoadsWithRealApplicationResources()
    {
        object? view = WpfHost.Invoke(() => (object)new ProblemsView());

        Assert.NotNull(view);
        Assert.IsType<ProblemsView>(view);
    }

    /// <summary>
    /// Все три кнопки действий видны при высоте дока 230 px — то есть при той, с которой
    /// панель и открывается. Регрессия ровно на измеренный дефект.
    /// </summary>
    [Fact]
    public void ActionButtons_AreVisibleAtDefaultDockHeight()
    {
        (int total, string[] hidden, string[] outside) = WpfHost.Invoke(() =>
        {
            var vm = new ProblemsViewModel(new StubPreflightBackend(SampleProblems()), new SilentEditor());
            var view = new ProblemsView { DataContext = vm };

            using var host = OffscreenHost.Show(view, 1200, DockHeight);

            vm.RunAsync(Project()).GetAwaiter().GetResult();
            vm.Selected = vm.Problems[0];
            host.Pump();

            Button[] actions =
            [
                .. Descendants<Button>(view).Where(b => b.Content is string s &&
                       (s == "открыть в VS Code" || s == "показать в браузере API" || s == "скрыть правило"))
            ];

            string[] notVisible = [.. actions.Where(b => !b.IsVisible).Select(b => (string)b.Content)];

            // Кнопка может считаться видимой и при этом стоять за нижним краем панели —
            // именно так и было: ScrollViewer её просто не показывал.
            string[] offscreen =
            [
                .. actions
                    .Where(b => b.IsVisible &&
                                b.TranslatePoint(new Point(0, b.ActualHeight), view).Y > view.ActualHeight + 0.5)
                    .Select(b => (string)b.Content)
            ];

            return (actions.Length, notVisible, offscreen);
        });

        Assert.Equal(3, total);
        Assert.Empty(hidden);
        Assert.Empty(outside);
    }

    /// <summary>
    /// Те же три команды доступны из контекстного меню списка: двойной щелчок нигде не был
    /// подписан, а жеста перехода в браузер API из списка не было вообще.
    /// </summary>
    [Fact]
    public void ProblemList_HasContextMenuWithAllThreeActions()
    {
        (string[] headers, int bound) = WpfHost.Invoke(() =>
        {
            var vm = new ProblemsViewModel(new StubPreflightBackend(SampleProblems()), new SilentEditor());
            var view = new ProblemsView { DataContext = vm };

            using var host = OffscreenHost.Show(view, 1200, DockHeight);
            vm.RunAsync(Project()).GetAwaiter().GetResult();
            vm.Selected = vm.Problems[0];
            host.Pump();

            ListBox list = Descendants<ListBox>(view).First(l => l.Name == "ProblemList");
            if (list.ContextMenu is null) return ([], 0);

            // Меню живёт вне визуального дерева, и его DataContext приходит через
            // PlacementTarget. Пока меню не открыто, привязки не вычисляются — а
            // «пункты есть, но команды не привязались» выглядит в окне как рабочее меню,
            // у которого всё серое. Поэтому открываем по-настоящему.
            list.ContextMenu.PlacementTarget = list;
            list.ContextMenu.IsOpen = true;
            host.Pump();

            MenuItem[] items = [.. list.ContextMenu.Items.OfType<MenuItem>()];
            int withCommand = items.Count(i => i.Command is not null);
            list.ContextMenu.IsOpen = false;

            return (items.Select(i => (string)i.Header).ToArray(), withCommand);
        });

        Assert.Contains(headers, h => h.Contains("VS Code", StringComparison.Ordinal));
        Assert.Contains(headers, h => h.Contains("браузере API", StringComparison.Ordinal));
        Assert.Contains(headers, h => h.Contains("правило", StringComparison.Ordinal));
        Assert.Equal(3, bound);
    }

    /// <summary>Подсказка строки называет жесты — иначе о двойном щелчке не узнать ниоткуда.</summary>
    [Fact]
    public void RowToolTip_MentionsGestures()
    {
        var item = new ProblemItemViewModel(SampleProblems()[0], ProblemGrouping.File);

        Assert.Contains("двойной щелчок", item.ToolTipText, StringComparison.Ordinal);
        Assert.Contains("правая кнопка", item.ToolTipText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ вспомогательное

    private static ModProject Project() => new()
    {
        ProjectDir = @"C:\proj",
        ModId = "ru.local.demo",
        PackageName = "demo"
    };

    private static ProblemItem[] SampleProblems() =>
    [
        new()
        {
            Code = "WOT003",
            Severity = ProblemSeverity.Error,
            Message = "gui.battle_control.g_sessionProvider в 1.44 больше нет",
            Explanation = PreflightExplanations.For("WOT003"),
            Hint = "Возьмите сервис через helpers.dependency",
            Reference = "gui/battle_control/__init__.py",
            FilePath = @"C:\proj\src\res\scripts\client\gui\mods\demo\main.py",
            RelativePath = "src/res/scripts/client/gui/mods/demo/main.py",
            Snippet = "provider = g_sessionProvider",
            ApiSymbol = "gui.battle_control",
            Line = 42
        },
        new()
        {
            Code = "WOT005",
            Severity = ProblemSeverity.Warning,
            Message = "fini() не обёрнут в try/except",
            Explanation = PreflightExplanations.For("WOT005"),
            Hint = "Оберните тело в try/except Exception",
            Reference = "gui/mods/__init__.py:_callModMethod",
            FilePath = @"C:\proj\src\res\scripts\client\gui\mods\demo\main.py",
            RelativePath = "src/res/scripts/client/gui/mods/demo/main.py",
            Snippet = "def fini():",
            Line = 88
        }
    ];

    private static List<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        int n = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) found.Add(typed);
            found.AddRange(Descendants<T>(child));
        }

        return found;
    }

    /// <summary>Окно за пределами экрана: без PresentationSource списки не создают контейнеры.</summary>
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

    /// <summary>Шов preflight с заранее готовыми находками: анализ здесь не проверяется.</summary>
    private sealed class StubPreflightBackend(params ProblemItem[] problems) : IPreflightBackend
    {
        public bool IsAvailable => true;

        public string UnavailableReason => string.Empty;

        public IReadOnlyList<PreflightRuleCard> Rules { get; } =
            [new PreflightRuleCard("WOT003", "Символа нет в 1.44", "…", ProblemSeverity.Error)];

        public Task<PreflightReport> AnalyzeAsync(
            ModProject project,
            PreflightOptions options,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PreflightReport(problems, 3, TimeSpan.FromMilliseconds(11)));

        public Task<PyrightProbe> ProbePyrightAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PyrightProbe(false, "в тесте разметки не нужен"));
    }

    private sealed class SilentEditor : IEditorLauncher
    {
        public bool IsAvailable => false;

        public event EventHandler<string>? LaunchFailed
        {
            add { }
            remove { }
        }

        public void OpenAt(string filePath, int line)
        {
        }

        public void OpenFolder(string folderPath)
        {
        }
    }
}
