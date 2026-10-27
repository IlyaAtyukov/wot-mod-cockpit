using System.Text.Json;

namespace WotModCockpit.Core.Settings;

/// <summary>
/// Перевод шаблона между моделью кокпита и той формой словаря, в которой его понимает
/// ModsSettingsAPI (и в которой он лежит в <c>modsettings.dat</c>).
///
/// <para><b>Требование к этому классу одно, и оно жёсткое:</b> разбор и обратная сборка
/// обязаны давать ТОТ ЖЕ САМЫЙ шаблон, вплоть до посторонних ключей и вплоть до различия
/// <c>3</c> и <c>3.0</c>. Причина не в аккуратности: <c>api.compareTemplates</c> при
/// отсутствии <c>settingsVersion</c> сравнивает шаблоны как текст JSON, поэтому любое
/// «безобидное» изменение формы при обратной записи означает молчаливый сброс настроек
/// игрока. Тест round-trip по 13 живым шаблонам — главная проверка модели.</para>
/// </summary>
public static class TemplateJson
{
    // ---------------------------------------------------------------- ключи шаблона

    /// <summary>Заголовок блока мода.</summary>
    public const string ModDisplayNameKey = "modDisplayName";

    /// <summary>Версия формы.</summary>
    public const string SettingsVersionKey = "settingsVersion";

    /// <summary>Тумблер мода. Он же зарезервированный ключ настроек.</summary>
    public const string EnabledKey = "enabled";

    /// <summary>Левая колонка.</summary>
    public const string Column1Key = "column1";

    /// <summary>Правая колонка.</summary>
    public const string Column2Key = "column2";

    /// <summary>Ключ мода. В шаблон НЕ пишется — его подставляет сам API.</summary>
    public const string LinkageKey = "linkage";

    // ---------------------------------------------------------------- ключи компонента

    /// <summary>Тип компонента.</summary>
    public const string TypeKey = "type";

    /// <summary>Подпись.</summary>
    public const string TextKey = "text";

    /// <summary>Подсказка.</summary>
    public const string TooltipKey = "tooltip";

    /// <summary>Ключ настройки.</summary>
    public const string VarNameKey = "varName";

    /// <summary>Значение.</summary>
    public const string ValueKey = "value";

    /// <summary>Варианты выбора.</summary>
    public const string OptionsKey = "options";

    /// <summary>Ширина.</summary>
    public const string WidthKey = "width";

    /// <summary>Минимум диапазона.</summary>
    public const string MinimumKey = "minimum";

    /// <summary>Максимум диапазона.</summary>
    public const string MaximumKey = "maximum";

    /// <summary>Шаг диапазона.</summary>
    public const string SnapIntervalKey = "snapInterval";

    /// <summary>Формат метки ползунка.</summary>
    public const string FormatKey = "format";

    /// <summary>Разрешение ручного ввода у счётчика.</summary>
    public const string CanManualInputKey = "canManualInput";

    /// <summary>Шаг делений диапазонного ползунка.</summary>
    public const string DivisionStepKey = "divisionStep";

    /// <summary>Минимальная ширина выделенного диапазона.</summary>
    public const string MinRangeDistanceKey = "minRangeDistance";

    /// <summary>Шаг подписей шкалы.</summary>
    public const string DivisionLabelStepKey = "divisionLabelStep";

    /// <summary>Суффикс подписей шкалы.</summary>
    public const string DivisionLabelPostfixKey = "divisionLabelPostfix";

    /// <summary>Кнопка справа от контрола.</summary>
    public const string ButtonKey = "button";

    /// <summary>Подпись варианта.</summary>
    public const string LabelKey = "label";

    /// <summary>
    /// Мёртвое поле <c>Dropdown</c>: окно его не читает, рендерер зашит в байткод.
    /// Модель хранит его в <see cref="SettingControl.Extra"/>, генератор не создаёт.
    /// </summary>
    public const string ItemRendererKey = "itemRenderer";

    /// <summary>Текст кнопки.</summary>
    public const string ButtonTextKey = "text";

    /// <summary>Иконка кнопки.</summary>
    public const string ButtonIconSourceKey = "iconSource";

    /// <summary>Ширина кнопки.</summary>
    public const string ButtonWidthKey = "width";

    /// <summary>Высота кнопки.</summary>
    public const string ButtonHeightKey = "height";

    /// <summary>Сдвиг кнопки по Y.</summary>
    public const string ButtonOffsetTopKey = "offsetTop";

