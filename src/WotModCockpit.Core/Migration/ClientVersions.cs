using WotModCockpit.Core.Services;

namespace WotModCockpit.Core.Migration;

/// <summary>
/// Версии клиента в формате <c>1.46.0.0</c>: сравнение и каталоги модов по версиям.
/// <para>
/// Каталог модов у игры свой на каждую версию (<c>mods\1.45.0.0</c>, <c>mods\1.46.0.0</c>), и
/// активен ровно один — тот, что в paths.xml. После обновления клиента старые каталоги остаются
/// на диске вместе с модами, а новый пуст: именно это и видит переход на новую версию.
/// </para>
/// </summary>
public static class ClientVersions
{
    /// <summary>Сравнение версий по частям как чисел: <c>1.46.0.0</c> новее <c>1.45.1.0</c>.</summary>
    /// <param name="a">Первая версия.</param>
    /// <param name="b">Вторая версия.</param>
    /// <returns>Знак сравнения; нечисловые части сравниваются как строки.</returns>
    public static int Compare(string? a, string? b)
    {
        string[] pa = (a ?? string.Empty).Split('.');
        string[] pb = (b ?? string.Empty).Split('.');
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            string x = i < pa.Length ? pa[i] : "0";
            string y = i < pb.Length ? pb[i] : "0";
            int cmp = long.TryParse(x, out long nx) && long.TryParse(y, out long ny)
                ? nx.CompareTo(ny)
                : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return 0;
    }

    /// <summary>
    /// Короткая форма для имён каталогов: хвостовые нули срезаются, но не меньше двух частей.
    /// <c>1.46.0.0</c> → <c>1.46</c>, <c>1.36.1.0</c> → <c>1.36.1</c>.
    /// </summary>
    /// <param name="version">Версия.</param>
    /// <returns>Короткая форма.</returns>
    public static string Short(string version)
    {
        List<string> parts = (version ?? string.Empty).Trim().Split('.').ToList();
        while (parts.Count > 2 && parts[^1] == "0")
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return string.Join('.', parts);
    }

    /// <summary>
    /// Каталоги <c>&lt;игра&gt;\mods\&lt;версия&gt;</c>, по возрастанию версии. Подкаталоги, имя
    /// которых не похоже на версию (<c>configs</c>, <c>temp</c>), пропускаются.
    /// </summary>
    /// <param name="gameRoot">Корень игры.</param>
    /// <returns>Пары «версия, каталог».</returns>
    public static IReadOnlyList<(string Version, string Dir)> ModsDirs(string gameRoot)
    {
        string mods = Path.Combine(gameRoot ?? string.Empty, "mods");
        if (!Directory.Exists(mods))
        {
            return Array.Empty<(string, string)>();
        }

        try
        {
            return Directory.GetDirectories(mods)
                .Select(d => (Version: Path.GetFileName(d), Dir: d))
                .Where(x => PathsXmlGameInstallLocator.LooksLikeVersion(x.Version))
                .OrderBy(x => x.Version, Comparer<string>.Create(Compare))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<(string, string)>();
        }
    }
}
