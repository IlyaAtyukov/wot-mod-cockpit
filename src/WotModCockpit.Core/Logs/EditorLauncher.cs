using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using WotModCockpit.Core.Abstractions;

namespace WotModCockpit.Core.Services;

/// <summary>
/// Открытие файла на нужной строке во внешнем редакторе (VS Code). Реализация <see cref="IEditorLauncher"/>.
/// <para>
/// Поиск редактора идёт лениво и кэшируется, в таком порядке:
/// </para>
/// <list type="number">
///   <item>явно заданный путь (<see cref="Configure"/> — из <c>CockpitSettings.EditorExe</c>);</item>
///   <item>переменная окружения <c>VSCODE_EXE</c>;</item>
///   <item>каталоги из <c>PATH</c>: <c>Code.exe</c>, <c>code.cmd</c>, <c>code.exe</c>, <c>code</c>.
///         Обычно срабатывает именно этот шаг: установщик VS Code кладёт в PATH свой
///         <c>&lt;папка VS Code&gt;\bin</c>, где есть <c>code</c> и <c>code.cmd</c>; туда же
///         попадают обёртки scoop и подобных менеджеров;</item>
///   <item>типовые места установки (<c>%LOCALAPPDATA%\Programs\Microsoft VS Code</c>,
///         <c>%ProgramFiles%\Microsoft VS Code</c>, <c>%ProgramFiles(x86)%</c>). Установку
///         в нестандартную папку указывают явно — полем <c>editorExe</c> или <c>VSCODE_EXE</c>.</item>
/// </list>
/// <para>
/// Тонкость с <c>bin\code</c>: это .cmd-обёртка, её нельзя запускать с <c>UseShellExecute = false</c>
/// как обычный .exe. Поэтому, найдя <c>bin\code[.cmd]</c>, реализация поднимается на уровень выше и
/// предпочитает соседний <c>Code.exe</c>; если его нет — запускает обёртку через <c>cmd /c</c>.
/// </para>
/// <para>
/// Метод <see cref="OpenAt"/> дёргается из обработчика двойного клика по строке лога, поэтому
/// бытовые неудачи (нет редактора, нет файла, процесс не стартовал) НЕ бросают исключений,
/// а уезжают в событие <see cref="LaunchFailed"/>.
/// </para>
/// </summary>
public sealed class VsCodeEditorLauncher : IEditorLauncher
{
    private const string DefaultArgsTemplate = "--goto \"{file}:{line}\"";

    private readonly object _gate = new();
    private string? _configuredExe;
    private string _argsTemplate = DefaultArgsTemplate;
    private bool _resolved;
    private ResolvedEditor? _editor;

    /// <summary>Создаёт лаунчер с автопоиском редактора. Используется контейнером DI.</summary>
    public VsCodeEditorLauncher()
    {
    }

    /// <summary>Создаёт лаунчер с явно заданным редактором.</summary>
    /// <param name="editorExe">Путь к исполняемому файлу редактора; пусто = автопоиск.</param>
    /// <param name="argsTemplate">Шаблон аргументов с подстановками <c>{file}</c> и <c>{line}</c>.</param>
    public VsCodeEditorLauncher(string? editorExe, string? argsTemplate)
        => Configure(editorExe, argsTemplate);

    /// <inheritdoc />
    public bool IsAvailable => Resolve() is not null;

    /// <inheritdoc />
    public event EventHandler<string>? LaunchFailed;

    /// <summary>
    /// Путь к тому, что реально будет запущено (для <c>bin\code.cmd</c> это соседний <c>Code.exe</c>),
    /// либо <c>null</c>, если редактор не найден. Диагностика для окна настроек.
    /// </summary>
    public string? ResolvedPath => Resolve()?.ExePath;

    /// <summary>Путь, по которому редактор был НАЙДЕН (может отличаться от <see cref="ResolvedPath"/>).</summary>
    public string? DiscoveredPath => Resolve()?.DisplayPath;

    /// <summary>
    /// Задаёт редактор и шаблон аргументов из <c>CockpitSettings</c> и сбрасывает кэш поиска.
    /// Отдельный метод, а не конструктор с <c>ISettingsStore</c>: лаунчер живёт синглтоном
    /// и не должен дёргать диск при построении контейнера.
    /// </summary>
    /// <param name="editorExe">Путь к <c>Code.exe</c>/<c>code.cmd</c>; пусто = автопоиск.</param>
    /// <param name="argsTemplate">Шаблон аргументов; пусто = <c>--goto "{file}:{line}"</c>.</param>
    public void Configure(string? editorExe, string? argsTemplate)
    {
        lock (_gate)
        {
            _configuredExe = string.IsNullOrWhiteSpace(editorExe) ? null : editorExe.Trim();
            _argsTemplate = string.IsNullOrWhiteSpace(argsTemplate) ? DefaultArgsTemplate : argsTemplate.Trim();
            _resolved = false;
            _editor = null;
        }
    }