    /// <summary>Сдвиг кнопки по X.</summary>
    public const string ButtonOffsetLeftKey = "offsetLeft";

    /// <summary>Сдвиг иконки по Y.</summary>
    public const string ButtonIconOffsetTopKey = "iconOffsetTop";

    /// <summary>Сдвиг иконки по X.</summary>
    public const string ButtonIconOffsetLeftKey = "iconOffsetLeft";

    private static readonly HashSet<string> KnownTemplateKeys = new(StringComparer.Ordinal)
    {
        ModDisplayNameKey, SettingsVersionKey, EnabledKey, Column1Key, Column2Key
    };

    private static readonly HashSet<string> KnownControlKeys = new(StringComparer.Ordinal)
    {
        TypeKey, TextKey, TooltipKey, VarNameKey, ValueKey, OptionsKey, WidthKey,
        MinimumKey, MaximumKey, SnapIntervalKey, FormatKey, CanManualInputKey,
        DivisionStepKey, MinRangeDistanceKey, DivisionLabelStepKey, DivisionLabelPostfixKey, ButtonKey
    };

    private static readonly HashSet<string> KnownButtonKeys = new(StringComparer.Ordinal)
    {
        ButtonTextKey, ButtonIconSourceKey, ButtonWidthKey, ButtonHeightKey,
        ButtonOffsetTopKey, ButtonOffsetLeftKey, ButtonIconOffsetTopKey, ButtonIconOffsetLeftKey
    };

    // ================================================================== сборка

    /// <summary>Собрать шаблон в словарь ровно той формы, которую понимает API.</summary>
    /// <param name="template">Шаблон.</param>
    /// <returns>Словарь-значение.</returns>
    public static SettingValue ToValue(SettingsTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var map = new List<KeyValuePair<string, SettingValue>>
        {
            Pair(ModDisplayNameKey, SettingValue.Of(template.ModDisplayName))
        };
        if (template.SettingsVersion is int version)
            map.Add(Pair(SettingsVersionKey, SettingValue.Of((long)version)));
        if (template.HasEnabled)
            map.Add(Pair(EnabledKey, SettingValue.Of(template.EnabledDefault)));

        map.Add(Pair(Column1Key, SettingValue.List(template.Column1.Select(ControlToValue))));
        map.Add(Pair(Column2Key, SettingValue.List(template.Column2.Select(ControlToValue))));

        foreach (string key in template.ExtraOrder)
            map.Add(Pair(key, template.Extra[key]));

        return SettingValue.Map(map);
    }

    /// <summary>Собрать один компонент.</summary>
    /// <param name="control">Контрол.</param>
    /// <returns>Словарь-значение.</returns>
    public static SettingValue ControlToValue(SettingControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        var map = new List<KeyValuePair<string, SettingValue>>();

        // Пустой type пишется только если он и был в исходном шаблоне: лишний ключ меняет
        // JSON шаблона, а по JSON API решает, сбрасывать ли настройки игрока.
        if (control.Kind != ControlKind.Unknown || control.RawTypeName is not null)
            map.Add(Pair(TypeKey, SettingValue.Of(control.TypeName)));

        // Empty не несёт вообще ничего, кроме type: createEmpty() кладёт ровно один ключ.
        if (control.Kind != ControlKind.Empty)
        {
            if (control.HasTextKey && (ControlKinds.HasText(control.Kind) || control.Text.Length > 0))
                map.Add(Pair(TextKey, SettingValue.Of(control.Text)));
            if (control.VarName is { Length: > 0 })
                map.Add(Pair(VarNameKey, SettingValue.Of(control.VarName)));
            if (control.Value is not null)
                map.Add(Pair(ValueKey, control.Value));
            if (control.Options.Count > 0 || control.HasOptionsKey)
                map.Add(Pair(OptionsKey, SettingValue.List(control.Options.Select(OptionToValue))));
            if (control.Minimum is not null) map.Add(Pair(MinimumKey, control.Minimum));
            if (control.Maximum is not null) map.Add(Pair(MaximumKey, control.Maximum));
            if (control.SnapInterval is not null) map.Add(Pair(SnapIntervalKey, control.SnapInterval));
            if (control.DivisionStep is not null) map.Add(Pair(DivisionStepKey, control.DivisionStep));
            if (control.MinRangeDistance is not null) map.Add(Pair(MinRangeDistanceKey, control.MinRangeDistance));
            if (control.DivisionLabelStep is not null) map.Add(Pair(DivisionLabelStepKey, control.DivisionLabelStep));
            if (control.DivisionLabelPostfix is not null)
                map.Add(Pair(DivisionLabelPostfixKey, SettingValue.Of(control.DivisionLabelPostfix)));
            if (control.Format is not null) map.Add(Pair(FormatKey, SettingValue.Of(control.Format)));
            if (control.CanManualInput is bool manual) map.Add(Pair(CanManualInputKey, SettingValue.Of(manual)));
            if (control.Width is int width) map.Add(Pair(WidthKey, SettingValue.Of((long)width)));
            if (control.Tooltip is not null) map.Add(Pair(TooltipKey, SettingValue.Of(control.Tooltip)));
            if (control.Button is ControlButton button) map.Add(Pair(ButtonKey, ButtonToValue(button)));
        }

        foreach (string key in control.ExtraOrder)
            map.Add(Pair(key, control.Extra[key]));

        return SettingValue.Map(map);
    }

