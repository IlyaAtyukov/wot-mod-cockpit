using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Services;

/// <summary>
/// Реализация <see cref="IBuildRunner"/>: запускает <c>build.py</c> проекта отдельным процессом
/// и стримит его stdout/stderr. Логика тулчейна (zip, py27c, проверка синтаксиса, установка в игру)
/// на C# НЕ повторяется — всё делает python-скрипт.
/// <para>
/// Единственный способ, которым кокпит вообще что-то пишет в каталог игры, — это флаги
/// <c>--install</c>/<c>--dev</c>/<c>--uninstall</c>, переданные сюда по явному нажатию пользователя.
/// </para>
/// </summary>
public sealed class PythonBuildRunner : IBuildRunner
{
    /// <summary>
    /// Строка успеха из stdout build.py:
    /// <c>[build] OK   C:\...\dist\ru.local.hello_tanker_1.0.0.mtmod (62848 bytes, 10 entries, all Stored)</c>.
    /// Строка <c>[build] OK   python 2.7 syntax check passed (...)</c> сюда не подходит — нет <c>.mtmod</c>.
    /// </summary>
    private static readonly Regex ArtifactRegex = new(
        @"^\[build\]\s+OK\s+(?<path>.+?\.mtmod)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Параметры запуска по умолчанию для действия — то, что подставляется, когда вызывающий
    /// передал <c>options = null</c>.
    /// <para>
    /// <see cref="BuildAction.Build"/> и <see cref="BuildAction.BuildAndInstall"/> получают
    /// <c>--clean</c>: обе кладут результат в <c>dist\</c>, и мусор от прошлой версии там не нужен
    /// (иначе в <c>dist</c> копятся <c>*_1.0.0.mtmod</c>, <c>*_1.0.1.mtmod</c> и становится неясно,
    /// что именно поедет в игру). Явно переданный <see cref="BuildRunOptions"/> уважается как есть.
    /// </para>
    /// </summary>
    /// <param name="action">Действие.</param>
    /// <returns>Параметры по умолчанию.</returns>
    public static BuildRunOptions DefaultOptionsFor(BuildAction action) => action switch
    {
        BuildAction.Build or BuildAction.BuildAndInstall => new BuildRunOptions { Clean = true },
        _ => new BuildRunOptions()
    };

    /// <summary>
    /// Собирает список аргументов процесса (без имени интерпретатора).
    /// Публичный и статический, чтобы разбор командной строки можно было проверить тестом,
    /// не запуская ничего, что пишет в игру.
    /// </summary>
    /// <param name="action">Действие.</param>
    /// <param name="project">Проект.</param>
    /// <param name="options">Разовые параметры; <c>null</c> = <see cref="DefaultOptionsFor"/>.</param>
    /// <returns>Аргументы в том порядке, в каком они уйдут в процесс.</returns>
    public static IReadOnlyList<string> BuildArgumentList(
        BuildAction action, ModProject project, BuildRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        BuildRunOptions o = options ?? DefaultOptionsFor(action);
        List<string> args = [project.BuildScriptPath];

        args.Add("--src");
        args.Add(project.SrcDir);
        args.Add("--dist");
        args.Add(project.DistDir);

        // Приоритет: разовое перекрытие → каталог, вычисленный тем, кто открыл проект (проект или
        // настройки кокпита) → каталог из wotmod.json.
        string gameDir = o.GameDirOverride
                         ?? (string.IsNullOrWhiteSpace(project.EffectiveGameDir) ? project.GameDir : project.EffectiveGameDir);
        if (!string.IsNullOrWhiteSpace(gameDir))
        {
            args.Add("--game-dir");
            args.Add(gameDir);
        }

        string? actionFlag = action.ToBuildFlag();
        if (actionFlag is not null)
        {
            args.Add(actionFlag);
        }

        if (o.Clean)
        {
            args.Add("--clean");
        }

        if (o.NoSyntaxCheck)
        {
            args.Add("--no-syntax-check");
        }

        if (o.CompileAll)
        {
            args.Add("--compile-all");
        }

        if (o.KeepPy)
        {
            args.Add("--keep-py");
        }

        // --list как основное действие уже добавлен выше; второй раз не дублируем.
        if (o.PrintListing && action != BuildAction.List)
        {
            args.Add("--list");
        }

        if (!string.IsNullOrWhiteSpace(o.Python27Path))
        {
            args.Add("--py27");
            args.Add(o.Python27Path);
        }

        foreach (string extra in project.ExtraBuildArgs)
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                args.Add(extra);
            }
        }

