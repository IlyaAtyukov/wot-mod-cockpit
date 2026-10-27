using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Migration;

/// <summary>Итог декомпиляции одного .pyc.</summary>
/// <param name="Text">Исходник; пусто — не вышло вовсе.</param>
/// <param name="Partial">Декомпилятор сдался на середине: текст обрывается.</param>
/// <param name="Error">Что сказал декомпилятор, если не вышло.</param>
public sealed record DecompileOutcome(string Text, bool Partial, string? Error)
{
    /// <summary>Хоть какой-то исходник есть.</summary>
    public bool HasText => Text.Length > 0;
}

/// <summary>Декомпилятор байткода Python 2.7. Подменяется в тестах.</summary>
public interface IPycDecompiler
{
    /// <summary>Декомпилирует один файл.</summary>
    /// <param name="pycPath">Путь к .pyc.</param>
    /// <param name="ct">Отмена.</param>
    /// <returns>Исходник или причина неудачи.</returns>
    Task<DecompileOutcome> DecompileAsync(string pycPath, CancellationToken ct);
}

/// <summary>
/// uncompyle6, запущенный как <c>python -m uncompyle6.bin.uncompile</c>.
/// <para>
/// Именно через <c>-m</c>, а не через <c>Scripts\uncompyle6.exe</c>: запускатель в venv хранит
/// путь к питону на момент создания venv и после переноса каталога молча ничего не делает.
/// </para>
/// <para>
/// uncompyle6 распространяется под GPL-3.0: кокпит лишь запускает его отдельным процессом из
/// окружения пользователя и не поставляет его вместе с собой (см. THIRD-PARTY-NOTICES.md).
/// </para>
/// <para>
/// У каждого экземпляра свой временный каталог (TEMP/TMP/TMPDIR процессов): uncompyle6 при выводе
/// в stdout на каждый файл заводит пустой <c>py-dis-*</c> в TEMP и не убирает его — восемь тысяч
/// пустых каталогов за одно извлечение. Каталог удаляется в <see cref="Dispose"/>.
/// </para>
/// </summary>
public sealed class Uncompyle6Decompiler : IPycDecompiler, IDisposable
{
    /// <summary>
    /// Строка, которую uncompyle6 печатает ТОЛЬКО при успехе. Код возврата не показатель: команда
    /// click завершается с 0 и тогда, когда разбор модуля упал (ошибка уходит в stderr, а в stdout
    /// остаётся одна шапка).
    /// </summary>
    public const string SuccessMarker = "# okay decompiling";

    /// <summary>
    /// Строки, которыми uncompyle6 отмечает, что сдался на середине файла, УЖЕ выведя текст модуля
    /// (не разобралась вложенная функция). Только в этом случае в выводе есть настоящий код.
    /// </summary>
    private static readonly string[] PartialMarkers =
    [
        "# Deparsing stopped due to parse error",
        "Deparsing hit an internal grammar-rule bug",
        "--- This code section failed",
    ];

    /// <summary>Начало отладочного вывода парсера (spark), который uncompyle6 печатает в stdout при ошибке разбора.</summary>
    private const string ParserDumpStart = "-- Stacks of completed symbols:";

    /// <summary>Конец правил и начало листинга инструкций в том же отладочном выводе.</summary>
    private const string ParserDumpContext = "Instruction context:";

    private readonly string _python;
    private readonly TimeSpan _timeout;
    private readonly string _tempDir;

    /// <summary>Создаёт декомпилятор.</summary>
    /// <param name="pythonExe">Python, в котором установлен uncompyle6.</param>
    /// <param name="timeout">Предел на один файл; по умолчанию 10 минут.</param>
    public Uncompyle6Decompiler(string pythonExe, TimeSpan? timeout = null)
    {
        _python = pythonExe ?? throw new ArgumentNullException(nameof(pythonExe));
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        _tempDir = Path.Combine(Path.GetTempPath(), "wotcockpit-uncompyle-" + Guid.NewGuid().ToString("N")[..8]);
    }

    /// <summary>Каким Python декомпилируем.</summary>
    public string PythonExe => _python;

