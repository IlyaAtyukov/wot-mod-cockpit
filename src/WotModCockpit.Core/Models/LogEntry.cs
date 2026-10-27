namespace WotModCockpit.Core.Models;

/// <summary>
/// Одна логическая запись из <c>&lt;game&gt;\python.log</c>: строка с таймстампом плюс
/// приклеенные к ней строки-продолжения (тело traceback).
/// <para>
/// Канонический формат строки, подтверждённый на реальном файле:
/// <code>
/// 2026-08-29 00:27:40.691: NOTICE: [NOTE] (scripts/client/predefined_hosts.py, 782) текст
/// 2026-08-28 01:02:11.100: ERROR: [ERROR] (scripts/common/items/vehicles.py, 4851) (compact description to XML mismatch?) (None,)
/// 2026-08-28 01:02:02.690: ERROR: [DEBUG] Response::onDone: Couldn't resolve host name...
/// 2026-08-28 01:01:55.680: INFO: [MOD] mod loaded: mod_infopanel
/// </code>
/// То есть: <c>&lt;дата&gt; &lt;время с миллисекундами&gt;: &lt;УРОВЕНЬ&gt;: [&lt;категория&gt;] (&lt;файл&gt;, &lt;строка&gt;) &lt;сообщение&gt;</c>,
/// где блок <c>(файл, строка)</c> ОПЦИОНАЛЕН.
/// </para>
/// </summary>
/// <param name="Timestamp">
/// Метка времени записи. <c>null</c> в двух случаях:
/// (1) строка-продолжение, поднятая до записи в одиночку;
/// (2) ПЕРВАЯ строка файла после усечения движком — она физически обрезана посередине
/// (реальный пример из python.log: <c>1:54.515: INFO: [MOD] mod loaded: mod_infopanel</c> — даты нет,
/// от времени остался хвост). Парсер обязан такое проглатывать, а не падать.
/// Время в файле локальное и БЕЗ смещения; реализация ставит смещение текущей машины.
/// </param>
/// <param name="Level">
/// Уровень в верхнем регистре. Реально встречающиеся в файле значения: <c>INFO</c>, <c>ERROR</c>,
/// <c>DEBUG</c>, <c>WARNING</c>, <c>NOTICE</c>. Список НЕ закрытый — незнакомый уровень надо пропускать как есть,
/// а не отбрасывать строку. Для нераспознанных строк — <see cref="LogLevels.Unknown"/>.
/// </param>
/// <param name="Category">
/// Содержимое первых квадратных скобок без самих скобок: <c>NOTE</c>, <c>ERROR</c>, <c>DEBUG</c>, <c>EXCEPTION</c>,
/// <c>MOD</c>, <c>XVM/PyMacro</c>, <c>HelloTanker/loader</c>, <c>gui.shared.gui_items.vehicle_equipment</c>…
/// Пустая строка, если скобок не было.
/// </param>
/// <param name="SourceFile">
/// Виртуальный путь исходника из блока <c>(файл, строка)</c>, ПРЯМЫМИ слэшами и без нормализации:
/// <c>scripts/client/predefined_hosts.py</c>. <c>null</c>, если блока не было.
/// Это путь внутри ресурсов игры, на диске такого файла нет — маппинг делает <see cref="Abstractions.ITracebackMapper"/>.
/// </param>
/// <param name="SourceLine">Номер строки из блока <c>(файл, строка)</c>. <c>null</c>, если блока не было.</param>
/// <param name="Message">Остаток строки после разобранных полей, с обрезанными краевыми пробелами. Может быть пустым.</param>
/// <param name="Continuation">
/// Строки без собственного таймстампа, приклеенные к этой записи в порядке появления в файле:
/// <c>Traceback (most recent call last):</c>, <c>  File "scripts/common/items/vehicles.py", line 4845, in getItemByCompactDescr</c>,
/// <c>TypeError: unsupported operand type(s) for &amp;: 'NoneType' and 'int'</c>.
/// Строки хранятся ДОСЛОВНО, вместе с ведущими пробелами — по ним <see cref="Abstractions.ITracebackMapper"/>
/// достаёт кадры. Пустой список, если продолжения не было. НИКОГДА не <c>null</c>.
/// </param>
/// <param name="IsMine">
/// true, если запись относится к моду разработчика. Критерий: <see cref="Category"/> или <see cref="Message"/>
/// содержит <see cref="ModProject.LogTag"/>, ЛИБО <see cref="SourceFile"/> начинается с
/// <see cref="ModProject.PackageVirtualPrefix"/> / равен <see cref="ModProject.EntryPointVirtualPath"/>.
/// Если тег проекта пуст — всегда false.
/// </param>
public sealed record LogEntry(
    DateTimeOffset? Timestamp,
    string Level,
    string Category,
    string? SourceFile,
    int? SourceLine,
    string Message,
    IReadOnlyList<string> Continuation,
    bool IsMine)
{
    /// <summary>
    /// Исходная строка файла ДОСЛОВНО (без перевода строки) — нужна, чтобы UI мог показать «как в файле»
    /// и чтобы поиск работал по сырому тексту. Пустая строка, если запись собрана искусственно.
    /// </summary>
    public string RawLine { get; init; } = string.Empty;

    /// <summary>
    /// Смещение начала строки в файле в БАЙТАХ на момент чтения. Нужно для перехода «двойной клик → позиция в файле».
    /// После усечения файла движком старые смещения становятся недействительными — потребитель обязан
    /// сбрасывать их по событию <see cref="Abstractions.ILogTailer.FileTruncated"/>. <c>-1</c> = неизвестно.
    /// </summary>
    public long ByteOffset { get; init; } = -1;

    /// <summary>true, если в продолжении есть строка <c>Traceback (most recent call last):</c>.</summary>
    public bool HasTraceback =>
        Continuation.Any(l => l.StartsWith("Traceback (most recent call last)", StringComparison.Ordinal));

    /// <summary>Уровень, приведённый к <see cref="LogSeverity"/> для раскраски и фильтров UI.</summary>
    public LogSeverity Severity => LogLevels.ToSeverity(Level);
}

