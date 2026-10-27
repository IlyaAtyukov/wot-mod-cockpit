namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT005. <c>init()</c>/<c>fini()</c> и шесть событий загрузчика не обёрнуты в <c>try/except</c>.
///
/// <para><b>Последствие, и оно двустороннее.</b> <c>gui/mods/__init__.py:_callModMethod</c> ловит
/// ТОЛЬКО <c>AttributeError</c>. Значит: любое другое исключение из <c>init()</c> всплывает
/// в <c>game.py:init()</c> и закрывает клиент; а <c>AttributeError</c> из вашего же <c>init()</c>
/// загрузчик проглотит молча (<c>except AttributeError: pass</c>) — мод «тихо не работает»,
/// и в <c>python.log</c> не будет ни строки.</para>
///
/// <para>Событий, которые рассылает загрузчик, ровно шесть: <c>onAccountShowGUI(ctx)</c>,
/// <c>onAccountBecomePlayer</c>, <c>onAccountBecomeNonPlayer</c>, <c>onAvatarBecomePlayer</c>,
/// <c>onConnected</c>, <c>onDisconnected</c> — все идут через тот же <c>_callModMethod</c>.</para>
/// </summary>
public sealed class EntryPointGuardRule : PerFileRule
{
    /// <summary>Имена, которые вызывает загрузчик клиента. Список закрытый — он зашит в движке.</summary>
    public static IReadOnlyList<string> LoaderCallbacks { get; } = new[]
    {
        "init", "fini",
        "onAccountShowGUI", "onAccountBecomePlayer", "onAccountBecomeNonPlayer",
        "onAvatarBecomePlayer", "onConnected", "onDisconnected"
    };

    /// <inheritdoc/>
    public override string Id => "WOT005";

    /// <inheritdoc/>
    public override string Title => "init()/fini() и события загрузчика без try/except";

    /// <inheritdoc/>
    protected override void AnalyzeFile(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);

        // Эти имена что-то значат только там, куда смотрит загрузчик: в самой точке входа
        // и в модуле, которому она делегирует. В служебном файле мода def init() — своё дело автора.
        if (!file.IsEntryPoint && !file.InEntryImportChain) return;

        foreach (PythonDefinition def in file.Model.Definitions)
        {
            if (!def.IsTopLevel || def.IsClass) continue;
            if (!LoaderCallbacks.Contains(def.Name, StringComparer.Ordinal)) continue;
            if (context.IsGuardedByDecorator(def.Decorators)) continue;
            if (GuardAnalysis.BodyIsGuarded(context, file, def)) continue;
            if (DelegatesToGuarded(context, file, def)) continue;

            // Есть try, но опасное лежит СНАРУЖИ него — покажем строку, а не только заголовок.
            PythonStatement? risky = file.Model.BodyIsGuarded(def)
                ? GuardAnalysis.FirstRiskyStatement(context, file, def)
                : null;

            bool isInit = def.Name == "init";
            string where = risky is null
                ? string.Empty
                : $" В теле есть try, но строка {risky.Line} («{Shorten(risky.RawFirstLine)}») лежит ВНЕ его.";

            output.Add(At(file, risky?.Line ?? def.Line,
                isInit ? PreflightSeverity.Error : PreflightSeverity.Warning,
                isInit
                    ? "init() вызывается загрузчиком напрямую, а _callModMethod ловит только AttributeError. " +
                      "Любое другое исключение отсюда дойдёт до game.py:init(), где стоит BigWorld.quit() — клиент закроется. " +
                      "AttributeError же будет проглочен молча, и мод перестанет работать без единой строки в логе." + where
                    : $"{def.Name}() вызывается загрузчиком через _callModMethod, который ловит только AttributeError. " +
                      "Исключение другого типа уйдёт в клиент, а AttributeError исчезнет молча — отладить это будет нечем." + where,
                "Оберните тело в try/except Exception с записью traceback либо навесьте декоратор-предохранитель " +
                "(@_safe в эталонном шаблоне; свой список декораторов задаётся в настройках preflight).",
                "scripts/client/gui/mods/__init__.py:_callModMethod"));
        }
    }

    private static string Shorten(string s)
    {
        string t = s.Trim();
        return t.Length <= 60 ? t : t[..60] + "…";
    }

    /// <summary>
    /// Тело функции целиком делегирует своей же защищённой функции — ровно так устроен
    /// стаб эталонного шаблона: <c>def onConnected(): _call('onConnected', ())</c>, а
    /// <c>_call</c> ловит всё. Без этой проверки правильно написанный стаб давал шесть
    /// предупреждений на ровном месте.
    /// </summary>
    private static bool DelegatesToGuarded(PreflightContext context, ModSourceFile file, PythonDefinition def)
    {
        bool any = false;

        foreach (PythonStatement st in file.Model.BodyOf(def))
        {
            if (st.EnclosingDefinition != def.Index) continue;
            if (st.StartsWith("return") || st.StartsWith("pass") || st.StartsWith("global")) continue;

            string code = st.Trimmed;
            if (!PreflightSyntax.IsBareCall(code, out string callee)) return false;

            PythonDefinition? target = file.Model.FindTopLevelDefinition(callee.Split('.')[0]);
            if (target is null || target.Index == def.Index) return false;
            if (!context.IsGuardedByDecorator(target.Decorators) && !GuardAnalysis.BodyIsGuarded(context, file, target)) return false;

            any = true;
        }

        return any;
    }
}
