using System.Buffers.Binary;
using System.IO.Compression;

namespace WotModCockpit.Core.Mods;

/// <summary>Откуда взялась иконка строки списка.</summary>
public enum ModIconSource
{
    /// <summary>Иконки у мода нет — UI рисует заглушку (<see cref="ModIconPlaceholder"/>).</summary>
    None,

    /// <summary>PNG прочитан прямо из архива мода.</summary>
    Archive,

    /// <summary>Архив не читается — UI рисует «серую плиту с вопросом», строка всё равно показывается.</summary>
    Unreadable
}

/// <summary>
/// Иконка мода, прочитанная из <c>.mtmod</c>. Core отдаёт БАЙТЫ, а не картинку:
/// проект <c>WotModCockpit.Core</c> собирается плоским <c>net8.0</c> без WPF, и ни одной ссылки
/// на <c>System.Windows.*</c> в нём быть не должно. Растеризацией занимается App.
/// </summary>
/// <param name="Source">Откуда иконка.</param>
/// <param name="EntryPath">Путь записи внутри архива, например <c>res/gui/maps/battlehits/modsListApi.png</c>.</param>
/// <param name="Bytes">Содержимое PNG, либо <c>null</c>.</param>
/// <param name="Width">Ширина из заголовка PNG (0, если неизвестна).</param>
/// <param name="Height">Высота из заголовка PNG (0, если неизвестна).</param>
/// <param name="Error">Причина, по которой иконку не удалось достать.</param>
public sealed record ModIcon(
    ModIconSource Source,
    string? EntryPath,
    byte[]? Bytes,
    int Width,
    int Height,
    string? Error)
{
    /// <summary>Иконки нет.</summary>
    public static ModIcon None { get; } = new(ModIconSource.None, null, null, 0, 0, null);

    /// <summary>Архив не читается.</summary>
    /// <param name="error">Текст ошибки.</param>
    /// <returns>Результат «иконку достать не удалось».</returns>
    public static ModIcon Failed(string error) => new(ModIconSource.Unreadable, null, null, 0, 0, error);

    /// <summary>true, если байты PNG получены.</summary>
    public bool HasBytes => Bytes is { Length: > 0 };

    /// <summary>
    /// true, если размер ровно такой, какой в игре у всех реальных иконок списка модов (50×50).
    /// Отклонение не ошибка — показать можно любую, — но для СВОИХ модов это повод предупредить.
    /// </summary>
    public bool IsGameSize => Width == ModIcons.GameIconSize && Height == ModIcons.GameIconSize;
}

/// <summary>
/// Заглушка для мода без иконки: буква и цвет. Рисует её App, Core только вычисляет,
/// ЧТО именно рисовать, — чтобы один и тот же мод всегда выглядел одинаково.
/// </summary>
/// <param name="Letter">Одна заглавная буква.</param>
/// <param name="ColorHex">Цвет в формате <c>#RRGGBB</c> из приглушённой палитры.</param>
public sealed record ModIconPlaceholder(string Letter, string ColorHex);

/// <summary>
/// Работа с иконками модов: найти PNG внутри <c>.mtmod</c>, прочитать его точечно (без распаковки
/// архива) и посчитать стабильную заглушку для тех, у кого иконки нет.
/// <para>
/// Контракт игрового списка модов восстановлен декомпиляцией установленного
/// <c>me.poliroid.modslistapi_1.6.01.mtmod</c>:
/// </para>
/// <code>
/// def __fixModIcon(self, path):
///     if not path or not ResMgr.isFile(path):
///         return DEFAULT_MOD_ICON          # '../../gui/maps/modslist/default-mod-icon.png'
///     return '../../%s' % path
/// </code>
/// То есть моду надо передавать путь ОТНОСИТЕЛЬНО <c>res/</c>, без <c>res/</c> и без <c>../../</c> —
/// префикс API дописывает сам. Ловушка отладки: при опечатке в пути НЕТ никакой диагностики,
/// мод молча получает серую иконку по умолчанию. Симптом «иконка чужая» = неверный путь.
/// </summary>
public static class ModIcons
{
    /// <summary>
    /// Размер иконки игрового списка модов. Замерено по всем реальным иконкам установки:
    /// <c>default-mod-icon.png</c>, <c>battlehits</c>, <c>mtrmanager</c>, <c>sessionlog</c>,
    /// <c>modsSettingsApi</c> — все ровно 50×50, PNG 8 бит RGBA.
    /// </summary>
    public const int GameIconSize = 50;

