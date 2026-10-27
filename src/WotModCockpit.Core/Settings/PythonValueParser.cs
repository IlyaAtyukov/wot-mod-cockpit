using System.Globalization;
using System.Text;

namespace WotModCockpit.Core.Settings;

/// <summary>Общий интерфейс узлов разбора выражения Python.</summary>
public interface IPyNode
{
    /// <summary>Смещение начала выражения в исходном тексте.</summary>
    int Start { get; set; }

    /// <summary>Смещение конца выражения в исходном тексте.</summary>
    int End { get; set; }
}

/// <summary>Базовый узел: хранит границы в исходном тексте.</summary>
public abstract record PyExprRecordBase : IPyNode
{
    /// <inheritdoc/>
    public int Start { get; set; }

    /// <inheritdoc/>
    public int End { get; set; }
}

/// <summary>Литерал: число, строка, <c>True</c>, <c>False</c>, <c>None</c>.</summary>
/// <param name="Value">Значение.</param>
public sealed record PyLiteralExpr(SettingValue Value) : PyExprRecordBase;

/// <summary>Список или кортеж.</summary>
/// <param name="Items">Элементы.</param>
/// <param name="IsTuple">Это кортеж в круглых скобках.</param>
public sealed record PyListExpr(IReadOnlyList<IPyNode> Items, bool IsTuple) : PyExprRecordBase;

/// <summary>Словарь.</summary>
/// <param name="Entries">Пары «ключ-значение».</param>
public sealed record PyDictExpr(IReadOnlyList<KeyValuePair<IPyNode, IPyNode>> Entries) : PyExprRecordBase;

/// <summary>Имя.</summary>
/// <param name="Name">Идентификатор.</param>
public sealed record PyNameExpr(string Name) : PyExprRecordBase;

/// <summary>Обращение к атрибуту: <c>templates.createCheckbox</c>.</summary>
/// <param name="Target">Слева от точки.</param>
/// <param name="Name">Имя атрибута.</param>
public sealed record PyAttrExpr(IPyNode Target, string Name) : PyExprRecordBase;

/// <summary>Индексирование: <c>DEFAULTS['delaySec']</c>.</summary>
/// <param name="Target">Что индексируем.</param>
/// <param name="Index">Индекс.</param>
public sealed record PyIndexExpr(IPyNode Target, IPyNode Index) : PyExprRecordBase;

/// <summary>Вызов: <c>templates.createSlider(u'...', 'x', 1, 0, 10, 1, format=u'{{value}}')</c>.</summary>
/// <param name="Func">Что вызываем.</param>
/// <param name="Args">Позиционные аргументы.</param>
/// <param name="Kwargs">Именованные аргументы.</param>
public sealed record PyCallExpr(
    IPyNode Func,
    IReadOnlyList<IPyNode> Args,
    IReadOnlyList<KeyValuePair<string, IPyNode>> Kwargs) : PyExprRecordBase;

