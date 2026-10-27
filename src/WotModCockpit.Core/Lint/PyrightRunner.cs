using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WotModCockpit.Core.Scaffold;

namespace WotModCockpit.Core.Lint;

/// <summary>Уровень диагностики pyright.</summary>
public enum PyrightSeverity
{
    /// <summary>Подсказка; в JSON приходит как <c>information</c>.</summary>
    Information = 0,

    /// <summary>Предупреждение.</summary>
    Warning = 1,

    /// <summary>Ошибка.</summary>
    Error = 2
}

/// <summary>
/// Одна диагностика pyright, приведённая к виду списка Problems кокпита.
/// </summary>
/// <param name="File">Абсолютный путь к файлу.</param>
/// <param name="Line">Строка, 1-based (pyright отдаёт 0-based).</param>
/// <param name="Column">Колонка, 1-based.</param>
/// <param name="Severity">Уровень.</param>
/// <param name="Rule">Правило pyright (<c>reportMissingImports</c>) либо <c>null</c> у синтаксических ошибок.</param>
/// <param name="Message">Текст.</param>
public sealed record PyrightDiagnostic(
    string File,
    int Line,
    int Column,
    PyrightSeverity Severity,
    string? Rule,
    string Message)
{
    /// <summary>Строка для списка Problems.</summary>
    public string Display => $"{Path.GetFileName(File)}({Line},{Column}): {Message}";
}

/// <summary>Чем закончился запуск.</summary>
public enum PyrightRunState
{
    /// <summary>Pyright отработал, диагностики разобраны.</summary>
    Completed = 0,

    /// <summary>Node.js или pyright не найдены. Фича просто недоступна — это НЕ ошибка сборки.</summary>
    NotInstalled = 1,

    /// <summary>Не уложился в таймаут.</summary>
    TimedOut = 2,

    /// <summary>Запустился, но вывод разобрать не удалось.</summary>
    Failed = 3,

    /// <summary>Отменён пользователем.</summary>
    Cancelled = 4
}

/// <summary>Результат разового запуска pyright.</summary>
/// <param name="State">Итог.</param>
/// <param name="Diagnostics">Диагностики по коду мода (шум уже отфильтрован).</param>
/// <param name="SuppressedCount">Сколько диагностик отброшено как заведомо ложные на коде Python 2.</param>
/// <param name="Duration">Сколько занял запуск.</param>
/// <param name="Message">Готовая к показу строка.</param>
/// <param name="ToolPath">Чем именно запускали — полезно, когда «у меня не работает».</param>
public sealed record PyrightResult(
    PyrightRunState State,
    IReadOnlyList<PyrightDiagnostic> Diagnostics,
    int SuppressedCount,
    TimeSpan Duration,
    string Message,
    string ToolPath)
{
    /// <summary>Запуск удался.</summary>
    public bool IsCompleted => State == PyrightRunState.Completed;

    /// <summary>Отказ с сообщением.</summary>
    /// <param name="state">Состояние.</param>
    /// <param name="message">Сообщение.</param>
    /// <param name="toolPath">Путь к инструменту, если он известен.</param>
    /// <returns>Результат-отказ.</returns>
    public static PyrightResult Fail(PyrightRunState state, string message, string toolPath = "") =>
        new(state, [], 0, TimeSpan.Zero, message, toolPath);
}

/// <summary>Разовый запуск pyright как линтера (не LSP).</summary>
public interface IPyrightRunner
{
    /// <summary>Pyright доступен: найдены и Node.js, и сам pyright.</summary>
    /// <returns>Путь к <c>pyright</c> либо пустая строка.</returns>
    string Locate();

    /// <summary>
    /// Проверяет код мода. Отсутствие Node.js возвращает
    /// <see cref="PyrightRunState.NotInstalled"/>, а не исключение.
    /// </summary>
    /// <param name="sourceDirectories">Каталоги с кодом мода.</param>
    /// <param name="wotSrcDirectory">Дерево исходников клиента для <c>extraPaths</c>.</param>
    /// <param name="timeout">Таймаут; <c>null</c> — 120 секунд.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <param name="ownModules">
    /// Точечные имена модулей САМОГО мода (<c>gui.mods.battle_clock</c>). По ним гасится
    /// <c>reportMissingImports</c>: такие модули лежат в проекте на диске, но выразить
    /// раскладку игры в <c>extraPaths</c> нельзя — см. <see cref="PyrightRunner"/>.
    /// </param>
    /// <returns>Результат; исключений не бросает.</returns>
    Task<PyrightResult> RunAsync(
        IEnumerable<string> sourceDirectories,
        string? wotSrcDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? ownModules = null);
}