    private static SettingValue OptionToValue(ControlOption option)
    {
        var map = new List<KeyValuePair<string, SettingValue>>
        {
            Pair(LabelKey, SettingValue.Of(option.Label))
        };
        if (option.Tooltip is not null) map.Add(Pair(TooltipKey, SettingValue.Of(option.Tooltip)));
        foreach (string key in option.ExtraOrder) map.Add(Pair(key, option.Extra[key]));
        return SettingValue.Map(map);
    }

    private static SettingValue ButtonToValue(ControlButton button)
    {
        var map = new List<KeyValuePair<string, SettingValue>>();
        if (button.Text is not null) map.Add(Pair(ButtonTextKey, SettingValue.Of(button.Text)));
        if (button.IconSource is not null) map.Add(Pair(ButtonIconSourceKey, SettingValue.Of(button.IconSource)));
        if (button.Width is double w) map.Add(Pair(ButtonWidthKey, Number(w)));
        if (button.Height is double h) map.Add(Pair(ButtonHeightKey, Number(h)));
        if (button.OffsetTop is double ot) map.Add(Pair(ButtonOffsetTopKey, Number(ot)));
        if (button.OffsetLeft is double ol) map.Add(Pair(ButtonOffsetLeftKey, Number(ol)));
        if (button.IconOffsetTop is double iot) map.Add(Pair(ButtonIconOffsetTopKey, Number(iot)));
        if (button.IconOffsetLeft is double iol) map.Add(Pair(ButtonIconOffsetLeftKey, Number(iol)));
        foreach (string key in button.ExtraOrder) map.Add(Pair(key, button.Extra[key]));
        return SettingValue.Map(map);
    }

    /// <summary>JSON шаблона.</summary>
    /// <param name="template">Шаблон.</param>
    /// <param name="indented">С отступами.</param>
    /// <param name="sortKeys">Сортировать ключи, как это делает <c>json.dumps(sort_keys=True)</c> у API.</param>
    /// <returns>Текст JSON.</returns>
    public static string ToJson(SettingsTemplate template, bool indented = false, bool sortKeys = false) =>
        ToValue(template).ToJson(indented, sortKeys);

    /// <summary>
    /// Канонический JSON шаблона: ключи отсортированы, пробелов нет. В таком виде два
    /// шаблона сравнимы напрямую — именно так их сравнивает <c>compareTemplates</c>.
    /// </summary>
    /// <param name="template">Шаблон.</param>
    /// <returns>Текст JSON.</returns>
    public static string ToCanonicalJson(SettingsTemplate template) => ToValue(template).ToCanonicalJson();

    /// <summary>Канонизировать чужой JSON, чтобы сравнить его с нашим.</summary>
    /// <param name="json">Текст JSON.</param>
    /// <returns>Канонический текст.</returns>
    public static string Canonicalize(string json) => SettingValue.ParseJson(json).ToCanonicalJson();

    // ================================================================== разбор

    /// <summary>Разобрать шаблон из текста JSON.</summary>
    /// <param name="json">Текст JSON шаблона (не всего файла состояния).</param>
    /// <param name="linkage">Ключ мода; в самом шаблоне его нет.</param>
    /// <returns>Шаблон.</returns>
    public static SettingsTemplate Parse(string json, string linkage = "") =>
        FromValue(SettingValue.ParseJson(json), linkage, null);

