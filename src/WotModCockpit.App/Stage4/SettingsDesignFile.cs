using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WotModCockpit.App.Stage4;

/// <summary>
/// <c>settings.design.json</c> — источник истины дизайнера, который лежит в корне проекта
/// мода рядом с <c>wotmod.json</c>.
/// <para>
/// Зачем отдельный файл, если форма и так видна в сгенерированном питоне: питон — это
/// ВЫВОД, а не ввод. Разбирать его обратно означало бы разбирать код, а любой разбор кода
/// теряет то, чего в коде нет — например, разложенную на четыре части подсказку. Дизайнер
/// же обязан открывать проект ровно в том виде, в каком его закрыли.
/// </para>
/// <para>
/// Формат — обычный JSON и он НЕ уезжает в игру: в <c>.mtmod</c> попадает только каталог
/// <c>src</c>, а этот файл лежит рядом с ним.
/// </para>
/// </summary>
public static class SettingsDesignFile
{
    /// <summary>Имя файла в корне проекта.</summary>
    public const string FileName = "settings.design.json";

    /// <summary>Версия формата: растёт, когда меняется раскладка ключей.</summary>
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Собирает содержимое файла из формы.</summary>
    /// <param name="form">Форма.</param>
    /// <returns>JSON-текст с переводами строк <c>\n</c>.</returns>
    public static string Save(SettingsFormSpec form)
    {
        ArgumentNullException.ThrowIfNull(form);

        var root = new JsonObject
        {
            ["cockpitDesigner"] = FormatVersion,
            ["linkage"] = form.Linkage,
            ["modDisplayName"] = form.ModDisplayName,
            ["settingsVersion"] = form.SettingsVersion,
            ["hasEnabled"] = form.HasEnabled,
            ["enabledDefault"] = form.EnabledDefault,
            ["column1"] = Column(form.Column1),
            ["column2"] = Column(form.Column2)
        };

        return root.ToJsonString(WriteOptions).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Читает форму из текста файла.
    /// </summary>
    /// <param name="json">Содержимое <c>settings.design.json</c>.</param>
    /// <param name="form">Разобранная форма.</param>
    /// <param name="error">Причина отказа.</param>
    /// <returns>true, если разобрали.</returns>
    public static bool TryLoad(string json, out SettingsFormSpec? form, out string error)
    {
        form = null;
        error = string.Empty;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException ex)
        {
            error = "файл дизайнера испорчен: " + ex.Message;
            return false;
        }

        if (node is not JsonObject root)
        {
            error = "файл дизайнера пуст или это не объект JSON";
            return false;
        }

        // Реализация дизайнера в Core кладёт форму во вложенный объект ("form" либо
        // "template") рядом с параметрами генерации, а ключ мода держит в корне.
        // Читаем оба вида: файл в проекте ОДИН, и кто бы его ни записал, открыть его
        // должен уметь и тот и другой.
        string linkage = Str(root, "linkage");

        if (root["form"] is JsonObject asForm)
        {
            root = asForm;
        }
        else if (root["template"] is JsonObject asTemplate)
        {
            root = asTemplate;
        }

        form = new SettingsFormSpec
        {
            Linkage = Str(root, "linkage").Length > 0 ? Str(root, "linkage") : linkage,
            ModDisplayName = Str(root, "modDisplayName"),
            SettingsVersion = (int)Num(root, "settingsVersion", 1),

            // Свой формат пишет hasEnabled всегда; в формате шаблона признаком служит
            // САМО НАЛИЧИЕ ключа enabled — ровно так же, как в живом modsettings.dat.
            HasEnabled = root["hasEnabled"] is not null
                ? Flag(root, "hasEnabled", true)
                : root["enabled"] is not null,
            EnabledDefault = root["enabledDefault"] is not null
                ? Flag(root, "enabledDefault", true)
                : Flag(root, "enabled", true),
            Column1 = ReadColumn(root["column1"] as JsonArray),
            Column2 = ReadColumn(root["column2"] as JsonArray)
        };

        return true;
    }

    // ================================================================= запись

