namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT019. Патч приватного метода клиента без учёта name mangling.
///
/// <para><b>Последствие.</b> Приватные методы подвергнуты name mangling: <c>__foo</c> внутри
/// <c>class Bar</c> лежит в атрибутах как <c>_Bar__foo</c>. <c>getattr</c>/<c>setattr</c>
/// по имени <c>'__foo'</c> не найдёт НИЧЕГО: либо <c>AttributeError</c> (см. WOT008), либо
/// патч молча не встанет.</para>
///
/// <para>Что проблема реальна, видно по тому, что реальные моды пишут манглированные имена
/// руками: <c>responsive_reticle</c> обращается к <c>_VehicleGunRotator__turretYaw</c> и держит
/// в <c>overrideIn</c> код <c>if funcName.startswith('__') …: funcName = '_' + cls.__name__ +
/// funcName</c>; XVM пишет <c>overrideMethod(LobbyEntry,
/// '_LobbyEntry__updateCommonChatAccessibility')</c>.</para>
/// </summary>
public sealed class NameManglingRule : PerFileRule
{
    private static readonly string[] AttributeCalls = { "getattr", "setattr", "hasattr", "delattr" };

    /// <inheritdoc/>
    public override string Id => "WOT019";

    /// <inheritdoc/>
    public override string Title => "Приватное имя без name mangling";

    /// <inheritdoc/>
    protected override void AnalyzeFile(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);

        foreach (PythonStatement st in file.Model.Statements)
        {
            string code = st.Trimmed;

            foreach (string call in AttributeCalls)
            {
                int open = PreflightSyntax.FindCall(code, call, allowMethodReceiver: false);
                if (open < 0) continue;

                IReadOnlyList<string> args = PreflightSyntax.SplitArgumentsRaw(code, st.RawCode, open);
                if (args.Count < 2) continue;

                string arg = args[1].Trim();
                if (arg.Length < 4) continue;
                if (arg[0] is not ('\'' or '"') || arg[^1] != arg[0]) continue;

                string name = arg[1..^1];
                if (!name.StartsWith("__", StringComparison.Ordinal)) continue;
                if (name.EndsWith("__", StringComparison.Ordinal)) continue;    // __init__, __name__ — не манглятся

                output.Add(At(file, st.Line, PreflightSeverity.Warning,
                    $"{call}(…, '{name}') не найдёт ничего: Python манглит приватные имена, и внутри класса Cls " +
                    $"атрибут физически называется _Cls{name}. Патч молча не встанет, а getattr без значения " +
                    "по умолчанию ещё и бросит AttributeError.",
                    $"Пишите '_' + cls.__name__ + '{name}' либо сразу манглированное имя _<ИмяКласса>{name} " +
                    "(так это делают responsive_reticle и XVM).",
                    "pruszko.responsive_reticle/overrideIn; XVM overrideMethod(LobbyEntry, '_LobbyEntry__…')"));
            }
        }
    }
}
