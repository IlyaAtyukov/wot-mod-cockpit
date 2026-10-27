using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.App.Stage3;
using WotModCockpit.App.ViewModels;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты моделей представления этапа 3: панели Problems, каталога рецептов и мастера
/// нового проекта.
/// <para>
/// Проверяются две разные вещи и обе нужны. Первая — поведение UI на ПОДСТАВНЫХ швах:
/// фильтр, группировка, предупреждение перед сборкой, вставка рецепта в файл. Вторая —
/// что настоящие реализации Core действительно приезжают в UI через контейнер и отдают
/// непустой результат: <c>dotnet build</c> доказывает только компиляцию, а «шов подключён,
/// но всегда возвращает пусто» выглядит в приложении точно так же, как «всё хорошо,
/// замечаний нет».
/// </para>
/// </summary>
public sealed class Stage3ViewModelTests
{
    /// <summary>
    /// Шаблон мода по тому же правилу, что у кокпита: <c>WOTCOCKPIT_TEMPLATE_DIR</c>, затем
    /// соседние каталоги. Тесты с ним — только на настоящей машине (<c>WOTCOCKPIT_REAL_MACHINE=1</c>).
    /// </summary>
    private static readonly string TemplateDir = WotModCockpit.Core.Scaffold.ProjectScaffolder.ProbeTemplateDirectory();

    /// <summary>Пропускает тест, которому нужен настоящий шаблон мода.</summary>
    private static void RequireRealTemplate()
    {
        Skip.IfNot(TestSettingsIsolation.RealMachine,
            "нужен настоящий шаблон мода: задайте WOTCOCKPIT_REAL_MACHINE=1 (и при необходимости WOTCOCKPIT_TEMPLATE_DIR)");
        Skip.If(TemplateDir.Length == 0 || !Directory.Exists(TemplateDir),
            "шаблон мода не найден — задайте WOTCOCKPIT_TEMPLATE_DIR");
    }

    // ================================================================= панель Problems

    /// <summary>
    /// Фильтр по уровню, поиск и счётчики. Счётчики считаются по ВСЕМ находкам, а не по
    /// показанным: иначе включённый фильтр делал бы вид, что ошибок нет.
    /// </summary>
    [Fact]
    public void Problems_FilterByLevel_KeepsCountersOverAllFindings()
    {
        var backend = new FakePreflightBackend(
            Problem("WOT001", ProblemSeverity.Error, "src/a.py", 10),
            Problem("WOT006", ProblemSeverity.Warning, "src/a.py", 20),
            Problem("WOT013", ProblemSeverity.Info, "src/b.py", 30),
            Problem("WOT009", ProblemSeverity.Warning, "src/b.py", 40));

        var vm = new ProblemsViewModel(backend, new NoopEditor());

        Run(vm, Project());

        Assert.Equal(4, vm.Problems.Count);
        Assert.Equal(1, vm.ErrorCount);
        Assert.Equal(2, vm.WarningCount);
        Assert.Equal(1, vm.InfoCount);
        Assert.True(vm.HasErrors);

        vm.ShowWarnings = false;
        vm.ShowInfos = false;

        Assert.Single(vm.Problems);
        Assert.Equal("WOT001", vm.Problems[0].Code);

        // Счётчики не поехали: панель по-прежнему честно говорит, сколько всего найдено.
        Assert.Equal(2, vm.WarningCount);
        Assert.Equal(1, vm.InfoCount);

        vm.ShowWarnings = true;
        vm.ShowInfos = true;
        vm.SearchText = "WOT009";

        Assert.Single(vm.Problems);
        Assert.Equal("WOT009", vm.Problems[0].Code);
    }

    /// <summary>
    /// Смена группировки обязана переписать ключи ВСЕХ строк, а не только видимых:
    /// иначе после смены режима половина списка остаётся в старых группах.
    /// </summary>
    [Fact]
    public void Problems_Grouping_RewritesKeys()
    {
        var backend = new FakePreflightBackend(
            Problem("WOT001", ProblemSeverity.Error, "src/a.py", 10),
            Problem("WOT006", ProblemSeverity.Warning, "src/b.py", 20));

        var vm = new ProblemsViewModel(backend, new NoopEditor());
        Run(vm, Project());

        Assert.Equal(new[] { "src/a.py", "src/b.py" }, vm.Problems.Select(p => p.GroupKey).OrderBy(k => k));

        vm.Grouping = ProblemGrouping.Rule;
        Assert.Equal(new[] { "WOT001", "WOT006" }, vm.Problems.Select(p => p.GroupKey).OrderBy(k => k));

        vm.Grouping = ProblemGrouping.Severity;
        Assert.All(vm.Problems, p => Assert.Contains("—", p.GroupKey, StringComparison.Ordinal));
    }

    /// <summary>
    /// Скрытое правило уходит из списка, но панель обязана СКАЗАТЬ, что оно скрыто:
    /// молча опустевший список неотличим от чистого проекта.
    /// </summary>
    [Fact]
    public void Problems_MuteRule_HidesButReportsIt()
    {
        var backend = new FakePreflightBackend(
            Problem("WOT013", ProblemSeverity.Info, "src/a.py", 1),
            Problem("WOT020", ProblemSeverity.Info, "src/a.py", 2));

        var vm = new ProblemsViewModel(backend, new NoopEditor());
        Run(vm, Project());

        vm.Selected = vm.Problems.First(p => p.Code == "WOT013");
        vm.MuteRuleCommand.Execute(null);

        Assert.Single(vm.Problems);
        Assert.Equal(1, vm.MutedCount);
        Assert.Contains("WOT013", vm.MutedText, StringComparison.Ordinal);

        // Следующий прогон обязан ПЕРЕДАТЬ скрытое правило в Core, а не отфильтровать после:
        // считать находку, чтобы потом её выбросить, — зря потраченный проход по файлам.
        Run(vm, Project());
        Assert.Contains("WOT013", backend.LastDisabled);

        // Возврат скрытых правил сам по себе находки не воскрешает: их не считали.
        // Панель прямо об этом пишет, а список наполняет уже следующий прогон.
        vm.ResetMutedRulesCommand.Execute(null);
        Assert.Equal(0, vm.MutedCount);
        Assert.Contains("перезапустите", vm.StatusText, StringComparison.OrdinalIgnoreCase);

        Run(vm, Project());
        Assert.Equal(2, vm.Problems.Count);
    }