    private static JsonArray Column(IReadOnlyList<SettingsControlSpec> column)
    {
        var array = new JsonArray();

        foreach (SettingsControlSpec control in column)
        {
            var item = new JsonObject { ["type"] = control.Type.ToString() };

            if (SettingsControlSchema.HasText(control.Type))
            {
                item["text"] = control.Text;
            }

            if (control.Tooltip.Length > 0)
            {
                item["tooltip"] = control.Tooltip;
            }

            if (SettingsControlSchema.HasValue(control.Type))
            {
                item["varName"] = control.VarName;
                item["value"] = Value(control.Value, control.FloatKeys.Contains(SettingsTemplateJson.ValueKey));
            }

            if (SettingsControlSchema.HasOptions(control.Type))
            {
                var options = new JsonArray();
                foreach (SettingsOptionSpec option in control.Options)
                {
                    var entry = new JsonObject { ["label"] = option.Label };
                    if (option.Tooltip.Length > 0)
                    {
                        entry["tooltip"] = option.Tooltip;
                    }

                    // Чужие ключи пункта (у живого InfoPanel это 'filename' с путём к скину)
                    // кладутся сырым литералом — вместе с ключом сохраняется и его тип.
                    foreach (KeyValuePair<string, string> extra in option.Extra)
                    {
                        entry[extra.Key] = RawJson(extra.Value);
                    }

                    options.Add(entry);
                }

                item["options"] = options;
            }

            if (SettingsControlSchema.HasRange(control.Type))
            {
                item[SettingsTemplateJson.MinimumKey] = NumberNode(control, SettingsTemplateJson.MinimumKey, control.Minimum);
                item[SettingsTemplateJson.MaximumKey] = NumberNode(control, SettingsTemplateJson.MaximumKey, control.Maximum);
                item[SettingsTemplateJson.SnapIntervalKey] =
                    NumberNode(control, SettingsTemplateJson.SnapIntervalKey, control.SnapInterval);
            }

            if (SettingsControlSchema.HasFormat(control.Type))
            {
                item["format"] = control.Format;
            }

            if (SettingsControlSchema.HasWidth(control.Type) && control.Width > 0)
            {
                item["width"] = control.Width;
            }

            if (control.Type == SettingsControlType.NumericStepper)
            {
                item["canManualInput"] = control.CanManualInput;
            }

            if (control.Type == SettingsControlType.RangeSlider)
            {
                item["divisionStep"] = control.DivisionStep;
                item["minRangeDistance"] = control.MinRangeDistance;
                item["divisionLabelStep"] = control.DivisionLabelStep;
                item["divisionLabelPostfix"] = control.DivisionLabelPostfix;
            }

            if (control.Unknown.Count > 0)
            {
                var extra = new JsonObject();
                foreach (KeyValuePair<string, string> pair in control.Unknown)
                {
                    extra[pair.Key] = RawJson(pair.Value);
                }

                item["unknown"] = extra;
            }

            array.Add(item);
        }

        return array;
    }

    /// <summary>
    /// Сырой литерал JSON как узел. Хранить чужое значение строкой значило бы потерять его
    /// тип: у числа <c>3</c> и строки <c>"3"</c> разный JSON, а <c>compareTemplates</c>
    /// сравнивает шаблоны текстом.
    /// </summary>
    private static JsonNode? RawJson(string literal)
    {
        try
        {
            return JsonNode.Parse(literal);
        }
        catch (JsonException)
        {
            return JsonValue.Create(literal);
        }
    }

