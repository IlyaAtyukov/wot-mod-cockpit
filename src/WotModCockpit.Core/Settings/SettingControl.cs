namespace WotModCockpit.Core.Settings;

/// <summary>
/// Один вариант в <c>options</c> у радиокнопок или выпадающего списка.
///
/// <para>API прогоняет варианты через <c>templates.generateOptions</c>, который принимает
/// строку, пару <c>(label, tooltip)</c>, список из одного элемента или готовый словарь,
/// а на выходе всегда даёт <c>{'label': ...}</c> с необязательным <c>tooltip</c>.</para>
///
/// <para><b>Про <see cref="Tooltip"/>.</b> Сгенерировать его API умеет, но окно его НЕ ЧИТАЕТ:
/// в теле <c>createRadioButtonGroup</c> из объекта варианта берётся только <c>label</c>.
/// Ни один из 55 живых вариантов подсказки не несёт. Поле в модели есть, чтобы импорт
/// ничего не терял, но пользы от него в 1.6.4 нет.</para>
/// </summary>
public sealed class ControlOption
{
    /// <summary>Пустой вариант.</summary>
    public ControlOption() { }

    /// <summary>Вариант с подписью.</summary>
    /// <param name="label">Подпись.</param>
    /// <param name="tooltip">Подсказка (окном 1.6.4 не читается).</param>
    public ControlOption(string label, string? tooltip = null)
    {
        Label = label;
        Tooltip = tooltip;
    }

    /// <summary>Подпись варианта — единственное, что реально рисует окно.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Подсказка варианта. Окно 1.6.4 её игнорирует.</summary>
    public string? Tooltip { get; set; }

    /// <summary>
    /// Посторонние ключи варианта, встреченные при импорте. В живых данных такое есть:
    /// один из чужих модов держит в вариантах <c>filename</c> и пользуется ими как носителем данных.
    /// Дизайнер их не создаёт, но и не выбрасывает.
    /// </summary>
    public IDictionary<string, SettingValue> Extra { get; } =
        new Dictionary<string, SettingValue>(StringComparer.Ordinal);

    /// <summary>Порядок посторонних ключей, каким он был в исходном шаблоне.</summary>
    public IList<string> ExtraOrder { get; } = new List<string>();

    /// <summary>Положить посторонний ключ, сохранив порядок.</summary>
    /// <param name="key">Ключ.</param>
    /// <param name="value">Значение.</param>
    public void SetExtra(string key, SettingValue value)
    {
        if (!Extra.ContainsKey(key)) ExtraOrder.Add(key);
        Extra[key] = value;
    }

    /// <summary>Копия варианта.</summary>
    /// <returns>Новый объект.</returns>
    public ControlOption Clone()
    {
        var copy = new ControlOption(Label, Tooltip);
        foreach (string key in ExtraOrder) copy.SetExtra(key, Extra[key]);
        return copy;
    }

    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>
/// Подсловарь <c>button</c> — маленькая кнопка справа от контрола.
///
/// <para>Клик шлёт <c>InteractiveEvent.BUTTON_CLICK(linkage, varName, ТЕКУЩЕЕ значение в UI)</c>,
/// которое доезжает до <c>buttonHandler(linkage, varName, value)</c>, переданного четвёртым
/// аргументом <c>setModTemplate</c>. Значение — то, что сейчас в интерфейсе, ЕЩЁ НЕ применённое.</para>
///
/// <para>Кнопка работает только у <c>CheckBox</c>, <c>RadioButtonGroup</c>, <c>Dropdown</c>,
/// <c>Slider</c> — см. <see cref="ControlKinds.HasButton"/>.</para>
/// </summary>
public sealed class ControlButton
{
    /// <summary>Умолчание ширины в окне.</summary>
    public const int DefaultWidth = 30;

    /// <summary>Умолчание высоты в окне.</summary>
    public const int DefaultHeight = 25;

    /// <summary>Текст кнопки. Задан — кнопка текстовая, и <see cref="IconSource"/> игнорируется.</summary>
    public string? Text { get; set; }

    /// <summary>Класс иконки. Работает, только если <see cref="Text"/> не задан.</summary>
    public string? IconSource { get; set; }

    /// <summary>Ширина; умолчание окна — 30.</summary>
    public double? Width { get; set; }

