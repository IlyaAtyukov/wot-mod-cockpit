using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.Core;
using WotModCockpit.Core.ApiIndex;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Preflight;

namespace WotModCockpit.Tests;

/// <summary>
/// Прогон preflight по НАСТОЯЩИМ модам: эталонному шаблону (он написан правильно, и на нём
/// ошибок быть не должно) и по декомпилированным сторонним модам (там измеряется доля
/// ложных срабатываний самого рискованного правила — сверки с индексом клиента).
/// <para>Проверкам цепочки импорта достаточно самого шаблона мода; всё, что сверяется с деревом
/// исходников клиента или с корпусом модов, — тесты на своей машине (<c>WOTCOCKPIT_REAL_MACHINE=1</c>).</para>
/// </summary>
public class PreflightRealModTests
{
    /// <summary>Эталонный шаблон мода (<see cref="ReposLayout.Template"/>).</summary>
    public static readonly string TemplateDir = ReposLayout.Template;

    private static string TemplateSrc => Path.Combine(TemplateDir, "src");

    private static bool TemplateAvailable => Directory.Exists(TemplateSrc);

    private static ClientApiOracle RealOracle() =>
        ClientApiOracle.Create(RealWotSource.Index, new SourceFileService());

    // ------------------------------------------------------------------ эталон

