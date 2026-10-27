using System.Globalization;
using System.Text;

namespace WotModCockpit.App.Stage4;

/// <summary>
/// Горячая клавиша ModsSettingsAPI: список кодов клавиш BigWorld, которые должны быть
/// нажаты ОДНОВРЕМЕННО.
/// <para>
/// Значением контрола <c>HotKey</c> служит не строка и не одна клавиша, а
/// <c>[код, код, …]</c>. Проверка в моде — <c>g_modsSettingsApi.checkKeyset(keys)</c>,
/// то есть <c>all(BigWorld.isKeyDown)</c> по разложенному множеству; пустой список
/// означает «клавиша не назначена» и всегда даёт False.
/// </para>
/// <para>
/// Три кода отрицательные и означают «любой из пары левый/правый»:
/// -1 ALT, -2 CTRL, -3 SHIFT (<c>_constants.SPECIAL_KEYS = range(-1, -4, -1)</c>).
/// В живых данных ещё встречается легаси-форма — вложенные списки обоих физических кодов
/// (<c>[[56, 184]]</c> = ALT, <c>[61, [29, 157]]</c> = Ctrl+F3): API плющит их сам
/// в <c>hotkeys._migrateKeys</c>. Кокпит такие значения ПОНИМАЕТ при импорте,
/// но ГЕНЕРИРУЕТ только плоскую современную форму.
/// </para>
/// </summary>
public static class SettingsHotKey
{
    /// <summary>Спецкод «любой Alt».</summary>
    public const int Alt = -1;

    /// <summary>Спецкод «любой Ctrl».</summary>
    public const int Ctrl = -2;

    /// <summary>Спецкод «любой Shift».</summary>
    public const int Shift = -3;

    /// <summary>
    /// Коды, которые API назначить не даст (<c>_constants.EXCLUDED_KEYS</c>):
    /// 0 KEY_NONE, 28 KEY_RETURN, 256 ЛКМ, 257 ПКМ, 258 СКМ.
    /// </summary>
    public static IReadOnlyCollection<int> ExcludedKeys { get; } = new HashSet<int> { 0, 28, 256, 257, 258 };

