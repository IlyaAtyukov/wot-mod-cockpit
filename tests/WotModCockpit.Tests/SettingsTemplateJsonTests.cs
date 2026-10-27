using System.Text.Json;
using WotModCockpit.App.Stage4;
using WotModCockpit.Core.Settings;

namespace WotModCockpit.Tests;

/// <summary>
/// Данные для тестов дизайнера настроек.
///
/// <para>Ресурс <c>Resources/modsettings.sample.json</c> — СИНТЕТИЧЕСКИЙ файл состояния
/// ModsSettingsAPI, написанный с нуля в формате <c>modsettings.dat</c>: 11 шаблонов вымышленных
/// модов <c>ru.example.*</c>, 50 компонентов, свои тексты. Чужих шаблонов и чужих текстов в нём
/// нет. Он повторяет СТРУКТУРНЫЕ случаи, которые кокпит встретил в реальных шаблонах популярных
/// модов (клиент 1.44), и ровно их набор типов — пять «проверенных боем»:</para>
/// <list type="bullet">
///   <item>легаси-горячие клавиши <c>[61, [29, 157]]</c> и <c>[[56, 184]]</c>, спецкод <c>[-1]</c>;</item>
///   <item>дробный ползунок <c>0.8 … 3.0</c> с шагом <c>0.01</c> и целое значение при дробных границах;</item>
///   <item>шаблоны без <c>settingsVersion</c>, без <c>enabled</c> и с <c>enabled: false</c>;</item>
///   <item>мёртвый <c>itemRenderer</c> у двух выпадающих списков, <c>filename</c> внутри вариантов;</item>
///   <item>посторонний ключ верхнего уровня <c>random</c> и галочку со значением-числом;</item>
///   <item>расхождение шаблона и сохранённых настроек и осиротевший ключ в <c>settings</c>;</item>
///   <item>пустую вторую колонку и пустой <c>storage</c>.</item>
/// </list>
/// <para>Живой файл машины (<c>%APPDATA%\Lesta\MirTankov\mods\modsettings.dat</c>) читают только
/// тесты с трейтом <c>Env=RealMachine</c>, и только при <c>WOTCOCKPIT_REAL_MACHINE=1</c>.
/// Оба файла используются ТОЛЬКО НА ЧТЕНИЕ.</para>
/// </summary>
public static class SettingsTestData
{
    /// <summary>Переменная окружения, которая включает тесты на данных этой машины.</summary>
    public const string RealMachineVariable = TestEnvironment.RealMachineVariable;

    /// <summary>Эталонный шаблон фикстуры: легаси-клавиша, без <c>settingsVersion</c>.</summary>
    public const string ReferenceLinkage = "ru.example.alpha";

    /// <summary>Рецепт R15 — рукописная форма шаблона через <c>templates.createX</c>.</summary>
    public static string RecipeR15Path => Path.Combine(
        RepoRoot, "src", "WotModCockpit.Core", "Recipes", "Recipes", "r15_mods_settings_api.py");

    /// <summary>Корень репозитория, найденный по файлу решения.</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Синтетический файл состояния в тестовых ресурсах.</summary>
    public static string StateCopyPath => Path.Combine(
        RepoRoot, "tests", "WotModCockpit.Tests", "Resources", "modsettings.sample.json");

    /// <summary>Содержимое синтетического файла состояния.</summary>
    public static string StateJson { get; } = File.ReadAllText(StateCopyPath);

    /// <summary>Ключи всех 11 модов фикстуры.</summary>
    public static IReadOnlyList<string> Linkages { get; } = TemplateImporter.ListLinkages(StateJson);

    /// <summary>
    /// Тесты на данных этой машины включены явно: <c>WOTCOCKPIT_REAL_MACHINE=1</c>. Правило одно на
    /// всю сборку — <see cref="TestEnvironment.IsRealMachine"/>.
    /// </summary>
    public static bool RealMachineEnabled => TestEnvironment.IsRealMachine;

    /// <summary>
    /// Живой файл состояния этой машины — там же, где его ищет сам кокпит: профиль «Мира танков»,
    /// затем профиль клиента Wargaming. Пусто — файла нет. Только чтение.
    /// </summary>
    public static string LiveStatePath => LiveModSettingsReader.FindDefaultPath();

