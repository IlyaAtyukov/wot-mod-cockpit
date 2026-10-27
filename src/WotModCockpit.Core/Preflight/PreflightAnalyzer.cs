using System.Diagnostics;
using System.Text;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Preflight.Rules;

namespace WotModCockpit.Core.Preflight;

/// <summary>
/// Прогон всех правил preflight по дереву мода.
///
/// <para>Аналог запуска Roslyn-анализаторов перед сборкой: читаем <c>.py</c> файлы проекта,
/// строим лексическую модель каждого, собираем общий контекст (индекс клиента, раскладка,
/// цепочка импорта точки входа) и по очереди зовём правила. Правило, упавшее с исключением,
/// не должно ронять весь анализ — оно отдаётся отдельной находкой.</para>
///
/// <para>Анализ рассчитан на единицы файлов: проект мода — это точка входа плюс пакет,
/// в эталонном шаблоне 3 файла и 1362 строки.</para>
/// </summary>
public sealed class PreflightAnalyzer
{
    /// <summary>Подкаталог, в котором загрузчик клиента ищет точки входа.</summary>
    public const string ModsDirectory = "res/scripts/client/gui/mods/";

    private readonly IReadOnlyList<IPreflightRule> _rules;

    /// <summary>Создаёт анализатор со стандартным набором правил.</summary>
    public PreflightAnalyzer() : this(CreateDefaultRules()) { }

    /// <summary>Создаёт анализатор с заданным набором правил (для тестов одного правила).</summary>
    /// <param name="rules">Правила.</param>
    public PreflightAnalyzer(IReadOnlyList<IPreflightRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
    }

    /// <summary>Правила в порядке ценности: сначала то, что закрывает клиент.</summary>
    /// <returns>Набор правил.</returns>
    public static IReadOnlyList<IPreflightRule> CreateDefaultRules() => new IPreflightRule[]
    {
        new ModuleLevelGameWorkRule(),      // WOT001 — клиент закрывается
        new UnknownApiRule(),               // WOT002/003/004 — «игра переехала»
        new EntryPointGuardRule(),          // WOT005
        new EventHandlerGuardRule(),        // WOT006
        new MonkeyPatchOriginalRule(),      // WOT007
        new GetAttrWithoutDefaultRule(),    // WOT008
        new SubscriptionLeakRule(),         // WOT009
        new CallbackLoopRule(),             // WOT010
        new EventBusListenerRule(),         // WOT011
        new EntryPointLayoutRule(),         // WOT014 — мод не будет найден вовсе
        new CodingDeclarationRule(),        // WOT015 — SyntaxError внутри клиента
        new FilePathRule(),                 // WOT016
        new StrUnicodeMixRule(),            // WOT017
        new ModApiImportRule(),             // WOT018
        new NameManglingRule(),             // WOT019
        new DuplicateTopLevelNameRule(),    // WOT022 — мод молча наполовину мёртв
        new LogPumpRule(),                  // WOT012 — удобство разработчика
        new LogDebugNoOpRule(),             // WOT013
        new PrintStatementRule(),           // WOT020
        new SysPathRule()                   // WOT021
    };

    /// <summary>Все идентификаторы, которые может выдать набор по умолчанию.</summary>
    public static IReadOnlyList<string> AllRuleIds { get; } = new[]
    {
        "WOT001", "WOT002", "WOT003", "WOT004", "WOT005", "WOT006", "WOT007",
        "WOT008", "WOT009", "WOT010", "WOT011", "WOT012", "WOT013", "WOT014",
        "WOT015", "WOT016", "WOT017", "WOT018", "WOT019", "WOT020", "WOT021", "WOT022"
    };

    /// <summary>Правила этого анализатора.</summary>
    public IReadOnlyList<IPreflightRule> Rules => _rules;

