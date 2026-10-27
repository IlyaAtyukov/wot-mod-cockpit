namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT006. Обработчик игрового события не обёрнут в <c>try/except</c>.
///
/// <para><b>Последствие.</b> <c>scripts/common/Event.py</c>, <c>Event.__call__</c>:
/// <code>
/// for delegate in self[:]:
///     try: delegate(*args, **kwargs)
///     except: LOG_CURRENT_EXCEPTION(); raise
/// </code>
/// то есть исключение логируется И ПЕРЕБРАСЫВАЕТСЯ. Из-за <c>raise</c> прерывается весь цикл:
/// делегаты, стоящие в списке ПОСЛЕ вашего, не вызываются вовсе — вы ломаете XVM и pmod
/// на том же событии. <c>SafeEvent</c> исключение глушит, но в клиенте 1.44 его почти нет:
/// 719 вхождений <c>Event.Event(</c> против 10 <c>SafeEvent(</c>, а в <c>PlayerEvents.py</c>
/// все 80 событий объявлены как <c>Event.Event()</c>.</para>
///
/// <para>Замер разведки: из 107 функций, зарегистрированных обработчиками в корпусе реальных
/// модов, try/except есть только у 26 — 76 % не защищены.</para>
/// </summary>
public sealed class EventHandlerGuardRule : PerFileRule
{
    /// <inheritdoc/>
    public override string Id => "WOT006";

    /// <inheritdoc/>
    public override string Title => "Обработчик события без try/except";

    /// <inheritdoc/>
    protected override void AnalyzeFile(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach ((string handler, int line, string via) in Registrations(context, file))
        {
            if (!seen.Add(handler)) continue;

            PythonDefinition? def = file.Model.FindDefinition(handler);
            if (def is null) continue;                                  // обработчик объявлен в другом файле
            if (context.IsGuardedByDecorator(def.Decorators)) continue;
            if (GuardAnalysis.BodyIsGuarded(context, file, def)) continue;

            // Есть try, но опасная работа лежит СНАРУЖИ него: покажем именно эту строку.
            PythonStatement? risky = file.Model.BodyIsGuarded(def)
                ? GuardAnalysis.FirstRiskyStatement(context, file, def)
                : null;
            string where = risky is null
                ? string.Empty
                : $" В теле есть try, но строка {risky.Line} («{Shorten(risky.RawFirstLine)}») лежит ВНЕ его.";

            // Повод у находки бывает разный, и объяснять их одинаково нельзя: у BigWorld.callback
            // никакой цепочки делегатов нет, там исключение уходит прямо в игровой цикл движка.
            bool viaCallback = via.Contains("callback", StringComparison.OrdinalIgnoreCase);

            output.Add(At(file, def.Line, PreflightSeverity.Warning,
                viaCallback
                    ? $"{handler}() поставлен отложенным вызовом ({via}) и не ловит исключений. " +
                      "Колбэк движок зовёт из своего игрового цикла, вне какого-либо try вашего мода: " +
                      "исключение отсюда уйдёт в клиент и попадёт в python.log как ошибка неизвестного " +
                      "происхождения — стек будет обрываться на BigWorld, а не на вашей подписке." + where
                    : $"{handler}() зарегистрирован как обработчик ({via}) и не ловит исключений. " +
                      "События клиента объявлены как Event.Event(), а Event.__call__ делает " +
                      "LOG_CURRENT_EXCEPTION() и raise: ваше исключение вернётся в код игры и оборвёт " +
                      "перебор делегатов — обработчики ДРУГИХ модов на этом же событии вызваны не будут." + where,
                "Оберните тело обработчика в try/except Exception (или навесьте декоратор-предохранитель).",
                viaCallback ? "scripts/client/game.py:autoFlushPythonLog (образец петли BigWorld.callback)"
                            : "scripts/common/Event.py:Event.__call__"));
        }
    }

    private static string Shorten(string s)
    {
        string t = s.Trim();
        return t.Length <= 60 ? t : t[..60] + "…";
    }

    /// <summary>Регистрации обработчиков: <c>+=</c>, <c>addListener</c>, <c>BigWorld.callback</c>.</summary>
    private static IEnumerable<(string Handler, int Line, string Via)> Registrations(PreflightContext context, ModSourceFile file)
    {
        foreach (PythonStatement st in file.Model.Statements)
        {
            string code = st.Trimmed;

            int plus = code.IndexOf("+=", StringComparison.Ordinal);
            if (plus > 0)
            {
                string owner = code[..plus].Trim();
                string handler = code[(plus + 2)..].Trim();
                if (owner.Contains('.', StringComparison.Ordinal) && IsPlainName(handler))
                    yield return (handler, st.Line, owner);
                continue;
            }

            // Регистрация через обёртку проекта, которая сама ловит исключения (XVM registerEvent),
            // защиты в обработчике не требует.
            bool viaSafeWrapper = false;
            foreach (string wrapper in context.Options.SafeSubscribeWrappers)
                if (code.Contains(wrapper + "(", StringComparison.Ordinal)) { viaSafeWrapper = true; break; }
            if (viaSafeWrapper) continue;

            foreach (string call in new[] { "addListener", "BigWorld.callback" })
            {
                int open = PreflightSyntax.FindCall(code, call);
                if (open < 0) continue;

                IReadOnlyList<string> args = PreflightSyntax.SplitArguments(code, open);
                if (args.Count < 2) continue;
                string handler = args[1].Trim();
                if (IsPlainName(handler)) yield return (handler, st.Line, call);
            }
        }
    }

    private static bool IsPlainName(string s)
    {
        if (s.Length == 0) return false;
        if (!char.IsLetter(s[0]) && s[0] != '_') return false;
        foreach (char c in s) if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }
}