    /// <summary>Высота; умолчание окна — 25.</summary>
    public double? Height { get; set; }

    /// <summary>Сдвиг по Y относительно вычисленной позиции.</summary>
    public double? OffsetTop { get; set; }

    /// <summary>Сдвиг по X относительно вычисленной позиции.</summary>
    public double? OffsetLeft { get; set; }

    /// <summary>Сдвиг иконки по Y; умолчание окна — 0.</summary>
    public double? IconOffsetTop { get; set; }

    /// <summary>Сдвиг иконки по X; умолчание окна — 0.</summary>
    public double? IconOffsetLeft { get; set; }

    /// <summary>Посторонние ключи кнопки, встреченные при импорте.</summary>
    public IDictionary<string, SettingValue> Extra { get; } =
        new Dictionary<string, SettingValue>(StringComparer.Ordinal);

    /// <summary>Порядок посторонних ключей.</summary>
    public IList<string> ExtraOrder { get; } = new List<string>();

    /// <summary>Положить посторонний ключ, сохранив порядок.</summary>
    /// <param name="key">Ключ.</param>
    /// <param name="value">Значение.</param>
    public void SetExtra(string key, SettingValue value)
    {
        if (!Extra.ContainsKey(key)) ExtraOrder.Add(key);
        Extra[key] = value;
    }

    /// <summary>Копия кнопки.</summary>
    /// <returns>Новый объект.</returns>
    public ControlButton Clone()
    {
        var copy = new ControlButton
        {
            Text = Text,
            IconSource = IconSource,
            Width = Width,
            Height = Height,
            OffsetTop = OffsetTop,
            OffsetLeft = OffsetLeft,
            IconOffsetTop = IconOffsetTop,
            IconOffsetLeft = IconOffsetLeft
        };
        foreach (string key in ExtraOrder) copy.SetExtra(key, Extra[key]);
        return copy;
    }
}

/// <summary>
/// Коды клавиш в значении <c>HotKey</c>: не одна клавиша, а СПИСОК кодов, которые
/// должны быть нажаты одновременно (<c>checkKeyset = all(isKeyDown)</c>).
/// </summary>
public static class HotKeyValue
{
    /// <summary>Любой ALT: <c>KEY_LALT</c> 56 или <c>KEY_RALT</c> 184.</summary>
    public const int Alt = -1;

    /// <summary>Любой CTRL: <c>KEY_LCONTROL</c> 29 или <c>KEY_RCONTROL</c> 157.</summary>
    public const int Ctrl = -2;

    /// <summary>Любой SHIFT: <c>KEY_LSHIFT</c> 42 или <c>KEY_RSHIFT</c> 54.</summary>
    public const int Shift = -3;

    /// <summary>Наибольший код в <c>scripts/client/Keys.py</c>.</summary>
    public const int MaxKeyCode = 326;

    /// <summary>
    /// Коды, которые API назначить не даст (<c>_constants.EXCLUDED_KEYS</c>):
    /// 0 <c>KEY_NONE</c>, 28 <c>KEY_RETURN</c>, 256 ЛКМ, 257 ПКМ, 258 СКМ.
    /// Escape (1) не запрещён, но захват им отменяется — назначить его тоже нельзя.
    /// </summary>
    public static IReadOnlySet<int> Excluded { get; } = new HashSet<int> { 0, 28, 256, 257, 258 };

    /// <summary>Пары L/R, которые <c>_migrateKeys</c> схлопывает в спецкод.</summary>
    private static readonly (int Left, int Right, int Special)[] Pairs =
    {
        (56, 184, Alt),
        (29, 157, Ctrl),
        (42, 54, Shift)
    };

    /// <summary>Спецкод модификатора: -1, -2, -3.</summary>
    /// <param name="code">Код.</param>
    /// <returns><c>true</c>, если это ALT/CTRL/SHIFT.</returns>
    public static bool IsModifier(int code) => code is Alt or Ctrl or Shift;

    /// <summary>Русское имя спецкода.</summary>
    /// <param name="code">Код.</param>
    /// <returns>Имя или <c>null</c>.</returns>
    public static string? ModifierName(int code) => code switch
    {
        Alt => "ALT",
        Ctrl => "CTRL",
        Shift => "SHIFT",
        _ => null
    };

