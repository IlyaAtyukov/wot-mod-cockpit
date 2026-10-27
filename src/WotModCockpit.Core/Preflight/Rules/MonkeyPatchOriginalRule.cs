namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT007. Монкипатч не вызывает сохранённый оригинал (или вызывает не на всех путях).
///
/// <para><b>Последствие.</b> Один и тот же метод клиента патчат сразу несколько модов:
/// <c>showShotResults</c> трогают и XVM, и pmod, и учебный шаблон. Обёртка, которая не позвала
/// оригинал, ломает и штатное поведение игры, и цепочку следующих модов — они получат управление
/// только если оригинал вызовут. Механика проверена на реальном коде: XVM
/// <c>xfw/events.py:_OverrideMethod</c> строит <c>lambda *a, **k: handler(orig, *a, **k)</c>,
/// то есть вызов оригинала целиком на совести автора и НЕ обёрнут в try; так же устроены
/// <c>modsListApi/utils.py:override</c> и <c>responsive_reticle/overrideIn</c>.</para>
///
/// <para>Отдельный случай — оригинал внутри одного <c>try</c> со своей логикой: упало своё,
/// и оригинал потерян. Это <see cref="PreflightSeverity.Warning"/>, а полное отсутствие
/// вызова — <see cref="PreflightSeverity.Error"/>.</para>
/// </summary>
public sealed class MonkeyPatchOriginalRule : PerFileRule
{
    /// <summary>
    /// Имена, которыми принято называть снятый оригинал. Список намеренно узкий: слишком общие
    /// <c>func</c>, <c>target</c>, <c>base</c>, <c>source</c> встречаются в любом коде и давали
    /// ложные срабатывания в вендорных библиотеках (замерено на XVM: 4 ошибки в <c>fs/</c>).
    /// </summary>
    public static IReadOnlyList<string> OriginalNames { get; } = new[]
    {
        "orig", "original", "old", "prev", "old_method", "oldMethod", "originalMethod", "orig_method"
    };

    /// <inheritdoc/>
    public override string Id => "WOT007";

    /// <inheritdoc/>
    public override string Title => "Монкипатч не вызывает оригинал";

    /// <inheritdoc/>
    protected override void AnalyzeFile(PreflightContext context, ModSourceFile file, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);

        var reported = new HashSet<string>(StringComparer.Ordinal);

        // (1) Декораторный стиль: оригинал приходит первым аргументом обёртки.
        foreach (PythonDefinition def in file.Model.Definitions)
        {
            if (def.IsClass || def.ParameterNames.Count == 0) continue;
            if (!HasPatchDecorator(context, def.Decorators)) continue;

            string original = def.ParameterNames[0];
            Inspect(file, def, original, reported, output, "первый аргумент декоратора-патчера", fromDecorator: true);
        }

        // (2) Явная установка: setattr(cls, 'm', wrapper) либо Cls.m = wrapper.
        HashSet<string>? gameNames = null;

