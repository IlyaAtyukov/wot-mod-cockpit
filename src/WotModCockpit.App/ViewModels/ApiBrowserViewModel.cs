using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WotModCockpit.App.ApiBrowser;
using WotModCockpit.App.Collections;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.ApiIndex;
using WotModCockpit.Core.Models;

namespace WotModCockpit.App.ViewModels;

/// <summary>Какой список показывает левая колонка браузера API.</summary>
public enum ApiBrowserPane
{
    /// <summary>Поиск символов по индексу.</summary>
    Search = 0,

    /// <summary>Дерево модулей с ленивой подгрузкой.</summary>
    Tree = 1,

    /// <summary>Каталог событий.</summary>
    Events = 2,

    /// <summary>Каталог сервисов.</summary>
    Services = 3,

    /// <summary>Каталог горячих точек.</summary>
    Hotspots = 4
}

/// <summary>
/// Модель представления экрана «Браузер API» — второго экрана кокпита.
/// <para>
/// Экран отвечает на один вопрос: «у меня 8052 файла незнакомого кода, что мне перехватить,
/// чтобы узнать о событии X». Отсюда три зоны: слева поиск/дерево/каталоги, в центре исходник
/// клиента, справа «где это вызывается» и генератор сниппетов.
/// </para>
/// <para>
/// Правила, которым здесь подчинено всё остальное:
/// </para>
/// <list type="number">
///   <item><b>UI не морозится.</b> Индексация, поиск, xref, каталоги и чтение файлов уходят
///         в пул потоков. Отменяются по-настоящему (прекращают работу) индексация, xref и
///         построение каталогов; поиск по символам ранжирует до конца, и у него отбрасывается
///         только результат — цена запроса 0.1-17 мс, прерывать там нечего. Набор текста и смена
///         выделения идут через задержку 150 мс: она нужна не ради скорости запроса, а чтобы
///         пролистывание списка стрелками не запускало по полному проходу дерева на строку.</item>
///   <item><b>Отсутствие индекса — это состояние экрана, а не авария.</b> Дерева исходников
///         может не быть вовсе, реализация Core может быть ещё не подключена: во всех случаях
///         экран открывается и объясняет, что происходит и что сделать.</item>
///   <item><b>Слои корпуса отсечены по умолчанию.</b> Сгенерированные ресурсы и вендорный код —
///         это 36.5 % символов, которые к моду отношения не имеют; они включаются галочками.</item>
/// </list>
/// </summary>
public sealed partial class ApiBrowserViewModel : ObservableObject, IDisposable
{
    /// <summary>Задержка перед поиском после нажатия клавиши.</summary>
    private const int SearchDebounceMs = 150;

    /// <summary>
    /// Задержка перед поиском ссылок после смены выделения. Нужна ровно по той же причине,
    /// что и у поиска, но цена ошибки здесь другая: xref — это ПОЛНОЕ перечитывание 8052 файлов
    /// (78 МБ). Замерено на живом окне без задержки: 40 нажатий стрелки вниз подряд дали
    /// p95 = 50 мс и максимум 313 мс на UI-потоке, и результат всех сорока запросов, кроме
    /// последнего, всё равно выбрасывался.
    /// </summary>
    private const int XrefDebounceMs = 150;

    /// <summary>
    /// Через сколько миллисекунд индексации показывать индикатор. Тёплое построение занимает
    /// 105-127 мс и обязано пройти незаметно; холодное (719 мс) индикатор покажет.
    /// </summary>
    private const int ProgressDelayMs = 300;

    private readonly IApiBrowserBackend _backend;
    private readonly ISettingsStore _settingsStore;
    private readonly IEditorLauncher _editor;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _xrefDebounce;
    private readonly DispatcherTimer _progressDelay;

    private readonly List<CatalogItemViewModel> _catalogAll = new();

    private CockpitSettings? _settings;
    private Action? _saveSettings;

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _xrefCts;
    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _catalogCts;

    private bool _initialized;
    private bool _disposed;
    private ApiCatalogKind? _loadedCatalog;

    /// <summary>
    /// Номер последнего запроса каталога. Ответ, пришедший на устаревший запрос, отбрасывается:
    /// см. <see cref="LoadCatalogAsync"/>.
    /// </summary>
    private int _catalogGeneration;

    /// <summary>
    /// Что выбрано ПОСЛЕДНИМ — запись каталога (<c>true</c>) или символ индекса (<c>false</c>).
    /// <para>
    /// Нужно ровно для сниппетов и решает конкретную путаницу. Выбор в списке каталога никуда
    /// не девается при уходе на другую панель, а генератор отдаёт записи каталога приоритет
    /// перед символом — в ней есть резолвер владельца, восстановленная сигнатура и
    /// предупреждения. Без этого признака «выбрал событие в каталоге → ушёл на поиск →
    /// нашёл метод → нажал монкипатч» молча копировало бы подписку на событие.
    /// </para>
    /// </summary>
    private bool _snippetFromCatalog;

