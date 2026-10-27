using System.Text;
using System.Text.Json;
using WotModCockpit.Core.Lint;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты линтера pyright.
///
/// <para>Разбор JSON проверяется на зафиксированном выводе и не требует Node.js.
/// Настоящий запуск помечен <see cref="SkippableFactAttribute"/>: без Node фича
/// недоступна, и это штатное состояние, а не падение тестов.</para>
///
/// <para>Числа, зафиксированные тестом <see cref="Run_OnReferenceMod_IsQuiet"/>,
/// получены замером на эталонном моде шаблона (1351 строка): без extraPaths — 19
/// диагностик, с extraPaths — 6, плюс заглушки нативных модулей — 3, после отсева
/// py2-встроенных имён — 0.</para>
/// </summary>
public class PyrightRunnerTests : IDisposable
{
    private readonly string _temp;

    /// <summary>Создаёт временный каталог.</summary>
    public PyrightRunnerTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "cockpit-pyright-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_temp);
    }

    /// <summary>Убирает временный каталог.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог.
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ разбор JSON

    /// <summary>Диагностики разбираются, строки переводятся в 1-based.</summary>
    [Fact]
    public void Parse_ReadsDiagnostics()
    {
        const string json = """
            {
              "version": "1.1.413",
              "generalDiagnostics": [
                {
                  "file": "C:\\proj\\main.py",
                  "severity": "error",
                  "message": "Import \"nonexistent\" could not be resolved",
                  "range": { "start": { "line": 41, "character": 7 }, "end": { "line": 41, "character": 18 } },
                  "rule": "reportMissingImports"
                }
              ],
              "summary": { "errorCount": 1 }
            }
            """;

        PyrightResult result = PyrightRunner.Parse(json);

        Assert.Equal(PyrightRunState.Completed, result.State);
        PyrightDiagnostic d = Assert.Single(result.Diagnostics);
        Assert.Equal(@"C:\proj\main.py", d.File);
        Assert.Equal(42, d.Line);
        Assert.Equal(8, d.Column);
        Assert.Equal(PyrightSeverity.Error, d.Severity);
        Assert.Equal("reportMissingImports", d.Rule);
    }

    /// <summary>
    /// Имена, которых нет в Python 3, но есть в Python 2.7 клиента, отсеиваются:
    /// иначе каждый честный <c>isinstance(x, unicode)</c> становится «ошибкой».
    /// </summary>
    [Fact]
    public void Parse_SuppressesPython2Builtins()
    {
        string json = BuildJson(
            ("reportUndefinedVariable", "\"unicode\" is not defined"),
            ("reportUndefinedVariable", "\"basestring\" is not defined"),
            ("reportUndefinedVariable", "\"xrange\" is not defined"),
            ("reportUndefinedVariable", "\"my_typo\" is not defined"));

        PyrightResult result = PyrightRunner.Parse(json);

        // Настоящая опечатка обязана выжить — ради неё правило и оставлено включённым.
        PyrightDiagnostic d = Assert.Single(result.Diagnostics);
        Assert.Contains("my_typo", d.Message, StringComparison.Ordinal);
        Assert.Equal(3, result.SuppressedCount);
    }

    /// <summary>Отсев не трогает другие правила: <c>unicode</c> в тексте импорта — не повод молчать.</summary>
    [Fact]
    public void Parse_SuppressionIsLimitedToUndefinedVariable()
    {
        string json = BuildJson(("reportMissingImports", "Import \"unicode\" could not be resolved"));

        PyrightResult result = PyrightRunner.Parse(json);

        Assert.Single(result.Diagnostics);
        Assert.Equal(0, result.SuppressedCount);
    }

    /// <summary>
    /// «Import "gui.modsListApi" could not be resolved» на API ЧУЖОГО мода отсеивается.
    ///
    /// <para>Замер, ради которого это сделано: отрендеренный каталог рецептов давал
    /// ровно 5 таких предупреждений — <c>gui.modsListApi</c> (R14, три места) и
    /// <c>gui.modsSettingsApi</c> (R15, два места), — и все пять ложные: этих модулей
    /// в клиенте нет и быть не может, оба рецепта потому и импортируют их под
    /// <c>except ImportError</c>. Заглушкой <c>.pyi</c> вопрос не решается: пакет
    /// <c>gui</c> в клиенте настоящий, и заглушка заслонила бы его целиком.</para>
    /// </summary>
    [Fact]
    public void Parse_SuppressesForeignModApiImports()
    {
        string json = BuildJson(
            ("reportMissingImports", "Import \"gui.modsListApi\" could not be resolved"),
            ("reportMissingImports", "Import \"gui.modsSettingsApi\" could not be resolved"),
            ("reportMissingImports", "Import \"xfw.loader\" could not be resolved"),
            ("reportMissingImports", "Import \"gui.Scaleform.typo\" could not be resolved"));

        PyrightResult result = PyrightRunner.Parse(json);

        // Опечатка в НАСТОЯЩЕМ модуле клиента обязана выжить: она и есть сигнал.
        PyrightDiagnostic d = Assert.Single(result.Diagnostics);
        Assert.Contains("gui.Scaleform.typo", d.Message, StringComparison.Ordinal);
        Assert.Equal(3, result.SuppressedCount);
    }

    /// <summary>
    /// Импорт СОБСТВЕННОГО пакета мода не считается ошибкой, а опечатка в нём — считается.
    ///
    /// <para>Измерено: <c>gui.mods.&lt;пакет&gt;</c> не резолвится ни при каком порядке
    /// <c>extraPaths</c> — <c>gui</c> в клиенте обычный пакет с <c>__init__.py</c>, pyright
    /// уходит туда и в каталог мода не заглядывает. Но в игре такой импорт правильный:
    /// движок монтирует <c>scripts/client</c> из архива мода в <c>sys.path</c>. Поэтому
    /// гасится только доказанное — имена, собранные обходом дерева проекта.</para>
    /// </summary>
    [Fact]
    public void Parse_SuppressesOwnModulesButNotTypos()
    {
        string json = BuildJson(
            ("reportMissingImports", "Import \"gui.mods.battle_clock\" could not be resolved"),
            ("reportMissingImports", "Import \"gui.mods.battle_clok\" could not be resolved"));

        var own = new HashSet<string>(StringComparer.Ordinal) { "gui.mods.battle_clock", "gui.mods.battle_clock.main" };

        PyrightResult result = PyrightRunner.Parse(json, default, string.Empty, own);

        PyrightDiagnostic d = Assert.Single(result.Diagnostics);
        Assert.Contains("battle_clok\"", d.Message, StringComparison.Ordinal);
        Assert.Equal(1, result.SuppressedCount);

        // Без списка модулей гасить нечего — поведение прежнее.
        Assert.Equal(2, PyrightRunner.Parse(json).Diagnostics.Count);
    }

    /// <summary>
    /// Как запускать найденный pyright. Три разных случая, и все три встречаются:
    /// <c>index.js</c> нужен Node, <c>pyright.exe</c> от pip запускается сам, а .cmd/.bat
    /// Win32 CreateProcess не запускает вовсе — им обязателен <c>cmd.exe /c</c>.
    /// </summary>
    [Fact]
    public void BuildCommandLine_PicksLauncherByExtension()
    {
        var runner = new PyrightRunner();

        (string exe, string[] args) = runner.BuildCommandLine(@"C:\p\index.js", ["--outputjson"]);
        Assert.Equal("node", exe);
        Assert.Equal([@"C:\p\index.js", "--outputjson"], args);

        (exe, args) = runner.BuildCommandLine(@"C:\p\pyright.cmd", ["--outputjson"]);
        Assert.Equal("cmd.exe", exe);
        Assert.Equal(["/c", @"C:\p\pyright.cmd", "--outputjson"], args);

        (exe, args) = runner.BuildCommandLine(@"C:\p\pyright.exe", ["--outputjson"]);
        Assert.Equal(@"C:\p\pyright.exe", exe);
        Assert.Equal(["--outputjson"], args);
    }

    /// <summary>Явный путь к node подставляется вместо <c>node</c> из PATH.</summary>
    [Fact]
    public void BuildCommandLine_UsesExplicitNode()
    {
        var runner = new PyrightRunner(nodePath: @"D:\node\node.exe");

        (string exe, _) = runner.BuildCommandLine(@"C:\p\index.js", []);

        Assert.Equal(@"D:\node\node.exe", exe);
    }

    /// <summary>
    /// Заданный в настройках, но несуществующий путь объясняется, а не проваливается
    /// молча в автопоиск: настройку заполняли осознанно.
    /// </summary>
    [Fact]
    public async Task Run_ExplicitPathMissing_ExplainsSetting()
    {
        var runner = new PyrightRunner(Path.Combine(_temp, "no-such-pyright.cmd"));
        Directory.CreateDirectory(Path.Combine(_temp, "src"));

        Assert.True(runner.ExplicitPathMissing);

        PyrightResult result = await runner.RunAsync([Path.Combine(_temp, "src")]);

        Assert.Equal(PyrightRunState.NotInstalled, result.State);
        Assert.Contains("pyrightPath", result.Message, StringComparison.Ordinal);
    }

    /// <summary>Пустой путь в настройках возвращает автопоиск — и это не «путь не найден».</summary>
    [Fact]
    public void ExplicitPathMissing_IsFalseWhenSettingIsEmpty()
    {
        Assert.False(new PyrightRunner().ExplicitPathMissing);
        Assert.False(new PyrightRunner("   ").ExplicitPathMissing);
    }

    /// <summary>Пустой список диагностик — это успех, а не сбой.</summary>
    [Fact]
    public void Parse_EmptyDiagnostics_IsSuccess()
    {
        PyrightResult result = PyrightRunner.Parse("""{"version":"1.1.413","generalDiagnostics":[]}""");

        Assert.Equal(PyrightRunState.Completed, result.State);
        Assert.Empty(result.Diagnostics);
        Assert.Contains("замечаний нет", result.Message, StringComparison.Ordinal);
    }

    /// <summary>Мусор перед JSON не мешает: pyright иногда печатает предупреждения выше.</summary>
    [Fact]
    public void Parse_ToleratesLeadingNoise()
    {
        PyrightResult result = PyrightRunner.Parse("warning: something\n{\"generalDiagnostics\":[]}");

        Assert.Equal(PyrightRunState.Completed, result.State);
    }

    /// <summary>Не-JSON — внятный отказ, а не исключение.</summary>
    [Fact]
    public void Parse_NonJson_Fails()
    {
        Assert.Equal(PyrightRunState.Failed, PyrightRunner.Parse("command not found").State);
    }

    // ------------------------------------------------------------------ конфиг

    /// <summary>
    /// Конфиг ставит режим, который единственный даёт сигнал без шума на коде Python 2:
    /// <c>typeCheckingMode off</c> и <c>pythonVersion 3.8</c>, а НЕ 2.7 (см. комментарий
    /// к <see cref="PyrightConfigWriter"/>: под 2.7 typeshed теряет все встроенные имена).
    /// </summary>
    [Fact]
    public void ConfigWriter_UsesLowNoiseSettings()
    {
        string src = Path.Combine(_temp, "src");
        Directory.CreateDirectory(src);

        string configPath = PyrightConfigWriter.Write(Path.Combine(_temp, "ws"), [src], null);

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(configPath));
        JsonElement root = doc.RootElement;

        Assert.Equal("off", root.GetProperty("typeCheckingMode").GetString());
        Assert.Equal("3.8", root.GetProperty("pythonVersion").GetString());
        Assert.Equal("none", root.GetProperty("reportMissingModuleSource").GetString());

        // 'include' быть НЕ должно: абсолютный путь там pyright молча игнорирует
        // и анализирует ноль файлов. Каталоги передаются аргументами командной строки.
        Assert.False(root.TryGetProperty("include", out _));
    }

    /// <summary>
    /// Нулевое число проанализированных файлов — это отказ, а не «замечаний нет».
    /// Именно так выглядит неправильно собранный конфиг, и молчаливый «успех» здесь
    /// убедил бы пользователя, что код проверен, хотя его никто не смотрел.
    /// </summary>
    [Fact]
    public void Parse_ZeroFilesAnalyzed_IsFailure()
    {
        PyrightResult result = PyrightRunner.Parse(
            """{"generalDiagnostics":[],"summary":{"filesAnalyzed":0,"errorCount":0}}""");

        Assert.Equal(PyrightRunState.Failed, result.State);
        Assert.Contains("ни одного файла", result.Message, StringComparison.Ordinal);
    }

    /// <summary>Каталоги клиента попадают в extraPaths настоящими путями Windows.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void ConfigWriter_AddsClientSourcesToExtraPaths()
    {
        Skip.IfNot(RealWotSource.Available, $"нет дерева исходников клиента ({RealWotSource.Dir})");

        string src = Path.Combine(_temp, "src");
        Directory.CreateDirectory(src);

        string configPath = PyrightConfigWriter.Write(Path.Combine(_temp, "ws"), [src], RealWotSource.Dir);

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(configPath));
        string[] extra = [.. doc.RootElement.GetProperty("extraPaths").EnumerateArray().Select(e => e.GetString()!)];

        Assert.Equal(3, extra.Length);
        Assert.All(extra, p => Assert.True(Directory.Exists(p), $"extraPaths указывает в никуда: {p}"));
        Assert.Contains(extra, p => p.EndsWith("/client", StringComparison.Ordinal));
        Assert.Contains(extra, p => p.EndsWith("/common", StringComparison.Ordinal));
    }

    /// <summary>Заглушки нативных модулей пишутся: без них 16 ложных «модуль не найден».</summary>
    [Fact]
    public void ConfigWriter_WritesNativeStubs()
    {
        string stubs = Path.Combine(_temp, "stubs");
        int written = PyrightConfigWriter.WriteNativeStubs(stubs);

        Assert.Equal(PyrightConfigWriter.NativeModules.Count, written);
        Assert.True(File.Exists(Path.Combine(stubs, "BigWorld.pyi")));
        Assert.True(File.Exists(Path.Combine(stubs, "ResMgr.pyi")));
        Assert.Contains("__getattr__", File.ReadAllText(Path.Combine(stubs, "BigWorld.pyi")), StringComparison.Ordinal);
    }

    /// <summary>
    /// Список нативных модулей не накрывает настоящие модули клиента. Если бы накрывал,
    /// заглушка сделала бы законным ЛЮБОЕ обращение к ним — например <c>Keys.KEY_ОПЕЧАТКА</c>.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void NativeModules_DoNotShadowRealClientModules()
    {
        Skip.IfNot(RealWotSource.Available, $"нет дерева исходников клиента ({RealWotSource.Dir})");

        string[] shadowed = [.. PyrightConfigWriter.NativeModules
            .Where(m => PyrightConfigWriter.ExistsInClientTree(RealWotSource.Dir, m))];

        Assert.True(shadowed.Length == 0,
            "эти модули существуют в клиенте настоящим питоном, заглушка их спрячет: " + string.Join(", ", shadowed));
    }

    /// <summary>
    /// Проверка присутствия учитывает регистр. Pyright регистр учитывает тоже:
    /// <c>import GUI</c> он в пакет <c>client/gui</c> НЕ резолвит, поэтому нативный
    /// <c>GUI</c> обязан получить заглушку, несмотря на одноимённый (с точностью
    /// до регистра) пакет рядом.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void ExistsInClientTree_IsCaseSensitive()
    {
        Skip.IfNot(RealWotSource.Available, $"нет дерева исходников клиента ({RealWotSource.Dir})");

        Assert.True(PyrightConfigWriter.ExistsInClientTree(RealWotSource.Dir, "gui"));
        Assert.False(PyrightConfigWriter.ExistsInClientTree(RealWotSource.Dir, "GUI"));
        Assert.True(PyrightConfigWriter.ExistsInClientTree(RealWotSource.Dir, "Keys"));
        Assert.False(PyrightConfigWriter.ExistsInClientTree(RealWotSource.Dir, "BigWorld"));
    }

    /// <summary>Даже если список ошибётся, настоящий модуль клиента заглушкой не накрывается.</summary>
    [Fact]
    public void WriteNativeStubs_SkipsModulesPresentInClientTree()
    {
        // Дерево-подделка, где BigWorld вдруг существует настоящим питоном.
        string fakeTree = Path.Combine(_temp, "tree");
        Directory.CreateDirectory(Path.Combine(fakeTree, "client"));
        File.WriteAllText(Path.Combine(fakeTree, "client", "BigWorld.py"), "x = 1\n");

        string stubs = Path.Combine(_temp, "stubs2");
        int written = PyrightConfigWriter.WriteNativeStubs(stubs, fakeTree);

        Assert.False(File.Exists(Path.Combine(stubs, "BigWorld.pyi")));
        Assert.True(File.Exists(Path.Combine(stubs, "ResMgr.pyi")));
        Assert.Equal(PyrightConfigWriter.NativeModules.Count - 1, written);
    }

    // ------------------------------------------------------------------ настоящий запуск

    /// <summary>Отсутствие каталогов с кодом — отказ, а не исключение.</summary>
    [Fact]
    public async Task Run_NoSources_FailsGracefully()
    {
        PyrightResult result = await new PyrightRunner().RunAsync([Path.Combine(_temp, "nope")]);

        Assert.Equal(PyrightRunState.Failed, result.State);
        Assert.NotEmpty(result.Message);
    }

    /// <summary>Отсутствие pyright не роняет приложение и объясняет, что делать.</summary>
    [Fact]
    public async Task Run_WithoutPyright_ReportsNotInstalled()
    {
        var runner = new PyrightRunner(pyrightPath: Path.Combine(_temp, "no-pyright.js"), nodePath: "definitely-not-node");
        string src = Path.Combine(_temp, "src");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "a.py"), "x = 1\n");

        PyrightResult result = await runner.RunAsync([src]);

        Assert.Equal(PyrightRunState.NotInstalled, result.State);
        Assert.Contains("pyright", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Pyright находится там, где он установлен.</summary>
    [SkippableFact]
    public void Locate_FindsInstalledPyright()
    {
        string path = new PyrightRunner().Locate();
        Skip.If(string.IsNullOrEmpty(path), "pyright не установлен на этой машине");

        Assert.True(File.Exists(path));
        Assert.EndsWith("index.js", path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Настоящий запуск ловит настоящую ошибку: несуществующий модуль.</summary>
    [SkippableFact]
    public async Task Run_DetectsMissingImport()
    {
        var runner = new PyrightRunner();
        Skip.If(string.IsNullOrEmpty(runner.Locate()), "pyright не установлен на этой машине");

        string src = Path.Combine(_temp, "src");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(
            Path.Combine(src, "mod.py"),
            "# -*- coding: utf-8 -*-\nfrom totally_nonexistent_module import thing\n\nthing()\n",
            new UTF8Encoding(false));

        PyrightResult result = await runner.RunAsync([src], RealWotSource.Available ? RealWotSource.Dir : null);

        Assert.Equal(PyrightRunState.Completed, result.State);
        Assert.Contains(result.Diagnostics, d => d.Rule == "reportMissingImports");
    }

    /// <summary>
    /// ЗАМЕР ШУМА. На эталонном моде шаблона (1351 строка) правильно настроенный pyright
    /// не выдаёт НИ ОДНОЙ диагностики. Это и есть ответ на вопрос «включать ли фичу
    /// по умолчанию»: да, потому что на исправном коде она молчит.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    [Trait("Needs", "Template")]
    public async Task Run_OnReferenceMod_IsQuiet()
    {
        var runner = new PyrightRunner();
        Skip.If(string.IsNullOrEmpty(runner.Locate()), "pyright не установлен на этой машине");
        Skip.IfNot(RealWotSource.Available, $"нет дерева исходников клиента ({RealWotSource.Dir})");

        string modSource = Path.Combine(RecipeToolchain.TemplateDir, "src", "res", "scripts", "client", "gui", "mods");
        Skip.IfNot(Directory.Exists(modSource), $"нет эталонного мода ({modSource})");

        // Копия без __pycache__: чужой .pyc в дереве анализа не нужен.
        string src = Path.Combine(_temp, "reference");
        CopyPythonTree(modSource, src);

        PyrightResult result = await runner.RunAsync([src], RealWotSource.Dir);

        Assert.Equal(PyrightRunState.Completed, result.State);

        string detail = string.Join('\n', result.Diagnostics.Select(d => $"  {d.Rule}: {d.Display}"));
        Assert.True(result.Diagnostics.Count == 0,
            $"на эталонном моде ожидался нулевой шум, получено {result.Diagnostics.Count}:\n{detail}");
    }

    private static void CopyPythonTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from, "*.py", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(from, file);
            if (relative.Contains("__pycache__", StringComparison.OrdinalIgnoreCase)) continue;

            string dest = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static string BuildJson(params (string Rule, string Message)[] items)
    {
        string entries = string.Join(",", items.Select(i => $$"""
            {
              "file": "C:\\proj\\main.py",
              "severity": "warning",
              "message": {{JsonSerializer.Serialize(i.Message)}},
              "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 1 } },
              "rule": "{{i.Rule}}"
            }
            """));

        return $$"""{"version":"1.1.413","generalDiagnostics":[{{entries}}]}""";
    }
}