        foreach (PythonStatement st in file.Model.Statements)
        {
            (string? target, string? wrapperName) = PatchTarget(st);
            if (wrapperName is null) continue;

            PythonDefinition? def = file.Model.FindDefinitionBefore(wrapperName, st.Line);
            if (def is null || def.IsClass) continue;
            if (HasPatchDecorator(context, def.Decorators)) continue;

            string? original = FindOriginalName(file, def);
            if (original is null)
            {
                // Самый тяжёлый случай темы: прежнее значение атрибута не сохранено ВООБЩЕ.
                // Раньше правило здесь молчало («нечего сверять»), хотя патч, затирающий
                // штатный метод клиента без сохранения оригинала, ломает игру сильнее,
                // чем обёртка, забывшая позвать сохранённый orig: вернуть поведение уже нечем.
                if (target is null) continue;

                gameNames ??= GuardAnalysis.GameBoundNames(context, file);
                if (!IsGameAttribute(target, gameNames)) continue;      // патчат свой класс — не наше дело

                // Перекрывается именно ШТАТНЫЙ метод клиента, а не добавляется новый атрибут
                // на чужой класс. Без этой сверки по индексу правило ругалось на
                // ToolTip.xvm_hide и MarkersManager.daapi_as2py у XVM — там сохранять нечего.
                int lastDot = target.LastIndexOf('.');
                string attribute = target[(lastDot + 1)..];
                string ownerPath = target[..lastDot];
                int ownerDot = ownerPath.LastIndexOf('.');
                string ownerClass = ownerDot >= 0 ? ownerPath[(ownerDot + 1)..] : ownerPath;
                if (!context.Api.DeclaresMethod(attribute, ownerClass)) continue;

                if (OriginalSavedSomewhere(file, target)) continue;
                if (!reported.Add(def.Name)) continue;

                output.Add(At(file, st.Line, PreflightSeverity.Warning,
                    $"{target} заменяется на {def.Name}(), но прежнее значение нигде в файле не сохранено. " +
                    "Штатное поведение метода клиента потеряно безвозвратно: вернуть его в fini() нечем, " +
                    "а мод, который пропатчил этот же метод ДО вас, потеряет свою обёртку целиком.",
                    $"Снимите оригинал перед установкой: _orig = {target}  (или getattr(...)), вызывайте его " +
                    "из обёртки на всех путях выхода.",
                    "xfw/events.py:_OverrideMethod (XVM), modsListApi/utils.py:override"));
                continue;
            }

            Inspect(file, def, original, reported, output, $"сохранённый оригинал {original}", fromDecorator: false);
        }
    }

    /// <summary>
    /// Куда ставится патч и чем. <c>(Avatar.PlayerAvatar.onEnterWorld, _my_enter)</c> для
    /// присваивания и для <c>setattr(cls, 'onEnterWorld', _my_enter)</c>.
    /// </summary>
    private static (string? Target, string? Wrapper) PatchTarget(PythonStatement st)
    {
        int open = PreflightSyntax.FindCall(st.Trimmed, "setattr", allowMethodReceiver: false);
        if (open >= 0)
        {
            IReadOnlyList<string> args = PreflightSyntax.SplitArguments(st.Trimmed, open);
            IReadOnlyList<string> raw = PreflightSyntax.SplitArgumentsRaw(st.Trimmed, st.RawCode, open);
            if (args.Count != 3) return (null, null);

            string? wrapper = PlainName(args[2]);
            if (wrapper is null) return (null, null);

            string owner = args[0].Trim();
            string attribute = raw.Count == 3 ? Unquote(raw[1]) : string.Empty;
            bool ownerIsName = owner.Length > 0 && owner.Split('.').All(p => PlainName(p) is not null);
            return (attribute.Length > 0 && ownerIsName ? owner + "." + attribute : null, wrapper);
        }

        string code = st.Trimmed;
        int eq = PreflightSyntax.IndexOfAssignment(code);
        if (eq <= 0) return (null, null);

        string left = code[..eq].Trim();
        if (!left.Contains('.', StringComparison.Ordinal)) return (null, null);
        if (left.Contains('(', StringComparison.Ordinal) || left.Contains('[', StringComparison.Ordinal)) return (null, null);

        return (left, PlainName(code[(eq + 1)..]));
    }

    /// <summary>Слева стоит атрибут ИГРОВОГО объекта, а не собственного класса мода.</summary>
    private static bool IsGameAttribute(string target, HashSet<string> gameNames)
    {
        int dot = target.IndexOf('.');
        if (dot <= 0) return false;
        return gameNames.Contains(target[..dot]);
    }

    /// <summary>
    /// Прежнее значение атрибута где-то в файле сохранено: <c>_orig = Cls.m</c> либо
    /// <c>_orig = getattr(Cls, 'm')</c>. Имя переменной здесь не важно — важен сам факт.
    /// </summary>
    private static bool OriginalSavedSomewhere(ModSourceFile file, string target)
    {
        int dot = target.LastIndexOf('.');
        string owner = dot > 0 ? target[..dot] : target;
        string attribute = dot > 0 ? target[(dot + 1)..] : target;

        foreach (PythonStatement st in file.Model.Statements)
        {
            string code = st.Trimmed;
            int eq = PreflightSyntax.IndexOfAssignment(code);
            if (eq <= 0) continue;

            string rhs = code[(eq + 1)..].Trim();
            if (string.Equals(rhs, target, StringComparison.Ordinal)) return true;

            int open = PreflightSyntax.FindCall(code, "getattr", allowMethodReceiver: false);
            if (open <= eq) continue;

            IReadOnlyList<string> args = PreflightSyntax.SplitArguments(code, open);
            IReadOnlyList<string> raw = PreflightSyntax.SplitArgumentsRaw(code, st.RawCode, open);
            if (args.Count < 2) continue;
            if (!string.Equals(args[0].Trim(), owner, StringComparison.Ordinal)) continue;
            if (string.Equals(Unquote(raw[1]), attribute, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static string Unquote(string s)
    {
        string t = s.Trim();
        if (t.Length < 2) return string.Empty;
        char q = t[0];
        return q is ('\'' or '"') && t[^1] == q ? t[1..^1] : string.Empty;
    }

    private void Inspect(ModSourceFile file, PythonDefinition wrapper, string original,
        HashSet<string> reported, ICollection<PreflightDiagnostic> output, string via, bool fromDecorator)
    {
        if (!reported.Add(wrapper.Name)) return;

        bool called = false;
        bool forwarded = false;
        bool calledInsideTry = false;
        bool tryHasOwnLogic = false;

        foreach (PythonStatement st in file.Model.BodyOf(wrapper))
        {
            if (st.EnclosingDefinition != wrapper.Index) continue;

            bool isCall = PreflightSyntax.FindCall(st.Trimmed, original) >= 0;
            if (isCall)
            {
                called = true;
                if (st.InTry) calledInsideTry = true;
                continue;
            }

            // Оригинал может не вызываться здесь, а ПЕРЕДАВАТЬСЯ дальше: именно так устроены
            // фреймворковые обёртки (`return func(old, *args, **kwargs)` в responsive_reticle
            // и в XFW). Вызов там — забота вызываемого обработчика, и ругаться нельзя.
            foreach ((string name, _) in PreflightSyntax.RootIdentifiers(st.Trimmed))
                if (string.Equals(name, original, StringComparison.Ordinal)) { forwarded = true; break; }

            if (st.InTry && !st.StartsWith("except") && !st.StartsWith("try")) tryHasOwnLogic = true;
        }

        if (!called)
        {
            if (forwarded) return;

            // Пришёл первым аргументом декоратора и не упомянут вовсе — это может быть
            // осознанная полная замена метода, поэтому предупреждение, а не ошибка.
            // Явно сохранённый оригинал, который забыли позвать, — точно ошибка.
            PreflightSeverity severity = fromDecorator ? PreflightSeverity.Warning : PreflightSeverity.Error;

            output.Add(At(file, wrapper.Line, severity,
                $"Обёртка {wrapper.Name}() ставится вместо метода клиента, но НИ РАЗУ не вызывает оригинал ({via}). " +
                "Штатное поведение игры пропадёт, а моды, которые пропатчили этот же метод после вас, " +
                "не получат управление вообще." +
                (fromDecorator ? " Если это сознательная полная замена метода — правило можно выключить в настройках." : string.Empty),
                $"Вызовите {original}(*args, **kwargs) на всех путях выхода — свою логику держите в отдельном try/except.",
                "xfw/events.py:_OverrideMethod (XVM), modsListApi/utils.py:override"));
            return;
        }

        if (calledInsideTry && tryHasOwnLogic)
        {
            output.Add(At(file, wrapper.Line, PreflightSeverity.Warning,
                $"В {wrapper.Name}() своя логика и вызов оригинала {original}() лежат в ОДНОМ try. " +
                "Если упадёт ваш код, оригинал не вызовется — метод клиента молча перестанет работать, " +
                "и следующие в цепочке моды тоже.",
                "Разнесите: свою логику — в собственный try/except, вызов оригинала — ВНЕ его, на всех путях.",
                "scripts/client/gui/mods/__init__.py (порядок init() модов задан сортировкой имён mod_*.pyc)"));
        }
    }

    private static bool HasPatchDecorator(PreflightContext context, IReadOnlyList<string> decorators)
    {
        foreach (string d in decorators)
        {
            string name = d;
            int dot = name.LastIndexOf('.');
            if (dot >= 0) name = name[(dot + 1)..];
            foreach (string patcher in context.Options.PatchDecorators)
                if (string.Equals(name, patcher, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string? PlainName(string raw)
    {
        string s = raw.Trim();
        if (s.Length == 0) return null;
        if (!char.IsLetter(s[0]) && s[0] != '_') return null;
        foreach (char c in s) if (!char.IsLetterOrDigit(c) && c != '_') return null;
        return s;
    }

    /// <summary>
    /// Имя переменной, в которую снят оригинал. Ищем среди параметров обёртки и среди
    /// присваиваний вида <c>orig = getattr(...)</c> / <c>orig = Cls.method</c> во всём файле.
    /// </summary>
    private static string? FindOriginalName(ModSourceFile file, PythonDefinition wrapper)
    {
        foreach (string p in wrapper.ParameterNames)
            if (OriginalNames.Contains(p, StringComparer.Ordinal)) return p;

        // Оригинал снимают ДО объявления обёртки — она его замыкает. Ищем ближайшее
        // такое присваивание выше по файлу, а не первое попавшееся.
        string? found = null;
        foreach (PythonStatement st in file.Model.Statements)
        {
            if (st.Line >= wrapper.Line) break;

            string code = st.Trimmed;
            int eq = PreflightSyntax.IndexOfAssignment(code);
            if (eq <= 0) continue;

            string name = code[..eq].Trim();
            if (!OriginalNames.Contains(name, StringComparer.Ordinal)) continue;

            string rhs = code[(eq + 1)..].Trim();
            if (rhs.StartsWith("getattr", StringComparison.Ordinal) || rhs.Contains('.', StringComparison.Ordinal))
                found = name;
        }
        return found;
    }
}