    /// <summary>
    /// Собирает контекст по каталогу исходников. Не бросает: нечитаемый файл просто
    /// пропускается.
    /// </summary>
    /// <param name="sourceRoot">Каталог <c>src</c> проекта.</param>
    /// <param name="projectDir">Корень проекта (для относительных путей); <c>null</c> = <paramref name="sourceRoot"/>.</param>
    /// <param name="oracle">Оракул клиентского API.</param>
    /// <param name="project">Проект (нужен правилам, которым важен паспорт мода).</param>
    /// <param name="options">Настройки.</param>
    /// <returns>Контекст.</returns>
    public static PreflightContext BuildContext(string sourceRoot, string? projectDir, ClientApiOracle oracle,
        ModProject? project = null, PreflightOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(oracle);

        options ??= new PreflightOptions();
        string root = projectDir ?? sourceRoot;

        var raw = new List<(string Full, string Relative, string Text, bool Bom)>();
        foreach (string path in EnumerateSources(sourceRoot))
        {
            (string? text, bool bom) = ReadSource(path, options.MaxFileLength);
            if (text is null) continue;
            raw.Add((path, Relative(root, path), text, bom));
        }

        // Точки входа: прямые дети res/scripts/client/gui/mods с префиксом mod_.
        var entryIndexes = new List<int>();
        for (int i = 0; i < raw.Count; i++)
        {
            int at = raw[i].Relative.IndexOf(ModsDirectory, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            string tail = raw[i].Relative[(at + ModsDirectory.Length)..];
            if (!tail.Contains('/', StringComparison.Ordinal) &&
                Path.GetFileName(tail).StartsWith("mod_", StringComparison.Ordinal))
                entryIndexes.Add(i);
        }

        string[] dotted = raw.Select(f => DottedModule(f.Relative)).ToArray();
        bool[] inChain = new bool[raw.Count];
        foreach (int i in entryIndexes) inChain[i] = true;

        // Цепочка импорта точки входа. Стаб грузит тело мода через строковую константу
        // (ENTRY = 'gui.mods.hello_tanker.main'), а не оператором import, поэтому одного
        // разбора импортов мало: ищем имя модуля и в тексте точки входа.
        var models = raw.Select(f => PythonLineModel.Parse(f.Text)).ToArray();
        for (int pass = 0; pass < 4; pass++)
        {
            bool changed = false;
            for (int i = 0; i < raw.Count; i++)
            {
                if (!inChain[i]) continue;
                for (int j = 0; j < raw.Count; j++)
                {
                    if (inChain[j] || dotted[j].Length == 0) continue;
                    if (!Mentions(raw[i].Text, models[i], dotted[j])) continue;
                    inChain[j] = true;
                    changed = true;
                }
            }
            if (!changed) break;
        }

        var files = new List<ModSourceFile>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            files.Add(new ModSourceFile
            {
                FullPath = raw[i].Full,
                RelativePath = raw[i].Relative,
                Text = raw[i].Text,
                Model = models[i],
                HasBom = raw[i].Bom,
                IsEntryPoint = entryIndexes.Contains(i),
                InEntryImportChain = inChain[i],
                DottedModule = dotted[i]
            });
        }

        var own = OwnModuleNames(raw.Select(f => f.Relative));
        if (project is not null && project.PackageName.Length > 0) own.Add(project.PackageName);

        bool hasFini = files.Any(f => f.Model.Definitions.Any(d => d.IsTopLevel && !d.IsClass && d.Name == "fini"));

        return new PreflightContext
        {
            Project = project,
            ProjectDir = root,
            Files = files,
            Api = oracle,
            Options = options,
            OwnModules = own,
            HasFini = hasFini
        };
    }

    /// <summary>Прогоняет все включённые правила.</summary>
    /// <param name="context">Контекст.</param>
    /// <returns>Результат с отсортированными находками.</returns>
    public PreflightResult Analyze(PreflightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();
        var diagnostics = new List<PreflightDiagnostic>();
        var ran = new List<string>();

        foreach (IPreflightRule rule in _rules)
        {
            if (!context.Options.IsEnabled(rule.Id)) continue;
            ran.Add(rule.Id);
            try
            {
                rule.Analyze(context, diagnostics);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // Упавшее правило не должно отменять весь preflight: это инструмент разработчика,
                // а не компилятор. Но и молчать нельзя — иначе правило «просто перестанет работать».
                diagnostics.Add(new PreflightDiagnostic(rule.Id, PreflightSeverity.Info,
                    context.ProjectDir, 0, 0,
                    $"Правило {rule.Id} ({rule.Title}) упало на этом проекте: {ex.GetType().Name}: {ex.Message}. " +
                    "Остальные правила отработали."));
            }
        }

        diagnostics.Sort(Compare);
        stopwatch.Stop();

        int lines = 0;
        foreach (ModSourceFile f in context.Files) lines += f.Model.LineCount;

        return new PreflightResult(diagnostics, context.Files.Count, lines, stopwatch.Elapsed, ran);
    }

    private static int Compare(PreflightDiagnostic a, PreflightDiagnostic b)
    {
        int bySeverity = b.Severity.CompareTo(a.Severity);
        if (bySeverity != 0) return bySeverity;

        int byFile = string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);
        if (byFile != 0) return byFile;

