namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT011. <c>addListener</c> без парного <c>removeListener</c>.
///
/// <para><b>Последствие.</b> Та же утечка, что в WOT009, но через шину событий GUI:
/// обработчик остаётся в <c>g_eventBus</c> и после того, как объект мода уже не нужен.</para>
///
/// <para><b>Правило различает аккуратный и неаккуратный код на реальном материале.</b>
/// XVM держит баланс идеально: в каждом из 18 своих файлов число <c>addListener</c> равно числу
/// <c>removeListener</c> (<c>swf.py</c> 2/2, <c>battle.py</c> 3/3, <c>xvm_main/init.py</c> 7/7).
/// А <c>battlehits/hooks.py</c> и <c>modsListApi/hooks.py</c> делают <c>addListener</c> один раз
/// и <c>removeListener</c> не делают нигде.</para>
///
/// <para>Если у мода нет <c>fini()</c>, он физически не выгружается до закрытия клиента,
/// и утечка ни на что не влияет — тогда это <see cref="PreflightSeverity.Info"/>.</para>
/// </summary>
public sealed class EventBusListenerRule : IPreflightRule
{
    /// <inheritdoc/>
    public string Id => "WOT011";

    /// <inheritdoc/>
    public string Title => "addListener без removeListener";

    /// <inheritdoc/>
    public void Analyze(PreflightContext context, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        int added = 0, removed = 0;
        ModSourceFile? firstFile = null;
        int firstLine = 0;

        foreach (ModSourceFile file in context.Files)
        {
            foreach (PythonStatement st in file.Model.Statements)
            {
                string code = st.Trimmed;
                if (PreflightSyntax.FindCall(code, "addListener") >= 0)
                {
                    added++;
                    if (firstFile is null) { firstFile = file; firstLine = st.Line; }
                }
                if (PreflightSyntax.FindCall(code, "removeListener") >= 0) removed++;
            }
        }

        if (firstFile is null || added <= removed) return;

        PreflightSeverity severity = context.HasFini ? PreflightSeverity.Warning : PreflightSeverity.Info;
        output.Add(new PreflightDiagnostic(Id, severity, firstFile.FullPath, firstLine, 0,
            $"В дереве мода {added} вызовов addListener и только {removed} removeListener. " +
            "Неснятый слушатель остаётся в g_eventBus на всю сессию клиента и будет получать события " +
            (context.HasFini
                ? "уже после fini(), когда состояние мода разобрано."
                : "до самого закрытия клиента (fini() у мода нет, выгрузиться он не может)."))
        {
            Hint = "На каждый addListener(event, handler, scope=...) добавьте removeListener с ТЕМИ ЖЕ " +
                   "аргументами в fini() — именно так это делает XVM в owg_module_fini().",
            Reference = "me.poliroid.modslistapi/hooks.py:44 (addListener без пары)",
            RelativePath = firstFile.RelativePath,
            Snippet = firstFile.Model.LineAt(firstLine)?.Raw.Trim()
        });
    }
}
