namespace WotModCockpit.Core.Recipes;

/// <summary>
/// Сложность рецепта. Не «уровень программиста», а объём того, что придётся понять
/// про клиент, прежде чем вставленный код станет своим.
/// </summary>
public enum RecipeDifficulty
{
    /// <summary>Вставил, поменял строку — работает.</summary>
    Beginner = 0,

    /// <summary>Нужно понимать жизненный цикл мода: когда звать, когда отписываться.</summary>
    Intermediate = 1,

    /// <summary>Трогает чужой код (монкипатч, чужие API) и может сломать другие моды.</summary>
    Advanced = 2
}

/// <summary>Тематическая рубрика рецепта — по ней рецепты группируются в списке.</summary>
public enum RecipeCategory
{
    /// <summary>Каркас мода, точка входа, жизненный цикл.</summary>
    Lifecycle = 0,

    /// <summary>Логирование и диагностика.</summary>
    Logging = 1,

    /// <summary>Показать что-то игроку: системные сообщения, боевой лог.</summary>
    Ui = 2,

    /// <summary>Подписка на события клиента.</summary>
    Events = 3,

    /// <summary>Чтение состояния игры: бой, ангар, свой танк.</summary>
    GameState = 4,

    /// <summary>Ввод: клавиатура.</summary>
    Input = 5,

    /// <summary>Настройки и конфигурация.</summary>
    Configuration = 6,

    /// <summary>Монкипатчи и перехват методов клиента.</summary>
    Patching = 7,

    /// <summary>Интеграция с API чужих модов.</summary>
    Interop = 8
}

/// <summary>
/// Символ, без которого рецепт не заработает: модуль клиента и объявленное в нём имя.
/// Служит для проверки совместимости с конкретной версией игры через индекс API.
/// </summary>
/// <param name="Module">
/// Импортируемое имя модуля (<c>gui.SystemMessages</c>, <c>PlayerEvents</c>).
/// </param>
/// <param name="Name">
/// Имя внутри модуля (<c>pushMessage</c>, <c>g_playerEvents</c>) либо <c>null</c>,
/// если проверять надо только наличие самого модуля.
/// </param>
/// <param name="IsNative">
/// Модуль нативный (<c>BigWorld</c>, <c>ResMgr</c>, <c>Math</c>): у него нет <c>.py</c>,
/// в индексе клиента его нет и быть не может, поэтому проверять его бессмысленно.
/// </param>
/// <param name="IsExternal">
/// Модуль принадлежит ЧУЖОМУ моду (<c>gui.modsListApi</c>): в клиенте его нет,
/// он ставится отдельно. Отсутствие в индексе — норма, а не дефект.
/// </param>
public sealed record RecipeRequirement(
    string Module,
    string? Name = null,
    bool IsNative = false,
    bool IsExternal = false)
{
    /// <summary>Проверяемая по индексу зависимость: не нативная и не чужой мод.</summary>
    public bool IsCheckable => !IsNative && !IsExternal;

    /// <summary>Читаемое имя для показа: <c>gui.SystemMessages.pushMessage</c>.</summary>
    public string Display => Name is null ? Module : Module + "." + Name;
}

/// <summary>
/// Точка подстановки в коде рецепта: что именно ищется в тексте и чем заменяется.
/// Пользователю показывается <see cref="Label"/>, а подставляется значение из паспорта проекта.
/// </summary>
/// <param name="Token">Литерал в коде рецепта, который надо заменить.</param>
/// <param name="Label">Человекочитаемое название поля.</param>
/// <param name="Kind">Какое поле паспорта проекта сюда идёт.</param>
public sealed record RecipePlaceholder(string Token, string Label, RecipeParameterKind Kind);

/// <summary>Поле паспорта проекта, которое подставляется в рецепт.</summary>
public enum RecipeParameterKind
{
    /// <summary>Идентификатор мода целиком: <c>ru.local.battle_timer</c>.</summary>
    ModId = 0,

    /// <summary>Отображаемое имя: <c>Battle Timer</c>.</summary>
    DisplayName = 1,

    /// <summary>Тег лога без скобок: <c>BattleTimer</c>.</summary>
    LogTag = 2,

    /// <summary>Имя пакета в snake_case: <c>battle_timer</c>.</summary>
    Package = 3
}

/// <summary>
/// Готовый кусок кода на Python 2.7 под типовую задачу мода.
/// <para>Сам код лежит НЕ в этой записи, а во встроенном ресурсе сборки
/// (<c>Recipes/Recipes/rNN_*.py</c>): так его можно править как обычный питон,
/// прогонять валидатором <c>check_py2_syntax.py</c> и не экранировать кавычки в C#.</para>
/// </summary>
/// <param name="Id">Устойчивый идентификатор (<c>R01</c>). По нему рецепт адресуется из UI и тестов.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Category">Рубрика.</param>
/// <param name="Difficulty">Сложность.</param>
/// <param name="Summary">Одна строка: что это.</param>
/// <param name="Rationale">Зачем и когда применять; показывается рядом с кодом.</param>
/// <param name="Pitfall">Где ломается — самая ценная часть карточки.</param>
/// <param name="ResourceName">Имя встроенного ресурса с кодом.</param>
/// <param name="Requirements">Символы клиента, от которых зависит рецепт.</param>
public sealed record Recipe(
    string Id,
    string Title,
    RecipeCategory Category,
    RecipeDifficulty Difficulty,
    string Summary,
    string Rationale,
    string Pitfall,
    string ResourceName,
    IReadOnlyList<RecipeRequirement> Requirements)
{
    /// <summary>
    /// Код рецепта как есть, с токенами <c>MyMod</c> / <c>my_mod</c>.
    /// Читается из встроенного ресурса при первом обращении и кэшируется.
    /// </summary>
    public string Code => _code ??= RecipeLibrary.ReadResource(ResourceName);

    private string? _code;

    /// <summary>Строки текста для поиска: заголовок, аннотация, обоснование.</summary>
    public string SearchText => string.Concat(Id, " ", Title, " ", Summary, " ", Rationale);
}

