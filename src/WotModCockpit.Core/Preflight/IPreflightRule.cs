using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Preflight;

/// <summary>
/// Имя, введённое в модуль строкой <c>import</c>. Нужно, чтобы отличить обращение к ИГРОВОМУ
/// модулю (<c>game_control.g_instance</c>) от обращения к чему угодно ещё
/// (<c>player.arena.vehicles</c>): без такой привязки правило WOT004 на корпусе давало
/// 124 срабатывания вместо 2.
/// </summary>
/// <param name="LocalName">Имя, под которым модуль виден в файле (<c>X</c> из <c>import X as Y</c> — это <c>Y</c>).</param>
/// <param name="Module">Полное точечное имя модуля.</param>
/// <param name="Line">Строка импорта, 1-based.</param>
/// <param name="InTry">Импорт стоит внутри <c>try:</c> — это идиома «необязательная зависимость».</param>
/// <param name="InConstantIf">Импорт стоит внутри ветки <c>if &lt;КОНСТАНТА&gt;:</c> (кроссклиентный код).</param>
/// <param name="AtModuleLevel">Импорт исполняется при импорте модуля, а не внутри функции.</param>
public sealed record ImportBinding(
    string LocalName,
    string Module,
    int Line,
    bool InTry,
    bool InConstantIf,
    bool AtModuleLevel);

/// <summary>Один файл мода вместе с разобранной лексической моделью.</summary>
public sealed class ModSourceFile
{
    private IReadOnlyDictionary<string, ImportBinding>? _bindings;
    private IReadOnlySet<string>? _shadowed;

    /// <summary>Абсолютный путь на диске.</summary>
    public required string FullPath { get; init; }

    /// <summary>Путь относительно корня проекта, прямые слэши.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Содержимое файла.</summary>
    public required string Text { get; init; }

    /// <summary>Лексическая модель.</summary>
    public required PythonLineModel Model { get; init; }

    /// <summary>Файл начинается с UTF-8 BOM.</summary>
    public bool HasBom { get; init; }

    /// <summary>
    /// Это точка входа: <c>&lt;src&gt;/res/scripts/client/gui/mods/mod_*.py</c>.
    /// Загрузчик клиента импортирует ТОЛЬКО такие файлы (и только <c>.pyc</c>).
    /// </summary>
    public bool IsEntryPoint { get; init; }

    /// <summary>
    /// Файл лежит в цепочке импорта точки входа: его код исполнится ещё до <c>init()</c>,
    /// поэтому необработанное исключение здесь закрывает клиент.
    /// </summary>
    public bool InEntryImportChain { get; init; }

    /// <summary>
    /// Импортируемое имя модуля внутри мода: <c>gui.mods.hello_tanker.main</c>.
    /// Пустая строка, если файл не лежит в <c>res/scripts/</c>.
    /// </summary>
    public string DottedModule { get; init; } = string.Empty;

    /// <summary>Имена, привязанные к модулям (<c>import X</c>, <c>import X as Y</c>, <c>from P import M</c>).</summary>
    public IReadOnlyDictionary<string, ImportBinding> Bindings => _bindings ??= BuildBindings();

    /// <summary>
    /// Имена, которые в этом файле переприсваиваются или являются параметрами функций.
    /// Такие имена НЕЛЬЗЯ считать привязанными к модулю: <c>vehicles</c> из
    /// <c>items.vehicles</c> и локальная переменная <c>vehicles</c> — разные вещи.
    /// </summary>
    public IReadOnlySet<string> ShadowedNames => _shadowed ??= BuildShadowed();

