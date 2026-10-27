namespace WotModCockpit.Core.Mods;

/// <summary>
/// Состояние файла мода в каталоге игры.
/// <para>
/// Способ выключения выбран не произвольно: в <c>paths.xml</c> каталог модов подключён строкой
/// <c>&lt;Path mask="*.mtmod" mode="recursive" root="res"&gt;./mods/1.44.0.0&lt;/Path&gt;</c>.
/// Маска задана ДАННЫМИ (строк «mtmod»/«wotmod» в <c>Tanki.exe</c> нет вообще), рекурсия настоящая
/// (в реальных установках большинство модов лежит в подпапках вроде <c>audioww\</c> или <c>foreign_pack\</c>).
/// Значит перенос файла в соседнюю подпапку НЕ выключает мод, а смена расширения — выключает,
/// причём молча: файл, не подходящий под маску, движок просто не перечисляет (в python.log
/// про него нет ни одной строки — ни «not loaded», ни «ignored»).
/// </para>
/// </summary>
public enum ModState
{
    /// <summary>Файл называется <c>*.mtmod</c> — движок его видит и грузит.</summary>
    Enabled,

    /// <summary>Файл называется <c>*.mtmod.off</c> — под маску не подходит, движок его не видит. Байты на месте.</summary>
    Disabled
}

/// <summary>Что удалось узнать из <c>meta.xml</c> внутри архива.</summary>
public enum ModMetaStatus
{
    /// <summary>Архив не читался (или не прочитался) — про meta.xml ничего не известно.</summary>
    Unknown,

    /// <summary><c>meta.xml</c> разобран, поля взяты из него.</summary>
    Present,

    /// <summary>
    /// <c>meta.xml</c> в архиве нет. Это НОРМА, а не поломка: на реальной установке
    /// его нет у 87 модов из 128 (весь <c>audioww\</c>, большая часть прицелов и ангаров).
    /// Имя и версия в этом случае выводятся из имени файла.
    /// </summary>
    Missing,

    /// <summary>
    /// <c>meta.xml</c> есть, но не разбирается как XML. Реальный случай на установке пользователя:
    /// <c>tweaker\mod.MCTCreator.TrackCrashTextures</c> — «not well-formed, line 5 column 44».
    /// Мод при этом рабочий, поэтому строка обязана показываться, а не исчезать.
    /// </summary>
    Broken
}

/// <summary>Чем закончилась попытка прочитать архив мода.</summary>
public enum ModArchiveStatus
{
    /// <summary>Архив намеренно не открывали (быстрый режим сканирования).</summary>
    NotRead,

    /// <summary>Центральный каталог ZIP прочитан.</summary>
    Ok,

    /// <summary>Файл не открывается как ZIP (обрезан, побит, не тот формат).</summary>
    Broken,

    /// <summary>Файл занят другим процессом — почти всегда это запущенная игра или открытый архиватор.</summary>
    Locked,

    /// <summary>Отказано в доступе.</summary>
    AccessDenied,

    /// <summary>Файла уже нет (исчез между перечислением и чтением).</summary>
    Missing
}

/// <summary>Сравнение установленного в игру файла с тем, что лежит в <c>dist\</c> проекта.</summary>
public enum ModBuildMatch
{
    /// <summary>Сравнение не проводилось (не наш мод, либо не удалось прочитать один из файлов).</summary>
    Unknown,

    /// <summary>У проекта нет собранного артефакта в <c>dist\</c> — сравнивать не с чем.</summary>
    NoBuild,

    /// <summary>
    /// Файлы совпадают побайтово (SHA-256). Проверено на реальной паре «dist — игра»:
    /// деплой — обычное копирование, поэтому совпадение точное, вплоть до времени записи.
    /// </summary>
    SameBytes,

    /// <summary>Файлы различаются: в игре лежит НЕ та сборка, что в <c>dist\</c>.</summary>
    DifferentBytes
}