    /// <summary>
    /// Повторяет <c>hotkeys._migrateKeys</c>: рекурсивно сплющивает вложенные списки
    /// и заменяет пару L/R одним спецкодом. Именно так живое <c>[61, [29, 157]]</c>
    /// превращается в <c>{61, -2}</c>.
    /// </summary>
    /// <param name="value">Значение <c>HotKey</c> из шаблона.</param>
    /// <returns>Плоский набор кодов в порядке появления, без повторов.</returns>
    public static IReadOnlyList<int> Flatten(SettingValue? value)
    {
        var flat = new List<int>();
        Walk(value, flat);

        var result = new List<int>();
        foreach (int code in flat)
        {
            int mapped = code;
            foreach ((int left, int right, int special) in Pairs)
                if (code == left || code == right)
                    mapped = special;
            if (!result.Contains(mapped)) result.Add(mapped);
        }
        return result;

        static void Walk(SettingValue? node, List<int> sink)
        {
            if (node is null) return;
            if (node.Kind == SettingValueKind.List)
            {
                foreach (SettingValue item in node.Items) Walk(item, sink);
                return;
            }
            if (node.IsNumeric) sink.Add((int)node.AsInteger);
        }
    }

    /// <summary>Значение записано вложенными списками — легаси-форма до <c>_migrateKeys</c>.</summary>
    /// <param name="value">Значение.</param>
    /// <returns><c>true</c>, если внутри есть вложенный список.</returns>
    public static bool IsLegacyNested(SettingValue? value) =>
        value is { Kind: SettingValueKind.List } &&
        value.Items.Any(i => i.Kind == SettingValueKind.List);

    /// <summary>Собрать современное плоское значение.</summary>
    /// <param name="codes">Коды.</param>
    /// <returns>Значение для поля <c>value</c>.</returns>
    public static SettingValue Create(params int[] codes) =>
        SettingValue.List(codes.Select(c => SettingValue.Of((long)c)));
}

/// <summary>
/// Один компонент колонки шаблона ModsSettingsAPI.
///
/// <para>Класс намеренно изменяемый (а не <c>record</c>): дизайнер редактирует контрол
/// прямо в списке колонки, а привязки WPF работают по ссылке на объект.</para>
///
/// <para>Все поля, которых у типа быть не должно, остаются <c>null</c> и в JSON не попадают:
/// лишний ключ в шаблоне меняет его JSON, а <c>compareTemplates</c> без <c>settingsVersion</c>
/// сравнивает шаблоны текстом — лишнее поле означает сброс настроек игрока на ровном месте.</para>
/// </summary>
public sealed class SettingControl
{
    /// <summary>Пустой контрол неизвестного типа. Пользоваться фабриками ниже.</summary>
    public SettingControl() { }

    /// <summary>Контрол заданного типа.</summary>
    /// <param name="kind">Тип.</param>
    public SettingControl(ControlKind kind) => Kind = kind;

    /// <summary>Тип контрола.</summary>
    public ControlKind Kind { get; set; } = ControlKind.Unknown;

    /// <summary>
    /// Исходная строка поля <c>type</c>, если тип не опознан. Для опознанных типов — <c>null</c>,
    /// строку даёт <see cref="ControlKinds.WireName"/>.
    /// </summary>
    public string? RawTypeName { get; set; }

    /// <summary>Строка поля <c>type</c> для записи в шаблон.</summary>
    public string TypeName =>
        Kind == ControlKind.Unknown ? RawTypeName ?? string.Empty : ControlKinds.WireName(Kind);

    /// <summary>Подпись. У <c>Label</c> и <c>CheckBox</c> допускает HTML-разметку.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// В исходном шаблоне ключ <c>text</c> присутствовал. Нужно ТОЛЬКО для точного
    /// round-trip: пустая подпись и отсутствие подписи — разные шаблоны, а разный шаблон
    /// без <c>settingsVersion</c> означает сброс настроек игрока. Дизайнер подпись пишет всегда.
    /// </summary>
    public bool HasTextKey { get; set; } = true;

    /// <summary>В исходном шаблоне ключ <c>options</c> присутствовал (пусть и пустым списком).</summary>
    public bool HasOptionsKey { get; set; }

