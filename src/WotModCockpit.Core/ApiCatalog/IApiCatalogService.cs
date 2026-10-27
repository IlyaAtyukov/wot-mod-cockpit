namespace WotModCockpit.Core.ApiCatalog;

/// <summary>Состояние каталогов.</summary>
public enum ApiCatalogStatus
{
    /// <summary>Построение ещё не запускалось.</summary>
    NotBuilt,

    /// <summary>
    /// Дерево исходников не указано или не существует. Это НЕ ошибка: у другого пользователя
    /// дампа uncompyle6 может не быть вовсе, и кокпит обязан работать без него.
    /// </summary>
    NoSources,

    /// <summary>Каталоги готовы.</summary>
    Ready,

    /// <summary>Построение упало. Текст в <see cref="ApiCatalogSnapshot.Error"/>.</summary>
    Failed,
}

/// <summary>
/// Неизменяемый снимок всех трёх доменных каталогов. Меняется целиком, атомарной заменой ссылки —
/// UI никогда не видит полусобранного состояния.
/// </summary>
public sealed class ApiCatalogSnapshot
{
    /// <summary>Создаёт снимок.</summary>
    /// <param name="status">Состояние.</param>
    /// <param name="sourceRoot">Корень дерева исходников, по которому строились каталоги.</param>
    /// <param name="events">Каталог событий.</param>
    /// <param name="services">Каталог сервисов.</param>
    /// <param name="hooks">Каталог горячих точек.</param>
    /// <param name="fileCount">Сколько файлов разобрано.</param>
    /// <param name="skippedFileCount">Сколько файлов пропущено из-за ошибки чтения.</param>
    /// <param name="duration">Сколько заняло построение.</param>
    /// <param name="error">Текст ошибки, если построение упало.</param>
    public ApiCatalogSnapshot(
        ApiCatalogStatus status,
        string sourceRoot,
        EventCatalog events,
        ServiceCatalog services,
        IReadOnlyList<HookPoint> hooks,
        int fileCount,
        int skippedFileCount,
        TimeSpan duration,
        string? error = null)
    {
        Status = status;
        SourceRoot = sourceRoot;
        Events = events;
        Services = services;
        Hooks = hooks;
        FileCount = fileCount;
        SkippedFileCount = skippedFileCount;
        Duration = duration;
        Error = error;
    }

    /// <summary>Состояние.</summary>
    public ApiCatalogStatus Status { get; }

    /// <summary>Корень дерева исходников.</summary>
    public string SourceRoot { get; }

    /// <summary>Каталог событий.</summary>
    public EventCatalog Events { get; }

    /// <summary>Каталог сервисов и слотов боевой сессии.</summary>
    public ServiceCatalog Services { get; }

    /// <summary>Каталог горячих точек, уже сверенный с исходниками текущей версии.</summary>
    public IReadOnlyList<HookPoint> Hooks { get; }

    /// <summary>Сколько файлов разобрано.</summary>
    public int FileCount { get; }

    /// <summary>
    /// Сколько файлов пропущено. Показывать в UI обязательно: рост этого числа на новой версии
    /// клиента — единственный сигнал мододелу, что каталоги стали неполными.
    /// </summary>
    public int SkippedFileCount { get; }

    /// <summary>Сколько заняло построение.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Текст ошибки, если построение упало.</summary>
    public string? Error { get; }

    /// <summary>Сколько горячих точек подтверждено в исходниках текущей версии.</summary>
    public int VerifiedHookCount
    {
        get
        {
            int n = 0;
            foreach (HookPoint h in Hooks)
            {
                if (h.IsUsable)
                    n++;
            }

            return n;
        }
    }

    /// <summary>
    /// Событий, до которых мод иначе не доберётся: подписки через <c>g_playerEvents</c>.
    /// Загрузчик модов отдаёт моду ровно 6 колбэков — этот список показывает всё остальное.
    /// </summary>
    /// <returns>Статистика подписок, по убыванию частоты.</returns>
    public IReadOnlyList<EventSubscriptionStat> PlayerEventSubscriptions() => Events.SubscribedVia("g_playerEvents");

    /// <summary>Пустой снимок «дерева исходников нет».</summary>
    /// <param name="root">Путь, который пытались использовать.</param>
    /// <returns>Снимок со статусом <see cref="ApiCatalogStatus.NoSources"/>.</returns>
    public static ApiCatalogSnapshot NoSourcesAt(string root) => new(
        ApiCatalogStatus.NoSources,
        root,
        EventCatalog.Empty,
        ServiceCatalog.Empty,
        new HookCatalog().Verify(ClientSourceScan.Empty(root)),
        0,
        0,
        TimeSpan.Zero);

    /// <summary>Начальный снимок до первого построения.</summary>
    public static ApiCatalogSnapshot NotBuiltYet { get; } = new(
        ApiCatalogStatus.NotBuilt,
        string.Empty,
        EventCatalog.Empty,
        ServiceCatalog.Empty,
        Array.Empty<HookPoint>(),
        0,
        0,
        TimeSpan.Zero);
}

/// <summary>
/// Доменные каталоги браузера API: события, сервисы и горячие точки, построенные по
/// декомпилированным исходникам клиента.
/// <para>
/// Смысл: у мододела 8052 файла незнакомого кода и вопрос «что мне перехватить, чтобы узнать
/// о событии X». Загрузчик модов отдаёт ровно 6 колбэков, в корпусе объявлено 1030 событий,
/// и никакой документации не существует — каталоги и есть ответ.
/// </para>
/// <para>
/// Построение асинхронное: полный проход по 59 МБ занимает доли секунды, но блокировать
/// UI-поток на это нельзя. Работать без дерева исходников сервис обязан — тогда каталоги
/// пустые, а статус равен <see cref="ApiCatalogStatus.NoSources"/>.
/// </para>
/// </summary>
public interface IApiCatalogService
{
    /// <summary>Текущий снимок. Никогда не <c>null</c>; до первого построения — <see cref="ApiCatalogStatus.NotBuilt"/>.</summary>
    ApiCatalogSnapshot Current { get; }

    /// <summary>Снимок заменён целиком.</summary>
    event EventHandler<ApiCatalogSnapshot>? CatalogChanged;

    /// <summary>
    /// Строит каталоги по указанному дереву исходников. Не бросает при отсутствующем или битом пути:
    /// возвращает снимок со статусом <see cref="ApiCatalogStatus.NoSources"/>.
    /// </summary>
    /// <param name="wotSrcDir">Корень дампа uncompyle6 или <c>null</c>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Построенный снимок; он же становится <see cref="Current"/>.</returns>
    Task<ApiCatalogSnapshot> BuildAsync(string? wotSrcDir, CancellationToken ct = default);

    /// <summary>Генератор сниппетов на Python 2.7 по элементам каталога.</summary>
    SnippetGenerator Snippets { get; }
}
