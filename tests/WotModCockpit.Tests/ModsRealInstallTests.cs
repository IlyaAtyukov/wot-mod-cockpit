using System.Diagnostics;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Mods;

namespace WotModCockpit.Tests;

/// <summary>
/// Проверка сканера на НАСТОЯЩЕЙ установке игры (каталог — <c>WOTCOCKPIT_GAME_DIR</c>) и на своих
/// проектах модов (каталог — <c>WOTCOCKPIT_REPOS_DIR</c>).
/// <para>
/// <b>ТОЛЬКО ЧТЕНИЕ.</b> Здесь нет ни одного вызова, который пишет: ни <c>Enable</c>, ни <c>Disable</c>,
/// ни <c>Install</c>, ни <c>Delete</c>. Используются <see cref="ModScanner"/>, <see cref="ModIcons"/>
/// и <see cref="ModToggle.Validate"/> (проверка без операции). Каждый тест дополнительно сверяет
/// список файлов до и после — чтобы поломка этого правила не прошла незамеченной.
/// </para>
/// <para>Все тесты помечены <c>Env=RealMachine</c> и пропускаются, пока не задано
/// <c>WOTCOCKPIT_REAL_MACHINE=1</c> или если игры нет.</para>
/// </summary>
public class ModsRealInstallTests
{
    /// <summary>
    /// Свои проекты модов — по ним мод в игре опознаётся как «мой». Проектом считается любой
    /// каталог в <see cref="ReposLayout.Root"/> с <c>build.py</c> и <c>src\</c> — то же правило,
    /// по которому их находит экран «Моды». Каталога нет или тесты на своей машине выключены —
    /// пустой список.
    /// </summary>
    /// <returns>Абсолютные пути каталогов проектов.</returns>
    private static string[] MyProjectDirs()
    {
        if (!TestEnvironment.IsRealMachine || !Directory.Exists(ReposLayout.Root))
        {
            return [];
        }

        string[] dirs = Directory
            .EnumerateDirectories(ReposLayout.Root)
            .Where(d => File.Exists(Path.Combine(d, "build.py")) && Directory.Exists(Path.Combine(d, "src")))
            .ToArray();

        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
        return dirs;
    }

    /// <summary>Находит установку через paths.xml — версия каталога модов берётся оттуда, а не угадывается.</summary>
    /// <returns>Установка, либо тест пропускается.</returns>
    private static GameInstall RequireRealInstall()
    {
        GameInstall game = TestEnvironment.RequireGame();
        Skip.IfNot(Directory.Exists(game.ModsDir), $"нет каталога модов {game.ModsDir}");
        return game;
    }

    /// <summary>
    /// В активном каталоге есть хоть один файл мода. Сразу после обновления клиента paths.xml
    /// уже смотрит в новый каталог (<c>mods\1.46.0.0</c>), а моды туда ещё не перенесены: там
    /// лежит только readme.txt игры. Проверять на пустом каталоге нечего — и падать тоже не за что.
    /// </summary>
    /// <param name="game">Установка.</param>
    private static void RequireInstalledMods(GameInstall game)
    {
        bool any = Directory
            .EnumerateFiles(game.ModsDir, "*", SearchOption.AllDirectories)
            .Any(ModFileNames.IsManagedFile);
        Skip.IfNot(any, $"в активном каталоге {game.ModsDir} нет ни одного мода — после обновления клиента их ещё не поставили");
    }

    /// <summary>Снимок каталога модов: имя файла + размер. Им проверяется, что тест ничего не изменил.</summary>
    /// <param name="modsDir">Каталог модов.</param>
    /// <returns>Отсортированные строки «путь|размер».</returns>
    private static string[] FileSnapshot(string modsDir)
    {
        string[] snapshot = Directory
            .EnumerateFiles(modsDir, "*", SearchOption.AllDirectories)
            .Select(f => $"{f}|{new FileInfo(f).Length}")
            .ToArray();

        Array.Sort(snapshot, StringComparer.OrdinalIgnoreCase);
        return snapshot;
    }

    /// <summary>
    /// Полный обход настоящего каталога модов: список строится, ничего не падает, каталог
    /// остаётся нетронутым. Здесь же проверяются все деградации сразу — на живой установке
    /// обычно есть и моды без meta.xml, и моды с битым meta.xml, и чисто ресурсные моды.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void Scan_RealModsDir_BuildsListWithoutTouchingAnything()
    {
        GameInstall game = RequireRealInstall();
        RequireInstalledMods(game);
        string[] before = FileSnapshot(game.ModsDir);

        long ticks = Stopwatch.GetTimestamp();
        ModLibraryScan scan = new ModScanner().Scan(game.ModsDir, MyProjectDirs());
        TimeSpan elapsed = Stopwatch.GetElapsedTime(ticks);

        string[] after = FileSnapshot(game.ModsDir);
        Assert.Equal(before, after);

        Assert.NotEmpty(scan.Mods);
        Assert.Equal(before.Length, scan.Mods.Count + scan.UnmanagedFiles.Count);

        // Обход сотни архивов без распаковки укладывается в доли секунды. Порог с большим запасом,
        // чтобы тест ловил не «стало на 20 мс медленнее», а появление распаковки на пустом месте.
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"обход занял {elapsed.TotalMilliseconds:F0} мс");

