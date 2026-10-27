using System.Text;

namespace WotModCockpit.Core.Preflight;

/// <summary>Что за блок открыт заголовком с двоеточием.</summary>
public enum PythonBlockKind
{
    /// <summary>Тело <c>def</c>.</summary>
    Def,

    /// <summary>Тело <c>class</c>.</summary>
    Class,

    /// <summary>Тело <c>try:</c> — то, что защищено.</summary>
    Try,

    /// <summary>Тело <c>except</c>/<c>finally</c> — это уже НЕ защищённая часть.</summary>
    Except,

    /// <summary>Тело <c>if</c>/<c>elif</c>/<c>else</c>.</summary>
    If,

    /// <summary>Тело <c>for</c>/<c>while</c>.</summary>
    Loop,

    /// <summary>Тело <c>with</c>.</summary>
    With,

    /// <summary>Любой другой блок с двоеточием.</summary>
    Other
}

/// <summary>Одна физическая строка файла.</summary>
/// <param name="Number">Номер, 1-based.</param>
/// <param name="Raw">Исходный текст без завершающих <c>\r\n</c>.</param>
/// <param name="Code">
/// Тот же текст, но с ВЫРЕЗАННЫМ содержимым строковых литералов и комментариев: символы заменены
/// пробелами, длина сохранена. Именно поэтому колонку находки можно брать прямо из индекса
/// в <see cref="Code"/> — она совпадёт с колонкой в <see cref="Raw"/>.
/// </param>
/// <param name="Indent">Отступ в символах (табуляция = 8).</param>
/// <param name="InsideStringLiteral">Строка целиком лежит внутри тройного литерала.</param>
/// <param name="CodeWithLiterals">
/// Тот же текст с вырезанными ТОЛЬКО комментариями: содержимое литералов на месте.
/// Нужен правилам, которым важно ЗНАЧЕНИЕ литерала — <c>open('flag.killme', 'w')</c>,
/// <c>setattr(cls, '__rotate', w)</c>. Длина и все позиции совпадают с <see cref="Code"/>,
/// поэтому границы аргументов достаточно посчитать один раз по <see cref="Code"/>.
/// </param>
public sealed record PythonPhysicalLine(int Number, string Raw, string Code, int Indent, bool InsideStringLiteral,
    string CodeWithLiterals)
{
    /// <summary>Код без отступа и хвостовых пробелов.</summary>
    public string Trimmed { get; } = Code.Trim();

    /// <summary>В строке нет исполняемого кода (пусто, комментарий или тело докстринга).</summary>
    public bool IsBlank => Trimmed.Length == 0;
}

/// <summary>
/// Логический оператор: одна или несколько физических строк, склеенных по незакрытым скобкам
/// и по обратному слэшу. Правила работают именно с ним — иначе
/// <c>g_eventBus.addListener(events.X,\n handler)</c> выглядел бы как два разных оператора.
/// </summary>
public sealed class PythonStatement
{
    /// <summary>Порядковый номер в <see cref="PythonLineModel.Statements"/>.</summary>
    public required int Index { get; init; }

    /// <summary>Номер первой физической строки, 1-based.</summary>
    public required int Line { get; init; }

    /// <summary>Номер последней физической строки, 1-based.</summary>
    public required int EndLine { get; init; }

    /// <summary>Отступ первой строки.</summary>
    public required int Indent { get; init; }

    /// <summary>Склеенный код: комментарии и содержимое литералов вырезаны.</summary>
    public required string Code { get; init; }

    /// <summary>
    /// Тот же склеенный код, но со ЗНАЧЕНИЯМИ литералов. Позиции совпадают с <see cref="Code"/>
    /// символ в символ, поэтому границы аргументов вызова считаются по <see cref="Code"/>
    /// (там кавычка внутри литерала не собьёт счётчик скобок), а сами значения читаются отсюда.
    /// </summary>
    public required string RawCode { get; init; }

    /// <summary>Код первой физической строки (для показа в списке находок).</summary>
    public required string RawFirstLine { get; init; }

    /// <summary>Оператор лежит внутри <c>try:</c> (в любом объемлющем блоке этого файла).</summary>
    public bool InTry { get; internal set; }

    /// <summary>Оператор лежит внутри <c>except</c>/<c>finally</c>.</summary>
    public bool InExcept { get; internal set; }

