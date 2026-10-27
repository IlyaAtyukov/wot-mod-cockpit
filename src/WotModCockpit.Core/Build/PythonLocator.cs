using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using WotModCockpit.Core.Abstractions;

namespace WotModCockpit.Core.Services;

/// <summary>
/// Найденный интерпретатор Python.
/// </summary>
/// <param name="ExecutablePath">
/// Абсолютный путь к <c>python.exe</c> (или к тому, что реально запускается на этой ОС).
/// Именно это значение годится для <c>ProcessStartInfo.FileName</c>.
/// </param>
/// <param name="Version">
/// Версия в том виде, в каком её напечатал сам интерпретатор: <c>3.12.10</c>.
/// Пустая строка, если версию выяснить не удалось (например, кандидат взят из реестра и не запускался).
/// </param>
/// <param name="Source">
/// Откуда взялся кандидат: <c>PATH</c>, <c>py -0p</c>, <c>registry</c>, <c>well-known</c>, <c>explicit</c>.
/// Нужно только для показа в UI и диагностики.
/// </param>
public sealed record PythonInstall(string ExecutablePath, string Version, string Source)
{
    /// <summary>Мажорная версия (<c>3</c>) либо 0, если версия неизвестна.</summary>
    public int Major => ParsePart(0);

    /// <summary>Минорная версия (<c>12</c>) либо 0.</summary>
    public int Minor => ParsePart(1);

    /// <summary>true, если это Python 3.x.</summary>
    public bool IsPython3 => Major == 3;

    /// <summary>
    /// Младшая минорная версия Python 3, на которой работает сборка шаблона: компилятор
    /// <c>tools/py27c.py</c> обращается к <c>ast.MatchValue</c>, а он есть только с 3.10.
    /// </summary>
    public const int MinimumBuildMinor = 10;

    /// <summary>
    /// true, если это Python 3.10 или новее — такой годится для <c>build.py</c> шаблона.
    /// На 3.8–3.9 <c>py27c</c> не импортируется, и сборка падает.
    /// </summary>
    public bool IsSupportedForBuild => IsPython3 && Minor >= MinimumBuildMinor;

    private int ParsePart(int index)
    {
        string[] parts = Version.Split('.');
        return parts.Length > index && int.TryParse(parts[index], out int v) ? v : 0;
    }
}