        foreach (string extra in o.AdditionalArgs)
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                args.Add(extra);
            }
        }

        return args;
    }

    /// <summary>
    /// Чем запускать build.py для проекта: <see cref="ModProject.EffectivePythonExe"/>, если его
    /// вычислил тот, кто открыл проект; иначе <see cref="ModProject.PythonExe"/>; иначе <c>python</c>.
    /// Сам раннер интерпретатор не ищет — поиск запускает процессы и делается заранее
    /// (<see cref="PythonLocator.ResolveForBuild"/>).
    /// </summary>
    /// <param name="project">Проект.</param>
    /// <returns>Команда или путь к интерпретатору.</returns>
    public static string PythonCommandFor(ModProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (!string.IsNullOrWhiteSpace(project.EffectivePythonExe)) return project.EffectivePythonExe.Trim();
        if (!string.IsNullOrWhiteSpace(project.PythonExe)) return project.PythonExe.Trim();
        return PythonLocator.LegacyDefaultCommand;
    }

    /// <summary>
    /// Командная строка целиком, для эха в панель сборки и кнопки «скопировать команду».
    /// Кавычки ставятся только там, где они нужны, — так строку можно вставить в консоль как есть.
    /// </summary>
    /// <param name="pythonExe">Команда python.</param>
    /// <param name="args">Аргументы.</param>
    /// <returns>Готовая строка.</returns>
    public static string BuildCommandLine(string pythonExe, IReadOnlyList<string> args)
    {
        StringBuilder sb = new();
        sb.Append(Quote(pythonExe));
        foreach (string a in args)
        {
            sb.Append(' ').Append(Quote(a));
        }

        return sb.ToString();

        static string Quote(string s) =>
            s.Length == 0 || s.AsSpan().IndexOfAny(' ', '\t', '"') >= 0
                ? '"' + s.Replace("\"", "\\\"") + '"'
                : s;
    }

    /// <summary>Извлекает путь артефакта из строки stdout build.py.</summary>
    /// <param name="line">Строка вывода.</param>
    /// <returns>Абсолютный путь к <c>.mtmod</c> либо <c>null</c>.</returns>
    public static string? TryParseArtifactPath(string line)
    {
        Match m = ArtifactRegex.Match(line);
        return m.Success ? m.Groups["path"].Value.Trim() : null;
    }

    /// <inheritdoc />
    public IBuildRun Start(BuildAction action, ModProject project, BuildRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new PythonBuildRun(action, project, options ?? DefaultOptionsFor(action));
    }

    /// <inheritdoc />
    public async Task<BuildResult> RunAsync(
        BuildAction action,
        ModProject project,
        BuildRunOptions? options,
        IProgress<BuildOutputLine>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        await using IBuildRun run = Start(action, project, options);

        await foreach (BuildOutputLine line in run.ReadOutputAsync(ct).ConfigureAwait(false))
        {
            progress?.Report(line);
        }

        return await run.Completion.ConfigureAwait(false);
    }
}

/// <summary>
/// Один запущенный процесс <c>build.py</c>: живой поток строк плюс итог.
/// stdout и stderr сливаются в один <see cref="Channel{T}"/> в порядке поступления —
/// это же снимает классический дедлок «оба потока перенаправлены, читаем по очереди».
/// </summary>
internal sealed class PythonBuildRun : IBuildRun
{
    private readonly Channel<BuildOutputLine> _channel =
        Channel.CreateUnbounded<BuildOutputLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly TaskCompletionSource<BuildResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Взводится, когда оба перенаправленных потока процесса отдали свой <c>null</c> (EOF).</summary>
    private readonly TaskCompletionSource _streamsDone =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Process? _process;
    private readonly CancellationTokenSource? _timeoutCts;
    private readonly List<string> _stdoutLines = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();

