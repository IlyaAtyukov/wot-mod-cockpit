using WotModCockpit.Core.ApiIndex;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты ранжирования поиска символов и поиска ссылок.
/// <para>
/// Проверяется не «что-то нашлось», а ПОРЯДОК выдачи: схема «класс совпадения — доминирующий
/// разряд, бонусы только внутри класса» имеет смысл ровно тогда, когда точное совпадение
/// невозможно перебить суммой бонусов, а сгенерированные ресурсы и stdlib не всплывают наверх.
/// </para>
/// </summary>
public class SymbolSearchTests : IDisposable
{
    private readonly string _root;

    /// <summary>Создаёт временный каталог для тестов xref (им нужны настоящие файлы).</summary>
    public SymbolSearchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wot-cockpit-xref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>Убирает временный каталог.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // временный каталог — не повод валить тест
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ синтетический индекс

    private static ApiIndex Index(params (string Name, SymbolKind Kind, string RelPath)[] symbols)
    {
        var files = new List<SourceFileEntry>();
        var entries = new List<SymbolEntry>();

        foreach ((string name, SymbolKind kind, string rel) in symbols)
        {
            int fileId = files.FindIndex(f => f.RelativePath == rel);
            if (fileId < 0)
            {
                files.Add(new SourceFileEntry
                {
                    RelativePath = rel,
                    VirtualPath = "scripts/" + rel,
                    Length = 100,
                    Root = SourceTreeLayout.ClassifyRoot(rel),
                    Tier = SourceTreeLayout.ClassifyTier(rel)
                });
                fileId = files.Count - 1;
            }

            entries.Add(new SymbolEntry
            {
                FileId = fileId,
                Line = entries.Count + 1,
                Parent = -1,
                Name = name,
                Kind = kind,
                Origin = SymbolOrigin.Keyword,
                Indent = 0
            });
        }

        return new ApiIndex(@"C:\fake-root", [.. files], [.. entries]);
    }

    private static string[] Names(IReadOnlyList<ApiSymbol> hits) => [.. hits.Select(h => h.Name)];

    // ------------------------------------------------------------------ классы совпадения

    /// <summary>
    /// Порядок классов: точное → префикс → акроним → подстрока → нечёткое.
    /// Разрыв в 1000 между разрядами гарантирует, что никакая сумма бонусов
    /// не поднимет подстроку выше префикса.
    /// </summary>
    [Fact]
    public void MatchTiers_AreOrderedExactPrefixAcronymSubstringFuzzy()
    {
        ApiIndex index = Index(
            ("pushMessage", SymbolKind.Function, "client/a.py"),
            ("pushMessageToChat", SymbolKind.Function, "client/a.py"),
            ("doPushMessageNow", SymbolKind.Function, "client/a.py"),
            ("pMe", SymbolKind.Function, "client/a.py"));

        IReadOnlyList<ApiSymbol> hits = SymbolSearch.Search(index, "pushMessage");

        Assert.Equal("pushMessage", hits[0].Name);
        Assert.Equal("pushMessageToChat", hits[1].Name);
        Assert.Equal("doPushMessageNow", hits[2].Name);
        Assert.True(hits[0].Score >= SymbolSearch.TierExact);
        Assert.InRange(hits[1].Score, SymbolSearch.TierPrefix, SymbolSearch.TierExactIgnoreCase);
        Assert.InRange(hits[2].Score, SymbolSearch.TierSubstring, SymbolSearch.TierAcronym);
    }

    /// <summary>Точное совпадение с учётом регистра выше, чем без учёта.</summary>
    [Fact]
    public void ExactCaseSensitive_BeatsCaseInsensitive()
    {
        ApiIndex index = Index(
            ("PushMessage", SymbolKind.Function, "client/a.py"),
            ("pushMessage", SymbolKind.Function, "client/a.py"));

        Assert.Equal("pushMessage", SymbolSearch.Search(index, "pushMessage")[0].Name);
        Assert.Equal("PushMessage", SymbolSearch.Search(index, "PushMessage")[0].Name);
    }