/// <summary>
/// Запускает pyright один раз на коде мода и разбирает <c>--outputjson</c>.
///
/// <para><b>Замер, ради которого всё это настроено так, а не иначе.</b> Эталонный мод шаблона
/// (mod_hello_tanker.py + hello_tanker/main.py, 1351 строка) даёт:
/// без extraPaths — 19 диагностик; с extraPaths — 6; с extraPaths и заглушками нативных
/// модулей — 3; после отсева py2-встроенных имён — 0. Все отсеянные — это три обращения
/// к <c>unicode</c>, которого в Python 3 нет, а в клиенте есть.
/// Режим <c>basic</c> вместо <c>off</c> добавляет 7 диагностик, и все семь ложные.</para>
///
/// <para><b>Значение по умолчанию — ВЫКЛЮЧЕНО</b> (<c>ProblemsViewModel.RunPyright</c> = false),
/// и это не противоречие с замером выше. Шума на исправном коде нет, но запуск стоит внешнего
/// процесса Node и секунды времени на каждый прогон preflight, а preflight идёт автоматически
/// при каждом открытии проекта и перед каждой сборкой. Поэтому pyright включается галочкой
/// в панели Problems, когда он нужен, и не удорожает остальные прогоны.</para>
///
/// <para><b>Где ищется.</b> Явный путь из настроек (<c>CockpitSettings.PyrightPath</c>) —
/// первым; затем необязательная локальная установка в <c>.scratch\pyright-test</c> шаблона мода,
/// <c>node_modules</c> рядом с кокпитом, <c>pyright.exe</c>/<c>pyright.cmd</c> из PATH и
/// <c>%APPDATA%\npm</c>. Явным путём
/// можно указать как <c>…\node_modules\pyright\index.js</c> (запустится через Node), так
/// и обёртку <c>pyright.cmd</c>/<c>pyright.exe</c> от npm -g, yarn, pnpm или pip — она
/// запускается напрямую и Node.js в PATH при этом не нужен.</para>
/// </summary>
public sealed class PyrightRunner : IPyrightRunner
{
    /// <summary>Таймаут по умолчанию. Холодный старт pyright обычно занимает около секунды — запас большой.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Метка «процесс вообще не стартовал» в stderr — отличает «нет Node» от «pyright ругается».</summary>
    private const string NotStartedMarker = "[не запустился] ";

    /// <summary>
    /// Имена, которых нет в Python 3, но которые есть в Python 2.7 клиента.
    /// Диагностика <c>reportUndefinedVariable</c> по ним — гарантированно ложная,
    /// потому что мы намеренно анализируем py2-код парсером py3.
    /// </summary>
    private static readonly HashSet<string> Python2Builtins = new(StringComparer.Ordinal)
    {
        "unicode", "basestring", "long", "xrange", "raw_input", "unichr", "reduce",
        "execfile", "reload", "cmp", "apply", "buffer", "intern", "coerce", "file", "unicodedata"
    };

    private readonly string _explicitPyright;
    private readonly string _explicitNode;
    private readonly string _templateDir;
    private string? _located;

