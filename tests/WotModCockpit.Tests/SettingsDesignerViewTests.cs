using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.App.Stage4;
using WotModCockpit.App.ViewModels;
using WotModCockpit.App.Views;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Tests;

/// <summary>
/// ЭТАП 4 — дизайнер настроек ModsSettingsAPI: разметка, проверки формы и генерируемый код.
///
/// <para><b>Почему разметка проверяется запуском.</b> <c>dotnet build</c> про XAML доказывает
/// ровно одно: что он компилируется в BAML. Ни одного <c>{StaticResource}</c> он не проверяет,
/// а неизвестный ключ — это <c>XamlParseException</c> при показе экрана. У дизайнера это
/// особенно опасно: одиннадцать шаблонов редакторов лежат в ОТДЕЛЬНОМ словаре
/// (Views/DataTemplates/ControlEditors.xaml), который подключается через MergedDictionaries
/// и до первого показа контрола нужного типа не разбирается вовсе. Поэтому тест проходит
/// по ВСЕМ одиннадцати типам и заставляет применить каждый шаблон.</para>
///
/// <para><b>Почему код проверяется тем же валидатором, что и рецепты.</b> Тело мода
/// компилируется внутри клиента игры, а не при сборке: ошибка в сгенерированном файле
/// вылезет одной строкой в python.log, когда мод уже у игрока.</para>
/// </summary>
[Collection("WpfBindingTrace")]
public sealed class SettingsDesignerViewTests : IDisposable
{
    private readonly string _temp;

