using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Services;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты <see cref="FileSystemDeployProbe"/>.
/// Проба только читает; фейковые установки строятся во временных каталогах,
/// а настоящая установка игры (только при <c>WOTCOCKPIT_REAL_MACHINE=1</c>) используется
/// исключительно на чтение.
/// </summary>
public class DeployProbeTests
{
    private readonly IDeployProbe _probe = new FileSystemDeployProbe();

    /// <summary>Проект-образец: шаблон hello_tanker.</summary>
    private static ModProject HelloTanker(string projectDir = @"C:\nowhere") => new()
    {
        ProjectDir = projectDir,
        ModId = "ru.local.hello_tanker",
        Version = "1.0.0",
        EntryPointModule = "mod_hello_tanker",
        PackageName = "hello_tanker"
    };

    /// <summary>Фейковая установка: paths.xml не нужен, <see cref="GameInstall"/> строится напрямую.</summary>
    /// <param name="tmp">Временный каталог.</param>
    /// <returns>Описание установки.</returns>
    private static GameInstall FakeGame(CoreTempDir tmp) => new(
        tmp.Path,
        "1.44.0.0",
        tmp.Combine("mods/1.44.0.0"),
        tmp.Combine("res_mods/1.44.0.0"),
        tmp.Combine("python.log"));

    // ------------------------------------------------------------------ реальная установка

    /// <summary>
    /// На настоящей установке hello_tanker не стоит, а в <c>res_mods\&lt;версия&gt;\scripts\client\gui\mods\</c>
    /// обычно лежат ЧУЖИЕ точки входа и пакеты сторонних модов. Чужое не должно попасть в результат
    /// ни при каких условиях: файлов, кроме точки входа и пакета самого проекта, в нём быть не может,
    /// а их у hello_tanker в игре нет. Версия каталогов берётся у определителя по <c>paths.xml</c>,
    /// а не строкой: после обновления клиента зонд смотрит уже в новый каталог.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void Probe_RealInstall_DoesNotReportForeignMods()
    {
        GameInstall game = TestEnvironment.RequireGame();

        DeployState state = _probe.Probe(game, HelloTanker());

        Assert.True(state.IsAbsent, "hello_tanker в игре не установлен — проба обязана это увидеть.");
        Assert.False(state.InMods);
        Assert.False(state.InResMods);
        Assert.False(state.IsShadowed);
        Assert.Empty(state.ResModsFiles);
        Assert.Null(state.MtmodPath);
    }

    // ------------------------------------------------------------------ фейковая установка

    /// <summary>Каталогов игры нет вовсе — <see cref="DeployState.Absent"/>, без исключения.</summary>
    [Fact]
    public void Probe_EmptyGameDir_ReturnsAbsent()
    {
        using CoreTempDir tmp = new();

        DeployState state = _probe.Probe(FakeGame(tmp), HelloTanker());

        Assert.True(state.IsAbsent);
        Assert.Empty(state.ResModsFiles);
        Assert.Empty(state.StaleMtmodPaths);
    }

    /// <summary>Только .mtmod в mods — перекрытия нет.</summary>
    [Fact]
    public void Probe_OnlyMtmod_NotShadowed()
    {
        using CoreTempDir tmp = new();
        string mtmod = tmp.WriteFile("mods/1.44.0.0/ru.local.hello_tanker_1.0.0.mtmod", "zip");

        DeployState state = _probe.Probe(FakeGame(tmp), HelloTanker());

        Assert.True(state.InMods);
        Assert.False(state.InResMods);
        Assert.False(state.IsShadowed);
        Assert.Equal(mtmod, state.MtmodPath);
        Assert.Equal(3, state.MtmodSizeBytes);
        Assert.NotNull(state.MtmodWriteTimeUtc);
    }

    /// <summary>
    /// В paths.xml у строки mods стоит <c>mode="recursive"</c>, поэтому .mtmod в подкаталоге
    /// игра тоже подхватит — и проба обязана.
    /// </summary>
    [Fact]
    public void Probe_MtmodInSubdirectory_IsFound()
    {
        using CoreTempDir tmp = new();
        string mtmod = tmp.WriteFile("mods/1.44.0.0/my_pack/ru.local.hello_tanker_1.0.0.mtmod", "zip");

        DeployState state = _probe.Probe(FakeGame(tmp), HelloTanker());

        Assert.True(state.InMods);
        Assert.Equal(mtmod, state.MtmodPath);
    }

