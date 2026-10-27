using WotModCockpit.Core.ApiIndex;

namespace WotModCockpit.App.ApiBrowser;

/// <summary>
/// ШОВ между экраном «Браузер API» и реализациями индекса/каталогов в Core.
/// <para>
/// Зачем он вообще есть. UI и две реализации Core (индекс символов и доменные каталоги)
/// писались параллельно. Экран обязан собираться и запускаться в любой момент этой работы,
/// а не только после того, как оба контракта Core дошли до финального вида. Поэтому
/// <see cref="ViewModels.ApiBrowserViewModel"/> знает ровно один интерфейс — этот, и ровно те типы,
/// которые Core уже отдаёт наружу (<see cref="ApiSymbol"/>, <see cref="SymbolKind"/>,
/// <see cref="SourceTier"/>, <see cref="SourceRoot"/>).
/// </para>
/// <para>
/// <b>Как подключить настоящий Core.</b> Написать один класс, реализующий
/// <see cref="IApiBrowserBackend"/> поверх <c>IApiIndexService</c> и <c>IApiCatalogService</c>,
/// и заменить им регистрацию <see cref="UnavailableApiBrowserBackend"/> в
/// <c>AppServices.BuildServiceProvider</c>. Больше нигде в UI менять ничего не нужно:
/// ни одна ViewModel и ни один XAML на типы Core-сервисов не ссылаются.
/// </para>
/// </summary>
public interface IApiBrowserBackend
{
    /// <summary>Текущее состояние индекса. Никогда не <c>null</c>.</summary>
    ApiIndexStatus Status { get; }

    /// <summary>
    /// Состояние изменилось (пошла индексация, закончилась, сломалась).
    /// Может приходить из ЛЮБОГО потока — подписчик обязан сам уйти на диспетчер.
    /// </summary>
    event EventHandler<ApiIndexStatus>? StatusChanged;

    /// <summary>
    /// Построить или перестроить индекс.
    /// </summary>
    /// <param name="treePath">
    /// Новый корень дерева исходников либо <c>null</c> — тогда берётся уже настроенный.
    /// </param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Итоговое состояние. Отказ — это <see cref="ApiIndexState.Failed"/>, а не исключение.</returns>
    Task<ApiIndexStatus> ReindexAsync(string? treePath, CancellationToken cancellationToken);

    /// <summary>Поиск символов с ранжированием.</summary>
    /// <param name="query">Запрос. Пустой — вернуть пусто, а не весь корпус.</param>
    /// <param name="filter">Слои и виды символов.</param>
    /// <param name="cancellationToken">Отмена: поиск идёт на каждое нажатие клавиши.</param>
    /// <returns>Отранжированная выдача, не длиннее <see cref="ApiSearchFilter.Limit"/>.</returns>
    Task<IReadOnlyList<ApiSymbol>> SearchAsync(string query, ApiSearchFilter filter, CancellationToken cancellationToken);

    /// <summary>Дети узла дерева модулей. <c>null</c> — корни (client, client_common, common).</summary>
    /// <param name="parentRelativePath">Относительный путь каталога с прямыми слэшами.</param>
    /// <param name="filter">Слои: сгенерированное и вендорное по умолчанию скрыты.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Каталоги и файлы одного уровня.</returns>
    Task<IReadOnlyList<ApiTreeNode>> GetTreeChildrenAsync(
        string? parentRelativePath,
        ApiSearchFilter filter,
        CancellationToken cancellationToken);

    /// <summary>Символы одного файла — для показа содержимого модуля в дереве.</summary>
    /// <param name="relativePath">Относительный путь файла.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Символы в порядке строк.</returns>
    Task<IReadOnlyList<ApiSymbol>> GetFileSymbolsAsync(string relativePath, CancellationToken cancellationToken);