/// <summary>
/// Связь установленного файла с проектом мода на диске разработчика — то, что делает мод «моим».
/// </summary>
/// <param name="ProjectDir">Абсолютный путь к каталогу проекта, например <c>&lt;репозитории&gt;\my-mod</c>.</param>
/// <param name="ModId">Идентификатор из <c>src\meta.xml</c> проекта, например <c>ru.example.my_mod</c>.</param>
/// <param name="MatchedById">
/// true — совпал <c>&lt;id&gt;</c> из meta.xml архива с <c>&lt;id&gt;</c> проекта (основной, надёжный признак).
/// false — совпало только имя файла по правилу build.py <c>&lt;id&gt;_&lt;version&gt;.mtmod</c> (запасной признак,
/// он же единственный для модов без meta.xml).
/// </param>
/// <param name="BuiltArtifactPath">Путь к самому свежему <c>dist\&lt;id&gt;_*.mtmod</c>, либо <c>null</c>.</param>
/// <param name="BuiltVersion">Версия из имени собранного артефакта, либо <c>null</c>.</param>
/// <param name="Match">Результат побайтового сравнения установленного файла с собранным.</param>
public sealed record MyModLink(
    string ProjectDir,
    string ModId,
    bool MatchedById,
    string? BuiltArtifactPath,
    string? BuiltVersion,
    ModBuildMatch Match);

/// <summary>
/// Один файл мода в каталоге игры — строка списка лаунчера.
/// <para>
/// Собирается ОДНИМ проходом: перечисление + <c>stat</c> + открытие ZIP + чтение центрального каталога
/// + чтение <c>meta.xml</c>. Распаковки нет нигде, включая иконку (она читается отдельной записью
/// по требованию, см. <see cref="ModIcons"/>). Замер на реальных 128 файлах (1.9 ГБ): 194 мс на всё.
/// </para>
/// <para>
/// Объект ОПИСЫВАЕТ диск и ничего на нём не меняет. Все поля — снимок на момент
/// <see cref="ScannedAtUtc"/>; перед любой операцией записи состояние перечитывается заново.
/// </para>
/// </summary>
public sealed record InstalledMod
{
    /// <summary>Абсолютный путь к файлу (<c>*.mtmod</c> либо <c>*.mtmod.off</c>).</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>Имя файла с расширением, как оно есть на диске.</summary>
    public string FileName => Path.GetFileName(FullPath);

    /// <summary>
    /// Путь относительно каталога модов игры, с разделителями Windows, например
    /// <c>audioww\audio_wm_batman_rus.mtmod</c>. Для файла в корне равен имени файла.
    /// </summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>
    /// Подпапка внутри каталога модов (<c>audioww</c>, <c>foreign_pack</c>, <c>com.modxvm.xfw</c>, …)
    /// либо пустая строка для корня. Это ГОТОВАЯ группировка: её сделал модпак, и список
    /// строится по ней, а не по выдуманным категориям.
    /// </summary>
    public string GroupName { get; init; } = string.Empty;

    /// <summary>Включён (<c>.mtmod</c>) или выключен (<c>.mtmod.off</c>).</summary>
    public ModState State { get; init; } = ModState.Enabled;

    /// <summary>Размер файла в байтах.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Время последней записи файла (UTC).</summary>
    public DateTimeOffset LastWriteTimeUtc { get; init; }

    /// <summary>Момент снятия снимка (UTC). Нужен, чтобы UI мог показать «данные от такого-то времени».</summary>
    public DateTimeOffset ScannedAtUtc { get; init; }

    // ---- meta.xml ---------------------------------------------------------------------

    /// <summary>Что известно про <c>meta.xml</c>.</summary>
    public ModMetaStatus MetaStatus { get; init; } = ModMetaStatus.Unknown;

    /// <summary><c>&lt;id&gt;</c> из meta.xml, например <c>ru.example.my_mod</c>. <c>null</c>, если meta.xml нет или он битый.</summary>
    public string? ModId { get; init; }

    /// <summary>
    /// <c>&lt;version&gt;</c> из meta.xml. Если мод кладёт <c>&lt;versionLst&gt;</c> вместо <c>&lt;version&gt;</c>
    /// (так делают некоторые модпаки) — берётся оно.
    /// </summary>
    public string? MetaVersion { get; init; }