/// <summary>
/// Значения, которые подставляются в рецепт. Берутся из паспорта открытого проекта,
/// чтобы вставленный код сразу писал в лог под тем же тегом, по которому фильтрует
/// живой <c>python.log</c> (этап 1).
/// </summary>
/// <param name="ModId">Идентификатор мода: <c>ru.local.battle_timer</c>.</param>
/// <param name="Package">Имя пакета: <c>battle_timer</c>.</param>
/// <param name="DisplayName">Отображаемое имя: <c>Battle Timer</c>.</param>
/// <param name="LogTag">Тег лога без скобок: <c>BattleTimer</c>.</param>
public sealed record RecipeParameters(
    string ModId,
    string Package,
    string DisplayName,
    string LogTag)
{
    /// <summary>
    /// Значения по умолчанию — ровно те токены, что стоят в исходном коде рецептов.
    /// Подстановка с ними не меняет ни байта, и именно поэтому её можно применять
    /// всегда, не проверяя, задан ли проект.
    /// </summary>
    public static RecipeParameters Default { get; } =
        new("ru.local.my_mod", "my_mod", "My Mod", "MyMod");

    /// <summary>Собирает параметры из проекта, выводя недостающее из имени пакета.</summary>
    /// <param name="modId">Идентификатор мода.</param>
    /// <param name="package">Имя пакета.</param>
    /// <param name="displayName">Отображаемое имя; пусто — вывести из пакета.</param>
    /// <param name="logTag">Тег лога; пусто — вывести из пакета.</param>
    /// <returns>Заполненные параметры.</returns>
    public static RecipeParameters From(string? modId, string? package, string? displayName = null, string? logTag = null)
    {
        string pkg = string.IsNullOrWhiteSpace(package) ? Default.Package : package.Trim();
        string tag = string.IsNullOrWhiteSpace(logTag) ? Scaffold.NameConventions.ToPascalCase(pkg) : logTag.Trim();
        string name = string.IsNullOrWhiteSpace(displayName) ? Scaffold.NameConventions.ToDisplayName(pkg) : displayName.Trim();
        string id = string.IsNullOrWhiteSpace(modId) ? "ru.local." + pkg : modId.Trim();
        return new RecipeParameters(id, pkg, name, tag);
    }
}

/// <summary>Как рецепт соотносится с индексом текущей версии клиента.</summary>
public enum RecipeCompatibility
{
    /// <summary>Индекс не собран — проверить нечем. Это НЕ «рецепт плох».</summary>
    Unknown = 0,

    /// <summary>Все проверяемые символы найдены в индексе.</summary>
    Ok = 1,

    /// <summary>Часть символов не найдена: рецепт требует проверки руками.</summary>
    NeedsReview = 2
}

/// <summary>
/// Результат сверки рецепта с индексом API текущего клиента.
/// </summary>
/// <param name="Recipe">Рецепт.</param>
/// <param name="Status">Итог.</param>
/// <param name="Missing">Символы, которых нет в индексе.</param>
/// <param name="CheckedCount">Сколько символов реально проверялось.</param>
public sealed record RecipeCompatibilityReport(
    Recipe Recipe,
    RecipeCompatibility Status,
    IReadOnlyList<RecipeRequirement> Missing,
    int CheckedCount)
{
    /// <summary>
    /// Готовая к показу строка.
    /// <para>
    /// Случай <see cref="CheckedCount"/> = 0 разведён с обычным «всё на месте» намеренно.
    /// У рецепта могут быть только непроверяемые зависимости — нативные модули движка
    /// (<c>BigWorld</c>, <c>ResMgr</c>) и API чужих модов, — и тогда строка
    /// «проверено символов: 0, все на месте» читается как противоречие: сверки не было,
    /// а панель рапортует об успехе.
    /// </para>
    /// </summary>
    public string Message => Status switch
    {
        RecipeCompatibility.Unknown => "индекс API не собран, совместимость не проверялась",
        RecipeCompatibility.Ok when CheckedCount == 0 =>
            "сверять нечего: рецепт опирается только на нативные модули движка и API чужих модов",
        RecipeCompatibility.Ok => $"проверено символов: {CheckedCount}, все на месте",
        _ => "нет в этой версии клиента: " + string.Join(", ", Missing.Select(m => m.Display))
    };
}
