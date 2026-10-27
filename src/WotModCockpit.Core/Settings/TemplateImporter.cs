using System.Text.Json;
using System.Text.RegularExpressions;

namespace WotModCockpit.Core.Settings;

/// <summary>Откуда пришёл шаблон.</summary>
public enum TemplateImportSource
{
    /// <summary>Ниоткуда — импорт не удался.</summary>
    None = 0,

    /// <summary>Из файла состояния <c>modsettings.dat</c>, секция <c>templates</c>.</summary>
    ModSettingsDat,

    /// <summary>Из Python-кода, записанного литеральными словарями (так пишет сам кокпит).</summary>
    PythonLiteral,

    /// <summary>Из Python-кода, записанного вызовами <c>templates.createX</c> (так пишет рецепт R15).</summary>
    PythonTemplatesApi
}

/// <summary>Итог импорта шаблона.</summary>
/// <param name="Success">Шаблон разобран. При <c>false</c> <paramref name="Template"/> равен <c>null</c>.</param>
/// <param name="Template">Шаблон.</param>
/// <param name="Source">Откуда.</param>
/// <param name="Problems">
/// Что помешало (при неудаче) или что стоит знать (при удаче): например, не нашлась
/// константа <c>LINKAGE</c>. Список НИКОГДА не молчит о том, чего импортёр не понял.
/// </param>
public sealed record TemplateImportResult(
    bool Success,
    SettingsTemplate? Template,
    TemplateImportSource Source,
    IReadOnlyList<string> Problems)
{
    /// <summary>Неудача с объяснением.</summary>
    /// <param name="source">Откуда пытались читать.</param>
    /// <param name="problems">Причины.</param>
    /// <returns>Результат.</returns>
    public static TemplateImportResult Failed(TemplateImportSource source, params string[] problems) =>
        new(false, null, source, problems);

    /// <summary>Все замечания одной строкой.</summary>
    /// <returns>Текст.</returns>
    public string Describe() => string.Join(Environment.NewLine, Problems);
}

/// <summary>
/// Импорт готового шаблона настроек: из живого состояния ModsSettingsAPI и из кода мода.
///
/// <para><b>Честная оценка возможностей.</b> Разобрать шаблон из ПРОИЗВОЛЬНОГО Python-кода
/// нельзя: словарь могут собрать циклом, склеить из кусков, прочитать из чужого модуля или
/// посчитать в рантайме — это решается только запуском кода, чего кокпит делать не станет.
/// Поэтому поддержаны ровно три формы записи, которые встречаются в жизни:</para>
/// <list type="number">
///   <item>литеральные словари — то, что порождает сам кокпит (ГЛАВНЫЙ сценарий:
///   открыть свои же настройки и поменять);</item>
///   <item>вызовы <c>templates.createX(...)</c> — то, что порождает рецепт R15 и что пишут руками;</item>
///   <item>значения, взятые из словаря умолчаний: <c>v('key')</c>, <c>DEFAULTS['key']</c>,
///   <c>_settings['key']</c>, <c>values.get('key', ...)</c>.</item>
/// </list>
/// <para>На всём остальном импортёр возвращает <c>Success = false</c> и говорит, ЧТО именно
/// не понял, — вместо того чтобы угадать и молча испортить чужую форму.</para>
/// </summary>
public static class TemplateImporter
{
    /// <summary>Секция файла состояния с шаблонами.</summary>
    public const string TemplatesSection = "templates";

    /// <summary>Секция файла состояния с текущими значениями игрока.</summary>
    public const string SettingsSection = "settings";

