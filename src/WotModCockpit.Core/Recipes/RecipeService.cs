using WotModCockpit.Core.ApiIndex;

namespace WotModCockpit.Core.Recipes;

/// <summary>
/// Реализация каталога рецептов поверх индекса API этапа 2.
///
/// <para>Свой индекс здесь не строится: модули и символы берутся из
/// <see cref="IApiIndexService"/>, который уже держит 166 574 символа по 8052 файлам.
/// Карта «модуль → файл» строится один раз на снимок индекса и пересобирается,
/// когда индекс пересобрали (сравнение по <see cref="ApiIndex.ApiIndex.BuiltAt"/>).</para>
/// </summary>
public sealed class RecipeService : IRecipeService
{
    /// <summary>Глубина разворачивания <c>from X import *</c>. Трёх кругов хватает с запасом.</summary>
    private const int MaxStarDepth = 3;

    private readonly IApiIndexService? _index;
    private readonly SourceFileService _sources;
    private readonly object _gate = new();

    private ApiIndex.ApiIndex? _mapSource;
    private Dictionary<string, int>? _moduleToFile;

    /// <summary>Создаёт сервис.</summary>
    /// <param name="index">
    /// Индекс API. <c>null</c> допустим: тогда совместимость всегда
    /// <see cref="RecipeCompatibility.Unknown"/>, а всё остальное работает.
    /// </param>
    /// <param name="sources">
    /// Чтение исходников клиента. Нужно там, где индекса недостаточно: константы
    /// (<c>SM_TYPE</c>, <c>KEY_F1</c>) в индекс намеренно не попадают
    /// (<c>ApiIndexOptions.IndexConstants</c> выключен, иначе индекс удваивается),
    /// а модули-ре-экспорты вроде <c>helpers.dependency</c> вообще не содержат
    /// собственных объявлений. <c>null</c> — завести своё.
    /// </param>
    public RecipeService(IApiIndexService? index = null, SourceFileService? sources = null)
    {
        _index = index;
        _sources = sources ?? new SourceFileService();
    }

    /// <inheritdoc />
    public IReadOnlyList<Recipe> All => RecipeLibrary.All;

    /// <inheritdoc />
    public Recipe? ById(string? id) => RecipeLibrary.ById(id);

