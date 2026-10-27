namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT018. API другого мода импортируется без <c>try/except ImportError</c>.
///
/// <para><b>Последствие.</b> Порядок <c>init()</c> у модов произвольный:
/// <c>gui/mods/__init__.py</c> обходит <c>set()</c> имён файлов, то есть порядок задан хешами
/// строк — он стабилен между запусками, но меняется при добавлении или удалении ЛЮБОГО мода.
/// Плюс пользователь может просто не поставить зависимость. Голый импорт чужого API на верхнем
/// уровне превращает отсутствие зависимости в <c>ImportError</c>, и весь ваш мод не загрузится.</para>
///
/// <para><b>Устоявшаяся идиома корпуса:</b> <c>battlehits/hooks.py:156-160</c> и
/// <c>mtrmanager/hooks.py:85</c> делают <c>g_modsListApi = None; try: from gui.modsListApi import
/// g_modsListApi; except ImportError: …</c> и дальше проверяют <c>is not None</c>. А
/// <c>izeberg.modssettingsapi/api.py:7</c> делает голый импорт — и весь ModsSettingsAPI не
/// загрузится, если ModsListAPI не установлен.</para>
/// </summary>
public sealed class ModApiImportRule : PerFileRule
{
    /// <inheritdoc/>
    public override string Id => "WOT018";

    /// <inheritdoc/>
    public override string Title => "Импорт API чужого мода без try/except ImportError";

    /// <inheritdoc/>
    protected override void AnalyzeFile(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);