    /// <summary>
    /// Оператор лежит внутри ветки <c>if &lt;КОНСТАНТА&gt;:</c> — так пишут кроссклиентный код
    /// (<c>if IS_WG:</c> в XVM). Отсутствие символа в 1.44 там не ошибка, а ожидаемая ветка.
    /// </summary>
    public bool InConstantIf { get; internal set; }

    /// <summary>Индекс объемлющего определения в <see cref="PythonLineModel.Definitions"/> либо -1.</summary>
    public int EnclosingDefinition { get; internal set; } = -1;

    /// <summary>
    /// Оператор исполняется ПРИ ИМПОРТЕ модуля: он не внутри <c>def</c>/<c>class</c>.
    /// Нулевой отступ здесь не требуется намеренно — тело модульного <c>if</c>/<c>for</c>
    /// исполняется при импорте точно так же (именно так устроен <c>battlehits</c>:
    /// <c>if not _configured: … configure()</c> в конце файла).
    /// </summary>
    public bool IsModuleLevel => EnclosingDefinition < 0;

    /// <summary>Код без хвостовых пробелов.</summary>
    public string Trimmed => Code.Trim();

    /// <summary>Оператор начинается с ключевого слова (с проверкой границы слова).</summary>
    /// <param name="keyword">Ключевое слово без пробела.</param>
    /// <returns><c>true</c>, если начинается.</returns>
    public bool StartsWith(string keyword)
    {
        string t = Trimmed;
        if (!t.StartsWith(keyword, StringComparison.Ordinal)) return false;
        if (t.Length == keyword.Length) return true;
        char c = t[keyword.Length];
        return !char.IsLetterOrDigit(c) && c != '_';
    }
}

/// <summary>Определение <c>def</c> или <c>class</c> вместе с границами тела и декораторами.</summary>
public sealed class PythonDefinition
{
    /// <summary>Порядковый номер в <see cref="PythonLineModel.Definitions"/>.</summary>
    public required int Index { get; init; }

    /// <summary>Имя.</summary>
    public required string Name { get; init; }

    /// <summary><c>class</c>, а не <c>def</c>.</summary>
    public required bool IsClass { get; init; }

    /// <summary>Строка объявления, 1-based.</summary>
    public required int Line { get; init; }

    /// <summary>Отступ объявления.</summary>
    public required int Indent { get; init; }

    /// <summary>Индекс оператора-заголовка.</summary>
    public required int HeaderStatement { get; init; }

    /// <summary>Первый оператор тела (индекс в <see cref="PythonLineModel.Statements"/>).</summary>
    public int BodyStart { get; internal set; }

    /// <summary>Первый оператор ПОСЛЕ тела (полуинтервал).</summary>
    public int BodyEnd { get; internal set; }

    /// <summary>Индекс объемлющего определения либо -1.</summary>
    public int Parent { get; internal set; } = -1;

    /// <summary>Декораторы в порядке объявления, без <c>@</c> и без аргументов: <c>_safe</c>, <c>overrideMethod</c>.</summary>
    public IReadOnlyList<string> Decorators { get; internal set; } = Array.Empty<string>();

    /// <summary>Текст в скобках объявления, без скобок; пусто у <c>class X:</c>.</summary>
    public string Parameters { get; internal set; } = string.Empty;

    /// <summary>Имена параметров в порядке объявления (без <c>*</c>, <c>**</c> и значений по умолчанию).</summary>
    public IReadOnlyList<string> ParameterNames { get; internal set; } = Array.Empty<string>();

    /// <summary>Определение верхнего уровня модуля.</summary>
    public bool IsTopLevel => Parent < 0 && Indent == 0;
}

