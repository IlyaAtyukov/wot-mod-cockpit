namespace WotModCockpit.Core.Preflight;

/// <summary>
/// WOT002 / WOT003 / WOT004 — сверка кода мода с индексом клиента (дерево исходников, которое
/// показывает браузер API). Самое ценное правило набора: мод компилируется ВНУТРИ игры и падает
/// только в рантайме одной строкой в <c>python.log</c>, а индекс знает точный состав клиента
/// и отвечает на вопрос «этого больше нет» до запуска.
/// <para>Версию клиента тексты находок не называют: индекс строится по тому дереву, что
/// выбрано в кокпите, и после перехода на новый клиент «1.44» в сообщении было бы неправдой.</para>
///
/// <list type="bullet">
///   <item><b>WOT002</b> — импортируется модуль, которого в клиенте нет.</item>
///   <item><b>WOT003</b> — модуль есть, а имени в нём нет (<c>from X import Y</c>).
///         Самый ценный вид: именно так выглядит «игра переехала».</item>
///   <item><b>WOT004</b> — обращение <c>X.attr</c> к атрибуту, которого в модуле нет.</item>
/// </list>
///
/// <para><b>Три приёма подавления ложных срабатываний</b> (замер разведки на 188 файлах
/// реальных модов: 124 → 5 → 2):</para>
/// <list type="number">
///   <item>транзитивное разворачивание <c>from X import *</c> при построении экспортов
///         (убрало 119 срабатываний: одни только <c>dependency.instance</c>/<c>descriptor</c>
///         давали 108, потому что <c>helpers/dependency.py</c> — чистый ре-экспорт);</item>
///   <item>снятие привязки у затенённых имён — если имя переприсвоено или является параметром
///         функции в этом же файле, оно уже не модуль;</item>
///   <item>отрицательный ретроспективный просмотр на точку: без него
///         <c>player.arena.vehicles.get(…)</c> трактовалось как обращение к модулю
///         <c>items.vehicles</c> (3 ложных из 5).</item>
/// </list>
///
/// <para>Плюс безусловные подавления: всё внутри <c>try:</c> (это идиома кроссмодового API —
/// официальный мод Lesta пишет <c>try: from gui import SystemMessages / except: import
/// SystemMessages</c>), нативные модули движка, stdlib, дополнительные .pkg игры и объявленные
/// внешние зависимости проекта.</para>
/// </summary>
public sealed class UnknownApiRule : IPreflightRule
{
    /// <summary>Идентификатор проверки модулей.</summary>
    public const string ModuleRuleId = "WOT002";

    /// <summary>Идентификатор проверки имён в <c>from … import …</c>.</summary>
    public const string NameRuleId = "WOT003";

    /// <summary>Идентификатор проверки атрибутов модуля.</summary>
    public const string AttributeRuleId = "WOT004";

    /// <inheritdoc/>
    public string Id => ModuleRuleId;

    /// <inheritdoc/>
    public string Title => "Символа нет в вашем клиенте (модуль / имя / атрибут)";

    /// <inheritdoc/>
    public void Analyze(PreflightContext context, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        // Без индекса сверять не с чем. Молчать здесь обязательно: дерева исходников
        // на машине может не быть, и это штатная ситуация, а не сбой.
        if (!context.Api.IsReady) return;

        foreach (ModSourceFile file in context.Files)
        {
            if (context.Options.IsEnabled(ModuleRuleId) || context.Options.IsEnabled(NameRuleId))
                CheckImports(context, file, output);
            if (context.Options.IsEnabled(AttributeRuleId))
                CheckAttributes(context, file, output);
        }
    }

    // ------------------------------------------------------------------ WOT002 / WOT003