    /// <summary><c>&lt;name&gt;</c> из meta.xml.</summary>
    public string? MetaName { get; init; }

    /// <summary><c>&lt;description&gt;</c> из meta.xml.</summary>
    public string? Description { get; init; }

    /// <summary>Текст ошибки разбора meta.xml, если <see cref="MetaStatus"/> = <see cref="ModMetaStatus.Broken"/>.</summary>
    public string? MetaError { get; init; }

    // ---- содержимое архива ------------------------------------------------------------

    /// <summary>Чем закончилось чтение архива.</summary>
    public ModArchiveStatus ArchiveStatus { get; init; } = ModArchiveStatus.NotRead;

    /// <summary>Человекочитаемая причина, если архив не прочитался.</summary>
    public string? ArchiveError { get; init; }

    /// <summary>Число записей в архиве. Разброс реальный: у 55 модов ≤10 записей, у 24 — больше 200.</summary>
    public int EntryCount { get; init; }

    /// <summary>
    /// Найденные точки входа — имена файлов вида <c>mod_*.py[c|o]</c> в
    /// <c>res/scripts/client/gui/mods/</c>. Пустой список = мод чисто ресурсный
    /// (озвучка, шкурка, ангар); таких 98 из 128.
    /// </summary>
    public IReadOnlyList<string> EntryPointModules { get; init; } = Array.Empty<string>();

    /// <summary>true, если у мода есть питоновская точка входа.</summary>
    public bool HasEntryPoint => EntryPointModules.Count > 0;

    /// <summary>
    /// Записи с нативным кодом (<c>.pyd</c>, <c>.dll</c>). Такие моды лезут в процесс игры,
    /// поэтому UI обязан помечать их отдельно: трогать их рискованнее всего.
    /// На реальной установке это ровно два мода — OpenWG и XFW crashfix.
    /// </summary>
    public IReadOnlyList<string> NativeEntries { get; init; } = Array.Empty<string>();

    /// <summary>true, если в архиве есть <c>.pyd</c>/<c>.dll</c>.</summary>
    public bool HasNativeCode => NativeEntries.Count > 0;

    /// <summary>
    /// Число записей, которые ТОЧНО сжаты (у них <c>CompressedLength != Length</c>).
    /// Больше нуля — мод не загрузится вовсе: движок скажет
    /// «Mod package '%s' load error: compression not supported», все 128 реальных архивов Stored.
    /// <para>
    /// Оценка односторонняя: «сжато» определяется точно, «Stored» — предположение
    /// (у случайно несжимаемых данных длины тоже совпадают). Для предупреждения этого хватает,
    /// ложной тревоги такой критерий не даёт.
    /// </para>
    /// </summary>
    public int CompressedEntryCount { get; init; }

    /// <summary>Путь записи-иконки внутри архива (<c>res/gui/…/modsListApi.png</c>), если она найдена.</summary>
    public string? IconEntryPath { get; init; }

    // ---- свой / чужой -----------------------------------------------------------------

    /// <summary>Связь с проектом пользователя, либо <c>null</c> для чужого мода.</summary>
    public MyModLink? Mine { get; init; }

    /// <summary>true — мод собран из проекта пользователя; false — чужой, поставленный модпаком или руками.</summary>
    public bool IsMine => Mine is not null;

    // ---- «дубли» ----------------------------------------------------------------------

    /// <summary>
    /// true, если тот же <c>&lt;id&gt;</c> нашёлся ещё в одном файле каталога (обычно это две версии сразу).
    /// Ровно та ловушка, которую уже ловит <c>DeployProbe.StaleMtmodPaths</c>: правишь одно, грузится другое.
    /// </summary>
    public bool HasDuplicateId { get; init; }

    /// <summary>
    /// true, если рядом лежит файл-близнец в противоположном состоянии: <c>X.mtmod</c> и <c>X.mtmod.off</c>
    /// одновременно. Обычно это значит, что переустановка модпака положила свежий файл рядом
    /// с выключенным — то есть мод, который пользователь выключал, снова включён.
    /// </summary>
    public bool HasStateTwin { get; init; }

