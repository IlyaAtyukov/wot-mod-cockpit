using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.App.ApiBrowser;
using WotModCockpit.App.ViewModels;
using WotModCockpit.App.Views;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.ApiIndex;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты САМОЙ РАЗМЕТКИ браузера API, а не его модели представления.
/// <para>
/// Зачем они нужны отдельно от <see cref="ApiBrowserViewModelTests"/>. <c>dotnet build</c>
/// про XAML доказывает ровно одно: что разметка компилируется в BAML. Он НЕ проверяет
/// ни одного <c>{StaticResource}</c>: неизвестный ключ — это <c>XamlParseException</c>
/// в момент загрузки окна, то есть падение при запуске приложения на ровном месте.
/// Точно так же он не видит ни опечатки в пути привязки, ни конвертера, который бросает
/// на реальном значении.
/// </para>
/// <para>
/// Поэтому здесь разметка ЗАГРУЖАЕТСЯ по-настоящему: поднимается <see cref="Application"/>
/// с теми же словарями ресурсов, что и в бою (<c>App.xaml</c> → DarkTheme + Controls),
/// экран показывается в окне за пределами экрана, прогоняется по всем пяти панелям
/// с непустыми данными, и всё это время протокол привязок WPF пишется в список.
/// Любая ошибка привязки роняет тест.
/// </para>
/// </summary>
/// <summary>
/// Общая коллекция тестов, которые слушают ПРОТОКОЛ ПРИВЯЗОК WPF.
///
/// <para>Слушатель ставится на <c>PresentationTraceSources.DataBindingSource</c> — источник
/// ОБЩИЙ на весь процесс. Пока один тест держит свой список ошибок, любой другой тест,
/// рисующий окно параллельно, роняет свои сообщения в тот же список: разбор чужой поломки
/// приходит к тому, кто её не вызывал. Одна коллекция на всех означает, что xunit запускает
/// их по очереди, и каждый тест видит ровно свои привязки.</para>
/// </summary>
[CollectionDefinition("WpfBindingTrace", DisableParallelization = true)]
public sealed class WpfBindingTraceCollection
{
}

[Collection("WpfBindingTrace")]
public sealed class ApiBrowserViewTests
{
    /// <summary>
    /// Экран должен ЗАГРУЗИТЬСЯ. Проверка ловит неизвестный ключ ресурса — самую частую
    /// причину падения WPF-приложения сразу после запуска.
    /// </summary>
    [Fact]
    public void View_LoadsWithRealApplicationResources()
    {
        object? view = WpfHost.Invoke(() => (object)new ApiBrowserView());

        Assert.NotNull(view);
        Assert.IsType<ApiBrowserView>(view);
    }

    /// <summary>
    /// Просмотрщик исходника — отдельный контрол со своей разметкой и своим кодом
    /// инициализации (AvalonEdit, панель поиска, слой подсветки строки). Он обязан
    /// создаваться без окна и без файла.
    /// </summary>
    [Fact]
    public void SourceViewer_LoadsAndSurvivesMissingFile()
    {
        string missing = Path.Combine(Path.GetTempPath(), "wotcockpit-no-such-file-" + Guid.NewGuid().ToString("N") + ".py");

        string notice = WpfHost.Invoke(() =>
        {
            var viewer = new WotModCockpit.App.Controls.PythonSourceView
            {
                Width = 600,
                Height = 400
            };

            // Файла нет на диске: индекс строится по дереву, которое пользователь волен
            // снести между двумя щелчками. Это обязано стать сообщением, а не исключением.
            viewer.SourcePath = missing;
            viewer.HighlightedLine = 42;

            Layout(viewer);
            return viewer.Notice;
        });

        // Notice остаётся пустым (замечание — про подсветку), а само отсутствие файла
        // показано оверлеем. Главное: ни одного исключения из UI-потока.
        Assert.NotNull(notice);
    }

