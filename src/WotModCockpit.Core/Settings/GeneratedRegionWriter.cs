using System.Text;

namespace WotModCockpit.Core.Settings;

/// <summary>Чем закончилась запись файла с генерируемым участком.</summary>
public enum RegionWriteOutcome
{
    /// <summary>Файла не было — создан целиком.</summary>
    Created = 0,

    /// <summary>Файл был, участок между маркерами заменён; всё вне участка сохранено байт в байт.</summary>
    Updated = 1,

    /// <summary>Содержимое совпало с тем, что уже лежит на диске. Файл не тронут, mtime не изменён.</summary>
    Unchanged = 2,

    /// <summary>Файл уже есть, и перезаписывать его не полагается (режим «создать один раз»).</summary>
    Skipped = 3,

    /// <summary>
    /// Файл есть, но маркеров в нём нет. Запись НЕ выполнена: молча затереть чужой файл —
    /// худшее, что может сделать генератор.
    /// </summary>
    Conflict = 4,

    /// <summary>Файл не удалось прочитать или записать (права, блокировка, диск).</summary>
    Failed = 5
}

/// <summary>Итог записи одного файла.</summary>
/// <param name="Path">Абсолютный путь.</param>
/// <param name="Outcome">Что произошло.</param>
/// <param name="Message">Человеческое пояснение; пусто для успешных исходов без нюансов.</param>
public sealed record RegionWriteResult(string Path, RegionWriteOutcome Outcome, string Message = "")
{
    /// <summary>Файл в порядке: либо записан, либо осознанно оставлен как есть.</summary>
    public bool Ok => Outcome is RegionWriteOutcome.Created or RegionWriteOutcome.Updated
        or RegionWriteOutcome.Unchanged or RegionWriteOutcome.Skipped;

    /// <summary>Однострочное представление для лога и тестов.</summary>
    public override string ToString() =>
        Message.Length > 0 ? $"{Outcome} {Path}: {Message}" : $"{Outcome} {Path}";
}

/// <summary>
/// Запись файла, часть которого генерируется, а часть принадлежит пользователю.
///
/// <para><b>Зачем отдельный механизм.</b> В кокпите уже есть вставка рецептов, но она умеет
/// ровно одно — дописать блок в конец файла (<c>RecipesViewModel</c>: <c>File.WriteAllText(path,
/// existing + tail + block)</c>). Для дизайнера этого мало: форму пересохраняют десятки раз,
/// и каждое сохранение обязано заменить ПРЕДЫДУЩИЙ сгенерированный текст, а не дописать ещё
/// одну копию, при этом не тронув ни строки пользовательского кода.</para>
///
/// <para><b>Правила.</b> Участок ограничен двумя строками-маркерами. Всё, что до открывающего
/// маркера (пролог: объявление кодировки) и после закрывающего (хвост: код пользователя),
/// при перегенерации сохраняется дословно. Файл БЕЗ маркеров не перезаписывается никогда —
/// возвращается <see cref="RegionWriteOutcome.Conflict"/>, и решение остаётся за человеком.</para>
///
/// <para><b>Почему маркер — комментарий с ASCII-меткой.</b> Опознание идёт по префиксу
/// <c># &gt;&gt;&gt;&gt;&gt; cockpit-generated:begin</c>: русский хвост маркера можно переписать
/// или перевести, и файл всё равно останется управляемым. И это законный комментарий Python,
/// поэтому файл с маркерами компилируется как обычно.</para>
/// </summary>
public static class GeneratedRegionWriter
{
    /// <summary>ASCII-префикс, по которому опознаётся открывающий маркер.</summary>
    public const string BeginToken = "# >>>>> cockpit-generated:begin";

    /// <summary>ASCII-префикс, по которому опознаётся закрывающий маркер.</summary>
    public const string EndToken = "# <<<<< cockpit-generated:end";

    /// <summary>Открывающий маркер целиком — то, что реально пишется в файл.</summary>
    public const string BeginMarker =
        BeginToken + " — СГЕНЕРИРОВАНО ДИЗАЙНЕРОМ НАСТРОЕК, ПРАВКИ ЗДЕСЬ БУДУТ ЗАТЁРТЫ";