    /// <summary>
    /// Подсказка. Живой формат (48 из 48 живых) —
    /// <c>{HEADER}Заголовок{/HEADER}{BODY}Текст{/BODY}</c>, его собирает клиентская
    /// <c>makeTooltip</c>. Простая строка тоже работает.
    /// </summary>
    public string? Tooltip { get; set; }

    /// <summary>Ключ в словаре настроек. Обязателен у всех, кроме <c>Label</c> и <c>Empty</c>.</summary>
    public string? VarName { get; set; }

    /// <summary>Значение по умолчанию. Тип зависит от <see cref="Kind"/>.</summary>
    public SettingValue? Value { get; set; }

    /// <summary>Варианты — только у <c>RadioButtonGroup</c> и <c>Dropdown</c>.</summary>
    public IList<ControlOption> Options { get; } = new List<ControlOption>();

    /// <summary>Ширина; окно читает её только у <c>Dropdown</c>, <c>Slider</c>, <c>TextInput</c>.</summary>
    public int? Width { get; set; }

    /// <summary>Нижняя граница диапазона.</summary>
    public SettingValue? Minimum { get; set; }

    /// <summary>Верхняя граница диапазона.</summary>
    public SettingValue? Maximum { get; set; }

    /// <summary>Шаг. У живых модов бывает дробным (0.01) вопреки докстрингу API.</summary>
    public SettingValue? SnapInterval { get; set; }

    /// <summary>
    /// Формат метки справа от ползунка. Заменяются ВСЕ вхождения <c>{{value}}</c>
    /// (<c>format.split(key).join(value)</c>). Умолчание фабрики — <c>{{value}}</c>.
    /// </summary>
    public string? Format { get; set; }

    /// <summary>Разрешён ли ручной ввод в <c>NumericStepper</c>. Python пишет ключ ВСЕГДА.</summary>
    public bool? CanManualInput { get; set; }

    /// <summary>Шаг делений <c>RangeSlider</c> (<c>divisionStep</c>).</summary>
    public SettingValue? DivisionStep { get; set; }

    /// <summary>Минимальная ширина выделенного диапазона (<c>minRangeDistance</c>).</summary>
    public SettingValue? MinRangeDistance { get; set; }

    /// <summary>Через сколько делений подписывать шкалу (<c>divisionLabelStep</c>).</summary>
    public SettingValue? DivisionLabelStep { get; set; }

    /// <summary>Суффикс подписи шкалы (<c>divisionLabelPostfix</c>).</summary>
    public string? DivisionLabelPostfix { get; set; }

    /// <summary>Кнопка справа от контрола.</summary>
    public ControlButton? Button { get; set; }

    /// <summary>
    /// Ключи, которых модель не знает. Сюда попадает, в частности, <c>itemRenderer</c>
    /// у <c>Dropdown</c>.
    /// <para>Измерено: во всём <c>modsettingsWindow.swf</c> строка <c>itemRenderer</c>
    /// встречается РОВНО ОДИН раз — в пуле строк, вплотную к <c>getClass</c> и
    /// <c>DropDownListItemRendererSound</c>, то есть рядом с записью жёстко зашитого класса.
    /// Больше того из одной записи пула вывести нельзя: пул общий для чтений и записей,
    /// так что «окно его не читает» — вывод, а не доказательство. Зато твёрдо известно другое:
    /// ни одна фабрика <c>templates.py</c> этот ключ не кладёт, и два живых мода ставят его
    /// руками. Поэтому правило одно и то же при любом ответе: импорт обязан ключ сохранить,
    /// дизайнер — никогда не порождать.</para>
    /// </summary>
    public IDictionary<string, SettingValue> Extra { get; } =
        new Dictionary<string, SettingValue>(StringComparer.Ordinal);

    /// <summary>Порядок посторонних ключей, каким он был в исходном шаблоне.</summary>
    public IList<string> ExtraOrder { get; } = new List<string>();

    /// <summary>Положить посторонний ключ, сохранив порядок.</summary>
    /// <param name="key">Ключ.</param>
    /// <param name="value">Значение.</param>
    public void SetExtra(string key, SettingValue value)
    {
        if (!Extra.ContainsKey(key)) ExtraOrder.Add(key);
        Extra[key] = value;
    }

