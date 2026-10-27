using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace WotModCockpit.Core.Mods;

/// <summary>Настройки обхода каталога модов. Значения по умолчанию рассчитаны на реальную установку (128 файлов, 1.9 ГБ).</summary>
public sealed record ModScanOptions
{
    /// <summary>Значения по умолчанию.</summary>
    public static ModScanOptions Default { get; } = new();

    /// <summary>
    /// Открывать ли архивы. false — только <c>stat</c> (перечисление 128 файлов + размеры и даты
    /// занимает 3 мс против 194 мс с чтением центральных каталогов). Нужен для «быстрого» обновления
    /// строки после операции, когда содержимое архива заведомо не менялось.
    /// </summary>
    public bool ReadArchives { get; init; } = true;

    /// <summary>
    /// Верхняя граница размера файла, который вообще имеет смысл хешировать при сверке
    /// «установлено против собрано». Чужие архивы не хешируются НИКОГДА (их 1.9 ГБ, пять штук
    /// больше 100 МБ), а свои заведомо меньше мегабайта — порог тут только страховочный.
    /// </summary>
    public long MaxHashSizeBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Максимальный размер <c>meta.xml</c>, который читается из архива. Реальные — сотни байт.</summary>
    public int MaxMetaBytes { get; init; } = 256 * 1024;
}

/// <summary>
/// Проект мода пользователя глазами лаунчера: что говорит <c>src\meta.xml</c> и что лежит в <c>dist\</c>.
/// <para>
/// Источник истины — именно <c>src\meta.xml</c>, а не <c>wotmod.json</c>: у трёх проектов пользователя
/// <c>wotmod.json</c> нет вовсе, а имя артефакта build.py собирает как <c>&lt;id&gt;_&lt;version&gt;.mtmod</c>.
/// </para>
/// </summary>
/// <param name="ProjectDir">Абсолютный путь к каталогу проекта.</param>
/// <param name="ModId"><c>&lt;id&gt;</c> из <c>src\meta.xml</c>, либо пустая строка, если meta.xml нет.</param>
/// <param name="Version"><c>&lt;version&gt;</c> из <c>src\meta.xml</c>.</param>
/// <param name="DisplayName"><c>&lt;name&gt;</c> из <c>src\meta.xml</c>, иначе имя каталога проекта.</param>
/// <param name="HasMeta">
/// false — у проекта ещё нет <c>src\meta.xml</c> (реальный случай: только что начатый проект).
/// Такой проект нечем связать с установленным файлом, и это само по себе стоит показать в UI.
/// </param>
/// <param name="MetaError">Причина, если meta.xml есть, но не читается.</param>
/// <param name="BuiltArtifactPath">Самый свежий <c>dist\&lt;id&gt;_*.mtmod</c>, либо <c>null</c>.</param>
/// <param name="BuiltVersion">Версия из имени собранного артефакта, либо <c>null</c>.</param>
/// <param name="BuiltSizeBytes">Размер собранного артефакта.</param>
/// <param name="BuiltWriteTimeUtc">Время записи собранного артефакта.</param>
public sealed record ModProjectInfo(
    string ProjectDir,
    string ModId,
    string Version,
    string DisplayName,
    bool HasMeta,
    string? MetaError,
    string? BuiltArtifactPath,
    string? BuiltVersion,
    long BuiltSizeBytes,
    DateTimeOffset? BuiltWriteTimeUtc)
{
    /// <summary>
    /// <c>&lt;description&gt;</c> из <c>src\meta.xml</c>, либо пустая строка.
    /// <para>
    /// Свойство, а не позиционный параметр: описания раньше в проекте не было вовсе, и строка
    /// «не установлен» показывала пустое место. Понадобилось оно, когда имя и описание стало
    /// можно править с экрана «Моды»: без него карточка не могла сказать, что в проекте
    /// описание уже новое, а в игре ещё старое.
    /// </para>
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// <c>&lt;name&gt;</c> из <c>src\meta.xml</c> КАК ЕСТЬ, либо пустая строка.
    /// <para>
    /// <see cref="DisplayName"/> для этого не годится: при пустом <c>&lt;name&gt;</c> он
    /// подставляет имя каталога проекта, и сравнение «имя в проекте против имени в игре»
    /// увидело бы различие там, где его нет, — пустое против пустого.
    /// </para>
    /// </summary>
    public string MetaName { get; init; } = string.Empty;
}

/// <summary>Состояние проекта пользователя относительно игры — строка группы «МОИ МОДЫ» для тех проектов, что не установлены.</summary>
/// <param name="Project">Сам проект.</param>
/// <param name="InstalledPaths">Все найденные в игре файлы этого мода (включая выключенные и лишние версии).</param>
/// <param name="ActivePath">Файл, который игра реально загрузит (включённый и самый свежий), либо <c>null</c>.</param>
/// <param name="Match">Сравнение <see cref="ActivePath"/> с собранным артефактом.</param>
public sealed record ProjectModStatus(
    ModProjectInfo Project,
    IReadOnlyList<string> InstalledPaths,
    string? ActivePath,
    ModBuildMatch Match)
{
    /// <summary>true, если мод установлен в игру хоть в каком-то виде.</summary>
    public bool IsInstalled => InstalledPaths.Count > 0;

    /// <summary>true, если файлы есть, но все выключены.</summary>
    public bool IsInstalledButDisabled => InstalledPaths.Count > 0 && ActivePath is null;
}

