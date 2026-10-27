using System.Xml.Linq;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Services;

/// <summary>
/// Реализация <see cref="IGameInstallLocator"/> поверх разбора <c>&lt;корень&gt;\paths.xml</c>.
/// <para>
/// Разбирается ровно то, что описано в контракте: внутри <c>&lt;Paths&gt;</c> берутся прямые дочерние
/// элементы <c>&lt;Path&gt;</c> В ПОРЯДКЕ ФАЙЛА. Из них опознаются два:
/// </para>
/// <code>
/// &lt;Path cacheSubdirs="true"&gt;./res_mods/1.44.0.0&lt;/Path&gt;                       ← россыпь, идёт ПЕРВОЙ
/// &lt;Path mask="*.mtmod" mode="recursive" root="res"&gt;./mods/1.44.0.0&lt;/Path&gt;    ← пакеты, идут второй
/// </code>
/// <para>
/// Блок <c>&lt;Packages&gt;</c> и замыкающий <c>&lt;Path&gt;./res&lt;/Path&gt;</c> игнорируются:
/// у последнего последний сегмент — <c>res</c>, он не проходит проверку «сегмент похож на версию».
/// </para>
/// <para>
/// Версия — последний сегмент пути. Если сегменты у res_mods и mods разошлись, истиной считается
/// сегмент строки с <c>mask="*.mtmod"</c>; каталоги при этом берутся каждый из своей строки,
/// потому что игра читает именно их (см. <see cref="Detect"/>).
/// </para>
/// </summary>
public sealed class PathsXmlGameInstallLocator : IGameInstallLocator
{
    /// <summary>Имя файла, из которого читается активная версия.</summary>
    public const string PathsXmlFileName = "paths.xml";

    /// <inheritdoc />
    public GameInstall? Detect(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        string pathsXml = Path.Combine(root, PathsXmlFileName);
        if (!File.Exists(pathsXml))
        {
            return null;
        }

        XDocument doc;
        try
        {
            // Игра держит paths.xml открытым только на чтение, но подстрахуемся общим доступом.
            using FileStream fs = new(pathsXml, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            doc = XDocument.Load(fs, LoadOptions.None);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // <Path> лежат внутри <Paths>; берём их в порядке файла, вложенные <Package> нам не нужны.
        List<XElement> pathElements = doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "Path", StringComparison.OrdinalIgnoreCase))
            .ToList();

        string? resModsRelative = null;
        string? resModsVersion = null;
        string? modsRelative = null;
        string? modsVersion = null;

        foreach (XElement el in pathElements)
        {
            string raw = (el.Value ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                continue;
            }

            string[] segments = SplitSegments(raw);
            if (segments.Length < 2)
            {
                continue; // "./res" и прочие односегментные — не наши
            }

            string last = segments[^1];
            string folder = segments[^2];

            if (!LooksLikeVersion(last))
            {
                continue;
            }

            bool isMtmodLine = string.Equals(
                (string?)el.Attribute("mask"), "*.mtmod", StringComparison.OrdinalIgnoreCase);

            if (modsRelative is null && (isMtmodLine || string.Equals(folder, "mods", StringComparison.OrdinalIgnoreCase)))
            {
                modsRelative = raw;
                modsVersion = last;
                continue;
            }

            if (resModsRelative is null && string.Equals(folder, "res_mods", StringComparison.OrdinalIgnoreCase))
            {
                resModsRelative = raw;
                resModsVersion = last;
            }
        }

        // Версия: истина — строка с *.mtmod. Если её нет, довольствуемся res_mods.
        string? version = modsVersion ?? resModsVersion;
        if (string.IsNullOrEmpty(version))
        {
            return null;
        }

        string modsDir = modsRelative is not null
            ? ResolveAgainstRoot(root, modsRelative)
            : Path.Combine(root, "mods", version);

        string resModsDir = resModsRelative is not null
            ? ResolveAgainstRoot(root, resModsRelative)
            : Path.Combine(root, "res_mods", version);

        return new GameInstall(
            root,
            version,
            modsDir,
            resModsDir,
            Path.Combine(root, "python.log"));
    }

    /// <summary>
    /// true, если последний сегмент пути похож на версию клиента: только цифры и точки,
    /// минимум одна точка (<c>1.44.0.0</c>, <c>1.36.1.0</c>). Отсекает замыкающий <c>./res</c>.
    /// </summary>
    /// <param name="segment">Последний сегмент пути из paths.xml.</param>
    /// <returns>Похож ли сегмент на номер версии.</returns>
    public static bool LooksLikeVersion(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return false;
        }

        bool hasDot = false;
        bool hasDigit = false;
        foreach (char c in segment)
        {
            if (c == '.')
            {
                hasDot = true;
            }
            else if (char.IsAsciiDigit(c))
            {
                hasDigit = true;
            }
            else
            {
                return false;
            }
        }

        return hasDot && hasDigit;
    }

    /// <summary>Разбивает путь из paths.xml на сегменты, отбрасывая <c>.</c> и пустые.</summary>
    /// <param name="raw">Путь как он записан в XML, например <c>./mods/1.44.0.0</c>.</param>
    /// <returns>Сегменты без точек-заглушек.</returns>
    private static string[] SplitSegments(string raw) =>
        raw.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
           .Where(s => s != ".")
           .ToArray();

    /// <summary>
    /// Приводит относительный путь из paths.xml к абсолютному от корня установки.
    /// Абсолютный путь в XML (теоретически возможен) возвращается как есть.
    /// </summary>
    /// <param name="root">Корень установки.</param>
    /// <param name="raw">Путь из XML.</param>
    /// <returns>Абсолютный путь без завершающего разделителя.</returns>
    private static string ResolveAgainstRoot(string root, string raw)
    {
        string relative = string.Join(Path.DirectorySeparatorChar, SplitSegments(raw));
        if (Path.IsPathRooted(raw))
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, relative)));
    }
}
