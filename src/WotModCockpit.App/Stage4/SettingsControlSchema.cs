namespace WotModCockpit.App.Stage4;

/// <summary>
/// Что каждый тип контрола ModsSettingsAPI 1.6.4 на самом деле умеет.
/// <para>
/// Таблица восстановлена из двух независимых источников и НЕ повторяет докстринги
/// <c>templates.py</c>: python-фабрики принимают <c>button=</c> у восьми типов и
/// <c>width=</c> у нескольких, но окно (<c>modsSettingsWindow.swf</c>) читает эти поля
/// далеко не везде. Дизайнер обязан быть строже API: поле, которое окно не читает,
/// порождать нельзя — иначе мододел будет крутить настройку, которая ничего не делает.
/// </para>
/// </summary>
public static class SettingsControlSchema
{
    /// <summary>Все типы в порядке палитры: сперва те, что встречаются в реальных шаблонах выборки.</summary>
    public static IReadOnlyList<SettingsControlType> PaletteOrder { get; } = new[]
    {
        SettingsControlType.CheckBox,
        SettingsControlType.RadioButtonGroup,
        SettingsControlType.Slider,
        SettingsControlType.Dropdown,
        SettingsControlType.HotKey,
        SettingsControlType.Label,
        SettingsControlType.Empty,
        SettingsControlType.TextInput,
        SettingsControlType.NumericStepper,
        SettingsControlType.ColorChoice,
        SettingsControlType.RangeSlider
    };

    /// <summary>Человеческое имя типа.</summary>
    /// <param name="type">Тип.</param>
    /// <returns>Название для палитры.</returns>
    public static string DisplayName(SettingsControlType type) => type switch
    {
        SettingsControlType.Empty => "Пустая строка",
        SettingsControlType.Label => "Заголовок",
        SettingsControlType.CheckBox => "Галочка",
        SettingsControlType.RadioButtonGroup => "Переключатели",
        SettingsControlType.Dropdown => "Выпадающий список",
        SettingsControlType.Slider => "Ползунок",
        SettingsControlType.TextInput => "Строка ввода",
        SettingsControlType.NumericStepper => "Число со стрелками",
        SettingsControlType.HotKey => "Горячая клавиша",
        SettingsControlType.ColorChoice => "Выбор цвета",
        SettingsControlType.RangeSlider => "Диапазон",
        _ => type.ToString()
    };

    /// <summary>Одна строка про то, зачем этот тип нужен.</summary>
    /// <param name="type">Тип.</param>
    /// <returns>Короткое пояснение.</returns>
    public static string Summary(SettingsControlType type) => type switch
    {
        SettingsControlType.Empty => "вертикальный отступ 20 px, в настройках не появляется",
        SettingsControlType.Label => "подпись без значения; единственный способ разбить колонку на секции",
        SettingsControlType.CheckBox => "включено / выключено, значение bool",
        SettingsControlType.RadioButtonGroup => "один вариант из нескольких, значение — индекс варианта",
        SettingsControlType.Dropdown => "один вариант из списка, значение — индекс; после 9 пунктов появляется прокрутка",
        SettingsControlType.Slider => "число мышью в диапазоне, справа метка по формату",
        SettingsControlType.TextInput => "произвольная строка; ввод ничем не ограничен",
        SettingsControlType.NumericStepper => "число стрелками, при желании с ручным вводом",
        SettingsControlType.HotKey => "сочетание клавиш; значение — список кодов, проверяется через checkKeyset",
        SettingsControlType.ColorChoice => "цвет пипеткой или по hex; возвращается строкой БЕЗ решётки и без нормализации",
        SettingsControlType.RangeSlider => "два конца диапазона, значение — пара чисел",
        _ => string.Empty
    };

    /// <summary>
    /// Сколько компонентов этого типа встретилось в выборке — 13 реальных шаблонах популярных
    /// модов, на которых проверялся кокпит (клиент 1.44, 56 компонентов).
    /// <para>
    /// Числа статические и намеренно не пересчитываются по <c>modsettings.dat</c> пользователя:
    /// «тип проверен на реальных шаблонах» — свойство ModsSettingsAPI и самого кокпита, а не
    /// машины, на которой он запущен. У человека без модов такой подсчёт объявил бы
    /// непроверенными все типы. Ноль — не «сломано», а «в выборке не встретился, поведение
    /// восстановлено по коду API»; в палитре это помечается.
    /// </para>
    /// </summary>
    /// <param name="type">Тип.</param>
    /// <returns>Число образцов в выборке.</returns>
    public static int LiveSamples(SettingsControlType type) => type switch
    {
        SettingsControlType.CheckBox => 25,
        SettingsControlType.RadioButtonGroup => 10,
        SettingsControlType.Slider => 9,
        SettingsControlType.Dropdown => 7,
        SettingsControlType.HotKey => 5,
        _ => 0
    };

    /// <summary>У типа есть подпись <c>text</c>.</summary>
    /// <param name="type">Тип.</param>
    /// <returns>true, если поле поддержано.</returns>
    public static bool HasText(SettingsControlType type) => type != SettingsControlType.Empty;

    /// <summary>У типа есть подсказка <c>tooltip</c>.</summary>
    /// <param name="type">Тип.</param>
    /// <returns>true, если поле поддержано.</returns>
    public static bool HasTooltip(SettingsControlType type) => type != SettingsControlType.Empty;