    /// <summary>
    /// Кандидаты в Python с uncompyle6: необязательный venv в рабочей папке шаблона мода
    /// (<c>&lt;шаблон&gt;\.tools\venv_uncompyle</c>, если он там заведён), Python из настроек,
    /// затем просто <c>python</c> и <c>py</c> из PATH.
    /// </summary>
    /// <param name="templateDir">Каталог шаблона мода или <c>null</c>.</param>
    /// <param name="settingsPython">Python из настроек кокпита.</param>
    /// <returns>Кандидаты по порядку.</returns>
    public static IEnumerable<string> DefaultCandidates(string? templateDir, string? settingsPython)
    {
        if (!string.IsNullOrWhiteSpace(templateDir))
        {
            yield return Path.Combine(templateDir, ".tools", "venv_uncompyle", "Scripts", "python.exe");
        }

        if (!string.IsNullOrWhiteSpace(settingsPython))
        {
            yield return settingsPython;
        }

        yield return "python";
        yield return "py";
    }

    /// <summary>
    /// Первый кандидат, в котором импортируется ровно то, что потом запускается:
    /// <c>uncompyle6.bin.uncompile</c> (ему, в отличие от самого пакета, нужен ещё и click).
    /// </summary>
    /// <param name="candidates">Кандидаты.</param>
    /// <returns>Путь или команда Python либо <c>null</c>.</returns>
    public static string? FindPython(IEnumerable<string> candidates)
    {
        foreach (string candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.IsPathRooted(candidate) && !File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var psi = new ProcessStartInfo(candidate)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("import uncompyle6.bin.uncompile, xdis");
                using Process? p = Process.Start(psi);
                if (p is null)
                {
                    continue;
                }

                // Вывод читается до ожидания: заполненный канал иначе подвесил бы процесс.
                Task<string> stdout = p.StandardOutput.ReadToEndAsync();
                Task<string> stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(30_000))
                {
                    p.Kill(entireProcessTree: true);
                    continue;
                }

                Task.WaitAll(stdout, stderr);
                if (p.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                // такого Python нет — следующий кандидат
            }
        }