/// <summary>Огрублённый уровень записи — для фильтра и цвета в UI.</summary>
public enum LogSeverity
{
    /// <summary>Уровень не распознан.</summary>
    Unknown = 0,
    /// <summary>DEBUG.</summary>
    Debug = 1,
    /// <summary>INFO и NOTICE.</summary>
    Info = 2,
    /// <summary>WARNING.</summary>
    Warning = 3,
    /// <summary>ERROR (в т.ч. категория EXCEPTION).</summary>
    Error = 4
}

/// <summary>Строковые константы уровней python.log и их огрубление.</summary>
public static class LogLevels
{
    /// <summary>DEBUG.</summary>
    public const string Debug = "DEBUG";
    /// <summary>INFO.</summary>
    public const string Info = "INFO";
    /// <summary>NOTICE.</summary>
    public const string Notice = "NOTICE";
    /// <summary>WARNING.</summary>
    public const string Warning = "WARNING";
    /// <summary>ERROR.</summary>
    public const string Error = "ERROR";

    /// <summary>Значение <see cref="LogEntry.Level"/> для строк, у которых уровень не разобрался.</summary>
    public const string Unknown = "UNKNOWN";

    /// <summary>Все уровни, реально встречающиеся в файле, в порядке возрастания важности.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Debug, Info, Notice, Warning, Error };

    /// <summary>Огрубление строкового уровня до <see cref="LogSeverity"/>. Регистронезависимо.</summary>
    public static LogSeverity ToSeverity(string? level) => level?.ToUpperInvariant() switch
    {
        Debug => LogSeverity.Debug,
        Info or Notice => LogSeverity.Info,
        Warning => LogSeverity.Warning,
        Error => LogSeverity.Error,
        _ => LogSeverity.Unknown
    };
}
