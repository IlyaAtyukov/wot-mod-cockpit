namespace WotModCockpit.Core.Recipes;

/// <summary>
/// Каталог готовых кусков кода для мода: поиск, подстановка паспорта проекта
/// и сверка с индексом API текущего клиента.
/// </summary>
public interface IRecipeService
{
    /// <summary>Все рецепты.</summary>
    IReadOnlyList<Recipe> All { get; }

    /// <summary>Рецепт по идентификатору.</summary>
    /// <param name="id">Идентификатор, например <c>R07</c>.</param>
    /// <returns>Рецепт либо <c>null</c>.</returns>
    Recipe? ById(string? id);

    /// <summary>
    /// Поиск по заголовку, аннотации и обоснованию. Пустой запрос возвращает всё —
    /// это список по умолчанию, а не «ничего не найдено».
    /// </summary>
    /// <param name="query">Запрос; <c>null</c> или пусто — все рецепты.</param>
    /// <param name="category">Ограничить рубрикой.</param>
    /// <param name="difficulty">Ограничить сложностью.</param>
    /// <returns>Рецепты в порядке убывания релевантности.</returns>
    IReadOnlyList<Recipe> Search(string? query, RecipeCategory? category = null, RecipeDifficulty? difficulty = null);

    /// <summary>
    /// Подставляет паспорт проекта в код рецепта: идентификатор, имя пакета, отображаемое имя
    /// и тег лога. Тег обязан совпасть с тем, по которому фильтрует живой python.log.
    /// </summary>
    /// <param name="recipe">Рецепт.</param>
    /// <param name="parameters">Значения; <c>null</c> — оставить токены шаблона как есть.</param>
    /// <returns>Готовый к вставке код.</returns>
    string Render(Recipe recipe, RecipeParameters? parameters = null);

    /// <summary>
    /// Сверяет символы, которые использует рецепт, с индексом API текущего клиента.
    /// Нативные модули (BigWorld, ResMgr) и API чужих модов не проверяются: их в индексе
    /// клиента нет и быть не может.
    /// </summary>
    /// <param name="recipe">Рецепт.</param>
    /// <returns>Отчёт; при несобранном индексе — <see cref="RecipeCompatibility.Unknown"/>.</returns>
    RecipeCompatibilityReport CheckCompatibility(Recipe recipe);

    /// <summary>Сверяет все рецепты сразу — для показа значков в списке.</summary>
    /// <returns>Отчёты в порядке <see cref="All"/>.</returns>
    IReadOnlyList<RecipeCompatibilityReport> CheckAll();
}
