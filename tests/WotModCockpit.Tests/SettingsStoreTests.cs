using System.IO;
using System.Text;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Scaffold;
using WotModCockpit.Core.Services;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты <see cref="JsonSettingsStore"/>. Главное требование контракта — настройки НЕ имеют права
/// мешать запуску: любой отказ чтения превращается в значения по умолчанию, а не в исключение.
/// Все тесты работают во временном каталоге и настоящий <c>%APPDATA%</c> не трогают
/// (а хранилище по умолчанию в тестовой сборке и так смотрит во временный каталог —
/// см. <see cref="TestSettingsIsolation"/>).
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    /// <summary>Готовит пустой временный каталог под settings.json.</summary>
    public SettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wot-cockpit-settings-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // уборка мусора не повод ронять прогон
        }
    }

    /// <summary>Без переопределения путь по умолчанию ведёт в <c>%APPDATA%\WotModCockpit\settings.json</c>.</summary>
    [Fact]
    public void DefaultSettingsPath_WithoutOverride_PointsToAppData()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            JsonSettingsStore.AppFolderName,
            JsonSettingsStore.FileName);

        Assert.Equal(expected, JsonSettingsStore.DefaultSettingsPath(null));
        Assert.Equal(expected, JsonSettingsStore.DefaultSettingsPath("   "));
    }

    /// <summary>Каталог из <c>WOTCOCKPIT_SETTINGS_DIR</c> заменяет <c>%APPDATA%\WotModCockpit</c>.</summary>
    [Fact]
    public void DefaultSettingsPath_WithOverride_UsesThatDirectory()
    {
        Assert.Equal(Path.Combine(_dir, JsonSettingsStore.FileName), JsonSettingsStore.DefaultSettingsPath(_dir));
        Assert.Equal(Path.Combine(_dir, JsonSettingsStore.FileName), JsonSettingsStore.DefaultSettingsPath("\"" + _dir + "\""));
    }

    /// <summary>
    /// Хранилище по умолчанию читает переменную окружения. В тестовой сборке она задана
    /// всегда, кроме прогона на настоящей машине, — поэтому ни один тест, поднимающий
    /// настоящий контейнер, не читает и не пишет настройки разработчика.
    /// </summary>
    [Fact]
    public void SettingsPath_Default_HonoursEnvironmentOverride()
    {
        string? overrideDir = Environment.GetEnvironmentVariable(JsonSettingsStore.SettingsDirEnvironmentVariable);

        Assert.Equal(JsonSettingsStore.DefaultSettingsPath(overrideDir), new JsonSettingsStore().SettingsPath);

        if (!TestSettingsIsolation.RealMachine)
        {
            Assert.False(string.IsNullOrWhiteSpace(overrideDir), "тестовая сборка обязана увести настройки во временный каталог");
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), new JsonSettingsStore().SettingsPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Файла нет — возвращаются умолчания, а не исключение.</summary>
    [Fact]
    public void Load_NoFile_ReturnsDefaults_DoesNotThrow()
    {
        CockpitSettings s = new JsonSettingsStore(_path).Load();

        Assert.NotNull(s);
        Assert.Equal(string.Empty, s.PythonExe);         // пусто = найти python самому
        Assert.Equal(CockpitSettings.DefaultModIdPrefix, s.ModIdPrefix);
        Assert.Equal(20_000, s.LogBufferCapacity);
        Assert.False(File.Exists(_path));   // чтение не создаёт файл
    }

    /// <summary>
    /// Умолчания модели ни к какой машине не привязаны: ни каталога игры, ни python,
    /// ни шаблона. Всё это находится пробой на первом запуске или выбирается пользователем.
    /// </summary>
    [Fact]
    public void Defaults_AreMachineIndependent()
    {
        var s = new CockpitSettings();

        Assert.Equal(string.Empty, s.GameDir);
        Assert.Equal(string.Empty, s.PythonExe);
        Assert.Equal(string.Empty, s.TemplateDir);
        Assert.Equal(string.Empty, s.LastProjectDir);
        Assert.Equal("ru.local", s.ModIdPrefix);
    }

    /// <summary>Битый JSON читается как «файла нет»: это ровно то, что требует ISettingsStore.</summary>
    [Fact]
    public void Load_CorruptJson_ReturnsDefaults_DoesNotThrow()
    {
        File.WriteAllText(_path, "{ это не json, ", Encoding.UTF8);

        CockpitSettings s = new JsonSettingsStore(_path).Load();

        Assert.NotNull(s);
        Assert.Equal(string.Empty, s.PythonExe);
        Assert.Equal(CockpitSettings.DefaultModIdPrefix, s.ModIdPrefix);
    }

    /// <summary>Пустой файл (оборванная запись) тоже не должен ронять загрузку.</summary>
    [Fact]
    public void Load_EmptyFile_ReturnsDefaults()
    {
        File.WriteAllText(_path, string.Empty);

        Assert.NotNull(new JsonSettingsStore(_path).Load());
    }

    /// <summary>
    /// Нечитаемый файл — пустой, «null», битый JSON (например, путь с одиночными обратными
    /// косыми, набранный руками) — досеивается так же, как первый запуск: иначе кокпит
    /// остался бы без каталога игры, а следующее сохранение записало бы пустоту навсегда.
    /// </summary>
    /// <param name="content">Содержимое settings.json.</param>
    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{ это не json, ")]
    [InlineData("{ \"pythonExe\": \"C:\\Python312\\python.exe\" }")]
    public void Load_UnreadableFile_SeedsLikeFirstRun(string content)
    {
        File.WriteAllText(_path, content, Encoding.UTF8);

        CockpitSettings s = new JsonSettingsStore(_path).Load();

        Assert.Equal(GameDirectoryFinder.FindFirst() ?? string.Empty, s.GameDir);
    }

    /// <summary>Сохранение и чтение — полный круг по всем значимым полям.</summary>
    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new JsonSettingsStore(_path);
        var written = new CockpitSettings
        {
            GameDir = @"C:\Games\Tanki",
            PythonExe = @"C:\Python312\python.exe",
            EditorExe = @"C:\Tools\VS Code\Code.exe",
            WotSrcDir = @"C:\src\wot-src",
            LastProjectDir = @"C:\src\wot-mod-example",
            TemplateDir = @"C:\src\wot-mod-template",
            ModIdPrefix = "io.github.dev",
            LogBufferCapacity = 50_000,
            LogPrimeTailKilobytes = 128,
            LogOnlyMine = true,
            AutoBuildOnSave = true
        };

        store.Save(written);
        CockpitSettings read = store.Load();

        Assert.Equal(written.GameDir, read.GameDir);
        Assert.Equal(written.PythonExe, read.PythonExe);
        Assert.Equal(written.EditorExe, read.EditorExe);
        Assert.Equal(written.WotSrcDir, read.WotSrcDir);
        Assert.Equal(written.LastProjectDir, read.LastProjectDir);
        Assert.Equal(written.TemplateDir, read.TemplateDir);
        Assert.Equal(written.ModIdPrefix, read.ModIdPrefix);
        Assert.Equal(50_000, read.LogBufferCapacity);
        Assert.Equal(128, read.LogPrimeTailKilobytes);
        Assert.True(read.LogOnlyMine);
        Assert.True(read.AutoBuildOnSave);
    }

    /// <summary>Save создаёт каталог настроек, если его ещё нет.</summary>
    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        string nested = Path.Combine(_dir, "a", "b", "settings.json");

        new JsonSettingsStore(nested).Save(new CockpitSettings());

        Assert.True(File.Exists(nested));
    }

    /// <summary>Формат — тот же camelCase-JSON, что и у wotmod.json, без BOM.</summary>
    [Fact]
    public void Save_WritesCamelCaseUtf8WithoutBom()
    {
        new JsonSettingsStore(_path).Save(new CockpitSettings { GameDir = @"C:\Games\Tanki" });

        byte[] bytes = File.ReadAllBytes(_path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        string json = File.ReadAllText(_path);
        Assert.Contains("\"gameDir\"", json, StringComparison.Ordinal);
        Assert.Contains("\"templateDir\"", json, StringComparison.Ordinal);
        Assert.Contains("\"modIdPrefix\"", json, StringComparison.Ordinal);
        Assert.Contains("\"logBufferCapacity\"", json, StringComparison.Ordinal);
    }

    /// <summary>Ёмкость буфера зажимается в разумные границы и при записи, и при чтении.</summary>
    [Theory]
    [InlineData(0, 1_000)]
    [InlineData(-5, 1_000)]
    [InlineData(999_999, 200_000)]
    [InlineData(42_000, 42_000)]
    public void Save_ClampsLogBufferCapacity(int given, int expected)
    {
        var store = new JsonSettingsStore(_path);
        store.Save(new CockpitSettings { LogBufferCapacity = given });

        Assert.Equal(expected, store.Load().LogBufferCapacity);
    }

    /// <summary>
    /// Пустая команда python — это «найти самому», а не ошибка: литерал «python» ломал сборку
    /// на машинах, где есть только лаунчер py. Пустой шаблон аргументов редактора по-прежнему
    /// подменяется рабочим — без него редактор открывался бы без перехода на строку.
    /// </summary>
    [Fact]
    public void Load_BlankPythonExe_MeansAutoDetect()
    {
        File.WriteAllText(_path, """{ "pythonExe": "   ", "editorArgsTemplate": "" }""");

        CockpitSettings s = new JsonSettingsStore(_path).Load();

        Assert.Equal(string.Empty, s.PythonExe);
        Assert.False(string.IsNullOrWhiteSpace(s.EditorArgsTemplate));
    }

    /// <summary>Префикс id новых модов: точки по краям снимаются, пустой — возвращается к ru.local.</summary>
    [Theory]
    [InlineData("io.github.dev", "io.github.dev")]
    [InlineData("  io.github.dev.  ", "io.github.dev")]
    [InlineData("", "ru.local")]
    [InlineData("  ", "ru.local")]
    [InlineData("...", "ru.local")]
    public void Load_ModIdPrefix_IsNormalized(string stored, string expected)
    {
        File.WriteAllText(_path, "{ \"modIdPrefix\": \"" + stored + "\" }");

        Assert.Equal(expected, new JsonSettingsStore(_path).Load().ModIdPrefix);
    }

    /// <summary>Недавние проекты чистятся от пустот и дублей и обрезаются до десяти.</summary>
    [Fact]
    public void Save_NormalizesRecentProjects()
    {
        var settings = new CockpitSettings();
        settings.RecentProjects.Add(@"C:\a");
        settings.RecentProjects.Add("   ");
        settings.RecentProjects.Add(@"c:\A");           // тот же путь в другом регистре
        for (int i = 0; i < 20; i++)
        {
            settings.RecentProjects.Add($@"C:\p{i}");
        }

        var store = new JsonSettingsStore(_path);
        store.Save(settings);
        List<string> recent = store.Load().RecentProjects;

        Assert.Equal(10, recent.Count);
        Assert.Equal(@"C:\a", recent[0]);
        Assert.DoesNotContain(recent, string.IsNullOrWhiteSpace);
        Assert.Equal(recent.Count, recent.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>Неизвестные поля в файле игнорируются — совместимость вперёд.</summary>
    [Fact]
    public void Load_UnknownFields_AreIgnored()
    {
        File.WriteAllText(_path, """{ "gameDir": "C:\\Games\\Tanki", "изБудущего": 1 }""");

        Assert.Equal(@"C:\Games\Tanki", new JsonSettingsStore(_path).Load().GameDir);
    }

    /// <summary>
    /// Первый запуск (файла нет): пути досеиваются ТОЛЬКО тем, что реально есть на диске,
    /// а проект мода не выбирается вовсе — иначе новичку открылся бы сам шаблон, и правки
    /// попадали бы в эталон, из которого мастер копирует новые моды.
    /// </summary>
    [Fact]
    public void Load_FirstRun_SeedsOnlyExistingPaths_AndNeverOpensAProject()
    {
        CockpitSettings s = new JsonSettingsStore(_path).Load();

        Assert.Equal(string.Empty, s.LastProjectDir);
        Assert.Empty(s.RecentProjects);

        if (s.GameDir.Length > 0)
        {
            Assert.True(File.Exists(Path.Combine(s.GameDir, "paths.xml")), "в настройки попал каталог без paths.xml: " + s.GameDir);
        }

        if (s.TemplateDir.Length > 0)
        {
            Assert.True(TemplateLocator.LooksLikeTemplate(s.TemplateDir), "в настройки попал не шаблон: " + s.TemplateDir);
        }

        if (s.WotSrcDir.Length > 0)
        {
            Assert.True(Directory.Exists(s.WotSrcDir));
        }
    }

    /// <summary>
    /// Первый запуск находит игру тем же поиском, что и <see cref="GameDirectoryFinder"/>:
    /// если поиск что-то нашёл, это и есть каталог игры в настройках, если нет — поле пустое,
    /// а не чужой несуществующий путь.
    /// </summary>
    [Fact]
    public void Load_FirstRun_GameDirComesFromDiscoveryOrStaysEmpty()
    {
        CockpitSettings s = new JsonSettingsStore(_path).Load();

        Assert.Equal(GameDirectoryFinder.FindFirst() ?? string.Empty, s.GameDir);
    }

    /// <summary>
    /// Существующий файл с пустым путём проекта НЕ досеивается: пустое поле там —
    /// осознанный выбор пользователя, возвращать стёртый путь на каждом старте нельзя.
    /// </summary>
    [Fact]
    public void Load_ExistingFile_DoesNotReseedClearedPaths()
    {
        File.WriteAllText(_path, """{ "gameDir": "", "lastProjectDir": "", "templateDir": "" }""");

        CockpitSettings s = new JsonSettingsStore(_path).Load();

        Assert.Equal(string.Empty, s.LastProjectDir);
        Assert.Equal(string.Empty, s.GameDir);
        Assert.Equal(string.Empty, s.TemplateDir);
    }
}