    /// <summary>«Где вызывается»: полнотекстовый поиск по корпусу с классификацией попаданий.</summary>
    /// <param name="request">Что и как искать.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Попадания плюс сводка (сколько всего, степень омонимии, частые приёмники).</returns>
    Task<ApiXrefResult> FindReferencesAsync(ApiXrefRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Мост к этапу 1: кадр traceback → символ.
    /// </summary>
    /// <param name="virtualPath">Путь как в <c>python.log</c>: <c>scripts/client/gui/SystemMessages.py</c>.</param>
    /// <param name="line">Номер строки из traceback (для декомпилированного клиента приблизительный).</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Ближайший объемлющий символ либо символ-модуль; <c>null</c>, если файла нет в индексе.</returns>
    Task<ApiSymbol?> ResolveVirtualPathAsync(string virtualPath, int line, CancellationToken cancellationToken);

    /// <summary>Доменный каталог целиком (события, сервисы, горячие точки).</summary>
    /// <param name="kind">Какой каталог.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Записи каталога.</returns>
    Task<IReadOnlyList<ApiCatalogEntry>> GetCatalogAsync(ApiCatalogKind kind, CancellationToken cancellationToken);

    /// <summary>
    /// Сгенерировать сниппет для буфера обмена.
    /// </summary>
    /// <param name="request">Что генерировать и для какого символа/записи каталога.</param>
    /// <returns>Готовый код либо <c>null</c>, если для этой цели сниппет не предусмотрен.</returns>
    ApiSnippet? BuildSnippet(ApiSnippetRequest request);
}

// ===================================================================== состояние индекса

/// <summary>Стадия жизни индекса.</summary>
public enum ApiIndexState
{
    /// <summary>Путь к дереву исходников не задан.</summary>
    NotConfigured = 0,

    /// <summary>Путь задан, но каталога нет или он не похож на дамп uncompyle6.</summary>
    Missing = 1,

    /// <summary>Идёт построение.</summary>
    Indexing = 2,

    /// <summary>Индекс построен и им можно пользоваться.</summary>
    Ready = 3,

    /// <summary>Построение сорвалось. Причина в <see cref="ApiIndexStatus.Error"/>.</summary>
    Failed = 4
}

/// <summary>
/// Состояние индекса для строки статуса и пустого экрана.
/// <para>
/// Прогресс здесь есть, но он не про проценты: замеренный бюджет полной индексации —
/// 719 мс холодно и 105-127 мс тепло, то есть индикатор в основном не успевает появиться.
/// Числа нужны для другого — чтобы мододел видел, СКОЛЬКО файлов и символов у него в индексе
/// и не пропустил рост <see cref="SkippedFiles"/> на новой версии клиента.
/// </para>
/// </summary>
public sealed record ApiIndexStatus
{
    /// <summary>Стадия.</summary>
    public ApiIndexState State { get; init; } = ApiIndexState.NotConfigured;

    /// <summary>Корень дерева исходников (может быть пустым).</summary>
    public string TreePath { get; init; } = string.Empty;

    /// <summary>Сколько файлов всего. 0, пока не посчитано.</summary>
    public int FilesTotal { get; init; }

    /// <summary>Сколько файлов уже разобрано.</summary>
    public int FilesDone { get; init; }

    /// <summary>Сколько файлов пропущено (не прочитались, не разобрались).</summary>
    public int SkippedFiles { get; init; }

    /// <summary>Сколько символов в индексе.</summary>
    public int SymbolCount { get; init; }

    /// <summary>Сколько времени заняло последнее построение.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Текст ошибки для <see cref="ApiIndexState.Failed"/>.</summary>
    public string? Error { get; init; }

    /// <summary>Индексом можно пользоваться.</summary>
    public bool IsReady => State == ApiIndexState.Ready && SymbolCount > 0;