    /// <summary>Закрывающий маркер целиком.</summary>
    public const string EndMarker =
        EndToken + " — ниже ваш код, дизайнер его не трогает";

    /// <summary>Пролог по умолчанию для генерируемого модуля Python 2.7.</summary>
    /// <remarks>
    /// Объявление кодировки обязано стоять в первых двух строках (PEP 263), поэтому оно живёт
    /// в прологе — ДО открывающего маркера. Иначе первая же перегенерация сдвинула бы его вниз.
    /// </remarks>
    public const string PythonPrologue = "# -*- coding: utf-8 -*-";

    /// <summary>Разбирает текст на пролог, генерируемый участок и хвост.</summary>
    /// <param name="text">Содержимое файла.</param>
    /// <returns>
    /// <c>Found</c> — маркеры найдены. <c>Prologue</c> и <c>Tail</c> отдаются вместе со своими
    /// переводами строк, чтобы сборка обратно была точной.
    /// </returns>
    public static (bool Found, string Prologue, string Body, string Tail) Split(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (false, string.Empty, string.Empty, string.Empty);

        int begin = IndexOfMarker(text, BeginToken, 0);
        if (begin < 0) return (false, text, string.Empty, string.Empty);

        int beginLineEnd = LineEnd(text, begin);
        int end = IndexOfMarker(text, EndToken, beginLineEnd);
        if (end < 0) return (false, text, string.Empty, string.Empty);

        int endLineEnd = LineEnd(text, end);

        string prologue = text[..begin];
        string body = text[beginLineEnd..end];
        string tail = endLineEnd >= text.Length ? string.Empty : text[endLineEnd..];

        return (true, prologue, body, tail);
    }