    /// <summary>Потолок на размер записи, которую вообще рассматриваем как иконку. Реальные — единицы килобайт.</summary>
    public const int MaxIconBytes = 256 * 1024;

    /// <summary>Имя файла иконки, принятое в ModsListAPI.</summary>
    private const string PrimaryIconFileName = "modsListApi.png";

    /// <summary>Сколько записей-кандидатов допустимо «пощупать» чтением заголовка, когда явного имени нет.</summary>
    private const int MaxProbedCandidates = 8;

    /// <summary>Начало любого PNG.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Палитра заглушек — восемь приглушённых тонов. Зелёный и голубой из неё ИСКЛЮЧЕНЫ намеренно:
    /// в кокпите эти два цвета означают «мой мод» и «моя машина», и чужой мод не должен
    /// случайно ими притвориться.
    /// </summary>
    private static readonly string[] PlaceholderPalette =
    [
        "#B08968", "#A2778F", "#8D8741", "#7E8AA2",
        "#A56C5B", "#6F8A8A", "#9A7BA8", "#8E9B6C"
    ];

    /// <summary>
    /// Путь, по которому иконку кладут СВОИ моды: <c>res/gui/maps/icons/mods/&lt;id&gt;/modsListApi.png</c>.
    /// Неймспейс по полному id выбран потому, что движок умеет ругаться
    /// «Package '%s' conflicting with '%s'. Conflicted file: '%s'» — два мода с одинаковым res-путём
    /// конфликтуют, а полный id делает коллизию невозможной.
    /// </summary>
    /// <param name="modId">Идентификатор мода, например <c>ru.example.my_mod</c>.</param>
    /// <returns>Путь записи внутри архива.</returns>
    public static string ArchiveEntryPathFor(string modId) =>
        $"res/gui/maps/icons/mods/{modId}/{PrimaryIconFileName}";

    /// <summary>
    /// Путь, который мод передаёт в <c>g_modsListApi.addModification(icon=...)</c> — тот же самый,
    /// но БЕЗ ведущего <c>res/</c>: API получает путь относительно корня ресурсов.
    /// </summary>
    /// <param name="modId">Идентификатор мода.</param>
    /// <returns>Значение параметра <c>icon</c>.</returns>
    public static string ApiIconPathFor(string modId) =>
        $"gui/maps/icons/mods/{modId}/{PrimaryIconFileName}";

    // ------------------------------------------------------------------ чтение из архива

    /// <summary>
    /// Достаёт иконку из файла мода. Открывает архив, читает ОДНУ запись — распаковки нет.
    /// Работает и для выключенного <c>.mtmod.off</c>: расширение на содержимое не влияет.
    /// </summary>
    /// <param name="mtmodPath">Путь к файлу мода.</param>
    /// <param name="modId">Известный id мода — по нему проверяется «правильный» путь в первую очередь.</param>
    /// <returns>Иконка либо <see cref="ModIcon.None"/>/<see cref="ModIcon.Failed"/>. Исключений не бросает.</returns>
    public static ModIcon Read(string mtmodPath, string? modId = null)
    {
        if (string.IsNullOrWhiteSpace(mtmodPath))
        {
            return ModIcon.None;
        }

        try
        {
            using FileStream stream = new(mtmodPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using ZipArchive zip = new(stream, ZipArchiveMode.Read, leaveOpen: false);
            return Read(zip, modId);
        }
        catch (InvalidDataException ex)
        {
            return ModIcon.Failed("архив не читается: " + ex.Message);
        }
        catch (Exception ex) when (ModScanner.IsExpectedIoFailure(ex))
        {
            return ModIcon.Failed(ex.Message);
        }
    }

    /// <summary>Достаёт иконку из уже открытого архива.</summary>
    /// <param name="zip">Открытый архив мода.</param>
    /// <param name="modId">Известный id мода.</param>
    /// <returns>Иконка либо <see cref="ModIcon.None"/>.</returns>
    public static ModIcon Read(ZipArchive zip, string? modId = null)
    {
        ArgumentNullException.ThrowIfNull(zip);

        List<ZipArchiveEntry> candidates = [];
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                entry.Length is > 0 and <= MaxIconBytes)
            {
                candidates.Add(entry);
            }
        }