    private Dictionary<string, ImportBinding> BuildBindings()
    {
        var map = new Dictionary<string, ImportBinding>(StringComparer.Ordinal);

        foreach (PythonStatement st in Model.Statements)
        {
            string code = st.Trimmed;

            if (st.StartsWith("import"))
            {
                foreach (string piece in code[6..].Split(','))
                {
                    string p = piece.Trim();
                    if (p.Length == 0) continue;

                    string module = p, local;
                    int asAt = p.IndexOf(" as ", StringComparison.Ordinal);
                    if (asAt >= 0)
                    {
                        module = p[..asAt].Trim();
                        local = p[(asAt + 4)..].Trim();
                    }
                    else
                    {
                        int dot = p.IndexOf('.');
                        // import a.b.c вводит имя `a`, а не `a.b.c`
                        local = dot < 0 ? p : p[..dot];
                        if (dot >= 0) module = p[..dot];
                    }
                    if (!IsIdentifier(local) || !IsDottedName(module)) continue;
                    map[local] = new ImportBinding(local, module, st.Line, st.InTry, st.InConstantIf, st.IsModuleLevel);
                }
                continue;
            }

            if (!st.StartsWith("from")) continue;

            int importAt = code.IndexOf(" import ", StringComparison.Ordinal);
            if (importAt < 0) continue;
            string parent = code[4..importAt].Trim();
            string names = code[(importAt + 8)..].Trim().Trim('(', ')');
            if (names.StartsWith('*') || !IsDottedName(parent)) continue;

            foreach (string piece in names.Split(','))
            {
                string p = piece.Trim();
                if (p.Length == 0) continue;
                string name = p, local = p;
                int asAt = p.IndexOf(" as ", StringComparison.Ordinal);
                if (asAt >= 0) { name = p[..asAt].Trim(); local = p[(asAt + 4)..].Trim(); }
                if (!IsIdentifier(name) || !IsIdentifier(local)) continue;
                // Привязка к модулю имеет смысл, только если parent.name — тоже модуль;
                // проверку делает потребитель (у него есть оракул).
                map[local] = new ImportBinding(local, parent + "." + name, st.Line, st.InTry, st.InConstantIf, st.IsModuleLevel);
            }
        }

        return map;
    }

    private HashSet<string> BuildShadowed()
    {
        var shadowed = new HashSet<string>(StringComparer.Ordinal);

        foreach (PythonDefinition def in Model.Definitions)
        {
            foreach (string p in def.ParameterNames) shadowed.Add(p);
            shadowed.Add(def.Name);
        }

        foreach (PythonStatement st in Model.Statements)
        {
            string code = st.Trimmed;

            if (st.StartsWith("for"))
            {
                int inAt = code.IndexOf(" in ", StringComparison.Ordinal);
                if (inAt > 4)
                    foreach (string piece in code[4..inAt].Split(','))
                        AddIfIdentifier(shadowed, piece);
                continue;
            }

            if (st.StartsWith("except"))
            {
                int comma = code.LastIndexOf(',');
                int asAt = code.IndexOf(" as ", StringComparison.Ordinal);
                if (asAt > 0) AddIfIdentifier(shadowed, code[(asAt + 4)..].TrimEnd(':'));
                else if (comma > 0) AddIfIdentifier(shadowed, code[(comma + 1)..].TrimEnd(':'));
                continue;
            }

            if (st.StartsWith("import") || st.StartsWith("from")) continue;

            int eq = IndexOfAssignment(code);
            if (eq <= 0) continue;
            foreach (string piece in code[..eq].Split(','))
                AddIfIdentifier(shadowed, piece);
        }

        // Имя, привязанное импортом, «затенённым» считать нельзя, если больше его никто
        // не трогает: def-ы и параметры уже учтены выше, а сам импорт в цикл не попал.
        return shadowed;
    }

    private static void AddIfIdentifier(HashSet<string> set, string raw)
    {
        string p = raw.Trim().Trim('(', ')', '[', ']').Trim();
        if (IsIdentifier(p)) set.Add(p);
    }