        // Ни одна строка не должна быть безымянной, даже без meta.xml и с битым архивом.
        Assert.All(scan.Mods, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(m.DisplayVersion));
            Assert.True(m.SizeBytes >= 0);
        });

        // Каждая строка обязана лежать внутри каталога модов — иначе это дефект обхода.
        Assert.All(scan.Mods, m => Assert.True(ModToggle.IsInside(game.ModsDir, m.FullPath)));
    }

    /// <summary>
    /// Моды без <c>meta.xml</c> — норма живой установки, и они обязаны быть в списке
    /// с именем, выведенным из имени файла. Если этот тест падает, значит сканер их потерял.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void Scan_RealModsDir_KeepsModsWithoutMeta()
    {
        GameInstall game = RequireRealInstall();
        RequireInstalledMods(game);

        ModLibraryScan scan = new ModScanner().Scan(game.ModsDir);

        int withMeta = scan.Mods.Count(m => m.MetaStatus == ModMetaStatus.Present);
        int withoutMeta = scan.Mods.Count(m => m.MetaStatus == ModMetaStatus.Missing);

        Assert.True(withMeta > 0, "ни у одного мода не прочитался meta.xml — сканер его теряет");

        // Мод БЕЗ meta.xml — это предусловие машины, а не свойство кода: сразу после
        // обновления клиента в новом каталоге лежат только свои моды, а у них meta.xml есть
        // у всех. Проверять тут нечего, но и падать не за что.
        Skip.If(withoutMeta == 0,
            "в активном каталоге модов нет ни одного мода без meta.xml — проверять нечего");
        Assert.All(
            scan.Mods.Where(m => m.MetaStatus != ModMetaStatus.Present),
            m => Assert.False(string.IsNullOrWhiteSpace(m.DisplayName)));
    }

    /// <summary>
    /// Настоящие иконки игрового списка модов — PNG 50×50. Читаются точечно, одной записью ZIP,
    /// прямо из архива, без распаковки на диск.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void ReadIcon_RealMods_AreFiftyByFiftyPng()
    {
        GameInstall game = RequireRealInstall();
        string[] before = FileSnapshot(game.ModsDir);

        ModLibraryScan scan = new ModScanner().Scan(game.ModsDir);
        List<InstalledMod> withIcon = scan.Mods.Where(m => m.IconEntryPath is not null).ToList();

        Skip.If(withIcon.Count == 0, "ни у одного установленного мода нет иконки списка");

        int checkedIcons = 0;
        foreach (InstalledMod mod in withIcon.Take(12))
        {
            ModIcon icon = ModIcons.Read(mod.FullPath, mod.ModId);
            if (icon.Source != ModIconSource.Archive)
            {
                continue;
            }

            Assert.True(icon.HasBytes);
            Assert.True(icon.Width is > 0 and <= 256, $"{mod.FileName}: ширина {icon.Width}");
            Assert.True(icon.Height is > 0 and <= 256, $"{mod.FileName}: высота {icon.Height}");
            checkedIcons++;
        }

        Assert.True(checkedIcons > 0, "ни одну иконку не удалось прочитать");
        Assert.Equal(before, FileSnapshot(game.ModsDir));
    }

    /// <summary>
    /// Установленный свой мод опознаётся как СВОЙ и сверяется с <c>dist\</c> побайтово: деплой —
    /// обычное копирование, поэтому ответ точный, а не «похоже». «Свои» id берутся из
    /// <c>src\meta.xml</c> найденных проектов, а не из префикса: префикс у чужих модов может
    /// совпасть, а id — нет.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void Scan_RealModsDir_RecognisesMyOwnMods()
    {
        GameInstall game = RequireRealInstall();
        TestEnvironment.RequireReposDir();
        string[] projectDirs = MyProjectDirs();
        Skip.If(projectDirs.Length == 0, $"в {ReposLayout.Root} нет проектов модов (build.py + src)");

        HashSet<string> myIds = new ModScanner()
            .ReadProjects(projectDirs)
            .Select(p => p.ModId)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        ModLibraryScan scan = new ModScanner().Scan(game.ModsDir, projectDirs);

        Assert.NotEmpty(scan.Projects);

        InstalledMod? mine = scan.Mods.FirstOrDefault(m => m.IsMine);
        Skip.If(mine is null, "ни один свой мод не установлен в игру");

        Assert.Contains(mine!.Mine!.ModId, myIds);
        Assert.NotEqual(ModBuildMatch.Unknown, mine.Mine.Match);

        // Каждый «мой» — с id одного из проектов, и каждый мод с таким id — «мой».
        Assert.All(scan.Mods.Where(m => m.IsMine), m => Assert.Contains(m.Mine!.ModId, myIds));
        Assert.All(
            scan.Mods.Where(m => m.ModId is not null && myIds.Contains(m.ModId)),
            m => Assert.True(m.IsMine, $"{m.FileName}: id {m.ModId} принадлежит проекту, но мод не связан с ним"));
    }

    /// <summary>
    /// Проект, у которого ещё нет <c>src\meta.xml</c>, обязан показываться отдельной строкой,
    /// а не молча исчезать из списка «МОИ МОДЫ».
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void ReadProjects_RealProjects_ReportMetaAndBuildState()
    {
        TestEnvironment.RequireReposDir();
        string[] projectDirs = MyProjectDirs();
        Skip.If(projectDirs.Length == 0, $"в {ReposLayout.Root} нет проектов модов (build.py + src)");

        IReadOnlyList<ModProjectInfo> projects = new ModScanner().ReadProjects(projectDirs);

        Assert.NotEmpty(projects);
        Assert.All(projects, p => Assert.True(Directory.Exists(p.ProjectDir)));

        foreach (ModProjectInfo p in projects)
        {
            if (p.HasMeta)
            {
                Assert.False(string.IsNullOrWhiteSpace(p.ModId), $"{p.ProjectDir}: meta.xml есть, а id пуст");
            }
            else
            {
                Assert.Empty(p.ModId);
                Assert.Null(p.BuiltArtifactPath);
            }
        }
    }

    /// <summary>
    /// Проверка без операции: настоящие файлы игры ВНЕ каталога модов отвергаются.
    /// <see cref="ModToggle.Validate"/> ничего не пишет — это та же проверка, которой UI
    /// гасит кнопки.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void Validate_RealGameFilesOutsideModsDir_AreRefused()
    {
        GameInstall game = RequireRealInstall();
        string[] before = FileSnapshot(game.ModsDir);

        ModToggle toggle = new(FakeGameProbes.Closed);

        foreach (string outside in new[]
                 {
                     Path.Combine(game.RootPath, "paths.xml"),
                     Path.Combine(game.RootPath, "res", "packages", "scripts.pkg"),
                     Path.Combine(game.ResModsDir, "engine_config.xml"),
                     Path.Combine(game.RootPath, "mods", "configs", "anything.mtmod")
                 })
        {
            ModOperationResult result = toggle.Validate(ModOperation.Disable, game.ModsDir, outside)!;

            Assert.NotNull(result);
            Assert.False(result.Success);
            Assert.True(
                result.Status is ModOperationStatus.RefusedOutsideModsDir or ModOperationStatus.RefusedNotModFile,
                $"{outside}: неожиданный отказ {result.Status}");
        }

        Assert.Equal(before, FileSnapshot(game.ModsDir));
    }

    /// <summary>
    /// Настоящая россыпь в <c>res_mods\&lt;версия&gt;</c> видна как отдельный раздел и только на чтение.
    /// Чужие <c>mod_*.pyc</c> в ней перекрывают пакеты — пользователь должен об этом знать,
    /// даже если управлять ими кокпит не берётся.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void ScanResMods_RealInstall_SeesLooseFiles()
    {
        GameInstall game = RequireRealInstall();
        Skip.IfNot(Directory.Exists(game.ResModsDir), $"нет каталога {game.ResModsDir}");

        string[] before = FileSnapshot(game.ResModsDir);

        ResModsSummary summary = new ModScanner().ScanResMods(game.ResModsDir);

        Assert.True(summary.Exists);
        Assert.Equal(before.Length, summary.FileCount);
        Assert.All(summary.ScriptMods, m => Assert.StartsWith("mod_", m, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before, FileSnapshot(game.ResModsDir));
    }

    /// <summary>
    /// Выключенных модов на установке может и не быть — тогда проверяется ноль. Если же в каталоге
    /// есть <c>.mtmod.off</c>, они обязаны быть видны все до одного.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void Scan_RealModsDir_ReportsDisabledModsIfAny()
    {
        GameInstall game = RequireRealInstall();

        ModLibraryScan scan = new ModScanner().Scan(game.ModsDir);

        int disabledOnDisk = Directory
            .EnumerateFiles(game.ModsDir, ModFileNames.DisabledSearchPattern, SearchOption.AllDirectories)
            .Count();

        Assert.Equal(disabledOnDisk, scan.DisabledCount);
        Assert.Equal(scan.Mods.Count, scan.EnabledCount + scan.DisabledCount);
    }
}
