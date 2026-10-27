using System.Text.RegularExpressions;

namespace WotModCockpit.Core.Settings;

/// <summary>Насколько серьёзна находка валидатора шаблона.</summary>
public enum TemplateIssueSeverity
{
    /// <summary>К сведению: форма соберётся и будет работать.</summary>
    Info = 0,

    /// <summary>Форма соберётся, но поведёт себя не так, как ждёт автор.</summary>
    Warning = 1,

    /// <summary>Окно настроек сломается или контрол не появится. Собирать мод нельзя.</summary>
    Error = 2
}

/// <summary>Одна находка валидатора.</summary>
/// <param name="Severity">Серьёзность.</param>
/// <param name="Code">Код правила, <c>MSA001</c>…</param>
/// <param name="Path">Где: <c>column1[2]</c>, <c>column2[0].options[3]</c> или пусто для уровня шаблона.</param>
/// <param name="Message">Что произойдёт у игрока.</param>
public sealed record TemplateIssue(TemplateIssueSeverity Severity, string Code, string Path, string Message)
{
    /// <summary>Как чинить.</summary>
    public string? Hint { get; init; }

    /// <summary>Ключ настройки, к которому относится находка.</summary>
    public string? VarName { get; init; }

    /// <inheritdoc/>
    public override string ToString() =>
        Path.Length > 0 ? $"{Code} {Severity} {Path}: {Message}" : $"{Code} {Severity}: {Message}";
}

/// <summary>Итог проверки шаблона.</summary>
/// <param name="Issues">Находки в порядке обхода.</param>
public sealed record TemplateValidationResult(IReadOnlyList<TemplateIssue> Issues)
{
    /// <summary>Пусто.</summary>
    public static TemplateValidationResult Empty { get; } = new(Array.Empty<TemplateIssue>());

    /// <summary>Сколько ошибок.</summary>
    public int ErrorCount => Issues.Count(i => i.Severity == TemplateIssueSeverity.Error);

    /// <summary>Сколько предупреждений.</summary>
    public int WarningCount => Issues.Count(i => i.Severity == TemplateIssueSeverity.Warning);

    /// <summary>Сколько замечаний.</summary>
    public int InfoCount => Issues.Count(i => i.Severity == TemplateIssueSeverity.Info);

    /// <summary>Есть ли хоть одна ошибка.</summary>
    public bool HasErrors => ErrorCount > 0;

    /// <summary>Есть ли находка с таким кодом.</summary>
    /// <param name="code">Код.</param>
    /// <returns><c>true</c>, если есть.</returns>
    public bool Has(string code) => Issues.Any(i => string.Equals(i.Code, code, StringComparison.Ordinal));

    /// <summary>Все находки одной строкой — для сообщений об ошибках в тестах.</summary>
    /// <returns>Текст.</returns>
    public string Describe() => string.Join(Environment.NewLine, Issues.Select(i => i.ToString()));
}

/// <summary>Числовые пределы формы, взятые из геометрии окна ModsSettingsAPI.</summary>
public static class TemplateLimits
{
    /// <summary>
    /// Сколько пикселей приходится на одну колонку: блок мода 900 px
    /// (<c>MOD_COMPONENT_WIDTH</c>), вторая колонка начинается на x=450, левый отступ 20.
    /// </summary>
    public const int ColumnWidthPx = 430;

    /// <summary>Разумный максимум <c>width</c> у Dropdown/Slider/TextInput. Живой максимум — 310.</summary>
    public const int MaxControlWidth = 400;

    /// <summary>Подпись контрола. Живой максимум — 55 символов; 90 уже гарантированно переносится.</summary>
    public const int MaxTextLength = 90;

    /// <summary>Заголовок блока мода.</summary>
    public const int MaxDisplayNameLength = 80;

    /// <summary>Подпись варианта выбора.</summary>
    public const int MaxOptionLabelLength = 90;

    /// <summary>Ключ настройки.</summary>
    public const int MaxVarNameLength = 64;