        return null;
    }

    /// <summary>
    /// Что значит вывод uncompyle6 (открыто для тестов).
    /// <list type="bullet">
    ///   <item>Успех — только при строке <see cref="SuccessMarker"/>, коде 0 и без признаков остановки.</item>
    ///   <item>Частично — uncompyle6 сдался на вложенной функции (<see cref="PartialMarkers"/>): текст
    ///         модуля он к этому времени уже вывел, и в нём есть код.</item>
    ///   <item>Всё остальное — не вышло. Ошибка разбора модуля или класса, неподдерживаемый байткод
    ///         («Unsupported Python version» — голой строкой в stdout): в выводе нет ни строки
    ///         исходника, только шапка и диагностика, и принимать её за код нельзя.</item>
    /// </list>
    /// Отладочный вывод парсера (правила грамматики и листинг инструкций — сотни строк перед
    /// текстом модуля) из текста вырезается: в дерево он не попадает и сравнение версий не путает.
    /// </summary>
    /// <param name="stdout">Вывод.</param>
    /// <param name="stderr">Ошибки.</param>
    /// <param name="exitCode">Код возврата.</param>
    /// <returns>Итог.</returns>
    public static DecompileOutcome Interpret(string stdout, string stderr, int exitCode)
    {
        string text = StripParserDump(stdout);
        string? stopped = PartialMarkers.FirstOrDefault(m =>
            stderr.Contains(m, StringComparison.Ordinal) || text.Contains(m, StringComparison.Ordinal));

        if (exitCode == 0 && stopped is null && text.Contains(SuccessMarker, StringComparison.Ordinal) && HasCode(text))
        {
            return new DecompileOutcome(text, false, null);
        }

        string why = stopped ?? LastLine(stderr.Trim().Length > 0 ? stderr : stdout);
        if (why.Length == 0)
        {
            why = "пустой вывод, код " + exitCode;
        }

        return stopped is not null && HasCode(text)
            ? new DecompileOutcome(text, true, why)
            : new DecompileOutcome(string.Empty, false, why);
    }

    /// <summary>
    /// Убирает отладочный вывод парсера: от «-- Stacks of completed symbols:» через правила до
    /// «Instruction context:» и следующий за ним листинг (строки с отступом и «-&gt;»). Таких
    /// блоков по одному на каждую неразобранную функцию, и все они идут до текста модуля.
    /// </summary>
    internal static string StripParserDump(string stdout)
    {
        if (!stdout.Contains(ParserDumpStart, StringComparison.Ordinal))
        {
            return stdout;
        }

        var kept = new List<string>();
        int state = 0; // 0 — текст, 1 — правила грамматики, 2 — листинг инструкций
        foreach (string raw in stdout.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line == ParserDumpStart)
            {
                state = 1;
                continue;
            }

            if (state == 1)
            {
                if (line == ParserDumpContext)
                {
                    state = 2;
                }

                continue;
            }

            if (state == 2)
            {
                if (line.Length == 0 || line.StartsWith("->", StringComparison.Ordinal) || char.IsWhiteSpace(line[0]))
                {
                    continue;
                }

                state = 0;
            }

            kept.Add(raw);
        }

        return string.Join('\n', kept);
    }

    /// <inheritdoc />
    public async Task<DecompileOutcome> DecompileAsync(string pycPath, CancellationToken ct)
    {
        Directory.CreateDirectory(_tempDir);
        var psi = new ProcessStartInfo(_python)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["TEMP"] = _tempDir;
        psi.Environment["TMP"] = _tempDir;
        psi.Environment["TMPDIR"] = _tempDir;
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("uncompyle6.bin.uncompile");
        psi.ArgumentList.Add(pycPath);

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("Python не запустился: " + _python);
        Task<string> stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // уже завершился
            }

            ct.ThrowIfCancellationRequested();
            return new DecompileOutcome(string.Empty, false, "декомпиляция не уложилась в " + _timeout.TotalMinutes + " мин");
        }

        return Interpret(await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), p.ExitCode);
    }

    /// <summary>Убирает временный каталог процессов (с пустыми <c>py-dis-*</c> внутри).</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // пустые каталоги во временной папке — не повод ронять переход
        }
    }

    /// <summary>В тексте есть хоть одна строка кода, а не только комментарии шапки.</summary>
    private static bool HasCode(string text) =>
        text.Split('\n').Select(l => l.Trim()).Any(l => l.Length > 0 && !l.StartsWith('#'));

    private static string LastLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? string.Empty;
}

/// <summary>Ход извлечения.</summary>
/// <param name="Stage">Что сейчас делается.</param>
/// <param name="Done">Сколько готово.</param>
/// <param name="Total">Сколько всего.</param>
public sealed record ClientSourceProgress(string Stage, int Done, int Total);

/// <summary>Итог извлечения.</summary>
/// <param name="TreeDir">Готовое дерево.</param>
/// <param name="Version">Версия клиента.</param>
/// <param name="Total">.pyc в scripts.pkg.</param>
/// <param name="Decompiled">Полностью.</param>
/// <param name="Partial">Частично.</param>
/// <param name="Failed">Не вышло.</param>
/// <param name="ExtraModules">Модулей дополнительных пакетов.</param>
/// <param name="Elapsed">Сколько заняло.</param>
public sealed record ClientSourceExtraction(
    string TreeDir,
    string Version,
    int Total,
    int Decompiled,
    int Partial,
    int Failed,
    int ExtraModules,
    TimeSpan Elapsed);

