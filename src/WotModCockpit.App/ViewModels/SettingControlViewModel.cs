using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WotModCockpit.App.Stage4;

namespace WotModCockpit.App.ViewModels;

/// <summary>Один пункт списка у переключателей и выпадающего списка.</summary>
public sealed partial class SettingOptionViewModel : ObservableObject
{
    private readonly Action _changed;

    /// <summary>Создаёт пункт.</summary>
    /// <param name="label">Текст пункта.</param>
    /// <param name="tooltip">Подсказка пункта.</param>
    /// <param name="changed">Что позвать при изменении.</param>
    public SettingOptionViewModel(string label, string tooltip, Action changed)
        : this(label, tooltip, changed, null)
    {
    }

    /// <summary>Создаёт пункт, сохраняя чужие ключи, пришедшие импортом.</summary>
    /// <param name="label">Текст пункта.</param>
    /// <param name="tooltip">Подсказка пункта.</param>
    /// <param name="changed">Что позвать при изменении.</param>
    /// <param name="extra">Чужие ключи пункта сырыми литералами JSON.</param>
    public SettingOptionViewModel(
        string label,
        string tooltip,
        Action changed,
        IReadOnlyDictionary<string, string>? extra)
    {
        _label = label;
        _tooltip = tooltip;
        _changed = changed;
        Extra = extra ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Чужие ключи ВНУТРИ пункта, пришедшие импортом (живой InfoPanel носит в пункте
    /// <c>filename</c> с путём к скину). Дизайнер их не редактирует, но и не теряет.
    /// </summary>
    public IReadOnlyDictionary<string, string> Extra { get; }

    /// <summary>У пункта есть чужие ключи, пришедшие импортом.</summary>
    public bool HasExtra => Extra.Count > 0;

    /// <summary>Строка про чужие ключи пункта для подписи в редакторе; пусто — их нет.</summary>
    public string ExtraSummary => Extra.Count == 0
        ? string.Empty
        : "чужие ключи пункта сохраняются как есть: "
          + string.Join(", ", Extra.Keys.OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>Текст пункта — единственное, что окно 1.6.4 из пункта читает.</summary>
    [ObservableProperty]
    private string _label;

    /// <summary>
    /// Этот пункт выбран значением контрола. Поддерживается родителем: значение
    /// контрола — это ИНДЕКС, а превью должно показывать выбранный пункт как выбранный.
    /// </summary>
    [ObservableProperty]
    private bool _isSelectedValue;

    /// <summary>
    /// Подсказка пункта. Фабрика её кладёт, но окно 1.6.4 её ИГНОРИРУЕТ: в теле
    /// <c>createRadioButtonGroup</c> нет ни одного чтения <c>tooltip</c> у пункта.
    /// Поле оставлено, чтобы не терять его при импорте чужого шаблона.
    /// </summary>
    [ObservableProperty]
    private string _tooltip;

    partial void OnLabelChanged(string value) => _changed();

    partial void OnTooltipChanged(string value) => _changed();
}

/// <summary>
/// Один контрол формы настроек: и модель для редактора свойств справа, и модель
/// для схематичного превью в центре.
/// <para>
/// Числа держатся СТРОКАМИ, а не <c>double</c>. Причина не в лени: поле, которое молча
/// откатывает «0,5» или пустую строку к прошлому значению, невозможно ни заполнить,
/// ни продиагностировать. Здесь неразобранное число остаётся в поле и превращается
/// в замечание, которое видно и у поля, и в общем списке.
/// </para>
/// </summary>
public sealed partial class SettingControlViewModel : ObservableObject
{
    private static readonly Regex TooltipSection =
        new(@"\{(?<tag>HEADER|BODY|NOTE|ATTENTION)\}(?<text>.*?)\{/\k<tag>\}",
            RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly Action _changed;
    private bool _muted;

    /// <summary>Создаёт контрол выбранного типа со значениями по умолчанию.</summary>
    /// <param name="type">Тип.</param>
    /// <param name="changed">Что позвать при любом изменении (перепроверка + перерисовка превью).</param>
    public SettingControlViewModel(SettingsControlType type, Action changed)
    {
        Type = type;
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));

        Options = new ObservableCollection<SettingOptionViewModel>();
        Options.CollectionChanged += (_, _) =>
        {
            SyncOptionSelection();
            OnPropertyChanged(nameof(OptionLabels));
            OnPropertyChanged(nameof(PreviewValueText));
            OnPropertyChanged(nameof(ScrollsInGame));
            _changed();
        };

        ApplyDefaults();
    }

    /// <summary>Тип контрола. Меняется только пересозданием: у типов разный набор полей.</summary>
    public SettingsControlType Type { get; }

    /// <summary>Строка <c>type</c> шаблона.</summary>
    public string TypeName => Type.ToString();

    /// <summary>Имя типа по-русски.</summary>
    public string TypeDisplay => SettingsControlSchema.DisplayName(Type);

    /// <summary>Ключ шаблона для выбора редактора и вида в превью.</summary>
    public string TemplateKey => Type.ToString();

    /// <summary>Одна строка про тип.</summary>
    public string TypeSummary => SettingsControlSchema.Summary(Type);

    /// <summary>Сколько таких контролов встретилось в живых шаблонах, по которым сверялся кокпит при разработке.</summary>
    public int LiveSamples => SettingsControlSchema.LiveSamples(Type);

    /// <summary>Живых образцов нет — тип восстановлен только по коду API.</summary>
    public bool IsUnproven => LiveSamples == 0;

    // =================================================================== общие поля

    /// <summary>Подпись контрола.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _text = string.Empty;

    /// <summary>Ключ настройки.</summary>
    [ObservableProperty]
    private string _varName = string.Empty;

    /// <summary>
    /// Подсказка вводится «как есть», а не четырьмя полями. Так открываются чужие
    /// подсказки, которые в формат <c>{HEADER}…{BODY}…</c> не укладываются.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    [NotifyPropertyChangedFor(nameof(HasTooltip))]
    private bool _rawTooltip;

    /// <summary>Подсказка одной строкой (режим «как есть»).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    [NotifyPropertyChangedFor(nameof(HasTooltip))]
    private string _tooltipRaw = string.Empty;

    /// <summary>Заголовок подсказки — секция <c>{HEADER}</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    [NotifyPropertyChangedFor(nameof(HasTooltip))]
    private string _tooltipHeader = string.Empty;

    /// <summary>Текст подсказки — секция <c>{BODY}</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    [NotifyPropertyChangedFor(nameof(HasTooltip))]
    private string _tooltipBody = string.Empty;

    /// <summary>Примечание — секция <c>{NOTE}</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    [NotifyPropertyChangedFor(nameof(HasTooltip))]
    private string _tooltipNote = string.Empty;

    /// <summary>Предупреждение — секция <c>{ATTENTION}</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    [NotifyPropertyChangedFor(nameof(HasTooltip))]
    private string _tooltipAttention = string.Empty;

    /// <summary>Готовая строка подсказки — ровно то, что уедет в шаблон.</summary>
    public string Tooltip
    {
        get
        {
            if (RawTooltip)
            {
                return TooltipRaw.Trim();
            }

            var sb = new StringBuilder();
            Section(sb, "HEADER", TooltipHeader);
            Section(sb, "BODY", TooltipBody);
            Section(sb, "NOTE", TooltipNote);
            Section(sb, "ATTENTION", TooltipAttention);
            return sb.ToString();

            static void Section(StringBuilder sb, string tag, string text)
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.Append('{').Append(tag).Append('}').Append(text.Trim())
                      .Append("{/").Append(tag).Append('}');
                }
            }
        }
    }