    /// <summary>Разобрать шаблон из узла <c>System.Text.Json</c>.</summary>
    /// <param name="element">Узел-словарь шаблона.</param>
    /// <param name="linkage">Ключ мода.</param>
    /// <param name="problems">Куда складывать замечания разбора; можно <c>null</c>.</param>
    /// <returns>Шаблон.</returns>
    public static SettingsTemplate FromJson(JsonElement element, string linkage = "", IList<string>? problems = null) =>
        FromValue(SettingValue.FromJson(element), linkage, problems);

    /// <summary>
    /// Разобрать шаблон из уже разобранного словаря. Через этот метод проходит и импорт
    /// из <c>modsettings.dat</c>, и импорт из Python-кода — потому обе дороги гарантированно
    /// дают одинаковую модель.
    /// </summary>
    /// <param name="value">Словарь шаблона.</param>
    /// <param name="linkage">Ключ мода.</param>
    /// <param name="problems">Куда складывать замечания разбора; можно <c>null</c>.</param>
    /// <returns>Шаблон.</returns>
    /// <exception cref="ArgumentException">Значение — не словарь.</exception>
    public static SettingsTemplate FromValue(SettingValue value, string linkage = "", IList<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Kind != SettingValueKind.Map)
            throw new ArgumentException($"шаблон обязан быть словарём, а не {value.Kind}", nameof(value));

        var template = new SettingsTemplate
        {
            Linkage = linkage,
            ModDisplayName = value.Get(ModDisplayNameKey)?.AsString ?? string.Empty,
            SettingsVersion = null,
            HasEnabled = false
        };

        if (value.Get(ModDisplayNameKey) is null)
            problems?.Add($"в шаблоне нет обязательного ключа '{ModDisplayNameKey}'");

        if (value.Get(SettingsVersionKey) is { } versionValue)
        {
            if (versionValue.IsNumeric) template.SettingsVersion = (int)versionValue.AsInteger;
            else problems?.Add($"'{SettingsVersionKey}' обязан быть числом, а не {versionValue.Kind}");
        }

        if (value.Get(EnabledKey) is { } enabledValue)
        {
            template.HasEnabled = true;
            template.EnabledDefault = enabledValue.Kind == SettingValueKind.Bool
                ? enabledValue.AsBool
                : enabledValue.AsNumber != 0d;
        }

        ReadColumn(value.Get(Column1Key), Column1Key, template.Column1, problems);
        ReadColumn(value.Get(Column2Key), Column2Key, template.Column2, problems);

        foreach (KeyValuePair<string, SettingValue> entry in value.Entries)
            if (!KnownTemplateKeys.Contains(entry.Key))
                template.SetExtra(entry.Key, entry.Value);