    /// <summary>Акроним по «горбам» CamelCase: <c>gvi</c> находит <c>getVehicleInfo</c>.</summary>
    [Theory]
    [InlineData("getVehicleInfo", "gvi", true)]
    [InlineData("pushMessage", "pushmsg", false)]
    [InlineData("get_vehicle_info", "gvi", true)]
    [InlineData("getVehicleInfo", "gxi", false)]
    [InlineData("getVehicleInfo", "g", false)]
    public void AcronymMatch_UsesCamelHumpsAndUnderscores(string name, string query, bool expected)
        => Assert.Equal(expected, SymbolSearch.AcronymMatch(name, query));

    /// <summary>
    /// Акроним <c>pushmsg</c> обязан находить <c>pushMessage</c> — но не по «горбам»,
    /// а нечётким поиском, и это реальный проверенный сценарий.
    /// </summary>
    [Fact]
    public void FuzzySubsequence_FindsPushMessageByPushmsg()
    {
        ApiIndex index = Index(
            ("pushMessage", SymbolKind.Method, "client/skeletons/gui/system_messages.py"),
            ("unrelatedName", SymbolKind.Method, "client/a.py"));

        IReadOnlyList<ApiSymbol> hits = SymbolSearch.Search(index, "pushmsg");

        Assert.Equal("pushMessage", hits[0].Name);
    }

    /// <summary>Короткий запрос не должен вываливать всю подстрочную выдачу.</summary>
    [Fact]
    public void ShortQuery_DoesNotTriggerSubstringOrFuzzy()
    {
        ApiIndex index = Index(
            ("alpha", SymbolKind.Function, "client/a.py"),
            ("beta", SymbolKind.Function, "client/a.py"));

        Assert.Equal(["alpha"], Names(SymbolSearch.Search(index, "a")));
    }

    // ------------------------------------------------------------------ бонусы

    /// <summary>
    /// Интерфейс сервиса из <c>skeletons/</c> обязан обгонять одноимённый метод из
    /// обычного файла: это самая частая цель поиска у мододела.
    /// </summary>
    [Fact]
    public void SkeletonsBonus_PutsServiceInterfaceFirst()
    {
        ApiIndex index = Index(
            ("pushMessage", SymbolKind.Method, "client/gui/SystemMessages.py"),
            ("pushMessage", SymbolKind.Method, "client/skeletons/gui/system_messages.py"));

        ApiSymbol first = SymbolSearch.Search(index, "pushMessage")[0];

        Assert.Equal("client/skeletons/gui/system_messages.py", first.RelativePath);
    }

    /// <summary>Сгенерированные ресурсы и stdlib по умолчанию не ищутся вовсе.</summary>
    [Fact]
    public void GeneratedAndVendor_AreExcludedByDefault()
    {
        ApiIndex index = Index(
            ("target", SymbolKind.Class, "client/gui/impl/gen/resources/strings.py"),
            ("target", SymbolKind.Class, "common/site-packages/future-0.18.2/x.py"),
            ("target", SymbolKind.Class, "client/a.py"));

        Assert.Single(SymbolSearch.Search(index, "target"));
        Assert.Equal(2, SymbolSearch.Search(index, "target", new SymbolSearchOptions { IncludeGenerated = true }).Count);
        Assert.Equal(3, SymbolSearch.Search(index, "target",
            new SymbolSearchOptions { IncludeGenerated = true, IncludeVendor = true }).Count);
    }

    /// <summary>Включённые слои всё равно ранжируются ниже игрового кода.</summary>
    [Fact]
    public void GameTier_RanksAboveGeneratedAndVendor()
    {
        ApiIndex index = Index(
            ("target", SymbolKind.Class, "client/gui/impl/gen/resources/strings.py"),
            ("target", SymbolKind.Class, "common/site-packages/future-0.18.2/x.py"),
            ("target", SymbolKind.Class, "client/a.py"));

        IReadOnlyList<ApiSymbol> hits = SymbolSearch.Search(index, "target",
            new SymbolSearchOptions { IncludeGenerated = true, IncludeVendor = true });

        Assert.Equal(SourceTier.Game, hits[0].Tier);
        Assert.Equal(SourceTier.Generated, hits[1].Tier);
        Assert.Equal(SourceTier.Vendor, hits[2].Tier);
    }