/// <summary>
/// Лексическая модель одного файла мода: физические строки, логические операторы,
/// определения и блочная структура.
///
/// <para><b>Почему не AST.</b> Код мода — Python 2.7 (<c>print x</c>, <c>except X, e</c>,
/// <c>0777</c>, <c>unicode</c>), из C# его дерево не построить, а тянуть в WPF-приложение
/// интерпретатор ради 12 файлов мода — несоразмерно. Подход тот же, что в
/// <see cref="ApiIndex.PythonSymbolScanner"/>: построчно, стек отступов.</para>
///
/// <para><b>Отличие от сканера индекса — вырезание строковых литералов.</b> Сканеру индекса
/// состояние между строками противопоказано: он читает вывод uncompyle6, где в 135 файлах
/// есть неэкранированные апострофы внутри литералов, и любой счётчик кавычек рассинхронится
/// до конца файла. Здесь вход другой — исходник, который написал человек и который обязан
/// компилироваться Python 2.7, поэтому тройные литералы отслеживаются между строками честно.
/// Без этого правила срабатывали бы на примерах кода внутри докстрингов: в эталонном шаблоне
/// (1088 строк) докстринги содержат и <c>BigWorld.callback</c>, и <c>getattr(cls, name)</c>.</para>
///
/// <para>Содержимое литералов заменяется ПРОБЕЛАМИ, а не удаляется: длина строки сохраняется,
/// поэтому колонка находки, посчитанная по <see cref="PythonPhysicalLine.Code"/>, совпадает
/// с колонкой в исходном тексте.</para>
/// </summary>
public sealed class PythonLineModel
{
    private readonly PythonPhysicalLine[] _lines;
    private readonly PythonStatement[] _statements;
    private readonly PythonDefinition[] _definitions;

    private PythonLineModel(
        PythonPhysicalLine[] lines,
        PythonStatement[] statements,
        PythonDefinition[] definitions,
        bool hasNonAscii,
        bool hasCodingDeclaration,
        bool hasBom)
    {
        _lines = lines;
        _statements = statements;
        _definitions = definitions;
        HasNonAscii = hasNonAscii;
        HasCodingDeclaration = hasCodingDeclaration;
        HasBom = hasBom;
    }

    /// <summary>Физические строки в порядке файла.</summary>
    public IReadOnlyList<PythonPhysicalLine> Lines => _lines;

    /// <summary>Логические операторы в порядке файла.</summary>
    public IReadOnlyList<PythonStatement> Statements => _statements;

    /// <summary>Определения <c>def</c>/<c>class</c> в порядке файла.</summary>
    public IReadOnlyList<PythonDefinition> Definitions => _definitions;

    /// <summary>В файле есть символ вне ASCII (то есть байт &gt;= 0x80 в исходнике).</summary>
    public bool HasNonAscii { get; }

    /// <summary>В первых двух строках объявлена кодировка по PEP 263.</summary>
    public bool HasCodingDeclaration { get; }

    /// <summary>Файл начинается с UTF-8 BOM.</summary>
    public bool HasBom { get; }

    /// <summary>Разбирает текст файла. Не бросает ни на каком входе.</summary>
    /// <param name="text">Содержимое файла.</param>
    /// <returns>Модель.</returns>
    public static PythonLineModel Parse(string? text)
    {
        text ??= string.Empty;

        bool hasBom = text.Length > 0 && text[0] == '﻿';
        if (hasBom) text = text[1..];

        PythonPhysicalLine[] lines = SplitAndStrip(text, out bool hasNonAscii);
        bool coding = HasCoding(lines);

        PythonStatement[] statements = BuildStatements(lines);
        PythonDefinition[] definitions = BuildStructure(statements);

        return new PythonLineModel(lines, statements, definitions, hasNonAscii, coding, hasBom);
    }

    // ------------------------------------------------------------------ строки

    private static bool HasCoding(PythonPhysicalLine[] lines)
    {
        // PEP 263 требует объявление в первых ДВУХ строках; ищем по СЫРОМУ тексту,
        // потому что объявление живёт в комментарии, а комментарии уже вырезаны.
        for (int i = 0; i < lines.Length && i < 2; i++)
        {
            string raw = lines[i].Raw;
            int hash = raw.IndexOf('#');
            if (hash < 0) continue;
            if (MatchesCoding(raw.AsSpan(hash))) return true;
        }
        return false;
    }

    /// <summary>Ищет <c>coding[:=]\s*[-\w.]+</c> — регулярка PEP 263, только вручную.</summary>
    private static bool MatchesCoding(ReadOnlySpan<char> span)
    {
        int at = span.IndexOf("coding", StringComparison.Ordinal);
        while (at >= 0)
        {
            int j = at + 6;
            if (j < span.Length && (span[j] == ':' || span[j] == '='))
            {
                j++;
                while (j < span.Length && span[j] == ' ') j++;
                if (j < span.Length && (char.IsLetterOrDigit(span[j]) || span[j] == '-' || span[j] == '_'))
                    return true;
            }
            ReadOnlySpan<char> rest = span[(at + 6)..];
            int next = rest.IndexOf("coding", StringComparison.Ordinal);
            if (next < 0) return false;
            at = at + 6 + next;
        }
        return false;
    }