        int byLine = a.Line.CompareTo(b.Line);
        return byLine != 0 ? byLine : string.CompareOrdinal(a.RuleId, b.RuleId);
    }

    // ------------------------------------------------------------------ вспомогательное

    /// <summary>Все <c>.py</c> файлы дерева, кроме служебных каталогов сборки.</summary>
    /// <param name="root">Корень.</param>
    /// <returns>Абсолютные пути.</returns>
    public static IEnumerable<string> EnumerateSources(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;

        IEnumerable<string> all;
        try { all = Directory.EnumerateFiles(root, "*.py", SearchOption.AllDirectories); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (string path in all)
        {
            // Служебные папки ищутся в пути ВНУТРИ проверяемого каталога, а не в полном.
            // Раньше проверялся полный путь, и проект, у которого папка с таким именем стояла
            // где-нибудь выше (D:\dist\моды\мой-мод), разбирался как ПУСТОЙ: ноль файлов,
            // ноль ошибок, «всё хорошо». Ведущий «/» нужен, чтобы папка первого уровня
            // (.scratch прямо в корне проекта) тоже совпадала с «/.scratch/».
            string inside = "/" + Path.GetRelativePath(root, path).Replace('\\', '/');
            if (inside.Contains("/__pycache__/", StringComparison.Ordinal)) continue;
            if (inside.Contains("/.scratch/", StringComparison.Ordinal)) continue;
            if (inside.Contains("/dist/", StringComparison.Ordinal)) continue;
            if (inside.Contains("/.tools/", StringComparison.Ordinal)) continue;
            yield return path;
        }
    }

    private static (string? Text, bool Bom) ReadSource(string path, int maxLength)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > maxLength) return (null, false);

            byte[] bytes = File.ReadAllBytes(path);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string text = new UTF8Encoding(false).GetString(bom ? bytes.AsSpan(3) : bytes);
            return (text, bom);
        }
        catch (IOException) { return (null, false); }
        catch (UnauthorizedAccessException) { return (null, false); }
    }

    private static string Relative(string root, string path)
    {
        try { return Path.GetRelativePath(root, path).Replace('\\', '/'); }
        catch (ArgumentException) { return path.Replace('\\', '/'); }
    }

    /// <summary>
    /// Имена, под которыми модули САМОГО мода могут быть импортированы. Берутся ВСЕ суффиксы
    /// пути каждого файла, а не только полное точечное имя, и вот почему: раскладка мода
    /// произвольна, корень <c>sys.path</c> задаёт загрузчик. XVM держит свои пакеты
    /// в <c>res/mods/openwg_libraries/xfw/…</c> и импортирует их как <c>xfw</c>, а тело —
    /// в <c>res/scripts/client/gui/mods/xvm_main/…</c>, импортируя как <c>xvm_main</c>.
    /// Без суффиксов правило WOT002 объявляло бы отсутствующим каждый собственный модуль мода:
    /// замерено на XVM 13.1 — 395 ложных срабатываний против 12 после исправления.
    /// <para>Суффиксы заодно моделируют НЕЯВНЫЙ относительный импорт Python 2.7: внутри пакета
    /// <c>import utils</c> сначала ищется <c>&lt;пакет&gt;.utils</c>.</para>
    /// </summary>
    /// <param name="relativePaths">Пути файлов мода относительно корня проекта.</param>
    /// <returns>Множество имён.</returns>
    public static HashSet<string> OwnModuleNames(IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);

        var own = new HashSet<string>(StringComparer.Ordinal);
        foreach (string relative in relativePaths)
        {
            string path = relative.Replace('\\', '/');
            if (path.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) path = path[..^3];

            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int count = parts.Length;
            if (count > 0 && parts[^1] == "__init__") count--;      // пакет = его каталог
            if (count == 0) continue;

            for (int i = 0; i < count; i++)
            {
                string name = string.Join('.', parts, i, count - i);
                if (name.Length > 0) own.Add(name);
            }
        }
        return own;
    }

    /// <summary>
    /// Импортируемое имя модуля внутри игры: <c>src/res/scripts/client/gui/mods/hello_tanker/main.py</c>
    /// → <c>gui.mods.hello_tanker.main</c> (корнем <c>sys.path</c> служит <c>scripts/client</c>).
    /// </summary>
    /// <param name="relativePath">Путь относительно корня проекта, прямые слэши.</param>
    /// <returns>Точечное имя либо пустая строка.</returns>
    public static string DottedModule(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        const string marker = "res/scripts/";
        int at = relativePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return string.Empty;

        string tail = relativePath[(at + marker.Length)..];
        int slash = tail.IndexOf('/');
        if (slash > 0) tail = tail[(slash + 1)..];         // client/ или common/

        if (tail.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) tail = tail[..^3];
        if (tail.EndsWith("/__init__", StringComparison.Ordinal)) tail = tail[..^9];
        else if (tail == "__init__") return string.Empty;

        return tail.Replace('/', '.');
    }

    /// <summary>Файл цепочки упоминает модуль: импортом либо строковой константой загрузчика.</summary>
    private static bool Mentions(string text, PythonLineModel model, string dotted)
    {
        if (text.Contains(dotted, StringComparison.Ordinal)) return true;

        int dot = dotted.LastIndexOf('.');
        string last = dot < 0 ? dotted : dotted[(dot + 1)..];
        foreach (PythonStatement st in model.Statements)
        {
            foreach (string module in PreflightSyntax.ImportedModules(st))
            {
                if (string.Equals(module, dotted, StringComparison.Ordinal)) return true;
                if (string.Equals(module, last, StringComparison.Ordinal)) return true;
                if (module.EndsWith("." + last, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }
}