    /// <summary>Несколько версий: действующая — свежайшая, остальные уезжают в <see cref="DeployState.StaleMtmodPaths"/>.</summary>
    [Fact]
    public void Probe_SeveralVersions_NewestWinsRestAreStale()
    {
        using CoreTempDir tmp = new();
        string old1 = tmp.WriteFile("mods/1.44.0.0/ru.local.hello_tanker_0.9.0.mtmod", "old");
        string old2 = tmp.WriteFile("mods/1.44.0.0/ru.local.hello_tanker_0.8.0.mtmod", "older");
        string fresh = tmp.WriteFile("mods/1.44.0.0/ru.local.hello_tanker_1.0.0.mtmod", "new");

        File.SetLastWriteTimeUtc(old2, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(old1, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(fresh, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        DeployState state = _probe.Probe(FakeGame(tmp), HelloTanker());

        Assert.Equal(fresh, state.MtmodPath);
        Assert.Equal(2, state.StaleMtmodPaths.Count);
        Assert.Contains(old1, state.StaleMtmodPaths);
        Assert.Contains(old2, state.StaleMtmodPaths);
    }

    /// <summary>Чужие .mtmod рядом с нашим не должны попасть ни в действующий, ни в устаревшие.</summary>
    [Fact]
    public void Probe_ForeignMtmods_AreIgnored()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("mods/1.44.0.0/com.modxvm.xvm_13.1.0.0059.mtmod", "xvm");
        tmp.WriteFile("mods/1.44.0.0/me.poliroid.pmod_1.81.12.mtmod", "pmod");
        tmp.WriteFile("mods/1.44.0.0/foreign_pack.mtmod", "foreign");
        string mine = tmp.WriteFile("mods/1.44.0.0/ru.local.hello_tanker_1.0.0.mtmod", "mine");

        DeployState state = _probe.Probe(FakeGame(tmp), HelloTanker());

        Assert.Equal(mine, state.MtmodPath);
        Assert.Empty(state.StaleMtmodPaths);
    }

    /// <summary>Dev-раскладка без установленного пакета: россыпь есть, перекрывать нечего.</summary>
    [Fact]
    public void Probe_OnlyResMods_NotShadowed()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("res_mods/1.44.0.0/scripts/client/gui/mods/mod_hello_tanker.pyc", "pyc");
        tmp.WriteFile("res_mods/1.44.0.0/scripts/client/gui/mods/hello_tanker/main.py", "print(1)");
        tmp.WriteFile("res_mods/1.44.0.0/scripts/client/gui/mods/hello_tanker/__init__.py", "");

        DeployState state = _probe.Probe(FakeGame(tmp), HelloTanker());

        Assert.False(state.InMods);
        Assert.True(state.InResMods);
        Assert.False(state.IsShadowed);
        Assert.Equal(3, state.ResModsFiles.Count);
        Assert.NotNull(state.ResModsNewestWriteTimeUtc);
    }

    /// <summary>
    /// ГЛАВНЫЙ случай: и .mtmod, и россыпь. res_mods в paths.xml стоит ПЕРВЫМ,
    /// значит пакет молча не читается — <see cref="DeployState.IsShadowed"/> обязан стать true.
    /// </summary>
    [Fact]
    public void Probe_BothPlaces_IsShadowed()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("mods/1.44.0.0/ru.local.hello_tanker_1.0.0.mtmod", "zip");
        tmp.WriteFile("res_mods/1.44.0.0/scripts/client/gui/mods/mod_hello_tanker.pyc", "pyc");

        DeployState state = _probe.Probe(FakeGame(tmp), HelloTanker());

        Assert.True(state.InMods);
        Assert.True(state.InResMods);
        Assert.True(state.IsShadowed);
        Assert.False(state.IsAbsent);
    }

    /// <summary>
    /// В res_mods лежат чужие моды с похожей раскладкой — они не наши и в результат не идут.
    /// Так выглядит res_mods почти любой установки со сторонними модами.
    /// </summary>
    [Fact]
    public void Probe_ForeignResModsFiles_AreNotReportedAsMine()
    {
        using CoreTempDir tmp = new();
        WriteForeignResMods(tmp);

        DeployState state = _probe.Probe(FakeGame(tmp), HelloTanker());

        Assert.True(state.IsAbsent);
        Assert.Empty(state.ResModsFiles);
    }