    // ---- вычисляемое для UI -----------------------------------------------------------

    /// <summary>
    /// Имя для показа: <c>&lt;name&gt;</c> из meta.xml, иначе выведенное из имени файла.
    /// Никогда не пустое — у 87 модов из 128 meta.xml просто нет.
    /// </summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(MetaName) ? MetaName!.Trim() : ModFileNames.DisplayNameFromFileName(FileName);

    /// <summary>Версия для показа: из meta.xml, иначе из имени файла, иначе «—».</summary>
    public string DisplayVersion
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(MetaVersion))
            {
                return MetaVersion!.Trim();
            }

            return ModFileNames.VersionFromFileName(FileName) ?? "—";
        }
    }

    /// <summary>Ключ для стабильной заглушки-иконки: id мода, а при его отсутствии — имя файла.</summary>
    public string IconKey => !string.IsNullOrWhiteSpace(ModId) ? ModId! : FileName;

    /// <summary>true, если архив прочитать не удалось — строка показывается, но с пометкой.</summary>
    public bool IsUnreadable =>
        ArchiveStatus is ModArchiveStatus.Broken or ModArchiveStatus.Locked
            or ModArchiveStatus.AccessDenied or ModArchiveStatus.Missing;
}

/// <summary>
/// Правила именования файлов модов — в одном месте, потому что от них зависит и сканер, и переключатель.
/// <para>
/// Включённый мод: <c>&lt;что угодно&gt;.mtmod</c>. Выключенный: то же самое плюс <c>.off</c>.
/// Суффикс <c>.off</c> выбран вместо <c>.disabled</c> по двум причинам: он короче (в подпапках модпаков
/// имена и так под сотню символов, нужен запас по MAX_PATH), и подстрока <c>.mtmod</c>
/// остаётся в имени — человек, открывший папку в проводнике, сразу понимает, что это за файл.
/// </para>
/// </summary>
public static class ModFileNames
{
    /// <summary>Расширение включённого мода.</summary>
    public const string ModExtension = ".mtmod";

    /// <summary>Суффикс, который дописывается к имени, чтобы выключить мод.</summary>
    public const string DisabledSuffix = ".off";

    /// <summary>Полное окончание имени выключенного мода.</summary>
    public const string DisabledExtension = ModExtension + DisabledSuffix;

    /// <summary>Маска перечисления включённых модов (та же, что в paths.xml).</summary>
    public const string EnabledSearchPattern = "*" + ModExtension;

    /// <summary>Маска перечисления выключенных модов.</summary>
    public const string DisabledSearchPattern = "*" + DisabledExtension;