    /// <summary>Контрол попадает в словарь настроек (есть и <c>varName</c>, и <c>value</c>).</summary>
    public bool ContributesSetting => !string.IsNullOrEmpty(VarName) && Value is not null;

    /// <summary>Копия контрола, ничем не связанная с исходным.</summary>
    /// <returns>Новый объект.</returns>
    public SettingControl Clone()
    {
        var copy = new SettingControl(Kind)
        {
            RawTypeName = RawTypeName,
            Text = Text,
            HasTextKey = HasTextKey,
            HasOptionsKey = HasOptionsKey,
            Tooltip = Tooltip,
            VarName = VarName,
            Value = Value,
            Width = Width,
            Minimum = Minimum,
            Maximum = Maximum,
            SnapInterval = SnapInterval,
            Format = Format,
            CanManualInput = CanManualInput,
            DivisionStep = DivisionStep,
            MinRangeDistance = MinRangeDistance,
            DivisionLabelStep = DivisionLabelStep,
            DivisionLabelPostfix = DivisionLabelPostfix,
            Button = Button?.Clone()
        };
        foreach (ControlOption option in Options) copy.Options.Add(option.Clone());
        foreach (string key in ExtraOrder) copy.SetExtra(key, Extra[key]);
        return copy;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        VarName is { Length: > 0 } ? $"{TypeName} {VarName}" : TypeName;

    // ------------------------------------------------------------------ фабрики

    /// <summary>Распорка 20 px.</summary>
    /// <returns>Контрол.</returns>
    public static SettingControl Empty() => new(ControlKind.Empty);

    /// <summary>Заголовок секции.</summary>
    /// <param name="text">Текст.</param>
    /// <param name="tooltip">Подсказка.</param>
    /// <returns>Контрол.</returns>
    public static SettingControl Label(string text, string? tooltip = null) =>
        new(ControlKind.Label) { Text = text, Tooltip = tooltip };

    /// <summary>Галочка.</summary>
    /// <param name="text">Подпись.</param>
    /// <param name="varName">Ключ настройки.</param>
    /// <param name="value">Значение по умолчанию.</param>
    /// <param name="tooltip">Подсказка.</param>
    /// <returns>Контрол.</returns>
    public static SettingControl CheckBox(string text, string varName, bool value, string? tooltip = null) =>
        new(ControlKind.CheckBox) { Text = text, VarName = varName, Value = SettingValue.Of(value), Tooltip = tooltip };

    /// <summary>Радиокнопки.</summary>
    /// <param name="text">Подпись.</param>
    /// <param name="varName">Ключ настройки.</param>
    /// <param name="value">Индекс варианта по умолчанию.</param>
    /// <param name="options">Подписи вариантов.</param>
    /// <returns>Контрол.</returns>
    public static SettingControl RadioButtonGroup(string text, string varName, int value, params string[] options)
    {
        var control = new SettingControl(ControlKind.RadioButtonGroup)
        {
            Text = text,
            VarName = varName,
            Value = SettingValue.Of((long)value)
        };
        foreach (string option in options) control.Options.Add(new ControlOption(option));
        return control;
    }

    /// <summary>Выпадающий список. Ширина по умолчанию — 200, как в окне.</summary>
    /// <param name="text">Подпись.</param>
    /// <param name="varName">Ключ настройки.</param>
    /// <param name="value">Индекс варианта по умолчанию.</param>
    /// <param name="options">Подписи вариантов.</param>
    /// <returns>Контрол.</returns>
    public static SettingControl Dropdown(string text, string varName, int value, params string[] options)
    {
        var control = new SettingControl(ControlKind.Dropdown)
        {
            Text = text,
            VarName = varName,
            Value = SettingValue.Of((long)value),
            Width = ControlKinds.DefaultWidth
        };
        foreach (string option in options) control.Options.Add(new ControlOption(option));
        return control;
    }

    /// <summary>Ползунок с целым диапазоном.</summary>
    /// <param name="text">Подпись.</param>
    /// <param name="varName">Ключ настройки.</param>
    /// <param name="value">Значение по умолчанию.</param>
    /// <param name="minimum">Минимум.</param>
    /// <param name="maximum">Максимум.</param>
    /// <param name="snapInterval">Шаг.</param>
    /// <param name="format">Формат метки; по умолчанию <c>{{value}}</c>.</param>
    /// <returns>Контрол.</returns>
    public static SettingControl Slider(
        string text, string varName, long value, long minimum, long maximum, long snapInterval,
        string? format = null) =>
        new(ControlKind.Slider)
        {
            Text = text,
            VarName = varName,
            Value = SettingValue.Of(value),
            Minimum = SettingValue.Of(minimum),
            Maximum = SettingValue.Of(maximum),
            SnapInterval = SettingValue.Of(snapInterval),
            Format = format ?? ControlKinds.SliderValuePlaceholder,
            Width = ControlKinds.DefaultWidth
        };

    /// <summary>Строка ввода.</summary>
    /// <param name="text">Подпись.</param>
    /// <param name="varName">Ключ настройки.</param>
    /// <param name="value">Значение по умолчанию.</param>
    /// <returns>Контрол.</returns>
    public static SettingControl TextInput(string text, string varName, string value) =>
        new(ControlKind.TextInput)
        {
            Text = text,
            VarName = varName,
            Value = SettingValue.Of(value),
            Width = ControlKinds.DefaultWidth
        };

    /// <summary>Числовой счётчик.</summary>
    /// <param name="text">Подпись.</param>
    /// <param name="varName">Ключ настройки.</param>
    /// <param name="value">Значение по умолчанию.</param>
    /// <param name="minimum">Минимум.</param>
    /// <param name="maximum">Максимум.</param>
    /// <param name="snapInterval">Шаг.</param>
    /// <param name="canManualInput">Разрешить ввод с клавиатуры.</param>
    /// <returns>Контрол.</returns>
    public static SettingControl NumericStepper(
        string text, string varName, long value, long minimum, long maximum, long snapInterval,
        bool canManualInput = false) =>
        new(ControlKind.NumericStepper)
        {
            Text = text,
            VarName = varName,
            Value = SettingValue.Of(value),
            Minimum = SettingValue.Of(minimum),
            Maximum = SettingValue.Of(maximum),
            SnapInterval = SettingValue.Of(snapInterval),
            CanManualInput = canManualInput
        };

    /// <summary>Горячая клавиша.</summary>
    /// <param name="text">Подпись.</param>
    /// <param name="varName">Ключ настройки.</param>
    /// <param name="codes">Коды клавиш, нажатые одновременно.</param>
    /// <returns>Контрол.</returns>
    public static SettingControl HotKey(string text, string varName, params int[] codes) =>
        new(ControlKind.HotKey) { Text = text, VarName = varName, Value = HotKeyValue.Create(codes) };

    /// <summary>Выбор цвета.</summary>
    /// <param name="text">Подпись.</param>
    /// <param name="varName">Ключ настройки.</param>
    /// <param name="hex">Цвет шестью hex-цифрами; ведущая решётка снимается.</param>
    /// <returns>Контрол.</returns>
    public static SettingControl ColorChoice(string text, string varName, string hex) =>
        new(ControlKind.ColorChoice)
        {
            Text = text,
            VarName = varName,
            Value = SettingValue.Of((hex ?? string.Empty).TrimStart('#'))
        };

    /// <summary>Диапазонный ползунок.</summary>
    /// <param name="text">Подпись.</param>
    /// <param name="varName">Ключ настройки.</param>
    /// <param name="left">Левая граница выделения.</param>
    /// <param name="right">Правая граница выделения.</param>
    /// <param name="minimum">Минимум шкалы.</param>
    /// <param name="maximum">Максимум шкалы.</param>
    /// <param name="snapInterval">Шаг.</param>
    /// <returns>Контрол.</returns>
    public static SettingControl RangeSlider(
        string text, string varName, long left, long right, long minimum, long maximum, long snapInterval) =>
        new(ControlKind.RangeSlider)
        {
            Text = text,
            VarName = varName,
            Value = SettingValue.List(left, right),
            Minimum = SettingValue.Of(minimum),
            Maximum = SettingValue.Of(maximum),
            SnapInterval = SettingValue.Of(snapInterval),
            DivisionStep = SettingValue.Of(snapInterval),
            MinRangeDistance = SettingValue.Of(snapInterval),
            DivisionLabelStep = SettingValue.Of(snapInterval),
            DivisionLabelPostfix = string.Empty
        };
}
