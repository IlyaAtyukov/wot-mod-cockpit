using System.Diagnostics;
using System.Text;
using WotModCockpit.Core.Settings;

namespace WotModCockpit.Tests;

/// <summary>
/// ГЛАВНЫЙ ТЕСТ ДИЗАЙНЕРА НАСТРОЕК.
///
/// <para>Сгенерированный модуль едет в чужую игру и компилируется УЖЕ ВНУТРИ КЛИЕНТА:
/// синтаксическая ошибка в нём не остановит сборку, а превратится в одну строку ERROR
/// в <c>python.log</c> у игрока. Поэтому каждый образец формы прогоняется через тот же
/// самый <c>tools/check_py2_syntax.py</c>, который стоит гейтом в <c>build.py</c>.</para>
///
/// <para>И этого мало: валидный синтаксис не означает, что напечатанный словарь совпадает
/// с задуманным шаблоном. Поэтому второй тест ЗАПУСКАЕТ сгенерированный модуль и сравнивает
/// то, что вернул <c>build_template()</c>, с каноническим JSON модели. Питон, на котором это
/// исполняется, — третий (двойки на машине нет), но сгенерированный код специально написан
/// на подмножестве, законном в обоих: ни <c>print</c>-оператора, ни голых <c>except</c>,
/// только литералы и <c>u'…'</c>.</para>
/// </summary>
public class SettingsCodeGeneratorSyntaxTests : IDisposable
{
    private readonly string _temp;