        string? chosen = PickIconEntry(candidates, modId);
        if (chosen is null)
        {
            return ModIcon.None;
        }

        ZipArchiveEntry? target = candidates.FirstOrDefault(
            e => e.FullName.Replace('\\', '/').Equals(chosen, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            return ModIcon.None;
        }

        try
        {
            byte[] bytes = ReadEntry(target);
            (int w, int h) = ReadPngSize(bytes) ?? (0, 0);
            return new ModIcon(ModIconSource.Archive, chosen, bytes, w, h, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return ModIcon.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Выбирает из PNG-записей архива ту, которая похожа на иконку списка модов.
    /// Порядок предпочтений — от точного контракта к догадке:
    /// <list type="number">
    ///   <item><c>res/gui/maps/icons/mods/&lt;id&gt;/modsListApi.png</c> — путь, которым пользуются свои моды;</item>
    ///   <item>любой <c>modsListApi.png</c> — так называют иконку battlehits, mtrmanager, sessionlog;</item>
    ///   <item><c>modsList.png</c> и <c>default-mod-icon.png</c> — встречающиеся варианты имени;</item>
    ///   <item><c>icon.png</c> внутри <c>res/gui/</c> — так делает ModsSettingsAPI;</item>
    ///   <item>иначе — первый квадратный PNG со стороной ≤64 внутри <c>res/gui/</c>
    ///         (проверяются заголовки не более восьми записей, чтобы обход оставался дешёвым).</item>
    /// </list>
    /// </summary>
    /// <param name="pngEntries">Записи-кандидаты (PNG подходящего размера).</param>
    /// <param name="modId">Известный id мода либо <c>null</c>.</param>
    /// <returns>Путь записи с прямыми слэшами либо <c>null</c>.</returns>
    internal static string? PickIconEntry(IReadOnlyList<ZipArchiveEntry> pngEntries, string? modId)
    {
        if (pngEntries.Count == 0)
        {
            return null;
        }

        List<string> names = pngEntries.Select(e => e.FullName.Replace('\\', '/')).ToList();

        if (!string.IsNullOrWhiteSpace(modId))
        {
            string exact = ArchiveEntryPathFor(modId!);
            string? hit = names.FirstOrDefault(n => n.Equals(exact, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit;
            }
        }

        string? ByFileName(string fileName) =>
            names.FirstOrDefault(n =>
                Path.GetFileName(n).Equals(fileName, StringComparison.OrdinalIgnoreCase));

        string? byName =
            ByFileName(PrimaryIconFileName) ??
            ByFileName("modsList.png") ??
            ByFileName("default-mod-icon.png");

        if (byName is not null)
        {
            return byName;
        }

        string? icon = names.FirstOrDefault(n =>
            Path.GetFileName(n).Equals("icon.png", StringComparison.OrdinalIgnoreCase) &&
            n.StartsWith("res/gui/", StringComparison.OrdinalIgnoreCase));

        if (icon is not null)
        {
            return icon;
        }

        int probed = 0;
        foreach (ZipArchiveEntry entry in pngEntries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith("res/gui/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (++probed > MaxProbedCandidates)
            {
                break;
            }

            try
            {
                (int w, int h)? size = ReadPngSize(ReadEntryHeader(entry));
                if (size is { } s && s.w == s.h && s.w is > 0 and <= 64)
                {
                    return name;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                // не прочиталась одна запись — просто не считаем её иконкой
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ заглушка

    /// <summary>
    /// Считает заглушку для мода без иконки: буква и цвет. Значения устойчивы —
    /// один и тот же мод всегда получает одну и ту же плашку, а соседние различаются.
    /// </summary>
    /// <param name="key">Ключ (id мода, а при его отсутствии — имя файла).</param>
    /// <param name="displayName">Имя для показа, из него берётся буква.</param>
    /// <returns>Буква и цвет.</returns>
    public static ModIconPlaceholder Placeholder(string? key, string? displayName)
    {
        string source = !string.IsNullOrWhiteSpace(displayName) ? displayName! : key ?? string.Empty;
        string letter = FirstLetter(source);
        string color = PlaceholderPalette[(int)(Fnv1A(key ?? source) % (uint)PlaceholderPalette.Length)];
        return new ModIconPlaceholder(letter, color);
    }

    /// <summary>
    /// Первая осмысленная буква имени. Точечные имена вроде <c>me.poliroid.battlehits</c>
    /// дают букву последнего сегмента (<c>B</c>), иначе весь список был бы из «M», «T» и «C».
    /// </summary>
    /// <param name="name">Имя мода.</param>
    /// <returns>Одна заглавная буква либо <c>?</c>.</returns>
    private static string FirstLetter(string name)
    {
        string s = name.Trim();
        if (s.Length == 0)
        {
            return "?";
        }

        // Enumerable.Reverse, а НЕ parts.Reverse(): у массива есть неявное преобразование в Span,
        // и тогда подхватывается MemoryExtensions.Reverse, который возвращает void.
        string[] parts = s.Split(['.', ' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in Enumerable.Reverse(parts))
        {
            foreach (char c in part)
            {
                if (char.IsLetterOrDigit(c))
                {
                    return char.ToUpperInvariant(c).ToString();
                }
            }
        }

        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                return char.ToUpperInvariant(c).ToString();
            }
        }

        return "?";
    }

    /// <summary>FNV-1a — маленький стабильный хеш, одинаковый между запусками (в отличие от string.GetHashCode).</summary>
    /// <param name="s">Строка.</param>
    /// <returns>Хеш.</returns>
    private static uint Fnv1A(string s)
    {
        uint hash = 2166136261;
        foreach (char c in s)
        {
            hash ^= char.ToLowerInvariant(c);
            hash *= 16777619;
        }

        return hash;
    }

    // ------------------------------------------------------------------ PNG

    /// <summary>
    /// Читает ширину и высоту из заголовка PNG (сигнатура + чанк IHDR). Ничего не декодирует.
    /// </summary>
    /// <param name="bytes">Начало файла PNG (достаточно 24 байт).</param>
    /// <returns>Размеры либо <c>null</c>, если это не PNG.</returns>
    public static (int Width, int Height)? ReadPngSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(PngSignature))
        {
            return null;
        }

        if (!bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
        return width > 0 && height > 0 ? (width, height) : null;
    }

    /// <summary>Читает запись архива целиком (её размер уже ограничен <see cref="MaxIconBytes"/>).</summary>
    /// <param name="entry">Запись.</param>
    /// <returns>Байты.</returns>
    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using Stream s = entry.Open();
        using MemoryStream ms = new(capacity: (int)Math.Min(entry.Length, MaxIconBytes));
        s.CopyTo(ms, 32 * 1024);
        return ms.ToArray();
    }

    /// <summary>Читает только первые 24 байта записи — этого хватает на заголовок PNG.</summary>
    /// <param name="entry">Запись.</param>
    /// <returns>Прочитанные байты (может быть меньше 24, если запись короче).</returns>
    private static byte[] ReadEntryHeader(ZipArchiveEntry entry)
    {
        using Stream s = entry.Open();
        byte[] buffer = new byte[24];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }
}