    /// <summary>
    /// Число так, как его надо записать: <c>0.5</c> либо <c>1.0</c>, когда поле дробное по
    /// смыслу. <c>JsonValue.Create(1.0)</c> напечатал бы <c>1</c> и дробность потерялась бы
    /// уже в самом документе дизайнера.
    /// </summary>
    private static JsonNode? NumberNode(SettingsControlSpec control, string key, double value)
    {
        if (!control.FloatKeys.Contains(key) || Math.Abs(value - Math.Round(value)) >= 1e-9)
        {
            return JsonValue.Create(value);
        }

        return JsonNode.Parse(((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture) + ".0");
    }

    private static JsonNode? Value(object? value, bool asFloat = false) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => Double(d, asFloat),
        string s => JsonValue.Create(s),
        IReadOnlyList<int> codes => new JsonArray(codes.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        IReadOnlyList<double> pair => new JsonArray(pair.Select(p => Double(p, asFloat)).ToArray()),
        _ => JsonValue.Create(value.ToString())
    };

    private static JsonNode? Double(double value, bool asFloat) =>
        asFloat && Math.Abs(value - Math.Round(value)) < 1e-9
            ? JsonNode.Parse(((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture) + ".0")
            : JsonValue.Create(value);

    // ================================================================= чтение

    private static IReadOnlyList<SettingsControlSpec> ReadColumn(JsonArray? array)
    {
        var result = new List<SettingsControlSpec>();
        if (array is null)
        {
            return result;
        }

        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            SettingsControlSpec? control = ReadControl(item);
            if (control is not null)
            {
                result.Add(control);
            }
        }

        return result;
    }

    /// <summary>
    /// Читает один компонент. Понимает и формат дизайнера, и ЖИВОЙ формат шаблона
    /// из <c>modsettings.dat</c>: там те же имена ключей, только подсказка склеена,
    /// а значение горячей клавиши может быть вложенным списком.
    /// </summary>
    /// <param name="item">Объект компонента.</param>
    /// <returns>Компонент либо <c>null</c>, если тип неизвестен.</returns>
    public static SettingsControlSpec? ReadControl(JsonObject item)
    {
        ArgumentNullException.ThrowIfNull(item);

        string typeName = Str(item, "type");
        if (!Enum.TryParse(typeName, ignoreCase: false, out SettingsControlType type))
        {
            return null;
        }

        var unknown = new Dictionary<string, string>(StringComparer.Ordinal);

        // Свой формат складывает чужие ключи во вложенный объект 'unknown'…
        if (item["unknown"] is JsonObject extra)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in extra)
            {
                unknown[pair.Key] = pair.Value?.ToJsonString() ?? "null";
            }
        }

        // …а ЖИВОЙ шаблон держит их прямо в компоненте. Это, например, itemRenderer у двух
        // живых выпадающих списков. Раньше такие ключи отбрасывались молча — но шаблон без
        // settingsVersion сравнивается ТЕКСТОМ, и потерянный ключ означает сброс настроек
        // игрока у чужого мода. Сюда же попадает ключ, который окно у ЭТОГО типа не читает
        // (ширина у галочки): написать его обратно некуда, но и терять нельзя.
        foreach (KeyValuePair<string, JsonNode?> pair in item)
        {
            if (!string.Equals(pair.Key, "unknown", StringComparison.Ordinal)
                && !WrittenBack(type, pair.Key)
                && !unknown.ContainsKey(pair.Key))
            {
                unknown[pair.Key] = pair.Value?.ToJsonString() ?? "null";
            }
        }

        object? value = ReadValue(item["value"], type);