        foreach (PythonStatement st in file.Model.Statements)
        {
            if (!st.StartsWith("import") && !st.StartsWith("from")) continue;

            foreach (string module in PreflightSyntax.ImportedModules(st))
            {
                if (!IsModApi(context, module)) continue;

                if (!st.InTry)
                {
                    // Голым импорт бывает по-разному, и последствие у этих случаев РАЗНОЕ.
                    // На уровне модуля ImportError случается при загрузке мода — весь мод
                    // не поднимется. Внутри функции он случится при ВЫЗОВЕ, и мод загрузится
                    // как ни в чём не бывало; говорить там «загрузчик не поднимет ваш мод»
                    // — прямая неправда.
                    if (st.IsModuleLevel)
                    {
                        output.Add(At(file, st.Line, PreflightSeverity.Warning,
                            $"{module} — API другого мода, а не часть клиента. Порядок init() у модов задаётся " +
                            "сортировкой имён mod_*.pyc и меняется при установке любого другого мода; зависимости " +
                            "может не оказаться вовсе. Голый импорт превратит это в ImportError, и загрузчик " +
                            "не поднимет ВАШ мод целиком.",
                            $"Оберните: try: from {module} import …  except ImportError: <заглушка> = None; " +
                            "и дальше проверяйте на None перед каждым использованием.",
                            "me.poliroid.battlehits/hooks.py:156-160 (как надо), " +
                            "izeberg.modssettingsapi/api.py:7 (как не надо)"));
                        continue;
                    }

                    // Функция, которую зовут ТОЛЬКО из-под try/except, уже защищена вызывающим:
                    // сказать здесь нечего. Ровно так устроен _build_template() в рецепте R15.
                    PythonDefinition? owner = EnclosingDefinition(file, st);
                    if (owner is not null && AllCallSitesGuarded(context, owner)) continue;

                    output.Add(At(file, st.Line, PreflightSeverity.Info,
                        $"{module} — API другого мода, а не часть клиента, и импорт здесь не обёрнут. " +
                        "Импорт локальный, поэтому мод всё равно загрузится: ImportError вылетит при первом " +
                        $"вызове {(owner is null ? "этого кода" : owner.Name + "()")} — то есть уже в рантайме, " +
                        "у пользователя без установленной зависимости.",
                        $"Оберните: try: from {module} import …  except ImportError: return <значение по умолчанию>.",
                        "me.poliroid.battlehits/hooks.py:156-160"));
                    continue;
                }

                // Импорт обёрнут — вторая, более полезная половина правила: используют ли результат
                // без проверки на None. Спрашивать это можно только тогда, когда имя ВООБЩЕ
                // доживает до места, где его можно проверить.
                if (!st.IsModuleLevel && ImportFailureLeavesNothingToCheck(file, st)) continue;

                foreach (string name in ImportedNames(st))
                {
                    if (name.Length == 0 || UsedWithNoneCheck(context, name)) continue;

                    output.Add(At(file, st.Line, PreflightSeverity.Info,
                        $"Импорт {module} защищён try/except, но нигде в дереве мода нет проверки " +
                        $"«{name} is None» / «if {name}». Если API не установлено, имя останется незаданным " +
                        "или равным None, и первое же обращение даст AttributeError уже в рантайме.",
                        $"Инициализируйте {name} = None до try и проверяйте перед использованием."));
                    break;
                }
            }
        }
    }

    /// <summary>Определение, внутри которого стоит оператор, либо <c>null</c> (уровень модуля).</summary>
    private static PythonDefinition? EnclosingDefinition(ModSourceFile file, PythonStatement st) =>
        st.EnclosingDefinition >= 0 && st.EnclosingDefinition < file.Model.Definitions.Count
            ? file.Model.Definitions[st.EnclosingDefinition]
            : null;

    /// <summary>
    /// Все вызовы функции во всём дереве мода стоят под <c>try</c> (и вызовов хотя бы один).
    /// </summary>
    private static bool AllCallSitesGuarded(PreflightContext context, PythonDefinition def)
    {
        int calls = 0;

        foreach (ModSourceFile f in context.Files)
        {
            foreach (PythonStatement st in f.Model.Statements)
            {
                if (st.Index == def.HeaderStatement) continue;
                if (PreflightSyntax.FindCall(st.Trimmed, def.Name, allowMethodReceiver: false) < 0) continue;

                calls++;
                if (!st.InTry) return false;
            }
        }

        return calls > 0;
    }

    /// <summary>
    /// Проверять имя на <c>None</c> после ЛОКАЛЬНОГО защищённого импорта бывает негде,
    /// и тогда требовать проверку — значит выдумывать дефект.
    ///
    /// <para>Две формы, которые реально пишут (обе есть в рецепте R14):</para>
    /// <list type="number">
    ///   <item>каждый <c>except</c> заканчивается <c>return</c>/<c>raise</c> — после неудачного
    ///         импорта до кода с этим именем управление вообще не доходит, а самого имени
    ///         в области видимости не появляется;</item>
    ///   <item>имя используется ТОЛЬКО внутри того же <c>try</c> — до него тоже не дойти.</item>
    /// </list>
    /// </summary>
    private static bool ImportFailureLeavesNothingToCheck(ModSourceFile file, PythonStatement import)
    {
        PythonStatement? tryHeader = EnclosingTry(file.Model, import);
        if (tryHeader is null) return false;

        (int bodyStart, int bodyEnd) = BlockBody(file.Model, tryHeader);

        if (AllHandlersExit(file.Model, tryHeader, bodyEnd)) return true;

        foreach (string name in ImportedNames(import))
        {
            if (name.Length == 0) continue;
            if (UsedOutside(file, import.EnclosingDefinition, name, bodyStart, bodyEnd)) return false;
        }

        return true;
    }

    /// <summary>Ближайший объемлющий <c>try:</c> в пределах той же функции.</summary>
    private static PythonStatement? EnclosingTry(PythonLineModel model, PythonStatement st)
    {
        int indent = st.Indent;
        for (int i = st.Index - 1; i >= 0; i--)
        {
            PythonStatement p = model.Statements[i];
            if (p.Indent >= indent) continue;
            if (p.StartsWith("try")) return p;
            if (p.StartsWith("def") || p.StartsWith("class")) return null;
            indent = p.Indent;
        }
        return null;
    }

    /// <summary>Полуинтервал операторов тела блока, открытого заголовком.</summary>
    private static (int Start, int End) BlockBody(PythonLineModel model, PythonStatement header)
    {
        int start = header.Index + 1;
        int end = start;
        while (end < model.Statements.Count && model.Statements[end].Indent > header.Indent) end++;
        return (start, end);
    }

    /// <summary>
    /// У <c>try</c> есть хотя бы один <c>except</c>, и КАЖДЫЙ выходит из функции
    /// (<c>return</c>/<c>raise</c>/<c>continue</c>).
    /// </summary>
    private static bool AllHandlersExit(PythonLineModel model, PythonStatement tryHeader, int from)
    {
        int handlers = 0;
        int i = from;

        while (i < model.Statements.Count)
        {
            PythonStatement h = model.Statements[i];
            if (h.Indent != tryHeader.Indent) break;
            if (!h.StartsWith("except") && !h.StartsWith("else") && !h.StartsWith("finally")) break;

            (int start, int end) = BlockBody(model, h);
            if (h.StartsWith("except"))
            {
                handlers++;
                bool exits = false;
                for (int k = start; k < end; k++)
                {
                    PythonStatement b = model.Statements[k];
                    if (b.StartsWith("return") || b.StartsWith("raise") || b.StartsWith("continue")) { exits = true; break; }
                }
                if (!exits) return false;
            }
            i = end;
        }

        return handlers > 0;
    }

    /// <summary>Имя упоминается в той же функции вне полуинтервала <c>[start, end)</c>.</summary>
    private static bool UsedOutside(ModSourceFile file, int definitionIndex, string name, int start, int end)
    {
        foreach (PythonStatement st in file.Model.Statements)
        {
            if (st.Index >= start && st.Index < end) continue;
            if (st.EnclosingDefinition != definitionIndex) continue;
            if (st.StartsWith("import") || st.StartsWith("from")) continue;

            foreach ((string id, _) in PreflightSyntax.RootIdentifiers(st.Trimmed))
                if (string.Equals(id, name, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool IsModApi(PreflightContext context, string module)
    {
        // Собственные пакеты мода — не чужое API. Без этой проверки XVM, у которого свои
        // пакеты называются xfw/xvm_main, получал 206 ложных срабатываний.
        if (context.OwnModules.Contains(module)) return false;
        int dot = module.IndexOf('.');
        if (dot > 0 && context.OwnModules.Contains(module[..dot])) return false;

        if (ClientApiOracle.IsModApi(module)) return true;
        foreach (string external in context.Options.ExternalDependencies)
            if (module == external || module.StartsWith(external + ".", StringComparison.Ordinal)) return true;
        return false;
    }

    private static IEnumerable<string> ImportedNames(PythonStatement st)
    {
        string code = st.Trimmed;
        if (!st.StartsWith("from")) yield break;

        int importAt = code.IndexOf(" import ", StringComparison.Ordinal);
        if (importAt < 0) yield break;

        foreach (string piece in code[(importAt + 8)..].Trim().Trim('(', ')').Split(','))
        {
            string p = piece.Trim();
            int asAt = p.IndexOf(" as ", StringComparison.Ordinal);
            if (asAt >= 0) p = p[(asAt + 4)..].Trim();
            if (p.Length > 0 && p != "*") yield return p;
        }
    }

    private static bool UsedWithNoneCheck(PreflightContext context, string name)
    {
        foreach (ModSourceFile f in context.Files)
            foreach (PythonStatement st in f.Model.Statements)
            {
                string code = st.Trimmed;
                if (code.Contains(name + " is None", StringComparison.Ordinal)) return true;
                if (code.Contains(name + " is not None", StringComparison.Ordinal)) return true;
                if (code.Contains(name + " != None", StringComparison.Ordinal)) return true;
                if (st.StartsWith("if") && code.Contains(name, StringComparison.Ordinal)) return true;
            }
        return false;
    }
}
