using System.Runtime.CompilerServices;

namespace WotModCockpit.Core.ApiIndex;

/// <summary>
/// Лексический сканер одного файла декомпилированного клиента.
///
/// <para><b>Почему не AST.</b> Дерево — вывод uncompyle6 из байткода Python 2.7, и оно невалидно
/// НИ как py2, НИ как py3: все 8052 файла содержат голый <c>return</c> на уровне модуля,
/// 595 файлов отвергает <c>ast.parse</c> (литералы <c>0777</c>/<c>123L</c>, <c>print x</c>,
/// <c>except X, e</c>), 10 файлов декомпилировались с ошибками прямо в тело кода.</para>
///
/// <para><b>Почему без состояния между строками — это главное правило этого файла.</b>
/// В 135 файлах (600 строк) uncompyle6 печатает строковые литералы с неэкранированными
/// апострофами, например
/// <code>_logger.warning(bu'Couldn't find 'actualValue' field in data %s', recData)</code>
/// (<c>client/messenger/formatters/service_channel.py:275</c>). Любой счётчик кавычек или скобок
/// на этом рассинхронизируется, остаётся «внутри строки» до конца файла и МОЛЧА съедает остаток:
/// замеренная потеря — 391 <c>def</c> в одном этом файле и 1520 по корпусу. Поэтому единственное
/// состояние сканера — стек отступов, и он самовосстанавливается на следующем же определении.</para>
///
/// <para><b>Цена отказа от состояния</b> — 148 ложных символов (0.094 %), и все 148 лежат
/// в <c>common/site-packages/future-0.18.2</c>, то есть отсекаются фильтром
/// <see cref="SourceTier.Vendor"/>. В игровом коде ложных срабатываний ноль.</para>
///
/// <para><b>Ловушка CRLF.</b> Файлы в CRLF, поэтому «пустая» строка после разбиения по <c>\n</c>
/// равна <c>"\r"</c>: она не пуста, её отступ считается нулевым и стек родителей разрушается
/// на КАЖДОЙ пустой строке (а uncompyle6 ставит пустую строку между каждыми двумя методами).
/// Результат бага — 0 методов вместо 104 215. Обрезка <c>\r</c> идёт до вычисления отступа.</para>
/// </summary>
public sealed class PythonSymbolScanner
{
    /// <summary>Максимальная глубина стека родителей. В корпусе максимум 9 уровней вложенных классов.</summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// Сколько символов максимум просматривается вперёд ради многострочной сигнатуры.
    /// Порог 512 обрывался ровно на одном объявлении игрового кода —
    /// <c>makeCustomizationItemModel</c> (<c>client/gui/impl/lobby/customization/customization_item_data_packer.py:109</c>,
    /// строка объявления 742 символа), и символ показывался голым именем, как будто у него нет
    /// параметров. 1024 закрывает и его, и запас на следующую версию клиента.
    /// </summary>
    public const int SignatureLookahead = 1024;

    /// <summary>Сколько строк максимум просматривается вперёд ради многострочной сигнатуры.</summary>
    public const int SignatureLookaheadLines = 8;

    /// <summary>Потолок пропуска docstring: даже при рассинхроне теряется не больше стольких строк.</summary>
    public const int DocstringLineLimit = 400;

    private readonly ApiIndexOptions _options;

    /// <summary>Создаёт сканер с настройками по умолчанию.</summary>
    public PythonSymbolScanner() : this(ApiIndexOptions.Default) { }

    /// <summary>Создаёт сканер.</summary>
    /// <param name="options">Настройки индексации. <c>null</c> = <see cref="ApiIndexOptions.Default"/>.</param>
    public PythonSymbolScanner(ApiIndexOptions? options) => _options = options ?? ApiIndexOptions.Default;

    private struct Frame
    {
        public int Indent;
        public int SymbolIndex; // -1 для блока if/try/for — тело такого блока должно видеть настоящего родителя
        public bool IsClass;
    }

