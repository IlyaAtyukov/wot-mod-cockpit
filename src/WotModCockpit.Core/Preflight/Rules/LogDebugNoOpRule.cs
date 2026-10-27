namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT013. <c>debug_utils.LOG_DEBUG</c> на розничном клиенте — пустая заглушка.
///
/// <para><b>Факт из клиента.</b> <c>scripts/common/debug_utils.py</c>: <c>LOG_DEBUG</c> объявлена
/// как <c>@_LogWrapper(LOG_LEVEL.SVR_RELEASE)</c> (строка 242), <c>SVR_RELEASE = 4</c>,
/// <c>RELEASE = 5</c> (строки 39-40), а на розничном клиенте <c>_logLevel = LOG_LEVEL.RELEASE</c>
/// (строка 55). <c>_LogWrapper.__call__</c> пишет только <c>if self.__lvl &gt;= _logLevel</c>
/// (строка 66): 4 &gt;= 5 ложно — вызов не делает НИЧЕГО.
/// <c>LOG_NOTE</c>/<c>LOG_WARNING</c>/<c>LOG_ERROR</c>/<c>LOG_CURRENT_EXCEPTION</c> объявлены
/// с <c>LOG_LEVEL.RELEASE</c> и работают; stdlib <c>logging</c> тоже работает.</para>
///
/// <para>Честная оценка ценности низкая: во всех 28 распакованных модах корпуса
/// <c>LOG_DEBUG</c> не встречается ни разу. Это ловушка для новичка, а не проблема экосистемы,
/// поэтому <see cref="PreflightSeverity.Info"/> и без шума.</para>
/// </summary>
public sealed class LogDebugNoOpRule : PerFileRule
{
    private static readonly string[] Names = { "LOG_DEBUG_DEV_NICE", "LOG_DEBUG_DEV", "LOG_DEBUG" };

    /// <inheritdoc/>
    public override string Id => "WOT013";

    /// <inheritdoc/>
    public override string Title => "LOG_DEBUG на релизном клиенте ничего не пишет";

    /// <inheritdoc/>
    protected override void AnalyzeFile(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);

        foreach (PythonStatement st in file.Model.Statements)
        {
            string code = st.Trimmed;
            foreach (string name in Names)
            {
                if (PreflightSyntax.FindCall(code, name) < 0) continue;

                output.Add(At(file, st.Line, PreflightSeverity.Info,
                    $"{name} на розничном клиенте — пустая заглушка: её уровень SVR_RELEASE=4 меньше " +
                    "_logLevel=RELEASE=5, и _LogWrapper.__call__ просто ничего не делает. Записи в python.log " +
                    "не будет, и вы будете час искать несуществующую проблему.",
                    "Возьмите LOG_NOTE/LOG_WARNING/LOG_ERROR (уровень RELEASE) или stdlib logging: " +
                    "_logger = logging.getLogger('MyMod'); _logger.info(...).",
                    "scripts/common/debug_utils.py:39-40, 55, 66, 242"));
                break;
            }
        }
    }
}
