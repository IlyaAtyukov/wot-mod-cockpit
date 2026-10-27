using System.IO;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Services;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты <see cref="WotSrcTracebackMapper"/>: разбор кадров traceback и сведение виртуальных путей
/// к реальным файлам. Раскладки на диске воспроизводятся во временном каталоге, чтобы тесты
/// не зависели от машины; отдельно есть проверка на РЕАЛЬНОМ дереве
/// <c>wot-mod-template\.scratch\wot-src</c> — она мягко пропускается, если дерева нет.
/// </summary>
public class TracebackMapperTests : IDisposable
{
    private static readonly string RealWotSrcDir = ReposLayout.ClientSource;
    private static readonly string RealTemplateDir = ReposLayout.Template;

    private readonly ITracebackMapper _mapper = new WotSrcTracebackMapper();
    private readonly ILogParser _parser = new PythonLogParser();
    private readonly string _root;

    /// <summary>Готовит временную раскладку: проект мода, дерево клиента, res_mods.</summary>
    public TracebackMapperTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wot-cockpit-tbmap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>Убирает временный каталог.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // временный каталог — не повод валить тест
        }

        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------- ExtractFrames

    /// <summary>
    /// Реальный traceback из python.log: два кадра в теле плюс кадр из заголовка записи.
    /// Порядок — как в файле: самый глубокий кадр тела последним, заголовок в самом конце.
    /// </summary>
    [Fact]
    public void ExtractFrames_RealTracebackFromPythonLog()
    {
        LogEntry entry = _parser.Parse("2026-08-28 01:02:11.100: ERROR: [EXCEPTION] (scripts/common/items/vehicles.py, 4850)")!
            with
        {
            Continuation = new[]
            {
                "Traceback (most recent call last):",
                "  File \"scripts/common/items/vehicles.py\", line 4845, in getItemByCompactDescr",
                "  File \"scripts/common/items/__init__.py\", line 419, in parseIntCompactDescr",
                "TypeError: unsupported operand type(s) for &: 'NoneType' and 'int'"
            }
        };

        IReadOnlyList<TracebackFrame> frames = _mapper.ExtractFrames(entry);

        Assert.Equal(3, frames.Count);

        Assert.Equal("scripts/common/items/vehicles.py", frames[0].VirtualPath);
        Assert.Equal(4845, frames[0].Line);
        Assert.Equal("getItemByCompactDescr", frames[0].Function);

        Assert.Equal("scripts/common/items/__init__.py", frames[1].VirtualPath);
        Assert.Equal(419, frames[1].Line);
        Assert.Equal("parseIntCompactDescr", frames[1].Function);

        // кадр из заголовка записи: функции нет
        Assert.Equal("scripts/common/items/vehicles.py", frames[2].VirtualPath);
        Assert.Equal(4850, frames[2].Line);
        Assert.Null(frames[2].Function);
    }

    /// <summary>
    /// Кадр мода в формате Python 2.7 — то, что увидит разработчик, когда упадёт его собственный код.
    /// </summary>
    [Fact]
    public void ExtractFrames_ModFrame()
    {
        LogEntry entry = _parser.Parse("2026-08-28 01:02:11.100: ERROR: [EXCEPTION] (scripts/client/gui/mods/mod_hello_tanker.py, 67)")!
            with
        {
            Continuation = new[]
            {
                "Traceback (most recent call last):",
                "  File \"scripts/client/gui/mods/mod_hello_tanker.py\", line 67, in init",
                "  File \"scripts/client/gui/mods/hello_tanker/main.py\", line 123, in _on_avatar_ready",
                "AttributeError: 'NoneType' object has no attribute 'arena'"
            }
        };

        IReadOnlyList<TracebackFrame> frames = _mapper.ExtractFrames(entry);

        Assert.Equal(3, frames.Count);
        Assert.Equal("scripts/client/gui/mods/hello_tanker/main.py", frames[1].VirtualPath);
        Assert.Equal(123, frames[1].Line);
        Assert.Equal("_on_avatar_ready", frames[1].Function);
    }

    /// <summary>
    /// XFW/OpenWG печатает каждый кадр ОТДЕЛЬНОЙ записью с собственным таймстампом —
    /// кадр надо доставать не только из Continuation, но и из Message. Строка из реального лога.
    /// </summary>
    [Fact]
    public void ExtractFrames_FrameInsideTimestampedMessage()
    {
        LogEntry entry = _parser.Parse(
            "2026-08-28 01:16:28.894: ERROR:   File \"res_mods/configs/xvm/py_macro\\xvm\\hitLog.py\", line 953, in _Vehicle_onHealthChanged")!;

        IReadOnlyList<TracebackFrame> frames = _mapper.ExtractFrames(entry);

        TracebackFrame f = Assert.Single(frames);
        Assert.Equal("res_mods/configs/xvm/py_macro/xvm/hitLog.py", f.VirtualPath);
        Assert.Equal(953, f.Line);
        Assert.Equal("_Vehicle_onHealthChanged", f.Function);
    }

    /// <summary>Записи без traceback дают пустой список, а не null.</summary>
    [Fact]
    public void ExtractFrames_NoFrames_ReturnsEmpty()
    {
        LogEntry entry = _parser.Parse("2026-08-28 01:02:03.345: INFO: [Scaleform] Create GUI Component: 'gui/flash/battle.swf'")!;

        Assert.Empty(_mapper.ExtractFrames(entry));
    }

    // ---------------------------------------------------------------- маппинг

    /// <summary>Кадр точки входа мода ведёт в исходник проекта, номер строки ТОЧНЫЙ.</summary>
    [Fact]
    public void Map_EntryPoint_GoesToProjectSource()
    {
        (ModProject project, _) = MakeFakeProject();

        TracebackMapping m = _mapper.Map(
            new TracebackFrame("scripts/client/gui/mods/mod_hello_tanker.py", 67, "init"),
            project, wotSrcDir: string.Empty, game: null);

        Assert.True(m.IsResolved);
        Assert.Equal(FrameOrigin.ModProject, m.Origin);
        Assert.False(m.IsApproximate);
        Assert.Equal(67, m.Line);
        Assert.Equal(
            Path.Combine(project.SrcDir, "res", "scripts", "client", "gui", "mods", "mod_hello_tanker.py"),
            m.DiskPath);
    }

    /// <summary>Кадр модуля пакета мода тоже ведёт в проект.</summary>
    [Fact]
    public void Map_PackageModule_GoesToProjectSource()
    {
        (ModProject project, _) = MakeFakeProject();

        string? disk = _mapper.MapToDisk(
            new TracebackFrame("scripts/client/gui/mods/hello_tanker/main.py", 123, "_on_avatar_ready"),
            project, string.Empty);

        Assert.Equal(
            Path.Combine(project.SrcDir, "res", "scripts", "client", "gui", "mods", "hello_tanker", "main.py"),
            disk);
    }

    /// <summary>
    /// Кадр клиента ведёт в декомпилированное дерево, где префикса <c>scripts/</c> НЕТ,
    /// а номер строки объявляется приблизительным.
    /// </summary>
    [Theory]
    [InlineData("scripts/client/predefined_hosts.py", "client", "predefined_hosts.py")]
    [InlineData("scripts/common/items/vehicles.py", "common/items", "vehicles.py")]
    [InlineData("scripts/client_common/ClientArena.py", "client_common", "ClientArena.py")]
    public void Map_ClientFrame_StripsScriptsPrefix(string virtualPath, string relDir, string fileName)
    {
        string wotSrc = Path.Combine(_root, "wot-src");
        string dir = Path.Combine(wotSrc, relDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), "# decompiled\n");

        TracebackMapping m = _mapper.Map(new TracebackFrame(virtualPath, 782, "getHostList"), null, wotSrc, null);

        Assert.True(m.IsResolved);
        Assert.Equal(FrameOrigin.ClientSource, m.Origin);
        Assert.True(m.IsApproximate);
        Assert.Equal(Path.Combine(dir, fileName), m.DiskPath);
    }

    /// <summary>Проект главнее клиента: одноимённый файл сначала ищется в исходниках мода.</summary>
    [Fact]
    public void Map_ProjectWinsOverClientSource()
    {
        (ModProject project, _) = MakeFakeProject();

        string wotSrc = Path.Combine(_root, "wot-src");
        string dir = Path.Combine(wotSrc, "client", "gui", "mods");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mod_hello_tanker.py"), "# из пакета игры\n");

        TracebackMapping m = _mapper.Map(
            new TracebackFrame("scripts/client/gui/mods/mod_hello_tanker.py", 67, "init"),
            project, wotSrc, null);

        Assert.Equal(FrameOrigin.ModProject, m.Origin);
        Assert.StartsWith(project.SrcDir, m.DiskPath!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Если файла нет ни в проекте, ни в клиенте — ищем в россыпи res_mods.</summary>
    [Fact]
    public void Map_FallsBackToResMods()
    {
        string gameRoot = Path.Combine(_root, "game");
        string resMods = Path.Combine(gameRoot, "res_mods", "1.44.0.0");
        string dir = Path.Combine(resMods, "scripts", "client", "gui", "mods", "someone_else");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.py"), "# чужая россыпь\n");

        var game = new GameInstall(
            gameRoot, "1.44.0.0",
            Path.Combine(gameRoot, "mods", "1.44.0.0"),
            resMods,
            Path.Combine(gameRoot, "python.log"));

        TracebackMapping m = _mapper.Map(
            new TracebackFrame("scripts/client/gui/mods/someone_else/main.py", 10, "boom"),
            null, string.Empty, game);

        Assert.True(m.IsResolved);
        Assert.Equal(FrameOrigin.ResMods, m.Origin);
        Assert.Equal(Path.Combine(dir, "main.py"), m.DiskPath);
    }

    /// <summary>Путь XVM относительно корня игры (обратные слэши посреди пути) тоже разрешается.</summary>
    [Fact]
    public void Map_XvmConfigPathRelativeToGameRoot()
    {
        string gameRoot = Path.Combine(_root, "game2");
        string dir = Path.Combine(gameRoot, "res_mods", "configs", "xvm", "py_macro", "xvm");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hitLog.py"), "# xvm\n");

        var game = new GameInstall(
            gameRoot, "1.44.0.0",
            Path.Combine(gameRoot, "mods", "1.44.0.0"),
            Path.Combine(gameRoot, "res_mods", "1.44.0.0"),
            Path.Combine(gameRoot, "python.log"));

        TracebackMapping m = _mapper.Map(
            new TracebackFrame(@"res_mods/configs/xvm/py_macro\xvm\hitLog.py", 953, "_Vehicle_onHealthChanged"),
            null, string.Empty, game);

        Assert.Equal(FrameOrigin.ResMods, m.Origin);
        Assert.Equal(Path.Combine(dir, "hitLog.py"), m.DiskPath);
    }

    /// <summary>Несуществующий файл не превращается в «красивый, но несуществующий» путь.</summary>
    [Fact]
    public void Map_Unresolved_WhenNothingExists()
    {
        (ModProject project, _) = MakeFakeProject();

        TracebackMapping m = _mapper.Map(
            new TracebackFrame("scripts/client/gui/mods/hello_tanker/no_such_module.py", 5, "f"),
            project, Path.Combine(_root, "no-such-wot-src"), null);

        Assert.False(m.IsResolved);
        Assert.Null(m.DiskPath);
        Assert.Equal(FrameOrigin.Unresolved, m.Origin);
    }

    /// <summary>Пустой каталог исходников клиента просто не разрешает кадры клиента, а не падает.</summary>
    [Fact]
    public void Map_EmptyWotSrcDir_IsNotAnError()
    {
        TracebackMapping m = _mapper.Map(
            new TracebackFrame("scripts/common/items/vehicles.py", 4845, "getItemByCompactDescr"),
            null, string.Empty, null);

        Assert.False(m.IsResolved);
    }

    // ---------------------------------------------------------------- реальное дерево

    /// <summary>
    /// Фактическая проверка на РЕАЛЬНЫХ данных этой машины: виртуальные пути из python.log
    /// обязаны сойтись с существующими файлами в <c>.scratch\wot-src</c> и в шаблоне мода.
    /// Если деревьев нет (другая машина, чистый клон) — тест мягко пропускается.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void Map_RealTreesOnThisMachine()
    {
        Skip.IfNot(Directory.Exists(RealWotSrcDir),
            $"нет декомпилированных исходников клиента ({RealWotSrcDir})");

        (string virt, string expectedRel)[] cases =
        {
            ("scripts/client/predefined_hosts.py", @"client\predefined_hosts.py"),
            ("scripts/common/items/vehicles.py", @"common\items\vehicles.py"),
            ("scripts/common/items/__init__.py", @"common\items\__init__.py")
        };

        foreach ((string virt, string expectedRel) in cases)
        {
            TracebackMapping m = _mapper.Map(new TracebackFrame(virt, 100, null), null, RealWotSrcDir, null);

            Assert.True(m.IsResolved, virt);
            Assert.Equal(FrameOrigin.ClientSource, m.Origin);
            Assert.True(m.IsApproximate);
            Assert.Equal(Path.Combine(RealWotSrcDir, expectedRel), m.DiskPath);
            Assert.True(File.Exists(m.DiskPath), m.DiskPath);
        }

        Skip.IfNot(File.Exists(Path.Combine(RealTemplateDir, "src", "meta.xml")),
            $"нет шаблона мода ({RealTemplateDir})");

        var project = new ModProject
        {
            ProjectDir = RealTemplateDir,
            ModId = "ru.local.hello_tanker",
            Version = "1.0.0",
            EntryPointModule = "mod_hello_tanker",
            PackageName = "hello_tanker",
            LogTag = "[HelloTanker"
        };

        TracebackMapping entry = _mapper.Map(
            new TracebackFrame("scripts/client/gui/mods/mod_hello_tanker.py", 67, "init"),
            project, RealWotSrcDir, null);

        Assert.Equal(FrameOrigin.ModProject, entry.Origin);
        Assert.Equal(
            Path.Combine(RealTemplateDir, @"src\res\scripts\client\gui\mods\mod_hello_tanker.py"),
            entry.DiskPath);

        TracebackMapping pkg = _mapper.Map(
            new TracebackFrame("scripts/client/gui/mods/hello_tanker/main.py", 12, "_tick"),
            project, RealWotSrcDir, null);

        Assert.Equal(FrameOrigin.ModProject, pkg.Origin);
        Assert.Equal(
            Path.Combine(RealTemplateDir, @"src\res\scripts\client\gui\mods\hello_tanker\main.py"),
            pkg.DiskPath);
    }

    // ---------------------------------------------------------------- фильтр

    /// <summary>«Только ошибки» пропускает ERROR/CRITICAL и записи с traceback, но не INFO.</summary>
    [Fact]
    public void LogFilter_OnlyErrors()
    {
        var filter = new LogFilter { OnlyErrors = true };

        LogEntry info = _parser.Parse("2026-08-28 01:02:03.345: INFO: [Scaleform] Create GUI Component: 'x.swf'")!;
        LogEntry warn = _parser.Parse("2026-08-28 01:02:04.965: WARNING: [gui.shared.gui_items.vehicle_equipment] bad args")!;
        LogEntry err = _parser.Parse("2026-08-28 01:02:11.100: ERROR: [ERROR] (scripts/common/items/vehicles.py, 4851) mismatch")!;
        LogEntry crit = _parser.Parse("2026-08-28 01:02:11.100: CRITICAL: [NOTE] всё плохо")!;
        LogEntry withTb = info with { Continuation = new[] { "Traceback (most recent call last):", "  File \"a.py\", line 1, in f" } };

        Assert.False(filter.IsMatch(info));
        Assert.False(filter.IsMatch(warn));
        Assert.True(filter.IsMatch(err));
        Assert.True(filter.IsMatch(crit));
        Assert.True(filter.IsMatch(withTb));
    }

    /// <summary>«Только моё» и «только ошибки» комбинируются по И.</summary>
    [Fact]
    public void LogFilter_OnlyMineAndOnlyErrors_Combine()
    {
        var project = new ModProject { LogTag = "[HelloTanker", EntryPointModule = "mod_hello_tanker" };

        LogEntry myInfo = _parser.MarkMine(
            _parser.Parse("2026-08-28 01:02:00.100: INFO: [HelloTanker/loader] mod loaded")!, project);
        LogEntry myError = _parser.MarkMine(
            _parser.Parse("2026-08-28 01:02:00.200: ERROR: [HelloTanker] boom")!, project);
        LogEntry foreignError = _parser.MarkMine(
            _parser.Parse("2026-08-28 01:02:11.100: ERROR: [ERROR] (scripts/common/items/vehicles.py, 4851) mismatch")!, project);

        var mine = new LogFilter { OnlyMine = true };
        Assert.True(mine.IsMatch(myInfo));
        Assert.True(mine.IsMatch(myError));
        Assert.False(mine.IsMatch(foreignError));

        var both = new LogFilter { OnlyMine = true, OnlyErrors = true };
        Assert.False(both.IsMatch(myInfo));
        Assert.True(both.IsMatch(myError));
        Assert.False(both.IsMatch(foreignError));

        // Запасной критерий по тегу работает и для непомеченных записей.
        LogEntry unmarked = _parser.Parse("2026-08-28 01:02:00.100: INFO: [HelloTanker/loader] mod loaded")!;
        Assert.False(unmarked.IsMine);
        Assert.True(new LogFilter { OnlyMine = true, Tag = "[HelloTanker" }.IsMatch(unmarked));
    }

    /// <summary>Текстовый поиск идёт и по телу traceback, и по имени исходника.</summary>
    [Fact]
    public void LogFilter_TextSearchCoversContinuationAndSourceFile()
    {
        LogEntry e = _parser.Parse("2026-08-28 01:02:11.100: ERROR: [EXCEPTION] (scripts/common/items/vehicles.py, 4850)")!
            with
        {
            Continuation = new[] { "TypeError: unsupported operand type(s) for &: 'NoneType' and 'int'" }
        };

        Assert.True(new LogFilter { Text = "typeerror" }.IsMatch(e));
        Assert.True(new LogFilter { Text = "vehicles.py" }.IsMatch(e));
        Assert.False(new LogFilter { Text = "scaleform" }.IsMatch(e));
    }

    // ---------------------------------------------------------------- вспомогательное

    /// <summary>Создаёт во временном каталоге раскладку проекта мода по образцу шаблона.</summary>
    /// <returns>Проект и путь к его каталогу.</returns>
    private (ModProject Project, string Dir) MakeFakeProject()
    {
        string projectDir = Path.Combine(_root, "mod");
        string mods = Path.Combine(projectDir, "src", "res", "scripts", "client", "gui", "mods");
        Directory.CreateDirectory(Path.Combine(mods, "hello_tanker"));

        File.WriteAllText(Path.Combine(mods, "mod_hello_tanker.py"), "# entry point\n");
        File.WriteAllText(Path.Combine(mods, "hello_tanker", "main.py"), "# mod body\n");
        File.WriteAllText(Path.Combine(mods, "hello_tanker", "__init__.py"), string.Empty);

        var project = new ModProject
        {
            ProjectDir = projectDir,
            ModId = "ru.local.hello_tanker",
            Version = "1.0.0",
            EntryPointModule = "mod_hello_tanker",
            PackageName = "hello_tanker",
            LogTag = "[HelloTanker"
        };

        return (project, projectDir);
    }
}