    /// <summary>
    /// Разбирает файл и дописывает найденные символы в <paramref name="output"/>.
    /// Не бросает ни на каком входе: битая декомпиляция, обрыв в середине строки, вложенность
    /// глубже <see cref="MaxDepth"/>, файл только из шапки, пустой файл — всё это штатные случаи.
    /// </summary>
    /// <param name="fileId">Идентификатор файла (индекс в массиве файлов).</param>
    /// <param name="text">Содержимое файла целиком.</param>
    /// <param name="output">Список-приёмник. <see cref="SymbolEntry.Parent"/> — индексы В ЭТОМ списке.</param>
    /// <param name="pool">Пул строк для имён и сигнатур. <c>null</c> = без пула.</param>
    public void Scan(int fileId, ReadOnlySpan<char> text, List<SymbolEntry> output, StringPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        pool ??= StringPool.Disabled;

        int line = 0;
        int pos = 0;
        int n = text.Length;

        // Флаги вынесены в локальные переменные: цикл проходит 1.46 млн строк корпуса,
        // и чтение полей объекта настроек на каждой строке заметно в профиле.
        bool optEvents = _options.IndexEvents;
        bool optSynthetic = _options.IndexSyntheticClasses;
        bool optConstants = _options.IndexConstants;
        bool optSingletons = _options.IndexModuleSingletons;
        bool optSignatures = _options.CaptureSignatures;
        bool optDocstrings = _options.SkipDocstrings;
        bool anyAssignmentRule = optEvents || optSynthetic || optConstants || optSingletons;

        // Один и тот же g_* переприсваивается в файле (g_replayCtrl = None ... = _ReplayCtrl()):
        // в индексе он должен быть ОДИН, иначе омонимия штрафует его же собственный балл.
        List<string>? seenSingletons = null;

        // Модульный синглтон вешается на символ-модуль, если построитель его добавил:
        // тогда в выдаче видно «PlayerEvents.g_playerEvents», а не голое имя.
        int moduleSymbol = output.Count > 0 && output[0].Kind == SymbolKind.Module && output[0].Parent < 0 ? 0 : -1;

        Span<Frame> stack = stackalloc Frame[MaxDepth];
        int sp = 0;

        DecoratorFlags pending = DecoratorFlags.None;
        bool sawDecorator = false;
        bool afterHeader = false;
        int docSkip = 0;
        char docQuote = '\0';

        while (pos < n)
        {
            line++;
            int lineStart = pos;
            int eol = text[pos..].IndexOf('\n');
            ReadOnlySpan<char> raw;
            if (eol < 0) { raw = text[pos..]; pos = n; }
            else { raw = text.Slice(pos, eol); pos += eol + 1; }

            // КРИТИЧНО: CRLF. Без этой обрезки пустая строка равна "\r", её отступ 0,
            // и стек родителей рушится на каждой пустой строке.
            if (raw.Length > 0 && raw[^1] == '\r') raw = raw[..^1];

            int ind = 0, col = 0;
            while (col < raw.Length && (raw[col] == ' ' || raw[col] == '\t'))
            {
                ind += raw[col] == '\t' ? 8 : 1;   // табуляция = 8 пробелов; табы встречаются в 79 файлах
                col++;
            }
            ReadOnlySpan<char> st = raw[col..];

            // Ограниченный пропуск docstring. По умолчанию ВЫКЛЮЧЕН: замерено, что он не отсекает
            // ни одного символа (ложные срабатывания сидят в модульных docstring в начале файла,
            // а не после определений), а рассинхрон стоит дорого.
            if (docSkip > 0)
            {
                docSkip--;
                if (IndexOfTriple(st, docQuote) >= 0) docSkip = 0;
                continue;
            }

            if (st.IsEmpty) continue;     // пустая строка стек не трогает
            if (st[0] == '#') continue;   // комментарий стек не трогает

            if (optDocstrings && afterHeader && (st[0] == '"' || st[0] == '\''))
            {
                afterHeader = false;
                char q = st[0];
                if (st.Length >= 3 && st[1] == q && st[2] == q)
                {
                    if (IndexOfTriple(st[3..], q) < 0) { docSkip = DocstringLineLimit; docQuote = q; }
                    continue;
                }
            }
            afterHeader = false;

            if (st[0] == '@')
            {
                pending |= ClassifyDecorator(st[1..]);
                sawDecorator = true;
                continue;
            }

            bool isDef = StartsWithKeyword(st, "def");
            bool isClass = StartsWithKeyword(st, "class");

            if (isDef || isClass)
            {
                int afterKeyword = isDef ? 4 : 6;
                ReadOnlySpan<char> name = ReadName(st[afterKeyword..], isDef, out int parenOffset);
                if (!name.IsEmpty)
                {
                    PopTo(stack, ref sp, ind);
                    int parent = NearestSymbol(stack, sp, out bool parentIsClass);

                    SymbolKind kind = isClass ? SymbolKind.Class
                                    : parent < 0 ? SymbolKind.Function
                                    : parentIsClass ? SymbolKind.Method
                                    : SymbolKind.NestedFunction;

                    string? signature = null;
                    if (optSignatures && parenOffset >= 0)
                        signature = CaptureSignature(text, lineStart + col + afterKeyword + parenOffset, pool);

                    output.Add(new SymbolEntry
                    {
                        FileId = fileId,
                        Line = line,
                        Parent = parent,
                        Name = pool.Intern(name),
                        Signature = signature,
                        Kind = kind,
                        Origin = SymbolOrigin.Keyword,
                        Decorators = sawDecorator ? pending : DecoratorFlags.None,
                        Indent = (byte)Math.Min(ind, 255)
                    });

                    if (sp < MaxDepth)
                        stack[sp++] = new Frame { Indent = ind, SymbolIndex = output.Count - 1, IsClass = isClass };

                    afterHeader = true;
                }

                pending = DecoratorFlags.None;
                sawDecorator = false;
                continue;
            }

            pending = DecoratorFlags.None;
            sawDecorator = false;

            // Строки-продолжения uncompyle6 всегда печатает ГЛУБЖЕ своего оператора, а закрывающую
            // скобку приклеивает к последнему элементу, поэтому они не всплывают на отступ родителя
            // и стек не ломают. Отслеживать баланс скобок для этого НЕ НУЖНО.
            PopTo(stack, ref sp, ind);

            // Дешёвый шлюз для трёх правил присваивания: одна векторизованная проверка на '='
            // вместо трёх поисков подстроки на каждой строке корпуса.
            int eq = anyAssignmentRule ? FindAssignment(st) : -1;

            if (eq > 0 && optEvents && TryReadEvent(st, out ReadOnlySpan<char> evName, out bool evQualified)
                && IsEventDeclarationContext(stack, sp, evQualified))
            {
                int owner = NearestClass(stack, sp);
                output.Add(new SymbolEntry
                {
                    FileId = fileId,
                    Line = line,
                    Parent = owner,
                    Name = pool.Intern(evName),
                    Signature = null,
                    Kind = SymbolKind.Event,
                    Origin = SymbolOrigin.EventAttribute,
                    Decorators = DecoratorFlags.None,
                    Indent = (byte)Math.Min(ind, 255)
                });
            }
            else if (eq > 0 && optSynthetic && TryReadSyntheticClass(st, out ReadOnlySpan<char> tName, out SymbolOrigin tOrigin))
            {
                int parent = NearestSymbol(stack, sp, out _);
                output.Add(new SymbolEntry
                {
                    FileId = fileId,
                    Line = line,
                    Parent = parent,
                    Name = pool.Intern(tName),
                    Signature = null,
                    Kind = SymbolKind.Class,
                    Origin = tOrigin,
                    Decorators = DecoratorFlags.None,
                    Indent = (byte)Math.Min(ind, 255)
                });
            }
            else if (eq > 0 && optConstants && TryReadConstant(st, out ReadOnlySpan<char> cName))
            {
                int parent = NearestSymbol(stack, sp, out _);
                output.Add(new SymbolEntry
                {
                    FileId = fileId,
                    Line = line,
                    Parent = parent,
                    Name = pool.Intern(cName),
                    Signature = null,
                    Kind = SymbolKind.Constant,
                    Origin = SymbolOrigin.Assignment,
                    Decorators = DecoratorFlags.None,
                    Indent = (byte)Math.Min(ind, 255)
                });
            }
            else if (eq > 0 && optSingletons && ind == 0 && TryReadModuleSingleton(st, out ReadOnlySpan<char> gName))
            {
                string interned = pool.Intern(gName);
                seenSingletons ??= new List<string>(4);
                if (!seenSingletons.Contains(interned, StringComparer.Ordinal))
                {
                    seenSingletons.Add(interned);
                    output.Add(new SymbolEntry
                    {
                        FileId = fileId,
                        Line = line,
                        Parent = moduleSymbol,
                        Name = interned,
                        Signature = null,
                        Kind = SymbolKind.Service,
                        Origin = SymbolOrigin.Assignment,
                        Decorators = DecoratorFlags.None,
                        Indent = 0
                    });
                }
            }

            if (IsBlockKeyword(st) && sp < MaxDepth)
                stack[sp++] = new Frame { Indent = ind, SymbolIndex = -1, IsClass = false };
        }
    }