    private static int IndexOfAssignment(string code)
    {
        int depth = 0;
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == '=' && depth == 0)
            {
                if (i + 1 < code.Length && code[i + 1] == '=') { i++; continue; }
                if (i > 0 && code[i - 1] is '=' or '!' or '<' or '>' or '+' or '-' or '*' or '/' or '%' or '|' or '&' or '^') continue;
                return i;
            }
        }
        return -1;
    }

    private static bool IsIdentifier(string s)
    {
        if (s.Length == 0) return false;
        if (!char.IsLetter(s[0]) && s[0] != '_') return false;
        foreach (char c in s) if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    private static bool IsDottedName(string s)
    {
        if (s.Length == 0) return false;
        foreach (string part in s.Split('.')) if (!IsIdentifier(part)) return false;
        return true;
    }
}

/// <summary>
/// Настройки прогона. Каждое правило можно выключить отдельно — именно поэтому правила
/// разложены по классам, а не свалены в один анализатор.
/// </summary>
public sealed class PreflightOptions
{
    /// <summary>Идентификаторы выключенных правил (<c>WOT013</c>, <c>WOT020</c>…).</summary>
    public HashSet<string> DisabledRules { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Декораторы-предохранители проекта: функция под таким декоратором считается обёрнутой
    /// в <c>try/except</c>. В эталонном шаблоне это <c>@_safe</c>; свой список — единственный
    /// способ убрать ложные срабатывания WOT005/WOT006 у мода с собственной обёрткой.
    /// </summary>
    public List<string> SafeDecorators { get; } = new()
    {
        "_safe", "safe", "safe_call", "_safe_call", "guard", "_guard", "catch_all", "_catch_all",
        "process", "adisp_process"
    };

    /// <summary>
    /// Функции-обёртки, которые САМИ ловят исключения обработчика (XVM <c>registerEvent</c>,
    /// свои <c>subscribe_safe</c>). Регистрация через них не требует try/except в обработчике.
    /// </summary>
    public List<string> SafeSubscribeWrappers { get; } = new() { "registerEvent", "subscribe_safe" };

    /// <summary>
    /// Декораторы-патчеры, у которых оригинал приходит ПЕРВЫМ аргументом обёртки
    /// (<c>@overrideMethod(Cls,'m')</c> XVM, <c>@overrideIn(Cls)</c> responsive_reticle).
    /// Без этого списка WOT007 ошибается на всём XVM-подобном коде.
    /// </summary>
    public List<string> PatchDecorators { get; } = new() { "overrideMethod", "overrideIn", "override", "hook" };

    /// <summary>
    /// Внешние зависимости проекта: модули, которых в клиенте нет и не должно быть
    /// (нативные <c>.pyd</c> фреймворков, API чужих модов). WOT002 их не трогает.
    /// </summary>
    public List<string> ExternalDependencies { get; } = new();

    /// <summary>
    /// Проект — БИБЛИОТЕКА для других модов, а не мод: точки входа <c>mod_*.py</c> у него
    /// нет и не должно быть. Так устроены ModsListAPI, ModsSettingsAPI и XVM — в их
    /// <c>.mtmod</c> нет ни одного <c>mod_*.pyc</c>, их поднимает сторонний загрузчик
    /// или импортируют соседние моды. Признак выключает «нет точки входа» в WOT014.
    /// </summary>
    public bool IsLibrary { get; set; }

    /// <summary>Максимальный размер файла мода для анализа, байт. Больше — файл пропускается.</summary>
    public int MaxFileLength { get; init; } = 4_000_000;

    /// <summary>Правило включено.</summary>
    /// <param name="ruleId">Идентификатор.</param>
    /// <returns><c>true</c>, если правило надо запускать.</returns>
    public bool IsEnabled(string ruleId) => !DisabledRules.Contains(ruleId);
}

/// <summary>Всё, что правило видит: проект, его файлы, индекс клиента и настройки.</summary>
public sealed class PreflightContext
{
    /// <summary>Проект мода. Может быть <c>null</c> при анализе одиночных файлов в тестах.</summary>
    public ModProject? Project { get; init; }

