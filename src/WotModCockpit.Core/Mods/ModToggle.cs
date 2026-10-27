using System.Diagnostics;

namespace WotModCockpit.Core.Mods;

/// <summary>Что именно делали с файлом мода.</summary>
public enum ModOperation
{
    /// <summary>Включение: <c>X.mtmod.off</c> → <c>X.mtmod</c>.</summary>
    Enable,

    /// <summary>Выключение: <c>X.mtmod</c> → <c>X.mtmod.off</c>.</summary>
    Disable,

    /// <summary>Установка собранного артефакта из <c>dist\</c> в каталог модов.</summary>
    Install,

    /// <summary>Удаление файла мода — единственная необратимая операция.</summary>
    Delete
}

/// <summary>
/// Итог операции. Отказ — это ЗНАЧЕНИЕ, а не исключение: каждый отказ должен доехать до интерфейса
/// с полным путём и внятной причиной, а не превратиться в диалог «произошла ошибка».
/// </summary>
public enum ModOperationStatus
{
    /// <summary>Сделано.</summary>
    Ok,

    /// <summary>Игра запущена (или выяснить не удалось) — при запущенном клиенте писать нельзя.</summary>
    RefusedGameRunning,

    /// <summary>Путь ведёт за пределы каталога модов. Ни одна операция за него не выходит, точка.</summary>
    RefusedOutsideModsDir,

    /// <summary>Это не <c>.mtmod</c> и не <c>.mtmod.off</c> — кокпит такими файлами не управляет.</summary>
    RefusedNotModFile,

    /// <summary>Каталога модов нет.</summary>
    RefusedModsDirMissing,

    /// <summary>Исходного файла нет на диске.</summary>
    RefusedSourceMissing,

    /// <summary>Файл уже в нужном состоянии — делать нечего.</summary>
    RefusedAlreadyInState,

    /// <summary>Целевое имя занято. Перезапись запрещена намеренно: она молча уничтожает чужой файл.</summary>
    RefusedTargetExists,

    /// <summary>Файл занят другим процессом.</summary>
    RefusedFileLocked,

    /// <summary>Файл на диске изменился с момента показа — операция отменена, список надо перечитать.</summary>
    RefusedChanged,

    /// <summary>Подтверждение (напечатанное имя файла) не совпало.</summary>
    RefusedConfirmationMismatch,

    /// <summary>Файловая система отказала уже на самой операции.</summary>
    Failed
}

/// <summary>
/// Ожидаемое состояние файла на момент подтверждения. Нужно ровно для одного случая:
/// пока открыт диалог, каталог модов мог измениться (модпак обновился, другой процесс подменил файл).
/// Если снимок разошёлся с диском — операция не выполняется.
/// </summary>
/// <param name="SizeBytes">Ожидаемый размер.</param>
/// <param name="LastWriteTimeUtc">Ожидаемое время записи.</param>
public sealed record ModWriteExpectation(long SizeBytes, DateTimeOffset LastWriteTimeUtc)
{
    /// <summary>Допуск по времени: разные ФС округляют время записи по-разному.</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    /// <summary>Строит ожидание из строки списка.</summary>
    /// <param name="mod">Мод, показанный пользователю.</param>
    /// <returns>Ожидание.</returns>
    public static ModWriteExpectation From(InstalledMod mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        return new ModWriteExpectation(mod.SizeBytes, mod.LastWriteTimeUtc);
    }

    /// <summary>Совпадает ли ожидание с тем, что сейчас на диске.</summary>
    /// <param name="info">Файл на диске.</param>
    /// <returns>true, если можно действовать.</returns>
    public bool Matches(FileInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        TimeSpan delta = info.LastWriteTimeUtc - LastWriteTimeUtc.UtcDateTime;
        return info.Length == SizeBytes && delta.Duration() <= Tolerance;
    }
}