    // ------------------------------------------------------------------ стек

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PopTo(Span<Frame> stack, ref int sp, int indent)
    {
        while (sp > 0 && indent <= stack[sp - 1].Indent) sp--;
    }

    private static int NearestSymbol(Span<Frame> stack, int sp, out bool isClass)
    {
        for (int k = sp - 1; k >= 0; k--)
            if (stack[k].SymbolIndex >= 0) { isClass = stack[k].IsClass; return stack[k].SymbolIndex; }
        isClass = false;
        return -1;
    }

    private static int NearestClass(Span<Frame> stack, int sp)
    {
        for (int k = sp - 1; k >= 0; k--)
            if (stack[k].SymbolIndex >= 0 && stack[k].IsClass) return stack[k].SymbolIndex;
        return -1;
    }

    // ------------------------------------------------------------------ разбор строки

    /// <summary>
    /// Позиция знака присваивания верхнего уровня строки либо -1.
    /// Дешёвый шлюз перед правилами событий, namedtuple и констант: один векторизованный
    /// поиск символа вместо трёх поисков подстроки на каждой из 1.46 млн строк корпуса.
    /// Сравнения (<c>==</c>, <c>!=</c>, <c>&lt;=</c>, <c>&gt;=</c>) и составные присваивания
    /// (<c>+=</c>, <c>-=</c>) отсекаются здесь же.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindAssignment(ReadOnlySpan<char> st)
    {
        int eq = st.IndexOf('=');
        if (eq <= 0) return -1;
        if (eq + 1 < st.Length && st[eq + 1] == '=') return -1;

        char prev = st[eq - 1];
        if (prev is '=' or '!' or '<' or '>' or '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^') return -1;

        return eq;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool StartsWithKeyword(ReadOnlySpan<char> st, string kw)
    {
        if (st.Length <= kw.Length) return false;
        if (!st.StartsWith(kw, StringComparison.Ordinal)) return false;
        char c = st[kw.Length];
        return c == ' ' || c == '\t';
    }

    /// <summary>
    /// Читает имя после <c>def</c>/<c>class</c>.
    /// Для <c>def</c> обязательна открывающая скобка: это бесплатно отсекает битую декомпиляцию вида
    /// <c>def isDailySubsQuestParse error at or near `LOAD_FAST'</c> (10 таких файлов в корпусе).
    /// </summary>
    private static ReadOnlySpan<char> ReadName(ReadOnlySpan<char> s, bool requireParen, out int parenOffset)
    {
        parenOffset = -1;
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        int start = i;
        if (i >= s.Length) return default;
        char c0 = s[i];
        if (!(char.IsAsciiLetter(c0) || c0 == '_')) return default;
        i++;
        while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_')) i++;
        int end = i;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        if (i >= s.Length) return default;

        if (s[i] == '(') parenOffset = i;
        else if (requireParen || s[i] != ':') return default;

        return s[start..end];
    }

    /// <summary>
    /// Объявление события: <c>[self.|cls.]&lt;имя&gt; = [Event.](Event|SafeEvent|SuspendedEvent)(...)</c>.
    /// Аргумент в скобках обязан допускаться и быть непустым: <c>Event.Event(em)</c>,
    /// <c>Event.Event(self.__eManager)</c> — это 2 из 3 реальных форм.
    /// </summary>
    /// <param name="st">Строка без отступа.</param>
    /// <param name="name">Имя события.</param>
    /// <param name="qualified">Слева стоял <c>self.</c> или <c>cls.</c> — это точно атрибут, а не локальная переменная.</param>
    /// <returns>true, если строка объявляет событие.</returns>
    private static bool TryReadEvent(ReadOnlySpan<char> st, out ReadOnlySpan<char> name, out bool qualified)
    {
        name = default;
        qualified = false;

        // дешёвый отсев: без слова Event строка событием быть не может
        if (st.IndexOf("Event", StringComparison.Ordinal) < 0) return false;

        int i = 0;
        if (st.StartsWith("self.", StringComparison.Ordinal)) { i = 5; qualified = true; }
        else if (st.StartsWith("cls.", StringComparison.Ordinal)) { i = 4; qualified = true; }

        int start = i;
        if (i >= st.Length || !(char.IsAsciiLetter(st[i]) || st[i] == '_')) return false;
        i++;
        while (i < st.Length && (char.IsAsciiLetterOrDigit(st[i]) || st[i] == '_')) i++;
        int end = i;

        while (i < st.Length && st[i] == ' ') i++;
        if (i >= st.Length || st[i] != '=') return false;
        i++;
        if (i < st.Length && st[i] == '=') return false;   // это сравнение, а не присваивание
        while (i < st.Length && st[i] == ' ') i++;

        if (st[i..].StartsWith("Event.", StringComparison.Ordinal)) i += 6;

        int kindStart = i;
        while (i < st.Length && char.IsAsciiLetter(st[i])) i++;
        ReadOnlySpan<char> kind = st[kindStart..i];
        if (!kind.SequenceEqual("Event") && !kind.SequenceEqual("SafeEvent") && !kind.SequenceEqual("SuspendedEvent"))
            return false;

        while (i < st.Length && st[i] == ' ') i++;
        if (i >= st.Length || st[i] != '(') return false;

        // хвост после закрывающей скобки должен быть пустым или комментарием
        int close = st.LastIndexOf(')');
        if (close < i) return false;
        for (int k = close + 1; k < st.Length; k++)
        {
            if (st[k] == ' ') continue;
            if (st[k] == '#') break;
            return false;
        }

        name = st[start..end];
        return true;
    }

    /// <summary>
    /// Годится ли текущее место стека под ОБЪЯВЛЕНИЕ события.
    /// <para>
    /// Без этой проверки правило срабатывало и на локальную переменную внутри метода:
    /// <c>eventHandler = Event.Event()</c> в <c>BattleReplay.setDataCallback</c>
    /// (<c>client/BattleReplay.py:1330</c>) попадало в индекс как событие класса
    /// <c>BattleReplay</c>, которого у него нет. Правило простое: либо слева написано
    /// <c>self.</c>/<c>cls.</c>, либо ближайший кадр — класс (тело класса или модуль),
    /// но не функция.
    /// </para>
    /// </summary>
    /// <param name="stack">Стек кадров.</param>
    /// <param name="sp">Вершина стека.</param>
    /// <param name="qualified">Слева был <c>self.</c>/<c>cls.</c>.</param>
    /// <returns>true, если это объявление атрибута-события.</returns>
    private static bool IsEventDeclarationContext(Span<Frame> stack, int sp, bool qualified)
    {
        if (qualified) return true;
        int nearest = NearestSymbol(stack, sp, out bool isClass);
        return nearest < 0 || isClass;
    }

    /// <summary>
    /// Модульный синглтон: <c>g_playerEvents = _PlayerEvents()</c> на нулевом отступе.
    /// <para>
    /// Это ГЛАВНАЯ точка входа мода: подписка в клиенте пишется как
    /// <c>g_playerEvents.onAvatarReady += handler</c>, само имя встречается 783 раза в 193 файлах,
    /// а объявления <c>def</c>/<c>class</c> у него нет — то есть без этого правила поиск главного
    /// имени всей фичи честно отвечал «ничего не найдено». В корпусе 142 таких присваивания
    /// (114 различных имён), то есть 0.08 % индекса.
    /// </para>
    /// <para>
    /// Правая часть НЕ проверяется на вызов намеренно: <c>g_replayCtrl = None</c> с последующим
    /// присваиванием в функции — такой же настоящий синглтон (<c>BattleReplay.g_replayCtrl</c>),
    /// и мододелу нужно именно имя, а не форма инициализации. Префикс <c>g_</c> — соглашение
    /// самого клиента, оно и служит фильтром.
    /// </para>
    /// </summary>
    /// <param name="st">Строка без отступа.</param>
    /// <param name="name">Имя синглтона.</param>
    /// <returns>true, если строка объявляет модульный синглтон.</returns>
    private static bool TryReadModuleSingleton(ReadOnlySpan<char> st, out ReadOnlySpan<char> name)
    {
        name = default;
        if (st.Length < 4 || st[0] != 'g' || st[1] != '_') return false;

        int i = 2;
        if (!(char.IsAsciiLetterOrDigit(st[i]) || st[i] == '_')) return false;
        while (i < st.Length && (char.IsAsciiLetterOrDigit(st[i]) || st[i] == '_')) i++;
        int end = i;

        while (i < st.Length && (st[i] == ' ' || st[i] == '\t')) i++;
        if (i >= st.Length || st[i] != '=') return false;
        if (i + 1 < st.Length && st[i + 1] == '=') return false;

        name = st[..end];
        return true;
    }

    /// <summary>
    /// Класс, созданный присваиванием: <c>X = namedtuple(...)</c> (559 штук) и <c>X = type(...)</c> (82).
    /// </summary>
    private static bool TryReadSyntheticClass(ReadOnlySpan<char> st, out ReadOnlySpan<char> name, out SymbolOrigin origin)
    {
        name = default;
        origin = SymbolOrigin.NamedTuple;

        bool hasNamedTuple = st.IndexOf("namedtuple(", StringComparison.Ordinal) >= 0;
        bool hasType = !hasNamedTuple && st.IndexOf("type(", StringComparison.Ordinal) >= 0;
        if (!hasNamedTuple && !hasType) return false;

        int i = 0;
        int start = i;
        if (!(char.IsAsciiLetter(st[i]) || st[i] == '_')) return false;
        i++;
        while (i < st.Length && (char.IsAsciiLetterOrDigit(st[i]) || st[i] == '_')) i++;
        int end = i;

        while (i < st.Length && st[i] == ' ') i++;
        if (i >= st.Length || st[i] != '=') return false;
        i++;
        if (i < st.Length && st[i] == '=') return false;
        while (i < st.Length && st[i] == ' ') i++;

        ReadOnlySpan<char> rest = st[i..];
        if (hasNamedTuple)
        {
            if (!EndsWithCall(rest, "namedtuple")) return false;
            origin = SymbolOrigin.NamedTuple;
        }
        else
        {
            if (!rest.StartsWith("type(", StringComparison.Ordinal)) return false;
            origin = SymbolOrigin.TypeCall;
        }

        name = st[start..end];
        return true;
    }

    /// <summary>Начинается ли выражение с вызова <paramref name="fn"/>, возможно через точечный префикс.</summary>
    private static bool EndsWithCall(ReadOnlySpan<char> rest, string fn)
    {
        int call = rest.IndexOf(fn, StringComparison.Ordinal);
        if (call < 0) return false;
        if (call + fn.Length >= rest.Length || rest[call + fn.Length] != '(') return false;
        for (int k = 0; k < call; k++)
            if (!(char.IsAsciiLetterOrDigit(rest[k]) || rest[k] == '_' || rest[k] == '.')) return false;
        return true;
    }

    /// <summary>Константа: <c>ALL_CAPS = ...</c> длиной не меньше двух символов.</summary>
    private static bool TryReadConstant(ReadOnlySpan<char> st, out ReadOnlySpan<char> name)
    {
        name = default;
        int i = 0;
        if (!(char.IsAsciiLetterUpper(st[i]) || st[i] == '_')) return false;
        bool hasLetter = false;
        while (i < st.Length && (char.IsAsciiLetterUpper(st[i]) || char.IsAsciiDigit(st[i]) || st[i] == '_'))
        {
            if (char.IsAsciiLetterUpper(st[i])) hasLetter = true;
            i++;
        }
        if (!hasLetter || i < 2) return false;
        int end = i;
        while (i < st.Length && st[i] == ' ') i++;
        if (i >= st.Length || st[i] != '=') return false;
        if (i + 1 < st.Length && st[i + 1] == '=') return false;
        name = st[..end];
        return true;
    }

    /// <summary>
    /// Забирает сигнатуру начиная с открывающей скобки, разворачивая перенос строки.
    /// Просмотр вперёд ОГРАНИЧЕН (<see cref="SignatureLookahead"/> символов и
    /// <see cref="SignatureLookaheadLines"/> строк) и НЕ меняет ни позицию разбора, ни стек:
    /// многострочных сигнатур в корпусе всего 11 на 117 901, и платить за них состоянием нельзя.
    /// </summary>
    private static string? CaptureSignature(ReadOnlySpan<char> text, int open, StringPool pool)
    {
        if (open < 0 || open >= text.Length || text[open] != '(') return null;

        int depth = 0, lines = 0;
        int limit = Math.Min(text.Length, open + SignatureLookahead);
        int close = -1;
        for (int i = open; i < limit; i++)
        {
            char c = text[i];
            if (c == '\n') { if (++lines > SignatureLookaheadLines) break; }
            else if (c == '(') depth++;
            else if (c == ')') { depth--; if (depth == 0) { close = i; break; } }
        }
        if (close < 0) return null;

        Span<char> buf = stackalloc char[Math.Min(close - open + 1, SignatureLookahead)];
        int w = 0;
        bool space = false;
        for (int i = open; i <= close && w < buf.Length; i++)
        {
            char c = text[i];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { space = true; continue; }
            if (space && w > 0 && buf[w - 1] != '(' && c != ')' && c != ',') buf[w++] = ' ';
            space = false;
            if (w < buf.Length) buf[w++] = c;
        }
        return w == 0 ? null : pool.Intern(buf[..w]);
    }

    private static int IndexOfTriple(ReadOnlySpan<char> s, char q)
    {
        for (int i = 0; i + 2 < s.Length; i++)
            if (s[i] == q && s[i + 1] == q && s[i + 2] == q) return i;
        return -1;
    }

    /// <summary>
    /// Открывает ли строка блок. Тело блока всегда с бо́льшим отступом, поэтому достаточно префикса;
    /// кадр кладётся в стек с <c>SymbolIndex = -1</c>, чтобы условный <c>def</c> (таких 617)
    /// видел настоящего родителя, а не блок.
    /// </summary>
    private static bool IsBlockKeyword(ReadOnlySpan<char> st) =>
           st.StartsWith("if ", StringComparison.Ordinal)
        || st.StartsWith("elif ", StringComparison.Ordinal)
        || st.StartsWith("else", StringComparison.Ordinal)
        || st.StartsWith("try", StringComparison.Ordinal)
        || st.StartsWith("except", StringComparison.Ordinal)
        || st.StartsWith("finally", StringComparison.Ordinal)
        || st.StartsWith("for ", StringComparison.Ordinal)
        || st.StartsWith("while ", StringComparison.Ordinal)
        || st.StartsWith("with ", StringComparison.Ordinal);

    private static DecoratorFlags ClassifyDecorator(ReadOnlySpan<char> d)
    {
        if (d.StartsWith("staticmethod", StringComparison.Ordinal)) return DecoratorFlags.Static;
        if (d.StartsWith("classmethod", StringComparison.Ordinal)) return DecoratorFlags.ClassMethod;
        if (d.StartsWith("property", StringComparison.Ordinal)) return DecoratorFlags.Property;
        if (d.StartsWith("abstractmethod", StringComparison.Ordinal)) return DecoratorFlags.Abstract;
        if (d.EndsWith(".setter", StringComparison.Ordinal) || d.EndsWith(".deleter", StringComparison.Ordinal))
            return DecoratorFlags.Property;
        if (d.IndexOf("adisp", StringComparison.Ordinal) >= 0 || d.StartsWith("th_async", StringComparison.Ordinal))
            return DecoratorFlags.Async;
        if (d.IndexOf("replace_none_kwargs", StringComparison.Ordinal) >= 0)
            return DecoratorFlags.DependencyKwargs;
        return DecoratorFlags.Other;
    }
}

/// <summary>
/// Пул строк с ключом-спаном: имён символов 156 710, а уникальных 80 724 — пул экономит 2.4 МБ
/// и, что важнее, убирает 76 тысяч аллокаций на каждой пересборке индекса.
/// <para>
/// Своя таблица (открытая адресация + FNV-1a) нужна потому, что
/// <c>Dictionary.GetAlternateLookup</c> появился только в .NET 9, а проект на net8.0;
/// без него интернирование спана требовало бы <c>ToString()</c> ДО поиска, то есть той самой
/// аллокации, ради устранения которой пул и заводится.
/// </para>
/// <para>Экземпляр НЕ потокобезопасен — он заводится по одному на поток индексации.</para>
/// </summary>
public sealed class StringPool
{
    private string?[] _slots;
    private int _count;
    private int _mask;
    private readonly bool _enabled;

