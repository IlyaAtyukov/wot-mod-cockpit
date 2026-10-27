namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT017. Не-ASCII текст без префикса <c>u</c> там, где он уходит в GUI или в лог.
///
/// <para><b>Последствие в логе.</b> <c>BWLogging.BWLogRedirectionHandler.emit</c> делает
/// <c>msg.encode(sys.getdefaultencoding())</c>, а <c>debug_utils._doLog</c> — <c>unicode(msg)</c>.
/// <c>sys.getdefaultencoding()</c> в этом клиенте равна <c>'utf-8'</c>
/// (<c>scripts/common/bw_site.py:7</c>, <c>set_default_encoding()</c> зовётся до загрузки модов),
/// поэтому utf-8-байты проходят, а вот py2-<c>str</c> в cp1251 (например путь из
/// <c>os.getcwd()</c> на русской Windows) даёт <c>UnicodeDecodeError</c>, <c>logging</c> глушит
/// его через <c>handleError</c>, и ЗАПИСЬ ТЕРЯЕТСЯ ЦЕЛИКОМ.</para>
///
/// <para><b>Последствие в GUI.</b> <c>SystemMessages.pushMessage</c> и
/// <c>MessengerEntry.g_instance.gui.addClientMessage</c> должны получать <c>unicode</c>;
/// смешение с py2-<c>str</c> даёт <c>UnicodeDecodeError</c> при конкатенации или
/// <c>.format()</c>. Официальный мод Lesta хранит все русские строки как <c>u"…"</c>.</para>
///
/// <para>Реализованы только локальные, проверяемые в одной строке пункты: литерал с не-ASCII
/// без <c>u</c> в конкатенации/форматировании и такой же литерал в аргументе GUI-вызова.
/// Прослеживание происхождения переменной разведка признала ненадёжным лексически.</para>
/// </summary>
public sealed class StrUnicodeMixRule : PerFileRule
{
    private static readonly string[] GuiCalls =
    {
        "pushMessage", "addClientMessage", "makeTooltip", "makeString", "showI18nInfoMessage"
    };

    /// <inheritdoc/>
    public override string Id => "WOT017";

    /// <inheritdoc/>
    public override string Title => "Смешивание str и unicode в тексте для GUI и лога";

    /// <inheritdoc/>
    protected override void AnalyzeFile(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);

        foreach (PythonPhysicalLine line in file.Model.Lines)
        {
            if (line.IsBlank) continue;

            bool guiCall = false;
            foreach (string call in GuiCalls)
                if (PreflightSyntax.FindCall(line.Code, call) >= 0) { guiCall = true; break; }

            bool formatting = line.Code.Contains('%', StringComparison.Ordinal)
                              || line.Code.Contains(".format(", StringComparison.Ordinal)
                              || line.Code.Contains('+', StringComparison.Ordinal);

            foreach ((string prefix, string content, int column) in PreflightSyntax.StringLiterals(line))
            {
                bool hasNonAscii = false;
                foreach (char c in content) if (c > 127) { hasNonAscii = true; break; }
                if (!hasNonAscii) continue;
                if (prefix.Contains('u', StringComparison.OrdinalIgnoreCase)) continue;

                if (guiCall)
                {
                    output.Add(At(file, line.Number, PreflightSeverity.Warning,
                        "Не-ASCII литерал без префикса u уходит в GUI. В Python 2.7 это py2-str: " +
                        "SystemMessages.pushMessage и addClientMessage ждут unicode, и при склейке с любой " +
                        "другой строкой получится UnicodeDecodeError — сообщение игрок не увидит.",
                        "Поставьте префикс u: u'Мод загружен'.",
                        "scripts/client/gui/SystemMessages.py:pushMessage", column));
                    continue;
                }

                if (formatting)
                {
                    output.Add(At(file, line.Number, PreflightSeverity.Warning,
                        "Не-ASCII литерал без префикса u участвует в конкатенации или форматировании. " +
                        "В Python 2.7 смешение py2-str с unicode даёт UnicodeDecodeError; если строка шла в лог, " +
                        "BWLogRedirectionHandler.emit проглотит ошибку через handleError и ЗАПИСЬ ПРОПАДЁТ ЦЕЛИКОМ.",
                        "Поставьте префикс u у всех не-ASCII литералов, а пути ОС прогоняйте через " +
                        "decode(sys.getfilesystemencoding()).",
                        "scripts/common/bw_site.py:7 (DEFAULT_ENCODING='utf-8'), BWLogging.BWLogRedirectionHandler.emit",
                        column));
                }
            }
        }
    }
}
