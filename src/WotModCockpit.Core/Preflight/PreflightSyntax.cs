using WotModCockpit.Core.ApiIndex;

namespace WotModCockpit.Core.Preflight;

/// <summary>
/// Мелкие лексические примитивы, общие для правил: корневые идентификаторы цепочки,
/// разбор вызова, классификация модуля.
/// </summary>
public static class PreflightSyntax
{
    /// <summary>
    /// Идентификаторы, стоящие В НАЧАЛЕ цепочки (то есть НЕ после точки).
    /// Отрицательный просмотр назад обязателен: без него <c>player.arena.vehicles.get(…)</c>
    /// трактовалось бы как обращение к модулю <c>vehicles</c> — на корпусе это давало
    /// 3 ложных срабатывания из 5.
    /// </summary>
    /// <param name="code">Код с вырезанными литералами.</param>
    /// <returns>Пары «имя, позиция».</returns>
    public static IEnumerable<(string Name, int Index)> RootIdentifiers(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        int i = 0;
        while (i < code.Length)
        {
            char c = code[i];
            if (!char.IsLetter(c) && c != '_') { i++; continue; }

            int start = i;
            while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i++;

            bool afterDot = start > 0 && (code[start - 1] == '.' || char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_');
            if (!afterDot) yield return (code[start..i], start);
        }
    }

    /// <summary>Атрибут, к которому обращаются после имени: пары <c>(root, attr, позиция attr)</c>.</summary>
    /// <param name="code">Код с вырезанными литералами.</param>
    /// <returns>Тройки.</returns>
    public static IEnumerable<(string Root, string Attribute, int Index)> RootAttributes(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        foreach ((string name, int index) in RootIdentifiers(code))
        {
            int after = index + name.Length;
            if (after >= code.Length || code[after] != '.') continue;

            int start = after + 1;
            int j = start;
            while (j < code.Length && (char.IsLetterOrDigit(code[j]) || code[j] == '_')) j++;
            if (j > start) yield return (name, code[start..j], start);
        }
    }

    /// <summary>
    /// Оператор — голый вызов вида <c>name(...)</c> или <c>a.b.c(...)</c>: результат никуда
    /// не присваивается. Именно так выглядит установка патча уровня модуля.
    /// </summary>
    /// <param name="code">Код оператора.</param>
    /// <param name="callee">Имя вызываемого (полная цепочка) при успехе.</param>
    /// <returns><c>true</c>, если это голый вызов.</returns>
    public static bool IsBareCall(string code, out string callee)
    {
        callee = string.Empty;
        ArgumentNullException.ThrowIfNull(code);

        string t = code.Trim();
        if (t.Length == 0 || !t.EndsWith(')')) return false;

        int open = t.IndexOf('(');
        if (open <= 0) return false;

        string head = t[..open].Trim();
        if (head.Length == 0) return false;
        if (!char.IsLetter(head[0]) && head[0] != '_') return false;
        foreach (char c in head) if (!char.IsLetterOrDigit(c) && c != '_' && c != '.') return false;

        // Ключевые слова — не вызовы.
        string root = head.Split('.')[0];
        if (root is "return" or "yield" or "assert" or "print" or "del" or "raise" or "lambda"
            or "if" or "elif" or "while" or "for" or "with" or "except" or "import" or "from") return false;

        callee = head;
        return true;
    }

    /// <summary>Правая часть присваивания — литерал (число, строка, True/False/None, литерал контейнера).</summary>
    /// <param name="rhs">Правая часть.</param>
    /// <returns><c>true</c>, если литерал.</returns>
    public static bool IsLiteralExpression(string rhs)
    {
        ArgumentNullException.ThrowIfNull(rhs);

        string t = rhs.Trim();
        if (t.Length == 0) return true;

        foreach ((string name, _) in RootIdentifiers(t))
        {
            if (name is "True" or "False" or "None" or "u" or "b" or "r" or "ur" or "br" or "rb") continue;
            return false;
        }
        return true;
    }

    /// <summary>Индекс <c>=</c> верхнего уровня (не сравнение, не в скобках) либо -1.</summary>
    /// <param name="code">Код оператора.</param>
    /// <returns>Индекс либо -1.</returns>
    public static int IndexOfAssignment(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        int depth = 0;
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == '=' && depth == 0)
            {
                if (i + 1 < code.Length && code[i + 1] == '=') { i++; continue; }
                // Составное присваивание — НЕ присваивание для наших целей: без этой проверки
                // строка `g_playerEvents.onAvatarReady += _on_avatar_ready` выглядела бы как
                // установка монкипатча (замерено на эталонном шаблоне: 2 ложных Error).
                if (i > 0 && code[i - 1] is '=' or '!' or '<' or '>' or '+' or '-' or '*' or '/'
                    or '%' or '|' or '&' or '^' or '~' or '@' or ':') continue;
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Аргументы вызова верхнего уровня: <c>f(a, g(b, c), d=1)</c> → <c>a</c>, <c>g(b, c)</c>, <c>d=1</c>.
    /// </summary>
    /// <param name="code">Код, содержащий вызов.</param>
    /// <param name="callStart">Позиция открывающей скобки.</param>
    /// <returns>Аргументы.</returns>
    public static IReadOnlyList<string> SplitArguments(string code, int callStart)
    {
        ArgumentNullException.ThrowIfNull(code);

        var args = new List<string>();
        foreach ((int start, int length) in SplitArgumentSpans(code, callStart))
            args.Add(code.Substring(start, length).Trim());
        return args;
    }

    /// <summary>
    /// То же самое, но значения аргументов читаются из <paramref name="rawCode"/>
    /// (<see cref="PythonStatement.RawCode"/>), а границы считаются по <paramref name="code"/>.
    /// Нужно правилам, которым важно ЗНАЧЕНИЕ литерала: <c>open('flag.killme', 'w')</c>.
    /// </summary>
    /// <param name="code">Код с вырезанными литералами (по нему считаются границы).</param>
    /// <param name="rawCode">Тот же код со значениями литералов, символ в символ той же длины.</param>
    /// <param name="callStart">Позиция открывающей скобки.</param>
    /// <returns>Аргументы со значениями литералов.</returns>
    public static IReadOnlyList<string> SplitArgumentsRaw(string code, string rawCode, int callStart)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(rawCode);

        var args = new List<string>();
        foreach ((int start, int length) in SplitArgumentSpans(code, callStart))
        {
            string source = start + length <= rawCode.Length ? rawCode : code;
            args.Add(source.Substring(start, length).Trim());
        }
        return args;
    }

    /// <summary>Границы аргументов вызова в исходной строке.</summary>
    /// <param name="code">Код с вырезанными литералами.</param>
    /// <param name="callStart">Позиция открывающей скобки.</param>
    /// <returns>Пары «начало, длина».</returns>
    public static IReadOnlyList<(int Start, int Length)> SplitArgumentSpans(string code, int callStart)
    {
        ArgumentNullException.ThrowIfNull(code);

        var spans = new List<(int, int)>();
        if (callStart < 0 || callStart >= code.Length || code[callStart] != '(') return spans;

        int depth = 0, start = callStart + 1;
        for (int i = callStart; i < code.Length; i++)
        {
            char c = code[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    // Пустой хвост у `f()` аргументом не считается, а у `f(a,)` — считается.
                    if (code[start..i].Trim().Length > 0 || spans.Count > 0) spans.Add((start, i - start));
                    return spans;
                }
            }
            else if (c == ',' && depth == 1)
            {
                spans.Add((start, i - start));
                start = i + 1;
            }
        }
        return spans;
    }