    /// <summary>
    /// Пропускает тест, если тесты на данных машины не включены или живого файла нет.
    /// Чужая машина такой тест ПРОПУСКАЕТ с причиной, а не валит и не засчитывает молча.
    /// </summary>
    /// <returns>Путь к живому <c>modsettings.dat</c>.</returns>
    public static string RequireLiveState()
    {
        Skip.IfNot(RealMachineEnabled,
            $"читает живой modsettings.dat этой машины — включается переменной {RealMachineVariable}=1");

        string path = LiveStatePath;
        Skip.If(path.Length == 0,
            @"нет живого modsettings.dat ни в %APPDATA%\Lesta\MirTankov\mods, ни в %APPDATA%\Wargaming.net\WorldOfTanks\mods");
        return path;
    }

    /// <summary>
    /// Кладёт копию фикстуры во временный каталог под именем <c>modsettings.dat</c> — для тестов,
    /// которые читают состояние по пути, как читатель кокпита.
    /// </summary>
    /// <param name="directory">Временный каталог теста.</param>
    /// <returns>Путь к копии.</returns>
    public static string CopyStateTo(string directory)
    {
        string path = Path.Combine(directory, "modsettings.dat");
        File.Copy(StateCopyPath, path, overwrite: true);
        return path;
    }

    /// <summary>Сырой JSON одного шаблона из указанного файла состояния.</summary>
    /// <param name="stateJson">Содержимое файла состояния.</param>
    /// <param name="linkage">Ключ мода.</param>
    /// <returns>JSON шаблона.</returns>
    public static string RawTemplate(string stateJson, string linkage)
    {
        using JsonDocument doc = JsonDocument.Parse(stateJson);
        return doc.RootElement
            .GetProperty(TemplateImporter.TemplatesSection)
            .GetProperty(linkage)
            .GetRawText();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WotModCockpit.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"не нашёл корень репозитория (WotModCockpit.sln) выше {AppContext.BaseDirectory}");
    }
}

/// <summary>
/// ГЛАВНЫЙ ТЕСТ МОДЕЛИ: round-trip по шаблонам формата <c>modsettings.dat</c>.
///
/// <para>Смысл жёсткости этой проверки не в аккуратности. <c>api.compareTemplates</c> при
/// отсутствии <c>settingsVersion</c> сравнивает шаблоны КАК ТЕКСТ JSON, поэтому любая потеря
/// или добавление ключа при обратной записи — это молчаливый сброс настроек игрока. Модель,
/// которая не возвращает шаблон в точности, непригодна.</para>
/// </summary>
public class SettingsTemplateJsonTests
{
    // ------------------------------------------------------------------ round-trip

