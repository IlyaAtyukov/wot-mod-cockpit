namespace WotModCockpit.Core.Preflight;

/// <summary>
/// Ответ на вопрос «действительно ли тело функции защищено от вылета исключения наружу».
///
/// <para><b>Почему это не в <see cref="PythonLineModel"/>.</b> Там есть
/// <see cref="PythonLineModel.BodyIsGuarded"/> — чисто лексическая проверка «в теле есть
/// хоть один <c>try</c>». Её одной мало, и это измерено: <c>init()</c>, который зовёт
/// <c>_load_config()</c> и <c>BigWorld.callback()</c> БЕЗ защиты, а <c>try</c> держит только
/// подписку на <c>PlayerEvents</c>, получал от WOT005 ровно ноль находок — хотя порча
/// конфига там даёт <c>IOError</c>, который дойдёт до <c>game.py:init()</c> и закроет клиент.
/// Синтетически то же самое: допишите в конец <c>init()</c> декоративный
/// <c>try: pass / except Exception: pass</c> — и WOT005 замолкает целиком.</para>
///
/// <para><b>Почему не «любой оператор вне try — это дыра».</b> Такое ужесточение измерено
/// и отвергнуто: на эталонном шаблоне оно даёт 9 ложных срабатываний — стаб, чьи
/// <c>init/fini/onAccount*</c> делегируют в защищённый <c>_call()</c>, и вложенный
/// <c>_runner()</c>, который начинается с <c>bucket.discard(...)</c> по локальному множеству.
/// Ни то, ни другое исключения не бросает.</para>
///
/// <para><b>Что здесь считается опасным.</b> Ровно то, что может бросить у мододела:
/// обращение к ИГРОВОМУ имени (оракул знает, какие модули клиентские), ленивый импорт
/// игрового модуля, чтение файла/JSON и вызов СВОЕЙ функции, которая делает то же самое
/// и сама не защищена. Именно поэтому предикат живёт здесь, а не в лексической модели:
/// без <see cref="PreflightContext"/> отличить <c>BigWorld.callback</c> от
/// <c>bucket.discard</c> нечем.</para>
///
/// <para><b>Правило монотонности.</b> Новая проверка только ДОБАВЛЯЕТ находки:
/// <see cref="BodyIsGuarded"/> = старое условие «есть try» И новое «опасное — под try».
/// Функция без единого <c>try</c> как была незащищённой, так и осталась.</para>
/// </summary>
public static class GuardAnalysis
{
    /// <summary>Глубина разбора «своя функция зовёт свою функцию». Трёх уровней хватает.</summary>
    private const int MaxCallDepth = 3;

    /// <summary>
    /// Вызовы, которые в коде мода бросают чаще всего и не относятся к игровому API:
    /// чтение конфига. Список закрытый намеренно — «любой вызов рискован» уже измерен
    /// и даёт ложные срабатывания.
    /// </summary>
    private static readonly (string Callee, bool AllowReceiver)[] RaisingBuiltins =
    [
        ("open", false),
        ("json.load", true),
        ("json.loads", true),
        ("codecs.open", true)
    ];

    /// <summary>
    /// Тело функции защищено: <c>try</c> в нём есть И ни один опасный оператор не лежит
    /// вне <c>try</c>.
    /// </summary>
    /// <param name="context">Контекст preflight (нужен оракул клиентского API).</param>
    /// <param name="file">Файл мода.</param>
    /// <param name="definition">Определение.</param>
    /// <returns><c>true</c>, если наружу исключение, скорее всего, не выйдет.</returns>
    public static bool BodyIsGuarded(PreflightContext context, ModSourceFile file, PythonDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(definition);

        // Старое, лексическое условие. Оставлено первым: оно дешевле и отсекает
        // функции вообще без try — там и разбирать нечего.
        if (!file.Model.BodyIsGuarded(definition)) return false;

        return FirstRiskyStatement(context, file, definition) is null;
    }

    /// <summary>
    /// Первый опасный оператор тела, лежащий ВНЕ <c>try</c>, либо <c>null</c>.
    /// Отдельный метод, чтобы находка могла указать на конкретную строку, а не только
    /// на заголовок функции.
    /// </summary>
    /// <param name="context">Контекст.</param>
    /// <param name="file">Файл.</param>
    /// <param name="definition">Определение.</param>
    /// <returns>Оператор либо <c>null</c>.</returns>
    public static PythonStatement? FirstRiskyStatement(PreflightContext context, ModSourceFile file,
        PythonDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(definition);

        HashSet<string> gameNames = GameBoundNames(context, file);
        var memo = new Dictionary<int, bool>();

        foreach (PythonStatement st in file.Model.BodyOf(definition))
        {
            if (st.EnclosingDefinition != definition.Index) continue;
            if (IsRisky(context, file, st, gameNames, memo, MaxCallDepth)) return st;
        }

        return null;
    }