    /// <summary>
    /// Контрольный выстрел по той же раскладке: если ПРИТВОРИТЬСЯ чужим модом, проба его находит —
    /// и точку входа, и весь пакет, но не соседний чужой мод. Значит, пустой результат выше — не
    /// «проба ничего не умеет», а честная фильтрация по имени точки входа и пакета.
    /// </summary>
    [Fact]
    public void Probe_ForeignResModsFiles_AreFoundWhenNamesMatch()
    {
        using CoreTempDir tmp = new();
        WriteForeignResMods(tmp);

        ModProject pretend = new()
        {
            ProjectDir = @"C:\nowhere",
            ModId = "org.example.foreign_pack",
            EntryPointModule = "mod_foreign_pack",
            PackageName = "foreign_pack"
        };

        DeployState state = _probe.Probe(FakeGame(tmp), pretend);

        Assert.True(state.InResMods);
        Assert.False(state.InMods);         // .mtmod с таким ModId в mods\ нет
        Assert.False(state.IsShadowed);
        Assert.Equal(3, state.ResModsFiles.Count);
        Assert.Contains(state.ResModsFiles, f => f.EndsWith("mod_foreign_pack.pyc", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(state.ResModsFiles, f => f.EndsWith(Path.Combine("foreign_pack", "x.xml"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(state.ResModsFiles, f => f.Contains("foreign_tool", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Раскладывает в фейковом res_mods два чужих мода: точку входа с пакетом и одиночную точку входа.
    /// </summary>
    /// <param name="tmp">Фейковая установка.</param>
    private static void WriteForeignResMods(CoreTempDir tmp)
    {
        tmp.WriteFile("res_mods/1.44.0.0/scripts/client/gui/mods/mod_foreign_pack.pyc", "foreign");
        tmp.WriteFile("res_mods/1.44.0.0/scripts/client/gui/mods/foreign_pack/x.xml", "<x/>");
        tmp.WriteFile("res_mods/1.44.0.0/scripts/client/gui/mods/foreign_pack/objects/box.dds", "dds");
        tmp.WriteFile("res_mods/1.44.0.0/scripts/client/gui/mods/mod_foreign_tool.pyc", "foreign");
    }

    /// <summary>Точка входа может лежать россыпью и как <c>.py</c>, и как <c>.pyc</c> — учитываются обе.</summary>
    [Fact]
    public void Probe_EntryPointBothExtensions_BothReported()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("res_mods/1.44.0.0/scripts/client/gui/mods/mod_hello_tanker.pyc", "pyc");
        tmp.WriteFile("res_mods/1.44.0.0/scripts/client/gui/mods/mod_hello_tanker.py", "src");

        DeployState state = _probe.Probe(FakeGame(tmp), HelloTanker());

        Assert.True(state.InResMods);
        Assert.Equal(2, state.ResModsFiles.Count);
    }

    /// <summary>Мод без пакета (одна точка входа) — пустой <c>PackageName</c> не должен ничего ломать.</summary>
    [Fact]
    public void Probe_EmptyPackageName_UsesEntryPointOnly()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("res_mods/1.44.0.0/scripts/client/gui/mods/mod_solo.pyc", "pyc");

        ModProject solo = new()
        {
            ProjectDir = @"C:\nowhere",
            ModId = "ru.local.solo",
            EntryPointModule = "mod_solo",
            PackageName = string.Empty
        };

        DeployState state = _probe.Probe(FakeGame(tmp), solo);

        Assert.True(state.InResMods);
        Assert.Single(state.ResModsFiles);
    }

    /// <summary>
    /// Префикс <c>&lt;ModId&gt;_</c> сам по себе не делает файл нашим: хвост обязан быть похож
    /// на версию. Пример не выдуманный — в каталогах модов встречается библиотека
    /// <c>crosshairs_api_1.1.mtmod</c>, и мод с id <c>crosshairs</c> присваивал бы её себе.
    /// </summary>
    [Fact]
    public void Probe_PrefixWithoutVersion_IsNotMine()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("mods/1.44.0.0/crosshairs_api_1.1.mtmod", "foreign");
        string mine = tmp.WriteFile("mods/1.44.0.0/crosshairs_2.3.mtmod", "mine");

        ModProject project = new() { ProjectDir = @"C:\nowhere", ModId = "crosshairs" };

        DeployState state = _probe.Probe(FakeGame(tmp), project);

        Assert.Equal(mine, state.MtmodPath);
        Assert.Empty(state.StaleMtmodPaths);
    }

    // ------------------------------------------------------------------ dev_manifest.json

    /// <summary>
    /// Dev-раскладка копирует ВЕСЬ <c>stage\res</c>, а не только <c>scripts\client\gui\mods\</c>.
    /// Файлы вне этого каталога проба обязана увидеть по <c>dist\dev_manifest.json</c>, который
    /// пишет build.py, — иначе мод с <c>gui\</c> или <c>audioww\</c> оставит в игре невидимую
    /// панели россыпь, молча перекрывающую .mtmod.
    /// </summary>
    [Fact]
    public void Probe_DevManifest_FindsFilesOutsideModsScripts()
    {
        using CoreTempDir tmp = new();
        using CoreTempDir projectDir = new("wotcockpit-project");

        string gui = tmp.WriteFile("res_mods/1.44.0.0/gui/flash/my_panel.swf", "swf");
        string cfg = tmp.WriteFile("res_mods/1.44.0.0/engine_config.xml", "<x/>");
        string entry = tmp.WriteFile("res_mods/1.44.0.0/scripts/client/gui/mods/mod_hello_tanker.pyc", "pyc");

        WriteDevManifest(projectDir, gui, cfg, entry);

        DeployState state = _probe.Probe(FakeGame(tmp), HelloTanker(projectDir.Path));

        Assert.True(state.InResMods);
        Assert.Equal(3, state.ResModsFiles.Count);          // без дублей: entry есть и в манифесте, и по имени
        Assert.Contains(gui, state.ResModsFiles);
        Assert.Contains(cfg, state.ResModsFiles);
        Assert.Contains(entry, state.ResModsFiles);
    }

    /// <summary>
    /// Манифест — не разрешение показывать что угодно: пути вне <c>res_mods\&lt;ver&gt;</c>
    /// (устаревший манифест от другой установки) и уже удалённые файлы игнорируются.
    /// </summary>
    [Fact]
    public void Probe_DevManifest_IgnoresStaleAndOutsidePaths()
    {
        using CoreTempDir tmp = new();
        using CoreTempDir other = new("wotcockpit-othergame");
        using CoreTempDir projectDir = new("wotcockpit-project");

        string outside = other.WriteFile("res_mods/1.44.0.0/scripts/client/gui/mods/mod_hello_tanker.pyc", "pyc");
        string vanished = tmp.Combine("res_mods/1.44.0.0/gui/flash/deleted.swf");

        WriteDevManifest(projectDir, outside, vanished);

        DeployState state = _probe.Probe(FakeGame(tmp), HelloTanker(projectDir.Path));

        Assert.True(state.IsAbsent);
        Assert.Empty(state.ResModsFiles);
    }

    /// <summary>Битый или пустой манифест не должен ронять пробу — работает запасной путь.</summary>
    [Fact]
    public void Probe_BrokenDevManifest_FallsBackToNameProbe()
    {
        using CoreTempDir tmp = new();
        using CoreTempDir projectDir = new("wotcockpit-project");

        projectDir.WriteFile("dist/dev_manifest.json", "{ это не json");
        string entry = tmp.WriteFile("res_mods/1.44.0.0/scripts/client/gui/mods/mod_hello_tanker.pyc", "pyc");

        DeployState state = _probe.Probe(FakeGame(tmp), HelloTanker(projectDir.Path));

        Assert.True(state.InResMods);
        Assert.Equal(new[] { entry }, state.ResModsFiles);
    }

    private static void WriteDevManifest(CoreTempDir projectDir, params string[] files)
    {
        string json = "{\"files\": [" +
                      string.Join(", ", files.Select(f => "\"" + f.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"")) +
                      "]}";

        projectDir.WriteFile("dist/dev_manifest.json", json);
    }

    /// <summary>Пустой ModId — искать .mtmod не по чему, но падать нельзя.</summary>
    [Fact]
    public void Probe_EmptyModId_NoMtmodSearch()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("mods/1.44.0.0/whatever.mtmod", "zip");

        ModProject blank = new() { ProjectDir = @"C:\nowhere", ModId = string.Empty };

        DeployState state = _probe.Probe(FakeGame(tmp), blank);

        Assert.False(state.InMods);
        Assert.True(state.IsAbsent);
    }
}
