using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.App.Stage4;
using WotModCockpit.App.ViewModels;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Settings;

namespace WotModCockpit.Tests;

/// <summary>
/// ЭТАП 4, финальный проход. Каждый тест закрывает дефект, найденный проверяющими, и
/// падает, если дефект вернётся. Проверки идут НАСТОЯЩИМ путём «экран → шов → генератор
/// Core», а чужие шаблоны берутся из синтетического файла состояния в тестовых ресурсах
/// (<c>Resources/modsettings.sample.json</c>), который повторяет структуру реальных.
/// </summary>
public sealed class Stage4FinalTests : IDisposable
{
    private readonly string _temp;

    /// <summary>Заводит временное дерево проекта.</summary>
    public Stage4FinalTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "cockpit-stage4final-" + Guid.NewGuid().ToString("N")[..8]);
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

    // ============================================================ форма чисел

    /// <summary>
    /// ДРОБНЫЙ ПОЛЗУНОК НЕ ВЫРОЖДАЕТСЯ В ЦЕЛЫЙ. Прозрачность 0…1 с шагом 0,05 и умолчанием 1
    /// обязана приехать в питон как <c>1.0</c>: <c>1</c> — это <c>int</c>, и в Python 2.7
    /// <c>settings.get('opacity') / 2</c> дало бы 0 вместо 0.5, а после первого «Применить»
    /// то же значение прилетело бы из Flash уже <c>float</c> — тип настройки скакал бы.
    /// </summary>
    [Fact]
    public void FractionalSlider_KeepsFloatForm_AllTheWayToPython()
    {
        (string json, string schema) = WpfHost.Invoke(() =>
        {
            SettingsDesignerViewModel vm = Screen();

            SettingControlViewModel slider = vm.InsertNew(SettingsControlType.Slider, 1, -1);
            slider.Text = "Прозрачность";
            slider.VarName = "opacity";
            slider.MinimumText = "0";
            slider.MaximumText = "1";
            slider.SnapText = "0.05";
            slider.NumberText = "1";
            vm.Revalidate();

            Assert.True(vm.CanGenerate, vm.StatusText);
            vm.GenerateCodeCommand.Execute(null);

            return (Artifact(vm, "шаблон (JSON)").Content, Artifact(vm, "settings_schema.py").Content);
        });

        Assert.Contains("\"minimum\": 0.0", json, StringComparison.Ordinal);
        Assert.Contains("\"maximum\": 1.0", json, StringComparison.Ordinal);
        Assert.Contains("\"snapInterval\": 0.05", json, StringComparison.Ordinal);
        Assert.Contains("\"value\": 1.0", json, StringComparison.Ordinal);

        // Заводское значение живёт единственным списком DEFAULTS — там оно тоже float.
        Assert.Contains("'opacity': 1.0", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("'opacity': 1,", schema, StringComparison.Ordinal);
    }

    /// <summary>
    /// Обратная сторона: ЦЕЛЫЙ ползунок остаётся целым. Приписка <c>.0</c> всем подряд
    /// разошлась бы с реальными шаблонами, где целые границы с шагом 1 хранятся как <c>int</c>,
    /// а изменение JSON шаблона без <c>settingsVersion</c> — это сброс настроек игрока.
    /// </summary>
    [Fact]
    public void IntegerSlider_StaysInteger()
    {
        string json = WpfHost.Invoke(() =>
        {
            SettingsDesignerViewModel vm = Screen();

            SettingControlViewModel slider = vm.InsertNew(SettingsControlType.Slider, 1, -1);
            slider.Text = "Задержка";
            slider.VarName = "delaySec";
            slider.MinimumText = "0";
            slider.MaximumText = "100";
            slider.SnapText = "1";
            slider.NumberText = "50";
            vm.Revalidate();

            vm.GenerateCodeCommand.Execute(null);
            return Artifact(vm, "шаблон (JSON)").Content;
        });

        Assert.Contains("\"minimum\": 0,", json, StringComparison.Ordinal);
        Assert.Contains("\"maximum\": 100,", json, StringComparison.Ordinal);
        Assert.Contains("\"value\": 50", json, StringComparison.Ordinal);
        Assert.DoesNotContain(".0", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Чужой дробный ползунок переживает дорогу «modsettings.dat → форма → шаблон»
    /// БЕЗ изменения формы чисел: у ru.example.ui-scale это minimum 0.8 и maximum 3.0.
    /// </summary>
    [Fact]
    public void ImportedFractionalSlider_SurvivesTheSeamUnchanged()
    {
        SettingsFormSpec form = SampleForm("ru.example.ui-scale", out _);
        string json = SettingsTemplateJson.Render(form);

        Assert.Contains("\"minimum\": 0.8", json, StringComparison.Ordinal);
        Assert.Contains("\"maximum\": 3.0", json, StringComparison.Ordinal);
        Assert.Contains("\"snapInterval\": 0.01", json, StringComparison.Ordinal);
        Assert.Contains("\"value\": 1.0", json, StringComparison.Ordinal);
    }

    // ============================================================ чужие ключи

    /// <summary>
    /// НИ ОДИН КЛЮЧ ЧУЖОГО ШАБЛОНА НЕ ТЕРЯЕТСЯ на мосту «экран → Core». Раньше мост
    /// молча выбрасывал <c>itemRenderer</c> у выпадающих списков и <c>filename</c>
    /// внутри вариантов — оба встречаются у реальных модов, — хотя про такой же ключ
    /// ВЕРХНЕГО уровня честно предупреждал. Шаблон без <c>settingsVersion</c> сравнивается
    /// ТЕКСТОМ, и потерянный ключ — это сброс настроек игрока у чужого мода.
    /// </summary>
    [Fact]
    public void EverySampleTemplate_KeepsForeignKeysInsideControlsAndOptions()
    {
        var lost = new List<string>();
        int controls = 0;
        int optionsWithExtra = 0;
        int controlsWithExtra = 0;

        foreach (string linkage in SettingsTestData.Linkages)
        {
            var original = (JsonObject)JsonNode.Parse(
                SettingsTestData.RawTemplate(SettingsTestData.StateJson, linkage))!;

            SettingsFormSpec form = SampleForm(linkage, out _);
            var rendered = (JsonObject)JsonNode.Parse(SettingsTemplateJson.Render(form))!;

            foreach (string column in new[] { "column1", "column2" })
            {
                if (original[column] is not JsonArray was || rendered[column] is not JsonArray now)
                {
                    continue;
                }

                Assert.Equal(was.Count, now.Count);

                for (int i = 0; i < was.Count; i++)
                {
                    var before = (JsonObject)was[i]!;
                    var after = (JsonObject)now[i]!;
                    controls++;

                    foreach (string key in before.Select(p => p.Key))
                    {
                        if (after[key] is null)
                        {
                            lost.Add($"{linkage}.{column}[{i}].{key}");
                        }
                    }

                    if (before.Any(p => p.Key is "itemRenderer"))
                    {
                        controlsWithExtra++;
                    }

                    if (before["options"] is JsonArray beforeOptions && after["options"] is JsonArray afterOptions)
                    {
                        for (int o = 0; o < beforeOptions.Count && o < afterOptions.Count; o++)
                        {
                            var wasOption = (JsonObject)beforeOptions[o]!;
                            var nowOption = (JsonObject)afterOptions[o]!;

                            foreach (string key in wasOption.Select(p => p.Key))
                            {
                                if (nowOption[key] is null)
                                {
                                    lost.Add($"{linkage}.{column}[{i}].options[{o}].{key}");
                                }
                                else if (key is "filename")
                                {
                                    optionsWithExtra++;
                                }
                            }
                        }
                    }
                }
            }
        }

        Assert.True(lost.Count == 0, "потеряны ключи: " + string.Join(", ", lost));
        Assert.Equal(50, controls);

        // Фикстура держит ровно те случаи, о которых говорили проверяющие: два itemRenderer
        // (ru.example.hit_log и ru.example.notices) и три filename в вариантах ru.example.tank_card.
        Assert.Equal(2, controlsWithExtra);
        Assert.Equal(3, optionsWithExtra);
    }

    /// <summary>
    /// Чужой ключ доезжает не только до JSON, но и до ПИТОНА: генератор Core печатает
    /// шаблон литеральными словарями, и <c>itemRenderer</c> обязан быть в них.
    /// </summary>
    [Fact]
    public void ForeignKey_ReachesGeneratedPython()
    {
        SettingsFormSpec form = SampleForm("ru.example.hit_log", out _);
        SettingsCodeResult result = Backend().Generate(form with { Linkage = "ru.local.copy" }, Request());

        Assert.Null(result.Error);
        string schema = result.Files.Single(f => f.RelativePath.EndsWith("settings_schema.py", StringComparison.Ordinal)).Content;

        Assert.Contains("'itemRenderer': u'DropDownListItemRendererSound'", schema, StringComparison.Ordinal);
    }

    /// <summary>
    /// ИМПОРТ ГОВОРИТ ПРАВДУ О ПРИВЕДЕНИЯХ. У ru.example.paint значение галочки записано
    /// числом (<c>0</c>), как бывает у реальных модов, и дизайнер приводит его к <c>false</c>.
    /// Приведение верное, но молчать о нём нельзя: человек сверяет импортированную форму
    /// с чужим модом.
    /// </summary>
    [Fact]
    public void Import_ReportsCheckBoxNumberToBoolCoercion()
    {
        SampleForm("ru.example.paint", out List<string> notes);

        Assert.Contains(notes, n => n.Contains("'value'", StringComparison.Ordinal)
                                    && n.Contains("0 → false", StringComparison.Ordinal));
    }

    /// <summary>
    /// А там, где терять нечего, замечаний быть не должно: все шаблоны фикстуры, кроме
    /// ru.example.paint, не содержат ни одного отброшенного ключа, и выдумывать их нельзя —
    /// иначе предупреждения перестанут читать.
    /// </summary>
    [Fact]
    public void Import_DoesNotInventLostKeys()
    {
        var noisy = new List<string>();

        foreach (string linkage in SettingsTestData.Linkages)
        {
            SampleForm(linkage, out List<string> notes);

            foreach (string note in notes.Where(n => n.Contains("потерян", StringComparison.Ordinal)))
            {
                // Единственная законная потеря — посторонний ключ ВЕРХНЕГО уровня
                // ('random' у ru.example.paint): дизайнер его не порождает.
                if (!note.Contains("посторонний ключ 'random'", StringComparison.Ordinal))
                {
                    noisy.Add(linkage + ": " + note);
                }
            }
        }

        Assert.True(noisy.Count == 0, string.Join(Environment.NewLine, noisy));
    }

    // ============================================================ ширина

    /// <summary>
    /// НОВЫЙ ПОЛЗУНОК НЕ ПОЛУЧАЕТ КЛЮЧ <c>width</c>. Фабрики <c>templates.createSlider</c>
    /// и родня при <c>width=None</c> ключ не кладут — окно подставляет 200 само. Кокпит,
    /// пишущий ключ, которого API бы не написал, добавляет лишнее расхождение JSON у модов
    /// без <c>settingsVersion</c>; в реальных шаблонах width есть далеко не у всех компонентов.
    /// </summary>
    [Fact]
    public void NewSlider_DoesNotWriteWidthKey()
    {
        (string json, bool usesDefault) = WpfHost.Invoke(() =>
        {
            SettingsDesignerViewModel vm = Screen();

            SettingControlViewModel slider = vm.InsertNew(SettingsControlType.Slider, 1, -1);
            slider.VarName = "delaySec";
            vm.Revalidate();

            return (SettingsTemplateJson.Render(vm.ToSpec()), slider.UsesDefaultWidth);
        });

        Assert.DoesNotContain("\"width\"", json, StringComparison.Ordinal);
        Assert.True(usesDefault, "плейсхолдер «200» не показался бы: поле ширины не пустое");
    }

    /// <summary>Заданная руками ширина по-прежнему уезжает в шаблон.</summary>
    [Fact]
    public void ExplicitWidth_IsStillWritten()
    {
        string json = WpfHost.Invoke(() =>
        {
            SettingsDesignerViewModel vm = Screen();

            SettingControlViewModel slider = vm.InsertNew(SettingsControlType.Slider, 1, -1);
            slider.VarName = "delaySec";
            slider.WidthText = "310";
            vm.Revalidate();

            return SettingsTemplateJson.Render(vm.ToSpec());
        });

        Assert.Contains("\"width\": 310", json, StringComparison.Ordinal);
    }

    // ============================================================ горячая клавиша

    /// <summary>
    /// ПОМОЩНИК ДЛЯ ГОРЯЧЕЙ КЛАВИШИ ЕСТЬ ВСЕГДА. <c>settings.py</c> создаётся ОДИН раз,
    /// поэтому условная генерация помощника означала бы, что контрол HotKey, добавленный
    /// завтра, помощника уже не получит никогда. Значение HotKey — просто список кодов,
    /// и без <c>checkKeyset</c> мододел не знает, что с ним делать.
    /// </summary>
    [Fact]
    public void HotKeyHelper_IsAlwaysInUserModule()
    {
        SettingsCodeResult result = Backend().Generate(SimpleForm(), Request());
        string runtime = result.Files.Single(f => f.RelativePath.EndsWith("/settings.py", StringComparison.Ordinal)).Content;

        Assert.Contains("def is_hotkey_down(name):", runtime, StringComparison.Ordinal);
        Assert.Contains("g_modsSettingsApi.checkKeyset(keys)", runtime, StringComparison.Ordinal);

        // Запасной путь без ModsSettingsAPI: спецкоды -1 ALT / -2 CTRL / -3 SHIFT
        // раскрываются в пары «левая-правая».
        Assert.Contains("{-1: (56, 184), -2: (29, 157), -3: (42, 54)}", runtime, StringComparison.Ordinal);
        Assert.Contains("BigWorld.isKeyDown", runtime, StringComparison.Ordinal);

        // WOT018: импорт чужого мода обёрнут и здесь.
        Assert.Contains("except ImportError:", runtime, StringComparison.Ordinal);
    }

    /// <summary>Сам ресурс-каркас проходит проверку синтаксиса Python 2.7.</summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public void UserModuleTemplate_PassesPython27SyntaxCheck()
    {
        Py27SyntaxGate.Require();

        string path = Path.Combine(_temp, "settings.py");
        File.WriteAllText(
            path,
            SettingsCodeGenerator.RawUserModuleTemplate.Replace("\r\n", "\n", StringComparison.Ordinal),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        (int exitCode, string output) = RecipeToolchain.RunSyntaxCheck(_temp);
        Assert.True(exitCode == 0, output);
    }

    // ============================================================ запись файлов

    /// <summary>
    /// РУКОПИСНЫЙ <c>settings_schema.py</c> БЕЗ МАРКЕРОВ — предупреждение ДО записи.
    /// Core на таком файле отказывается писать вовсе (<c>RegionWriteOutcome.Conflict</c>);
    /// шов путь оставляет ради миграции старых файлов, но человек обязан узнать, что теряет
    /// файл целиком.
    /// </summary>
    [Fact]
    public void SchemaWithoutMarkers_IsReportedBeforeOverwrite()
    {
        string dir = Path.Combine(_temp, "src", "res", "scripts", "client", "gui", "mods", "hello_tanker");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings_schema.py"), "# -*- coding: utf-8 -*-\nSECRET = 42\n");

        SettingsCodeResult result = Backend().Generate(SimpleForm(), Request());

        Assert.Null(result.Error);
        Assert.Contains(result.Issues, i => i.Severity == SettingsIssueSeverity.Warning
                                            && i.Message.Contains("маркеров", StringComparison.Ordinal)
                                            && i.Message.Contains("ЦЕЛИКОМ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Строка «что будет с файлом» различает два случая. Раньше любому существующему файлу
    /// обещали «будет перезаписан целиком» — после подключения Core это неправда в обычном
    /// случае и мешает заметить единственный случай, когда правда.
    /// </summary>
    [Fact]
    public void ArtifactAction_DistinguishesMarkedFileFromHandWritten()
    {
        (string withMarkers, string without) = WpfHost.Invoke(() =>
        {
            SettingsDesignerViewModel vm = Screen();
            SettingControlViewModel box = vm.InsertNew(SettingsControlType.CheckBox, 1, -1);
            box.Text = "Галочка";
            box.VarName = "flag";
            vm.Revalidate();

            vm.GenerateCodeCommand.Execute(null);
            vm.WriteFilesCommand.Execute(null);

            // Файл записан кокпитом — значит маркеры в нём есть.
            vm.GenerateCodeCommand.Execute(null);
            string marked = Artifact(vm, "settings_schema.py").Action;

            // Тот же файл, но переписанный человеком без маркеров.
            string path = Artifact(vm, "settings_schema.py").FullPath;
            File.WriteAllText(path, "# -*- coding: utf-8 -*-\nSECRET = 42\n");

            vm.GenerateCodeCommand.Execute(null);
            return (marked, Artifact(vm, "settings_schema.py").Action);
        });

        Assert.Contains("между маркерами", withMarkers, StringComparison.Ordinal);
        Assert.Contains("ЦЕЛИКОМ", without, StringComparison.Ordinal);
    }

    /// <summary>
    /// «ЗАПИСАТЬ В ПРОЕКТ» НЕ ПИШЕТ СТАРУЮ ФОРМУ. Генерация закрыта <c>CanGenerate</c>,
    /// а запись раньше — только наличием артефактов: собрал форму, добавил дубль varName,
    /// кнопка генерации погасла, а кнопка записи осталась яркой и клала на диск прежний код.
    /// </summary>
    [Fact]
    public void WriteFiles_IsDisabled_WhenFormChangedAfterGeneration()
    {
        (bool afterGenerate, bool afterEdit, bool stale, string header, bool afterRegenerate) =
            WpfHost.Invoke(() =>
            {
                SettingsDesignerViewModel vm = Screen();
                SettingControlViewModel box = vm.InsertNew(SettingsControlType.CheckBox, 1, -1);
                box.Text = "Галочка";
                box.VarName = "flag";
                vm.Revalidate();

                vm.GenerateCodeCommand.Execute(null);
                bool ready = vm.WriteFilesCommand.CanExecute(null);

                // Дубль ключа: ошибка формы.
                SettingControlViewModel twin = vm.InsertNew(SettingsControlType.CheckBox, 1, -1);
                twin.Text = "Дубль";
                twin.VarName = "flag";
                vm.Revalidate();

                bool broken = vm.WriteFilesCommand.CanExecute(null);
                bool isStale = vm.IsPreviewStale;
                string title = vm.CodeHeader;

                // Ошибку убрали, но код не пересобрали: писать всё ещё нечего.
                twin.VarName = "other";
                vm.Revalidate();
                bool fixedButStale = vm.WriteFilesCommand.CanExecute(null);

                return (ready, broken, isStale, title, fixedButStale);
            });

        Assert.True(afterGenerate, "после сборки записывать было нельзя");
        Assert.False(afterEdit, "кнопка записи осталась активной при ошибке в форме");
        Assert.True(stale, "предпросмотр не помечен устаревшим");
        Assert.Contains("форма изменилась", header, StringComparison.Ordinal);
        Assert.False(afterRegenerate, "запись включилась без повторной сборки кода");
    }

    /// <summary>
    /// ГЕНЕРАТОР ОТКАЗЫВАЕТСЯ ПИСАТЬ ЗНАЧЕНИЯ В КОНФИГ МОДА. Модуль настроек переписывает
    /// свой файл целиком при каждом «Применить»; указать туда <c>config.json</c> рецепта 09
    /// значит стереть у игрока конфиг мода. Охранка живёт в самом шве, а не у одного
    /// вызывающего: проверка, до которой экран не доходит, — мёртвый код.
    /// </summary>
    [Fact]
    public void Generate_Refuses_WhenValuesPathIsTheModConfig()
    {
        SettingsCodeResult result = Backend().Generate(SimpleForm(), Request() with
        {
            ConfigRelativePath = "mods/configs/hello_tanker/config.json",
            GameConfigRelative = @"mods\configs\hello_tanker\config.json"
        });

        Assert.NotNull(result.Error);
        Assert.Contains("рецепта 09", result.Error!, StringComparison.Ordinal);
        Assert.Empty(result.Files);
    }

    // ============================================================ фрагмент и пустая форма

    /// <summary>
    /// Готовый фрагмент вызова из <c>main.py</c> ПОКАЗЫВАЕТСЯ. Без него экран говорил
    /// «не забудьте позвать settings.register() из onAccountShowGUI» и не показывал кода.
    /// В проект фрагмент не пишется: вызов обязан стоять внутри <c>onAccountShowGUI</c>,
    /// а дописывание в конец файла положило бы его на уровень модуля (WOT001).
    /// </summary>
    [Fact]
    public void IntegrationSnippet_IsShownButNeverWritten()
    {
        GeneratedArtifactViewModel snippet = WpfHost.Invoke(() =>
        {
            SettingsDesignerViewModel vm = Screen();
            SettingControlViewModel box = vm.InsertNew(SettingsControlType.CheckBox, 1, -1);
            box.Text = "Галочка";
            box.VarName = "flag";
            vm.Revalidate();

            vm.GenerateCodeCommand.Execute(null);
            return vm.Artifacts.Single(a => a.RelativePath.Contains("main.py", StringComparison.Ordinal));
        });

        Assert.Contains("def onAccountShowGUI(ctx):", snippet.Content, StringComparison.Ordinal);
        Assert.Contains("settings.register()", snippet.Content, StringComparison.Ordinal);
        Assert.Equal(string.Empty, snippet.FullPath);
        Assert.Contains("не пишется", snippet.Action, StringComparison.Ordinal);
    }

    /// <summary>
    /// ФОРМА ИЗ ОДНОГО ТУМБЛЕРА ГЕНЕРИРУЕТСЯ. Мод «включить/выключить одной галочкой» —
    /// нормальный сценарий: <c>api.getSettingsFromTemplate</c> читает <c>enabled</c> ДО
    /// колонок. Раньше MSA006 был ошибкой и такой мод не собирался вовсе.
    /// </summary>
    [Fact]
    public void EnabledOnlyForm_Generates()
    {
        (bool canGenerate, int errors, string schema) = WpfHost.Invoke(() =>
        {
            SettingsDesignerViewModel vm = Screen();
            vm.HasEnabled = true;
            vm.Revalidate();

            bool ready = vm.CanGenerate;
            vm.GenerateCodeCommand.Execute(null);

            return (ready, vm.ErrorCount, ready ? Artifact(vm, "settings_schema.py").Content : string.Empty);
        });

        Assert.True(canGenerate, "форма из одного тумблера не генерируется");
        Assert.Equal(0, errors);
        Assert.Contains("HAS_ENABLED = True", schema, StringComparison.Ordinal);
        Assert.Contains("'enabled'", schema, StringComparison.Ordinal);
    }

    // ============================================================ сквозной прогон

    /// <summary>
    /// СКВОЗНОЙ ПРОГОН «мышь → диск». Форма из пяти типов, встречающихся в реальных шаблонах,
    /// собирается на настоящем экране, пишется в проект настоящей кнопкой — и то, что легло
    /// на диск, проходит <c>tools/check_py2_syntax.py</c>, тот же гейт, что стоит в build.py.
    /// Фрагмент вызова для <c>main.py</c> при этом на диск НЕ попадает.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public void EndToEnd_ScreenWritesFilesThatPassPython27SyntaxCheck()
    {
        Py27SyntaxGate.Require();

        (int written, string status) = WpfHost.Invoke(() =>
        {
            SettingsDesignerViewModel vm = Screen();

            SettingControlViewModel box = vm.InsertNew(SettingsControlType.CheckBox, 1, -1);
            box.Text = "Показывать приветствие";
            box.VarName = "showGreeting";

            SettingControlViewModel radio = vm.InsertNew(SettingsControlType.RadioButtonGroup, 1, -1);
            radio.Text = "Режим работы";
            radio.VarName = "mode";

            SettingControlViewModel slider = vm.InsertNew(SettingsControlType.Slider, 2, -1);
            slider.Text = "Прозрачность";
            slider.VarName = "opacity";
            slider.MinimumText = "0";
            slider.MaximumText = "1";
            slider.SnapText = "0.05";
            slider.NumberText = "0.8";

            SettingControlViewModel drop = vm.InsertNew(SettingsControlType.Dropdown, 2, -1);
            drop.Text = "Что показывать";
            drop.VarName = "what";

            SettingControlViewModel key = vm.InsertNew(SettingsControlType.HotKey, 2, -1);
            key.Text = "Показать панель";
            key.VarName = "toggleKey";
            key.CtrlKey = true;

            vm.Revalidate();
            Assert.True(vm.CanGenerate, "форма не готова: " + vm.StatusText);

            vm.GenerateCodeCommand.Execute(null);
            Assert.True(vm.WriteFilesCommand.CanExecute(null), "запись недоступна: " + vm.StatusText);
            vm.WriteFilesCommand.Execute(null);

            return (vm.Artifacts.Count(a => a.FullPath.Length > 0), vm.StatusText);
        });

        Assert.Equal(3, written);
        Assert.Contains("записано файлов: 3", status, StringComparison.Ordinal);

        string package = Path.Combine(_temp, "src", "res", "scripts", "client", "gui", "mods", "hello_tanker");
        Assert.True(File.Exists(Path.Combine(_temp, "settings.design.json")));
        Assert.True(File.Exists(Path.Combine(package, "settings_schema.py")));
        Assert.True(File.Exists(Path.Combine(package, "settings.py")));

        // Фрагмент — не файл: на диске его быть не должно ни под каким именем.
        Assert.Empty(Directory.GetFiles(_temp, "*main.py*", SearchOption.TopDirectoryOnly));

        // Дробный ползунок доехал до диска дробным.
        string schema = File.ReadAllText(Path.Combine(package, "settings_schema.py"), Encoding.UTF8);
        Assert.Contains("'opacity': 0.8", schema, StringComparison.Ordinal);
        Assert.Contains("'maximum': 1.0", schema, StringComparison.Ordinal);

        // Помощник для горячей клавиши лежит рядом, в пользовательском модуле.
        string runtime = File.ReadAllText(Path.Combine(package, "settings.py"), Encoding.UTF8);
        Assert.Contains("def is_hotkey_down(name):", runtime, StringComparison.Ordinal);

        (int exitCode, string output) = RecipeToolchain.RunSyntaxCheck(_temp);
        Assert.True(exitCode == 0, "check_py2_syntax.py отверг записанный код:" + Environment.NewLine + output);
    }

    // ============================================================ служебное

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

    private static SettingsFormSpec SimpleForm() => new()
    {
        Linkage = "ru.local.hello_tanker",
        ModDisplayName = "Hello Tanker",
        SettingsVersion = 1,
        HasEnabled = true,
        EnabledDefault = true,
        Column1 =
        [
            new SettingsControlSpec
            {
                Type = SettingsControlType.CheckBox,
                Text = "Показывать приветствие",
                VarName = "showGreeting",
                Value = true
            }
        ]
    };

    /// <summary>Форма чужого мода из синтетического файла состояния плюс замечания импорта.</summary>
    private static SettingsFormSpec SampleForm(string linkage, out List<string> notes)
    {
        notes = new List<string>();
        var template = (JsonObject)JsonNode.Parse(
            SettingsTestData.RawTemplate(SettingsTestData.StateJson, linkage))!;

        return SettingsDesignFile.FromLiveTemplate(template, linkage, notes);
    }

    /// <summary>Экран дизайнера, поднятый на настоящем контейнере и временном проекте.</summary>
    private SettingsDesignerViewModel Screen()
    {
        ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();
        var vm = provider.GetRequiredService<SettingsDesignerViewModel>();

        vm.ConfirmHandler = static (_, _) => true;
        vm.SetProject(new ModProject
        {
            ProjectDir = _temp,
            SrcDirRelative = "src",
            PackageName = "hello_tanker",
            ModId = "ru.local.hello_tanker",
            LogTag = "[HelloTanker"
        });

        vm.Linkage = "ru.local.hello_tanker";
        vm.ModDisplayName = "Hello Tanker";
        vm.SettingsVersionText = 1.ToString(CultureInfo.InvariantCulture);

        return vm;
    }

    private static GeneratedArtifactViewModel Artifact(SettingsDesignerViewModel vm, string name) =>
        vm.Artifacts.First(a => a.RelativePath.EndsWith(name, StringComparison.Ordinal));
}