    private static PythonPhysicalLine[] SplitAndStrip(string text, out bool hasNonAscii)
    {
        var result = new List<PythonPhysicalLine>(Math.Max(16, text.Length / 32));
        bool nonAscii = false;

        char tripleQuote = '\0';   // != 0 => мы внутри тройного литерала
        int pos = 0;
        int number = 0;

        while (pos < text.Length)
        {
            int eol = text.IndexOf('\n', pos);
            string raw;
            if (eol < 0) { raw = text[pos..]; pos = text.Length; }
            else { raw = text[pos..eol]; pos = eol + 1; }

            number++;
            if (raw.Length > 0 && raw[^1] == '\r') raw = raw[..^1];

            foreach (char c in raw) if (c > 127) { nonAscii = true; break; }

            bool wholeLineInString = tripleQuote != '\0';
            string code = StripLine(raw, ref tripleQuote, out string withLiterals);

            int indent = 0;
            foreach (char c in raw)
            {
                if (c == ' ') indent++;
                else if (c == '\t') indent += 8;
                else break;
            }

            result.Add(new PythonPhysicalLine(number, raw, code, indent, wholeLineInString, withLiterals));
        }

        hasNonAscii = nonAscii;
        return result.ToArray();
    }

    /// <summary>
    /// Заменяет пробелами комментарий и содержимое строковых литералов, сохраняя длину строки.
    /// <paramref name="tripleQuote"/> — единственное состояние, переносимое между строками.
    /// </summary>
    private static string StripLine(string raw, ref char tripleQuote, out string withLiterals)
    {
        if (raw.Length == 0) { withLiterals = raw; return raw; }

        char[] buffer = raw.ToCharArray();     // без комментариев И без содержимого литералов
        char[] keep = raw.ToCharArray();       // без комментариев, но со значениями литералов
        int i = 0;

        if (tripleQuote != '\0')
        {
            int close = FindTripleClose(raw, 0, tripleQuote);
            if (close < 0)
            {
                for (int k = 0; k < buffer.Length; k++) buffer[k] = ' ';
                withLiterals = new string(keep);
                return new string(buffer);
            }
            for (int k = 0; k < close; k++) buffer[k] = ' ';
            tripleQuote = '\0';
            i = close + 3;
        }

        while (i < buffer.Length)
        {
            char c = buffer[i];

            if (c == '#')
            {
                for (int k = i; k < buffer.Length; k++) { buffer[k] = ' '; keep[k] = ' '; }
                break;
            }

            if (c is '\'' or '"')
            {
                bool triple = i + 2 < buffer.Length && buffer[i + 1] == c && buffer[i + 2] == c;
                if (triple)
                {
                    int close = FindTripleClose(raw, i + 3, c);
                    if (close < 0)
                    {
                        for (int k = i + 3; k < buffer.Length; k++) buffer[k] = ' ';
                        tripleQuote = c;
                        break;
                    }
                    for (int k = i + 3; k < close; k++) buffer[k] = ' ';
                    i = close + 3;
                    continue;
                }

                int j = i + 1;
                bool closed = false;
                while (j < buffer.Length)
                {
                    if (buffer[j] == '\\') { j += 2; continue; }
                    if (buffer[j] == c) { closed = true; break; }
                    j++;
                }
                int end = closed ? j : buffer.Length;
                for (int k = i + 1; k < end && k < buffer.Length; k++) buffer[k] = ' ';
                i = closed ? j + 1 : buffer.Length;
                continue;
            }

            i++;
        }

        withLiterals = new string(keep);
        return new string(buffer);
    }

    /// <summary>Индекс закрывающего тройного литерала начиная с <paramref name="from"/> либо -1.</summary>
    private static int FindTripleClose(string raw, int from, char quote)
    {
        for (int i = from; i + 2 < raw.Length; i++)
        {
            if (raw[i] == '\\') { i++; continue; }
            if (raw[i] == quote && raw[i + 1] == quote && raw[i + 2] == quote) return i;
        }
        return -1;
    }

    // ------------------------------------------------------- логические операторы

