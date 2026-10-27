using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WotModCockpit.App.Stage4;

/// <summary>
/// Шаблон формы в двух текстовых видах: как он ляжет в <c>modsettings.dat</c> (JSON)
/// и как он выглядит в сгенерированном питоне (литерал словаря).
/// <para>
/// Оба вида строятся из ОДНОЙ функции раскладки полей, поэтому «показать JSON шаблона»
/// в кокпите и то, что реально уедет в файл состояния, не могут разъехаться.
/// Порядок ключей — алфавитный: сам API сериализует состояние с <c>sort_keys=True</c>,
/// и при сравнении шаблонов (<c>compareTemplates</c> без <c>settingsVersion</c>)
/// сравнивается именно нормализованный JSON.
/// </para>
/// </summary>
public static class SettingsTemplateJson
{
    /// <summary>Значение поля шаблона: чем оно является для сериализатора.</summary>
    private enum Kind
    {
        Raw,
        Text,
        Ascii,

        /// <summary>Число, которое обязано сохранить дробную часть: 1 печатается как <c>1.0</c>.</summary>
        Float,

        /// <summary>Готовый литерал JSON — печатается как есть (чужие ключи из импорта).</summary>
        Json
    }

    /// <summary>Имя поля с минимумом диапазона.</summary>
    public const string MinimumKey = "minimum";

    /// <summary>Имя поля с максимумом диапазона.</summary>
    public const string MaximumKey = "maximum";

    /// <summary>Имя поля с шагом.</summary>
    public const string SnapIntervalKey = "snapInterval";

    /// <summary>Имя поля со значением.</summary>
    public const string ValueKey = "value";

    /// <summary>Одно поле компонента: имя, значение и то, unicode это или ASCII-строка.</summary>
    private readonly record struct Field(string Name, object? Value, Kind Kind);

