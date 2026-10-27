using WotModCockpit.Core.ApiIndex;
using WotModCockpit.Core.Recipes;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты каталога рецептов: состав, поиск, подстановка паспорта проекта
/// и сверка с индексом API клиента 1.44.
/// </summary>
public class RecipeServiceTests
{
    private readonly RecipeService _service = new();

    // ------------------------------------------------------------------ состав каталога

    /// <summary>Каталог покрывает обязательный минимум тем.</summary>
    [Fact]
    public void Library_HasAllRecipes()
    {
        Assert.Equal(16, RecipeLibrary.All.Count);
        Assert.Equal(16, RecipeLibrary.All.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>Код каждого рецепта читается из ресурса и не пуст.</summary>
    [Fact]
    public void EveryRecipe_HasCode()
    {
        foreach (Recipe r in RecipeLibrary.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Code), $"{r.Id}: пустой код");
            Assert.True(r.Code.Length > 500, $"{r.Id}: подозрительно короткий код ({r.Code.Length} символов)");
            Assert.DoesNotContain("\r", r.Code, StringComparison.Ordinal);
        }
    }

    /// <summary>Карточка рецепта заполнена целиком: без «где ломается» она бесполезна.</summary>
    [Fact]
    public void EveryRecipe_HasCompleteCard()
    {
        foreach (Recipe r in RecipeLibrary.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Title), $"{r.Id}: нет заголовка");
            Assert.False(string.IsNullOrWhiteSpace(r.Summary), $"{r.Id}: нет аннотации");
            Assert.False(string.IsNullOrWhiteSpace(r.Rationale), $"{r.Id}: нет обоснования");
            Assert.False(string.IsNullOrWhiteSpace(r.Pitfall), $"{r.Id}: нет раздела «где ломается»");
        }
    }

    /// <summary>Поиск по идентификатору находит ровно этот рецепт и ставит его первым.</summary>
    [Fact]
    public void Search_ById_RanksExactFirst()
    {
        IReadOnlyList<Recipe> found = _service.Search("R07");

        Assert.NotEmpty(found);
        Assert.Equal("R07", found[0].Id);
    }

    /// <summary>Пустой запрос — это «показать всё», а не «ничего не найдено».</summary>
    [Fact]
    public void Search_EmptyQuery_ReturnsEverything()
    {
        Assert.Equal(RecipeLibrary.All.Count, _service.Search(null).Count);
        Assert.Equal(RecipeLibrary.All.Count, _service.Search("   ").Count);
    }

    /// <summary>Фильтр по рубрике и сложности сужает выдачу.</summary>
    [Fact]
    public void Search_FiltersByCategoryAndDifficulty()
    {
        IReadOnlyList<Recipe> interop = _service.Search(null, RecipeCategory.Interop);
        Assert.All(interop, r => Assert.Equal(RecipeCategory.Interop, r.Category));
        Assert.NotEmpty(interop);

        IReadOnlyList<Recipe> advanced = _service.Search(null, difficulty: RecipeDifficulty.Advanced);
        Assert.All(advanced, r => Assert.Equal(RecipeDifficulty.Advanced, r.Difficulty));
        Assert.NotEmpty(advanced);
    }

    /// <summary>Поиск по смыслу находит рецепт про хоткеи.</summary>
    [Fact]
    public void Search_ByMeaning_FindsRecipe()
    {
        Assert.Contains(_service.Search("хоткей"), r => r.Id == "R08");
        Assert.Contains(_service.Search("монкипатч"), r => r.Id == "R10");
        Assert.Contains(_service.Search("callback"), r => r.Id == "R07");
    }

    // ------------------------------------------------------------------ подстановка

    /// <summary>Подстановка меняет все четыре токена и не оставляет ни одного старого.</summary>
    [Fact]
    public void Render_ReplacesEveryToken()
    {
        RecipeParameters p = RecipeParameters.From("ru.local.battle_timer", "battle_timer");

        foreach (Recipe r in RecipeLibrary.All)
        {
            string code = _service.Render(r, p);

            Assert.DoesNotContain("MyMod", code, StringComparison.Ordinal);
            Assert.DoesNotContain("my_mod", code, StringComparison.Ordinal);
            Assert.DoesNotContain("My Mod", code, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Идентификатор не склеивается из старого префикса и нового хвоста.
    /// Это и есть причина, по которой подстановка идёт от длинных токенов к коротким.
    /// </summary>
    [Fact]
    public void Render_ModId_IsNotGluedFromPrefixAndTail()
    {
        RecipeParameters p = RecipeParameters.From("org.example.timer", "timer", "Timer", "Timer");
        string code = _service.Render(RecipeLibrary.ById("R01")!, p);

        Assert.Contains("org.example.timer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ru.local.timer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ru.local.", code, StringComparison.Ordinal);
    }

    /// <summary>Тег лога попадает в LOG_PREFIX и в имя логгера — по нему фильтрует живой лог.</summary>
    [Fact]
    public void Render_LogTag_ReachesPrefixAndLogger()
    {
        RecipeParameters p = RecipeParameters.From("ru.local.battle_timer", "battle_timer");
        string code = _service.Render(RecipeLibrary.ById("R02")!, p);

        Assert.Contains("LOG_PREFIX = '[BattleTimer]'", code, StringComparison.Ordinal);
        Assert.Contains("logging.getLogger('BattleTimer')", code, StringComparison.Ordinal);
    }

    /// <summary>Каталог конфигов и маркер патча получают имя пакета.</summary>
    [Fact]
    public void Render_PackageName_ReachesConfigPathAndPatchMarker()
    {
        RecipeParameters p = RecipeParameters.From("ru.local.battle_timer", "battle_timer");

        Assert.Contains("'battle_timer'", _service.Render(RecipeLibrary.ById("R09")!, p), StringComparison.Ordinal);
        Assert.Contains("_battle_timer_patched", _service.Render(RecipeLibrary.ById("R10")!, p), StringComparison.Ordinal);
    }

    /// <summary>Без параметров код возвращается как есть — это законный режим «проект не открыт».</summary>
    [Fact]
    public void Render_WithoutParameters_ReturnsTemplateTokens()
    {
        string code = _service.Render(RecipeLibrary.ById("R01")!);
        Assert.Contains("MyMod", code, StringComparison.Ordinal);
    }

    /// <summary>Значения по умолчанию равны токенам шаблона: подстановка ими ничего не меняет.</summary>
    [Fact]
    public void Render_WithDefaultParameters_IsIdentity()
    {
        foreach (Recipe r in RecipeLibrary.All)
        {
            Assert.Equal(r.Code, _service.Render(r, RecipeParameters.Default));
        }
    }

    /// <summary>Недостающие поля паспорта выводятся из имени пакета.</summary>
    [Fact]
    public void Parameters_AreDerivedFromPackage()
    {
        RecipeParameters p = RecipeParameters.From(null, "battle_timer");

        Assert.Equal("ru.local.battle_timer", p.ModId);
        Assert.Equal("BattleTimer", p.LogTag);
        Assert.Equal("Battle Timer", p.DisplayName);
    }

    // ------------------------------------------------------------------ совместимость

    /// <summary>Без индекса совместимость неизвестна, но сервис работает и не падает.</summary>
    [Fact]
    public void Compatibility_WithoutIndex_IsUnknown()
    {
        IReadOnlyList<RecipeCompatibilityReport> reports = new RecipeService().CheckAll();

        Assert.Equal(RecipeLibrary.All.Count, reports.Count);
        Assert.All(reports, r => Assert.Equal(RecipeCompatibility.Unknown, r.Status));
    }

    /// <summary>
    /// На РЕАЛЬНОМ индексе клиента 1.44 все проверяемые символы всех рецептов найдены.
    /// Это и есть доказательство, что рецепты написаны под эту версию игры, а не «вообще».
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void Compatibility_OnRealIndex_EveryRecipeIsOk()
    {
        Skip.IfNot(RealWotSource.Available, $"нет дерева исходников клиента ({RealWotSource.Dir})");

        var service = new RecipeService(new FakeIndexService(RealWotSource.Index));
        IReadOnlyList<RecipeCompatibilityReport> reports = service.CheckAll();

        string[] broken = [.. reports
            .Where(r => r.Status != RecipeCompatibility.Ok)
            .Select(r => $"{r.Recipe.Id}: {r.Message}")];

        Assert.True(broken.Length == 0, "рецепты со ссылками на несуществующий API:\n" + string.Join('\n', broken));
    }

    /// <summary>Проверяются только реальные зависимости: нативные модули и чужие моды — нет.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void Compatibility_SkipsNativeAndExternalModules()
    {
        Skip.IfNot(RealWotSource.Available, $"нет дерева исходников клиента ({RealWotSource.Dir})");

        var service = new RecipeService(new FakeIndexService(RealWotSource.Index));

        // R07 зависит только от BigWorld — нативного модуля, которого в индексе нет и быть не может.
        RecipeCompatibilityReport periodic = service.CheckCompatibility(RecipeLibrary.ById("R07")!);
        Assert.Equal(RecipeCompatibility.Ok, periodic.Status);
        Assert.Equal(0, periodic.CheckedCount);

        // R14 зависит только от чужого мода ModsListAPI — его в клиенте нет по определению.
        RecipeCompatibilityReport modsList = service.CheckCompatibility(RecipeLibrary.ById("R14")!);
        Assert.Equal(RecipeCompatibility.Ok, modsList.Status);
        Assert.Equal(0, modsList.CheckedCount);
    }

    /// <summary>Выдуманный символ ловится: проверка действительно что-то проверяет.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void Compatibility_DetectsMissingSymbol()
    {
        Skip.IfNot(RealWotSource.Available, $"нет дерева исходников клиента ({RealWotSource.Dir})");

        var service = new RecipeService(new FakeIndexService(RealWotSource.Index));
        var fake = new Recipe(
            "RXX", "тест", RecipeCategory.Events, RecipeDifficulty.Beginner, "s", "r", "p", "r01_skeleton.py",
            [
                new RecipeRequirement("PlayerEvents", "g_playerEvents"),
                new RecipeRequirement("PlayerEvents", "onSomethingThatNeverExisted"),
                new RecipeRequirement("gui.this_module_does_not_exist", "X")
            ]);

        RecipeCompatibilityReport report = service.CheckCompatibility(fake);

        Assert.Equal(RecipeCompatibility.NeedsReview, report.Status);
        Assert.Equal(3, report.CheckedCount);
        Assert.Equal(2, report.Missing.Count);
        Assert.Contains(report.Missing, m => m.Name == "onSomethingThatNeverExisted");
        Assert.Contains(report.Missing, m => m.Module == "gui.this_module_does_not_exist");
    }

    /// <summary>Минимальная подделка сервиса индекса: тестам нужен только снимок.</summary>
    private sealed class FakeIndexService(ApiIndex index) : IApiIndexService
    {
        public ApiIndex Index { get; } = index;

        public ApiIndexBuildStatus Status => ApiIndexBuildStatus.NotBuilt;

        public IReadOnlyList<ApiIndexBuildError> Errors => [];

        public event EventHandler<ApiIndexBuildStatus>? StatusChanged { add { } remove { } }

        public Task<ApiIndexBuildStatus> RebuildAsync(string? sourceRoot = null, IProgress<ApiIndexProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public Task<ApiIndexBuildStatus> EnsureBuiltAsync(string? sourceRoot = null, IProgress<ApiIndexProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public void Cancel() { }

        public IReadOnlyList<ApiSymbol> Search(string? query, SymbolSearchOptions? options = null) => [];

        public Task<XrefResult> FindReferencesAsync(XrefQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ApiSymbol? ResolveTracebackFrame(string? virtualPath, int line) => null;

        public SourceSnippet? GetSnippet(ApiSymbol symbol, int before = 0, int after = 12) => null;

        public IReadOnlyList<ApiSymbol> Children(ApiSymbol symbol) => [];
    }
}