    /// <summary>Строка для панели состояния браузера API.</summary>
    /// <returns>Одна короткая строка на русском.</returns>
    public string Describe() => State switch
    {
        ApiIndexState.NotConfigured => "исходники клиента не указаны",
        // Одно состояние на две беды: каталога нет вовсе и каталог есть, но это не дамп.
        // Различать их в ОДНОЙ строке шапки не нужно — подробности с инструкцией показывает
        // пустое состояние панели, а шапке важно не соврать «не найден» про существующую папку.
        ApiIndexState.Missing => "каталог исходников не найден или не похож на дамп",
        ApiIndexState.Indexing => FilesTotal > 0
            ? $"индексация… {FilesDone} из {FilesTotal}"
            : "индексация…",
        ApiIndexState.Failed => "индекс не построен: " + (Error ?? "причина неизвестна"),
        _ => $"{SymbolCount:N0} символов в {FilesTotal:N0} файлах за {Elapsed.TotalMilliseconds:N0} мс"
             + (SkippedFiles > 0 ? $" · пропущено {SkippedFiles}" : string.Empty)
    };
}

// ===================================================================== поиск

/// <summary>
/// Фильтр поиска и дерева. Слои по умолчанию отсечены не из вкусовщины: сгенерированные
/// ресурсы дают 21.5 % символов почти чистого шума (в одном <c>strings.py</c> 17 652 символа
/// вида <c>class _label(DynAccessor)</c>), а вендорный слой — ещё 15 % и ВСЕ 148 ложных
/// срабатываний лексического сканера.
/// </summary>
public sealed record ApiSearchFilter
{
    /// <summary>Показывать <see cref="SourceTier.Generated"/>.</summary>
    public bool IncludeGenerated { get; init; }

    /// <summary>Показывать <see cref="SourceTier.Vendor"/>.</summary>
    public bool IncludeVendor { get; init; }

    /// <summary>Оставить только этот вид символа. <c>null</c> — все виды.</summary>
    public SymbolKind? Kind { get; init; }

    /// <summary>Потолок выдачи.</summary>
    public int Limit { get; init; } = 200;

    /// <summary>Значение по умолчанию: только игровой код, до 200 результатов.</summary>
    public static ApiSearchFilter Default { get; } = new();
}

// ===================================================================== дерево модулей

/// <summary>Узел дерева модулей: каталог или файл.</summary>
/// <param name="Name">Имя для показа (<c>SystemMessages.py</c> или <c>gui</c>).</param>
/// <param name="RelativePath">Путь от корня дерева с прямыми слэшами.</param>
/// <param name="IsDirectory">Узел — каталог.</param>
public sealed record ApiTreeNode(string Name, string RelativePath, bool IsDirectory)
{
    /// <summary>Виртуальный путь файла (как в traceback). Для каталога — пусто.</summary>
    public string VirtualPath { get; init; } = string.Empty;

    /// <summary>Абсолютный путь на диске.</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>Сколько символов в файле либо во всём поддереве.</summary>
    public int SymbolCount { get; init; }

    /// <summary>Слой корпуса.</summary>
    public SourceTier Tier { get; init; } = SourceTier.Game;
}

// ===================================================================== xref

/// <summary>Что за попадание нашлось в строке. Категории взаимоисключающие.</summary>
public enum ApiXrefKind
{
    /// <summary>Строка начинается с <c>def</c>/<c>class</c> — это объявление.</summary>
    Declaration = 0,

    /// <summary>Дальше идёт <c>(</c> — вызов.</summary>
    Call = 1,

    /// <summary>Дальше идёт <c>+=</c> или <c>-=</c> — подписка/отписка на событие.</summary>
    EventSubscription = 2,

    /// <summary>Слева стоит точка — чтение атрибута.</summary>
    AttributeRead = 3,

    /// <summary>Строка импорта.</summary>
    Import = 4,

    /// <summary>Попадание внутри строкового литерала.</summary>
    StringLiteral = 5,

    /// <summary>Комментарий. В этом корпусе почти не встречается: вне 4-строчных шапок их ровно 2.</summary>
    Comment = 6,