    /// <summary>
    /// Шаблон в виде JSON — ровно как он выглядит в секции <c>templates</c> файла
    /// <c>modsettings.dat</c>: отступ 4, ключи по алфавиту, кириллица как есть
    /// (API пишет с <c>ensure_ascii=False</c>).
    /// </summary>
    /// <param name="form">Форма.</param>
    /// <returns>JSON-текст.</returns>
    public static string Render(SettingsFormSpec form)
    {
        ArgumentNullException.ThrowIfNull(form);

        var sb = new StringBuilder();
        sb.Append("{\n");

        var root = new List<Field>
        {
            new("column1", form.Column1, Kind.Raw),
            new("column2", form.Column2, Kind.Raw),
            new("modDisplayName", form.ModDisplayName, Kind.Text),
            new("settingsVersion", (double)form.SettingsVersion, Kind.Raw)
        };

        if (form.HasEnabled)
        {
            root.Add(new Field("enabled", form.EnabledDefault, Kind.Raw));
        }

        root.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        for (int i = 0; i < root.Count; i++)
        {
            Field field = root[i];
            sb.Append("    ").Append(Quote(field.Name)).Append(": ");

            if (field.Value is IReadOnlyList<SettingsControlSpec> column)
            {
                AppendColumn(sb, column, 1, json: true);
            }
            else
            {
                sb.Append(Scalar(field.Value, json: true, field.Kind));
            }

            sb.Append(i == root.Count - 1 ? "\n" : ",\n");
        }

        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// Тело <c>build_template</c> для генерируемого питона: те же словари, но значения
    /// контролов берутся через <c>v('varName')</c>, чтобы шаблон строился ОТ СОХРАНЁННЫХ
    /// значений игрока, а не от заводских.
    /// </summary>
    /// <param name="form">Форма.</param>
    /// <param name="indent">Базовый отступ (число пробелов) для строки <c>return {</c>.</param>
    /// <returns>Питоновский литерал словаря.</returns>
    public static string RenderPythonTemplate(SettingsFormSpec form, int indent)
    {
        ArgumentNullException.ThrowIfNull(form);

        string pad = new(' ', indent);
        var sb = new StringBuilder();

        sb.Append("{\n");
        sb.Append(pad).Append("    'modDisplayName': MOD_DISPLAY_NAME,\n");
        sb.Append(pad).Append("    'settingsVersion': SETTINGS_VERSION,\n");

        if (form.HasEnabled)
        {
            sb.Append(pad).Append("    'enabled': v('enabled'),\n");
        }

        AppendPythonColumn(sb, "column1", form.Column1, pad);
        AppendPythonColumn(sb, "column2", form.Column2, pad);

        sb.Append(pad).Append('}');
        return sb.ToString();
    }

    private static void AppendPythonColumn(
        StringBuilder sb,
        string name,
        IReadOnlyList<SettingsControlSpec> column,
        string pad)
    {
        if (column.Count == 0)
        {
            sb.Append(pad).Append("    '").Append(name).Append("': [],\n");
            return;
        }

        sb.Append(pad).Append("    '").Append(name).Append("': [\n");

        foreach (SettingsControlSpec control in column)
        {
            sb.Append(pad).Append("        {\n");

            IReadOnlyList<Field> fields = Fields(control);
            for (int i = 0; i < fields.Count; i++)
            {
                Field field = fields[i];
                sb.Append(pad).Append("            '").Append(field.Name).Append("': ");

                if (field.Name == "value" && control.VarName.Length > 0)
                {
                    sb.Append("v('").Append(control.VarName).Append("')");
                }
                else if (field.Value is IReadOnlyList<SettingsOptionSpec> options)
                {
                    AppendPythonOptions(sb, options, pad);
                }
                else
                {
                    sb.Append(Scalar(field.Value, json: false, field.Kind));
                }

                sb.Append(i == fields.Count - 1 ? "\n" : ",\n");
            }

            sb.Append(pad).Append("        },\n");
        }

        sb.Append(pad).Append("    ],\n");
    }

    private static void AppendPythonOptions(
        StringBuilder sb,
        IReadOnlyList<SettingsOptionSpec> options,
        string pad)
    {
        sb.Append("[\n");

        for (int i = 0; i < options.Count; i++)
        {
            SettingsOptionSpec option = options[i];
            sb.Append(pad).Append("                {'label': ").Append(PyString(option.Label, unicode: true));

            if (option.Tooltip.Length > 0)
            {
                sb.Append(", 'tooltip': ").Append(PyString(option.Tooltip, unicode: true));
            }

            // Чужие ключи пункта (живой InfoPanel носит в пункте 'filename' с путём к скину).
            foreach (KeyValuePair<string, string> extra in OptionExtras(option))
            {
                sb.Append(", '").Append(extra.Key).Append("': ").Append(JsonLiteralToPython(extra.Value));
            }

            sb.Append('}').Append(i == options.Count - 1 ? "\n" : ",\n");
        }

        sb.Append(pad).Append("            ]");
    }

    private static void AppendColumn(
        StringBuilder sb,
        IReadOnlyList<SettingsControlSpec> column,
        int level,
        bool json)
    {
        if (column.Count == 0)
        {
            sb.Append("[]");
            return;
        }

        string pad = new(' ', 4 * level);
        sb.Append("[\n");

        for (int i = 0; i < column.Count; i++)
        {
            sb.Append(pad).Append("    {\n");

            IReadOnlyList<Field> fields = Fields(column[i]);
            for (int f = 0; f < fields.Count; f++)
            {
                Field field = fields[f];
                sb.Append(pad).Append("        ").Append(Quote(field.Name)).Append(": ");

                if (field.Value is IReadOnlyList<SettingsOptionSpec> options)
                {
                    AppendJsonOptions(sb, options, pad);
                }
                else
                {
                    sb.Append(Scalar(field.Value, json, field.Kind));
                }

                sb.Append(f == fields.Count - 1 ? "\n" : ",\n");
            }

            sb.Append(pad).Append(i == column.Count - 1 ? "    }\n" : "    },\n");
        }

        sb.Append(pad).Append(']');
    }

    private static void AppendJsonOptions(StringBuilder sb, IReadOnlyList<SettingsOptionSpec> options, string pad)
    {
        sb.Append("[\n");

        for (int i = 0; i < options.Count; i++)
        {
            SettingsOptionSpec option = options[i];
            sb.Append(pad).Append("            {").Append(Quote("label")).Append(": ").Append(JsonString(option.Label));

            if (option.Tooltip.Length > 0)
            {
                sb.Append(", ").Append(Quote("tooltip")).Append(": ").Append(JsonString(option.Tooltip));
            }

            foreach (KeyValuePair<string, string> extra in OptionExtras(option))
            {
                sb.Append(", ").Append(Quote(extra.Key)).Append(": ").Append(extra.Value);
            }

            sb.Append('}').Append(i == options.Count - 1 ? "\n" : ",\n");
        }

        sb.Append(pad).Append("        ]");
    }

    /// <summary>
    /// Поля одного компонента в алфавитном порядке — ровно те, что окно читает,
    /// плюс чужие ключи, пришедшие импортом.
    /// <para>
    /// Сам дизайнер сверх этого списка ничего не порождает: <c>width</c> у типов кроме
    /// Dropdown/Slider/TextInput не делает ничего, а <c>itemRenderer</c> ни одна фабрика
    /// <c>templates.py</c> не кладёт. Но если такой ключ ПРИШЁЛ из чужого шаблона, он
    /// печатается обратно как есть: без <c>settingsVersion</c> API сравнивает шаблоны как
    /// текст, и выброшенный ключ означал бы сброс настроек игрока.
    /// </para>
    /// </summary>
    private static IReadOnlyList<Field> Fields(SettingsControlSpec control)
    {
        var fields = new List<Field> { new("type", control.Type.ToString(), Kind.Ascii) };

        // Ключ 'text' пишется ВСЕГДА, даже когда подпись пуста: createBase в templates.py
        // кладёт его безусловно, и все 56 компонентов в 13 живых шаблонах его имеют.
        // Пропустить ключ — не то же самое, что записать пустую строку: окно читает cfg.text
        // у динамического объекта, и отсутствующее свойство даёт undefined, а не ''.
        // Тот же безусловный порядок уже действует в SettingsDesignFile.Column().
        if (SettingsControlSchema.HasText(control.Type))
        {
            fields.Add(new Field("text", control.Text, Kind.Text));
        }

        if (SettingsControlSchema.HasTooltip(control.Type) && control.Tooltip.Length > 0)
        {
            fields.Add(new Field("tooltip", control.Tooltip, Kind.Text));
        }

        if (SettingsControlSchema.HasValue(control.Type))
        {
            fields.Add(new Field("varName", control.VarName, Kind.Ascii));
            fields.Add(new Field(ValueKey, control.Value, ValueKind(control)));
        }

        if (SettingsControlSchema.HasOptions(control.Type))
        {
            fields.Add(new Field("options", control.Options, Kind.Raw));
        }

        if (SettingsControlSchema.HasRange(control.Type))
        {
            fields.Add(new Field(MinimumKey, control.Minimum, NumberKind(control, MinimumKey)));
            fields.Add(new Field(MaximumKey, control.Maximum, NumberKind(control, MaximumKey)));
            fields.Add(new Field(SnapIntervalKey, control.SnapInterval, NumberKind(control, SnapIntervalKey)));
        }

        if (SettingsControlSchema.HasFormat(control.Type))
        {
            fields.Add(new Field("format", control.Format, Kind.Text));
        }

        if (SettingsControlSchema.HasWidth(control.Type) && control.Width > 0)
        {
            fields.Add(new Field("width", (double)control.Width, Kind.Raw));
        }

        if (control.Type == SettingsControlType.NumericStepper)
        {
            // Фабрика кладёт этот ключ ВСЕГДА, даже когда ручной ввод не нужен.
            fields.Add(new Field("canManualInput", control.CanManualInput, Kind.Raw));
        }

        if (control.Type == SettingsControlType.RangeSlider)
        {
            fields.Add(new Field("divisionStep", control.DivisionStep, Kind.Raw));
            fields.Add(new Field("minRangeDistance", control.MinRangeDistance, Kind.Raw));
            fields.Add(new Field("divisionLabelStep", control.DivisionLabelStep, Kind.Raw));
            fields.Add(new Field("divisionLabelPostfix", control.DivisionLabelPostfix, Kind.Text));
        }

        foreach (KeyValuePair<string, string> extra in control.Unknown)
        {
            if (fields.All(f => !string.Equals(f.Name, extra.Key, StringComparison.Ordinal)))
            {
                fields.Add(new Field(extra.Key, extra.Value, Kind.Json));
            }
        }

        fields.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return fields;
    }

    /// <summary>Чужие ключи пункта в устойчивом (алфавитном) порядке.</summary>
    private static IEnumerable<KeyValuePair<string, string>> OptionExtras(SettingsOptionSpec option) =>
        option.Extra
            .Where(p => !string.Equals(p.Key, "label", StringComparison.Ordinal)
                        && !string.Equals(p.Key, "tooltip", StringComparison.Ordinal))
            .OrderBy(p => p.Key, StringComparer.Ordinal);

    private static Kind ValueKind(SettingsControlSpec control) => control.Type switch
    {
        SettingsControlType.TextInput => Kind.Text,
        SettingsControlType.ColorChoice => Kind.Ascii,
        _ => NumberKind(control, ValueKey)
    };

    private static Kind NumberKind(SettingsControlSpec control, string key) =>
        control.FloatKeys.Contains(key) ? Kind.Float : Kind.Raw;

    private static string Scalar(object? value, bool json, Kind kind = Kind.Raw) => value switch
    {
        null => json ? "null" : "None",
        bool b => json ? (b ? "true" : "false") : (b ? "True" : "False"),
        string s when kind == Kind.Json => json ? s : JsonLiteralToPython(s),
        string s => json ? JsonString(s) : PyString(s, kind != Kind.Ascii),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => Number(d, kind == Kind.Float),
        IReadOnlyList<int> codes => "[" + string.Join(", ", codes.Select(c => c.ToString(CultureInfo.InvariantCulture))) + "]",
        IReadOnlyList<double> pair => "[" + string.Join(", ", pair.Select(p => Number(p, kind == Kind.Float))) + "]",
        _ => json ? JsonString(value.ToString() ?? string.Empty) : PyString(value.ToString() ?? string.Empty, true)
    };

    /// <summary>
    /// Число в форме, которую задал автор поля.
    ///
    /// <para>По умолчанию хвост <c>.0</c> НЕ приписывается: в 13 живых шаблонах целые границы
    /// (<c>minimum 0</c>, <c>maximum 100</c>, <c>snapInterval 1</c>) хранятся как <c>int</c>,
    /// и лишняя дробная часть изменила бы JSON шаблона — а при отсутствии
    /// <c>settingsVersion</c> это сброс настроек игрока.</para>
    ///
    /// <para><paramref name="forceFloat"/> ставится там, где дробность есть по смыслу поля
    /// (см. <see cref="SettingsControlSpec.FloatKeys"/>): у ползунка «прозрачность» 0…1 с
    /// шагом 0,05 умолчание <c>1</c> без этого стало бы <c>int</c>, и в Python 2.7
    /// <c>settings.get('opacity') / 2</c> дало бы 0 вместо 0.5.</para>
    /// </summary>
    private static string Number(double value, bool forceFloat = false)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return forceFloat ? "0.0" : "0";
        }