/// <summary>
/// Результат обхода каталога модов. Снимок: ничего не наблюдает и сам не обновляется.
/// </summary>
/// <param name="ModsDir">Каталог, который обходили.</param>
/// <param name="Mods">Найденные файлы модов — и включённые, и выключенные.</param>
/// <param name="Projects">Проекты пользователя и их состояние в игре (в том числе не установленные).</param>
/// <param name="UnmanagedFiles">
/// Прочие файлы каталога (не <c>.mtmod</c> и не <c>.mtmod.off</c>). Показываются справочно:
/// движок их игнорирует по маске, а кокпит их не трогает вообще.
/// </param>
/// <param name="Errors">Нефатальные сложности обхода (недоступный подкаталог и т.п.).</param>
/// <param name="Elapsed">Сколько занял обход.</param>
/// <param name="ScannedAtUtc">Момент снятия снимка.</param>
public sealed record ModLibraryScan(
    string ModsDir,
    IReadOnlyList<InstalledMod> Mods,
    IReadOnlyList<ProjectModStatus> Projects,
    IReadOnlyList<string> UnmanagedFiles,
    IReadOnlyList<string> Errors,
    TimeSpan Elapsed,
    DateTimeOffset ScannedAtUtc)
{
    /// <summary>Пустой снимок — годится как значение до первого обхода.</summary>
    /// <param name="modsDir">Каталог модов.</param>
    /// <returns>Снимок без единого мода.</returns>
    public static ModLibraryScan Empty(string modsDir = "") => new(
        modsDir,
        Array.Empty<InstalledMod>(),
        Array.Empty<ProjectModStatus>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        TimeSpan.Zero,
        DateTimeOffset.UtcNow);

    /// <summary>Сколько модов включено.</summary>
    public int EnabledCount => Mods.Count(m => m.State == ModState.Enabled);

    /// <summary>Сколько модов выключено. Их нельзя прятать: иначе пользователь будет чинить несуществующую поломку.</summary>
    public int DisabledCount => Mods.Count(m => m.State == ModState.Disabled);

    /// <summary>Сколько модов собрано из проектов пользователя.</summary>
    public int MineCount => Mods.Count(m => m.IsMine);
}

/// <summary>
/// Сводка по «россыпи» в <c>res_mods\&lt;версия&gt;</c> — ВТОРОЙ, независимой категории модов.
/// <para>
/// Почему она отдельно и только на чтение: в <c>paths.xml</c> строка <c>./res_mods/1.44.0.0</c>
/// идёт ПЕРВОЙ, то есть файлы отсюда молча перекрывают одноимённые файлы из любого <c>.mtmod</c>.
/// Но мод здесь — это не файл, а набор путей (<c>mod_foreign_pack.pyc</c> + каталог
/// <c>foreign_pack\</c> + <c>.dds</c>), и выключить его одним переименованием нельзя. Поэтому кокпит
/// показывает этот раздел как «россыпью в res_mods (не управляется)» и не трогает его никогда.
/// </para>
/// </summary>
/// <param name="Dir">Каталог <c>res_mods\&lt;версия&gt;</c>.</param>
/// <param name="Exists">Существует ли каталог.</param>
/// <param name="FileCount">Сколько в нём файлов (рекурсивно).</param>
/// <param name="TotalBytes">Суммарный размер.</param>
/// <param name="TopLevelNames">Имена первого уровня — по ним видно, что там вообще лежит.</param>
/// <param name="ScriptMods">
/// Точки входа модов-россыпи: имена <c>mod_*.py[c|o]</c> из
/// <c>scripts\client\gui\mods\</c>. Именно они перекрывают питоновские моды из пакетов.
/// </param>
public sealed record ResModsSummary(
    string Dir,
    bool Exists,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> TopLevelNames,
    IReadOnlyList<string> ScriptMods)
{
    /// <summary>Пустая сводка для случая, когда каталога нет.</summary>
    /// <param name="dir">Каталог.</param>
    /// <returns>Сводка «ничего нет».</returns>
    public static ResModsSummary Empty(string dir) =>
        new(dir, false, 0, 0, Array.Empty<string>(), Array.Empty<string>());

    /// <summary>true, если в res_mods вообще что-то лежит.</summary>
    public bool HasContent => FileCount > 0;
}