/// <summary>
/// Дерево исходников новой версии клиента: <c>res\packages\scripts.pkg</c> → .pyc →
/// декомпиляция в несколько потоков → <c>client\</c>, <c>client_common\</c>, <c>common\</c>.
/// <para>
/// Игра только читается. Дерево собирается во временном каталоге рядом с целью и встаёт на
/// место одним переименованием в самом конце: недоделанное дерево (отмена, сбой) не выглядит
/// готовым и не подхватывается индексом. Файлы, которые декомпилятор осилил лишь частично,
/// всё равно кладутся — с пометкой в первой строке: определения до места сбоя индексу нужны.
/// </para>
/// <para>
/// Декомпиляция, не давшая ни одного файла (сломанный uncompyle6, пустой пакет), — ошибка, а не
/// пустое «готовое» дерево: иначе кокпит переключился бы на него и потерял рабочее.
/// </para>
/// </summary>
public sealed class ClientSourceExtractor
{
    /// <summary>
    /// Начало пометки частичной декомпиляции. Общее у кокпита и у ручного разбора шаблона
    /// (там в скобках «uncompyle6: parse error»): сравнение версий пропускает строку по нему.
    /// </summary>
    public const string PartialHeaderPrefix = "# ВНИМАНИЕ: декомпиляция частичная";

    /// <summary>Пометка в первой строке частично декомпилированного файла.</summary>
    public const string PartialHeader = PartialHeaderPrefix + " (uncompyle6 сдался на середине), текст обрывается";

    private static readonly string[] TreeRoots = ["client", "client_common", "common"];

    /// <summary>Паузы между попытками переименовать готовое дерево (антивирус, индексатор).</summary>
    private static readonly int[] MoveRetryDelaysMs = [100, 250, 500, 1000];

    private readonly IPycDecompiler _decompiler;
    private readonly int _parallelism;

    /// <summary>Создаёт извлекатель.</summary>
    /// <param name="decompiler">Декомпилятор.</param>
    /// <param name="parallelism">Сколько файлов сразу; 0 — по числу ядер минус два.</param>
    public ClientSourceExtractor(IPycDecompiler decompiler, int parallelism = 0)
    {
        _decompiler = decompiler ?? throw new ArgumentNullException(nameof(decompiler));
        _parallelism = parallelism > 0 ? parallelism : Math.Max(1, Environment.ProcessorCount - 2);
    }

    /// <summary>Путь к scripts.pkg игры.</summary>
    /// <param name="game">Установка.</param>
    /// <returns>Путь.</returns>
    public static string ScriptsPackage(GameInstall game) => Path.Combine(game.RootPath, "res", "packages", "scripts.pkg");

