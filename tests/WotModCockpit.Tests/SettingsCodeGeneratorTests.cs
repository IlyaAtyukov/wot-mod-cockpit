using WotModCockpit.Core.Settings;

namespace WotModCockpit.Tests;

/// <summary>
/// Пять форм, на которых гоняется генератор. Набор подобран не «для красоты»: каждая форма
/// закрывает свой класс дефектов кодогенерации.
/// </summary>
public static class SettingsDesignSamples
{
    /// <summary>Все образцы разом — для тестов, которые гоняют валидатор по кругу.</summary>
    /// <returns>Пары «имя — шаблон».</returns>
    public static IEnumerable<(string Name, SettingsTemplate Template)> All()
    {
        yield return ("minimal", Minimal());
        yield return ("all_types", AllControlTypes());
        yield return ("fractional", FractionalSlider());
        yield return ("legacy_hotkey", LegacyHotkeySample());
        yield return ("tricky_text", TrickyText());
        yield return ("toggle_only", ToggleOnly());
    }

    /// <summary>
    /// Мод «включить/выключить одной галочкой»: обе колонки пусты, весь смысл формы —
    /// верхний тумблер. Образец законный: <c>api.getSettingsFromTemplate</c> читает
    /// <c>enabled</c> ДО колонок, и окно рисует блок мода с тумблером. До правки MSA006 это
    /// была ошибка уровня Error, и такая форма вообще не генерировалась.
    /// </summary>
    /// <returns>Шаблон.</returns>
    public static SettingsTemplate ToggleOnly() => new()
    {
        Linkage = "ru.local.hello_tanker",
        ModDisplayName = "Hello Tanker",
        SettingsVersion = 1,
        HasEnabled = true,
        EnabledDefault = true
    };

    /// <summary>Параметры генерации для образцов.</summary>
    /// <returns>Параметры.</returns>
    public static SettingsCodeOptions Options() => new()
    {
        PackageName = "hello_tanker",
        LogTag = "HelloTanker",
        ConfigRelativePath = "mods/configs/hello_tanker/settings.json"
    };

    /// <summary>Самая маленькая осмысленная форма: одна галочка и никакого тумблера.</summary>
    /// <returns>Шаблон.</returns>
    public static SettingsTemplate Minimal()
    {
        var t = new SettingsTemplate
        {
            Linkage = "ru.local.hello_tanker",
            ModDisplayName = "Hello Tanker",
            SettingsVersion = 1,
            HasEnabled = false
        };
        t.Column1.Add(SettingControl.CheckBox("Включить приветствие", "showGreeting", true,
            "{HEADER}Приветствие{/HEADER}{BODY}Системное сообщение при входе в ангар{/BODY}"));
        return t;
    }

    /// <summary>
    /// Все 11 типов сразу, включая шесть, которых нет в реальных шаблонах выборки. Именно здесь ловятся
    /// пропущенные ключи (<c>canManualInput</c>, <c>divisionLabelPostfix</c>) и печать
    /// значений редких видов (список пары, hex-строка, коды клавиш).
    /// </summary>
    /// <returns>Шаблон.</returns>
    public static SettingsTemplate AllControlTypes()
    {
        var t = new SettingsTemplate
        {
            Linkage = "ru.local.every_control",
            ModDisplayName = "Все контролы",
            SettingsVersion = 7,
            HasEnabled = true,
            EnabledDefault = true
        };

        t.Column1.Add(SettingControl.Label("Основное", "{HEADER}Секция{/HEADER}{BODY}Заголовок группы{/BODY}"));
        t.Column1.Add(SettingControl.CheckBox("Показывать приветствие", "showGreeting", true));
        t.Column1.Add(SettingControl.Empty());
        t.Column1.Add(SettingControl.RadioButtonGroup("Стиль сообщения", "style", 1, "Кратко", "Подробно", "Отладочно"));
        t.Column1.Add(SettingControl.Dropdown("Положение панели", "position", 0, "Слева", "По центру", "Справа"));
        t.Column1.Add(SettingControl.Slider("Непрозрачность", "opacity", 80, 0, 100, 5, "{{value}} %"));

        t.Column2.Add(SettingControl.TextInput("Текст приветствия", "greetingText", "Привет, танкист!"));
        t.Column2.Add(SettingControl.NumericStepper("Задержка, с", "delaySec", 3, 0, 10, 1, canManualInput: true));
        t.Column2.Add(SettingControl.HotKey("Горячая клавиша", "toggleKey", 61, HotKeyValue.Ctrl));
        t.Column2.Add(SettingControl.ColorChoice("Цвет маркера", "markColor", "#FFCC00"));
        t.Column2.Add(SettingControl.RangeSlider("Диапазон уровней", "tierRange", 5, 8, 1, 10, 1));

        return t;
    }