/// <summary>
/// Обход каталога модов игры. ТОЛЬКО ЧТЕНИЕ: ни одного вызова, который создаёт, переименовывает
/// или удаляет файлы. Всё, что умеет писать, живёт в <see cref="ModToggle"/>.
/// <para>
/// Три вещи, ради которых сканер написан именно так:
/// </para>
/// <list type="number">
///   <item><b>Без распаковки.</b> Открывается ZIP, читается центральный каталог, точечно достаётся
///         <c>meta.xml</c>. Реальный замер: 128 архивов, 74 665 записей, 1.9 ГБ — 194 мс. Самый
///         большой архив (163 МБ) отдаёт список записей за 1 мс.</item>
///   <item><b>Ничто не роняет список.</b> Битый ZIP, отсутствующий meta.xml, невалидный XML внутри,
///         занятый файл, отказ в доступе — всё это штатные состояния строки, а не исключение.
///         Скрыть от пользователя существующий в каталоге файл опаснее, чем показать его без данных.</item>
///   <item><b>Своё отличается от чужого.</b> Основной признак — совпадение <c>&lt;id&gt;</c> архива с
///         <c>&lt;id&gt;</c> из <c>src\meta.xml</c> проекта; запасной — имя файла по правилу build.py
///         <c>&lt;id&gt;_&lt;version&gt;.mtmod</c> (единственный доступный для модов без meta.xml).</item>
/// </list>
/// </summary>
public sealed class ModScanner
{
    /// <summary>Путь внутри архива, под которым движок ищет точки входа модов.</summary>
    private const string EntryPointPrefix = "res/scripts/client/gui/mods/";

    private readonly ModScanOptions _options;

    /// <summary>Создаёт сканер.</summary>
    /// <param name="options">Настройки обхода; <c>null</c> — значения по умолчанию.</param>
    public ModScanner(ModScanOptions? options = null) => _options = options ?? ModScanOptions.Default;

    // ------------------------------------------------------------------ полный обход

    /// <summary>
    /// Обходит каталог модов и связывает найденное с проектами пользователя.
    /// </summary>
    /// <param name="modsDir">Каталог <c>&lt;game&gt;\mods\&lt;version&gt;</c>. Может не существовать — это не ошибка.</param>
    /// <param name="projectDirs">Каталоги проектов пользователя; <c>null</c> — связывать не с чем.</param>
    /// <returns>Снимок каталога. Никогда не <c>null</c>, исключений не бросает.</returns>
    public ModLibraryScan Scan(string modsDir, IReadOnlyList<string>? projectDirs = null)
    {
        long started = Stopwatch.GetTimestamp();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<string> errors = [];

        IReadOnlyList<ModProjectInfo> projects = ReadProjects(projectDirs, errors);

        if (string.IsNullOrWhiteSpace(modsDir) || !SafeDirExists(modsDir))
        {
            if (!string.IsNullOrWhiteSpace(modsDir))
            {
                errors.Add($"каталог модов не найден: {modsDir}");
            }

            return new ModLibraryScan(
                modsDir ?? string.Empty,
                Array.Empty<InstalledMod>(),
                projects.Select(p => new ProjectModStatus(p, Array.Empty<string>(), null, ModBuildMatch.Unknown)).ToArray(),
                Array.Empty<string>(),
                errors,
                Stopwatch.GetElapsedTime(started),
                now);
        }

        List<InstalledMod> mods = [];
        List<string> unmanaged = [];

        foreach (string file in EnumerateFilesSafe(modsDir, errors))
        {
            if (ModFileNames.IsManagedFile(file))
            {
                mods.Add(ScanFile(modsDir, file, projects, now));
            }
            else
            {
                unmanaged.Add(file);
            }
        }

        MarkDuplicates(mods);

        return new ModLibraryScan(
            modsDir,
            mods,
            BuildProjectStatuses(projects, mods),
            unmanaged,
            errors,
            Stopwatch.GetElapsedTime(started),
            now);
    }

    // ------------------------------------------------------------------ один файл

    /// <summary>
    /// Перечитывает ОДИН файл — ровно то, что нужно после включения/выключения:
    /// перечитывать 128 архивов ради одной изменившейся строки незачем.
    /// </summary>
    /// <param name="modsDir">Каталог модов (нужен, чтобы посчитать относительный путь и группу).</param>
    /// <param name="fullPath">Абсолютный путь к файлу мода.</param>
    /// <param name="projects">Проекты пользователя для отметки «мой»; можно <c>null</c>.</param>
    /// <param name="scannedAtUtc">Момент снимка; <c>null</c> — сейчас.</param>
    /// <returns>
    /// Описание файла, либо <c>null</c>, если файла нет на диске или это вообще не файл мода.
    /// </returns>
    public InstalledMod? ScanOne(
        string modsDir,
        string fullPath,
        IReadOnlyList<ModProjectInfo>? projects = null,
        DateTimeOffset? scannedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !ModFileNames.IsManagedFile(fullPath))
        {
            return null;
        }

