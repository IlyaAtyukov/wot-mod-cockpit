using System.Text;
using System.Text.Json;

namespace WotModCockpit.Core.Migration;

/// <summary>Отметка, которую кокпит кладёт в дерево исходников, извлечённое им самим.</summary>
public sealed record ClientSourceMarker
{
    /// <summary>Версия клиента из paths.xml (<c>1.46.0.0</c>).</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Когда извлечено, ISO 8601 UTC.</summary>
    public string ExtractedUtc { get; init; } = string.Empty;

    /// <summary>Сколько .pyc было в scripts.pkg.</summary>
    public int Total { get; init; }

    /// <summary>Декомпилировано полностью.</summary>
    public int Decompiled { get; init; }

    /// <summary>Декомпилировано частично (текст обрывается).</summary>
    public int Partial { get; init; }

    /// <summary>Не декомпилировано вовсе.</summary>
    public int Failed { get; init; }
}

/// <summary>
/// Деревья декомпилированных исходников клиента: где лежат, какой они версии, что о них известно.
/// <para>
/// Дерево — каталог с <c>client\</c>, <c>client_common\</c>, <c>common\</c> в корне, как его
/// ждут индекс и маппер traceback. Деревья разных версий живут рядом и называются
/// <c>wot-src-&lt;версия&gt;</c> (<c>wot-src-1.45</c>, <c>wot-src-1.46</c>): старое не
/// затирается, оно нужно как база сравнения при следующем переходе.
/// </para>
/// </summary>
public static class ClientSourceTrees
{
    /// <summary>Отметка дерева, извлечённого кокпитом.</summary>
    public const string MarkerFile = ".cockpit-client.json";

    /// <summary>
    /// Модули дополнительных пакетов клиента той же версии (<c>battle_royale.pkg</c>,
    /// <c>comp7.pkg</c>, …). В scripts.pkg их нет, а моды их импортируют — без списка
    /// preflight ругался бы на существующие модули.
    /// </summary>
    public const string ExtraModulesFile = ".cockpit-extra-modules.txt";

    /// <summary>Список модулей, которые при извлечении не декомпилировались: по одному на строку.</summary>
    public const string FailedModulesFile = ".cockpit-failed-modules.txt";

    /// <summary>Префикс имени каталога дерева.</summary>
    public const string DirPrefix = "wot-src-";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Каталог похож на дерево исходников клиента.</summary>
    /// <param name="dir">Каталог.</param>
    /// <returns>Признак.</returns>
    public static bool LooksLikeTree(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) &&
        (Directory.Exists(Path.Combine(dir, "client")) || Directory.Exists(Path.Combine(dir, "common")));