    /// <summary>Ключ мода.</summary>
    public const int MaxLinkageLength = 100;
}

/// <summary>Коды правил валидатора. Вынесены отдельно, чтобы тесты не сверялись со строками текста.</summary>
public static class TemplateRules
{
    /// <summary>linkage пуст, не ASCII или содержит пробел.</summary>
    public const string BadLinkage = "MSA001";

    /// <summary>Пустой modDisplayName.</summary>
    public const string EmptyDisplayName = "MSA002";

    /// <summary>Слишком длинный modDisplayName.</summary>
    public const string LongDisplayName = "MSA003";

    /// <summary>Нет settingsVersion.</summary>
    public const string NoSettingsVersion = "MSA004";

    /// <summary>Отрицательный settingsVersion.</summary>
    public const string BadSettingsVersion = "MSA005";

    /// <summary>Обе колонки пусты.</summary>
    public const string EmptyForm = "MSA006";

    /// <summary>Посторонний ключ верхнего уровня.</summary>
    public const string ExtraTemplateKey = "MSA007";

    /// <summary>Тип контрола не входит в 11 типов API.</summary>
    public const string UnknownType = "MSA010";

    /// <summary>Пустая подпись.</summary>
    public const string EmptyText = "MSA011";

    /// <summary>Слишком длинная подпись.</summary>
    public const string LongText = "MSA012";

    /// <summary>Нет varName у контрола, который обязан его иметь.</summary>
    public const string MissingVarName = "MSA013";

    /// <summary>varName — не ASCII-идентификатор.</summary>
    public const string BadVarName = "MSA014";

    /// <summary>varName занят самим API.</summary>
    public const string ReservedVarName = "MSA015";

    /// <summary>Два контрола с одним varName.</summary>
    public const string DuplicateVarName = "MSA016";

    /// <summary>У Label или Empty есть varName либо value.</summary>
    public const string LabelWithValue = "MSA017";

    /// <summary>Нет value у контрола, который обязан его иметь.</summary>
    public const string MissingValue = "MSA018";

    /// <summary>Тип значения не тот, который вернёт окно.</summary>
    public const string ValueTypeMismatch = "MSA019";

    /// <summary>Пустой список options.</summary>
    public const string NoOptions = "MSA020";

    /// <summary>Индекс value вне списка options.</summary>
    public const string OptionIndexOutOfRange = "MSA021";

    /// <summary>Пустая подпись варианта.</summary>
    public const string EmptyOptionLabel = "MSA022";

    /// <summary>У Dropdown больше 9 вариантов — появится прокрутка.</summary>
    public const string DropdownScroll = "MSA023";

    /// <summary>minimum не меньше maximum.</summary>
    public const string BadRange = "MSA024";

    /// <summary>snapInterval не положителен.</summary>
    public const string BadSnapInterval = "MSA025";

    /// <summary>snapInterval больше самого диапазона.</summary>
    public const string CoarseSnapInterval = "MSA026";

    /// <summary>value вне диапазона.</summary>
    public const string ValueOutOfRange = "MSA027";

    /// <summary>В format нет плейсхолдера.</summary>
    public const string FormatWithoutPlaceholder = "MSA028";

    /// <summary>Цвет записан не шестью hex-цифрами.</summary>
    public const string BadColor = "MSA029";

    /// <summary>В горячей клавише запрещённый код.</summary>
    public const string ExcludedKey = "MSA030";

    /// <summary>Горячая клавиша записана легаси-формой вложенных списков.</summary>
    public const string LegacyHotKey = "MSA031";

    /// <summary>Код клавиши вне диапазона Keys.py.</summary>
    public const string BadKeyCode = "MSA032";

    /// <summary>Кнопка у типа, который её не читает.</summary>
    public const string ButtonNotSupported = "MSA033";

    /// <summary>В кнопке одновременно текст и иконка.</summary>
    public const string ButtonTextAndIcon = "MSA034";