    /// <summary>Всё остальное.</summary>
    Other = 7
}

/// <summary>
/// Запрос «где это вызывается».
/// <para>
/// Границы идентификатора применяются ВСЕГДА и настройкой не являются: без них
/// <c>getVehicle</c> даёт 2587 попаданий вместо 143, то есть панель просто не работает.
/// Настройка тут одна и важная — <see cref="Receiver"/>: она лечит омонимию, ради которой
/// панель и нужна (<c>settings.update</c> — 21 попадание против 12 625 у голого <c>update</c>).
/// </para>
/// </summary>
public sealed record ApiXrefRequest
{
    /// <summary>Имя символа.</summary>
    public required string Name { get; init; }

    /// <summary>Приёмник (<c>g_playerEvents</c>, <c>settings</c>) либо <c>null</c>.</summary>
    public string? Receiver { get; init; }

    /// <summary>Игнорировать регистр. По умолчанию выключено: это в 6-10 раз дороже.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>Искать и в сгенерированных ресурсах.</summary>
    public bool IncludeGenerated { get; init; }

    /// <summary>Искать и в вендорном коде.</summary>
    public bool IncludeVendor { get; init; }

    /// <summary>Потолок выдачи.</summary>
    public int Limit { get; init; } = 500;
}

/// <summary>Одно попадание xref.</summary>
/// <param name="RelativePath">Путь относительно дерева.</param>
/// <param name="VirtualPath">Путь как в traceback.</param>
/// <param name="FullPath">Абсолютный путь на диске.</param>
/// <param name="Line">Номер строки, 1-based.</param>
/// <param name="Text">Текст строки, обрезанный слева по отступу.</param>
/// <param name="Kind">Классификация попадания.</param>
public sealed record ApiXrefHit(
    string RelativePath,
    string VirtualPath,
    string FullPath,
    int Line,
    string Text,
    ApiXrefKind Kind)
{
    /// <summary>Слой корпуса — им красится значок.</summary>
    public SourceTier Tier { get; init; } = SourceTier.Game;

    /// <summary>Подкорень.</summary>
    public SourceRoot Root { get; init; } = SourceRoot.Client;
}

/// <summary>
/// Результат поиска ссылок вместе со сводкой. Сводка здесь не украшение: <see cref="DeclarationCount"/>
/// и <see cref="TopReceivers"/> — это единственный способ честно сказать пользователю, что его
/// запрос бессмыслен, и подсказать, чем его уточнить.
/// </summary>
/// <param name="Needle">Что реально искали (с приёмником, если он был).</param>
/// <param name="Hits">Попадания, не длиннее лимита запроса.</param>
/// <param name="TotalHits">Сколько попаданий всего, до обрезания лимитом.</param>
/// <param name="FileCount">В скольких файлах.</param>
/// <param name="DeclarationCount">Сколько разных мест объявляют это имя.</param>
/// <param name="TopReceivers">Самые частые приёмники — готовые кандидаты для уточнения.</param>
/// <param name="Elapsed">Сколько занял поиск.</param>
public sealed record ApiXrefResult(
    string Needle,
    IReadOnlyList<ApiXrefHit> Hits,
    int TotalHits,
    int FileCount,
    int DeclarationCount,
    IReadOnlyList<string> TopReceivers,
    TimeSpan Elapsed)
{
    /// <summary>Список обрезан лимитом.</summary>
    public bool Truncated => TotalHits > Hits.Count;

    /// <summary>Имя объявлено больше чем в десятке мест: без приёмника искать бесполезно.</summary>
    public bool IsHomonym => DeclarationCount > 10;

    /// <summary>Пустой результат.</summary>
    /// <param name="needle">Что искали.</param>
    /// <returns>Результат без попаданий.</returns>
    public static ApiXrefResult Empty(string needle) =>
        new(needle, Array.Empty<ApiXrefHit>(), 0, 0, 0, Array.Empty<string>(), TimeSpan.Zero);
}

// ===================================================================== каталоги

/// <summary>Какой доменный каталог.</summary>
public enum ApiCatalogKind
{
    /// <summary>События клиента (<c>self.onX = Event.Event()</c>) — 1030 объявлений.</summary>
    Events = 0,

    /// <summary>Сервисы: интерфейсы из <c>client/skeletons/**</c> — 215 классов.</summary>
    Services = 1,