    /// <summary>
    /// Дробный диапазон, как у <c>ru.example.ui-scale</c> в фикстуре (и у реальных модов
    /// масштаба интерфейса): <c>0.8 … 3.0</c> с шагом <c>0.01</c>.
    /// Проверяет, что <c>3.0</c> не превратится в <c>3</c>: без <c>settingsVersion</c> API
    /// сравнивает шаблоны ТЕКСТОМ, и такая подмена молча сбросила бы настройки игрока.
    /// </summary>
    /// <returns>Шаблон.</returns>
    public static SettingsTemplate FractionalSlider()
    {
        var t = new SettingsTemplate
        {
            Linkage = "ru.local.scaling",
            ModDisplayName = "Масштаб",
            SettingsVersion = 2,
            HasEnabled = true
        };

        var slider = new SettingControl(ControlKind.Slider)
        {
            Text = "Множитель",
            VarName = "scale",
            Value = SettingValue.Of(1.0d, "1.0"),
            Minimum = SettingValue.Of(0.8d, "0.8"),
            Maximum = SettingValue.Of(3.0d, "3.0"),
            SnapInterval = SettingValue.Of(0.01d, "0.01"),
            Format = "Множитель: {{value}}",
            Width = 310
        };
        t.Column1.Add(slider);

        // Целый ползунок рядом с дробным: типы чисел не должны «слипнуться».
        t.Column2.Add(SettingControl.Slider("Интервал", "intervalMin", 2, 1, 14, 1, "{{value}} мин."));

        return t;
    }

    /// <summary>
    /// Эталонный шаблон фикстуры <c>ru.example.alpha</c> (<c>Resources/modsettings.sample.json</c>),
    /// собранный средствами модели: без <c>settingsVersion</c> и с ЛЕГАСИ-формой горячей клавиши
    /// (<c>[61, [29, 157]]</c>), какая встречается в реальных шаблонах. Импортированное значение
    /// обязано доехать до сгенерированного кода без «улучшений»: любая правка шаблона у мода без
    /// <c>settingsVersion</c> означает сброс настроек игрока.
    /// </summary>
    /// <returns>Шаблон.</returns>
    public static SettingsTemplate LegacyHotkeySample()
    {
        var t = new SettingsTemplate
        {
            Linkage = "ru.example.alpha",
            ModDisplayName = "Пример: напоминания",
            SettingsVersion = null,
            HasEnabled = true,
            EnabledDefault = true
        };

        var radio = SettingControl.RadioButtonGroup(
            "Как напоминать", "outputMode", 0,
            "В системный канал", "Крупно поверх прицела", "Только звуком");
        radio.Tooltip = "{HEADER}Как напоминать{/HEADER}{BODY}Выберите, каким способом " +
                        "выводить напоминание{/BODY}";
        t.Column1.Add(radio);

        t.Column1.Add(SettingControl.CheckBox(
            "Напоминать и в тренировочных боях", "inTraining", true,
            "{HEADER}Напоминать и в тренировочных боях{/HEADER}{BODY}Без этой галочки напоминания " +
            "приходят только в обычных боях{/BODY}"));

        var slider = SettingControl.Slider(
            "Интервал между напоминаниями", "intervalMin", 2, 1, 14, 1, "{{value}} мин.");
        slider.Width = 150;
        slider.Tooltip = "{HEADER}Интервал{/HEADER}{BODY}Через сколько минут повторять напоминание. " +
                         "Слишком частые напоминания быстро надоедают, поэтому начните с двух-трёх " +
                         "минут{/BODY}";
        t.Column2.Add(slider);

        var hotkey = new SettingControl(ControlKind.HotKey)
        {
            Text = "Клавиша: напомнить прямо сейчас",
            VarName = "showKey",
            Tooltip = "{HEADER}Клавиша напоминания{/HEADER}{BODY}Показывает напоминание вне очереди.{/BODY}",
            // Легаси-форма: вложенный список пары L/R, которую _migrateKeys схлопнет в -2.
            Value = SettingValue.List([SettingValue.Of(61L), SettingValue.List(29L, 157L)])
        };
        t.Column2.Add(hotkey);

        return t;
    }