    private static PythonStatement[] BuildStatements(PythonPhysicalLine[] lines)
    {
        var result = new List<PythonStatement>(lines.Length);
        var sb = new StringBuilder(128);
        var rawSb = new StringBuilder(128);

        int i = 0;
        while (i < lines.Length)
        {
            if (lines[i].IsBlank) { i++; continue; }

            int startLine = lines[i].Number;
            int indent = lines[i].Indent;
            string rawFirst = lines[i].Raw;

            sb.Clear();
            rawSb.Clear();
            int depth = 0;
            int last = i;

            while (i < lines.Length)
            {
                string code = lines[i].Code;

                // Оба представления режутся ОДНИМИ И ТЕМИ ЖЕ границами, посчитанными по Code:
                // только так позиции в Code и RawCode совпадают символ в символ.
                int from = 0;
                while (from < code.Length && char.IsWhiteSpace(code[from])) from++;
                int to = code.Length;
                while (to > from && char.IsWhiteSpace(code[to - 1])) to--;

                if (sb.Length > 0) { sb.Append(' '); rawSb.Append(' '); }
                sb.Append(code, from, to - from);
                rawSb.Append(lines[i].CodeWithLiterals, from, to - from);

                depth += BracketDelta(code);
                if (depth < 0) depth = 0;
                bool backslash = to > from && code[to - 1] == '\\';
                last = i;
                i++;

                if (depth == 0 && !backslash) break;
            }

            if (sb.Length == 0) continue;

            result.Add(new PythonStatement
            {
                Index = result.Count,
                Line = startLine,
                EndLine = lines[last].Number,
                Indent = indent,
                Code = sb.ToString(),
                RawCode = rawSb.ToString(),
                RawFirstLine = rawFirst
            });
        }

        return result.ToArray();
    }

    private static int BracketDelta(string code)
    {
        int d = 0;
        foreach (char c in code)
        {
            if (c is '(' or '[' or '{') d++;
            else if (c is ')' or ']' or '}') d--;
        }
        return d;
    }

    // -------------------------------------------------------- блоки и определения

    private struct Block
    {
        public int HeaderIndent;
        public PythonBlockKind Kind;
        public int DefinitionIndex;
        public bool ConstantIf;
    }

    private static PythonDefinition[] BuildStructure(PythonStatement[] statements)
    {
        var definitions = new List<PythonDefinition>();
        var stack = new List<Block>();
        var pendingDecorators = new List<string>();

        foreach (PythonStatement st in statements)
        {
            while (stack.Count > 0 && st.Indent <= stack[^1].HeaderIndent)
            {
                Block closed = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                if (closed.DefinitionIndex >= 0) definitions[closed.DefinitionIndex].BodyEnd = st.Index;
            }

            bool inTry = false, inExcept = false, constIf = false;
            int enclosingDef = -1;
            for (int k = stack.Count - 1; k >= 0; k--)
            {
                if (stack[k].Kind == PythonBlockKind.Try) inTry = true;
                if (stack[k].Kind == PythonBlockKind.Except) inExcept = true;
                if (stack[k].ConstantIf) constIf = true;
                if (enclosingDef < 0 && stack[k].DefinitionIndex >= 0) enclosingDef = stack[k].DefinitionIndex;
            }

            st.InTry = inTry;
            st.InExcept = inExcept;
            st.InConstantIf = constIf;
            st.EnclosingDefinition = enclosingDef;

            string trimmed = st.Trimmed;

            if (trimmed.StartsWith('@'))
            {
                string name = DecoratorName(trimmed);
                if (name.Length > 0) pendingDecorators.Add(name);
                continue;
            }

            bool isDef = st.StartsWith("def");
            bool isClass = st.StartsWith("class");

            if (isDef || isClass)
            {
                string name = ReadDefinitionName(trimmed, isDef ? 3 : 5);
                var def = new PythonDefinition
                {
                    Index = definitions.Count,
                    Name = name,
                    IsClass = isClass,
                    Line = st.Line,
                    Indent = st.Indent,
                    HeaderStatement = st.Index,
                    Parent = enclosingDef,
                    Decorators = pendingDecorators.Count > 0 ? pendingDecorators.ToArray() : Array.Empty<string>()
                };
                def.BodyStart = st.Index + 1;
                def.BodyEnd = statements.Length;
                if (isDef)
                {
                    def.Parameters = ReadParameters(trimmed);
                    def.ParameterNames = SplitParameterNames(def.Parameters);
                }
                definitions.Add(def);
                pendingDecorators.Clear();

                stack.Add(new Block
                {
                    HeaderIndent = st.Indent,
                    Kind = isClass ? PythonBlockKind.Class : PythonBlockKind.Def,
                    DefinitionIndex = def.Index,
                    ConstantIf = constIf
                });
                continue;
            }

            pendingDecorators.Clear();

            if (trimmed.EndsWith(':'))
            {
                PythonBlockKind kind =
                    st.StartsWith("try") ? PythonBlockKind.Try
                    : st.StartsWith("except") || st.StartsWith("finally") ? PythonBlockKind.Except
                    : st.StartsWith("if") || st.StartsWith("elif") || st.StartsWith("else") ? PythonBlockKind.If
                    : st.StartsWith("for") || st.StartsWith("while") ? PythonBlockKind.Loop
                    : st.StartsWith("with") ? PythonBlockKind.With
                    : PythonBlockKind.Other;

                stack.Add(new Block
                {
                    HeaderIndent = st.Indent,
                    Kind = kind,
                    DefinitionIndex = -1,
                    ConstantIf = constIf || (kind == PythonBlockKind.If && IsConstantCondition(trimmed))
                });
            }
        }

        foreach (Block open in stack)
            if (open.DefinitionIndex >= 0) definitions[open.DefinitionIndex].BodyEnd = statements.Length;

        return definitions.ToArray();
    }