    /// <summary>
    /// У типа есть <c>varName</c> и <c>value</c>. У <c>Label</c> и <c>Empty</c> их быть НЕ ДОЛЖНО:
    /// окно читает <c>returnValue</c> у компонента с varName, а у подписи его нет — «Применить» упадёт.
    /// </summary>
    /// <param name="type">Тип.</param>
    /// <returns>true, если это контрол со значением.</returns>
    public static bool HasValue(SettingsControlType type) =>
        type is not (SettingsControlType.Empty or SettingsControlType.Label);

    /// <summary>У типа есть список пунктов.</summary>
    /// <param name="type">Тип.</param>
    /// <returns>true для переключателей и выпадающего списка.</returns>
    public static bool HasOptions(SettingsControlType type) =>
        type is SettingsControlType.RadioButtonGroup or SettingsControlType.Dropdown;

    /// <summary>
    /// Окно читает <c>width</c> только у этих трёх типов; умолчание у всех — 200.
    /// У остальных ширина в шаблоне не делает НИЧЕГО.
    /// </summary>
    /// <param name="type">Тип.</param>
    /// <returns>true, если ширина поддержана.</returns>
    public static bool HasWidth(SettingsControlType type) =>
        type is SettingsControlType.Dropdown or SettingsControlType.Slider or SettingsControlType.TextInput;

    /// <summary>У типа есть числовой диапазон.</summary>
    /// <param name="type">Тип.</param>
    /// <returns>true для Slider, NumericStepper и RangeSlider.</returns>
    public static bool HasRange(SettingsControlType type) =>
        type is SettingsControlType.Slider or SettingsControlType.NumericStepper or SettingsControlType.RangeSlider;

    /// <summary>У типа есть формат метки значения.</summary>
    /// <param name="type">Тип.</param>
    /// <returns>true только для Slider.</returns>
    public static bool HasFormat(SettingsControlType type) => type is SettingsControlType.Slider;

    /// <summary>
    /// Кнопка рядом с контролом (<c>button</c>) поддержана окном ТОЛЬКО у этих четырёх типов,
    /// хотя python-фабрики принимают её у восьми. В палитре v1 кнопки нет вовсе — поле
    /// сохраняется только при импорте чужого шаблона.
    /// </summary>
    /// <param name="type">Тип.</param>
    /// <returns>true, если окно прочитает <c>button</c>.</returns>
    public static bool SupportsButton(SettingsControlType type) =>
        type is SettingsControlType.CheckBox or SettingsControlType.RadioButtonGroup
            or SettingsControlType.Dropdown or SettingsControlType.Slider;

    /// <summary>
    /// Высота компонента в пикселях окна — для схематичного превью. Числа выведены из
    /// констант <c>Constants</c> в байткоде окна (COMPONENT_HEADER_MARGIN 20,
    /// RADIO_BUTTONS_MARGIN 20, EMPTY_COMPONENT_HEIGHT 20) и точны настолько, насколько
    /// вообще может быть точен офлайн-расчёт: реальная высота зависит от шрифтов и скинов,
    /// которые окно импортирует из десяти библиотек клиента.
    /// </summary>
    /// <param name="type">Тип.</param>
    /// <param name="optionCount">Число пунктов (для переключателей).</param>
    /// <returns>Высота в пикселях окна.</returns>
    public static double PreviewHeight(SettingsControlType type, int optionCount) => type switch
    {
        SettingsControlType.Empty => 20,
        SettingsControlType.Label => 22,
        SettingsControlType.CheckBox => 22,
        SettingsControlType.RadioButtonGroup => 20 + Math.Max(1, optionCount) * 24,
        SettingsControlType.Dropdown => 20 + 26,
        SettingsControlType.Slider => 20 + 22,
        SettingsControlType.TextInput => 20 + 26,
        SettingsControlType.NumericStepper => 26,
        SettingsControlType.HotKey => 26,
        SettingsControlType.ColorChoice => 26,
        SettingsControlType.RangeSlider => 20 + 34,
        _ => 24
    };

    /// <summary>Ширина блока мода в окне: <c>Constants.MOD_COMPONENT_WIDTH</c>.</summary>
    public const double ModWidth = 900;

    /// <summary>Отступ блока слева: <c>Constants.MOD_PADDING_LEFT</c>.</summary>
    public const double ModPaddingLeft = 20;

    /// <summary>Ширина одной колонки: вторая колонка ставится в x=450, значит на колонку ровно 430.</summary>
    public const double ColumnWidth = 430;

    /// <summary>Отступ между контролами: <c>Constants.COMPONENT_MARGIN_BOTTOM</c>.</summary>
    public const double ComponentMargin = 10;

    /// <summary>Ширина по умолчанию у Dropdown/Slider/TextInput, которую подставляет само окно.</summary>
    public const int DefaultWidth = 200;

    /// <summary>Столько пунктов Dropdown показывает без прокрутки: <c>ComponentsFactory.SCROLL_ITEM_LIMIT</c>.</summary>
    public const int DropdownScrollLimit = 9;

    /// <summary>Однострочные контролы окно ставит в x=315 — подпись слева, контрол справа.</summary>
    public const double InlineControlX = 315;
}