    /// <summary>
    /// Форма, которая ломает наивную конкатенацию строк: апостроф, обратный слэш, перевод
    /// строки, кавычки, HTML в заголовке, кириллица везде. Плюс посторонний ключ
    /// <c>itemRenderer</c>, пришедший «импортом», и кнопка у ползунка.
    /// </summary>
    /// <returns>Шаблон.</returns>
    public static SettingsTemplate TrickyText()
    {
        var t = new SettingsTemplate
        {
            Linkage = "ru.local.tricky",
            ModDisplayName = "<b>Мод</b> «Кавычки» — 100% \"строк\"",
            SettingsVersion = 3,
            HasEnabled = true,
            EnabledDefault = false
        };

        t.Column1.Add(SettingControl.CheckBox(
            "Путь: C:\\Games\\Tanki — не 'обычный' путь", "weirdPath", false,
            "{HEADER}Кавычки{/HEADER}{BODY}Строка\nс переводом строки и \\ обратным слэшем{/BODY}"));

        var dropdown = SettingControl.Dropdown("Набор скинов", "skinPack", 1, "Один", "Два", "Три");
        // Мёртвое поле, встречающееся у реальных модов: окно его не читает, но импорт обязан его сохранить,
        // иначе шаблон изменится и настройки игрока сбросятся.
        dropdown.SetExtra(TemplateJson.ItemRendererKey, SettingValue.Of("DropDownListItemRendererSound"));
        t.Column1.Add(dropdown);

        var slider = SettingControl.Slider("Прозрачность", "alpha", 50, 0, 100, 10, "Прозрачность: {{value}}");
        slider.Button = new ControlButton { Text = "Сброс", Width = 60, Height = 25 };
        t.Column2.Add(slider);

        t.Column2.Add(SettingControl.Label("Пусто ниже"));

        return t;
    }
}

