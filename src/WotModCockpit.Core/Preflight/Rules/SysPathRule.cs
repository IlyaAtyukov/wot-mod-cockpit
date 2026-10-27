namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT021. Изменение <c>sys.path</c> / <c>sys.meta_path</c> без симметричного отката.
///
/// <para><b>Последствие.</b> Мод, подмешавший каталог в <c>sys.path</c> или finder
/// в <c>sys.meta_path</c> и не снявший его в <c>fini()</c>, портит окружение остальным модам:
/// их импорты пойдут через ваш finder уже после того, как ваш код выгружен.</para>
///
/// <para>Ценность разная: для <c>sys.path</c> это скорее напоминание о симметрии (оба
/// срабатывания на корпусе — законный код XVM, <c>xvm_pymacro/__init__.py:177</c>), а вот
/// несимметричный <c>sys.meta_path</c> реально ломает импорт другим модам. Эталонный стаб
/// шаблона ставит свой PEP-302 finder в <c>_install_fallback()</c> и СНИМАЕТ его
/// в <c>_remove_fallback()</c>, вызываемом из <c>fini()</c> — это и есть образец.</para>
/// </summary>
public sealed class SysPathRule : IPreflightRule
{
    /// <inheritdoc/>
    public string Id => "WOT021";

    /// <inheritdoc/>
    public string Title => "sys.path/sys.meta_path без симметричного отката";

    /// <inheritdoc/>
    public void Analyze(PreflightContext context, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        Check(context, output, "sys.meta_path", "import других модов");
        Check(context, output, "sys.path", "поиск модулей у других модов");
    }

    private void Check(PreflightContext context, ICollection<PreflightDiagnostic> output, string target, string what)
    {
        bool removed = false;
        foreach (ModSourceFile f in context.Files)
            foreach (PythonStatement st in f.Model.Statements)
            {
                string code = st.Trimmed;
                if (code.Contains(target + ".remove", StringComparison.Ordinal)
                    || code.Contains("del " + target + "[", StringComparison.Ordinal)
                    || code.Contains(target + ".pop", StringComparison.Ordinal))
                { removed = true; break; }
            }
        if (removed) return;

        foreach (ModSourceFile file in context.Files)
        {
            foreach (PythonStatement st in file.Model.Statements)
            {
                string code = st.Trimmed;
                bool mutates =
                    code.Contains(target + ".insert", StringComparison.Ordinal) ||
                    code.Contains(target + ".append", StringComparison.Ordinal) ||
                    code.Contains(target + ".extend", StringComparison.Ordinal) ||
                    (target == "sys.path" && PreflightSyntax.IndexOfAssignment(code) is int eq && eq > 0
                        && code[..eq].Trim() == target);
                if (!mutates) continue;

                output.Add(new PreflightDiagnostic(Id, PreflightSeverity.Info, file.FullPath, st.Line, 0,
                    $"{target} меняется, а обратной операции ({target}.remove / del {target}[i]) нигде в дереве " +
                    $"мода нет. Изменение переживёт fini() и продолжит влиять на {what} до закрытия клиента.")
                {
                    Hint = $"Сделайте пару: установка при инициализации, снятие в fini() — как _install_fallback()/" +
                           "_remove_fallback() в стабе эталонного шаблона.",
                    Reference = "src/res/scripts/client/gui/mods/mod_hello_tanker.py:_remove_fallback",
                    RelativePath = file.RelativePath,
                    Snippet = file.Model.LineAt(st.Line)?.Raw.Trim()
                });
                return;                                 // одного сообщения на мод достаточно
            }
        }
    }
}
