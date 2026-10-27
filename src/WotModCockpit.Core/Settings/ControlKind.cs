namespace WotModCockpit.Core.Settings;

/// <summary>
/// Тип контрола в окне настроек ModsSettingsAPI 1.6.4.
///
/// <para>Значения перечисления соответствуют строкам <c>COMPONENT_TYPE</c> из
/// <c>gui/modsSettingsApi/_constants.py</c> ОДИН В ОДИН, включая регистр
/// (<c>CheckBox</c>, <c>HotKey</c>, <c>RadioButtonGroup</c>). Их ровно 11 —
/// столько же фабрик в <c>templates.py</c> и столько же веток в
/// <c>ModsSettingsComponent.getComponentByType</c> в байткоде окна.</para>
///
/// <para>Неизвестный тип окно не рисует: пишет в лог
/// <c>[ModsSettings API] Unexpected type of component: &lt;type&gt;</c> и подставляет
/// пустой <c>MovieClip</c>. Если у такого компонента есть <c>varName</c>, кнопка
/// «Применить» ломается целиком. Поэтому <see cref="Unknown"/> в модели есть, но
/// валидатор считает его ошибкой.</para>
/// </summary>
public enum ControlKind
{
    /// <summary>
    /// Тип, которого нет в API. В модели существует только чтобы импорт чужого шаблона
    /// НЕ ТЕРЯЛ данные молча; исходная строка типа лежит в <see cref="SettingControl.RawTypeName"/>.
    /// </summary>
    Unknown = 0,

    /// <summary>Распорка 400x20. Ни подписи, ни значения. <c>createEmpty()</c>.</summary>
    Empty,

    /// <summary>Подпись-заголовок. Единственный способ разбить колонку на секции. <c>createLabel</c>.</summary>
    Label,

    /// <summary>
    /// Галочка. Значение — <c>bool</c>. Самый частый контрол (25 из 56 компонентов в выборке
    /// реальных шаблонов). <c>createCheckbox</c>.
    /// </summary>
    CheckBox,

    /// <summary>Радиокнопки списком. Значение — ИНДЕКС в <c>options</c>. <c>createRadioButtonGroup</c>.</summary>
    RadioButtonGroup,

    /// <summary>Выпадающий список. Значение — ИНДЕКС в <c>options</c>. <c>createDropdown</c>.</summary>
    Dropdown,

    /// <summary>Ползунок. Значение — число из диапазона. <c>createSlider</c>.</summary>
    Slider,

    /// <summary>Строка ввода. Значение — строка. В выборке реальных шаблонов не встречается. <c>createInput</c>.</summary>
    TextInput,

    /// <summary>Числовой степпер. В выборке реальных шаблонов не встречается. <c>createNumericStepper</c>.</summary>
    NumericStepper,

    /// <summary>Горячая клавиша. Значение — СПИСОК кодов клавиш. <c>createHotkey</c>.</summary>
    HotKey,

    /// <summary>
    /// Выбор цвета. Значение — hex БЕЗ решётки. В выборке реальных шаблонов не встречается.
    /// <c>createColorChoice</c>.
    /// </summary>
    ColorChoice,

    /// <summary>
    /// Диапазонный ползунок. Значение — <c>[слева, справа]</c>. В выборке реальных шаблонов
    /// не встречается. <c>createRangeSlider</c>.
    /// </summary>
    RangeSlider
}

/// <summary>
/// Что каждый тип контрола умеет. Таблица не выдумана: она снята с двух сторон —
/// с фабрик <c>templates.py</c> (что API кладёт в словарь) и с байткода
/// <c>modsSettingsWindow.swf</c> (что окно РЕАЛЬНО читает). Там, где они расходятся,
/// таблица следует за окном: поле, которое Python принимает, а Flash не читает,
/// поддержкой не считается — иначе кокпит порождал бы мёртвый код.
/// </summary>
public static class ControlKinds
{
    /// <summary>Ширина <c>Dropdown</c>/<c>Slider</c>/<c>TextInput</c>, если её не задать (умолчание окна).</summary>
    public const int DefaultWidth = 200;

    /// <summary>Плейсхолдер в <c>format</c> у <c>Slider</c> (<c>Constants.SLIDER_VALUE_KEY</c>).</summary>
    public const string SliderValuePlaceholder = "{{value}}";

    /// <summary>После скольких пунктов у <c>Dropdown</c> появляется прокрутка (<c>SCROLL_ITEM_LIMIT</c>).</summary>
    public const int DropdownScrollLimit = 9;

