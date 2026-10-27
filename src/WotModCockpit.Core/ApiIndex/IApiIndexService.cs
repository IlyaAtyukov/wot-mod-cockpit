namespace WotModCockpit.Core.ApiIndex;

/// <summary>Состояние индекса API.</summary>
public enum ApiIndexBuildState
{
    /// <summary>Путь к дереву исходников не задан. Это НЕ ошибка: у другого пользователя дерева может не быть.</summary>
    NotConfigured = 0,

    /// <summary>Путь задан, но каталога нет.</summary>
    RootMissing = 1,

    /// <summary>Идёт сборка.</summary>
    Building = 2,

    /// <summary>Индекс готов.</summary>
    Ready = 3,

    /// <summary>Сборка упала.</summary>
    Failed = 4,

    /// <summary>Сборку отменили.</summary>
    Cancelled = 5
}

/// <summary>Состояние индекса в виде, готовом к показу.</summary>
/// <param name="State">Состояние.</param>
/// <param name="RootPath">Корень дерева, по которому собран (или должен быть собран) индекс.</param>
/// <param name="FileCount">Сколько файлов проиндексировано.</param>
/// <param name="SymbolCount">Сколько символов найдено.</param>
/// <param name="ErrorCount">Сколько файлов пропущено из-за ошибок.</param>
/// <param name="BuildTime">Сколько заняла сборка.</param>
/// <param name="Message">Готовая к показу строка.</param>
public sealed record ApiIndexBuildStatus(
    ApiIndexBuildState State,
    string RootPath,
    int FileCount,
    int SymbolCount,
    int ErrorCount,
    TimeSpan BuildTime,
    string Message)
{
    /// <summary>Индексом можно пользоваться.</summary>
    public bool IsReady => State == ApiIndexBuildState.Ready;

    /// <summary>Идёт сборка.</summary>
    public bool IsBuilding => State == ApiIndexBuildState.Building;

    /// <summary>
    /// Нужно показать пустое состояние с кнопкой «указать папку», а не ошибку.
    /// </summary>
    public bool NeedsSourceTree => State is ApiIndexBuildState.NotConfigured or ApiIndexBuildState.RootMissing;

    /// <summary>Стартовое состояние: ничего не собрано.</summary>
    public static ApiIndexBuildStatus NotBuilt { get; } = new(
        ApiIndexBuildState.NotConfigured, string.Empty, 0, 0, 0, TimeSpan.Zero,
        SourceTreeLayout.NotConfigured.Describe());
}

/// <summary>
/// Фасад браузера API для UI: асинхронная сборка индекса, состояние, поиск символов
/// и поиск ссылок.
///
/// <para>Обязан корректно работать <b>без дерева исходников вообще</b>: пустой индекс,
/// внятный статус, никаких исключений — дерева на машине пользователя может не быть,
/// и это штатная ситуация, а не сбой.</para>
/// </summary>
public interface IApiIndexService
{
    /// <summary>Текущий индекс. Пока сборка не прошла — <see cref="ApiIndex.Empty"/>, но никогда не <c>null</c>.</summary>
    ApiIndex Index { get; }

    /// <summary>Текущее состояние.</summary>
    ApiIndexBuildStatus Status { get; }

    /// <summary>
    /// Файлы, которые не удалось разобрать при последней сборке. Показывать обязательно:
    /// если счётчик вырастет на новой версии клиента, мододел должен это заметить.
    /// </summary>
    IReadOnlyList<ApiIndexBuildError> Errors { get; }

    /// <summary>Состояние изменилось (в том числе началась и закончилась сборка).</summary>
    event EventHandler<ApiIndexBuildStatus>? StatusChanged;

    /// <summary>
    /// Пересобирает индекс. Повторный вызов во время сборки отменяет предыдущую.
    /// </summary>
    /// <param name="sourceRoot">Корень дерева; <c>null</c> = взять из настроек (<c>WotSrcDir</c>).</param>
    /// <param name="progress">Приёмник прогресса; проценты в UI не нужны, нужен признак жизни.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Итоговое состояние. Исключений не бросает, отмена возвращает <see cref="ApiIndexBuildState.Cancelled"/>.</returns>
    Task<ApiIndexBuildStatus> RebuildAsync(string? sourceRoot = null, IProgress<ApiIndexProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Собирает индекс, если он ещё не собран для этого корня. Если собран — ничего не делает.
    /// </summary>
    /// <param name="sourceRoot">Корень дерева; <c>null</c> = из настроек.</param>
    /// <param name="progress">Приёмник прогресса.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Состояние.</returns>
    Task<ApiIndexBuildStatus> EnsureBuiltAsync(string? sourceRoot = null, IProgress<ApiIndexProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Отменяет текущую сборку, если она идёт.</summary>
    void Cancel();

    /// <summary>Поиск символов с ранжированием.</summary>
    /// <param name="query">Запрос.</param>
    /// <param name="options">Настройки поиска.</param>
    /// <returns>Результаты по убыванию балла; пустой список, если индекс не собран.</returns>
    IReadOnlyList<ApiSymbol> Search(string? query, SymbolSearchOptions? options = null);

    /// <summary>Поиск ссылок «где это вызывается».</summary>
    /// <param name="query">Запрос.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Результат поиска.</returns>
    Task<XrefResult> FindReferencesAsync(XrefQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Превращает кадр traceback из <c>python.log</c> в символ браузера API.
    /// Это мост к этапу 1: клик по фрейму лога должен открывать карточку метода.
    /// </summary>
    /// <param name="virtualPath">Виртуальный путь из лога, например <c>scripts/client/Avatar.py</c>.</param>
    /// <param name="line">Номер строки из лога.</param>
    /// <returns>Символ, внутри которого лежит строка, либо <c>null</c>.</returns>
    ApiSymbol? ResolveTracebackFrame(string? virtualPath, int line);

    /// <summary>Кусок исходника вокруг объявления символа.</summary>
    /// <param name="symbol">Символ.</param>
    /// <param name="before">Сколько строк до.</param>
    /// <param name="after">Сколько строк после.</param>
    /// <returns>Кусок либо <c>null</c>.</returns>
    SourceSnippet? GetSnippet(ApiSymbol symbol, int before = 0, int after = 12);

    /// <summary>Непосредственные потомки символа: методы класса, вложенные классы, события класса.</summary>
    /// <param name="symbol">Символ.</param>
    /// <returns>Потомки.</returns>
    IReadOnlyList<ApiSymbol> Children(ApiSymbol symbol);
}