    /// <summary>Собирает полный текст файла из пролога, генерируемого тела и хвоста.</summary>
    /// <param name="prologue">Пролог; пусто — без пролога.</param>
    /// <param name="body">Генерируемое тело без маркеров.</param>
    /// <param name="tail">Хвост пользователя; пусто — без хвоста.</param>
    /// <param name="newLine">Перевод строки; пусто — <c>\n</c>.</param>
    /// <returns>Полный текст.</returns>
    public static string Compose(string? prologue, string body, string? tail = null, string? newLine = null)
    {
        string nl = string.IsNullOrEmpty(newLine) ? "\n" : newLine;
        var sb = new StringBuilder(4096);

        if (!string.IsNullOrEmpty(prologue))
        {
            sb.Append(Normalize(prologue, nl));
            if (!EndsWithNewLine(sb, nl)) sb.Append(nl);
        }

        sb.Append(BeginMarker).Append(nl);

        string normalizedBody = Normalize(body ?? string.Empty, nl);
        sb.Append(normalizedBody);
        if (normalizedBody.Length > 0 && !EndsWithNewLine(sb, nl)) sb.Append(nl);

        sb.Append(EndMarker).Append(nl);

        if (!string.IsNullOrEmpty(tail))
        {
            string normalizedTail = Normalize(tail, nl);
            sb.Append(normalizedTail);
            if (!EndsWithNewLine(sb, nl)) sb.Append(nl);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Подставляет новое тело в существующий текст, сохраняя пролог и хвост дословно.
    /// </summary>
    /// <param name="existing">Текущее содержимое файла.</param>
    /// <param name="body">Новое тело участка.</param>
    /// <returns>Новый текст либо <c>null</c>, если маркеров нет и подставлять некуда.</returns>
    public static string? Replace(string? existing, string body)
    {
        (bool found, string prologue, _, string tail) = Split(existing);
        if (!found) return null;

        return Compose(prologue, body, tail, DetectNewLine(existing));
    }

    /// <summary>
    /// Записывает файл с генерируемым участком.
    /// </summary>
    /// <param name="path">Абсолютный путь к файлу.</param>
    /// <param name="body">Генерируемое тело без маркеров.</param>
    /// <param name="prologue">Пролог, который пишется ТОЛЬКО при создании файла.</param>
    /// <param name="initialTail">Хвост, который пишется ТОЛЬКО при создании файла.</param>
    /// <returns>Итог; исключения наружу не выпускаются.</returns>
    public static RegionWriteResult Write(string path, string body, string? prologue = PythonPrologue, string? initialTail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            if (!File.Exists(path))
            {
                string created = Compose(prologue, body, initialTail);
                WriteAllText(path, created);
                return new RegionWriteResult(path, RegionWriteOutcome.Created);
            }

            string existing = File.ReadAllText(path, Encoding.UTF8);
            string? updated = Replace(existing, body);

            if (updated is null)
            {
                return new RegionWriteResult(path, RegionWriteOutcome.Conflict,
                    "файл уже существует, но маркеров сгенерированного участка в нём нет. " +
                    "Кокпит не перезаписывает файлы, которых не создавал: переименуйте свой файл " +
                    "или удалите его, если он был создан дизайнером раньше и маркеры потерялись.");
            }

            if (string.Equals(existing, updated, StringComparison.Ordinal))
            {
                return new RegionWriteResult(path, RegionWriteOutcome.Unchanged);
            }

            WriteAllText(path, updated);
            return new RegionWriteResult(path, RegionWriteOutcome.Updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new RegionWriteResult(path, RegionWriteOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Записывает файл, который создаётся ОДИН РАЗ и дальше принадлежит пользователю
    /// (<c>settings.py</c>). Существующий файл не трогается никогда.
    /// </summary>
    /// <param name="path">Абсолютный путь.</param>
    /// <param name="content">Полное содержимое для первого создания.</param>
    /// <returns>Итог.</returns>
    public static RegionWriteResult WriteOnce(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            if (File.Exists(path))
            {
                return new RegionWriteResult(path, RegionWriteOutcome.Skipped,
                    "файл уже есть и принадлежит вам — дизайнер его не переписывает");
            }

            WriteAllText(path, content);
            return new RegionWriteResult(path, RegionWriteOutcome.Created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new RegionWriteResult(path, RegionWriteOutcome.Failed, ex.Message);
        }
    }

    /// <summary>В тексте есть оба маркера в правильном порядке.</summary>
    /// <param name="text">Текст.</param>
    /// <returns>Признак.</returns>
    public static bool HasRegion(string? text) => Split(text).Found;

    private static void WriteAllText(string path, string text)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Строго UTF-8 БЕЗ BOM: три лишних байта в начале .py — это отдельная находка
        // preflight (WOT015) и грабли для всего, что читает файл побайтово.
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Индекс начала строки, которая начинается (после пробелов) с маркера.</summary>
    private static int IndexOfMarker(string text, string token, int from)
    {
        int i = from;
        while (i < text.Length)
        {
            int lineStart = i;
            int lineEnd = text.IndexOf('\n', i);
            if (lineEnd < 0) lineEnd = text.Length;

            int probe = lineStart;
            while (probe < lineEnd && (text[probe] == ' ' || text[probe] == '\t')) probe++;

            if (probe + token.Length <= lineEnd &&
                string.CompareOrdinal(text, probe, token, 0, token.Length) == 0)
            {
                return lineStart;
            }

            i = lineEnd + 1;
        }

        return -1;
    }

    /// <summary>Индекс первого символа ПОСЛЕ строки, начинающейся в <paramref name="lineStart"/>.</summary>
    private static int LineEnd(string text, int lineStart)
    {
        int nl = text.IndexOf('\n', lineStart);
        return nl < 0 ? text.Length : nl + 1;
    }

    /// <summary>Преобладающий перевод строки текста: CRLF, если его больше, иначе LF.</summary>
    private static string DetectNewLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "\n";

        int crlf = 0;
        int lf = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++; else lf++;
        }

        return crlf > lf ? "\r\n" : "\n";
    }

    private static string Normalize(string text, string newLine)
    {
        string unified = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return newLine == "\n" ? unified : unified.Replace("\n", newLine);
    }

    private static bool EndsWithNewLine(StringBuilder sb, string newLine)
    {
        if (sb.Length < newLine.Length) return false;
        for (int i = 0; i < newLine.Length; i++)
        {
            if (sb[sb.Length - newLine.Length + i] != newLine[i]) return false;
        }
        return true;
    }
}