    /// <summary>
    /// Клавиши, которые предлагает встроенный редактор. Таблица выведена из
    /// <c>scripts/client/Keys.py</c> распакованного клиента (240 констант <c>KEY_*</c>);
    /// выброшены запрещённые коды, джойстик, LCD-клавиатура и мультимедийные клавиши.
    /// Если у реализации генератора есть своя таблица из индекса API
    /// (<see cref="ISettingsDesignerBackend.KnownKeys"/>), экран берёт её, а не эту.
    /// </summary>
    public static IReadOnlyList<SettingsKeyCode> KnownKeys { get; } = new SettingsKeyCode[]
    {
        new(1, "KEY_ESCAPE", "Esc"),
        new(2, "KEY_1", "1"),
        new(3, "KEY_2", "2"),
        new(4, "KEY_3", "3"),
        new(5, "KEY_4", "4"),
        new(6, "KEY_5", "5"),
        new(7, "KEY_6", "6"),
        new(8, "KEY_7", "7"),
        new(9, "KEY_8", "8"),
        new(10, "KEY_9", "9"),
        new(11, "KEY_0", "0"),
        new(12, "KEY_MINUS", "- минус"),
        new(13, "KEY_EQUALS", "= равно"),
        new(14, "KEY_BACKSPACE", "Backspace"),
        new(15, "KEY_TAB", "Tab"),
        new(16, "KEY_Q", "Q"),
        new(17, "KEY_W", "W"),
        new(18, "KEY_E", "E"),
        new(19, "KEY_R", "R"),
        new(20, "KEY_T", "T"),
        new(21, "KEY_Y", "Y"),
        new(22, "KEY_U", "U"),
        new(23, "KEY_I", "I"),
        new(24, "KEY_O", "O"),
        new(25, "KEY_P", "P"),
        new(26, "KEY_LBRACKET", "["),
        new(27, "KEY_RBRACKET", "]"),
        new(29, "KEY_LCONTROL", "левый Ctrl"),
        new(30, "KEY_A", "A"),
        new(31, "KEY_S", "S"),
        new(32, "KEY_D", "D"),
        new(33, "KEY_F", "F"),
        new(34, "KEY_G", "G"),
        new(35, "KEY_H", "H"),
        new(36, "KEY_J", "J"),
        new(37, "KEY_K", "K"),
        new(38, "KEY_L", "L"),
        new(39, "KEY_SEMICOLON", "; точка с запятой"),
        new(40, "KEY_APOSTROPHE", "' апостроф"),
        new(41, "KEY_GRAVE", "` тильда"),
        new(42, "KEY_LSHIFT", "левый Shift"),
        new(43, "KEY_BACKSLASH", @"\ обратный слэш"),
        new(44, "KEY_Z", "Z"),
        new(45, "KEY_X", "X"),
        new(46, "KEY_C", "C"),
        new(47, "KEY_V", "V"),
        new(48, "KEY_B", "B"),
        new(49, "KEY_N", "N"),
        new(50, "KEY_M", "M"),
        new(51, "KEY_COMMA", ", запятая"),
        new(52, "KEY_PERIOD", ". точка"),
        new(53, "KEY_SLASH", "/ слэш"),
        new(54, "KEY_RSHIFT", "правый Shift"),
        new(55, "KEY_NUMPADSTAR", "Num *"),
        new(56, "KEY_LALT", "левый Alt"),
        new(57, "KEY_SPACE", "Пробел"),
        new(58, "KEY_CAPSLOCK", "Caps Lock"),
        new(59, "KEY_F1", "F1"),
        new(60, "KEY_F2", "F2"),
        new(61, "KEY_F3", "F3"),
        new(62, "KEY_F4", "F4"),
        new(63, "KEY_F5", "F5"),
        new(64, "KEY_F6", "F6"),
        new(65, "KEY_F7", "F7"),
        new(66, "KEY_F8", "F8"),
        new(67, "KEY_F9", "F9"),
        new(68, "KEY_F10", "F10"),
        new(69, "KEY_NUMLOCK", "Num Lock"),
        new(70, "KEY_SCROLL", "Scroll Lock"),
        new(71, "KEY_NUMPAD7", "Num 7"),
        new(72, "KEY_NUMPAD8", "Num 8"),
        new(73, "KEY_NUMPAD9", "Num 9"),
        new(74, "KEY_NUMPADMINUS", "Num -"),
        new(75, "KEY_NUMPAD4", "Num 4"),
        new(76, "KEY_NUMPAD5", "Num 5"),
        new(77, "KEY_NUMPAD6", "Num 6"),
        new(78, "KEY_ADD", "Num +"),
        new(79, "KEY_NUMPAD1", "Num 1"),
        new(80, "KEY_NUMPAD2", "Num 2"),
        new(81, "KEY_NUMPAD3", "Num 3"),
        new(82, "KEY_NUMPAD0", "Num 0"),
        new(83, "KEY_NUMPADPERIOD", "Num ."),
        new(86, "KEY_OEM_102", "OEM 102"),
        new(87, "KEY_F11", "F11"),
        new(88, "KEY_F12", "F12"),
        new(100, "KEY_F13", "F13"),
        new(101, "KEY_F14", "F14"),
        new(102, "KEY_F15", "F15"),
        new(141, "KEY_NUMPADEQUALS", "Num ="),
        new(156, "KEY_NUMPADENTER", "Num Enter"),
        new(157, "KEY_RCONTROL", "правый Ctrl"),
        new(179, "KEY_NUMPADCOMMA", "Num ,"),
        new(181, "KEY_NUMPADSLASH", "Num /"),
        new(183, "KEY_SYSRQ", "Print Screen"),
        new(184, "KEY_RALT", "правый Alt"),
        new(197, "KEY_PAUSE", "Pause"),
        new(199, "KEY_HOME", "Home"),
        new(200, "KEY_UPARROW", "стрелка вверх"),
        new(201, "KEY_PGUP", "Page Up"),
        new(203, "KEY_LEFTARROW", "стрелка влево"),
        new(205, "KEY_RIGHTARROW", "стрелка вправо"),
        new(207, "KEY_END", "End"),
        new(208, "KEY_DOWNARROW", "стрелка вниз"),
        new(209, "KEY_PGDN", "Page Down"),
        new(210, "KEY_INSERT", "Insert"),
        new(211, "KEY_DELETE", "Delete"),
        new(219, "KEY_LWIN", "левый Win"),
        new(220, "KEY_RWIN", "правый Win"),
        new(221, "KEY_APPS", "Menu"),
        new(259, "KEY_MOUSE3", "мышь 4"),
        new(260, "KEY_MOUSE4", "мышь 5"),
        new(261, "KEY_MOUSE5", "мышь 6"),
        new(262, "KEY_MOUSE6", "мышь 7"),
        new(263, "KEY_MOUSE7", "мышь 8")
    };

    private static readonly Dictionary<int, SettingsKeyCode> ByCode =
        KnownKeys.ToDictionary(k => k.Code);

    /// <summary>Пары «левый/правый», которые API сводит к одному спецкоду.</summary>
    private static readonly Dictionary<int, int> Migrations = new()
    {
        [56] = Alt,     // KEY_LALT
        [184] = Alt,    // KEY_RALT
        [29] = Ctrl,    // KEY_LCONTROL
        [157] = Ctrl,   // KEY_RCONTROL
        [42] = Shift,   // KEY_LSHIFT
        [54] = Shift    // KEY_RSHIFT
    };