    private int _streamsClosed;
    private int _consumed;
    private int _cancelled;
    private bool _disposed;
    private string? _failureReason;

    internal PythonBuildRun(BuildAction action, ModProject project, BuildRunOptions options)
    {
        StartedUtc = DateTimeOffset.UtcNow;

        IReadOnlyList<string> args = PythonBuildRunner.BuildArgumentList(action, project, options);
        string python = PythonBuildRunner.PythonCommandFor(project);
        CommandLine = PythonBuildRunner.BuildCommandLine(python, args);

        _channel.Writer.TryWrite(BuildOutputLine.Note("$ " + CommandLine));

        if (!File.Exists(project.BuildScriptPath))
        {
            FailToLaunch($"не найден сборочный скрипт: {project.BuildScriptPath}");
            return;
        }

        ProcessStartInfo psi = new()
        {
            FileName = python,
            WorkingDirectory = Directory.Exists(project.ProjectDir)
                ? project.ProjectDir
                : Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        // Без этого python на Windows пишет в перенаправленный поток в cp1251 и кириллица бьётся.
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        Process proc = new() { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => OnData(e.Data, BuildStreamKind.Stdout);
        proc.ErrorDataReceived += (_, e) => OnData(e.Data, BuildStreamKind.Stderr);

        try
        {
            proc.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException or PlatformNotSupportedException)
        {
            proc.Dispose();
            FailToLaunch(FirstLine(ex.Message));
            return;
        }

        _process = proc;
        _clock.Restart();

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.StandardInput.Close();

        if (options.Timeout > TimeSpan.Zero && options.Timeout != Timeout.InfiniteTimeSpan)
        {
            _timeoutCts = new CancellationTokenSource(options.Timeout);
            _timeoutCts.Token.Register(() => CancelWithReason($"таймаут {options.Timeout.TotalSeconds:0} с"));
        }

        _ = Task.Run(WaitForCompletionAsync);
    }

    /// <inheritdoc />
    public string CommandLine { get; }

    /// <inheritdoc />
    public DateTimeOffset StartedUtc { get; }

    /// <inheritdoc />
    public Task<BuildResult> Completion => _completion.Task;

    /// <inheritdoc />
    public async IAsyncEnumerable<BuildOutputLine> ReadOutputAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
        {
            throw new InvalidOperationException(
                "IBuildRun.ReadOutputAsync можно перечислить только один раз: это живой канал, а не буфер.");
        }

        // Отмена не роняет перечисление исключением: она убивает процесс, канал закрывается
        // сам, а итог уезжает в Completion как ExitCodeCancelled.
        using CancellationTokenRegistration registration =
            ct.Register(() => CancelWithReason("отменено пользователем"));

        ChannelReader<BuildOutputLine> reader = _channel.Reader;
        while (true)
        {
            bool hasMore;
            try
            {
                hasMore = await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                hasMore = false;
            }

            if (!hasMore)
            {
                yield break;
            }

            if (reader.TryRead(out BuildOutputLine? line) && line is not null)
            {
                yield return line;
            }
        }
    }

    /// <inheritdoc />
    public void Cancel() => CancelWithReason("отменено пользователем");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Cancel();

        try
        {
            await _completion.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // процесс не умер за 10 с — дальше держать вызывающего смысла нет
        }

        _timeoutCts?.Dispose();
        _process?.Dispose();
    }

    // ------------------------------------------------------------------ внутреннее

