namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT012. Мод не пампит <c>BigWorld.flushPythonLog</c> — живой лог в кокпите будет отставать.
///
/// <para><b>Факт.</b> <c>scripts/client/game.py:34-35</c> объявляет <c>autoFlushPythonLog()</c>
/// (<c>flushPythonLog</c> + <c>BigWorld.callback(5.0, себя)</c>), но вызывается она на строках
/// 51-52 только под <c>if constants.IS_DEVELOPMENT</c>, а <c>IS_DEVELOPMENT</c> =
/// (<c>realm.CURRENT_REALM == 'DEV'</c>) — на боевом RU это <c>False</c>. То есть на розничном
/// клиенте петли сброса нет вообще, и свежие строки висят в буфере.</para>
///
/// <para>Это НЕ дефект мода: мод без пампинга полностью исправен, просто его лог виден
/// с задержкой, и живой tail этапа 1 выглядит замершим. Поэтому только
/// <see cref="PreflightSeverity.Info"/>.</para>
/// </summary>
public sealed class LogPumpRule : IPreflightRule
{
    /// <inheritdoc/>
    public string Id => "WOT012";

    /// <inheritdoc/>
    public string Title => "Нет пампинга python.log";

    /// <inheritdoc/>
    public void Analyze(PreflightContext context, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);
        if (context.Files.Count == 0) return;

        ModSourceFile? where = null;
        int line = 0;
        bool inLoop = false;

        foreach (ModSourceFile file in context.Files)
        {
            foreach (PythonDefinition def in file.Model.Definitions)
            {
                bool flushes = false, reschedules = false;
                foreach (PythonStatement st in file.Model.BodyOf(def))
                {
                    if (st.EnclosingDefinition != def.Index) continue;
                    string code = st.Trimmed;
                    if (code.Contains("flushPythonLog", StringComparison.Ordinal))
                    {
                        flushes = true;
                        where ??= file;
                        if (line == 0) line = st.Line;
                    }
                    // Петля может быть и косвенной: эталонный шаблон зовёт свою обёртку
                    // _delayed(CB_LOGPUMP, 0.2, _flush_python_log), а не BigWorld.callback
                    // напрямую. Признак петли — функция передаёт куда-то саму себя.
                    if (code.Contains(def.Name + ")", StringComparison.Ordinal)
                        || code.Contains(def.Name + ",", StringComparison.Ordinal)
                        || (code.Contains("callback", StringComparison.Ordinal) && code.Contains("lambda", StringComparison.Ordinal)))
                        reschedules = true;
                }
                if (flushes && reschedules) inLoop = true;
            }

            if (where is null)
                foreach (PythonStatement st in file.Model.Statements)
                    if (st.Trimmed.Contains("flushPythonLog", StringComparison.Ordinal)) { where = file; line = st.Line; break; }
        }

        if (inLoop) return;

        ModSourceFile anchor = where ?? context.Files[0];
        string message = where is null
            ? "Мод нигде не зовёт BigWorld.flushPythonLog. Клиент сбрасывает python.log на диск сам ТОЛЬКО при " +
              "IS_DEVELOPMENT (game.py:51-52), а на боевом реалме этот флаг выключен — свежие строки висят " +
              "в буфере, и живой лог в кокпите будет выглядеть замершим."
            : "BigWorld.flushPythonLog вызывается, но не в самоперепланирующейся петле: одиночный сброс живого " +
              "лога не даёт, следующие строки снова осядут в буфере.";

        output.Add(new PreflightDiagnostic(Id, PreflightSeverity.Info, anchor.FullPath, line, 0, message)
        {
            Hint = "Заведите петлю: def _flush(): BigWorld.flushPythonLog(); _delayed('logpump', 0.2, _flush) — " +
                   "и гасите её колбэк в fini(), сделав последний сброс уже после отмены.",
            Reference = "scripts/client/game.py:34-35, 51-52",
            RelativePath = anchor.RelativePath,
            Snippet = line > 0 ? anchor.Model.LineAt(line)?.Raw.Trim() : null
        });
    }
}
