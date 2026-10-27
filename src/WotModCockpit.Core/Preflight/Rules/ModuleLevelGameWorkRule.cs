namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT001. Код уровня модуля делает игровую работу вне <c>try/except</c>.
///
/// <para><b>Последствие.</b> <c>scripts/client/gui/mods/__init__.py:_findValidMODs</c> зовёт
/// <c>importlib.import_module</c> в блоке, где стоит ТОЛЬКО <c>except ImportError</c>.
/// Любое другое исключение при импорте (<c>AttributeError</c>, <c>TypeError</c>, <c>KeyError</c>)
/// проходит насквозь через <c>gui.shared.personality.init()</c> в
/// <c>scripts/client/game.py:init()</c>, где на строках 127-129 стоит
/// <c>except Exception: LOG_CURRENT_EXCEPTION(); BigWorld.quit()</c> — КЛИЕНТ ЗАКРЫВАЕТСЯ.
/// Заодно обрывается <c>forEach</c>, и моды, чья очередь ещё не наступила, не получат
/// <c>init()</c> вовсе.</para>
///
/// <para><b>Это не теория.</b> Ни один реальный мод из корпуса разведки, кроме XVM, не имеет
/// <c>init()</c>: и <c>battlehits</c>, и <c>mtrmanager</c>, и <c>modsListApi</c>, и ОФИЦИАЛЬНЫЙ
/// <c>lesta_replays_upload</c> делают всю работу на уровне модуля. На 188 файлов пришлось
/// 200 исполняемых операторов нулевого отступа, при этом хоть какой-то модульный <c>try</c>
/// есть лишь в 5 файлах.</para>
/// </summary>
public sealed class ModuleLevelGameWorkRule : PerFileRule
{
    /// <inheritdoc/>
    public override string Id => "WOT001";

    /// <inheritdoc/>
    public override string Title => "Игровая работа на уровне модуля без try/except";

    /// <inheritdoc/>
    protected override void AnalyzeFile(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);

        HashSet<string> gameNames = GameBoundNames(context, file);

        foreach (PythonStatement st in file.Model.Statements)
        {
            if (!st.IsModuleLevel) continue;
            if (st.InTry) continue;                                  // защищено — это и есть лечение
            if (IsStructural(st)) continue;

            string code = st.Trimmed;
            bool bare = PreflightSyntax.IsBareCall(code, out string callee);

            string? touched = FirstGameName(code, gameNames);
            if (touched is null && !bare) continue;

            PreflightSeverity severity = file.InEntryImportChain
                ? PreflightSeverity.Error
                : PreflightSeverity.Warning;

            string what;
            if (touched is not null)
            {
                what = $"обращается к игровому API ({touched})";
            }
            else
            {
                // Голый вызов СВОЕЙ функции, которая игрового API не трогает, — это обычно
                // сборка собственного синглтона мода. Она клиент не роняет; шуметь тут нельзя.
                if (!CallTouchesGame(context, file, callee, gameNames))
                {
                    output.Add(At(file, st.Line, PreflightSeverity.Info,
                        $"Вызов {callee}() исполняется при импорте модуля. Пока он не трогает игровое API, это безопасно, " +
                        "но любая будущая правка внутри превратит его в риск закрытия клиента.",
                        "Перенесите вызов в init(), обёрнутый в try/except Exception.",
                        "scripts/client/gui/mods/__init__.py:_findValidMODs"));
                    continue;
                }
                what = $"вызывает {callee}(), которая работает с игровым API";
            }

            output.Add(At(file, st.Line, severity,
                $"Оператор исполняется ПРИ ИМПОРТЕ мода и {what}. Исключение отсюда загрузчик не поймает " +
                "(в _findValidMODs стоит только except ImportError), оно дойдёт до game.py:init(), где " +
                "выполняется LOG_CURRENT_EXCEPTION() и BigWorld.quit() — клиент закроется, а моды после вашего " +
                "не получат init() вообще.",
                "Уберите работу с игровым API на уровень init() и оберните её в try/except Exception " +
                "(или закройте эту строку модульным try/except).",
                "scripts/client/game.py:127-129"));
        }
    }

    /// <summary>Оператор структурный: импорт, объявление, декоратор, <c>__all__</c>, литеральная константа.</summary>
    private static bool IsStructural(PythonStatement st)
    {
        string code = st.Trimmed;

        if (code.StartsWith('@')) return true;
        if (st.StartsWith("import") || st.StartsWith("from")) return true;
        if (st.StartsWith("def") || st.StartsWith("class")) return true;
        if (st.StartsWith("global") || st.StartsWith("pass") || st.StartsWith("del")) return true;
        if (code.EndsWith(':')) return true;                      // заголовок if/try/for/while/with

        int eq = PreflightSyntax.IndexOfAssignment(code);
        if (eq > 0 && PreflightSyntax.IsLiteralExpression(code[(eq + 1)..])) return true;

        return false;
    }

    /// <summary>Имена файла, привязанные к ИГРОВЫМ модулям (клиент или движок).</summary>
    private static HashSet<string> GameBoundNames(PreflightContext context, ModSourceFile file)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (ImportBinding b in file.Bindings.Values)
            if (PreflightSyntax.IsGameModule(context.Api, context, b.Module)) names.Add(b.LocalName);
        return names;
    }

    private static string? FirstGameName(string code, HashSet<string> gameNames)
    {
        if (gameNames.Count == 0) return null;
        foreach ((string name, _) in PreflightSyntax.RootIdentifiers(code))
            if (gameNames.Contains(name)) return name;
        return null;
    }

    /// <summary>
    /// Тело локальной функции трогает игровое API (в том числе ленивым импортом внутри себя).
    /// Именно так устроен опасный вариант: <c>_install()</c> на уровне модуля, а внутри
    /// <c>import Avatar</c> и <c>getattr</c>.
    /// </summary>
    private static bool CallTouchesGame(PreflightContext context, ModSourceFile file, string callee, HashSet<string> gameNames)
    {
        string name = callee.Split('.')[0];
        PythonDefinition? def = file.Model.FindTopLevelDefinition(name);
        if (def is null) return true;                              // не наша функция — считаем игровой

        foreach (PythonStatement body in file.Model.BodyOf(def))
        {
            string code = body.Trimmed;

            // Ленивый импорт игрового модуля внутри функции — тоже игровая работа
            // (и именно так выглядит правильно написанный, но неудачно вызванный код).
            if (body.StartsWith("import") || body.StartsWith("from"))
            {
                foreach (string module in PreflightSyntax.ImportedModules(body))
                    if (PreflightSyntax.IsGameModule(context.Api, context, module)) return true;
                continue;
            }

            foreach ((string id, _) in PreflightSyntax.RootIdentifiers(code))
                if (gameNames.Contains(id)) return true;
        }
        return false;
    }

}