        return template;
    }

    private static void ReadColumn(
        SettingValue? column, string key, IList<SettingControl> sink, IList<string>? problems)
    {
        if (column is null) return;
        if (column.Kind != SettingValueKind.List)
        {
            problems?.Add($"'{key}' обязан быть списком, а не {column.Kind}");
            return;
        }
        for (int i = 0; i < column.Items.Count; i++)
        {
            SettingValue item = column.Items[i];
            if (item.Kind != SettingValueKind.Map)
            {
                problems?.Add($"{key}[{i}] обязан быть словарём, а не {item.Kind}");
                continue;
            }
            sink.Add(ControlFromValue(item, $"{key}[{i}]", problems));
        }
    }

    /// <summary>Разобрать один компонент.</summary>
    /// <param name="value">Словарь компонента.</param>
    /// <param name="path">Путь для сообщений об ошибках.</param>
    /// <param name="problems">Куда складывать замечания; можно <c>null</c>.</param>
    /// <returns>Контрол.</returns>
    public static SettingControl ControlFromValue(
        SettingValue value, string path = "", IList<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var control = new SettingControl();

        string? typeName = value.Get(TypeKey)?.AsString;
        if (ControlKinds.TryParse(typeName, out ControlKind kind))
        {
            control.Kind = kind;
        }
        else
        {
            control.Kind = ControlKind.Unknown;
            control.RawTypeName = typeName;
            problems?.Add(typeName is null
                ? $"{path}: нет ключа '{TypeKey}'"
                : $"{path}: тип '{typeName}' не входит в 11 типов ModsSettingsAPI");
        }

        control.Text = value.Get(TextKey)?.AsString ?? string.Empty;
        control.HasTextKey = value.Has(TextKey);
        control.HasOptionsKey = value.Has(OptionsKey);
        control.Tooltip = value.Get(TooltipKey)?.AsString;
        control.VarName = value.Get(VarNameKey)?.AsString;
        control.Value = value.Get(ValueKey);
        control.Minimum = value.Get(MinimumKey);
        control.Maximum = value.Get(MaximumKey);
        control.SnapInterval = value.Get(SnapIntervalKey);
        control.DivisionStep = value.Get(DivisionStepKey);
        control.MinRangeDistance = value.Get(MinRangeDistanceKey);
        control.DivisionLabelStep = value.Get(DivisionLabelStepKey);
        control.DivisionLabelPostfix = value.Get(DivisionLabelPostfixKey)?.AsString;
        control.Format = value.Get(FormatKey)?.AsString;

        if (value.Get(CanManualInputKey) is { } manual)
            control.CanManualInput = manual.Kind == SettingValueKind.Bool ? manual.AsBool : manual.AsNumber != 0d;

        if (value.Get(WidthKey) is { } width)
        {
            if (width.IsNumeric) control.Width = (int)width.AsInteger;
            else problems?.Add($"{path}: '{WidthKey}' обязан быть числом, а не {width.Kind}");
        }

        if (value.Get(OptionsKey) is { } options)
        {
            if (options.Kind == SettingValueKind.List)
            {
                foreach (SettingValue option in options.Items)
                    control.Options.Add(OptionFromValue(option));
            }
            else
            {
                problems?.Add($"{path}: '{OptionsKey}' обязан быть списком, а не {options.Kind}");
            }
        }

        if (value.Get(ButtonKey) is { Kind: SettingValueKind.Map } button)
            control.Button = ButtonFromValue(button);

        foreach (KeyValuePair<string, SettingValue> entry in value.Entries)
            if (!KnownControlKeys.Contains(entry.Key))
                control.SetExtra(entry.Key, entry.Value);

        return control;
    }

    /// <summary>
    /// Разобрать вариант выбора. Форма, которую кладёт <c>generateOptions</c>, —
    /// словарь; голая строка тоже понимается (её кокпит превращает в <c>{'label': ...}</c>,
    /// как это сделал бы сам API).
    /// </summary>
    /// <param name="value">Вариант.</param>
    /// <returns>Вариант модели.</returns>
    public static ControlOption OptionFromValue(SettingValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Kind == SettingValueKind.String)
            return new ControlOption(value.AsString ?? string.Empty);

        var option = new ControlOption
        {
            Label = value.Get(LabelKey)?.AsString ?? string.Empty,
            Tooltip = value.Get(TooltipKey)?.AsString
        };
        foreach (KeyValuePair<string, SettingValue> entry in value.Entries)
            if (!string.Equals(entry.Key, LabelKey, StringComparison.Ordinal) &&
                !string.Equals(entry.Key, TooltipKey, StringComparison.Ordinal))
                option.SetExtra(entry.Key, entry.Value);
        return option;
    }

    /// <summary>Разобрать подсловарь кнопки.</summary>
    /// <param name="value">Словарь кнопки.</param>
    /// <returns>Кнопка модели.</returns>
    public static ControlButton ButtonFromValue(SettingValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var button = new ControlButton
        {
            Text = value.Get(ButtonTextKey)?.AsString,
            IconSource = value.Get(ButtonIconSourceKey)?.AsString,
            Width = OptionalNumber(value.Get(ButtonWidthKey)),
            Height = OptionalNumber(value.Get(ButtonHeightKey)),
            OffsetTop = OptionalNumber(value.Get(ButtonOffsetTopKey)),
            OffsetLeft = OptionalNumber(value.Get(ButtonOffsetLeftKey)),
            IconOffsetTop = OptionalNumber(value.Get(ButtonIconOffsetTopKey)),
            IconOffsetLeft = OptionalNumber(value.Get(ButtonIconOffsetLeftKey))
        };
        foreach (KeyValuePair<string, SettingValue> entry in value.Entries)
            if (!KnownButtonKeys.Contains(entry.Key))
                button.SetExtra(entry.Key, entry.Value);
        return button;
    }

    private static double? OptionalNumber(SettingValue? value) => value is { IsNumeric: true } ? value.AsNumber : null;

    private static SettingValue Number(double value) =>
        Math.Abs(value % 1d) < double.Epsilon ? SettingValue.Of((long)value) : SettingValue.Of(value);

    private static KeyValuePair<string, SettingValue> Pair(string key, SettingValue value) => new(key, value);
}