    /// <summary>Извлекает и декомпилирует исходники клиента.</summary>
    /// <param name="game">Установка (версия и путь к пакетам).</param>
    /// <param name="targetDir">Куда положить дерево; не должен существовать.</param>
    /// <param name="progress">Ход работы.</param>
    /// <param name="ct">Отмена.</param>
    /// <returns>Итог.</returns>
    /// <exception cref="InvalidOperationException">Ни один файл не декомпилировался.</exception>
    /// <exception cref="IOException">
    /// Цель уже есть; либо дерево готово, но не переименовалось — тогда оно оставлено во временном
    /// каталоге, и его путь в сообщении.
    /// </exception>
    public async Task<ClientSourceExtraction> ExtractAsync(
        GameInstall game,
        string targetDir,
        IProgress<ClientSourceProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(game);
        string target = Path.GetFullPath(targetDir);
        if (Directory.Exists(target))
        {
            throw new IOException("каталог уже существует: " + target);
        }

        string pkg = ScriptsPackage(game);
        if (!File.Exists(pkg))
        {
            throw new FileNotFoundException("в игре нет scripts.pkg", pkg);
        }

        var watch = Stopwatch.StartNew();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string staging = target + ".partial-" + suffix;

        // Копии .pyc — не внутри будущего дерева (в готовое дерево они не попадут ни при каком
        // исходе, а их удаление — уборка, а не условие успеха) и плоско, под номерами, в коротком
        // временном каталоге: Python без включённых длинных путей Windows не открывает файл длиннее
        // 260 символов, а gui\impl\gen\view_models\… рядом с глубокой целью за них выходят.
        string pycRoot = Path.Combine(Path.GetTempPath(), "wotcockpit-pyc-" + suffix);

        ClientSourceExtraction result;
        try
        {
            progress?.Report(new ClientSourceProgress("извлечение scripts.pkg", 0, 0));
            List<(string Entry, string Pyc)> jobs = ExtractPyc(pkg, pycRoot, ct);
            if (jobs.Count == 0)
            {
                throw new InvalidOperationException("в scripts.pkg нет ни одного .pyc из client, client_common, common: " + pkg);
            }

            Directory.CreateDirectory(staging);
            int done = 0, decompiled = 0, partial = 0, failed = 0;
            string? firstError = null;
            var failedModules = new System.Collections.Concurrent.ConcurrentBag<string>();
            await Parallel.ForEachAsync(
                jobs,
                new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = ct },
                async (job, token) =>
                {
                    DecompileOutcome outcome;
                    try
                    {
                        outcome = await _decompiler.DecompileAsync(job.Pyc, token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Один упавший файл — не повод бросать остальные восемь тысяч.
                        outcome = new DecompileOutcome(string.Empty, false, ex.Message);
                    }

                    if (outcome.HasText)
                    {
                        string dst = Path.Combine(staging, TreeRelative(job.Entry));
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        string text = outcome.Partial ? PartialHeader + "\n" + outcome.Text : outcome.Text;
                        await File.WriteAllTextAsync(dst, text, new UTF8Encoding(false), token).ConfigureAwait(false);
                        if (outcome.Partial)
                        {
                            Interlocked.Increment(ref partial);
                            Interlocked.CompareExchange(ref firstError, outcome.Error ?? "частично", null);
                        }
                        else
                        {
                            Interlocked.Increment(ref decompiled);
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                        Interlocked.CompareExchange(ref firstError, outcome.Error ?? "без объяснения", null);
                        failedModules.Add(ApiIndex.SourceFileEntry.ToDottedModule(job.Entry["scripts/".Length..^1]));
                    }

                    int n = Interlocked.Increment(ref done);
                    if (n % 50 == 0 || n == jobs.Count)
                    {
                        progress?.Report(new ClientSourceProgress("декомпиляция", n, jobs.Count));
                    }
                }).ConfigureAwait(false);

            // Настоящий клиент даёт тысячи полностью разобранных файлов. Ни одного — значит,
            // декомпилятор сломан или байткод ему не по зубам, и такое дерево не годится.
            if (decompiled == 0)
            {
                throw new InvalidOperationException(
                    $"декомпиляция не дала ни одного полностью разобранного файла (частично {partial}, не вышло {failed} " +
                    $"из {jobs.Count}): {firstError}");
            }

            progress?.Report(new ClientSourceProgress("модули дополнительных пакетов", jobs.Count, jobs.Count));
            IReadOnlyList<string> extra = ExtraModules(game);
            await File.WriteAllLinesAsync(
                Path.Combine(staging, ClientSourceTrees.ExtraModulesFile),
                new[] { "# Модули дополнительных .pkg клиента " + game.Version + " (кокпит, при извлечении исходников)." }.Concat(extra),
                new UTF8Encoding(false),
                ct).ConfigureAwait(false);

            // Не декомпилировавшиеся модули — поимённо: в игре они есть, и проверка совместимости
            // не должна выдавать их за удалённые.
            if (!failedModules.IsEmpty)
            {
                await File.WriteAllLinesAsync(
                    Path.Combine(staging, ClientSourceTrees.FailedModulesFile),
                    new[] { "# Модули клиента " + game.Version + ", которые uncompyle6 не разобрал (в игре есть, исходника нет)." }
                        .Concat(failedModules.Where(m => m.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal)),
                    new UTF8Encoding(false),
                    ct).ConfigureAwait(false);
            }

            ClientSourceTrees.WriteMarker(staging, new ClientSourceMarker
            {
                Version = game.Version,
                ExtractedUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Total = jobs.Count,
                Decompiled = decompiled,
                Partial = partial,
                Failed = failed,
            });

            result = new ClientSourceExtraction(target, game.Version, jobs.Count, decompiled, partial, failed, extra.Count, TimeSpan.Zero);
        }
        catch
        {
            TryDelete(staging);
            TryDelete(pycRoot);
            throw;
        }

        TryDelete(pycRoot);
        await MoveIntoPlaceAsync(staging, target).ConfigureAwait(false);
        return result with { Elapsed = watch.Elapsed };
    }

    /// <summary>
    /// Модули дополнительных пакетов (<c>res\packages\*.pkg</c>, кроме scripts.pkg): записи вида
    /// <c>battle_royale/scripts/client/X/Y.pyc</c> дают модуль <c>X.Y</c>.
    /// </summary>
    /// <param name="game">Установка.</param>
    /// <returns>Отсортированный список модулей.</returns>
    public static IReadOnlyList<string> ExtraModules(GameInstall game)
    {
        var modules = new SortedSet<string>(StringComparer.Ordinal);
        string dir = Path.Combine(game.RootPath, "res", "packages");
        if (!Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        foreach (string pkg in Directory.EnumerateFiles(dir, "*.pkg"))
        {
            if (string.Equals(Path.GetFileName(pkg), "scripts.pkg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using FileStream fs = new(pkg, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using ZipArchive zip = new(fs, ZipArchiveMode.Read);
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    string name = e.FullName.Replace('\\', '/');
                    if (!name.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string[] parts = name[..^4].Split('/');
                    int scripts = Array.IndexOf(parts, "scripts");
                    if (scripts < 0 || scripts + 2 >= parts.Length || !TreeRoots.Contains(parts[scripts + 1]))
                    {
                        continue;
                    }

                    string module = string.Join('.', parts.Skip(scripts + 2));
                    if (module.EndsWith(".__init__", StringComparison.Ordinal))
                    {
                        module = module[..^".__init__".Length];
                    }

                    if (module.Length > 0 && module != "__init__")
                    {
                        modules.Add(module);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // пакет не читается как zip (бывают и не-zip .pkg) — пропускаем
            }
        }

        return modules.ToArray();
    }

    /// <summary>
    /// Готовое дерево — на место. Несколько попыток: Windows не переименовывает каталог, пока в
    /// нём держит файл антивирус или индексатор, а они как раз проверяют восемь тысяч новых
    /// файлов. Не вышло — работа НЕ удаляется: дерево остаётся во временном каталоге, и его путь
    /// в ошибке.
    /// </summary>
    private static async Task MoveIntoPlaceAsync(string staging, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(staging, target);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= MoveRetryDelaysMs.Length)
                {
                    throw new IOException(
                        $"исходники готовы, но каталог не переименовать: {staging} → {target} ({ex.Message}). " +
                        "Закройте то, что держит файлы в нём, и переименуйте каталог вручную либо укажите его кокпиту как есть.",
                        ex);
                }

                await Task.Delay(MoveRetryDelaysMs[attempt]).ConfigureAwait(false);
            }
        }
    }

    private static List<(string Entry, string Pyc)> ExtractPyc(string pkg, string pycRoot, CancellationToken ct)
    {
        var jobs = new List<(string, string)>();
        using FileStream fs = new(pkg, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using ZipArchive zip = new(fs, ZipArchiveMode.Read);
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string name = e.FullName.Replace('\\', '/');
            if (!name.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase) || !name.StartsWith("scripts/", StringComparison.Ordinal))
            {
                continue;
            }

            string rest = name["scripts/".Length..];
            int slash = rest.IndexOf('/');
            if (slash <= 0 || !TreeRoots.Contains(rest[..slash]) || rest.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            // Имя копии — номер: путь к ней короткий и одинаковый для всех, где бы ни лежала цель.
            // Куда класть исходник, решает запись пакета (Entry), а не имя копии.
            string dst = Path.Combine(pycRoot, jobs.Count.ToString("D5", CultureInfo.InvariantCulture) + ".pyc");
            Directory.CreateDirectory(pycRoot);
            e.ExtractToFile(dst, overwrite: true);
            jobs.Add((name, dst));
        }

        return jobs;
    }

    /// <summary><c>scripts/client/X/Y.pyc</c> → <c>client\X\Y.py</c>.</summary>
    private static string TreeRelative(string entry) =>
        entry["scripts/".Length..^1].Replace('/', Path.DirectorySeparatorChar);

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // временный каталог — не повод терять настоящую ошибку
        }
    }
}