    /// <summary>
    /// Просмотрщик обязан ПРОКРУТИТЬ документ к подсвеченной строке.
    /// <para>
    /// Проверка не косметическая. Символ в клиенте живёт на строке 127, 1667 или 3193,
    /// и если после щелчка по результату поиска в панели видно начало файла, то браузер
    /// API не отвечает на свой единственный вопрос — «покажи мне это место».
    /// Одного <c>ScrollToLine</c> в коде для этого мало: WPF обновляет
    /// <c>ScrollViewer.VerticalOffset</c> только на следующем проходе разметки, поэтому
    /// любая арифметика поверх свежепрочитанного <c>VerticalOffset</c> считает по старому
    /// значению и отменяет прокрутку. Ловится это только с настоящим окном и настоящей
    /// разметкой, то есть здесь.
    /// </para>
    /// </summary>
    [Fact]
    public void SourceViewer_ScrollsToHighlightedLine()
    {
        const int lines = 400;
        const int target = 300;

        string path = Path.Combine(Path.GetTempPath(), "wotcockpit-scroll-" + Guid.NewGuid().ToString("N") + ".py");
        var text = new System.Text.StringBuilder();
        for (int i = 1; i <= lines; i++)
            text.Append("x").Append(i).Append(" = ").Append(i).Append("  # line ").Append(i).Append('\n');

        File.WriteAllText(path, text.ToString());

        try
        {
            (int First, int Last) visible = WpfHost.Invoke(() =>
            {
                var viewer = new WotModCockpit.App.Controls.PythonSourceView();

                using OffscreenWindow host = OffscreenWindow.Show(viewer);

                viewer.SourcePath = path;
                viewer.HighlightedLine = target;

                // Файл читается в фоне (Task.Run), прокрутка — уже после того, как текст доедет.
                host.Pump(TimeSpan.FromMilliseconds(900));

                ICSharpCode.AvalonEdit.TextEditor editor =
                    Descendants<ICSharpCode.AvalonEdit.TextEditor>(viewer).First();

                var shown = editor.TextArea.TextView.VisualLines
                    .Select(v => v.FirstDocumentLine.LineNumber)
                    .ToList();

                return shown.Count == 0 ? (0, 0) : (shown.Min(), shown.Max());
            });

            Assert.True(
                visible.First <= target && target <= visible.Last,
                $"строка {target} осталась за кадром: в панели видны строки {visible.First}-{visible.Last}");
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Главная проверка: экран проходит по всем пяти панелям с непустыми данными,
    /// realize'ит шаблоны элементов и НЕ даёт ни одной ошибки привязки.
    /// </summary>
    [Fact]
    public void AllPanes_RenderRealData_WithoutBindingErrors()
    {
        var backend = new ViewFakeBackend();
        var errors = new List<string>();

        int symbolRows = 0;
        int catalogRows = 0;
        int xrefRows = 0;

        WpfHost.Invoke(() =>
        {
            using BindingErrorSink sink = BindingErrorSink.Attach(errors);

            var vm = new ApiBrowserViewModel(backend, new ViewNullSettings(), new ViewNoopEditor());
            vm.AttachSettings(new CockpitSettings { WotSrcDir = @"C:\src" }, () => { });
            vm.Initialize();

            var view = new ApiBrowserView { DataContext = vm };

            using OffscreenWindow host = OffscreenWindow.Show(view);

            // Поиск: дебаунс 150 мс, поэтому крутим очередь с паузой.
            vm.SearchText = "pushMessage";
            host.Pump(TimeSpan.FromMilliseconds(600));

            symbolRows = CountRealized(view, "Results");

            vm.SelectedResult = vm.Results.FirstOrDefault();
            host.Pump(TimeSpan.FromMilliseconds(200));

            xrefRows = CountRealized(view, "XrefHits");

            // Каталоги — по очереди все три: у каждого свой набор пометок в шаблоне
            // (устаревшая запись, «это Event, а не SafeEvent», «только в бою»).
            foreach (ApiBrowserPane pane in new[] { ApiBrowserPane.Events, ApiBrowserPane.Services, ApiBrowserPane.Hotspots })
            {
                vm.Pane = pane;
                host.Pump(TimeSpan.FromMilliseconds(200));
                catalogRows = Math.Max(catalogRows, CountRealized(view, "CatalogItems"));
                vm.SelectedCatalogItem = vm.CatalogItems.FirstOrDefault();
                host.Pump(TimeSpan.FromMilliseconds(200));
            }

            // Дерево модулей: раскрываем корень, чтобы отработал HierarchicalDataTemplate
            // и ленивая подгрузка детей.
            vm.Pane = ApiBrowserPane.Tree;
            host.Pump(TimeSpan.FromMilliseconds(300));

            foreach (ApiTreeNodeViewModel root in vm.TreeRoots)
            {
                root.IsExpanded = true;
            }

            host.Pump(TimeSpan.FromMilliseconds(300));

            // Обратно на поиск и на слои: галочки перезапускают запрос.
            vm.Pane = ApiBrowserPane.Search;
            vm.IncludeGenerated = true;
            vm.IncludeVendor = true;
            host.Pump(TimeSpan.FromMilliseconds(400));

            vm.Dispose();
        });

        Assert.True(symbolRows > 0, "шаблон символа ни разу не применился — проверка привязок ничего не проверила");
        Assert.True(catalogRows > 0, "шаблон записи каталога ни разу не применился");
        Assert.True(xrefRows > 0, "шаблон попадания xref ни разу не применился");

        Assert.True(
            errors.Count == 0,
            "ошибки привязки WPF в разметке браузера API:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// Главное окно тоже обязано загружаться: в него добавлены переключатель экранов и
    /// вложенный <see cref="ApiBrowserView"/>, то есть этап 1 можно сломать разметкой.
    /// Окно строится из НАСТОЯЩЕГО контейнера — так же, как в бою.
    /// </summary>
    [Fact]
    public void MainWindow_WithApiBrowserTab_Loads()
    {
        var errors = new List<string>();

        bool built = WpfHost.Invoke(() =>
        {
            using BindingErrorSink sink = BindingErrorSink.Attach(errors);
            using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();

            // Show() не вызываем: Loaded поднял бы тейлер лога и полез в игру.
            // Конструктора достаточно — именно он грузит BAML обоих экранов.
            var window = provider.GetRequiredService<WotModCockpit.App.MainWindow>();
            bool ok = window.Content is not null;

            window.Close();
            return ok;
        });

        Assert.True(built);
        Assert.True(
            errors.Count == 0,
            "ошибки привязки WPF в главном окне:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    // ================================================================= инфраструктура

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
    }

    /// <summary>
    /// Сколько контейнеров списка реально создано. Нужно не ради числа, а чтобы тест
    /// не превратился в тихо зелёный: если шаблон не применился, привязки в нём никто
    /// не вычислял и проверять было нечего.
    /// </summary>
    /// <param name="root">Корень визуального дерева экрана.</param>
    /// <param name="itemsSourcePath">Имя свойства модели, на которое смотрит список.</param>
    /// <returns>Число созданных контейнеров.</returns>
    private static int CountRealized(DependencyObject root, string itemsSourcePath)
    {
        foreach (ItemsControl list in Descendants<ItemsControl>(root))
        {
            System.Windows.Data.BindingExpression? binding = list.GetBindingExpression(ItemsControl.ItemsSourceProperty);
            if (binding?.ParentBinding.Path?.Path != itemsSourcePath)
                continue;

            int realized = 0;
            for (int i = 0; i < list.Items.Count; i++)
            {
                if (list.ItemContainerGenerator.ContainerFromIndex(i) is not null)
                    realized++;
            }

            if (realized > 0)
                return realized;
        }

        return 0;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is T hit)
                yield return hit;

            foreach (T deep in Descendants<T>(child))
                yield return deep;
        }
    }

    // WpfHost вынесен в отдельный файл (WpfHost.cs): Application — синглтон домена
    // с потоковой привязкой, поэтому хост обязан быть ОДИН на все тесты разметки.

    /// <summary>
    /// Окно за пределами экрана. Оно нужно именно как окно: без <c>PresentationSource</c>
    /// виртуализованные списки не создают контейнеры, и шаблоны элементов остаются
    /// непроверенными. Не активируется и не появляется в панели задач.
    /// </summary>
    private sealed class OffscreenWindow : IDisposable
    {
        private readonly Window _window;

        private OffscreenWindow(Window window) => _window = window;

        public static OffscreenWindow Show(UIElement content)
        {
            var window = new Window
            {
                Content = content,
                Width = 1600,
                Height = 900,
                Left = -32000,
                Top = -32000,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false,
                AllowsTransparency = false
            };

            window.Show();
            window.UpdateLayout();

            return new OffscreenWindow(window);
        }

        /// <summary>Крутит очередь диспетчера, давая отработать таймерам и привязкам.</summary>
        /// <param name="duration">Сколько крутить.</param>
        public void Pump(TimeSpan duration)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < duration)
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
                Thread.Sleep(15);
            }

            _window.UpdateLayout();
        }

        public void Dispose() => _window.Close();
    }