    private static readonly JsonDocumentOptions StateJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64
    };

    // ================================================================== modsettings.dat

    /// <summary>
    /// Перечислить моды, чьи шаблоны лежат в файле состояния. Порядок — как в файле
    /// (API пишет его с <c>sort_keys=True</c>).
    /// </summary>
    /// <param name="stateJson">Содержимое <c>modsettings.dat</c>.</param>
    /// <returns>Ключи модов.</returns>
    public static IReadOnlyList<string> ListLinkages(string stateJson)
    {
        ArgumentNullException.ThrowIfNull(stateJson);
        using JsonDocument doc = JsonDocument.Parse(stateJson, StateJsonOptions);
        if (!doc.RootElement.TryGetProperty(TemplatesSection, out JsonElement templates) ||
            templates.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }
        return templates.EnumerateObject().Select(p => p.Name).ToArray();
    }

    /// <summary>
    /// Достать шаблон одного мода из файла состояния.
    ///
    /// <para>Файл читается ТОЛЬКО на чтение и никогда не переписывается: это живое состояние
    /// настроек игрока, а API держит его копию в памяти и перезаписывает файл целиком.</para>
    /// </summary>
    /// <param name="stateJson">Содержимое <c>modsettings.dat</c>.</param>
    /// <param name="linkage">Ключ мода.</param>
    /// <returns>Результат импорта.</returns>
    public static TemplateImportResult FromModSettingsDat(string stateJson, string linkage)
    {
        ArgumentNullException.ThrowIfNull(stateJson);
        ArgumentNullException.ThrowIfNull(linkage);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stateJson, StateJsonOptions);
        }
        catch (JsonException ex)
        {
            return TemplateImportResult.Failed(TemplateImportSource.ModSettingsDat,
                $"modsettings.dat — не JSON: {ex.Message}");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty(TemplatesSection, out JsonElement templates) ||
                templates.ValueKind != JsonValueKind.Object)
            {
                return TemplateImportResult.Failed(TemplateImportSource.ModSettingsDat,
                    $"в файле состояния нет секции '{TemplatesSection}'");
            }
            if (!templates.TryGetProperty(linkage, out JsonElement template))
            {
                return TemplateImportResult.Failed(TemplateImportSource.ModSettingsDat,
                    $"мода '{linkage}' нет среди {templates.EnumerateObject().Count()} сохранённых шаблонов");
            }
            if (template.ValueKind != JsonValueKind.Object)
            {
                return TemplateImportResult.Failed(TemplateImportSource.ModSettingsDat,
                    $"шаблон мода '{linkage}' — не словарь, а {template.ValueKind}");
            }

            var problems = new List<string>();
            SettingsTemplate model = TemplateJson.FromJson(template, linkage, problems);
            return new TemplateImportResult(true, model, TemplateImportSource.ModSettingsDat, problems);
        }
    }

    /// <summary>Прочитать шаблон прямо из файла состояния на диске.</summary>
    /// <param name="path">Путь к <c>modsettings.dat</c>.</param>
    /// <param name="linkage">Ключ мода.</param>
    /// <returns>Результат импорта.</returns>
    public static TemplateImportResult FromModSettingsDatFile(string path, string linkage)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            return TemplateImportResult.Failed(TemplateImportSource.ModSettingsDat, $"файла нет: {path}");
        return FromModSettingsDat(File.ReadAllText(path), linkage);
    }

    /// <summary>
    /// Текущие значения игрока из секции <c>settings</c>. Они МОГУТ расходиться с шаблоном:
    /// шаблон — «как задумал автор», settings — «как хочет игрок». Живой пример:
    /// в шаблоне выпадающего списка <c>statType = 4</c>, а в настройках игрока 0.
    /// </summary>
    /// <param name="stateJson">Содержимое <c>modsettings.dat</c>.</param>
    /// <param name="linkage">Ключ мода.</param>
    /// <returns>Ключ настройки -&gt; значение; пусто, если мода нет.</returns>
    public static IReadOnlyList<KeyValuePair<string, SettingValue>> ReadSavedSettings(
        string stateJson, string linkage)
    {
        ArgumentNullException.ThrowIfNull(stateJson);
        using JsonDocument doc = JsonDocument.Parse(stateJson, StateJsonOptions);
        if (!doc.RootElement.TryGetProperty(SettingsSection, out JsonElement settings) ||
            settings.ValueKind != JsonValueKind.Object ||
            !settings.TryGetProperty(linkage, out JsonElement mod) ||
            mod.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<KeyValuePair<string, SettingValue>>();
        }
        return SettingValue.FromJson(mod).Entries;
    }

    /// <summary>Разобрать шаблон из голого JSON (без обёртки файла состояния).</summary>
    /// <param name="templateJson">JSON шаблона.</param>
    /// <param name="linkage">Ключ мода.</param>
    /// <returns>Результат импорта.</returns>
    public static TemplateImportResult FromTemplateJson(string templateJson, string linkage = "")
    {
        ArgumentNullException.ThrowIfNull(templateJson);
        try
        {
            var problems = new List<string>();
            SettingsTemplate model = TemplateJson.FromValue(
                SettingValue.ParseJson(templateJson), linkage, problems);
            return new TemplateImportResult(true, model, TemplateImportSource.ModSettingsDat, problems);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return TemplateImportResult.Failed(TemplateImportSource.ModSettingsDat, ex.Message);
        }
    }

    // ================================================================== Python

    private static readonly Regex TopLevelAssignment = new(
        @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)[ \t]*=[ \t]*(?![=])",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ReturnKeyword = new(
        @"\breturn\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DictAssignment = new(
        @"^[ \t]*[A-Za-z_][A-Za-z0-9_]*[ \t]*=[ \t]*(?=\{)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Разобрать шаблон из Python-кода мода.
    ///
    /// <para>Ищется словарь с ключом <c>modDisplayName</c>: сначала среди тех, что стоят после
    /// <c>return</c>, затем среди присваиваний. Найденный словарь вычисляется — с раскрытием
    /// констант модуля, обращений к словарю умолчаний и вызовов <c>templates.createX</c>.</para>
    /// </summary>
    /// <param name="source">Текст модуля Python 2.</param>
    /// <returns>Результат импорта.</returns>
    public static TemplateImportResult FromPython(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var problems = new List<string>();
        var evaluator = new PythonEvaluator(source, problems);

        if (!TryFindTemplateDict(source, out IPyNode? dict, out string? searchError))
        {
            return TemplateImportResult.Failed(TemplateImportSource.PythonLiteral,
                searchError ?? "в коде не нашлось словаря с ключом 'modDisplayName' — " +
                "кокпит понимает только литеральный словарь и вызовы templates.createX");
        }

        SettingValue? value = evaluator.Eval(dict!);
        if (value is null || value.Kind != SettingValueKind.Map)
        {
            problems.Insert(0, "словарь шаблона найден, но вычислить его не удалось");
            return new TemplateImportResult(false, null,
                evaluator.UsedFactories ? TemplateImportSource.PythonTemplatesApi : TemplateImportSource.PythonLiteral,
                problems);
        }

        string linkage = evaluator.ResolveLinkage() ?? value.Get(TemplateJson.LinkageKey)?.AsString ?? string.Empty;
        if (linkage.Length == 0)
            problems.Add("в модуле не нашлось строковой константы LINKAGE — ключ мода придётся задать вручную");

        SettingsTemplate template = TemplateJson.FromValue(value, linkage, problems);
        TemplateImportSource kind = evaluator.UsedFactories
            ? TemplateImportSource.PythonTemplatesApi
            : TemplateImportSource.PythonLiteral;

        // Шаблон без единого контрола почти всегда означает, что колонки наполняются кодом
        // (циклом, вызовом helper'а, append'ом), которого кокпит не исполняет. Отдать такую
        // «пустую форму» как успех значило бы молча стереть чужие контролы при первом же
        // сохранении — лучше честно сказать, что разобрать не вышло.
        if (template.ControlCount == 0)
        {
            problems.Insert(0,
                "словарь шаблона найден, но обе колонки пусты — похоже, контролы добавляются кодом, " +
                "который кокпит не исполняет; форму придётся собрать в дизайнере заново");
            return new TemplateImportResult(false, null, kind, problems);
        }

        return new TemplateImportResult(true, template, kind, problems);
    }

    /// <summary>Разобрать шаблон из файла Python на диске.</summary>
    /// <param name="path">Путь к модулю.</param>
    /// <returns>Результат импорта.</returns>
    public static TemplateImportResult FromPythonFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            return TemplateImportResult.Failed(TemplateImportSource.PythonLiteral, $"файла нет: {path}");
        return FromPython(File.ReadAllText(path));
    }

    private static bool TryFindTemplateDict(string source, out IPyNode? dict, out string? error)
    {
        dict = null;
        error = null;
        var failures = new List<string>();

        foreach (int offset in CandidateOffsets(source))
        {
            if (!PythonValueParser.TryParse(source, offset, out IPyNode? node, out string? parseError))
            {
                if (parseError is not null) failures.Add(parseError);
                continue;
            }
            if (node is PyDictExpr d && HasKey(d, TemplateJson.ModDisplayNameKey))
            {
                dict = d;
                return true;
            }
        }

        if (failures.Count > 0)
            error = "словарь шаблона не разобран: " + failures[0];
        return false;
    }

    private static IEnumerable<int> CandidateOffsets(string source)
    {
        foreach (Match m in ReturnKeyword.Matches(source))
        {
            int p = SkipSpaces(source, m.Index + m.Length);
            if (p < source.Length && source[p] == '{') yield return p;
        }
        foreach (Match m in DictAssignment.Matches(source))
            yield return m.Index + m.Length;
    }

    private static int SkipSpaces(string text, int pos)
    {
        while (pos < text.Length)
        {
            char c = text[pos];
            if (c == '#') { while (pos < text.Length && text[pos] != '\n') pos++; continue; }
            if (c == '\\' && pos + 1 < text.Length && (text[pos + 1] == '\n' || text[pos + 1] == '\r')) { pos++; continue; }
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            break;
        }
        return pos;
    }

    private static bool HasKey(PyDictExpr dict, string key) =>
        dict.Entries.Any(e => e.Key is PyLiteralExpr { Value.Kind: SettingValueKind.String } literal &&
                              string.Equals(literal.Value.AsString, key, StringComparison.Ordinal));

    // ------------------------------------------------------------------ вычислитель

    /// <summary>
    /// Вычисляет разобранное выражение в значение шаблона. Ничего не исполняет: раскрывает
    /// только те конструкции, которые перечислены в документации <see cref="TemplateImporter"/>.
    /// </summary>
    private sealed class PythonEvaluator
    {
        /// <summary>Имена словарей, в которых мод держит значения по умолчанию.</summary>
        private static readonly string[] DefaultsNames =
            { "DEFAULTS", "_settings", "_values", "_defaults", "DEFAULT_SETTINGS", "SETTINGS" };

        /// <summary>Имена, под которыми в шаблон приходит словарь текущих значений.</summary>
        private static readonly string[] ValuesNames = { "values", "settings", "_values", "_settings" };

        /// <summary>
        /// Помощники «взять текущее значение по ключу». <c>_v</c> — тот, что печатает
        /// кодогенератор кокпита: <c>_v(values, 'varName')</c>.
        /// </summary>
        private static readonly HashSet<string> ValueHelperNames =
            new(StringComparer.Ordinal) { "v", "_v", "value", "_value", "default" };

        /// <summary>
        /// Позиционные параметры фабрик <c>templates.py</c> — снято с самих фабрик,
        /// чтобы позиционные вызовы читались так же, как их прочитает Python.
        /// </summary>
        private static readonly Dictionary<string, string[]> FactoryParameters = new(StringComparer.Ordinal)
        {
            ["createEmpty"] = Array.Empty<string>(),
            ["createLabel"] = new[] { "text", "tooltip" },
            ["createCheckbox"] = new[] { "text", "varName", "value", "tooltip", "button" },
            ["createRadioButtonGroup"] = new[] { "text", "varName", "options", "value", "tooltip", "button" },
            ["createDropdown"] = new[] { "text", "varName", "options", "value", "tooltip", "button", "width" },
            ["createSlider"] = new[] { "text", "varName", "value", "min", "max", "interval", "format", "tooltip", "button", "width" },
            ["createInput"] = new[] { "text", "varName", "value", "tooltip", "button", "width" },
            ["createNumericStepper"] = new[] { "text", "varName", "value", "min", "max", "interval", "tooltip", "button", "manual" },
            ["createHotkey"] = new[] { "text", "varName", "value", "tooltip", "button" },
            ["createColorChoice"] = new[] { "text", "varName", "value", "tooltip", "button" },
            ["createRangeSlider"] = new[] { "text", "varName", "value", "min", "max", "interval", "step", "minRange", "labelStep", "labelPostfix", "tooltip", "button" },
            ["createButton"] = new[] { "width", "height", "text", "offsetTop", "offsetLeft", "icon", "iconOffsetTop", "iconOffsetLeft" }
        };

        private readonly string _source;
        private readonly List<string> _problems;
        private readonly Dictionary<string, int> _assignments = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SettingValue?> _cache = new(StringComparer.Ordinal);
        private readonly HashSet<string> _evaluating = new(StringComparer.Ordinal);

        public PythonEvaluator(string source, List<string> problems)
        {
            _source = source;
            _problems = problems;
            foreach (Match m in TopLevelAssignment.Matches(source))
            {
                string name = m.Groups["name"].Value;
                // Первое присваивание выигрывает: перезапись константы ниже по файлу
                // почти всегда означает рантайм-логику, которую импортёр не моделирует.
                if (!_assignments.ContainsKey(name)) _assignments[name] = m.Index + m.Length;
            }
        }

        /// <summary>Встретились ли вызовы <c>templates.createX</c>.</summary>
        public bool UsedFactories { get; private set; }

        /// <summary>Значение константы <c>LINKAGE</c>, если она есть и строковая.</summary>
        /// <returns>Ключ мода или <c>null</c>.</returns>
        public string? ResolveLinkage() => Lookup("LINKAGE")?.AsString;

        /// <summary>Вычислить выражение.</summary>
        /// <param name="node">Узел.</param>
        /// <returns>Значение или <c>null</c>, если вычислить нельзя.</returns>
        public SettingValue? Eval(IPyNode node)
        {
            switch (node)
            {
                case PyLiteralExpr literal:
                    return literal.Value;

                case PyListExpr list:
                    {
                        var items = new List<SettingValue>(list.Items.Count);
                        foreach (IPyNode item in list.Items)
                        {
                            SettingValue? v = Eval(item);
                            if (v is null) return null;
                            items.Add(v);
                        }
                        return SettingValue.List(items);
                    }

                case PyDictExpr dict:
                    {
                        var entries = new List<KeyValuePair<string, SettingValue>>(dict.Entries.Count);
                        foreach (KeyValuePair<IPyNode, IPyNode> entry in dict.Entries)
                        {
                            SettingValue? key = Eval(entry.Key);
                            if (key is not { Kind: SettingValueKind.String })
                            {
                                _problems.Add("ключ словаря обязан быть строковым литералом");
                                return null;
                            }
                            SettingValue? v = Eval(entry.Value);
                            if (v is null) return null;
                            entries.Add(new KeyValuePair<string, SettingValue>(key.AsString!, v));
                        }
                        return SettingValue.Map(entries);
                    }

                case PyNameExpr name:
                    {
                        SettingValue? v = Lookup(name.Name);
                        if (v is null) _problems.Add($"не удалось вычислить имя '{name.Name}'");
                        return v;
                    }

                case PyIndexExpr index:
                    return EvalIndex(index);

                case PyCallExpr call:
                    return EvalCall(call);

                case PyAttrExpr attr:
                    {
                        if (attr.Target is PyNameExpr module)
                        {
                            // Константу из другого модуля кокпит не читает, но одноимённая
                            // константа этого же файла — почти всегда она и есть. Догадку
                            // не замалчиваем: она попадает в Problems.
                            SettingValue? v = Lookup(attr.Name);
                            if (v is not null)
                            {
                                _problems.Add($"'{module.Name}.{attr.Name}' взято из константы {attr.Name} " +
                                              "этого же файла — проверьте, что это то же значение");
                                return v;
                            }
                            _problems.Add($"не удалось вычислить '{module.Name}.{attr.Name}': " +
                                          "константы из другого модуля кокпит не читает");
                            return null;
                        }
                        _problems.Add("обращение к атрибуту вычислить нельзя");
                        return null;
                    }

                default:
                    _problems.Add($"конструкция {node.GetType().Name} не поддержана");
                    return null;
            }
        }

        private SettingValue? EvalIndex(PyIndexExpr index)
        {
            SettingValue? key = Eval(index.Index);
            if (key is not { Kind: SettingValueKind.String })
            {
                _problems.Add("индексировать словарь умолчаний можно только строковым ключом");
                return null;
            }

            if (index.Target is PyNameExpr target)
            {
                // values['x'] — параметр build_template: реальные значения игрока кокпиту
                // неизвестны, берём заводское из словаря умолчаний.
                SettingValue? fromDefaults = LookupDefault(key.AsString!);
                if (fromDefaults is not null) return fromDefaults;

                SettingValue? container = Lookup(target.Name);
                if (container is { Kind: SettingValueKind.Map } map && map.Get(key.AsString!) is { } found)
                    return found;

                _problems.Add($"не нашлось значение по умолчанию для '{key.AsString}' " +
                              $"(искали в {target.Name} и в словарях умолчаний)");
                return null;
            }

            SettingValue? evaluated = Eval(index.Target);
            if (evaluated is { Kind: SettingValueKind.Map } m && m.Get(key.AsString!) is { } value) return value;
            _problems.Add($"не удалось взять '{key.AsString}' из выражения");
            return null;
        }

        private SettingValue? EvalCall(PyCallExpr call)
        {
            string? funcName = call.Func switch
            {
                PyNameExpr n => n.Name,
                PyAttrExpr a => a.Name,
                _ => null
            };

            if (funcName is null)
            {
                _problems.Add("вызов слишком сложный, чтобы его понять");
                return null;
            }

            // Помощник «взять текущее значение»: v('key') или _v(values, 'key').
            // Вторая форма — та, что печатает кодогенератор кокпита: помощник вынесен на
            // уровень модуля, потому что py27c.py не умеет замыканий. Ключ — ПОСЛЕДНИЙ
            // аргумент; первый ('values') вычислять не пробуем, это параметр функции.
            if (ValueHelperNames.Contains(funcName) && call.Args.Count is 1 or 2)
            {
                SettingValue? key = Eval(call.Args[^1]);
                if (key is { Kind: SettingValueKind.String })
                {
                    SettingValue? found = LookupDefault(key.AsString!);
                    if (found is not null) return found;
                    _problems.Add($"не нашлось значение по умолчанию для '{key.AsString}'");
                    return null;
                }
            }

            // values.get('key', fallback)
            if (funcName == "get" && call.Func is PyAttrExpr { Target: PyNameExpr owner } &&
                call.Args.Count >= 1 && Eval(call.Args[0]) is { Kind: SettingValueKind.String } getKey)
            {
                SettingValue? found = LookupDefault(getKey.AsString!);
                if (found is not null) return found;
                if (Lookup(owner.Name) is { Kind: SettingValueKind.Map } ownerMap &&
                    ownerMap.Get(getKey.AsString!) is { } inOwner)
                {
                    return inOwner;
                }
                if (call.Args.Count >= 2) return Eval(call.Args[1]);
                _problems.Add($"не нашлось значение по умолчанию для '{getKey.AsString}'");
                return null;
            }

            if (FactoryParameters.TryGetValue(funcName, out string[]? parameters))
            {
                UsedFactories = true;
                return EvalFactory(funcName, parameters, call);
            }

            _problems.Add($"вызов '{funcName}(...)' не поддержан: кокпит понимает только " +
                          "templates.createX и обращение к словарю умолчаний");
            return null;
        }

        private SettingValue? EvalFactory(string factory, string[] parameters, PyCallExpr call)
        {
            var bound = new Dictionary<string, SettingValue>(StringComparer.Ordinal);

            for (int i = 0; i < call.Args.Count; i++)
            {
                if (i >= parameters.Length)
                {
                    _problems.Add($"{factory}: аргументов больше, чем параметров ({parameters.Length})");
                    return null;
                }
                SettingValue? v = Eval(call.Args[i]);
                if (v is null) return null;
                if (v.Kind != SettingValueKind.Null) bound[parameters[i]] = v;
            }

            foreach (KeyValuePair<string, IPyNode> kw in call.Kwargs)
            {
                if (!parameters.Contains(kw.Key))
                {
                    _problems.Add($"{factory}: нет параметра '{kw.Key}'");
                    return null;
                }
                SettingValue? v = Eval(kw.Value);
                if (v is null) return null;
                if (v.Kind != SettingValueKind.Null) bound[kw.Key] = v;
            }

            return factory == "createButton" ? BuildButton(bound) : BuildComponent(factory, bound);
        }

        private SettingValue? BuildComponent(string factory, Dictionary<string, SettingValue> a)
        {
            if (!ControlKinds.TryParseFactory(factory, out ControlKind kind))
            {
                _problems.Add($"неизвестная фабрика '{factory}'");
                return null;
            }

            var map = new List<KeyValuePair<string, SettingValue>>
            {
                new(TemplateJson.TypeKey, SettingValue.Of(ControlKinds.WireName(kind)))
            };

            if (kind == ControlKind.Empty) return SettingValue.Map(map);

            Put(map, TemplateJson.TextKey, a.GetValueOrDefault("text") ?? SettingValue.Of(string.Empty));
            if (a.TryGetValue("varName", out SettingValue? varName)) Put(map, TemplateJson.VarNameKey, varName);

            if (a.TryGetValue("options", out SettingValue? options))
            {
                SettingValue? generated = GenerateOptions(options);
                if (generated is null) return null;
                Put(map, TemplateJson.OptionsKey, generated);
            }

            if (a.TryGetValue("value", out SettingValue? value))
            {
                // createColorChoice сам снимает ведущую решётку — повторяем это,
                // иначе импорт и генерация разойдутся на одном символе.
                if (kind == ControlKind.ColorChoice && value.Kind == SettingValueKind.String)
                    value = SettingValue.Of((value.AsString ?? string.Empty).TrimStart('#'));
                Put(map, TemplateJson.ValueKey, value);
            }

            if (ControlKinds.HasRange(kind))
            {
                if (a.TryGetValue("min", out SettingValue? min)) Put(map, TemplateJson.MinimumKey, min);
                if (a.TryGetValue("max", out SettingValue? max)) Put(map, TemplateJson.MaximumKey, max);
                if (a.TryGetValue("interval", out SettingValue? step)) Put(map, TemplateJson.SnapIntervalKey, step);
            }

            if (kind == ControlKind.RangeSlider)
            {
                if (a.TryGetValue("step", out SettingValue? divisionStep))
                    Put(map, TemplateJson.DivisionStepKey, divisionStep);
                if (a.TryGetValue("minRange", out SettingValue? minRange))
                    Put(map, TemplateJson.MinRangeDistanceKey, minRange);
                if (a.TryGetValue("labelStep", out SettingValue? labelStep))
                    Put(map, TemplateJson.DivisionLabelStepKey, labelStep);
                if (a.TryGetValue("labelPostfix", out SettingValue? labelPostfix))
                    Put(map, TemplateJson.DivisionLabelPostfixKey, labelPostfix);
            }

            // Эти два ключа Python кладёт ВСЕГДА, даже если аргумент не передали.
            if (kind == ControlKind.Slider)
                Put(map, TemplateJson.FormatKey,
                    a.GetValueOrDefault("format") ?? SettingValue.Of(ControlKinds.SliderValuePlaceholder));
            if (kind == ControlKind.NumericStepper)
                Put(map, TemplateJson.CanManualInputKey,
                    a.GetValueOrDefault("manual") ?? SettingValue.False);

            if (a.TryGetValue("width", out SettingValue? width)) Put(map, TemplateJson.WidthKey, width);
            if (a.TryGetValue("tooltip", out SettingValue? tooltip)) Put(map, TemplateJson.TooltipKey, tooltip);
            if (a.TryGetValue("button", out SettingValue? button)) Put(map, TemplateJson.ButtonKey, button);

            return SettingValue.Map(map);
        }

        private static SettingValue BuildButton(Dictionary<string, SettingValue> a)
        {
            var map = new List<KeyValuePair<string, SettingValue>>();
            if (a.TryGetValue("width", out SettingValue? w)) map.Add(new(TemplateJson.ButtonWidthKey, w));
            if (a.TryGetValue("height", out SettingValue? h)) map.Add(new(TemplateJson.ButtonHeightKey, h));
            if (a.TryGetValue("text", out SettingValue? t)) map.Add(new(TemplateJson.ButtonTextKey, t));
            if (a.TryGetValue("offsetTop", out SettingValue? ot)) map.Add(new(TemplateJson.ButtonOffsetTopKey, ot));
            if (a.TryGetValue("offsetLeft", out SettingValue? ol)) map.Add(new(TemplateJson.ButtonOffsetLeftKey, ol));
            // icon уезжает в iconSource, ТОЛЬКО если текста нет: в createButton они исключают друг друга.
            if (!a.ContainsKey("text") && a.TryGetValue("icon", out SettingValue? icon))
                map.Add(new(TemplateJson.ButtonIconSourceKey, icon));
            if (a.TryGetValue("iconOffsetTop", out SettingValue? iot))
                map.Add(new(TemplateJson.ButtonIconOffsetTopKey, iot));
            if (a.TryGetValue("iconOffsetLeft", out SettingValue? iol))
                map.Add(new(TemplateJson.ButtonIconOffsetLeftKey, iol));
            return SettingValue.Map(map);
        }

        /// <summary>Повторяет <c>templates.generateOptions</c>.</summary>
        private SettingValue? GenerateOptions(SettingValue options)
        {
            if (options.Kind != SettingValueKind.List)
            {
                _problems.Add("options обязаны быть списком");
                return null;
            }

            var result = new List<SettingValue>(options.Items.Count);
            foreach (SettingValue option in options.Items)
            {
                switch (option.Kind)
                {
                    case SettingValueKind.String:
                        result.Add(SettingValue.Map(new[]
                        {
                            new KeyValuePair<string, SettingValue>(TemplateJson.LabelKey, option)
                        }));
                        break;

                    case SettingValueKind.List when option.Items.Count is 1 or 2:
                        {
                            var pair = new List<KeyValuePair<string, SettingValue>>
                            {
                                new(TemplateJson.LabelKey, option.Items[0])
                            };
                            if (option.Items.Count == 2)
                                pair.Add(new KeyValuePair<string, SettingValue>(TemplateJson.TooltipKey, option.Items[1]));
                            result.Add(SettingValue.Map(pair));
                            break;
                        }

                    case SettingValueKind.Map:
                        result.Add(option);
                        break;

                    default:
                        _problems.Add($"вариант выбора вида {option.Kind} не понят: " +
                                      "generateOptions принимает строку, пару (label, tooltip), список или словарь");
                        return null;
                }
            }
            return SettingValue.List(result);
        }

        private static void Put(List<KeyValuePair<string, SettingValue>> map, string key, SettingValue value) =>
            map.Add(new KeyValuePair<string, SettingValue>(key, value));

        private SettingValue? LookupDefault(string key)
        {
            foreach (string name in DefaultsNames)
                if (Lookup(name) is { Kind: SettingValueKind.Map } map && map.Get(key) is { } found)
                    return found;
            foreach (string name in ValuesNames)
                if (Lookup(name) is { Kind: SettingValueKind.Map } map && map.Get(key) is { } found)
                    return found;
            return null;
        }

        private SettingValue? Lookup(string name)
        {
            if (_cache.TryGetValue(name, out SettingValue? cached)) return cached;
            if (!_assignments.TryGetValue(name, out int offset)) return null;
            if (!_evaluating.Add(name))
            {
                _problems.Add($"циклическая ссылка на '{name}'");
                return null;
            }
            try
            {
                SettingValue? value = null;
                if (PythonValueParser.TryParse(_source, offset, out IPyNode? node, out _) && node is not null)
                    value = Eval(node);
                _cache[name] = value;
                return value;
            }
            finally
            {
                _evaluating.Remove(name);
            }
        }
    }
}