/// <summary>Результат операции над файлом мода — то, что уходит и в интерфейс, и в журнал.</summary>
/// <param name="Operation">Что делали.</param>
/// <param name="Status">Чем кончилось.</param>
/// <param name="Message">Причина/подробность на русском, для показа пользователю.</param>
/// <param name="SourcePath">Полный путь исходного файла.</param>
/// <param name="TargetPath">Полный путь результата, если он есть.</param>
public sealed record ModOperationResult(
    ModOperation Operation,
    ModOperationStatus Status,
    string Message,
    string SourcePath,
    string? TargetPath = null)
{
    /// <summary>true, если операция выполнена.</summary>
    public bool Success => Status == ModOperationStatus.Ok;

    /// <summary>Момент операции.</summary>
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Сопутствующие операции — например, выключение предыдущих версий того же мода при установке.
    /// Всегда непустой список только у <see cref="ModOperation.Install"/>.
    /// </summary>
    public IReadOnlyList<ModOperationResult> Related { get; init; } = Array.Empty<ModOperationResult>();

    /// <summary>Строка для журнала операций: время, действие, итог, пути.</summary>
    /// <returns>Одна строка без перевода каретки.</returns>
    public string ToLogLine()
    {
        string verb = Operation.ToString().ToUpperInvariant().PadRight(7);
        string outcome = Success ? "ok  " : Status == ModOperationStatus.Failed ? "FAIL" : "REFUSED";
        string arrow = TargetPath is null ? string.Empty : $" -> {TargetPath}";
        return $"{TimestampUtc.ToLocalTime():yyyy-MM-ddTHH:mm:sszzz}  {verb} {outcome}  {SourcePath}{arrow}  ({Message})";
    }
}

/// <summary>Запущена ли игра. Неизвестность — отдельное состояние, и трактуется она в пользу запрета.</summary>
public enum GameRunningStatus
{
    /// <summary>Клиента в списке процессов нет — писать можно.</summary>
    NotRunning,

    /// <summary>Клиент запущен — писать нельзя.</summary>
    Running,

    /// <summary>Выяснить не удалось — считаем, что нельзя.</summary>
    Unknown
}

/// <summary>Ответ пробы процессов.</summary>
/// <param name="Status">Состояние.</param>
/// <param name="ProcessName">Имя найденного процесса.</param>
/// <param name="ProcessId">PID найденного процесса.</param>
/// <param name="Message">Готовая фраза для баннера в интерфейсе.</param>
public sealed record GameRunningState(
    GameRunningStatus Status,
    string? ProcessName,
    int? ProcessId,
    string Message)
{
    /// <summary>Игра не запущена — операции разрешены.</summary>
    public static GameRunningState NotRunning { get; } =
        new(GameRunningStatus.NotRunning, null, null, "игра не запущена — изменения разрешены");

    /// <summary>true, если операции записи запрещены (запущена ИЛИ неизвестно).</summary>
    public bool BlocksWrites => Status != GameRunningStatus.NotRunning;
}

/// <summary>
/// Определяет, запущен ли клиент игры. Отдельная абстракция, чтобы тесты могли проверить
/// отказ при запущенной игре, не запуская игру.
/// </summary>
public interface IGameRunningProbe
{
    /// <summary>Опрашивает список процессов.</summary>
    /// <returns>Состояние. Исключений не бросает — вместо них <see cref="GameRunningStatus.Unknown"/>.</returns>
    GameRunningState Check();
}

/// <summary>
/// Проба по списку процессов Windows.
/// <para>
/// Что считается игрой: процессы <c>Tanki</c>, <c>WorldOfTanks</c>, <c>WorldOfTanks_internal</c>.
/// Что игрой НЕ считается: <c>lgc_api</c> (Lesta Game Center) — он висит в трее постоянно, и запрет
/// по нему заблокировал бы экран навсегда.
/// </para>
/// <para>
/// Имя процесса берётся без обращения к <c>MainModule</c> намеренно: доступ к модулям чужого
/// процесса часто отказывает, а ронять из-за этого всю проверку нельзя. Настоящий клиент
/// (<c>win64\Tanki.exe</c>) и лаунчер в корне называются одинаково — и это не мешает: запрет
/// в обоих случаях безопасен.
/// </para>
/// </summary>
public sealed class ProcessGameRunningProbe : IGameRunningProbe
{
    /// <summary>Имена процессов клиента (без <c>.exe</c>, как их отдаёт <see cref="Process.ProcessName"/>).</summary>
    private static readonly string[] ClientProcessNames = ["Tanki", "WorldOfTanks", "WorldOfTanks_internal"];