    /// <summary>
    /// ГЛАВНЫЙ ТЕСТ НА ЛОЖНЫЕ СРАБАТЫВАНИЯ: эталонный шаблон написан правильно, поэтому
    /// НИ ОДНОЙ ошибки на нём быть не должно. Предупреждения допустимы, но их тоже считаем.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    [Trait("Needs", "Template")]
    public void ReferenceTemplate_ProducesNoErrors()
    {
        Skip.IfNot(TemplateAvailable, TestEnvironment.TemplateMissingReason);
        TestEnvironment.RequireClientSource();

        PreflightContext context = PreflightAnalyzer.BuildContext(TemplateSrc, TemplateDir, RealOracle());
        PreflightResult result = new PreflightAnalyzer().Analyze(context);

        string details = string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString()));
        Assert.True(result.ErrorCount == 0, "на эталонном шаблоне не должно быть ошибок:" + Environment.NewLine + details);
        Assert.Equal(0, result.WarningCount);
        Assert.True(result.FilesAnalyzed >= 3, $"файлов разобрано {result.FilesAnalyzed}");
    }

    /// <summary>Точка входа и тело мода попадают в цепочку импорта, а сам стаб — точка входа.</summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    public void ReferenceTemplate_EntryChainIsDetected()
    {
        Skip.IfNot(TemplateAvailable, TestEnvironment.TemplateMissingReason);

        PreflightContext context = PreflightAnalyzer.BuildContext(
            TemplateSrc, TemplateDir, ClientApiOracle.Create(ApiIndex.Empty, new SourceFileService()));

        ModSourceFile stub = context.Files.Single(f => f.RelativePath.EndsWith("mod_hello_tanker.py", StringComparison.Ordinal));
        ModSourceFile main = context.Files.Single(f => f.RelativePath.EndsWith("hello_tanker/main.py", StringComparison.Ordinal));

        Assert.True(stub.IsEntryPoint);
        Assert.True(stub.InEntryImportChain);
        // main.py загружается через строковую константу ENTRY, а не оператором import —
        // цепочка обязана это учитывать, иначе WOT001 там не поднимется до Error.
        Assert.False(main.IsEntryPoint);
        Assert.True(main.InEntryImportChain);
        Assert.Equal("gui.mods.hello_tanker.main", main.DottedModule);
        Assert.True(context.HasFini);
    }

    /// <summary>Анализ эталонного мода укладывается в бюджет интерактивного отклика.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    [Trait("Needs", "Template")]
    public void ReferenceTemplate_AnalysisIsFast()
    {
        Skip.IfNot(TemplateAvailable, TestEnvironment.TemplateMissingReason);
        TestEnvironment.RequireClientSource();

        ClientApiOracle oracle = RealOracle();
        var analyzer = new PreflightAnalyzer();

        // Первый прогон прогревает кеш экспортов клиентских модулей.
        analyzer.Analyze(PreflightAnalyzer.BuildContext(TemplateSrc, TemplateDir, oracle));

        var sw = Stopwatch.StartNew();
        PreflightResult result = analyzer.Analyze(PreflightAnalyzer.BuildContext(TemplateSrc, TemplateDir, oracle));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500,
            $"анализ 3 файлов занял {sw.ElapsedMilliseconds} мс (внутренний замер {result.Elapsed.TotalMilliseconds:F0} мс)");
    }

    /// <summary>
    /// Сломанная копия эталона обязана дать ровно те ошибки, ради которых правила писались.
    /// Отсутствующий модуль подобран по дереву клиента 1.44 — в другой версии он может появиться.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    [Trait("Needs", "Template")]
    public void BrokenCopyOfTemplate_IsCaught()
    {
        Skip.IfNot(TemplateAvailable, TestEnvironment.TemplateMissingReason);
        TestEnvironment.RequireReferenceClientSource("1.44");

        using var fx = new PreflightFixture();
        // Стаб берём как есть, поэтому и пакет обязан называться hello_tanker: иначе стаб
        // ссылался бы на чужой модуль, и цепочка импорта не сошлась бы (это и есть подводный
        // камень B2/B3/B4 мастера нового проекта).
        fx.AddEntryPoint("mod_hello_tanker", File.ReadAllText(
            Path.Combine(TemplateSrc, "res", "scripts", "client", "gui", "mods", "mod_hello_tanker.py")));

        // Тело мода портим ровно тремя способами из отчёта разведки.
        fx.AddPackageModule("hello_tanker", "main", """
            import BigWorld
            from gui.impl.lobby.page.chats_presenter import ChatsPresenter

            BigWorld.callback(1.0, None)


            def init():
                ChatsPresenter()
            """);

        PreflightContext context = PreflightAnalyzer.BuildContext(fx.SrcDir, fx.ProjectDir, RealOracle());
        PreflightResult result = new PreflightAnalyzer().Analyze(context);

        Assert.Contains(result.Diagnostics, d => d.RuleId == "WOT001" && d.Severity == PreflightSeverity.Error);
        Assert.Contains(result.Diagnostics, d => d.RuleId == "WOT002" && d.Severity == PreflightSeverity.Error);
        Assert.Contains(result.Diagnostics, d => d.RuleId == "WOT005" && d.Severity == PreflightSeverity.Error);
    }

    // ------------------------------------------------------------------ оракул

    /// <summary>
    /// Оракул знает и scripts.pkg, и дополнительные пакеты, и stdlib, и нативные модули.
    /// Наличие и отсутствие модулей сверено с деревом клиента 1.44.
    /// </summary>
    [SkippableTheory]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    [InlineData("gui.SystemMessages", true)]
    [InlineData("PlayerEvents", true)]
    [InlineData("helpers.dependency", true)]
    [InlineData("skeletons.gui.app_loader", true)]
    [InlineData("frontline.gui.impl.lobby", true)]          // дополнительный .pkg
    [InlineData("battle_royale", true)]
    [InlineData("comp7", true)]
    [InlineData("json", true)]                              // common/Lib без префикса
    [InlineData("typing", true)]                            // site-packages/typing-3.6.2
    [InlineData("gui.impl.lobby.page.chats_presenter", false)]
    [InlineData("gui.lobby_state_machine.states", false)]
    public void Oracle_KnowsWhatIsInTheClient(string module, bool exists)
    {
        TestEnvironment.RequireReferenceClientSource("1.44");

        Assert.Equal(exists, RealOracle().ModuleExists(module));
    }

    /// <summary>Нативные и встроенные модули не считаются игровым API и не проверяются по индексу.</summary>
    [Theory]
    [InlineData("BigWorld")]
    [InlineData("ResMgr")]
    [InlineData("Math")]
    [InlineData("GUI")]
    [InlineData("CGF")]
    public void Oracle_TreatsEngineModulesAsNative(string module)
    {
        Assert.True(ClientApiOracle.IsNative(module));
        Assert.False(ClientApiOracle.IsBuiltin(module));
    }

    /// <summary>stdlib отделена от игрового кода: иначе <c>logging.getLogger</c> считался бы игровой работой.</summary>
    [SkippableTheory]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    [InlineData("json")]
    [InlineData("logging")]
    [InlineData("os")]
    [InlineData("sys")]
    [InlineData("typing")]
    public void Oracle_TreatsStdlibAsVendor(string module)
    {
        TestEnvironment.RequireClientSource();

        Assert.True(RealOracle().IsVendorModule(module), module);
    }

    /// <summary>
    /// Транзитивное разворачивание <c>from X import *</c> работает на настоящем клиенте:
    /// <c>helpers/dependency.py</c> — чистый ре-экспорт, и без разворачивания
    /// <c>dependency.instance</c> давал бы лавину ложных срабатываний (108 на корпусе).
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void Oracle_ExpandsStarImports()
    {
        TestEnvironment.RequireClientSource();

        ModuleFacts? facts = RealOracle().FactsOf("helpers.dependency");

        Assert.NotNull(facts);
        Assert.Contains("instance", facts!.Exports);
        Assert.Contains("descriptor", facts.Exports);
    }

    /// <summary>Символы, которых в 1.44 действительно нет, оракул не находит.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void Oracle_DoesNotInventMissingSymbols()
    {
        TestEnvironment.RequireReferenceClientSource("1.44");

        ClientApiOracle oracle = RealOracle();

        ModuleFacts? gameControl = oracle.FactsOf("gui.game_control");
        Assert.NotNull(gameControl);
        Assert.DoesNotContain("g_instance", gameControl!.Exports);        // подтверждено грепом по дереву 1.44

        ModuleFacts? lobbyEntry = oracle.FactsOf("gui.Scaleform.lobby_entry");
        Assert.NotNull(lobbyEntry);
        Assert.Contains("LobbyEntry", lobbyEntry!.Exports);
        Assert.DoesNotContain("getLobbyStateMachine", lobbyEntry.Exports);
    }

    // ------------------------------------------------------- реальные моды из игры

    /// <summary>
    /// Корпус: распакованные и декомпилированные сторонние моды, по подкаталогу на мод с именем
    /// вида <c>com.modxvm.xvm_13.1.0.0059</c>. Это чужой код, в репозиторий он не входит.
    /// <para>
    /// Пороги ниже сняты на конкретных версиях модов из каталога модов клиента 1.44, поэтому
    /// корпус — свойство машины: путь задаёт <c>WOTCOCKPIT_MOD_CORPUS_DIR</c>, а без неё при
    /// <c>WOTCOCKPIT_REAL_MACHINE=1</c> берётся <c>&lt;шаблон&gt;\.scratch\mod-corpus</c>.
    /// Корпуса нет — тесты пропускаются с путём, по которому его искали.
    /// </para>
    /// </summary>
    /// <returns>Каталог корпуса.</returns>
    private static string RequireCorpusDir()
    {
        TestEnvironment.RequireReferenceClientSource("1.44");

        string dir = TestEnvironment.Read(TestEnvironment.ModCorpusVariable)
                     ?? Path.Combine(ReposLayout.Template, ".scratch", "mod-corpus");
        Skip.IfNot(Directory.Exists(dir),
            $"нет корпуса модов ({dir}) — укажите его в {TestEnvironment.ModCorpusVariable}");
        return dir;
    }

    /// <summary>Сообщение о пропуске: какого мода нет в корпусе.</summary>
    /// <param name="dir">Каталог мода в корпусе.</param>
    /// <returns>Текст.</returns>
    private static string NoCorpus(string dir) =>
        $"в корпусе модов нет {Path.GetFileName(dir)} ({dir})";

    /// <summary>
    /// Замер доли ложных срабатываний самого рискованного правила на реальных модах.
    /// Порог выбран по разведке: WOT004 после трёх приёмов подавления обязано давать
    /// единицы срабатываний, а не сотни (было 124 у наивной реализации).
    /// </summary>
    [SkippableTheory]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    [Trait("Needs", "ModCorpus")]
    [InlineData("com.modxvm.xvm_13.1.0.0059", 0, 40)]
    [InlineData("me.poliroid.battlehits_2.2.5", 0, 0)]
    [InlineData("me.poliroid.modslistapi_1.6.01", 0, 0)]
    [InlineData("izeberg.modssettingsapi_1.6.4", 0, 0)]
    [InlineData("lesta_replays_upload", 0, 0)]
    [InlineData("pruszko.responsive_reticle_1.0.3", 0, 0)]
    public void RealMods_UnknownApiRule_StaysWithinMeasuredBudget(string mod, int minHits, int maxHits)
    {
        string dir = Path.Combine(RequireCorpusDir(), mod);
        Skip.IfNot(Directory.Exists(dir), NoCorpus(dir));

        PreflightContext context = PreflightAnalyzer.BuildContext(dir, dir, RealOracle());
        PreflightResult result = new PreflightAnalyzer(new IPreflightRule[] { new UnknownApiRule() }).Analyze(context);

        // ЗАМЕР НА НУЛЕ ФАЙЛОВ — НЕ ЗАМЕР. У пяти модов нижняя и верхняя граница — ноль, и
        // пустой разбор проходил бы их всегда. Так и было: корпус лёг в .scratch, префлайт
        // пропускал его целиком, и все шесть замеров были зелёными, ничего не проверяя.
        Assert.True(result.FilesAnalyzed > 0,
            $"{mod}: не разобрано ни одного файла — замер ничего не проверяет ({dir})");

        int hits = result.Diagnostics.Count;
        Assert.True(hits >= minHits && hits <= maxHits,
            $"{mod}: {hits} срабатываний WOT002/003/004, ожидалось {minHits}..{maxHits}" + Environment.NewLine +
            string.Join(Environment.NewLine, result.Diagnostics.Take(50).Select(d => d.ToString())));
    }

    /// <summary>
    /// Правило WOT004 (атрибуты модулей) на XVM обязано давать РОВНО те два истинных
    /// срабатывания, которые нашла разведка: <c>gui.game_control.g_instance</c> и
    /// <c>tooltips_vehicle.CrystalBlockConstructor</c>. Это прямой контроль ложных срабатываний.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    [Trait("Needs", "ModCorpus")]
    public void RealMods_AttributeRule_FindsOnlyTheTwoTruePositives()
    {
        string dir = Path.Combine(RequireCorpusDir(), "com.modxvm.xvm_13.1.0.0059");
        Skip.IfNot(Directory.Exists(dir), NoCorpus(dir));

        PreflightContext context = PreflightAnalyzer.BuildContext(dir, dir, RealOracle());
        PreflightResult result = new PreflightAnalyzer(new IPreflightRule[] { new UnknownApiRule() }).Analyze(context);

        PreflightDiagnostic[] attributes = result.Diagnostics.Where(d => d.RuleId == "WOT004").ToArray();

        Assert.Equal(2, attributes.Length);
        Assert.Contains(attributes, d => d.Message.Contains("g_instance", StringComparison.Ordinal));
        Assert.Contains(attributes, d => d.Message.Contains("CrystalBlockConstructor", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ служба и DI

    /// <summary>Служба резолвится из контейнера Core.</summary>
    [Fact]
    public void Container_ResolvesPreflightService()
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddCockpitCore()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var service = provider.GetRequiredService<IPreflightService>();

        Assert.NotNull(service);
        // 20 классов правил на 22 идентификатора: UnknownApiRule один, а кодов у него четыре
        // (WOT002/003/004 — модуль, символ, арность).
        Assert.Equal(20, service.Rules.Count);
        Assert.Equal(22, PreflightAnalyzer.AllRuleIds.Count);
    }

    /// <summary>Асинхронный прогон по проекту работает и без дерева исходников клиента.</summary>
    [Fact]
    public async Task Service_AnalyzesProject_WithoutClientSources()
    {
        using var fx = new PreflightFixture();
        fx.AddEntryPoint("mod_demo", "import BigWorld\n\nBigWorld.callback(1.0, None)\n");

        using ServiceProvider provider = new ServiceCollection().AddCockpitCore().BuildServiceProvider();
        var service = provider.GetRequiredService<IPreflightService>();

        var project = new ModProject { ProjectDir = fx.ProjectDir, ModId = "ru.local.demo", PackageName = "demo" };
        PreflightResult result = await service.AnalyzeAsync(project);

        Assert.Contains(result.Diagnostics, d => d.RuleId == "WOT001");
        Assert.True(result.Elapsed >= TimeSpan.Zero);
        Assert.Contains("preflight", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>Несуществующий каталог — пустой результат, а не исключение.</summary>
    [Fact]
    public async Task Service_MissingDirectory_ReturnsEmpty()
    {
        using ServiceProvider provider = new ServiceCollection().AddCockpitCore().BuildServiceProvider();
        var service = provider.GetRequiredService<IPreflightService>();

        PreflightResult result = await service.AnalyzeAsync(
            Path.Combine(Path.GetTempPath(), "wot-cockpit-no-such-src-" + Guid.NewGuid().ToString("N")));

        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.FilesAnalyzed);
    }

    /// <summary>Отмена не превращается в зависание и не портит состояние службы.</summary>
    [Fact]
    public async Task Service_Cancellation_IsHonoured()
    {
        using var fx = new PreflightFixture();
        fx.AddEntryPoint("mod_demo", "def init():\n    pass\n");

        using ServiceProvider provider = new ServiceCollection().AddCockpitCore().BuildServiceProvider();
        var service = provider.GetRequiredService<IPreflightService>();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.AnalyzeAsync(fx.SrcDir, fx.ProjectDir, null, cts.Token));
    }
}