    /// <summary>Ключ itemRenderer, которого не кладёт ни одна фабрика API.</summary>
    public const string DeadItemRenderer = "MSA035";

    /// <summary>Посторонний ключ у контрола.</summary>
    public const string ExtraControlKey = "MSA036";

    /// <summary>width у типа, который его не читает.</summary>
    public const string WidthNotSupported = "MSA037";

    /// <summary>width шире колонки.</summary>
    public const string WidthTooLarge = "MSA038";

    /// <summary>Тип, у которого нет живых образцов.</summary>
    public const string UnprovenType = "MSA039";

    /// <summary>Диапазон RangeSlider уже, чем minRangeDistance.</summary>
    public const string RangeTooNarrow = "MSA040";

    /// <summary>options у типа, который их не читает.</summary>
    public const string OptionsNotSupported = "MSA041";
}

/// <summary>
/// Проверка шаблона перед генерацией кода.
///
/// <para>Существует потому, что <b>сам ModsSettingsAPI не проверяет НИЧЕГО</b>: он принимает
/// любой словарь, а последствия видны только в игре — контрол не появился, «Применить»
/// не работает, окно настроек не открылось вообще ни у одного мода. Каждое правило здесь
/// отвечает на вопрос «что увидит игрок», а не «красиво ли это».</para>
/// </summary>
public static class TemplateValidator
{
    private static readonly Regex VarNamePattern =
        new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ColorPattern =
        new(@"^[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Проверить шаблон целиком.</summary>
    /// <param name="template">Шаблон.</param>
    /// <returns>Все находки.</returns>
    public static TemplateValidationResult Validate(SettingsTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var issues = new List<TemplateIssue>();

        ValidateHeader(template, issues);

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        ValidateColumn(template.Column1, TemplateJson.Column1Key, seen, issues);
        ValidateColumn(template.Column2, TemplateJson.Column2Key, seen, issues);

        return new TemplateValidationResult(issues);
    }

    // ------------------------------------------------------------------ шапка

    private static void ValidateHeader(SettingsTemplate template, List<TemplateIssue> issues)
    {
        if (!IsUsableLinkage(template.Linkage))
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Error, TemplateRules.BadLinkage, string.Empty,
                template.Linkage.Length == 0
                    ? "не задан linkage — ключ, под которым API хранит настройки мода"
                    : $"linkage '{template.Linkage}' содержит не-ASCII символы, пробел или слишком длинный")
            {
                Hint = "только ASCII без пробелов, например 'ru.local.my_mod'. " +
                       "generateSettingsData делает sorted(templates, key=str.lower), и на unicode-строке " +
                       "str.lower в Python 2 бросает TypeError — окно настроек не откроется НИ У ОДНОГО мода"
            });
        }

        if (string.IsNullOrWhiteSpace(template.ModDisplayName))
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Error, TemplateRules.EmptyDisplayName, string.Empty,
                "пустой modDisplayName: блок мода в окне настроек будет без заголовка")
            {
                Hint = "задайте название мода — его видит игрок в списке"
            });
        }
        else if (template.ModDisplayName.Length > TemplateLimits.MaxDisplayNameLength)
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Warning, TemplateRules.LongDisplayName, string.Empty,
                $"заголовок мода длиной {template.ModDisplayName.Length} символов не влезет в шапку блока " +
                $"(разумный предел {TemplateLimits.MaxDisplayNameLength})"));
        }

        if (template.SettingsVersion is null)
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Warning, TemplateRules.NoSettingsVersion, string.Empty,
                "нет settingsVersion: API будет сравнивать шаблоны как ТЕКСТ JSON, и правка одной буквы " +
                "в подписи молча сбросит настройки игрока")
            {
                Hint = "проставьте settingsVersion и поднимайте его при изменении состава контролов"
            });
        }
        else if (template.SettingsVersion < 0)
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Error, TemplateRules.BadSettingsVersion, string.Empty,
                $"settingsVersion = {template.SettingsVersion}: версия сравнивается как число, " +
                "отрицательная не даст обновить форму никогда"));
        }

        if (template.ControlCount == 0)
        {
            // Форма из одного тумблера ЗАКОННА: api.getSettingsFromTemplate читает 'enabled'
            // ДО колонок, окно рисует блок мода с тумблером, и мод «включить/выключить одной
            // галочкой» — обычное дело. Ошибка только когда тумблера нет: вот тогда блок
            // действительно пуст и генерировать нечего.
            issues.Add(template.HasEnabled
                ? new TemplateIssue(TemplateIssueSeverity.Warning, TemplateRules.EmptyForm, string.Empty,
                    "обе колонки пусты: у мода в окне будет только тумблер «вкл/выкл»")
                {
                    Hint = "это рабочая форма — 'enabled' попадает в настройки и без колонок"
                }
                : new TemplateIssue(TemplateIssueSeverity.Error, TemplateRules.EmptyForm, string.Empty,
                    "обе колонки пусты и тумблера нет: блок мода появится в окне пустым"));
        }

        foreach (string key in template.ExtraOrder)
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Warning, TemplateRules.ExtraTemplateKey, string.Empty,
                $"посторонний ключ верхнего уровня '{key}': API его не понимает, но включает в сравнение шаблонов")
            {
                Hint = "без settingsVersion меняющееся значение такого ключа сбрасывает настройки игрока " +
                       "на КАЖДОМ запуске (так бывает с ключом вроде 'random', который мод перезаписывает сам)"
            });
        }
    }

    private static bool IsUsableLinkage(string linkage)
    {
        if (linkage.Length == 0 || linkage.Length > TemplateLimits.MaxLinkageLength) return false;
        foreach (char c in linkage)
            if (c <= ' ' || c > '~')
                return false;
        return true;
    }

    // ------------------------------------------------------------------ колонка

    private static void ValidateColumn(
        IList<SettingControl> column, string columnKey,
        Dictionary<string, string> seen, List<TemplateIssue> issues)
    {
        for (int i = 0; i < column.Count; i++)
            ValidateControl(column[i], $"{columnKey}[{i}]", seen, issues);
    }

    private static void ValidateControl(
        SettingControl control, string path,
        Dictionary<string, string> seen, List<TemplateIssue> issues)
    {
        void Add(TemplateIssueSeverity severity, string code, string message, string? hint = null) =>
            issues.Add(new TemplateIssue(severity, code, path, message) { Hint = hint, VarName = control.VarName });

        if (control.Kind == ControlKind.Unknown)
        {
            Add(TemplateIssueSeverity.Error, TemplateRules.UnknownType,
                $"тип '{control.RawTypeName ?? "<нет>"}' не входит в 11 типов ModsSettingsAPI: окно напишет в лог " +
                "'Unexpected type of component' и подставит пустышку",
                control.VarName is { Length: > 0 }
                    ? "у пустышки нет returnValue, поэтому кнопка «Применить» СЛОМАЕТСЯ целиком"
                    : "выберите один из 11 поддержанных типов");
            return;
        }

        ValidateTextAndVar(control, path, seen, issues, Add);
        ValidateValue(control, Add);
        ValidateOptions(control, path, issues, Add);
        ValidateRange(control, Add);
        ValidateWidthAndButton(control, Add);
        ValidateExtras(control, Add);

        if (!ControlKinds.ProvenInLiveTemplates(control.Kind) &&
            control.Kind is not (ControlKind.Label or ControlKind.Empty))
        {
            Add(TemplateIssueSeverity.Info, TemplateRules.UnprovenType,
                $"тип {ControlKinds.WireName(control.Kind)} восстановлен по коду API, но не встречается " +
                "ни в одном из 13 живых шаблонов, по которым сверялся кокпит при разработке",
                "проверьте его в игре отдельно, прежде чем полагаться на него");
        }
    }

    private static void ValidateTextAndVar(
        SettingControl control, string path,
        Dictionary<string, string> seen, List<TemplateIssue> issues,
        Action<TemplateIssueSeverity, string, string, string?> add)
    {
        if (ControlKinds.HasText(control.Kind))
        {
            if (string.IsNullOrWhiteSpace(control.Text))
            {
                add(TemplateIssueSeverity.Error, TemplateRules.EmptyText,
                    $"у {ControlKinds.DisplayName(control.Kind)} нет подписи: игрок не поймёт, что это за настройка",
                    control.Kind is ControlKind.Dropdown or ControlKind.Slider or ControlKind.TextInput
                        ? "пустая подпись у этих типов допустима технически — контрол просто поднимется на y=0, " +
                          "но настройка останется безымянной"
                        : null);
            }
            else if (control.Text.Length > TemplateLimits.MaxTextLength)
            {
                add(TemplateIssueSeverity.Warning, TemplateRules.LongText,
                    $"подпись длиной {control.Text.Length} символов не влезет в колонку " +
                    $"(~{TemplateLimits.ColumnWidthPx} px, живой максимум — 55 символов)", null);
            }
        }

        if (!ControlKinds.HasValue(control.Kind))
        {
            if (control.VarName is { Length: > 0 } || control.Value is not null)
            {
                add(TemplateIssueSeverity.Error, TemplateRules.LabelWithValue,
                    $"у {ControlKinds.DisplayName(control.Kind)} не должно быть ни varName, ни value",
                    "getConfigData в окне читает returnValue у КАЖДОГО компонента с varName, " +
                    "а у подписи его нет — кнопка «Применить» сломается");
            }
            return;
        }

        string? varName = control.VarName;
        if (string.IsNullOrEmpty(varName))
        {
            add(TemplateIssueSeverity.Error, TemplateRules.MissingVarName,
                "нет varName: значение контрола некуда сохранять, в словаре настроек его не будет", null);
            return;
        }

        if (string.Equals(varName, TemplateJson.EnabledKey, StringComparison.Ordinal))
        {
            add(TemplateIssueSeverity.Error, TemplateRules.ReservedVarName,
                "varName 'enabled' занят самим API: getSettingsFromTemplate кладёт его первым, " +
                "и ваше значение будет затёрто тумблером мода", "переименуйте, например в 'isEnabled'");
        }
        else if (!VarNamePattern.IsMatch(varName) || varName.Length > TemplateLimits.MaxVarNameLength)
        {
            add(TemplateIssueSeverity.Error, TemplateRules.BadVarName,
                $"varName '{varName}' — не ASCII-идентификатор: это ключ словаря Python 2 и ключ JSON", null);
        }

        if (seen.TryGetValue(varName, out string? firstPath))
        {
            issues.Add(new TemplateIssue(TemplateIssueSeverity.Error, TemplateRules.DuplicateVarName, path,
                $"varName '{varName}' уже занят контролом {firstPath}: в словаре настроек останется " +
                "только одно значение, второй контрол будет читать чужое")
            { VarName = varName });
        }
        else
        {
            seen[varName] = path;
        }
    }

    private static void ValidateValue(
        SettingControl control, Action<TemplateIssueSeverity, string, string, string?> add)
    {
        if (!ControlKinds.HasValue(control.Kind)) return;

        SettingValue? value = control.Value;
        if (value is null)
        {
            add(TemplateIssueSeverity.Error, TemplateRules.MissingValue,
                $"у {ControlKinds.DisplayName(control.Kind)} нет value: " +
                "getSettingsFromTemplate берёт пару varName->value и без value контрол в настройки не попадёт", null);
            return;
        }

        switch (control.Kind)
        {
            case ControlKind.CheckBox when value.Kind != SettingValueKind.Bool:
                add(TemplateIssueSeverity.Warning, TemplateRules.ValueTypeMismatch,
                    $"value галочки записан как {value.Kind}, а окно вернёт bool: " +
                    "после первого «Применить» тип значения поменяется", "пишите True или False");
                break;

            case ControlKind.RadioButtonGroup or ControlKind.Dropdown when !value.IsNumeric:
                add(TemplateIssueSeverity.Error, TemplateRules.ValueTypeMismatch,
                    $"value обязан быть ИНДЕКСОМ варианта (число), а не {value.Kind}", null);
                break;

            case ControlKind.Slider or ControlKind.NumericStepper when !value.IsNumeric:
                add(TemplateIssueSeverity.Error, TemplateRules.ValueTypeMismatch,
                    $"value обязан быть числом, а не {value.Kind}", null);
                break;

            case ControlKind.TextInput when value.Kind != SettingValueKind.String:
                add(TemplateIssueSeverity.Error, TemplateRules.ValueTypeMismatch,
                    $"value строки ввода обязан быть строкой, а не {value.Kind}", null);
                break;

            case ControlKind.ColorChoice:
                if (value.Kind != SettingValueKind.String)
                {
                    add(TemplateIssueSeverity.Error, TemplateRules.ValueTypeMismatch,
                        $"value выбора цвета обязан быть строкой hex, а не {value.Kind}", null);
                }
                else if (!ColorPattern.IsMatch(value.AsString ?? string.Empty))
                {
                    add(TemplateIssueSeverity.Error, TemplateRules.BadColor,
                        $"цвет '{value.AsString}' записан не шестью hex-цифрами без решётки",
                        "окно возвращает getPixel().toString(16) — строчными и БЕЗ ведущих нулей " +
                        "(чёрный придёт как '0'), поэтому мод обязан нормализовать значение сам");
                }
                break;

            case ControlKind.HotKey:
                ValidateHotKey(value, add);
                break;

            case ControlKind.RangeSlider:
                if (value.Kind != SettingValueKind.List || value.Items.Count != 2 || !value.Items.All(i => i.IsNumeric))
                {
                    add(TemplateIssueSeverity.Error, TemplateRules.ValueTypeMismatch,
                        "value диапазонного ползунка обязан быть списком из двух чисел [слева, справа]", null);
                }
                else if (control.MinRangeDistance is { IsNumeric: true } minRange &&
                         value.Items[1].AsNumber - value.Items[0].AsNumber < minRange.AsNumber)
                {
                    add(TemplateIssueSeverity.Warning, TemplateRules.RangeTooNarrow,
                        $"выделенный диапазон уже, чем minRangeDistance = {minRange}", null);
                }
                break;
        }
    }

    private static void ValidateHotKey(
        SettingValue value, Action<TemplateIssueSeverity, string, string, string?> add)
    {
        if (value.Kind != SettingValueKind.List)
        {
            add(TemplateIssueSeverity.Error, TemplateRules.ValueTypeMismatch,
                $"value горячей клавиши обязан быть СПИСКОМ кодов, а не {value.Kind}",
                "клавиши в списке должны быть нажаты одновременно: checkKeyset = all(isKeyDown)");
            return;
        }

        if (HotKeyValue.IsLegacyNested(value))
        {
            add(TemplateIssueSeverity.Warning, TemplateRules.LegacyHotKey,
                $"горячая клавиша записана легаси-формой с вложенными списками: {value}",
                "API сплющит её сам (_migrateKeys), но писать надо плоско со спецкодами: " +
                "-1 ALT, -2 CTRL, -3 SHIFT — например [61, -2] вместо [61, [29, 157]]");
        }

        foreach (int code in HotKeyValue.Flatten(value))
        {
            if (HotKeyValue.Excluded.Contains(code))
            {
                add(TemplateIssueSeverity.Error, TemplateRules.ExcludedKey,
                    $"код {code} входит в EXCLUDED_KEYS: API не даст игроку назначить его, " +
                    "и переназначить клавишу будет нечем",
                    "запрещены 0 KEY_NONE, 28 KEY_RETURN, 256 ЛКМ, 257 ПКМ, 258 СКМ");
            }
            else if (code < HotKeyValue.Shift || code > HotKeyValue.MaxKeyCode)
            {
                add(TemplateIssueSeverity.Error, TemplateRules.BadKeyCode,
                    $"код {code} вне диапазона Keys.py (спецкоды -1..-3, обычные 1..{HotKeyValue.MaxKeyCode})", null);
            }
        }
    }

    private static void ValidateOptions(
        SettingControl control, string path, List<TemplateIssue> issues,
        Action<TemplateIssueSeverity, string, string, string?> add)
    {
        if (!ControlKinds.HasOptions(control.Kind))
        {
            if (control.Options.Count > 0)
                add(TemplateIssueSeverity.Warning, TemplateRules.OptionsNotSupported,
                    $"{ControlKinds.DisplayName(control.Kind)} не читает options — список вариантов будет проигнорирован",
                    "options есть только у RadioButtonGroup и Dropdown");
            return;
        }

        if (control.Options.Count == 0)
        {
            add(TemplateIssueSeverity.Error, TemplateRules.NoOptions,
                "пустой список options: выбирать будет не из чего, а value-индекс укажет в никуда", null);
            return;
        }

        if (control.Value is { IsNumeric: true } value)
        {
            long index = value.AsInteger;
            if (index < 0 || index >= control.Options.Count)
            {
                add(TemplateIssueSeverity.Error, TemplateRules.OptionIndexOutOfRange,
                    $"value = {index}, а вариантов {control.Options.Count}: value — это ИНДЕКС в options, " +
                    $"допустимо 0..{control.Options.Count - 1}", null);
            }
        }

        for (int i = 0; i < control.Options.Count; i++)
        {
            ControlOption option = control.Options[i];
            if (string.IsNullOrWhiteSpace(option.Label))
            {
                issues.Add(new TemplateIssue(TemplateIssueSeverity.Warning, TemplateRules.EmptyOptionLabel,
                    $"{path}.options[{i}]", "вариант без подписи: окно нарисует пустую строку")
                { VarName = control.VarName });
            }
            else if (option.Label.Length > TemplateLimits.MaxOptionLabelLength)
            {
                issues.Add(new TemplateIssue(TemplateIssueSeverity.Warning, TemplateRules.EmptyOptionLabel,
                    $"{path}.options[{i}]",
                    $"подпись варианта длиной {option.Label.Length} символов не влезет в колонку")
                { VarName = control.VarName });
            }
        }

        if (control.Kind == ControlKind.Dropdown && control.Options.Count > ControlKinds.DropdownScrollLimit)
        {
            add(TemplateIssueSeverity.Info, TemplateRules.DropdownScroll,
                $"вариантов {control.Options.Count}: после {ControlKinds.DropdownScrollLimit} у списка " +
                "появляется полоса прокрутки (SCROLL_ITEM_LIMIT)", null);
        }
    }

    private static void ValidateRange(
        SettingControl control, Action<TemplateIssueSeverity, string, string, string?> add)
    {
        if (!ControlKinds.HasRange(control.Kind)) return;

        SettingValue? min = control.Minimum;
        SettingValue? max = control.Maximum;
        SettingValue? snap = control.SnapInterval;

        if (min is not { IsNumeric: true } || max is not { IsNumeric: true })
        {
            add(TemplateIssueSeverity.Error, TemplateRules.BadRange,
                "не заданы числовые minimum и maximum: окно не сможет построить шкалу", null);
            return;
        }

        if (min.AsNumber >= max.AsNumber)
        {
            add(TemplateIssueSeverity.Error, TemplateRules.BadRange,
                $"minimum {min} не меньше maximum {max}: ползунок будет неподвижен", null);
        }

        if (snap is not { IsNumeric: true } || snap.AsNumber <= 0d)
        {
            add(TemplateIssueSeverity.Error, TemplateRules.BadSnapInterval,
                $"snapInterval = {snap?.ToString() ?? "<нет>"}: шаг обязан быть положительным числом, " +
                "иначе значение не будет меняться", null);
        }
        else if (snap.AsNumber > max.AsNumber - min.AsNumber)
        {
            add(TemplateIssueSeverity.Warning, TemplateRules.CoarseSnapInterval,
                $"шаг {snap} больше всего диапазона {min}..{max}: доступными окажутся только края", null);
        }

        if (control.Kind != ControlKind.RangeSlider && control.Value is { IsNumeric: true } value &&
            (value.AsNumber < min.AsNumber || value.AsNumber > max.AsNumber))
        {
            add(TemplateIssueSeverity.Error, TemplateRules.ValueOutOfRange,
                $"value {value} вне диапазона {min}..{max}", null);
        }

        if (control.Kind == ControlKind.Slider)
        {
            string format = control.Format ?? ControlKinds.SliderValuePlaceholder;
            if (!format.Contains(ControlKinds.SliderValuePlaceholder, StringComparison.Ordinal))
            {
                add(TemplateIssueSeverity.Warning, TemplateRules.FormatWithoutPlaceholder,
                    $"в format '{format}' нет плейсхолдера {ControlKinds.SliderValuePlaceholder}: " +
                    "метка справа от ползунка покажет эту строку как есть и не будет меняться", null);
            }
        }
    }

    private static void ValidateWidthAndButton(
        SettingControl control, Action<TemplateIssueSeverity, string, string, string?> add)
    {
        if (control.Width is int width)
        {
            if (!ControlKinds.HasWidth(control.Kind))
            {
                add(TemplateIssueSeverity.Warning, TemplateRules.WidthNotSupported,
                    $"{ControlKinds.DisplayName(control.Kind)} не читает width — окно возьмёт свою ширину",
                    "width поддержан только у Dropdown, Slider и TextInput");
            }
            else if (width <= 0 || width > TemplateLimits.MaxControlWidth)
            {
                add(TemplateIssueSeverity.Warning, TemplateRules.WidthTooLarge,
                    $"width = {width} при ширине колонки около {TemplateLimits.ColumnWidthPx} px " +
                    $"(разумный предел {TemplateLimits.MaxControlWidth}, живой максимум 310)", null);
            }
        }

        if (control.Button is not ControlButton button) return;

        if (!ControlKinds.HasButton(control.Kind))
        {
            add(TemplateIssueSeverity.Error, TemplateRules.ButtonNotSupported,
                $"{ControlKinds.DisplayName(control.Kind)} не читает button: кнопка не появится, " +
                "а обработчик не будет вызван никогда",
                "button работает только у CheckBox, RadioButtonGroup, Dropdown и Slider");
        }

        if (button.Text is { Length: > 0 } && button.IconSource is { Length: > 0 })
        {
            add(TemplateIssueSeverity.Warning, TemplateRules.ButtonTextAndIcon,
                "в кнопке заданы и text, и iconSource: окно нарисует текстовую кнопку, иконка пропадёт",
                "createButton кладёт iconSource, ТОЛЬКО если text не задан — оставьте что-то одно");
        }
    }

    private static void ValidateExtras(
        SettingControl control, Action<TemplateIssueSeverity, string, string, string?> add)
    {
        foreach (string key in control.ExtraOrder)
        {
            if (string.Equals(key, TemplateJson.ItemRendererKey, StringComparison.Ordinal))
            {
                add(TemplateIssueSeverity.Warning, TemplateRules.DeadItemRenderer,
                    "itemRenderer: ни одна фабрика templates.py этот ключ не кладёт, а рендерер " +
                    "окно берёт само — App.utils.classFactory.getClass('DropDownListItemRendererSound'). " +
                    "В пуле строк окна 'itemRenderer' встречается ровно один раз, рядом с именем " +
                    "этого класса",
                    "сам кокпит такой ключ не порождает; если он пришёл импортом — он сохраняется " +
                    "как есть, потому что участвует в сравнении шаблонов");
                continue;
            }
            add(TemplateIssueSeverity.Info, TemplateRules.ExtraControlKey,
                $"посторонний ключ '{key}': окно его не читает, но он участвует в сравнении шаблонов", null);
        }
    }
}