    /// <summary>
    /// Условие вида <c>if IS_WG:</c> / <c>if not constants.IS_DEVELOPMENT:</c> — то есть ветка,
    /// про которую автор САМ знает, что она не для этого клиента. Отсутствие символа внутри
    /// такой ветки понижается до предупреждения.
    /// </summary>
    private static bool IsConstantCondition(string header)
    {
        string cond = header[2..^1].Trim();            // между 'if' и ':'
        if (cond.StartsWith("not ", StringComparison.Ordinal)) cond = cond[4..].Trim();
        if (cond.Length == 0) return false;

        bool hasLetter = false;
        foreach (char c in cond)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '.') return false;
            if (char.IsLetter(c)) hasLetter = true;
        }
        if (!hasLetter) return false;

        // Последний сегмент цепочки обязан быть ALL_CAPS: constants.IS_DEVELOPMENT — да,
        // gui.mods.enabled — нет.
        int dot = cond.LastIndexOf('.');
        string tail = dot < 0 ? cond : cond[(dot + 1)..];
        if (tail.Length == 0) return false;
        foreach (char c in tail) if (char.IsLower(c)) return false;
        return true;
    }

    private static string DecoratorName(string trimmed)
    {
        int i = 1;
        while (i < trimmed.Length && char.IsWhiteSpace(trimmed[i])) i++;
        int start = i;
        while (i < trimmed.Length && (char.IsLetterOrDigit(trimmed[i]) || trimmed[i] == '_' || trimmed[i] == '.')) i++;
        return i > start ? trimmed[start..i] : string.Empty;
    }

    private static string ReadDefinitionName(string trimmed, int keywordLength)
    {
        int i = keywordLength;
        while (i < trimmed.Length && char.IsWhiteSpace(trimmed[i])) i++;
        int start = i;
        while (i < trimmed.Length && (char.IsLetterOrDigit(trimmed[i]) || trimmed[i] == '_')) i++;
        return i > start ? trimmed[start..i] : string.Empty;
    }

    private static string ReadParameters(string trimmed)
    {
        int open = trimmed.IndexOf('(');
        if (open < 0) return string.Empty;
        int depth = 0;
        for (int i = open; i < trimmed.Length; i++)
        {
            if (trimmed[i] == '(') depth++;
            else if (trimmed[i] == ')')
            {
                depth--;
                if (depth == 0) return trimmed[(open + 1)..i];
            }
        }
        return trimmed[(open + 1)..];
    }

    private static string[] SplitParameterNames(string parameters)
    {
        if (parameters.Trim().Length == 0) return Array.Empty<string>();

        var names = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i <= parameters.Length; i++)
        {
            if (i == parameters.Length || (parameters[i] == ',' && depth == 0))
            {
                string piece = parameters[start..i].Trim().TrimStart('*');
                int eq = piece.IndexOf('=');
                if (eq >= 0) piece = piece[..eq].Trim();
                if (piece.Length > 0 && (char.IsLetter(piece[0]) || piece[0] == '_')) names.Add(piece);
                start = i + 1;
                continue;
            }
            if (parameters[i] is '(' or '[' or '{') depth++;
            else if (parameters[i] is ')' or ']' or '}') depth--;
        }
        return names.ToArray();
    }

    // ------------------------------------------------------------------ запросы

    /// <summary>Определение по имени (первое подходящее) либо <c>null</c>.</summary>
    /// <param name="name">Имя.</param>
    /// <returns>Определение.</returns>
    public PythonDefinition? FindDefinition(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (PythonDefinition d in _definitions)
            if (string.Equals(d.Name, name, StringComparison.Ordinal)) return d;
        return null;
    }

    /// <summary>
    /// Ближайшее определение с таким именем, объявленное ДО указанной строки. Имя
    /// <c>wrapper</c> в моде встречается по нескольку раз (у каждого патча своя обёртка),
    /// поэтому «первое подходящее» указывало бы не на тот код: на эталонном шаблоне это
    /// давало три ложные ошибки WOT007.
    /// </summary>
    /// <param name="name">Имя.</param>
    /// <param name="beforeLine">Строка использования, 1-based.</param>
    /// <returns>Определение либо <c>null</c>.</returns>
    public PythonDefinition? FindDefinitionBefore(string? name, int beforeLine)
    {
        if (string.IsNullOrEmpty(name)) return null;

        PythonDefinition? best = null;
        foreach (PythonDefinition d in _definitions)
        {
            if (d.Line > beforeLine) break;
            if (!string.Equals(d.Name, name, StringComparison.Ordinal)) continue;
            if (best is null || d.Line > best.Line) best = d;
        }
        return best;
    }

    /// <summary>Определение верхнего уровня по имени либо <c>null</c>.</summary>
    /// <param name="name">Имя.</param>
    /// <returns>Определение.</returns>
    public PythonDefinition? FindTopLevelDefinition(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (PythonDefinition d in _definitions)
            if (d.IsTopLevel && string.Equals(d.Name, name, StringComparison.Ordinal)) return d;
        return null;
    }

    /// <summary>Операторы тела определения (без заголовка).</summary>
    /// <param name="definition">Определение.</param>
    /// <returns>Операторы.</returns>
    public IEnumerable<PythonStatement> BodyOf(PythonDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        for (int i = definition.BodyStart; i < definition.BodyEnd && i < _statements.Length; i++)
            yield return _statements[i];
    }

    /// <summary>
    /// В теле функции ЕСТЬ <c>try:</c>/<c>except</c>. Проверяются только операторы САМОЙ
    /// функции: <c>try</c> внутри вложенного <c>def</c> тело внешней функции не защищает.
    ///
    /// <para><b>Это НЕ ответ на вопрос «защищено ли тело».</b> Проверка чисто лексическая
    /// и не знает, ЧТО именно закрывает найденный <c>try</c>: <c>init()</c>, который зовёт
    /// <c>_load_config()</c> и <c>BigWorld.callback()</c> без защиты, а <c>try</c> держит
    /// только подписку на <c>PlayerEvents</c>, отвечает здесь <c>true</c>. Ответ на настоящий
    /// вопрос даёт <see cref="GuardAnalysis.BodyIsGuarded"/> — он живёт в правилах, потому
    /// что без <see cref="PreflightContext"/> отличить <c>BigWorld.callback</c> от
    /// <c>bucket.discard</c> нечем, а здесь оракула клиентского API нет.</para>
    ///
    /// <para>Метод оставлен публичным как дешёвый предфильтр (функция вообще без <c>try</c>
    /// разбора не требует) и как способ отличить «try нет совсем» от «try есть, но опасное
    /// лежит вне него» — второе даёт находке конкретную строку вместо заголовка функции.</para>
    /// </summary>
    /// <param name="definition">Определение.</param>
    /// <returns><c>true</c>, если в теле есть хотя бы один <c>try</c>.</returns>
    public bool BodyIsGuarded(PythonDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        foreach (PythonStatement st in BodyOf(definition))
        {
            if (st.EnclosingDefinition != definition.Index) continue;
            if (st.StartsWith("try") || st.StartsWith("except")) return true;
        }
        return false;
    }

    /// <summary>Строка файла по номеру (1-based) либо <c>null</c>.</summary>
    /// <param name="number">Номер строки.</param>
    /// <returns>Строка.</returns>
    public PythonPhysicalLine? LineAt(int number) =>
        number >= 1 && number <= _lines.Length ? _lines[number - 1] : null;

    /// <summary>Сколько физических строк в файле.</summary>
    public int LineCount => _lines.Length;
}