    /// <inheritdoc />
    public GameRunningState Check()
    {
        try
        {
            foreach (string name in ClientProcessNames)
            {
                Process[] found = Process.GetProcessesByName(name);
                try
                {
                    if (found.Length > 0)
                    {
                        Process p = found[0];
                        return new GameRunningState(
                            GameRunningStatus.Running,
                            name,
                            SafePid(p),
                            $"ИГРА ЗАПУЩЕНА ({name}.exe, PID {SafePid(p)?.ToString() ?? "?"}). " +
                            "Моды читаются один раз при старте клиента, поэтому изменения заблокированы — закройте игру.");
                    }
                }
                finally
                {
                    foreach (Process p in found)
                    {
                        p.Dispose();
                    }
                }
            }

            return GameRunningState.NotRunning;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return new GameRunningState(
                GameRunningStatus.Unknown,
                null,
                null,
                "не удалось проверить, запущена ли игра (" + ex.Message + ") — изменения заблокированы на всякий случай");
        }
    }

    /// <summary>PID без риска словить исключение на уже умершем процессе.</summary>
    /// <param name="p">Процесс.</param>
    /// <returns>PID либо <c>null</c>.</returns>
    private static int? SafePid(Process p)
    {
        try
        {
            return p.Id;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Проба, которая всегда говорит одно и то же. Нужна тестам и «безопасному» режиму,
/// когда проверять процессы незачем.
/// </summary>
/// <param name="state">Ответ, который она будет выдавать.</param>
public sealed class FixedGameRunningProbe(GameRunningState state) : IGameRunningProbe
{
    /// <inheritdoc />
    public GameRunningState Check() => state;
}

/// <summary>
/// Включение и выключение мода — единственный способ записи, который здесь вообще предусмотрен,
/// и он обратим.
/// <para>
/// <b>Как выключается.</b> Переименованием на месте: <c>X.mtmod</c> → <c>X.mtmod.off</c>.
/// Обоснование не умозрительное: маска <c>*.mtmod</c> задана ДАННЫМИ в <c>paths.xml</c>
/// (строк «mtmod»/«wotmod» в <c>Tanki.exe</c> нет), файл вне маски движок не перечисляет вовсе
/// и молчит о нём в логе, а Win32-перечисление по <c>*.mtmod</c> файл с суффиксом <c>.off</c>
/// не возвращает (проверено на фейковом каталоге; короткие имена 8.3 ложных совпадений не дают —
/// у них расширение максимум три символа, а <c>.mtmod</c> — пять).
/// </para>
/// <para>
/// <b>Почему не перенос в подпапку.</b> У каталога модов <c>mode="recursive"</c>, и в подпапках
/// лежат ЖИВЫЕ моды (96 из 128). Перенос рядом ничего не выключает.
/// </para>
/// <para>
/// <b>Чего здесь нет и не будет.</b> Массовых операций, «выключить всё», автоочистки,
/// перезаписи существующих файлов и работы за пределами каталога модов.
/// </para>
/// </summary>
public sealed class ModToggle
{
    private readonly IGameRunningProbe _gameProbe;

    /// <summary>Создаёт переключатель.</summary>
    /// <param name="gameProbe">Проба «запущена ли игра»; <c>null</c> — настоящая, по списку процессов.</param>
    public ModToggle(IGameRunningProbe? gameProbe = null) => _gameProbe = gameProbe ?? new ProcessGameRunningProbe();

    /// <summary>Текущее состояние игры — им же UI гасит кнопки.</summary>
    /// <returns>Состояние.</returns>
    public GameRunningState CheckGame() => _gameProbe.Check();

    // ------------------------------------------------------------------ операции

    /// <summary>
    /// Выключает мод: <c>X.mtmod</c> → <c>X.mtmod.off</c>. Файл остаётся на месте, байт в байт.
    /// </summary>
    /// <param name="modsDir">Каталог модов игры — граница, за которую операция не выходит.</param>
    /// <param name="fullPath">Полный путь к включённому файлу мода.</param>
    /// <param name="expect">Ожидаемое состояние файла на момент подтверждения; можно <c>null</c>.</param>
    /// <returns>Результат — успешный или с причиной отказа. Исключений не бросает.</returns>
    public ModOperationResult Disable(string modsDir, string fullPath, ModWriteExpectation? expect = null) =>
        Rename(ModOperation.Disable, modsDir, fullPath, expect);

    /// <summary>
    /// Включает мод обратно: <c>X.mtmod.off</c> → <c>X.mtmod</c>. Операция, обратная <see cref="Disable"/>,
    /// и дающая побайтово тот же файл.
    /// </summary>
    /// <param name="modsDir">Каталог модов игры.</param>
    /// <param name="fullPath">Полный путь к выключенному файлу мода.</param>
    /// <param name="expect">Ожидаемое состояние файла; можно <c>null</c>.</param>
    /// <returns>Результат — успешный или с причиной отказа.</returns>
    public ModOperationResult Enable(string modsDir, string fullPath, ModWriteExpectation? expect = null) =>
        Rename(ModOperation.Enable, modsDir, fullPath, expect);

    /// <summary>Общая механика обоих переименований.</summary>
    /// <param name="operation">Включение или выключение.</param>
    /// <param name="modsDir">Каталог модов.</param>
    /// <param name="fullPath">Исходный файл.</param>
    /// <param name="expect">Ожидаемое состояние.</param>
    /// <returns>Результат.</returns>
    private ModOperationResult Rename(ModOperation operation, string modsDir, string fullPath, ModWriteExpectation? expect)
    {
        string source = Normalize(fullPath);

        ModOperationResult? refusal = Validate(operation, modsDir, source, expect);
        if (refusal is not null)
        {
            return refusal;
        }

        ModState desired = operation == ModOperation.Disable ? ModState.Disabled : ModState.Enabled;
        if (ModFileNames.StateOf(source) == desired)
        {
            return new ModOperationResult(
                operation,
                ModOperationStatus.RefusedAlreadyInState,
                desired == ModState.Disabled ? "мод уже выключен" : "мод уже включён",
                source);
        }

        string? target = desired == ModState.Disabled
            ? ModFileNames.ToDisabledPath(source)
            : ModFileNames.ToEnabledPath(source);

        if (target is null)
        {
            return new ModOperationResult(
                operation, ModOperationStatus.RefusedNotModFile, "это не файл мода", source);
        }

        // Цель обязана остаться в том же каталоге и внутри каталога модов: переименование —
        // операция на месте, и никакого «заодно переложим» здесь быть не может.
        if (!IsInside(modsDir, target) ||
            !string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase))
        {
            return new ModOperationResult(
                operation, ModOperationStatus.RefusedOutsideModsDir,
                "результат оказался бы вне каталога модов", source, target);
        }

        if (File.Exists(target))
        {
            return new ModOperationResult(
                operation, ModOperationStatus.RefusedTargetExists,
                "файл с таким именем уже есть — перезапись запрещена, разберитесь вручную", source, target);
        }

        ModOperationResult? locked = CheckNotLocked(operation, source);
        if (locked is not null)
        {
            return locked;
        }

        try
        {
            // Именно File.Move БЕЗ overwrite. Перегрузка с overwrite:true и File.Replace
            // затирают цель молча — ровно та семантика, которой здесь быть не должно.
            File.Move(source, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ModOperationResult(
                operation, ModOperationStatus.Failed, ex.Message, source, target);
        }

        return new ModOperationResult(
            operation,
            ModOperationStatus.Ok,
            desired == ModState.Disabled
                ? "мод выключен, файл на месте; изменения вступят в силу при следующем запуске игры"
                : "мод включён; изменения вступят в силу при следующем запуске игры",
            source,
            target);
    }

    // ------------------------------------------------------------------ проверки

    /// <summary>
    /// Проверяет всё, что должно быть проверено ДО записи. Порядок важен: сперва границы и запреты,
    /// потом состояние диска.
    /// </summary>
    /// <param name="operation">Операция (нужна только для заполнения результата).</param>
    /// <param name="modsDir">Каталог модов.</param>
    /// <param name="fullPath">Файл, над которым собираются работать.</param>
    /// <param name="expect">Ожидаемое состояние файла; можно <c>null</c>.</param>
    /// <returns><c>null</c>, если всё в порядке, иначе готовый отказ.</returns>
    public ModOperationResult? Validate(
        ModOperation operation,
        string modsDir,
        string fullPath,
        ModWriteExpectation? expect = null)
    {
        string source = Normalize(fullPath);

        if (string.IsNullOrWhiteSpace(modsDir))
        {
            return new ModOperationResult(
                operation, ModOperationStatus.RefusedModsDirMissing, "каталог модов не задан", source);
        }

        if (!Directory.Exists(modsDir))
        {
            return new ModOperationResult(
                operation, ModOperationStatus.RefusedModsDirMissing, $"каталога модов нет: {modsDir}", source);
        }

        if (!IsInside(modsDir, source))
        {
            return new ModOperationResult(
                operation, ModOperationStatus.RefusedOutsideModsDir,
                $"файл лежит вне каталога модов {modsDir} — операция запрещена", source);
        }

        if (!ModFileNames.IsManagedFile(source))
        {
            return new ModOperationResult(
                operation, ModOperationStatus.RefusedNotModFile,
                "кокпит управляет только файлами *.mtmod и *.mtmod.off", source);
        }

        FileInfo info = new(source);
        if (!info.Exists)
        {
            return new ModOperationResult(
                operation, ModOperationStatus.RefusedSourceMissing, "файла больше нет на диске", source);
        }

        if (expect is not null && !expect.Matches(info))
        {
            return new ModOperationResult(
                operation, ModOperationStatus.RefusedChanged,
                "файл изменился с момента показа — список надо обновить", source);
        }

        GameRunningState game = _gameProbe.Check();
        if (game.BlocksWrites)
        {
            return new ModOperationResult(
                operation, ModOperationStatus.RefusedGameRunning, game.Message, source);
        }

        return null;
    }

    /// <summary>
    /// Проверяет, что файл никем не удерживается: открывает его на <see cref="FileShare.None"/>
    /// и сразу закрывает. При незапущенной игре реальный <c>.mtmod</c> так открывается без проблем —
    /// то есть проверка различает состояния, а не срабатывает всегда.
    /// </summary>
    /// <param name="operation">Операция.</param>
    /// <param name="source">Файл.</param>
    /// <returns><c>null</c>, если файл свободен, иначе отказ.</returns>
    private static ModOperationResult? CheckNotLocked(ModOperation operation, string source)
    {
        try
        {
            using FileStream _ = new(source, FileMode.Open, FileAccess.Read, FileShare.None);
            return null;
        }
        catch (IOException ex)
        {
            return new ModOperationResult(
                operation, ModOperationStatus.RefusedFileLocked,
                "файл занят другим процессом (обычно это запущенная игра или открытый архиватор): " + ex.Message,
                source);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ModOperationResult(
                operation, ModOperationStatus.RefusedFileLocked, "нет доступа к файлу: " + ex.Message, source);
        }
    }

    /// <summary>
    /// Лежит ли путь внутри каталога модов. Сравнение по нормализованным полным путям,
    /// обязательно по границе разделителя — иначе <c>mods\1.44.0.0-backup</c> сошёл бы за
    /// <c>mods\1.44.0.0</c>. Сам каталог модов «внутри себя» не считается.
    /// </summary>
    /// <param name="modsDir">Каталог модов.</param>
    /// <param name="path">Проверяемый путь.</param>
    /// <returns>true, если путь внутри.</returns>
    public static bool IsInside(string modsDir, string path)
    {
        if (string.IsNullOrWhiteSpace(modsDir) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsDir));
            string full = Path.GetFullPath(path);

            return full.Length > root.Length + 1 &&
                   full.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                   (full[root.Length] == Path.DirectorySeparatorChar || full[root.Length] == Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ModScanner.IsExpectedIoFailure(ex))
        {
            return false;
        }
    }

    /// <summary>Приводит путь к полному виду, не падая на мусоре.</summary>
    /// <param name="path">Путь.</param>
    /// <returns>Полный путь либо исходная строка.</returns>
    internal static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path ?? string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ModScanner.IsExpectedIoFailure(ex))
        {
            return path;
        }
    }
}
