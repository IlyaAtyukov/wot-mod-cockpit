using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.Core;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.ApiIndex;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Tests;

/// <summary>
/// Единожды собранный индекс РЕАЛЬНОГО дерева исходников клиента.
/// Сборка стоит 0.1-0.7 с, поэтому она делается один раз на весь прогон, а тесты,
/// которым дерево нужно, мягко пропускаются, если его нет или тесты на своей машине
/// не включены (<c>WOTCOCKPIT_REAL_MACHINE=1</c>, путь — <c>WOTCOCKPIT_CLIENT_SRC</c>).
/// </summary>
public static class RealWotSource
{
    /// <summary>Дерево исходников клиента (<see cref="ReposLayout.ClientSource"/>).</summary>
    public static readonly string Dir = ReposLayout.ClientSource;

    /// <summary>Дерево на месте и тесты на своей машине включены.</summary>
    public static bool Available => Directory.Exists(Dir);

    private static readonly Lazy<ApiIndexBuildResult> Lazy =
        new(() => new ApiIndexBuilder().Build(Dir), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Результат сборки.</summary>
    public static ApiIndexBuildResult Result => Lazy.Value;

    /// <summary>Индекс.</summary>
    public static ApiIndex Index => Lazy.Value.Index;
}

/// <summary>
/// Тесты сборки индекса: синтетическое дерево во временном каталоге плюс проверки
/// на реальном дереве (эталонные числа — по дереву клиента 1.44, 8052 файла).
/// </summary>
public class ApiIndexBuilderTests : IDisposable
{
    private readonly string _root;

    /// <summary>Создаёт временное дерево, повторяющее раскладку дампа.</summary>
    public ApiIndexBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wot-cockpit-apiindex-" + Guid.NewGuid().ToString("N"));
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

    private void Write(string relativePath, params string[] lines)
    {
        string full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Join("\r\n", lines) + "\r\n");
    }

    private static string[] Header(string virtualPath) =>
    [
        "# uncompyle6 version 3.9.3",
        "# Python bytecode version base 2.7 (62211)",
        "# Decompiled from: Python 3.12.10",
        "# Embedded file name: " + virtualPath
    ];

    private void WriteSmallTree()
    {
        Write("client/gui/SystemMessages.py",
            [.. Header("scripts/client/gui/SystemMessages.py"),
             "def pushMessage(text, type=None):",
             "    pass",
             "",
             "class SystemMessagesInterface(object):",
             "",
             "    def pushMessage(self, text):",
             "        pass"]);

        Write("client/skeletons/gui/system_messages.py",
            [.. Header("scripts/client/skeletons/gui/system_messages.py"),
             "class ISystemMessages(object):",
             "",
             "    def pushMessage(self, message, type=None):",
             "        raise NotImplementedError"]);

        Write("client_common/ClientArena.py",
            [.. Header("scripts/client_common/ClientArena.py"),
             "class ClientArena(object):",
             "",
             "    def __init__(self):",
             "        self.onPeriodChange = Event.Event(em)",
             "        self.onVehicleKilled = Event.Event(em)"]);

        Write("common/Event.py",
            [.. Header("scripts/common/Event.py"),
             "class Event(list):",
             "",
             "    def __call__(self, *args):",
             "        pass"]);

        // сгенерированные ресурсы и вендорный код — те самые слои, которые надо убирать из выдачи
        Write("client/gui/impl/gen/resources/strings.py",
            [.. Header("scripts/client/gui/impl/gen/resources/strings.py"),
             "class account_dashboard(DynAccessor):",
             "",
             "    class _bonusXp(DynAccessor):",
             "        pass"]);

        Write("common/site-packages/future-0.18.2/future/backports/x.py",
            [.. Header("scripts/common/site-packages/future-0.18.2/future/backports/x.py"),
             "class Vendored(object):",
             "    pass"]);
    }

    // ------------------------------------------------------------------ отсутствующее дерево