    /// <summary>Приватные имена уходят вниз: <c>__x</c> ниже <c>_x</c> ниже <c>x</c>.</summary>
    [Fact]
    public void PrivateNames_RankLower()
    {
        ApiIndex index = Index(
            ("__onAvatarReady", SymbolKind.Method, "client/a.py"),
            ("_onAvatarReady", SymbolKind.Method, "client/a.py"),
            ("onAvatarReady", SymbolKind.Method, "client/a.py"));

        Assert.Equal(["onAvatarReady", "_onAvatarReady", "__onAvatarReady"],
            Names(SymbolSearch.Search(index, "onAvatarReady")));
    }

    /// <summary>
    /// Штраф за омонимию гасит имена, объявленные в сотнях мест (<c>__init__</c> — 9204,
    /// <c>update</c> — 279): такое имя само по себе бесполезно как результат поиска.
    /// </summary>
    [Fact]
    public void HomonymPenalty_LowersMassivelyDeclaredNames()
    {
        var entry = new SymbolEntry { Name = "update", Kind = SymbolKind.Method, Line = 1 };
        var file = new SourceFileEntry
        {
            RelativePath = "client/a.py",
            VirtualPath = "scripts/client/a.py",
            Length = 1,
            Root = SourceRoot.Client,
            Tier = SourceTier.Game
        };

        int unique = SymbolSearch.Bonus(entry, file, declarationCount: 1);
        int few = SymbolSearch.Bonus(entry, file, declarationCount: 3);
        int homonym = SymbolSearch.Bonus(entry, file, declarationCount: 279);

        Assert.Equal(4, unique - few);          // штраф линеен: 2 балла за каждое объявление
        Assert.Equal(298, unique - homonym);    // и упирается в потолок 300 (у update 279*2 > 300)
        Assert.Equal(homonym, SymbolSearch.Bonus(entry, file, declarationCount: 9204));
    }

    /// <summary>Степень омонимии видна в результате: у имени из 200 мест это 200.</summary>
    [Fact]
    public void DeclarationCount_IsReportedOnEveryHit()
    {
        var symbols = new List<(string, SymbolKind, string)>();
        for (int i = 0; i < 200; i++) symbols.Add(("update", SymbolKind.Method, $"client/f{i}.py"));
        symbols.Add(("updater", SymbolKind.Method, "client/z.py"));

        ApiIndex index = Index([.. symbols]);

        Assert.Equal(200, SymbolSearch.Search(index, "update")[0].DeclarationCount);
        Assert.Equal(1, SymbolSearch.Search(index, "updater")[0].DeclarationCount);
    }

    /// <summary>Вид символа влияет на балл: класс выше метода при прочих равных.</summary>
    [Fact]
    public void ClassKind_RanksAboveMethod()
    {
        ApiIndex index = Index(
            ("IItemsCache", SymbolKind.Method, "client/a.py"),
            ("IItemsCache", SymbolKind.Class, "client/b.py"));

        Assert.Equal(SymbolKind.Class, SymbolSearch.Search(index, "IItemsCache")[0].Kind);
    }

    // ------------------------------------------------------------------ поведение выдачи

    /// <summary>
    /// Порядок обязан быть устойчивым: при точном совпадении десятки символов получают
    /// одинаковый балл, и без дополнительного ключа выдача прыгала бы между запусками.
    /// </summary>
    [Fact]
    public void Ordering_IsStableAcrossRuns()
    {
        var symbols = new List<(string, SymbolKind, string)>();
        for (int i = 0; i < 50; i++) symbols.Add(("getVehicle", SymbolKind.Method, $"client/f{i:D2}.py"));

        ApiIndex index = Index([.. symbols]);

        string[] a = [.. SymbolSearch.Search(index, "getVehicle").Select(h => h.RelativePath)];
        string[] b = [.. SymbolSearch.Search(index, "getVehicle").Select(h => h.RelativePath)];

        Assert.Equal(a, b);
        Assert.Equal("client/f00.py", a[0]);
    }

