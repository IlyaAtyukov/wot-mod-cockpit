using WotModCockpit.Core.Settings;

namespace WotModCockpit.Tests;

/// <summary>
/// Импорт шаблона: из файла состояния <c>modsettings.dat</c> и из Python-кода мода.
///
/// <para>Отдельная тема этих тестов — ЧЕСТНОСТЬ отказа. Разобрать шаблон из произвольного
/// кода нельзя, и главное требование к импортёру не «понять как можно больше», а «не сделать
/// вид, что понял». Поэтому здесь столько же тестов на отказ, сколько на успех.</para>
/// </summary>
public class TemplateImporterTests
{
    // ================================================================== modsettings.dat

    /// <summary>Все 11 модов фикстуры перечисляются, включая ключи с точками и дефисом.</summary>
    [Fact]
    public void ListLinkages_ReturnsAllEleven()
    {
        Assert.Equal(11, SettingsTestData.Linkages.Count);
        Assert.Contains(SettingsTestData.ReferenceLinkage, SettingsTestData.Linkages);
        Assert.Contains("ru.example.ui-scale", SettingsTestData.Linkages);
    }

    /// <summary>Каждый из 11 шаблонов разбирается без замечаний разбора.</summary>
    [Fact]
    public void EverySampleTemplate_ImportsCleanly()
    {
        foreach (string linkage in SettingsTestData.Linkages)
        {
            TemplateImportResult result = TemplateImporter.FromModSettingsDat(SettingsTestData.StateJson, linkage);

            Assert.True(result.Success, $"{linkage}: {result.Describe()}");
            Assert.Equal(TemplateImportSource.ModSettingsDat, result.Source);
            Assert.Empty(result.Problems);
            Assert.Equal(linkage, result.Template!.Linkage);
            Assert.NotEmpty(result.Template.ModDisplayName);
        }
    }

    /// <summary>Эталонный шаблон фикстуры разбирается поле за полем.</summary>
    [Fact]
    public void ReferenceSampleTemplate_IsParsedFieldByField()
    {
        SettingsTemplate template = TemplateImporter
            .FromModSettingsDat(SettingsTestData.StateJson, SettingsTestData.ReferenceLinkage).Template!;

        Assert.Equal("Пример: напоминания", template.ModDisplayName);
        Assert.True(template.HasEnabled);
        Assert.Null(template.SettingsVersion);
        Assert.Equal(2, template.Column1.Count);
        Assert.Equal(2, template.Column2.Count);

        SettingControl radio = template.Column1[0];
        Assert.Equal(ControlKind.RadioButtonGroup, radio.Kind);
        Assert.Equal("outputMode", radio.VarName);
        Assert.Equal(3, radio.Options.Count);
        Assert.Equal(0, radio.Value!.AsInteger);
        Assert.All(radio.Options, o => Assert.Null(o.Tooltip));
        Assert.StartsWith("{HEADER}", radio.Tooltip, StringComparison.Ordinal);

        SettingControl slider = template.Column2[0];
        Assert.Equal(ControlKind.Slider, slider.Kind);
        Assert.Equal("{{value}} мин.", slider.Format);
        Assert.Equal(150, slider.Width);
        Assert.Equal(1, slider.Minimum!.AsInteger);
        Assert.Equal(14, slider.Maximum!.AsInteger);

        SettingControl hotkey = template.Column2[1];
        Assert.Equal(ControlKind.HotKey, hotkey.Kind);
        Assert.Equal(new[] { 61, HotKeyValue.Ctrl }, HotKeyValue.Flatten(hotkey.Value));
    }