    /// <summary>Горячие точки: что на практике патчат реальные моды — 252 пары (класс, член).</summary>
    Hotspots = 2
}

/// <summary>
/// Запись доменного каталога. Одна форма на все три каталога намеренно: экран показывает их
/// одинаковым списком, а различия сидят в <see cref="Badge"/> и <see cref="Detail"/>.
/// </summary>
public sealed record ApiCatalogEntry
{
    /// <summary>Какой каталог.</summary>
    public required ApiCatalogKind Kind { get; init; }

    /// <summary>Имя: <c>onArenaPeriodChange</c>, <c>IItemsCache</c>, <c>Vehicle.onHealthChanged</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Владелец: класс события, модуль интерфейса, класс горячей точки.</summary>
    public string Owner { get; init; } = string.Empty;

    /// <summary>
    /// Как до него добраться из мода: <c>from PlayerEvents import g_playerEvents</c>,
    /// <c>sessionProvider.shared.feedback</c>, <c>dependency.instance(IItemsCache)</c>.
    /// Пусто = путь не выведен (владелец достижим только изнутри клиента).
    /// </summary>
    public string AccessPath { get; init; } = string.Empty;

    /// <summary>Сигнатура: <c>(vehicleID, attackerID, damage)</c>.</summary>
    public string Signature { get; init; } = string.Empty;

    /// <summary>
    /// Имя, по которому искать ссылки. Обычно совпадает с <see cref="Name"/>, но у горячей
    /// точки <see cref="Name"/> — это пара <c>Класс.член</c> для показа, а искать надо член.
    /// Пусто = брать <see cref="Name"/>.
    /// </summary>
    public string XrefName { get; init; } = string.Empty;

    /// <summary>
    /// Приёмник для уточнения поиска ссылок: <c>g_playerEvents</c>, <c>arena</c>,
    /// <c>PlayerAvatar</c>. Только ГОЛЫЙ идентификатор — поиск ссылок ищет подстроку
    /// «приёмник.имя», поэтому выражение вроде <c>dependency.instance(IItemsCache)</c>
    /// или человеческая фраза из <see cref="AccessPath"/> дают ровно ноль попаданий.
    /// <c>null</c> = уточнять нечем, искать голое имя.
    /// </summary>
    public string? XrefReceiver { get; init; }

    /// <summary>Короткая пометка справа: <c>SafeEvent</c>, <c>674 вызова</c>, <c>3 мода</c>.</summary>
    public string Badge { get; init; } = string.Empty;

    /// <summary>Развёрнутое пояснение для карточки.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Путь как в traceback.</summary>
    public string VirtualPath { get; init; } = string.Empty;

    /// <summary>Путь относительно дерева.</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>Абсолютный путь на диске.</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>Строка объявления.</summary>
    public int Line { get; init; }

    /// <summary>Насколько популярна запись (число вызовов сервиса, число модов).</summary>
    public int Weight { get; init; }

    /// <summary>
    /// Запись НЕ подтверждена текущим индексом: в каталоге она есть, а в клиенте её больше нет.
    /// Из 252 горячих точек таких 45 — это мёртвые ветки XVM под клиент WG.
    /// Такие записи не выбрасываются, а помечаются: «этого в вашем клиенте нет» — тоже ответ.
    /// </summary>
    public bool IsStale { get; init; }

    /// <summary>Событие бросает исключение наружу (<c>Event</c>, а не <c>SafeEvent</c>).</summary>
    public bool ThrowsToClient { get; init; }

    /// <summary>Владелец существует только в бою — сниппет должен быть двухступенчатым.</summary>
    public bool BattleOnly { get; init; }

    /// <summary>
    /// НЕПРОЗРАЧНАЯ ссылка на исходную запись каталога Core (событие, сервис, горячая точка).
    /// UI её не разыменовывает и о её типе ничего не знает; она нужна только затем, чтобы
    /// генератор сниппетов получил ту же запись целиком, а не её обеднённую проекцию —
    /// иначе из сниппета пропали бы путь доступа, резолвер владельца и предупреждения,
    /// ради которых каталог и существует.
    /// </summary>
    public object? Source { get; init; }
}

// ===================================================================== сниппеты

/// <summary>Какой сниппет генерировать.</summary>
public enum ApiSnippetKind
{
    /// <summary>Подписка на событие всегда доступного владельца (шаблон A).</summary>
    EventSubscription = 0,

