using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Services;

/// <summary>
/// Реализация <see cref="IProjectStore"/> поверх <c>wotmod.json</c> + <c>src\meta.xml</c>.
/// <para>
/// Источники истины разведены так, как требует контракт: манифест мода (<c>id</c>, <c>version</c>,
/// <c>name</c>, <c>description</c>) живёт в <c>src\meta.xml</c> и всегда перекрывает кэш в
/// <c>wotmod.json</c>; всё остальное (python, теги, флаги сборки) — собственность кокпита.
/// </para>
/// </summary>
public sealed class JsonProjectStore : IProjectStore
{
    /// <summary>Относительный путь до каталога, где лежат точки входа: <c>res\scripts\client\gui\mods</c>.</summary>
    private static readonly string[] ModsSegments = ["res", "scripts", "client", "gui", "mods"];

    /// <summary>Каталоги, которые никогда не являются пакетом мода.</summary>
    private static readonly HashSet<string> IgnoredPackageDirs =
        new(StringComparer.OrdinalIgnoreCase) { "__pycache__", ".git", ".svn", ".idea", ".vs" };

    /// <summary>
    /// Литерал тега в исходнике точки входа: <c>'[HelloTanker/loader] '</c> → группа <c>HelloTanker</c>.
    /// Ищем открывающую кавычку, сразу за ней <c>[</c> и имя из букв/цифр.
    /// </summary>
    private static readonly Regex LogTagRegex = new(
        @"['""]\[(?<tag>[A-Za-z][A-Za-z0-9_.\-]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>UTF-8 без BOM: build.py и git не любят BOM в json.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <inheritdoc />
    public ModProject? Load(string dir)
    {
        string? root = NormalizeDir(dir);
        if (root is null)
        {
            return null;
        }

        if (!LooksLikeProject(root))
        {
            return null;
        }

        ModProject? project = null;

        string jsonPath = Path.Combine(root, ProjectStoreDefaults.FileName);
        if (File.Exists(jsonPath))
        {
            try
            {
                string json = File.ReadAllText(jsonPath);
                project = JsonSerializer.Deserialize<ModProject>(json, ProjectStoreDefaults.JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Битый wotmod.json = как будто его нет. Замечание выдаст Validate (NO_WOTMOD_JSON).
                project = null;
            }
        }

        // Нет файла либо он не разобрался — выводим проект из содержимого каталога.
        project ??= Sniff(root);
        if (project is null)
        {
            return null;
        }

        // ProjectDir всегда фактический: перенос каталога не должен ломать проект.
        project.ProjectDir = root;

        // Пустоты, которых нет в json, добираем с диска. Уже заполненное не трогаем.
        FillGapsFromDisk(project);

        // meta.xml главнее кэша в json.
        MetaXml meta = ReadMetaXml(project.MetaXmlPath);
        if (meta.Readable)
        {
            if (meta.Id is not null)
            {
                project.ModId = meta.Id;
            }

            if (meta.Version is not null)
            {
                project.Version = meta.Version;
            }

            if (meta.Name is not null)
            {
                project.DisplayName = meta.Name;
            }

            if (meta.Description is not null)
            {
                project.Description = meta.Description;
            }
        }

        return project;
    }

    /// <inheritdoc />
    public void Save(ModProject p)
    {
        ArgumentNullException.ThrowIfNull(p);

        if (string.IsNullOrWhiteSpace(p.ProjectDir))
        {
            throw new ArgumentException(
                "ModProject.ProjectDir не заполнен — некуда писать wotmod.json.", nameof(p));
        }

        Directory.CreateDirectory(p.ProjectDir);

        string target = Path.Combine(p.ProjectDir, ProjectStoreDefaults.FileName);
        string json = JsonSerializer.Serialize(p, ProjectStoreDefaults.JsonOptions);

        WriteAtomic(target, json);
    }

    /// <inheritdoc />
    public ModProject? Sniff(string dir)
    {
        string? root = NormalizeDir(dir);
        if (root is null || !LooksLikeProject(root))
        {
            return null;
        }

        ModProject project = new() { ProjectDir = root };

        MetaXml meta = ReadMetaXml(project.MetaXmlPath);
        project.ModId = meta.Id ?? string.Empty;
        project.Version = meta.Version ?? "1.0.0";
        project.DisplayName = meta.Name ?? string.Empty;
        project.Description = meta.Description ?? string.Empty;

        FillGapsFromDisk(project);

        return project;
    }

    /// <inheritdoc />
    public IReadOnlyList<ProjectValidation> Validate(ModProject p)
    {
        ArgumentNullException.ThrowIfNull(p);

        List<ProjectValidation> issues = [];

        if (string.IsNullOrWhiteSpace(p.ProjectDir))
        {
            issues.Add(new ProjectValidation(
                ValidationSeverity.Error, "NO_BUILD_SCRIPT",
                "Не задан каталог проекта — искать build.py негде."));
            return issues;
        }

        // --- сборочный скрипт ---
        if (!File.Exists(p.BuildScriptPath))
        {
            issues.Add(new ProjectValidation(
                ValidationSeverity.Error, "NO_BUILD_SCRIPT",
                $"Не найден сборочный скрипт {p.BuildScriptRelative} — собрать мод нечем.",
                p.BuildScriptPath));
        }

        // --- манифест мода ---
        MetaXml meta = default;
        if (!File.Exists(p.MetaXmlPath))
        {
            issues.Add(new ProjectValidation(
                ValidationSeverity.Error, "NO_META_XML",
                "Не найден src\\meta.xml — build.py не сможет узнать id и версию мода.",
                p.MetaXmlPath));
        }
        else
        {
            meta = ReadMetaXml(p.MetaXmlPath);
            if (!meta.Readable)
            {
                issues.Add(new ProjectValidation(
                    ValidationSeverity.Error, "META_UNREADABLE",
                    "src\\meta.xml не разбирается как XML.",
                    p.MetaXmlPath));
            }
            else
            {
                if (meta.Id is not null && !string.Equals(meta.Id, p.ModId, StringComparison.Ordinal))
                {
                    issues.Add(new ProjectValidation(
                        ValidationSeverity.Warning, "ID_MISMATCH",
                        $"id в meta.xml ({meta.Id}) не совпадает с wotmod.json ({p.ModId}).",
                        p.MetaXmlPath));
                }

                if (meta.Version is not null && !string.Equals(meta.Version, p.Version, StringComparison.Ordinal))
                {
                    issues.Add(new ProjectValidation(
                        ValidationSeverity.Warning, "VERSION_MISMATCH",
                        $"version в meta.xml ({meta.Version}) не совпадает с wotmod.json ({p.Version}).",
                        p.MetaXmlPath));
                }

                if (meta.Name is not null && !string.Equals(meta.Name, p.DisplayName, StringComparison.Ordinal))
                {
                    issues.Add(new ProjectValidation(
                        ValidationSeverity.Warning, "NAME_MISMATCH",
                        $"name в meta.xml ({meta.Name}) не совпадает с wotmod.json ({p.DisplayName}).",
                        p.MetaXmlPath));
                }
            }
        }

        // --- точка входа ---
        if (string.IsNullOrWhiteSpace(p.EntryPointModule))
        {
            issues.Add(new ProjectValidation(
                ValidationSeverity.Error, "NO_ENTRY_POINT",
                "Не задан модуль точки входа (ожидается src\\res\\scripts\\client\\gui\\mods\\mod_*.py).",
                Path.Combine(p.SrcDir, "res", "scripts", "client", "gui", "mods")));
        }
        else
        {
            if (!File.Exists(p.EntryPointSourcePath))
            {
                issues.Add(new ProjectValidation(
                    ValidationSeverity.Error, "NO_ENTRY_POINT",
                    $"Файл точки входа {p.EntryPointModule}.py не найден.",
                    p.EntryPointSourcePath));
            }

            if (!p.EntryPointModule.StartsWith("mod_", StringComparison.Ordinal))
            {
                issues.Add(new ProjectValidation(
                    ValidationSeverity.Warning, "ENTRY_POINT_NOT_MOD_PREFIXED",
                    $"Имя точки входа «{p.EntryPointModule}» не начинается с mod_ — загрузчик клиента " +
                    "видит только mod_*.pyc, мод не будет подхвачен.",
                    p.EntryPointSourcePath));
            }
        }

        // --- пакет мода ---
        if (!string.IsNullOrWhiteSpace(p.PackageName))
        {
            string packageDir = Path.Combine(p.SrcDir, "res", "scripts", "client", "gui", "mods", p.PackageName);
            if (!Directory.Exists(packageDir))
            {
                issues.Add(new ProjectValidation(
                    ValidationSeverity.Warning, "NO_PACKAGE_DIR",
                    $"Каталог пакета мода «{p.PackageName}» не найден.",
                    packageDir));
            }
        }

        // --- дескриптор кокпита ---
        string jsonPath = Path.Combine(p.ProjectDir, ProjectStoreDefaults.FileName);
        if (!File.Exists(jsonPath))
        {
            issues.Add(new ProjectValidation(
                ValidationSeverity.Info, "NO_WOTMOD_JSON",
                "wotmod.json ещё не создан — настройки проекта выведены из содержимого каталога.",
                jsonPath));
        }
        else if (!IsReadableJson(jsonPath))
        {
            issues.Add(new ProjectValidation(
                ValidationSeverity.Warning, "NO_WOTMOD_JSON",
                "wotmod.json не разбирается — настройки выведены из содержимого каталога, файл будет перезаписан при сохранении.",
                jsonPath));
        }

        // --- окружение ---
        // Проверяется ровно то, чем будет запущена сборка: вычисленный открывшим проект Python,
        // иначе то же правило без настроек кокпита (PythonLocator.ResolveForBuild).
        string python = !string.IsNullOrWhiteSpace(p.EffectivePythonExe)
            ? p.EffectivePythonExe
            : PythonLocator.ResolveForBuild(p.PythonExe, null) ?? p.PythonExe;

        // Python 2.x — тоже «не найден»: build.py под ним падает голым SyntaxError, а на машине
        // модмейкера 2.7 нередко единственный (или явно указанный) интерпретатор.
        PythonInstall? resolved = PythonLocator.Resolve(python);
        if (resolved is not { IsPython3: true })
        {
            string shown = string.IsNullOrWhiteSpace(python) ? "автопоиск: PATH, py -0p, реестр" : python;
            string what = resolved is null
                ? $"Не удалось найти Python 3.x («{shown}»)"
                : $"«{shown}» — это Python {resolved.Version}, а build.py нужен Python 3.x";
            issues.Add(new ProjectValidation(
                ValidationSeverity.Warning, "PYTHON_NOT_FOUND",
                what + " — сборку запустить не получится. " +
                "Поставьте Python 3.10+ (годится и лаунчер py) или укажите pythonExe в настройках кокпита."));
        }

        if (!string.IsNullOrWhiteSpace(p.GameDir) && !Directory.Exists(p.GameDir))
        {
            issues.Add(new ProjectValidation(
                ValidationSeverity.Warning, "GAME_DIR_NOT_FOUND",
                $"Каталог игры «{p.GameDir}» из wotmod.json не существует. Сборка и установка идут в каталог " +
                "игры из шапки кокпита, а gameDir проекта нужен, только когда игра там не выбрана. " +
                "Если проект пришёл с другой машины, очистите gameDir в wotmod.json.",
                p.GameDir));
        }

        if (string.IsNullOrEmpty(p.LogTag))
        {
            issues.Add(new ProjectValidation(
                ValidationSeverity.Info, "EMPTY_LOG_TAG",
                "Тег мода в логе не задан — фильтр «только моё» в python.log работать не будет."));
        }

        // По убыванию строгости, внутри одной строгости — стабильный порядок обнаружения.
        return issues
            .Select(static (issue, index) => (issue, index))
            .OrderByDescending(static x => (int)x.issue.Severity)
            .ThenBy(static x => x.index)
            .Select(static x => x.issue)
            .ToArray();
    }

    /// <inheritdoc />
    public void SyncMetaXml(ModProject p)
    {
        ArgumentNullException.ThrowIfNull(p);

        if (string.IsNullOrWhiteSpace(p.ProjectDir))
        {
            throw new ArgumentException(
                "ModProject.ProjectDir не заполнен — некуда писать meta.xml.", nameof(p));
        }

        string path = p.MetaXmlPath;
        XDocument doc;

        if (File.Exists(path))
        {
            try
            {
                doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            }
            catch (System.Xml.XmlException)
            {
                doc = NewMetaDocument();
            }
        }
        else
        {
            doc = NewMetaDocument();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        XElement rootEl = doc.Root ?? throw new InvalidOperationException("meta.xml без корневого элемента.");

        SetChild(rootEl, "id", p.ModId);
        SetChild(rootEl, "version", p.Version);
        SetChild(rootEl, "name", p.DisplayName);
        SetChild(rootEl, "description", p.Description);

        // Писать ОБЯЗАТЕЛЬНО через поток с явной UTF-8: XDocument.Save(TextWriter) берёт кодировку
        // у писателя и штампует в декларацию encoding="utf-16", после чего файл, физически лежащий
        // в UTF-8, перестаёт читаться XDocument.Load — meta.xml молча становится «нечитаемым».
        XmlWriterSettings settings = new()
        {
            Encoding = Utf8NoBom,
            Indent = false,                       // раскладку уже сохранил LoadOptions.PreserveWhitespace
            OmitXmlDeclaration = doc.Declaration is null,
            NewLineHandling = NewLineHandling.None
        };

        using MemoryStream ms = new();
        using (XmlWriter writer = XmlWriter.Create(ms, settings))
        {
            doc.Save(writer);
        }

        byte[] bytes = ms.ToArray();
        byte[] newLine = Utf8NoBom.GetBytes(Environment.NewLine);
        if (bytes.Length == 0 || bytes[^1] != (byte)'\n')
        {
            bytes = [.. bytes, .. newLine];
        }

        WriteAtomic(path, bytes);
    }

    // ------------------------------------------------------------------ внутреннее

    /// <summary>Пустой каркас meta.xml на случай, когда файла нет или он безнадёжно битый.</summary>
    /// <returns>Документ <c>&lt;root/&gt;</c> с XML-декларацией.</returns>
    private static XDocument NewMetaDocument() =>
        new(new XDeclaration("1.0", "UTF-8", null), new XElement("root"));

    /// <summary>Создаёт или обновляет дочерний элемент, не трогая остальные.</summary>
    /// <param name="parent">Родитель.</param>
    /// <param name="name">Имя элемента.</param>
    /// <param name="value">Новое значение.</param>
    private static void SetChild(XElement parent, string name, string value)
    {
        XElement? el = parent.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

        if (el is not null)
        {
            el.Value = value;
            return;
        }

        XElement fresh = new(name, value);
        XElement? last = parent.Elements().LastOrDefault();
        if (last is not null)
        {
            // Дописываем сразу за последним элементом, повторяя отступ перед ним,
            // чтобы файл не превратился в одну строку.
            string indent = last.PreviousNode is XText t && t.Value.Contains('\n')
                ? t.Value[t.Value.LastIndexOf('\n')..]
                : Environment.NewLine + "  ";
            last.AddAfterSelf(new XText(indent), fresh);
        }
        else
        {
            parent.Add(fresh);
        }
    }

    /// <summary>Абсолютный путь к существующему каталогу либо <c>null</c>.</summary>
    /// <param name="dir">Путь-кандидат, возможно относительный.</param>
    /// <returns>Нормализованный путь или <c>null</c>.</returns>
    private static string? NormalizeDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir.Trim()));
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Каталог похож на проект мода, если есть хоть что-то из трёх:
    /// <c>wotmod.json</c>, <c>build.py</c>, <c>src\meta.xml</c>.
    /// </summary>
    /// <param name="root">Абсолютный путь к каталогу.</param>
    /// <returns>Похож ли каталог на проект.</returns>
    private static bool LooksLikeProject(string root) =>
        File.Exists(Path.Combine(root, ProjectStoreDefaults.FileName)) ||
        File.Exists(Path.Combine(root, "build.py")) ||
        File.Exists(Path.Combine(root, "src", "meta.xml"));

    /// <summary>
    /// Дозаполняет пустые поля проекта тем, что видно на диске:
    /// точка входа, пакет мода, тег в логе. Уже заданные значения не трогаются.
    /// </summary>
    /// <param name="project">Проект с заполненным <see cref="ModProject.ProjectDir"/>.</param>
    private static void FillGapsFromDisk(ModProject project)
    {
        string modsDir = project.SrcDir;
        foreach (string segment in ModsSegments)
        {
            modsDir = Path.Combine(modsDir, segment);
        }

        if (string.IsNullOrWhiteSpace(project.EntryPointModule))
        {
            project.EntryPointModule = FindEntryPointModule(modsDir) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(project.PackageName))
        {
            project.PackageName = FindPackageName(modsDir) ?? string.Empty;
        }

        if (string.IsNullOrEmpty(project.LogTag) && !string.IsNullOrEmpty(project.EntryPointModule))
        {
            project.LogTag = SniffLogTag(project.EntryPointSourcePath) ?? string.Empty;
        }
    }

    /// <summary>
    /// Ищет <c>mod_*.py</c> в каталоге точек входа. Несколько находок — берётся первая
    /// по алфавиту (порядковое сравнение), это детерминированно и воспроизводимо.
    /// </summary>
    /// <param name="modsDir">Каталог <c>src\res\scripts\client\gui\mods</c>.</param>
    /// <returns>Имя модуля без расширения либо <c>null</c>.</returns>
    private static string? FindEntryPointModule(string modsDir)
    {
        if (!Directory.Exists(modsDir))
        {
            return null;
        }

        try
        {
            return Directory.GetFiles(modsDir, "mod_*.py", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(static n => !string.IsNullOrEmpty(n))
                .OrderBy(static n => n, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Единственный «настоящий» подкаталог в каталоге точек входа — пакет мода.</summary>
    /// <param name="modsDir">Каталог <c>src\res\scripts\client\gui\mods</c>.</param>
    /// <returns>Имя пакета либо <c>null</c>.</returns>
    private static string? FindPackageName(string modsDir)
    {
        if (!Directory.Exists(modsDir))
        {
            return null;
        }

        try
        {
            return Directory.GetDirectories(modsDir)
                .Select(Path.GetFileName)
                .Where(static n => !string.IsNullOrEmpty(n) && !IgnoredPackageDirs.Contains(n!))
                .OrderBy(static n => n, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Эвристика тега: в исходнике точки входа ищется литерал вида <c>'[HelloTanker/loader] '</c>,
    /// из него берётся <c>[HelloTanker</c> (без закрывающей скобки — чтобы ловились и
    /// <c>[HelloTanker]</c>, и <c>[HelloTanker/loader]</c>).
    /// </summary>
    /// <param name="entryPointSourcePath">Путь к <c>mod_*.py</c>.</param>
    /// <returns>Тег либо <c>null</c>.</returns>
    private static string? SniffLogTag(string entryPointSourcePath)
    {
        if (!File.Exists(entryPointSourcePath))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(entryPointSourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Самый частый тег, а не первый попавшийся: в шапке файла бывают комментарии-примеры.
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (Match m in LogTagRegex.Matches(text))
        {
            string tag = "[" + m.Groups["tag"].Value;
            counts[tag] = counts.TryGetValue(tag, out int c) ? c + 1 : 1;
        }

        if (counts.Count == 0)
        {
            return null;
        }

        return counts
            .OrderByDescending(static kv => kv.Value)
            .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
            .First().Key;
    }

    /// <summary>Проверяет, разбирается ли <c>wotmod.json</c>, не меняя ничего.</summary>
    /// <param name="jsonPath">Путь к файлу.</param>
    /// <returns>true, если файл читается и разбирается.</returns>
    private static bool IsReadableJson(string jsonPath)
    {
        try
        {
            string json = File.ReadAllText(jsonPath);
            return JsonSerializer.Deserialize<ModProject>(json, ProjectStoreDefaults.JsonOptions) is not null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Атомарная запись текста в UTF-8 без BOM.</summary>
    /// <param name="target">Целевой файл.</param>
    /// <param name="content">Содержимое.</param>
    private static void WriteAtomic(string target, string content) =>
        WriteAtomic(target, Utf8NoBom.GetBytes(content));

    /// <summary>Атомарная запись: временный файл рядом + <c>File.Move(overwrite: true)</c>.</summary>
    /// <param name="target">Целевой файл.</param>
    /// <param name="content">Содержимое в готовых байтах.</param>
    private static void WriteAtomic(string target, byte[] content)
    {
        string dir = Path.GetDirectoryName(target) ?? ".";
        Directory.CreateDirectory(dir);

        string tmp = Path.Combine(dir, Path.GetFileName(target) + ".tmp-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllBytes(tmp, content);
            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch (IOException)
                {
                    // временный файл — не повод падать
                }
            }
        }
    }

    /// <summary>Разобранный <c>meta.xml</c>. <see cref="Readable"/> = false, если файла нет или он не XML.</summary>
    /// <param name="Readable">Удалось ли прочитать и разобрать файл.</param>
    /// <param name="Id">Значение <c>&lt;id&gt;</c> или <c>null</c>.</param>
    /// <param name="Version">Значение <c>&lt;version&gt;</c> или <c>null</c>.</param>
    /// <param name="Name">Значение <c>&lt;name&gt;</c> или <c>null</c>.</param>
    /// <param name="Description">Значение <c>&lt;description&gt;</c> или <c>null</c>.</param>
    private readonly record struct MetaXml(
        bool Readable, string? Id, string? Version, string? Name, string? Description);

    /// <summary>Читает <c>src\meta.xml</c>, не бросая исключений на бытовые отказы.</summary>
    /// <param name="path">Путь к meta.xml.</param>
    /// <returns>Разобранные поля.</returns>
    private static MetaXml ReadMetaXml(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            XDocument doc = XDocument.Load(path);
            XElement? root = doc.Root;
            if (root is null)
            {
                return default;
            }

            return new MetaXml(
                true,
                Child(root, "id"),
                Child(root, "version"),
                Child(root, "name"),
                Child(root, "description"));
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return default;
        }

        static string? Child(XElement root, string name) =>
            root.Elements()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim();
    }
}