    /// <summary>У контрола есть подсказка — в превью рядом с ним появляется значок «i».</summary>
    public bool HasTooltip => Tooltip.Length > 0;

    // =================================================================== значения

    /// <summary>Значение галочки.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewValueText))]
    private bool _boolValue;

    /// <summary>Индекс выбранного пункта у переключателей и списка.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewValueText))]
    private int _indexValue;

    partial void OnIndexValueChanged(int value) => SyncOptionSelection();

    private void SyncOptionSelection()
    {
        for (int i = 0; i < Options.Count; i++)
        {
            Options[i].IsSelectedValue = i == IndexValue;
        }
    }

    /// <summary>Числовое значение строкой.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewValueText))]
    [NotifyPropertyChangedFor(nameof(PreviewFillWidth))]
    private string _numberText = "0";

    /// <summary>Строковое значение (TextInput).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewValueText))]
    private string _stringValue = string.Empty;

    /// <summary>Цвет шестью hex-цифрами БЕЗ решётки — ровно так его хранит API.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewValueText))]
    [NotifyPropertyChangedFor(nameof(ColorBrush))]
    private string _colorValue = "FFCC00";

    /// <summary>Левый конец диапазона строкой.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewValueText))]
    [NotifyPropertyChangedFor(nameof(RangeFillOffset))]
    [NotifyPropertyChangedFor(nameof(RangeFillWidth))]
    private string _rangeLowText = "20";

    /// <summary>Правый конец диапазона строкой.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewValueText))]
    [NotifyPropertyChangedFor(nameof(RangeFillOffset))]
    [NotifyPropertyChangedFor(nameof(RangeFillWidth))]
    private string _rangeHighText = "80";

    // =================================================================== диапазон и вид

    /// <summary>Минимум строкой.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewFillWidth))]
    [NotifyPropertyChangedFor(nameof(RangeFillOffset))]
    [NotifyPropertyChangedFor(nameof(RangeFillWidth))]
    private string _minimumText = "0";

