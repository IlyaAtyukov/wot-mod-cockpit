using System.Text.Json;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Services;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты <see cref="JsonProjectStore"/>.
/// Реальный шаблон (<see cref="ReposLayout.Template"/>) используется ТОЛЬКО НА ЧТЕНИЕ;
/// всё, что пишется, пишется во временные каталоги. Без шаблона тесты на нём пропускаются.
/// </summary>
public class ProjectStoreTests
{
    private readonly IProjectStore _store = new JsonProjectStore();

    // ------------------------------------------------------------------ реальный шаблон

    /// <summary>
    /// Шаблон не содержит <c>wotmod.json</c> — значит, весь проект должен вывестись из содержимого
    /// каталога: meta.xml + единственный <c>mod_*.py</c> + единственный подкаталог пакета.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    public void Sniff_RealTemplate_DerivesEverythingFromDisk()
    {
        string templateDir = TestEnvironment.RequireTemplate();

        ModProject? p = _store.Sniff(templateDir);

        Assert.NotNull(p);
        Assert.Equal(templateDir, p!.ProjectDir);
        Assert.Equal("ru.local.hello_tanker", p.ModId);
        Assert.Equal("1.0.0", p.Version);
        Assert.Equal("Hello Tanker", p.DisplayName);
        Assert.False(string.IsNullOrWhiteSpace(p.Description));
        Assert.Equal("mod_hello_tanker", p.EntryPointModule);
        Assert.Equal("hello_tanker", p.PackageName);
        Assert.Equal("[HelloTanker", p.LogTag);
    }

    /// <summary>Вычисляемые пути шаблона должны указывать на реально существующие файлы.</summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    public void Sniff_RealTemplate_DerivedPathsExist()
    {
        string templateDir = TestEnvironment.RequireTemplate();

        ModProject p = _store.Sniff(templateDir)!;

        Assert.True(File.Exists(p.BuildScriptPath), p.BuildScriptPath);
        Assert.True(File.Exists(p.MetaXmlPath), p.MetaXmlPath);
        Assert.True(File.Exists(p.EntryPointSourcePath), p.EntryPointSourcePath);
        Assert.Equal("scripts/client/gui/mods/mod_hello_tanker.py", p.EntryPointVirtualPath);
        Assert.Equal("scripts/client/gui/mods/hello_tanker/", p.PackageVirtualPrefix);
        Assert.Equal(
            Path.Combine(templateDir, "dist", "ru.local.hello_tanker_1.0.0.mtmod"),
            p.ExpectedArtifactPath);
    }

    /// <summary>Load без wotmod.json обязан дать то же, что Sniff.</summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    public void Load_RealTemplateWithoutJson_FallsBackToSniff()
    {
        string templateDir = TestEnvironment.RequireTemplate();

        Assert.False(
            File.Exists(Path.Combine(templateDir, ProjectStoreDefaults.FileName)),
            "тест рассчитан на шаблон без wotmod.json");

        ModProject? p = _store.Load(templateDir);

        Assert.NotNull(p);
        Assert.Equal("ru.local.hello_tanker", p!.ModId);
        Assert.Equal("mod_hello_tanker", p.EntryPointModule);
        Assert.Equal("hello_tanker", p.PackageName);
    }

    /// <summary>На реальном шаблоне ошибок быть не должно — он собирается как есть.</summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    public void Validate_RealTemplate_HasNoErrors()
    {
        string templateDir = TestEnvironment.RequireTemplate();

        ModProject p = _store.Load(templateDir)!;
        IReadOnlyList<ProjectValidation> issues = _store.Validate(p);

        Assert.DoesNotContain(issues, i => i.Severity == ValidationSeverity.Error);

        // wotmod.json ещё не создан — это ожидаемое Info, а не проблема.
        Assert.Contains(issues, i => i.Code == "NO_WOTMOD_JSON" && i.Severity == ValidationSeverity.Info);
    }

    // ------------------------------------------------------------------ временные каталоги

    /// <summary>Каталог, ничем не похожий на проект мода, — <c>null</c>, без исключения.</summary>
    [Fact]
    public void Load_NotAProject_ReturnsNull()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("readme.txt", "просто каталог");

