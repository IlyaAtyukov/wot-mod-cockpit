using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using WotModCockpit.Core.Scaffold;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты мастера нового проекта.
///
/// <para>ГЛАВНЫЙ ТЕСТ здесь — <see cref="Created_Project_Builds_RealMtmod"/>: он создаёт
/// проект во временном каталоге, запускает настоящий <c>build.py --clean</c> и проверяет,
/// что <c>.mtmod</c> собрался и внутри лежит <c>mod_&lt;новоеимя&gt;.pyc</c> с magic
/// Python 2.7. Без него мастер бесполезен: подстановка, которая «выглядит правильной»,
/// но даёт несобираемый проект, — это худший из возможных исходов.</para>
///
/// <para>В игру не пишется ничего: <c>--clean</c> без <c>--install</c> и <c>--dev</c>
/// работает только внутри каталога проекта.</para>
/// </summary>
public class ProjectScaffolderTests : IDisposable
{
    /// <summary>Magic-заголовок .pyc Python 2.7 — по нему клиент отличает свой байткод.</summary>
    private static readonly byte[] Py27Magic = [0x03, 0xF3, 0x0D, 0x0A];

    /// <summary>
    /// Настоящий шаблон мода — по тому же правилу, что у кокпита: <c>WOTCOCKPIT_TEMPLATE_DIR</c>,
    /// затем соседние каталоги <c>wot-mod-template*</c>. Пусто — не нашёлся.
    /// </summary>
    private static readonly string TemplateDir = ProjectScaffolder.ProbeTemplateDirectory();

    /// <summary>
    /// Тесты с настоящим шаблоном идут только на настоящей машине (<c>WOTCOCKPIT_REAL_MACHINE=1</c>):
    /// шаблон — отдельный репозиторий, и у постороннего его может не быть.
    /// </summary>
    private static bool TemplateAvailable =>
        TestSettingsIsolation.RealMachine && TemplateDir.Length > 0 && File.Exists(Path.Combine(TemplateDir, "build.py"));

    /// <summary>Почему тест с шаблоном пропущен.</summary>
    private static string NoTemplateReason => TestSettingsIsolation.RealMachine
        ? "шаблон мода не найден рядом с кокпитом — задайте WOTCOCKPIT_TEMPLATE_DIR"
        : "нужен настоящий шаблон мода: задайте WOTCOCKPIT_REAL_MACHINE=1 (и при необходимости WOTCOCKPIT_TEMPLATE_DIR)";

    private readonly string _temp;
    private readonly IProjectScaffolder _scaffolder = new ProjectScaffolder();