    /// <summary>Максимум строкой.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewFillWidth))]
    [NotifyPropertyChangedFor(nameof(RangeFillOffset))]
    [NotifyPropertyChangedFor(nameof(RangeFillWidth))]
    private string _maximumText = "100";

    /// <summary>Шаг строкой (<c>snapInterval</c>).</summary>
    [ObservableProperty]
    private string _snapText = "1";

    /// <summary>Формат метки ползунка.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewValueText))]
    private string _format = "{{value}}";

    /// <summary>
    /// Ширина строкой; пусто — окно подставит своё умолчание 200.
    /// <para>Новый контрол приходит именно с ПУСТЫМ полем, а не с готовой «200»: фабрики
    /// <c>templates.createSlider/createDropdown/createInput</c> при <c>width=None</c> ключ
    /// в шаблон НЕ кладут, а окно подставляет 200 само. Заполненное поле означало бы, что
    /// кокпит пишет ключ, которого API бы не написал: в 13 живых шаблонах <c>width</c> есть
    /// лишь у 13 компонентов из 56, а лишний ключ — это лишнее расхождение JSON у модов
    /// без <c>settingsVersion</c>.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewWidth))]
    [NotifyPropertyChangedFor(nameof(PreviewFillWidth))]
    [NotifyPropertyChangedFor(nameof(UsesDefaultWidth))]
    private string _widthText = string.Empty;

    /// <summary>Поле ширины пусто — окно подставит своё умолчание (для плейсхолдера в UI).</summary>
    public bool UsesDefaultWidth => WidthText.Trim().Length == 0;

    /// <summary>Текст плейсхолдера поля ширины.</summary>
    public static string DefaultWidthHint =>
        SettingsControlSchema.DefaultWidth.ToString(CultureInfo.InvariantCulture);

    /// <summary>Разрешить ручной ввод числа в степпере.</summary>
    [ObservableProperty]
    private bool _canManualInput;

    /// <summary>RangeSlider: шаг делений шкалы.</summary>
    [ObservableProperty]
    private string _divisionStepText = "10";

    /// <summary>RangeSlider: минимальная ширина выбранного диапазона.</summary>
    [ObservableProperty]
    private string _minRangeDistanceText = "1";

    /// <summary>RangeSlider: через сколько делений подписывать шкалу.</summary>
    [ObservableProperty]
    private string _divisionLabelStepText = "10";

    /// <summary>RangeSlider: приписка к подписям шкалы.</summary>
    [ObservableProperty]
    private string _divisionLabelPostfix = string.Empty;

    // =================================================================== горячая клавиша

    /// <summary>В сочетании участвует Alt (спецкод -1: любой из левого и правого).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotKeyCodes))]
    [NotifyPropertyChangedFor(nameof(PreviewValueText))]
    [NotifyPropertyChangedFor(nameof(HotKeyLiteral))]
    private bool _altKey;

    /// <summary>В сочетании участвует Ctrl (спецкод -2).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotKeyCodes))]
    [NotifyPropertyChangedFor(nameof(PreviewValueText))]
    [NotifyPropertyChangedFor(nameof(HotKeyLiteral))]
    private bool _ctrlKey;

    /// <summary>В сочетании участвует Shift (спецкод -3).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotKeyCodes))]
    [NotifyPropertyChangedFor(nameof(PreviewValueText))]
    [NotifyPropertyChangedFor(nameof(HotKeyLiteral))]
    private bool _shiftKey;

    /// <summary>Основная клавиша сочетания; <c>null</c> — только модификаторы.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotKeyCodes))]
    [NotifyPropertyChangedFor(nameof(PreviewValueText))]
    [NotifyPropertyChangedFor(nameof(HotKeyLiteral))]
    private SettingsKeyCode? _mainKey;

    /// <summary>Коды клавиш так, как они уедут в шаблон.</summary>
    public IReadOnlyList<int> HotKeyCodes
    {
        get
        {
            var codes = new List<int>();
            if (CtrlKey)
            {
                codes.Add(SettingsHotKey.Ctrl);
            }

            if (AltKey)
            {
                codes.Add(SettingsHotKey.Alt);
            }

            if (ShiftKey)
            {
                codes.Add(SettingsHotKey.Shift);
            }

            if (MainKey is not null)
            {
                codes.Add(MainKey.Code);
            }

            return codes;
        }
    }

    /// <summary>Значение горячей клавиши так, как оно выглядит в коде: <c>[61, -2]</c>.</summary>
    public string HotKeyLiteral => SettingsHotKey.RenderList(HotKeyCodes);

    // =================================================================== пункты списка

    /// <summary>Пункты списка.</summary>
    public ObservableCollection<SettingOptionViewModel> Options { get; }

    /// <summary>Подписи пунктов — их рисует превью.</summary>
    public IReadOnlyList<string> OptionLabels => Options.Select(o => o.Label).ToList();

    /// <summary>Выбранный пункт списка (для редактора).</summary>
    [ObservableProperty]
    private SettingOptionViewModel? _selectedOption;