    /// <summary>Создаёт пул.</summary>
    /// <param name="enabled">false — интернирование выключено, <see cref="Intern"/> просто копирует спан.</param>
    /// <param name="capacityPow2">Начальная ёмкость, степень двойки.</param>
    public StringPool(bool enabled = true, int capacityPow2 = 1 << 14)
    {
        if (capacityPow2 <= 0 || (capacityPow2 & (capacityPow2 - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacityPow2), "ёмкость должна быть степенью двойки");
        _enabled = enabled;
        _slots = new string?[capacityPow2];
        _mask = capacityPow2 - 1;
    }

    /// <summary>Пул-пустышка: ничего не хранит, каждый вызов создаёт новую строку.</summary>
    public static StringPool Disabled { get; } = new(enabled: false, capacityPow2: 2);

    /// <summary>Сколько различных строк лежит в пуле.</summary>
    public int Count => _count;

    /// <summary>Возвращает единственный экземпляр строки, равной спану.</summary>
    /// <param name="s">Спан.</param>
    /// <returns>Строка из пула.</returns>
    public string Intern(ReadOnlySpan<char> s)
    {
        if (!_enabled) return s.ToString();

        int h = Hash(s) & _mask;
        while (true)
        {
            string? cur = _slots[h];
            if (cur is null)
            {
                string str = s.ToString();
                _slots[h] = str;
                if (++_count * 2 > _slots.Length) Grow();
                return str;
            }
            if (cur.Length == s.Length && s.SequenceEqual(cur.AsSpan())) return cur;
            h = (h + 1) & _mask;
        }
    }

    private void Grow()
    {
        string?[] old = _slots;
        _slots = new string?[old.Length * 2];
        _mask = _slots.Length - 1;
        foreach (string? v in old)
        {
            if (v is null) continue;
            int h = Hash(v.AsSpan()) & _mask;
            while (_slots[h] is not null) h = (h + 1) & _mask;
            _slots[h] = v;
        }
    }

    private static int Hash(ReadOnlySpan<char> s)
    {
        uint h = 2166136261u;
        for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
        return (int)(h & 0x7FFFFFFF);
    }
}