        if (Math.Abs(value - Math.Round(value)) >= 1e-9)
        {
            return value.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        string whole = ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        return forceFloat ? whole + ".0" : whole;
    }

    /// <summary>
    /// Литерал JSON → литерал Python 2.7. Нужен для чужих ключей, пришедших импортом:
    /// в шаблоне они печатаются как есть, а в питоне <c>true</c>/<c>false</c>/<c>null</c>
    /// пришлось бы писать иначе.
    /// </summary>
    /// <param name="jsonLiteral">Готовый JSON-литерал.</param>
    /// <returns>Литерал Python.</returns>
    private static string JsonLiteralToPython(string jsonLiteral)
    {
        try
        {
            return PythonNode(JsonNode.Parse(jsonLiteral));
        }
        catch (JsonException)
        {
            // Не JSON — значит это просто текст (так выглядели чужие ключи в старых
            // документах дизайнера). Печатаем строкой, ничего не теряя.
            return PyString(jsonLiteral, unicode: true);
        }
    }

    private static string PythonNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "None";

            case JsonArray array:
                return "[" + string.Join(", ", array.Select(PythonNode)) + "]";

            case JsonObject obj:
                return "{" + string.Join(", ", obj.Select(p =>
                    PyString(p.Key, unicode: false) + ": " + PythonNode(p.Value))) + "}";
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Null => "None",
            JsonValueKind.Number => node.ToJsonString(),
            _ => PyString(node.GetValue<string>(), unicode: true)
        };
    }

    private static string Quote(string name) => JsonString(name);

    private static string JsonString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');

        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Строковый литерал Python 2.7. Тексты, которые видит игрок, пишутся с префиксом
    /// <c>u</c>: без него кириллица в py2 стала бы байтовой строкой в неизвестной кодировке.
    /// Ключи и коды (<c>varName</c>, <c>type</c>, hex цвета) остаются байтовыми ASCII-строками.
    /// </summary>
    private static string PyString(string value, bool unicode)
    {
        var sb = new StringBuilder(value.Length + 3);

        if (unicode)
        {
            sb.Append('u');
        }

        sb.Append('\'');

        foreach (char c in value)
        {
            switch (c)
            {
                case '\'': sb.Append("\\'"); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }

        sb.Append('\'');
        return sb.ToString();
    }
}