    /// <summary>Добавить пункт в конец списка.</summary>
    [RelayCommand]
    private void AddOption()
    {
        var option = new SettingOptionViewModel($"Вариант {Options.Count + 1}", string.Empty, _changed);
        Options.Add(option);
        SelectedOption = option;
    }

    private bool CanEditOption() => SelectedOption is not null;

    /// <summary>Удалить выбранный пункт.</summary>
    [RelayCommand(CanExecute = nameof(CanEditOption))]
    private void RemoveOption()
    {
        SettingOptionViewModel? option = SelectedOption;
        if (option is null)
        {
            return;
        }

        int index = Options.IndexOf(option);
        Options.Remove(option);

        // Значение контрола — ИНДЕКС в списке: после удаления его надо подтянуть,
        // иначе шаблон будет ссылаться на пункт, которого больше нет.
        if (IndexValue >= Options.Count)
        {
            IndexValue = Math.Max(0, Options.Count - 1);
        }

        SelectedOption = Options.Count == 0
            ? null
            : Options[Math.Min(index, Options.Count - 1)];
    }

    /// <summary>Поднять выбранный пункт.</summary>
    [RelayCommand(CanExecute = nameof(CanEditOption))]
    private void MoveOptionUp() => MoveOption(-1);

    /// <summary>Опустить выбранный пункт.</summary>
    [RelayCommand(CanExecute = nameof(CanEditOption))]
    private void MoveOptionDown() => MoveOption(1);

    private void MoveOption(int delta)
    {
        SettingOptionViewModel? option = SelectedOption;
        if (option is null)
        {
            return;
        }

        int index = Options.IndexOf(option);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Options.Count)
        {
            return;
        }

        Options.Move(index, target);

        if (IndexValue == index)
        {
            IndexValue = target;
        }
        else if (IndexValue == target)
        {
            IndexValue = index;
        }