    /// <summary>Отметка дерева либо <c>null</c>, если её нет (дерево положено не кокпитом).</summary>
    /// <param name="dir">Дерево.</param>
    /// <returns>Отметка.</returns>
    public static ClientSourceMarker? ReadMarker(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        try
        {
            string path = Path.Combine(dir, MarkerFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<ClientSourceMarker>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Записывает отметку.</summary>
    /// <param name="dir">Дерево.</param>
    /// <param name="marker">Отметка.</param>
    public static void WriteMarker(string dir, ClientSourceMarker marker) =>
        File.WriteAllText(Path.Combine(dir, MarkerFile), JsonSerializer.Serialize(marker, Json), new UTF8Encoding(false));

    /// <summary>
    /// Версия дерева: из отметки, а без неё — из имени каталога (<c>wot-src-1.45</c> → <c>1.45</c>).
    /// Дерево без того и другого (<c>.scratch\wot-src</c>) — версия неизвестна.
    /// </summary>
    /// <param name="dir">Дерево.</param>
    /// <returns>Версия либо <c>null</c>.</returns>
    public static string? VersionOf(string? dir)
    {
        ClientSourceMarker? marker = ReadMarker(dir);
        if (marker is not null && marker.Version.Length > 0)
        {
            return marker.Version;
        }

        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir ?? string.Empty));
        if (name.StartsWith(DirPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string tail = name[DirPrefix.Length..];
            if (Services.PathsXmlGameInstallLocator.LooksLikeVersion(tail))
            {
                return tail;
            }
        }

        return null;
    }

    /// <summary>
    /// Дерево той же версии: версии совпадают после отбрасывания хвостовых нулей
    /// (<c>1.46</c> = <c>1.46.0.0</c>).
    /// </summary>
    /// <param name="dir">Дерево.</param>
    /// <param name="version">Версия клиента.</param>
    /// <returns>Признак.</returns>
    public static bool IsForVersion(string? dir, string version)
    {
        string? v = VersionOf(dir);
        return v is not null && ClientVersions.Compare(ClientVersions.Short(v), ClientVersions.Short(version)) == 0;
    }

    /// <summary>
    /// Где должно лежать дерево версии: рядом с текущим деревом из настроек, а если его нет —
    /// в <c>.scratch</c> шаблона мода, а без шаблона — в локальных данных кокпита.
    /// </summary>
    /// <param name="version">Версия клиента.</param>
    /// <param name="currentTree">Текущее дерево (wotSrcDir) или <c>null</c>.</param>
    /// <param name="templateDir">Каталог шаблона мода или <c>null</c>.</param>
    /// <returns>Путь каталога (может не существовать).</returns>
    public static string DefaultDirFor(string version, string? currentTree, string? templateDir)
    {
        string name = DirPrefix + ClientVersions.Short(version);
        if (!string.IsNullOrWhiteSpace(currentTree) && LooksLikeTree(currentTree))
        {
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentTree)));
            if (!string.IsNullOrEmpty(parent))
            {
                return Path.Combine(parent, name);
            }
        }

        if (!string.IsNullOrWhiteSpace(templateDir) && Directory.Exists(templateDir))
        {
            return Path.Combine(templateDir, ".scratch", name);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WotModCockpit", "client-src", name);
    }

    /// <summary>
    /// Уже готовое дерево нужной версии: каталог по умолчанию и его собрат с полной версией в имени.
    /// </summary>
    /// <param name="version">Версия клиента.</param>
    /// <param name="currentTree">Текущее дерево (wotSrcDir).</param>
    /// <param name="templateDir">Каталог шаблона мода.</param>
    /// <returns>Путь либо <c>null</c>.</returns>
    public static string? FindExisting(string version, string? currentTree, string? templateDir)
    {
        if (!string.IsNullOrWhiteSpace(currentTree) && LooksLikeTree(currentTree) && IsForVersion(currentTree, version))
        {
            return Path.GetFullPath(currentTree);
        }

        string preferred = DefaultDirFor(version, currentTree, templateDir);
        string? parent = Path.GetDirectoryName(preferred);
        foreach (string candidate in new[]
                 {
                     preferred,
                     parent is null ? preferred : Path.Combine(parent, DirPrefix + version),
                 })
        {
            // Имя говорит о версии, но отметка кокпита главнее: дерево другой версии под этим
            // именем не годится ни как новое, ни как база сравнения.
            if (LooksLikeTree(candidate) && IsForVersion(candidate, version))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Модули дополнительных пакетов из дерева либо <c>null</c>, если списка нет.</summary>
    /// <param name="dir">Дерево.</param>
    /// <returns>Список модулей.</returns>
    public static IReadOnlyList<string>? ReadExtraModules(string? dir) => ReadModuleList(dir, ExtraModulesFile);

    /// <summary>
    /// Модули, которые при извлечении не декомпилировались (в игре есть, исходника в дереве нет),
    /// либо <c>null</c>, если списка нет.
    /// </summary>
    /// <param name="dir">Дерево.</param>
    /// <returns>Список модулей.</returns>
    public static IReadOnlyList<string>? ReadFailedModules(string? dir) => ReadModuleList(dir, FailedModulesFile);

    private static IReadOnlyList<string>? ReadModuleList(string? dir, string fileName)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        try
        {
            string path = Path.Combine(dir, fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            return File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