    /// <summary>Позиция открывающей скобки вызова <paramref name="callee"/> либо -1.</summary>
    /// <param name="code">Код.</param>
    /// <param name="callee">Имя вызываемого.</param>
    /// <param name="allowMethodReceiver">
    /// Разрешать точку перед именем: <c>true</c> нужен для методов (<c>g_eventBus.addListener</c>),
    /// <c>false</c> — для встроенных функций, иначе <c>shelve.open(...)</c> засчитывается
    /// и как <c>shelve.open</c>, и как <c>open</c> (замерено: 20 дублей вместо 10 находок).
    /// </param>
    /// <returns>Позиция <c>(</c> либо -1.</returns>
    public static int FindCall(string code, string callee, bool allowMethodReceiver = true)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(callee);

        int from = 0;
        while (true)
        {
            int at = code.IndexOf(callee, from, StringComparison.Ordinal);
            if (at < 0) return -1;

            int after = at + callee.Length;
            bool boundaryBefore = at == 0 || (!char.IsLetterOrDigit(code[at - 1]) && code[at - 1] != '_'
                                              && (allowMethodReceiver || code[at - 1] != '.'));
            if (boundaryBefore)
            {
                int j = after;
                while (j < code.Length && code[j] == ' ') j++;
                if (j < code.Length && code[j] == '(') return j;
            }
            from = at + 1;
        }
    }

    /// <summary>
    /// Точечные имена модулей, которые упоминает оператор <c>import</c>/<c>from</c>.
    /// Для <c>import a.b, c as d</c> это <c>a.b</c> и <c>c</c>; для <c>from X import …</c> — <c>X</c>.
    /// </summary>
    /// <param name="statement">Оператор.</param>
    /// <returns>Имена модулей.</returns>
    public static IEnumerable<string> ImportedModules(PythonStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        string code = statement.Trimmed;

        if (statement.StartsWith("from"))
        {
            int importAt = code.IndexOf(" import ", StringComparison.Ordinal);
            if (importAt > 4)
            {
                string module = code[4..importAt].Trim();
                if (module.Length > 0) yield return module;
            }
            yield break;
        }

        if (!statement.StartsWith("import")) yield break;

        foreach (string piece in code[6..].Split(','))
        {
            string p = piece.Trim();
            int asAt = p.IndexOf(" as ", StringComparison.Ordinal);
            if (asAt >= 0) p = p[..asAt].Trim();
            if (p.Length > 0) yield return p;
        }
    }

    /// <summary>
    /// Однострочные строковые литералы строки: префикс (<c>u</c>, <c>b</c>, <c>r</c>…),
    /// содержимое и позиция. Читается из <see cref="PythonPhysicalLine.Raw"/> по координатам
    /// кавычек, найденных в <see cref="PythonPhysicalLine.Code"/> — там содержимое уже вырезано,
    /// поэтому кавычка внутри литерала за границу не сойдёт.
    /// </summary>
    /// <param name="line">Строка файла.</param>
    /// <returns>Литералы в порядке появления.</returns>
    public static IEnumerable<(string Prefix, string Content, int Column)> StringLiterals(PythonPhysicalLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        string code = line.Code;
        string raw = line.Raw;
        if (line.InsideStringLiteral || code.Length != raw.Length) yield break;

        int i = 0;
        while (i < code.Length)
        {
            char c = code[i];
            if (c is not ('\'' or '"')) { i++; continue; }

            // Тройные литералы пропускаем: докстринг — не текст для GUI.
            if (i + 2 < code.Length && code[i + 1] == c && code[i + 2] == c) { i += 3; continue; }

            int close = code.IndexOf(c, i + 1);
            if (close < 0) yield break;

            int prefixStart = i;
            while (prefixStart > 0 && char.IsLetter(raw[prefixStart - 1])) prefixStart--;

            yield return (raw[prefixStart..i], raw[(i + 1)..close], i + 1);
            i = close + 1;
        }
    }

    /// <summary>
    /// Модуль относится к ИГРЕ (клиентский код или нативный модуль движка), а не к stdlib
    /// и не к собственному дереву мода. Только такие имена интересны правилам WOT001/WOT004.
    /// </summary>
    /// <param name="oracle">Оракул.</param>
    /// <param name="context">Контекст.</param>
    /// <param name="module">Точечное имя модуля.</param>
    /// <returns><c>true</c>, если игровой.</returns>
    public static bool IsGameModule(ClientApiOracle oracle, PreflightContext context, string module)
    {
        ArgumentNullException.ThrowIfNull(oracle);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(module)) return false;

        if (ClientApiOracle.IsNative(module)) return true;
        if (ClientApiOracle.IsBuiltin(module)) return false;
        if (context.OwnModules.Contains(module)) return false;
        if (ClientApiOracle.IsModApi(module)) return false;
        if (!oracle.ModuleExists(module)) return false;
        return !oracle.IsVendorModule(module);
    }
}