    /// <summary>Приёмник строк обоих потоков процесса. <c>null</c> = поток закрыт.</summary>
    /// <param name="data">Строка без перевода строки либо <c>null</c>.</param>
    /// <param name="kind">Из какого потока пришло.</param>
    private void OnData(string? data, BuildStreamKind kind)
    {
        if (data is null)
        {
            // Оба потока закрылись — ждать больше нечего.
            if (Interlocked.Increment(ref _streamsClosed) >= 2)
            {
                _streamsDone.TrySetResult();
            }

            return;
        }

        if (kind == BuildStreamKind.Stdout)
        {
            lock (_gate)
            {
                _stdoutLines.Add(data);
            }
        }

        _channel.Writer.TryWrite(new BuildOutputLine(kind, data, DateTimeOffset.UtcNow));
    }

    /// <summary>Дожидается конца процесса и закрывает канал итогом.</summary>
    private async Task WaitForCompletionAsync()
    {
        Process proc = _process!;

        try
        {
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // процесс уже исчез — дальше по общей ветке
        }

        // Асинхронные события чтения могут дописать пару строк уже после выхода процесса.
        await Task.WhenAny(_streamsDone.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);

        _clock.Stop();
        TimeSpan elapsed = _clock.Elapsed;

        int exitCode;
        try
        {
            exitCode = proc.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = BuildResult.ExitCodeLaunchFailed;
        }

        bool cancelled = Volatile.Read(ref _cancelled) != 0;
        string artifact = cancelled ? string.Empty : FindArtifactPath();

        BuildResult result;
        if (cancelled)
        {
            _channel.Writer.TryWrite(BuildOutputLine.Note(
                $"[кокпит] сборка прервана ({_failureReason ?? "отменено пользователем"}), {elapsed.TotalSeconds:0.0} с"));

            result = BuildResult.Cancelled(CommandLine, elapsed) with
            {
                FailureReason = _failureReason ?? "отменено пользователем"
            };
        }
        else
        {
            _channel.Writer.TryWrite(BuildOutputLine.Note(
                $"[кокпит] процесс завершился с кодом {exitCode} за {elapsed.TotalSeconds:0.0} с"));

            result = new BuildResult(exitCode == 0, exitCode, artifact, elapsed)
            {
                CommandLine = CommandLine,
                FailureReason = exitCode == 0 ? null : $"build.py завершился с кодом {exitCode}"
            };
        }

        _channel.Writer.TryComplete();
        _completion.TrySetResult(result);
    }

    /// <summary>Последний путь <c>.mtmod</c>, напечатанный build.py в stdout.</summary>
    /// <returns>Путь либо пустая строка.</returns>
    private string FindArtifactPath()
    {
        lock (_gate)
        {
            for (int i = _stdoutLines.Count - 1; i >= 0; i--)
            {
                string? path = PythonBuildRunner.TryParseArtifactPath(_stdoutLines[i]);
                if (path is not null)
                {
                    return path;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>Убивает дерево процессов. Идемпотентно.</summary>
    /// <param name="reason">Причина для <see cref="BuildResult.FailureReason"/>.</param>
    private void CancelWithReason(string reason)
    {
        if (Interlocked.Exchange(ref _cancelled, 1) != 0)
        {
            return;
        }

        _failureReason = reason;

        Process? proc = _process;
        if (proc is null)
        {
            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // процесс уже мёртв — это не ошибка
        }
    }

    /// <summary>Процесса не будет: сразу отдаём готовый провал запуска.</summary>
    /// <param name="reason">Человекочитаемая причина.</param>
    private void FailToLaunch(string reason)
    {
        _channel.Writer.TryWrite(BuildOutputLine.Note("[кокпит] не удалось запустить: " + reason));
        _channel.Writer.TryComplete();
        _streamsDone.TrySetResult();
        _completion.TrySetResult(BuildResult.LaunchFailed(CommandLine, reason));
    }

    /// <summary>Первая строка сообщения исключения — остальное в UI не помещается.</summary>
    /// <param name="message">Сообщение.</param>
    /// <returns>Первая строка.</returns>
    private static string FirstLine(string message)
    {
        int nl = message.IndexOfAny(['\r', '\n']);
        return nl < 0 ? message : message[..nl];
    }
}