    /// <summary>Лимит соблюдается, и обрезка не портит верхушку выдачи.</summary>
    [Fact]
    public void Limit_IsRespected_AndTopSurvivesPruning()
    {
        var symbols = new List<(string, SymbolKind, string)> { ("onAvatarReady", SymbolKind.Method, "client/a.py") };
        for (int i = 0; i < 3000; i++) symbols.Add(($"onAvatarReadyHandler{i}", SymbolKind.Method, $"client/f{i}.py"));

        ApiIndex index = Index([.. symbols]);
        IReadOnlyList<ApiSymbol> hits = SymbolSearch.Search(index, "onAvatarReady", new SymbolSearchOptions { Limit = 5 });

        Assert.Equal(5, hits.Count);
        Assert.Equal("onAvatarReady", hits[0].Name);
    }

    /// <summary>Фильтры по виду символа и по пути.</summary>
    [Fact]
    public void Filters_KindAndPath()
    {
        ApiIndex index = Index(
            ("target", SymbolKind.Class, "client/skeletons/gui/x.py"),
            ("target", SymbolKind.Method, "client/gui/y.py"),
            ("target", SymbolKind.Event, "client/gui/z.py"));

        Assert.Single(SymbolSearch.Search(index, "target", new SymbolSearchOptions { Kinds = [SymbolKind.Event] }));
        Assert.Single(SymbolSearch.Search(index, "target", new SymbolSearchOptions { PathContains = "skeletons/" }));
        Assert.Equal(2, SymbolSearch.Search(index, "target",
            new SymbolSearchOptions { Kinds = [SymbolKind.Class, SymbolKind.Method] }).Count);
    }

    /// <summary>Пустой запрос и пустой индекс — пустая выдача, без исключений.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyQuery_ReturnsNothing(string? query)
    {
        Assert.Empty(SymbolSearch.Search(Index(("a", SymbolKind.Function, "client/a.py")), query));
        Assert.Empty(SymbolSearch.Search(ApiIndex.Empty, "anything"));
    }

    // ------------------------------------------------------------------ xref

