namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT010. Самоперепланирующийся <c>BigWorld.callback</c> без сохранения id и <c>cancelCallback</c>.
///
/// <para><b>Последствие.</b> <c>BigWorld.callback(delay, fn)</c> возвращает id; функция, которая
/// ставит саму себя, образует вечный таймер. Если id не гасить, петля переживёт бой и выстрелит
/// в разрушенные объекты — а на выходе из клиента ещё и в полуразобранный движок.</para>
///
/// <para><b>Почему формулировка узкая.</b> Общее правило «любой callback без сохранения id»
/// разведка отвергла замером: 38 срабатываний, из которых 23 — <c>BigWorld.callback(0, …)</c>,
/// то есть штатная отложка на следующий кадр, гасить которую бессмысленно. Контрольный
/// аргумент: сам клиент 1.44 зовёт <c>BigWorld.callback</c> 459 раз, а <c>cancelCallback</c> —
/// только 296. Здесь ловятся ровно петли: в корпусе их 12, и лишь в одном из этих файлов
/// вообще встречается <c>cancelCallback</c> (три такие петли — в ОФИЦИАЛЬНОМ моде Lesta).</para>
/// </summary>
public sealed class CallbackLoopRule : IPreflightRule
{
    /// <inheritdoc/>
    public string Id => "WOT010";

    /// <inheritdoc/>
    public string Title => "Петля BigWorld.callback без cancelCallback";

    /// <inheritdoc/>
    public void Analyze(PreflightContext context, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        bool treeCancels = false;
        foreach (ModSourceFile f in context.Files)
            foreach (PythonStatement st in f.Model.Statements)
                if (st.Trimmed.Contains("cancelCallback", StringComparison.Ordinal)) { treeCancels = true; break; }

        foreach (ModSourceFile file in context.Files)
        {
            foreach (PythonDefinition def in file.Model.Definitions)
            {
                if (def.IsClass) continue;

                foreach (PythonStatement st in file.Model.BodyOf(def))
                {
                    if (st.EnclosingDefinition != def.Index) continue;

                    int open = PreflightSyntax.FindCall(st.Trimmed, "BigWorld.callback");
                    if (open < 0) open = PreflightSyntax.FindCall(st.Trimmed, "callback");
                    if (open < 0) continue;

                    IReadOnlyList<string> args = PreflightSyntax.SplitArguments(st.Trimmed, open);
                    if (args.Count < 2) continue;
                    if (!SchedulesItself(args[1], def.Name)) continue;

                    bool idSaved = PreflightSyntax.IndexOfAssignment(st.Trimmed) > 0;
                    if (idSaved && treeCancels) continue;      // id сохранён и в дереве есть отмена — это порядок

                    string why = !treeCancels
                        ? "во всём дереве мода нет ни одного BigWorld.cancelCallback"
                        : "возвращённый id никуда не сохраняется, погасить эту петлю нечем";

                    output.Add(new PreflightDiagnostic(Id, PreflightSeverity.Warning, file.FullPath, st.Line, 0,
                        $"{def.Name}() перепланирует саму себя через BigWorld.callback, но {why}. " +
                        "Такая петля переживёт и бой, и выгрузку мода: колбэк сработает на уже разрушенных объектах " +
                        (context.HasFini
                            ? "и продолжит работать после fini()."
                            : "и остановить её будет нечем — у мода вообще нет fini()."))
                    {
                        Hint = "Складывайте id в множество и гасите его в fini(): " +
                               "cb_id = BigWorld.callback(...); _callbacks.add(cb_id), а в fini() — " +
                               "BigWorld.cancelCallback(cb_id) в try/except (движок бросает, если колбэк уже сработал).",
                        Reference = "scripts/client/game.py:34-35 autoFlushPythonLog",
                        RelativePath = file.RelativePath,
                        Snippet = file.Model.LineAt(st.Line)?.Raw.Trim()
                    });
                    break;                                     // одна находка на функцию — этого достаточно
                }
            }
        }
    }

    /// <summary>Второй аргумент — сама функция либо <c>lambda …: F(…)</c>.</summary>
    private static bool SchedulesItself(string argument, string functionName)
    {
        string a = argument.Trim();
        if (string.Equals(a, functionName, StringComparison.Ordinal)) return true;
        if (!a.StartsWith("lambda", StringComparison.Ordinal)) return false;
        return PreflightSyntax.FindCall(a, functionName) >= 0;
    }
}