    /// <summary>Создаёт временный каталог под сгенерированные файлы.</summary>
    public SettingsDesignerViewTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "cockpit-designer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_temp);
    }

    /// <summary>Убирает временный каталог.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_temp))
            {
                Directory.Delete(_temp, recursive: true);
            }
        }
        catch (IOException)
        {
            // Временный каталог; не удалился — не беда.
        }
    }

    // ================================================================= разметка

    /// <summary>Экран обязан загружаться: неизвестный ключ ресурса = падение при показе.</summary>
    [Fact]
    public void View_LoadsWithRealApplicationResources()
    {
        object? view = WpfHost.Invoke(() => (object)new SettingsDesignerView());

        Assert.NotNull(view);
        Assert.IsType<SettingsDesignerView>(view);
    }

    /// <summary>
    /// Все одиннадцать типов контролов обязаны и рисоваться в схеме окна, и открываться
    /// в редакторе свойств — без единой ошибки привязки.
    /// </summary>
    [Fact]
    public void View_RendersEveryControlType_WithoutBindingErrors()
    {
        var errors = new List<string>();

        // Импорт читает синтетическую фикстуру, а не modsettings.dat игрока: так панель импорта
        // на любой машине рисует одни и те же строки, а настоящий профиль не читается вовсе.
        string state = SettingsTestData.CopyStateTo(_temp);

        (int types, int realized, int imported) = WpfHost.Invoke(() =>
        {
            using BindingErrorSink sink = BindingErrorSink.Attach(errors);
            using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();

            var vm = provider.GetRequiredService<SettingsDesignerViewModel>();
            vm.ConfirmHandler = static (_, _) => true;
            vm.LiveStatePathOverride = state;
            vm.ModDisplayName = "Тестовый мод";
            vm.Linkage = "ru.local.test_mod";

            var view = new SettingsDesignerView { DataContext = vm };
            using var host = OffscreenHost.Show(view, 1440, 820);

            int count = 0;
            foreach (SettingsControlType type in Enum.GetValues<SettingsControlType>())
            {
                // Раскладываем по обеим колонкам: обе обязаны отрисоваться.
                SettingControlViewModel control = vm.InsertNew(type, count % 2 == 0 ? 1 : 2, -1);
                vm.Selected = control;
                host.Pump();
                count++;
            }

            // Панель кода и панель импорта тоже обязаны открываться без ошибок привязки.
            vm.ShowTemplateJsonCommand.Execute(null);
            host.Pump();

            vm.OpenLiveImportCommand.Execute(null);
            host.Pump();
            int importRows = vm.LiveTemplates.Count;
            Assert.Equal(state, vm.LivePath);
            vm.ShowImport = false;
            host.Pump();

            int realizedItems = CountRealized(view);
            return (count, realizedItems, importRows);
        });

        Assert.Equal(11, types);
        Assert.True(realized >= 11, $"шаблоны превью не применились: создано контейнеров {realized}");
        Assert.Equal(SettingsTestData.Linkages.Count, imported);
        Assert.True(
            errors.Count == 0,
            "ошибки привязки WPF в дизайнере настроек:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// Главное окно с четвёртой вкладкой обязано собираться из НАСТОЯЩЕГО контейнера,
    /// а переключатель экранов — оставаться взаимоисключающим.
    /// </summary>
    [Fact]
    public void MainWindow_WithSettingsTab_LoadsAndSwitchesScreens()
    {
        var errors = new List<string>();

        (bool built, bool buildScreenOff, bool recipesOff, bool backAgain) = WpfHost.Invoke(() =>
        {
            using BindingErrorSink sink = BindingErrorSink.Attach(errors);
            using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();

            // Show() не зовём: Loaded поднял бы тейлер лога и полез в игру.
            var window = provider.GetRequiredService<WotModCockpit.App.MainWindow>();
            var main = provider.GetRequiredService<MainViewModel>();

            bool ok = window.Content is not null && main.HasSettingsDesigner;

            main.IsSettingsScreen = true;
            bool buildOff = !main.IsBuildScreen && !main.IsApiScreen;
            bool recipes = main.Stage3 is null || !main.Stage3.IsRecipesScreen;

            // Возврат на экран сборки обязан гасить дизайнер.
            main.IsBuildScreen = true;
            bool back = !main.IsSettingsScreen && main.IsBuildScreen;

            window.Close();
            return (ok, buildOff, recipes, back);
        });

        Assert.True(built, "окно не собралось либо дизайнер не подключён");
        Assert.True(buildScreenOff, "включённый дизайнер не погасил экран сборки");
        Assert.True(recipesOff, "включённый дизайнер не погасил рецепты");
        Assert.True(backAgain, "возврат на экран сборки не погасил дизайнер");
        Assert.True(
            errors.Count == 0,
            "ошибки привязки WPF в главном окне:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    // ================================================================= проверки формы

    /// <summary>
    /// Проверки, которых у самого ModsSettingsAPI нет ни одной. Каждая из них — это то,
    /// что иначе мододел узнал бы из сломанного окна настроек в игре.
    /// </summary>
    [Fact]
    public void Validation_CatchesWhatTheApiSilentlySwallows()
    {
        string[] messages = WpfHost.Invoke(() =>
        {
            using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();
            var vm = provider.GetRequiredService<SettingsDesignerViewModel>();

            vm.Linkage = "ru.local.test";
            vm.ModDisplayName = "Тест";

            // Два контрола с одинаковым ключом — окно покажет оба, а в настройках
            // останется одно значение.
            SettingControlViewModel first = vm.InsertNew(SettingsControlType.CheckBox, 1, -1);
            first.VarName = "sameName";

            SettingControlViewModel second = vm.InsertNew(SettingsControlType.CheckBox, 2, -1);
            second.VarName = "sameName";

            // Ключ, занятый самим API под тумблер мода.
            SettingControlViewModel reserved = vm.InsertNew(SettingsControlType.CheckBox, 1, -1);
            reserved.VarName = "enabled";

            // Значение выпадающего списка — ИНДЕКС; за пределами списка окно сломается.
            SettingControlViewModel dropdown = vm.InsertNew(SettingsControlType.Dropdown, 2, -1);
            dropdown.IndexValue = 7;

            // Минимум не меньше максимума.
            SettingControlViewModel slider = vm.InsertNew(SettingsControlType.Slider, 1, -1);
            slider.MinimumText = "10";
            slider.MaximumText = "5";

            // Формат без подстановки: метка станет статичной.
            slider.Format = "секунды";

            // Цвет не шестью hex-цифрами.
            SettingControlViewModel color = vm.InsertNew(SettingsControlType.ColorChoice, 2, -1);
            color.ColorValue = "не цвет";

            vm.Revalidate();

            return vm.Issues.Select(i => i.Badge + " :: " + i.Message).ToArray();
        });

        string all = string.Join(Environment.NewLine, messages);

        Assert.Contains("уже занят", all, StringComparison.Ordinal);
        Assert.Contains("'enabled' занят самим API", all, StringComparison.Ordinal);
        Assert.Contains("вне списка", all, StringComparison.Ordinal);
        Assert.Contains("строго меньше максимума", all, StringComparison.Ordinal);
        Assert.Contains("{{value}}", all, StringComparison.Ordinal);
        Assert.Contains("шестью шестнадцатеричными", all, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ошибки блокируют генерацию, а исправление их разблокирует. Иначе кнопка «сгенерировать»
    /// выпускала бы код, который сломает окно настроек у игрока.
    /// </summary>
    [Fact]
    public void Generation_IsBlockedWhileFormHasErrors()
    {
        (bool blocked, bool allowed) = WpfHost.Invoke(() =>
        {
            using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();
            var vm = provider.GetRequiredService<SettingsDesignerViewModel>();

            vm.SetProject(Project(_temp));
            vm.Linkage = "ru.local.test";
            vm.ModDisplayName = "Тест";

            SettingControlViewModel control = vm.InsertNew(SettingsControlType.CheckBox, 1, -1);
            control.VarName = "не идентификатор";
            vm.Revalidate();

            bool no = !vm.CanGenerate;

            control.VarName = "goodName";
            vm.Revalidate();

            return (no, vm.CanGenerate);
        });

        Assert.True(blocked, "генерация не заблокирована при ошибке в форме");
        Assert.True(allowed, "генерация не разблокировалась после исправления");
    }

    // ================================================================= генерация

    /// <summary>
    /// ГЛАВНЫЙ ТЕСТ ЭТАПА: сгенерированный код обязан проходить тот же
    /// <c>tools/check_py2_syntax.py</c>, который стоит гейтом в build.py.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public void GeneratedCode_PassesPython27SyntaxCheck()
    {
        Py27SyntaxGate.Require();

        SettingsCodeResult result = Generate(FullForm());

        Assert.Null(result.Error);
        Assert.Equal(3, result.Files.Count);

        foreach (SettingsCodeFile file in result.Files)
        {
            string path = Path.Combine(_temp, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        (int exitCode, string output) = RecipeToolchain.RunSyntaxCheck(_temp);

        Assert.True(exitCode == 0, "check_py2_syntax.py отверг сгенерированный код:" + Environment.NewLine + output);
    }

    /// <summary>
    /// Сгенерированный модуль обязан объявлять всё, на что опирается остальной мод,
    /// и обязан переживать отсутствие ModsSettingsAPI (правило WOT018).
    /// </summary>
    [Fact]
    public void GeneratedCode_SurvivesMissingApiAndKeepsDefaults()
    {
        SettingsCodeResult result = Generate(FullForm());

        string schema = result.Files.Single(f => f.RelativePath.EndsWith("settings_schema.py", StringComparison.Ordinal)).Content;
        string runtime = result.Files.Single(f => f.RelativePath.EndsWith("/settings.py", StringComparison.Ordinal)).Content;

        // Паспорт формы.
        Assert.Contains("LINKAGE = 'ru.local.hello_tanker'", schema, StringComparison.Ordinal);
        Assert.Contains("SETTINGS_VERSION = 3", schema, StringComparison.Ordinal);
        Assert.Contains("def build_template(values):", schema, StringComparison.Ordinal);

        // Значения контролов подставляются ИЗ СОХРАНЁННЫХ, а не из умолчаний: иначе
        // ModsSettingsAPI при каждой смене шаблона сбрасывал бы выбор игрока.
        Assert.Contains("v('showGreeting')", schema, StringComparison.Ordinal);

        // Ключи, которых не кладёт ни одна фабрика API, дизайнер не порождает сам.
        Assert.DoesNotContain("itemRenderer", schema, StringComparison.Ordinal);

        // WOT018: импорт чужого мода обёрнут.
        Assert.Contains("from gui.modsSettingsApi import g_modsSettingsApi", runtime, StringComparison.Ordinal);
        Assert.Contains("except ImportError:", runtime, StringComparison.Ordinal);

        // Событие широковещательное — фильтр по linkage обязателен.
        Assert.Contains("if linkage != settings_schema.LINKAGE:", runtime, StringComparison.Ordinal);

        // Повторная регистрация добавила бы ВТОРУЮ неснимаемую подписку.
        Assert.Contains("if _registered:", runtime, StringComparison.Ordinal);
    }

    /// <summary>
    /// Шаблон, который уедет в <c>modsettings.dat</c>, обязан быть структурно неотличим
    /// от тех, что пишут реальные моды: ни одного лишнего поля.
    /// </summary>
    [Fact]
    public void TemplateJson_HasOnlyFieldsTheWindowReads()
    {
        string json = SettingsTemplateJson.Render(FullForm());
        JsonNode root = JsonNode.Parse(json)!;

        Assert.Equal("Hello Tanker", root["modDisplayName"]!.GetValue<string>());
        Assert.Equal(3, root["settingsVersion"]!.GetValue<int>());
        Assert.True(root["enabled"]!.GetValue<bool>());

        var byType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (string column in new[] { "column1", "column2" })
        {
            foreach (JsonNode? node in (JsonArray)root[column]!)
            {
                var item = (JsonObject)node!;
                string type = item["type"]!.GetValue<string>();
                byType[type] = new HashSet<string>(item.Select(p => p.Key), StringComparer.Ordinal);
            }
        }

        // Как в реальных шаблонах: CheckBox = text, tooltip, type, value, varName.
        Assert.Equal(
            new[] { "text", "tooltip", "type", "value", "varName" },
            byType["CheckBox"].OrderBy(k => k, StringComparer.Ordinal).ToArray());

        // Slider = format, maximum, minimum, snapInterval, text, tooltip, type, value, varName, width.
        Assert.Equal(
            new[] { "format", "maximum", "minimum", "snapInterval", "text", "type", "value", "varName", "width" },
            byType["Slider"].OrderBy(k => k, StringComparer.Ordinal).ToArray());

        // У подписи не должно быть ни varName, ни value: окно читает returnValue
        // у компонента с varName, а у Label его нет — «Применить» сломалось бы.
        Assert.DoesNotContain("varName", byType["Label"]);
        Assert.DoesNotContain("value", byType["Label"]);

        // Empty — единственный компонент вообще без полей.
        Assert.Equal(new[] { "type" }, byType["Empty"].ToArray());

        // itemRenderer дизайнер не порождает сам (импортированный — сохраняет, см. Stage4FinalTests).
        Assert.DoesNotContain("itemRenderer", json, StringComparison.Ordinal);
    }

    /// <summary>Горячая клавиша генерируется плоской современной формой со спецкодами.</summary>
    [Fact]
    public void HotKey_IsWrittenFlatWithSpecialCodes()
    {
        string json = SettingsTemplateJson.Render(FullForm());
        JsonNode root = JsonNode.Parse(json)!;

        JsonObject hotkey = ((JsonArray)root["column2"]!)
            .Cast<JsonObject>()
            .Single(o => o["type"]!.GetValue<string>() == "HotKey");

        int[] codes = ((JsonArray)hotkey["value"]!).Select(n => n!.GetValue<int>()).ToArray();

        // -2 = любой из левого и правого Ctrl, 61 = KEY_F3.
        Assert.Equal(new[] { -2, 61 }, codes);
    }

    /// <summary>Легаси-форма (вложенные списки), какая бывает в реальных шаблонах, при импорте разворачивается.</summary>
    [Fact]
    public void HotKey_LegacyNestedValue_IsFlattenedOnImport()
    {
        // Ровно то, что лежит в шаблоне ru.example.alpha фикстуры: F3 + (LCTRL или RCTRL).
        var value = new object?[] { 61d, new object?[] { 29d, 157d } };

        IReadOnlyList<int> flat = SettingsHotKey.Flatten(value);

        Assert.Equal(new[] { -2, 61 }, flat.ToArray());
        Assert.Equal("Ctrl + F3", SettingsHotKey.Describe(flat));

        // И обратная форма ALT из [[56, 184]].
        IReadOnlyList<int> alt = SettingsHotKey.Flatten(new object?[] { new object?[] { 56d, 184d } });
        Assert.Equal(new[] { -1 }, alt.ToArray());
    }

    // ================================================================= состояние ModsSettingsAPI

    /// <summary>
    /// Файл состояния читается и НЕ МЕНЯЕТСЯ. Это настройки игрока для всех установленных
    /// модов: испортить их нельзя, поэтому размер и время правки сверяются до и после чтения.
    /// Здесь — на синтетической фикстуре, разложенной во временный каталог.
    /// </summary>
    [Fact]
    public void SampleState_IsParsedAndNeverModified()
    {
        int templates = AssertParsedAndNeverModified(SettingsTestData.CopyStateTo(_temp));

        Assert.Equal(SettingsTestData.Linkages.Count, templates);
    }

    /// <summary>То же для живого <c>modsettings.dat</c> этой машины.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void LiveState_IsParsedAndNeverModified()
    {
        string path = SettingsTestData.RequireLiveState();

        int templates = AssertParsedAndNeverModified(path);

        Skip.If(templates == 0, $"в {path} нет ни одного шаблона — моды с ModsSettingsAPI не установлены");
    }

    /// <summary>
    /// Читает файл состояния читателем кокпита, открывает каждый шаблон в дизайнере
    /// и сверяет, что файл не тронут.
    /// </summary>
    /// <param name="path">Файл состояния.</param>
    /// <returns>Сколько шаблонов прочитано.</returns>
    private static int AssertParsedAndNeverModified(string path)
    {
        var before = new FileInfo(path);
        long size = before.Length;
        DateTime stamp = before.LastWriteTimeUtc;

        var reader = new LiveModSettingsReader();
        bool ok = reader.Read(path);

        var after = new FileInfo(path);

        Assert.True(ok, "файл состояния не прочитан: " + reader.Error);
        Assert.Equal(size, after.Length);
        Assert.Equal(stamp, after.LastWriteTimeUtc);

        // Любой шаблон из файла обязан открываться в дизайнере.
        foreach (LiveModTemplate template in reader.Templates)
        {
            SettingsImportResult result = reader.ToForm(template);

            Assert.Null(result.Error);
            Assert.NotNull(result.Form);
            Assert.Equal(template.Linkage, result.Form!.Linkage);
            Assert.Equal(template.ControlCount, result.Form.Column1.Count + result.Form.Column2.Count);
        }

        return reader.Templates.Count;
    }

    /// <summary>Экран обязан открываться и без установленного ModsSettingsAPI.</summary>
    [Fact]
    public void Designer_SurvivesMissingLiveState()
    {
        var reader = new LiveModSettingsReader();
        bool ok = reader.Read(Path.Combine(_temp, "no-such-modsettings.dat"));

        Assert.False(ok);
        Assert.NotEmpty(reader.Error);
        Assert.Empty(reader.Templates);
        Assert.Empty(reader.BusyHotKeys);
    }

    /// <summary>Круговой рейс: форма → settings.design.json → форма.</summary>
    [Fact]
    public void DesignFile_RoundTripsTheForm()
    {
        SettingsFormSpec form = FullForm();

        string json = SettingsDesignFile.Save(form);
        Assert.True(SettingsDesignFile.TryLoad(json, out SettingsFormSpec? back, out string error), error);

        Assert.NotNull(back);
        Assert.Equal(form.Linkage, back!.Linkage);
        Assert.Equal(form.ModDisplayName, back.ModDisplayName);
        Assert.Equal(form.SettingsVersion, back.SettingsVersion);
        Assert.Equal(form.HasEnabled, back.HasEnabled);
        Assert.Equal(form.Column1.Count, back.Column1.Count);
        Assert.Equal(form.Column2.Count, back.Column2.Count);

        // Самое важное: JSON шаблона после круга обязан совпасть побайтно, иначе
        // ModsSettingsAPI при следующем запуске сочтёт шаблон изменившимся.
        Assert.Equal(SettingsTemplateJson.Render(form), SettingsTemplateJson.Render(back));
    }

    // ================================================================= инфраструктура

    private SettingsCodeResult Generate(SettingsFormSpec form)
    {
        var backend = new BuiltInSettingsDesignerBackend();

        return backend.Generate(form, new SettingsCodeRequest
        {
            ProjectDir = _temp,
            SrcDirRelative = "src",
            PackageName = "hello_tanker",
            LogTag = "HelloTanker",
            ModId = "ru.local.hello_tanker",
            ConfigRelativePath = "mods/configs/hello_tanker/settings.json"
        });
    }

    /// <summary>Форма со ВСЕМИ одиннадцатью типами и кириллицей в подписях.</summary>
    private static SettingsFormSpec FullForm() => new()
    {
        Linkage = "ru.local.hello_tanker",
        ModDisplayName = "Hello Tanker",
        SettingsVersion = 3,
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
                Type = SettingsControlType.TextInput,
                Text = "Текст приветствия",
                VarName = "greetingText",
                Value = "Привет, танкист!",
                Width = 200
            },
            new SettingsControlSpec
            {
                Type = SettingsControlType.Slider,
                Text = "Задержка",
                VarName = "delaySec",
                Value = 3d,
                Minimum = 0,
                Maximum = 10,
                SnapInterval = 0.5,
                Format = "{{value}} сек.",
                Width = 200
            },
            new SettingsControlSpec { Type = SettingsControlType.Empty },
            new SettingsControlSpec
            {
                Type = SettingsControlType.NumericStepper,
                Text = "Повторов",
                VarName = "repeats",
                Value = 2d,
                Minimum = 1,
                Maximum = 5,
                SnapInterval = 1
            }
        ],
        Column2 =
        [
            new SettingsControlSpec
            {
                Type = SettingsControlType.RadioButtonGroup,
                Text = "Способ оповещения",
                VarName = "notifyMode",
                Value = 0d,
                Options =
                [
                    new SettingsOptionSpec("Сообщение в чат «Я обнаружен»"),
                    new SettingsOptionSpec("Команда «Нужна помощь!»")
                ]
            },
            new SettingsControlSpec
            {
                Type = SettingsControlType.Dropdown,
                Text = "Положение панели",
                VarName = "position",
                Value = 1d,
                Width = 200,
                Options =
                [
                    new SettingsOptionSpec("Слева"),
                    new SettingsOptionSpec("По центру"),
                    new SettingsOptionSpec("Справа")
                ]
            },
            new SettingsControlSpec
            {
                Type = SettingsControlType.HotKey,
                Text = "Горячая клавиша",
                VarName = "toggleKey",
                Value = new[] { -2, 61 }
            },
            new SettingsControlSpec
            {
                Type = SettingsControlType.ColorChoice,
                Text = "Цвет маркера",
                VarName = "markColor",
                Value = "FFCC00"
            },
            new SettingsControlSpec
            {
                Type = SettingsControlType.RangeSlider,
                Text = "Дальность",
                VarName = "distance",
                Value = new[] { 20d, 80d },
                Minimum = 0,
                Maximum = 100,
                SnapInterval = 1,
                DivisionStep = 10,
                MinRangeDistance = 5,
                DivisionLabelStep = 20,
                DivisionLabelPostfix = " м"
            }
        ]
    };

    private static ModProject Project(string dir) => new()
    {
        ProjectDir = dir,
        ModId = "ru.local.hello_tanker",
        Version = "1.0.0",
        DisplayName = "Hello Tanker",
        PackageName = "hello_tanker",
        EntryPointModule = "mod_hello_tanker",
        LogTag = "[HelloTanker"
    };

    private static int CountRealized(DependencyObject root)
    {
        int count = 0;
        foreach (ListBoxItem item in Descendants<ListBoxItem>(root))
        {
            if (item.DataContext is SettingControlViewModel)
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < children; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                yield return match;
            }

            foreach (T nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Окно за пределами экрана: разметке нужна настоящая раскладка.</summary>
    private sealed class OffscreenHost : IDisposable
    {
        private readonly Window _window;

        private OffscreenHost(Window window) => _window = window;

        public static OffscreenHost Show(FrameworkElement content, double width, double height)
        {
            var window = new Window
            {
                Content = content,
                Width = width,
                Height = height,
                Left = -32000,
                Top = -32000,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false
            };

            window.Show();
            window.UpdateLayout();
            return new OffscreenHost(window);
        }

        public void Pump()
        {
            for (int i = 0; i < 6; i++)
            {
                Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.SystemIdle);
            }

            _window.UpdateLayout();
        }

        public void Dispose() => _window.Close();
    }

    /// <summary>Ловушка ошибок привязки WPF: они по умолчанию уходят молча.</summary>
    private sealed class BindingErrorSink : IDisposable
    {
        private readonly TraceListener _listener;
        private readonly SourceLevels _previous;

        private BindingErrorSink(TraceListener listener, SourceLevels previous)
        {
            _listener = listener;
            _previous = previous;
        }

        public static BindingErrorSink Attach(List<string> sink)
        {
            PresentationTraceSources.Refresh();

            SourceLevels previous = PresentationTraceSources.DataBindingSource.Switch.Level;
            var listener = new CollectingListener(sink);

            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

            return new BindingErrorSink(listener, previous);
        }

        public void Dispose()
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(_listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = _previous;
            _listener.Dispose();
        }

        private sealed class CollectingListener : TraceListener
        {
            private readonly List<string> _sink;

            public CollectingListener(List<string> sink) => _sink = sink;

            public override void Write(string? message)
            {
            }

            public override void WriteLine(string? message)
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    _sink.Add(message!);
                }
            }
        }
    }
}