    private void WriteFile(string relativePath, params string[] lines)
    {
        string full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Join("\r\n",
            new[]
            {
                "# uncompyle6 version 3.9.3",
                "# Python bytecode version base 2.7 (62211)",
                "# Decompiled from: Python 3.12.10",
                "# Embedded file name: scripts/" + relativePath
            }.Concat(lines)) + "\r\n");
    }

    private ApiIndex BuildXrefTree()
    {
        WriteFile("client/PlayerEvents.py",
            "class _PlayerEvents(object):",
            "",
            "    def __init__(self):",
            "        self.onAvatarReady = Event.Event()");

        WriteFile("client/AutoShootGunController.py",
            "from PlayerEvents import g_playerEvents",
            "",
            "class AutoShootGunController(object):",
            "",
            "    def start(self):",
            "        g_playerEvents.onAvatarReady += self.__onAvatarReady",
            "",
            "    def stop(self):",
            "        g_playerEvents.onAvatarReady -= self.__onAvatarReady",
            "",
            "    def __onAvatarReady(self):",
            "        LOG_DEBUG('onAvatarReady fired')",
            "        self.arena.onAvatarReady(self.vehicleID)",
            "        x = self.onAvatarReadyFlag",
            "        y = myonAvatarReady");

        WriteFile("client/gui/impl/gen/resources/strings.py", "onAvatarReady = 1");
        WriteFile("common/site-packages/future-0.18.2/x.py", "onAvatarReady = 2");

        return new ApiIndexBuilder().Build(_root).Index;
    }

    /// <summary>
    /// Границы идентификатора — самый мощный одиночный фильтр xref
    /// (<c>getVehicle</c> 2587 → 143). <c>onAvatarReadyFlag</c> и <c>myonAvatarReady</c>
    /// попаданиями не считаются.
    /// </summary>
    [Fact]
    public void Xref_IdentifierBoundaries_RejectPartialMatches()
    {
        ApiIndex index = BuildXrefTree();

        XrefResult result = XrefSearch.Find(index, new XrefQuery { Name = "onAvatarReady" });

        Assert.DoesNotContain(result.Hits, h => h.Text.Contains("onAvatarReadyFlag", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Hits, h => h.Text.Contains("myonAvatarReady", StringComparison.Ordinal));
        Assert.All(result.Hits, h => Assert.NotEqual(SourceTier.Generated, h.Tier));
        Assert.All(result.Hits, h => Assert.NotEqual(SourceTier.Vendor, h.Tier));
    }

    /// <summary>Категории попаданий: подписка, отписка, вызов, объявление, литерал, чтение атрибута.</summary>
    [Fact]
    public void Xref_ClassifiesHits()
    {
        ApiIndex index = BuildXrefTree();

        XrefResult result = XrefSearch.Find(index, new XrefQuery { Name = "onAvatarReady" });

        Assert.Equal(1, result.ByCategory[XrefCategory.Subscribe]);
        Assert.Equal(1, result.ByCategory[XrefCategory.Unsubscribe]);
        Assert.True(result.ByCategory[XrefCategory.Call] >= 1);
        Assert.Equal(1, result.ByCategory[XrefCategory.StringLiteral]);
        Assert.Equal(2, result.FileCount);
    }

    /// <summary>Классификация одной строки — чистая функция, её стоит проверить отдельно.</summary>
    [Theory]
    [InlineData("        g_playerEvents.onAvatarReady += self.__x", 23, XrefCategory.Subscribe)]
    [InlineData("        g_playerEvents.onAvatarReady -= self.__x", 23, XrefCategory.Unsubscribe)]
    [InlineData("        self.arena.onAvatarReady(vehID)", 19, XrefCategory.Call)]
    [InlineData("    def onAvatarReady(self):", 8, XrefCategory.Declaration)]
    [InlineData("from x import onAvatarReady", 14, XrefCategory.Import)]
    [InlineData("        LOG_DEBUG('onAvatarReady fired')", 19, XrefCategory.StringLiteral)]
    [InlineData("        # onAvatarReady", 10, XrefCategory.Comment)]
    [InlineData("        x = self.onAvatarReady", 17, XrefCategory.AttributeRead)]
    [InlineData("        onAvatarReady = 1", 8, XrefCategory.Other)]
    public void Xref_Classify_SingleLine(string line, int column, XrefCategory expected)
    {
        Assert.Equal(expected, XrefSearch.Classify(line, column, "onAvatarReady".Length));
    }

    /// <summary>
    /// Уточнение приёмником — лекарство от омонимии: <c>g_playerEvents.onAvatarReady</c>
    /// оставляет только подписку и отписку.
    /// </summary>
    [Fact]
    public void Xref_ReceiverNarrowsResults()
    {
        ApiIndex index = BuildXrefTree();

        XrefResult all = XrefSearch.Find(index, new XrefQuery { Name = "onAvatarReady" });
        XrefResult scoped = XrefSearch.Find(index, new XrefQuery { Name = "onAvatarReady", Receiver = "g_playerEvents" });

        Assert.Equal("g_playerEvents.onAvatarReady", scoped.Needle);
        Assert.Equal(2, scoped.TotalHits);
        Assert.True(scoped.TotalHits < all.TotalHits);
        Assert.Contains(all.TopReceivers, r => r.Receiver == "g_playerEvents");
    }

    /// <summary>Слои включаются флагами, как и в поиске символов.</summary>
    [Fact]
    public void Xref_TiersCanBeIncluded()
    {
        ApiIndex index = BuildXrefTree();

        XrefResult withAll = XrefSearch.Find(index, new XrefQuery
        {
            Name = "onAvatarReady",
            IncludeGenerated = true,
            IncludeVendor = true
        });

        Assert.Equal(4, withAll.FileCount);
    }

    /// <summary>Лимит обрезает список, но полное число попаданий остаётся честным.</summary>
    [Fact]
    public void Xref_LimitTruncatesButTotalStaysHonest()
    {
        ApiIndex index = BuildXrefTree();

        XrefResult result = XrefSearch.Find(index, new XrefQuery { Name = "onAvatarReady", Limit = 2 });

        Assert.Equal(2, result.Hits.Count);
        Assert.True(result.TotalHits > 2);
        Assert.True(result.Truncated);
    }

    /// <summary>Фильтр по категориям.</summary>
    [Fact]
    public void Xref_CategoryFilter()
    {
        ApiIndex index = BuildXrefTree();

        XrefResult subs = XrefSearch.Find(index, new XrefQuery
        {
            Name = "onAvatarReady",
            Categories = [XrefCategory.Subscribe, XrefCategory.Unsubscribe]
        });

        Assert.Equal(2, subs.TotalHits);
        Assert.All(subs.Hits, h => Assert.True(h.Category is XrefCategory.Subscribe or XrefCategory.Unsubscribe));
    }

    /// <summary>Пустое имя и пустой индекс — пустой результат.</summary>
    [Fact]
    public void Xref_EmptyInputs()
    {
        Assert.Equal(0, XrefSearch.Find(ApiIndex.Empty, new XrefQuery { Name = "x" }).TotalHits);
        Assert.Equal(0, XrefSearch.Find(BuildXrefTree(), new XrefQuery { Name = "   " }).TotalHits);
    }

    /// <summary>Приёмник читается слева от точки — это готовый кандидат для уточнения запроса.</summary>
    [Theory]
    [InlineData("        g_playerEvents.onAvatarReady += x", 23, "g_playerEvents")]
    [InlineData("        self.arena.onAvatarReady()", 19, "arena")]
    [InlineData("onAvatarReady = 1", 0, null)]
    public void Xref_ReadReceiver(string line, int column, string? expected)
        => Assert.Equal(expected, XrefSearch.ReadReceiver(line, column));

    // ------------------------------------------------------------------ реальное дерево

    /// <summary>
    /// Регрессия качества выдачи на РЕАЛЬНЫХ запросах. Эти четыре запроса были проверены
    /// вручную по исходникам клиента, и если ранжирование поедет, тест это поймает.
    /// </summary>
    [SkippableTheory]
    [Trait("Env", "RealMachine")]
    [InlineData("pushMessage", "ISystemMessages.pushMessage", "client/skeletons/gui/system_messages.py")]
    [InlineData("pushmsg", "ISystemMessages.pushMessage", "client/skeletons/gui/system_messages.py")]
    [InlineData("IItemsCache", "IItemsCache", "client/skeletons/gui/shared/__init__.py")]
    [InlineData("arenaperiod", "ISharedControllersLocator.arenaPeriod", "client/skeletons/gui/battle_session.py")]
    public void RealTree_TopHit_MatchesManuallyVerifiedAnswer(string query, string expectedQualified, string expectedPath)
    {
        Skip.IfNot(RealWotSource.Available, $"нет дерева исходников клиента ({RealWotSource.Dir})");

        IReadOnlyList<ApiSymbol> hits = SymbolSearch.Search(RealWotSource.Index, query);

        Assert.NotEmpty(hits);
        Assert.Equal(expectedQualified, hits[0].QualifiedName);
        Assert.Equal(expectedPath, hits[0].RelativePath);
    }

    /// <summary>
    /// Событие <c>onArenaPeriodChange</c> ищется по имени только потому, что события
    /// индексируются отдельным видом символа: как <c>def</c>/<c>class</c> оно не объявлено нигде.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void RealTree_EventIsFoundByName()
    {
        Skip.IfNot(RealWotSource.Available, $"нет дерева исходников клиента ({RealWotSource.Dir})");

        IReadOnlyList<ApiSymbol> hits = SymbolSearch.Search(RealWotSource.Index, "onArenaPeriodChange");

        Assert.NotEmpty(hits);
        Assert.Equal(SymbolKind.Event, hits[0].Kind);
    }

    /// <summary>
    /// Подписки на события <c>g_playerEvents</c>: загрузчик отдаёт моду 6 событий,
    /// а в клиенте их подписано на порядок больше — именно это и есть ответ на вопрос
    /// «что мне перехватить».
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void RealTree_PlayerEventsSubscriptions_AreFound()
    {
        Skip.IfNot(RealWotSource.Available, $"нет дерева исходников клиента ({RealWotSource.Dir})");

        XrefResult result = XrefSearch.Find(RealWotSource.Index, new XrefQuery
        {
            Name = "onAvatarReady",
            Receiver = "g_playerEvents",
            Limit = 1000
        });

        Assert.True(result.TotalHits > 10, $"подписок найдено {result.TotalHits}");
        Assert.True(result.ByCategory.GetValueOrDefault(XrefCategory.Subscribe) > 0);
        Assert.True(result.ByCategory.GetValueOrDefault(XrefCategory.Unsubscribe) > 0);
    }

    // ------------------------------------------------------------------ запрос с точкой

    /// <summary>
    /// Запрос вида «Владелец.имя» — это ровно та форма, которой мододел называет API и которую
    /// кокпит сам показывает в колонке квалифицированного имени. Раньше она давала НОЛЬ на
    /// любом запросе: сравнение шло с коротким именем, а точки в нём не бывает никогда
    /// (вдобавок '.' уходила в 31-й бит маски, которого нет ни у одного имени, и предфильтр
    /// отсекал вообще всё).
    /// </summary>
    [Fact]
    public void DottedQuery_IsSplitIntoOwnerAndName()
    {
        ApiIndex index = OwnedIndex();

        IReadOnlyList<ApiSymbol> hits = SymbolSearch.Search(index, "ISystemMessages.pushMessage");

        Assert.NotEmpty(hits);
        Assert.Equal("ISystemMessages.pushMessage", hits[0].QualifiedName);

        // Голова обязана ОТСЕКАТЬ чужих однофамильцев, а не просто добавлять баллов.
        Assert.DoesNotContain(hits, h => h.QualifiedName == "Other.pushMessage");
    }

    /// <summary>Головой может быть и модуль: <c>gui.SystemMessages</c>, <c>helpers.dependency</c>.</summary>
    [Fact]
    public void DottedQuery_HeadMatchesModulePath()
    {
        ApiIndex index = OwnedIndex();

        Assert.Equal("pushMessage", SymbolSearch.Search(index, "SystemMessages.pushMessage")[0].Name);
        Assert.Equal("instance", SymbolSearch.Search(index, "dependency.instance")[0].Name);
        Assert.Empty(SymbolSearch.Search(index, "somethingElse.pushMessage"));
    }

    /// <summary>Разбор запроса — чистая функция, её границы стоит зафиксировать отдельно.</summary>
    [Theory]
    [InlineData("pushMessage", "", "pushMessage")]
    [InlineData("SystemMessages.pushMessage", "SystemMessages", "pushMessage")]
    [InlineData("gui.impl.lobby.Model", "gui.impl.lobby", "Model")]
    [InlineData("  gui.SystemMessages  ", "gui", "SystemMessages")]
    [InlineData("gui.", "", "gui")]
    [InlineData("", "", "")]
    public void SplitQuery_CutsAtTheLastDot(string query, string head, string tail)
    {
        (string h, string t) = SymbolSearch.SplitQuery(query);
        Assert.Equal(head, h);
        Assert.Equal(tail, t);
    }

    /// <summary>
    /// Те самые запросы, которые на настоящем дереве давали ноль. Числа рядом — сколько даёт
    /// голое имя, то есть цена дефекта.
    /// </summary>
    [SkippableTheory]
    [Trait("Env", "RealMachine")]
    [InlineData("SystemMessages.pushMessage", "pushMessage")]
    [InlineData("PlayerEvents.onAvatarReady", "onAvatarReady")]
    [InlineData("_PlayerEvents.onAvatarReady", "onAvatarReady")]
    [InlineData("dependency.instance", "instance")]
    [InlineData("gui.SystemMessages", "SystemMessages")]
    [InlineData("helpers.dependency", "dependency")]
    public void RealTree_DottedQuery_FindsWhatTheUiShows(string query, string expectedName)
    {
        Skip.IfNot(RealWotSource.Available, $"нет дерева исходников клиента ({RealWotSource.Dir})");

        IReadOnlyList<ApiSymbol> hits = SymbolSearch.Search(RealWotSource.Index, query);

        Assert.NotEmpty(hits);
        Assert.Equal(expectedName, hits[0].Name);
    }

    /// <summary>
    /// Модульные синглтоны: главная точка входа мода. <c>g_playerEvents</c> упоминается в клиенте
    /// 783 раза, но объявлен присваиванием, а не <c>def</c>/<c>class</c> — и до правила
    /// <see cref="ApiIndexOptions.IndexModuleSingletons"/> поиск по нему честно отвечал
    /// «ничего не найдено».
    /// </summary>
    [SkippableTheory]
    [Trait("Env", "RealMachine")]
    [InlineData("g_playerEvents", "PlayerEvents.g_playerEvents")]
    [InlineData("g_currentVehicle", "CurrentVehicle.g_currentVehicle")]
    [InlineData("g_clientUpdateManager", "ClientUpdateManager.g_clientUpdateManager")]
    public void RealTree_ModuleSingleton_IsFoundByName(string query, string expectedQualified)
    {
        Skip.IfNot(RealWotSource.Available, $"нет дерева исходников клиента ({RealWotSource.Dir})");

        IReadOnlyList<ApiSymbol> hits = SymbolSearch.Search(RealWotSource.Index, query);

        Assert.NotEmpty(hits);
        Assert.Equal(expectedQualified, hits[0].QualifiedName);
        Assert.Equal(SymbolKind.Service, hits[0].Kind);
    }

    /// <summary>
    /// Индекс с настоящими владельцами: базовый <see cref="Index"/> кладёт всё на уровень
    /// модуля, а запросу с точкой нужны как раз родители.
    /// </summary>
    /// <returns>Индекс из четырёх символов в трёх файлах.</returns>
    private static ApiIndex OwnedIndex()
    {
        SourceFileEntry File(string rel) => new()
        {
            RelativePath = rel,
            VirtualPath = "scripts/" + rel,
            Length = 100,
            Root = SourceTreeLayout.ClassifyRoot(rel),
            Tier = SourceTreeLayout.ClassifyTier(rel)
        };

        SourceFileEntry[] files =
        [
            File("client/skeletons/gui/system_messages.py"),
            File("client/gui/SystemMessages.py"),
            File("common/dependency_injection_container.py")
        ];

        SymbolEntry Sym(string name, SymbolKind kind, int fileId, int parent, int line) => new()
        {
            FileId = fileId,
            Line = line,
            Parent = parent,
            Name = name,
            Kind = kind,
            Origin = SymbolOrigin.Keyword,
            Indent = 0
        };

        SymbolEntry[] symbols =
        [
            Sym("ISystemMessages", SymbolKind.Class, 0, -1, 22),
            Sym("pushMessage", SymbolKind.Method, 0, 0, 23),
            Sym("Other", SymbolKind.Class, 1, -1, 10),
            Sym("pushMessage", SymbolKind.Method, 1, 2, 11),
            Sym("instance", SymbolKind.Function, 2, -1, 50)
        ];

        return new ApiIndex(@"C:\fake-root", files, symbols);
    }

    /// <summary>
    /// Омонимия должна быть видна: <c>update</c> объявлен в сотнях мест, и UI обязан сказать
    /// об этом, а не выдать две тысячи строк как ответ.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void RealTree_HomonymIsReported()
    {
        Skip.IfNot(RealWotSource.Available, $"нет дерева исходников клиента ({RealWotSource.Dir})");

        XrefResult result = XrefSearch.Find(RealWotSource.Index, new XrefQuery { Name = "update", Limit = 10 });

        Assert.True(result.IsHomonym);
        Assert.True(result.DeclarationCount > 100, $"объявлений update: {result.DeclarationCount}");
        Assert.NotEmpty(result.TopReceivers);
    }
}