    /// <summary>Двухступенчатая подписка на владельца, живущего только в бою (шаблон A2).</summary>
    LateEventSubscription = 1,

    /// <summary>Безопасный монкипатч метода (шаблон B).</summary>
    MonkeyPatch = 2,

    /// <summary>Получение сервиса через <c>helpers.dependency</c> (шаблон C).</summary>
    ServiceAccess = 3
}

/// <summary>Что именно генерировать и для чего.</summary>
public sealed record ApiSnippetRequest
{
    /// <summary>Вид сниппета.</summary>
    public required ApiSnippetKind Kind { get; init; }

    /// <summary>Символ из индекса, если сниппет строится по нему.</summary>
    public ApiSymbol? Symbol { get; init; }

    /// <summary>Запись каталога, если сниппет строится по ней.</summary>
    public ApiCatalogEntry? Entry { get; init; }

    /// <summary>Идентификатор мода из <c>meta.xml</c> — попадает в имена и в тег лога.</summary>
    public string ModId { get; init; } = "ru.local.my_mod";
}

/// <summary>Сгенерированный сниппет.</summary>
/// <param name="Title">Заголовок для показа («подписка на onAvatarReady»).</param>
/// <param name="Code">Код Python 2.7 целиком.</param>
/// <param name="Note">Предупреждение, которое нельзя потерять при копировании.</param>
public sealed record ApiSnippet(string Title, string Code, string Note);

// ===================================================================== заглушка

/// <summary>
/// Реализация «индекс не подключён». Живёт в контейнере до тех пор, пока адаптер к Core
/// не зарегистрирован, и существует ровно ради одного: экран браузера API обязан
/// открываться, объяснять своё состояние и НЕ падать.
/// </summary>
public sealed class UnavailableApiBrowserBackend : IApiBrowserBackend
{
    private static readonly ApiIndexStatus Empty = new()
    {
        State = ApiIndexState.Failed,
        Error = "реализация индекса не подключена (IApiBrowserBackend)"
    };

    /// <inheritdoc />
    public ApiIndexStatus Status => Empty;

    /// <inheritdoc />
    public event EventHandler<ApiIndexStatus>? StatusChanged
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public Task<ApiIndexStatus> ReindexAsync(string? treePath, CancellationToken cancellationToken)
        => Task.FromResult(Empty);

    /// <inheritdoc />
    public Task<IReadOnlyList<ApiSymbol>> SearchAsync(string query, ApiSearchFilter filter, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ApiSymbol>>(Array.Empty<ApiSymbol>());

    /// <inheritdoc />
    public Task<IReadOnlyList<ApiTreeNode>> GetTreeChildrenAsync(string? parentRelativePath, ApiSearchFilter filter, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ApiTreeNode>>(Array.Empty<ApiTreeNode>());

    /// <inheritdoc />
    public Task<IReadOnlyList<ApiSymbol>> GetFileSymbolsAsync(string relativePath, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ApiSymbol>>(Array.Empty<ApiSymbol>());

    /// <inheritdoc />
    public Task<ApiXrefResult> FindReferencesAsync(ApiXrefRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ApiXrefResult.Empty(request.Name));

    /// <inheritdoc />
    public Task<ApiSymbol?> ResolveVirtualPathAsync(string virtualPath, int line, CancellationToken cancellationToken)
        => Task.FromResult<ApiSymbol?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyList<ApiCatalogEntry>> GetCatalogAsync(ApiCatalogKind kind, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ApiCatalogEntry>>(Array.Empty<ApiCatalogEntry>());

    /// <inheritdoc />
    public ApiSnippet? BuildSnippet(ApiSnippetRequest request) => null;
}
