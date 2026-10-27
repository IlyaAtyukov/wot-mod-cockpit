using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Services;

/// <summary>
/// Переносит пути из настроек на новое место, когда папка с репозиториями переехала.
/// <para>
/// <b>Зачем.</b> Если папку с репозиториями перенесли (например, с системного диска на
/// другой), пути в настройках переносятся по совпадающему хвосту. Без этого кокпит после
/// переезда запускается и выглядит рабочим, но молча теряет половину смысла: в настройках
/// остаются пути в несуществующую папку, свои моды в списке «Моды» становятся ЧУЖИМИ (их
/// проекты ищутся от последнего открытого проекта и его соседей), «Открыть в VS Code»
/// гаснет, а браузер API и разбор трейсбеков теряют исходники клиента.
/// </para>
/// <para>
/// <b>Почему не хватило того, что уже было.</b> <see cref="JsonSettingsStore"/> умеет
/// находить пути от места, где лежит сам кокпит, — но только на самом первом запуске, когда
/// файла настроек ещё нет. Правило там верное: «если файл есть, ПУСТОЕ поле — выбор
/// пользователя». Но непустой путь, которого больше нет на диске, выбором пользователя не
/// является — это просто устаревшее значение. Ровно в эту щель переезд и попал.
/// </para>
/// <para>
/// <b>Как переносится.</b> У пути берётся хвост (<c>repos\wot-mod-template\.scratch\wot-src-1.45</c>)
/// и примеряется к каталогам, в которых лежит сам кокпит, — от ближнего к дальнему,
/// начиная с самого длинного хвоста. Кандидат принимается, только если он похож на то,
/// чем должен быть: у проекта есть <c>build.py</c>, у исходников клиента — папка
/// <c>client</c>, у pyright — сам файл. Так путь не уедет в первую попавшуюся папку с
/// похожим именем.
/// </para>
/// <para>
/// <b>Чего здесь нет и почему.</b> Путь, который существует, не трогается никогда.
/// Путь, для которого новое место не нашлось, остаётся как был — удалять запись
/// пользователя молча нельзя. Каталог игры, редактор, Python и Node не переносятся:
/// они живут не в папке репозиториев, и искать их рядом с кокпитом бессмысленно.
/// </para>
/// </summary>
public static class SettingsRelocator
{
    /// <summary>Сколько уровней вверх от каталога кокпита просматривать.</summary>
    private const int MaxAncestors = 16;

    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    /// Читает настройки, переносит устаревшие пути и, если что-то перенесено, сохраняет.
    /// <para>
    /// Никогда не бросает: кокпит без перенесённых путей полезнее, чем кокпит, который не
    /// стартует.
    /// </para>
    /// <para>
    /// ПЕРЕНОС ДЕЙСТВУЕТ ТОЛЬКО ЧЕРЕЗ ЗАПИСЬ. Остальные службы читают настройки с диска
    /// сами, каждая в свой момент, поэтому перенос, который не удалось сохранить, им не
    /// виден. Отсюда и договор о результате: пустой список — на диске ничего не изменилось,
    /// будь то «переносить нечего» или «записать не удалось».
    /// </para>
    /// </summary>
    /// <param name="store">Хранилище настроек.</param>
    /// <param name="anchor">Каталог, от которого искать новое место, — каталог сборки кокпита.</param>
    /// <param name="exists">Проверка существования файла или каталога; для тестов.</param>
    /// <returns>Что перенесено и сохранено; пусто, если переносить было нечего или запись не удалась.</returns>
    public static IReadOnlyList<Relocation> HealStore(
        ISettingsStore store, string anchor, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            CockpitSettings settings = store.Load();
            IReadOnlyList<Relocation> moved = Heal(settings, anchor, exists);
            if (moved.Count > 0)
            {
                store.Save(settings);
            }

            return moved;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or NotSupportedException
                                      or ArgumentException
                                      or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>
    /// Переносит устаревшие пути в переданных настройках. Правит объект на месте.
    /// </summary>
    /// <param name="settings">Настройки.</param>
    /// <param name="anchor">Каталог, от которого искать новое место.</param>
    /// <param name="exists">Проверка существования файла или каталога; для тестов.</param>
    /// <returns>Список переносов в порядке полей.</returns>
    public static IReadOnlyList<Relocation> Heal(
        CockpitSettings settings, string anchor, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        exists ??= PathExists;

        var moved = new List<Relocation>();

        // Исходники клиента: хвост минимум из двух частей. Одна часть — это имя вроде
        // «wot-src», а его мало, чтобы отличить дамп клиента от чужой папки.
        settings.WotSrcDir = Fix(nameof(settings.WotSrcDir), settings.WotSrcDir, 2,
            c => exists(Path.Combine(c, "client")));

        // Проекты: хвост может быть и из одной части — это имя проекта
        // (<репозитории>\my-mod), оно различимо само. Годится только каталог с build.py.
        settings.LastProjectDir = Fix(nameof(settings.LastProjectDir), settings.LastProjectDir, 1,
            c => exists(Path.Combine(c, "build.py")));

        // Шаблон мода живёт рядом с проектами и переезжает вместе с ними; признак тот же — build.py.
        settings.TemplateDir = Fix(nameof(settings.TemplateDir), settings.TemplateDir, 1,
            c => exists(Path.Combine(c, "build.py")));

        for (int i = 0; i < settings.RecentProjects.Count; i++)
        {
            settings.RecentProjects[i] = Fix($"{nameof(settings.RecentProjects)}[{i}]",
                settings.RecentProjects[i], 1, c => exists(Path.Combine(c, "build.py")));
        }

        // pyright бывает установлен рядом с шаблоном (node_modules), и такой путь переезжает
        // вместе с папкой репозиториев. Последняя часть — имя файла вроде index.js, поэтому
        // хвост минимум из двух частей.
        settings.PyrightPath = Fix(nameof(settings.PyrightPath), settings.PyrightPath, 2, exists);

        return moved;

        string Fix(string field, string value, int minTail, Func<string, bool> accept)
        {
            if (string.IsNullOrWhiteSpace(value) || exists(value))
            {
                return value;
            }

            string? found = Relocate(value, anchor, minTail, candidate => exists(candidate) && accept(candidate));
            if (found is null)
            {
                return value;
            }

            moved.Add(new Relocation(field, value, found));
            return found;
        }
    }

    /// <summary>
    /// Ищет новое место для пути, которого больше нет.
    /// </summary>
    /// <param name="stale">Устаревший абсолютный путь.</param>
    /// <param name="anchor">Каталог, от которого искать: сам он и все его родители.</param>
    /// <param name="minTail">Сколько последних частей пути должны совпасть как минимум.</param>
    /// <param name="accept">Годится ли кандидат.</param>
    /// <returns>Найденный путь либо <see langword="null"/>.</returns>
    public static string? Relocate(string stale, string anchor, int minTail, Func<string, bool> accept)
    {
        ArgumentNullException.ThrowIfNull(accept);

        if (string.IsNullOrWhiteSpace(stale) || string.IsNullOrWhiteSpace(anchor)
            || !Path.IsPathFullyQualified(stale))
        {
            return null;
        }

        // Первая часть — буква диска или имя сервера: она и поменялась, её не переносим.
        string[] parts = stale.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        List<string> ancestors = Ancestors(anchor);
        int longest = parts.Length - 1;

        // Самый длинный хвост — первым: чем больше совпало, тем меньше шанс ошибиться.
        for (int length = longest; length >= Math.Max(1, minTail); length--)
        {
            string tail = Path.Combine(parts[^length..]);
            foreach (string ancestor in ancestors)
            {
                string candidate = Path.Combine(ancestor, tail);
                if (accept(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    /// <summary>Сам каталог и его родители, от ближнего к дальнему.</summary>
    private static List<string> Ancestors(string anchor)
    {
        var result = new List<string>();
        try
        {
            for (DirectoryInfo? dir = new(anchor); dir is not null && result.Count < MaxAncestors; dir = dir.Parent)
            {
                result.Add(dir.FullName);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Негодный якорь — искать не от чего.
        }

        return result;
    }

    private static bool PathExists(string path)
    {
        try
        {
            return Directory.Exists(path) || File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>Один перенесённый путь.</summary>
/// <param name="Field">Поле настроек, например <c>WotSrcDir</c> или <c>RecentProjects[0]</c>.</param>
/// <param name="From">Старый путь, которого больше нет.</param>
/// <param name="To">Новый путь, найденный рядом с кокпитом.</param>
public sealed record Relocation(string Field, string From, string To);