    /// <summary>Все 11 типов API в порядке палитры. <see cref="ControlKind.Unknown"/> сюда не входит.</summary>
    public static IReadOnlyList<ControlKind> All { get; } = new[]
    {
        ControlKind.Label, ControlKind.Empty, ControlKind.CheckBox, ControlKind.RadioButtonGroup,
        ControlKind.Dropdown, ControlKind.Slider, ControlKind.TextInput, ControlKind.NumericStepper,
        ControlKind.HotKey, ControlKind.ColorChoice, ControlKind.RangeSlider
    };

    /// <summary>Строка поля <c>type</c> в шаблоне.</summary>
    /// <param name="kind">Тип.</param>
    /// <returns>Строка ровно как в <c>COMPONENT_TYPE</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Для <see cref="ControlKind.Unknown"/>: у него своя строка.</exception>
    public static string WireName(ControlKind kind) => kind switch
    {
        ControlKind.Empty => "Empty",
        ControlKind.Label => "Label",
        ControlKind.CheckBox => "CheckBox",
        ControlKind.RadioButtonGroup => "RadioButtonGroup",
        ControlKind.Dropdown => "Dropdown",
        ControlKind.Slider => "Slider",
        ControlKind.TextInput => "TextInput",
        ControlKind.NumericStepper => "NumericStepper",
        ControlKind.HotKey => "HotKey",
        ControlKind.ColorChoice => "ColorChoice",
        ControlKind.RangeSlider => "RangeSlider",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
            "у Unknown нет канонической строки: она хранится в SettingControl.RawTypeName")
    };

    /// <summary>
    /// Разобрать строку поля <c>type</c>. Регистр ВАЖЕН: окно сравнивает строки точно,
    /// и <c>checkbox</c> для него — неизвестный тип.
    /// </summary>
    /// <param name="value">Строка из шаблона.</param>
    /// <param name="kind">Найденный тип.</param>
    /// <returns><c>true</c>, если такой тип у API есть.</returns>
    public static bool TryParse(string? value, out ControlKind kind)
    {
        kind = ControlKind.Unknown;
        if (string.IsNullOrEmpty(value)) return false;
        foreach (ControlKind candidate in All)
        {
            if (string.Equals(WireName(candidate), value, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>Имя фабрики в <c>gui.modsSettingsApi.templates</c>.</summary>
    /// <param name="kind">Тип.</param>
    /// <returns>Имя функции или <c>null</c> для <see cref="ControlKind.Unknown"/>.</returns>
    public static string? FactoryName(ControlKind kind) => kind switch
    {
        ControlKind.Empty => "createEmpty",
        ControlKind.Label => "createLabel",
        ControlKind.CheckBox => "createCheckbox",
        ControlKind.RadioButtonGroup => "createRadioButtonGroup",
        ControlKind.Dropdown => "createDropdown",
        ControlKind.Slider => "createSlider",
        ControlKind.TextInput => "createInput",
        ControlKind.NumericStepper => "createNumericStepper",
        ControlKind.HotKey => "createHotkey",
        ControlKind.ColorChoice => "createColorChoice",
        ControlKind.RangeSlider => "createRangeSlider",
        _ => null
    };

    /// <summary>Тип по имени фабрики <c>templates.createX</c>.</summary>
    /// <param name="factory">Имя функции.</param>
    /// <param name="kind">Найденный тип.</param>
    /// <returns><c>true</c>, если такая фабрика есть.</returns>
    public static bool TryParseFactory(string? factory, out ControlKind kind)
    {
        kind = ControlKind.Unknown;
        if (string.IsNullOrEmpty(factory)) return false;
        foreach (ControlKind candidate in All)
        {
            if (string.Equals(FactoryName(candidate), factory, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>Русское имя для палитры и списка ошибок.</summary>
    /// <param name="kind">Тип.</param>
    /// <returns>Название.</returns>
    public static string DisplayName(ControlKind kind) => kind switch
    {
        ControlKind.Empty => "Отступ",
        ControlKind.Label => "Заголовок",
        ControlKind.CheckBox => "Галочка",
        ControlKind.RadioButtonGroup => "Радиокнопки",
        ControlKind.Dropdown => "Выпадающий список",
        ControlKind.Slider => "Ползунок",
        ControlKind.TextInput => "Строка ввода",
        ControlKind.NumericStepper => "Числовой счётчик",
        ControlKind.HotKey => "Горячая клавиша",
        ControlKind.ColorChoice => "Выбор цвета",
        ControlKind.RangeSlider => "Диапазон",
        _ => "Неизвестный тип"
    };

    /// <summary>
    /// У контрола есть <c>varName</c> и <c>value</c>, то есть он попадает в словарь настроек.
    /// У <see cref="ControlKind.Label"/> и <see cref="ControlKind.Empty"/> их быть НЕ ДОЛЖНО:
    /// <c>getConfigData</c> в окне читает <c>returnValue</c> у любого компонента с <c>varName</c>,
    /// а у подписи его нет — «Применить» упадёт.
    /// </summary>
    /// <param name="kind">Тип.</param>
    /// <returns><c>true</c>, если контрол хранит значение.</returns>
    public static bool HasValue(ControlKind kind) =>
        kind is not (ControlKind.Empty or ControlKind.Label or ControlKind.Unknown);

    /// <summary>Есть подпись <c>text</c>. Нет только у <see cref="ControlKind.Empty"/>.</summary>
    /// <param name="kind">Тип.</param>
    /// <returns><c>true</c>, если подпись поддержана.</returns>
    public static bool HasText(ControlKind kind) => kind is not (ControlKind.Empty or ControlKind.Unknown);

    /// <summary>Есть подсказка <c>tooltip</c> (иконка «i» рядом). Нет только у <see cref="ControlKind.Empty"/>.</summary>
    /// <param name="kind">Тип.</param>
    /// <returns><c>true</c>, если подсказка поддержана.</returns>
    public static bool HasTooltip(ControlKind kind) => kind is not (ControlKind.Empty or ControlKind.Unknown);

    /// <summary>Есть список <c>options</c>: только у радиокнопок и выпадающего списка.</summary>
    /// <param name="kind">Тип.</param>
    /// <returns><c>true</c>, если список вариантов поддержан.</returns>
    public static bool HasOptions(ControlKind kind) =>
        kind is ControlKind.RadioButtonGroup or ControlKind.Dropdown;

    /// <summary>
    /// Окно читает <c>width</c>: только <c>Dropdown</c>, <c>Slider</c>, <c>TextInput</c>
    /// (в байткоде трёх фабрик стоит <c>cfg.hasOwnProperty('width') ? cfg.width : 200</c>,
    /// у остальных строки <c>width</c> нет вообще).
    /// </summary>
    /// <param name="kind">Тип.</param>
    /// <returns><c>true</c>, если ширина поддержана.</returns>
    public static bool HasWidth(ControlKind kind) =>
        kind is ControlKind.Dropdown or ControlKind.Slider or ControlKind.TextInput;

    /// <summary>
    /// Окно читает <c>button</c>: только <c>CheckBox</c>, <c>RadioButtonGroup</c>,
    /// <c>Dropdown</c>, <c>Slider</c>. Python-фабрики принимают <c>button=</c> у восьми типов,
    /// но у остальных четырёх Flash его молча игнорирует.
    /// </summary>
    /// <param name="kind">Тип.</param>
    /// <returns><c>true</c>, если кнопка поддержана.</returns>
    public static bool HasButton(ControlKind kind) =>
        kind is ControlKind.CheckBox or ControlKind.RadioButtonGroup
             or ControlKind.Dropdown or ControlKind.Slider;

    /// <summary>Есть <c>minimum</c>/<c>maximum</c>/<c>snapInterval</c>.</summary>
    /// <param name="kind">Тип.</param>
    /// <returns><c>true</c>, если диапазон поддержан.</returns>
    public static bool HasRange(ControlKind kind) =>
        kind is ControlKind.Slider or ControlKind.NumericStepper or ControlKind.RangeSlider;

    /// <summary>Есть <c>format</c> — только у <see cref="ControlKind.Slider"/>.</summary>
    /// <param name="kind">Тип.</param>
    /// <returns><c>true</c>, если формат поддержан.</returns>
    public static bool HasFormat(ControlKind kind) => kind == ControlKind.Slider;

    /// <summary>
    /// Тип встречается в выборке — 13 реальных шаблонах популярных модов, на которых проверялся
    /// кокпит (клиент 1.44), — то есть проверен боем. Для остальных шести (<c>Label</c>,
    /// <c>Empty</c>, <c>TextInput</c>, <c>NumericStepper</c>, <c>ColorChoice</c>,
    /// <c>RangeSlider</c>) поведение восстановлено только по коду — в палитре их стоит помечать.
    /// <para>Классификация статическая: это свойство API и кокпита, а не того, какие моды стоят
    /// на машине пользователя.</para>
    /// </summary>
    /// <param name="kind">Тип.</param>
    /// <returns><c>true</c>, если тип встречается в выборке реальных шаблонов.</returns>
    public static bool ProvenInLiveTemplates(ControlKind kind) =>
        kind is ControlKind.CheckBox or ControlKind.RadioButtonGroup
             or ControlKind.Dropdown or ControlKind.Slider or ControlKind.HotKey;
}