        Assert.Null(_store.Load(tmp.Path));
        Assert.Null(_store.Sniff(tmp.Path));
    }

    /// <summary>Несуществующий каталог — <c>null</c>.</summary>
    [Fact]
    public void Load_MissingDirectory_ReturnsNull() =>
        Assert.Null(_store.Load(Path.Combine(Path.GetTempPath(), "no-project-" + Guid.NewGuid().ToString("N"))));

    /// <summary>Пустой путь — <c>null</c>.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Load_BlankPath_ReturnsNull(string dir) => Assert.Null(_store.Load(dir));

    /// <summary>Круг «сохранили → прочитали»: все поля кокпита переживают запись в json.</summary>
    [Fact]
    public void SaveThenLoad_RoundTripsCockpitFields()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp);

        ModProject p = _store.Load(tmp.Path)!;
        p.PythonExe = @"C:\Python312\python.exe";
        p.GameDir = @"C:\Games\Tanki";
        p.LogTag = "[Custom";
        p.GameConfigRelative = @"mods\configs\hello_tanker\config.json";
        p.ExtraBuildArgs = ["--no-syntax-check"];

        _store.Save(p);

        Assert.True(File.Exists(Path.Combine(tmp.Path, ProjectStoreDefaults.FileName)));

        ModProject reloaded = _store.Load(tmp.Path)!;

        Assert.Equal(@"C:\Python312\python.exe", reloaded.PythonExe);
        Assert.Equal(@"C:\Games\Tanki", reloaded.GameDir);
        Assert.Equal("[Custom", reloaded.LogTag);
        Assert.Equal(@"mods\configs\hello_tanker\config.json", reloaded.GameConfigRelative);
        Assert.Equal(new List<string> { "--no-syntax-check" }, reloaded.ExtraBuildArgs);
        Assert.Equal(tmp.Path, reloaded.ProjectDir);
    }

    /// <summary>
    /// <c>ProjectDir</c> помечен <c>[JsonIgnore]</c> и в файл попадать не должен: иначе перенос
    /// каталога ломал бы проект. Ключи — camelCase.
    /// </summary>
    [Fact]
    public void Save_DoesNotPersistProjectDir_AndUsesCamelCase()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp);

        ModProject p = _store.Load(tmp.Path)!;
        _store.Save(p);

        string json = File.ReadAllText(Path.Combine(tmp.Path, ProjectStoreDefaults.FileName));

        Assert.DoesNotContain("projectDir", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"modId\"", json);
        Assert.Contains("\"entryPointModule\"", json);
        Assert.Contains("\"srcDirRelative\"", json);

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("projectDir", out _));
    }

    /// <summary>Файл пишется в UTF-8 БЕЗ BOM: BOM ломает и git-диффы, и часть питоновских читалок.</summary>
    [Fact]
    public void Save_WritesUtf8WithoutBom()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp);

        _store.Save(_store.Load(tmp.Path)!);

        byte[] bytes = File.ReadAllBytes(Path.Combine(tmp.Path, ProjectStoreDefaults.FileName));

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    /// <summary>Кириллица в описании не должна превращаться в \uXXXX.</summary>
    [Fact]
    public void Save_KeepsCyrillicReadable()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp, description: "Учебный шаблон мода");

        _store.Save(_store.Load(tmp.Path)!);

        string json = File.ReadAllText(Path.Combine(tmp.Path, ProjectStoreDefaults.FileName));

        Assert.Contains("Учебный шаблон мода", json);
    }

    /// <summary>Сохранение без ProjectDir — <see cref="ArgumentException"/>, это ошибка вызывающего.</summary>
    [Fact]
    public void Save_WithoutProjectDir_Throws() =>
        Assert.Throws<ArgumentException>(() => _store.Save(new ModProject { ModId = "x" }));

    /// <summary>meta.xml главнее кэша в wotmod.json: расхождение чинится в пользу манифеста.</summary>
    [Fact]
    public void Load_MetaXmlOverridesJsonCache()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp);

        tmp.WriteFile(ProjectStoreDefaults.FileName, """
            {
              "modId": "ru.local.STALE",
              "version": "0.0.1",
              "displayName": "Устаревшее имя",
              "entryPointModule": "mod_hello_tanker",
              "packageName": "hello_tanker",
              "pythonExe": "python"
            }
            """);

        ModProject p = _store.Load(tmp.Path)!;

        Assert.Equal("ru.local.hello_tanker", p.ModId);
        Assert.Equal("1.0.0", p.Version);
        Assert.Equal("Hello Tanker", p.DisplayName);
    }

    /// <summary>Битый wotmod.json = как будто его нет: Load работает, Validate предупреждает.</summary>
    [Fact]
    public void Load_CorruptJson_FallsBackToSniffAndWarns()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp);
        tmp.WriteFile(ProjectStoreDefaults.FileName, "{ это не json ");

        ModProject? p = _store.Load(tmp.Path);

        Assert.NotNull(p);
        Assert.Equal("ru.local.hello_tanker", p!.ModId);
        Assert.Equal("mod_hello_tanker", p.EntryPointModule);

        IReadOnlyList<ProjectValidation> issues = _store.Validate(p);
        Assert.Contains(issues, i => i.Code == "NO_WOTMOD_JSON" && i.Severity == ValidationSeverity.Warning);
    }

    /// <summary>Неизвестные поля в json игнорируются — совместимость вперёд.</summary>
    [Fact]
    public void Load_UnknownJsonFields_AreIgnored()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp);
        tmp.WriteFile(ProjectStoreDefaults.FileName, """
            {
              "logTag": "[Hello",
              "somethingFromTheFuture": { "a": 1 },
              "anotherOne": [1, 2, 3]
            }
            """);

        ModProject? p = _store.Load(tmp.Path);

        Assert.NotNull(p);
        Assert.Equal("[Hello", p!.LogTag);
    }

    /// <summary>Несколько <c>mod_*.py</c> — берётся первый по алфавиту, детерминированно.</summary>
    [Fact]
    public void Sniff_SeveralEntryPoints_TakesFirstAlphabetically()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp);
        tmp.WriteFile("src/res/scripts/client/gui/mods/mod_aaa.py", "# aaa");
        tmp.WriteFile("src/res/scripts/client/gui/mods/mod_zzz.py", "# zzz");

        ModProject p = _store.Sniff(tmp.Path)!;

        Assert.Equal("mod_aaa", p.EntryPointModule);
    }

    /// <summary><c>__pycache__</c> не является пакетом мода.</summary>
    [Fact]
    public void Sniff_IgnoresPycacheAsPackage()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp);
        tmp.MakeDir("src/res/scripts/client/gui/mods/__pycache__");

        ModProject p = _store.Sniff(tmp.Path)!;

        Assert.Equal("hello_tanker", p.PackageName);
    }

    /// <summary>Проект без meta.xml и без точки входа — две ошибки, отсортированные первыми.</summary>
    [Fact]
    public void Validate_BrokenProject_ReportsErrorsFirst()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("build.py", "# stub");

        ModProject p = _store.Load(tmp.Path)!;
        IReadOnlyList<ProjectValidation> issues = _store.Validate(p);

        Assert.Contains(issues, i => i.Code == "NO_META_XML" && i.Severity == ValidationSeverity.Error);
        Assert.Contains(issues, i => i.Code == "NO_ENTRY_POINT" && i.Severity == ValidationSeverity.Error);
        Assert.Equal(ValidationSeverity.Error, issues[0].Severity);

        // Отсортировано по убыванию строгости.
        for (int i = 1; i < issues.Count; i++)
        {
            Assert.True(issues[i - 1].Severity >= issues[i].Severity);
        }
    }

    /// <summary>Нет build.py — собрать нечем, это ошибка.</summary>
    [Fact]
    public void Validate_NoBuildScript_IsError()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp, withBuildScript: false);

        ModProject p = _store.Load(tmp.Path)!;

        Assert.Contains(_store.Validate(p), i => i.Code == "NO_BUILD_SCRIPT" && i.Severity == ValidationSeverity.Error);
    }

    /// <summary>Точка входа без префикса <c>mod_</c> — загрузчик клиента её не увидит.</summary>
    [Fact]
    public void Validate_EntryPointWithoutModPrefix_IsWarning()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp);
        tmp.WriteFile("src/res/scripts/client/gui/mods/hello.py", "# no mod_ prefix");

        ModProject p = _store.Load(tmp.Path)!;
        p.EntryPointModule = "hello";

        Assert.Contains(
            _store.Validate(p),
            i => i.Code == "ENTRY_POINT_NOT_MOD_PREFIXED" && i.Severity == ValidationSeverity.Warning);
    }

    /// <summary>Расхождение кэша с meta.xml после ручной правки формы — предупреждение, не ошибка.</summary>
    [Fact]
    public void Validate_IdAndVersionMismatch_AreWarnings()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp);

        ModProject p = _store.Load(tmp.Path)!;
        p.ModId = "ru.local.other";
        p.Version = "9.9.9";
        p.DisplayName = "Другое имя";

        IReadOnlyList<ProjectValidation> issues = _store.Validate(p);

        Assert.Contains(issues, i => i.Code == "ID_MISMATCH" && i.Severity == ValidationSeverity.Warning);
        Assert.Contains(issues, i => i.Code == "VERSION_MISMATCH" && i.Severity == ValidationSeverity.Warning);
        Assert.Contains(issues, i => i.Code == "NAME_MISMATCH" && i.Severity == ValidationSeverity.Warning);
    }

    /// <summary>Несуществующий каталог игры и пустой тег лога — мягкие замечания.</summary>
    [Fact]
    public void Validate_GameDirAndLogTag()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp);

        ModProject p = _store.Load(tmp.Path)!;
        p.GameDir = @"Z:\Games\NoSuchWoT";
        p.LogTag = string.Empty;

        IReadOnlyList<ProjectValidation> issues = _store.Validate(p);

        Assert.Contains(issues, i => i.Code == "GAME_DIR_NOT_FOUND" && i.Severity == ValidationSeverity.Warning);
        Assert.Contains(issues, i => i.Code == "EMPTY_LOG_TAG" && i.Severity == ValidationSeverity.Info);
    }

    /// <summary>Заведомо отсутствующий python — предупреждение PYTHON_NOT_FOUND.</summary>
    [Fact]
    public void Validate_MissingPython_IsWarning()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp);

        ModProject p = _store.Load(tmp.Path)!;
        p.PythonExe = "python-which-does-not-exist-" + Guid.NewGuid().ToString("N");

        Assert.Contains(_store.Validate(p), i => i.Code == "PYTHON_NOT_FOUND");
    }

    /// <summary>
    /// SyncMetaXml пишет id/version/name/description обратно в манифест
    /// и не выбрасывает остальные элементы файла.
    /// </summary>
    [Fact]
    public void SyncMetaXml_UpdatesFieldsAndKeepsTheRest()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp);
        tmp.WriteFile("src/meta.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <root>
              <id>ru.local.hello_tanker</id>
              <version>1.0.0</version>
              <name>Hello Tanker</name>
              <description>Старое описание</description>
              <custom-thing keep="yes">не трогать</custom-thing>
            </root>
            """);

        ModProject p = _store.Load(tmp.Path)!;
        p.Version = "1.0.1";
        p.Description = "Новое описание";

        _store.SyncMetaXml(p);

        string xml = File.ReadAllText(Path.Combine(tmp.Path, "src", "meta.xml"));

        Assert.Contains("<version>1.0.1</version>", xml);
        Assert.Contains("<description>Новое описание</description>", xml);
        Assert.Contains("<custom-thing keep=\"yes\">не трогать</custom-thing>", xml);
        Assert.Contains("<id>ru.local.hello_tanker</id>", xml);

        ModProject reloaded = _store.Load(tmp.Path)!;
        Assert.Equal("1.0.1", reloaded.Version);
        Assert.Equal("Новое описание", reloaded.Description);
    }

    /// <summary>Отсутствующий элемент дописывается, а не теряется.</summary>
    [Fact]
    public void SyncMetaXml_AddsMissingElements()
    {
        using CoreTempDir tmp = new();
        MakeMinimalProject(tmp);
        tmp.WriteFile("src/meta.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <root>
              <id>ru.local.hello_tanker</id>
            </root>
            """);

        ModProject p = _store.Load(tmp.Path)!;
        p.Version = "2.0.0";
        p.DisplayName = "Hello Tanker";
        p.Description = "Описание";

        _store.SyncMetaXml(p);

        ModProject reloaded = _store.Load(tmp.Path)!;

        Assert.Equal("2.0.0", reloaded.Version);
        Assert.Equal("Hello Tanker", reloaded.DisplayName);
        Assert.Equal("Описание", reloaded.Description);
    }

    // ------------------------------------------------------------------ вспомогательное

    /// <summary>
    /// Строит во временном каталоге минимальный проект по образцу шаблона:
    /// build.py, src\meta.xml, точка входа и пакет.
    /// </summary>
    /// <param name="tmp">Временный каталог.</param>
    /// <param name="description">Значение <c>&lt;description&gt;</c>.</param>
    /// <param name="withBuildScript">Создавать ли build.py.</param>
    private static void MakeMinimalProject(
        CoreTempDir tmp, string description = "Учебный шаблон мода", bool withBuildScript = true)
    {
        if (withBuildScript)
        {
            tmp.WriteFile("build.py", "# stub build script\n");
        }

        tmp.WriteFile("src/meta.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <root>
              <id>ru.local.hello_tanker</id>
              <version>1.0.0</version>
              <name>Hello Tanker</name>
              <description>{description}</description>
            </root>
            """);

        tmp.WriteFile(
            "src/res/scripts/client/gui/mods/mod_hello_tanker.py",
            "def _note(m):\n    debug_utils.LOG_NOTE('[HelloTanker/loader] ' + m)\n");

        tmp.WriteFile("src/res/scripts/client/gui/mods/hello_tanker/__init__.py", "");
        tmp.WriteFile("src/res/scripts/client/gui/mods/hello_tanker/main.py", "# body\n");
    }
}