    /// <summary>
    /// Дерева может не быть вовсе — это штатная ситуация, а не сбой. Никаких исключений,
    /// пустой индекс и внятное сообщение.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\nope\wot-src")]
    [InlineData(@"Z:\wot-src")]
    public void Build_MissingTree_ReturnsEmptyIndexWithoutThrowing(string? path)
    {
        ApiIndexBuildResult result = new ApiIndexBuilder().Build(path);

        Assert.True(result.Index.IsEmpty);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Index.Files);
        Assert.False(result.Layout.Exists);
        Assert.NotEmpty(result.Layout.Describe());
    }

    /// <summary>Пустой каталог даёт пустой индекс, но раскладка уже «существует».</summary>
    [Fact]
    public void Build_EmptyDirectory_ReturnsEmptyIndex()
    {
        ApiIndexBuildResult result = new ApiIndexBuilder().Build(_root);

        Assert.True(result.Layout.Exists);
        Assert.False(result.Layout.LooksLikeClientSource);
        Assert.Equal(0, result.Index.SymbolCount);
    }

    // ------------------------------------------------------------------ синтетическое дерево

    /// <summary>Базовая сборка: файлы, подкорни, слои, виртуальные пути, символы-модули.</summary>
    [Fact]
    public void Build_SmallTree_ClassifiesRootsTiersAndVirtualPaths()
    {
        WriteSmallTree();
        ApiIndex index = new ApiIndexBuilder().Build(_root).Index;

        Assert.Equal(6, index.Files.Count);
        Assert.Equal(3, index.FileCountOf(SourceRoot.Client));
        Assert.Equal(1, index.FileCountOf(SourceRoot.ClientCommon));
        Assert.Equal(2, index.FileCountOf(SourceRoot.Common));

        Assert.Equal(1, index.FileCountOf(SourceTier.Generated));
        Assert.Equal(1, index.FileCountOf(SourceTier.Vendor));
        Assert.Equal(4, index.FileCountOf(SourceTier.Game));

        SourceFileEntry sm = index.Files.Single(f => f.RelativePath == "client/gui/SystemMessages.py");
        Assert.Equal("scripts/client/gui/SystemMessages.py", sm.VirtualPath);
        Assert.Equal("gui.SystemMessages", sm.DottedModule);
        Assert.Equal("SystemMessages", sm.ModuleName);

        Assert.Equal(6, index.CountOf(SymbolKind.Module));
        Assert.Equal(2, index.CountOf(SymbolKind.Event));
        Assert.Equal(3, index.DeclarationCount("pushMessage"));
    }

    /// <summary>Квалифицированное имя и владелец собираются по цепочке родителей.</summary>
    [Fact]
    public void Build_QualifiedNamesAndChildren()
    {
        WriteSmallTree();
        ApiIndex index = new ApiIndexBuilder().Build(_root).Index;

        ApiSymbol iface = index.Declarations("ISystemMessages").Single();
        Assert.Equal(SymbolKind.Class, iface.Kind);
        Assert.True(iface.IsServiceInterface);

        ApiSymbol method = index.Children(iface.SymbolId).Single();
        Assert.Equal("pushMessage", method.Name);
        Assert.Equal("ISystemMessages.pushMessage", method.QualifiedName);
        Assert.Equal("ISystemMessages", method.Owner);
        Assert.Equal("(self, message, type=None)", method.Signature);
        Assert.Equal("ISystemMessages.pushMessage(self, message, type=None)", method.Display);
    }

    /// <summary>Кадр traceback из <c>python.log</c> превращается в символ — это мост к этапу 1.</summary>
    [Fact]
    public void EnclosingSymbol_ResolvesTracebackFrame()
    {
        WriteSmallTree();
        ApiIndex index = new ApiIndexBuilder().Build(_root).Index;

        ApiSymbol? sym = index.EnclosingSymbol("scripts/client/gui/SystemMessages.py", 11);

        Assert.NotNull(sym);
        Assert.Equal("pushMessage", sym!.Name);
        Assert.Equal(SymbolKind.Method, sym.Kind);

        // тот же файл находится и по относительному пути без префикса scripts/
        Assert.True(index.FileIdByVirtualPath("client/gui/SystemMessages.py") >= 0);
        Assert.Equal(-1, index.FileIdByVirtualPath("scripts/client/no_such_file.py"));
        Assert.Null(index.EnclosingSymbol(null, 1));
    }

    /// <summary>Прогресс приходит и заканчивается на «обработано == всего».</summary>
    [Fact]
    public void Build_ReportsProgress()
    {
        WriteSmallTree();
        var reports = new List<ApiIndexProgress>();

        new ApiIndexBuilder(ApiIndexOptions.Default with { ProgressBatch = 1 })
            .Build(_root, new Progress<ApiIndexProgress>(p => { lock (reports) reports.Add(p); }));

        // Progress<T> постит в пул потоков — дождёмся, но без бесконечного ожидания
        SpinWait.SpinUntil(() => { lock (reports) return reports.Any(r => r.Processed == 6); }, TimeSpan.FromSeconds(5));

        lock (reports)
        {
            Assert.NotEmpty(reports);
            Assert.Contains(reports, r => r.Processed == 6 && r.Total == 6);
            Assert.Equal(1.0, reports.Max(r => r.Fraction), 3);
        }
    }

    /// <summary>Отмена прекращает сборку исключением <see cref="OperationCanceledException"/>.</summary>
    [Fact]
    public void Build_Cancellation_Throws()
    {
        WriteSmallTree();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => new ApiIndexBuilder().Build(_root, null, cts.Token));
    }

    /// <summary>
    /// Нечитаемый файл не роняет сборку: он попадает в список ошибок, а остальное дерево
    /// индексируется. Счётчик ошибок обязан быть виден пользователю.
    /// </summary>
    [Fact]
    public void Build_UnreadableFile_IsReportedButDoesNotFailBuild()
    {
        WriteSmallTree();
        string locked = Path.Combine(_root, "client", "locked.py");
        File.WriteAllText(locked, "def f():\r\n    pass\r\n");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ApiIndexBuildResult result = new ApiIndexBuilder().Build(_root);

            Assert.Single(result.Errors);
            Assert.Contains("locked.py", result.Errors[0].Path);
            Assert.True(result.Index.SymbolCount > 0);
            Assert.Equal(7, result.Index.Files.Count);
        }
    }

    /// <summary>Порядок файлов детерминирован: два прогона дают одинаковый индекс.</summary>
    [Fact]
    public void Build_IsDeterministic()
    {
        WriteSmallTree();
        var builder = new ApiIndexBuilder();

        ApiIndex a = builder.Build(_root).Index;
        ApiIndex b = builder.Build(_root).Index;

        Assert.Equal(
            a.Files.Select(f => f.RelativePath),
            b.Files.Select(f => f.RelativePath));
        Assert.Equal(
            Enumerable.Range(0, a.SymbolCount).Select(i => a.QualifiedName(i)),
            Enumerable.Range(0, b.SymbolCount).Select(i => b.QualifiedName(i)));
    }

    // ------------------------------------------------------------------ сервис

    /// <summary>Сервис резолвится из контейнера и живёт без дерева исходников.</summary>
    [Fact]
    public void Service_ResolvesFromContainer()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddCockpitCore()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var service = provider.GetRequiredService<IApiIndexService>();

        Assert.NotNull(service);
        Assert.Same(ApiIndex.Empty, service.Index);
        Assert.Equal(ApiIndexBuildState.NotConfigured, service.Status.State);
        Assert.True(service.Status.NeedsSourceTree);
        Assert.Empty(service.Search("pushMessage"));
    }

    /// <summary>Без пути — <see cref="ApiIndexBuildState.NotConfigured"/>; с несуществующим путём — <see cref="ApiIndexBuildState.RootMissing"/>.</summary>
    [Fact]
    public async Task Service_EmptyStates_AreDistinguished()
    {
        using var service = new ApiIndexService(new FakeSettings(string.Empty), new SourceFileService());

        ApiIndexBuildStatus notConfigured = await service.RebuildAsync();
        Assert.Equal(ApiIndexBuildState.NotConfigured, notConfigured.State);
        Assert.Contains("не указано", notConfigured.Message);

        ApiIndexBuildStatus missing = await service.RebuildAsync(@"C:\nope\wot-src");
        Assert.Equal(ApiIndexBuildState.RootMissing, missing.State);
        Assert.True(missing.NeedsSourceTree);
        Assert.True(service.Index.IsEmpty);
    }

    /// <summary>
    /// Промах папкой в диалоге «выбрать папку…» — САМАЯ частая ошибка первого запуска, и она
    /// обязана быть отличима от успеха. Раньше существующий каталог без <c>client\</c> и
    /// <c>common\</c> доходил до <see cref="ApiIndexBuildState.Ready"/> с нулём символов: шапка
    /// писала «0 символов в 0 файлах за 1 мс», пустое состояние приглашало вводить имя символа,
    /// а поиск молча ничего не находил — ни одной подсказки, что выбрана не та папка.
    /// </summary>
    [Fact]
    public async Task Service_ExistingButNotADump_IsNotReported_AsReady()
    {
        // каталог есть, .py внутри тоже есть, но подкорней client\ / common\ нет
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "readme.txt"), "не дамп");
        File.WriteAllText(Path.Combine(_root, "setup.py"), "def main(): pass\r\n");

        using var service = new ApiIndexService(new FakeSettings(_root), new SourceFileService());

        ApiIndexBuildStatus status = await service.RebuildAsync();

        Assert.Equal(ApiIndexBuildState.RootMissing, status.State);
        Assert.True(status.NeedsSourceTree);
        Assert.Equal(0, status.SymbolCount);
        Assert.True(service.Index.IsEmpty);

        // сообщение обязано называть и путь, и то, чего в нём не хватает
        Assert.Contains(_root, status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("client", status.Message, StringComparison.Ordinal);
        Assert.Contains("common", status.Message, StringComparison.Ordinal);

        // а настоящее дерево из того же каталога после этого собирается как обычно
        WriteSmallTree();
        Assert.Equal(ApiIndexBuildState.Ready, (await service.RebuildAsync()).State);
        Assert.NotEmpty(service.Search("pushMessage"));
    }

    /// <summary>Наличия одного <c>common\</c> достаточно: дампы бывают и без <c>client\</c>.</summary>
    [Fact]
    public async Task Service_CommonOnlyTree_IsAccepted()
    {
        Write("common/Event.py",
            [.. Header("scripts/common/Event.py"),
             "class Event(list):",
             "",
             "    def __call__(self, *args):",
             "        pass"]);

        using var service = new ApiIndexService(new FakeSettings(_root), new SourceFileService());

        Assert.Equal(ApiIndexBuildState.Ready, (await service.RebuildAsync()).State);
        Assert.NotEmpty(service.Search("Event"));
    }

    /// <summary>Сборка по синтетическому дереву доводит сервис до готового состояния.</summary>
    [Fact]
    public async Task Service_Rebuild_ReachesReady()
    {
        WriteSmallTree();
        using var service = new ApiIndexService(new FakeSettings(_root), new SourceFileService());
        var statuses = new List<ApiIndexBuildState>();
        service.StatusChanged += (_, s) => { lock (statuses) statuses.Add(s.State); };

        ApiIndexBuildStatus status = await service.EnsureBuiltAsync();

        Assert.Equal(ApiIndexBuildState.Ready, status.State);
        Assert.Equal(6, status.FileCount);
        Assert.Equal(0, status.ErrorCount);
        lock (statuses) Assert.Contains(ApiIndexBuildState.Building, statuses);

        Assert.NotEmpty(service.Search("pushMessage"));
        Assert.NotNull(service.ResolveTracebackFrame("scripts/client/gui/SystemMessages.py", 11));

        ApiSymbol symbol = service.Search("ISystemMessages")[0];
        Assert.NotNull(service.GetSnippet(symbol, after: 2));
        Assert.NotEmpty(service.Children(symbol));

        // повторный EnsureBuiltAsync ничего не пересобирает
        ApiIndex before = service.Index;
        Assert.Same(before, service.Index);
        Assert.Equal(ApiIndexBuildState.Ready, (await service.EnsureBuiltAsync()).State);
        Assert.Same(before, service.Index);
    }

    /// <summary>Отменённая сборка возвращает статус, а не бросает исключение наружу.</summary>
    [Fact]
    public async Task Service_Cancellation_ReturnsCancelledStatus()
    {
        WriteSmallTree();
        using var service = new ApiIndexService(new FakeSettings(_root), new SourceFileService());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        ApiIndexBuildStatus status = await service.RebuildAsync(_root, null, cts.Token);

        Assert.Equal(ApiIndexBuildState.Cancelled, status.State);
    }

    private sealed class FakeSettings(string wotSrcDir) : ISettingsStore
    {
        public string SettingsPath => "<fake>";
        public CockpitSettings Load() => new() { WotSrcDir = wotSrcDir };
        public void Save(CockpitSettings settings) { }
    }

    // ------------------------------------------------------------------ реальное дерево

    /// <summary>
    /// Эталонные числа корпуса клиента 1.44, замеренные независимо (python-скриптами и прототипом
    /// на C#): 8052 файла, 117 901 <c>def</c> и 38 809 <c>class</c>. Любое расхождение здесь
    /// означает, что сканер начал молча терять символы. Дерево другой версии даёт другие числа
    /// законно — на нём тест пропускается.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_MatchesReferenceCounts()
    {
        TestEnvironment.RequireReferenceClientSource("1.44");

        ApiIndex index = RealWotSource.Index;

        Assert.Equal(8052, index.Files.Count);
        Assert.Equal(6396, index.FileCountOf(SourceRoot.Client));
        Assert.Equal(56, index.FileCountOf(SourceRoot.ClientCommon));
        Assert.Equal(1599, index.FileCountOf(SourceRoot.Common));

        int defs = index.CountOf(SymbolKind.Function, SymbolOrigin.Keyword)
                 + index.CountOf(SymbolKind.Method, SymbolOrigin.Keyword)
                 + index.CountOf(SymbolKind.NestedFunction, SymbolOrigin.Keyword);

        Assert.Equal(117_901, defs);
        Assert.Equal(38_809, index.CountOf(SymbolKind.Class, SymbolOrigin.Keyword));
        Assert.Equal(8052, index.CountOf(SymbolKind.Module));
    }

    /// <summary>Слои корпуса клиента 1.44: игровой код, сгенерированные ресурсы, вендорный код.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_TierClassification()
    {
        TestEnvironment.RequireReferenceClientSource("1.44");

        ApiIndex index = RealWotSource.Index;

        Assert.Equal(1311, index.FileCountOf(SourceTier.Generated));
        Assert.Equal(1167, index.FileCountOf(SourceTier.Vendor));
        Assert.Equal(5574, index.FileCountOf(SourceTier.Game));
    }

    /// <summary>Шапка uncompyle6 есть у всех файлов дерева, и виртуальный путь начинается с <c>scripts/</c>.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_EveryFileHasVirtualPath()
    {
        TestEnvironment.RequireClientSource();

        ApiIndex index = RealWotSource.Index;

        Assert.DoesNotContain(index.Files, f => !f.VirtualPath.StartsWith("scripts/", StringComparison.Ordinal));
    }

    /// <summary>Ни один файл не должен пропадать из-за ошибки — список ошибок пуст.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_BuildsWithoutErrors()
    {
        TestEnvironment.RequireClientSource();

        Assert.Empty(RealWotSource.Result.Errors);
    }

    /// <summary>
    /// Десять файлов, декомпилированных с ошибками, обязаны разбираться, а не ронять индекс.
    /// Список снят с эталонного дерева клиента 1.44: в другом дереве ломаются другие файлы.
    /// </summary>
    [SkippableTheory]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    [InlineData("client/gui/impl/lobby/customization/customization_seasons_data_packer.py")]
    [InlineData("client/gui/server_events/events_helpers.py")]
    [InlineData("client/gui/shared/utils/requesters/ItemsRequester.py")]
    [InlineData("client/math_utils.py")]
    [InlineData("client/messenger/gui/Scaleform/view/lobby/antispam_message.py")]
    [InlineData("common/cgf_demo/test_movers.py")]
    [InlineData("common/live_crc.py")]
    [InlineData("common/serializable_types/customizations/customization_outfit.py")]
    [InlineData("common/serialization/component_xml_deserializer.py")]
    [InlineData("common/UnitRoster.py")]
    public void RealTree_BrokenDecompiledFiles_AreParsed(string relativePath)
    {
        TestEnvironment.RequireReferenceClientSource("1.44");

        ApiIndex index = RealWotSource.Index;
        SourceFileEntry file = index.Files.Single(f => f.RelativePath == relativePath);
        int fileId = index.Files.ToList().IndexOf(file);

        IReadOnlyList<ApiSymbol> symbols = index.SymbolsOfFile(fileId);

        Assert.NotEmpty(symbols);
        Assert.StartsWith("scripts/", file.VirtualPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ключевая проверка доменной ценности: события-атрибуты, которых нет в индексе def/class.
    /// У <c>onArenaPeriodChange</c> ноль объявлений как def/class — без отдельного вида символа
    /// это событие не находится вообще.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_EventsAreIndexedSeparately()
    {
        TestEnvironment.RequireClientSource();

        ApiIndex index = RealWotSource.Index;

        Assert.True(index.CountOf(SymbolKind.Event) > 600,
            $"событий найдено {index.CountOf(SymbolKind.Event)}, ожидалось больше 600");

        IReadOnlyList<ApiSymbol> arena = index.Declarations("onArenaPeriodChange");
        Assert.NotEmpty(arena);
        Assert.All(arena, s => Assert.Equal(SymbolKind.Event, s.Kind));

        ApiSymbol become = index.Declarations("onAccountBecomePlayer")
            .Single(s => s.RelativePath == "client/PlayerEvents.py");
        Assert.Equal(SymbolKind.Event, become.Kind);
        Assert.Equal("_PlayerEvents", become.Owner);
    }

    /// <summary>Реальный кадр traceback разрешается в метод клиента.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_TracebackFrameResolvesToSymbol()
    {
        TestEnvironment.RequireClientSource();

        ApiIndex index = RealWotSource.Index;
        int fileId = index.FileIdByVirtualPath("scripts/client/PlayerEvents.py");

        Assert.True(fileId >= 0);

        ApiSymbol? symbol = index.EnclosingSymbol("scripts/client/PlayerEvents.py", 60);
        Assert.NotNull(symbol);
        Assert.Equal("client/PlayerEvents.py", symbol!.RelativePath);
    }
}
