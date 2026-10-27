using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.App.Stage4;
using WotModCockpit.Core.Settings;

namespace WotModCockpit.Tests;

/// <summary>
/// Гейт для тестов, которые гоняют код через <c>tools/check_py2_syntax.py</c> шаблона мода.
///
/// <para>Валидатор лежит в соседнем репозитории <c>wot-mod-template</c> (его находит
/// <see cref="RecipeToolchain"/>; переменная <c>WOTCOCKPIT_TEMPLATE_DIR</c> указывает путь
/// явно) и запускается питоном 3. На чистом клоне нет ни того, ни другого, и такие тесты
/// обязаны ПРОПУСКАТЬСЯ с причиной, а не краснеть. Работоспособность цепочки проверяется
/// один раз пробным прогоном на заведомо правильном файле: так гейт не зависит от того,
/// каким именно питоном пользуется <see cref="RecipeToolchain.RunSyntaxCheck"/>.</para>
/// </summary>
internal static class Py27SyntaxGate
{
    private static readonly Lazy<string?> ProbeFailure = new(Probe);

    /// <summary>Пропускает тест, если валидатора нет или его нечем запустить.</summary>
    public static void Require()
    {
        Skip.IfNot(RecipeToolchain.CheckerAvailable,
            $"нет валидатора ({RecipeToolchain.SyntaxChecker}): шаблон мода не найден рядом с кокпитом " +
            "и не указан переменной WOTCOCKPIT_TEMPLATE_DIR");

        string? failure = ProbeFailure.Value;
        Skip.If(failure is not null, failure);
    }

    /// <summary>Пробный прогон валидатора на правильном файле.</summary>
    /// <returns><c>null</c>, если цепочка работает, иначе причина пропуска.</returns>
    private static string? Probe()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cockpit-py27-probe-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "probe.py"), "# -*- coding: utf-8 -*-\nPROBE = 1\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            (int exitCode, string output) = RecipeToolchain.RunSyntaxCheck(dir);
            return exitCode == 0
                ? null
                : $"валидатор не отработал даже на пробном файле (код {exitCode}) — нет рабочего python 3? {output.Trim()}";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            return "не удалось запустить python для tools/check_py2_syntax.py: " + ex.Message;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>
/// ШОВ ЭКРАН → CORE. Дизайнер настроек в приложении обязан работать НАСТОЯЩИМ генератором
/// Core, а не встроенной заглушкой: именно генератор Core проверен круговым рейсом по
/// шаблонам формата <c>modsettings.dat</c> и умеет писать схему участком по маркерам.
///
/// <para>Тесты здесь проверяют ровно стык: что контейнер отдаёт этот генератор, что через
/// него проходит форма экрана, что порождённый код компилируется валидатором Python 2.7,
/// что перегенерация не съедает дописанное человеком и что чужой шаблон переживает
/// дорогу «modsettings.dat → форма экрана → шаблон Core» без потери состава.</para>
/// </summary>
public class CoreSettingsDesignerBackendTests : IDisposable
{
    private readonly string _temp;

