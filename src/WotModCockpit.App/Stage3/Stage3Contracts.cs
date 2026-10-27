using WotModCockpit.Core.Models;

namespace WotModCockpit.App.Stage3;

/// <summary>
/// ШОВ между экранами этапа 3 (Problems, рецепты, мастер нового проекта) и реализациями Core.
/// <para>
/// Зачем он есть — ровно та же причина, что у <see cref="ApiBrowser.IApiBrowserBackend"/>:
/// UI и три реализации Core (preflight-правила, каталог рецептов, скаффолдер) пишутся
/// параллельно. Экраны обязаны собираться, открываться и честно объяснять своё состояние
/// в любой момент этой работы, а не только после того, как контракты Core дошли до финала.
/// </para>
/// <para>
/// <b>Как подключить настоящий Core.</b> Написать по одному классу на интерфейс
/// (<see cref="IPreflightBackend"/>, <see cref="IRecipeBackend"/>, <see cref="IScaffoldBackend"/>)
/// и заменить ими регистрацию заглушек в <c>AppServices.BuildServiceProvider</c>. Ни одна
/// ViewModel и ни один XAML на типы Core этапа 3 напрямую не ссылаются.
/// </para>
/// </summary>
public interface IPreflightBackend
{
    /// <summary>Анализ подключён и им можно пользоваться.</summary>
    bool IsAvailable { get; }

    /// <summary>Почему анализ недоступен. Пусто, если <see cref="IsAvailable"/>.</summary>
    string UnavailableReason { get; }

    /// <summary>
    /// Карточки всех правил: код, заголовок, обоснование, серьёзность по умолчанию.
    /// Нужны панели Problems, чтобы объяснять «почему это плохо» даже там, где сама находка
    /// пришла коротким сообщением, и чтобы правило можно было выключить осознанно.
    /// </summary>
    IReadOnlyList<PreflightRuleCard> Rules { get; }

    /// <summary>
    /// Прогнать статический анализ по коду мода.
    /// </summary>
    /// <param name="project">Проект мода.</param>
    /// <param name="options">Что именно гонять (выключенные правила, pyright).</param>
    /// <param name="cancellationToken">Отмена: анализ идёт и перед сборкой, и по кнопке.</param>
    /// <returns>
    /// Отчёт. Отказ реализации — это <see cref="PreflightReport.Error"/>, а не исключение:
    /// панель Problems обязана пережить незаконченный Core.
    /// </returns>
    Task<PreflightReport> AnalyzeAsync(
        ModProject project,
        PreflightOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Проверить, есть ли на машине pyright и можно ли его запускать.
    /// Вызывается лениво — только когда пользователь включает соответствующий тумблер.
    /// </summary>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Состояние вместе с готовой к показу строкой.</returns>
    Task<PyrightProbe> ProbePyrightAsync(CancellationToken cancellationToken);
}

/// <summary>Насколько серьёзна находка. Шкала — по последствию у игрока, а не «по ощущениям».</summary>
public enum ProblemSeverity
{
    /// <summary>Справка: мод исправен, но факт про клиент знать полезно.</summary>
    Info = 0,

    /// <summary>Сломается вероятностно: утечка подписки, незакрытая петля, обрыв чужих модов.</summary>
    Warning = 1,

    /// <summary>Клиент закроется, мод не загрузится или не будет найден движком.</summary>
    Error = 2
}

/// <summary>
/// Одна находка для панели Problems.
/// <para>
/// Поля <see cref="Explanation"/> и <see cref="Hint"/> здесь не украшение: панель обязана
/// показывать не только «что», но и «почему это плохо» и «как чинить». Мододел не обязан
/// помнить, что в <c>game.py:init()</c> стоит <c>BigWorld.quit()</c> — это работа диагностики.
/// </para>
/// </summary>
public sealed record ProblemItem
{
    /// <summary>Код правила: <c>WOT001</c>…<c>WOT021</c>, <c>pyright:…</c>, <c>NO_ENTRY_POINT</c>.</summary>
    public required string Code { get; init; }

    /// <summary>Серьёзность.</summary>
    public required ProblemSeverity Severity { get; init; }

    /// <summary>Что произойдёт. Одно-два предложения, с последствием.</summary>
    public required string Message { get; init; }

    /// <summary>Развёрнутое «почему это плохо» — берётся из карточки правила.</summary>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>Как чинить: конкретное действие, а не «исправьте ошибку».</summary>
    public string Hint { get; init; } = string.Empty;