        SelectedOption = option;
    }

    partial void OnSelectedOptionChanged(SettingOptionViewModel? value)
    {
        RemoveOptionCommand.NotifyCanExecuteChanged();
        MoveOptionUpCommand.NotifyCanExecuteChanged();
        MoveOptionDownCommand.NotifyCanExecuteChanged();
    }

    // =================================================================== замечания

    private string _errorText = string.Empty;
    private string _warningText = string.Empty;

    /// <summary>Ошибки этого контрола одной строкой. Пусто — ошибок нет.</summary>
    public string ErrorText => _errorText;

    /// <summary>Предупреждения одной строкой.</summary>
    public string WarningText => _warningText;

    /// <summary>У контрола есть ошибка: генерацию она блокирует.</summary>
    public bool HasError => _errorText.Length > 0;

    /// <summary>У контрола есть предупреждение.</summary>
    public bool HasWarning => _warningText.Length > 0;

    /// <summary>
    /// Записывает итог проверки. Отдельный метод, а не свойства: проверку запускает
    /// изменение любого поля, и запись результата обратно в свойства устроила бы петлю.
    /// </summary>
    /// <param name="errors">Ошибки.</param>
    /// <param name="warnings">Предупреждения.</param>
    public void SetIssues(IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
    {
        _muted = true;
        try
        {
            _errorText = string.Join("; ", errors);
            _warningText = string.Join("; ", warnings);

            OnPropertyChanged(nameof(ErrorText));
            OnPropertyChanged(nameof(WarningText));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(HasWarning));
        }
        finally
        {
            _muted = false;
        }
    }

    // =================================================================== превью

    /// <summary>Подпись в превью: пустая заменяется на явную пометку, чтобы контрол не «пропал».</summary>
    public string PreviewText => Text.Length > 0 ? Text : "(без подписи)";

    /// <summary>Значение так, как его покажет окно игры.</summary>
    public string PreviewValueText => Type switch
    {
        SettingsControlType.Slider => Format.Replace(
            "{{value}}",
            NumberText.Trim().Length == 0 ? "0" : NumberText.Trim(),
            StringComparison.Ordinal),
        SettingsControlType.NumericStepper => NumberText.Trim(),
        SettingsControlType.Dropdown or SettingsControlType.RadioButtonGroup =>
            IndexValue >= 0 && IndexValue < Options.Count ? Options[IndexValue].Label : "(нет пункта)",
        SettingsControlType.TextInput => StringValue,
        SettingsControlType.ColorChoice => "#" + ColorValue.ToUpperInvariant(),
        SettingsControlType.HotKey => SettingsHotKey.Describe(HotKeyCodes),
        SettingsControlType.RangeSlider => RangeLowText.Trim() + " / " + RangeHighText.Trim(),
        _ => string.Empty
    };

    /// <summary>Ширина контрола в пикселях окна для превью.</summary>
    public double PreviewWidth
    {
        get
        {
            if (!SettingsControlSchema.HasWidth(Type))
            {
                return Type == SettingsControlType.RangeSlider ? 240 : SettingsControlSchema.ColumnWidth;
            }

            double width = ParseNumber(WidthText) ?? SettingsControlSchema.DefaultWidth;
            return Math.Clamp(width, 20, SettingsControlSchema.ColumnWidth);
        }
    }

    /// <summary>Кисть образца цвета.</summary>
    public Brush ColorBrush
    {
        get
        {
            if (TryParseColor(ColorValue, out Color color))
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }

            return Brushes.Transparent;
        }
    }

    /// <summary>
    /// Сколько пикселей дорожки ползунка закрашено значением. Считается здесь, а не
    /// конвертером в разметке: превью обязано меняться на каждое нажатие клавиши в поле.
    /// </summary>
    public double PreviewFillWidth
    {
        get
        {
            double min = Minimum ?? 0;
            double max = Maximum ?? 0;
            double value = ValueNumber ?? min;

            if (max <= min)
            {
                return 0;
            }

            double ratio = Math.Clamp((value - min) / (max - min), 0, 1);
            return Math.Round(PreviewWidth * ratio);
        }
    }

    /// <summary>Отступ закрашенной части диапазонного ползунка.</summary>
    public double RangeFillOffset
    {
        get
        {
            double min = Minimum ?? 0;
            double max = Maximum ?? 0;
            double low = RangeLow ?? min;

            if (max <= min)
            {
                return 0;
            }

            return Math.Round(240 * Math.Clamp((low - min) / (max - min), 0, 1));
        }
    }

    /// <summary>Ширина закрашенной части диапазонного ползунка.</summary>
    public double RangeFillWidth
    {
        get
        {
            double min = Minimum ?? 0;
            double max = Maximum ?? 0;
            double low = RangeLow ?? min;
            double high = RangeHigh ?? max;

            if (max <= min || high <= low)
            {
                return 2;
            }

            return Math.Max(2, Math.Round(240 * Math.Clamp((high - low) / (max - min), 0, 1)));
        }
    }

    /// <summary>Высота контрола в схеме окна — по константам раскладки самого окна.</summary>
    public double PreviewHeight => SettingsControlSchema.PreviewHeight(Type, Options.Count);

    /// <summary>Список прокручивается: у окна лимит 9 пунктов.</summary>
    public bool ScrollsInGame =>
        Type == SettingsControlType.Dropdown && Options.Count > SettingsControlSchema.DropdownScrollLimit;

    // =================================================================== схема полей

    /// <summary>У контрола есть подпись.</summary>
    public bool SupportsText => SettingsControlSchema.HasText(Type);

    /// <summary>У контрола есть <c>varName</c> и значение.</summary>
    public bool SupportsValue => SettingsControlSchema.HasValue(Type);

    /// <summary>У контрола есть список пунктов.</summary>
    public bool SupportsOptions => SettingsControlSchema.HasOptions(Type);

    /// <summary>Окно читает у этого типа <c>width</c>.</summary>
    public bool SupportsWidth => SettingsControlSchema.HasWidth(Type);

    /// <summary>У контрола есть числовой диапазон.</summary>
    public bool SupportsRange => SettingsControlSchema.HasRange(Type);

    /// <summary>У контрола есть формат метки.</summary>
    public bool SupportsFormat => SettingsControlSchema.HasFormat(Type);

    // =================================================================== разбор чисел

    /// <summary>Разобранное значение либо <c>null</c>.</summary>
    public double? ValueNumber => ParseNumber(NumberText);

    /// <summary>Разобранный минимум.</summary>
    public double? Minimum => ParseNumber(MinimumText);

    /// <summary>Разобранный максимум.</summary>
    public double? Maximum => ParseNumber(MaximumText);

    /// <summary>Разобранный шаг.</summary>
    public double? SnapInterval => ParseNumber(SnapText);

    /// <summary>Разобранная ширина; <c>null</c> — поле пустое, ширина не пишется.</summary>
    public double? Width => WidthText.Trim().Length == 0 ? null : ParseNumber(WidthText);

    /// <summary>Разобранный левый конец диапазона.</summary>
    public double? RangeLow => ParseNumber(RangeLowText);

    /// <summary>Разобранный правый конец диапазона.</summary>
    public double? RangeHigh => ParseNumber(RangeHighText);

    /// <summary>
    /// Число из текста поля. Запятая принимается наравне с точкой: раскладка у мододела
    /// русская, а Python понимает только точку — приводим здесь, а не ругаемся.
    /// </summary>
    /// <param name="text">Текст поля.</param>
    /// <returns>Число либо <c>null</c>.</returns>
    public static double? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string normalized = text.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    /// <summary>Разбирает шесть hex-цифр в цвет.</summary>
    /// <param name="hex">Строка без решётки.</param>
    /// <param name="color">Цвет.</param>
    /// <returns>true, если разобрано.</returns>
    public static bool TryParseColor(string? hex, out Color color)
    {
        color = Colors.Transparent;

        string value = (hex ?? string.Empty).Trim().TrimStart('#');
        if (value.Length != 6 || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            return false;
        }

        color = Color.FromRgb((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        return true;
    }

    // =================================================================== снимок и загрузка

    /// <summary>Собирает неизменяемый снимок для генератора.</summary>
    /// <returns>Компонент шаблона.</returns>
    public SettingsControlSpec ToSpec() => new()
    {
        Type = Type,
        Text = Text.Trim(),
        Tooltip = Tooltip,
        VarName = SupportsValue ? VarName.Trim() : string.Empty,
        Value = SpecValue(),
        Options = SupportsOptions
            ? Options.Select(o => new SettingsOptionSpec(o.Label, o.Tooltip) { Extra = o.Extra }).ToList()
            : Array.Empty<SettingsOptionSpec>(),
        Minimum = Minimum ?? 0,
        Maximum = Maximum ?? 0,
        SnapInterval = SnapInterval ?? 1,
        Format = Format,
        Width = (int)(Width ?? 0),
        CanManualInput = CanManualInput,
        DivisionStep = ParseNumber(DivisionStepText) ?? 10,
        MinRangeDistance = ParseNumber(MinRangeDistanceText) ?? 1,
        DivisionLabelStep = ParseNumber(DivisionLabelStepText) ?? 10,
        DivisionLabelPostfix = DivisionLabelPostfix,
        Unknown = Unknown,
        FloatKeys = FloatKeys()
    };

    /// <summary>
    /// Какие числа этого контрола обязаны уехать в шаблон С ДРОБНОЙ ЧАСТЬЮ.
    ///
    /// <para>Правило одно на весь диапазон: если ХОТЬ ОДНО из четырёх чисел ползунка дробное,
    /// дробными печатаются все четыре. Иначе ползунок «прозрачность» 0…1 с шагом 0,05 и
    /// умолчанием <c>1</c> отдал бы в питон <c>int</c>, и <c>settings.get('opacity') / 2</c>
    /// дало бы 0 вместо 0.5 — а после первого «Применить» то же значение прилетело бы из
    /// Flash уже <c>float</c>, то есть тип настройки скакал бы. У целого ползунка
    /// (0…100 шаг 1) всё остаётся целым — ровно как в 13 живых шаблонах.</para>
    /// </summary>
    /// <returns>Имена числовых полей, печатаемых с точкой.</returns>
    private IReadOnlySet<string> FloatKeys()
    {
        if (!SupportsRange)
        {
            return SettingsControlSpec.NoFloatKeys;
        }

        bool fractional =
            LooksFractional(MinimumText) || LooksFractional(MaximumText) || LooksFractional(SnapText)
            || (Type == SettingsControlType.RangeSlider
                ? LooksFractional(RangeLowText) || LooksFractional(RangeHighText)
                : LooksFractional(NumberText));

        return fractional
            ? new HashSet<string>(StringComparer.Ordinal)
            {
                SettingsTemplateJson.MinimumKey,
                SettingsTemplateJson.MaximumKey,
                SettingsTemplateJson.SnapIntervalKey,
                SettingsTemplateJson.ValueKey
            }
            : SettingsControlSpec.NoFloatKeys;
    }

    /// <summary>Число записано дробным — по точке в тексте либо по самому значению.</summary>
    private static bool LooksFractional(string? text)
    {
        double? value = ParseNumber(text);
        if (value is null)
        {
            return false;
        }

        return text!.Contains('.', StringComparison.Ordinal)
               || text.Contains(',', StringComparison.Ordinal)
               || Math.Abs(value.Value - Math.Round(value.Value)) >= 1e-9;
    }

    /// <summary>Чужие ключи компонента, пришедшие импортом.</summary>
    public IReadOnlyDictionary<string, string> Unknown { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private object? SpecValue() => Type switch
    {
        SettingsControlType.CheckBox => BoolValue,
        SettingsControlType.RadioButtonGroup or SettingsControlType.Dropdown => (double)IndexValue,
        SettingsControlType.Slider or SettingsControlType.NumericStepper => ValueNumber ?? 0,
        SettingsControlType.TextInput => StringValue,
        SettingsControlType.ColorChoice => ColorValue.Trim().TrimStart('#').ToUpperInvariant(),
        SettingsControlType.HotKey => HotKeyCodes.ToArray(),
        SettingsControlType.RangeSlider => new[] { RangeLow ?? 0, RangeHigh ?? 0 },
        _ => null
    };

    /// <summary>
    /// Заполняет контрол из снимка — так работает импорт из проекта и из живого
    /// <c>modsettings.dat</c>.
    /// </summary>
    /// <param name="spec">Снимок.</param>
    /// <param name="keys">Таблица клавиш, из которой опознаётся основная клавиша сочетания.</param>
    public void Load(SettingsControlSpec spec, IReadOnlyList<SettingsKeyCode> keys)
    {
        ArgumentNullException.ThrowIfNull(spec);

        _muted = true;
        try
        {
            Text = spec.Text;
            VarName = spec.VarName;
            Unknown = spec.Unknown;
            LoadTooltip(spec.Tooltip);

            Options.Clear();
            foreach (SettingsOptionSpec option in spec.Options)
            {
                Options.Add(new SettingOptionViewModel(option.Label, option.Tooltip, _changed, option.Extra));
            }

            MinimumText = Num(spec.Minimum, spec.FloatKeys.Contains(SettingsTemplateJson.MinimumKey));
            MaximumText = Num(spec.Maximum, spec.FloatKeys.Contains(SettingsTemplateJson.MaximumKey));
            SnapText = Num(spec.SnapInterval, spec.FloatKeys.Contains(SettingsTemplateJson.SnapIntervalKey));
            this.Format = string.IsNullOrEmpty(spec.Format) ? "{{value}}" : spec.Format;
            WidthText = spec.Width > 0 ? spec.Width.ToString(CultureInfo.InvariantCulture) : string.Empty;
            CanManualInput = spec.CanManualInput;
            DivisionStepText = Num(spec.DivisionStep);
            MinRangeDistanceText = Num(spec.MinRangeDistance);
            DivisionLabelStepText = Num(spec.DivisionLabelStep);
            DivisionLabelPostfix = spec.DivisionLabelPostfix;

            LoadValue(spec, keys);

            // Редактор пунктов правит ВЫБРАННЫЙ пункт: без этого после импорта поля
            // «текст пункта» стояли бы пустыми, пока пользователь не ткнёт в список.
            SelectedOption = Options.FirstOrDefault();
        }
        finally
        {
            _muted = false;
        }

        OnPropertyChanged(string.Empty);
        _changed();
    }

    private void LoadValue(SettingsControlSpec spec, IReadOnlyList<SettingsKeyCode> keys)
    {
        switch (Type)
        {
            case SettingsControlType.CheckBox:
                BoolValue = spec.Value switch
                {
                    bool b => b,
                    double d => d != 0,
                    _ => false
                };
                break;

            case SettingsControlType.RadioButtonGroup:
            case SettingsControlType.Dropdown:
                IndexValue = spec.Value switch
                {
                    double d => (int)d,
                    int i => i,
                    _ => 0
                };
                break;

            case SettingsControlType.Slider:
            case SettingsControlType.NumericStepper:
                NumberText = spec.Value switch
                {
                    double d => Num(d, spec.FloatKeys.Contains(SettingsTemplateJson.ValueKey)),
                    int i => i.ToString(CultureInfo.InvariantCulture),
                    string s => s,
                    _ => "0"
                };
                break;

            case SettingsControlType.TextInput:
                StringValue = spec.Value as string ?? string.Empty;
                break;

            case SettingsControlType.ColorChoice:
                ColorValue = (spec.Value as string ?? "FFCC00").TrimStart('#').ToUpperInvariant();
                break;

            case SettingsControlType.HotKey:
                LoadHotKey(spec.Value, keys);
                break;

            case SettingsControlType.RangeSlider:
                if (spec.Value is IReadOnlyList<double> pair && pair.Count >= 2)
                {
                    bool asFloat = spec.FloatKeys.Contains(SettingsTemplateJson.ValueKey);
                    RangeLowText = Num(pair[0], asFloat);
                    RangeHighText = Num(pair[1], asFloat);
                }

                break;
        }
    }

    private void LoadHotKey(object? value, IReadOnlyList<SettingsKeyCode> keys)
    {
        IReadOnlyList<int> codes = value switch
        {
            int[] array => array,
            IReadOnlyList<int> list => list,
            _ => Array.Empty<int>()
        };

        CtrlKey = codes.Contains(SettingsHotKey.Ctrl);
        AltKey = codes.Contains(SettingsHotKey.Alt);
        ShiftKey = codes.Contains(SettingsHotKey.Shift);

        int main = codes.FirstOrDefault(c => !SettingsHotKey.IsModifier(c));
        MainKey = main == 0 ? null : keys.FirstOrDefault(k => k.Code == main);
    }

    private void LoadTooltip(string tooltip)
    {
        TooltipHeader = string.Empty;
        TooltipBody = string.Empty;
        TooltipNote = string.Empty;
        TooltipAttention = string.Empty;
        TooltipRaw = string.Empty;
        RawTooltip = false;

        if (string.IsNullOrEmpty(tooltip))
        {
            return;
        }

        MatchCollection matches = TooltipSection.Matches(tooltip);
        string rest = TooltipSection.Replace(tooltip, string.Empty).Trim();

        if (matches.Count == 0 || rest.Length > 0)
        {
            // Подсказка не укладывается в четыре секции — показываем как есть,
            // чтобы при пересохранении не потерять ни куска чужого текста.
            RawTooltip = true;
            TooltipRaw = tooltip;
            return;
        }

        foreach (Match match in matches)
        {
            string text = match.Groups["text"].Value;
            switch (match.Groups["tag"].Value)
            {
                case "HEADER": TooltipHeader = text; break;
                case "BODY": TooltipBody = text; break;
                case "NOTE": TooltipNote = text; break;
                case "ATTENTION": TooltipAttention = text; break;
            }
        }
    }

    /// <summary>
    /// Число в поле ввода. <paramref name="asFloat"/> сохраняет хвост <c>.0</c> у целого по
    /// значению: без него импортированный ползунок 0.8…3.0 показал бы максимум как «3»,
    /// и при следующем сохранении дробность потерялась бы вместе с текстом.
    /// </summary>
    private static string Num(double value, bool asFloat = false)
    {
        if (Math.Abs(value - Math.Round(value)) >= 1e-9)
        {
            return value.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        string whole = ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        return asFloat ? whole + ".0" : whole;
    }

    // =================================================================== умолчания

    private void ApplyDefaults()
    {
        _muted = true;
        try
        {
            switch (Type)
            {
                case SettingsControlType.Label:
                    Text = "Заголовок раздела";
                    break;

                case SettingsControlType.Empty:
                    break;

                case SettingsControlType.CheckBox:
                    Text = "Новая галочка";
                    BoolValue = true;
                    break;

                case SettingsControlType.RadioButtonGroup:
                    Text = "Режим работы";
                    Options.Add(new SettingOptionViewModel("Вариант 1", string.Empty, _changed));
                    Options.Add(new SettingOptionViewModel("Вариант 2", string.Empty, _changed));
                    SelectedOption = Options[0];
                    break;

                case SettingsControlType.Dropdown:
                    Text = "Выбор";
                    Options.Add(new SettingOptionViewModel("Вариант 1", string.Empty, _changed));
                    Options.Add(new SettingOptionViewModel("Вариант 2", string.Empty, _changed));
                    SelectedOption = Options[0];
                    break;

                case SettingsControlType.Slider:
                    Text = "Величина";
                    NumberText = "50";
                    MinimumText = "0";
                    MaximumText = "100";
                    SnapText = "1";
                    break;

                case SettingsControlType.TextInput:
                    Text = "Текст";
                    StringValue = string.Empty;
                    break;

                case SettingsControlType.NumericStepper:
                    Text = "Число";
                    NumberText = "1";
                    MinimumText = "0";
                    MaximumText = "10";
                    SnapText = "1";
                    break;

                case SettingsControlType.HotKey:
                    Text = "Горячая клавиша";
                    break;

                case SettingsControlType.ColorChoice:
                    Text = "Цвет";
                    ColorValue = "FFCC00";
                    break;

                case SettingsControlType.RangeSlider:
                    Text = "Диапазон";
                    MinimumText = "0";
                    MaximumText = "100";
                    SnapText = "1";
                    RangeLowText = "20";
                    RangeHighText = "80";
                    break;
            }
        }
        finally
        {
            _muted = false;
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_muted)
        {
            return;
        }

        // Замечания пишутся отдельным методом, поэтому петли «проверка меняет свойство,
        // свойство запускает проверку» здесь нет.
        _changed();
    }
}

/// <summary>Строка палитры: тип контрола, который можно положить в форму.</summary>
/// <param name="Type">Тип.</param>
public sealed record SettingsPaletteItem(SettingsControlType Type)
{
    /// <summary>Имя по-русски.</summary>
    public string Title => SettingsControlSchema.DisplayName(Type);

    /// <summary>Строка <c>type</c> шаблона.</summary>
    public string TypeName => Type.ToString();

    /// <summary>Одна строка про тип.</summary>
    public string Summary => SettingsControlSchema.Summary(Type);

    /// <summary>Сколько таких контролов в живых шаблонах, по которым сверялся кокпит при разработке.</summary>
    public int LiveSamples => SettingsControlSchema.LiveSamples(Type);

    /// <summary>Живых образцов нет: тип восстановлен по коду API и в бою не проверен.</summary>
    public bool IsUnproven => LiveSamples == 0;

    /// <summary>Пометка о проверенности для палитры.</summary>
    public string LiveBadge => LiveSamples > 0
        ? "живых: " + LiveSamples.ToString(CultureInfo.InvariantCulture)
        : "нет живых образцов";

    /// <summary>Подсказка строки палитры.</summary>
    public string ToolTipText => IsUnproven
        ? $"{Summary}\n\ntype = {TypeName}\nВ 13 живых шаблонах, по которым сверялся кокпит, такого контрола нет: "
          + "тип восстановлен по коду ModsSettingsAPI и в бою не проверен."
        : $"{Summary}\n\ntype = {TypeName}\nВ живых шаблонах встречается {LiveSamples} раз.";
}
