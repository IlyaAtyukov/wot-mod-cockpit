namespace WotModCockpit.Core.Models;

/// <summary>Из какого потока процесса пришла строка вывода сборки.</summary>
public enum BuildStreamKind
{
    /// <summary>stdout процесса build.py. Обычные строки вида <c>[build] ...</c>.</summary>
    Stdout = 0,

    /// <summary>stderr процесса build.py. Сюда падают python-трейсбеки самого тулчейна и предупреждения.</summary>
    Stderr = 1,

    /// <summary>
    /// Строка, которую добавил САМ кокпит, а не процесс: эхо командной строки перед запуском,
    /// «отменено пользователем», «процесс завершился с кодом N». Помечается, чтобы UI красил её иначе
    /// и чтобы парсер артефакта её не разбирал.
    /// </summary>
    Diagnostic = 2
}

/// <summary>
/// Одна строка вывода сборки. Строки НЕ содержат завершающего <c>\r\n</c>.
/// </summary>
/// <param name="Stream">Источник строки.</param>
/// <param name="Text">Текст строки без перевода строки. Может быть пустой строкой.</param>
/// <param name="TimestampUtc">Момент, когда строка была прочитана кокпитом (не временем внутри build.py).</param>
public sealed record BuildOutputLine(BuildStreamKind Stream, string Text, DateTimeOffset TimestampUtc)
{
    /// <summary>Удобный конструктор строки stdout с текущим временем.</summary>
    public static BuildOutputLine Out(string text) => new(BuildStreamKind.Stdout, text, DateTimeOffset.UtcNow);

    /// <summary>Удобный конструктор строки stderr с текущим временем.</summary>
    public static BuildOutputLine Err(string text) => new(BuildStreamKind.Stderr, text, DateTimeOffset.UtcNow);

    /// <summary>Удобный конструктор служебной строки кокпита с текущим временем.</summary>
    public static BuildOutputLine Note(string text) => new(BuildStreamKind.Diagnostic, text, DateTimeOffset.UtcNow);
}

/// <summary>
/// Итог одного запуска build.py.
/// </summary>
/// <param name="Success">
/// true ⟺ процесс завершился с кодом 0 И не был отменён. Наличие строк в stderr само по себе провалом НЕ считается —
/// build.py пишет туда и безобидные предупреждения.
/// </param>
/// <param name="ExitCode">
/// Код возврата процесса. Специальные значения кокпита, когда процесса не было или он не дожил:
/// <c>-1</c> — не удалось запустить python (<c>Win32Exception</c>, нет файла);
/// <c>-2</c> — отменено пользователем (<see cref="OperationCanceledException"/> / убит по таймауту).
/// </param>
/// <param name="ArtifactPath">
/// Абсолютный путь к собранному <c>.mtmod</c>. Реализация ОБЯЗАНА брать его из stdout — build.py печатает
/// строку вида <c>[build] OK   D:\...\ru.local.hello_tanker_1.0.0.mtmod (62848 bytes, 10 entries, all Stored)</c>.
/// Если такой строки нет (например, действие <see cref="BuildAction.Uninstall"/> или сборка упала) —
/// здесь <see cref="string.Empty"/>, а НЕ предсказанный путь и не null.
/// </param>
/// <param name="Duration">Время от старта процесса до его выхода.</param>
public sealed record BuildResult(
    bool Success,
    int ExitCode,
    string ArtifactPath,
    TimeSpan Duration)
{
    /// <summary>Код возврата «не удалось запустить процесс».</summary>
    public const int ExitCodeLaunchFailed = -1;

    /// <summary>Код возврата «отменено пользователем / по таймауту».</summary>
    public const int ExitCodeCancelled = -2;

    /// <summary>
    /// Полная командная строка, которой был запущен процесс (для эха в лог сборки и для кнопки «скопировать команду»).
    /// Пустая строка, если процесс так и не стартовал.
    /// </summary>
    public string CommandLine { get; init; } = string.Empty;

    /// <summary>
    /// Короткое пояснение к провалу для UI (первая строка исключения запуска, «отменено пользователем»,
    /// «таймаут N с»). <c>null</c>, если <see cref="Success"/> = true.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>Готовый результат «не смогли запустить python».</summary>
    public static BuildResult LaunchFailed(string commandLine, string reason) =>
        new(false, ExitCodeLaunchFailed, string.Empty, TimeSpan.Zero)
        { CommandLine = commandLine, FailureReason = reason };

    /// <summary>Готовый результат «пользователь отменил сборку».</summary>
    public static BuildResult Cancelled(string commandLine, TimeSpan elapsed) =>
        new(false, ExitCodeCancelled, string.Empty, elapsed)
        { CommandLine = commandLine, FailureReason = "отменено пользователем" };
}

/// <summary>
/// Параметры одного запуска build.py поверх того, что уже записано в <see cref="ModProject"/>.
/// Все поля опциональны: <c>new BuildRunOptions()</c> — корректный «обычный» запуск.
/// </summary>
public sealed record BuildRunOptions
{
    /// <summary>Добавить <c>--clean</c>: очистить <c>dist\</c> перед сборкой.</summary>
    public bool Clean { get; init; }

    /// <summary>Добавить <c>--no-syntax-check</c>: пропустить грамматический шлюз <c>tools/check_py2_syntax.py</c>.</summary>
    public bool NoSyntaxCheck { get; init; }

    /// <summary>Добавить <c>--compile-all</c>: компилировать в .pyc и вложенные модули, а не только точку входа.</summary>
    public bool CompileAll { get; init; }

    /// <summary>Добавить <c>--keep-py</c>: положить в архив .py рядом с .pyc.</summary>
    public bool KeepPy { get; init; }

    /// <summary>Добавить <c>--list</c> дополнительно к основному действию: напечатать листинг архива.</summary>
    public bool PrintListing { get; init; }

    /// <summary>
    /// Значение для <c>--py27</c>/<c>--py2</c> — путь к внешнему Python 2.7.
    /// <c>null</c> = не передавать флаг (тулчейн обойдётся встроенным <c>tools/py27c.py</c>).
    /// </summary>
    public string? Python27Path { get; init; }

    /// <summary>
    /// Перекрыть <c>--game-dir</c> для этого запуска. <c>null</c> = взять <see cref="ModProject.GameDir"/>,
    /// а если и он пуст — не передавать флаг вовсе (build.py возьмёт свой дефолт).
    /// </summary>
    public string? GameDirOverride { get; init; }

    /// <summary>Произвольные дополнительные аргументы, дописываемые в конец командной строки. Пусто по умолчанию.</summary>
    public IReadOnlyList<string> AdditionalArgs { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Жёсткий предел на время работы процесса. По истечении процесс убивается,
    /// результат — <see cref="BuildResult.ExitCodeCancelled"/>. По умолчанию 5 минут.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}