    /// <summary>
    /// Место в декомпилированном клиенте, из которого следует правило
    /// (<c>scripts/client/game.py:127</c>). По нему открывается браузер API этапа 2.
    /// </summary>
    public string Reference { get; init; } = string.Empty;

    /// <summary>Кусок исходной строки мода — то, на что показывает находка.</summary>
    public string Snippet { get; init; } = string.Empty;

    /// <summary>Абсолютный путь к файлу. Для находок уровня проекта — корень проекта.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Путь относительно корня проекта с прямыми слэшами. Пусто для находок уровня проекта.</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>Строка, 1-based. 0 — находка не привязана к строке.</summary>
    public int Line { get; init; }

    /// <summary>Колонка, 1-based. 0 — не привязана.</summary>
    public int Column { get; init; }

    /// <summary>Кто нашёл: <c>preflight</c>, <c>pyright</c>, <c>проект</c>.</summary>
    public string Source { get; init; } = "preflight";

    /// <summary>
    /// Символ клиента, о котором находка (для WOT002/003/004). Пусто — переход
    /// в браузер API невозможен, и кнопка перехода не показывается.
    /// </summary>
    public string ApiSymbol { get; init; } = string.Empty;

    /// <summary>Виртуальный путь клиента (<c>scripts/client/…</c>) для перехода в браузер API.</summary>
    public string ApiVirtualPath { get; init; } = string.Empty;

    /// <summary>Находка привязана к строке файла.</summary>
    public bool HasLocation => Line > 0 && FilePath.Length > 0;

    /// <summary>Строка «файл:строка» для списка.</summary>
    public string Location => Line > 0
        ? $"{(RelativePath.Length > 0 ? RelativePath : FilePath)}:{Line}"
        : RelativePath.Length > 0 ? RelativePath : FilePath;
}

/// <summary>
/// Карточка правила: то, что панель показывает в подробностях и по чему правило выключают.
/// </summary>
/// <param name="Code">Код правила.</param>
/// <param name="Title">Короткий заголовок.</param>
/// <param name="Rationale">Почему это правило вообще есть (факт про клиент).</param>
/// <param name="DefaultSeverity">Серьёзность по умолчанию.</param>
public sealed record PreflightRuleCard(
    string Code,
    string Title,
    string Rationale,
    ProblemSeverity DefaultSeverity);

/// <summary>Что и как гонять в одном прогоне.</summary>
public sealed record PreflightOptions
{
    /// <summary>Коды правил, которые пользователь скрыл. Реализация обязана их не считать вовсе.</summary>
    public IReadOnlyCollection<string> DisabledRules { get; init; } = Array.Empty<string>();

    /// <summary>Дополнительно запустить pyright и разобрать его JSON в те же находки.</summary>
    public bool RunPyright { get; init; }
}

/// <summary>
/// Отчёт одного прогона. Пустой список находок и заполненный <see cref="Error"/> —
/// разные вещи: первое значит «чисто», второе — «проверить не удалось».
/// </summary>
/// <param name="Problems">Находки.</param>
/// <param name="FilesAnalyzed">Сколько файлов мода разобрано.</param>
/// <param name="Elapsed">Сколько занял прогон.</param>
public sealed record PreflightReport(
    IReadOnlyList<ProblemItem> Problems,
    int FilesAnalyzed,
    TimeSpan Elapsed)
{
    /// <summary>Почему прогон не состоялся. <c>null</c> — прогон прошёл.</summary>
    public string? Error { get; init; }

    /// <summary>Замечание о прогоне, которое не является находкой (например «pyright пропущен»).</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>Пустой отчёт.</summary>
    public static PreflightReport Empty { get; } =
        new(Array.Empty<ProblemItem>(), 0, TimeSpan.Zero);

    /// <summary>Отчёт-отказ.</summary>
    /// <param name="error">Причина.</param>
    /// <returns>Отчёт без находок и с причиной.</returns>
    public static PreflightReport Failed(string error) =>
        new(Array.Empty<ProblemItem>(), 0, TimeSpan.Zero) { Error = error };
}

/// <summary>Что известно про pyright на машине пользователя.</summary>
/// <param name="IsAvailable">Запускать можно.</param>
/// <param name="Description">Готовая к показу строка: версия либо причина отказа.</param>
public sealed record PyrightProbe(bool IsAvailable, string Description);

// ===================================================================== рецепты

/// <summary>Шов к каталогу рецептов Core.</summary>
public interface IRecipeBackend
{
    /// <summary>Каталог подключён.</summary>
    bool IsAvailable { get; }

