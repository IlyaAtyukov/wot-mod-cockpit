using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Services;

/// <summary>
/// Фильтр ленты лога для UI. Условия КОМБИНИРУЮТСЯ по И: запись показывается, только если прошла
/// все включённые проверки. Пустой фильтр (<c>new LogFilter()</c>) пропускает всё.
/// <para>
/// Класс изменяемый и дешёвый: UI держит один экземпляр, переключает свойства по галочкам и
/// прогоняет буфер записей через <see cref="IsMatch"/>. Метод чистый, состояния между вызовами нет.
/// </para>
/// </summary>
public sealed class LogFilter
{
    private string _text = string.Empty;

    /// <summary>
    /// Показывать только записи мода (<see cref="LogEntry.IsMine"/>).
    /// Флаг <c>IsMine</c> проставляет парсер (<c>ILogParser.MarkMine</c>) по
    /// <see cref="ModProject.LogTag"/> и виртуальным путям; если запись пришла непомеченной,
    /// сработает запасная проверка по <see cref="Tag"/>.
    /// </summary>
    public bool OnlyMine { get; set; }

    /// <summary>
    /// Показывать только проблемные записи: уровень <c>ERROR</c>/<c>CRITICAL</c>/<c>FATAL</c>,
    /// категория <c>EXCEPTION</c>/<c>ERROR</c>, либо запись с телом traceback
    /// (<see cref="LogEntry.HasTraceback"/>). Предупреждения СЮДА НЕ входят — для них
    /// есть <see cref="MinSeverity"/>.
    /// </summary>
    public bool OnlyErrors { get; set; }

    /// <summary>
    /// Нижняя граница по уровню: записи слабее отбрасываются.
    /// <c>null</c> = не фильтровать. <see cref="LogSeverity.Unknown"/> в качестве границы
    /// пропускает всё, а сами записи с нераспознанным уровнем не отбрасываются никогда.
    /// </summary>
    public LogSeverity? MinSeverity { get; set; }

    /// <summary>
    /// Подстрока для поиска по тексту записи: сырая строка файла, сообщение, категория, уровень,
    /// путь исходника и тело traceback. Регистр не важен. Пустая строка = поиск выключен.
    /// </summary>
    public string Text
    {
        get => _text;
        set => _text = value ?? string.Empty;
    }

    /// <summary>
    /// Тег мода (например <c>[HelloTanker</c>) — запасной критерий для <see cref="OnlyMine"/>,
    /// когда запись не прошла через <c>MarkMine</c>. Пустая строка = запасной критерий выключен.
    /// </summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>true, если ни одно условие не включено.</summary>
    public bool IsEmpty =>
        !OnlyMine && !OnlyErrors && MinSeverity is null && _text.Length == 0;

    /// <summary>Сбрасывает все условия.</summary>
    public void Clear()
    {
        OnlyMine = false;
        OnlyErrors = false;
        MinSeverity = null;
        _text = string.Empty;
    }

    /// <summary>
    /// Проходит ли запись фильтр.
    /// </summary>
    /// <param name="entry">Запись. <c>null</c> не проходит никогда.</param>
    /// <returns>true, если запись надо показать.</returns>
    public bool IsMatch(LogEntry? entry)
    {
        if (entry is null)
            return false;

        if (OnlyMine && !IsMineOrTagged(entry))
            return false;

        if (OnlyErrors && !IsProblem(entry))
            return false;

        if (MinSeverity is LogSeverity min && entry.Severity != LogSeverity.Unknown && entry.Severity < min)
            return false;

        if (_text.Length > 0 && !ContainsText(entry, _text))
            return false;

        return true;
    }

    /// <summary>Отфильтрованная последовательность — удобная обёртка для UI.</summary>
    /// <param name="entries">Исходные записи.</param>
    /// <returns>Записи, прошедшие фильтр, в исходном порядке.</returns>
    public IEnumerable<LogEntry> Apply(IEnumerable<LogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Where(IsMatch!);
    }

    /// <summary>
    /// Считается ли запись проблемной для режима «только ошибки»:
    /// ERROR/CRITICAL/FATAL, категория EXCEPTION/ERROR либо наличие traceback.
    /// </summary>
    /// <param name="entry">Запись.</param>
    /// <returns>true, если запись проблемная.</returns>
    public static bool IsProblem(LogEntry entry)
    {
        if (entry is null)
            return false;

        if (entry.Severity == LogSeverity.Error)
            return true;

        string lvl = entry.Level ?? string.Empty;
        if (lvl.Equals("CRITICAL", StringComparison.OrdinalIgnoreCase) ||
            lvl.Equals("FATAL", StringComparison.OrdinalIgnoreCase) ||
            lvl.Equals("EXCEPTION", StringComparison.OrdinalIgnoreCase))
            return true;

        string cat = entry.Category ?? string.Empty;
        if (cat.Equals("EXCEPTION", StringComparison.OrdinalIgnoreCase) ||
            cat.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
            cat.Equals("CRITICAL", StringComparison.OrdinalIgnoreCase))
            return true;

        return entry.HasTraceback;
    }

    private bool IsMineOrTagged(LogEntry entry)
    {
        if (entry.IsMine)
            return true;

        if (Tag.Length == 0)
            return false;

        if (entry.Category.Contains(Tag, StringComparison.Ordinal) ||
            entry.Message.Contains(Tag, StringComparison.Ordinal) ||
            entry.RawLine.Contains(Tag, StringComparison.Ordinal))
            return true;

        foreach (string cont in entry.Continuation)
        {
            if (cont.Contains(Tag, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool ContainsText(LogEntry entry, string needle)
    {
        if (entry.RawLine.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            entry.Message.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            entry.Category.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            entry.Level.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return true;

        if (entry.SourceFile is not null &&
            entry.SourceFile.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (string cont in entry.Continuation)
        {
            if (cont.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
