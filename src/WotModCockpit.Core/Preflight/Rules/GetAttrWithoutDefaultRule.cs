namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT008. <c>getattr(obj, name)</c> без значения по умолчанию.
///
/// <para><b>Последствие.</b> Это главный способ уронить клиент при обновлении игры: метод
/// переименовали — <c>getattr</c> бросает <c>AttributeError</c>. Если снятие оригинала стоит
/// на уровне модуля, исключение закроет клиент (WOT001); если в <c>init()</c> — загрузчик
/// проглотит его молча (<c>except AttributeError: pass</c> в <c>_callModMethod</c>), и мод
/// «тихо не работает».</para>
///
/// <para>Замер разведки: 19 сайтов двухаргументного <c>getattr</c> на 188 файлов корпуса, ВСЕ —
/// снятие оригинала для патча (XVM <c>xfw/events.py:43,48,49,73,82,90,97</c>,
/// <c>modsSettingsApi/utils.py:25,46</c>, <c>responsive_reticle</c>). Контраст: ОФИЦИАЛЬНЫЙ мод
/// Lesta везде пишет <c>getattr(module, class_name, None)</c> и проверяет на <c>None</c>.</para>
/// </summary>
public sealed class GetAttrWithoutDefaultRule : PerFileRule
{
    /// <inheritdoc/>
    public override string Id => "WOT008";

    /// <inheritdoc/>
    public override string Title => "getattr без значения по умолчанию";

    /// <inheritdoc/>
    protected override void AnalyzeFile(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);

        foreach (PythonStatement st in file.Model.Statements)
        {
            // try/except вокруг — законный способ, именно его и советует правило.
            if (st.InTry) continue;

            string code = st.Trimmed;
            int open = PreflightSyntax.FindCall(code, "getattr", allowMethodReceiver: false);
            if (open < 0) continue;

            IReadOnlyList<string> args = PreflightSyntax.SplitArgumentsRaw(code, st.RawCode, open);
            if (args.Count != 2) continue;

            bool looksLikeOriginal = LooksLikeOriginal(code, out string assigned);
            bool patchNearby = PatchNearby(file, st);
            PreflightSeverity severity = looksLikeOriginal || patchNearby
                ? PreflightSeverity.Error
                : PreflightSeverity.Warning;

            string what = looksLikeOriginal
                ? $"Сюда снимается оригинал метода ({assigned})"
                : "Здесь берётся атрибут игрового объекта";

            output.Add(At(file, st.Line, severity,
                $"{what} через getattr БЕЗ третьего аргумента. Как только игра переименует атрибут, " +
                "вылетит AttributeError: на уровне модуля он закроет клиент (game.py:init() зовёт BigWorld.quit()), " +
                "а внутри init() загрузчик проглотит его молча — мод перестанет работать без записи в логе.",
                $"Пишите getattr({args[0]}, {args[1]}, None) и проверяйте результат на None, как это делает " +
                "официальный мод Lesta (mod_lesta_replays_upload.py:1730).",
                "scripts/client/gui/mods/__init__.py:_callModMethod"));
        }
    }

    private static bool LooksLikeOriginal(string code, out string assigned)
    {
        assigned = string.Empty;
        int eq = PreflightSyntax.IndexOfAssignment(code);
        if (eq <= 0) return false;

        string name = code[..eq].Trim();
        if (!MonkeyPatchOriginalRule.OriginalNames.Contains(name, StringComparer.Ordinal)) return false;
        assigned = name;
        return true;
    }

    /// <summary>Рядом, в той же функции, есть <c>setattr</c> — значит это точно установка патча.</summary>
    private static bool PatchNearby(ModSourceFile file, PythonStatement st)
    {
        if (st.EnclosingDefinition < 0) return false;

        PythonDefinition def = file.Model.Definitions[st.EnclosingDefinition];
        foreach (PythonStatement other in file.Model.BodyOf(def))
            if (PreflightSyntax.FindCall(other.Trimmed, "setattr", allowMethodReceiver: false) >= 0) return true;
        return false;
    }
}
