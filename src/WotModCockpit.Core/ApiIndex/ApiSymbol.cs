namespace WotModCockpit.Core.ApiIndex;

/// <summary>
/// Компактная запись символа во внутренних массивах <see cref="ApiIndex"/>.
/// <para>
/// Это <b>структура, а не record</b>, и это осознанно: символов 156 710, и запись-класс стоила бы
/// лишних десятков мегабайт и одного разыменования на каждое сравнение при поиске. Наружу
/// отдаётся <see cref="ApiSymbol"/> — уже полноценная запись со всеми выведенными полями.
/// </para>
/// <para>
/// Квалифицированное имя (<c>Class.method</c>) здесь НЕ хранится: оно собирается по
/// <see cref="Parent"/> в момент показа, цепочка не длиннее 9 звеньев.
/// </para>
/// </summary>
public struct SymbolEntry
{
    /// <summary>Индекс файла в <see cref="ApiIndex.Files"/>.</summary>
    public int FileId;

    /// <summary>Номер строки объявления, 1-based.</summary>
    public int Line;

    /// <summary>Индекс родительского символа в том же массиве либо -1.</summary>
    public int Parent;

    /// <summary>Короткое имя. Строка из пула — одинаковые имена делят один объект.</summary>
    public string Name;

    /// <summary>
    /// Список параметров вместе со скобками (<c>(self, vehicleID)</c>) или базовые классы
    /// для <see cref="SymbolKind.Class"/>. <c>null</c>, если распознать не удалось.
    /// </summary>
    public string? Signature;

    /// <summary>Вид символа.</summary>
    public SymbolKind Kind;

    /// <summary>Как символ распознан.</summary>
    public SymbolOrigin Origin;

    /// <summary>Флаги декораторов, накопленные со строк <c>@...</c> непосредственно перед объявлением.</summary>
    public DecoratorFlags Decorators;

    /// <summary>Отступ объявления в символах (обрезан до 255).</summary>
    public byte Indent;
}

/// <summary>
/// Файл дерева исходников. <c>FullPath</c> и dotted-модуль не хранятся, а выводятся из корня
/// и <see cref="RelativePath"/> — это экономит около 4 МБ на 8052 файлах.
/// </summary>
public sealed class SourceFileEntry
{
    /// <summary>Путь относительно корня дерева, ПРЯМЫЕ слэши: <c>client/gui/SystemMessages.py</c>.</summary>
    public required string RelativePath { get; init; }

    /// <summary>
    /// Виртуальный путь из 4-й строки шапки (<c>scripts/client/gui/SystemMessages.py</c>).
    /// Есть у всех 8052 файлов; именно им оперирует traceback в <c>python.log</c>, поэтому это
    /// мост к этапу 1. Если шапки нет — равен <see cref="RelativePath"/>.
    /// </summary>
    public required string VirtualPath { get; init; }

    /// <summary>Размер в символах после декодирования.</summary>
    public required int Length { get; init; }

    /// <summary>Подкорень.</summary>
    public required SourceRoot Root { get; init; }

    /// <summary>Слой корпуса.</summary>
    public required SourceTier Tier { get; init; }

    /// <summary>
    /// Импортируемое имя модуля: <c>client/gui/SystemMessages.py</c> → <c>gui.SystemMessages</c>.
    /// Первый сегмент физического пути отбрасывается, <c>/__init__</c> отрезается.
    /// </summary>
    public string DottedModule => ToDottedModule(RelativePath);

    /// <summary>Имя модуля без пакета: <c>SystemMessages</c>; для <c>__init__.py</c> — имя пакета.</summary>
    public string ModuleName
    {
        get
        {
            string dotted = DottedModule;
            int dot = dotted.LastIndexOf('.');
            return dot < 0 ? dotted : dotted[(dot + 1)..];
        }
    }

    /// <summary>Абсолютный путь на диске.</summary>
    /// <param name="treeRoot">Корень дерева исходников.</param>
    /// <returns>Полный путь.</returns>
    public string FullPath(string treeRoot) =>
        Path.Combine(treeRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Превращает относительный путь в импортируемое имя модуля.</summary>
    /// <param name="relativePath">Относительный путь с прямыми слэшами.</param>
    /// <returns>Точечное имя модуля.</returns>
    public static string ToDottedModule(string relativePath)
    {
        string s = relativePath;
        int slash = s.IndexOf('/');
        if (slash > 0) s = s[(slash + 1)..];
        if (s.EndsWith(".py", StringComparison.Ordinal)) s = s[..^3];
        if (s.EndsWith("/__init__", StringComparison.Ordinal)) s = s[..^9];
        else if (s == "__init__") s = string.Empty;
        return s.Replace('/', '.');
    }
}

/// <summary>
/// Символ в том виде, в котором его получает UI: со всеми выведенными полями,
/// готовый к показу и к переходу в редактор.
/// </summary>
/// <param name="Name">Короткое имя (<c>pushMessage</c>).</param>
/// <param name="QualifiedName">Имя с владельцами через точку (<c>ISystemMessages.pushMessage</c>).</param>
/// <param name="Kind">Вид символа.</param>
/// <param name="Module">Импортируемое имя модуля (<c>gui.SystemMessages</c>).</param>
/// <param name="VirtualPath">Путь как в traceback (<c>scripts/client/gui/SystemMessages.py</c>).</param>
/// <param name="RelativePath">Путь относительно дерева (<c>client/gui/SystemMessages.py</c>).</param>
/// <param name="FullPath">Абсолютный путь на диске — по нему открывается редактор.</param>
/// <param name="Line">Строка объявления, 1-based.</param>
/// <param name="Signature">Список параметров со скобками либо <c>null</c>.</param>
public sealed record ApiSymbol(
    string Name,
    string QualifiedName,
    SymbolKind Kind,
    string Module,
    string VirtualPath,
    string RelativePath,
    string FullPath,
    int Line,
    string? Signature)
{
    /// <summary>Индекс символа в <see cref="ApiIndex"/> — ключ для xref и для перехода к детям.</summary>
    public int SymbolId { get; init; } = -1;

    /// <summary>Непосредственный владелец (класс или функция) либо <c>null</c> для верхнего уровня.</summary>
    public string? Owner { get; init; }

    /// <summary>Флаги декораторов.</summary>
    public DecoratorFlags Decorators { get; init; }

    /// <summary>Как символ распознан.</summary>
    public SymbolOrigin Origin { get; init; }

    /// <summary>Подкорень.</summary>
    public SourceRoot Root { get; init; }

    /// <summary>Слой корпуса.</summary>
    public SourceTier Tier { get; init; }

    /// <summary>
    /// Сколько РАЗНЫХ мест объявляют это имя во всём корпусе. Показывать обязательно:
    /// у <c>update</c> это 279, и поиск ссылок по такому имени без уточнения приёмником бесполезен.
    /// </summary>
    public int DeclarationCount { get; init; }

    /// <summary>Балл ранжирования, если символ пришёл из поиска.</summary>
    public int Score { get; init; }

    /// <summary>Файл лежит в <c>skeletons/</c> — то есть это интерфейс сервиса.</summary>
    public bool IsServiceInterface =>
        RelativePath.Contains("/skeletons/", StringComparison.Ordinal);

    /// <summary>Готовая к показу подпись: <c>ISystemMessages.pushMessage(self, message, type)</c>.</summary>
    public string Display => Signature is null ? QualifiedName : QualifiedName + Signature;
}