        return new SettingsControlSpec
        {
            Type = type,
            Text = Str(item, "text"),
            Tooltip = Str(item, "tooltip"),
            VarName = Str(item, "varName"),
            Value = value,
            Options = ReadOptions(item["options"] as JsonArray),
            Minimum = Num(item, SettingsTemplateJson.MinimumKey, 0),
            Maximum = Num(item, SettingsTemplateJson.MaximumKey, 100),
            SnapInterval = Num(item, SettingsTemplateJson.SnapIntervalKey, 1),
            Format = item["format"] is null ? "{{value}}" : Str(item, "format"),
            Width = (int)Num(item, "width", 0),
            CanManualInput = Flag(item, "canManualInput", false),
            DivisionStep = Num(item, "divisionStep", 10),
            MinRangeDistance = Num(item, "minRangeDistance", 1),
            DivisionLabelStep = Num(item, "divisionLabelStep", 10),
            DivisionLabelPostfix = Str(item, "divisionLabelPostfix"),
            Unknown = unknown,
            FloatKeys = FloatKeys(item)
        };
    }

    /// <summary>
    /// Ключ, который писатель шаблона (<see cref="SettingsTemplateJson"/>) вернёт обратно
    /// сам. Всё остальное обязано уехать в <see cref="SettingsControlSpec.Unknown"/>.
    /// </summary>
    /// <param name="type">Тип компонента.</param>
    /// <param name="key">Имя ключа.</param>
    /// <returns>true, если ключ переживёт обратную запись без помощи Unknown.</returns>
    private static bool WrittenBack(SettingsControlType type, string key) => key switch
    {
        "type" => true,
        "text" => SettingsControlSchema.HasText(type),
        "tooltip" => SettingsControlSchema.HasTooltip(type),
        "varName" or "value" => SettingsControlSchema.HasValue(type),
        "options" => SettingsControlSchema.HasOptions(type),
        SettingsTemplateJson.MinimumKey or SettingsTemplateJson.MaximumKey or SettingsTemplateJson.SnapIntervalKey =>
            SettingsControlSchema.HasRange(type),
        "format" => SettingsControlSchema.HasFormat(type),
        "width" => SettingsControlSchema.HasWidth(type),
        "canManualInput" => type == SettingsControlType.NumericStepper,
        "divisionStep" or "minRangeDistance" or "divisionLabelStep" or "divisionLabelPostfix" =>
            type == SettingsControlType.RangeSlider,
        _ => false
    };

    /// <summary>
    /// Числовые поля, записанные С ТОЧКОЙ (<c>3.0</c>, <c>0.05</c>). Форма числа значима:
    /// в Python 2.7 <c>3</c> — это <c>int</c>, и деление на нём целочисленное, а сам JSON
    /// шаблона меняется — то есть без <c>settingsVersion</c> настройки игрока сбрасываются.
    /// </summary>
    private static IReadOnlySet<string> FloatKeys(JsonObject item)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (string key in new[]
                 {
                     SettingsTemplateJson.MinimumKey,
                     SettingsTemplateJson.MaximumKey,
                     SettingsTemplateJson.SnapIntervalKey,
                     SettingsTemplateJson.ValueKey
                 })
        {
            if (LooksFractional(item[key]))
            {
                keys.Add(key);
            }
        }

        return keys.Count == 0 ? SettingsControlSpec.NoFloatKeys : keys;
    }

    private static bool LooksFractional(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return false;

            case JsonArray array:
                return array.Any(LooksFractional);
        }

        if (node.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        // ToJsonString у разобранного числа отдаёт исходный текст токена, поэтому
        // «3.0» отличимо от «3» — именно этим и различаются float и int в питоне.
        string raw = node.ToJsonString();
        return raw.Contains('.', StringComparison.Ordinal)
               || raw.Contains('e', StringComparison.OrdinalIgnoreCase);
    }

    private static object? ReadValue(JsonNode? node, SettingsControlType type)
    {
        if (node is null)
        {
            return null;
        }

        switch (type)
        {
            case SettingsControlType.HotKey:
                return SettingsHotKey.Flatten(Numbers(node)).ToArray();

            case SettingsControlType.RangeSlider:
            {
                double[] pair = Numbers(node).Select(ToDouble).ToArray();
                return pair.Length >= 2 ? new[] { pair[0], pair[1] } : new[] { 0d, 0d };
            }

            case SettingsControlType.CheckBox:
                return node.GetValueKind() switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => node.GetValue<double>() != 0,
                    _ => false
                };

            case SettingsControlType.TextInput:
            case SettingsControlType.ColorChoice:
                return node.ToString();

            default:
                return node.GetValueKind() == JsonValueKind.Number
                    ? node.GetValue<double>()
                    : node.ToString();
        }
    }

    /// <summary>Разворачивает число или (возможно вложенный) массив чисел в плоскую последовательность.</summary>
    private static IEnumerable<object?> Numbers(JsonNode? node)
    {
        switch (node)
        {
            case null:
                yield break;

            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    if (item is JsonArray)
                    {
                        yield return Numbers(item).ToList();
                    }
                    else if (item is not null && item.GetValueKind() == JsonValueKind.Number)
                    {
                        yield return item.GetValue<double>();
                    }
                }

                break;

            default:
                if (node.GetValueKind() == JsonValueKind.Number)
                {
                    yield return node.GetValue<double>();
                }

                break;
        }
    }

    private static double ToDouble(object? value) => value switch
    {
        double d => d,
        int i => i,
        long l => l,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
        _ => 0
    };

    private static IReadOnlyList<SettingsOptionSpec> ReadOptions(JsonArray? array)
    {
        var options = new List<SettingsOptionSpec>();
        if (array is null)
        {
            return options;
        }

        foreach (JsonNode? node in array)
        {
            switch (node)
            {
                case JsonObject obj:
                {
                    // Пункт бывает НОСИТЕЛЕМ ДАННЫХ: живой InfoPanel держит в каждом пункте
                    // 'filename' с путём к скину и по нему же находит файл. Такие ключи
                    // сохраняются сырым литералом — вместе с ключом и его тип.
                    var extra = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, JsonNode?> pair in obj)
                    {
                        if (!string.Equals(pair.Key, "label", StringComparison.Ordinal)
                            && !string.Equals(pair.Key, "tooltip", StringComparison.Ordinal))
                        {
                            extra[pair.Key] = pair.Value?.ToJsonString() ?? "null";
                        }
                    }

                    options.Add(new SettingsOptionSpec(Str(obj, "label"), Str(obj, "tooltip"))
                    {
                        Extra = extra
                    });
                    break;
                }

                case JsonValue value:
                    options.Add(new SettingsOptionSpec(value.ToString()));
                    break;
            }
        }

        return options;
    }

    private static string Str(JsonObject obj, string name)
    {
        JsonNode? node = obj[name];
        if (node is null)
        {
            return string.Empty;
        }

        return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToString();
    }

    private static double Num(JsonObject obj, string name, double fallback)
    {
        JsonNode? node = obj[name];
        if (node is null)
        {
            return fallback;
        }

        return node.GetValueKind() == JsonValueKind.Number
            ? node.GetValue<double>()
            : double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : fallback;
    }

    private static bool Flag(JsonObject obj, string name, bool fallback)
    {
        JsonNode? node = obj[name];
        return node?.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => node.GetValue<double>() != 0,
            _ => fallback
        };
    }

    /// <summary>
    /// Читает ОДИН шаблон мода прямо из живого <c>modsettings.dat</c>.
    /// Файл открывается только на чтение и никогда не переписывается: это состояние
    /// настроек игрока, и испортить его нельзя.
    /// </summary>
    /// <param name="templateJson">Объект шаблона из секции <c>templates</c>.</param>
    /// <param name="linkage">Ключ мода — он в самом шаблоне не хранится.</param>
    /// <param name="notes">Куда сложить замечания о том, что пришлось привести к своему виду.</param>
    /// <returns>Форма.</returns>
    public static SettingsFormSpec FromLiveTemplate(JsonObject templateJson, string linkage, List<string> notes)
    {
        ArgumentNullException.ThrowIfNull(templateJson);
        ArgumentNullException.ThrowIfNull(notes);

        bool hasEnabled = templateJson["enabled"] is not null;
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "modDisplayName", "settingsVersion", "enabled", "column1", "column2", "linkage"
        };

        foreach (KeyValuePair<string, JsonNode?> pair in templateJson)
        {
            if (!known.Contains(pair.Key))
            {
                notes.Add($"в шаблоне есть посторонний ключ '{pair.Key}' — API его сохраняет, "
                          + "но дизайнер такие ключи не порождает; он потерян");
            }
        }

        if (templateJson["settingsVersion"] is null)
        {
            notes.Add("у шаблона НЕТ settingsVersion: API сравнивает такой шаблон целиком по JSON, "
                      + "и любая правка умолчания сбрасывает настройки игрока. Кокпит поставил версию 1");
        }

        var form = new SettingsFormSpec
        {
            Linkage = linkage,
            ModDisplayName = Str(templateJson, "modDisplayName"),
            SettingsVersion = (int)Num(templateJson, "settingsVersion", 1),
            HasEnabled = hasEnabled,
            EnabledDefault = Flag(templateJson, "enabled", true),
            Column1 = ReadColumn(templateJson["column1"] as JsonArray),
            Column2 = ReadColumn(templateJson["column2"] as JsonArray)
        };

        foreach (SettingsControlSpec control in form.AllControls)
        {
            if (control.Type == SettingsControlType.HotKey && control.Value is int[] codes)
            {
                notes.Add($"горячая клавиша '{control.VarName}' приведена к плоской форме "
                          + SettingsHotKey.RenderList(codes) + " (" + SettingsHotKey.Describe(codes) + ")");
            }
        }

        ReportDifferences(templateJson, form, notes);
        return form;
    }

    /// <summary>
    /// Сверяет ИСХОДНЫЙ шаблон с тем, что дизайнер напечатает обратно, и докладывает
    /// про каждое расхождение.
    /// <para>
    /// Проверка сквозная, а не по списку известных полей: перечислять руками то, что
    /// теряется, — значит рано или поздно забыть строку и оставить пользователя с
    /// уверенностью, что «потерян только random». Здесь же сравниваются РЕЗУЛЬТАТЫ:
    /// исходный объект компонента против того, что вернёт <see cref="SettingsTemplateJson"/>.
    /// </para>
    /// </summary>
    /// <param name="original">Исходный шаблон.</param>
    /// <param name="form">Разобранная форма.</param>
    /// <param name="notes">Куда складывать замечания.</param>
    private static void ReportDifferences(JsonObject original, SettingsFormSpec form, List<string> notes)
    {
        JsonObject? rendered;
        try
        {
            rendered = JsonNode.Parse(SettingsTemplateJson.Render(form)) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        if (rendered is null)
        {
            return;
        }

        foreach (string column in new[] { "column1", "column2" })
        {
            if (original[column] is not JsonArray before || rendered[column] is not JsonArray after)
            {
                continue;
            }

            for (int i = 0; i < before.Count && i < after.Count; i++)
            {
                if (before[i] is JsonObject was && after[i] is JsonObject now)
                {
                    CompareControl(was, now, column, i, notes);
                }
            }
        }
    }

    private static void CompareControl(JsonObject was, JsonObject now, string column, int index, List<string> notes)
    {
        string where = $"{column}[{index}] ({Str(was, "type")}"
                       + (Str(was, "varName").Length > 0 ? " '" + Str(was, "varName") + "'" : string.Empty) + ")";

        foreach (KeyValuePair<string, JsonNode?> pair in was)
        {
            if (string.Equals(pair.Key, "options", StringComparison.Ordinal))
            {
                CompareOptions(pair.Value as JsonArray, now["options"] as JsonArray, where, notes);
                continue;
            }

            if (now[pair.Key] is null)
            {
                notes.Add($"{where}: ключ '{pair.Key}' окно у этого типа не читает — "
                          + "дизайнер его не покажет и при обратной записи потеряет");
                continue;
            }

            string before = pair.Value?.ToJsonString() ?? "null";
            string after = now[pair.Key]!.ToJsonString();

            // Горячую клавишу дизайнер разворачивает намеренно, и про это уже сказано выше.
            if (!string.Equals(before, after, StringComparison.Ordinal)
                && !(string.Equals(pair.Key, SettingsTemplateJson.ValueKey, StringComparison.Ordinal)
                     && string.Equals(Str(was, "type"), nameof(SettingsControlType.HotKey), StringComparison.Ordinal)))
            {
                notes.Add($"{where}: значение ключа '{pair.Key}' приведено к своему виду: "
                          + before + " → " + after);
            }
        }
    }

    private static void CompareOptions(JsonArray? was, JsonArray? now, string where, List<string> notes)
    {
        if (was is null || now is null)
        {
            return;
        }

        for (int i = 0; i < was.Count && i < now.Count; i++)
        {
            if (was[i] is not JsonObject before || now[i] is not JsonObject after)
            {
                continue;
            }

            foreach (KeyValuePair<string, JsonNode?> pair in before)
            {
                if (after[pair.Key] is null)
                {
                    notes.Add($"{where}: у варианта {i + 1} потерян ключ '{pair.Key}'");
                }
            }
        }
    }

    /// <summary>Текст файла в UTF-8 без BOM — так же, как кокпит пишет питон мода.</summary>
    /// <returns>Кодировка.</returns>
    public static Encoding Utf8NoBom() => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
