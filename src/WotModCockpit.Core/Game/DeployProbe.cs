using System.Text.Json;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Services;

/// <summary>
/// Реализация <see cref="IDeployProbe"/>: чтение <c>mods\&lt;ver&gt;</c> и <c>res_mods\&lt;ver&gt;</c>.
/// ТОЛЬКО ЧТЕНИЕ — ни одного вызова, который создаёт, переименовывает или удаляет файлы.
/// <para>
/// Отдельно про чужие моды: в реальном <c>res_mods\&lt;версия&gt;\scripts\client\gui\mods\</c> часто
/// лежат чужие <c>mod_*.pyc</c> и их каталоги ресурсов (<c>mod_foreign_pack.pyc</c> + <c>foreign_pack\</c>).
/// Проба смотрит РОВНО на то, что положил сам кокпит:
/// <list type="number">
///   <item><c>&lt;DistDir&gt;\dev_manifest.json</c> — точный список файлов последней Dev-раскладки
///         (его пишет build.py в <c>do_dev_deploy</c>); берутся только те пути из него, которые
///         существуют и лежат ВНУТРИ <c>res_mods\&lt;ver&gt;</c>;</item>
///   <item>запасной путь, когда манифеста нет: <c>&lt;EntryPointModule&gt;.py[c|o]</c> и каталог
///         <c>&lt;PackageName&gt;\</c> внутри <c>scripts\client\gui\mods\</c>.</item>
/// </list>
/// Одного запасного пути мало: <c>--dev</c> копирует ВЕСЬ <c>stage\res</c>, то есть мод с файлами
/// вне <c>scripts\client\gui\mods\</c> (<c>gui\</c>, <c>audioww\</c>, <c>engine_config.xml</c>)
/// оставил бы в игре невидимую для панели россыпь, которая при этом перекрывает .mtmod.
/// </para>
/// </summary>
public sealed class FileSystemDeployProbe : IDeployProbe
{
    /// <summary>Подкаталог внутри <c>res_mods\&lt;ver&gt;</c>, где живут точки входа модов.</summary>
    private static readonly string[] ModsScriptsSegments = ["scripts", "client", "gui", "mods"];

    /// <summary>Имя файла-манифеста Dev-раскладки, который пишет build.py (константа DEV_MANIFEST).</summary>
    private const string DevManifestFileName = "dev_manifest.json";

    /// <inheritdoc />
    public DeployState Probe(GameInstall g, ModProject p)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(p);

        (string? mtmod, IReadOnlyList<string> stale, DateTimeOffset? mtmodTime, long? mtmodSize) = ProbeMods(g, p);
        (IReadOnlyList<string> resFiles, DateTimeOffset? resNewest) = ProbeResMods(g, p);

        bool inMods = mtmod is not null;
        bool inResMods = resFiles.Count > 0;

        if (!inMods && !inResMods)
        {
            return DeployState.Absent;
        }