    /// <summary>Создаёт запускатель.</summary>
    /// <param name="pyrightPath">
    /// Явный путь из <c>CockpitSettings.PyrightPath</c>: либо <c>index.js</c> пакета pyright,
    /// либо обёртка <c>pyright.cmd</c>/<c>pyright.exe</c>. Пусто — искать самому.
    /// </param>
    /// <param name="nodePath">Явный путь к <c>node</c>; пусто — <c>node</c> из PATH.</param>
    /// <param name="templateDir">
    /// Каталог шаблона из <c>CockpitSettings.TemplateDir</c> — для необязательной пробы
    /// локальной установки pyright в нём. Пусто — шаблон ищется сам (<see cref="TemplateLocator"/>).
    /// </param>
    public PyrightRunner(string? pyrightPath = null, string? nodePath = null, string? templateDir = null)
    {
        _explicitPyright = pyrightPath?.Trim() ?? string.Empty;
        _explicitNode = string.IsNullOrWhiteSpace(nodePath) ? "node" : nodePath.Trim();
        _templateDir = templateDir?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Указанный в настройках путь задан, но такого файла нет. Отдельное состояние, потому что
    /// молча свалиться на автопоиск здесь нельзя: пользователь настройку заполнил осознанно
    /// и должен узнать, что она не сработала.
    /// </summary>
    public bool ExplicitPathMissing =>
        _explicitPyright.Length > 0 && !File.Exists(_explicitPyright);

    /// <inheritdoc />
    public string Locate()
    {
        if (_located is not null) return _located;

        _located = Probe();
        return _located;
    }

    private string Probe()
    {
        if (!string.IsNullOrWhiteSpace(_explicitPyright) && File.Exists(_explicitPyright)) return _explicitPyright;

        // Порядок проб: сначала локальные установки (их версия зафиксирована в package-lock),
        // потом то, что видно из PATH, потом глобальная установка npm по умолчанию.
        foreach (string candidate in CandidatePaths())
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Кривой элемент PATH — не повод бросать поиск.
            }
        }

