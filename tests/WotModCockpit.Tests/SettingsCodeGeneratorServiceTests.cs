using System.Text;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Settings;

namespace WotModCockpit.Tests;

/// <summary>
/// Интеграция дизайнера с проектом мода: документ <c>settings.design.json</c>, безопасная
/// перезапись генерируемого участка и то, что кокпит НЕ делает молча.
///
/// <para>Главное здесь — второй прогон генерации. Форму пересохраняют десятки раз, и каждый
/// раз кокпит переписывает файл, в котором к тому моменту может лежать код человека.
/// Механизм вставки рецептов, который есть в кокпите сегодня, для этого не годится: он умеет
/// только дописывать блок в конец файла, то есть на десятом сохранении в файле лежало бы
/// десять копий схемы.</para>
/// </summary>
public class SettingsCodeGeneratorServiceTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsDesignerService _service = new();

    /// <summary>Создаёт временный проект мода.</summary>
    public SettingsCodeGeneratorServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cockpit-designer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    /// <summary>Убирает временный проект.</summary>
    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ участок

    /// <summary>Новый файл создаётся с прологом, маркерами и телом.</summary>
    [Fact]
    public void RegionWriter_CreatesFileWithMarkers()
    {
        string path = Path.Combine(_root, "a.py");
        RegionWriteResult result = GeneratedRegionWriter.Write(path, "X = 1\n");

        Assert.Equal(RegionWriteOutcome.Created, result.Outcome);

        string text = File.ReadAllText(path, Encoding.UTF8);
        Assert.StartsWith("# -*- coding: utf-8 -*-", text, StringComparison.Ordinal);
        Assert.Contains(GeneratedRegionWriter.BeginToken, text, StringComparison.Ordinal);
        Assert.Contains(GeneratedRegionWriter.EndToken, text, StringComparison.Ordinal);
        Assert.Contains("X = 1", text, StringComparison.Ordinal);
        Assert.True(GeneratedRegionWriter.HasRegion(text));
    }

    /// <summary>
    /// ГЛАВНОЕ СВОЙСТВО: между генерациями пользовательский код вне участка не трогается —
    /// ни выше открывающего маркера, ни ниже закрывающего.
    /// </summary>
    [Fact]
    public void RegionWriter_KeepsUserCodeOutsideTheRegion()
    {
        string path = Path.Combine(_root, "b.py");
        GeneratedRegionWriter.Write(path, "VERSION = 1\n");

        string text = File.ReadAllText(path, Encoding.UTF8);
        text = text.Replace("# -*- coding: utf-8 -*-", "# -*- coding: utf-8 -*-\n# мой комментарий сверху",
            StringComparison.Ordinal);
        text += "\n\ndef my_helper():\n    return VERSION * 2\n";
        File.WriteAllText(path, text, new UTF8Encoding(false));

        RegionWriteResult second = GeneratedRegionWriter.Write(path, "VERSION = 2\n");
        Assert.Equal(RegionWriteOutcome.Updated, second.Outcome);

        string after = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("# мой комментарий сверху", after, StringComparison.Ordinal);
        Assert.Contains("def my_helper():", after, StringComparison.Ordinal);
        Assert.Contains("VERSION = 2", after, StringComparison.Ordinal);
        Assert.DoesNotContain("VERSION = 1", after, StringComparison.Ordinal);
    }

    /// <summary>Повторная запись того же содержимого не трогает файл вообще.</summary>
    [Fact]
    public void RegionWriter_IsIdempotent()
    {
        string path = Path.Combine(_root, "c.py");
        GeneratedRegionWriter.Write(path, "X = 1\n");
        DateTime stamp = File.GetLastWriteTimeUtc(path);

        RegionWriteResult again = GeneratedRegionWriter.Write(path, "X = 1\n");

        Assert.Equal(RegionWriteOutcome.Unchanged, again.Outcome);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    /// <summary>
    /// Чужой файл без маркеров не перезаписывается. Молча затереть файл, которого кокпит
    /// не создавал, — худшее, что мог бы сделать генератор.
    /// </summary>
    [Fact]
    public void RegionWriter_RefusesToClobberForeignFile()
    {
        string path = Path.Combine(_root, "d.py");
        File.WriteAllText(path, "# написано человеком\nVALUE = 42\n", new UTF8Encoding(false));

        RegionWriteResult result = GeneratedRegionWriter.Write(path, "X = 1\n");

        Assert.Equal(RegionWriteOutcome.Conflict, result.Outcome);
        Assert.False(result.Ok);
        Assert.Equal("# написано человеком\nVALUE = 42\n", File.ReadAllText(path, Encoding.UTF8));
        Assert.False(GeneratedRegionWriter.HasRegion(File.ReadAllText(path, Encoding.UTF8)));
    }

    /// <summary>Файл «создать один раз» второй раз не пишется никогда.</summary>
    [Fact]
    public void RegionWriter_WriteOnce_NeverOverwrites()
    {
        string path = Path.Combine(_root, "e.py");
        Assert.Equal(RegionWriteOutcome.Created, GeneratedRegionWriter.WriteOnce(path, "A = 1\n").Outcome);

        File.WriteAllText(path, "A = 999  # правка человека\n", new UTF8Encoding(false));
        Assert.Equal(RegionWriteOutcome.Skipped, GeneratedRegionWriter.WriteOnce(path, "A = 1\n").Outcome);

        Assert.Contains("правка человека", File.ReadAllText(path, Encoding.UTF8), StringComparison.Ordinal);
    }

    /// <summary>Перевод строки файла сохраняется: CRLF-файл не превращается в смесь.</summary>
    [Fact]
    public void RegionWriter_KeepsExistingLineEndings()
    {
        string path = Path.Combine(_root, "f.py");
        string crlf = GeneratedRegionWriter.Compose("# -*- coding: utf-8 -*-", "X = 1\n", null, "\r\n");
        File.WriteAllText(path, crlf, new UTF8Encoding(false));

        GeneratedRegionWriter.Write(path, "X = 2\n");

        string after = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("X = 2\r\n", after, StringComparison.Ordinal);

        // Ни одного одиночного LF: смешанные концы строк ломают всё, что читает файл построчно.
        for (int i = 0; i < after.Length; i++)
        {
            if (after[i] == '\n') Assert.True(i > 0 && after[i - 1] == '\r', $"одиночный LF в позиции {i}");
        }
    }

    // ------------------------------------------------------------------ служба

    /// <summary>
    /// Полный круг: применили форму — файлы легли на место, документ сохранился,
    /// повторное применение ничего не сломало.
    /// </summary>
    [Fact]
    public void Apply_WritesBothModulesAndTheDesignDocument()
    {
        ModProject project = Project();
        SettingsDesign design = Design(project, SettingsDesignSamples.AllControlTypes());

        SettingsApplyResult result = _service.Apply(project, design);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(2, result.Writes.Count);
        Assert.True(File.Exists(Path.Combine(PackageDir(project), "settings_schema.py")));
        Assert.True(File.Exists(Path.Combine(PackageDir(project), "settings.py")));
        Assert.True(File.Exists(_service.DesignFilePath(project)));
        Assert.True(_service.HasDesign(project));
    }

    /// <summary>
    /// Второе применение: схема обновилась, пользовательский <c>settings.py</c> остался
    /// нетронутым вместе с дописанным в него кодом.
    /// </summary>
    [Fact]
    public void Apply_Twice_UpdatesSchemaAndKeepsUserModule()
    {
        ModProject project = Project();
        SettingsDesign design = Design(project, SettingsDesignSamples.Minimal());
        Assert.True(_service.Apply(project, design).Success);

        string userPath = Path.Combine(PackageDir(project), "settings.py");
        File.AppendAllText(userPath, "\n\ndef my_own_helper():\n    return get('showGreeting')\n", new UTF8Encoding(false));

        string schemaPath = Path.Combine(PackageDir(project), "settings_schema.py");
        File.AppendAllText(schemaPath, "\n\ndef my_schema_helper():\n    return VAR_NAMES\n", new UTF8Encoding(false));

        // Меняем форму: добавляем контрол и поднимаем версию.
        design.Template.Column2.Add(SettingControl.Slider("Задержка", "delaySec", 3, 0, 10, 1));
        design.Template.SettingsVersion = 2;

        SettingsApplyResult second = _service.Apply(project, design);
        Assert.True(second.Success, second.Summary);

        Assert.Equal(RegionWriteOutcome.Updated, second.Writes[0].Outcome);
        Assert.Equal(RegionWriteOutcome.Skipped, second.Writes[1].Outcome);

        string user = File.ReadAllText(userPath, Encoding.UTF8);
        Assert.Contains("def my_own_helper():", user, StringComparison.Ordinal);

        string schema = File.ReadAllText(schemaPath, Encoding.UTF8);
        Assert.Contains("def my_schema_helper():", schema, StringComparison.Ordinal);
        Assert.Contains("'delaySec'", schema, StringComparison.Ordinal);
        Assert.Contains("SETTINGS_VERSION = 2", schema, StringComparison.Ordinal);

        // Схема в файле ровно одна: дописывания копий не произошло.
        Assert.Equal(1, CountOccurrences(schema, "def build_template(values):"));
    }

    /// <summary>Документ дизайнера переживает круг «записали — прочитали» без потерь.</summary>
    [Fact]
    public void Design_RoundTripsThroughJson()
    {
        ModProject project = Project();
        SettingsDesign design = Design(project, SettingsDesignSamples.TrickyText());
        Assert.True(_service.Apply(project, design).Success);

        SettingsDesign? loaded = _service.Load(project, out string error);

        Assert.True(loaded is not null, error);
        Assert.Equal(string.Empty, error);
        Assert.Equal(
            TemplateJson.ToCanonicalJson(design.Template),
            TemplateJson.ToCanonicalJson(loaded!.Template));
        Assert.Equal(design.Code.PackageName, loaded.Code.PackageName);
        Assert.Equal(design.Code.ConfigRelativePath, loaded.Code.ConfigRelativePath);
        Assert.Equal(design.Template.Linkage, loaded.Template.Linkage);
    }

    /// <summary>
    /// Секция <c>template</c> документа — это ТОТ ЖЕ словарь, что уезжает в modsettings.dat.
    /// Значит содержимое проекта можно прямо сравнивать с состоянием API в modsettings.dat.
    /// </summary>
    [Fact]
    public void DesignFile_CarriesTheTemplateVerbatim()
    {
        ModProject project = Project();
        SettingsDesign design = Design(project, SettingsDesignSamples.LegacyHotkeySample());
        Assert.True(_service.Apply(project, design).Success);

        string json = File.ReadAllText(_service.DesignFilePath(project), Encoding.UTF8);
        SettingValue root = SettingValue.ParseJson(json);

        SettingValue? template = root.Get(SettingsDesign.TemplateKey);
        Assert.NotNull(template);
        Assert.Equal(TemplateJson.ToCanonicalJson(design.Template), template!.ToCanonicalJson());
    }

    /// <summary>Битый JSON не роняет открытие проекта, а превращается в текст ошибки.</summary>
    [Fact]
    public void Load_BrokenJson_ReturnsErrorInsteadOfThrowing()
    {
        ModProject project = Project();
        File.WriteAllText(_service.DesignFilePath(project), "{ это не json ", new UTF8Encoding(false));

        SettingsDesign? loaded = _service.Load(project, out string error);

        Assert.Null(loaded);
        Assert.NotEqual(string.Empty, error);
    }

    /// <summary>Нет документа — нет и ошибки: это штатное состояние нового проекта.</summary>
    [Fact]
    public void Load_MissingFile_IsNotAnError()
    {
        ModProject project = Project();
        Assert.Null(_service.Load(project, out string error));
        Assert.Equal(string.Empty, error);
    }

    /// <summary>
    /// Мод без python-пакета отбраковывается с объяснением: класть модуль настроек в точку
    /// входа нельзя — её компилирует <c>tools/py27c.py</c>, который не умеет ни замыканий,
    /// ни половины синтаксиса.
    /// </summary>
    [Fact]
    public void Apply_WithoutPackage_FailsWithExplanation()
    {
        ModProject project = Project();
        project.PackageName = string.Empty;

        SettingsApplyResult result = _service.Apply(project, SettingsDesign.ForProject(project));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("PackageName", StringComparison.Ordinal));
    }

    /// <summary>Форма с ошибкой до диска не доезжает: полусломанный код в проект не кладётся.</summary>
    [Fact]
    public void Apply_InvalidTemplate_WritesNothing()
    {
        ModProject project = Project();
        SettingsDesign design = Design(project, SettingsDesignSamples.Minimal());

        // 'enabled' занят самим API: getSettingsFromTemplate кладёт его первым.
        design.Template.Column1[0].VarName = "enabled";

        SettingsApplyResult result = _service.Apply(project, design);

        Assert.False(result.Success);
        Assert.Empty(result.Writes);
        Assert.False(Directory.Exists(PackageDir(project)) &&
                     File.Exists(Path.Combine(PackageDir(project), "settings_schema.py")));
    }

    /// <summary>
    /// Столкновение с рецептом 09 отбраковывается: один файл на конфиг мода и на значения
    /// настроек — это два источника умолчаний, и модуль настроек, переписывающий свой файл
    /// целиком, вычистил бы оттуда чужие ключи при первом же «Применить».
    /// </summary>
    [Fact]
    public void Apply_WhenConfigPathCollidesWithRecipe09_Refuses()
    {
        ModProject project = Project();
        project.GameConfigRelative = @"mods\configs\hello_tanker\config.json";

        SettingsDesign design = Design(project, SettingsDesignSamples.Minimal());
        design.Code = design.Code with { ConfigRelativePath = "mods/configs/hello_tanker/config.json" };

        SettingsApplyResult result = _service.Apply(project, design);

        Assert.False(result.Success);
        Assert.Empty(result.Writes);
        Assert.Contains(result.Errors, e => e.Contains("рецепт", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Разные файлы — конфликта нет: настройки живут в своём settings.json.</summary>
    [Fact]
    public void Apply_WithSeparateConfigFiles_Succeeds()
    {
        ModProject project = Project();
        project.GameConfigRelative = @"mods\configs\hello_tanker\config.json";

        SettingsDesign design = Design(project, SettingsDesignSamples.Minimal());

        Assert.True(_service.Apply(project, design).Success);
    }

    /// <summary>
    /// Изменение СОСТАВА настроек без поднятия версии — то самое, из-за чего «я добавил галку,
    /// а её нет». Служба обязана это заметить и объяснить.
    /// </summary>
    [Fact]
    public void NeedsVersionBump_DetectsCompositionChange()
    {
        ModProject project = Project();
        SettingsDesign design = Design(project, SettingsDesignSamples.Minimal());
        Assert.True(_service.Apply(project, design).Success);

        Assert.False(_service.NeedsVersionBump(design, out _));

        design.Template.Column2.Add(SettingControl.CheckBox("Новая галка", "brandNew", true));

        Assert.True(_service.NeedsVersionBump(design, out string reason));
        Assert.Contains("brandNew", reason, StringComparison.Ordinal);

        design.Template.SettingsVersion = 2;
        Assert.False(_service.NeedsVersionBump(design, out _));
    }

    /// <summary>Косметика без смены состава версию поднимать не требует.</summary>
    [Fact]
    public void NeedsVersionBump_IgnoresCosmeticChanges()
    {
        ModProject project = Project();
        SettingsDesign design = Design(project, SettingsDesignSamples.Minimal());
        Assert.True(_service.Apply(project, design).Success);

        design.Template.Column1[0].Text = "Другая подпись";

        Assert.False(_service.NeedsVersionBump(design, out _));
    }

    /// <summary>
    /// Служба видит, подключён ли модуль настроек к телу мода, и не лезет в <c>main.py</c> сама.
    /// </summary>
    [Fact]
    public void IsWiredUp_SeesTheCallInModBody()
    {
        ModProject project = Project();
        SettingsDesign design = Design(project, SettingsDesignSamples.Minimal());
        Assert.True(_service.Apply(project, design).Success);

        Assert.False(_service.IsWiredUp(project, design));

        File.WriteAllText(
            Path.Combine(PackageDir(project), "main.py"),
            "# -*- coding: utf-8 -*-\nimport settings\n\n\ndef onAccountShowGUI(ctx):\n    settings.register()\n",
            new UTF8Encoding(false));

        Assert.True(_service.IsWiredUp(project, design));
    }

    /// <summary>Подсказка по подключению называет реальное имя модуля и ставит вызов в ангаре.</summary>
    [Fact]
    public void IntegrationSnippet_PointsAtOnAccountShowGUI()
    {
        GeneratedSettingsCode code = SettingsCodeGenerator.Generate(
            SettingsDesignSamples.Minimal(), SettingsDesignSamples.Options() with { UserModuleName = "prefs" });

        Assert.Contains("from . import prefs", code.IntegrationSnippet, StringComparison.Ordinal);
        Assert.Contains("def onAccountShowGUI(ctx):", code.IntegrationSnippet, StringComparison.Ordinal);
        Assert.Contains("prefs.register()", code.IntegrationSnippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// Пакет без <c>__init__.py</c> — не пакет, и <c>from . import settings</c> не сработает.
    /// Служба обязана сказать об этом, а не оставить мод молча неработающим.
    /// </summary>
    [Fact]
    public void Apply_WithoutPackageInit_Reports()
    {
        ModProject project = Project();

        // Именно БЕЗ вспомогательного Design(): тот создаёт __init__.py сам.
        var design = SettingsDesign.ForProject(project);
        design.Template = SettingsDesignSamples.Minimal();
        design.Template.Linkage = project.ModId;

        SettingsApplyResult result = _service.Apply(project, design, saveDesign: false);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("__init__.py", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ вспомогательное

    private ModProject Project() => new()
    {
        ProjectDir = _root,
        ModId = "ru.local.hello_tanker",
        DisplayName = "Hello Tanker",
        PackageName = "hello_tanker",
        EntryPointModule = "mod_hello_tanker",
        LogTag = "[HelloTanker"
    };

    private static string PackageDir(ModProject project) =>
        Path.Combine(project.SrcDir, "res", "scripts", "client", "gui", "mods", project.PackageName);

    /// <summary>Документ дизайнера под проект, с уже созданным <c>__init__.py</c>.</summary>
    private static SettingsDesign Design(ModProject project, SettingsTemplate template)
    {
        string packageDir = PackageDir(project);
        Directory.CreateDirectory(packageDir);
        string init = Path.Combine(packageDir, "__init__.py");
        if (!File.Exists(init)) File.WriteAllText(init, "# -*- coding: utf-8 -*-\n", new UTF8Encoding(false));

        var design = SettingsDesign.ForProject(project);
        design.Template = template;
        design.Template.Linkage = project.ModId;
        return design;
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }
}