    /// <summary>Ловушка протокола привязок WPF на время одной проверки.</summary>
    private sealed class BindingErrorSink : IDisposable
    {
        private readonly TraceListener _listener;
        private readonly SourceLevels _previous;

        private BindingErrorSink(TraceListener listener, SourceLevels previous)
        {
            _listener = listener;
            _previous = previous;
        }

        public static BindingErrorSink Attach(List<string> sink)
        {
            PresentationTraceSources.Refresh();

            SourceLevels previous = PresentationTraceSources.DataBindingSource.Switch.Level;
            var listener = new CollectingListener(sink);

            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

            return new BindingErrorSink(listener, previous);
        }

        public void Dispose()
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(_listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = _previous;
            _listener.Dispose();
        }

        private sealed class CollectingListener : TraceListener
        {
            private readonly List<string> _sink;

            public CollectingListener(List<string> sink) => _sink = sink;

            public override void Write(string? message)
            {
            }

            public override void WriteLine(string? message)
            {
                if (!string.IsNullOrWhiteSpace(message))
                    _sink.Add(message!);
            }
        }
    }

    // ---------------------------------------------------------------- подставные сервисы

    /// <summary>Шов с непустыми ответами: разметке нужны данные, иначе шаблоны не применятся.</summary>
    private sealed class ViewFakeBackend : IApiBrowserBackend
    {
        public ApiIndexStatus Status { get; } = new()
        {
            State = ApiIndexState.Ready,
            TreePath = @"C:\src",
            FilesTotal = 8052,
            FilesDone = 8052,
            SkippedFiles = 10,
            SymbolCount = 156_710,
            Elapsed = TimeSpan.FromMilliseconds(719)
        };

