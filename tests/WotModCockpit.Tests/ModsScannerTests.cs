using WotModCockpit.Core.Mods;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты <see cref="ModScanner"/>. Всё — на фейковом каталоге во временной папке.
/// Сканер обязан быть ТОЛЬКО читающим и не падать ни на одном из состояний, которые реально
/// встречаются в каталоге игры: мод без meta.xml (таких на живой установке большинство), битый XML внутри meta.xml,
/// огрызок вместо архива, чужое расширение, занятый файл, вложенные подпапки.
/// </summary>
public class ModsScannerTests
{
    private static readonly ModScanner Scanner = new();

    // ------------------------------------------------------------------ перечисление

    /// <summary>Каталога модов нет — пустой снимок и замечание, а не исключение.</summary>
    [Fact]
    public void Scan_MissingDir_ReturnsEmptyScan()
    {
        ModLibraryScan scan = Scanner.Scan(@"C:\nowhere\mods\1.44.0.0");

        Assert.Empty(scan.Mods);
        Assert.Single(scan.Errors);
        Assert.Equal(0, scan.EnabledCount);
    }

    /// <summary>Пустой путь не должен ничего ломать.</summary>
    [Fact]
    public void Scan_EmptyPath_ReturnsEmptyScan()
    {
        ModLibraryScan scan = Scanner.Scan(string.Empty);

        Assert.Empty(scan.Mods);
        Assert.Empty(scan.Errors);
    }

    /// <summary>
    /// Обход рекурсивный — ровно как у движка (<c>mode="recursive"</c> в paths.xml).
    /// Подпапка становится группой: это готовая группировка, сделанная модпаком.
    /// </summary>
    [Fact]
    public void Scan_WalksSubfolders_AndFillsGroups()
    {
        using FakeGameDir game = new();
        game.AddMod("root_1.0.0.mtmod");
        game.AddMod("audioww/voice_pack_2.0.mtmod");
        game.AddMod("foreign_pack/skins_3.1.mtmod");
        game.AddMod("com.modxvm.xfw/xfw.crashfix_12.0.0.mtmod");

        ModLibraryScan scan = Scanner.Scan(game.ModsDir);

        Assert.Equal(4, scan.Mods.Count);
        Assert.Equal(string.Empty, Find(scan, "root_1.0.0.mtmod").GroupName);
        Assert.Equal("audioww", Find(scan, "voice_pack_2.0.mtmod").GroupName);
        Assert.Equal("foreign_pack", Find(scan, "skins_3.1.mtmod").GroupName);
        Assert.Equal("com.modxvm.xfw", Find(scan, "xfw.crashfix_12.0.0.mtmod").GroupName);
        Assert.Equal(
            Path.Combine("audioww", "voice_pack_2.0.mtmod"),
            Find(scan, "voice_pack_2.0.mtmod").RelativePath);
    }