    /// <inheritdoc />
    public IReadOnlyList<Recipe> Search(string? query, RecipeCategory? category = null, RecipeDifficulty? difficulty = null)
    {
        IEnumerable<Recipe> source = RecipeLibrary.All;

        if (category is not null) source = source.Where(r => r.Category == category);
        if (difficulty is not null) source = source.Where(r => r.Difficulty == difficulty);

        if (string.IsNullOrWhiteSpace(query)) return [.. source];

        string q = query.Trim();

        // Ранжирование намеренно простое: рецептов 16, а не 16 000. Точное попадание в
        // идентификатор — выше всего, дальше заголовок, дальше остальной текст карточки.
        var scored = new List<(Recipe Recipe, int Score)>();
        foreach (Recipe r in source)
        {
            int score = 0;
            if (r.Id.Equals(q, StringComparison.OrdinalIgnoreCase)) score += 1000;
            if (r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 100;
            if (r.Summary.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 50;
            if (r.Rationale.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 10;
            if (r.Pitfall.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 10;
            if (r.Requirements.Any(x => x.Display.Contains(q, StringComparison.OrdinalIgnoreCase))) score += 30;

            if (score > 0) scored.Add((r, score));
        }

        return [.. scored.OrderByDescending(x => x.Score).ThenBy(x => x.Recipe.Id, StringComparer.Ordinal).Select(x => x.Recipe)];
    }

    /// <inheritdoc />
    public string Render(Recipe recipe, RecipeParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        string code = recipe.Code;
        if (parameters is null) return code;

        // ОДИН проход, а не четыре Replace подряд. Токены пересекаются ('my_mod' — часть
        // 'ru.local.my_mod'), и цепочка Replace бьёт по своему же результату: пакет
        // 'my_mod_extra' давал MOD_ID = 'ru.local.my_mod_extra_extra', а отображаемое имя
        // 'MyMod Pro' следующей заменой тега превращалось в 'MM Pro'.
        RecipeParameters d = RecipeParameters.Default;
        return Scaffold.NameConventions.ReplaceTokensOnce(
            code,
            [
                (d.ModId, parameters.ModId),
                (d.DisplayName, parameters.DisplayName),
                (d.LogTag, parameters.LogTag),
                (d.Package, parameters.Package)
            ],
            out _);
    }

    /// <inheritdoc />
    public RecipeCompatibilityReport CheckCompatibility(Recipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        ApiIndex.ApiIndex? index = _index?.Index;
        if (index is null || index.IsEmpty)
        {
            return new RecipeCompatibilityReport(recipe, RecipeCompatibility.Unknown, [], 0);
        }

        Dictionary<string, int> map = ModuleMap(index);

        var missing = new List<RecipeRequirement>();
        int checkedCount = 0;

        foreach (RecipeRequirement req in recipe.Requirements)
        {
            if (!req.IsCheckable) continue;
            checkedCount++;

            if (!map.TryGetValue(req.Module, out int fileId))
            {
                missing.Add(req);
                continue;
            }

            if (req.Name is null) continue;

            if (!ModuleExports(index, map, req.Module, req.Name, MaxStarDepth)) missing.Add(req);
        }

        RecipeCompatibility status = missing.Count == 0 ? RecipeCompatibility.Ok : RecipeCompatibility.NeedsReview;
        return new RecipeCompatibilityReport(recipe, status, missing, checkedCount);
    }

    /// <inheritdoc />
    public IReadOnlyList<RecipeCompatibilityReport> CheckAll() =>
        [.. RecipeLibrary.All.Select(CheckCompatibility)];

    /// <summary>
    /// Модуль экспортирует имя. Три источника, в порядке дешевизны.
    ///
    /// <para>(1) Символ индекса в этом файле — так находятся классы, функции, события
    /// и модульные синглтоны. Ищется среди символов ИМЕННО ЭТОГО файла, а не по всему
    /// индексу: <c>instance</c> объявлено в клиенте десятки раз, и глобальный поиск
    /// подтвердил бы что угодно.</para>
    ///
    /// <para>(2) Подмодуль пакета: <c>helpers.i18n</c> при <c>Module=helpers</c>.</para>
    ///
    /// <para>(3) Текст исходника — присваивание на нулевом отступе и разворачивание
    /// <c>from X import *</c>. Без этого шага <c>helpers/dependency.py</c> (чистый
    /// ре-экспорт <c>from dependency_injection_container import *</c>) не подтвердил бы
    /// ни одного своего имени, а константы <c>SM_TYPE</c> и <c>KEY_F1</c> не подтвердились
    /// бы никогда: их в индексе нет по решению этапа 2.</para>
    /// </summary>
    private bool ModuleExports(ApiIndex.ApiIndex index, Dictionary<string, int> map, string module, string name, int depth)
    {
        if (depth < 0 || !map.TryGetValue(module, out int fileId)) return false;

        foreach (ApiSymbol s in index.SymbolsOfFile(fileId))
        {
            if (string.Equals(s.Name, name, StringComparison.Ordinal)) return true;
        }

        if (map.ContainsKey(module + "." + name)) return true;

        string? text = _sources.ReadText(index.Files[fileId].FullPath(index.RootPath));
        if (text is null) return false;

        var starTargets = new List<string>();

        foreach (string raw in text.Split('\n'))
        {
            // Только нулевой отступ: присваивание внутри функции экспортом не является.
            if (raw.Length == 0 || char.IsWhiteSpace(raw[0])) continue;

            string line = raw.TrimEnd();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line.StartsWith("from ", StringComparison.Ordinal))
            {
                int importAt = line.IndexOf(" import ", StringComparison.Ordinal);
                if (importAt < 0) continue;

                string source = line[5..importAt].Trim();
                string imported = line[(importAt + 8)..].Trim();

                if (imported == "*")
                {
                    if (source.Length > 0 && source[0] != '.') starTargets.Add(source);
                    continue;
                }

                foreach (string part in imported.Split(','))
                {
                    // 'X as Y' экспортирует Y.
                    string bound = part.Trim().TrimEnd('\\', ')', '(').Trim();
                    int asAt = bound.IndexOf(" as ", StringComparison.Ordinal);
                    if (asAt >= 0) bound = bound[(asAt + 4)..].Trim();
                    if (string.Equals(bound, name, StringComparison.Ordinal)) return true;
                }

                continue;
            }

            if (IsAssignmentTo(line, name)) return true;
        }

        foreach (string target in starTargets)
        {
            if (ModuleExports(index, map, target, name, depth - 1)) return true;
        }

        return false;
    }

    /// <summary>Строка вида <c>NAME = ...</c> на нулевом отступе (но не <c>NAME == ...</c>).</summary>
    private static bool IsAssignmentTo(string line, string name)
    {
        if (!line.StartsWith(name, StringComparison.Ordinal)) return false;

        int i = name.Length;
        while (i < line.Length && line[i] == ' ') i++;

        return i < line.Length && line[i] == '=' && (i + 1 >= line.Length || line[i + 1] != '=');
    }

    /// <summary>
    /// Карта «точечное имя модуля → идентификатор файла». Строится один раз на снимок
    /// индекса: 8052 файла обходить на каждый рецепт незачем.
    /// </summary>
    private Dictionary<string, int> ModuleMap(ApiIndex.ApiIndex index)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_mapSource, index) && _moduleToFile is not null) return _moduleToFile;

            var map = new Dictionary<string, int>(index.Files.Count, StringComparer.Ordinal);
            for (int i = 0; i < index.Files.Count; i++)
            {
                string dotted = index.Files[i].DottedModule;
                if (dotted.Length > 0) map.TryAdd(dotted, i);
            }

            _mapSource = index;
            _moduleToFile = map;
            return map;
        }
    }
}