    /// <summary>Почему каталог недоступен.</summary>
    string UnavailableReason { get; }

    /// <summary>Все рецепты каталога.</summary>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Рецепты в порядке каталога.</returns>
    Task<IReadOnlyList<RecipeCard>> GetRecipesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Сверить рецепт с индексом API текущего клиента.
    /// </summary>
    /// <param name="recipe">Рецепт.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>
    /// Итог сверки. <see cref="RecipeCompatibilityState.Unknown"/> означает «индекса нет,
    /// проверить нечем» — это НЕ «рецепт плох», и UI обязан различать эти два случая.
    /// </returns>
    Task<RecipeCompatibility> CheckAsync(RecipeCard recipe, CancellationToken cancellationToken);
}

/// <summary>Сложность рецепта: сколько надо понять про клиент, прежде чем код станет своим.</summary>
public enum RecipeLevel
{
    /// <summary>Вставил, поменял строку — работает.</summary>
    Beginner = 0,

    /// <summary>Нужно понимать жизненный цикл мода: когда звать, когда отписываться.</summary>
    Intermediate = 1,

    /// <summary>Трогает чужой код и может сломать другие моды.</summary>
    Advanced = 2
}

/// <summary>Один рецепт каталога.</summary>
public sealed record RecipeCard
{
    /// <summary>Устойчивый идентификатор (<c>R01</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Заголовок.</summary>
    public required string Title { get; init; }

    /// <summary>Рубрика для группировки списка.</summary>
    public string Category { get; init; } = "Прочее";

    /// <summary>Сложность.</summary>
    public RecipeLevel Level { get; init; } = RecipeLevel.Intermediate;

    /// <summary>Одна строка: что это.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Зачем и когда применять.</summary>
    public string Rationale { get; init; } = string.Empty;

    /// <summary>Где ломается — самая ценная часть карточки.</summary>
    public string Pitfall { get; init; } = string.Empty;

    /// <summary>Код на Python 2.7 с токенами по умолчанию (<c>MyMod</c>, <c>my_mod</c>).</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>Символы клиента, от которых зависит рецепт — для сверки с индексом и перехода в браузер API.</summary>
    public IReadOnlyList<RecipeSymbol> Symbols { get; init; } = Array.Empty<RecipeSymbol>();

    /// <summary>Строка для поиска по каталогу.</summary>
    public string SearchText => string.Concat(Id, " ", Title, " ", Summary, " ", Rationale, " ", Category);
}

/// <summary>Символ клиента, нужный рецепту.</summary>
/// <param name="Module">Импортируемый модуль (<c>gui.SystemMessages</c>, <c>PlayerEvents</c>).</param>
/// <param name="Name">Имя внутри модуля либо <c>null</c>, если важен только сам модуль.</param>
/// <param name="Checkable">
/// Символ вообще можно проверить по индексу. Нативные модули (<c>BigWorld</c>) и API чужих
/// модов (<c>gui.modsListApi</c>) в индексе клиента отсутствуют законно.
/// </param>
public sealed record RecipeSymbol(string Module, string? Name = null, bool Checkable = true)
{
    /// <summary>Читаемое имя: <c>gui.SystemMessages.pushMessage</c>.</summary>
    public string Display => Name is null ? Module : Module + "." + Name;
}

/// <summary>Как рецепт соотносится с индексом текущего клиента.</summary>
public enum RecipeCompatibilityState
{
    /// <summary>Индекс не собран — проверить нечем.</summary>
    Unknown = 0,

    /// <summary>Все проверяемые символы найдены.</summary>
    Ok = 1,

    /// <summary>Часть символов не найдена: рецепт требует проверки руками.</summary>
    NeedsReview = 2
}

/// <summary>Итог сверки рецепта с индексом.</summary>
/// <param name="State">Итог.</param>
/// <param name="Message">Готовая к показу строка.</param>
/// <param name="Missing">Символы, которых нет в индексе.</param>
public sealed record RecipeCompatibility(
    RecipeCompatibilityState State,
    string Message,
    IReadOnlyList<string> Missing)
{
    /// <summary>«Не проверяли»: индекса нет.</summary>
    public static RecipeCompatibility Unknown { get; } = new(
        RecipeCompatibilityState.Unknown,
        "индекс API не собран — совместимость не проверялась",
        Array.Empty<string>());
}

// ===================================================================== мастер проекта

/// <summary>Шов к скаффолдеру нового проекта.</summary>
public interface IScaffoldBackend
{
    /// <summary>Мастер подключён.</summary>
    bool IsAvailable { get; }