    /// <summary>Все 11 шаблонов фикстуры разбираются и собираются обратно без единого расхождения.</summary>
    [Fact]
    public void AllSampleTemplates_SurviveRoundTrip()
    {
        Assert.Equal(11, SettingsTestData.Linkages.Count);

        var failures = new List<string>();
        int controls = 0;

        foreach (string linkage in SettingsTestData.Linkages)
        {
            string raw = SettingsTestData.RawTemplate(SettingsTestData.StateJson, linkage);
            TemplateImportResult import = TemplateImporter.FromModSettingsDat(SettingsTestData.StateJson, linkage);

            Assert.True(import.Success, $"{linkage}: {import.Describe()}");
            controls += import.Template!.ControlCount;

            string expected = TemplateJson.Canonicalize(raw);
            string actual = TemplateJson.ToCanonicalJson(import.Template);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                failures.Add($"{linkage}:{Environment.NewLine}  было:  {expected}{Environment.NewLine}  стало: {actual}");
        }

        Assert.True(failures.Count == 0,
            $"расхождений {failures.Count} из {SettingsTestData.Linkages.Count}:{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures));
        Assert.Equal(50, controls);
    }

    /// <summary>
    /// Тот же прогон по ЖИВОМУ файлу этой машины: каждый шаблон, который туда записали
    /// установленные моды, переживает разбор и обратную запись.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void LiveMachineState_SurvivesRoundTrip()
    {
        string path = SettingsTestData.RequireLiveState();

        string json = File.ReadAllText(path);
        IReadOnlyList<string> linkages = TemplateImporter.ListLinkages(json);
        Skip.If(linkages.Count == 0, $"в {path} нет ни одного шаблона — моды с ModsSettingsAPI не установлены");

        var failures = new List<string>();
        foreach (string linkage in linkages)
        {
            TemplateImportResult import = TemplateImporter.FromModSettingsDat(json, linkage);
            Assert.True(import.Success, $"{linkage}: {import.Describe()}");

            string expected = TemplateJson.Canonicalize(SettingsTestData.RawTemplate(json, linkage));
            string actual = TemplateJson.ToCanonicalJson(import.Template!);
            if (!string.Equals(expected, actual, StringComparison.Ordinal)) failures.Add(linkage);
        }
        Assert.True(failures.Count == 0, "разошлись: " + string.Join(", ", failures));
    }

    /// <summary>
    /// Фикстура остаётся ПРЕДСТАВИТЕЛЬНОЙ для живого файла: всё, из чего сложены шаблоны
    /// установленных модов, в ней тоже встречается.
    ///
    /// <para>Сравнивать байты или linkage бессмысленно: фикстура синтетическая, а живой файл
    /// ModsSettingsAPI переписывает при каждом изменении настроек. Проверяется то, ради чего
    /// фикстура существует, — СТРУКТУРА: ключи шаблона, типы компонентов, ключи компонента и
    /// ключи варианта. Новый ключ или тип в живом файле значит, что у остальных тестов появилась
    /// слепая зона, и в фикстуру нужно добавить такой случай. Компоненты типов вне выборки
    /// реальных шаблонов не сравниваются: их фикстура не держит намеренно.</para>
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void SampleFixture_CoversLiveFileStructure()
    {
        string path = SettingsTestData.RequireLiveState();

        StateShape live = StateShape.Of(File.ReadAllText(path));
        Skip.If(live.Templates == 0, $"в {path} нет ни одного шаблона — сравнивать не с чем");

        StateShape sample = StateShape.Of(SettingsTestData.StateJson);

        var missing = new List<string>();
        missing.AddRange(live.TopLevelKeys.Except(sample.TopLevelKeys).Select(k => "секция '" + k + "'"));
        missing.AddRange(live.TemplateKeys.Except(sample.TemplateKeys).Select(k => "ключ шаблона '" + k + "'"));
        missing.AddRange(live.ControlTypes.Except(sample.ControlTypes).Select(k => "тип '" + k + "'"));
        missing.AddRange(live.ControlKeys.Except(sample.ControlKeys).Select(k => "ключ компонента '" + k + "'"));
        missing.AddRange(live.OptionKeys.Except(sample.OptionKeys).Select(k => "ключ варианта '" + k + "'"));

        Assert.True(missing.Count == 0,
            "в живом файле есть то, чего нет в Resources/modsettings.sample.json — добавьте такой случай " +
            "в фикстуру своими словами: " + string.Join(", ", missing));
    }

    // ------------------------------------------------------------------ отдельные места

    /// <summary>
    /// Дробные границы не превращаются в целые. ru.example.ui-scale держит minimum 0.8 и
    /// maximum 3.0: если записать 3 вместо 3.0, JSON шаблона изменится и API сбросит настройки.
    /// </summary>
    [Fact]
    public void FractionalRange_KeepsPythonNumberForm()
    {
        SettingsTemplate template = Sample("ru.example.ui-scale");
        SettingControl slider = template.AllControls.First(c => c.Kind == ControlKind.Slider);

        Assert.Equal(SettingValueKind.Number, slider.Maximum!.Kind);
        Assert.Contains("\"maximum\":3.0", TemplateJson.ToJson(template), StringComparison.Ordinal);
        Assert.Contains("\"minimum\":0.8", TemplateJson.ToJson(template), StringComparison.Ordinal);
    }

    /// <summary>Целое остаётся целым: 1 не должно стать 1.0.</summary>
    [Fact]
    public void IntegerRange_StaysInteger()
    {
        SettingControl slider = SettingControl.Slider("Задержка", "delaySec", 3, 0, 10, 1);
        string json = TemplateJson.ControlToValue(slider).ToJson();

        Assert.Contains("\"minimum\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"maximum\":10", json, StringComparison.Ordinal);
        Assert.DoesNotContain(".0", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Мёртвое поле itemRenderer при импорте сохраняется: выбросить чужой ключ значило бы
    /// изменить шаблон и сбросить настройки игрока.
    /// </summary>
    [Fact]
    public void DeadItemRenderer_IsPreservedOnImport()
    {
        SettingsTemplate template = Sample("ru.example.hit_log");
        SettingControl dropdown = template.AllControls.First(c => c.VarName == "displayMode");

        Assert.Equal(ControlKind.Dropdown, dropdown.Kind);
        Assert.True(dropdown.Extra.ContainsKey(TemplateJson.ItemRendererKey));
        Assert.Equal("DropDownListItemRendererSound", dropdown.Extra[TemplateJson.ItemRendererKey].AsString);
    }

    /// <summary>Посторонний ключ верхнего уровня ('random' у ru.example.paint) не теряется.</summary>
    [Fact]
    public void ExtraTemplateKey_IsPreserved()
    {
        SettingsTemplate template = Sample("ru.example.paint");

        Assert.Contains("random", template.ExtraOrder);
        Assert.Contains("\"random\":", TemplateJson.ToJson(template), StringComparison.Ordinal);
    }

    /// <summary>
    /// Посторонний ключ внутри варианта выбора не теряется: ru.example.tank_card держит в
    /// вариантах 'filename' и пользуется вариантом как носителем данных — так делают и
    /// реальные моды.
    /// </summary>
    [Fact]
    public void ExtraOptionKey_IsPreserved()
    {
        SettingsTemplate template = Sample("ru.example.tank_card");
        SettingControl control = template.FindByVarName("layoutId")!;

        Assert.Equal(3, control.Options.Count);
        Assert.All(control.Options, o => Assert.Contains("filename", o.ExtraOrder));
        Assert.Contains("\"filename\":", TemplateJson.ToJson(template), StringComparison.Ordinal);
    }

    /// <summary>Мод без тумблера (нет ключа 'enabled') не получает его при обратной записи.</summary>
    [Fact]
    public void TemplateWithoutEnabled_DoesNotGainIt()
    {
        SettingsTemplate template = Sample("ru.example.stats");

        Assert.False(template.HasEnabled);
        Assert.DoesNotContain("\"enabled\"", TemplateJson.ToJson(template), StringComparison.Ordinal);
    }

    /// <summary>Мод с тумблером сохраняет его значение.</summary>
    [Fact]
    public void TemplateWithEnabled_KeepsIt()
    {
        SettingsTemplate template = Sample(SettingsTestData.ReferenceLinkage);

        Assert.True(template.HasEnabled);
        Assert.True(template.EnabledDefault);
        Assert.Null(template.SettingsVersion);
    }

    /// <summary>Выключенный по умолчанию тумблер остаётся выключенным.</summary>
    [Fact]
    public void TemplateWithDisabledToggle_KeepsFalse()
    {
        SettingsTemplate template = Sample("ru.example.ui-scale");

        Assert.True(template.HasEnabled);
        Assert.False(template.EnabledDefault);
        Assert.Contains("\"enabled\":false", TemplateJson.ToJson(template), StringComparison.Ordinal);
    }

    /// <summary>settingsVersion читается там, где он есть (3 мода из 11), включая нулевой.</summary>
    [Fact]
    public void SettingsVersion_IsReadWhereItExists()
    {
        Assert.Equal(2, Sample("ru.example.reticle").SettingsVersion);
        Assert.Equal(0, Sample("ru.example.tank_card").SettingsVersion);
        Assert.Equal(4, Sample("ru.example.stats").SettingsVersion);

        int withVersion = SettingsTestData.Linkages.Count(l => Sample(l).SettingsVersion is not null);
        Assert.Equal(3, withVersion);
    }

    /// <summary>Empty пишется ровно одним ключом — так же, как это делает createEmpty().</summary>
    [Fact]
    public void Empty_WritesTypeOnly()
    {
        Assert.Equal("{\"type\":\"Empty\"}", TemplateJson.ControlToValue(SettingControl.Empty()).ToJson());
    }

    /// <summary>Label не получает ни varName, ни value.</summary>
    [Fact]
    public void Label_WritesTextAndTooltipOnly()
    {
        SettingValue value = TemplateJson.ControlToValue(SettingControl.Label("Основное", "подсказка"));

        Assert.Equal(new[] { "type", "text", "tooltip" }, value.Entries.Select(e => e.Key));
    }

    /// <summary>
    /// Отсутствующий ключ text не выдумывается: пустая подпись и отсутствие подписи —
    /// разные шаблоны для compareTemplates.
    /// </summary>
    [Fact]
    public void MissingTextKey_IsNotInvented()
    {
        SettingValue parsed = SettingValue.ParseJson(
            """{"type":"CheckBox","varName":"a","value":true}""");
        SettingControl control = TemplateJson.ControlFromValue(parsed);

        Assert.False(control.HasTextKey);
        Assert.Equal(parsed.ToCanonicalJson(), TemplateJson.ControlToValue(control).ToCanonicalJson());
    }

    /// <summary>Пустой список options сохраняется как пустой, а не исчезает.</summary>
    [Fact]
    public void EmptyOptionsKey_IsPreserved()
    {
        SettingValue parsed = SettingValue.ParseJson(
            """{"type":"Dropdown","text":"x","varName":"a","value":0,"options":[]}""");
        SettingControl control = TemplateJson.ControlFromValue(parsed);

        Assert.True(control.HasOptionsKey);
        Assert.Equal(parsed.ToCanonicalJson(), TemplateJson.ControlToValue(control).ToCanonicalJson());
    }

    /// <summary>Неизвестный тип не выбрасывается, а сохраняется вместе с исходной строкой.</summary>
    [Fact]
    public void UnknownType_IsPreservedNotDropped()
    {
        var problems = new List<string>();
        SettingValue parsed = SettingValue.ParseJson(
            """{"type":"SuperWidget","text":"x","varName":"a","value":1}""");
        SettingControl control = TemplateJson.ControlFromValue(parsed, "column1[0]", problems);

        Assert.Equal(ControlKind.Unknown, control.Kind);
        Assert.Equal("SuperWidget", control.RawTypeName);
        Assert.Single(problems);
        Assert.Equal(parsed.ToCanonicalJson(), TemplateJson.ControlToValue(control).ToCanonicalJson());
    }

    // ------------------------------------------------------------------ getSettingsFromTemplate

    /// <summary>
    /// <see cref="SettingsTemplate.BuildDefaults"/> обязан повторять <c>api.getSettingsFromTemplate</c>:
    /// набор ключей, выведенный из шаблона, обязан быть подмножеством того, что API хранит
    /// в секции settings того же файла состояния.
    /// </summary>
    [Fact]
    public void BuildDefaults_MatchesWhatApiStores()
    {
        var mismatches = new List<string>();

        foreach (string linkage in SettingsTestData.Linkages)
        {
            SettingsTemplate template = Sample(linkage);
            var derived = template.BuildDefaults().Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var stored = TemplateImporter.ReadSavedSettings(SettingsTestData.StateJson, linkage)
                .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

            derived.ExceptWith(stored);
            if (derived.Count > 0) mismatches.Add($"{linkage}: лишние ключи {string.Join(", ", derived)}");
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    /// <summary>
    /// Обратное расхождение — тоже факт про API, а не про модель: ключи удалённых контролов
    /// не чистятся, пока шаблон не заменён. В фикстуре так устроен ru.example.tank_card
    /// (settingsVersion=0, никогда не поднимался): в его настройках лежит осиротевший 'inReplays'.
    /// </summary>
    [Fact]
    public void OrphanSetting_SurvivesInSavedState()
    {
        var derived = Sample("ru.example.tank_card").BuildDefaults().Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var stored = TemplateImporter.ReadSavedSettings(SettingsTestData.StateJson, "ru.example.tank_card")
            .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        stored.ExceptWith(derived);
        Assert.Contains("inReplays", stored);
    }

    /// <summary>Тумблер идёт в словаре настроек первым — как в getSettingsFromTemplate.</summary>
    [Fact]
    public void BuildDefaults_PutsEnabledFirst()
    {
        SettingsTemplate template = Sample(SettingsTestData.ReferenceLinkage);
        IReadOnlyList<KeyValuePair<string, SettingValue>> defaults = template.BuildDefaults();

        Assert.Equal(TemplateJson.EnabledKey, defaults[0].Key);
        Assert.Equal(
            new[] { "enabled", "outputMode", "inTraining", "intervalMin", "showKey" },
            defaults.Select(p => p.Key));
    }

    /// <summary>Label и Empty в словарь настроек не попадают.</summary>
    [Fact]
    public void BuildDefaults_SkipsLabelAndEmpty()
    {
        var template = new SettingsTemplate { Linkage = "ru.local.x", ModDisplayName = "X", HasEnabled = false };
        template.Column1.Add(SettingControl.Label("Заголовок"));
        template.Column1.Add(SettingControl.Empty());
        template.Column1.Add(SettingControl.CheckBox("Галка", "flag", true));

        Assert.Equal(new[] { "flag" }, template.BuildDefaults().Select(p => p.Key));
    }

    private static SettingsTemplate Sample(string linkage) =>
        TemplateImporter.FromModSettingsDat(SettingsTestData.StateJson, linkage).Template!;

    /// <summary>
    /// Из чего сложен файл состояния: секции, ключи шаблонов, типы и ключи компонентов,
    /// ключи вариантов. Значения и тексты не учитываются.
    /// </summary>
    private sealed class StateShape
    {
        /// <summary>Секции верхнего уровня (<c>settings</c>, <c>storage</c>, <c>templates</c>).</summary>
        public HashSet<string> TopLevelKeys { get; } = new(StringComparer.Ordinal);

        /// <summary>Ключи шаблонов (<c>modDisplayName</c>, <c>settingsVersion</c>, посторонние).</summary>
        public HashSet<string> TemplateKeys { get; } = new(StringComparer.Ordinal);

        /// <summary>Строки поля <c>type</c> компонентов.</summary>
        public HashSet<string> ControlTypes { get; } = new(StringComparer.Ordinal);

        /// <summary>Ключи компонентов.</summary>
        public HashSet<string> ControlKeys { get; } = new(StringComparer.Ordinal);

        /// <summary>Ключи вариантов выбора.</summary>
        public HashSet<string> OptionKeys { get; } = new(StringComparer.Ordinal);

        /// <summary>Сколько шаблонов в файле.</summary>
        public int Templates { get; private set; }

        /// <summary>Снимает структуру с текста файла состояния.</summary>
        /// <param name="stateJson">Содержимое файла.</param>
        /// <returns>Структура.</returns>
        public static StateShape Of(string stateJson)
        {
            var shape = new StateShape();
            using JsonDocument doc = JsonDocument.Parse(stateJson);

            foreach (JsonProperty section in doc.RootElement.EnumerateObject())
            {
                shape.TopLevelKeys.Add(section.Name);
            }

            if (!doc.RootElement.TryGetProperty(TemplateImporter.TemplatesSection, out JsonElement templates) ||
                templates.ValueKind != JsonValueKind.Object)
            {
                return shape;
            }

            foreach (JsonProperty template in templates.EnumerateObject())
            {
                if (template.Value.ValueKind != JsonValueKind.Object) continue;
                shape.Templates++;

                foreach (JsonProperty key in template.Value.EnumerateObject())
                {
                    shape.TemplateKeys.Add(key.Name);
                    if (key.Name is not ("column1" or "column2") || key.Value.ValueKind != JsonValueKind.Array) continue;

                    foreach (JsonElement control in key.Value.EnumerateArray())
                    {
                        shape.AddControl(control);
                    }
                }
            }

            return shape;
        }

        private void AddControl(JsonElement control)
        {
            if (control.ValueKind != JsonValueKind.Object) return;

            // Типы вне выборки реальных шаблонов (Label, TextInput, ColorChoice…) фикстура
            // намеренно не держит: их покрывают образцы в коде (SettingsDesignSamples.AllControlTypes).
            // Неизвестную API строку типа, наоборот, учитываем — такой случай фикстуре нужен.
            string? type = control.TryGetProperty("type", out JsonElement typeNode) &&
                           typeNode.ValueKind == JsonValueKind.String
                ? typeNode.GetString()
                : null;
            if (ControlKinds.TryParse(type, out ControlKind kind) && !ControlKinds.ProvenInLiveTemplates(kind)) return;

            foreach (JsonProperty key in control.EnumerateObject())
            {
                ControlKeys.Add(key.Name);

                if (key.Name == "type" && key.Value.ValueKind == JsonValueKind.String)
                {
                    ControlTypes.Add(key.Value.GetString()!);
                }

                if (key.Name != "options" || key.Value.ValueKind != JsonValueKind.Array) continue;

                foreach (JsonElement option in key.Value.EnumerateArray())
                {
                    if (option.ValueKind != JsonValueKind.Object) continue;
                    foreach (JsonProperty optionKey in option.EnumerateObject())
                    {
                        OptionKeys.Add(optionKey.Name);
                    }
                }
            }
        }
    }
}