    /// <summary>Создаёт временный каталог для сгенерированных модулей.</summary>
    public SettingsCodeGeneratorSyntaxTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "cockpit-settings-" + Guid.NewGuid().ToString("N")[..8]);
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
            // Временный каталог; не удалился — не беда.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Каждая из пяти форм даёт пару модулей, которые проходят валидатор Python 2.7.
    /// Проверяется весь каталог разом — ровно так же, как это делает сборка мода.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public void EverySample_PassesPython27SyntaxCheck()
    {
        Skip.IfNot(RecipeToolchain.CheckerAvailable, RecipeToolchain.MissingReason);

        int files = 0;
        foreach ((string name, SettingsTemplate template) in SettingsDesignSamples.All())
        {
            string dir = Path.Combine(_temp, name);
            Directory.CreateDirectory(dir);

            GeneratedSettingsCode code = SettingsCodeGenerator.Generate(template, SettingsDesignSamples.Options());
            Write(Path.Combine(dir, code.SchemaFileName), code.SchemaModuleCode);
            Write(Path.Combine(dir, code.UserFileName), code.UserModuleCode);
            files += 2;
        }

        (int exitCode, string output) = RecipeToolchain.RunSyntaxCheck(_temp);

        Assert.True(exitCode == 0, $"check_py2_syntax.py вернул {exitCode} на {files} файлах:\n{output}");
        Assert.DoesNotContain("[FAIL]", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Сам встроенный ресурс-каркас — валидный Python 2.7 и без подстановок. Иначе первая же
    /// правка каркаса ломала бы генератор незаметно: подстановка токенов синтаксис не чинит.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public void RawTemplateResource_PassesPython27SyntaxCheck()
    {
        Skip.IfNot(RecipeToolchain.CheckerAvailable, RecipeToolchain.MissingReason);

        string path = Path.Combine(_temp, "settings.py");
        Write(path, SettingsCodeGenerator.RawUserModuleTemplate);

        (int exitCode, string output) = RecipeToolchain.RunSyntaxCheck(path);
        Assert.True(exitCode == 0, $"каркас настроек не проходит валидатор:\n{output}");
    }

    /// <summary>
    /// Экзотический, но допустимый паспорт проекта не ломает ни строковые литералы,
    /// ни отступы.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public void UnusualProjectIdentity_StillProducesValidPython()
    {
        Skip.IfNot(RecipeToolchain.CheckerAvailable, RecipeToolchain.MissingReason);

        var options = new SettingsCodeOptions
        {
            PackageName = "very_long_mod_name_42",
            LogTag = "VeryLongModName42",
            ConfigRelativePath = "mods/configs/very_long_mod_name_42/settings.json"
        };

        GeneratedSettingsCode code = SettingsCodeGenerator.Generate(SettingsDesignSamples.TrickyText(), options);
        Write(Path.Combine(_temp, code.SchemaFileName), code.SchemaModuleCode);
        Write(Path.Combine(_temp, code.UserFileName), code.UserModuleCode);

        (int exitCode, string output) = RecipeToolchain.RunSyntaxCheck(_temp);
        Assert.True(exitCode == 0, $"check_py2_syntax.py вернул {exitCode}:\n{output}");
    }

    /// <summary>
    /// ЗАПУСК сгенерированного модуля: <c>build_template(DEFAULTS)</c> обязан вернуть ровно тот
    /// шаблон, который лежит в модели, а <c>DEFAULTS</c> — ровно тот словарь, который
    /// ModsSettingsAPI выведет из шаблона (<c>getSettingsFromTemplate</c>).
    ///
    /// <para>Это и есть проверка «код делает то, что задумано», а не «код компилируется».
    /// Сравнение идёт по каноническому JSON — по тому же представлению, по которому
    /// <c>compareTemplates</c> решает, сбрасывать ли настройки игрока.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Python")]
    public void GeneratedModule_ProducesExactlyTheModelledTemplate()
    {
        string python = TestPython.Require();

        string probe = Path.Combine(_temp, "probe.py");
        File.WriteAllText(probe, ProbeScript, new UTF8Encoding(false));

        foreach ((string name, SettingsTemplate template) in SettingsDesignSamples.All())
        {
            string dir = Path.Combine(_temp, "run_" + name);
            Directory.CreateDirectory(dir);

            GeneratedSettingsCode code = SettingsCodeGenerator.Generate(template, SettingsDesignSamples.Options());
            string modulePath = Path.Combine(dir, code.SchemaFileName);
            Write(modulePath, code.SchemaModuleCode);

            (int exitCode, string stdout, string stderr) = RunPython(python, probe, modulePath);
            Assert.True(exitCode == 0, $"[{name}] сгенерированный модуль не исполняется:\n{stderr}");

            string[] parts = stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(parts.Length >= 3, $"[{name}] проба вернула {parts.Length} строк:\n{stdout}");

            Assert.Equal(TemplateJson.ToCanonicalJson(template), parts[0]);
            Assert.Equal(CanonicalDefaults(template), parts[1]);
            Assert.Equal(template.Linkage, parts[2]);
        }
    }

    /// <summary>
    /// Отсутствие ключа в сохранённых значениях не роняет <c>build_template</c>: именно так
    /// выглядит жизнь после добавления контрола без поднятия <c>settingsVersion</c>.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Python")]
    public void GeneratedModule_SurvivesMissingSavedKeys()
    {
        string python = TestPython.Require();

        string probe = Path.Combine(_temp, "probe_partial.py");
        File.WriteAllText(probe, PartialProbeScript, new UTF8Encoding(false));

        SettingsTemplate template = SettingsDesignSamples.AllControlTypes();
        GeneratedSettingsCode code = SettingsCodeGenerator.Generate(template, SettingsDesignSamples.Options());
        string modulePath = Path.Combine(_temp, "partial_" + code.SchemaFileName);
        Write(modulePath, code.SchemaModuleCode);

        (int exitCode, string stdout, string stderr) = RunPython(python, probe, modulePath);

        Assert.True(exitCode == 0, $"build_template упал на неполном словаре значений:\n{stderr}");
        // Значение, которое в словаре БЫЛО, доехало; остальные взяты из DEFAULTS.
        Assert.Contains("\"opacity\":11", stdout, StringComparison.Ordinal);
        Assert.Contains("\"showGreeting\":true", stdout, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ вспомогательное

    /// <summary>Канонический JSON словаря умолчаний, который выведет API из шаблона.</summary>
    private static string CanonicalDefaults(SettingsTemplate template) =>
        SettingValue.Map(template.BuildDefaults()).ToCanonicalJson();

    private static void Write(string path, string text) =>
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>
    /// Запускает пробу тем же Python 3, что и остальные тесты (<see cref="TestPython"/>), а не
    /// первым <c>python</c> из PATH: у модмейкера там нередко стоит 2.7, на котором проба
    /// (<c>importlib.util</c>) падает, хотя Python 3 на машине есть.
    /// </summary>
    /// <param name="python">Интерпретатор из <see cref="TestPython.Require"/>.</param>
    /// <param name="script">Проба.</param>
    /// <param name="modulePath">Сгенерированный модуль.</param>
    /// <returns>Код возврата, stdout, stderr.</returns>
    private static (int ExitCode, string StdOut, string StdErr) RunPython(string python, string script, string modulePath) =>
        Run(python, [script, modulePath]);

    private static (int ExitCode, string StdOut, string StdErr) Run(string exe, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        // Без этого вывод с кириллицей на русской Windows уедет в cp1251 и сравнение развалится.
        TestPython.UseUtf8(psi);

        using Process p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        return (p.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Проба: загружает сгенерированный модуль и печатает три строки — канонический JSON
    /// шаблона, канонический JSON <c>DEFAULTS</c> и <c>LINKAGE</c>.
    /// </summary>
    private const string ProbeScript = """
        # -*- coding: utf-8 -*-
        import importlib.util
        import json
        import sys

        spec = importlib.util.spec_from_file_location('generated_schema', sys.argv[1])
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)

        def canonical(obj):
            return json.dumps(obj, sort_keys=True, ensure_ascii=False, separators=(',', ':'))

        sys.stdout.write(canonical(module.build_template(module.DEFAULTS)) + '\n')
        sys.stdout.write(canonical(module.DEFAULTS) + '\n')
        sys.stdout.write(module.LINKAGE + '\n')
        """;

    /// <summary>Проба на НЕПОЛНОМ словаре значений: один ключ есть, остальных нет.</summary>
    private const string PartialProbeScript = """
        # -*- coding: utf-8 -*-
        import importlib.util
        import json
        import sys

        spec = importlib.util.spec_from_file_location('generated_schema', sys.argv[1])
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)

        template = module.build_template({'opacity': 11})

        settings = {}
        if 'enabled' in template:
            settings['enabled'] = template['enabled']
        for column in ('column1', 'column2'):
            for component in template.get(column, []):
                if 'varName' in component and 'value' in component:
                    settings[component['varName']] = component['value']

        sys.stdout.write(json.dumps(settings, sort_keys=True, ensure_ascii=False, separators=(',', ':')) + '\n')
        """;
}
