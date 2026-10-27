using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Services;

/// <summary>
/// Разбор строк <c>&lt;game&gt;\python.log</c> клиента 1.44. Реализация <see cref="ILogParser"/>.
/// <para>
/// Класс без состояния и потокобезопасен: все регэкспы статические и скомпилированные,
/// метод <see cref="Parse"/> смотрит ровно на одну строку. Склейку продолжений делает
/// <see cref="LogEntryAssembler"/> (или вызывающий вручную — см. <see cref="ILogParser"/>).
/// </para>
/// <para>
/// Калибровка на реальном <c>&lt;папка игры&gt;\python.log</c> эталонной установки клиента 1.44
/// (635 378 байт, 6027 строк):
/// строк с полным таймстампом — 5503, из них разобрано 5503 (100,0 %, нераспознанных 0);
/// строк-продолжений — 524 (в т.ч. 1 обрезанная усечением первая строка файла,
/// 115 <c>Traceback (most recent call last):</c>, 230 кадров <c>  File "...", line N, in ...</c>).
/// Уровни: INFO 4561, ERROR 412, DEBUG 332, WARNING 179, NOTICE 19. Категорий 54 штуки,
/// у 166 строк категории нет вовсе, у 285 строк присутствует блок <c>(файл, строка)</c>.
/// </para>
/// </summary>
public sealed class PythonLogParser : ILogParser
{
    // ---------------------------------------------------------------- регэкспы