    /// <summary>
    /// Имена файла, за которыми стоит ИГРА. Шире, чем в
    /// <see cref="Rules.ModuleLevelGameWorkRule"/>: сюда попадают и имена, взятые
    /// <c>from &lt;игровой модуль&gt; import X</c> (<c>g_playerEvents</c>), потому что
    /// обращение к такому объекту — это тоже вызов кода клиента.
    /// </summary>
    /// <param name="context">Контекст.</param>
    /// <param name="file">Файл.</param>
    /// <returns>Множество локальных имён.</returns>
    public static HashSet<string> GameBoundNames(PreflightContext context, ModSourceFile file)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(file);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (ImportBinding b in file.Bindings.Values)
        {
            if (PreflightSyntax.IsGameModule(context.Api, context, b.Module)) { names.Add(b.LocalName); continue; }

            // `from PlayerEvents import g_playerEvents`: сам «PlayerEvents.g_playerEvents»
            // модулем не является, а вот его родитель — игровой модуль.
            int dot = b.Module.LastIndexOf('.');
            if (dot > 0 && PreflightSyntax.IsGameModule(context.Api, context, b.Module[..dot])) names.Add(b.LocalName);
        }
        return names;
    }

    // ------------------------------------------------------------------ внутреннее

    private static bool IsRisky(PreflightContext context, ModSourceFile file, PythonStatement st,
        HashSet<string> gameNames, Dictionary<int, bool> memo, int depth)
    {
        if (st.InTry) return false;                 // это и есть лечение
        if (st.InExcept) return false;              // ветка восстановления: там пишут в лог, а не работают
        if (IsStructuralHeader(st)) return false;

        string code = st.Trimmed;

        // (1) Ленивый импорт игрового модуля: ImportError/AttributeError отсюда — обычное дело.
        if (st.StartsWith("import") || st.StartsWith("from"))
        {
            foreach (string module in PreflightSyntax.ImportedModules(st))
                if (PreflightSyntax.IsGameModule(context.Api, context, module)) return true;
            return false;
        }

        // (2) Обращение к игровому имени: именно ОБРАЩЕНИЕ (вызов или атрибут),
        // а не упоминание. Без этого «if g_playerEvents is not None:» считалось бы риском.
        if (TouchesGameName(code, gameNames)) return true;

        // (3) Чтение конфига встроенными средствами — второй по частоте источник
        // исключения в init() после игрового API.
        foreach ((string callee, bool receiver) in RaisingBuiltins)
            if (PreflightSyntax.FindCall(code, callee, receiver) >= 0) return true;

        // (4) Вызов СВОЕЙ функции, которая делает то же самое и сама не защищена.
        if (depth <= 0) return false;
        foreach ((string name, _) in PreflightSyntax.RootIdentifiers(code))
        {
            if (!IsCallOrAttribute(code, name)) continue;

            PythonDefinition? target = file.Model.FindTopLevelDefinition(name);
            if (target is null || target.IsClass) continue;
            if (FunctionIsRisky(context, file, target, gameNames, memo, depth - 1)) return true;
        }

        return false;
    }

    /// <summary>
    /// Своя функция опасна: она не под декоратором-предохранителем и в её теле есть
    /// опасный оператор вне <c>try</c>.
    /// </summary>
    private static bool FunctionIsRisky(PreflightContext context, ModSourceFile file, PythonDefinition def,
        HashSet<string> gameNames, Dictionary<int, bool> memo, int depth)
    {
        if (memo.TryGetValue(def.Index, out bool cached)) return cached;

        // Пометка ДО обхода: рекурсивная функция (_pump зовёт _pump) не должна зациклиться.
        memo[def.Index] = false;
        if (context.IsGuardedByDecorator(def.Decorators)) return false;

        bool risky = false;
        foreach (PythonStatement st in file.Model.BodyOf(def))
        {
            if (st.EnclosingDefinition != def.Index) continue;
            if (!IsRisky(context, file, st, gameNames, memo, depth)) continue;
            risky = true;
            break;
        }

        memo[def.Index] = risky;
        return risky;
    }

    /// <summary>Заголовок блока, который сам по себе ничего не исполняет.</summary>
    private static bool IsStructuralHeader(PythonStatement st)
    {
        string code = st.Trimmed;
        if (code.StartsWith('@')) return true;
        if (code is "return" or "pass" or "break" or "continue") return true;

        // if/for/while/with НЕ пропускаем: `for v in BigWorld.player().arena.vehicles:`
        // — это обращение к игре в самом заголовке.
        return st.StartsWith("try") || st.StartsWith("except") || st.StartsWith("finally")
               || st.StartsWith("else") || st.StartsWith("def") || st.StartsWith("class")
               || st.StartsWith("global");
    }

    /// <summary>В коде есть <c>name.</c> или <c>name(</c>, где <c>name</c> — игровое имя.</summary>
    private static bool TouchesGameName(string code, HashSet<string> gameNames)
    {
        if (gameNames.Count == 0) return false;

        foreach ((string name, int index) in PreflightSyntax.RootIdentifiers(code))
        {
            if (!gameNames.Contains(name)) continue;
            if (IsCallOrAttributeAt(code, index + name.Length)) return true;
        }
        return false;
    }

    private static bool IsCallOrAttribute(string code, string name)
    {
        foreach ((string found, int index) in PreflightSyntax.RootIdentifiers(code))
            if (string.Equals(found, name, StringComparison.Ordinal) && IsCallOrAttributeAt(code, index + name.Length))
                return true;
        return false;
    }

    private static bool IsCallOrAttributeAt(string code, int after)
    {
        while (after < code.Length && code[after] == ' ') after++;
        return after < code.Length && (code[after] == '.' || code[after] == '(');
    }
}