/// <summary>
/// Разбор ПОДМНОЖЕСТВА выражений Python — ровно того, которым записывают шаблон настроек.
///
/// <para><b>Почему подмножество, а не полноценный парсер.</b> Разобрать словарь из
/// ПРОИЗВОЛЬНОГО кода мода нельзя в принципе: шаблон могут собрать циклом, склеить из
/// нескольких словарей, достать из чужого модуля или посчитать в рантайме. Кокпит
/// поддерживает три записи, которые встречаются в жизни, — литеральный словарь (то, что
/// порождает сам кокпит), вызовы <c>templates.createX</c> и рукописный вариант рецепта R15 —
/// и на всём остальном ЧЕСТНО говорит «не удалось разобрать», вместо того чтобы угадать
/// и испортить чужую форму.</para>
///
/// <para>Поддержано: числа (с сохранением формы <c>3</c> против <c>3.0</c>), строки всех
/// префиксов и тройные, неявная склейка соседних строк, списки, кортежи, словари,
/// <c>True</c>/<c>False</c>/<c>None</c>, унарный минус, имена, атрибуты, индексирование
/// и вызовы с именованными аргументами. Комментарии пропускаются.</para>
/// </summary>
public static class PythonValueParser
{
    /// <summary>Разобрать одно выражение, начиная с указанного смещения.</summary>
    /// <param name="source">Текст модуля.</param>
    /// <param name="offset">С какого символа начинать.</param>
    /// <param name="node">Разобранное выражение.</param>
    /// <param name="error">Причина отказа.</param>
    /// <returns><c>true</c>, если выражение разобрано.</returns>
    public static bool TryParse(string source, int offset, out IPyNode? node, out string? error)
    {
        ArgumentNullException.ThrowIfNull(source);
        node = null;
        error = null;
        try
        {
            var parser = new Parser(source, offset);
            node = parser.ParseExpression();
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Форма числа, которую примет и JSON: без ведущих нулей и без голой точки.</summary>
    private static readonly System.Text.RegularExpressions.Regex JsonSafeNumber = new(
        @"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private sealed class Parser
    {
        private readonly string _text;
        private int _pos;

        public Parser(string text, int start)
        {
            _text = text;
            _pos = start;
        }

        public IPyNode ParseExpression()
        {
            SkipTrivia();
            int start = _pos;
            if (Peek() == '-')
            {
                _pos++;
                IPyNode inner = ParseExpression();
                if (inner is PyLiteralExpr { Value.IsNumeric: true } literal)
                {
                    SettingValue negated = literal.Value.Kind == SettingValueKind.Integer
                        ? SettingValue.Of(-literal.Value.AsInteger, "-" + literal.Value.NumberText())
                        : SettingValue.Of(-literal.Value.AsNumber, "-" + literal.Value.NumberText());
                    return Located(new PyLiteralExpr(negated), start);
                }
                throw Fail("унарный минус поддержан только перед числом");
            }
            IPyNode node = ParsePrimary();
            return ParsePostfix(node, start);
        }

        private IPyNode ParsePostfix(IPyNode node, int start)
        {
            while (true)
            {
                SkipTrivia();
                char c = Peek();
                if (c == '.')
                {
                    _pos++;
                    SkipTrivia();
                    string name = ReadName() ?? throw Fail("после точки ожидалось имя атрибута");
                    node = Located(new PyAttrExpr(node, name), start);
                    continue;
                }
                if (c == '[')
                {
                    _pos++;
                    IPyNode index = ParseExpression();
                    Expect(']');
                    node = Located(new PyIndexExpr(node, index), start);
                    continue;
                }
                if (c == '(')
                {
                    _pos++;
                    var args = new List<IPyNode>();
                    var kwargs = new List<KeyValuePair<string, IPyNode>>();
                    ReadArguments(args, kwargs);
                    node = Located(new PyCallExpr(node, args, kwargs), start);
                    continue;
                }
                return node;
            }
        }

        private void ReadArguments(List<IPyNode> args, List<KeyValuePair<string, IPyNode>> kwargs)
        {
            SkipTrivia();
            if (Peek() == ')') { _pos++; return; }
            while (true)
            {
                SkipTrivia();
                int save = _pos;
                string? name = ReadName();
                SkipTrivia();
                if (name is not null && Peek() == '=' && PeekAt(1) != '=')
                {
                    _pos++;
                    kwargs.Add(new KeyValuePair<string, IPyNode>(name, ParseExpression()));
                }
                else
                {
                    _pos = save;
                    args.Add(ParseExpression());
                }
                SkipTrivia();
                char c = Peek();
                if (c == ',') { _pos++; SkipTrivia(); if (Peek() == ')') { _pos++; return; } continue; }
                if (c == ')') { _pos++; return; }
                throw Fail($"в списке аргументов ожидались ',' или ')', встречено '{Describe(c)}'");
            }
        }

        private IPyNode ParsePrimary()
        {
            SkipTrivia();
            int start = _pos;
            char c = Peek();

            if (c == '\0') throw Fail("выражение оборвалось");

            if (c == '[' || c == '(')
            {
                char close = c == '[' ? ']' : ')';
                _pos++;
                var items = new List<IPyNode>();
                bool sawComma = false;
                SkipTrivia();
                if (Peek() == close) { _pos++; return Located(new PyListExpr(items, close == ')'), start); }
                while (true)
                {
                    items.Add(ParseExpression());
                    SkipTrivia();
                    char n = Peek();
                    if (n == ',')
                    {
                        sawComma = true;
                        _pos++;
                        SkipTrivia();
                        if (Peek() == close) { _pos++; break; }
                        continue;
                    }
                    if (n == close) { _pos++; break; }
                    throw Fail($"в списке ожидались ',' или '{close}', встречено '{Describe(n)}'");
                }
                // (x) — это просто скобки, а (x,) — кортеж из одного элемента.
                if (close == ')' && items.Count == 1 && !sawComma) return items[0];
                return Located(new PyListExpr(items, close == ')'), start);
            }

            if (c == '{')
            {
                _pos++;
                var entries = new List<KeyValuePair<IPyNode, IPyNode>>();
                SkipTrivia();
                if (Peek() == '}') { _pos++; return Located(new PyDictExpr(entries), start); }
                while (true)
                {
                    IPyNode key = ParseExpression();
                    SkipTrivia();
                    Expect(':');
                    IPyNode value = ParseExpression();
                    entries.Add(new KeyValuePair<IPyNode, IPyNode>(key, value));
                    SkipTrivia();
                    char n = Peek();
                    if (n == ',') { _pos++; SkipTrivia(); if (Peek() == '}') { _pos++; break; } continue; }
                    if (n == '}') { _pos++; break; }
                    throw Fail($"в словаре ожидались ',' или '}}', встречено '{Describe(n)}'");
                }
                return Located(new PyDictExpr(entries), start);
            }

            if (IsStringStart(out _)) return Located(new PyLiteralExpr(ReadStringChain()), start);

            if (char.IsDigit(c) || (c == '.' && char.IsDigit(PeekAt(1))))
                return Located(new PyLiteralExpr(ReadNumber()), start);

            string? name = ReadName();
            if (name is null) throw Fail($"неожиданный символ '{Describe(c)}'");
            return name switch
            {
                "True" => Located(new PyLiteralExpr(SettingValue.True), start),
                "False" => Located(new PyLiteralExpr(SettingValue.False), start),
                "None" => Located(new PyLiteralExpr(SettingValue.Null), start),
                _ => Located(new PyNameExpr(name), start)
            };
        }

        // -------------------------------------------------------------- лексика

        private char Peek() => _pos < _text.Length ? _text[_pos] : '\0';

        private char PeekAt(int delta) => _pos + delta < _text.Length ? _text[_pos + delta] : '\0';

        private void Expect(char c)
        {
            SkipTrivia();
            if (Peek() != c) throw Fail($"ожидался '{c}', встречено '{Describe(Peek())}'");
            _pos++;
        }

        /// <summary>Пробелы, переносы строк, комментарии и продолжения строки обратным слэшем.</summary>
        private void SkipTrivia()
        {
            while (_pos < _text.Length)
            {
                char c = _text[_pos];
                if (c == '#')
                {
                    while (_pos < _text.Length && _text[_pos] != '\n') _pos++;
                    continue;
                }
                if (c == '\\' && _pos + 1 < _text.Length && (_text[_pos + 1] == '\n' || _text[_pos + 1] == '\r'))
                {
                    _pos++;
                    continue;
                }
                if (char.IsWhiteSpace(c)) { _pos++; continue; }
                return;
            }
        }

        private string? ReadName()
        {
            if (_pos >= _text.Length) return null;
            char c = _text[_pos];
            if (c != '_' && !char.IsLetter(c)) return null;
            int start = _pos;
            while (_pos < _text.Length && (_text[_pos] == '_' || char.IsLetterOrDigit(_text[_pos]))) _pos++;
            return _text[start.._pos];
        }

        private SettingValue ReadNumber()
        {
            int start = _pos;
            if (Peek() == '0' && (PeekAt(1) == 'x' || PeekAt(1) == 'X'))
            {
                _pos += 2;
                while (Uri.IsHexDigit(Peek())) _pos++;
                string hex = _text[(start + 2).._pos];
                // Исходный текст НЕ сохраняем: '0x10' — валидный литерал Python, но не JSON.
                return SettingValue.Of(long.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }

            bool fractional = false;
            while (_pos < _text.Length)
            {
                char c = _text[_pos];
                if (char.IsDigit(c)) { _pos++; continue; }
                if (c == '.' && !fractional) { fractional = true; _pos++; continue; }
                if ((c == 'e' || c == 'E') && _pos + 1 < _text.Length &&
                    (char.IsDigit(_text[_pos + 1]) || _text[_pos + 1] == '+' || _text[_pos + 1] == '-'))
                {
                    fractional = true;
                    _pos += 2;
                    continue;
                }
                if (c == 'L' || c == 'l') { _pos++; break; }   // питоновский long-суффикс
                break;
            }

            string raw = _text[start.._pos];
            string clean = raw.TrimEnd('L', 'l');
            // Исходную запись сохраняем, только если она годится и для JSON: питоновские
            // '01' (восьмеричное) и '.5' валидны в коде, но не в шаблоне.
            string? keep = JsonSafeNumber.IsMatch(clean) ? clean : null;
            if (!fractional && long.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                return SettingValue.Of(l, keep);
            if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return SettingValue.Of(d, keep);
            throw Fail($"не число: '{raw}'");
        }

        private bool IsStringStart(out bool raw)
        {
            raw = false;
            int p = _pos;
            int prefix = 0;
            while (p < _text.Length && prefix < 2)
            {
                char c = char.ToLowerInvariant(_text[p]);
                if (c is 'u' or 'b' or 'r')
                {
                    if (c == 'r') raw = true;
                    p++;
                    prefix++;
                    continue;
                }
                break;
            }
            return p < _text.Length && (_text[p] == '\'' || _text[p] == '"');
        }

        /// <summary>Соседние строковые литералы склеиваются, как это делает сам Python.</summary>
        private SettingValue ReadStringChain()
        {
            var builder = new StringBuilder();
            while (true)
            {
                int save = _pos;
                if (!IsStringStart(out _)) { _pos = save; break; }
                builder.Append(ReadOneString());
                SkipTrivia();
                if (!IsStringStart(out _)) break;
            }
            return SettingValue.Of(builder.ToString());
        }

        private string ReadOneString()
        {
            bool raw = false;
            while (_pos < _text.Length)
            {
                char c = char.ToLowerInvariant(_text[_pos]);
                if (c is 'u' or 'b' or 'r')
                {
                    if (c == 'r') raw = true;
                    _pos++;
                    continue;
                }
                break;
            }

            char quote = Peek();
            if (quote != '\'' && quote != '"') throw Fail("ожидалась строка");

            bool triple = PeekAt(1) == quote && PeekAt(2) == quote;
            _pos += triple ? 3 : 1;

            var builder = new StringBuilder();
            while (true)
            {
                if (_pos >= _text.Length) throw Fail("строка не закрыта");
                char c = _text[_pos];

                if (c == quote)
                {
                    if (!triple) { _pos++; break; }
                    if (PeekAt(1) == quote && PeekAt(2) == quote) { _pos += 3; break; }
                    builder.Append(c);
                    _pos++;
                    continue;
                }

                if (c == '\\')
                {
                    if (raw)
                    {
                        builder.Append(c);
                        _pos++;
                        if (_pos < _text.Length) { builder.Append(_text[_pos]); _pos++; }
                        continue;
                    }
                    _pos++;
                    if (_pos >= _text.Length) throw Fail("строка обрывается на обратном слэше");
                    char e = _text[_pos++];
                    switch (e)
                    {
                        case 'n': builder.Append('\n'); break;
                        case 't': builder.Append('\t'); break;
                        case 'r': builder.Append('\r'); break;
                        case '0': builder.Append('\0'); break;
                        case 'a': builder.Append('\a'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'v': builder.Append('\v'); break;
                        case '\\': builder.Append('\\'); break;
                        case '\'': builder.Append('\''); break;
                        case '"': builder.Append('"'); break;
                        case '\n': break;                              // продолжение строки
                        case '\r': if (Peek() == '\n') _pos++; break;
                        case 'x': builder.Append(ReadHex(2)); break;
                        case 'u': builder.Append(ReadHex(4)); break;
                        case 'U': builder.Append(char.ConvertFromUtf32(ReadHexCode(8))); break;
                        default:
                            // Python 2 неизвестный escape оставляет как есть, вместе со слэшем.
                            builder.Append('\\').Append(e);
                            break;
                    }
                    continue;
                }

                builder.Append(c);
                _pos++;
            }
            return builder.ToString();
        }

        private char ReadHex(int digits) => (char)ReadHexCode(digits);

        private int ReadHexCode(int digits)
        {
            int start = _pos;
            for (int i = 0; i < digits && _pos < _text.Length && Uri.IsHexDigit(_text[_pos]); i++) _pos++;
            if (_pos == start) throw Fail("после \\x/\\u ожидались шестнадцатеричные цифры");
            return int.Parse(_text[start.._pos], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static string Describe(char c) => c == '\0' ? "конец файла" : c.ToString();

        private T Located<T>(T node, int start) where T : IPyNode
        {
            node.Start = start;
            node.End = _pos;
            return node;
        }

        private FormatException Fail(string message)
        {
            int line = 1;
            for (int i = 0; i < _pos && i < _text.Length; i++) if (_text[i] == '\n') line++;
            return new FormatException($"строка {line}: {message}");
        }
    }
}
