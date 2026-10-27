using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Abstractions;

/// <summary>
/// Разбор строк <c>python.log</c> в <see cref="LogEntry"/>.
/// <para>Парсер БЕЗ СОСТОЯНИЯ и потокобезопасен: он смотрит ровно на одну строку.
/// Склейку продолжений (тело traceback) делает вызывающий — типовой цикл:</para>
/// <code>
/// LogEntry? current = null;
/// var cont = new List&lt;string&gt;();
/// foreach (var line in lines)
/// {
///     if (parser.IsContinuation(line) &amp;&amp; current is not null) { cont.Add(line); continue; }
///     if (current is not null) Emit(current with { Continuation = cont.ToArray() });
///     cont.Clear();
///     current = parser.Parse(line);
///     if (current is null) { /* мусорная строка: показать как есть, уровнем UNKNOWN */ }
/// }
/// </code>
/// <para>Формат, на который опирается реализация (подтверждён на реальном файле):</para>
/// <code>
/// 2026-08-29 00:27:40.691: NOTICE: [NOTE] (scripts/client/predefined_hosts.py, 782) текст
/// └──── дата ────┘ └── время ──┘  └уровень┘ └катег.┘ └──── (файл, строка) ────┘ └сообщение┘
/// </code>
/// Блок <c>(файл, строка)</c> присутствует не всегда. Категория — всегда первые квадратные скобки.
/// </summary>
public interface ILogParser
{
    /// <summary>
    /// Разбирает одну строку файла.
    /// </summary>
    /// <param name="line">Строка БЕЗ завершающего перевода строки. Может быть пустой.</param>
    /// <returns>
    /// Разобранная запись с ПУСТЫМ <see cref="LogEntry.Continuation"/> (склейку делает вызывающий),
    /// либо <c>null</c>, если строка не является началом записи (тогда см. <see cref="IsContinuation"/>).
    /// <para>Краевые случаи, которые ОБЯЗАНЫ быть обработаны без исключений:</para>
    /// <list type="bullet">
    ///   <item><b>Обрезанная первая строка после усечения лога.</b> Движок режет файл с 2048 КБ до 512 КБ
    ///         прямо посреди строки, и первая строка выглядит как
    ///         <c>1:54.515: INFO: [MOD] mod loaded: mod_infopanel</c> — даты нет, от времени остался хвост.
    ///         Такую строку надо разобрать как запись с <see cref="LogEntry.Timestamp"/> = <c>null</c>,
    ///         но с корректными Level/Category/Message.</item>
    ///   <item><b>Нет блока (файл, строка)</b> → <see cref="LogEntry.SourceFile"/> и
    ///         <see cref="LogEntry.SourceLine"/> = <c>null</c>.</item>
    ///   <item><b>Незнакомый уровень</b> → положить его в <see cref="LogEntry.Level"/> как есть, строку не терять.</item>
    ///   <item><b>Круглые скобки внутри сообщения</b> (реальный пример:
    ///         <c>[ERROR] (scripts/common/items/vehicles.py, 4851) (compact description to XML mismatch?) (None,)</c>)
    ///         — как <c>(файл, строка)</c> считается ТОЛЬКО первый блок и только если он выглядит как
    ///         <c>путь.py</c> + запятая + число. Остальное уходит в <see cref="LogEntry.Message"/>.</item>
    ///   <item><b>Пустая строка / <c>null</c></b> → <c>null</c>, без исключения.</item>
    /// </list>
    /// Время в файле локальное, без указания зоны: реализация приписывает смещение текущей машины.
    /// <see cref="LogEntry.IsMine"/> здесь всегда <c>false</c> — отметку ставит <see cref="MarkMine"/>,
    /// потому что парсер ничего не знает о проекте.
    /// </returns>
    LogEntry? Parse(string line);

    /// <summary>
    /// true, если строка — продолжение предыдущей записи, а не начало новой.
    /// <para>Критерий: строка НЕ начинается с таймстампа <c>ГГГГ-ММ-ДД ЧЧ:ММ:СС.мс: </c>.
    /// Практически это тело traceback:</para>
    /// <code>
    /// Traceback (most recent call last):
    ///   File "scripts/common/items/vehicles.py", line 4845, in getItemByCompactDescr
    /// TypeError: unsupported operand type(s) for &amp;: 'NoneType' and 'int'
    /// </code>
    /// <para>ВАЖНО: пустая строка тоже считается продолжением (в теле traceback бывают пустые строки),
    /// но продолжением она становится только при наличии открытой записи — за это отвечает вызывающий.</para>
    /// </summary>
    /// <param name="line">Строка без перевода строки.</param>
    /// <returns>true, если строку надо приклеить к предыдущей записи.</returns>
    bool IsContinuation(string line);

    /// <summary>
    /// Возвращает копию записи с корректно выставленным <see cref="LogEntry.IsMine"/> для данного проекта.
    /// <para>Запись считается «моей», если выполнено хотя бы одно:</para>
    /// <list type="bullet">
    ///   <item><see cref="LogEntry.Category"/> или <see cref="LogEntry.Message"/> содержит
    ///         <see cref="ModProject.LogTag"/> (сравнение Ordinal, регистр важен);</item>
    ///   <item><see cref="LogEntry.SourceFile"/> равен <see cref="ModProject.EntryPointVirtualPath"/>
    ///         или начинается с <see cref="ModProject.PackageVirtualPrefix"/>;</item>
    ///   <item>любая строка <see cref="LogEntry.Continuation"/> ссылается на файл мода
    ///         (та же проверка по виртуальным путям).</item>
    /// </list>
    /// Если <see cref="ModProject.LogTag"/> пуст и виртуальные пути не совпали — <c>false</c>.
    /// <paramref name="project"/> = <c>null</c> → всегда <c>false</c>.
    /// </summary>
    /// <param name="entry">Запись.</param>
    /// <param name="project">Проект или <c>null</c>.</param>
    /// <returns>Та же запись либо её копия с изменённым флагом.</returns>
    LogEntry MarkMine(LogEntry entry, ModProject? project);
}