    private static void CheckImports(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output)
    {
        foreach (PythonStatement st in file.Model.Statements)
        {
            // Защищённый импорт — законная идиома, и ветка except тоже: официальный мод Lesta
            // пишет `try: import urllib2 / except ImportError: import urllib.request as urllib2`,
            // где второй импорт заведомо не для этого клиента.
            if (st.InTry || st.InExcept) continue;
            string code = st.Trimmed;

            if (st.StartsWith("import"))
            {
                foreach (string piece in code[6..].Split(','))
                {
                    string p = piece.Trim();
                    int asAt = p.IndexOf(" as ", StringComparison.Ordinal);
                    if (asAt >= 0) p = p[..asAt].Trim();
                    if (p.Length == 0) continue;
                    CheckModule(context, file, st, p, output);
                }
                continue;
            }

            if (!st.StartsWith("from")) continue;

            int importAt = code.IndexOf(" import ", StringComparison.Ordinal);
            if (importAt < 4) continue;

            string module = code[4..importAt].Trim();
            if (module.Length == 0 || module[0] == '.') continue;      // явный относительный импорт
            if (module == "__future__") continue;

            if (!CheckModule(context, file, st, module, output)) continue;
            if (!context.Options.IsEnabled(NameRuleId)) continue;

            string names = code[(importAt + 8)..].Trim().Trim('(', ')');
            if (names.StartsWith('*')) continue;

            ModuleFacts? facts = context.Api.FactsOf(module);
            if (facts is null || facts.IsOpaque) continue;             // непрозрачный модуль не проверяем

            foreach (string piece in names.Split(','))
            {
                string p = piece.Trim();
                int asAt = p.IndexOf(" as ", StringComparison.Ordinal);
                if (asAt >= 0) p = p[..asAt].Trim();
                if (p.Length == 0 || !IsIdentifier(p)) continue;

                if (facts.Exports.Contains(p)) continue;
                if (context.Api.HasSubmodule(module, p)) continue;

                PreflightSeverity severity = st.InConstantIf ? PreflightSeverity.Warning : PreflightSeverity.Error;
                output.Add(new PreflightDiagnostic(NameRuleId, severity, file.FullPath, st.Line, 0,
                    $"Модуль {module} в индексе клиента есть, а имени {p} в нём НЕТ. На запуске это ImportError: " +
                    "загрузчик поймает его молча (в _findValidMODs стоит except ImportError) и просто не поднимет " +
                    "мод — ни одной внятной строки в python.log не будет.")
                {
                    Hint = $"Найдите замену в браузере API (вкладка «API», поиск по {module}) или сделайте " +
                           $"импорт необязательным: try: from {module} import {p} / except ImportError: {p} = None.",
                    Reference = "индекс клиента (браузер API), " + module,
                    RelativePath = file.RelativePath,
                    Snippet = file.Model.LineAt(st.Line)?.Raw.Trim()
                });
            }
        }
    }

    /// <summary>Проверяет модуль. Возвращает <c>true</c>, если модуль существует и его имена можно сверять.</summary>
    private static bool CheckModule(PreflightContext context, ModSourceFile file, PythonStatement st,
        string module, ICollection<PreflightDiagnostic> output)
    {
        if (module.Length == 0 || module[0] == '.') return false;
        if (IsOwn(context, module)) return false;
        if (ClientApiOracle.IsNative(module) || ClientApiOracle.IsBuiltin(module)) return false;
        if (ClientApiOracle.IsModApi(module)) return false;                  // это забота WOT018
        foreach (string external in context.Options.ExternalDependencies)
            if (module == external || module.StartsWith(external + ".", StringComparison.Ordinal)) return false;

        if (context.Api.ModuleExists(module)) return true;

        // `os.path` — не файл, а имя, которое os.py сам кладёт в sys.modules. Такой «модуль
        // как атрибут родителя» законен: проверяем родителя и его экспорты, иначе получаем
        // шесть ложных срабатываний на одном только `import os, os.path` (замерено на XVM).
        int lastDot = module.LastIndexOf('.');
        if (lastDot > 0)
        {
            string parent = module[..lastDot];
            string tail = module[(lastDot + 1)..];
            ModuleFacts? parentFacts = context.Api.FactsOf(parent);
            // Родитель без исходника (модуль из дополнительного .pkg) НЕ подавляет находку:
            // состав таких пакетов известен по списку модулей целиком, и отсутствие ребёнка
            // в нём — настоящая ошибка (так ловятся frontline/story_mode/battle_royale …states).
            if (parentFacts is not null && (parentFacts.IsOpaque || parentFacts.Exports.Contains(tail))) return false;
        }

        if (!context.Options.IsEnabled(ModuleRuleId)) return false;

        PreflightSeverity severity = st.InConstantIf ? PreflightSeverity.Warning : PreflightSeverity.Error;
        output.Add(new PreflightDiagnostic(ModuleRuleId, severity, file.FullPath, st.Line, 0,
            $"Модуля {module} в вашем клиенте нет (проверено по индексу из {context.Api.ModuleCount} модулей: " +
            "scripts.pkg плюс 21 дополнительный пакет игры). На запуске это ImportError, а его загрузчик " +
            "проглатывает молча — мод просто не появится, и искать причину в логе будет негде.")
        {
            Hint = "Проверьте имя в браузере API либо сделайте зависимость необязательной: " +
                   $"try: import {module} / except ImportError: … (и выключите эту часть мода). " +
                   "Если это внешняя зависимость (.pyd фреймворка, API другого мода) — добавьте её " +
                   "в список внешних зависимостей проекта.",
            Reference = "scripts/client/gui/mods/__init__.py:_findValidMODs (except ImportError)",
            RelativePath = file.RelativePath,
            Snippet = file.Model.LineAt(st.Line)?.Raw.Trim()
        });
        return false;
    }