    /// <summary>Создаёт модель представления. Ничего не читает с диска — это делает <see cref="Initialize"/>.</summary>
    /// <param name="backend">Шов к индексу и каталогам Core.</param>
    /// <param name="settingsStore">Хранилище настроек: отсюда берётся путь к дереву исходников.</param>
    /// <param name="editor">Внешний редактор для кнопки «открыть в VS Code».</param>
    public ApiBrowserViewModel(IApiBrowserBackend backend, ISettingsStore settingsStore, IEditorLauncher editor)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));

        _dispatcher = Dispatcher.CurrentDispatcher;

        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(SearchDebounceMs)
        };
        _searchDebounce.Tick += OnSearchDebounceTick;

        _xrefDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(XrefDebounceMs)
        };
        _xrefDebounce.Tick += OnXrefDebounceTick;

        _progressDelay = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(ProgressDelayMs)
        };
        _progressDelay.Tick += OnProgressDelayTick;

        _backend.StatusChanged += OnBackendStatusChanged;

        ApplyStatus(_backend.Status);
    }

    // =================================================================== состояние индекса

    /// <summary>Последнее известное состояние индекса.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyStateText))]
    [NotifyPropertyChangedFor(nameof(HasIndex))]
    [NotifyPropertyChangedFor(nameof(IsIndexing))]
    [NotifyCanExecuteChangedFor(nameof(ReindexCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelIndexCommand))]
    private ApiIndexStatus _indexStatus = new();

    /// <summary>Строка состояния индекса для нижней панели.</summary>
    [ObservableProperty]
    private string _indexStatusText = string.Empty;

    /// <summary>Путь к дереву декомпилированных исходников.</summary>
    [ObservableProperty]
    private string _treePath = string.Empty;

    /// <summary>
    /// Показывать индикатор индексации. Ставится не сразу, а через <see cref="ProgressDelayMs"/>:
    /// тёплое построение короче задержки и мигать индикатором на нём незачем.
    /// </summary>
    [ObservableProperty]
    private bool _showIndexProgress;

    /// <summary>Индексом можно пользоваться.</summary>
    public bool HasIndex => IndexStatus.IsReady;

    /// <summary>Индекс сейчас строится.</summary>
    public bool IsIndexing => IndexStatus.State == ApiIndexState.Indexing;

    /// <summary>
    /// Индикатор без делений. Реализация Core вправе не сообщать число файлов заранее
    /// (перечисление дерева само по себе стоит 20-25 мс) — тогда показывается бегунок.
    /// </summary>
    public bool IndexProgressIndeterminate => IndexStatus.FilesTotal <= 0;

    /// <summary>
    /// Что показать вместо содержимого, когда индекса нет. Это не «пусто», а инструкция:
    /// пользователь должен понять, откуда вообще берутся исходники клиента.
    /// </summary>
    public string EmptyStateText => IndexStatus.State switch
    {
        ApiIndexState.NotConfigured =>
            "Дерево исходников клиента не указано.\n\n"
            + "Браузер API работает по дампу декомпилированного клиента: 8052 файла .py,\n"
            + "полученных uncompyle6 из scripts.pkg вашей версии игры.\n"
            + "В шаблоне мода такой дамп лежит в  <проект>\\.scratch\\wot-src\\  —\n"
            + "внутри должны быть папки client\\, client_common\\ и common\\.\n\n"
            + "Нажмите «выбрать папку…» и укажите её.",

        ApiIndexState.Missing =>
            $"Каталог не найден или не похож на дамп исходников:\n{TreePath}\n\n"
            + "Внутри ожидаются папки client\\, client_common\\ и common\\.\n"
            + "Укажите другую папку кнопкой «выбрать папку…».",

        ApiIndexState.Indexing =>
            "Индексация исходников клиента…\n\n"
            + "Полное построение занимает около 0.7 с на холодном кэше\n"
            + "и около 0.1 с на тёплом. Окно при этом не блокируется.",

        ApiIndexState.Failed =>
            "Индекс не построен.\n\n"
            + (IndexStatus.Error ?? "причина неизвестна")
            + "\n\nЭкран останется пустым, но кокпит работает: сборка и лента python.log не зависят от индекса.",

        _ => "Введите имя символа в поле поиска слева.\n\n"
             + "Например: pushMessage, IItemsCache, onArenaPeriodChange, arenaperiod.\n"
             + "Работают точное совпадение, префикс, акроним по горбам CamelCase и нечёткий поиск."
    };

    private bool CanReindex() => !IsIndexing;

    /// <summary>Перестроить индекс с нуля.</summary>
    [RelayCommand(CanExecute = nameof(CanReindex))]
    private Task ReindexAsync() => BuildIndexAsync(null);

    private bool CanCancelIndex() => IsIndexing;

    /// <summary>Прервать построение индекса.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelIndex))]
    private void CancelIndex()
    {
        _indexCts?.Cancel();
        StatusMessage = "индексация прервана";
    }

    /// <summary>Выбрать каталог с декомпилированными исходниками клиента.</summary>
    [RelayCommand]
    private async Task BrowseTreeAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Каталог декомпилированных исходников клиента (внутри client\\, client_common\\, common\\)",
            Multiselect = false
        };

        try
        {
            if (!string.IsNullOrWhiteSpace(TreePath) && Directory.Exists(TreePath))
                dialog.InitialDirectory = TreePath;
        }
        catch (Exception)
        {
            // Путь мог стать недоступным (снятый сетевой диск) — диалог просто откроется по умолчанию.
        }

        if (dialog.ShowDialog() != true)
            return;

        await BuildIndexAsync(dialog.FolderName).ConfigureAwait(true);
    }

    // =================================================================== левая колонка

    /// <summary>Какой список показан слева.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCatalogPane))]
    [NotifyPropertyChangedFor(nameof(CatalogHint))]
    [NotifyPropertyChangedFor(nameof(CatalogEmptyText))]
    private ApiBrowserPane _pane = ApiBrowserPane.Search;

    /// <summary>Слева показан один из доменных каталогов.</summary>
    public bool IsCatalogPane => Pane is ApiBrowserPane.Events or ApiBrowserPane.Services or ApiBrowserPane.Hotspots;

    /// <summary>
    /// Что за каталог сейчас открыт — строкой, для подсказки над списком.
    /// <para>
    /// Сюда же вынесено предупреждение про <c>Event</c> и <c>SafeEvent</c>: оно верно для
    /// 1023 записей из 1030, то есть это свойство КОРПУСА, а не строки списка. На строке оно
    /// занимало половину высоты и не различало ничего.
    /// </para>
    /// </summary>
    public string CatalogHint => Pane switch
    {
        ApiBrowserPane.Events =>
            "События клиента: self.onX = Event.Event(). Загрузчик отдаёт моду только 6 событий "
            + "(они закреплены сверху), остальные надо добывать подпиской — здесь они все.\n"
            + "Почти все они Event, а не SafeEvent: исключение из вашего обработчика улетит в клиент — "
            + "заворачивайте тело в try/except. Не знаете слова клиента — напишите «бой», «ангар», «урон».",
        ApiBrowserPane.Services =>
            "Сервисы: интерфейсы из client/skeletons/**. Берутся через dependency.instance(I…). "
            + "Список отсортирован по числу обращений в клиенте, а не по алфавиту.",
        ApiBrowserPane.Hotspots => HasIndex
            ? "Горячие точки: что на практике патчат реальные моды из mods\\. "
              + "Красным помечено то, чего в вашем клиенте больше нет."
            : "Горячие точки: что на практике патчат реальные моды из mods\\. "
              + "Дерево исходников не указано, поэтому по вашему клиенту НИЧЕГО не проверено — "
              + "красных пометок не будет, даже если патч мёртв.",
        _ => string.Empty
    };

    // ---- поиск

    /// <summary>Запрос поиска символов.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Результаты поиска.</summary>
    public BulkObservableCollection<SymbolViewModel> Results { get; } = new();

    /// <summary>Выбранный результат.</summary>
    [ObservableProperty]
    private SymbolViewModel? _selectedResult;

    /// <summary>Идёт поиск.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftBusyText))]
    private bool _isSearching;

    /// <summary>Строятся доменные каталоги — самая долгая операция экрана (657-924 мс).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftBusyText))]
    [NotifyCanExecuteChangedFor(nameof(CancelCatalogCommand))]
    private bool _isCatalogLoading;

    /// <summary>
    /// Что показать в нижней полоске левой колонки. Одна строка на все длинные операции этой
    /// колонки: пустая — колонка ничем не занята.
    /// </summary>
    public string LeftBusyText => IsCatalogLoading
        ? "строим каталоги: это второй полный проход по дереву, около 0.7 с…"
        : IsSearching
            ? "ищем…"
            : string.Empty;

    /// <summary>Сводка выдачи: сколько нашлось и не упёрлось ли в потолок.</summary>
    [ObservableProperty]
    private string _searchStatus = string.Empty;

    /// <summary>Искать и в сгенерированных ресурсах (21.5 % символов, почти чистый шум).</summary>
    [ObservableProperty]
    private bool _includeGenerated;

    /// <summary>Искать и в вендорном коде (stdlib, site-packages).</summary>
    [ObservableProperty]
    private bool _includeVendor;

    private ApiSearchFilter Filter => new()
    {
        IncludeGenerated = IncludeGenerated,
        IncludeVendor = IncludeVendor,
        Limit = 200
    };

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    partial void OnIncludeGeneratedChanged(bool value) => RestartSearch();

    partial void OnIncludeVendorChanged(bool value) => RestartSearch();

    partial void OnSelectedResultChanged(SymbolViewModel? value)
    {
        if (value is not null)
            ShowSymbol(value);
    }

    private void RestartSearch()
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();

        // Дерево тоже фильтруется по слоям: перестраиваем корни, иначе галочка «показывать
        // сгенерированное» на дереве не отражается вообще никак.
        if (Pane == ApiBrowserPane.Tree)
            _ = LoadTreeRootsAsync();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        _ = RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();

        string query = SearchText?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            _searchCts = null;
            Results.ReplaceAll(Array.Empty<SymbolViewModel>());
            SearchStatus = string.Empty;
            IsSearching = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        IsSearching = true;

        try
        {
            IReadOnlyList<ApiSymbol> found = await _backend
                .SearchAsync(query, Filter, cts.Token)
                .ConfigureAwait(true);

            if (cts.IsCancellationRequested || !ReferenceEquals(_searchCts, cts))
                return;

            Results.ReplaceAll(found.Select(s => new SymbolViewModel(s)));

            SearchStatus = found.Count == 0
                ? "ничего не найдено"
                : found.Count >= Filter.Limit
                    ? $"показаны первые {found.Count} — уточните запрос"
                    : $"найдено {found.Count}";

            if (found.Count == 0)
            {
                string hidden = await DescribeHiddenLayersAsync(query, cts).ConfigureAwait(true);
                if (hidden.Length > 0 && ReferenceEquals(_searchCts, cts))
                    SearchStatus = hidden;
            }
        }
        catch (OperationCanceledException)
        {
            // Следующий запрос уже в пути.
        }
        catch (Exception ex)
        {
            SearchStatus = "поиск не выполнен";
            StatusMessage = "поиск: " + Short(ex);
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
                IsSearching = false;
        }
    }

    /// <summary>
    /// Что сказать про СКРЫТЫЕ слои корпуса. Галочки «+ ресурсы» и «+ stdlib» по умолчанию
    /// выключены, и «ничего не найдено» при выключенных галочках — это ложь: имён, живущих
    /// ТОЛЬКО в сгенерированных ресурсах, 18 738, только в вендорном коде — 12 301.
    /// Своими глазами: <c>AccountDashboardModel</c> объявлен в
    /// <c>client/gui/impl/gen/view_models/…/account_dashboard_model.py:16</c>, и собственный
    /// каталог горячих точек ссылается как раз туда — то есть сгенерированные вью-модели
    /// реальные моды патчат.
    /// <para>
    /// Повтор делается ТОЛЬКО при пустой выдаче, и это осознанная граница: второй полный проход
    /// ранжирования на каждое нажатие клавиши удвоил бы цену самой частой операции экрана ради
    /// приписки, а неправдой был именно ноль — «ничего не найдено» там, где найдено 12.
    /// </para>
    /// </summary>
    /// <param name="query">Тот же запрос.</param>
    /// <param name="cts">Токен текущего поиска — повтор не должен пережить свой запрос.</param>
    /// <returns>Готовая приписка либо пустая строка.</returns>
    private async Task<string> DescribeHiddenLayersAsync(string query, CancellationTokenSource cts)
    {
        if (IncludeGenerated && IncludeVendor)
            return string.Empty;

        IReadOnlyList<ApiSymbol> all;
        try
        {
            all = await _backend
                .SearchAsync(query, Filter with { IncludeGenerated = true, IncludeVendor = true }, cts.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (Exception)
        {
            // Приписка — удобство, а не результат: её отказ не имеет права портить выдачу.
            return string.Empty;
        }

        int generated = 0, vendor = 0;
        foreach (ApiSymbol s in all)
        {
            if (s.Tier == SourceTier.Generated && !IncludeGenerated) generated++;
            else if (s.Tier == SourceTier.Vendor && !IncludeVendor) vendor++;
        }

        if (generated == 0 && vendor == 0)
            return string.Empty;

        var parts = new List<string>(2);
        if (generated > 0) parts.Add($"{generated} в сгенерированных");
        if (vendor > 0) parts.Add($"{vendor} в вендорных");

        return "в игровом коде ничего; " + string.Join(", ", parts) + " — включите фильтр над списком";
    }

    // ---- дерево модулей

    /// <summary>Корни дерева модулей (client, client_common, common).</summary>
    public ObservableCollection<ApiTreeNodeViewModel> TreeRoots { get; } = new();

    /// <summary>Выбранный узел дерева.</summary>
    [ObservableProperty]
    private ApiTreeNodeViewModel? _selectedTreeNode;

    partial void OnSelectedTreeNodeChanged(ApiTreeNodeViewModel? value)
    {
        if (value is null)
            return;

        if (value.Symbol is not null)
        {
            ShowSymbol(value.Symbol);
            return;
        }

        if (!value.IsDirectory && value.FullPath.Length > 0)
            ShowFile(value.FullPath, 1, value.VirtualPath, value.RelativePath);
    }

    private async Task LoadTreeRootsAsync()
    {
        TreeRoots.Clear();

        if (!HasIndex)
            return;

        try
        {
            IReadOnlyList<ApiTreeNode> roots = await _backend
                .GetTreeChildrenAsync(null, Filter, CancellationToken.None)
                .ConfigureAwait(true);

            foreach (ApiTreeNode node in roots)
                TreeRoots.Add(new ApiTreeNodeViewModel(node, LoadTreeChildrenAsync));
        }
        catch (Exception ex)
        {
            StatusMessage = "дерево модулей: " + Short(ex);
        }
    }

    /// <summary>
    /// Подгружает детей узла. Каталог отдаёт подкаталоги и файлы, файл — свои символы:
    /// именно поэтому дерево доводит до символа, а не просто до файла.
    /// </summary>
    /// <param name="node">Раскрываемый узел.</param>
    private async Task LoadTreeChildrenAsync(ApiTreeNodeViewModel node)
    {
        try
        {
            if (node.IsDirectory)
            {
                IReadOnlyList<ApiTreeNode> children = await _backend
                    .GetTreeChildrenAsync(node.RelativePath, Filter, CancellationToken.None)
                    .ConfigureAwait(true);

                node.SetChildren(children.Select(c => new ApiTreeNodeViewModel(c, LoadTreeChildrenAsync)));
            }
            else
            {
                IReadOnlyList<ApiSymbol> symbols = await _backend
                    .GetFileSymbolsAsync(node.RelativePath, CancellationToken.None)
                    .ConfigureAwait(true);

                node.SetChildren(symbols.Select(s => new ApiTreeNodeViewModel(new SymbolViewModel(s))));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "узел дерева: " + Short(ex);
            node.SetChildren(Array.Empty<ApiTreeNodeViewModel>());
        }
    }

    // ---- каталоги

    /// <summary>Записи открытого каталога после фильтра.</summary>
    public BulkObservableCollection<CatalogItemViewModel> CatalogItems { get; } = new();

    /// <summary>Выбранная запись каталога.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopySnippetCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCatalogSnippetCommand))]
    [NotifyPropertyChangedFor(nameof(HasCatalogSnippet))]
    private CatalogItemViewModel? _selectedCatalogItem;

    /// <summary>
    /// У выбранной записи каталога есть свой, подобранный под неё сниппет. Каталог знает то,
    /// чего не знает индекс: событие боевого владельца требует двухступенчатой подписки,
    /// сервису нужен <c>dependency</c>, горячей точке — монкипатч. Поэтому кнопка
    /// «рекомендуемый» отдельная и появляется только тогда, когда рекомендовать есть что.
    /// </summary>
    public bool HasCatalogSnippet => SelectedCatalogItem is not null;

    /// <summary>Поиск внутри каталога.</summary>
    [ObservableProperty]
    private string _catalogFilterText = string.Empty;

    /// <summary>Сводка по каталогу.</summary>
    [ObservableProperty]
    private string _catalogStatus = string.Empty;

    /// <summary>
    /// Предупреждение по ВЫБРАННОЙ записи каталога — то самое, что раньше висело на каждой
    /// строке списка. Показывается в карточке над исходником, где на него есть место.
    /// </summary>
    [ObservableProperty]
    private string _catalogWarning = string.Empty;

    /// <summary>
    /// Пустое состояние каталога. Отдельно от <see cref="EmptyStateText"/>: «нажмите
    /// выбрать папку» и «этот каталог пока не подключён» — разные проблемы с разным решением.
    /// </summary>
    public string CatalogEmptyText => !HasIndex
        ? EmptyStateText
        : Pane switch
        {
            ApiBrowserPane.Hotspots =>
                """
                Каталог горячих точек пуст.

                Он строится не по клиенту, а по РЕАЛЬНЫМ модам из mods\<версия>\:
                какие пары (класс, метод) они на практике патчат.
                Это отдельный сканер чужих .mtmod, и он подключается независимо от индекса.
                """,
            _ =>
                """
                В этом каталоге ничего не нашлось.

                Снимите фильтр в поле над списком или переиндексируйте дерево.
                """
        };

    partial void OnPaneChanged(ApiBrowserPane value)
    {
        // Уход с панели каталога снимает его построение: ждать секунду ради списка,
        // на который человек больше не смотрит, незачем.
        if (!IsCatalogPane && IsCatalogLoading)
            _catalogCts?.Cancel();

        switch (value)
        {
            case ApiBrowserPane.Tree when TreeRoots.Count == 0:
                _ = LoadTreeRootsAsync();
                break;
            case ApiBrowserPane.Events:
                _ = LoadCatalogAsync(ApiCatalogKind.Events);
                break;
            case ApiBrowserPane.Services:
                _ = LoadCatalogAsync(ApiCatalogKind.Services);
                break;
            case ApiBrowserPane.Hotspots:
                _ = LoadCatalogAsync(ApiCatalogKind.Hotspots);
                break;
        }
    }

    partial void OnCatalogFilterTextChanged(string value) => ApplyCatalogFilter();

    partial void OnSelectedCatalogItemChanged(CatalogItemViewModel? value)
    {
        if (value is null)
            return;

        _snippetFromCatalog = true;
        SnippetTarget = value.SnippetTitle;

        // Полное предупреждение показывается в карточке выбранной записи, а не в каждой строке
        // списка: «Event, а не SafeEvent» верно для 99.3 % записей каталога событий.
        CatalogWarning = value.Warning;

        if (value.CanNavigate)
            ShowFile(value.FullPath, value.Line, value.VirtualPath, value.Entry.RelativePath);

        // Каталог знает имя, но не знает символа индекса — xref всё равно осмыслен:
        // для события это готовый список мест, где на него подписываются.
        ScheduleXref(NameOf(value), value.Entry.XrefReceiver);
    }

    /// <summary>
    /// Что искать по записи каталога. Имя для ПОКАЗА и имя для ПОИСКА — разные вещи:
    /// у горячей точки в карточке стоит пара «Класс.член», а искать надо член.
    /// Приёмник сюда не выводится вовсе: его считает шов (см. <c>CoreApiBrowserBackend</c>)
    /// по полям записи, а не по человекочитаемой строке пути доступа.
    /// </summary>
    /// <param name="item">Запись каталога.</param>
    /// <returns>Имя для поиска ссылок.</returns>
    private static string NameOf(CatalogItemViewModel item)
        => item.Entry.XrefName.Length > 0 ? item.Entry.XrefName : item.Entry.Name;

    private async Task LoadCatalogAsync(ApiCatalogKind kind)
    {
        if (_loadedCatalog == kind && _catalogAll.Count > 0)
        {
            ApplyCatalogFilter();
            return;
        }

        // Первый каталог строится около секунды (это ВТОРОЙ полный проход по дереву, отдельный
        // от индекса), и за это время пользователь успевает щёлкнуть соседнюю панель. Без номера
        // поколения ответ на УСТАРЕВШИЙ запрос дописывался бы в _catalogAll поверх нового:
        // замерено — «События» сразу за «Сервисами» давали один склеенный список на 1245 записей
        // (1030 + 215) под заголовком «Сервисы», и статус честно писал «показано 1245 из 1245».
        int generation = ++_catalogGeneration;

        _catalogAll.Clear();
        CatalogItems.ReplaceAll(Array.Empty<CatalogItemViewModel>());
        CatalogStatus = "загрузка каталога…";

        // Построение каталогов — САМАЯ ДОЛГАЯ операция экрана (657-924 мс, это второй полный
        // проход по дереву). Раньше оно шло с CancellationToken.None: уйти с панели было
        // нельзя, отменить нечем, а признак работы сводился к статической строке.
        _catalogCts?.Cancel();
        _catalogCts?.Dispose();
        var cts = new CancellationTokenSource();
        _catalogCts = cts;
        IsCatalogLoading = true;

        try
        {
            IReadOnlyList<ApiCatalogEntry> entries = await _backend
                .GetCatalogAsync(kind, cts.Token)
                .ConfigureAwait(true);

            if (generation != _catalogGeneration)
                return;

            _catalogAll.Clear();
            _catalogAll.AddRange(entries.Select(e => new CatalogItemViewModel(e)));
            _loadedCatalog = kind;
            ApplyCatalogFilter();
        }
        catch (OperationCanceledException)
        {
            if (generation != _catalogGeneration)
                return;

            _loadedCatalog = null;
            CatalogStatus = "построение каталога прервано";
        }
        catch (Exception ex)
        {
            if (generation != _catalogGeneration)
                return;

            _loadedCatalog = null;
            CatalogStatus = "каталог недоступен";
            StatusMessage = "каталог: " + Short(ex);
        }
        finally
        {
            if (ReferenceEquals(_catalogCts, cts))
                IsCatalogLoading = false;
        }
    }

    private bool CanCancelCatalog() => IsCatalogLoading;

    /// <summary>Прервать построение доменных каталогов.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelCatalog))]
    private void CancelCatalog()
    {
        _catalogCts?.Cancel();
        StatusMessage = "построение каталогов прервано";
    }

    /// <summary>
    /// Фильтрует и УПОРЯДОЧИВАЕТ каталог. Порядок здесь — не украшение, а сам ответ:
    /// <list type="number">
    ///   <item>шесть колбэков загрузчика идут первыми (их отдают моду без подписки вообще);</item>
    ///   <item>совпадения по ИМЕНИ выше совпадений по пути доступа и пути файла;</item>
    ///   <item>совпадения по синониму («бой» → arena) ниже прямых, но выше совпадений по пути;</item>
    ///   <item>внутри группы сохраняется исходный порядок — по числу подписок в клиенте.</item>
    /// </list>
    /// </summary>
    private void ApplyCatalogFilter()
    {
        string needle = CatalogFilterText?.Trim() ?? string.Empty;
        IReadOnlyList<string> synonyms = CatalogSynonyms.Expand(needle);

        var shown = new List<(CatalogItemViewModel Item, int Rank)>(_catalogAll.Count);
        var usedSynonyms = new List<string>(4);

        foreach (CatalogItemViewModel item in _catalogAll)
        {
            int rank;
            if (needle.Length == 0)
            {
                rank = item.IsLoaderCallback ? 0 : 3;
            }
            else if (item.MatchesName(needle))
            {
                rank = item.IsLoaderCallback ? 0 : 1;
            }
            else if (item.Matches(needle))
            {
                rank = item.IsLoaderCallback ? 0 : 3;
            }
            else
            {
                string? hit = synonyms.FirstOrDefault(item.Matches);
                if (hit is null)
                    continue;

                rank = item.IsLoaderCallback ? 0 : item.MatchesName(hit) ? 2 : 3;
                if (!usedSynonyms.Contains(hit, StringComparer.OrdinalIgnoreCase))
                    usedSynonyms.Add(hit);
            }

            shown.Add((item, rank));
        }

        // OrderBy устойчив, поэтому внутри группы сохраняется порядок каталога (по весу).
        List<CatalogItemViewModel> ordered = shown.OrderBy(x => x.Rank).Select(x => x.Item).ToList();
        CatalogItems.ReplaceAll(ordered);

        int stale = ordered.Count(i => i.IsStale);
        string status = _catalogAll.Count == 0
            ? "каталог пуст"
            : $"показано {ordered.Count} из {_catalogAll.Count}"
              + (stale > 0 ? $" · устаревших {stale}" : string.Empty);

        if (usedSynonyms.Count > 0)
            status += $" · по запросу «{needle}» искали также: {string.Join(", ", usedSynonyms.Take(4))}";

        // Каталог патчей отвечает на два вопроса: «что патчат моды» и «есть ли это в ВАШЕМ
        // клиенте». Без индекса второй ответ не получен вовсе, и молчать об этом нельзя:
        // иначе все 252 записи читаются как «есть в вашем клиенте».
        if (Pane == ApiBrowserPane.Hotspots && !HasIndex && _catalogAll.Count > 0)
            status += " · по клиенту НЕ проверено: дерево исходников не указано";

        CatalogStatus = status;
    }

    // =================================================================== центр: исходник

    /// <summary>Символ, открытый в центре. <c>null</c> — открыт просто файл.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySnippetCommand))]
    private SymbolViewModel? _currentSymbol;

    /// <summary>Абсолютный путь показываемого файла.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInEditorCommand))]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private string? _sourcePath;

    /// <summary>Строка, к которой прокручен просмотрщик.</summary>
    [ObservableProperty]
    private int _sourceLine;

    /// <summary>Виртуальный путь показываемого файла — тот самый, что в traceback.</summary>
    [ObservableProperty]
    private string _sourceVirtualPath = string.Empty;

    /// <summary>Относительный путь показываемого файла.</summary>
    [ObservableProperty]
    private string _sourceRelativePath = string.Empty;

    /// <summary>Заголовок карточки: квалифицированное имя с сигнатурой.</summary>
    [ObservableProperty]
    private string _sourceTitle = string.Empty;

    /// <summary>Подзаголовок: вид, модуль, декораторы.</summary>
    [ObservableProperty]
    private string _sourceSubtitle = string.Empty;

    /// <summary>В центре что-то открыто.</summary>
    public bool HasSource => !string.IsNullOrEmpty(SourcePath);

    private void ShowSymbol(SymbolViewModel symbol)
    {
        _snippetFromCatalog = false;
        CatalogWarning = string.Empty;
        CurrentSymbol = symbol;
        SourcePath = symbol.FullPath;
        SourceLine = symbol.Line;
        SourceVirtualPath = symbol.VirtualPath;
        SourceRelativePath = symbol.Symbol.RelativePath;
        SourceTitle = symbol.Header;

        SourceSubtitle = string.Join(
            "  ·  ",
            new[]
            {
                symbol.KindText,
                symbol.Module,
                symbol.Decorators.Length == 0 ? null : symbol.Decorators,
                symbol.IsServiceInterface ? "skeletons/" : null,
                symbol.HomonymText.Length == 0 ? null : symbol.HomonymText
            }.Where(s => !string.IsNullOrEmpty(s)));

        SnippetTarget = symbol.IsServiceInterface ? "сниппет: получить сервис" : "сниппет: монкипатч";

        // Приёмник передаётся СЫРЫМ, без оглядки на XrefUseReceiver: фильтрует его сам
        // RunXrefAsync. Иначе выключенная галочка стирала бы приёмник насовсем и включить
        // её обратно было бы нечем — пришлось бы заново выбирать символ.
        // Запуск отложенный: стрелка вниз по списку не должна стоить полного прохода дерева.
        ScheduleXref(symbol.Name, symbol.XrefReceiver);
    }

    private void ShowFile(string fullPath, int line, string virtualPath, string relativePath)
    {
        CurrentSymbol = null;
        SourcePath = fullPath;
        SourceLine = line;
        SourceVirtualPath = virtualPath;
        SourceRelativePath = relativePath;
        SourceTitle = Path.GetFileName(fullPath);
        SourceSubtitle = relativePath;
    }

    private bool CanOpenInEditor() => HasSource;

    /// <summary>Открыть текущий файл в VS Code на текущей строке.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInEditor))]
    private void OpenInEditor()
    {
        string? path = SourcePath;
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            _editor.OpenAt(path, Math.Max(1, SourceLine));
            StatusMessage = $"открыто: {path}:{Math.Max(1, SourceLine)}";
        }
        catch (Exception ex)
        {
            StatusMessage = "VS Code: " + Short(ex);
        }
    }

    private bool CanCopyImport() => CurrentSymbol is not null;

    /// <summary>Скопировать строку импорта символа.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyImport))]
    private void CopyImport()
    {
        SymbolViewModel? symbol = CurrentSymbol;
        if (symbol is null)
            return;

        if (CopyToClipboard(symbol.ImportLine))
            StatusMessage = "скопировано: " + symbol.ImportLine;
    }

    /// <summary>Скопировать виртуальный путь (тот, которым оперирует traceback).</summary>
    [RelayCommand]
    private void CopyVirtualPath()
    {
        if (SourceVirtualPath.Length > 0 && CopyToClipboard(SourceVirtualPath))
            StatusMessage = "скопировано: " + SourceVirtualPath;
    }

    /// <summary>Скопировать путь к файлу на диске.</summary>
    [RelayCommand]
    private void CopyDiskPath()
    {
        if (!string.IsNullOrEmpty(SourcePath) && CopyToClipboard(SourcePath))
            StatusMessage = "скопировано: " + SourcePath;
    }

    // =================================================================== правая колонка: xref

    /// <summary>Попадания «где это вызывается».</summary>
    public BulkObservableCollection<XrefHitViewModel> XrefHits { get; } = new();

    /// <summary>Выбранное попадание.</summary>
    [ObservableProperty]
    private XrefHitViewModel? _selectedXrefHit;

    /// <summary>По какому имени сейчас показаны ссылки.</summary>
    [ObservableProperty]
    private string _xrefTitle = string.Empty;

    /// <summary>Сводка по ссылкам, включая честное предупреждение об омонимии.</summary>
    [ObservableProperty]
    private string _xrefStatus = string.Empty;

    /// <summary>Идёт поиск ссылок.</summary>
    [ObservableProperty]
    private bool _isXrefRunning;

    /// <summary>
    /// Уточнять поиск приёмником (<c>settings.update</c> вместо <c>update</c>).
    /// Включено по умолчанию: сужение доходит до 601 раза.
    /// </summary>
    [ObservableProperty]
    private bool _xrefUseReceiver = true;

    /// <summary>Игнорировать регистр. Выключено по умолчанию: это в 6-10 раз дороже.</summary>
    [ObservableProperty]
    private bool _xrefIgnoreCase;

    private string _xrefName = string.Empty;
    private string? _xrefReceiver;

    private string _pendingXrefName = string.Empty;
    private string? _pendingXrefReceiver;

    /// <summary>
    /// Откладывает поиск ссылок на <see cref="XrefDebounceMs"/>. Вызывается ОТОВСЮДУ, где
    /// выделение меняется стрелками: и из списка выдачи, и из списка каталога. Идущий запрос
    /// снимается сразу же (это работает и стоит 49-63 мс), а новый стартует только по тику —
    /// поэтому пролистывание списка не запускает по полному проходу дерева на каждую строку.
    /// </summary>
    /// <param name="name">Имя символа.</param>
    /// <param name="receiver">Приёмник либо <c>null</c>.</param>
    private void ScheduleXref(string? name, string? receiver)
    {
        _pendingXrefName = name ?? string.Empty;
        _pendingXrefReceiver = receiver;

        _xrefCts?.Cancel();
        IsXrefRunning = false;

        _xrefDebounce.Stop();
        _xrefDebounce.Start();

        // Заголовок и статус меняются сразу: панель обязана показывать имя ТОГО символа,
        // который выделен сейчас, а не того, по которому ещё висит старый результат.
        string? shown = XrefUseReceiver ? receiver : null;
        XrefTitle = _pendingXrefName.Length == 0
            ? string.Empty
            : shown is null ? _pendingXrefName : shown + "." + _pendingXrefName;
        XrefStatus = _pendingXrefName.Length == 0 ? string.Empty : "ищем…";
    }

    private void OnXrefDebounceTick(object? sender, EventArgs e)
    {
        _xrefDebounce.Stop();
        _ = RunXrefAsync(_pendingXrefName, _pendingXrefReceiver);
    }

    partial void OnXrefUseReceiverChanged(bool value) => RerunXref();

    partial void OnXrefIgnoreCaseChanged(bool value) => RerunXref();

    partial void OnSelectedXrefHitChanged(XrefHitViewModel? value)
    {
        if (value is null)
            return;

        // Переход по ссылке не меняет ВЫБРАННЫЙ символ: сниппеты и панель ссылок остаются
        // на своём символе, а в центре показывается место вызова. Иначе один щелчок по ссылке
        // сбрасывал бы весь контекст.
        SourcePath = value.Hit.FullPath;
        SourceLine = value.Hit.Line;
        SourceVirtualPath = value.Hit.VirtualPath;
        SourceRelativePath = value.Hit.RelativePath;
        SourceTitle = $"{value.Hit.RelativePath}:{value.Hit.Line}";
        SourceSubtitle = value.KindBadge + "  ·  " + value.Hit.Text.Trim();
    }

    /// <summary>Перезапустить поиск ссылок по текущему символу.</summary>
    [RelayCommand]
    private Task RerunXrefAsync() => RunXrefAsync(_xrefName, _xrefReceiver);

    private void RerunXref()
    {
        if (_xrefName.Length > 0)
            _ = RunXrefAsync(_xrefName, _xrefReceiver);
    }

    /// <summary>
    /// Ищет места употребления имени. Приёмник принимается СЫРЫМ и запоминается сырым —
    /// галочка <see cref="XrefUseReceiver"/> применяется только к запросу, чтобы её можно
    /// было выключить и включить обратно, не потеряв уточнение.
    /// </summary>
    /// <param name="name">Имя символа.</param>
    /// <param name="receiver">Приёмник либо <c>null</c>, если уточнять нечем.</param>
    private async Task RunXrefAsync(string? name, string? receiver)
    {
        _xrefDebounce.Stop();
        _xrefCts?.Cancel();
        _xrefCts?.Dispose();

        _xrefName = name ?? string.Empty;
        _xrefReceiver = receiver;
        _pendingXrefName = _xrefName;
        _pendingXrefReceiver = receiver;

        if (_xrefName.Length == 0)
        {
            _xrefCts = null;
            XrefHits.ReplaceAll(Array.Empty<XrefHitViewModel>());
            XrefTitle = string.Empty;
            XrefStatus = string.Empty;
            XrefReceiverHint = string.Empty;
            return;
        }

        string? effectiveReceiver = XrefUseReceiver ? receiver : null;

        XrefTitle = effectiveReceiver is null ? _xrefName : effectiveReceiver + "." + _xrefName;

        var request = new ApiXrefRequest
        {
            Name = _xrefName,
            Receiver = effectiveReceiver,
            IgnoreCase = XrefIgnoreCase,
            IncludeGenerated = IncludeGenerated,
            IncludeVendor = IncludeVendor,
            Limit = 500
        };

        var cts = new CancellationTokenSource();
        _xrefCts = cts;
        IsXrefRunning = true;
        XrefStatus = "ищем…";

        try
        {
            ApiXrefResult result = await _backend
                .FindReferencesAsync(request, cts.Token)
                .ConfigureAwait(true);

            if (cts.IsCancellationRequested || !ReferenceEquals(_xrefCts, cts))
                return;

            // Уточнение приёмником дало ноль — не оставляем пользователя ни с ответом, ни со
            // способом его получить. Повторяем голым именем и ЧЕСТНО пишем, что показано.
            // Пустой список здесь особенно вреден тем, что вместе с ним пуста и подсказка
            // «частые приёмники»: она строится из попаданий, которых нет.
            string fallbackNote = string.Empty;
            if (result.TotalHits == 0 && effectiveReceiver is not null)
            {
                ApiXrefResult bare = await _backend
                    .FindReferencesAsync(request with { Receiver = null }, cts.Token)
                    .ConfigureAwait(true);

                if (cts.IsCancellationRequested || !ReferenceEquals(_xrefCts, cts))
                    return;

                if (bare.TotalHits > 0)
                {
                    fallbackNote = $"через {effectiveReceiver}. ничего нет; ";
                    result = bare;
                    XrefTitle = _xrefName;
                }
            }

            XrefHits.ReplaceAll(result.Hits.Select(h => new XrefHitViewModel(h)));
            XrefStatus = fallbackNote + DescribeXref(result);
            XrefReceiverHint = BuildReceiverHint(result);
        }
        catch (OperationCanceledException)
        {
            // Пришёл следующий запрос.
        }
        catch (Exception ex)
        {
            XrefHits.ReplaceAll(Array.Empty<XrefHitViewModel>());
            XrefStatus = "поиск ссылок не выполнен";
            StatusMessage = "ссылки: " + Short(ex);
        }
        finally
        {
            if (ReferenceEquals(_xrefCts, cts))
                IsXrefRunning = false;
        }
    }

    /// <summary>
    /// Подсказка: чем уточнить запрос. Ядро отдаёт самые частые приёмники — это готовые
    /// кандидаты, и они полезнее любого совета «попробуйте сузить поиск».
    /// </summary>
    [ObservableProperty]
    private string _xrefReceiverHint = string.Empty;

    /// <summary>
    /// Честная сводка по ссылкам. Если имя объявлено больше чем в десятке мест, поиск без
    /// приёмника заведомо бесполезен, и это надо СКАЗАТЬ, а не выдавать пару тысяч строк
    /// как ответ на вопрос.
    /// </summary>
    /// <param name="result">Результат поиска вместе со сводкой.</param>
    /// <returns>Строка для панели.</returns>
    private string DescribeXref(ApiXrefResult result)
    {
        if (result.TotalHits == 0)
            return $"ссылок не найдено · {result.Elapsed.TotalMilliseconds:0.#} мс";

        string head = result.Truncated
            ? $"показаны {result.Hits.Count} из {result.TotalHits} в {result.FileCount} файлах"
            : $"{result.TotalHits} в {result.FileCount} файлах";

        head += $" · {result.Elapsed.TotalMilliseconds:0.#} мс";

        if (result.DeclarationCount > 1)
            head += $" · имя объявлено в {result.DeclarationCount} местах";

        // Омонимия — это не «много результатов», это «ответ не про то, что вы спрашивали».
        // Если запрос уже уточнён приёмником (в нём есть точка), предупреждать не о чем.
        if (result.IsHomonym && !result.Needle.Contains('.', StringComparison.Ordinal))
            head += " — без уточнения приёмником список бесполезен";

        return head;
    }

    /// <summary>Строит строку с кандидатами в приёмники.</summary>
    /// <param name="result">Результат поиска.</param>
    /// <returns>Готовая подсказка либо пустая строка.</returns>
    private static string BuildReceiverHint(ApiXrefResult result)
    {
        if (result.TopReceivers.Count == 0)
            return string.Empty;

        return "частые приёмники: " + string.Join(", ", result.TopReceivers.Take(5));
    }

    // =================================================================== правая колонка: сниппеты

    /// <summary>Какой сниппет предлагается для текущего выбора.</summary>
    [ObservableProperty]
    private string _snippetTarget = string.Empty;

    /// <summary>Текст последнего сгенерированного сниппета — показывается целиком.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnippet))]
    private string _snippetPreview = string.Empty;

    /// <summary>Предупреждение к сниппету.</summary>
    [ObservableProperty]
    private string _snippetNote = string.Empty;

    /// <summary>Есть что показать в панели сниппетов.</summary>
    public bool HasSnippet => SnippetPreview.Length > 0;

    /// <summary>Идентификатор мода: подставляется в сниппеты. Обновляется из главного экрана.</summary>
    [ObservableProperty]
    private string _modId = "ru.local.my_mod";

    private bool CanCopySnippet() => CurrentSymbol is not null || SelectedCatalogItem is not null;

    /// <summary>
    /// Сгенерировать сниппет и положить его в буфер обмена. То, что скопировано, показывается
    /// целиком: код, уезжающий в чужой проект вслепую, — плохая идея.
    /// </summary>
    /// <param name="kind">Какой сниппет.</param>
    [RelayCommand(CanExecute = nameof(CanCopySnippet))]
    private async Task CopySnippetAsync(ApiSnippetKind kind)
    {
        // Цель у сниппета РОВНО ОДНА, и это последний выбор пользователя. Отдать генератору
        // сразу и символ, и запись каталога нельзя: он предпочтёт запись каталога, и «монкипатч
        // выбранного метода» превратится в подписку на событие, которое выбирали десять минут
        // назад в другой панели. Если последнего выбора нет — берём то, что вообще есть.
        bool useCatalog = _snippetFromCatalog
            ? SelectedCatalogItem is not null
            : CurrentSymbol is null && SelectedCatalogItem is not null;

        var request = new ApiSnippetRequest
        {
            Kind = kind,
            Symbol = useCatalog ? null : CurrentSymbol?.Symbol,
            Entry = useCatalog ? SelectedCatalogItem?.Entry : null,
            ModId = string.IsNullOrWhiteSpace(ModId) ? "ru.local.my_mod" : ModId
        };

        ApiSnippet? snippet;
        try
        {
            snippet = _backend.BuildSnippet(request);

            // Подписка и получение сервиса строятся ПО КАТАЛОГУ (там резолвер владельца,
            // восстановленная сигнатура и предупреждения), а каталоги ленивые. Пользователь,
            // нашедший событие поиском и не заходивший на вкладку «события», получал
            // «для этой цели сниппета нет» — при том что сниппет прекрасно строится, надо лишь
            // сперва построить каталог. Строим и пробуем ещё раз, один раз.
            if (snippet is null && request.Symbol is not null && NeedsCatalog(kind))
            {
                StatusMessage = "строим каталоги для сниппета…";
                await EnsureCatalogForSnippetAsync(kind).ConfigureAwait(true);
                snippet = _backend.BuildSnippet(request);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "построение каталогов прервано — сниппет не собран";
            return;
        }
        catch (Exception ex)
        {
            StatusMessage = "генератор сниппетов: " + Short(ex);
            return;
        }

        if (snippet is null)
        {
            SnippetPreview = string.Empty;
            SnippetNote = string.Empty;
            StatusMessage = NeedsCatalog(kind)
                ? $"в каталоге нет записи «{CurrentSymbol?.Name ?? "—"}»: подписку и сервис кокпит строит только по каталогу — для метода нажмите «монкипатч»"
                : "для этой цели сниппета нет";
            return;
        }

        SnippetPreview = snippet.Code;
        SnippetNote = snippet.Note;
        SnippetTarget = snippet.Title;

        StatusMessage = CopyToClipboard(snippet.Code)
            ? "сниппет скопирован: " + snippet.Title
            : "сниппет построен, но в буфер не попал";
    }

    /// <summary>Нужен ли каталог, чтобы построить сниппет этого вида.</summary>
    /// <param name="kind">Вид сниппета.</param>
    /// <returns>true для подписки и получения сервиса.</returns>
    private static bool NeedsCatalog(ApiSnippetKind kind)
        => kind is ApiSnippetKind.EventSubscription or ApiSnippetKind.LateEventSubscription or ApiSnippetKind.ServiceAccess;

    /// <summary>
    /// Прогревает нужный каталог, не трогая состояние левой колонки: пользователь стоит
    /// на панели поиска, и подменять ему список незачем.
    /// </summary>
    /// <param name="kind">Вид сниппета.</param>
    private async Task EnsureCatalogForSnippetAsync(ApiSnippetKind kind)
    {
        ApiCatalogKind catalog = kind == ApiSnippetKind.ServiceAccess
            ? ApiCatalogKind.Services
            : ApiCatalogKind.Events;

        IsCatalogLoading = true;
        try
        {
            await _backend.GetCatalogAsync(catalog, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsCatalogLoading = false;
        }
    }

    /// <summary>Сгенерировать сниппет, подходящий выбранной записи каталога.</summary>
    [RelayCommand]
    private Task CopyCatalogSnippetAsync()
    {
        CatalogItemViewModel? item = SelectedCatalogItem;
        if (item is null)
        {
            StatusMessage = "запись каталога не выбрана";
            return Task.CompletedTask;
        }

        // Кнопка живёт в списке каталога и означает именно «сниппет для ЭТОЙ записи»,
        // даже если после её выбора успели щёлкнуть символ в панели ссылок.
        _snippetFromCatalog = true;
        return CopySnippetAsync(item.SnippetKind);
    }

    // =================================================================== мост к этапу 1

    /// <summary>
    /// Открыть кадр traceback в браузере API по ВИРТУАЛЬНОМУ пути (<c>scripts/client/…</c>).
    /// Это и есть мост между лентой python.log и индексом: одно и то же поле
    /// <c>VirtualPath</c> хранится в обоих.
    /// </summary>
    /// <param name="virtualPath">Путь из traceback.</param>
    /// <param name="line">Номер строки из traceback.</param>
    /// <returns>true, если файл нашёлся в индексе.</returns>
    public async Task<bool> OpenVirtualPathAsync(string virtualPath, int line)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
            return false;

        try
        {
            ApiSymbol? symbol = await _backend
                .ResolveVirtualPathAsync(virtualPath, line, CancellationToken.None)
                .ConfigureAwait(true);

            if (symbol is null)
            {
                StatusMessage = "в индексе нет файла " + virtualPath;
                return false;
            }

            var vm = new SymbolViewModel(symbol);
            ShowSymbol(vm);

            // Номер строки из traceback точнее, чем строка объявления символа: показываем
            // именно кадр, а карточку символа оставляем как контекст.
            if (line > 0)
                SourceLine = line;

            StatusMessage = $"{virtualPath}:{line} → {vm.Header}";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = "переход по traceback: " + Short(ex);
            return false;
        }
    }

    // =================================================================== жизненный цикл

    /// <summary>Последнее сообщение для строки состояния экрана.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// Отдаёт модели общий с главным экраном объект настроек, чтобы путь к исходникам
    /// не разъезжался между двумя копиями (главный экран пишет его же при загрузке проекта).
    /// </summary>
    /// <param name="settings">Настройки кокпита.</param>
    /// <param name="save">Как их сохранить.</param>
    public void AttachSettings(CockpitSettings settings, Action save)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _saveSettings = save;
    }

    /// <summary>
    /// Поднимает экран: читает путь к исходникам и запускает построение индекса в фоне.
    /// Вызывать с UI-потока, один раз.
    /// </summary>
    public void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;

        _settings ??= TryLoadSettings();
        TreePath = _settings?.WotSrcDir ?? string.Empty;

        _ = BuildIndexAsync(TreePath);
    }

    /// <summary>
    /// Перейти на другое дерево исходников клиента — то же, что «выбрать папку…», но без диалога:
    /// так переход на новую версию клиента переключает кокпит на её исходники.
    /// </summary>
    /// <param name="path">Новое дерево.</param>
    /// <returns>Задача построения индекса.</returns>
    public Task UseTreeAsync(string path) => BuildIndexAsync(path);

    private CockpitSettings? TryLoadSettings()
    {
        try
        {
            return _settingsStore.Load();
        }
        catch (Exception ex)
        {
            StatusMessage = "настройки: " + Short(ex);
            return null;
        }
    }

    /// <summary>
    /// Строит индекс в фоне. Путь сохраняется в настройки ТОЛЬКО если он изменился —
    /// иначе кокпит переписывал бы settings.json на каждом запуске.
    /// </summary>
    /// <param name="path">Новый путь либо <c>null</c> — оставить текущий.</param>
    private async Task BuildIndexAsync(string? path)
    {
        _indexCts?.Cancel();
        _indexCts?.Dispose();

        var cts = new CancellationTokenSource();
        _indexCts = cts;

        if (!string.IsNullOrWhiteSpace(path) && !string.Equals(path, TreePath, StringComparison.OrdinalIgnoreCase))
        {
            TreePath = path;
            if (_settings is not null)
            {
                _settings.WotSrcDir = path;
                try
                {
                    if (_saveSettings is not null)
                        _saveSettings();
                    else
                        _settingsStore.Save(_settings);
                }
                catch (Exception ex)
                {
                    StatusMessage = "путь не сохранён: " + Short(ex);
                }
            }

            // Дерево сменилось — всё, что было построено по старому, недействительно.
            // Номер поколения двигаем здесь же: каталог по СТАРОМУ дереву может быть ещё
            // в пути, и его ответ не имеет права осесть в списке нового дерева.
            TreeRoots.Clear();
            _catalogGeneration++;
            _catalogAll.Clear();
            _loadedCatalog = null;
            CatalogItems.ReplaceAll([]);
        }

        _progressDelay.Stop();
        _progressDelay.Start();

        try
        {
            ApiIndexStatus status = await _backend
                .ReindexAsync(string.IsNullOrWhiteSpace(path) ? null : path, cts.Token)
                .ConfigureAwait(true);

            if (ReferenceEquals(_indexCts, cts))
                ApplyStatus(status);
        }
        catch (OperationCanceledException)
        {
            ApplyStatus(_backend.Status);
        }
        catch (Exception ex)
        {
            ApplyStatus(new ApiIndexStatus
            {
                State = ApiIndexState.Failed,
                TreePath = TreePath,
                Error = Short(ex)
            });
        }
        finally
        {
            // ВСЁ в этом блоке — только для СВОЕГО построения. Отменённая переиндексация
            // не имеет права гасить индикатор той, что уже идёт ей на смену: иначе смена
            // папки во время индексации оставляла бы экран без признаков жизни.
            if (ReferenceEquals(_indexCts, cts))
            {
                _progressDelay.Stop();
                ShowIndexProgress = false;

                // Дерево и каталоги подтягиваются только для видимой панели: строить всё
                // сразу после каждой переиндексации незачем.
                if (Pane == ApiBrowserPane.Tree)
                    await LoadTreeRootsAsync().ConfigureAwait(true);
            }
        }
    }

    private void OnProgressDelayTick(object? sender, EventArgs e)
    {
        _progressDelay.Stop();
        ShowIndexProgress = IsIndexing;
    }

    private void OnBackendStatusChanged(object? sender, ApiIndexStatus status)
    {
        if (_dispatcher.CheckAccess())
            ApplyStatus(status);
        else
            _dispatcher.BeginInvoke(new Action(() => ApplyStatus(status)));
    }

    private void ApplyStatus(ApiIndexStatus status)
    {
        bool wasReady = IndexStatus.IsReady;

        IndexStatus = status;
        IndexStatusText = status.Describe();

        if (status.TreePath.Length > 0)
            TreePath = status.TreePath;

        if (status.State != ApiIndexState.Indexing)
            ShowIndexProgress = false;

        // Индекс перестал быть готовым — всё, что построено по нему, обязано исчезнуть вместе
        // с ним. Иначе после «отменить» на экране оставались строки выдачи и попадания ссылок,
        // указывающие в индекс, которого уже нет.
        if (wasReady && !status.IsReady)
        {
            _xrefDebounce.Stop();
            _searchCts?.Cancel();
            _xrefCts?.Cancel();
            Results.ReplaceAll([]);
            XrefHits.ReplaceAll([]);
            SearchStatus = string.Empty;
            XrefStatus = string.Empty;
            XrefTitle = string.Empty;
            XrefReceiverHint = string.Empty;
            CurrentSymbol = null;
        }

        OnPropertyChanged(nameof(CatalogHint));
        OnPropertyChanged(nameof(HasIndex));
        OnPropertyChanged(nameof(IsIndexing));
        OnPropertyChanged(nameof(IndexProgressIndeterminate));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(CatalogEmptyText));
        ReindexCommand.NotifyCanExecuteChanged();
        CancelIndexCommand.NotifyCanExecuteChanged();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _backend.StatusChanged -= OnBackendStatusChanged;

        _searchDebounce.Stop();
        _xrefDebounce.Stop();
        _progressDelay.Stop();

        _searchCts?.Cancel();
        _xrefCts?.Cancel();
        _indexCts?.Cancel();
        _catalogCts?.Cancel();

        _searchCts?.Dispose();
        _xrefCts?.Dispose();
        _indexCts?.Dispose();
        _catalogCts?.Dispose();
    }

    private bool CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            // Буфер обмена держит другой процесс — обычное дело в Windows и точно не повод падать.
            StatusMessage = "буфер обмена: " + Short(ex);
            return false;
        }
    }

    private static string Short(Exception ex) => ex switch
    {
        NotImplementedException => "сервис Core ещё не реализован (заглушка)",
        _ => ex.Message
    };
}
