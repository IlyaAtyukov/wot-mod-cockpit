namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT020. <c>print</c> вместо логирования.
///
/// <para><b>Последствие.</b> <c>print</c> пишет в <c>sys.stdout</c>, который движок
/// перенаправляет: строка попадёт в <c>python.log</c> без уровня, без имени модуля и без времени,
/// а с py2-<c>str</c> в неизвестной кодировке может ещё и бросить <c>UnicodeEncodeError</c>.
/// Фильтр «моё» в кокпите такую строку не увидит — у неё нет вашего тега.</para>
///
/// <para>Ценность правила невысока и честно измерена: 11 срабатываний на корпусе, ВСЕ
/// в dev-путях XVM (<c>xfw/logger.py</c>, <c>xvm_main/stats.py</c>). Поэтому
/// <see cref="PreflightSeverity.Info"/> и без давления.</para>
/// </summary>
public sealed class PrintStatementRule : PerFileRule
{
    /// <inheritdoc/>
    public override string Id => "WOT020";

    /// <inheritdoc/>
    public override string Title => "print вместо логирования";

    /// <inheritdoc/>
    protected override void AnalyzeFile(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);

        foreach (PythonStatement st in file.Model.Statements)
        {
            string code = st.Trimmed;
            if (!code.StartsWith("print", StringComparison.Ordinal)) continue;
            if (code.Length > 5 && code[5] is not (' ' or '(')) continue;

            output.Add(At(file, st.Line, PreflightSeverity.Info,
                "print пишет прямо в перенаправленный stdout: строка попадёт в python.log без уровня, " +
                "без имени модуля и без вашего тега, поэтому фильтр «моё» в кокпите её не поймает. " +
                "На py2-строке в неизвестной кодировке print ещё и способен бросить UnicodeEncodeError.",
                "Замените на _logger.info('%s …', LOG_PREFIX, …) — stdlib logging попадает в python.log " +
                "как «INFO: [MyMod] …»."));
        }
    }
}