    // ------------------------------------------------------------------------ WOT004

    private static void CheckAttributes(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output)
    {
        // Привязка «имя -> модуль» только для тех имён, которые в этом файле НЕ затенены.
        var bound = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ImportBinding b in file.Bindings.Values)
        {
            if (file.ShadowedNames.Contains(b.LocalName)) continue;
            if (ClientApiOracle.IsNative(b.Module)) continue;             // .py нет — проверять нечем
            if (!PreflightSyntax.IsGameModule(context.Api, context, b.Module)) continue;
            if (context.Api.FactsOf(b.Module) is null) continue;
            bound[b.LocalName] = b.Module;
        }
        if (bound.Count == 0) return;

        foreach (PythonStatement st in file.Model.Statements)
        {
            if (st.InTry) continue;
            string code = st.Trimmed;
            if (st.StartsWith("import") || st.StartsWith("from")) continue;
            // getattr(mod, name, None) — сознательная проверка наличия, не ошибка.
            if (code.Contains("getattr", StringComparison.Ordinal)) continue;

            foreach ((string root, string attribute, int column) in PreflightSyntax.RootAttributes(code))
            {
                if (!bound.TryGetValue(root, out string? module)) continue;

                ModuleFacts? facts = context.Api.FactsOf(module);
                if (facts is null || facts.IsOpaque) continue;
                if (facts.Exports.Contains(attribute)) continue;
                if (context.Api.HasSubmodule(module, attribute)) continue;

                PreflightSeverity severity = st.IsModuleLevel || InsideInit(file, st)
                    ? PreflightSeverity.Error
                    : PreflightSeverity.Warning;

                string message =
                    $"У модуля {module} нет атрибута {attribute} — импорт пройдёт, а обращение " +
                    $"{root}.{attribute} даст AttributeError в рантайме. " +
                    (severity == PreflightSeverity.Error
                        ? "Здесь это критично: строка исполняется при импорте мода или в init(), " +
                          "то есть исключение дойдёт до game.py:init() и закроет клиент."
                        : "Из обработчика события это исключение вернётся в клиент через Event.__call__ " +
                          "и оборвёт обработчики других модов.");

                output.Add(new PreflightDiagnostic(AttributeRuleId, severity, file.FullPath, st.Line, column, message)
                {
                    RelativePath = file.RelativePath,
                    Snippet = file.Model.LineAt(st.Line)?.Raw.Trim(),
                    Hint = $"Сверьтесь с браузером API по модулю {module}. Если атрибут действительно " +
                           $"необязательный, берите его через getattr({root}, '{attribute}', None) и проверяйте на None.",
                    Reference = $"{module} (индекс клиента, браузер API)"
                });
            }
        }
    }

    private static bool InsideInit(ModSourceFile file, PythonStatement st)
    {
        if (st.EnclosingDefinition < 0) return false;
        PythonDefinition def = file.Model.Definitions[st.EnclosingDefinition];
        return def.IsTopLevel && def.Name is "init" or "fini";
    }

    private static bool IsOwn(PreflightContext context, string module)
    {
        if (context.OwnModules.Contains(module)) return true;
        int dot = module.IndexOf('.');
        return dot > 0 && context.OwnModules.Contains(module[..dot]);
    }

    private static bool IsIdentifier(string s)
    {
        if (s.Length == 0) return false;
        if (!char.IsLetter(s[0]) && s[0] != '_') return false;
        foreach (char c in s) if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }
}