        public event EventHandler<ApiIndexStatus>? StatusChanged;

        public Task<ApiIndexStatus> ReindexAsync(string? treePath, CancellationToken cancellationToken)
        {
            StatusChanged?.Invoke(this, Status);
            return Task.FromResult(Status);
        }

        public Task<IReadOnlyList<ApiSymbol>> SearchAsync(string query, ApiSearchFilter filter, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ApiSymbol>>(new[]
            {
                Sym("pushMessage", SymbolKind.Method, "gui.SystemMessages", "SystemMessagesInterface", 127, 3, SourceTier.Game),
                Sym("IItemsCache", SymbolKind.Class, "skeletons.gui.shared", null, 8, 1, SourceTier.Game),
                Sym("_label", SymbolKind.Class, "gui.impl.gen.resources.strings", "account", 4210, 279, SourceTier.Generated),
                Sym("urlopen", SymbolKind.Function, "site-packages.future.request", null, 55, 2, SourceTier.Vendor)
            });

        public Task<IReadOnlyList<ApiTreeNode>> GetTreeChildrenAsync(string? parentRelativePath, ApiSearchFilter filter, CancellationToken cancellationToken)
        {
            if (parentRelativePath is null)
            {
                return Task.FromResult<IReadOnlyList<ApiTreeNode>>(new[]
                {
                    new ApiTreeNode("client", "client", true) { SymbolCount = 123_364 },
                    new ApiTreeNode("client_common", "client_common", true) { SymbolCount = 1458 },
                    new ApiTreeNode("common", "common", true) { SymbolCount = 31_888 }
                });
            }

            return Task.FromResult<IReadOnlyList<ApiTreeNode>>(new[]
            {
                new ApiTreeNode("gui", parentRelativePath + "/gui", true) { SymbolCount = 42 },
                new ApiTreeNode("PlayerEvents.py", parentRelativePath + "/PlayerEvents.py", false)
                {
                    SymbolCount = 81,
                    VirtualPath = "scripts/" + parentRelativePath + "/PlayerEvents.py",
                    FullPath = @"C:\src\" + parentRelativePath.Replace('/', '\\') + @"\PlayerEvents.py"
                }
            });
        }

        public Task<IReadOnlyList<ApiSymbol>> GetFileSymbolsAsync(string relativePath, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ApiSymbol>>(new[]
            {
                Sym("_PlayerEvents", SymbolKind.Class, "PlayerEvents", null, 12, 1, SourceTier.Game)
            });

        public Task<ApiXrefResult> FindReferencesAsync(ApiXrefRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ApiXrefResult(
                request.Receiver is null ? request.Name : request.Receiver + "." + request.Name,
                new[]
                {
                    new ApiXrefHit("client/Avatar.py", "scripts/client/Avatar.py", @"C:\src\client\Avatar.py", 2476, "g_playerEvents.onAvatarReady()", ApiXrefKind.Call),
                    new ApiXrefHit("client/DualAccuracyBase.py", "scripts/client/DualAccuracyBase.py", @"C:\src\client\DualAccuracyBase.py", 126, "g_playerEvents.onAvatarReady += self.__onAvatarReady", ApiXrefKind.EventSubscription),
                    new ApiXrefHit("client/gui/mods.py", "scripts/client/gui/mods.py", @"C:\src\client\gui\mods.py", 44, "'onAvatarReady'", ApiXrefKind.StringLiteral)
                },
                3,
                3,
                51,
                new[] { "g_playerEvents", "settings" },
                TimeSpan.FromMilliseconds(3.4)));

        public Task<ApiSymbol?> ResolveVirtualPathAsync(string virtualPath, int line, CancellationToken cancellationToken)
            => Task.FromResult<ApiSymbol?>(Sym("pushMessage", SymbolKind.Method, "gui.SystemMessages", "SystemMessagesInterface", 127, 3, SourceTier.Game));

        public Task<IReadOnlyList<ApiCatalogEntry>> GetCatalogAsync(ApiCatalogKind kind, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ApiCatalogEntry>>(new[]
            {
                new ApiCatalogEntry
                {
                    Kind = kind,
                    Name = "onArenaPeriodChange",
                    Owner = "ClientArena",
                    AccessPath = "BigWorld.player().arena",
                    Signature = "(period, periodEndTime, periodLength, periodAdditionalInfo)",
                    Badge = "Event",
                    Detail = "поднимается из ClientArena.__onPeriodChange",
                    VirtualPath = "scripts/client_common/ClientArena.py",
                    RelativePath = "client_common/ClientArena.py",
                    FullPath = @"C:\src\client_common\ClientArena.py",
                    Line = 270,
                    Weight = 32,
                    ThrowsToClient = true,
                    BattleOnly = true
                },
                new ApiCatalogEntry
                {
                    Kind = kind,
                    Name = "ServerInfoPresenter.__updateModel",
                    Owner = "ServerInfoPresenter",
                    Badge = "1 мод",
                    Detail = "патчит XVM",
                    IsStale = true
                }
            });

        public ApiSnippet? BuildSnippet(ApiSnippetRequest request)
            => new("подписка на onArenaPeriodChange", "def init():\n    pass\n", "исключение из обработчика улетит в клиент");

        private static ApiSymbol Sym(
            string name,
            SymbolKind kind,
            string module,
            string? owner,
            int line,
            int declarations,
            SourceTier tier)
        {
            string relative = module.Replace('.', '/') + ".py";

            return new ApiSymbol(
                name,
                owner is null ? name : owner + "." + name,
                kind,
                module,
                "scripts/client/" + relative,
                "client/" + relative,
                @"C:\src\client\" + relative.Replace('/', '\\'),
                line,
                kind is SymbolKind.Method or SymbolKind.Function ? "(self, message)" : null)
            {
                Owner = owner,
                DeclarationCount = declarations,
                Tier = tier,
                Decorators = DecoratorFlags.Property | DecoratorFlags.Static
            };
        }
    }

    private sealed class ViewNullSettings : ISettingsStore
    {
        public string SettingsPath => Path.Combine(Path.GetTempPath(), "cockpit-view-test.json");

        public CockpitSettings Load() => new();

        public void Save(CockpitSettings settings)
        {
            // тест не пишет в профиль пользователя
        }
    }

    private sealed class ViewNoopEditor : IEditorLauncher
    {
        public bool IsAvailable => false;

        public event EventHandler<string>? LaunchFailed
        {
            add { }
            remove { }
        }

        public void OpenFolder(string path)
        {
        }

        public void OpenAt(string filePath, int line)
        {
        }
    }
}