    /// <summary>Имя константы клиента для кода.</summary>
    /// <param name="code">Код клавиши.</param>
    /// <returns><c>KEY_F3</c>, <c>CTRL</c> либо сам код строкой.</returns>
    public static string NameOf(int code) => code switch
    {
        Alt => "ALT",
        Ctrl => "CTRL",
        Shift => "SHIFT",
        _ => ByCode.TryGetValue(code, out SettingsKeyCode? key)
            ? key.Name
            : code.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>Как показать код человеку.</summary>
    /// <param name="code">Код клавиши.</param>
    /// <returns><c>F3</c>, <c>Ctrl</c> либо <c>код 999</c>.</returns>
    public static string LabelOf(int code) => code switch
    {
        Alt => "Alt",
        Ctrl => "Ctrl",
        Shift => "Shift",
        _ => ByCode.TryGetValue(code, out SettingsKeyCode? key)
            ? key.Label
            : "код " + code.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>Код является модификатором (спецкодом).</summary>
    /// <param name="code">Код клавиши.</param>
    /// <returns>true для -1, -2, -3.</returns>
    public static bool IsModifier(int code) => code is Alt or Ctrl or Shift;

    /// <summary>
    /// Приводит любое значение <c>value</c> контрола HotKey к плоской современной форме:
    /// разворачивает вложенные списки и заменяет физические L/R-коды спецкодами —
    /// ровно так же, как это делает <c>hotkeys._migrateKeys</c> перед проверкой.
    /// </summary>
    /// <param name="raw">Значение из шаблона: числа и/или вложенные списки чисел.</param>
    /// <returns>Плоский список без повторов: сперва модификаторы, потом клавиши.</returns>
    public static IReadOnlyList<int> Flatten(IEnumerable<object?>? raw)
    {
        var flat = new List<int>();
        Collect(raw, flat, 0);

        var result = new List<int>();
        foreach (int code in flat)
        {
            int migrated = Migrations.TryGetValue(code, out int special) ? special : code;
            if (!result.Contains(migrated))
            {
                result.Add(migrated);
            }
        }

        return Order(result);
    }

    private static void Collect(IEnumerable<object?>? raw, List<int> sink, int depth)
    {
        if (raw is null || depth > 8)
        {
            return;
        }

        foreach (object? item in raw)
        {
            switch (item)
            {
                case null:
                    break;
                case int i:
                    sink.Add(i);
                    break;
                case long l:
                    sink.Add((int)l);
                    break;
                case double d:
                    sink.Add((int)d);
                    break;
                case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed):
                    sink.Add(parsed);
                    break;
                case IEnumerable<object?> nested:
                    Collect(nested, sink, depth + 1);
                    break;
            }
        }
    }

    /// <summary>Модификаторы вперёд — так же читается и итоговая подпись.</summary>
    /// <param name="codes">Коды.</param>
    /// <returns>Упорядоченный список.</returns>
    public static IReadOnlyList<int> Order(IEnumerable<int> codes)
    {
        var list = codes.Distinct().ToList();
        list.Sort(static (a, b) =>
        {
            bool ma = IsModifier(a);
            bool mb = IsModifier(b);
            if (ma != mb)
            {
                return ma ? -1 : 1;
            }

            // Между собой модификаторы идут Ctrl, Alt, Shift — как их принято писать.
            return ma ? Rank(a).CompareTo(Rank(b)) : a.CompareTo(b);
        });

        return list;

        static int Rank(int code) => code switch { Ctrl => 0, Alt => 1, _ => 2 };
    }

    /// <summary>Подпись сочетания для человека: <c>Ctrl + F3</c>.</summary>
    /// <param name="codes">Коды.</param>
    /// <returns>Строка; для пустого списка — «не назначена».</returns>
    public static string Describe(IReadOnlyList<int>? codes)
    {
        if (codes is null || codes.Count == 0)
        {
            return "не назначена";
        }

        var sb = new StringBuilder();
        foreach (int code in Order(codes))
        {
            if (sb.Length > 0)
            {
                sb.Append(" + ");
            }

            sb.Append(LabelOf(code));
        }

        return sb.ToString();
    }

    /// <summary>Значение так, как оно выглядит в шаблоне: <c>[61, -2]</c>.</summary>
    /// <param name="codes">Коды.</param>
    /// <returns>Литерал списка.</returns>
    public static string RenderList(IReadOnlyList<int>? codes)
    {
        if (codes is null || codes.Count == 0)
        {
            return "[]";
        }

        return "[" + string.Join(", ", codes.Select(c => c.ToString(CultureInfo.InvariantCulture))) + "]";
    }

    /// <summary>
    /// Разбирает строку кодов, введённую руками: <c>61, -2</c> либо <c>[61, -2]</c>.
    /// </summary>
    /// <param name="text">Текст.</param>
    /// <param name="codes">Разобранные коды.</param>
    /// <returns>true, если разобрано целиком.</returns>
    public static bool TryParseCodes(string? text, out IReadOnlyList<int> codes)
    {
        var result = new List<int>();
        codes = result;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (string part in text.Trim().Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
            {
                return false;
            }

            if (!result.Contains(code))
            {
                result.Add(code);
            }
        }

        codes = Order(result);
        return true;
    }
}