    /// <summary>Почему мастер недоступен.</summary>
    string UnavailableReason { get; }

    /// <summary>
    /// Каталог шаблона по умолчанию (<c>…\wot-mod-template</c>: переменная окружения, настройка
    /// <c>templateDir</c> или соседний каталог — см. <c>TemplateLocator</c>) либо пусто.
    /// </summary>
    string DefaultTemplateDir { get; }

    /// <summary>
    /// Дополнительные замечания, которых UI сам знать не может: например, что имя пакета
    /// совпало с именем модуля клиента и импорты в игре начнут разъезжаться.
    /// </summary>
    /// <param name="request">Заполненная форма.</param>
    /// <returns>Замечания; пустой список — возражений нет.</returns>
    IReadOnlyList<ScaffoldIssue> Validate(ScaffoldRequest request);

    /// <summary>
    /// Что именно будет создано. Вызывается на каждое изменение формы — обязан быть дешёвым
    /// и НИЧЕГО не писать на диск.
    /// </summary>
    /// <param name="request">Заполненная форма.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>План: файлы, переименования, подстановки.</returns>
    Task<ScaffoldPlan> PlanAsync(ScaffoldRequest request, CancellationToken cancellationToken);

    /// <summary>Создать проект по плану.</summary>
    /// <param name="request">Заполненная форма.</param>
    /// <param name="progress">Куда писать ход работы (строками, как build.py).</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Итог. Отказ — это <see cref="ScaffoldResult.Success"/> = false, а не исключение.</returns>
    Task<ScaffoldResult> CreateAsync(
        ScaffoldRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Форма мастера. Пять полей, из которых выводятся двенадцать подстановок.</summary>
public sealed record ScaffoldRequest
{
    /// <summary>Каталог шаблона, откуда копировать.</summary>
    public string TemplateDir { get; init; } = string.Empty;

    /// <summary>Каталог, КОТОРЫЙ будет создан (родитель + имя проекта).</summary>
    public string TargetDir { get; init; } = string.Empty;

    /// <summary>Идентификатор мода: <c>ru.local.battle_timer</c>.</summary>
    public string ModId { get; init; } = string.Empty;

    /// <summary>Имя python-пакета: <c>battle_timer</c>. Из него выводится имя точки входа.</summary>
    public string Package { get; init; } = string.Empty;

    /// <summary>Отображаемое имя: <c>Battle Timer</c>.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Версия мода: <c>1.0.0</c>.</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>Тег лога: <c>BattleTimer</c>. Скобки допустимы и снимаются автоматически.</summary>
    public string LogTag { get; init; } = string.Empty;

    /// <summary>Описание для meta.xml.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Корень установленной игры из настроек кокпита. Нужен ровно для одного: не дать
    /// создать проект внутри игры, которая во всём остальном UI подписана «только чтение».
    /// Пусто — проверка не делается.
    /// </summary>
    public string GameDir { get; init; } = string.Empty;
}

/// <summary>Что делает мастер с одним файлом шаблона.</summary>
public enum ScaffoldActionKind
{
    /// <summary>Копируется как есть.</summary>
    Copy = 0,

    /// <summary>Копируется под новым именем.</summary>
    Rename = 1,

    /// <summary>Копируется с подстановкой констант внутри.</summary>
    Rewrite = 2,

    /// <summary>Каталог.</summary>
    Directory = 3,

    /// <summary>Создаётся заново, в шаблоне такого файла нет.</summary>
    Create = 4
}

/// <summary>Одна строка предпросмотра мастера.</summary>
/// <param name="RelativePath">Путь в новом проекте.</param>
/// <param name="Kind">Что с ним будет.</param>
/// <param name="Note">Пояснение: какие именно константы подставляются.</param>
public sealed record ScaffoldFileAction(string RelativePath, ScaffoldActionKind Kind, string Note);

/// <summary>Замечание мастера по форме.</summary>
/// <param name="Field">Имя поля формы, к которому относится (<c>Package</c>, <c>ModId</c>, …).</param>
/// <param name="Message">Текст.</param>
/// <param name="Blocking">Создавать нельзя, пока не исправлено.</param>
public sealed record ScaffoldIssue(string Field, string Message, bool Blocking);

/// <summary>План: что появится на диске.</summary>
/// <param name="Files">Файлы и каталоги.</param>
/// <param name="Issues">Замечания к форме.</param>
public sealed record ScaffoldPlan(
    IReadOnlyList<ScaffoldFileAction> Files,
    IReadOnlyList<ScaffoldIssue> Issues)
{
    /// <summary>Пустой план.</summary>
    public static ScaffoldPlan Empty { get; } =
        new(Array.Empty<ScaffoldFileAction>(), Array.Empty<ScaffoldIssue>());

    /// <summary>Есть замечание, при котором создавать нельзя.</summary>
    public bool HasBlocking => Issues.Any(i => i.Blocking);
}

/// <summary>Итог создания проекта.</summary>
/// <param name="Success">Проект создан.</param>
/// <param name="ProjectDir">Куда создан.</param>
/// <param name="Message">Строка для показа.</param>
/// <param name="Log">Подробности: что копировалось, что подставлялось, что проверялось.</param>
public sealed record ScaffoldResult(
    bool Success,
    string ProjectDir,
    string Message,
    IReadOnlyList<string> Log)
{
    /// <summary>Отказ.</summary>
    /// <param name="message">Причина.</param>
    /// <returns>Неуспешный итог.</returns>
    public static ScaffoldResult Failed(string message) =>
        new(false, string.Empty, message, Array.Empty<string>());
}

// ===================================================================== заглушки

/// <summary>
/// «Анализ не подключён». Живёт в контейнере, пока не зарегистрирован адаптер к Core,
/// и существует ровно ради одного: панель Problems обязана открываться, объяснять
/// своё состояние и НЕ падать.
/// </summary>
public sealed class UnavailablePreflightBackend : IPreflightBackend
{
    private const string Reason = "реализация preflight не подключена (IPreflightBackend)";

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public string UnavailableReason => Reason;

    /// <inheritdoc />
    public IReadOnlyList<PreflightRuleCard> Rules => Array.Empty<PreflightRuleCard>();

    /// <inheritdoc />
    public Task<PreflightReport> AnalyzeAsync(
        ModProject project,
        PreflightOptions options,
        CancellationToken cancellationToken)
        => Task.FromResult(PreflightReport.Failed(Reason));

    /// <inheritdoc />
    public Task<PyrightProbe> ProbePyrightAsync(CancellationToken cancellationToken)
        => Task.FromResult(new PyrightProbe(false, Reason));
}

/// <summary>«Каталог рецептов не подключён».</summary>
public sealed class UnavailableRecipeBackend : IRecipeBackend
{
    private const string Reason = "каталог рецептов не подключён (IRecipeBackend)";

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public string UnavailableReason => Reason;

    /// <inheritdoc />
    public Task<IReadOnlyList<RecipeCard>> GetRecipesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RecipeCard>>(Array.Empty<RecipeCard>());

    /// <inheritdoc />
    public Task<RecipeCompatibility> CheckAsync(RecipeCard recipe, CancellationToken cancellationToken)
        => Task.FromResult(RecipeCompatibility.Unknown);
}

/// <summary>«Мастер нового проекта не подключён».</summary>
public sealed class UnavailableScaffoldBackend : IScaffoldBackend
{
    private const string Reason = "мастер нового проекта не подключён (IScaffoldBackend)";

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public string UnavailableReason => Reason;

    /// <inheritdoc />
    public string DefaultTemplateDir => string.Empty;

    /// <inheritdoc />
    public IReadOnlyList<ScaffoldIssue> Validate(ScaffoldRequest request)
        => new[] { new ScaffoldIssue(string.Empty, Reason, true) };

    /// <inheritdoc />
    public Task<ScaffoldPlan> PlanAsync(ScaffoldRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new ScaffoldPlan(
            Array.Empty<ScaffoldFileAction>(),
            new[] { new ScaffoldIssue(string.Empty, Reason, true) }));

    /// <inheritdoc />
    public Task<ScaffoldResult> CreateAsync(
        ScaffoldRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
        => Task.FromResult(ScaffoldResult.Failed(Reason));
}