        try
        {
            if (!File.Exists(fullPath))
            {
                return null;
            }
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            return null;
        }

        return ScanFile(
            modsDir,
            fullPath,
            projects ?? Array.Empty<ModProjectInfo>(),
            scannedAtUtc ?? DateTimeOffset.UtcNow);
    }

    // ------------------------------------------------------------------ россыпь в res_mods

    /// <summary>
    /// Снимает сводку по <c>res_mods\&lt;версия&gt;</c>. ТОЛЬКО ЧТЕНИЕ и только сводку:
    /// управлять россыпью кокпит не берётся — ошибка здесь ломает то, что перекрывает пакеты.
    /// </summary>
    /// <param name="resModsDir">Каталог россыпи (<see cref="Models.GameInstall.ResModsDir"/>).</param>
    /// <returns>Сводка. Никогда не <c>null</c>, исключений не бросает.</returns>
    public ResModsSummary ScanResMods(string resModsDir)
    {
        if (string.IsNullOrWhiteSpace(resModsDir) || !SafeDirExists(resModsDir))
        {
            return ResModsSummary.Empty(resModsDir ?? string.Empty);
        }

        int count = 0;
        long bytes = 0;
        List<string> scriptMods = [];
        List<string> topLevel = [];

        string modsScripts = Path.Combine(resModsDir, "scripts", "client", "gui", "mods");

        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(resModsDir, "*", SearchOption.TopDirectoryOnly))
            {
                topLevel.Add(Path.GetFileName(entry));
            }

            topLevel.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            // перечислить верхний уровень не вышло — сводка всё равно должна вернуться
        }

        List<string> errors = [];
        foreach (string file in EnumerateFilesSafe(resModsDir, errors))
        {
            count++;
            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (Exception ex) when (IsExpectedIoFailure(ex))
            {
                // недоступный файл просто не учитываем в сумме
            }

            string name = Path.GetFileName(file);
            if (name.StartsWith("mod_", StringComparison.OrdinalIgnoreCase) &&
                (name.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(".pyo", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(Path.GetDirectoryName(file), modsScripts, StringComparison.OrdinalIgnoreCase))
            {
                scriptMods.Add(name);
            }
        }

        scriptMods.Sort(StringComparer.OrdinalIgnoreCase);
        return new ResModsSummary(resModsDir, true, count, bytes, topLevel, scriptMods);
    }

    // ------------------------------------------------------------------ проекты пользователя

    /// <summary>
    /// Читает <c>src\meta.xml</c> и содержимое <c>dist\</c> перечисленных каталогов проектов.
    /// Каталог, которого нет, и проект без meta.xml — штатные состояния, а не ошибки.
    /// </summary>
    /// <param name="projectDirs">Каталоги проектов.</param>
    /// <param name="errors">Сюда дописываются нефатальные сложности; можно <c>null</c>.</param>
    /// <returns>Описания проектов в том же порядке, в каком пришли каталоги.</returns>
    public IReadOnlyList<ModProjectInfo> ReadProjects(IReadOnlyList<string>? projectDirs, List<string>? errors = null)
    {
        if (projectDirs is null || projectDirs.Count == 0)
        {
            return Array.Empty<ModProjectInfo>();
        }

        List<ModProjectInfo> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in projectDirs)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string dir;
            try
            {
                dir = Path.GetFullPath(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors?.Add($"негодный путь проекта: {raw}");
                continue;
            }

            if (!seen.Add(dir) || !SafeDirExists(dir))
            {
                continue;
            }

            result.Add(ReadProject(dir));
        }

        return result;
    }

    /// <summary>Читает один проект.</summary>
    /// <param name="projectDir">Абсолютный путь к каталогу проекта.</param>
    /// <returns>Описание проекта; при отсутствии meta.xml — с <c>HasMeta = false</c>.</returns>
    private ModProjectInfo ReadProject(string projectDir)
    {
        string metaPath = Path.Combine(projectDir, "src", "meta.xml");
        string fallbackName = Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        string id = string.Empty;
        string version = string.Empty;
        string name = fallbackName;
        string description = string.Empty;
        string metaName = string.Empty;
        bool hasMeta = false;
        string? metaError = null;

        try
        {
            if (File.Exists(metaPath))
            {
                hasMeta = true;
                ModMeta meta = ParseMeta(File.ReadAllBytes(metaPath));
                if (meta.Error is not null)
                {
                    metaError = meta.Error;
                }
                else
                {
                    id = meta.Id ?? string.Empty;
                    version = meta.Version ?? string.Empty;
                    name = string.IsNullOrWhiteSpace(meta.Name) ? fallbackName : meta.Name!;
                    description = meta.Description ?? string.Empty;
                    metaName = meta.Name ?? string.Empty;
                }
            }
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            metaError = ex.Message;
        }

        (string? builtPath, string? builtVersion, long builtSize, DateTimeOffset? builtTime) = FindBuiltArtifact(projectDir, id);

        return new ModProjectInfo(projectDir, id, version, name, hasMeta, metaError, builtPath, builtVersion, builtSize, builtTime)
        {
            Description = description,
            MetaName = metaName,
        };
    }

    /// <summary>
    /// Ищет самый свежий <c>dist\&lt;id&gt;_*.mtmod</c>. Когда id неизвестен (нет meta.xml),
    /// берётся любой <c>.mtmod</c> в <c>dist\</c> — лучше показать «собрано вот это», чем ничего.
    /// </summary>
    /// <param name="projectDir">Каталог проекта.</param>
    /// <param name="modId">Идентификатор мода либо пустая строка.</param>
    /// <returns>Путь, версия, размер и время артефакта.</returns>
    private static (string? Path, string? Version, long Size, DateTimeOffset? Time) FindBuiltArtifact(string projectDir, string modId)
    {
        string dist = Path.Combine(projectDir, "dist");
        if (!SafeDirExists(dist))
        {
            return (null, null, 0, null);
        }

        FileInfo? newest = null;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dist, ModFileNames.EnabledSearchPattern, SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(file);
                if (modId.Length > 0 &&
                    !fileName.StartsWith(modId + "_", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.Equals(modId + ModFileNames.ModExtension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FileInfo info = new(file);
                if (newest is null || info.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                {
                    newest = info;
                }
            }
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            return (null, null, 0, null);
        }

        if (newest is null)
        {
            return (null, null, 0, null);
        }

        return (newest.FullName, ModFileNames.VersionFromFileName(newest.Name), newest.Length, newest.LastWriteTimeUtc);
    }

    // ------------------------------------------------------------------ разбор одного файла

    /// <summary>Собирает описание одного файла мода.</summary>
    /// <param name="modsDir">Каталог модов.</param>
    /// <param name="fullPath">Путь к файлу.</param>
    /// <param name="projects">Проекты пользователя.</param>
    /// <param name="now">Момент снимка.</param>
    /// <returns>Строка списка.</returns>
    private InstalledMod ScanFile(string modsDir, string fullPath, IReadOnlyList<ModProjectInfo> projects, DateTimeOffset now)
    {
        InstalledMod mod = new()
        {
            FullPath = fullPath,
            RelativePath = RelativeTo(modsDir, fullPath),
            GroupName = GroupOf(modsDir, fullPath),
            State = ModFileNames.StateOf(fullPath),
            ScannedAtUtc = now
        };

        try
        {
            FileInfo info = new(fullPath);
            mod = mod with { SizeBytes = info.Length, LastWriteTimeUtc = info.LastWriteTimeUtc };
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            mod = mod with { ArchiveStatus = ModArchiveStatus.Missing, ArchiveError = ex.Message };
        }

        if (_options.ReadArchives && mod.ArchiveStatus != ModArchiveStatus.Missing)
        {
            mod = ReadArchive(mod);
        }

        return LinkToProject(mod, projects);
    }

    /// <summary>
    /// Открывает архив и снимает всё, что берётся из центрального каталога, плюс <c>meta.xml</c>.
    /// Любая беда с файлом превращается в статус, а не в исключение.
    /// </summary>
    /// <param name="mod">Заготовка строки.</param>
    /// <returns>Строка с заполненными полями архива.</returns>
    private InstalledMod ReadArchive(InstalledMod mod)
    {
        try
        {
            // FileShare.ReadWrite — намеренно: если игра держит файл открытым, прочитать его
            // всё равно можно, и список обязан строиться. Монопольный доступ проверяется
            // отдельно и только перед ЗАПИСЬЮ (см. ModToggle).
            using FileStream stream = new(mod.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using ZipArchive zip = new(stream, ZipArchiveMode.Read, leaveOpen: false);

            List<string> entryPoints = [];
            List<string> native = [];
            int compressed = 0;
            string? iconEntry = null;
            ZipArchiveEntry? metaEntry = null;
            List<ZipArchiveEntry> pngCandidates = [];

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');

                if (metaEntry is null && name.Equals("meta.xml", StringComparison.OrdinalIgnoreCase))
                {
                    metaEntry = entry;
                }

                if (entry.CompressedLength != entry.Length)
                {
                    compressed++;
                }

                if (name.StartsWith(EntryPointPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string tail = name[EntryPointPrefix.Length..];
                    if (!tail.Contains('/') &&
                        tail.StartsWith("mod_", StringComparison.OrdinalIgnoreCase) &&
                        (tail.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase) ||
                         tail.EndsWith(".pyo", StringComparison.OrdinalIgnoreCase) ||
                         tail.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
                    {
                        entryPoints.Add(tail);
                    }
                }

                if (name.EndsWith(".pyd", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    native.Add(name);
                }

                if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                    entry.Length is > 0 and <= ModIcons.MaxIconBytes)
                {
                    pngCandidates.Add(entry);
                }
            }

            ModMeta meta = metaEntry is null ? ModMeta.Absent : ReadMetaEntry(metaEntry);
            iconEntry = ModIcons.PickIconEntry(pngCandidates, meta.Id);

            entryPoints.Sort(StringComparer.OrdinalIgnoreCase);
            native.Sort(StringComparer.OrdinalIgnoreCase);

            return mod with
            {
                ArchiveStatus = ModArchiveStatus.Ok,
                ArchiveError = null,
                EntryCount = zip.Entries.Count,
                EntryPointModules = entryPoints,
                NativeEntries = native,
                CompressedEntryCount = compressed,
                IconEntryPath = iconEntry,
                MetaStatus = meta.Status,
                ModId = meta.Id,
                MetaVersion = meta.Version,
                MetaName = meta.Name,
                Description = meta.Description,
                MetaError = meta.Error
            };
        }
        catch (InvalidDataException ex)
        {
            return mod with { ArchiveStatus = ModArchiveStatus.Broken, ArchiveError = ex.Message };
        }
        catch (FileNotFoundException ex)
        {
            return mod with { ArchiveStatus = ModArchiveStatus.Missing, ArchiveError = ex.Message };
        }
        catch (DirectoryNotFoundException ex)
        {
            return mod with { ArchiveStatus = ModArchiveStatus.Missing, ArchiveError = ex.Message };
        }
        catch (UnauthorizedAccessException ex)
        {
            return mod with { ArchiveStatus = ModArchiveStatus.AccessDenied, ArchiveError = ex.Message };
        }
        catch (IOException ex)
        {
            return mod with { ArchiveStatus = ModArchiveStatus.Locked, ArchiveError = ex.Message };
        }
    }

    /// <summary>Читает и разбирает запись <c>meta.xml</c>, ограничивая объём чтения.</summary>
    /// <param name="entry">Запись архива.</param>
    /// <returns>Разобранная мета либо описание поломки.</returns>
    private ModMeta ReadMetaEntry(ZipArchiveEntry entry)
    {
        try
        {
            if (entry.Length > _options.MaxMetaBytes)
            {
                return ModMeta.Failed($"meta.xml слишком велик ({entry.Length} Б)");
            }

            using Stream s = entry.Open();
            using MemoryStream buffer = new(capacity: (int)Math.Max(entry.Length, 256));
            s.CopyTo(buffer, 16 * 1024);
            return ParseMeta(buffer.ToArray());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OutOfMemoryException)
        {
            return ModMeta.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Разбирает <c>meta.xml</c>. Схема у модов не железная, поэтому:
    /// теги ищутся без учёта регистра и на любой глубине, а <c>&lt;versionLst&gt;</c> принимается
    /// как версия (так делают некоторые модпаки).
    /// DTD запрещён намеренно — файл чужой.
    /// </summary>
    /// <param name="bytes">Содержимое файла.</param>
    /// <returns>Разобранная мета либо описание поломки.</returns>
    private static ModMeta ParseMeta(byte[] bytes)
    {
        try
        {
            using MemoryStream ms = new(bytes, writable: false);
            XmlReaderSettings settings = new()
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                CloseInput = false
            };
            using XmlReader reader = XmlReader.Create(ms, settings);
            XDocument doc = XDocument.Load(reader);

            string? Tag(params string[] names)
            {
                foreach (XElement el in doc.Descendants())
                {
                    foreach (string n in names)
                    {
                        if (el.Name.LocalName.Equals(n, StringComparison.OrdinalIgnoreCase) && !el.HasElements)
                        {
                            string value = el.Value.Trim();
                            if (value.Length > 0)
                            {
                                return value;
                            }
                        }
                    }
                }

                return null;
            }

            return new ModMeta(
                ModMetaStatus.Present,
                Tag("id"),
                Tag("version", "versionLst"),
                Tag("name"),
                Tag("description"),
                null);
        }
        catch (XmlException ex)
        {
            return ModMeta.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            return ModMeta.Failed(ex.Message);
        }
    }

    // ------------------------------------------------------------------ свой / чужой

    /// <summary>
    /// Отмечает мод как «мой», если он собран из известного проекта.
    /// <para>
    /// По <c>&lt;id&gt;</c> из meta.xml архива — надёжно, и среди ВСЕХ проектов, а не первого
    /// подходящего. По имени файла <c>&lt;id&gt;_&lt;версия&gt;.mtmod</c> — только у модов без
    /// <c>&lt;id&gt;</c> (без meta.xml), для которых другого способа нет, и тогда побеждает самый
    /// длинный <c>&lt;id&gt;</c>: иначе <c>ru.local.clock_lite_1.0.mtmod</c> досталось бы проекту
    /// <c>ru.local.clock</c>, стоящему в списке раньше, а архив со своим чужим <c>&lt;id&gt;</c>
    /// стал бы «моим» за одно совпадение начала имени.
    /// </para>
    /// </summary>
    /// <param name="mod">Строка списка.</param>
    /// <param name="projects">Проекты пользователя.</param>
    /// <returns>Та же строка, возможно с заполненным <see cref="InstalledMod.Mine"/>.</returns>
    private InstalledMod LinkToProject(InstalledMod mod, IReadOnlyList<ModProjectInfo> projects)
    {
        if (projects.Count == 0)
        {
            return mod;
        }

        ModProjectInfo? match = null;
        bool byId = false;
        if (!string.IsNullOrEmpty(mod.ModId))
        {
            match = projects.FirstOrDefault(p => p.ModId.Length > 0 && mod.ModId.Equals(p.ModId, StringComparison.OrdinalIgnoreCase));
            byId = match is not null;
        }
        else
        {
            string baseName = ModFileNames.BaseFileName(mod.FullPath);
            match = projects
                .Where(p => p.ModId.Length > 0 &&
                            (baseName.StartsWith(p.ModId + "_", StringComparison.OrdinalIgnoreCase) ||
                             baseName.Equals(p.ModId + ModFileNames.ModExtension, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(p => p.ModId.Length)
                .FirstOrDefault();
        }

        if (match is null)
        {
            return mod;
        }

        return mod with
        {
            Mine = new MyModLink(
                match.ProjectDir,
                match.ModId,
                byId,
                match.BuiltArtifactPath,
                match.BuiltVersion,
                CompareWithBuild(mod, match))
        };
    }

    /// <summary>
    /// Сравнивает установленный файл с собранным артефактом. Сначала по размеру (дёшево),
    /// и только при совпадении — SHA-256 обоих. Чужие моды не хешируются никогда:
    /// сюда попадают исключительно свои, их размер — сотни килобайт.
    /// </summary>
    /// <param name="mod">Установленный файл.</param>
    /// <param name="project">Проект с собранным артефактом.</param>
    /// <returns>Результат сравнения.</returns>
    private ModBuildMatch CompareWithBuild(InstalledMod mod, ModProjectInfo project)
    {
        if (project.BuiltArtifactPath is null)
        {
            return ModBuildMatch.NoBuild;
        }

        if (mod.SizeBytes != project.BuiltSizeBytes)
        {
            return ModBuildMatch.DifferentBytes;
        }

        if (mod.SizeBytes > _options.MaxHashSizeBytes)
        {
            return ModBuildMatch.Unknown;
        }

        try
        {
            return HashFile(mod.FullPath).SequenceEqual(HashFile(project.BuiltArtifactPath))
                ? ModBuildMatch.SameBytes
                : ModBuildMatch.DifferentBytes;
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            return ModBuildMatch.Unknown;
        }
    }

    /// <summary>Считает SHA-256 файла потоком, не поднимая его целиком в память.</summary>
    /// <param name="path">Путь к файлу.</param>
    /// <returns>Хеш.</returns>
    private static byte[] HashFile(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        using SHA256 sha = SHA256.Create();
        return sha.ComputeHash(fs);
    }

    // ------------------------------------------------------------------ сводка по каталогу

    /// <summary>
    /// Помечает два вида дублей: один <c>&lt;id&gt;</c> в двух файлах (грузится непонятно что)
    /// и пару <c>X.mtmod</c> + <c>X.mtmod.off</c> (выключенный мод снова включён переустановкой сборки).
    /// </summary>
    /// <param name="mods">Список модов; правится на месте.</param>
    private static void MarkDuplicates(List<InstalledMod> mods)
    {
        Dictionary<string, int> byId = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> byBaseName = new(StringComparer.OrdinalIgnoreCase);

        foreach (InstalledMod m in mods)
        {
            if (!string.IsNullOrWhiteSpace(m.ModId))
            {
                byId[m.ModId!] = byId.GetValueOrDefault(m.ModId!) + 1;
            }

            string key = TwinKey(m);
            byBaseName[key] = byBaseName.GetValueOrDefault(key) + 1;
        }

        for (int i = 0; i < mods.Count; i++)
        {
            InstalledMod m = mods[i];
            bool dupId = !string.IsNullOrWhiteSpace(m.ModId) && byId.GetValueOrDefault(m.ModId!) > 1;
            bool twin = byBaseName.GetValueOrDefault(TwinKey(m)) > 1;

            if (dupId || twin)
            {
                mods[i] = m with { HasDuplicateId = dupId, HasStateTwin = twin };
            }
        }
    }

    /// <summary>Ключ «того же самого файла в другом состоянии»: каталог + имя без <c>.off</c>.</summary>
    /// <param name="mod">Мод.</param>
    /// <returns>Ключ пары.</returns>
    private static string TwinKey(InstalledMod mod) =>
        (Path.GetDirectoryName(mod.FullPath) ?? string.Empty) + "|" + ModFileNames.BaseFileName(mod.FullPath);

    /// <summary>Сводит проекты пользователя с тем, что реально нашлось в игре.</summary>
    /// <param name="projects">Проекты.</param>
    /// <param name="mods">Найденные моды.</param>
    /// <returns>Состояние каждого проекта.</returns>
    private static IReadOnlyList<ProjectModStatus> BuildProjectStatuses(
        IReadOnlyList<ModProjectInfo> projects,
        List<InstalledMod> mods)
    {
        if (projects.Count == 0)
        {
            return Array.Empty<ProjectModStatus>();
        }

        List<ProjectModStatus> result = [];

        foreach (ModProjectInfo p in projects)
        {
            List<InstalledMod> installed = mods
                .Where(m => m.Mine is not null && m.Mine.ProjectDir.Equals(p.ProjectDir, StringComparison.OrdinalIgnoreCase))
                .ToList();

            InstalledMod? active = installed
                .Where(m => m.State == ModState.Enabled)
                .OrderByDescending(m => m.LastWriteTimeUtc)
                .ThenBy(m => m.FullPath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            ModBuildMatch match = active?.Mine?.Match
                ?? (p.BuiltArtifactPath is null ? ModBuildMatch.NoBuild : ModBuildMatch.Unknown);

            result.Add(new ProjectModStatus(
                p,
                installed.Select(m => m.FullPath).ToArray(),
                active?.FullPath,
                match));
        }

        return result;
    }

    // ------------------------------------------------------------------ мелочи

    /// <summary>
    /// Перечисляет ВСЕ файлы каталога рекурсивно — ровно так же, как их видит движок
    /// (<c>mode="recursive"</c> в paths.xml). Недоступный подкаталог не валит обход.
    /// </summary>
    /// <param name="root">Каталог модов.</param>
    /// <param name="errors">Сюда пишутся сложности.</param>
    /// <returns>Абсолютные пути файлов.</returns>
    private static IEnumerable<string> EnumerateFilesSafe(string root, List<string> errors)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Device,
            MatchType = MatchType.Simple,
            ReturnSpecialDirectories = false
        };

        IEnumerator<string> enumerator;
        try
        {
            enumerator = Directory.EnumerateFiles(root, "*", options).GetEnumerator();
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            errors.Add($"не удалось перечислить {root}: {ex.Message}");
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception ex) when (IsExpectedIoFailure(ex))
                {
                    errors.Add($"обход {root} прерван: {ex.Message}");
                    break;
                }

                yield return current;
            }
        }
    }

    /// <summary>Путь относительно каталога модов, с разделителями Windows.</summary>
    /// <param name="root">Каталог модов.</param>
    /// <param name="fullPath">Путь к файлу.</param>
    /// <returns>Относительный путь либо имя файла, если посчитать не вышло.</returns>
    private static string RelativeTo(string root, string fullPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                return Path.GetRelativePath(root, fullPath).Replace('/', Path.DirectorySeparatorChar);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            // ниже вернём имя файла
        }

        return Path.GetFileName(fullPath);
    }

    /// <summary>Первый сегмент относительного пути — та самая группировка, которую сделал модпак.</summary>
    /// <param name="root">Каталог модов.</param>
    /// <param name="fullPath">Путь к файлу.</param>
    /// <returns>Имя подпапки либо пустая строка для корня.</returns>
    private static string GroupOf(string root, string fullPath)
    {
        string rel = RelativeTo(root, fullPath);
        int sep = rel.IndexOf(Path.DirectorySeparatorChar);
        return sep <= 0 ? string.Empty : rel[..sep];
    }

    /// <summary>Существование каталога без исключений.</summary>
    /// <param name="dir">Путь.</param>
    /// <returns>true, если каталог есть.</returns>
    private static bool SafeDirExists(string dir)
    {
        try
        {
            return Directory.Exists(dir);
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            return false;
        }
    }

    /// <summary>Обыденные беды файловой системы, от которых обход не должен падать.</summary>
    /// <param name="ex">Исключение.</param>
    /// <returns>true, если его можно проглотить.</returns>
    internal static bool IsExpectedIoFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException;

    /// <summary>Разобранное содержимое <c>meta.xml</c>.</summary>
    /// <param name="Status">Состояние разбора.</param>
    /// <param name="Id">Идентификатор мода.</param>
    /// <param name="Version">Версия.</param>
    /// <param name="Name">Имя.</param>
    /// <param name="Description">Описание.</param>
    /// <param name="Error">Причина поломки, если разбор не удался.</param>
    private sealed record ModMeta(
        ModMetaStatus Status,
        string? Id,
        string? Version,
        string? Name,
        string? Description,
        string? Error)
    {
        /// <summary>Мета отсутствует — это норма для 87 модов из 128.</summary>
        public static ModMeta Absent { get; } = new(ModMetaStatus.Missing, null, null, null, null, null);

        /// <summary>Мета есть, но не разбирается.</summary>
        /// <param name="error">Текст ошибки.</param>
        /// <returns>Описание поломки.</returns>
        public static ModMeta Failed(string error) => new(ModMetaStatus.Broken, null, null, null, null, error);
    }
}