    /// <summary>
    /// Полный таймстамп в начале строки: <c>2026-08-29 00:27:40.691: </c>.
    /// Единственный критерий «строка начинает новую запись» для <see cref="IsContinuation"/>.
    /// Допускается разделитель <c>T</c> и от одной до шести цифр долей секунды — движок пишет три,
    /// но лишняя терпимость ничего не стоит.
    /// </summary>
    private static readonly Regex TimestampPrefixRe = new(
        @"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}\.\d{1,6}:[ \t]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Голова записи с ПОЛНЫМ таймстампом: сам таймстамп + весь остаток строки.</summary>
    private static readonly Regex HeadFullRe = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}\.\d{1,6}):[ \t](?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Голова записи с ОБРЕЗАННЫМ таймстампом — первая строка файла после усечения движком.
    /// Реальный пример из python.log: <c>1:54.515: INFO: [MOD] mod loaded: mod_infopanel</c>.
    /// Чтобы не хватать обычные строки traceback, требуется, чтобы «огрызок» состоял только из
    /// цифр/двоеточий/точек, а сразу за ним шло что-то, похожее на уровень.
    /// </summary>
    private static readonly Regex HeadPartialRe = new(
        @"^(?<ts>[0-9][0-9:.]{1,24}):[ \t](?=[A-Z][A-Z0-9_]{1,15}:[ \t])(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Тело записи: <c>УРОВЕНЬ: [категория] (файл, строка) сообщение</c>.
    /// Категория и блок <c>(файл, строка)</c> — опциональны.
    /// <para>
    /// Блок с файлом распознаётся ТОЛЬКО если он идёт сразу после уровня/категории и выглядит как
    /// «путь, оканчивающийся на .py» + запятая + число. Поэтому реальная строка
    /// <c>[ERROR] (scripts/common/items/vehicles.py, 4851) (compact description to XML mismatch?) (None,)</c>
    /// даёт файл <c>scripts/common/items/vehicles.py</c>, строку 4851, а весь остаток
    /// <c>(compact description to XML mismatch?) (None,)</c> уходит в сообщение.
    /// </para>
    /// </summary>
    private static readonly Regex BodyRe = new(
        @"^(?<lvl>[A-Z][A-Z0-9_]*):[ \t](?:\[(?<cat>[^\]]*)\][ \t]?)?(?:\((?<file>[^()]*?\.pyc?),[ \t]?(?<line>\d+)\)[ \t]?)?(?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Кадр traceback в теле записи:
    /// <c>  File "scripts/common/items/vehicles.py", line 4845, in getItemByCompactDescr</c>.
    /// Хвост <c>, in &lt;функция&gt;</c> необязателен — клиентский интерпретатор его иногда не печатает.
    /// Публичный, потому что тем же выражением пользуется <see cref="WotSrcTracebackMapper"/>.
    /// </summary>
    internal static readonly Regex TracebackFrameRe = new(
        @"^[ \t]*File[ \t]+""(?<path>[^""]+)"",[ \t]*line[ \t]+(?<line>\d+)(?:,[ \t]*in[ \t]+(?<fn>.+?))?[ \t]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Форматы, которыми разбирается таймстамп. Время локальное, зона в файле не указана.</summary>
    private static readonly string[] TimestampFormats =
    {
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss.FFFFFF",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss.FFFFFF"
    };

    // ---------------------------------------------------------------- ILogParser

    /// <inheritdoc />
    public LogEntry? Parse(string line)
    {
        if (string.IsNullOrEmpty(line))
            return null;

        DateTimeOffset? timestamp = null;
        string rest;

        Match full = HeadFullRe.Match(line);
        if (full.Success)
        {
            timestamp = ParseTimestamp(full.Groups["ts"].Value);
            rest = full.Groups["rest"].Value;
        }
        else
        {
            // Обрезанная усечением первая строка файла: даты нет, от времени остался хвост.
            Match partial = HeadPartialRe.Match(line);
            if (!partial.Success)
                return null;          // это продолжение предыдущей записи либо мусор

            rest = partial.Groups["rest"].Value;
        }

        Match body = BodyRe.Match(rest);
        if (!body.Success)
        {
            // Таймстамп есть, а тело не по формату — строку терять нельзя.
            return new LogEntry(
                timestamp, LogLevels.Unknown, string.Empty, null, null,
                rest.Trim(), Array.Empty<string>(), false)
            { RawLine = line };
        }

        string level = body.Groups["lvl"].Value;
        string category = body.Groups["cat"].Success ? body.Groups["cat"].Value : string.Empty;

        string? sourceFile = null;
        int? sourceLine = null;
        if (body.Groups["file"].Success &&
            int.TryParse(body.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ln))
        {
            sourceFile = body.Groups["file"].Value;
            sourceLine = ln;
        }

        return new LogEntry(
            timestamp,
            level,
            category,
            sourceFile,
            sourceLine,
            body.Groups["msg"].Value.Trim(),
            Array.Empty<string>(),
            false)
        { RawLine = line };
    }

    /// <inheritdoc />
    public bool IsContinuation(string line)
    {
        // Пустая строка — тоже продолжение (в теле traceback пустые строки встречаются).
        // Решение «есть ли к чему приклеивать» принимает вызывающий / LogEntryAssembler.
        if (string.IsNullOrEmpty(line))
            return true;

        return !TimestampPrefixRe.IsMatch(line);
    }

    /// <inheritdoc />
    public LogEntry MarkMine(LogEntry entry, ModProject? project)
    {
        ArgumentNullException.ThrowIfNull(entry);

        bool mine = IsMine(entry, project);
        return mine == entry.IsMine ? entry : entry with { IsMine = mine };
    }

    // ---------------------------------------------------------------- вспомогательное

    /// <summary>
    /// Проверка «запись относится к моду разработчика» без создания копии записи.
    /// Критерии перечислены в <see cref="ILogParser.MarkMine"/>.
    /// </summary>
    /// <param name="entry">Запись.</param>
    /// <param name="project">Проект или <c>null</c>.</param>
    /// <returns>true, если запись «моя».</returns>
    public static bool IsMine(LogEntry entry, ModProject? project)
    {
        if (entry is null || project is null)
            return false;

        string tag = project.LogTag ?? string.Empty;
        if (tag.Length > 0)
        {
            // Тег пишется вместе с открывающей скобкой ('[HelloTanker'), чтобы ловить и
            // '[HelloTanker]', и '[HelloTanker/loader]'. В LogEntry.Category скобок уже нет,
            // поэтому сравниваем и с «одетой» обратно в скобки категорией.
            if (entry.Category.Contains(tag, StringComparison.Ordinal) ||
                (entry.Category.Length > 0 && ("[" + entry.Category + "]").Contains(tag, StringComparison.Ordinal)) ||
                entry.Message.Contains(tag, StringComparison.Ordinal) ||
                entry.RawLine.Contains(tag, StringComparison.Ordinal))
                return true;

            foreach (string cont in entry.Continuation)
            {
                if (cont.Contains(tag, StringComparison.Ordinal))
                    return true;
            }
        }

        if (IsMyVirtualPath(entry.SourceFile, project))
            return true;

        foreach (string cont in entry.Continuation)
        {
            Match m = TracebackFrameRe.Match(cont);
            if (m.Success && IsMyVirtualPath(m.Groups["path"].Value, project))
                return true;
        }

        return false;
    }

    /// <summary>
    /// true, если виртуальный путь указывает на файл мода: точку входа либо модуль пакета мода.
    /// Сравнение по прямым слэшам, регистр пути игнорируется (Windows), расширение <c>.pyc</c>
    /// приравнивается к <c>.py</c> — в логе встречаются оба.
    /// </summary>
    /// <param name="virtualPath">Виртуальный путь из лога либо <c>null</c>.</param>
    /// <param name="project">Проект.</param>
    /// <returns>true, если путь принадлежит моду.</returns>
    internal static bool IsMyVirtualPath(string? virtualPath, ModProject? project)
    {
        if (project is null || string.IsNullOrEmpty(virtualPath))
            return false;

        string vp = NormalizeVirtualPath(virtualPath);

        if (!string.IsNullOrEmpty(project.EntryPointModule))
        {
            string entry = NormalizeVirtualPath(project.EntryPointVirtualPath);
            if (vp.Equals(entry, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        string prefix = project.PackageVirtualPrefix;
        if (!string.IsNullOrEmpty(prefix) &&
            vp.StartsWith(NormalizeVirtualPath(prefix), StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Приводит путь из лога к канону: обратные слэши → прямые, снимается ведущий <c>./</c>,
    /// схлопываются двойные слэши, <c>.pyc</c> → <c>.py</c>.
    /// </summary>
    /// <param name="path">Путь как он записан в логе.</param>
    /// <returns>Нормализованный путь с прямыми слэшами.</returns>
    internal static string NormalizeVirtualPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        string p = path.Replace('\\', '/').Trim();

        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];

        while (p.Contains("//", StringComparison.Ordinal))
            p = p.Replace("//", "/", StringComparison.Ordinal);

        if (p.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase))
            p = p[..^1];

        return p;
    }

    /// <summary>Разбирает таймстамп в локальном времени машины.</summary>
    /// <param name="raw">Текст таймстампа без завершающего двоеточия.</param>
    /// <returns>Момент времени либо <c>null</c>, если формат не распознан.</returns>
    private static DateTimeOffset? ParseTimestamp(string raw)
    {
        if (!DateTime.TryParseExact(raw, TimestampFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime dt))
            return null;

        return new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
    }
}

/// <summary>
/// Склейка потока строк <c>python.log</c> в законченные <see cref="LogEntry"/>.
/// <para>
/// Строки без собственного таймстампа (тело traceback) приклеиваются к предыдущей записи, поэтому
/// запись отдаётся наружу только тогда, когда стало ясно, что продолжений больше не будет —
/// то есть на следующей строке с таймстампом либо на <see cref="Flush"/>.
/// </para>
/// <para>
/// Типовое использование в паре с <see cref="ILogTailer"/>:
/// <code>
/// var asm = new LogEntryAssembler(parser) { Project = project };
/// tailer.LineRead      += (_, line) =&gt; { foreach (var e in asm.Push(line)) Append(e); };
/// tailer.FileTruncated += (_, _)    =&gt; { foreach (var e in asm.Flush()) Append(e); asm.Reset(); };
/// </code>
/// Класс НЕ потокобезопасен: тейлер поднимает события из одного фонового потока, этого достаточно.
/// </para>
/// </summary>
public sealed class LogEntryAssembler
{
    private readonly ILogParser _parser;
    private readonly List<string> _continuation = new();
    private LogEntry? _pending;
    private long _pendingOffset = -1;
    private long _byteOffset;

    /// <summary>Создаёт сборщик поверх парсера.</summary>
    /// <param name="parser">Парсер строк. Обычно <see cref="PythonLogParser"/>.</param>
    public LogEntryAssembler(ILogParser parser) => _parser = parser ?? throw new ArgumentNullException(nameof(parser));

    /// <summary>
    /// Проект, по которому проставляется <see cref="LogEntry.IsMine"/>. <c>null</c> — флаг всегда false.
    /// Можно менять на лету (например, при открытии другого проекта).
    /// </summary>
    public ModProject? Project { get; set; }

    /// <summary>
    /// Считать ли байтовые смещения строк. Если true, каждой записи проставляется
    /// <see cref="LogEntry.ByteOffset"/>; смещение считается в UTF-8 байтах от начала файла и
    /// становится недействительным после усечения — на него надо звать <see cref="Reset"/>.
    /// </summary>
    public bool TrackByteOffsets { get; set; }

    /// <summary>Текущее байтовое смещение (действительно только при <see cref="TrackByteOffsets"/>).</summary>
    public long BytePosition => _byteOffset;

    /// <summary>
    /// Что делать со строкой, которую парсер не смог разобрать И некуда приклеить.
    /// <c>false</c> (по умолчанию) — молча выбросить. <c>true</c> — отдать её отдельной записью
    /// уровня <see cref="LogLevels.Unknown"/> с сырым текстом: в инструменте отладки «строка была
    /// в файле, но её не показали» хуже, чем строка без разбора.
    /// </summary>
    public bool EmitUnparsedLines { get; set; }

    /// <summary>
    /// Скармливает очередную строку файла и возвращает записи, ставшие законченными.
    /// Обычно возвращает пустую последовательность (продолжение) или одну запись.
    /// </summary>
    /// <param name="line">Строка без перевода строки.</param>
    /// <returns>Готовые записи в порядке файла.</returns>
    public IEnumerable<LogEntry> Push(string line)
    {
        line ??= string.Empty;

        long lineStart = _byteOffset;
        if (TrackByteOffsets)
            _byteOffset += System.Text.Encoding.UTF8.GetByteCount(line) + 1; // +1 = '\n'

        if (_parser.IsContinuation(line))
        {
            if (_pending is not null)
            {
                _continuation.Add(line);
                yield break;
            }

            // Продолжение без открытой записи бывает ровно в одном месте: сразу после усечения,
            // когда первая строка файла обрезана посередине. Пытаемся разобрать её как запись.
            LogEntry? salvaged = _parser.Parse(line);
            if (salvaged is null)
            {
                if (EmitUnparsedLines)
                    yield return Raw(line, lineStart);

                yield break;
            }

            _pending = WithOffset(salvaged, lineStart);
            _pendingOffset = lineStart;
            yield break;
        }

        if (_pending is not null)
        {
            yield return Complete();
        }

        LogEntry? parsed = _parser.Parse(line);
        if (parsed is null)
        {
            if (EmitUnparsedLines)
                yield return Raw(line, lineStart);

            yield break;
        }

        _pending = WithOffset(parsed, lineStart);
        _pendingOffset = lineStart;
    }

    /// <summary>
    /// Отдаёт незакрытую запись, если она есть. Звать перед сбросом состояния и когда поток строк
    /// заведомо кончился (файл дочитан до конца, слежение остановлено).
    /// </summary>
    /// <returns>Ноль или одна запись.</returns>
    public IEnumerable<LogEntry> Flush()
    {
        if (_pending is not null)
            yield return Complete();
    }

    /// <summary>
    /// Полный сброс состояния. ОБЯЗАТЕЛЕН по событию <see cref="ILogTailer.FileTruncated"/>:
    /// после усечения накопленное тело traceback принадлежит выброшенной части файла,
    /// а все выданные ранее <see cref="LogEntry.ByteOffset"/> недействительны.
    /// </summary>
    public void Reset()
    {
        _pending = null;
        _pendingOffset = -1;
        _continuation.Clear();
        _byteOffset = 0;
    }

    /// <summary>Задаёт стартовое байтовое смещение (например, после прогрева хвостом).</summary>
    /// <param name="position">Смещение в байтах от начала файла.</param>
    public void SetBytePosition(long position) => _byteOffset = position < 0 ? 0 : position;

    /// <summary>Неразобранная строка как самостоятельная запись уровня UNKNOWN.</summary>
    /// <param name="line">Строка файла дословно.</param>
    /// <param name="offset">Смещение её начала в байтах.</param>
    /// <returns>Запись, которую можно показать «как есть».</returns>
    private LogEntry Raw(string line, long offset)
    {
        var entry = new LogEntry(
            null, LogLevels.Unknown, string.Empty, null, null, line, Array.Empty<string>(), false)
        {
            RawLine = line
        };

        return TrackByteOffsets ? entry with { ByteOffset = offset } : entry;
    }

    private LogEntry Complete()
    {
        LogEntry entry = _pending!;
        _pending = null;

        if (_continuation.Count > 0)
        {
            entry = entry with { Continuation = _continuation.ToArray() };
            _continuation.Clear();
        }

        entry = _parser.MarkMine(entry, Project);
        return TrackByteOffsets ? entry with { ByteOffset = _pendingOffset } : entry;
    }

    [SuppressMessage("Style", "IDE0060", Justification = "offset нужен только при TrackByteOffsets")]
    private LogEntry WithOffset(LogEntry entry, long offset) =>
        TrackByteOffsets ? entry with { ByteOffset = offset } : entry;
}