    /// <summary>Мода нет — понятный отказ, а не исключение и не пустой шаблон.</summary>
    [Fact]
    public void UnknownLinkage_FailsWithExplanation()
    {
        TemplateImportResult result = TemplateImporter.FromModSettingsDat(SettingsTestData.StateJson, "нет такого");

        Assert.False(result.Success);
        Assert.Null(result.Template);
        Assert.Contains("11", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>Битый файл состояния — отказ, а не исключение.</summary>
    [Fact]
    public void BrokenState_FailsWithoutThrowing()
    {
        TemplateImportResult result = TemplateImporter.FromModSettingsDat("{ это не json", "x");

        Assert.False(result.Success);
        Assert.NotEmpty(result.Problems);
    }

    /// <summary>Файла нет — отказ с путём в сообщении.</summary>
    [Fact]
    public void MissingFile_FailsWithPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "нет-такого-modsettings.dat");
        TemplateImportResult result = TemplateImporter.FromModSettingsDatFile(path, "x");

        Assert.False(result.Success);
        Assert.Contains(path, result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Секция settings — это «как хочет игрок», и она законно расходится с шаблоном
    /// («как задумал автор»). В фикстуре: metric в шаблоне 4, у игрока 0.
    /// </summary>
    [Fact]
    public void SavedSettings_MayDivergeFromTemplate()
    {
        SettingsTemplate template = TemplateImporter
            .FromModSettingsDat(SettingsTestData.StateJson, "ru.example.stats").Template!;
        var stored = TemplateImporter
            .ReadSavedSettings(SettingsTestData.StateJson, "ru.example.stats")
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        Assert.Equal(4, template.FindByVarName("metric")!.Value!.AsInteger);
        Assert.Equal(0, stored["metric"].AsInteger);
    }

    /// <summary>Фикстура читается и с диска — тем же путём, каким кокпит читает живой файл.</summary>
    [Fact]
    public void SampleFile_ImportsFromDisk()
    {
        TemplateImportResult result =
            TemplateImporter.FromModSettingsDatFile(SettingsTestData.StateCopyPath, SettingsTestData.ReferenceLinkage);

        Assert.True(result.Success, result.Describe());
        Assert.Equal("Пример: напоминания", result.Template!.ModDisplayName);
    }

    /// <summary>
    /// Живой файл этой машины читается тем же кодом. Какие моды стоят у человека, тест не
    /// знает и не предполагает: каждый шаблон из файла обязан импортироваться с диска так же,
    /// как из уже прочитанного текста.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void LiveMachineFile_Imports()
    {
        string path = SettingsTestData.RequireLiveState();

        string json = File.ReadAllText(path);
        IReadOnlyList<string> linkages = TemplateImporter.ListLinkages(json);
        Skip.If(linkages.Count == 0, $"в {path} нет ни одного шаблона — моды с ModsSettingsAPI не установлены");

        foreach (string linkage in linkages)
        {
            TemplateImportResult fromFile = TemplateImporter.FromModSettingsDatFile(path, linkage);
            TemplateImportResult fromText = TemplateImporter.FromModSettingsDat(json, linkage);

            Assert.True(fromFile.Success, $"{linkage}: {fromFile.Describe()}");
            Assert.True(fromText.Success, $"{linkage}: {fromText.Describe()}");
            Assert.Equal(
                TemplateJson.ToCanonicalJson(fromText.Template!),
                TemplateJson.ToCanonicalJson(fromFile.Template!));
        }
    }

    // ================================================================== Python: успех

    /// <summary>Литеральная форма — та, что порождает сам кокпит.</summary>
    [Fact]
    public void LiteralForm_Imports()
    {
        TemplateImportResult result = TemplateImporter.FromPython(LiteralModule);

        Assert.True(result.Success, result.Describe());
        Assert.Equal(TemplateImportSource.PythonLiteral, result.Source);

        SettingsTemplate template = result.Template!;
        Assert.Equal("ru.local.hello_tanker", template.Linkage);
        Assert.Equal("Hello Tanker", template.ModDisplayName);
        Assert.Equal(3, template.SettingsVersion);
        Assert.True(template.HasEnabled);
        Assert.Equal(3, template.Column1.Count);
        Assert.Equal(2, template.Column2.Count);
    }

    /// <summary>Форма через templates.createX — та, что порождает рецепт R15.</summary>
    [Fact]
    public void FactoryForm_Imports()
    {
        TemplateImportResult result = TemplateImporter.FromPython(FactoryModule);

        Assert.True(result.Success, result.Describe());
        Assert.Equal(TemplateImportSource.PythonTemplatesApi, result.Source);
        Assert.Equal(3, result.Template!.Column1.Count);
    }

    /// <summary>
    /// ГЛАВНЫЙ ТЕСТ ИМПОРТЁРА: обе записи одного и того же шаблона дают ОДИН И ТОТ ЖЕ словарь.
    /// Это то самое равенство, ради которого кокпит имеет право писать литералы вместо
    /// вызовов чужого API (и тем самым не ловить WOT018).
    /// </summary>
    [Fact]
    public void LiteralAndFactoryForms_ProduceIdenticalTemplates()
    {
        SettingsTemplate literal = TemplateImporter.FromPython(LiteralModule).Template!;
        SettingsTemplate factory = TemplateImporter.FromPython(FactoryModule).Template!;

        Assert.Equal(TemplateJson.ToCanonicalJson(literal), TemplateJson.ToCanonicalJson(factory));
    }

    /// <summary>Разобранный из кода шаблон валиден.</summary>
    [Fact]
    public void ImportedFromPython_PassesValidation()
    {
        SettingsTemplate template = TemplateImporter.FromPython(LiteralModule).Template!;
        TemplateValidationResult result = TemplateValidator.Validate(template);

        Assert.False(result.HasErrors, result.Describe());
        Assert.Equal(0, result.WarningCount);
    }

    /// <summary>Значения берутся из словаря умолчаний, а не остаются вызовом v('...').</summary>
    [Fact]
    public void DefaultsAreResolved()
    {
        SettingsTemplate template = TemplateImporter.FromPython(LiteralModule).Template!;

        Assert.True(template.FindByVarName("showGreeting")!.Value!.AsBool);
        Assert.Equal(3, template.FindByVarName("delaySec")!.Value!.AsInteger);
        Assert.Equal(new[] { 61, HotKeyValue.Ctrl },
            HotKeyValue.Flatten(template.FindByVarName("toggleKey")!.Value));
    }

    /// <summary>Соседние строковые литералы склеиваются — так пишут длинные тултипы.</summary>
    [Fact]
    public void AdjacentStringLiterals_AreConcatenated()
    {
        SettingsTemplate template = TemplateImporter.FromPython(LiteralModule).Template!;

        Assert.Equal("{HEADER}Приветствие{/HEADER}{BODY}Сообщение в ангаре{/BODY}",
            template.FindByVarName("showGreeting")!.Tooltip);
    }

    /// <summary>generateOptions понимает и голые строки, и пары, и готовые словари.</summary>
    [Fact]
    public void OptionForms_AreNormalized()
    {
        const string module = """
            LINKAGE = 'ru.local.opts'
            _settings = {'a': 0, 'b': 1, 'c': 2}


            def _build_template():
                from gui.modsSettingsApi import templates
                return {
                    'modDisplayName': u'Opts',
                    'column1': [
                        templates.createDropdown(u'Строки', 'a', [u'Раз', u'Два'], _settings['a']),
                        templates.createDropdown(u'Пары', 'b', [(u'Раз', u'т1'), (u'Два', u'т2')], _settings['b']),
                        templates.createDropdown(u'Словари', 'c', [{'label': u'Раз'}, {'label': u'Два'}], _settings['c']),
                    ],
                    'column2': [],
                }
            """;

        SettingsTemplate template = TemplateImporter.FromPython(module).Template!;

        Assert.Equal(new[] { "Раз", "Два" }, template.FindByVarName("a")!.Options.Select(o => o.Label));
        Assert.Equal("т1", template.FindByVarName("b")!.Options[0].Tooltip);
        Assert.Equal(new[] { "Раз", "Два" }, template.FindByVarName("c")!.Options.Select(o => o.Label));
    }

    /// <summary>Фабрики дописывают ключи, которые Python кладёт всегда: format и canManualInput.</summary>
    [Fact]
    public void FactoriesAddAlwaysWrittenKeys()
    {
        const string module = """
            LINKAGE = 'ru.local.always'
            _settings = {'s': 1, 'n': 2}


            def _build_template():
                from gui.modsSettingsApi import templates
                return {
                    'modDisplayName': u'Always',
                    'column1': [
                        templates.createSlider(u'Ползунок', 's', _settings['s'], 0, 10, 1),
                        templates.createNumericStepper(u'Счётчик', 'n', _settings['n'], 0, 10, 1),
                    ],
                    'column2': [],
                }
            """;

        SettingsTemplate template = TemplateImporter.FromPython(module).Template!;

        Assert.Equal("{{value}}", template.FindByVarName("s")!.Format);
        Assert.False(template.FindByVarName("n")!.CanManualInput);
        Assert.Null(template.FindByVarName("s")!.Width);
    }

    /// <summary>createColorChoice снимает решётку — импортёр повторяет это, иначе разойдёмся на символ.</summary>
    [Fact]
    public void ColorChoice_StripsHashLikeThePythonFactory()
    {
        const string module = """
            LINKAGE = 'ru.local.color'
            _settings = {'c': '#FFCC00'}


            def _build_template():
                from gui.modsSettingsApi import templates
                return {
                    'modDisplayName': u'Color',
                    'column1': [templates.createColorChoice(u'Цвет', 'c', _settings['c'])],
                    'column2': [],
                }
            """;

        SettingsTemplate template = TemplateImporter.FromPython(module).Template!;
        Assert.Equal("FFCC00", template.FindByVarName("c")!.Value!.AsString);
    }

    /// <summary>
    /// Настоящий рецепт R15 из ресурсов кокпита разбирается целиком. Это проверка не на
    /// синтетике: дизайнер обязан открывать то, что кокпит уже умеет вставлять.
    /// </summary>
    [SkippableFact]
    public void RecipeR15_Imports()
    {
        Skip.IfNot(File.Exists(SettingsTestData.RecipeR15Path), SettingsTestData.RecipeR15Path);

        TemplateImportResult result = TemplateImporter.FromPythonFile(SettingsTestData.RecipeR15Path);

        Assert.True(result.Success, result.Describe());
        Assert.Equal(TemplateImportSource.PythonTemplatesApi, result.Source);

        SettingsTemplate template = result.Template!;
        Assert.Equal("ru.local.my_mod", template.Linkage);
        Assert.Equal("My Mod", template.ModDisplayName);
        Assert.Equal(1, template.SettingsVersion);
        Assert.True(template.HasEnabled);
        Assert.Equal(3, template.Column1.Count);
        Assert.Equal(2, template.Column2.Count);

        Assert.Equal(ControlKind.Label, template.Column1[0].Kind);
        Assert.Equal(ControlKind.CheckBox, template.Column1[1].Kind);
        Assert.Equal(ControlKind.NumericStepper, template.Column1[2].Kind);
        Assert.Equal(ControlKind.TextInput, template.Column2[1].Kind);
        Assert.Equal("Привет, танкист!", template.FindByVarName("greetingText")!.Value!.AsString);
    }

    /// <summary>Шаблон из рецепта проходит валидацию без ошибок.</summary>
    [SkippableFact]
    public void RecipeR15_PassesValidation()
    {
        Skip.IfNot(File.Exists(SettingsTestData.RecipeR15Path), SettingsTestData.RecipeR15Path);

        SettingsTemplate template = TemplateImporter.FromPythonFile(SettingsTestData.RecipeR15Path).Template!;
        TemplateValidationResult result = TemplateValidator.Validate(template);

        Assert.False(result.HasErrors, result.Describe());
    }

    /// <summary>
    /// СКВОЗНАЯ ПРОВЕРКА: каждый из 11 шаблонов фикстуры записывается литералами Python
    /// и читается обратно без единого расхождения. Это доказывает, что дизайнер сможет
    /// открыть свою же форму заново — главный сценарий импорта.
    /// </summary>
    [Fact]
    public void EverySampleTemplate_SurvivesPythonRoundTrip()
    {
        var failures = new List<string>();

        foreach (string linkage in SettingsTestData.Linkages)
        {
            SettingsTemplate original = TemplateImporter
                .FromModSettingsDat(SettingsTestData.StateJson, linkage).Template!;

            TemplateImportResult back = TemplateImporter.FromPython(AsPythonModule(original));
            if (!back.Success)
            {
                failures.Add($"{linkage}: {back.Describe()}");
                continue;
            }

            string expected = TemplateJson.ToCanonicalJson(original);
            string actual = TemplateJson.ToCanonicalJson(back.Template!);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                failures.Add($"{linkage}:{Environment.NewLine}  было:  {expected}{Environment.NewLine}  стало: {actual}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Записать шаблон литералами Python 2 — ровно та форма, которую порождает кокпит.</summary>
    private static string AsPythonModule(SettingsTemplate template) =>
        $"# -*- coding: utf-8 -*-\nLINKAGE = '{template.Linkage}'\n\n\n" +
        "def build_template(values):\n    return " + PyLiteral(TemplateJson.ToValue(template)) + "\n";

    private static string PyLiteral(SettingValue value) => value.Kind switch
    {
        SettingValueKind.Null => "None",
        SettingValueKind.Bool => value.AsBool ? "True" : "False",
        SettingValueKind.Integer or SettingValueKind.Number => value.NumberText(),
        SettingValueKind.String => "u" + SettingValue.Of(value.AsString).ToJson(),
        SettingValueKind.List => "[" + string.Join(", ", value.Items.Select(PyLiteral)) + "]",
        SettingValueKind.Map => "{" + string.Join(", ",
            value.Entries.Select(e => "u" + SettingValue.Of(e.Key).ToJson() + ": " + PyLiteral(e.Value))) + "}",
        _ => throw new InvalidOperationException(value.Kind.ToString())
    };

    // ================================================================== Python: честный отказ

    /// <summary>В файле нет шаблона — отказ с объяснением.</summary>
    [Fact]
    public void NoTemplateInFile_Fails()
    {
        TemplateImportResult result = TemplateImporter.FromPython("import logging\n\n\ndef init():\n    pass\n");

        Assert.False(result.Success);
        Assert.Null(result.Template);
        Assert.Contains("modDisplayName", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>Колонки собирает чужая функция — импортёр обязан сказать, чего он не понял.</summary>
    [Fact]
    public void ColumnsBuiltByHelper_FailsHonestly()
    {
        const string module = """
            LINKAGE = 'ru.local.dynamic'


            def build_template(values):
                return {
                    'modDisplayName': u'Dynamic',
                    'column1': _make_column(values),
                    'column2': [],
                }
            """;

        TemplateImportResult result = TemplateImporter.FromPython(module);

        Assert.False(result.Success);
        Assert.Null(result.Template);
        Assert.Contains("_make_column", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>Колонки наполняются циклом — отдавать пустую форму как успех нельзя.</summary>
    [Fact]
    public void ColumnsFilledInLoop_FailsHonestly()
    {
        const string module = """
            LINKAGE = 'ru.local.loop'
            NAMES = ['a', 'b', 'c']


            def build_template(values):
                result = {
                    'modDisplayName': u'Loop',
                    'column1': [],
                    'column2': [],
                }
                for name in NAMES:
                    result['column1'].append(_make(name))
                return result
            """;

        TemplateImportResult result = TemplateImporter.FromPython(module);

        Assert.False(result.Success);
        Assert.Contains("колонки пусты", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>Значение по умолчанию неоткуда взять — отказ, а не выдуманный ноль.</summary>
    [Fact]
    public void UnresolvableDefault_FailsHonestly()
    {
        const string module = """
            LINKAGE = 'ru.local.nodefaults'


            def build_template(values):
                return {
                    'modDisplayName': u'NoDefaults',
                    'column1': [
                        {'type': 'CheckBox', 'text': u'Галка', 'varName': 'flag', 'value': values['flag']},
                    ],
                    'column2': [],
                }
            """;

        TemplateImportResult result = TemplateImporter.FromPython(module);

        Assert.False(result.Success);
        Assert.Contains("flag", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>Незнакомый именованный аргумент фабрики — отказ, а не тихая потеря поля.</summary>
    [Fact]
    public void UnknownFactoryArgument_FailsHonestly()
    {
        const string module = """
            LINKAGE = 'ru.local.badarg'
            _settings = {'flag': True}


            def _build_template():
                from gui.modsSettingsApi import templates
                return {
                    'modDisplayName': u'BadArg',
                    'column1': [templates.createCheckbox(u'Галка', 'flag', _settings['flag'], colour='red')],
                    'column2': [],
                }
            """;

        TemplateImportResult result = TemplateImporter.FromPython(module);

        Assert.False(result.Success);
        Assert.Contains("colour", result.Describe(), StringComparison.Ordinal);
    }

    /// <summary>Отсутствие LINKAGE не ломает импорт, но и не замалчивается.</summary>
    [Fact]
    public void MissingLinkage_IsReportedButNotFatal()
    {
        // Сырая строка берёт концы строк из исходника, а git на Windows может выдать его с CRLF.
        string module = LiteralModule.ReplaceLineEndings("\n").Replace("LINKAGE = 'ru.local.hello_tanker'\n",
            string.Empty, StringComparison.Ordinal);

        TemplateImportResult result = TemplateImporter.FromPython(module);

        Assert.True(result.Success, result.Describe());
        Assert.Equal(string.Empty, result.Template!.Linkage);
        Assert.Contains("LINKAGE", result.Describe(), StringComparison.Ordinal);
    }

    // ================================================================== разбор выражений

    /// <summary>Числа сохраняют питоновскую форму записи.</summary>
    [Theory]
    [InlineData("3", "3")]
    [InlineData("3.0", "3.0")]
    [InlineData("0.01", "0.01")]
    [InlineData("-2", "-2")]
    [InlineData("-0.5", "-0.5")]
    [InlineData("0x10", "16")]
    public void Parser_KeepsNumberForm(string source, string expected)
    {
        Assert.Equal(expected, ParseValue(source).ToJson());
    }

    /// <summary>Строки всех употребимых форм.</summary>
    [Theory]
    [InlineData("u'Привет'", "Привет")]
    [InlineData("'plain'", "plain")]
    [InlineData("\"double\"", "double")]
    [InlineData("u'a' u'b'", "ab")]
    [InlineData("'\\n'", "\n")]
    [InlineData("r'C:\\path'", "C:\\path")]
    [InlineData("u'{{value}} сек.'", "{{value}} сек.")]
    public void Parser_ReadsStrings(string source, string expected)
    {
        Assert.Equal(expected, ParseValue(source).AsString);
    }

    /// <summary>Тройные кавычки.</summary>
    [Fact]
    public void Parser_ReadsTripleQuotedString()
    {
        Assert.Equal("две\nстроки", ParseValue("u'''две\nстроки'''").AsString);
    }

    /// <summary>Списки, словари и кортежи.</summary>
    [Fact]
    public void Parser_ReadsContainers()
    {
        Assert.Equal("[61,-2]", ParseValue("[61, -2]").ToJson());
        Assert.Equal("[[56,184]]", ParseValue("[[56, 184]]").ToJson());
        Assert.Equal("""{"a":1,"b":true}""", ParseValue("{'a': 1, 'b': True}").ToJson());
        Assert.Equal("[1,2]", ParseValue("(1, 2)").ToJson());
        Assert.Equal("[1,2]", ParseValue("[1, 2,]").ToJson());
    }

    /// <summary>Комментарии и переносы внутри скобок не мешают.</summary>
    [Fact]
    public void Parser_SkipsCommentsInsideBrackets()
    {
        Assert.Equal("""{"a":1}""", ParseValue("{\n    # это комментарий\n    'a': 1,  # и это\n}").ToJson());
    }

    /// <summary>True/False/None.</summary>
    [Fact]
    public void Parser_ReadsKeywords()
    {
        Assert.Equal(SettingValueKind.Bool, ParseValue("True").Kind);
        Assert.False(ParseValue("False").AsBool);
        Assert.Equal(SettingValueKind.Null, ParseValue("None").Kind);
    }

    /// <summary>Незакрытая скобка — понятная ошибка с номером строки, а не исключение наружу.</summary>
    [Fact]
    public void Parser_ReportsErrorWithLineNumber()
    {
        Assert.False(PythonValueParser.TryParse("{\n'a': 1\n", 0, out IPyNode? node, out string? error));
        Assert.Null(node);
        Assert.NotNull(error);
        Assert.Contains("строка", error, StringComparison.Ordinal);
    }

    private static SettingValue ParseValue(string source)
    {
        Assert.True(PythonValueParser.TryParse(source, 0, out IPyNode? node, out string? error), error);
        SettingValue? value = TemplateImporter
            .FromPython($"X = {source}\n\n\ndef build_template(values):\n    return {{'modDisplayName': u'X', " +
                        "'column1': [{'type': 'CheckBox', 'text': u't', 'varName': 'probe', 'value': X}], " +
                        "'column2': []}\n")
            .Template?.FindByVarName("probe")?.Value;
        Assert.NotNull(value);
        return value!;
    }

    // ================================================================== тестовые модули

    /// <summary>Литеральная форма — то, что порождает дизайнер настроек.</summary>
    private const string LiteralModule = """
        # -*- coding: utf-8 -*-
        LINKAGE = 'ru.local.hello_tanker'
        SETTINGS_VERSION = 3
        MOD_DISPLAY_NAME = u'Hello Tanker'

        DEFAULTS = {
            'enabled': True,
            'showGreeting': True,
            'delaySec': 3,
            'position': 1,
            'toggleKey': [61, -2],
        }


        def build_template(values):
            def v(name):
                if name in values:
                    return values[name]
                return DEFAULTS[name]

            return {
                'modDisplayName': MOD_DISPLAY_NAME,
                'settingsVersion': SETTINGS_VERSION,
                'enabled': v('enabled'),
                'column1': [
                    {'type': 'Label', 'text': u'Основное'},
                    {'type': 'CheckBox', 'text': u'Показывать приветствие',
                     'varName': 'showGreeting', 'value': v('showGreeting'),
                     'tooltip': u'{HEADER}Приветствие{/HEADER}'
                                u'{BODY}Сообщение в ангаре{/BODY}'},
                    {'type': 'Slider', 'text': u'Задержка', 'varName': 'delaySec',
                     'value': v('delaySec'), 'minimum': 0, 'maximum': 10,
                     'snapInterval': 1, 'format': u'{{value}} сек.', 'width': 200},
                ],
                'column2': [
                    {'type': 'Dropdown', 'text': u'Положение', 'varName': 'position',
                     'value': v('position'), 'width': 200,
                     'options': [{'label': u'Слева'}, {'label': u'По центру'}, {'label': u'Справа'}]},
                    {'type': 'HotKey', 'text': u'Горячая клавиша', 'varName': 'toggleKey',
                     'value': v('toggleKey')},
                ],
            }
        """;

    /// <summary>Та же самая форма, записанная вызовами чужого API.</summary>
    private const string FactoryModule = """
        # -*- coding: utf-8 -*-
        LINKAGE = 'ru.local.hello_tanker'
        SETTINGS_VERSION = 3

        _settings = {
            'enabled': True,
            'showGreeting': True,
            'delaySec': 3,
            'position': 1,
            'toggleKey': [61, -2],
        }


        def _build_template():
            from gui.modsSettingsApi import templates
            return {
                'modDisplayName': u'Hello Tanker',
                'settingsVersion': SETTINGS_VERSION,
                'enabled': _settings['enabled'],
                'column1': [
                    templates.createLabel(u'Основное'),
                    templates.createCheckbox(
                        u'Показывать приветствие', 'showGreeting', _settings['showGreeting'],
                        tooltip=u'{HEADER}Приветствие{/HEADER}{BODY}Сообщение в ангаре{/BODY}'),
                    templates.createSlider(
                        u'Задержка', 'delaySec', _settings['delaySec'], 0, 10, 1,
                        format=u'{{value}} сек.', width=200),
                ],
                'column2': [
                    templates.createDropdown(
                        u'Положение', 'position', [u'Слева', u'По центру', u'Справа'],
                        _settings['position'], width=200),
                    templates.createHotkey(
                        u'Горячая клавиша', 'toggleKey', _settings['toggleKey']),
                ],
            }
        """;
}