        return new DeployState(inMods, mtmod, inResMods, resFiles, inMods && inResMods)
        {
            StaleMtmodPaths = stale,
            MtmodWriteTimeUtc = mtmodTime,
            MtmodSizeBytes = mtmodSize,
            ResModsNewestWriteTimeUtc = resNewest
        };
    }

    /// <summary>
    /// Рекурсивный поиск <c>&lt;ModId&gt;_*.mtmod</c> (и точного <c>&lt;ModId&gt;.mtmod</c>) в
    /// <see cref="GameInstall.ModsDir"/>. Свежайший файл — действующий, остальные — устаревшие.
    /// </summary>
    /// <param name="g">Установка игры.</param>
    /// <param name="p">Проект.</param>
    /// <returns>Путь к действующему пакету, устаревшие пути, время и размер действующего.</returns>
    private static (string? Mtmod, IReadOnlyList<string> Stale, DateTimeOffset? WriteTime, long? Size)
        ProbeMods(GameInstall g, ModProject p)
    {
        if (string.IsNullOrWhiteSpace(p.ModId) || string.IsNullOrWhiteSpace(g.ModsDir) || !SafeDirExists(g.ModsDir))
        {
            return (null, Array.Empty<string>(), null, null);
        }

        string exact = p.ModId + ".mtmod";
        string prefix = p.ModId + "_";

        List<FileInfo> found = [];
        foreach (string file in EnumerateFilesSafe(g.ModsDir, "*.mtmod"))
        {
            string name = Path.GetFileName(file);
            bool mine =
                name.Equals(exact, StringComparison.OrdinalIgnoreCase) ||
                IsVersionedCopy(name, prefix);

            if (!mine)
            {
                continue;
            }

            try
            {
                found.Add(new FileInfo(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // недоступный файл считаем несуществующим
            }
        }

        if (found.Count == 0)
        {
            return (null, Array.Empty<string>(), null, null);
        }

        // Свежайший по времени записи; при равенстве — стабильный порядок по имени.
        found.Sort(static (a, b) =>
        {
            int byTime = b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);
            return byTime != 0 ? byTime : string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase);
        });

        FileInfo winner = found[0];
        IReadOnlyList<string> stale = found.Count > 1
            ? found.Skip(1).Select(static f => f.FullName).ToArray()
            : Array.Empty<string>();

        long? size = null;
        try
        {
            size = winner.Length;
        }
        catch (IOException)
        {
            // размер не критичен
        }

        return (winner.FullName, stale, new DateTimeOffset(winner.LastWriteTimeUtc, TimeSpan.Zero), size);
    }

    /// <summary>
    /// Мой ли это пакет: имя <c>&lt;ModId&gt;_&lt;версия&gt;.mtmod</c>, где хвост после подчёркивания —
    /// ТОЛЬКО цифры и точки. Без этой проверки мод с id <c>crosshairs</c> присваивал бы себе чужой
    /// <c>crosshairs_api_1.1.mtmod</c> (такие соседи в <c>&lt;папка игры&gt;\mods\&lt;версия&gt;</c>
    /// встречаются на деле) и показывал бы его как «установлен» либо как «лишняя старая версия».
    /// </summary>
    /// <param name="fileName">Имя файла с расширением.</param>
    /// <param name="prefix">Ожидаемый префикс <c>&lt;ModId&gt;_</c>.</param>
    /// <returns>true, если имя — это наш пакет с номером версии.</returns>
    private static bool IsVersionedCopy(string fileName, string prefix)
    {
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length <= prefix.Length)
        {
            return false;
        }

        string tail = stem[prefix.Length..];

        bool digit = false;
        foreach (char c in tail)
        {
            if (char.IsAsciiDigit(c))
            {
                digit = true;
            }
            else if (c != '.')
            {
                return false;
            }
        }

        return digit;
    }

    /// <summary>
    /// Ищет файлы ИМЕННО ЭТОГО мода в <see cref="GameInstall.ResModsDir"/>: сначала по
    /// <c>dist\dev_manifest.json</c> (точный список последней Dev-раскладки), затем запасным путём
    /// по <c>scripts\client\gui\mods\&lt;EntryPointModule&gt;.py[c|o]</c> и содержимому
    /// <c>scripts\client\gui\mods\&lt;PackageName&gt;\</c>.
    /// </summary>
    /// <param name="g">Установка игры.</param>
    /// <param name="p">Проект.</param>
    /// <returns>Пути найденных файлов и время самого свежего из них.</returns>
    private static (IReadOnlyList<string> Files, DateTimeOffset? Newest) ProbeResMods(GameInstall g, ModProject p)
    {
        if (string.IsNullOrWhiteSpace(g.ResModsDir) || !SafeDirExists(g.ResModsDir))
        {
            return (Array.Empty<string>(), null);
        }

        List<string> files = [];
        files.AddRange(ReadDevManifestFiles(g, p));

        string modsRoot = g.ResModsDir;
        foreach (string segment in ModsScriptsSegments)
        {
            modsRoot = Path.Combine(modsRoot, segment);
        }

        if (SafeDirExists(modsRoot))
        {
            if (!string.IsNullOrWhiteSpace(p.EntryPointModule))
            {
                foreach (string ext in new[] { ".pyc", ".py", ".pyo" })
                {
                    string candidate = Path.Combine(modsRoot, p.EntryPointModule + ext);
                    if (SafeFileExists(candidate))
                    {
                        files.Add(candidate);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(p.PackageName))
            {
                string packageDir = Path.Combine(modsRoot, p.PackageName);
                if (SafeDirExists(packageDir))
                {
                    files.AddRange(EnumerateFilesSafe(packageDir, "*"));
                }
            }
        }

        if (files.Count == 0)
        {
            return (Array.Empty<string>(), null);
        }

        // Манифест и запасной путь заведомо пересекаются — один и тот же файл не должен считаться дважды.
        files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        files.Sort(StringComparer.OrdinalIgnoreCase);

        DateTimeOffset? newest = null;
        foreach (string f in files)
        {
            try
            {
                DateTime t = File.GetLastWriteTimeUtc(f);
                if (newest is null || t > newest.Value.UtcDateTime)
                {
                    newest = new DateTimeOffset(t, TimeSpan.Zero);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // время не критично
            }
        }

        return (files, newest);
    }

    /// <summary>
    /// Читает <c>&lt;DistDir&gt;\dev_manifest.json</c> — список файлов, которые
    /// <c>build.py --dev</c> положил в игру, — и возвращает те из них, которые СЕЙЧАС существуют
    /// и лежат внутри <see cref="GameInstall.ResModsDir"/>.
    /// <para>
    /// Манифест пишет сам build.py (<c>do_dev_deploy</c>: <c>{"files": [абсолютные пути]}</c>),
    /// поэтому чужие моды сюда попасть не могут по построению. Ограничение по каталогу — защита
    /// от устаревшего манифеста, снятого с другой установки игры.
    /// </para>
    /// </summary>
    /// <param name="g">Установка игры.</param>
    /// <param name="p">Проект (нужен его <see cref="ModProject.DistDir"/>).</param>
    /// <returns>Существующие файлы россыпи. Пустой список, если манифеста нет или он битый.</returns>
    private static IReadOnlyList<string> ReadDevManifestFiles(GameInstall g, ModProject p)
    {
        if (string.IsNullOrWhiteSpace(p.ProjectDir))
        {
            return Array.Empty<string>();
        }

        string manifest;
        string resModsFull;
        try
        {
            manifest = Path.Combine(p.DistDir, DevManifestFileName);
            resModsFull = Path.GetFullPath(g.ResModsDir).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Array.Empty<string>();
        }

        if (!SafeFileExists(manifest))
        {
            return Array.Empty<string>();
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
            root = doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Битый или занятый манифест — просто нет дополнительной информации.
            return Array.Empty<string>();
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("files", out JsonElement list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        string prefix = resModsFull + Path.DirectorySeparatorChar;
        List<string> result = [];

        foreach (JsonElement item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? raw = item.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && SafeFileExists(full))
            {
                result.Add(full);
            }
        }

        return result;
    }

    /// <summary>
    /// Рекурсивный обход каталога, глотающий отказы доступа на любом уровне: рядом с нашей
    /// россыпью в res_mods лежат чужие моды, и один закрытый подкаталог не должен ронять пробу.
    /// Проба идёт не по таймеру, а по явным событиям (смена игры/проекта, конец сборки, кнопка
    /// «обновить»), поэтому дешевизна тут вторична, а устойчивость — нет.
    /// </summary>
    /// <param name="root">Корень обхода.</param>
    /// <param name="pattern">Маска имён файлов.</param>
    /// <returns>Абсолютные пути найденных файлов.</returns>
    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
    {
        Stack<string> pending = new();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string dir = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, pattern);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string f in files)
            {
                yield return f;
            }

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string d in subdirs)
            {
                pending.Push(d);
            }
        }
    }

    /// <summary>Существование каталога без исключений на кривом пути.</summary>
    /// <param name="path">Путь.</param>
    /// <returns>true, если каталог есть.</returns>
    private static bool SafeDirExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Существование файла без исключений на кривом пути.</summary>
    /// <param name="path">Путь.</param>
    /// <returns>true, если файл есть.</returns>
    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
