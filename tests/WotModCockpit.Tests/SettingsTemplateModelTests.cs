using WotModCockpit.Core.Settings;

namespace WotModCockpit.Tests;

/// <summary>
/// Модель шаблона: типы контролов, значения и вывод словаря настроек.
/// Всё, что тут утверждается про API, сверено либо с декомпилированным
/// <c>gui/modsSettingsApi</c>, либо с реальными шаблонами популярных модов (клиент 1.44);
/// в тестах эти случаи воспроизводит синтетическая фикстура <c>Resources/modsettings.sample.json</c>.
/// </summary>
public class SettingsTemplateModelTests
{
    // ------------------------------------------------------------------ типы

    /// <summary>Типов ровно 11 — столько же, сколько строк в COMPONENT_TYPE.</summary>
    [Fact]
    public void ControlKinds_AreExactlyEleven()
    {
        Assert.Equal(11, ControlKinds.All.Count);
        Assert.Equal(11, ControlKinds.All.Select(ControlKinds.WireName).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Строки типов совпадают с COMPONENT_TYPE побуквенно, включая регистр.</summary>
    [Theory]
    [InlineData(ControlKind.Empty, "Empty")]
    [InlineData(ControlKind.Label, "Label")]
    [InlineData(ControlKind.CheckBox, "CheckBox")]
    [InlineData(ControlKind.RadioButtonGroup, "RadioButtonGroup")]
    [InlineData(ControlKind.Dropdown, "Dropdown")]
    [InlineData(ControlKind.Slider, "Slider")]
    [InlineData(ControlKind.TextInput, "TextInput")]
    [InlineData(ControlKind.NumericStepper, "NumericStepper")]
    [InlineData(ControlKind.HotKey, "HotKey")]
    [InlineData(ControlKind.ColorChoice, "ColorChoice")]
    [InlineData(ControlKind.RangeSlider, "RangeSlider")]
    public void WireName_MatchesApiConstant(ControlKind kind, string expected)
    {
        Assert.Equal(expected, ControlKinds.WireName(kind));
        Assert.True(ControlKinds.TryParse(expected, out ControlKind parsed));
        Assert.Equal(kind, parsed);
    }

    /// <summary>Регистр важен: окно сравнивает строку типа точно.</summary>
    [Theory]
    [InlineData("checkbox")]
    [InlineData("CHECKBOX")]
    [InlineData("Checkbox")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_IsCaseSensitive(string? value)
    {
        Assert.False(ControlKinds.TryParse(value, out ControlKind kind));
        Assert.Equal(ControlKind.Unknown, kind);
    }

    /// <summary>Имена фабрик templates.py разбираются обратно.</summary>
    [Fact]
    public void FactoryNames_RoundTrip()
    {
        foreach (ControlKind kind in ControlKinds.All)
        {
            string factory = ControlKinds.FactoryName(kind)!;
            Assert.True(ControlKinds.TryParseFactory(factory, out ControlKind parsed), factory);
            Assert.Equal(kind, parsed);
        }
        Assert.Equal("createInput", ControlKinds.FactoryName(ControlKind.TextInput));
        Assert.Equal("createCheckbox", ControlKinds.FactoryName(ControlKind.CheckBox));
    }

    /// <summary>width окно читает только у трёх типов.</summary>
    [Fact]
    public void Width_IsSupportedByThreeKindsOnly()
    {
        Assert.Equal(
            new[] { ControlKind.Dropdown, ControlKind.Slider, ControlKind.TextInput },
            ControlKinds.All.Where(ControlKinds.HasWidth).OrderBy(k => (int)k));
        Assert.Equal(200, ControlKinds.DefaultWidth);
    }

    /// <summary>button окно читает только у четырёх типов, хотя Python принимает его у восьми.</summary>
    [Fact]
    public void Button_IsSupportedByFourKindsOnly()
    {
        Assert.Equal(
            new[] { ControlKind.CheckBox, ControlKind.RadioButtonGroup, ControlKind.Dropdown, ControlKind.Slider },
            ControlKinds.All.Where(ControlKinds.HasButton).OrderBy(k => (int)k));
    }

    /// <summary>options есть только у радиокнопок и выпадающего списка.</summary>
    [Fact]
    public void Options_AreSupportedByTwoKindsOnly()
    {
        Assert.Equal(
            new[] { ControlKind.RadioButtonGroup, ControlKind.Dropdown },
            ControlKinds.All.Where(ControlKinds.HasOptions).OrderBy(k => (int)k));
    }

    /// <summary>Значение есть у всех, кроме подписи и распорки.</summary>
    [Fact]
    public void Label_And_Empty_HaveNoValue()
    {
        Assert.False(ControlKinds.HasValue(ControlKind.Label));
        Assert.False(ControlKinds.HasValue(ControlKind.Empty));
        Assert.All(ControlKinds.All.Where(k => k is not (ControlKind.Label or ControlKind.Empty)),
            k => Assert.True(ControlKinds.HasValue(k)));
    }

    /// <summary>
    /// Список «проверенных боем» типов и фикстура согласованы: фикстура повторяет набор типов
    /// реальной выборки — ровно пять проверенных, ни одного сверх. Тип, добавленный в фикстуру
    /// без поправки списка (или наоборот), здесь и всплывёт.
    /// </summary>
    [Fact]
    public void ProvenKinds_MatchSampleComposition()
    {
        var sample = new HashSet<ControlKind>();
        foreach (string linkage in SettingsTestData.Linkages)
        {
            SettingsTemplate template = TemplateImporter
                .FromModSettingsDat(SettingsTestData.StateJson, linkage).Template!;
            foreach (SettingControl control in template.AllControls) sample.Add(control.Kind);
        }

        Assert.Equal(
            sample.OrderBy(k => (int)k),
            ControlKinds.All.Where(ControlKinds.ProvenInLiveTemplates).OrderBy(k => (int)k));
    }

    /// <summary>
    /// Ядро и экран говорят одно и то же: тип помечен проверенным в Core тогда и только тогда,
    /// когда у палитры экрана для него есть ненулевое число образцов выборки.
    /// </summary>
    [Fact]
    public void ProvenKinds_AgreeWithPaletteCounts()
    {
        foreach (ControlKind kind in ControlKinds.All)
        {
            var type = Enum.Parse<WotModCockpit.App.Stage4.SettingsControlType>(kind.ToString());
            bool counted = WotModCockpit.App.Stage4.SettingsControlSchema.LiveSamples(type) > 0;

            Assert.True(ControlKinds.ProvenInLiveTemplates(kind) == counted,
                $"{kind}: Core считает тип {(ControlKinds.ProvenInLiveTemplates(kind) ? "" : "не")}проверенным, " +
                $"а палитра показывает {WotModCockpit.App.Stage4.SettingsControlSchema.LiveSamples(type)} образцов");
        }
    }

    // ------------------------------------------------------------------ значения

    /// <summary>Целое и дробное — разные вещи: 3 не равно 3.0 в тексте шаблона.</summary>
    [Fact]
    public void Integer_And_Number_AreDistinct()
    {
        Assert.Equal("3", SettingValue.Of(3L).ToJson());
        Assert.Equal("3.0", SettingValue.Of(3d).ToJson());
        Assert.NotEqual(SettingValue.Of(3L), SettingValue.Of(3d));
    }

    /// <summary>Исходная запись числа сохраняется при разборе.</summary>
    [Theory]
    [InlineData("0.8", "0.8")]
    [InlineData("3.0", "3.0")]
    [InlineData("0.01", "0.01")]
    [InlineData("14", "14")]
    [InlineData("-1", "-1")]
    public void ParsedNumbers_KeepTheirText(string input, string expected)
    {
        Assert.Equal(expected, SettingValue.ParseJson(input).ToJson());
    }

    /// <summary>Канонический JSON сортирует ключи — так же, как json.dumps(sort_keys=True) у API.</summary>
    [Fact]
    public void CanonicalJson_SortsKeys()
    {
        SettingValue value = SettingValue.ParseJson("""{"b":1,"a":{"z":1,"y":2}}""");

        Assert.Equal("""{"a":{"y":2,"z":1},"b":1}""", value.ToCanonicalJson());
        Assert.Equal("""{"b":1,"a":{"z":1,"y":2}}""", value.ToJson());
    }

    /// <summary>Кириллица не экранируется — как в файле состояния (ensure_ascii=False).</summary>
    [Fact]
    public void CyrillicIsNotEscaped()
    {
        Assert.Equal("\"Привет\"", SettingValue.Of("Привет").ToJson());
    }

    /// <summary>Равенство структурное, а не по ссылке.</summary>
    [Fact]
    public void Equality_IsStructural()
    {
        Assert.Equal(SettingValue.ParseJson("""{"a":1,"b":[1,2]}"""),
                     SettingValue.ParseJson("""{"b":[1,2],"a":1}"""));
        Assert.NotEqual(SettingValue.ParseJson("[1,2]"), SettingValue.ParseJson("[2,1]"));
    }

    // ------------------------------------------------------------------ горячие клавиши

    /// <summary>
    /// Значения HotKey в формах, встречающихся в реальных шаблонах, сплющиваются ровно так,
    /// как это делает hotkeys._migrateKeys:
    /// пара L/R превращается в один отрицательный спецкод.
    /// </summary>
    [Theory]
    [InlineData("[[56, 184]]", new[] { HotKeyValue.Alt })]
    [InlineData("[61, [29, 157]]", new[] { 61, HotKeyValue.Ctrl })]
    [InlineData("[-1]", new[] { HotKeyValue.Alt })]
    [InlineData("[42]", new[] { HotKeyValue.Shift })]
    [InlineData("[]", new int[] { })]
    public void HotKey_FlattensLikeMigrateKeys(string json, int[] expected)
    {
        Assert.Equal(expected, HotKeyValue.Flatten(SettingValue.ParseJson(json)));
    }

    /// <summary>Легаси-форма (вложенные списки) распознаётся, современная — нет.</summary>
    [Fact]
    public void LegacyHotKeyForm_IsDetected()
    {
        Assert.True(HotKeyValue.IsLegacyNested(SettingValue.ParseJson("[61, [29, 157]]")));
        Assert.False(HotKeyValue.IsLegacyNested(SettingValue.ParseJson("[61, -2]")));
    }

    /// <summary>Запрещённые коды — ровно те пять, что перечислены в EXCLUDED_KEYS.</summary>
    [Fact]
    public void ExcludedKeys_AreTheApiSet()
    {
        Assert.Equal(new[] { 0, 28, 256, 257, 258 }, HotKeyValue.Excluded.OrderBy(x => x));
    }

    /// <summary>Все значения HotKey фикстуры (легаси и спецкоды) раскладываются без запрещённых кодов.</summary>
    [Fact]
    public void SampleHotKeys_AreValidAfterFlattening()
    {
        var seen = new List<string>();
        foreach (string linkage in SettingsTestData.Linkages)
        {
            SettingsTemplate template = TemplateImporter
                .FromModSettingsDat(SettingsTestData.StateJson, linkage).Template!;
            foreach (SettingControl control in template.AllControls.Where(c => c.Kind == ControlKind.HotKey))
            {
                IReadOnlyList<int> codes = HotKeyValue.Flatten(control.Value);
                Assert.NotEmpty(codes);
                Assert.All(codes, code => Assert.DoesNotContain(code, HotKeyValue.Excluded));
                seen.Add(string.Join("+", codes));
            }
        }
        Assert.Equal(5, seen.Count);
    }

    // ------------------------------------------------------------------ фабрики и копии

    /// <summary>Фабрики выдают контролы, которые валидатор принимает без единого замечания.</summary>
    [Fact]
    public void Factories_ProduceValidControls()
    {
        SettingsTemplate template = FullTemplate();
        TemplateValidationResult result = TemplateValidator.Validate(template);

        Assert.False(result.HasErrors, result.Describe());
        Assert.Equal(0, result.WarningCount);
    }

    /// <summary>Копия шаблона ни в чём не связана с исходным.</summary>
    [Fact]
    public void Clone_IsDeep()
    {
        SettingsTemplate original = FullTemplate();
        SettingsTemplate copy = original.Clone();

        copy.ModDisplayName = "Другое";
        copy.Column1[0].Text = "Изменено";
        copy.Column2.Clear();
        copy.AllControls.First(c => c.Options.Count > 0).Options[0].Label = "Изменено";

        Assert.NotEqual(copy.ModDisplayName, original.ModDisplayName);
        Assert.NotEqual("Изменено", original.Column1[0].Text);
        Assert.NotEmpty(original.Column2);
        Assert.NotEqual("Изменено", original.AllControls.First(c => c.Options.Count > 0).Options[0].Label);
    }

    /// <summary>Копия сериализуется в тот же JSON, что и оригинал.</summary>
    [Fact]
    public void Clone_SerializesIdentically()
    {
        SettingsTemplate original = FullTemplate();
        Assert.Equal(TemplateJson.ToCanonicalJson(original), TemplateJson.ToCanonicalJson(original.Clone()));
    }

    /// <summary>createColorChoice снимает ведущую решётку — повторяем это в фабрике модели.</summary>
    [Fact]
    public void ColorChoice_StripsLeadingHash()
    {
        Assert.Equal("FFCC00", SettingControl.ColorChoice("Цвет", "markColor", "#FFCC00").Value!.AsString);
    }

    /// <summary>Поиск контрола по ключу настройки.</summary>
    [Fact]
    public void FindByVarName_LooksInBothColumns()
    {
        SettingsTemplate template = FullTemplate();

        Assert.NotNull(template.FindByVarName("showGreeting"));
        Assert.NotNull(template.FindByVarName("markColor"));
        Assert.Null(template.FindByVarName("нет такого"));
    }

    /// <summary>Шаблон со всеми 11 типами, который используют несколько тестов.</summary>
    internal static SettingsTemplate FullTemplate()
    {
        var template = new SettingsTemplate
        {
            Linkage = "ru.local.hello_tanker",
            ModDisplayName = "Hello Tanker",
            SettingsVersion = 3,
            HasEnabled = true,
            EnabledDefault = true
        };

        template.Column1.Add(SettingControl.Label("Основное"));
        template.Column1.Add(SettingControl.CheckBox(
            "Показывать приветствие", "showGreeting", true,
            "{HEADER}Приветствие{/HEADER}{BODY}Системное сообщение при входе в ангар{/BODY}"));
        template.Column1.Add(SettingControl.Empty());
        template.Column1.Add(SettingControl.RadioButtonGroup(
            "Куда выводить приветствие", "greetingTarget", 0, "В чат", "В центр экрана", "В журнал"));
        template.Column1.Add(SettingControl.Slider(
            "Задержка", "delaySec", 3, 0, 10, 1, "{{value}} сек."));
        template.Column1.Add(SettingControl.TextInput("Текст приветствия", "greetingText", "Привет, танкист!"));

        template.Column2.Add(SettingControl.Label("Внешний вид"));
        template.Column2.Add(SettingControl.Dropdown("Положение", "position", 1, "Слева", "По центру", "Справа"));
        template.Column2.Add(SettingControl.NumericStepper("Повторов", "repeats", 2, 1, 5, 1));
        template.Column2.Add(SettingControl.ColorChoice("Цвет маркера", "markColor", "FFCC00"));
        template.Column2.Add(SettingControl.HotKey("Горячая клавиша", "toggleKey", 61, HotKeyValue.Ctrl));
        template.Column2.Add(SettingControl.RangeSlider("Диапазон уровней", "tiers", 4, 8, 1, 10, 1));

        return template;
    }
}