    /// <summary>Временное дерево проекта.</summary>
    public CoreSettingsDesignerBackendTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "cockpit-seam-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_temp);
    }

    /// <summary>Убирает временное дерево.</summary>
    public void Dispose()
    {
        try { if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// В боевом контейнере стоит генератор Core, а не встроенная заглушка. Если эта проверка
    /// упадёт, экран продолжит работать — но кодом, который никто не сверял с шаблонами модов.
    /// </summary>
    [Fact]
    public void Container_ResolvesCoreGenerator()
    {
        using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();

        var backend = provider.GetRequiredService<ISettingsDesignerBackend>();

        Assert.IsType<CoreSettingsDesignerBackend>(backend);
        Assert.True(backend.IsAvailable, backend.UnavailableReason);
        Assert.Contains("Core", backend.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Форма экрана, пропущенная через генератор Core, даёт три файла, и оба питоновских
    /// проходят тот же <c>tools/check_py2_syntax.py</c>, что стоит гейтом в build.py.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public void Generate_PassesPython27SyntaxCheck()
    {
        Py27SyntaxGate.Require();

        SettingsCodeResult result = Backend().Generate(Form(), Request());

        Assert.Null(result.Error);

        // Три файла для записи плюс фрагмент вызова для main.py, который в проект не пишется.
        Assert.Equal(4, result.Files.Count);
        Assert.Equal(1, result.Files.Count(f => f.IsSnippet));
        Assert.Contains("Core", result.GeneratorName, StringComparison.Ordinal);

        foreach (SettingsCodeFile file in result.Files.Where(f => !f.IsSnippet))
        {
            string path = Path.Combine(_temp, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content.Replace("\r\n", "\n", StringComparison.Ordinal),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        // Валидатор молча выходит с нулём на пустом каталоге, поэтому сначала убеждаемся,
        // что проверять действительно есть что.
        Assert.Equal(2, Directory.GetFiles(_temp, "*.py", SearchOption.AllDirectories).Length);

        (int exitCode, string output) = RecipeToolchain.RunSyntaxCheck(_temp);

        Assert.True(exitCode == 0, "check_py2_syntax.py отверг код генератора Core:" + Environment.NewLine + output);
    }

    /// <summary>
    /// Схема пишется участком между маркерами: дописанное человеком НИЖЕ закрывающего
    /// маркера переживает перегенерацию формы. Ради этого шов и подключали — встроенный
    /// генератор переписывал файл целиком и правки терял.
    /// </summary>
    [Fact]
    public void Regeneration_KeepsHandWrittenTail()
    {
        SettingsCodeResult first = Backend().Generate(Form(), Request());
        SettingsCodeFile schema = first.Files.Single(f => f.RelativePath.EndsWith("settings_schema.py", StringComparison.Ordinal));

        string path = Path.Combine(_temp, schema.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, schema.Content + "\n\nMY_OWN_CONSTANT = 42\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // Форма изменилась: в ней стало на контрол больше.
        SettingsFormSpec changed = Form() with
        {
            SettingsVersion = 2,
            Column2 =
            [
                .. Form().Column2,
                new SettingsControlSpec
                {
                    Type = SettingsControlType.CheckBox,
                    Text = "Новая галочка",
                    VarName = "brandNew",
                    Value = false
                }
            ]
        };

        SettingsCodeResult second = Backend().Generate(changed, Request());
        string regenerated = second.Files
            .Single(f => f.RelativePath.EndsWith("settings_schema.py", StringComparison.Ordinal)).Content;

        Assert.Contains("MY_OWN_CONSTANT = 42", regenerated, StringComparison.Ordinal);
        Assert.Contains("'brandNew'", regenerated, StringComparison.Ordinal);
        Assert.Contains("SETTINGS_VERSION = 2", regenerated, StringComparison.Ordinal);
    }

    /// <summary>
    /// Форма читается из проекта в обе стороны: и из <c>settings.design.json</c>, и —
    /// когда документа нет — прямо из сгенерированного <c>settings_schema.py</c>.
    /// Второе умеет только Core, встроенный импортёр так не умел.
    /// </summary>
    [Fact]
    public void Import_ReadsDesignFile_AndFallsBackToSchemaModule()
    {
        SettingsCodeResult generated = Backend().Generate(Form(), Request());

        foreach (SettingsCodeFile file in generated.Files)
        {
            string path = Path.Combine(_temp, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        SettingsImportResult fromDesign = Backend().ImportFromProject(_temp);

        Assert.Null(fromDesign.Error);
        Assert.NotNull(fromDesign.Form);
        Assert.Equal("ru.local.hello_tanker", fromDesign.Form!.Linkage);
        Assert.Equal(Form().Column1.Count, fromDesign.Form.Column1.Count);
        Assert.Equal(Form().Column2.Count, fromDesign.Form.Column2.Count);

        // Убираем документ дизайнера — форма обязана прочитаться из кода мода.
        File.Delete(Path.Combine(_temp, SettingsDesignerService.DesignFileName));

        SettingsImportResult fromCode = Backend().ImportFromProject(_temp);

        Assert.Null(fromCode.Error);
        Assert.NotNull(fromCode.Form);
        Assert.EndsWith("settings_schema.py", fromCode.Source, StringComparison.Ordinal);
        Assert.Equal(
            Form().AllControls.Count(c => c.VarName.Length > 0),
            fromCode.Form!.AllControls.Count(c => c.VarName.Length > 0));
    }

    /// <summary>
    /// Каждый шаблон файла состояния проходит дорогу «JSON из modsettings.dat → форма
    /// экрана → шаблон Core» без потери состава: те же типы, те же ключи, в том же порядке.
    /// Файл при этом только читается. Здесь — на синтетической фикстуре, разложенной во
    /// временный каталог под настоящим именем.
    /// </summary>
    [Fact]
    public void EverySampleTemplate_SurvivesTheSeam()
    {
        int checkedTemplates = AssertEveryTemplateSurvivesTheSeam(SettingsTestData.CopyStateTo(_temp));

        Assert.Equal(SettingsTestData.Linkages.Count, checkedTemplates);
    }

    /// <summary>
    /// ЖИВЫЕ ДАННЫЕ. То же для <c>modsettings.dat</c> этой машины: шаблоны всех установленных
    /// модов переживают шов, а сам файл — настройки игрока — не меняется.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void EveryLiveTemplate_SurvivesTheSeam()
    {
        string path = SettingsTestData.RequireLiveState();

        int checkedTemplates = AssertEveryTemplateSurvivesTheSeam(path);

        Skip.If(checkedTemplates == 0, $"в {path} нет ни одного шаблона — моды с ModsSettingsAPI не установлены");
    }

    /// <summary>Прогоняет через шов каждый шаблон файла состояния и сверяет, что файл не тронут.</summary>
    /// <param name="path">Файл состояния.</param>
    /// <returns>Сколько шаблонов проверено.</returns>
    private static int AssertEveryTemplateSurvivesTheSeam(string path)
    {
        var reader = new LiveModSettingsReader();
        Assert.True(reader.Read(path), reader.Error);

        long size = new FileInfo(path).Length;
        DateTime stamp = new FileInfo(path).LastWriteTimeUtc;
        int checkedTemplates = 0;

        // Исходные шаблоны читаем из файла сами: сравнивать надо с тем, что лежит на диске,
        // а не с тем, что уже прошло через читатель кокпита.
        var section = (JsonObject)JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!["templates"]!;

        foreach (LiveModTemplate live in reader.Templates)
        {
            SettingsImportResult imported = reader.ToForm(live);
            Assert.Null(imported.Error);

            SettingsTemplate core = TemplateJson.Parse(
                Backend().RenderTemplateJson(imported.Form!),
                live.Linkage);

            var original = (JsonObject)section[live.Linkage]!;

            string[] liveTypes = [.. Types(original, "column1"), .. Types(original, "column2")];
            string[] seamTypes =
            [
                .. core.Column1.Select(c => c.TypeName),
                .. core.Column2.Select(c => c.TypeName)
            ];

            Assert.Equal(liveTypes, seamTypes);

            string[] liveNames = [.. Names(original, "column1"), .. Names(original, "column2")];
            string[] seamNames =
            [
                .. core.Column1.Where(c => c.VarName is { Length: > 0 }).Select(c => c.VarName!),
                .. core.Column2.Where(c => c.VarName is { Length: > 0 }).Select(c => c.VarName!)
            ];

            Assert.Equal(liveNames, seamNames);
            Assert.Equal(live.DisplayName, core.ModDisplayName);

            checkedTemplates++;
        }

        // Файл состояния — настройки игрока: кокпит его только читает.
        Assert.Equal(size, new FileInfo(path).Length);
        Assert.Equal(stamp, new FileInfo(path).LastWriteTimeUtc);

        return checkedTemplates;
    }

    /// <summary>
    /// Валидатор Core доносит до экрана то, чего экран сам знать не может, и НЕ повторяет
    /// то, о чём экран уже сказал своими словами.
    /// </summary>
    [Fact]
    public void Validate_AddsCoreRules_WithoutRepeatingTheDesigner()
    {
        SettingsFormSpec form = Form() with
        {
            Column1 =
            [
                .. Form().Column1,

                // Дубль ключа: об этом экран говорит сам (MSA016) — повтора быть не должно.
                new SettingsControlSpec
                {
                    Type = SettingsControlType.CheckBox,
                    Text = "Дубль",
                    VarName = "showGreeting",
                    Value = true
                },

                // Подпись длиннее, чем влезает в колонку: об этом экран не знает (MSA012).
                new SettingsControlSpec
                {
                    Type = SettingsControlType.CheckBox,
                    Text = new string('я', TemplateLimits.MaxTextLength + 10),
                    VarName = "veryLongText",
                    Value = true
                }
            ]
        };

        IReadOnlyList<SettingsIssue> issues = Backend().Validate(form);
        string all = string.Join(Environment.NewLine, issues.Select(i => i.Message));

        Assert.Contains(TemplateRules.LongText, all, StringComparison.Ordinal);
        Assert.DoesNotContain(TemplateRules.DuplicateVarName, all, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ вспомогательное

    private static CoreSettingsDesignerBackend Backend() => new(new SettingsDesignerService());

    private SettingsCodeRequest Request() => new()
    {
        ProjectDir = _temp,
        SrcDirRelative = "src",
        PackageName = "hello_tanker",
        LogTag = "HelloTanker",
        ModId = "ru.local.hello_tanker",
        ConfigRelativePath = "mods/configs/hello_tanker/settings.json"
    };

    /// <summary>Форма из типов, которые чаще всего встречаются в реальных шаблонах, плюс заголовок.</summary>
    private static SettingsFormSpec Form() => new()
    {
        Linkage = "ru.local.hello_tanker",
        ModDisplayName = "Hello Tanker",
        SettingsVersion = 1,
        HasEnabled = true,
        EnabledDefault = true,
        Column1 =
        [
            new SettingsControlSpec { Type = SettingsControlType.Label, Text = "Основное" },
            new SettingsControlSpec
            {
                Type = SettingsControlType.CheckBox,
                Text = "Показывать приветствие",
                Tooltip = "{HEADER}Приветствие{/HEADER}{BODY}Сообщение в ангаре{/BODY}",
                VarName = "showGreeting",
                Value = true
            },
            new SettingsControlSpec
            {
                Type = SettingsControlType.Slider,
                Text = "Задержка, с",
                VarName = "delay",
                Value = 3d,
                Minimum = 1,
                Maximum = 10,
                SnapInterval = 1,
                Format = "{{value}} с",
                Width = 200
            }
        ],
        Column2 =
        [
            new SettingsControlSpec
            {
                Type = SettingsControlType.Dropdown,
                Text = "Где показывать",
                VarName = "place",
                Value = 1L,
                Options = [new SettingsOptionSpec("В центре"), new SettingsOptionSpec("Сверху")],
                Width = 200
            },
            new SettingsControlSpec
            {
                Type = SettingsControlType.HotKey,
                Text = "Клавиша",
                VarName = "hotKey",
                Value = new[] { -2, 61 }
            }
        ]
    };

    private static IEnumerable<string> Types(JsonObject template, string column) =>
        (template[column] as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => o["type"]?.GetValue<string>() ?? string.Empty);

    private static IEnumerable<string> Names(JsonObject template, string column) =>
        (template[column] as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => o["varName"]?.GetValue<string>() ?? string.Empty)
            .Where(n => n.Length > 0);
}