        return string.Empty;
    }

    private IEnumerable<string> CandidatePaths()
    {
        // Необязательная локальная установка в рабочей папке шаблона мода
        // (<шаблон>\.scratch\pyright-test): её версия зафиксирована в package-lock.
        // Шаблона нет — пробы просто нет.
        string template = TemplateLocator.Resolve(_templateDir);
        if (template.Length > 0)
        {
            yield return Path.Combine(template, ".scratch", "pyright-test", "node_modules", "pyright", "index.js");
        }

        // node_modules рядом с кокпитом или выше.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "node_modules", "pyright", "index.js");
        }

        // PATH: обёртки pip (pyright.exe), npm -g с другим префиксом, scoop, volta, nvm-windows
        // (pyright.cmd). То, что работает в терминале пользователя, обязано найтись и здесь.
        foreach (string candidate in FromPath())
        {
            yield return candidate;
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            yield return Path.Combine(appData, "npm", "node_modules", "pyright", "index.js");
        }
    }

    /// <summary>Кандидаты <c>pyright.exe</c> и <c>pyright.cmd</c> из каталогов переменной PATH.</summary>
    private static IEnumerable<string> FromPath()
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = raw.Trim().Trim('"');
            if (dir.Length == 0 || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                continue;
            }

            yield return Path.Combine(dir, "pyright.exe");
            yield return Path.Combine(dir, "pyright.cmd");
        }
    }

    /// <inheritdoc />
    public async Task<PyrightResult> RunAsync(
        IEnumerable<string> sourceDirectories,
        string? wotSrcDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? ownModules = null)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectories);

        var own = ownModules is null
            ? null
            : new HashSet<string>(ownModules.Where(m => !string.IsNullOrWhiteSpace(m)), StringComparer.Ordinal);

        string[] sources = [.. sourceDirectories.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))];
        if (sources.Length == 0)
        {
            return PyrightResult.Fail(PyrightRunState.Failed, "не задан ни один существующий каталог с кодом мода");
        }

        if (ExplicitPathMissing)
        {
            return PyrightResult.Fail(PyrightRunState.NotInstalled,
                $"в настройках кокпита указан pyright '{_explicitPyright}', но такого файла нет. " +
                "Исправьте поле pyrightPath в settings.json (годится и node_modules\\pyright\\index.js, " +
                "и обёртка pyright.cmd) либо очистите его, чтобы вернуться к автопоиску.");
        }

        string pyright = Locate();
        if (string.IsNullOrEmpty(pyright))
        {
            return PyrightResult.Fail(PyrightRunState.NotInstalled,
                "pyright не найден. Установите Node.js и выполните 'npm install -g pyright' — либо укажите путь " +
                "к нему в настройках кокпита (поле pyrightPath в settings.json; годится и " +
                "node_modules\\pyright\\index.js, и обёртка pyright.cmd от npm/yarn/pnpm/pip). " +
                "Без pyright недоступна только проверка типов, всё остальное работает.");
        }

        string workspace = Path.Combine(Path.GetTempPath(), "WotModCockpit", "pyright", Guid.NewGuid().ToString("N")[..12]);
        var sw = Stopwatch.StartNew();

        try
        {
            string configPath = PyrightConfigWriter.Write(workspace, sources, wotSrcDirectory);

            // Каталоги идут АРГУМЕНТАМИ, а не через 'include' в конфиге: абсолютный путь
            // в 'include' pyright молча игнорирует и анализирует ноль файлов (см. комментарий
            // в PyrightConfigWriter.Write).
            List<string> args = ["--outputjson", "--project", configPath];
            args.AddRange(sources);

            // index.js запускается Node'ом, а обёртка npm/yarn/pnpm/pip — сама собой. Иначе
            // указать её в настройках было бы бессмысленно: Node попытался бы выполнить .cmd
            // как скрипт. Отдельно .cmd/.bat: CreateProcess их не запускает вовсе, нужен cmd.exe.
            (string exe, string[] argv) = BuildCommandLine(pyright, args);

            (int exitCode, string stdout, string stderr, bool timedOut) = await RunProcessAsync(
                exe,
                argv,
                workspace,
                timeout ?? DefaultTimeout,
                cancellationToken).ConfigureAwait(false);

            sw.Stop();

            if (timedOut)
            {
                return PyrightResult.Fail(PyrightRunState.TimedOut,
                    $"pyright не уложился в {(timeout ?? DefaultTimeout).TotalSeconds:0} с и был остановлен", pyright);
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                // exitCode 1 у pyright означает «есть ошибки», а не «сбой», поэтому
                // судим не по коду возврата, а по наличию разбираемого вывода.
                string detail = string.IsNullOrWhiteSpace(stderr) ? $"код возврата {exitCode}" : stderr.Trim();

                // Процесс не стартовал — фича недоступна, а не сломана.
                if (detail.Contains(NotStartedMarker, StringComparison.Ordinal))
                {
                    return PyrightResult.Fail(PyrightRunState.NotInstalled,
                        $"не удалось запустить '{exe}'. Если это node — установите Node.js или укажите путь к нему " +
                        "в настройках кокпита (поле nodePath в settings.json). Без этого недоступна только проверка " +
                        "типов, всё остальное работает.",
                        pyright);
                }

                return PyrightResult.Fail(PyrightRunState.Failed, "pyright ничего не вернул: " + Shorten(detail), pyright);
            }

            return Parse(stdout, sw.Elapsed, pyright, own);
        }
        catch (OperationCanceledException)
        {
            return PyrightResult.Fail(PyrightRunState.Cancelled, "проверка отменена", pyright);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return PyrightResult.Fail(PyrightRunState.Failed, "не удалось запустить pyright: " + ex.Message, pyright);
        }
        finally
        {
            TryDeleteDirectory(workspace);
        }
    }

    /// <summary>
    /// Как именно запускать найденный pyright.
    ///
    /// <para>Три разных случая, и все три встречаются у пользователей:
    /// <c>index.js</c> из <c>node_modules</c> (нужен Node), <c>pyright.exe</c> от pip
    /// (запускается сам), <c>pyright.cmd</c> от npm/yarn/pnpm — а .cmd и .bat Win32
    /// <c>CreateProcess</c> не запускает вовсе, для них обязателен <c>cmd.exe /c</c>.</para>
    /// </summary>
    /// <param name="pyright">Путь к найденному pyright.</param>
    /// <param name="arguments">Аргументы pyright.</param>
    /// <returns>Что запускать и с какими аргументами.</returns>
    public (string Executable, string[] Arguments) BuildCommandLine(string pyright, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(pyright);
        ArgumentNullException.ThrowIfNull(arguments);

        string extension = Path.GetExtension(pyright);

        if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase))
        {
            return (_explicitNode, [pyright, .. arguments]);
        }

        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return ("cmd.exe", ["/c", pyright, .. arguments]);
        }

        return (pyright, [.. arguments]);
    }

    /// <summary>
    /// Разбирает JSON pyright. Публичный, чтобы разбор можно было проверить тестом
    /// на зафиксированном выводе, не запуская Node.
    /// </summary>
    /// <param name="json">Вывод <c>--outputjson</c>.</param>
    /// <param name="duration">Длительность запуска.</param>
    /// <param name="toolPath">Чем запускали.</param>
    /// <param name="ownModules">Точечные имена модулей самого мода; <c>null</c> — не гасить ничего.</param>
    /// <returns>Результат.</returns>
    public static PyrightResult Parse(string json, TimeSpan duration = default, string toolPath = "",
        IReadOnlySet<string>? ownModules = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        // Pyright иногда печатает предупреждения до JSON; берём от первой '{'.
        int start = json.IndexOf('{');
        if (start < 0) return PyrightResult.Fail(PyrightRunState.Failed, "вывод pyright не содержит JSON", toolPath);

        var diagnostics = new List<PyrightDiagnostic>();
        int suppressed = 0;

        using JsonDocument doc = JsonDocument.Parse(json[start..]);

        // «Ноль проанализированных файлов» — это НЕ «замечаний нет». Так выглядит
        // неправильно собранный конфиг (например абсолютный путь в 'include'), и молча
        // отрапортовать успех здесь — худшее, что можно сделать: пользователь решит,
        // что код проверен, а его никто не смотрел.
        if (doc.RootElement.TryGetProperty("summary", out JsonElement summaryElement) &&
            summaryElement.TryGetProperty("filesAnalyzed", out JsonElement analyzed) &&
            analyzed.TryGetInt32(out int analyzedCount) && analyzedCount == 0)
        {
            return PyrightResult.Fail(PyrightRunState.Failed,
                "pyright не проанализировал ни одного файла — проверка ничего не подтверждает", toolPath);
        }

        if (!doc.RootElement.TryGetProperty("generalDiagnostics", out JsonElement list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return new PyrightResult(PyrightRunState.Completed, [], 0, duration, "pyright: замечаний нет", toolPath);
        }

        foreach (JsonElement item in list.EnumerateArray())
        {
            string file = item.TryGetProperty("file", out JsonElement f) ? f.GetString() ?? string.Empty : string.Empty;
            string message = item.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? string.Empty : string.Empty;
            string? rule = item.TryGetProperty("rule", out JsonElement r) ? r.GetString() : null;

            int line = 0, column = 0;
            if (item.TryGetProperty("range", out JsonElement range) &&
                range.TryGetProperty("start", out JsonElement startPos))
            {
                if (startPos.TryGetProperty("line", out JsonElement l)) line = l.GetInt32() + 1;
                if (startPos.TryGetProperty("character", out JsonElement c)) column = c.GetInt32() + 1;
            }

            PyrightSeverity severity = item.TryGetProperty("severity", out JsonElement s)
                ? ToSeverity(s.GetString())
                : PyrightSeverity.Warning;

            if (IsPython2Noise(rule, message)
                || IsForeignModApi(rule, message)
                || IsOwnModule(rule, message, ownModules)) { suppressed++; continue; }

            diagnostics.Add(new PyrightDiagnostic(file, line, column, severity, rule, message));
        }

        string summary = diagnostics.Count == 0
            ? suppressed == 0
                ? "pyright: замечаний нет"
                : $"pyright: замечаний нет (отброшено заведомо ложных: {suppressed} — " +
                  "имена Python 2, импорты API чужих модов и собственных модулей мода)"
            : $"pyright: замечаний {diagnostics.Count}" + (suppressed > 0 ? $", отброшено {suppressed}" : string.Empty);

        return new PyrightResult(PyrightRunState.Completed, diagnostics, suppressed, duration, summary, toolPath);
    }

    /// <summary>
    /// Диагностика заведомо ложная, потому что py2-код анализируется парсером py3.
    /// Список закрытый и короткий: глушить целые правила нельзя, иначе потеряется
    /// именно тот сигнал, ради которого фича и нужна.
    /// </summary>
    private static bool IsPython2Noise(string? rule, string message)
    {
        if (!string.Equals(rule, "reportUndefinedVariable", StringComparison.Ordinal)) return false;

        // Сообщение вида: "unicode" is not defined
        int first = message.IndexOf('"');
        if (first < 0) return false;
        int second = message.IndexOf('"', first + 1);
        if (second <= first + 1) return false;

        return Python2Builtins.Contains(message[(first + 1)..second]);
    }

    /// <summary>
    /// «Import "gui.modsListApi" could not be resolved» на API ЧУЖОГО мода.
    ///
    /// <para>Такого модуля в дереве клиента нет и быть не может: это API другого мода,
    /// который пользователь ставит отдельно. Замер на отрендеренном каталоге рецептов:
    /// 5 предупреждений, ВСЕ пять — <c>reportMissingImports</c> на <c>gui.modsListApi</c>
    /// (R14, три места) и <c>gui.modsSettingsApi</c> (R15, два места), причём оба рецепта
    /// импортируют их под <c>except ImportError</c> именно потому, что зависимость может
    /// отсутствовать. Это не дефект, а сама суть внешней зависимости.</para>
    ///
    /// <para>Заглушку <c>.pyi</c> здесь поставить нельзя: <c>gui</c> — НАСТОЯЩИЙ пакет
    /// клиента, и пакет-заглушка <c>gui/</c> в stubPath заслонил бы его целиком вместе
    /// с <c>gui.Scaleform</c>, <c>gui.shared</c> и всем остальным. Поэтому глушится
    /// диагностика, а не резолвится модуль.</para>
    ///
    /// <para>Настоящий риск такого импорта (порядок init() модов, отсутствующая
    /// зависимость) разбирает правило WOT018 — там же, где ему и место.</para>
    /// </summary>
    private static bool IsForeignModApi(string? rule, string message)
    {
        if (!string.Equals(rule, "reportMissingImports", StringComparison.Ordinal) &&
            !string.Equals(rule, "reportMissingModuleSource", StringComparison.Ordinal))
        {
            return false;
        }

        int first = message.IndexOf('"');
        if (first < 0) return false;
        int second = message.IndexOf('"', first + 1);
        if (second <= first + 1) return false;

        return Preflight.ClientApiOracle.IsModApi(message[(first + 1)..second]);
    }

    /// <summary>
    /// «Import "gui.mods.battle_clock" could not be resolved» на модуле САМОГО мода.
    ///
    /// <para>Такой модуль лежит в проекте на диске, и в игре он импортируется именно так:
    /// движок монтирует <c>scripts/client</c> из архива мода в <c>sys.path</c>, поэтому
    /// <c>res/scripts/client/gui/mods/&lt;пакет&gt;</c> становится <c>gui.mods.&lt;пакет&gt;</c>.
    /// Выразить это в <c>extraPaths</c> нельзя, и это ИЗМЕРЕНО: добавление корня мода
    /// в <c>extraPaths</c> не помогает ни первым, ни последним — <c>gui</c> в клиенте
    /// обычный пакет с <c>__init__.py</c>, pyright резолвит его туда и в каталог мода
    /// уже не заглядывает.</para>
    ///
    /// <para>Гасится только то, что мы можем ДОКАЗАТЬ: имена собираются обходом дерева
    /// проекта. Опечатка (<c>gui.mods.battle_clok</c>) в этот список не попадёт и останется
    /// находкой.</para>
    /// </summary>
    private static bool IsOwnModule(string? rule, string message, IReadOnlySet<string>? ownModules)
    {
        if (ownModules is null || ownModules.Count == 0) return false;
        if (!string.Equals(rule, "reportMissingImports", StringComparison.Ordinal)) return false;

        int first = message.IndexOf('"');
        if (first < 0) return false;
        int second = message.IndexOf('"', first + 1);
        if (second <= first + 1) return false;

        return ownModules.Contains(message[(first + 1)..second]);
    }

    private static PyrightSeverity ToSeverity(string? value) => value switch
    {
        "error" => PyrightSeverity.Error,
        "information" => PyrightSeverity.Information,
        _ => PyrightSeverity.Warning
    };

    private static async Task<(int ExitCode, string Stdout, string Stderr, bool TimedOut)> RunProcessAsync(
        string fileName, string[] arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string a in arguments) psi.ArgumentList.Add(a.Trim('"'));

        using var process = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Процесс не стартовал: чаще всего это Node.js, которого нет в PATH.
            // Штатная ситуация, а не сбой приложения.
            return (-1, string.Empty, NotStartedMarker + fileName + ": " + ex.Message, false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (ct.IsCancellationRequested) throw;
            return (-1, stdout.ToString(), stderr.ToString(), true);
        }

        return (process.ExitCode, stdout.ToString(), stderr.ToString(), false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Процесс уже умер сам.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Временный каталог в %TEMP%: не удалился — не беда.
        }
    }

    private static string Shorten(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
