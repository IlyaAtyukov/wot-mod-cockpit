using CommunityToolkit.Mvvm.ComponentModel;
using WotModCockpit.Core.Models;

namespace WotModCockpit.App.ViewModels;

/// <summary>
/// Одна строка ленты лога. Объектов этого класса в памяти держится до
/// <see cref="CockpitSettings.LogBufferCapacity"/> штук, поэтому он намеренно тощий:
/// никаких кистей, никаких вычислений в конструкторе сверх необходимого,
/// кадры traceback строятся ЛЕНИВО — только когда запись реально развернули.
/// </summary>
public sealed partial class LogEntryViewModel : ObservableObject
{
    private readonly Func<LogEntryViewModel, IReadOnlyList<TracebackFrameViewModel>>? _framesFactory;
    private string? _continuationText;

    /// <summary>Создаёт модель представления поверх разобранной записи.</summary>
    /// <param name="entry">Запись лога.</param>
    /// <param name="framesFactory">
    /// Построитель кадров traceback. Вызывается один раз при первом разворачивании записи.
    /// <c>null</c> = записи не разворачиваются (например, маппер ещё заглушка).
    /// </param>
    /// <param name="isSystem">
    /// true для строк, которые вставил САМ кокпит (разделитель «лог усечён игрой»).
    /// Такие строки не подчиняются фильтрам «только моё» / «только ошибки».
    /// </param>
    public LogEntryViewModel(
        LogEntry entry,
        Func<LogEntryViewModel, IReadOnlyList<TracebackFrameViewModel>>? framesFactory = null,
        bool isSystem = false)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _framesFactory = framesFactory;
        IsSystem = isSystem;

        TimeText = entry.Timestamp?.ToString("HH:mm:ss.fff") ?? "  --:--:--  ";
        CanExpand = !isSystem && (entry.Continuation.Count > 0 || entry.SourceFile is not null);
    }

    /// <summary>Исходная запись. Нужна мапперу traceback.</summary>
    public LogEntry Entry { get; }

    /// <summary>Время в виде <c>ЧЧ:ММ:СС.мс</c>. Для обрезанной движком строки — прочерк.</summary>
    public string TimeText { get; }

    /// <summary>Уровень как в файле (INFO, ERROR, DEBUG, WARNING, NOTICE, UNKNOWN…).</summary>
    public string Level => Entry.Level;

    /// <summary>Категория из первых квадратных скобок.</summary>
    public string Category => Entry.Category;

    /// <summary>Текст сообщения.</summary>
    public string Message => Entry.Message;

    /// <summary>Огрублённый уровень — по нему красится строка.</summary>
    public LogSeverity Severity => Entry.Severity;

    /// <summary>Запись относится к моду разработчика.</summary>
    public bool IsMine => Entry.IsMine;

    /// <summary>Строку вставил кокпит, а не игра.</summary>
    public bool IsSystem { get; }

    /// <summary>Есть что разворачивать: тело traceback либо блок <c>(файл, строка)</c>.</summary>
    public bool CanExpand { get; }

    /// <summary>Виртуальный путь исходника из заголовка записи, если он был.</summary>
    public string? SourceFile => Entry.SourceFile;

    /// <summary>Тело traceback одним куском, дословно.</summary>
    public string ContinuationText =>
        _continuationText ??= Entry.Continuation.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, Entry.Continuation);

    /// <summary>Подсказка на всю строку: сообщение целиком плюс путь исходника.</summary>
    public string ToolTipText =>
        Entry.SourceFile is null
            ? Message
            : $"{Entry.SourceFile}, {Entry.SourceLine}\n{Message}";

    /// <summary>Запись развёрнута — видно тело traceback и кликабельные кадры.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>
    /// Кадры traceback с уже посчитанным отображением на диск. Пустой список, пока запись
    /// не развёрнута: строить их для 20 000 записей никто не собирается.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<TracebackFrameViewModel> _frames = Array.Empty<TracebackFrameViewModel>();

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || Frames.Count > 0 || _framesFactory is null)
        {
            return;
        }

        Frames = _framesFactory(this);
    }

    /// <summary>
    /// Подстрочный поиск по записи: сырая строка файла, сообщение, категория, тело traceback.
    /// Регистр не важен.
    /// </summary>
    /// <param name="needle">Искомая подстрока. Пустая = совпадает всё.</param>
    /// <returns>true, если запись показывать.</returns>
    public bool Matches(string? needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            return true;
        }

        const StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

        if (Entry.RawLine.Contains(needle, Cmp)
            || Message.Contains(needle, Cmp)
            || Category.Contains(needle, Cmp)
            || Level.Contains(needle, Cmp))
        {
            return true;
        }

        for (int i = 0; i < Entry.Continuation.Count; i++)
        {
            if (Entry.Continuation[i].Contains(needle, Cmp))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Кадр traceback, уже сопоставленный с файлом на диске. Клик по нему открывает VS Code.
/// Неизменяемый: пересчитывать нечего, а объектов бывает много.
/// </summary>
public sealed class TracebackFrameViewModel
{
    /// <summary>Создаёт модель представления поверх результата сопоставления.</summary>
    /// <param name="mapping">Результат <see cref="Core.Abstractions.ITracebackMapper.Map"/>.</param>
    public TracebackFrameViewModel(TracebackMapping mapping)
    {
        Mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));

        TracebackFrame f = mapping.Frame;
        Display = f.Function is null
            ? $"{f.VirtualPath}:{f.Line}"
            : $"{f.VirtualPath}:{f.Line}  →  {f.Function}()";
    }

    /// <summary>Результат сопоставления кадра с диском.</summary>
    public TracebackMapping Mapping { get; }

    /// <summary>Откуда файл: мод / клиент / res_mods / не нашли.</summary>
    public FrameOrigin Origin => Mapping.Origin;

    /// <summary>Есть куда переходить.</summary>
    public bool IsResolved => Mapping.IsResolved;

    /// <summary>Абсолютный путь к файлу либо <c>null</c>.</summary>
    public string? DiskPath => Mapping.DiskPath;

    /// <summary>Строка, на которую надо поставить курсор.</summary>
    public int TargetLine => Mapping.Line;

    /// <summary>Номер строки не гарантирован (декомпилированный клиент).</summary>
    public bool IsApproximate => Mapping.IsApproximate;

    /// <summary>Текст кадра для списка: виртуальный путь, строка, функция.</summary>
    public string Display { get; }

    /// <summary>Подсказка: реальный путь на диске или причина, почему открыть нельзя.</summary>
    public string ToolTipText => Mapping.IsResolved
        ? $"{Mapping.DiskPath}:{Mapping.Line}"
          + (Mapping.IsApproximate ? "\nНомер строки ПРИБЛИЗИТЕЛЬНЫЙ: декомпилятор не сохраняет нумерацию." : string.Empty)
          + (Mapping.Origin == FrameOrigin.ResMods ? "\nЭто КОПИЯ в res_mods — правки затрёт следующая Dev-раскладка." : string.Empty)
        : "Файл на диске не найден: путь виртуальный (внутри scripts.pkg или .mtmod).";
}