/// <summary>
/// Поиск интерпретатора Python 3.x на машине разработчика: PATH → лаунчер <c>py.exe -0p</c>
/// (он же читает <c>HKCU/HKLM\SOFTWARE\Python\PythonCore</c>) → прямой запрос реестра через
/// <c>reg.exe</c> → типовые каталоги установки.
/// <para>
/// Python 2.7 здесь НЕ ищется и не нужен: байткод 2.7 делает <c>tools/py27c.py</c> из-под 3.x.
/// </para>
/// <para>
/// Класс статический и кэширующий: <see cref="IProjectStore.Validate"/> дёргает его на каждую проверку проекта,
/// а каждый запуск <c>python --version</c> стоит десятки миллисекунд.
/// Сбросить кэш можно через <see cref="ClearCache"/>.
/// </para>
/// <para>
/// Реестр читается через <c>reg.exe</c>, а не через <c>Microsoft.Win32.Registry</c>: это запасной
/// путь третьего уровня, лаунчер <c>py.exe</c> и так возвращает всё, что записано в реестре, и
/// переписывать работающий разбор ради него смысла нет. (Сам API реестра в плоском <c>net8.0</c>
/// доступен за проверкой <c>OperatingSystem.IsWindows()</c> — им пользуется <see cref="GameDirectoryFinder"/>.)
/// </para>
/// </summary>
public static class PythonLocator
{
    /// <summary>Сколько ждать ответа от <c>python --version</c> и <c>py -0p</c>.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Версия из вывода <c>Python 3.12.10</c> либо просто <c>3.12.10</c>.</summary>
    private static readonly Regex VersionRegex = new(
        @"(?<v>\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Строка вывода <c>py -0p</c>: <c> -V:3.12 *        C:\...\python.exe</c>.</summary>
    private static readonly Regex PyLauncherRegex = new(
        @"^\s*-V:(?<ver>[\d.]+)[^\s]*\s+\*?\s*(?<path>\S.*\S)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Кэш «команда → результат». Ключ пустой строки — поиск без явного кандидата.</summary>
    private static readonly ConcurrentDictionary<string, PythonInstall?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Кэш полного перечисления найденных интерпретаторов.</summary>
    private static IReadOnlyList<PythonInstall>? _all;

    /// <summary>
    /// Проверяет конкретную команду/путь и, если это рабочий Python, возвращает его описание.
    /// </summary>
    /// <param name="command">
    /// Команда (<c>python</c>, <c>python3</c>, <c>py</c>) либо абсолютный путь к <c>python.exe</c>.
    /// Пустая строка или <c>null</c> = взять первый найденный автоматически (<see cref="Locate"/>).
    /// </param>
    /// <returns>Описание интерпретатора либо <c>null</c>, если запустить его не удалось.</returns>
    public static PythonInstall? Resolve(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return Locate();
        }

        return Cache.GetOrAdd(command.Trim(), static cmd =>
        {
            string? version = QueryVersion(cmd);
            if (version is null)
            {
                return null;
            }

            string resolved = ResolveExecutablePath(cmd) ?? cmd;
            return new PythonInstall(resolved, version, "explicit");
        });
    }

    /// <summary>
    /// Устаревшее значение по умолчанию: раньше кокпит записывал его и в <c>settings.json</c>,
    /// и в каждый созданный <c>wotmod.json</c>. Оно не выбор пользователя, а «как получится»,
    /// поэтому для сборки трактуется так же, как пустое значение.
    /// </summary>
    public const string LegacyDefaultCommand = "python";

    /// <summary>
    /// Чем запускать build.py: одно правило для сборки, проверки проекта и строки состояния.
    /// <list type="number">
    ///   <item>Python проекта (<c>pythonExe</c> в <c>wotmod.json</c>), если он задан и это не
    ///         устаревший литерал <see cref="LegacyDefaultCommand"/>, — как есть, даже если он не
    ///         запускается: явный выбор не подменяется молча, сборка честно скажет, что не так;</item>
    ///   <item>Python из настроек кокпита — на тех же условиях;</item>
    ///   <item><c>python</c>, если он запускается и это настоящий Python 3.10+ (а не заглушка
    ///         Microsoft Store и не 2.7, который часто стоит у модмейкеров первым в PATH);</item>
    ///   <item>иначе — первый найденный автоматически (<see cref="Locate"/>: лаунчер <c>py -0p</c>,
    ///         реестр, типовые каталоги), абсолютным путём.</item>
    /// </list>
    /// <para>Запускает процессы (<c>python --version</c>, <c>py -0p</c>) — не для UI-потока.
    /// Результаты кэшируются вместе с остальным поиском.</para>
    /// </summary>
    /// <param name="projectPython">Значение из <c>wotmod.json</c>; пусто — не задано.</param>
    /// <param name="settingsPython">Значение из настроек кокпита; пусто — не задано.</param>
    /// <returns>Команда или путь для запуска либо <c>null</c>, если Python 3.x на машине не нашёлся.</returns>
    public static string? ResolveForBuild(string? projectPython, string? settingsPython)
    {
        if (IsExplicit(projectPython))
        {
            return projectPython!.Trim();
        }

        if (IsExplicit(settingsPython))
        {
            return settingsPython!.Trim();
        }

        if (Resolve(LegacyDefaultCommand) is { IsSupportedForBuild: true })
        {
            return LegacyDefaultCommand;
        }

        return Locate()?.ExecutablePath;

        static bool IsExplicit(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value.Trim(), LegacyDefaultCommand, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Находит «лучший» Python 3.x: сначала то, что лежит в PATH под именем <c>python</c>,
    /// иначе самый свежий из найденных. Годный для сборки 3.10+
    /// (<see cref="PythonInstall.IsSupportedForBuild"/>) предпочитается более старому 3.x;
    /// Python 2.x не возвращается никогда — даже если другого на машине нет.
    /// </summary>
    /// <returns>Интерпретатор либо <c>null</c>, если на машине не нашлось ни одного Python 3.x.</returns>
    public static PythonInstall? Locate() =>
        Cache.GetOrAdd(string.Empty, static _ =>
        {
            IReadOnlyList<PythonInstall> all = LocateAll();
            return all.FirstOrDefault(static i => i.Source == "PATH" && i.IsSupportedForBuild)
                   ?? all.FirstOrDefault(static i => i.IsSupportedForBuild)
                   ?? all.FirstOrDefault(static i => i.Source == "PATH" && i.IsPython3)
                   ?? all.FirstOrDefault(static i => i.IsPython3);
        });

    /// <summary>
    /// Перечисляет все найденные интерпретаторы: PATH, лаунчер <c>py</c>, реестр, типовые каталоги.
    /// Дубликаты по пути схлопываются, порядок — по убыванию версии внутри каждого источника.
    /// </summary>
    /// <returns>Список найденного; пустой, если ничего не нашлось.</returns>
    public static IReadOnlyList<PythonInstall> LocateAll()
    {
        if (_all is not null)
        {
            return _all;
        }

        List<PythonInstall> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void Add(string exe, string version, string source)
        {
            string full;
            try
            {
                full = Path.GetFullPath(exe);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            if (!seen.Add(full))
            {
                return;
            }

            result.Add(new PythonInstall(full, version, source));
        }

        // 1) PATH: то, что реально запустится по короткому имени.
        foreach (string cmd in new[] { "python", "python3" })
        {
            string? version = QueryVersion(cmd);
            if (version is null)
            {
                continue;
            }

            string? exe = ResolveExecutablePath(cmd);
            if (exe is not null)
            {
                Add(exe, version, "PATH");
            }
        }

        // 2) py.exe -0p — официальный лаунчер, перечисляет всё, что записано в реестре.
        foreach ((string exe, string ver) in QueryPyLauncher())
        {
            if (File.Exists(exe))
            {
                Add(exe, QueryVersion(exe) ?? ver, "py -0p");
            }
        }

        // 3) Реестр напрямую (на случай, если py.exe не установлен).
        foreach (string exe in QueryRegistryInstallPaths())
        {
            if (File.Exists(exe))
            {
                Add(exe, QueryVersion(exe) ?? string.Empty, "registry");
            }
        }

        // 4) Типовые каталоги установки.
        foreach (string exe in EnumerateWellKnownPaths())
        {
            if (File.Exists(exe))
            {
                Add(exe, QueryVersion(exe) ?? string.Empty, "well-known");
            }
        }

        _all = result;
        return result;
    }

    /// <summary>Сбрасывает кэш поиска. Нужен настройкам («перепроверить») и тестам.</summary>
    public static void ClearCache()
    {
        Cache.Clear();
        _all = null;
    }

    // ------------------------------------------------------------------ внутреннее

    /// <summary>
    /// Запускает <c>&lt;cmd&gt; --version</c> и вытаскивает номер версии.
    /// Python 3.4+ печатает версию в stdout, старые — в stderr, поэтому читаются оба потока.
    /// </summary>
    /// <param name="command">Команда или путь.</param>
    /// <returns>Версия вида <c>3.12.10</c> либо <c>null</c>.</returns>
    private static string? QueryVersion(string command)
    {
        (int code, string stdout, string stderr) = RunCaptured(command, ["--version"]);
        if (code != 0)
        {
            return null;
        }

        string text = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;

        // Заглушка из Microsoft Store печатает подсказку об установке и возвращает 0 — версии там нет.
        if (!text.Contains("Python", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Match m = VersionRegex.Match(text);
        return m.Success ? m.Groups["v"].Value : null;
    }

    /// <summary>
    /// Узнаёт настоящий путь исполняемого файла, спросив об этом сам интерпретатор
    /// (<c>-c "import sys; print(sys.executable)"</c>). Это надёжнее разбора <c>PATH</c>
    /// и заодно отсеивает WindowsApps-заглушку.
    /// </summary>
    /// <param name="command">Команда или путь.</param>
    /// <returns>Абсолютный путь либо <c>null</c>.</returns>
    private static string? ResolveExecutablePath(string command)
    {
        (int code, string stdout, _) = RunCaptured(command, ["-c", "import sys; print(sys.executable)"]);
        if (code != 0)
        {
            return null;
        }

        string path = stdout.Trim();
        return path.Length > 0 && File.Exists(path) ? path : null;
    }

    /// <summary>Разбирает вывод <c>py -0p</c>.</summary>
    /// <returns>Пары «путь к python.exe, версия».</returns>
    private static IEnumerable<(string Exe, string Version)> QueryPyLauncher()
    {
        (int code, string stdout, string stderr) = RunCaptured("py", ["-0p"]);
        if (code != 0)
        {
            yield break;
        }

        foreach (string line in (stdout + "\n" + stderr).Split('\n'))
        {
            Match m = PyLauncherRegex.Match(line.TrimEnd('\r'));
            if (m.Success)
            {
                yield return (m.Groups["path"].Value, m.Groups["ver"].Value);
            }
        }
    }

    /// <summary>
    /// Читает <c>HKLM/HKCU\SOFTWARE\Python\PythonCore\*\InstallPath</c> через <c>reg.exe</c>
    /// (см. пояснение в шапке класса о том, почему не <c>Microsoft.Win32.Registry</c>).
    /// </summary>
    /// <returns>Кандидаты в <c>python.exe</c>.</returns>
    private static IEnumerable<string> QueryRegistryInstallPaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (string hive in new[] { "HKLM", "HKCU" })
        {
            (int code, string stdout, _) = RunCaptured(
                "reg", ["query", $@"{hive}\SOFTWARE\Python\PythonCore", "/s", "/v", "ExecutablePath"]);

            if (code != 0)
            {
                continue;
            }

            foreach (string raw in stdout.Split('\n'))
            {
                string line = raw.Trim();
                int idx = line.IndexOf("REG_SZ", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    continue;
                }

                string value = line[(idx + "REG_SZ".Length)..].Trim();
                if (value.Length > 0)
                {
                    yield return value;
                }
            }
        }
    }

    /// <summary>Типовые каталоги установки Python на Windows.</summary>
    /// <returns>Кандидаты в <c>python.exe</c>.</returns>
    private static IEnumerable<string> EnumerateWellKnownPaths()
    {
        List<string> roots = [];

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (localAppData.Length > 0)
        {
            roots.Add(Path.Combine(localAppData, "Programs", "Python"));
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length > 0)
        {
            roots.Add(programFiles);
        }

        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (programFilesX86.Length > 0)
        {
            roots.Add(programFilesX86);
        }

        roots.Add(@"C:\");

        foreach (string root in roots)
        {
            string[] dirs;
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                dirs = Directory.GetDirectories(root, "Python3*");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Свежие версии первыми: Python313 раньше Python39.
            Array.Sort(dirs, static (a, b) => string.Compare(b, a, StringComparison.OrdinalIgnoreCase));

            foreach (string d in dirs)
            {
                yield return Path.Combine(d, "python.exe");
            }
        }
    }

    /// <summary>
    /// Запускает процесс, дожидается конца и возвращает оба потока.
    /// Любой отказ запуска — это <c>(-1, "", "")</c>, а не исключение.
    /// </summary>
    /// <param name="fileName">Исполняемый файл или команда.</param>
    /// <param name="args">Аргументы.</param>
    /// <returns>Код возврата, stdout, stderr.</returns>
    private static (int ExitCode, string Stdout, string Stderr) RunCaptured(string fileName, string[] args)
    {
        ProcessStartInfo psi = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };

        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using Process? proc = Process.Start(psi);
            if (proc is null)
            {
                return (-1, string.Empty, string.Empty);
            }

            proc.StandardInput.Close();

            Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> errTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // уже мёртв
                }

                return (-1, string.Empty, string.Empty);
            }

            return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException or PlatformNotSupportedException)
        {
            return (-1, string.Empty, string.Empty);
        }
    }
}