/// <summary>
/// Модульные проверки печати кода: печатается ли ровно то, что лежит в модели, и в той ли
/// форме, в какой это переживёт Python 2.7 и ModsSettingsAPI.
/// </summary>
public class SettingsCodeGeneratorTests
{
    /// <summary>Заголовок модуля схемы содержит все константы, на которые опирается settings.py.</summary>
    [Fact]
    public void SchemaModule_DeclaresConstantsUsedByUserModule()
    {
        string code = SettingsCodeGenerator.BuildSchemaRegion(
            SettingsDesignSamples.AllControlTypes(), SettingsDesignSamples.Options());

        Assert.Contains("LINKAGE = 'ru.local.every_control'", code, StringComparison.Ordinal);
        Assert.Contains("SETTINGS_VERSION = 7", code, StringComparison.Ordinal);
        Assert.Contains("MOD_DISPLAY_NAME = u'Все контролы'", code, StringComparison.Ordinal);
        Assert.Contains("HAS_ENABLED = True", code, StringComparison.Ordinal);
        Assert.Contains("def build_template(values):", code, StringComparison.Ordinal);
        Assert.Contains("def _v(values, name):", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// linkage печатается БАЙТОВОЙ строкой, а не unicode. Это не стиль: <c>generateSettingsData</c>
    /// делает <c>sorted(templates, key=str.lower)</c>, и несвязанный <c>str.lower</c> на
    /// unicode-объекте в Python 2 бросает TypeError — окно настроек не откроется ни у одного мода.
    /// </summary>
    [Fact]
    public void Linkage_And_VarNames_ArePrintedAsByteStrings()
    {
        string code = SettingsCodeGenerator.BuildSchemaRegion(SettingsDesignSamples.AllControlTypes());

        Assert.DoesNotContain("LINKAGE = u'", code, StringComparison.Ordinal);
        Assert.Contains("'varName': 'showGreeting'", code, StringComparison.Ordinal);
        Assert.DoesNotContain("'varName': u'", code, StringComparison.Ordinal);
        Assert.Contains("'type': 'CheckBox'", code, StringComparison.Ordinal);
        Assert.DoesNotContain("'type': u'", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Любой не-ASCII текст печатается с префиксом <c>u</c>. В Python 2 это разница между
    /// работающим GUI и <c>UnicodeDecodeError</c>, который <c>logging</c> проглотит вместе
    /// со всей записью (правило WOT017).
    /// </summary>
    [Fact]
    public void NonAsciiText_AlwaysGetsUnicodePrefix()
    {
        foreach ((string name, SettingsTemplate template) in SettingsDesignSamples.All())
        {
            string code = SettingsCodeGenerator.BuildSchemaRegion(template);

            foreach (string line in code.Split('\n'))
            {
                int quote = line.IndexOf('\'', StringComparison.Ordinal);
                if (quote <= 0) continue;
                if (!line.Any(ch => ch > 127)) continue;
                if (line.TrimStart().StartsWith('#')) continue;

                // В строках с кириллицей обязан быть хотя бы один u'…'.
                Assert.True(line.Contains("u'", StringComparison.Ordinal),
                    $"[{name}] строка с не-ASCII без префикса u: {line}");
            }
        }
    }

    /// <summary>
    /// Значение контрола НЕ печатается литералом: вместо него стоит <c>_v(values, 'varName')</c>.
    /// Иначе шаблон строился бы от заводских значений, и первое же принятое изменение шаблона
    /// сбросило бы настройки игрока — сливать старые значения ModsSettingsAPI не умеет.
    /// </summary>
    [Fact]
    public void ControlValues_AreTakenFromSavedValues_NotFromDefaults()
    {
        string code = SettingsCodeGenerator.BuildSchemaRegion(SettingsDesignSamples.AllControlTypes());

        Assert.Contains("'value': _v(values, 'showGreeting')", code, StringComparison.Ordinal);
        Assert.Contains("'enabled': _v(values, 'enabled')", code, StringComparison.Ordinal);
        Assert.Contains("'value': _v(values, 'markColor')", code, StringComparison.Ordinal);

        // …и при этом DEFAULTS остаётся единственным источником заводских значений.
        Assert.Contains("DEFAULTS = {", code, StringComparison.Ordinal);
        Assert.Contains("'markColor': u'FFCC00'", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Дробные числа не схлопываются в целые. <c>3.0</c> и <c>3</c> в JSON — разные шаблоны,
    /// а без <c>settingsVersion</c> API сравнивает шаблоны текстом и сбрасывает настройки.
    /// </summary>
    [Fact]
    public void FractionalRange_KeepsItsExactNumericForm()
    {
        string code = SettingsCodeGenerator.BuildSchemaRegion(SettingsDesignSamples.FractionalSlider());

        Assert.Contains("'minimum': 0.8", code, StringComparison.Ordinal);
        Assert.Contains("'maximum': 3.0", code, StringComparison.Ordinal);
        Assert.Contains("'snapInterval': 0.01", code, StringComparison.Ordinal);

        // У соседнего целого ползунка числа остались целыми.
        Assert.Contains("'maximum': 14", code, StringComparison.Ordinal);
        Assert.DoesNotContain("'maximum': 14.0", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ключ <c>settingsVersion</c> не выдумывается: у шаблона без версии (как у многих реальных
    /// модов) его нет, и появление ключа изменило бы шаблон, то есть сбросило бы настройки игрока.
    /// </summary>
    [Fact]
    public void MissingSettingsVersion_IsNotInvented()
    {
        string code = SettingsCodeGenerator.BuildSchemaRegion(SettingsDesignSamples.LegacyHotkeySample());

        Assert.Contains("SETTINGS_VERSION = None", code, StringComparison.Ordinal);
        Assert.DoesNotContain("'settingsVersion':", code, StringComparison.Ordinal);
    }

    /// <summary>Легаси-значение горячей клавиши доезжает как есть, без «модернизации».</summary>
    [Fact]
    public void LegacyHotKeyValue_IsPreservedVerbatim()
    {
        string code = SettingsCodeGenerator.BuildSchemaRegion(SettingsDesignSamples.LegacyHotkeySample());
        Assert.Contains("'showKey': [61, [29, 157]]", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Посторонние ключи, пришедшие импортом (в том числе мёртвый <c>itemRenderer</c>),
    /// сохраняются. Дизайнер их не создаёт, но выбросить их — значит изменить шаблон.
    /// </summary>
    [Fact]
    public void ImportedExtraKeys_SurviveGeneration()
    {
        string code = SettingsCodeGenerator.BuildSchemaRegion(SettingsDesignSamples.TrickyText());
        Assert.Contains("'itemRenderer': u'DropDownListItemRendererSound'", code, StringComparison.Ordinal);
    }

    /// <summary>Апостроф, обратный слэш и перевод строки экранируются, а не рвут литерал.</summary>
    [Fact]
    public void TrickyText_IsEscaped()
    {
        string code = SettingsCodeGenerator.BuildSchemaRegion(SettingsDesignSamples.TrickyText());

        Assert.Contains(@"C:\\Games\\Tanki", code, StringComparison.Ordinal);
        Assert.Contains(@"\'обычный\'", code, StringComparison.Ordinal);
        Assert.Contains(@"Строка\nс переводом", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Строка\nс переводом", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Замыканий в порождённом коде нет: при сборке с <c>--compile-all</c> вложенные модули
    /// компилирует <c>tools/py27c.py</c>, у которого замыканий нет вовсе, и сборка упала бы
    /// с <c>Py27CompileError</c>, не сославшись на дизайнер.
    /// </summary>
    [Fact]
    public void GeneratedCode_HasNoNestedFunctions()
    {
        foreach ((string name, SettingsTemplate template) in SettingsDesignSamples.All())
        {
            string code = SettingsCodeGenerator.BuildSchemaRegion(template);
            foreach (string line in code.Split('\n'))
            {
                bool nested = line.StartsWith("    def ", StringComparison.Ordinal)
                              || line.StartsWith("\tdef ", StringComparison.Ordinal);
                Assert.False(nested, $"[{name}] вложенная функция в машинном коде: {line}");
            }
        }
    }

    /// <summary>
    /// Модуль схемы не импортирует чужой мод. Это и есть причина печатать литеральные словари
    /// вместо <c>templates.createX</c>: иначе на файл срабатывало бы WOT018, а мод переставал
    /// бы работать без установленного ModsSettingsAPI.
    /// </summary>
    [Fact]
    public void SchemaModule_DoesNotImportForeignMod()
    {
        foreach ((string name, SettingsTemplate template) in SettingsDesignSamples.All())
        {
            string code = SettingsCodeGenerator.BuildSchemaRegion(template);
            Assert.False(code.Contains("modsSettingsApi", StringComparison.Ordinal),
                $"[{name}] модуль схемы импортирует ModsSettingsAPI");
        }
    }

    /// <summary>
    /// Пользовательский модуль оборачивает импорт чужого API и переживает его отсутствие —
    /// то, ради чего существует правило WOT018.
    /// </summary>
    [Fact]
    public void UserModule_GuardsForeignImport()
    {
        string code = SettingsCodeGenerator.BuildUserModule(SettingsDesignSamples.Options());

        Assert.Contains("g_modsSettingsApi = None", code, StringComparison.Ordinal);
        Assert.Contains("from gui.modsSettingsApi import g_modsSettingsApi", code, StringComparison.Ordinal);
        Assert.Contains("except ImportError:", code, StringComparison.Ordinal);
        Assert.Contains("if g_modsSettingsApi is None:", code, StringComparison.Ordinal);
        Assert.Contains("if linkage != settings_schema.LINKAGE:", code, StringComparison.Ordinal);
    }

    /// <summary>Подстановка паспорта проекта доезжает во все три места сразу.</summary>
    [Fact]
    public void UserModule_SubstitutesProjectIdentity()
    {
        string code = SettingsCodeGenerator.BuildUserModule(SettingsDesignSamples.Options());

        Assert.Contains("LOG_PREFIX = '[HelloTanker/settings]'", code, StringComparison.Ordinal);
        Assert.Contains("logging.getLogger('HelloTanker')", code, StringComparison.Ordinal);
        Assert.Contains(
            "CONFIG_RELATIVE_PATH = os.path.join('mods', 'configs', 'hello_tanker', 'settings.json')",
            code, StringComparison.Ordinal);
        Assert.DoesNotContain("my_mod", code, StringComparison.Ordinal);
        Assert.DoesNotContain("MyMod", code, StringComparison.Ordinal);
    }

    /// <summary>Переименование модулей доезжает до импорта и всех обращений.</summary>
    [Fact]
    public void UserModule_HonoursCustomModuleNames()
    {
        var options = SettingsDesignSamples.Options() with { SchemaModuleName = "form_schema", UserModuleName = "prefs" };
        string code = SettingsCodeGenerator.BuildUserModule(options);

        Assert.Contains("import form_schema", code, StringComparison.Ordinal);
        Assert.Contains("form_schema.DEFAULTS", code, StringComparison.Ordinal);
        Assert.DoesNotContain("settings_schema", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Путь к своему JSON собирается через <c>os.path.join</c>, а не склейкой строк:
    /// литерал с обратными слэшами в Python 2 — это набор escape-последовательностей,
    /// и один <c>\n</c> в имени каталога рушит путь молча.
    /// </summary>
    [Fact]
    public void ConfigPath_IsBuiltWithOsPathJoin()
    {
        var options = SettingsDesignSamples.Options() with { ConfigRelativePath = @"mods\configs\deep\nested\x.json" };
        string code = SettingsCodeGenerator.BuildUserModule(options);

        Assert.Contains(
            "CONFIG_RELATIVE_PATH = os.path.join('mods', 'configs', 'deep', 'nested', 'x.json')",
            code, StringComparison.Ordinal);
    }

    /// <summary>Параметры генерации отбраковывают то, что превратилось бы в неработающий питон.</summary>
    [Theory]
    [InlineData("my mod", "settings_schema", "settings", "mods/configs/x/settings.json")]
    [InlineData("my_mod", "settings", "settings", "mods/configs/x/settings.json")]
    [InlineData("my_mod", "settings_schema", "settings", "C:/absolute/settings.json")]
    [InlineData("my_mod", "settings_schema", "settings", "mods/../../settings.json")]
    public void Options_RejectWhatWouldNotWork(string package, string schema, string user, string config)
    {
        var options = new SettingsCodeOptions
        {
            PackageName = package,
            SchemaModuleName = schema,
            UserModuleName = user,
            ConfigRelativePath = config
        };

        Assert.NotEmpty(options.Validate());
    }

    /// <summary>Нормальные параметры проходят.</summary>
    [Fact]
    public void Options_AcceptSaneValues() => Assert.Empty(SettingsDesignSamples.Options().Validate());

    /// <summary>Паспорт проекта разбирается корректно: тег лога приходит с открывающей скобкой.</summary>
    [Fact]
    public void Options_ForProject_StripsLogTagBracket()
    {
        var project = new Core.Models.ModProject
        {
            ProjectDir = @"C:\tmp\proj",
            ModId = "ru.local.battle_timer",
            PackageName = "battle_timer",
            LogTag = "[BattleTimer"
        };

        SettingsCodeOptions options = SettingsCodeOptions.ForProject(project);

        Assert.Equal("BattleTimer", options.LogTag);
        Assert.Equal("battle_timer", options.PackageName);
        Assert.Equal("mods/configs/battle_timer/settings.json", options.ConfigRelativePath);
    }

    /// <summary>Пустой тег лога выводится из имени пакета, а не остаётся заглушкой.</summary>
    [Fact]
    public void Options_ForProject_DerivesLogTagFromPackage()
    {
        var project = new Core.Models.ModProject { PackageName = "battle_timer" };
        Assert.Equal("BattleTimer", SettingsCodeOptions.ForProject(project).LogTag);
    }

    /// <summary>Генерация детерминирована: два прогона дают побайтно одинаковый текст.</summary>
    [Fact]
    public void Generation_IsDeterministic()
    {
        foreach ((string name, SettingsTemplate template) in SettingsDesignSamples.All())
        {
            string first = SettingsCodeGenerator.BuildSchemaRegion(template, SettingsDesignSamples.Options());
            string second = SettingsCodeGenerator.BuildSchemaRegion(template.Clone(), SettingsDesignSamples.Options());
            Assert.True(string.Equals(first, second, StringComparison.Ordinal), $"[{name}] генерация недетерминирована");
        }
    }

    /// <summary>
    /// Образец <c>legacy_hotkey</c> — это эталонный шаблон <c>ru.example.alpha</c> из файла
    /// состояния в тестовых ресурсах, воспроизведённый средствами модели. Совпадение
    /// канонических JSON означает, что круг «импортировали чужой шаблон — открыли в редакторе —
    /// сгенерировали код» вернул бы игроку ровно его шаблон, а не «почти такой же».
    /// </summary>
    [Fact]
    public void LegacyHotkeySample_MatchesTheTemplateInSampleState()
    {
        SettingValue state = SettingValue.ParseJson(SettingsTestData.StateJson);
        SettingValue? stored = state.Get("templates")?.Get(SettingsTestData.ReferenceLinkage);

        Assert.NotNull(stored);
        Assert.Equal(stored!.ToCanonicalJson(), TemplateJson.ToCanonicalJson(SettingsDesignSamples.LegacyHotkeySample()));
    }

    /// <summary>Валидатор шаблона не находит ошибок ни в одном образце — иначе тесты ниже лгут.</summary>
    [Fact]
    public void AllSamples_PassTemplateValidation()
    {
        foreach ((string name, SettingsTemplate template) in SettingsDesignSamples.All())
        {
            TemplateValidationResult result = TemplateValidator.Validate(template);
            Assert.True(result.ErrorCount == 0, $"[{name}] ошибки шаблона:{Environment.NewLine}{result.Describe()}");
        }
    }
}