    /// <summary>Создаёт временный корень для проектов теста.</summary>
    public ProjectScaffolderTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "cockpit-scaffold-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_temp);
    }

    /// <summary>Убирает временный корень.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // build.py мог оставить открытый дескриптор; каталог временный.
        }

        GC.SuppressFinalize(this);
    }

    private ScaffoldOptions Options(string package, string? target = null) => new()
    {
        TargetDirectory = target ?? Path.Combine(_temp, package),
        Package = package,
        TemplateDirectory = TemplateDir
    };

    // ------------------------------------------------------------------ главный тест

    /// <summary>
    /// Созданный проект собирается в настоящий <c>.mtmod</c>, и внутри лежит точка входа
    /// с новым именем, скомпилированная в байткод Python 2.7, а все записи — Stored.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public async Task Created_Project_Builds_RealMtmod()
    {
        Skip.IfNot(TemplateAvailable, NoTemplateReason);

        ScaffoldResult result = await _scaffolder.CreateAsync(Options("battle_timer"));
        Assert.True(result.Success, string.Join('\n', result.Errors));

        (int exitCode, string output) = RunBuild(result.TargetDirectory);
        Assert.True(exitCode == 0, $"build.py вернул {exitCode}:\n{output}");

        string dist = Path.Combine(result.TargetDirectory, "dist");
        string[] archives = Directory.GetFiles(dist, "*.mtmod");
        Assert.Single(archives);
        Assert.Equal("ru.local.battle_timer_1.0.0.mtmod", Path.GetFileName(archives[0]));

        using ZipArchive zip = ZipFile.OpenRead(archives[0]);

        // Точка входа с НОВЫМ именем и обязательно .pyc: розничный клиент грузит
        // только res/scripts/client/gui/mods/mod_*.pyc.
        ZipArchiveEntry entry = Assert.Single(zip.Entries,
            e => e.FullName == "res/scripts/client/gui/mods/mod_battle_timer.pyc");

        byte[] head = new byte[4];
        using (Stream s = entry.Open()) Assert.Equal(4, s.Read(head, 0, 4));
        Assert.Equal(Py27Magic, head);

        // Тело мода едет обычным .py под новым именем пакета.
        Assert.Contains(zip.Entries, e => e.FullName == "res/scripts/client/gui/mods/battle_timer/main.py");

        // Ни одной записи со старым именем шаблона.
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("hello_tanker", StringComparison.OrdinalIgnoreCase));

        // Deflate даёт "compression not supported" при загрузке мода.
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            Assert.True(e.CompressedLength == e.Length, $"{e.FullName}: запись сжата, а движок требует Stored");
        }
    }

    /// <summary>
    /// Свежесозданный проект проходит собственный валидатор синтаксиса и собственный
    /// тест стаба-загрузчика. Второй важен особо: он жёстко зашивает имя точки входа
    /// и падает, если переименовать только <c>src/</c>.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public async Task Created_Project_PassesItsOwnTests()
    {
        Skip.IfNot(TemplateAvailable, NoTemplateReason);

        ScaffoldResult result = await _scaffolder.CreateAsync(Options("radar_sweep"));
        Assert.True(result.Success, string.Join('\n', result.Errors));

        (int syntaxExit, string syntaxOut) = RunPython(result.TargetDirectory, ["tools/check_py2_syntax.py", "src"]);
        Assert.True(syntaxExit == 0, $"check_py2_syntax.py вернул {syntaxExit}:\n{syntaxOut}");

        (int stubExit, string stubOut) = RunPython(result.TargetDirectory, ["tools/test_stub_loader.py"]);
        Assert.True(stubExit == 0, $"test_stub_loader.py вернул {stubExit}:\n{stubOut}");
        Assert.Contains("check group(s) passed", stubOut, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ подстановки

    /// <summary>Файл точки входа и каталог пакета переименованы, старых имён нет.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "Template")]
    public async Task Create_RenamesEntryPointAndPackage()
    {
        Skip.IfNot(TemplateAvailable, NoTemplateReason);

        ScaffoldResult result = await _scaffolder.CreateAsync(Options("battle_timer"));
        Assert.True(result.Success, string.Join('\n', result.Errors));

        string mods = Path.Combine(result.TargetDirectory, "src", "res", "scripts", "client", "gui", "mods");

        Assert.True(File.Exists(Path.Combine(mods, "mod_battle_timer.py")));
        Assert.True(Directory.Exists(Path.Combine(mods, "battle_timer")));
        Assert.True(File.Exists(Path.Combine(mods, "battle_timer", "main.py")));

        Assert.False(File.Exists(Path.Combine(mods, "mod_hello_tanker.py")));
        Assert.False(Directory.Exists(Path.Combine(mods, "hello_tanker")));
    }

    /// <summary>
    /// Три константы стаба выводятся из одного имени пакета и согласованы между собой.
    /// Расхождение любой из них даёт мод, который грузится и не находит своё тело.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "Template")]
    public async Task Create_StubConstants_AreConsistent()
    {
        Skip.IfNot(TemplateAvailable, NoTemplateReason);

        ScaffoldResult result = await _scaffolder.CreateAsync(Options("battle_timer"));
        string stub = await File.ReadAllTextAsync(result.EntryPointPath);

        Assert.Contains("MOD_ID = 'ru.local.battle_timer'", stub, StringComparison.Ordinal);
        Assert.Contains("PACKAGE = 'gui.mods.battle_timer'", stub, StringComparison.Ordinal);
        Assert.Contains("ENTRY = 'gui.mods.battle_timer.main'", stub, StringComparison.Ordinal);
        Assert.Contains("RES_DIR = 'scripts/client/gui/mods/battle_timer'", stub, StringComparison.Ordinal);
        Assert.Contains("[BattleTimer/loader]", stub, StringComparison.Ordinal);
    }

    /// <summary>Паспорт мода в теле: идентификатор, имя, тег лога, каталог конфигов, маркер патча.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "Template")]
    public async Task Create_BodyConstants_AreSubstituted()
    {
        Skip.IfNot(TemplateAvailable, NoTemplateReason);

        ScaffoldResult result = await _scaffolder.CreateAsync(Options("battle_timer"));
        string main = await File.ReadAllTextAsync(Path.Combine(result.PackageDirectory, "main.py"));

        Assert.Contains("MOD_ID = 'ru.local.battle_timer'", main, StringComparison.Ordinal);
        Assert.Contains("MOD_NAME = 'Battle Timer'", main, StringComparison.Ordinal);
        Assert.Contains("LOG_PREFIX = '[BattleTimer]'", main, StringComparison.Ordinal);
        Assert.Contains("logging.getLogger('BattleTimer')", main, StringComparison.Ordinal);
        Assert.Contains("'battle_timer', 'config.json'", main, StringComparison.Ordinal);
        Assert.Contains("_PATCH_MARKER = '_battle_timer_patched'", main, StringComparison.Ordinal);
    }

    /// <summary>meta.xml получает идентификатор, версию, имя и описание.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "Template")]
    public async Task Create_WritesMetaXml()
    {
        Skip.IfNot(TemplateAvailable, NoTemplateReason);

        ScaffoldOptions options = Options("battle_timer") with
        {
            Version = "2.3.1",
            DisplayName = "Battle Timer Pro",
            Description = "Таймер боя"
        };

        ScaffoldResult result = await _scaffolder.CreateAsync(options);
        string meta = await File.ReadAllTextAsync(Path.Combine(result.TargetDirectory, "src", "meta.xml"));

        Assert.Contains("<id>ru.local.battle_timer</id>", meta, StringComparison.Ordinal);
        Assert.Contains("<version>2.3.1</version>", meta, StringComparison.Ordinal);
        Assert.Contains("<name>Battle Timer Pro</name>", meta, StringComparison.Ordinal);
        Assert.Contains("<description>Таймер боя</description>", meta, StringComparison.Ordinal);
    }

    /// <summary>Версия из мастера доезжает до имени артефакта.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public async Task Create_WithCustomVersion_BuildsMatchingArchiveName()
    {
        Skip.IfNot(TemplateAvailable, NoTemplateReason);

        ScaffoldOptions options = Options("radar_sweep") with { Version = "2.3.1", ModId = "org.example.radar" };
        ScaffoldResult result = await _scaffolder.CreateAsync(options);
        Assert.True(result.Success, string.Join('\n', result.Errors));

        (int exitCode, string output) = RunBuild(result.TargetDirectory);
        Assert.True(exitCode == 0, output);

        string[] archives = Directory.GetFiles(Path.Combine(result.TargetDirectory, "dist"), "*.mtmod");
        Assert.Equal("org.example.radar_2.3.1.mtmod", Path.GetFileName(Assert.Single(archives)));
    }

    /// <summary>Файлы, которые не должны ехать в новый проект, не едут.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "Template")]
    public async Task Create_SkipsScratchAndBuildArtifacts()
    {
        Skip.IfNot(TemplateAvailable, NoTemplateReason);

        ScaffoldResult result = await _scaffolder.CreateAsync(Options("battle_timer"));

        Assert.False(Directory.Exists(Path.Combine(result.TargetDirectory, ".scratch")));
        Assert.False(Directory.Exists(Path.Combine(result.TargetDirectory, ".tools")));
        Assert.False(Directory.Exists(Path.Combine(result.TargetDirectory, "dist")));
        Assert.Empty(Directory.GetFiles(result.TargetDirectory, "*.pyc", SearchOption.AllDirectories));
    }

    // ------------------------------------------------------------------ валидация

    /// <summary>Непустой каталог не затирается без явного разрешения.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "Template")]
    public async Task Create_RefusesNonEmptyDirectory_WithoutOverwrite()
    {
        Skip.IfNot(TemplateAvailable, NoTemplateReason);

        string target = Path.Combine(_temp, "occupied");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "important.txt"), "не трогать");

        ScaffoldResult result = await _scaffolder.CreateAsync(Options("battle_timer", target));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("не пуст", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(target, "important.txt")));
    }

    /// <summary>С явным разрешением непустой каталог используется.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "Template")]
    public async Task Create_AcceptsNonEmptyDirectory_WithOverwrite()
    {
        Skip.IfNot(TemplateAvailable, NoTemplateReason);

        string target = Path.Combine(_temp, "occupied2");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "old.txt"), "x");

        ScaffoldResult result = await _scaffolder.CreateAsync(Options("battle_timer", target) with { Overwrite = true });

        Assert.True(result.Success, string.Join('\n', result.Errors));
        Assert.True(File.Exists(result.EntryPointPath));
    }

    /// <summary>Пустой существующий каталог — законная цель.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "Template")]
    public async Task Create_AcceptsExistingEmptyDirectory()
    {
        Skip.IfNot(TemplateAvailable, NoTemplateReason);

        string target = Path.Combine(_temp, "empty");
        Directory.CreateDirectory(target);

        ScaffoldResult result = await _scaffolder.CreateAsync(Options("battle_timer", target));
        Assert.True(result.Success, string.Join('\n', result.Errors));
    }

    /// <summary>Создание проекта внутри самого шаблона запрещено: это порча шаблона.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "Template")]
    public async Task Create_RefusesTargetInsideTemplate()
    {
        Skip.IfNot(TemplateAvailable, NoTemplateReason);

        ScaffoldOptions options = Options("battle_timer", Path.Combine(TemplateDir, "newproj"));
        ScaffoldResult result = await _scaffolder.CreateAsync(options);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("внутри шаблона", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(TemplateDir, "newproj")));
    }

    /// <summary>Отсутствующий шаблон — внятный отказ, а не исключение.</summary>
    [Fact]
    public async Task Create_MissingTemplate_FailsGracefully()
    {
        ScaffoldOptions options = Options("battle_timer") with
        {
            TemplateDirectory = Path.Combine(_temp, "no-such-template")
        };

        ScaffoldResult result = await _scaffolder.CreateAsync(options);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }

    /// <summary>Плохие имена отбраковываются до того, как на диске что-то создано.</summary>
    [Theory]
    [InlineData("Battle Timer")]     // пробел и заглавные
    [InlineData("battle-timer")]     // дефис
    [InlineData("2fast")]            // начинается с цифры
    [InlineData("gui")]              // имя модуля клиента
    [InlineData("battle timer")]     // пробел
    [InlineData("")]                 // пусто
    [InlineData("Привет")]           // не латиница
    public async Task Create_RejectsInvalidPackageName(string package)
    {
        ScaffoldResult result = await _scaffolder.CreateAsync(Options(package, Path.Combine(_temp, "bad")));

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.False(Directory.Exists(Path.Combine(_temp, "bad", "src")));
    }

    /// <summary>Идентификатор мода проверяется отдельно: build.py режет разделители пути.</summary>
    [Theory]
    [InlineData("ru/local/x")]
    [InlineData("ru.local.../x")]
    [InlineData("nodot")]
    [InlineData("ru.local.Привет")]
    public async Task Create_RejectsInvalidModId(string modId)
    {
        ScaffoldOptions options = Options("battle_timer", Path.Combine(_temp, "badid")) with { ModId = modId };
        ScaffoldResult result = await _scaffolder.CreateAsync(options);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }

    /// <summary>Хорошие имена принимаются.</summary>
    [Theory]
    [InlineData("battle_timer")]
    [InlineData("radar")]
    [InlineData("mod_x2")]
    public void Validate_AcceptsGoodPackageNames(string package)
    {
        Assert.True(NameConventions.IsValidPackage(package, out string error), error);
    }

    /// <summary>
    /// Имена устройств DOS отвергаются с объяснением. Все они подходят под
    /// <c>^[a-z][a-z0-9_]*$</c> и раньше проходили проверку, после чего мастер падал
    /// сырым системным текстом «Could not find a part of the path '\\.\con'».
    /// </summary>
    [Theory]
    [InlineData("con")]
    [InlineData("prn")]
    [InlineData("aux")]
    [InlineData("nul")]
    [InlineData("com1")]
    [InlineData("com9")]
    [InlineData("lpt1")]
    [InlineData("lpt9")]
    public void Validate_RejectsWindowsDeviceNames(string package)
    {
        Assert.False(NameConventions.IsValidPackage(package, out string error));
        Assert.Contains("устройством Windows", error, StringComparison.Ordinal);
    }

    /// <summary>Похожие, но незанятые имена по-прежнему проходят: устройства COM10 в DOS нет.</summary>
    [Theory]
    [InlineData("com10")]
    [InlineData("console")]
    [InlineData("auxiliary")]
    public void Validate_AcceptsNamesThatOnlyLookLikeDevices(string package)
    {
        Assert.True(NameConventions.IsValidPackage(package, out string error), error);
    }

    // ------------------------------------------------------------------ тег лога

    /// <summary>
    /// Тег лога принимается и в скобках: именно так он выглядит в шаблоне
    /// (<c>LOG_PREFIX = '[HelloTanker]'</c>) и в его README, и копировать его оттуда
    /// вместе со скобками — нормальное поведение, а не ошибка.
    /// </summary>
    [Theory]
    [InlineData("TankRadar")]
    [InlineData("[TankRadar]")]
    [InlineData("  [TankRadar]  ")]
    [InlineData("[[TankRadar]]")]
    public void LogTag_BracketsAreStrippedSilently(string tag)
    {
        var options = Options("tank_radar") with { LogTag = tag };

        Assert.Equal("TankRadar", options.EffectiveLogTag);
        Assert.Empty(options.Validate());
    }

    /// <summary>Скобка ВНУТРИ тега по-прежнему отвергается — она ломает фильтр живого лога.</summary>
    [Fact]
    public void LogTag_InnerBracketStillRejected()
    {
        var options = Options("tank_radar") with { LogTag = "Tank]Radar" };

        IReadOnlyList<string> errors = options.Validate();

        Assert.Single(errors);
        Assert.Contains("подставляются автоматически", errors[0], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ запрет писать в игру

    /// <summary>
    /// Каталог внутри установленной игры отвергается, и на диске после этого ничего нет.
    /// До этой проверки мастер спокойно раскладывал 14 файлов в <c>mods\&lt;версия&gt;</c> —
    /// в тот самый каталог, который читают панель деплоя и детектор перекрытия.
    /// </summary>
    [Fact]
    public async Task Create_RefusesTargetInsideGame()
    {
        string game = Path.Combine(_temp, "FakeGame");
        Directory.CreateDirectory(Path.Combine(game, "mods", "1.44.0.0"));

        string target = Path.Combine(game, "mods", "1.44.0.0", "evil");
        ScaffoldOptions options = Options("evil_mod", target) with { GameDirectory = game };

        ScaffoldResult result = await _scaffolder.CreateAsync(options);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("внутри установленной игры", StringComparison.Ordinal));
        Assert.False(Directory.Exists(target));
    }

    /// <summary>Сам корень игры — тоже запрет.</summary>
    [Fact]
    public async Task Create_RefusesGameRootItself()
    {
        string game = Path.Combine(_temp, "FakeGame2");
        Directory.CreateDirectory(game);

        ScaffoldOptions options = Options("evil_mod", game) with { GameDirectory = game };

        Assert.NotNull(ProjectScaffolder.GameGuard(game, game));
        Assert.False((await _scaffolder.CreateAsync(options)).Success);
    }

    /// <summary>Пустой путь к игре ничего не запрещает: настройка может быть не заполнена.</summary>
    [Fact]
    public void GameGuard_EmptyGameDirectory_AllowsEverything()
    {
        Assert.Null(ProjectScaffolder.GameGuard(@"C:\anywhere\mod", null));
        Assert.Null(ProjectScaffolder.GameGuard(@"C:\anywhere\mod", "   "));
    }

    /// <summary>Каталог рядом с игрой, но не внутри неё, запретом не считается.</summary>
    [Fact]
    public void GameGuard_SiblingDirectory_IsAllowed()
    {
        Assert.Null(ProjectScaffolder.GameGuard(@"C:\Games\TankiMods\radar", @"C:\Games\Tanki"));
    }

    // ------------------------------------------------------------------ провал не мусорит

    /// <summary>
    /// Провал пост-проверки не оставляет на диске недоделанный проект, и повтор в тот же
    /// каталог проходит. До исправления мастер писал 14 файлов и только потом проверял
    /// результат: одна опечатка в имени пакета делала выбранный каталог непригодным
    /// («каталог не пуст») до ручной уборки.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "Template")]
    public async Task Create_Failure_LeavesNothingBehind()
    {
        Skip.IfNot(TemplateAvailable, NoTemplateReason);

        // Шаблон без точки входа: пост-проверка обязана отказать уже после копирования.
        string broken = Path.Combine(_temp, "broken-template");
        CopyTree(TemplateDir, broken);
        string entry = Path.Combine(broken, "src", "res", "scripts", "client", "gui", "mods", "mod_hello_tanker.py");
        File.Delete(entry);

        string target = Path.Combine(_temp, "after-failure");
        ScaffoldResult failed = await _scaffolder.CreateAsync(
            Options("some_mod", target) with { TemplateDirectory = broken });

        Assert.False(failed.Success);
        Assert.True(!Directory.Exists(target) || !Directory.EnumerateFileSystemEntries(target).Any(),
            "после провала каталог назначения обязан остаться пустым");
        Assert.Empty(Directory.EnumerateDirectories(_temp, ".cockpit-new-*"));

        // И повтор в тот же каталог должен пройти, а не упереться в «каталог не пуст».
        ScaffoldResult retry = await _scaffolder.CreateAsync(Options("some_mod", target));
        Assert.True(retry.Success, string.Join("; ", retry.Errors));
    }

    // ------------------------------------------------------------------ где искать шаблон

    /// <summary>Шаблоном считается только каталог, где есть И build.py, И src\meta.xml.</summary>
    [Fact]
    public void TemplateLocator_LooksLikeTemplate_NeedsBuildPyAndMeta()
    {
        string onlyBuild = Path.Combine(_temp, "only-build");
        Directory.CreateDirectory(onlyBuild);
        File.WriteAllText(Path.Combine(onlyBuild, "build.py"), "# stand");

        Assert.False(TemplateLocator.LooksLikeTemplate(onlyBuild));
        Assert.False(TemplateLocator.LooksLikeTemplate(null));
        Assert.False(TemplateLocator.LooksLikeTemplate("   "));
        Assert.True(TemplateLocator.LooksLikeTemplate(FakeTemplate(Path.Combine(_temp, "full"))));
    }

    /// <summary>
    /// Соседний каталог находится и под именем распакованного GitHub-архива
    /// (<c>wot-mod-template-main</c>), но каноническое имя на том же уровне выигрывает.
    /// </summary>
    [Fact]
    public void TemplateLocator_ProbeSiblings_AcceptsZipVariant_ButPrefersCanonicalName()
    {
        string repos = Path.Combine(_temp, "repos");
        string start = Path.Combine(repos, "wot-mod-cockpit", "src", "WotModCockpit.App", "bin", "Debug");
        Directory.CreateDirectory(start);

        string zip = FakeTemplate(Path.Combine(repos, "wot-mod-template-main"));
        Assert.Equal(zip, TemplateLocator.ProbeSiblings(start), ignoreCase: true);

        string canonical = FakeTemplate(Path.Combine(repos, "wot-mod-template"));
        Assert.Equal(canonical, TemplateLocator.ProbeSiblings(start), ignoreCase: true);
    }

    /// <summary>Одноимённая папка без build.py шаблоном не считается — проба идёт дальше и ничего не находит.</summary>
    [Fact]
    public void TemplateLocator_ProbeSiblings_IgnoresLookalikeFolder()
    {
        string repos = Path.Combine(_temp, "lookalike");
        string start = Path.Combine(repos, "wot-mod-cockpit", "bin");
        Directory.CreateDirectory(start);
        Directory.CreateDirectory(Path.Combine(repos, "wot-mod-template"));

        string found = TemplateLocator.ProbeSiblings(start);

        Assert.False(found.StartsWith(_temp, StringComparison.OrdinalIgnoreCase), "принят каталог без build.py: " + found);
    }

    /// <summary>
    /// Настройка <c>templateDir</c> выигрывает у соседнего каталога; переменная окружения — у обоих.
    /// Переменную тест не трогает (тесты идут параллельно), поэтому ожидание учитывает её, если она задана.
    /// </summary>
    [Fact]
    public void TemplateLocator_Resolve_ConfiguredWinsOverSiblings()
    {
        string configured = FakeTemplate(Path.Combine(_temp, "configured-template"));
        string fromEnvironment = TemplateLocator.FromEnvironment();

        string expected = fromEnvironment.Length > 0 ? fromEnvironment : configured;

        Assert.Equal(expected, TemplateLocator.Resolve(configured), ignoreCase: true);
        Assert.Equal(expected, TemplateLocator.Resolve(new WotModCockpit.Core.Models.CockpitSettings { TemplateDir = configured }), ignoreCase: true);
    }

    /// <summary>
    /// Отказ «шаблон не найден» называет настройки, которые есть на самом деле, — раньше он
    /// отсылал к полю TemplateDir, которого в настройках не было.
    /// </summary>
    [Fact]
    public async Task Create_NoTemplateAnywhere_NamesRealSettings()
    {
        string missing = Path.Combine(_temp, "no-such-template");
        ScaffoldResult result = await new ProjectScaffolder(missing).CreateAsync(new ScaffoldOptions
        {
            TargetDirectory = Path.Combine(_temp, "target"),
            Package = "battle_timer",
            TemplateDirectory = missing
        });

        Assert.False(result.Success);
        string error = Assert.Single(result.Errors);
        Assert.Contains("templateDir", error, StringComparison.Ordinal);
        Assert.Contains(TemplateLocator.EnvironmentVariable, error, StringComparison.Ordinal);
        Assert.Contains("wot-mod-template", error, StringComparison.Ordinal);
    }

    /// <summary>Префикс id из настроек подставляется в выводимый идентификатор; пустой — ru.local.</summary>
    [Theory]
    [InlineData(null, "ru.local.battle_timer")]
    [InlineData("", "ru.local.battle_timer")]
    [InlineData("io.github.dev", "io.github.dev.battle_timer")]
    [InlineData(" io.github.dev. ", "io.github.dev.battle_timer")]
    public void Options_EffectiveModId_UsesPrefix(string? prefix, string expected)
    {
        var options = new ScaffoldOptions
        {
            TargetDirectory = Path.Combine(_temp, "x"),
            Package = "battle_timer",
            ModIdPrefix = prefix
        };

        Assert.Equal(expected, options.EffectiveModId);
    }

    /// <summary>Каталог, похожий на шаблон: build.py и src\meta.xml.</summary>
    private static string FakeTemplate(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "build.py"), "# stand");
        File.WriteAllText(Path.Combine(dir, "src", "meta.xml"), "<root><id>ru.local.hello_tanker</id></root>");
        return Path.GetFullPath(dir);
    }

    private static void CopyTree(string from, string to)
    {
        string[] skip = [".git", ".scratch", ".tools", "dist", "__pycache__", "bin", "obj", "node_modules"];

        bool Skipped(string path) =>
            Path.GetRelativePath(from, path).Split(Path.DirectorySeparatorChar).Any(p => skip.Contains(p));

        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            if (Skipped(dir)) continue;
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (Skipped(file)) continue;
            string destination = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    // ------------------------------------------------------------------ имена

    /// <summary>Вывод трёх написаний имени из одного поля.</summary>
    [Theory]
    [InlineData("battle_timer", "BattleTimer", "Battle Timer")]
    [InlineData("radar", "Radar", "Radar")]
    [InlineData("my_cool_mod", "MyCoolMod", "My Cool Mod")]
    public void NameConventions_DerivesAllForms(string package, string pascal, string display)
    {
        Assert.Equal(pascal, NameConventions.ToPascalCase(package));
        Assert.Equal(display, NameConventions.ToDisplayName(package));
    }

    /// <summary>Имя пакета выводится из отображаемого имени — это единственное поле, которое хочется вводить.</summary>
    [Theory]
    [InlineData("Battle Timer", "battle_timer")]
    [InlineData("BattleTimer", "battle_timer")]
    [InlineData("My Cool Mod!", "my_cool_mod")]
    [InlineData("Radar", "radar")]
    public void NameConventions_ToSnakeCase(string display, string expected)
    {
        Assert.Equal(expected, NameConventions.ToSnakeCase(display));
    }

    // ------------------------------------------------------------------ вспомогательное

    /// <summary>
    /// Обычная сборка созданного проекта. Каталог игры — пустая заглушка рядом с проектом: без
    /// <c>--game-dir</c> build.py взял бы свой каталог по умолчанию и прочитал бы <c>paths.xml</c>
    /// и все <c>.mtmod</c> настоящей установки, хотя тесту она не нужна.
    /// </summary>
    /// <param name="projectDir">Каталог созданного проекта.</param>
    /// <returns>Код возврата и вывод build.py.</returns>
    private static (int ExitCode, string Output) RunBuild(string projectDir)
    {
        string game = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(projectDir)!, "fake-game")).FullName;
        return RunPython(projectDir, ["build.py", "--clean", "--game-dir", game]);
    }

    /// <summary>
    /// Запускает скрипт проекта тем же Python 3, что и остальные тесты (<see cref="TestPython"/>),
    /// с вводом-выводом в UTF-8: первый <c>python</c> из PATH у модмейкера нередко 2.7.
    /// </summary>
    /// <param name="workingDirectory">Каталог проекта.</param>
    /// <param name="args">Скрипт и его аргументы.</param>
    /// <returns>Код возврата и объединённый вывод.</returns>
    private static (int ExitCode, string Output) RunPython(string workingDirectory, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = TestPython.Require(),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        TestPython.UseUtf8(psi);

        using Process p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(300_000);
        return (p.ExitCode, stdout + stderr);
    }
}