    /// <inheritdoc />
    public void OpenAt(string filePath, int line)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            OnLaunchFailed("Не задан путь к файлу.");
            return;
        }

        if (line <= 0)
            line = 1;

        string full;
        try
        {
            full = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            OnLaunchFailed($"Некорректный путь: {filePath}");
            return;
        }

        if (!File.Exists(full))
        {
            OnLaunchFailed($"Файл не найден: {full}");
            return;
        }

        ResolvedEditor? editor = Resolve();
        if (editor is null)
        {
            // ФОЛБЭК НЕ ЗАПУСКАЕТ ФАЙЛ. Кадры traceback — это .py мода и декомпилированного
            // клиента, а .py на машине с установленным Python ассоциирован с интерпретатором:
            // «открыть» такой файл ассоциацией ОС значит ВЫПОЛНИТЬ чужой исходник по клику
            // в ленте лога. Показываем его в проводнике и честно говорим, чего не хватает.
            RevealInExplorer(full);
            OnLaunchFailed(
                $"VS Code не найден — файл только показан в проводнике: {full} (строка {line}). "
                + "Укажите путь к редактору в settings.json (editorExe).");

            return;
        }

        string args = _argsTemplate
            .Replace("{file}", full, StringComparison.Ordinal)
            .Replace("{line}", line.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        TryStart(editor, args, $"Не удалось открыть {Path.GetFileName(full)}:{line} в редакторе");
    }

    /// <inheritdoc />
    public void OpenFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            OnLaunchFailed("Не задан путь к каталогу.");
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(folderPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            OnLaunchFailed($"Некорректный путь: {folderPath}");
            return;
        }

        if (!Directory.Exists(full))
        {
            OnLaunchFailed($"Каталог не найден: {full}");
            return;
        }

        ResolvedEditor? editor = Resolve();
        if (editor is null)
        {
            if (!TryShellOpen(full))
                OnLaunchFailed("VS Code не найден. Укажите путь к редактору в настройках кокпита.");

            return;
        }

        TryStart(editor, $"\"{full}\"", $"Не удалось открыть каталог {full} в редакторе");
    }

    // ---------------------------------------------------------------- запуск

    private void TryStart(ResolvedEditor editor, string args, string failurePrefix)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory
        };

        if (editor.NeedsCmdShell)
        {
            // .cmd-обёртку напрямую как .exe запускать нельзя.
            psi.FileName = "cmd.exe";
            psi.Arguments = $"/c \"\"{editor.ExePath}\" {args}\"";
        }
        else
        {
            psi.FileName = editor.ExePath;
            psi.Arguments = args;
        }

        try
        {
            using Process? p = Process.Start(psi);
            if (p is null)
                OnLaunchFailed($"{failurePrefix}: процесс не запустился.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                       or ObjectDisposedException or PlatformNotSupportedException)
        {
            OnLaunchFailed($"{failurePrefix}: {ex.Message}");
        }
    }

    /// <summary>
    /// Показывает файл в проводнике, выделив его. Именно ПОКАЗЫВАЕТ, а не открывает:
    /// запускается всегда <c>explorer.exe</c>, ассоциация расширения не задействуется.
    /// </summary>
    /// <param name="full">Абсолютный путь к существующему файлу.</param>
    private static void RevealInExplorer(string full)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                       or ObjectDisposedException or PlatformNotSupportedException)
        {
            // Проводник — необязательная любезность; о самой проблеме уже сообщено отдельно.
        }
    }

    /// <summary>
    /// Открывает путь ассоциацией ОС. Допустимо ТОЛЬКО для каталогов
    /// (<see cref="OpenFolder"/>): каталог ассоциация всегда открывает проводником,
    /// выполнить его нельзя. Для файлов см. <see cref="RevealInExplorer"/>.
    /// </summary>
    /// <param name="path">Путь к каталогу.</param>
    /// <returns>Удалось ли запустить.</returns>
    private bool TryShellOpen(string path)
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return p is not null || File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                       or ObjectDisposedException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private void OnLaunchFailed(string reason) => LaunchFailed?.Invoke(this, reason);

    // ---------------------------------------------------------------- поиск редактора

    private ResolvedEditor? Resolve()
    {
        lock (_gate)
        {
            if (_resolved)
                return _editor;

            _editor = Discover(_configuredExe);
            _resolved = true;
            return _editor;
        }
    }

    /// <summary>
    /// Ищет VS Code. Возвращает <c>null</c>, если не нашли — тогда работает фолбэк на ассоциацию ОС.
    /// </summary>
    /// <param name="configured">Явно заданный путь или <c>null</c>.</param>
    /// <returns>Описание найденного редактора либо <c>null</c>.</returns>
    private static ResolvedEditor? Discover(string? configured)
    {
        // 1. Явно заданный путь.
        ResolvedEditor? explicitOne = FromCandidate(configured);
        if (explicitOne is not null)
            return explicitOne;

        // 2. Переменная окружения.
        ResolvedEditor? fromEnv = FromCandidate(Environment.GetEnvironmentVariable("VSCODE_EXE"));
        if (fromEnv is not null)
            return fromEnv;

        // 3. PATH. Порядок имён важен: Code.exe запускается напрямую, code.cmd — через cmd /c.
        string[] names = OperatingSystem.IsWindows()
            ? new[] { "Code.exe", "code.exe", "code.cmd", "code" }
            : new[] { "code" };

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0)
                continue;

            foreach (string name in names)
            {
                ResolvedEditor? hit = SafeFromCandidate(Path.Combine(trimmed, name));
                if (hit is not null)
                    return hit;
            }
        }

        // 4. Типовые каталоги установки.
        foreach (string root in WellKnownRoots())
        {
            ResolvedEditor? hit = SafeFromCandidate(Path.Combine(root, "Code.exe"))
                                  ?? SafeFromCandidate(Path.Combine(root, "bin", "code.cmd"));
            if (hit is not null)
                return hit;
        }

        return null;
    }

    private static IEnumerable<string> WellKnownRoots()
    {
        string?[] bases =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } local
                ? Path.Combine(local, "Programs", "Microsoft VS Code")
                : null,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } pf
                ? Path.Combine(pf, "Microsoft VS Code")
                : null,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) is { Length: > 0 } pf86
                ? Path.Combine(pf86, "Microsoft VS Code")
                : null
        };

        foreach (string? b in bases)
        {
            if (!string.IsNullOrEmpty(b))
                yield return b;
        }
    }

    private static ResolvedEditor? SafeFromCandidate(string candidate)
    {
        try
        {
            return FromCandidate(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                       or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Превращает путь-кандидат в описание редактора, если файл существует.
    /// Для <c>bin\code[.cmd]</c> пытается подняться на уровень выше к соседнему <c>Code.exe</c>.
    /// </summary>
    /// <param name="candidate">Путь-кандидат.</param>
    /// <returns>Описание редактора либо <c>null</c>.</returns>
    private static ResolvedEditor? FromCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        string p = candidate.Trim().Trim('"');
        if (!File.Exists(p))
            return null;

        string full = Path.GetFullPath(p);
        string ext = Path.GetExtension(full);

        // bin\code, bin\code.cmd -> ..\Code.exe (его можно запускать напрямую)
        string? binDir = Path.GetDirectoryName(full);
        if (binDir is not null &&
            string.Equals(Path.GetFileName(binDir), "bin", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(binDir);
            if (parent is not null)
            {
                string exe = Path.Combine(parent, "Code.exe");
                if (File.Exists(exe))
                    return new ResolvedEditor(Path.GetFullPath(exe), NeedsCmdShell: false, DisplayPath: full);
            }
        }

        bool needsShell = ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                          ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                          ext.Length == 0;          // расширения нет — на Windows это shell-обёртка

        if (!OperatingSystem.IsWindows())
            needsShell = false;

        return new ResolvedEditor(full, needsShell, full);
    }

    /// <summary>Найденный редактор.</summary>
    /// <param name="ExePath">Что запускать.</param>
    /// <param name="NeedsCmdShell">Нужен ли <c>cmd /c</c> (обёртка .cmd, а не .exe).</param>
    /// <param name="DisplayPath">Путь для показа в UI (может отличаться: нашли обёртку, запускаем .exe).</param>
    private sealed record ResolvedEditor(string ExePath, bool NeedsCmdShell, string DisplayPath);
}