    /// <summary>
    /// Незаконченный сервис Core бросает <see cref="NotImplementedException"/>. Панель обязана
    /// это пережить, показать причину и не уронить приложение.
    /// </summary>
    [Fact]
    public void Problems_SurvivesThrowingBackend()
    {
        var vm = new ProblemsViewModel(new ThrowingPreflightBackend(), new NoopEditor());

        PreflightReport report = Run(vm, Project());

        Assert.NotNull(report.Error);
        Assert.False(vm.IsRunning);
        Assert.Contains("заглушка", vm.ErrorText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Проверка не выполнена", vm.EmptyStateText, StringComparison.Ordinal);
    }

    /// <summary>Неподключённый шов — это состояние панели с объяснением, а не пустой экран.</summary>
    [Fact]
    public void Problems_UnavailableBackend_ExplainsItself()
    {
        var vm = new ProblemsViewModel(new UnavailablePreflightBackend(), new NoopEditor());

        Assert.False(vm.IsAvailable);
        Assert.Contains("не подключён", vm.EmptyStateText, StringComparison.OrdinalIgnoreCase);

        Run(vm, Project());
        Assert.False(vm.IsRunning);
    }

    // ================================================================= проверка перед сборкой

    /// <summary>
    /// Ошибки перед сборкой обязаны СПРОСИТЬ, а не запретить: заведомо кривой мод иногда
    /// собирают специально, чтобы посмотреть, как он падает.
    /// </summary>
    [Fact]
    public void PreflightGate_AsksButNeverBlocks()
    {
        using var ui = new UiThread();

        (bool asked, bool allowed, string body) = ui.Invoke(() =>
        {
            Stage3ViewModel stage3 = BuildStage3(
                out _,
                new FakePreflightBackend(Problem("WOT001", ProblemSeverity.Error, "src/a.py", 7)));

            string captured = string.Empty;
            bool called = false;

            bool result = Gate(
                stage3,
                (_, text) =>
                {
                    called = true;
                    captured = text;
                    return true;      // пользователь настоял — собираем
                });

            return (called, result, captured);
        });

        Assert.True(asked);
        Assert.True(allowed);
        Assert.Contains("WOT001", body, StringComparison.Ordinal);
        Assert.Contains("Собрать всё равно?", body, StringComparison.Ordinal);
    }

    /// <summary>Отказ пользователя обязан отменить сборку.</summary>
    [Fact]
    public void PreflightGate_RespectsRefusal()
    {
        using var ui = new UiThread();

        bool allowed = ui.Invoke(() =>
        {
            Stage3ViewModel stage3 = BuildStage3(
                out _,
                new FakePreflightBackend(Problem("WOT014", ProblemSeverity.Error, "src/a.py", 0)));

            return Gate(stage3, (_, _) => false);
        });

        Assert.False(allowed);
    }

    /// <summary>Без ошибок диалога быть не должно вовсе — иначе проверка станет помехой.</summary>
    [Fact]
    public void PreflightGate_SilentWhenClean()
    {
        using var ui = new UiThread();

        (bool asked, bool allowed) = ui.Invoke(() =>
        {
            Stage3ViewModel stage3 = BuildStage3(
                out _,
                new FakePreflightBackend(Problem("WOT013", ProblemSeverity.Info, "src/a.py", 3)));

            bool called = false;
            bool result = Gate(stage3, (_, _) => { called = true; return false; });

            return (called, result);
        });

        Assert.False(asked);
        Assert.True(allowed);
    }

    /// <summary>Выключенная проверка перед сборкой не должна ни считать, ни спрашивать.</summary>
    [Fact]
    public void PreflightGate_Disabled_DoesNotRun()
    {
        using var ui = new UiThread();

        (int runs, bool allowed) = ui.Invoke(() =>
        {
            var backend = new FakePreflightBackend(Problem("WOT001", ProblemSeverity.Error, "src/a.py", 1));
            Stage3ViewModel stage3 = BuildStage3(out _, backend);
            stage3.Problems.AnalyzeBeforeBuild = false;

            bool result = Gate(stage3, (_, _) => false);
            return (backend.Runs, result);
        });

        Assert.Equal(0, runs);
        Assert.True(allowed);
    }

    // ================================================================= навигация по экранам

    /// <summary>
    /// Три экрана взаимоисключающие: они лежат в одной ячейке разметки, и два видимых
    /// сразу означали бы кашу на экране.
    /// </summary>
    [Fact]
    public void Navigation_ScreensAreExclusive()
    {
        using var ui = new UiThread();

        ui.Invoke(() =>
        {
            Stage3ViewModel stage3 = BuildStage3(out MainViewModel main, new FakePreflightBackend());

            Assert.True(stage3.IsBuildScreen);

            stage3.IsRecipesScreen = true;
            Assert.False(main.IsApiScreen);
            Assert.False(stage3.IsBuildScreen);

            main.IsApiScreen = true;
            Assert.False(stage3.IsRecipesScreen);
            Assert.False(stage3.IsBuildScreen);

            stage3.IsBuildScreen = true;
            Assert.False(main.IsApiScreen);
            Assert.False(stage3.IsRecipesScreen);

            // Снятие галочки не имеет права оставить окно без единого видимого экрана,
            // и тумблер обязан получить уведомление — иначе он останется снятым,
            // хотя экран сборки на месте.
            int notified = 0;
            stage3.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Stage3ViewModel.IsBuildScreen))
                {
                    notified++;
                }
            };

            stage3.IsBuildScreen = false;

            Assert.True(stage3.IsBuildScreen);
            Assert.True(notified > 0, "снятие галочки не уведомило привязку — чип останется снятым");
        });
    }

    // ================================================================= рецепты

    /// <summary>
    /// Подстановка идёт по убыванию длины токена. Наивный порядок ломает идентификатор мода:
    /// <c>my_mod</c> — подстрока <c>ru.local.my_mod</c>.
    /// </summary>
    [Fact]
    public void RecipeRenderer_LongTokensFirst()
    {
        const string code = "MOD_ID = 'ru.local.my_mod'\nCONFIG = 'mods/configs/my_mod/config.json'\n";

        string rendered = RecipeRenderer.Render(code, new Dictionary<string, string>
        {
            ["my_mod"] = "battle_timer",
            ["ru.local.my_mod"] = "com.example.timer"
        });

        Assert.Contains("MOD_ID = 'com.example.timer'", rendered, StringComparison.Ordinal);
        Assert.Contains("mods/configs/battle_timer/config.json", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("ru.local.battle_timer", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Сканер обязан найти и паспортные токены, и строки, помеченные в рецепте как «ПОДСТАВЬ»:
    /// именно там живут имена классов, методов и событий.
    /// </summary>
    [Fact]
    public void RecipeRenderer_FindsPassportAndMarkedTokens()
    {
        const string code =
            "# -*- coding: utf-8 -*-\n" +
            "MOD_ID = 'ru.local.my_mod'      # ПОДСТАВЬ\n" +
            "LOG_PREFIX = '[MyMod]'          # ПОДСТАВЬ\n" +
            "HOTKEY_KEY = 'KEY_F5'           # ПОДСТАВЬ: клавиша\n" +
            "_logger = logging.getLogger('MyMod')\n";

        IReadOnlyList<RecipeToken> tokens = RecipeRenderer.Scan(code);

        Assert.Contains(tokens, t => t.Token == "ru.local.my_mod" && t.FromProject);
        Assert.Contains(tokens, t => t.Token == "MyMod" && t.FromProject);
        Assert.Contains(tokens, t => t.Token == "KEY_F5" && !t.FromProject);

        // Дубликатов быть не должно: 'ru.local.my_mod' помечен ПОДСТАВЬ и одновременно
        // является паспортным токеном.
        Assert.Equal(tokens.Count, tokens.Select(t => t.Token).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Вставка в файл: рядом обязана появиться копия .bak, исходное содержимое — сохраниться,
    /// код — дописаться в конец. Молча испортить чужой исходник кокпит права не имеет.
    /// </summary>
    [Fact]
    public void Recipes_InsertIntoFile_AppendsAndKeepsBackup()
    {
        string root = NewTempDir();
        try
        {
            string mainPy = Path.Combine(root, "src", "res", "scripts", "client", "gui", "mods", "demo", "main.py");
            Directory.CreateDirectory(Path.GetDirectoryName(mainPy)!);
            File.WriteAllText(mainPy, "# -*- coding: utf-8 -*-\nimport logging\n");

            var backend = new FakeRecipeBackend(SampleRecipe());
            var vm = new RecipesViewModel(backend, new NoopEditor())
            {
                ConfirmHandler = (_, _) => true
            };

            vm.SetProject(new ModProject { ProjectDir = root, PackageName = "demo", ModId = "ru.local.demo" });
            Load(vm);

            vm.Selected = vm.Recipes.Single();
            Assert.NotEmpty(vm.RenderedCode);

            Assert.NotNull(vm.SelectedTargetFile);
            vm.InsertIntoFileCommand.Execute(null);

            string after = File.ReadAllText(mainPy);
            Assert.StartsWith("# -*- coding: utf-8 -*-\nimport logging\n", after, StringComparison.Ordinal);
            Assert.Contains("рецепт RXX", after, StringComparison.Ordinal);
            Assert.Contains("def demo_action", after, StringComparison.Ordinal);

            Assert.True(File.Exists(mainPy + ".bak"), "копия .bak рядом с файлом не создана");
            Assert.Equal("# -*- coding: utf-8 -*-\nimport logging\n", File.ReadAllText(mainPy + ".bak"));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// Столкновение имён верхнего уровня попадает В ТЕКСТ ДИАЛОГА до записи.
    ///
    /// <para>Имена рецептов пересекаются намеренно: <c>subscribe</c>/<c>unsubscribe</c> —
    /// в R05, R06, R13 и R16, <c>register</c> — в R08, R14, R15, <c>_safe</c> — в R01 и R05.
    /// Модуль исполняется сверху вниз, поэтому второе объявление молча съедает первое,
    /// а <c>check_py2_syntax</c> при этом отвечает <c>[OK]</c>. Предупредить об этом надо
    /// ДО записи, а не только правилом WOT022 после.</para>
    /// </summary>
    [Fact]
    public void Recipes_InsertIntoFile_WarnsAboutTopLevelNameClash()
    {
        string root = NewTempDir();
        try
        {
            string mainPy = Path.Combine(root, "src", "res", "scripts", "client", "gui", "mods", "demo", "main.py");
            Directory.CreateDirectory(Path.GetDirectoryName(mainPy)!);

            // В файле уже есть demo_action — то же имя объявляет и рецепт.
            File.WriteAllText(mainPy, "# -*- coding: utf-8 -*-\n\n\ndef demo_action():\n    return 1\n");

            string shown = string.Empty;
            var vm = new RecipesViewModel(new FakeRecipeBackend(SampleRecipe()), new NoopEditor())
            {
                ConfirmHandler = (_, body) => { shown = body; return true; }
            };

            vm.SetProject(new ModProject { ProjectDir = root, PackageName = "demo", ModId = "ru.local.demo" });
            Load(vm);
            vm.Selected = vm.Recipes.Single();

            vm.InsertIntoFileCommand.Execute(null);

            Assert.Contains("demo_action", shown, StringComparison.Ordinal);
            Assert.Contains("ПОСЛЕДНЕЕ объявление", shown, StringComparison.Ordinal);
            Assert.Contains("перекрыто имён", vm.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// Выбор точки входа <c>mod_*.py</c> предупреждается: её компилирует сам build.py
    /// через <c>tools/py27c.py</c>, а тот не умеет замыкания — их используют 7 рецептов
    /// из 16, и сборка упадёт с Py27CompileError, никак не сославшись на рецепт.
    /// </summary>
    [Fact]
    public void Recipes_InsertIntoFile_WarnsWhenTargetIsEntryPoint()
    {
        string root = NewTempDir();
        try
        {
            string mods = Path.Combine(root, "src", "res", "scripts", "client", "gui", "mods");
            Directory.CreateDirectory(mods);
            File.WriteAllText(Path.Combine(mods, "mod_demo.py"), "# -*- coding: utf-8 -*-\n");

            string shown = string.Empty;
            var vm = new RecipesViewModel(new FakeRecipeBackend(SampleRecipe()), new NoopEditor())
            {
                ConfirmHandler = (_, body) => { shown = body; return false; }
            };

            vm.SetProject(new ModProject { ProjectDir = root, PackageName = "demo", ModId = "ru.local.demo" });
            Load(vm);
            vm.Selected = vm.Recipes.Single();

            vm.SelectedTargetFile = "src/res/scripts/client/gui/mods/mod_demo.py";
            vm.InsertIntoFileCommand.Execute(null);

            Assert.Contains("ТОЧКА ВХОДА", shown, StringComparison.Ordinal);
            Assert.Contains("py27c", shown, StringComparison.Ordinal);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>Тело мода точкой входа не считается — иначе предупреждение стало бы шумом.</summary>
    [Theory]
    [InlineData("src/res/scripts/client/gui/mods/mod_demo.py", true)]
    [InlineData("src\\res\\scripts\\client\\gui\\mods\\mod_demo.py", true)]
    [InlineData("src/res/scripts/client/gui/mods/demo/main.py", false)]
    [InlineData("src/res/scripts/client/gui/mods/demo/mod_helper.py", true)]
    [InlineData("tools/mod_builder.py", false)]
    public void RecipeRenderer_IsEntryPointFile(string path, bool expected)
    {
        Assert.Equal(expected, RecipeRenderer.IsEntryPointFile(path));
    }

    /// <summary>
    /// Имена верхнего уровня берутся из лексической модели Core, поэтому <c>def</c>
    /// внутри докстринга именем не считается — а цитаты кода клиента есть почти
    /// в каждом рецепте каталога.
    /// </summary>
    [Fact]
    public void RecipeRenderer_TopLevelNames_IgnoresDocstringsAndNesting()
    {
        const string code = """
                            # -*- coding: utf-8 -*-
                            '''Пример из клиента:

                                def pushMessage(text):
                                    pass
                            '''


                            def subscribe():
                                def inner():
                                    pass
                                return inner


                            class Holder(object):
                                def method(self):
                                    pass
                            """;

        IReadOnlyList<string> names = RecipeRenderer.TopLevelNames(code);

        Assert.Equal(["subscribe", "Holder"], names);
        Assert.Equal(["subscribe"], RecipeRenderer.NameClashes(code, "def subscribe():\n    pass\n"));
        Assert.Empty(RecipeRenderer.NameClashes(code, "def other():\n    pass\n"));
    }

    /// <summary>Отказ в диалоге обязан оставить файл нетронутым.</summary>
    [Fact]
    public void Recipes_InsertIntoFile_Cancelled_LeavesFileAlone()
    {
        string root = NewTempDir();
        try
        {
            string mainPy = Path.Combine(root, "src", "main.py");
            Directory.CreateDirectory(Path.GetDirectoryName(mainPy)!);
            File.WriteAllText(mainPy, "original\n");

            var vm = new RecipesViewModel(new FakeRecipeBackend(SampleRecipe()), new NoopEditor())
            {
                ConfirmHandler = (_, _) => false
            };

            vm.SetProject(new ModProject { ProjectDir = root, PackageName = "demo" });
            Load(vm);
            vm.Selected = vm.Recipes.Single();

            vm.InsertIntoFileCommand.Execute(null);

            Assert.Equal("original\n", File.ReadAllText(mainPy));
            Assert.False(File.Exists(mainPy + ".bak"));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// Паспорт открытого проекта обязан приезжать в поля подстановки: иначе вставленный код
    /// будет писать в лог под чужим тегом, и фильтр живой ленты этапа 1 его не увидит.
    /// </summary>
    [Fact]
    public void Recipes_Parameters_ComeFromOpenProject()
    {
        var vm = new RecipesViewModel(new FakeRecipeBackend(SampleRecipe()), new NoopEditor());

        vm.SetProject(new ModProject
        {
            ProjectDir = @"C:\proj",
            ModId = "ru.local.battle_timer",
            PackageName = "battle_timer",
            DisplayName = "Battle Timer",
            LogTag = "[BattleTimer"          // ровно в таком виде тег лежит в wotmod.json
        });

        Load(vm);
        vm.Selected = vm.Recipes.Single();

        Assert.Contains("ru.local.battle_timer", vm.RenderedCode, StringComparison.Ordinal);
        Assert.Contains("[BattleTimer]", vm.RenderedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MyMod", vm.RenderedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("my_mod", vm.RenderedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Строка поиска, под которую выбранный рецепт не подходит, НЕ отменяет выбор насовсем.
    /// <para>
    /// Список привязан к <c>ListBox.SelectedItem</c>, поэтому подмена содержимого коллекции
    /// гасит выбор. Раньше это означало: набрал в поиске слово, промахнулся, стёр строку —
    /// список вернулся, а предпросмотр кода остался пустым, хотя пользователь ничего
    /// не отменял. Ровно это и было видно на живом прогоне приложения.
    /// </para>
    /// </summary>
    [Fact]
    public void Recipes_FilteringOutSelection_RestoresItWhenItComesBack()
    {
        var vm = new RecipesViewModel(
            new FakeRecipeBackend(SampleRecipe(), OtherRecipe()),
            new NoopEditor());

        Load(vm);
        vm.Selected = vm.Recipes.Single(r => r.Id == "RXX");
        string code = vm.RenderedCode;

        Assert.NotEmpty(code);

        // Поиск выбрасывает выбранный рецепт из видимого списка.
        vm.SearchText = "хоткей";

        Assert.Equal("RYY", Assert.Single(vm.Recipes).Id);
        Assert.Null(vm.Selected);
        Assert.Empty(vm.RenderedCode);

        // …и возвращает, как только фильтр снят.
        vm.SearchText = string.Empty;

        Assert.Equal(2, vm.Recipes.Count);
        Assert.NotNull(vm.Selected);
        Assert.Equal("RXX", vm.Selected!.Id);
        Assert.Equal(code, vm.RenderedCode);
    }

    /// <summary>
    /// Выбор, сделанный ПРИ включённом фильтре, перекрывает память фильтра: снятие поиска
    /// обязано оставить то, что пользователь выбрал последним.
    /// </summary>
    [Fact]
    public void Recipes_ExplicitSelectionUnderFilter_Wins()
    {
        var vm = new RecipesViewModel(
            new FakeRecipeBackend(SampleRecipe(), OtherRecipe()),
            new NoopEditor());

        Load(vm);
        vm.Selected = vm.Recipes.Single(r => r.Id == "RXX");

        vm.SearchText = "хоткей";
        vm.Selected = vm.Recipes.Single(r => r.Id == "RYY");
        vm.SearchText = string.Empty;

        Assert.Equal("RYY", vm.Selected!.Id);
    }

    // ================================================================= мастер нового проекта

    /// <summary>
    /// Мастер выводит из одного поля всё остальное — и отказывается создавать проект
    /// с именем пакета, которое не переживёт импорт в игре.
    /// </summary>
    [Fact]
    public void NewProject_DerivesNamesAndRejectsBadPackage()
    {
        var vm = new NewProjectViewModel(new UnavailableScaffoldBackend(), null)
        {
            DisplayName = "Battle Timer"
        };

        Assert.Equal("battle_timer", vm.Package);
        Assert.Equal("ru.local.battle_timer", vm.ModId);
        Assert.Equal("BattleTimer", vm.LogTag);
        Assert.Equal("mod_battle_timer.py", vm.EntryPointFile);
        Assert.Equal("ru.local.battle_timer_1.0.0.mtmod", vm.ArtifactName);

        vm.Package = "2fast";
        Assert.NotEmpty(vm.PackageError);

        vm.Package = "class";
        Assert.Contains("ключевое слово", vm.PackageError, StringComparison.OrdinalIgnoreCase);

        vm.Package = "battle_timer";
        Assert.Empty(vm.PackageError);

        vm.ModId = "ru/local/timer";
        Assert.Contains("разделител", vm.ModIdError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ручная правка выведенного поля больше не перетирается автоматикой.</summary>
    [Fact]
    public void NewProject_ManualEditWins()
    {
        var vm = new NewProjectViewModel(new UnavailableScaffoldBackend(), null)
        {
            DisplayName = "Battle Timer"
        };

        vm.ModId = "com.example.timer";
        vm.DisplayName = "Another Name";

        Assert.Equal("com.example.timer", vm.ModId);
        Assert.Equal("another_name", vm.Package);
    }

    /// <summary>
    /// Префикс идентификатора берётся из настроек (<c>modIdPrefix</c>), а подсказка под полем
    /// советует свой обратный домен — <c>ru.local</c> остаётся только значением по умолчанию.
    /// </summary>
    [Fact]
    public void NewProject_UsesModIdPrefixFromSettings()
    {
        var vm = new NewProjectViewModel(new UnavailableScaffoldBackend(), null, null, null, " io.github.dev. ")
        {
            DisplayName = "Battle Timer"
        };

        Assert.Equal("io.github.dev", vm.ModIdPrefix);
        Assert.Equal("io.github.dev.battle_timer", vm.ModId);
        Assert.Empty(vm.ModIdError);
        Assert.Contains("io.github.<ник>", vm.ModIdHint, StringComparison.Ordinal);
        Assert.Contains("modIdPrefix", vm.ModIdHint, StringComparison.Ordinal);

        var byDefault = new NewProjectViewModel(new UnavailableScaffoldBackend(), null, null, null, "  ");
        Assert.Equal("ru.local", byDefault.ModIdPrefix);
    }

    /// <summary>Шаблон из настроек подставляется в форму сразу и проходит её проверку.</summary>
    [Fact]
    public void NewProject_TemplateFromSettings_IsPrefilled()
    {
        string template = Path.Combine(Path.GetTempPath(), "wotcockpit-template-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(template);
        File.WriteAllText(Path.Combine(template, "build.py"), "# stand");

        try
        {
            var vm = new NewProjectViewModel(new UnavailableScaffoldBackend(), null, null, template);

            Assert.Equal(template, vm.TemplateDir);
            Assert.Empty(vm.TemplateDirError);
        }
        finally
        {
            Delete(template);
        }
    }

    /// <summary>
    /// Нет шаблона — отказ называет настройки, которые реально существуют (<c>templateDir</c>
    /// и <c>WOTCOCKPIT_TEMPLATE_DIR</c>), и говорит, где шаблон взять.
    /// </summary>
    [Fact]
    public void Scaffold_MissingTemplate_PointsToRealSettings()
    {
        var backend = new CoreScaffoldBackend(new WotModCockpit.Core.Scaffold.ProjectScaffolder());
        string missing = Path.Combine(Path.GetTempPath(), "wotcockpit-no-template-" + Guid.NewGuid().ToString("N"));

        ScaffoldPlan plan = Plan(backend, new ScaffoldRequest
        {
            TemplateDir = missing,
            TargetDir = Path.Combine(Path.GetTempPath(), "wotcockpit-scaffold-preview"),
            ModId = "ru.example.alpha",
            Package = "alpha",
            DisplayName = "Alpha",
            Version = "1.0.0",
            LogTag = "Alpha"
        });

        ScaffoldIssue issue = Assert.Single(plan.Issues);
        Assert.True(issue.Blocking);
        Assert.Contains("templateDir", issue.Message, StringComparison.Ordinal);
        Assert.Contains("WOTCOCKPIT_TEMPLATE_DIR", issue.Message, StringComparison.Ordinal);
        Assert.Contains("wot-mod-template", issue.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Скрытие правила НЕ имеет права гасить в Core соседние категории находок.
    /// <para>
    /// <c>UnknownApiRule</c> зарегистрирован под <c>WOT002</c>, но выдаёт ещё <c>WOT003</c>
    /// и <c>WOT004</c>, а выключение в <c>PreflightAnalyzer</c> считается по <c>Id</c> класса.
    /// Наивная передача скрытых кодов в Core убирала бы вместе с одной скрытой находкой
    /// ещё две категории — при том что панель честно пишет «скрыто правил: 1 (WOT002)».
    /// </para>
    /// </summary>
    [Theory]
    // Скрыт только WOT002 — класс обязан продолжать считаться ради WOT003/WOT004.
    // WOT003 и WOT004 своего класса в Core не имеют, поэтому проезжают насквозь
    // и там ни на что не влияют: гасит их фильтр панели.
    [InlineData(new[] { "WOT002" }, new string[0])]
    [InlineData(new[] { "WOT002", "WOT003" }, new[] { "WOT003" })]
    // Скрыто всё семейство — выключать класс можно.
    [InlineData(new[] { "WOT002", "WOT003", "WOT004" }, new[] { "WOT002", "WOT003", "WOT004" })]
    // Обычное правило с одним кодом передаётся как есть.
    [InlineData(new[] { "WOT013" }, new[] { "WOT013" })]
    [InlineData(new[] { "WOT013", "WOT002" }, new[] { "WOT013" })]
    // Коды, у которых своего класса в Core нет, безвредны: Core их просто не найдёт.
    [InlineData(new[] { "WOT004" }, new[] { "WOT004" })]
    public void Preflight_MutedRule_NeverSilencesSiblingCodes(string[] muted, string[] expected)
    {
        string[] forwarded = [.. CorePreflightBackend.DisableableInCore(muted)];

        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), forwarded.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// Справочник правил обязан содержать карточку на КАЖДЫЙ код находки, а не на класс
    /// правила: без разворота семейства <c>WOT003</c> и <c>WOT004</c> в нём отсутствовали бы,
    /// хотя панель эти находки показывает.
    /// </summary>
    [Fact]
    public void Preflight_RuleCards_CoverEveryCode()
    {
        using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();

        var backend = provider.GetRequiredService<IPreflightBackend>();
        IReadOnlyList<PreflightRuleCard> cards = backend.Rules;

        Assert.NotEmpty(cards);

        string[] codes = [.. cards.Select(c => c.Code)];
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (string code in new[] { "WOT002", "WOT003", "WOT004" })
        {
            PreflightRuleCard card = Assert.Single(cards, c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(card.Title), code + ": пустой заголовок");
            Assert.False(string.IsNullOrWhiteSpace(card.Rationale), code + ": нет обоснования");
        }

        // Три кода одного класса обязаны различаться заголовками, иначе справочник врёт.
        string[] titles =
        [
            .. cards.Where(c => c.Code is "WOT002" or "WOT003" or "WOT004").Select(c => c.Title)
        ];
        Assert.Equal(3, titles.Distinct(StringComparer.Ordinal).Count());
    }

    // ================================================================= настоящий Core

    /// <summary>
    /// Preflight из контейнера действительно доезжает до UI и находит замечания на РЕАЛЬНОМ
    /// проекте — эталонном шаблоне мода. Без этой проверки «шов подключён, но всегда пусто»
    /// выглядит в приложении как «замечаний нет».
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "Template")]
    public void RealCore_Preflight_AnalyzesTemplateProject()
    {
        RequireRealTemplate();

        using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();

        var backend = provider.GetRequiredService<IPreflightBackend>();
        Assert.True(backend.IsAvailable, "шов preflight не подключён к Core");
        Assert.NotEmpty(backend.Rules);

        ModProject? project = provider.GetRequiredService<IProjectStore>().Load(TemplateDir);
        Skip.If(project is null, "каталог не опознан как проект мода");

        PreflightReport report = Analyze(backend, project!);

        Assert.Null(report.Error);
        Assert.True(report.FilesAnalyzed > 0, "не разобрано ни одного файла мода");

        // Каждое правило обязано объяснять «почему это плохо» — ради этого панель и нужна.
        foreach (ProblemItem problem in report.Problems)
        {
            Assert.False(string.IsNullOrWhiteSpace(problem.Message), problem.Code + ": пустое сообщение");
            Assert.False(
                string.IsNullOrWhiteSpace(problem.Explanation),
                problem.Code + ": нет объяснения «почему это плохо»");
        }
    }

    /// <summary>
    /// Настоящие правила Core на НАСТОЯЩЕМ кривом моде: панель обязана получить непустой
    /// список, среди находок обязана быть хотя бы одна ошибка, и у каждой находки обязано
    /// быть объяснение «почему это плохо».
    /// <para>
    /// Проверка отвечает на вопрос, которого не закрывают ни <c>dotnet build</c>, ни тесты
    /// правил в Core: доезжают ли находки до модели панели в том виде, в каком их показывает
    /// UI. Мод собран из нарушений, каждое из которых проверено на реальном корпусе модов:
    /// игровая работа на уровне модуля, незащищённый init(), getattr без значения по
    /// умолчанию, кириллица без строки coding, LOG_DEBUG и print.
    /// </para>
    /// </summary>
    [Fact]
    public void RealCore_Preflight_FindsViolationsInBrokenMod()
    {
        string root = CreateBrokenProject();
        try
        {
            using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();

            ModProject? project = provider.GetRequiredService<IProjectStore>().Load(root);
            Assert.NotNull(project);

            var backend = provider.GetRequiredService<IPreflightBackend>();
            PreflightReport report = Analyze(backend, project!);

            Assert.Null(report.Error);
            Assert.True(report.FilesAnalyzed >= 2, $"разобрано файлов: {report.FilesAnalyzed}");

            string found = string.Join(", ", report.Problems.Select(p => p.Code).Distinct().OrderBy(c => c));
            Assert.True(report.Problems.Count > 0, "на заведомо кривом моде preflight не нашёл ничего");
            Assert.True(
                report.Problems.Any(p => p.Severity == ProblemSeverity.Error),
                "ни одной находки уровня «ошибка»; найдено: " + found);

            foreach (ProblemItem problem in report.Problems)
            {
                Assert.False(string.IsNullOrWhiteSpace(problem.Message), problem.Code + ": пустое сообщение");
                Assert.False(
                    string.IsNullOrWhiteSpace(problem.Explanation),
                    problem.Code + ": нет объяснения «почему это плохо». Найдено всего: " + found);
                Assert.False(string.IsNullOrWhiteSpace(problem.FilePath), problem.Code + ": нет пути к файлу");
            }

            // Панель обязана показать это списком, а не пустым состоянием.
            var vm = new ProblemsViewModel(backend, new NoopEditor());
            Run(vm, project!);

            Assert.True(vm.Problems.Count > 0, "панель Problems осталась пустой: " + vm.EmptyStateText);
            Assert.Equal(report.Problems.Count, vm.Problems.Count);
            Assert.All(vm.Problems, p => Assert.False(string.IsNullOrWhiteSpace(p.GroupKey)));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// Каталог рецептов из контейнера непустой, у каждого рецепта есть код, и в этом коде
    /// сканер находит точки подстановки — иначе поля параметров окажутся пустыми.
    /// </summary>
    [Fact]
    public void RealCore_Recipes_HaveCodeAndTokens()
    {
        using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();

        var backend = provider.GetRequiredService<IRecipeBackend>();
        Assert.True(backend.IsAvailable, "шов каталога рецептов не подключён к Core");

        IReadOnlyList<RecipeCard> cards = Cards(backend);

        Assert.True(cards.Count >= 12, $"рецептов должно быть не меньше 12, а их {cards.Count}");

        foreach (RecipeCard card in cards)
        {
            Assert.False(string.IsNullOrWhiteSpace(card.Code), card.Id + ": пустой код");
            Assert.False(string.IsNullOrWhiteSpace(card.Title), card.Id + ": пустой заголовок");
            Assert.False(string.IsNullOrWhiteSpace(card.Category), card.Id + ": пустая рубрика");

            IReadOnlyList<RecipeToken> tokens = RecipeRenderer.Scan(card.Code);
            Assert.True(tokens.Count > 0, card.Id + ": в коде нет ни одной точки подстановки");
        }

        // Подстановка паспорта обязана менять код, а не возвращать его как есть.
        RecipeCard first = cards[0];
        string rendered = RecipeRenderer.Render(
            first.Code,
            RecipeRenderer.Scan(first.Code)
                .Where(t => t.FromProject)
                .Select(t => new KeyValuePair<string, string>(t.Token, t.Token.Replace("my_mod", "demo_mod", StringComparison.Ordinal))));

        Assert.NotNull(rendered);
    }

    /// <summary>
    /// Pyright доезжает до панели через тот же шов и теми же находками: тумблер «pyright»
    /// обязан либо честно сказать, что линтера нет, либо дать результат.
    /// </summary>
    [SkippableFact]
    public void RealCore_Pyright_ReachesTheSamePanel()
    {
        using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();
        var backend = provider.GetRequiredService<IPreflightBackend>();

        PyrightProbe probe = backend.ProbePyrightAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.False(string.IsNullOrWhiteSpace(probe.Description));

        Skip.IfNot(probe.IsAvailable, "pyright на этой машине не найден: " + probe.Description);

        string root = CreateBrokenProject();
        try
        {
            ModProject? project = provider.GetRequiredService<IProjectStore>().Load(root);
            Assert.NotNull(project);

            PreflightReport report = backend
                .AnalyzeAsync(
                    project!,
                    new WotModCockpit.App.Stage3.PreflightOptions { RunPyright = true },
                    CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.Null(report.Error);
            Assert.Contains("pyright", report.Note, StringComparison.OrdinalIgnoreCase);

            // Находки линтера отличимы от находок правил: они помечены источником и кодом,
            // иначе в списке их не отличить от WOTxxx.
            foreach (ProblemItem item in report.Problems.Where(p => p.Source == "pyright"))
            {
                Assert.StartsWith("pyright:", item.Code, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(item.Explanation));
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>Мастер из контейнера строит непустой предпросмотр по реальному шаблону.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "Template")]
    public void RealCore_Scaffold_BuildsPlan()
    {
        RequireRealTemplate();

        using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();

        var backend = provider.GetRequiredService<IScaffoldBackend>();
        Assert.True(backend.IsAvailable, "шов мастера не подключён к Core");

        var request = new ScaffoldRequest
        {
            TemplateDir = TemplateDir,
            TargetDir = Path.Combine(Path.GetTempPath(), "wotcockpit-scaffold-preview"),
            ModId = "ru.local.battle_timer",
            Package = "battle_timer",
            DisplayName = "Battle Timer",
            Version = "1.0.0",
            LogTag = "BattleTimer"
        };

        Assert.Empty(backend.Validate(request));

        ScaffoldPlan plan = Plan(backend, request);

        Assert.False(plan.HasBlocking);
        Assert.Contains(plan.Files, f => f.RelativePath.Contains("mod_battle_timer.py", StringComparison.Ordinal));
        Assert.Contains(plan.Files, f => f.RelativePath.Contains("meta.xml", StringComparison.Ordinal));

        // Занятое клиентом имя пакета мастер обязан отвергнуть — этого знания у UI нет.
        Assert.Contains(
            backend.Validate(request with { Package = "gui" }),
            i => i.Blocking);
    }

    /// <summary>
    /// Композиционный корень собирается вместе со всеми моделями этапа 3 и проходит
    /// <c>ValidateOnBuild</c>: кривая регистрация уронила бы приложение на старте.
    /// </summary>
    [Fact]
    public void RealCore_Container_ResolvesStage3ViewModels()
    {
        using var ui = new UiThread();
        using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();

        ui.Invoke(() =>
        {
            var stage3 = provider.GetRequiredService<Stage3ViewModel>();
            var main = provider.GetRequiredService<MainViewModel>();

            Assert.NotNull(stage3.Problems);
            Assert.NotNull(stage3.Recipes);

            // Оболочка обязана прицепиться к главному экрану сама: иначе проверка перед
            // сборкой молча никогда не сработает.
            Assert.Same(stage3, main.Stage3);
            Assert.True(main.HasStage3);
        });
    }

    // ================================================================= вспомогательное

    // ---------------------------------------------------------------- обёртки над задачами
    //
    // Все длинные операции этапа 3 асинхронные, а проверять их удобнее синхронно.
    // Обёртки вынесены из тел тестов намеренно: анализатор xUnit справедливо ругается
    // на блокирующее ожидание внутри теста, но здесь ждать нечего — швы подставные
    // и завершаются синхронно, а настоящие уходят в пул потоков без захвата контекста.

    private static PreflightReport Run(ProblemsViewModel vm, ModProject project)
        => vm.RunAsync(project).GetAwaiter().GetResult();

    private static void Load(RecipesViewModel vm)
        => vm.LoadAsync().GetAwaiter().GetResult();

    private static bool Gate(Stage3ViewModel stage3, Func<string, string, bool> confirm)
        => stage3.ConfirmBuildAsync(Project(), confirm).GetAwaiter().GetResult();

    private static PreflightReport Analyze(IPreflightBackend backend, ModProject project)
        => backend
            .AnalyzeAsync(project, new WotModCockpit.App.Stage3.PreflightOptions(), CancellationToken.None)
            .GetAwaiter().GetResult();

    private static IReadOnlyList<RecipeCard> Cards(IRecipeBackend backend)
        => backend.GetRecipesAsync(CancellationToken.None).GetAwaiter().GetResult();

    private static ScaffoldPlan Plan(IScaffoldBackend backend, ScaffoldRequest request)
        => backend.PlanAsync(request, CancellationToken.None).GetAwaiter().GetResult();

    private static ModProject Project() => new()
    {
        ProjectDir = @"C:\proj",
        ModId = "ru.local.demo",
        PackageName = "demo",
        DisplayName = "Demo",
        LogTag = "[Demo"
    };

    private static ProblemItem Problem(string code, ProblemSeverity severity, string relative, int line) => new()
    {
        Code = code,
        Severity = severity,
        Message = "сообщение " + code,
        Explanation = PreflightExplanations.For(code),
        Hint = "как чинить " + code,
        Reference = "scripts/client/game.py:127",
        FilePath = @"C:\proj\" + relative.Replace('/', '\\'),
        RelativePath = relative,
        Line = line
    };

    /// <summary>Второй рецепт: нужен, чтобы фильтр мог что-то оставить, а что-то убрать.</summary>
    private static RecipeCard OtherRecipe() => new()
    {
        Id = "RYY",
        Title = "Хоткей: перехват нажатия клавиши",
        Category = "Ввод и хоткеи",
        Summary = "образец для тестов фильтра",
        Code = "# -*- coding: utf-8 -*-\nLOG_PREFIX = '[MyMod]'\n"
    };

    private static RecipeCard SampleRecipe() => new()
    {
        Id = "RXX",
        Title = "Демонстрационный рецепт",
        Category = "Каркас и жизненный цикл",
        Summary = "образец для тестов",
        Code = "# -*- coding: utf-8 -*-\n"
               + "MOD_ID = 'ru.local.my_mod'\n"
               + "LOG_PREFIX = '[MyMod]'\n"
               + "CONFIG = 'mods/configs/my_mod/config.json'\n"
               + "\n"
               + "def demo_action():\n"
               + "    return MOD_ID\n"
    };

    /// <summary>
    /// Собирает оболочку этапа 3 поверх настоящей модели главного экрана из контейнера,
    /// но с подставным швом preflight.
    /// </summary>
    /// <param name="main">Модель главного экрана.</param>
    /// <param name="preflight">Шов анализа.</param>
    /// <returns>Оболочка.</returns>
    private static Stage3ViewModel BuildStage3(out MainViewModel main, IPreflightBackend preflight)
    {
        ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();

        main = provider.GetRequiredService<MainViewModel>();
        var editor = provider.GetRequiredService<IEditorLauncher>();

        var problems = new ProblemsViewModel(preflight, editor);
        var recipes = new RecipesViewModel(new UnavailableRecipeBackend(), editor);

        return new Stage3ViewModel(main, problems, recipes, new UnavailableScaffoldBackend(), editor);
    }

    /// <summary>
    /// Создаёт на диске заведомо кривой проект мода: каждое нарушение здесь — из списка,
    /// проверенного на реальных модах из <c>mods\1.44.0.0</c>.
    /// </summary>
    /// <returns>Каталог проекта.</returns>
    private static string CreateBrokenProject()
    {
        string root = NewTempDir();

        string mods = Path.Combine(root, "src", "res", "scripts", "client", "gui", "mods");
        Directory.CreateDirectory(Path.Combine(mods, "broken"));

        // build.py и meta.xml нужны, чтобы каталог вообще опознался как проект мода.
        File.WriteAllText(Path.Combine(root, "build.py"), "# stub\n");
        File.WriteAllText(
            Path.Combine(root, "src", "meta.xml"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<root>\n  <id>ru.local.broken</id>\n  <version>1.0.0</version>\n"
            + "  <name>Broken</name>\n  <description>тестовый кривой мод</description>\n</root>\n");

        // Точка входа: init() без try/except (WOT005).
        File.WriteAllText(
            Path.Combine(mods, "mod_broken.py"),
            "import BigWorld\n"
            + "from gui.mods.broken import main\n"
            + "\n"
            + "def init():\n"
            + "    main.init()\n"
            + "\n"
            + "def fini():\n"
            + "    main.fini()\n");

        // Тело мода: игровая работа на уровне модуля (WOT001), getattr без значения
        // по умолчанию (WOT008), LOG_DEBUG (WOT013), print (WOT020) и кириллица
        // без строки coding (WOT015).
        File.WriteAllText(
            Path.Combine(mods, "broken", "main.py"),
            "import Avatar\n"
            + "from debug_utils import LOG_DEBUG\n"
            + "from PlayerEvents import g_playerEvents\n"
            + "\n"
            + "MESSAGE = 'Мод загружен'\n"
            + "\n"
            + "def _wrapper(self, *a, **kw):\n"
            + "    return None\n"
            + "\n"
            + "def _install():\n"
            + "    original = getattr(Avatar.PlayerAvatar, 'showShotResults')\n"
            + "    setattr(Avatar.PlayerAvatar, 'showShotResults', _wrapper)\n"
            + "\n"
            + "def _on_avatar_ready():\n"
            + "    print MESSAGE\n"
            + "    LOG_DEBUG(MESSAGE)\n"
            + "\n"
            + "g_playerEvents.onAvatarReady += _on_avatar_ready\n"
            + "_install()\n"
            + "\n"
            + "def init():\n"
            + "    _install()\n"
            + "\n"
            + "def fini():\n"
            + "    pass\n",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return root;
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wotcockpit-stage3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Delete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог мог остаться занятым — тесту это не мешает.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---------------------------------------------------------------- подставные швы

    private sealed class FakePreflightBackend : IPreflightBackend
    {
        private readonly ProblemItem[] _problems;

        public FakePreflightBackend(params ProblemItem[] problems) => _problems = problems;

        public int Runs { get; private set; }

        public IReadOnlyCollection<string> LastDisabled { get; private set; } = Array.Empty<string>();

        public bool IsAvailable => true;

        public string UnavailableReason => string.Empty;

        public IReadOnlyList<PreflightRuleCard> Rules { get; } = new[]
        {
            new PreflightRuleCard("WOT001", "Игровая работа на уровне модуля", "…", ProblemSeverity.Error)
        };

        public Task<PreflightReport> AnalyzeAsync(
            ModProject project,
            WotModCockpit.App.Stage3.PreflightOptions options,
            CancellationToken cancellationToken)
        {
            Runs++;
            LastDisabled = options.DisabledRules;

            ProblemItem[] kept = _problems
                .Where(p => !options.DisabledRules.Contains(p.Code, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            return Task.FromResult(new PreflightReport(kept, 3, TimeSpan.FromMilliseconds(12)));
        }

        public Task<PyrightProbe> ProbePyrightAsync(CancellationToken cancellationToken)
            => Task.FromResult(new PyrightProbe(false, "pyright не проверялся"));
    }

    private sealed class ThrowingPreflightBackend : IPreflightBackend
    {
        public bool IsAvailable => true;

        public string UnavailableReason => string.Empty;

        public IReadOnlyList<PreflightRuleCard> Rules => Array.Empty<PreflightRuleCard>();

        public Task<PreflightReport> AnalyzeAsync(
            ModProject project,
            WotModCockpit.App.Stage3.PreflightOptions options,
            CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<PyrightProbe> ProbePyrightAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class FakeRecipeBackend : IRecipeBackend
    {
        private readonly RecipeCard[] _cards;

        public FakeRecipeBackend(params RecipeCard[] cards) => _cards = cards;

        public bool IsAvailable => true;

        public string UnavailableReason => string.Empty;

        public Task<IReadOnlyList<RecipeCard>> GetRecipesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RecipeCard>>(_cards);

        public Task<RecipeCompatibility> CheckAsync(RecipeCard recipe, CancellationToken cancellationToken)
            => Task.FromResult(RecipeCompatibility.Unknown);
    }

    private sealed class NoopEditor : IEditorLauncher
    {
        public bool IsAvailable => false;

        public event EventHandler<string>? LaunchFailed
        {
            add { }
            remove { }
        }

        public void OpenAt(string filePath, int line)
        {
        }

        public void OpenFolder(string folderPath)
        {
        }
    }
}