    /// <summary>true, если имя оканчивается на <c>.mtmod</c> (регистр не важен — маска Win32 тоже его не различает).</summary>
    /// <param name="path">Путь или имя файла.</param>
    /// <returns>Признак включённого мода.</returns>
    public static bool IsEnabledFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path!.EndsWith(ModExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>true, если имя оканчивается на <c>.mtmod.off</c>.</summary>
    /// <param name="path">Путь или имя файла.</param>
    /// <returns>Признак выключенного мода.</returns>
    public static bool IsDisabledFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path!.EndsWith(DisabledExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>true, если файл вообще относится к тем, которыми кокпит имеет право управлять.</summary>
    /// <param name="path">Путь или имя файла.</param>
    /// <returns>Признак управляемого файла.</returns>
    public static bool IsManagedFile(string? path) => IsEnabledFile(path) || IsDisabledFile(path);

    /// <summary>Состояние по имени файла.</summary>
    /// <param name="path">Путь или имя файла.</param>
    /// <returns>Включён или выключен.</returns>
    public static ModState StateOf(string path) => IsDisabledFile(path) ? ModState.Disabled : ModState.Enabled;

    /// <summary>
    /// Путь этого же мода в выключенном состоянии (дописывает <c>.off</c>).
    /// Для уже выключенного возвращает его же.
    /// </summary>
    /// <param name="path">Путь к файлу мода.</param>
    /// <returns>Путь с суффиксом <c>.off</c>, либо <c>null</c>, если это вообще не файл мода.</returns>
    public static string? ToDisabledPath(string path)
    {
        if (IsDisabledFile(path))
        {
            return path;
        }

        return IsEnabledFile(path) ? path + DisabledSuffix : null;
    }

    /// <summary>
    /// Путь этого же мода во включённом состоянии (снимает <c>.off</c>).
    /// Для уже включённого возвращает его же.
    /// </summary>
    /// <param name="path">Путь к файлу мода.</param>
    /// <returns>Путь без суффикса <c>.off</c>, либо <c>null</c>, если это вообще не файл мода.</returns>
    public static string? ToEnabledPath(string path)
    {
        if (IsDisabledFile(path))
        {
            return path[..^DisabledSuffix.Length];
        }

        return IsEnabledFile(path) ? path : null;
    }

    /// <summary>
    /// Имя файла без суффикса <c>.off</c> — «одинаковое» имя для включённого и выключенного вида,
    /// по нему ищется близнец (<see cref="InstalledMod.HasStateTwin"/>).
    /// </summary>
    /// <param name="path">Путь или имя файла.</param>
    /// <returns>Имя файла, оканчивающееся на <c>.mtmod</c>.</returns>
    public static string BaseFileName(string path)
    {
        string name = Path.GetFileName(path);
        return IsDisabledFile(name) ? name[..^DisabledSuffix.Length] : name;
    }

    /// <summary>
    /// Версия, выведенная из имени файла по правилу build.py <c>&lt;id&gt;_&lt;version&gt;.mtmod</c>.
    /// Версией считается только хвост после последнего <c>_</c>, состоящий из цифр и точек
    /// (<c>1.0.0</c>, <c>13.1.0.0059</c>, <c>2.2.4.256.260809.224857</c>).
    /// </summary>
    /// <param name="fileName">Имя файла (можно с путём).</param>
    /// <returns>Версия либо <c>null</c>.</returns>
    public static string? VersionFromFileName(string fileName)
    {
        string stem = StripExtensions(Path.GetFileName(fileName));
        int underscore = stem.LastIndexOf('_');
        if (underscore <= 0 || underscore == stem.Length - 1)
        {
            return null;
        }

        string tail = stem[(underscore + 1)..];
        bool hasDigit = false;
        foreach (char c in tail)
        {
            if (char.IsAsciiDigit(c))
            {
                hasDigit = true;
            }
            else if (c != '.')
            {
                return null;
            }
        }

        return hasDigit ? tail : null;
    }

    /// <summary>
    /// Имя для показа, выведенное из имени файла: снимаются расширения и хвост версии,
    /// разделители заменяются пробелами. <c>me.poliroid.battlehits_2.2.5.mtmod</c> → <c>me.poliroid.battlehits</c>.
    /// </summary>
    /// <param name="fileName">Имя файла (можно с путём).</param>
    /// <returns>Непустая строка для показа.</returns>
    public static string DisplayNameFromFileName(string fileName)
    {
        string stem = StripExtensions(Path.GetFileName(fileName));
        string? version = VersionFromFileName(fileName);
        if (version is not null && stem.Length > version.Length + 1)
        {
            stem = stem[..(stem.Length - version.Length - 1)];
        }

        return string.IsNullOrWhiteSpace(stem) ? Path.GetFileName(fileName) : stem;
    }

    /// <summary>Снимает <c>.mtmod</c> и, если он есть, предшествующий ему <c>.off</c>.</summary>
    /// <param name="fileName">Имя файла.</param>
    /// <returns>Имя без расширений мода.</returns>
    private static string StripExtensions(string fileName)
    {
        string name = fileName;
        if (IsDisabledFile(name))
        {
            name = name[..^DisabledSuffix.Length];
        }

        if (IsEnabledFile(name))
        {
            name = name[..^ModExtension.Length];
        }

        return name;
    }
}