    /// <summary>Корень проекта на диске.</summary>
    public string ProjectDir { get; init; } = string.Empty;

    /// <summary>Файлы мода (<c>.py</c> под <c>src/</c>).</summary>
    public required IReadOnlyList<ModSourceFile> Files { get; init; }

    /// <summary>Оракул клиентского API. Никогда не <c>null</c>; может быть «не готов».</summary>
    public required ClientApiOracle Api { get; init; }

    /// <summary>Настройки прогона.</summary>
    public PreflightOptions Options { get; init; } = new();

    /// <summary>Имена модулей, которые принадлежат самому моду (и потому импортируются законно).</summary>
    public IReadOnlySet<string> OwnModules { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>В дереве мода есть <c>def fini()</c> — то есть мод умеет выгружаться.</summary>
    public bool HasFini { get; init; }

    /// <summary>Декоратор входит в список предохранителей проекта.</summary>
    /// <param name="decorators">Декораторы определения.</param>
    /// <returns><c>true</c>, если хотя бы один — предохранитель.</returns>
    public bool IsGuardedByDecorator(IReadOnlyList<string> decorators)
    {
        ArgumentNullException.ThrowIfNull(decorators);
        foreach (string d in decorators)
        {
            string name = d;
            int dot = name.LastIndexOf('.');
            if (dot >= 0) name = name[(dot + 1)..];
            foreach (string safe in Options.SafeDecorators)
                if (string.Equals(name, safe, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

/// <summary>
/// Одно правило preflight. Аналог <c>DiagnosticAnalyzer</c> из Roslyn, только лексический
/// и заточенный под загрузчик модов «Мира танков».
/// </summary>
public interface IPreflightRule
{
    /// <summary>Идентификатор: <c>WOT001</c>…<c>WOT021</c>.</summary>
    string Id { get; }

    /// <summary>Короткое название для списка правил в настройках.</summary>
    string Title { get; }

    /// <summary>Правило включено по умолчанию.</summary>
    bool EnabledByDefault => true;

    /// <summary>Проверяет проект и дописывает находки.</summary>
    /// <param name="context">Контекст.</param>
    /// <param name="output">Приёмник находок.</param>
    void Analyze(PreflightContext context, ICollection<PreflightDiagnostic> output);
}

/// <summary>Основа для правил, работающих по одному файлу за раз.</summary>
public abstract class PerFileRule : IPreflightRule
{
    /// <inheritdoc/>
    public abstract string Id { get; }

    /// <inheritdoc/>
    public abstract string Title { get; }

    /// <inheritdoc/>
    public void Analyze(PreflightContext context, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);
        foreach (ModSourceFile file in context.Files) AnalyzeFile(context, file, output);
    }

    /// <summary>Проверяет один файл.</summary>
    /// <param name="context">Контекст.</param>
    /// <param name="file">Файл.</param>
    /// <param name="output">Приёмник находок.</param>
    protected abstract void AnalyzeFile(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output);

    /// <summary>Собирает находку с координатами из файла.</summary>
    /// <param name="file">Файл.</param>
    /// <param name="line">Строка, 1-based.</param>
    /// <param name="severity">Серьёзность.</param>
    /// <param name="message">Сообщение с последствием.</param>
    /// <param name="hint">Как чинить.</param>
    /// <param name="reference">Ссылка на исходник клиента.</param>
    /// <param name="column">Колонка, 1-based.</param>
    /// <returns>Находка.</returns>
    protected PreflightDiagnostic At(ModSourceFile file, int line, PreflightSeverity severity, string message,
        string? hint = null, string? reference = null, int column = 0) =>
        new(Id, severity, file.FullPath, line, column, message)
        {
            Hint = hint,
            Reference = reference,
            RelativePath = file.RelativePath,
            Snippet = file.Model.LineAt(line)?.Raw.Trim()
        };
}