    /// <summary>
    /// Чужое расширение — не мод. Движок такие файлы игнорирует по маске, и кокпит их
    /// НЕ берёт в управление, но показывает справочно: скрывать существующий файл нельзя.
    /// </summary>
    [Fact]
    public void Scan_ForeignExtensions_AreNotManaged()
    {
        using FakeGameDir game = new();
        game.AddMod("real_1.0.0.mtmod");
        game.AddRawFile("old.wotmod");
        game.AddRawFile("readme.txt");
        game.AddRawFile("net.openwg/org.pypi.sentry-sdk_1.45.0.wotmod");

        ModLibraryScan scan = Scanner.Scan(game.ModsDir);

        InstalledMod only = Assert.Single(scan.Mods);
        Assert.Equal("real_1.0.0.mtmod", only.FileName);
        Assert.Equal(3, scan.UnmanagedFiles.Count);
        Assert.DoesNotContain(scan.UnmanagedFiles, f => f.EndsWith(".mtmod", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Выключенный мод виден в списке и помечен, а не спрятан.</summary>
    [Fact]
    public void Scan_DisabledMod_IsListedAsDisabled()
    {
        using FakeGameDir game = new();
        game.AddMod("on_1.0.0.mtmod");
        game.AddMod("off_1.0.0.mtmod.off");

        ModLibraryScan scan = Scanner.Scan(game.ModsDir);

        Assert.Equal(2, scan.Mods.Count);
        Assert.Equal(1, scan.EnabledCount);
        Assert.Equal(1, scan.DisabledCount);
        Assert.Equal(ModState.Disabled, Find(scan, "off_1.0.0.mtmod.off").State);
    }

    // ------------------------------------------------------------------ meta.xml

    /// <summary>meta.xml разбирается, все четыре поля попадают в строку.</summary>
    [Fact]
    public void Scan_ReadsMetaXml()
    {
        using FakeGameDir game = new();
        game.AddMod(
            "ru.example.alpha_1.0.0.mtmod",
            FakeGameDir.Meta("ru.example.alpha", "1.0.0", "Alpha Path", "Описание мода Alpha."));

        InstalledMod mod = Assert.Single(Scanner.Scan(game.ModsDir).Mods);

        Assert.Equal(ModMetaStatus.Present, mod.MetaStatus);
        Assert.Equal("ru.example.alpha", mod.ModId);
        Assert.Equal("1.0.0", mod.MetaVersion);
        Assert.Equal("Alpha Path", mod.MetaName);
        Assert.Equal("Описание мода Alpha.", mod.Description);
        Assert.Equal("Alpha Path", mod.DisplayName);
        Assert.Equal(ModArchiveStatus.Ok, mod.ArchiveStatus);
    }

    /// <summary>
    /// Модов без meta.xml на живой установке большинство — имя и версия обязаны
    /// выводиться из имени файла, а не превращаться в пустую строку.
    /// </summary>
    [Fact]
    public void Scan_NoMeta_FallsBackToFileName()
    {
        using FakeGameDir game = new();
        game.AddMod("audioww/audio_voice_pack_rus_1.2.3.mtmod");

        InstalledMod mod = Assert.Single(Scanner.Scan(game.ModsDir).Mods);

        Assert.Equal(ModMetaStatus.Missing, mod.MetaStatus);
        Assert.Null(mod.ModId);
        Assert.Equal("audio_voice_pack_rus", mod.DisplayName);
        Assert.Equal("1.2.3", mod.DisplayVersion);
    }

    /// <summary>
    /// Битый meta.xml — реальный случай (парсер отвечает «not well-formed, line 5 column 44»),
    /// при этом сам мод рабочий. Строка обязана остаться в списке.
    /// </summary>
    [Fact]
    public void Scan_BrokenMetaXml_KeepsRowWithMark()
    {
        using FakeGameDir game = new();
        game.AddMod("tweaker/mod.example.TrackTextures_2.2.4.mtmod", "<root><id>broken<id></root>");

        InstalledMod mod = Assert.Single(Scanner.Scan(game.ModsDir).Mods);

        Assert.Equal(ModMetaStatus.Broken, mod.MetaStatus);
        Assert.NotNull(mod.MetaError);
        Assert.Equal(ModArchiveStatus.Ok, mod.ArchiveStatus);
        Assert.Equal("mod.example.TrackTextures", mod.DisplayName);
    }

    /// <summary>
    /// Встречаются моды, которые кладут <c>&lt;versionLst&gt;</c> вместо <c>&lt;version&gt;</c>, —
    /// это тоже версия.
    /// </summary>
    [Fact]
    public void Scan_VersionLst_IsAcceptedAsVersion()
    {
        using FakeGameDir game = new();
        game.AddMod(
            "modpack.example.atlas_pack.mtmod",
            "<root><id>modpack.example.atlas</id><versionLst>1.44.0.0</versionLst></root>");

        InstalledMod mod = Assert.Single(Scanner.Scan(game.ModsDir).Mods);

        Assert.Equal("modpack.example.atlas", mod.ModId);
        Assert.Equal("1.44.0.0", mod.MetaVersion);
    }

    // ------------------------------------------------------------------ содержимое архива

    /// <summary>Точка входа и нативные модули определяются по центральному каталогу, без распаковки.</summary>
    [Fact]
    public void Scan_DetectsEntryPointAndNativeCode()
    {
        using FakeGameDir game = new();
        game.AddMod("net.openwg.common_2.5.2.mtmod", null,
        [
            FakeGameDir.Entry("res/scripts/client/gui/mods/mod_openwg.pyc", "bytecode"),
            FakeGameDir.Entry("res/scripts/client/gui/mods/openwg/main.pyc", "bytecode"),
            FakeGameDir.Entry("res/scripts/common/openwg_audio.pyd", "native"),
            FakeGameDir.Entry("res/gui/flash/thing.swf", "flash")
        ]);
        game.AddMod("foreign_pack/pure_resource_1.0.mtmod", null,
        [
            FakeGameDir.Entry("res/vehicles/skin.dds", "dds")
        ]);

        ModLibraryScan scan = Scanner.Scan(game.ModsDir);

        InstalledMod python = Find(scan, "net.openwg.common_2.5.2.mtmod");
        Assert.True(python.HasEntryPoint);
        Assert.Equal("mod_openwg.pyc", Assert.Single(python.EntryPointModules));
        Assert.True(python.HasNativeCode);
        Assert.Equal(4, python.EntryCount);

        InstalledMod resource = Find(scan, "pure_resource_1.0.mtmod");
        Assert.False(resource.HasEntryPoint);
        Assert.False(resource.HasNativeCode);
    }

    /// <summary>
    /// Сжатые записи обязаны быть видны: движок такой мод не загрузит вовсе
    /// («load error: compression not supported»), а реальные моды пакуются методом Stored.
    /// </summary>
    [Fact]
    public void Scan_CompressedEntries_AreFlagged()
    {
        using FakeGameDir game = new();
        game.AddMod("stored_1.0.mtmod", FakeGameDir.Meta("a.stored", "1.0", "Stored", "d"));
        game.AddMod("deflated_1.0.mtmod", null,
            [FakeGameDir.Entry("res/big.txt", new string('a', 4096))],
            compress: true);

        ModLibraryScan scan = Scanner.Scan(game.ModsDir);

        Assert.Equal(0, Find(scan, "stored_1.0.mtmod").CompressedEntryCount);
        Assert.True(Find(scan, "deflated_1.0.mtmod").CompressedEntryCount > 0);
    }

    /// <summary>Битый архив не роняет обход: строка остаётся, статус говорит правду.</summary>
    [Fact]
    public void Scan_BrokenArchive_IsListedAsUnreadable()
    {
        using FakeGameDir game = new();
        game.AddMod("good_1.0.mtmod");
        game.AddBrokenMod("bad_1.0.mtmod");

        ModLibraryScan scan = Scanner.Scan(game.ModsDir);

        Assert.Equal(2, scan.Mods.Count);
        InstalledMod bad = Find(scan, "bad_1.0.mtmod");
        Assert.Equal(ModArchiveStatus.Broken, bad.ArchiveStatus);
        Assert.True(bad.IsUnreadable);
        Assert.NotNull(bad.ArchiveError);
        Assert.Equal("bad", bad.DisplayName);
        Assert.Equal(ModArchiveStatus.Ok, Find(scan, "good_1.0.mtmod").ArchiveStatus);
    }

    /// <summary>
    /// Файл, удерживаемый другим процессом монопольно, обязан попасть в список с пометкой
    /// «занят», а не выкинуть исключение посреди обхода.
    /// </summary>
    [Fact]
    public void Scan_LockedFile_IsListedAsLocked()
    {
        using FakeGameDir game = new();
        string locked = game.AddMod("locked_1.0.mtmod", FakeGameDir.Meta("a.locked", "1.0", "Locked", "d"));
        game.AddMod("free_1.0.mtmod");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ModLibraryScan scan = Scanner.Scan(game.ModsDir);

            Assert.Equal(2, scan.Mods.Count);
            InstalledMod row = Find(scan, "locked_1.0.mtmod");
            Assert.Equal(ModArchiveStatus.Locked, row.ArchiveStatus);
            Assert.True(row.SizeBytes > 0, "stat работает и на занятом файле — размер и дата обязаны быть");
        }
    }

    /// <summary>Быстрый режим не открывает архивы вовсе — только stat.</summary>
    [Fact]
    public void Scan_WithoutArchives_SkipsZipReading()
    {
        using FakeGameDir game = new();
        game.AddMod("x_1.0.mtmod", FakeGameDir.Meta("a.x", "1.0", "X", "d"));

        ModScanner fast = new(new ModScanOptions { ReadArchives = false });
        InstalledMod mod = Assert.Single(fast.Scan(game.ModsDir).Mods);

        Assert.Equal(ModArchiveStatus.NotRead, mod.ArchiveStatus);
        Assert.Equal(ModMetaStatus.Unknown, mod.MetaStatus);
        Assert.True(mod.SizeBytes > 0);
    }

    // ------------------------------------------------------------------ дубли

    /// <summary>Один id в двух файлах — ровно та ловушка «правлю, а в игре не меняется».</summary>
    [Fact]
    public void Scan_DuplicateId_IsMarked()
    {
        using FakeGameDir game = new();
        game.AddMod("me.poliroid.pmod_1.81.11.mtmod", FakeGameDir.Meta("me.poliroid.pmod", "1.81.11", "pmod", "d"));
        game.AddMod("me.poliroid.pmod_1.81.12.mtmod", FakeGameDir.Meta("me.poliroid.pmod", "1.81.12", "pmod", "d"));
        game.AddMod("other_1.0.mtmod", FakeGameDir.Meta("other", "1.0", "Other", "d"));

        ModLibraryScan scan = Scanner.Scan(game.ModsDir);

        Assert.True(Find(scan, "me.poliroid.pmod_1.81.11.mtmod").HasDuplicateId);
        Assert.True(Find(scan, "me.poliroid.pmod_1.81.12.mtmod").HasDuplicateId);
        Assert.False(Find(scan, "other_1.0.mtmod").HasDuplicateId);
    }

    /// <summary>
    /// Пара <c>X.mtmod</c> + <c>X.mtmod.off</c> значит, что выключенный мод снова включён
    /// (обычно переустановкой сборки) — это надо показать явно.
    /// </summary>
    [Fact]
    public void Scan_EnabledAndDisabledTwins_AreMarked()
    {
        using FakeGameDir game = new();
        game.AddMod("foreign_pack.mtmod");
        game.AddMod("foreign_pack.mtmod.off");
        game.AddMod("lonely_1.0.mtmod");

        ModLibraryScan scan = Scanner.Scan(game.ModsDir);

        Assert.True(Find(scan, "foreign_pack.mtmod").HasStateTwin);
        Assert.True(Find(scan, "foreign_pack.mtmod.off").HasStateTwin);
        Assert.False(Find(scan, "lonely_1.0.mtmod").HasStateTwin);
    }

    // ------------------------------------------------------------------ свой / чужой

    /// <summary>
    /// Мод считается своим по совпадению id с <c>src\meta.xml</c> проекта, а установленный файл
    /// сверяется с <c>dist\</c> побайтово: деплой — обычное копирование, поэтому ответ точный.
    /// </summary>
    [Fact]
    public void Scan_LinksInstalledFileToProject_AndComparesWithBuild()
    {
        using FakeGameDir game = new();
        string project = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha Path");
        string artifact = Path.Combine(project, "dist", "ru.example.alpha_1.0.0.mtmod");
        game.CopyIntoMods(artifact, "ru.example.alpha_1.0.0.mtmod");
        game.AddMod("foreign_1.0.mtmod", FakeGameDir.Meta("tv.example.foreign", "1.0", "Foreign", "d"));

        ModLibraryScan scan = Scanner.Scan(game.ModsDir, [project]);

        InstalledMod mine = Find(scan, "ru.example.alpha_1.0.0.mtmod");
        Assert.True(mine.IsMine);
        Assert.True(mine.Mine!.MatchedById);
        Assert.Equal(ModBuildMatch.SameBytes, mine.Mine.Match);
        Assert.Equal("1.0.0", mine.Mine.BuiltVersion);

        Assert.False(Find(scan, "foreign_1.0.mtmod").IsMine);
        Assert.Equal(1, scan.MineCount);
    }

    /// <summary>В игре лежит не та сборка, что в dist — это обязано быть видно.</summary>
    [Fact]
    public void Scan_DifferentBuildInGame_IsDetected()
    {
        using FakeGameDir game = new();
        string project = game.AddProject("wot-mod-delta", "ru.example.delta", "1.0.0", "Delta Path");
        game.AddMod(
            "ru.example.delta_1.0.0.mtmod",
            FakeGameDir.Meta("ru.example.delta", "1.0.0", "Delta Path", "старая сборка"),
            [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_delta.pyc", "ДРУГИЕ байты")]);

        ModLibraryScan scan = Scanner.Scan(game.ModsDir, [project]);

        InstalledMod mine = Assert.Single(scan.Mods);
        Assert.True(mine.IsMine);
        Assert.Equal(ModBuildMatch.DifferentBytes, mine.Mine!.Match);
    }

    /// <summary>
    /// Запасной признак «мой»: у мода нет meta.xml, но имя файла построено по правилу build.py
    /// <c>&lt;id&gt;_&lt;version&gt;.mtmod</c>.
    /// </summary>
    [Fact]
    public void Scan_MatchesProjectByFileName_WhenArchiveHasNoMeta()
    {
        using FakeGameDir game = new();
        string project = game.AddProject("wot-mod-delta", "ru.example.delta", "1.0.0", "Delta Path");
        game.AddMod("ru.example.delta_1.0.0.mtmod");

        InstalledMod mod = Assert.Single(Scanner.Scan(game.ModsDir, [project]).Mods);

        Assert.True(mod.IsMine);
        Assert.False(mod.Mine!.MatchedById);
    }

    /// <summary>
    /// Проект без <c>src\meta.xml</c> (обычное дело для только что начатого проекта) не должен
    /// ничего ломать: он показывается отдельно, как проект, который пока не с чем связать.
    /// </summary>
    [Fact]
    public void Scan_ProjectWithoutMeta_IsReportedButNotLinked()
    {
        using FakeGameDir game = new();
        string amber = game.AddProject("wot-mod-amber", modId: null, buildArtifact: false);
        game.AddMod("someone_else_1.0.mtmod");

        ModLibraryScan scan = Scanner.Scan(game.ModsDir, [amber]);

        ProjectModStatus status = Assert.Single(scan.Projects);
        Assert.False(status.Project.HasMeta);
        Assert.Empty(status.Project.ModId);
        Assert.False(status.IsInstalled);
        Assert.Equal(0, scan.MineCount);
    }

    /// <summary>Собранный, но не установленный проект — строка «не установлен» в группе «МОИ МОДЫ».</summary>
    [Fact]
    public void Scan_ProjectBuiltButNotInstalled_IsReported()
    {
        using FakeGameDir game = new();
        string project = game.AddProject("wot-mod-delta", "ru.example.delta", "1.0.0", "Delta Path");

        ModLibraryScan scan = Scanner.Scan(game.ModsDir, [project]);

        ProjectModStatus status = Assert.Single(scan.Projects);
        Assert.False(status.IsInstalled);
        Assert.Null(status.ActivePath);
        Assert.NotNull(status.Project.BuiltArtifactPath);
        Assert.Equal("1.0.0", status.Project.BuiltVersion);
    }

    /// <summary>Установлен, но выключен — отдельное состояние, иначе пользователь будет искать пропавший мод.</summary>
    [Fact]
    public void Scan_ProjectInstalledButDisabled_IsReported()
    {
        using FakeGameDir game = new();
        string project = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha Path");
        string artifact = Path.Combine(project, "dist", "ru.example.alpha_1.0.0.mtmod");
        game.CopyIntoMods(artifact, "ru.example.alpha_1.0.0.mtmod.off");

        ModLibraryScan scan = Scanner.Scan(game.ModsDir, [project]);

        ProjectModStatus status = Assert.Single(scan.Projects);
        Assert.True(status.IsInstalled);
        Assert.True(status.IsInstalledButDisabled);
    }

    /// <summary>Несуществующий каталог проекта просто пропускается.</summary>
    [Fact]
    public void ReadProjects_SkipsMissingDirs()
    {
        IReadOnlyList<ModProjectInfo> projects = Scanner.ReadProjects([@"C:\nowhere\wot-mod-ghost", string.Empty]);

        Assert.Empty(projects);
    }

    // ------------------------------------------------------------------ россыпь в res_mods

    /// <summary>
    /// Россыпь в <c>res_mods</c> — вторая, независимая категория модов, которая перекрывает пакеты.
    /// Кокпит обязан её ВИДЕТЬ (иначе «выключил мод, а он работает» не объяснить),
    /// но не берётся ею управлять.
    /// </summary>
    [Fact]
    public void ScanResMods_SummarisesLooseFiles()
    {
        using FakeGameDir game = new();
        string resMods = game.Combine("res_mods/1.44.0.0");
        Directory.CreateDirectory(Path.Combine(resMods, "scripts", "client", "gui", "mods", "foreign_pack"));
        File.WriteAllText(Path.Combine(resMods, "engine_config.xml"), "<root/>");
        File.WriteAllText(Path.Combine(resMods, "scripts", "client", "gui", "mods", "mod_foreign_pack.pyc"), "bytecode");
        File.WriteAllText(Path.Combine(resMods, "scripts", "client", "gui", "mods", "foreign_pack", "skin.dds"), "dds");

        ResModsSummary summary = new ModScanner().ScanResMods(resMods);

        Assert.True(summary.Exists);
        Assert.True(summary.HasContent);
        Assert.Equal(3, summary.FileCount);
        Assert.True(summary.TotalBytes > 0);
        Assert.Contains("engine_config.xml", summary.TopLevelNames);
        Assert.Equal("mod_foreign_pack.pyc", Assert.Single(summary.ScriptMods));
    }

    /// <summary>Каталога res_mods нет — пустая сводка, без исключения.</summary>
    [Fact]
    public void ScanResMods_MissingDir_ReturnsEmpty()
    {
        ResModsSummary summary = new ModScanner().ScanResMods(@"C:\nowhere\res_mods\1.44.0.0");

        Assert.False(summary.Exists);
        Assert.False(summary.HasContent);
        Assert.Empty(summary.ScriptMods);
    }

    // ------------------------------------------------------------------ один файл

    /// <summary>Перечитывание одной строки: то, чем пользуются после включения/выключения.</summary>
    [Fact]
    public void ScanOne_ReturnsSingleRow_OrNullForMissing()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("x_1.0.mtmod", FakeGameDir.Meta("a.x", "1.0", "X", "d"));

        InstalledMod? found = Scanner.ScanOne(game.ModsDir, path);
        Assert.NotNull(found);
        Assert.Equal("a.x", found!.ModId);

        Assert.Null(Scanner.ScanOne(game.ModsDir, Path.Combine(game.ModsDir, "ghost.mtmod")));
        Assert.Null(Scanner.ScanOne(game.ModsDir, game.AddRawFile("notamod.txt")));
    }

    // ------------------------------------------------------------------ имена файлов

    /// <summary>Разбор версии из имени по правилу build.py.</summary>
    /// <param name="fileName">Имя файла.</param>
    /// <param name="expected">Ожидаемая версия (<c>null</c> — версии в имени нет).</param>
    [Theory]
    [InlineData("ru.example.alpha_1.0.0.mtmod", "1.0.0")]
    [InlineData("com.modxvm.xvm_13.1.0.0059.mtmod", "13.1.0.0059")]
    [InlineData("mod.example.TrackTextures_2.2.4.256.260809.224857.mtmod", "2.2.4.256.260809.224857")]
    [InlineData("ru.example.alpha_1.0.0.mtmod.off", "1.0.0")]
    [InlineData("foreign_pack.mtmod", null)]
    [InlineData("crosshairs_api_1.1.mtmod", "1.1")]
    public void VersionFromFileName_Works(string fileName, string? expected) =>
        Assert.Equal(expected, ModFileNames.VersionFromFileName(fileName));

    /// <summary>Имя для показа, выведенное из имени файла.</summary>
    /// <param name="fileName">Имя файла.</param>
    /// <param name="expected">Ожидаемое имя.</param>
    [Theory]
    [InlineData("ru.example.alpha_1.0.0.mtmod", "ru.example.alpha")]
    [InlineData("me.poliroid.battlehits_2.2.5.mtmod.off", "me.poliroid.battlehits")]
    [InlineData("foreign_pack.mtmod", "foreign_pack")]
    public void DisplayNameFromFileName_Works(string fileName, string expected) =>
        Assert.Equal(expected, ModFileNames.DisplayNameFromFileName(fileName));

    /// <summary>Превращение имени во включённое/выключенное и обратно — строго взаимно обратное.</summary>
    [Fact]
    public void ModFileNames_ToDisabled_AndBack_AreInverse()
    {
        const string path = @"C:\Games\Tanki\mods\1.44.0.0\audioww\x_1.0.mtmod";

        string? off = ModFileNames.ToDisabledPath(path);
        Assert.Equal(path + ".off", off);
        Assert.Equal(path, ModFileNames.ToEnabledPath(off!));

        Assert.Equal(off, ModFileNames.ToDisabledPath(off!));
        Assert.Equal(path, ModFileNames.ToEnabledPath(path));
        Assert.Null(ModFileNames.ToDisabledPath(@"D:\x\readme.txt"));
    }

    /// <summary>Регистр расширения не важен — маска Win32 его тоже не различает.</summary>
    [Fact]
    public void ModFileNames_IgnoresExtensionCase()
    {
        Assert.True(ModFileNames.IsEnabledFile("X.MTMOD"));
        Assert.True(ModFileNames.IsDisabledFile("X.MtMod.OFF"));
        Assert.False(ModFileNames.IsManagedFile("X.mtmodoff"));
        Assert.False(ModFileNames.IsManagedFile("X.wotmod"));
    }

    // ------------------------------------------------------------------ помощники

    /// <summary>Находит строку по имени файла — иначе тесты утонут в индексах.</summary>
    /// <param name="scan">Снимок.</param>
    /// <param name="fileName">Имя файла.</param>
    /// <returns>Строка списка.</returns>
    private static InstalledMod Find(ModLibraryScan scan, string fileName) =>
        Assert.Single(scan.Mods, m => m.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
}
